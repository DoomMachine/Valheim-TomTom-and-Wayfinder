using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The plugin's own log file, BepInEx\&lt;Edition&gt;.log beside LogOutput.log (the last session's is kept as
    /// &lt;Edition&gt;-prev.log): the plugin's warnings and errors, game errors whose stack trace runs through its code and,
    /// with VerboseLog (TomTom), every line it logs and the Diag.Trace lines. LogOutput.log and Player.log are not
    /// changed by it.
    ///
    /// Threading: the hooks run inside other code (every Plugin.Log call; Unity's logging of an error, which has no try
    /// around its handlers) and the flush timer on a pool thread, so every entry point catches everything and never logs.
    /// Gate is a LEAF lock: under it nothing is called outside System.*, LogRules, LogRotation, LineBudget and
    /// RepeatCollapse (no BepInEx, no Unity, no configuration, no other Waypointer type), so it cannot join a cycle with
    /// BepInEx's configuration lock (held while SettingChanged runs), BepInEx's writer locks, the timer scheduler or
    /// Unity's log dispatch.
    /// </summary>
    internal static class LogFile
    {
        // ---- static initialisers: allocations only (a type initialiser that threw would make every use throw)
        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);   // never throws on a lone surrogate
        private static readonly TimerCallback FlushCallback = OnFlushTimer;          // C# 5: delegates cached by hand
        private static readonly EventHandler<LogEventArgs> OwnEventHandler = OnOwnEvent;
        private static readonly Application.LogCallback UnityLogHandler = OnUnityLog;
        private const int MaxWarnings = 3;
        private const int FlushPeriodMs = 1000;
        private const int MaxUnityKinds = 64;
        private const string UnitySource = "Game";

        // ---- read without Gate (volatile) by the hooks and Update; written only under Gate
        private static volatile bool _open;
        private static volatile LogDetail _level;
        private static volatile string _parked;
        private static volatile bool _repeatPending;

        // ---- set by Open on the main thread before the hooks are added; read-only afterwards
        private static int _mainThreadId;
        private static string _folder, _edition, _facts;
        private static string[] _scrubFrom, _scrubTo;
        private static bool _hooked;
        private static int _warnings;                    // EmitParkedFailure only (main thread)

        // ---- Gate
        private static FileStream _stream;
        private static StreamWriter _writer;
        private static System.Threading.Timer _timer;
        private static LineBudget _budget;
        private static RepeatCollapse _repeat;
        private static Dictionary<string, int> _unitySeen;
        private static int _unityDropped;
        private static string _sessionName;              // the name this session writes, from its first open
        private static bool _rotationTried, _dirty, _inWrite, _shutDown, _full;
        private static TimeSpan _offset;
        private static long _offsetMinute = -1;

        // =============================================================================================== entry points

        /// <summary>Awake, main thread, once, before anything else is set up. Never throws.</summary>
        internal static void Open(LogDetail detail, string facts)
        {
            try
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _edition = Edition.Name;
                _facts = facts;
                _folder = Paths.BepInExRootPath;                      // beside LogOutput.log, also under a mod manager
                BuildScrub();                                         // BepInEx and environment lookups: outside Gate

                lock (Gate)
                {
#if WAYFINDER
                    _repeat = new RepeatCollapse(false);           // Wayfinder: a repeat is never counted
#else
                    _repeat = new RepeatCollapse(true);
#endif
                    _unitySeen = new Dictionary<string, int>(StringComparer.Ordinal);
                    _level = LogRules.Normalize(detail);
                    if (_level != LogDetail.Off) OpenLocked("session started");
                }

                // Once, here, on the main thread: Unity's add accessor is a plain read-combine-write of a static field.
                if (!_hooked)
                {
                    Plugin.Log.LogEvent += OwnEventHandler;
                    Application.logMessageReceived += UnityLogHandler;
                    _hooked = true;
                }
            }
            catch (Exception e) { Fail("the log file could not be opened", e); }
        }

        /// <summary>Diag.Trace. Never throws.</summary>
        internal static void Write(int level, string source, string text)
        {
            try
            {
                if (!_open) return;
                WriteCore(level, source, text);
            }
            catch (Exception e) { Fail("a line could not be written", e); }
        }

        /// <summary>The ErrorLog/VerboseLog settings changed. Any thread (a configuration reload runs it on the reloading
        /// thread, under BepInEx's configuration lock): no Unity call unless on the main thread, the hooks untouched.</summary>
        internal static void SetLevel(LogDetail requested)
        {
            try
            {
                LogDetail next = LogRules.Normalize(requested);
                int frame = OnMainThread() ? Time.frameCount : -1;
                lock (Gate)
                {
                    if (_shutDown) return;
                    LogDetail was = _level;
                    if (next == was && (next == LogDetail.Off || _writer != null)) return;
                    DateTime local = LocalNowLocked();
                    if (next == LogDetail.Off)
                    {
                        if (_writer != null)
                        {
                            FlushRepeatLocked(frame, local, true);
                            EndSectionLocked(was, frame, local);
                            NoteLocked(LogRules.Info, _edition, "log: " + LogRules.DetailText(next)
                                       + " - nothing more is written until a setting turns it on again", frame, local);
                        }
                        _level = LogDetail.Off;
                        CloseLocked(true);
                        return;
                    }
                    _level = next;
                    if (_writer == null)
                    {
                        OpenLocked("turned on again");                   // Append; rotation only if none ran yet
                        return;                                           // OpenLocked starts the timer and sets Diag.On
                    }
                    bool toVerbose = LogRules.IsVerbose(next), fromVerbose = LogRules.IsVerbose(was);
                    FlushRepeatLocked(frame, local, true);
                    if (toVerbose && !fromVerbose)
                    {
                        _budget.StartSection();
                        NoteLocked(LogRules.Info, _edition, "log: " + LogRules.DetailText(next) + " - every line from here", frame, local);
                        StartTimerLocked();
                    }
                    else if (fromVerbose && !toVerbose)
                    {
                        EndSectionLocked(was, frame, local);
                        NoteLocked(LogRules.Info, _edition, "log: " + LogRules.DetailText(next) + " - warnings and errors from here",
                                   frame, local);
                        StopTimerLocked();
                    }
                    FlushLocked();
                    Diag.On = _writer != null && LogRules.IsVerbose(_level);
                }
            }
            catch (Exception e) { Fail("the log setting could not be applied", e); }
        }

        /// <summary>Plugin.OnApplicationQuit. Main thread. Never throws.</summary>
        internal static void NoteQuit()
        {
            try
            {
                int frame = OnMainThread() ? Time.frameCount : -1;
                lock (Gate)
                {
                    if (_writer == null) return;
                    DateTime local = LocalNowLocked();
                    FlushRepeatLocked(frame, local, true);
                    NoteLocked(LogRules.Info, _edition, "the game is quitting", frame, local);
                    FlushLocked();
                }
            }
            catch (Exception e) { Fail("the log could not be written at quit", e); }
        }

        /// <summary>Plugin.OnDestroy, both paths, after the rest of its work. Main thread. Never throws; final.</summary>
        internal static void Close()
        {
            try
            {
                int frame = OnMainThread() ? Time.frameCount : -1;
                lock (Gate)
                {
                    if (_writer != null)
                    {
                        DateTime local = LocalNowLocked();
                        FlushRepeatLocked(frame, local, true);
                        EndSectionLocked(_level, frame, local);
                        NoteUnityDroppedLocked(frame, local);
                        NoteLocked(LogRules.Info, _edition, "log closed", frame, local);
                    }
                    _shutDown = true;                                     // nothing opens the file after this
                    CloseLocked(true);
                }
                if (_hooked)
                {
                    _hooked = false;
                    try { Application.logMessageReceived -= UnityLogHandler; } catch (Exception) { }
                    try { if (Plugin.Log != null) Plugin.Log.LogEvent -= OwnEventHandler; } catch (Exception) { }
                }
            }
            catch (Exception e) { Fail("the log could not be closed", e); }
        }

        /// <summary>
        /// Update's first statement (before the dedicated server's branch), and once after Open in Awake and after Close
        /// in OnDestroy - Update never runs when Awake disabled the plugin. Main thread, outside Gate on purpose: the
        /// warning passes through BepInEx's listeners and then OnOwnEvent; if writing it fails again, Fail closes the
        /// file, so at most one more warning follows (and MaxWarnings caps the session). Also writes a repeat count that
        /// is due when no flush timer runs (without VerboseLog).
        /// </summary>
        internal static void EmitParkedFailure()
        {
            try
            {
                if (_repeatPending && !LogRules.IsVerbose(_level))       // with VerboseLog the timer does this
                {
                    int frame = OnMainThread() ? Time.frameCount : -1;
                    lock (Gate)
                    {
                        // Without VerboseLog every line is flushed when written, so _dirty is true only when a count
                        // was just written: no flush on the frames where nothing is due.
                        if (_writer != null && FlushRepeatLocked(frame, LocalNowLocked(), false) && _dirty) FlushLocked();
                    }
                }
                if (_parked == null) return;
                string message;
                lock (Gate)
                {
                    message = _parked;
                    _parked = null;
                }
                if (message == null || _warnings >= MaxWarnings || Plugin.Log == null) return;
                _warnings++;
                Plugin.Log.LogWarning(message);                          // the only log call the log file makes
            }
            catch (Exception) { }
        }

        // ===================================================================================================== hooks

        /// <summary>Plugin.Log's own event: this plugin's lines only, after BepInEx's listeners, on the logging thread.
        /// A throw here would surface in the plugin's own Log call.</summary>
        private static void OnOwnEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (!_open || e == null) return;                          // off, closed, failed or full
                int level = (int)e.Level;
                if (!LogRules.Admits(level, _level)) return;              // an Info line without VerboseLog: done
                object data = e.Data;
                WriteCore(level, _edition, data == null ? string.Empty : data.ToString());
            }
            catch (Exception x) { Fail("a line could not be written", x); }
        }

        /// <summary>Application.logMessageReceived: the main thread only, and Unity has no try around it - a throw would
        /// skip every later handler for this message.</summary>
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (!_open) return;
                if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
                if (!LogRules.IsOurStack(stackTrace)) return;
                WriteCore(LogRules.Error, UnitySource, condition + "\nStack trace:\n" + stackTrace);
            }
            catch (Exception x) { Fail("a game error could not be written", x); }
        }

        /// <summary>VerboseLog only. On the game's Mono, Timer.Dispose() does not stop a callback already queued, so this
        /// one owns nothing: it takes Gate and reads the writer again.</summary>
        private static void OnFlushTimer(object state)
        {
            try
            {
                lock (Gate)
                {
                    if (_writer == null) return;                          // closed, off, failed or full meanwhile
                    if (!FlushRepeatLocked(-1, LocalNowLocked(), false)) return;
                    if (!_dirty) return;
                    _writer.Flush();
                    _dirty = false;
                }
            }
            catch (Exception x) { Fail("the log could not be flushed", x); }   // never rethrow on a pool thread
        }

        // ============================================================================================ write path

        // Inside a caller's try. Unity and text rules before the lock; the lock is a leaf.
        private static void WriteCore(int level, string source, string text)
        {
            int frame = OnMainThread() ? Time.frameCount : -1;           // Unity: main thread only, outside Gate
            text = LogRules.Scrub(text, _scrubFrom, _scrubTo);
#if WAYFINDER
            text = LogRules.HidePositions(text);
#endif
            lock (Gate)
            {
                if (_inWrite) return;                                     // re-entry on this thread: dropped, never recursed
                if (_writer == null || !LogRules.Admits(level, _level)) return;
                _inWrite = true;
                try { WriteLineLocked(level, source, text, frame); }
                finally { _inWrite = false; }
            }                                                             // an IOException leaves the lock -> the caller's Fail
        }

        private static void WriteLineLocked(int level, string source, string text, int frame)
        {
            DateTime local = LocalNowLocked();
            if (_repeat.Repeats(level, source, text, local))
            {
                _repeatPending = true;                                    // no I/O, no budget
                return;
            }
            if (ReferenceEquals(source, UnitySource) && !UnityLineAllowedLocked(text)) return;
            if (!FlushRepeatLocked(frame, local, true)) return;
            if (!EmitLocked(level, source, text, frame, local))
            {
                if (_repeat != null) _repeat.Forget();
                return;
            }
            _repeat.Wrote(level, source, text);
            if ((level & LogRules.Problems) != 0) FlushLocked();         // problems reach the system at once
            else _dirty = true;                                           // the timer, the next problem or a note flushes it
        }

        // The same game error is written UnityRepeats times a session; later ones are counted (only the count of lines
        // left out is written, at close, and not in Wayfinder).
        private static bool UnityLineAllowedLocked(string text)
        {
            string key = text == null ? string.Empty : (text.Length > 200 ? text.Substring(0, 200) : text);
            int seen;
            if (_unitySeen.TryGetValue(key, out seen))
            {
                if (seen >= LogRules.UnityRepeats)
                {
                    _unityDropped++;
                    return false;
                }
                _unitySeen[key] = seen + 1;
                return true;
            }
            if (_unitySeen.Count < MaxUnityKinds) _unitySeen[key] = 1;
            return true;
        }

        private static void NoteUnityDroppedLocked(int frame, DateTime local)
        {
            if (_unityDropped <= 0) return;
#if WAYFINDER
            NoteLocked(LogRules.Info, _edition, "some game errors came more than 5 times; the later ones were left out",
                       frame, local);
#else
            NoteLocked(LogRules.Info, _edition, _unityDropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + " more game error line(s) were left out: each came more than 5 times", frame, local);
#endif
            _unityDropped = 0;
        }

        // False when the line was not written (left out, or the file stopped).
        private static bool EmitLocked(int level, string source, string text, int frame, DateTime local)
        {
            string line = LogRules.FormatLine(local, frame, LogRules.LevelTag(level), source, text);
            int bytes = Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
            LineDecision d = _budget.Decide(level, bytes);
            if (d == LineDecision.Drop) return false;
            if (d == LineDecision.NoticeThenDrop)
            {
                NoteLocked(LogRules.Info, _edition, "ordinary lines stop here (8 MiB in this stretch of VerboseLog); "
                           + "warnings and errors go on", frame, local);
                return false;
            }
            if (d == LineDecision.FinalThenStop)
            {
                NoteLocked(LogRules.Warning, _edition, "this log reached 16 MiB: nothing more is written to it in this "
                           + "session (LogOutput.log goes on)", frame, local);
                _budget.Stop();
                _full = true;                                             // no reopen and no other name this session
                if (_parked == null)
                    _parked = Where() + " reached 16 MiB, so it stops for this session; LogOutput.log goes on.";
                CloseLocked(true);
                return false;
            }
            _writer.Write(line);
            _writer.Write(Environment.NewLine);
            _budget.Wrote(level, bytes, false);
            return true;
        }

        // Headers, notes, counts. False if the file is (now) closed.
        private static bool NoteLocked(int level, string source, string text, int frame, DateTime local)
        {
            if (_writer == null) return false;
            string line = LogRules.FormatLine(local, frame, LogRules.LevelTag(level), source, text);
            int bytes = Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
            if (_budget.DecideNotice(bytes) != LineDecision.Write) return true;
            _writer.Write(line);
            _writer.Write(Environment.NewLine);
            _budget.Wrote(level, bytes, true);
            _dirty = true;
            return true;
        }

        // all: at close, quit, a setting change or before another line; otherwise only when due (timer, per-frame poll).
        private static bool FlushRepeatLocked(int frame, DateTime local, bool all)
        {
            if (_repeat == null) return _writer != null;
            string s = all ? _repeat.TakeSummary() : _repeat.TakeSummaryIfDue(local);
            _repeatPending = _repeat.Pending;
            if (s == null) return _writer != null;
            return NoteLocked(_repeat.Level, _repeat.Source, s, frame, local);
        }

        private static void EndSectionLocked(LogDetail was, int frame, DateTime local)
        {
            if (!LogRules.IsVerbose(was) || _budget == null) return;
            long dropped = _budget.StartSection();
            if (dropped > 0)
                NoteLocked(LogRules.Info, _edition, "the stretch of VerboseLog ends: "
                           + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           + " ordinary line(s) were left out after its 8 MiB", frame, local);
        }

        private static void FlushLocked()
        {
            if (_writer == null) return;
            _writer.Flush();                                              // to the system, not to the disk
            _dirty = false;
        }

        // =========================================================================================== open / close

        // Under Gate. False when no file could be opened (the reason is parked).
        private static bool OpenLocked(string why)
        {
            if (_writer != null) return true;
            if (_shutDown || _full) return false;

            string note = null;
            FileMode plainMode = FileMode.Append;                         // a reopen never empties anything
            if (!_rotationTried)
            {
                _rotationTried = true;                                    // at most one rotation per session
                LogRotation.Outcome o = LogRotation.Rotate(_folder, _edition, out plainMode);
                note = RotationNote(o);
            }
            string refusal = null;
            bool reopened = _sessionName != null;
            if (reopened && OpenWriter(_sessionName, FileMode.Append, true, ref refusal))
            {
                HeaderLocked(why, note);
                return true;
            }
            if (_full) return false;                                      // this session's file is full: no other name
            for (int i = 0; i <= LogRules.Fallbacks; i++)
            {
                string name = LogRules.FileName(_edition, false);              // no ?: here: preflight check 20
                if (i > 0) name = LogRules.FallbackName(_edition, i);           // reads every store into this local
                if (name == _sessionName) continue;
                // The plain name may hold the last session's lines: Append, never Create, unless the rotation said so.
                // A numbered name holds an earlier second copy of the game's sessions: Append, so they are never
                // emptied (a live one refuses the open on Windows; one past half the cap is skipped in OpenWriter).
                FileMode mode = FileMode.Append;
                if (i == 0) mode = plainMode;
                if (OpenWriter(name, mode, false, ref refusal))
                {
                    HeaderLocked(why, note);
                    return true;
                }
            }
            string failure = "no log file could be opened (" + refusal + ")";
            if (refusal != null && refusal.IndexOf("already holds a long log", StringComparison.Ordinal) >= 0)
                failure += "; the numbered files (written while another copy of the game or a program holds the log) are "
                         + "kept until you delete them, so delete the long ones (" + LogRules.FallbackName(_edition, 1)
                         + " to ." + LogRules.Fallbacks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                         + ") to make room";
            Park(failure, null);
            return false;
        }

        /// <summary>The log's only FileStream and StreamWriter. Under Gate. FileShare.Read: a reader that allows writing
        /// can open the file while the game runs, as with LogOutput.log; no other writer, and no rename or delete while
        /// it is open - which is what makes LogRotation's first move a probe.</summary>
        private static bool OpenWriter(string name, FileMode mode, bool sessionFile, ref string refusal)
        {
            FileStream stream = null;
            try
            {
                stream = new FileStream(PathOf(name), mode, FileAccess.Write, FileShare.Read, 8192);
                if (!sessionFile && mode == FileMode.Append && stream.Length > LogRules.FileCap / 2)
                {
                    stream.Dispose();                                     // the last session's long log: kept, use .N
                    refusal = name + " already holds a long log";
                    return false;
                }
                LineBudget budget = new LineBudget(stream.Length);        // what the file holds counts
                if (budget.Stopped)
                {
                    stream.Dispose();
                    if (sessionFile) _full = true;                       // this session's file is full: stays closed
                    refusal = name + " is full";
                    return false;
                }
                StreamWriter writer = new StreamWriter(stream, Utf8NoBom, 4096);
                writer.AutoFlush = false;
                _stream = stream;
                _writer = writer;
                _budget = budget;
                _sessionName = name;
                _dirty = false;
                _open = true;
                Diag.On = LogRules.IsVerbose(_level);
                if (LogRules.IsVerbose(_level)) StartTimerLocked();
                return true;
            }
            catch (Exception e)
            {
                if (stream != null) { try { stream.Dispose(); } catch (Exception) { } }   // never leave a handle open
                refusal = name + ": " + e.GetType().Name;
                return false;
            }
        }

        private static void HeaderLocked(string why, string rotationNote)
        {
            DateTime local = LocalNowLocked();
            NoteLocked(LogRules.Info, _edition, (_facts ?? _edition) + " - " + LogRules.DetailText(_level), -1, local);
            NoteLocked(LogRules.Info, _edition, why + " " + local.ToString("yyyy-MM-dd HH:mm:ss",
                       System.Globalization.CultureInfo.InvariantCulture) + " (" + LogRules.FormatOffset(_offset)
                       + "); times are local, f<n> is the game's frame", -1, local);
#if WAYFINDER
            NoteLocked(LogRules.Info, _edition, "folders are written as <config>, <BepInEx>, <game> and <home>, and numbers "
                       + "that could be a position as #; every line is written at once; the file stops at 16 MiB", -1, local);
#else
            NoteLocked(LogRules.Info, _edition, "folders are written as <config>, <BepInEx>, <game> and <home>; VerboseLog "
                       + "lines name positions and pins, so read this file before you share it; warnings and errors are "
                       + "written at once, other lines within 1 s; a stretch of VerboseLog keeps 8 MiB of ordinary lines, "
                       + "the file stops at 16 MiB", -1, local);
#endif
            if (rotationNote != null) NoteLocked(LogRules.Info, _edition, rotationNote, -1, local);
            if (LogRules.IsVerbose(_level)) _budget.StartSection();
            FlushLocked();
        }

        // Under Gate. Fields cleared FIRST (a hook, the timer or a re-entry then finds nothing half-closed), then each
        // dispose in its own try: StreamWriter.Dispose closes its stream even when its flush throws, and the stream is
        // disposed again in case it was not reached.
        private static void CloseLocked(bool graceful)
        {
            StreamWriter w = _writer;
            FileStream s = _stream;
            _writer = null;
            _stream = null;
            _open = false;
            _dirty = false;
            Diag.On = false;
            StopTimerLocked();
            if (_repeat != null) _repeat.Forget();
            _repeatPending = false;
            Exception flushError = null;
            if (w != null && graceful) { try { w.Flush(); } catch (Exception e) { flushError = e; } }
            if (w != null) { try { w.Dispose(); } catch (Exception) { } }
            if (s != null) { try { s.Dispose(); } catch (Exception) { } }
            if (flushError != null) Park("its last lines could not be written", flushError);
        }

        // Any failure: close the file and keep the first reason for the main thread. Never throws, never logs (a log
        // call here would come straight back into OnOwnEvent).
        private static void Fail(string what, Exception e)
        {
            try
            {
                lock (Gate)
                {
                    CloseLocked(false);
                    Park(what, e);
                }
            }
            catch (Exception) { }
        }

        private static void Park(string what, Exception e)
        {
            if (_parked != null) return;                                  // the first cause only
            string message = e == null ? null : LogRules.Scrub(e.Message, _scrubFrom, _scrubTo);
#if WAYFINDER
            message = LogRules.HidePositions(message);
#endif
            _parked = Where() + ": " + what + (e == null ? "" : " (" + e.GetType().Name + ": " + message + ")")
                    + ". The game and LogOutput.log are not affected; turn the [7 - Logging] setting off and on again in "
                    + "game (ConfigurationManager), or restart the game or server, to try again.";
        }

        // ================================================================================================= timer

        private static void StartTimerLocked()
        {
            if (_timer != null) return;
            try { _timer = new System.Threading.Timer(FlushCallback, null, FlushPeriodMs, FlushPeriodMs); }
            catch (Exception) { _timer = null; }   // no timer: lines are still flushed at problems, notes and close
        }

        private static void StopTimerLocked()
        {
            System.Threading.Timer t = _timer;
            _timer = null;
            if (t != null) { try { t.Dispose(); } catch (Exception) { } }   // never Dispose(WaitHandle), never wait under Gate
        }

        // =============================================================================================== helpers

        private static bool OnMainThread()
        {
            return _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static string PathOf(string name)
        {
            return Path.Combine(_folder, name);
        }

        // The file named relative to the game folder (never an absolute path).
        private static string Where()
        {
            return "The log file BepInEx\\" + (_sessionName ?? LogRules.FileName(_edition ?? Edition.Name, false));
        }

        private static DateTime LocalNowLocked()
        {
            DateTime utc = DateTime.UtcNow;
            long minute = utc.Ticks / TimeSpan.TicksPerMinute;
            if (minute != _offsetMinute)                                  // a daylight-saving change shows within a minute
            {
                _offsetMinute = minute;
                try { _offset = TimeZoneInfo.Local.GetUtcOffset(utc); }
                catch (Exception) { _offset = TimeSpan.Zero; }            // e.g. a server without time-zone data
            }
            return new DateTime(utc.Ticks + _offset.Ticks, DateTimeKind.Unspecified);
        }

        private static void BuildScrub()
        {
            string config = null, root = null, game = null, home = null, homeShort = null;
            try { config = Paths.ConfigPath; } catch (Exception) { }
            try { root = Paths.BepInExRootPath; } catch (Exception) { }
            try { game = Paths.GameRootPath; } catch (Exception) { }
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { }
            try { homeShort = LogRules.HomeFromTempPath(Path.GetTempPath()); } catch (Exception) { }
            string[] from, to;
            LogRules.ScrubPairs(config, root, game, home, homeShort, out from, out to);
            _scrubFrom = from;
            _scrubTo = to;
        }

        private static string RotationNote(LogRotation.Outcome o)
        {
            switch (o)
            {
                case LogRotation.Outcome.PlainHeld:
                    return "the last session's log could not be renamed (another program has it open): its lines are above "
                         + "this session's, or, if it cannot be written either (another copy of the game, or a program that "
                         + "locks it), this session writes a numbered file";
                case LogRotation.Outcome.PrevKept:
                    return LogRules.FileName(_edition, true) + " could not be replaced, so it is kept: the last session's "
                         + "lines are above this session's, in this file";
                case LogRotation.Outcome.StageKept:
                    return "the last session's log is kept as " + LogRules.StageName(_edition);
                case LogRotation.Outcome.Failed:
                    return "the last session's log could not be looked at; it was not changed";
                default:
                    return null;
            }
        }
    }
}
