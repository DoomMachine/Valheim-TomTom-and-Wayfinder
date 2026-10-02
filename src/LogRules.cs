using System;
using System.Globalization;
using System.Text;

namespace Waypointer
{
    /// <summary>
    /// What the plugin's own log file holds, from the [7 - Logging] settings. Wayfinder has no Verbose: its file holds
    /// warnings and errors only, since a line for every click and Find would tell where things are.
    /// </summary>
    internal enum LogDetail
    {
        Off = 0,
        Errors = 1,
#if !WAYFINDER
        Verbose = 2,
#endif
    }

    /// <summary>
    /// The log file's rules: which lines it admits, its file names, how a line is written, and what a line may not
    /// show. No Unity and no BepInEx, so tests/ runs them; and nothing that .NET Framework 4.x's mscorlib lacks, since
    /// run-tests.sh also compiles this with the Framework's csc (so no String.Replace(string, string, StringComparison)).
    /// </summary>
    internal static class LogRules
    {
        // BepInEx.Logging.LogLevel's values (preflight compares them with the shipped BepInEx.dll).
        internal const int Fatal = 1, Error = 2, Warning = 4, Message = 8, Info = 16, Debug = 32, All = 63;
        internal const int Problems = Fatal | Error | Warning;

        internal const long SectionCap = 8L * 1024 * 1024;   // ordinary lines in one stretch of VerboseLog
        internal const long FileCap = 16L * 1024 * 1024;     // every line, per file (an appended file counts what it holds)
        internal const int Reserve = 1024;                   // kept for the last line, notes and counts
        internal const int MaxTextChars = 32 * 1024;         // one message; a longer one is cut, and says so
        internal const int Fallbacks = 4;                    // <Edition>.log.1 .. .4, while another game holds the file
        internal const int UnityRepeats = 5;                 // the same game error is written this often per session
        internal const string Indent = "    | ";             // starts every continuation line

        /// <summary>The file's detail from the two settings: VerboseLog (TomTom only) wins, then ErrorLog.</summary>
        internal static LogDetail Detail(bool errorLog, bool verboseLog)
        {
#if !WAYFINDER
            if (verboseLog) return LogDetail.Verbose;
#endif
            return errorLog ? LogDetail.Errors : LogDetail.Off;
        }

        /// <summary>Any value the enum does not name behaves as Errors, so nothing but Verbose admits every level.</summary>
        internal static LogDetail Normalize(LogDetail detail)
        {
            if (detail == LogDetail.Off) return LogDetail.Off;
#if !WAYFINDER
            if (detail == LogDetail.Verbose) return LogDetail.Verbose;
#endif
            return LogDetail.Errors;
        }

        internal static bool IsVerbose(LogDetail detail)
        {
#if WAYFINDER
            return false;
#else
            return Normalize(detail) == LogDetail.Verbose;
#endif
        }

        internal static bool Admits(int level, LogDetail detail)
        {
            LogDetail d = Normalize(detail);
            if (d == LogDetail.Off) return false;
            if (IsVerbose(d)) return (level & All) != 0;
            return (level & Problems) != 0;
        }

        internal static string DetailText(LogDetail detail)
        {
            LogDetail d = Normalize(detail);
#if WAYFINDER
            return d == LogDetail.Off ? "ErrorLog off" : "ErrorLog on";
#else
            return d == LogDetail.Off ? "ErrorLog off, VerboseLog off"
                 : d == LogDetail.Errors ? "ErrorLog on, VerboseLog off"
                 : "VerboseLog on";
#endif
        }

        internal static string FileName(string edition, bool previous)
        {
            return previous ? edition + "-prev.log" : edition + ".log";
        }

