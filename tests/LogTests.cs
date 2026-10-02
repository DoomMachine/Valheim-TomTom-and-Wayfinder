using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Waypointer
{
    // The log file's Unity-free parts: LogRules (what is admitted, file names, how a line is written, what a line may not
    // show, the caps, repeats) and LogRotation (on a temporary folder). Compiled as TomTom, so LogDetail.Verbose exists
    // here; Wayfinder's lack of it is checked by preflight against the built DLL.
    public static partial class TestMain
    {
        private static void LogTests()
        {
            LogAdmitTests();
            LogNameTests();
            LogLineTests();
            LogScrubTests();
            LogHidePositionsTests();
            LogBudgetTests();
            LogRepeatTests();
            LogRotationTests();

            // The unreadable-lines warning counts only lines meant to hold a waypoint (RouteFile.IsDataLine).
            Check("route lines: the header, comments and blank lines are not data",
                !RouteFile.IsDataLine(RouteFile.Header("TomTom")) && !RouteFile.IsDataLine("  # note") && !RouteFile.IsDataLine("")
                && !RouteFile.IsDataLine("   ") && !RouteFile.IsDataLine(null), "");
            Check("route lines: a waypoint, or garbage where one should be, is data",
                RouteFile.IsDataLine("100|35|200|1|1|Camp") && RouteFile.IsDataLine("garbage") && RouteFile.IsDataLine(" 1|2 "), "");
        }

        private static void LogAdmitTests()
        {
            int[] levels = { 1, 2, 4, 8, 16, 32, 63 };
            bool offNone = true;
            for (int i = 0; i < levels.Length; i++) if (LogRules.Admits(levels[i], LogDetail.Off)) offNone = false;
            Check("log: Off admits nothing, Fatal included", offNone, "");

            Check("log: Errors admits Fatal, Error, Warning",
                LogRules.Admits(1, LogDetail.Errors) && LogRules.Admits(2, LogDetail.Errors) && LogRules.Admits(4, LogDetail.Errors)
                && LogRules.Admits(2 | 16, LogDetail.Errors), "");
            Check("log: Errors leaves out Message, Info, Debug and unknown levels",
                !LogRules.Admits(8, LogDetail.Errors) && !LogRules.Admits(16, LogDetail.Errors) && !LogRules.Admits(32, LogDetail.Errors)
                && !LogRules.Admits(0, LogDetail.Errors) && !LogRules.Admits(64, LogDetail.Errors), "");

            bool verboseAll = true;
            for (int i = 0; i < 6; i++) if (!LogRules.Admits(1 << i, LogDetail.Verbose)) verboseAll = false;
            Check("log: Verbose admits every level", verboseAll && !LogRules.Admits(0, LogDetail.Verbose)
                && !LogRules.Admits(64, LogDetail.Verbose), "");

            LogDetail three = (LogDetail)3, minus = (LogDetail)(-1);
            Check("log: a value the enum does not name behaves as Errors",
                LogRules.Admits(2, three) && !LogRules.Admits(16, three) && LogRules.Admits(2, minus) && !LogRules.Admits(16, minus)
                && !LogRules.IsVerbose(three) && !LogRules.IsVerbose(minus), "");
            Check("log: IsVerbose only for Verbose",
                LogRules.IsVerbose(LogDetail.Verbose) && !LogRules.IsVerbose(LogDetail.Errors) && !LogRules.IsVerbose(LogDetail.Off), "");

            Check("log: the two settings make the detail",
                LogRules.Detail(true, false) == LogDetail.Errors && LogRules.Detail(false, false) == LogDetail.Off
                && LogRules.Detail(true, true) == LogDetail.Verbose && LogRules.Detail(false, true) == LogDetail.Verbose, "");
            Check("log: the detail as words",
                LogRules.DetailText(LogDetail.Errors) == "ErrorLog on, VerboseLog off"
                && LogRules.DetailText(LogDetail.Off) == "ErrorLog off, VerboseLog off"
                && LogRules.DetailText(LogDetail.Verbose) == "VerboseLog on", LogRules.DetailText(LogDetail.Errors));

            Check("log: level constants are BepInEx's",
                LogRules.Fatal == 1 && LogRules.Error == 2 && LogRules.Warning == 4 && LogRules.Message == 8 && LogRules.Info == 16
                && LogRules.Debug == 32 && LogRules.All == 63 && LogRules.Problems == 7, "");

            Check("log: level tags",
                LogRules.LevelTag(1) == "FATAL" && LogRules.LevelTag(2) == "ERROR" && LogRules.LevelTag(4) == "WARN "
                && LogRules.LevelTag(8) == "INFO " && LogRules.LevelTag(16) == "INFO " && LogRules.LevelTag(32) == "DEBUG"
                && LogRules.LevelTag(2 | 16) == "ERROR", "");

            Check("log: a game error is ours by its stack trace",
                LogRules.IsOurStack("  at Waypointer.MapPatches.X ()") && !LogRules.IsOurStack("  at WaypointerX.Y ()")
                && !LogRules.IsOurStack("  at MobTracker.X ()") && !LogRules.IsOurStack("") && !LogRules.IsOurStack(null), "");
        }

        private static void LogNameTests()
        {
            Check("log: file names",
                LogRules.FileName("TomTom", false) == "TomTom.log" && LogRules.FileName("TomTom", true) == "TomTom-prev.log"
                && LogRules.FallbackName("TomTom", 3) == "TomTom.log.3" && LogRules.StageName("TomTom") == "TomTom-prev.log.new",
                LogRules.FallbackName("TomTom", 3));
            System.Collections.Generic.List<string> all = new System.Collections.Generic.List<string>();
            string[] editions = { "TomTom", "Wayfinder" };
            for (int e = 0; e < editions.Length; e++)
            {
                all.Add(LogRules.FileName(editions[e], false));
                all.Add(LogRules.FileName(editions[e], true));
                all.Add(LogRules.StageName(editions[e]));
                for (int n = 1; n <= LogRules.Fallbacks; n++) all.Add(LogRules.FallbackName(editions[e], n));
            }
            bool distinct = true, safe = true;
            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++) if (string.Equals(all[i], all[j], StringComparison.OrdinalIgnoreCase)) distinct = false;
                string s = all[i];
                if (s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0 || s.IndexOf(':') >= 0 || s.IndexOf("..", StringComparison.Ordinal) >= 0
                    || string.Equals(s, "LogOutput.log", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("LogOutput", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("waypoints_", StringComparison.OrdinalIgnoreCase)) safe = false;
            }
            Check("log: every name is distinct, a bare file name, and never LogOutput or a route", distinct && safe && all.Count == 14, "");
        }

        private static void LogLineTests()
        {
            DateTime t = new DateTime(2026, 10, 2, 9, 5, 7, 42);
            string line = LogRules.FormatLine(t, 123, "ERROR", "TomTom", "boom");
            Check("log line: format", line == "2026-10-02 09:05:07.042  f123  ERROR  TomTom  boom", line);
            line = LogRules.FormatLine(t, -1, "INFO ", "Game", "x");
            Check("log line: no frame off the main thread", line == "2026-10-02 09:05:07.042  f-  INFO   Game  x", line);

            Check("log line: offsets",
                LogRules.FormatOffset(new TimeSpan(3, 0, 0)) == "UTC+03:00" && LogRules.FormatOffset(new TimeSpan(-5, -30, 0)) == "UTC-05:30"
                && LogRules.FormatOffset(TimeSpan.Zero) == "UTC+00:00", LogRules.FormatOffset(new TimeSpan(-5, -30, 0)));

            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            string[] cultures = { "ar-SA", "fa-IR", "de-DE", "th-TH" };
            bool invariant = true;
            string seen = "";
            for (int i = 0; i < cultures.Length; i++)
            {
                try
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(cultures[i]);
                    string l = LogRules.FormatLine(t, 123, "ERROR", "TomTom", "boom");
                    if (l != "2026-10-02 09:05:07.042  f123  ERROR  TomTom  boom") { invariant = false; seen = cultures[i] + ": " + l; }
                }
                catch (CultureNotFoundException) { }
                finally { Thread.CurrentThread.CurrentCulture = saved; }
            }
            Check("log line: the same under any culture (digits, separators, calendar)", invariant, seen);

            string nl = Environment.NewLine + LogRules.Indent;
            string body;
            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\r\nb"));
            bool cont = body == "a" + nl + "b";
            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\rb"));
            cont = cont && body == "a" + nl + "b";
            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\nb"));
            cont = cont && body == "a" + nl + "b";
            Check("log line: a line break becomes an indented continuation", cont, body);
            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\n"));
            string body2 = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\r\n\r\n"));
            Check("log line: trailing line breaks are dropped", body == "a" && body2 == "a", body2);

            string forged = LogRules.FormatLine(t, 1, "INFO ", "s", "x\r\n2026-10-02 10:00:00.000  f1  ERROR  TomTom  forged");
            string[] parts = forged.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
            bool noForgery = parts.Length == 2 && parts[1].StartsWith(LogRules.Indent, StringComparison.Ordinal);
            Check("log line: a text cannot start a line of its own", noForgery, forged);

            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", "a\u0000b\u001bc\u007fd\u0085e\u2028f\u2029g\th"));
            Check("log line: control characters escaped, a tab kept",
                body == "a\\u0000b\\u001bc\\u007fd\\u0085e\\u2028f\\u2029g\th", body);

            line = LogRules.FormatLine(t, 1, "INFO ", null, null);
            Check("log line: no text and no source", line == "2026-10-02 09:05:07.042  f1  INFO     ", "'" + line + "'");

            string longText = new string('x', 100000);
            body = Body(LogRules.FormatLine(t, 1, "INFO ", "s", longText));
            Check("log line: a long text is cut, and says so",
                body == new string('x', LogRules.MaxTextChars) + " ... (67232 more characters)", body.Length.ToString());
        }

        // The text part of a formatted line: after the fifth field's two spaces ("date time  fN  TAG  source  ").
        private static string Body(string line)
        {
            int at = 0;
            for (int k = 0; k < 4; k++) at = line.IndexOf("  ", at, StringComparison.Ordinal) + 2;
            return line.Substring(at);
        }

        private static void LogScrubTests()
        {
            string[] from, to;
            LogRules.ScrubPairs(@"D:\Games\Valheim\BepInEx\config", @"D:\Games\Valheim\BepInEx",
                @"D:\Games\Valheim", @"X:\Profiles\Some One", @"X:\Profiles\SOMEON~1", out from, out to);
            string s = LogRules.Scrub(@"Could not find d:\games\valheim\BepInEx\config\DoomMachine.TomTom\waypoints_1.txt", from, to);
            Check("log scrub: the config folder, ignoring case", s == @"Could not find <config>\DoomMachine.TomTom\waypoints_1.txt", s);
            s = LogRules.Scrub("at D:/Games/Valheim/BepInEx/plugins/x.dll and D:/Games/Valheim/valheim_Data", from, to);
            Check("log scrub: '/' paths, several, longest first", s == "at <BepInEx>/plugins/x.dll and <game>/valheim_Data", s);
            s = LogRules.Scrub(@"X:\Profiles\Some One\AppData\LocalLow\IronGate and X:\Profiles\SOMEON~1\AppData\Local\Temp", from, to);
            Check("log scrub: the profile folder and its short form", s == @"<home>\AppData\LocalLow\IronGate and <home>\AppData\Local\Temp", s);
            string plain = "nothing to hide";
            Check("log scrub: unchanged text is the same instance", ReferenceEquals(LogRules.Scrub(plain, from, to), plain), "");
            Check("log scrub: null text", LogRules.Scrub(null, from, to) == null, "");

            LogRules.ScrubPairs(@"C:\", "/a/", null, null, null, out from, out to);
            Check("log scrub: a folder shorter than 4 characters is never replaced", from.Length == 0, from.Length.ToString());
            LogRules.ScrubPairs(null, @"D:\Games\Valheim\BepInEx\", null, null, null, out from, out to);
            s = LogRules.Scrub(@"D:\Games\Valheim\BepInEx\TomTom.log", from, to);
            Check("log scrub: trailing separators trimmed", s == @"<BepInEx>\TomTom.log", s);

            Check("log scrub: the short profile folder from the temporary folder",
                LogRules.HomeFromTempPath(@"X:\Profiles\SOMEON~1\AppData\Local\Temp\") == @"X:\Profiles\SOMEON~1"
                && LogRules.HomeFromTempPath("/tmp/") == null && LogRules.HomeFromTempPath(null) == null, "");
        }

        private static void LogHidePositionsTests()
        {
            string s = LogRules.HidePositions("The given key '(123.45, 30.00, -987.65)' was not present in the dictionary.");
            Check("log hide: a Vector3 in a message", s == "The given key '(#, #, #)' was not present in the dictionary.", s);
            s = LogRules.HidePositions("An item with the same key has already been added. Key: 12,-5");
            Check("log hide: two integers joined by a comma", s == "An item with the same key has already been added. Key: #", s);
            s = LogRules.HidePositions("at 1 , 2 , 3 then");
            Check("log hide: three integers with spaces", s == "at # then", s);
            string[] same = {
                "ToggleWindowKey = F11", "Saved route of world 1234567890: some lines could not be read",
                "  at Waypointer.LocationSearch.Finish () [0x0012c] in <8a2f3b>:0",
                "Func`3[Minimap,UnityEngine.Vector3,System.Boolean]", "Alpha1", "" };
            bool kept = true;
            string changed = "";
            for (int i = 0; i < same.Length; i++)
                if (LogRules.HidePositions(same[i]) != same[i]) { kept = false; changed = same[i] + " -> " + LogRules.HidePositions(same[i]); }
            Check("log hide: frames, key names and a world id stay", kept, changed);
            Check("log hide: null", LogRules.HidePositions(null) == null, "");
            s = LogRules.HidePositions("v1.2.3 and x-12,5 and a12,5 and 1,2,3,4");
            // As the two regular expressions would: "x-12,5" -> the look-behind sees '-', so "12,5" goes; "a12,5" stays
            // (glued to a word); "1,2,3,4" keeps its fourth number.
            Check("log hide: the expressions' rules at the edges", s == "v#.3 and x-# and a12,5 and #,4", s);
        }

        private static void LogBudgetTests()
        {
            LineBudget b = new LineBudget(0);
            long written = 0;
            int writes = 0;
            LineDecision d;
            while ((d = b.Decide(LogRules.Info, 200)) == LineDecision.Write) { b.Wrote(LogRules.Info, 200, false); written += 200; writes++; }
            Check("log budget: ordinary lines up to the section cap", d == LineDecision.NoticeThenDrop && written <= LogRules.SectionCap
                && written + 200 > LogRules.SectionCap, written.ToString());
            Check("log budget: one notice, then dropped", b.Decide(LogRules.Info, 200) == LineDecision.Drop, "");
            Check("log budget: warnings still written past the section cap", b.Decide(LogRules.Warning, 200) == LineDecision.Write, "");
            long dropped = b.StartSection();
            Check("log budget: a new section reports what was left out, and writes again",
                dropped == 2 && b.Decide(LogRules.Info, 200) == LineDecision.Write, dropped.ToString());

            b = new LineBudget(0);
            int finals = 0;
            bool afterOnlyDrop = true, stopped = false;
            long total = 0;
            for (int i = 0; i < 1000000; i++)
            {
                d = b.Decide(LogRules.Error, 200);
                if (d == LineDecision.Write) { if (stopped) afterOnlyDrop = false; b.Wrote(LogRules.Error, 200, false); total += 200; }
                else if (d == LineDecision.FinalThenStop) { finals++; stopped = true; if (b.DecideNotice(LogRules.Reserve) == LineDecision.Write) total += LogRules.Reserve; b.Stop(); }
                else if (d != LineDecision.Drop) afterOnlyDrop = false;
            }
            Check("log budget: the file cap is reached once, then nothing", finals == 1 && afterOnlyDrop && total <= LogRules.FileCap,
                finals + " " + total);

            Check("log budget: a full file stays full", new LineBudget(LogRules.FileCap).Stopped, "");
            b = new LineBudget(LogRules.FileCap - LogRules.Reserve - 100);
            Check("log budget: an appended file counts what it holds", b.Decide(LogRules.Info, 200) == LineDecision.FinalThenStop, "");
            b = new LineBudget(-5);
            Check("log budget: a negative length counts as empty", !b.Stopped && b.FileBytes == 0, "");
        }

        private static void LogRepeatTests()
        {
            DateTime t0 = new DateTime(2026, 10, 2, 12, 0, 0);
            RepeatCollapse r = new RepeatCollapse(true);
            Check("log repeats: nothing to repeat at first", !r.Repeats(4, "TomTom", "x", t0), "");
            r.Wrote(4, "TomTom", "x");
            bool all = true;
            for (int i = 1; i <= 4; i++) if (!r.Repeats(4, "TomTom", "x", t0.AddMilliseconds(100 * i))) all = false;
            string sum = r.TakeSummary();
            Check("log repeats: counted, with the time of the last", all && sum == "(repeated 4 more times until 12:00:00.400: x)", sum);
            Check("log repeats: counting goes on", r.Repeats(4, "TomTom", "x", t0.AddSeconds(1)) && r.TakeSummary() == "(repeated 1 more time until 12:00:01.000: x)", "");
            Check("log repeats: another level, source or text is not a repeat",
                !r.Repeats(2, "TomTom", "x", t0) && !r.Repeats(4, "Game", "x", t0) && !r.Repeats(4, "TomTom", "y", t0), "");
            r.Forget();
            Check("log repeats: forgotten", !r.Repeats(4, "TomTom", "x", t0) && r.TakeSummary() == null, "");

            r = new RepeatCollapse(true);
            r.Wrote(4, "TomTom", "x");
            r.Repeats(4, "TomTom", "x", t0);
            Check("log repeats: not due 1 s after the last", r.TakeSummaryIfDue(t0.AddSeconds(1)) == null, "");
            Check("log repeats: due 2 s after the last", r.TakeSummaryIfDue(t0.AddSeconds(2)) != null, "");
            r.Wrote(4, "TomTom", "x");
            string due = null;
            for (int i = 0; i <= 3800 && due == null; i++)
            {
                DateTime now = t0.AddMilliseconds(16 * i);
                r.Repeats(4, "TomTom", "x", now);
                due = r.TakeSummaryIfDue(now);
            }
            Check("log repeats: due once a minute during a burst", due != null, "");

            RepeatCollapse w = new RepeatCollapse(false);
            w.Wrote(2, "Wayfinder", "boom\nStack trace:\nat X");
            w.Repeats(2, "Wayfinder", "boom\nStack trace:\nat X", t0);
            w.Repeats(2, "Wayfinder", "boom\nStack trace:\nat X", t0);
            sum = w.TakeSummary();
            Check("log repeats: without a count (Wayfinder)", sum == "(repeated; repeats are not written: boom)", sum);
        }

        private static void LogRotationTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypointer-logrotation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;   // file sharing is enforced
            string plain = Path.Combine(dir, "TomTom.log"), prev = Path.Combine(dir, "TomTom-prev.log");
            string stage = Path.Combine(dir, "TomTom-prev.log.new");
            try
            {
                FileMode mode;
                LogRotation.Outcome o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: nothing there", o == LogRotation.Outcome.NothingToRotate && mode == FileMode.Create
                    && Directory.GetFiles(dir).Length == 0, o + " " + mode);

                File.WriteAllText(plain, "last");
                o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: the last session's file becomes -prev", o == LogRotation.Outcome.Rotated && mode == FileMode.Create
                    && !File.Exists(plain) && !File.Exists(stage) && File.ReadAllText(prev) == "last", o + " " + mode);

                File.WriteAllText(plain, "newer");
                File.WriteAllText(Path.Combine(dir, "TomTom.log.2"), "stale second copy");
                o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: an older -prev is replaced, a second copy's numbered file is kept",
                    o == LogRotation.Outcome.Rotated && File.ReadAllText(prev) == "newer" && !File.Exists(stage)
                    && File.ReadAllText(Path.Combine(dir, "TomTom.log.2")) == "stale second copy", o.ToString());
                File.Delete(Path.Combine(dir, "TomTom.log.2"));

                if (windows)
                {
                    File.WriteAllText(plain, "held by a writer");
                    long started = DateTime.UtcNow.Ticks;
                    using (FileStream held = new FileStream(plain, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        o = LogRotation.Rotate(dir, "TomTom", out mode);
                    }
                    double ms = (DateTime.UtcNow.Ticks - started) / (double)TimeSpan.TicksPerMillisecond;
                    Check("log rotation: a file another game writes is left alone, and so is -prev",
                        o == LogRotation.Outcome.PlainHeld && mode == FileMode.Append && File.ReadAllText(prev) == "newer"
                        && File.ReadAllText(plain) == "held by a writer" && !File.Exists(stage) && ms < 2000, o + " " + mode + " " + ms);

                    using (FileStream reader = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        o = LogRotation.Rotate(dir, "TomTom", out mode);
                    }
                    Check("log rotation: a file a reader holds is appended to, never emptied",
                        o == LogRotation.Outcome.PlainHeld && mode == FileMode.Append && File.ReadAllText(plain) == "held by a writer"
                        && File.ReadAllText(prev) == "newer", o + " " + mode);

                    using (FileStream heldPrev = new FileStream(prev, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        o = LogRotation.Rotate(dir, "TomTom", out mode);
                        string prevNow;
                        using (StreamReader sr = new StreamReader(heldPrev)) prevNow = sr.ReadToEnd();
                        Check("log rotation: a -prev that cannot be replaced is kept, and the last session's file put back",
                            o == LogRotation.Outcome.PrevKept && mode == FileMode.Append && prevNow == "newer" && !File.Exists(stage),
                            o + " " + mode);
                    }
                    Check("log rotation: ... with its lines", File.ReadAllText(plain) == "held by a writer", "");
                }

                File.Delete(plain);
                File.Delete(prev);
                File.WriteAllText(stage, "stopped mid-rotation");
                o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: a lone stage file becomes -prev", o == LogRotation.Outcome.NothingToRotate
                    && File.ReadAllText(prev) == "stopped mid-rotation" && !File.Exists(stage), o.ToString());

                File.WriteAllText(stage, "older");
                File.WriteAllText(plain, "newest");
                o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: a stage file beside a newer plain one is dropped; the plain one becomes -prev",
                    o == LogRotation.Outcome.Rotated && File.ReadAllText(prev) == "newest" && !File.Exists(stage), o.ToString());

                File.WriteAllText(stage, "newer than prev");
                o = LogRotation.Rotate(dir, "TomTom", out mode);
                Check("log rotation: a stage file without a plain one replaces -prev",
                    o == LogRotation.Outcome.NothingToRotate && File.ReadAllText(prev) == "newer than prev" && !File.Exists(stage), o.ToString());

                o = LogRotation.Rotate(Path.Combine(dir, "missing folder"), "TomTom", out mode);
                Check("log rotation: a folder that is not there throws nothing", o == LogRotation.Outcome.NothingToRotate, o.ToString());
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