        internal static string FallbackName(string edition, int n)
        {
            return edition + ".log." + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Where the last session's file waits while "-prev" is replaced (LogRotation; never a live file).</summary>
        internal static string StageName(string edition)
        {
            return edition + "-prev.log.new";
        }

        internal static string LevelTag(int level)
        {
            if ((level & Fatal) != 0) return "FATAL";
            if ((level & Error) != 0) return "ERROR";
            if ((level & Warning) != 0) return "WARN ";
            if ((level & (Message | Info)) != 0) return "INFO ";
            return "DEBUG";
        }

        /// <summary>A game error is this plugin's when its stack trace runs through the plugin's code.</summary>
        internal static bool IsOurStack(string stackTrace)
        {
            return stackTrace != null && stackTrace.IndexOf("Waypointer.", StringComparison.Ordinal) >= 0;
        }

        /// <summary>"yyyy-MM-dd HH:mm:ss.fff  f(frame|-)  TAG  source  text", local time, invariant culture.</summary>
        internal static string FormatLine(DateTime localTime, int frame, string levelTag, string source, string text)
        {
            StringBuilder b = new StringBuilder(64 + (text == null ? 0 : Math.Min(text.Length, MaxTextChars)));
            b.Append(localTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            b.Append("  f");
            if (frame < 0) b.Append('-'); else b.Append(frame.ToString(CultureInfo.InvariantCulture));
            b.Append("  ").Append(levelTag).Append("  ").Append(source ?? string.Empty).Append("  ");
            AppendText(b, text);
            return b.ToString();
        }

        /// <summary>"UTC+03:00", "UTC-05:30", "UTC+00:00".</summary>
        internal static string FormatOffset(TimeSpan utcOffset)
        {
            TimeSpan a = utcOffset.Duration();
            return "UTC" + (utcOffset < TimeSpan.Zero ? "-" : "+") + a.Hours.ToString("00", CultureInfo.InvariantCulture) + ":"
                 + a.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        // Line breaks become an indented continuation, so no text can start a line of its own; other control
        // characters are escaped; trailing line breaks are dropped (Unity's stack trace ends with one).
        private static void AppendText(StringBuilder b, string text)
        {
            if (text == null) return;
            int end = text.Length;
            while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == '\r')) end--;
            int limit = end > MaxTextChars ? MaxTextChars : end;
            for (int i = 0; i < limit; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < limit && text[i + 1] == '\n') i++;
                    b.Append(Environment.NewLine).Append(Indent);
                }
                else if ((c < ' ' && c != '\t') || c == '\u007f' || c == '\u0085' || c == '\u2028' || c == '\u2029')
                {
                    b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else b.Append(c);
            }
            if (limit < end)
                b.Append(" ... (").Append((end - limit).ToString(CultureInfo.InvariantCulture)).Append(" more characters)");
        }

        /// <summary>
        /// The folders a line may name, and what each is written as: the config folder, the BepInEx folder, the game
        /// folder and the user's profile folder (also in its 8.3 short form), each also with '/', longest first so a
        /// folder inside another is replaced before it. A folder shorter than 4 characters ("C:\") is never replaced.
        /// </summary>
        internal static void ScrubPairs(string configPath, string bepinexRoot, string gameRoot, string home, string homeShort,
                                        out string[] from, out string[] to)
        {
            string[] f = { configPath, bepinexRoot, gameRoot, home, homeShort };
            string[] t = { "<config>", "<BepInEx>", "<game>", "<home>", "<home>" };
            int n = 0;
            string[] fs = new string[10], ts = new string[10];
            for (int k = 0; k < f.Length; k++)
            {
                string p = f[k] == null ? null : f[k].TrimEnd('\\', '/');
                if (p == null || p.Length < 4) continue;
                if (Array.IndexOf(fs, p, 0, n) < 0) { fs[n] = p; ts[n] = t[k]; n++; }
                string other = p.IndexOf('\\') >= 0 ? p.Replace('\\', '/') : p.Replace('/', '\\');
                if (other != p && Array.IndexOf(fs, other, 0, n) < 0) { fs[n] = other; ts[n] = t[k]; n++; }
            }
            for (int i = 1; i < n; i++)
                for (int j = i; j > 0 && fs[j].Length > fs[j - 1].Length; j--)
                {
                    string x = fs[j]; fs[j] = fs[j - 1]; fs[j - 1] = x;
                    x = ts[j]; ts[j] = ts[j - 1]; ts[j - 1] = x;
                }
            from = new string[n];
            to = new string[n];
            Array.Copy(fs, from, n);
            Array.Copy(ts, to, n);
        }

        /// <summary>The profile folder's short form, from the temporary folder's path ("X:\Profiles\ABCDEF~1\AppData\..."),
        /// or null when that path has no "\AppData\" in it.</summary>
        internal static string HomeFromTempPath(string tempPath)
        {
            if (tempPath == null) return null;
            int at = tempPath.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? tempPath.Substring(0, at) : null;
        }

        /// <summary>Replaces each from[k] by to[k], ordinal, ignoring case; the same instance when nothing matched.</summary>
        internal static string Scrub(string text, string[] from, string[] to)
        {
            if (text == null || from == null || to == null) return text;
            for (int k = 0; k < from.Length && k < to.Length; k++)
            {
                string f = from[k];
                if (string.IsNullOrEmpty(f)) continue;
                int at = text.IndexOf(f, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                StringBuilder b = new StringBuilder(text.Length);
                int start = 0;
                while (at >= 0)
                {
                    b.Append(text, start, at - start).Append(to[k]);
                    start = at + f.Length;
                    at = start < text.Length ? text.IndexOf(f, start, StringComparison.OrdinalIgnoreCase) : -1;
                }
                b.Append(text, start, text.Length - start);
                text = b.ToString();
            }
            return text;
        }

        /// <summary>
        /// Wayfinder's rule for its log: numbers that could be a position are written as '#'. First every number with a
        /// fraction (as the expression -?\d+\.\d+ finds them), then two or three whole numbers joined by commas that do
        /// not follow a letter, a digit, '_', '.' or '#' (-?\d+\s*,\s*-?\d+(\s*,\s*-?\d+)? with that look-behind). In that
        /// order, so "(123.45, 30.00, -987.65)" becomes "(#, #, #)". Stack frames ("[0x0012c] in &lt;8a2f3b&gt;:0"),
        /// key names ("F11", "Alpha1") and a world's id stay as they are. Written by hand: no regular expressions.
        /// </summary>
        internal static string HidePositions(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = ReplaceMatches(text, false);
            return ReplaceMatches(text, true);
        }

        private static string ReplaceMatches(string text, bool commaGroups)
        {
            StringBuilder b = null;
            int copied = 0;
            for (int s = 0; s < text.Length; )
            {
                int end = commaGroups ? MatchCommaGroup(text, s) : MatchFraction(text, s);
                if (end < 0) { s++; continue; }
                if (b == null) b = new StringBuilder(text.Length);
                b.Append(text, copied, s - copied).Append('#');
                copied = end;
                s = end;
            }
            if (b == null) return text;
            b.Append(text, copied, text.Length - copied);
            return b.ToString();
        }

        // -?\d+ at i: the index after it, or -1.
        private static int Integer(string text, int i)
        {
            if (i < text.Length && text[i] == '-') i++;
            int start = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return i > start ? i : -1;
        }

        // -?\d+\.\d+ at s.
        private static int MatchFraction(string text, int s)
        {
            int i = Integer(text, s);
            if (i < 0 || i >= text.Length || text[i] != '.') return -1;
            int start = ++i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return i > start ? i : -1;
        }

        // (?<![\w.#])-?\d+\s*,\s*-?\d+(\s*,\s*-?\d+)? at s.
        private static int MatchCommaGroup(string text, int s)
        {
            if (s > 0)
            {
                char p = text[s - 1];
                if (char.IsLetterOrDigit(p) || p == '_' || p == '.' || p == '#') return -1;
            }
            int i = Integer(text, s);
            if (i < 0) return -1;
            i = CommaThenInteger(text, i);
            if (i < 0) return -1;
            int third = CommaThenInteger(text, i);
            return third < 0 ? i : third;
        }

        // \s*,\s*-?\d+ at i.
        private static int CommaThenInteger(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != ',') return -1;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return Integer(text, i);
        }
    }

    internal enum LineDecision { Write, NoticeThenDrop, Drop, FinalThenStop }

    /// <summary>The caps. Used only under LogFile's lock; single-threaded here.</summary>
    internal sealed class LineBudget
    {
        private long _file, _section, _dropped;
        private bool _noticed, _stopped;

        internal LineBudget(long existingBytes)
        {
            _file = existingBytes > 0 ? existingBytes : 0;            // an appended file counts what it holds
            _stopped = _file >= LogRules.FileCap - LogRules.Reserve;
        }

        internal bool Stopped { get { return _stopped; } }
        internal long FileBytes { get { return _file; } }

        /// <summary>An ordinary or problem line.</summary>
        internal LineDecision Decide(int level, int bytes)
        {
            if (_stopped) return LineDecision.Drop;
            if (_file + bytes > LogRules.FileCap - LogRules.Reserve) return LineDecision.FinalThenStop;
            if ((level & LogRules.Problems) == 0 && _section + bytes > LogRules.SectionCap)
            {
                _dropped++;
                if (_noticed) return LineDecision.Drop;
                _noticed = true;
                return LineDecision.NoticeThenDrop;
            }
            return LineDecision.Write;
        }

        /// <summary>Headers, notes, repeat counts and the last line: never cut by the section cap.</summary>
        internal LineDecision DecideNotice(int bytes)
        {
            if (_stopped) return LineDecision.Drop;
            if (_file + bytes > LogRules.FileCap) { _stopped = true; return LineDecision.Drop; }
            return LineDecision.Write;
        }

        internal void Wrote(int level, int bytes, bool notice)
        {
            _file += bytes;
            if (!notice && (level & LogRules.Problems) == 0) _section += bytes;
        }

        internal void Stop() { _stopped = true; }

        /// <summary>Starts a stretch of VerboseLog; returns how many ordinary lines the last one left out.</summary>
        internal long StartSection()
        {
            long d = _dropped;
            _section = 0;
            _dropped = 0;
            _noticed = false;
            return d;
        }
    }

    /// <summary>Consecutive identical lines are counted, not written. Used only under LogFile's lock.</summary>
    internal sealed class RepeatCollapse
    {
        internal const long QuietTicks = 2 * TimeSpan.TicksPerSecond;   // a burst that ended
        internal const long BurstTicks = 60 * TimeSpan.TicksPerSecond;  // once a minute during a burst
        private readonly bool _withCount;
        private int _level;
        private string _source, _text;
        private int _count;
        private DateTime _first, _last;

        /// <param name="withCount">False for Wayfinder: its summary says a line came again, never how often.</param>
        internal RepeatCollapse(bool withCount)
        {
            _withCount = withCount;
        }

        internal bool Pending { get { return _count > 0; } }
        internal int Level { get { return _level; } }
        internal string Source { get { return _source; } }

        internal bool Repeats(int level, string source, string text, DateTime now)
        {
            if (_text == null || level != _level || !string.Equals(text, _text, StringComparison.Ordinal)
                || !string.Equals(source, _source, StringComparison.Ordinal)) return false;
            if (_count == 0) _first = now;
            _count++;
            _last = now;
            return true;
        }

        /// <summary>Names the line it counts (a summary written earlier may now stand between them); counting goes on.</summary>
        internal string TakeSummary()
        {
            if (_count == 0) return null;
            string s = _withCount
                ? "(repeated " + _count.ToString(CultureInfo.InvariantCulture) + (_count == 1 ? " more time" : " more times")
                  + " until " + _last.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + ": " + Head(_text, 80) + ")"
                : "(repeated; repeats are not written: " + Head(_text, 80) + ")";
            _count = 0;
            return s;
        }

        internal string TakeSummaryIfDue(DateTime now)
        {
            if (_count == 0) return null;
            if (now.Ticks - _last.Ticks >= QuietTicks || now.Ticks - _first.Ticks >= BurstTicks) return TakeSummary();
            return null;
        }

        internal void Wrote(int level, string source, string text)
        {
            _level = level;
            _source = source;
            _text = text;
            _count = 0;
        }

        internal void Forget()
        {
            _text = null;
            _source = null;
            _count = 0;
        }

        private static string Head(string text, int n)
        {
            if (text == null) return string.Empty;
            int cut = text.IndexOfAny(new char[] { '\r', '\n' });
            if (cut < 0) cut = text.Length;
            if (cut > n) cut = n;
            return text.Substring(0, cut);
        }
    }
}
