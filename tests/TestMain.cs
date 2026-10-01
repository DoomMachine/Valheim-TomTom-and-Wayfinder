using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace Waypointer
{
    public static class TestMain
    {
        private static int _failures;

        public static int Main(string[] args)
        {
            // expectation format: "OK A B [E] name" or "FAIL"
            Expect("1234, -567", 1234f, -567f, false, 0f, "");
            Expect("1234 -567", 1234f, -567f, false, 0f, "");
            Expect("1234;-567", 1234f, -567f, false, 0f, "");
            Expect("1234, -567, 30", 1234f, -567f, true, 30f, "");
            Expect("(1234, -567)", 1234f, -567f, false, 0f, "");
            Expect("[1234, -567, -12]", 1234f, -567f, true, -12f, "");
            Expect("12.5, 13.25", 12.5f, 13.25f, false, 0f, "");
            Expect("-1234.5,-567.25", -1234.5f, -567.25f, false, 0f, "");
            Expect("1234, -567, Silver vein", 1234f, -567f, false, 0f, "Silver vein");
            Expect("1234, -567, 30, Silver vein", 1234f, -567f, true, 30f, "Silver vein");
            Expect("Stone circle: 1234, -567", 1234f, -567f, false, 0f, "Stone circle");
            Expect("Camp 2: 1234, -567", 1234f, -567f, false, 0f, "Camp 2");
            Expect("1234, -567, Camp 2", 1234f, -567f, false, 0f, "Camp 2");
            Expect("x=1234 y=-567", 1234f, -567f, false, 0f, "");
            // Labelled triple: y is Valheim's altitude, z is the north/south axis.
            Expect("x 1234 y 30 z -567", 1234f, -567f, true, 30f, "");
            Expect("  1234 , -567  ", 1234f, -567f, false, 0f, "");

            // Explicit axis labels must be honoured literally: Valheim's y is ALTITUDE, so a labelled
            // "Y=30" is the elevation, not the north/south axis.
            Expect("X=-500 Y=30 Z=200", -500f, 200f, true, 30f, "");
            Expect("x:-500, y:30, z:200", -500f, 200f, true, 30f, "");
            Expect("x=1234 z=-567", 1234f, -567f, false, 0f, "");
            // The form in-game and mod coordinate readouts produce.
            Expect("X: 1234 Y: 56 Z: -789", 1234f, -789f, true, 56f, "");

            // Names before the numbers, and names that begin with a word that parses as a float.
            Expect("Silver vein 1234 -567", 1234f, -567f, false, 0f, "Silver vein");
            Expect("1234, -567, Infinity Tower", 1234f, -567f, false, 0f, "Infinity Tower");
            Expect("Camp: North: 100, 200", 100f, 200f, false, 0f, "Camp");
            Expect("42: 123, 456", 123f, 456f, false, 0f, "42");

            // Digit grouping is ambiguous enough to be worth refusing outright.
            ExpectFail("1,234,567");

            // ...but deliberate, space-separated fields are still fine.
            Expect("1, 234, 567", 1f, 234f, true, 567f, "");

            ExpectFail("abc");
            ExpectFail("1234");
            ExpectFail("");
            ExpectFail("999999, 1");
            ExpectFail("1, NaN");

            // Typographic minus/dash and full-width (IME) input must not shift the axes.
            Expect("\u22121234, 567, 30", -1234f, 567f, true, 30f, "");
            Expect("Camp: \u22121234, 567, 30", -1234f, 567f, true, 30f, "Camp");
            Expect("1234, \u2013567", 1234f, -567f, false, 0f, "");
            Expect("\u20141234, 567, 30", -1234f, 567f, true, 30f, "");   // em dash
            Expect("\u20101234, 567", -1234f, 567f, false, 0f, "");        // hyphen
            Expect("1234, \u2011567", 1234f, -567f, false, 0f, "");        // non-breaking hyphen
            Expect("x 1234 y 30 z \u2212567", 1234f, -567f, true, 30f, "");
            Expect("\uFF11\uFF12\uFF13\uFF14\uFF0C-567", 1234f, -567f, false, 0f, "");
            Expect("Camp\uFF1A\u22121234, 567, 30", -1234f, 567f, true, 30f, "Camp");
            Expect("Camp \u2013 North: 1234, -567", 1234f, -567f, false, 0f, "Camp \u2013 North");
            ExpectFail("\uFF11\uFF0C\uFF12\uFF13\uFF14\uFF0C\uFF15\uFF16\uFF17");

            // No-break, thin and ideographic spaces separate fields like a plain space...
            Expect("1234\u00A0-567", 1234f, -567f, false, 0f, "");
            Expect("1234,\u00A0-567", 1234f, -567f, false, 0f, "");
            Expect("1234\u00A0-567\u00A0100", 1234f, -567f, true, 100f, "");
            Expect("1234\u3000-567", 1234f, -567f, false, 0f, "");
            Expect("1234\u2009-567", 1234f, -567f, false, 0f, "");
            Expect("100\u00A0200", 100f, 200f, false, 0f, "");
            // ...but one used as locale digit grouping ("1 234") is refused, not read as two fields.
            ExpectFail("1\u00A0234, -567, 30");
            ExpectFail("10\u202F500, 200");

            // x and y without z are the two map axes, bound by name whatever order they are written in.
            Expect("y=-567 x=1234", 1234f, -567f, false, 0f, "");
            Expect("Y: 30, X: -500 Camp", -500f, 30f, false, 0f, "Camp");

            // Behaviour that already holds and is worth pinning down.
            ExpectFail("500,300");                       // comma + exactly three digits reads as grouping
            Expect("Camp: 123, 456", 123f, 456f, false, 0f, "Camp");
            Expect("Silver vein 1234 -567 30", 1234f, -567f, true, 30f, "Silver vein");
            Expect("+1234, +567", 1234f, 567f, false, 0f, "");
            Expect("1e3, -2E3", 1000f, -2000f, false, 0f, "");
            Expect("1234\t-567\tCamp", 1234f, -567f, false, 0f, "Camp");
            Expect("1234, -567, nan", 1234f, -567f, false, 0f, "nan");

            // A comma between two digits that could be a decimal comma (1.4.1): refused when reading it as one names
            // another place; everything that parsed before and is not ambiguous reads as before.
            ExpectFail("1234,5; -567,25"); ExpectFail("1234,5 -567,25"); ExpectFail("1234,5\t-567,25\t30");
            ExpectFail("12,5 30"); ExpectFail("1234 -567,30"); ExpectFail("100,20, 30"); ExpectFail("(1234,57, -567,25, 30,12)");
            ExpectFail("Camp: 1234,5; -567,25"); ExpectFail("Mine 1,2 1234 -567"); ExpectFail("x 12,5 z 30");
            Expect("1234,-567,30", 1234f, -567f, true, 30f, ""); Expect("12,34", 12f, 34f, false, 0f, "");
            Expect("100,20,30", 100f, 20f, true, 30f, ""); Expect("12.5,13", 12.5f, 13f, false, 0f, "");
            Expect("1,5 12.5", 1f, 5f, true, 12.5f, ""); Expect("12, 5 30", 12f, 5f, true, 30f, "");
            Expect("1234 -567 Tower 1,2", 1234f, -567f, false, 0f, "Tower 1 2"); Expect("Camp 1,2: 100, 200", 100f, 200f, false, 0f, "Camp 1,2");
            ParsedCoord pcComma;
            string errComma;
            Check("parse: the decimal-comma refusal gives the remedy first",
                !CoordinateParser.TryParseOne("12,5 30", out pcComma, out errComma) && errComma != null && errComma.StartsWith("use a dot", StringComparison.Ordinal), errComma);
            Check("parse: a comma that could be a decimal comma is refused as such, not as a fourth number",
                !CoordinateParser.TryParseOne("1234,5; -567,25", out pcComma, out errComma) && errComma != null
                && errComma.Contains("a comma between two digits could be either"), errComma);
            // More than three numbers: a value split by a decimal comma, or a number meant as a name (1.4.1).
            ExpectFail("1, 2, 3, 4"); ExpectFail("1234, -567, 30, 2"); ExpectFail("1234,5,-567,25");
            Expect("2: 1234, -567, 30", 1234f, -567f, true, 30f, "2");
            // A leading word that starts like a number is a mistyped number, not a name (1.4.1).
            ExpectFail("1234m, -567, 20"); ExpectFail("-1234m, 567, 20"); ExpectFail("1e39, 1, 2"); ExpectFail("1.2.3 100 200"); ExpectFail("12a 34 56");
            ExpectFail(".5km 100 200"); ExpectFail("Camp: 1234m, -567, 20"); ExpectFail("x 1234m y 30 z -567"); ExpectFail("2nd camp 1234 -567");
            Expect("2nd camp: 1234, -567", 1234f, -567f, false, 0f, "2nd camp"); Expect("Camp2 1234 -567", 1234f, -567f, false, 0f, "Camp2");
            Expect("T2 portal 1234 -567", 1234f, -567f, false, 0f, "T2 portal"); Expect("Area51 1234 -567", 1234f, -567f, false, 0f, "Area51");
            Expect("Infinity Tower 100 200", 100f, 200f, false, 0f, "Infinity Tower"); Expect("-Infinity 100 200", 100f, 200f, false, 0f, "-Infinity");
            Expect("1234, -567, 2nd camp", 1234f, -567f, false, 0f, "2nd camp");

            // The game's own 'pos' line and Server Devcommands' (1.5.0): read in the axis order the header names, in either
            // mode; a decimal comma is read only inside the vector, which both write with ", " between the values.
            ExpectPos("Player position (X,Y,Z): (1234, 30, -567) , Zone: 19,-9, Center dist: 1358.03", 1234f, 30f, -567f, "");
            ExpectPos("Player position (X,Y,Z): (1234, 30, 6720) , Zone: 19,105, Center dist: 6832.35", 1234f, 30f, 6720f, "");
            ExpectPos("Player position (X,Y,Z): (1234, 30, -567) , Zone: 19,-9, Center dist: 1358,03", 1234f, 30f, -567f, "");
            ExpectPos("Player position (X,Z,Y): (1234, -567, 30)", 1234f, 30f, -567f, "");
            ExpectPos("Player position (X,Z,Y): (1234,57, -567,25, 30,12)", 1234.57f, 30.12f, -567.25f, "");
            ExpectPos("Player position (X,Z,Y): (1234.57, -567.25, 30.12)", 1234.57f, 30.12f, -567.25f, "");
            ExpectPos("Camp: Player position (X,Y,Z): (1234, 30, -567) , Zone: 19,-9, Center dist: 1358.03", 1234f, 30f, -567f, "Camp");
            ExpectPos("player position (x,y,z): (1234,30,-567)", 1234f, 30f, -567f, "");
            ExpectFail("Player position (X,Y,Z): (1234, 30)"); ExpectFail("Player position (X,X,Z): (1, 2, 3)");
            ExpectFail("Player position (X,Z,Y): (25000, 0, 0)");
            Expect("(1234, 30, -567)", 1234f, 30f, true, -567f, "");   // a bare vector is still positional
            List<string> posErrors;
            List<ParsedCoord> posList = CoordinateParser.ParseList("Player position (X,Y,Z): (1234, 30, 6720) , Zone: 19,105, Center dist: 6832.35\n1234, -567\n", out posErrors);
            Check("parse: a pos line in a pasted list is read, and the next line too", posList.Count == 2 && posErrors.Count == 0,
                "got " + posList.Count + "/" + posErrors.Count);

            // List parsing
            string block = "# a comment\n"
                         + "1234, -567\n"
                         + "\n"
                         + "// another comment\n"
                         + "2000 3000 40 Swamp camp\n"
                         + "garbage line\n"
                         + "Boss: -100, -200\n";
            List<string> errors;
            List<ParsedCoord> list = CoordinateParser.ParseList(block, out errors);
            Check("list count", list.Count == 3, "got " + list.Count);
            Check("list errors", errors.Count == 1, "got " + errors.Count);
            if (list.Count == 3)
            {
                Check("list[1] elev", list[1].HasElevation && Near(list[1].Elevation, 40f), "bad elevation");
                Check("list[1] name", list[1].Name == "Swamp camp", "got " + list[1].Name);
                Check("list[2] name", list[2].Name == "Boss", "got " + list[2].Name);
            }

            // A Windows paste: CRLF line ends, blank and whitespace-only lines.
            list = CoordinateParser.ParseList("1234, -567\r\n\r\n   \r\n2000 3000 Camp\r\n", out errors);
            Check("CRLF list", list.Count == 2 && errors.Count == 0 && list[1].Name == "Camp",
                  "got " + list.Count + "/" + errors.Count);

            // Axis mapping: user X,Y + elevation -> Valheim (x, altitude, z)
            ParsedCoord pc;
            string err;
            Check("parse 100, 200, 35", CoordinateParser.TryParseOne("100, 200, 35", out pc, out err), err);
            Vector3 w = CoordinateParser.ToWorld(pc, false);
            Check("default mapping", Near(w.x, 100f) && Near(w.y, 35f) && Near(w.z, 200f), "got " + w);

            // Raw Valheim order: x, y(altitude), z
            Vector3 raw = CoordinateParser.ToWorld(pc, true);
            Check("raw mapping", Near(raw.x, 100f) && Near(raw.y, 200f) && Near(raw.z, 35f), "got " + raw);

            // Two values in raw mode still mean the two horizontal axes
            Check("parse 100, 200", CoordinateParser.TryParseOne("100, 200", out pc, out err), err);
            Vector3 raw2 = CoordinateParser.ToWorld(pc, true);
            Check("raw mapping, no elevation",
                  Near(raw2.x, 100f) && Near(raw2.y, 0f) && Near(raw2.z, 200f), "got " + raw2);

            // Labelled input already names Valheim's axes, so raw order must not re-map it
            CoordinateParser.TryParseOne("X: 1234 Y: 56 Z: -789", out pc, out err);
            Vector3 rawLab = CoordinateParser.ToWorld(pc, true);
            Check("raw mapping, labelled",
                  Near(rawLab.x, 1234f) && Near(rawLab.y, 56f) && Near(rawLab.z, -789f), "got " + rawLab);

            // Formatting round trip
            Plugin.RawValheimOrder.Value = false;
            string formatted = CoordinateFormat.Format(new Vector3(100f, 35f, 200f), true);
            Check("format default", formatted == "100, 200, 35", "got " + formatted);
            Plugin.RawValheimOrder.Value = true;
            formatted = CoordinateFormat.Format(new Vector3(100f, 35f, 200f), true);
            Check("format raw", formatted == "100, 35, 200", "got " + formatted);
            Plugin.RawValheimOrder.Value = false;
            // Two values: the x, z readout shape preflight looks for in CoordinateFormat.
            formatted = CoordinateFormat.Format(new Vector3(100f, 35f, 200f), false);
            Check("format 2D", formatted == "100, 200", "got " + formatted);

            SafeFileTests();
            RouteReadTests();
            HotkeysTests();
            SearchTests();
            ProtocolTests();
            CaptionTests();
            CaptionCacheTests();
            RouteFileTests();
            ArrivalTests();
            MapClickTests();

            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : (_failures + " TEST(S) FAILED"));
            return _failures == 0 ? 0 : 1;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.001f; }

        // The route file's crash-safe replace (SafeFile), including every state an interrupted save can leave.
        private static void SafeFileTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypointer-safefile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;   // file locks are enforced
            try
            {
                string f = Path.Combine(dir, "waypoints_1.txt");
                string fNew = f + SafeFile.NewSuffix, fOld = f + SafeFile.OldSuffix;

                SafeFile.WriteAllText(f, "first");
                Check("safe write: new file", Only(f, "first"), Leftovers(f));

                SafeFile.WriteAllText(f, "second");
                Check("safe write: replaces, no leftovers", Only(f, "second"), Leftovers(f));

                File.WriteAllText(fOld, "stale");
                File.WriteAllText(fNew, "partial");
                SafeFile.WriteAllText(f, "third");
                Check("safe write: clears an earlier save's leftovers", Only(f, "third"), Leftovers(f));

                byte[] raw = File.ReadAllBytes(f);
                Check("safe write: UTF-8 without BOM", raw.Length == 5 && raw[0] == (byte)'t', "first byte " + (raw.Length > 0 ? raw[0].ToString() : "none"));

                File.SetAttributes(f, FileAttributes.ReadOnly);
                bool refused = false;
                try { SafeFile.WriteAllText(f, "fourth"); }
                catch (UnauthorizedAccessException) { refused = true; }
                string readFrom = SafeFile.RecoverInterrupted(f);
                bool stillReadOnly = (File.GetAttributes(f) & FileAttributes.ReadOnly) != 0;
                File.SetAttributes(f, FileAttributes.Normal);
                Check("safe write: a read-only route file is refused and kept", refused && File.ReadAllText(f) == "third" && !File.Exists(fNew), Leftovers(f));
                Check("recover: a read-only route file is read as it is and stays read-only", readFrom == f && stillReadOnly,
                    "read " + readFrom + ", read-only " + stillReadOnly);

                File.WriteAllText(fOld, "stale");
                File.SetAttributes(fOld, FileAttributes.ReadOnly);
                File.WriteAllText(fNew, "partial");
                File.SetAttributes(fNew, FileAttributes.ReadOnly);
                SafeFile.WriteAllText(f, "fifth");
                Check("safe write: read-only leftovers of its own do not block it", Only(f, "fifth"), Leftovers(f));

                // What an interrupted save can leave behind: what is read, and what recovery does.
                Check("read: the file itself", SafeFile.ReadablePath(f) == f, "got " + SafeFile.ReadablePath(f));
                File.WriteAllText(fNew, "unfinished");
                Check("read: a .new beside the file is an unfinished save, ignored", SafeFile.ReadablePath(f) == f && SafeFile.RecoverInterrupted(f) == f && File.ReadAllText(f) == "fifth", Leftovers(f));
                File.Delete(fNew);

                File.Move(f, fOld);                        // cut short between the two moves:
                File.WriteAllText(fNew, "complete new");   // .old = the previous text, .new = the new one
                DateTime stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(fNew, stamp);     // a rename keeps this time stamp; a rewrite would not
                Check("read: .new when the swap was cut short", SafeFile.ReadablePath(f) == fNew, "got " + SafeFile.ReadablePath(f));
                string got = SafeFile.RecoverInterrupted(f);
                Check("recover: renames .new back, rewrites nothing",
                    got == f && File.ReadAllText(f) == "complete new" && !File.Exists(fNew) && File.GetLastWriteTimeUtc(f) == stamp,
                    Leftovers(f) + " written " + File.GetLastWriteTimeUtc(f).ToString("o"));
                File.Delete(f);
                Check("read: .old when only it is left", SafeFile.ReadablePath(f) == fOld, "got " + SafeFile.ReadablePath(f));
                got = SafeFile.RecoverInterrupted(f);
                Check("recover: renames .old back", got == f && File.ReadAllText(f) == "fifth" && !File.Exists(fOld), Leftovers(f));
                File.Delete(f);
                Check("read and recover: nothing saved", SafeFile.ReadablePath(f) == null && SafeFile.RecoverInterrupted(f) == null, Leftovers(f));

                // .old2, where the file steps aside when a .old left by an earlier save is held by another program:
                // read before .old, renamed back like the other copies, and tidied up by the next save.
                string fSpare = f + SafeFile.SpareSuffix;
                File.WriteAllText(fSpare, "spare");
                File.WriteAllText(fOld, "older");
                Check("read: .old2 before .old when only they are left", SafeFile.ReadablePath(f) == fSpare, "got " + SafeFile.ReadablePath(f));
                got = SafeFile.RecoverInterrupted(f);
                Check("recover: renames .old2 back", got == f && File.ReadAllText(f) == "spare" && !File.Exists(fSpare) && File.Exists(fOld), Leftovers(f));
                SafeFile.WriteAllText(f, "tidied");
                Check("safe write: a leftover .old is tidied up", Only(f, "tidied"), Leftovers(f));
                File.WriteAllText(fSpare, "stale spare");
                SafeFile.WriteAllText(f, "tidied again");
                Check("safe write: a leftover .old2 is tidied up", Only(f, "tidied again"), Leftovers(f));
                File.Delete(f);

                File.WriteAllText(fNew, "complete new");
                File.WriteAllText(fOld, "old");
                SafeFile.WriteAllText(f, "after recovery");
                Check("safe write: after an interrupted swap", Only(f, "after recovery"), Leftovers(f));

                // A directory squatting on a name makes that one step fail (checked on Windows, under .NET and Mono),
                // standing in for a crash, a full disk or another program at exactly that step.
                // The current file is not touched until the new text is complete: when .new cannot be written,
                // the route file is still in place.
                Directory.CreateDirectory(fNew);
                bool failed = false;
                try { SafeFile.WriteAllText(f, "not written"); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Directory.Delete(fNew, true);
                Check("safe write: the current file is not moved until the new text is written", failed && Only(f, "after recovery"), Leftovers(f));

                // A copy left by an interrupted save is renamed back before anything is written - never written
                // over, never deleted - so when it cannot be renamed the save fails and the copy is untouched.
                File.Move(f, fNew);
                Directory.CreateDirectory(f);
                failed = false;
                try { SafeFile.WriteAllText(f, "would overwrite"); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                Directory.Delete(f, true);
                Check("safe write: a copy that cannot be renamed back is never written over",
                    failed && File.Exists(fNew) && File.ReadAllText(fNew) == "after recovery" && !File.Exists(fOld), Leftovers(f));

                // The .new and .old copies are the mod's own: one marked read-only comes back writable.
                File.SetAttributes(fNew, FileAttributes.ReadOnly);
                got = SafeFile.RecoverInterrupted(f);
                bool writable = File.Exists(f) && (File.GetAttributes(f) & FileAttributes.ReadOnly) == 0;
                if (File.Exists(f)) File.SetAttributes(f, FileAttributes.Normal);
                Check("recover: a read-only copy is renamed back writable", got == f && writable && !File.Exists(fNew), Leftovers(f));
                File.Move(f, fOld);
                File.SetAttributes(fOld, FileAttributes.ReadOnly);
                bool wrote = true;
                try { SafeFile.WriteAllText(f, "over read-only"); }
                catch (UnauthorizedAccessException) { wrote = false; }
                if (File.Exists(f)) File.SetAttributes(f, FileAttributes.Normal);
                Check("safe write: a read-only copy left by an interrupted save does not block it", wrote && Only(f, "over read-only"), Leftovers(f));

                if (windows)
                {
                    // A copy held open exclusively by another program (a backup or sync tool). Windows then refuses
                    // every rename, write and delete of it, whatever SafeFile does, so these checks cannot show that
                    // the copy is protected - "a copy that cannot be renamed back is never written over" above and
                    // the last test below do. They show that recovery reads it where it is, and that a save fails
                    // (so the caller keeps the change and retries) without creating a route file.
                    File.Delete(f);
                    File.WriteAllText(fNew, "only copy");
                    bool threw = false;
                    using (new FileStream(fNew, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        got = SafeFile.RecoverInterrupted(f);
                        Check("recover: a locked copy is read where it is", got == fNew && !File.Exists(f), Leftovers(f));
                        try { SafeFile.WriteAllText(f, "would overwrite"); }
                        catch (IOException) { threw = true; }
                        catch (UnauthorizedAccessException) { threw = true; }
                    }
                    Check("safe write: while another program holds the only copy exclusively, a save fails and creates no route file",
                        threw && !File.Exists(f) && File.ReadAllText(fNew) == "only copy", Leftovers(f));
                    SafeFile.WriteAllText(f, "unlocked");
                    Check("safe write: once the other program lets go, saving goes through and tidies up", Only(f, "unlocked"), Leftovers(f));

                    // The survivor must be moved back, never opened for writing: a reader that allows renames
                    // (a scanner, a sync tool) still sees the original text afterwards.
                    File.Delete(f);
                    File.WriteAllText(fNew, "only copy");
                    using (FileStream hold = new FileStream(fNew, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        string threwWhat = null;
                        try { SafeFile.WriteAllText(f, "moved"); }
                        catch (Exception e) { threwWhat = e.GetType().Name; }
                        byte[] buf = new byte[16];
                        int n = hold.Read(buf, 0, buf.Length);
                        Check("safe write: the only copy is moved aside, never truncated",
                            threwWhat == null && File.Exists(f) && File.ReadAllText(f) == "moved"
                                && Encoding.UTF8.GetString(buf, 0, n) == "only copy",
                            Leftovers(f) + (threwWhat != null ? " threw " + threwWhat : ""));
                    }

                    // A .old left by an earlier save and held open by another program: the file steps aside as .old2
                    // instead, and the save goes through (in 1.4.0 it failed until that program let go).
                    File.WriteAllText(fOld, "stale");
                    string heldWhat = null;
                    using (new FileStream(fOld, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { SafeFile.WriteAllText(f, "past a held .old"); }
                        catch (Exception e) { heldWhat = e.GetType().Name; }
                        Check("safe write: a .old held by another program does not stop the save (the file steps aside as .old2)",
                            heldWhat == null && File.ReadAllText(f) == "past a held .old" && !File.Exists(fNew)
                                && !File.Exists(f + SafeFile.SpareSuffix) && File.Exists(fOld),
                            Leftovers(f) + (heldWhat != null ? " threw " + heldWhat : ""));
                    }
                    Check("safe write: ...and the held .old is left as it was", File.ReadAllText(fOld) == "stale", Leftovers(f));
                    SafeFile.WriteAllText(f, "let go");
                    Check("safe write: once it is let go, the next save tidies it up", Only(f, "let go"), Leftovers(f));

                    // .old and .old2 both held: the save fails (the caller keeps the change and retries), the file untouched.
                    File.WriteAllText(fOld, "stale");
                    File.WriteAllText(f + SafeFile.SpareSuffix, "stale too");
                    bool bothHeld = false;
                    using (new FileStream(fOld, FileMode.Open, FileAccess.Read, FileShare.None))
                    using (new FileStream(f + SafeFile.SpareSuffix, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { SafeFile.WriteAllText(f, "not saved"); }
                        catch (IOException) { bothHeld = true; }
                        catch (UnauthorizedAccessException) { bothHeld = true; }
                    }
                    Check("safe write: with .old and .old2 both held, the save fails and the file is untouched",
                        bothHeld && File.ReadAllText(f) == "let go" && SafeFile.ReadablePath(f) == f, Leftovers(f));
                    SafeFile.WriteAllText(f, "after both");
                    Check("safe write: ...and the next save after they are let go tidies both up", Only(f, "after both"), Leftovers(f));
                }
            }
            catch (Exception e)
            {
                Fail("SafeFile tests", e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                try
                {
                    foreach (string x in Directory.GetFiles(dir)) File.SetAttributes(x, FileAttributes.Normal);
                    Directory.Delete(dir, true);
                }
                catch (Exception) { }
            }
        }

        // Keys Valheim cannot read (Hotkeys): the first failing read is caught and warned about once, then the key
        // reads as not pressed without asking the game again, like an unbound key. ZInput here is the shim, whose
        // Plus and WheelUp throw as Valheim 1.0.16's do, and whose F13 fails with another exception type. Every
        // read must pass logWarning: false (the shim records it), or the game would log on every poll of a key it
        // cannot map.
        private static void HotkeysTests()
        {
            ZInput.Reset();
            Plugin.Log.Warnings.Clear();
            Hotkeys.Forget();
            try
            {
                ConfigEntry<KeyCode> toggle = new ConfigEntry<KeyCode>("1 - Keys", "ToggleWindowKey", KeyCode.F11);
                ConfigEntry<KeyCode> modifier = new ConfigEntry<KeyCode>("1 - Keys", "MapModifierKey", KeyCode.LeftAlt);
                ConfigEntry<KeyCode> skip = new ConfigEntry<KeyCode>("1 - Keys", "SkipWaypointKey", KeyCode.None);

                ZInput.Down.Add(KeyCode.F11);
                bool pressed = Hotkeys.Pressed(toggle);
                Check("keys: a readable key is read with GetKeyDown, logWarning false", pressed && ZInput.LastRead == "GetKeyDown:F11:False", ZInput.LastRead);
                ZInput.Down.Add(KeyCode.LeftAlt);
                bool held = Hotkeys.Held(modifier);
                Check("keys: a held key is read with GetKey, logWarning false", held && ZInput.LastRead == "GetKey:LeftAlt:False", ZInput.LastRead);
                ZInput.Down.Remove(KeyCode.F11);
                Check("keys: a readable key that is up reads false", !Hotkeys.Pressed(toggle) && ZInput.LastRead == "GetKeyDown:F11:False", ZInput.LastRead);

                int reads = ZInput.Reads;
                Check("keys: an unbound key reads false without asking the game",
                    !Hotkeys.Pressed(skip) && !Hotkeys.Held(skip) && ZInput.Reads == reads, "reads " + (ZInput.Reads - reads));

                // Held down, even: it cannot be read at all.
                toggle.Value = KeyCode.Plus;
                ZInput.Down.Add(KeyCode.Plus);
                bool threw = false;
                bool first = true;
                reads = ZInput.Reads;
                try { first = Hotkeys.Pressed(toggle); }
                catch (Exception) { threw = true; }
                Check("keys: an unreadable key throws nothing and reads false", !threw && !first && ZInput.Reads == reads + 1,
                    "threw " + threw + ", read " + first + ", reads " + (ZInput.Reads - reads));
                Check("keys: one warning, naming the setting and the key",
                    Plugin.Log.Warnings.Count == 1 && Plugin.Log.Warnings[0].Contains("ToggleWindowKey") && Plugin.Log.Warnings[0].Contains("Plus"),
                    Plugin.Log.Warnings.Count + " warning(s): " + string.Join(" | ", Plugin.Log.Warnings.ToArray()));

                reads = ZInput.Reads;
                bool again = Hotkeys.Pressed(toggle) | Hotkeys.Pressed(toggle) | Hotkeys.Held(toggle);
                Check("keys: after that it is neither read again nor warned about again",
                    !again && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 1,
                    "read " + again + ", reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);

                Check("keys: other keys still read while one cannot be", Hotkeys.Held(modifier) && ZInput.LastRead == "GetKey:LeftAlt:False", ZInput.LastRead);

                Check("keys: a key whose read failed is known as unreadable, others are not",
                    Hotkeys.IsUnreadable(KeyCode.Plus) && !Hotkeys.IsUnreadable(KeyCode.F11) && !Hotkeys.IsUnreadable(KeyCode.LeftAlt), "");

                skip.Value = KeyCode.Plus;
                reads = ZInput.Reads;
                Check("keys: the unreadable key is skipped in every setting, and each setting holding it is named once",
                    !Hotkeys.Pressed(skip) && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 2
                        && Plugin.Log.Warnings[1].Contains("SkipWaypointKey") && Plugin.Log.Warnings[1].Contains("ArgumentOutOfRangeException"),
                    "reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count + ": " + string.Join(" | ", Plugin.Log.Warnings.ToArray()));
                bool quiet = Hotkeys.Pressed(skip) | Hotkeys.Held(skip) | Hotkeys.Pressed(toggle);
                Check("keys: ...and not again", !quiet && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 2,
                    "reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);

                toggle.Value = KeyCode.F11;
                ZInput.Down.Add(KeyCode.F11);
                Check("keys: a setting changed to a readable key works at once", Hotkeys.Pressed(toggle) && ZInput.LastRead == "GetKeyDown:F11:False", ZInput.LastRead);

                // Plugin.BindConfig calls Forget when any key setting changes.
                Hotkeys.Forget();
                bool forgotten = !Hotkeys.IsUnreadable(KeyCode.Plus);
                reads = ZInput.Reads;
                bool retried = Hotkeys.Pressed(skip);
                Check("keys: after Forget an unreadable key is tried once more and warned about once more",
                    forgotten && !retried && ZInput.Reads == reads + 1 && Plugin.Log.Warnings.Count == 3,
                    "reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);

                modifier.Value = KeyCode.WheelUp;
                threw = false;
                held = true;
                try { held = Hotkeys.Held(modifier); }
                catch (Exception) { threw = true; }
                Check("keys: an unreadable modifier reads as not held, with one warning",
                    !threw && !held && Plugin.Log.Warnings.Count == 4 && Plugin.Log.Warnings[3].Contains("MapModifierKey"),
                    "threw " + threw + ", held " + held + ", warnings " + Plugin.Log.Warnings.Count);

                // Any failure of the read is caught, not only the ArgumentOutOfRangeException 1.0.16 throws.
                toggle.Value = KeyCode.F13;
                threw = false;
                first = true;
                try { first = Hotkeys.Pressed(toggle); }
                catch (Exception) { threw = true; }
                reads = ZInput.Reads;
                bool later = true;
                try { later = Hotkeys.Pressed(toggle); }
                catch (Exception) { threw = true; }
                Check("keys: a read failing with another exception is caught the same way",
                    !threw && !first && !later && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 5
                        && Plugin.Log.Warnings[4].Contains("InvalidOperationException"),
                    "threw " + threw + ", read " + first + "/" + later + ", reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);
            }
            catch (Exception e)
            {
                Fail("Hotkeys tests", e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Hotkeys.Forget();
                ZInput.Reset();
                Plugin.Log.Warnings.Clear();
            }
        }

        private static bool Only(string f, string text)
        {
            return File.Exists(f) && File.ReadAllText(f) == text
                && !File.Exists(f + SafeFile.NewSuffix) && !File.Exists(f + SafeFile.OldSuffix) && !File.Exists(f + SafeFile.SpareSuffix);
        }

        private static string Leftovers(string f)
        {
            return "file=" + (File.Exists(f) ? File.ReadAllText(f) : "(none)")
                + " new=" + File.Exists(f + SafeFile.NewSuffix) + " old=" + File.Exists(f + SafeFile.OldSuffix)
                + " old2=" + File.Exists(f + SafeFile.SpareSuffix);
        }

        private static void Expect(string input, float a, float b, bool hasElev, float elev, string name)
        {
            ParsedCoord pc;
            string error;
            if (!CoordinateParser.TryParseOne(input, out pc, out error))
            {
                Fail(input, "expected success, got error: " + error);
                return;
            }
            if (!Near(pc.A, a) || !Near(pc.B, b))
            { Fail(input, string.Format("axes {0},{1} expected {2},{3}", pc.A, pc.B, a, b)); return; }
            if (pc.HasElevation != hasElev)
            { Fail(input, "hasElevation " + pc.HasElevation + " expected " + hasElev); return; }
            if (hasElev && !Near(pc.Elevation, elev))
            { Fail(input, "elevation " + pc.Elevation + " expected " + elev); return; }
            if (pc.Name != name)
            { Fail(input, "name [" + pc.Name + "] expected [" + name + "]"); return; }
            Console.WriteLine("  ok   " + input);
        }

        private static void ExpectPos(string input, float x, float alt, float z, string name)
        {
            ParsedCoord pc;
            string error;
            if (!CoordinateParser.TryParseOne(input, out pc, out error))
            {
                Fail(input, "expected a pos line, got error: " + error);
                return;
            }
            Vector3 w = CoordinateParser.ToWorld(pc, false);
            Vector3 r = CoordinateParser.ToWorld(pc, true);
            if (!pc.Labelled || !Near(w.x, x) || !Near(w.y, alt) || !Near(w.z, z) || !Near(r.x, x) || !Near(r.y, alt) || !Near(r.z, z))
            {
                Fail(input, string.Format("world {0},{1},{2} and raw {3},{4},{5}, expected {6},{7},{8} in both", w.x, w.y, w.z, r.x, r.y, r.z, x, alt, z));
                return;
            }
            if (pc.Name != name)
            { Fail(input, "name [" + pc.Name + "] expected [" + name + "]"); return; }
            Console.WriteLine("  ok   " + input);
        }

        private static void ExpectFail(string input)
        {
            ParsedCoord pc;
            string error;
            if (CoordinateParser.TryParseOne(input, out pc, out error))
            {
                Fail(input, "expected rejection, but it parsed");
                return;
            }
            Console.WriteLine("  ok   (rejected) " + input);
        }

        // ---------------------------------------------------------------- location search (1.2.0)

        private static SearchHit Hit(string prefab, float x, float z, bool placed)
        {
            SearchHit h = new SearchHit();
            h.Prefab = prefab; h.Label = prefab; h.X = x; h.Y = 0f; h.Z = z; h.Placed = placed;
            return h;
        }

        private static int CountPrefab(List<SearchHit> hits, string prefab)
        {
            int n = 0;
            for (int i = 0; i < hits.Count; i++) if (hits[i].Prefab == prefab) n++;
            return n;
        }

        private static void SearchTests()
        {
            // --- the catalogue
            HashSet<string> names = new HashSet<string>();
            bool nonEmpty = true, labelled = true, uniqueNames = true;
            for (int i = 0; i < SearchCatalog.Queries.Length; i++)
            {
                SearchQuery q = SearchCatalog.Queries[i];
                if (!names.Add(q.Name)) uniqueNames = false;
                if (q.Locations.Length + q.Objects.Length == 0) nonEmpty = false;
                HashSet<string> prefabs = new HashSet<string>();
                for (int j = 0; j < q.Locations.Length; j++)
                {
                    if (string.IsNullOrEmpty(q.Locations[j].Prefab) || string.IsNullOrEmpty(q.Locations[j].Label)) labelled = false;
                    if (!prefabs.Add(q.Locations[j].Prefab)) nonEmpty = false;
                }
                for (int j = 0; j < q.Objects.Length; j++)
                    if (string.IsNullOrEmpty(q.Objects[j].Prefab) || string.IsNullOrEmpty(q.Objects[j].Label)) labelled = false;
            }
            Check("search: every query has a unique name, something to look for, and no location listed twice", uniqueNames && nonEmpty, "");
            Check("search: every location and object has a prefab and a label", labelled, "");
            Check("search: the four unique places are the ones the game places once (Big Rock Clearing, Haldor, Hildir, Bog Witch)",
                SearchCatalog.IsUnique("BigRockClearing") && SearchCatalog.IsUnique("Vendor_BlackForest")
                && SearchCatalog.IsUnique("Hildir_camp") && SearchCatalog.IsUnique("BogWitch_Camp")
                && !SearchCatalog.IsUnique("Ruin1") && SearchCatalog.UniqueLocations.Length == 4, "");
            SearchQuery spear = null;
            for (int i = 0; i < SearchCatalog.Queries.Length; i++) if (SearchCatalog.Queries[i].Name == "Wooden Spear") spear = SearchCatalog.Queries[i];
            bool spearOk = spear != null && spear.Locations.Length == 18 && spear.Items.Length == 1 && spear.Items[0] == "SpearWood";
            bool noStoneHouse4 = true;
            for (int i = 0; spear != null && i < spear.Locations.Length; i++)
                if (spear.Locations[i].Prefab == "StoneHouse4" || spear.Locations[i].Prefab == "TrollCave02") noStoneHouse4 = false;
            Check("search: Wooden Spear covers the 18 location types the 1.0.16 data lists - not StoneHouse4 (no chest) nor TrollCave02 (its spear chests are switched off)", spearOk && noStoneHouse4, "");
            SearchQuery greatsword = SearchCatalog.ByName("Wooden Greatsword");
            string[] gsPlaces = { "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2", "Mistlands_GuardTower1_ruined_new",
                "Mistlands_GuardTower1_ruined_new2", "Mistlands_GuardTower3_ruined_new", "Mistlands_RockSpire1" };
            bool gsListed = greatsword != null && greatsword.Locations.Length == gsPlaces.Length && greatsword.Items.Length == 1
                && greatsword.Items[0] == "THSwordWood" && greatsword.Objects.Length == 0 && !greatsword.PlacesHoldObjects;
            for (int i = 0; gsListed && i < gsPlaces.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < greatsword.Locations.Length; j++) if (greatsword.Locations[j].Prefab == gsPlaces[i]) found = true;
                if (!found) gsListed = false;
            }
            for (int j = 0; greatsword != null && j < greatsword.Locations.Length; j++)
                if (greatsword.Locations[j].Prefab == "Mistlands_DvergrBossEntrance1") gsListed = false;   // the Infested Citadel holds none
            Check("search: Wooden Greatsword covers the 6 location types the 1.0.16 data lists - the Infested Mines through their treasure rooms, three ruined Dvergr towers and the rock spire; not the Infested Citadel", gsListed, "");
            bool gsNamed = greatsword != null;
            for (int j = 0; greatsword != null && j < greatsword.Locations.Length; j++)
            {
                string p = greatsword.Locations[j].Prefab, l = greatsword.Locations[j].Label;
                if (p.StartsWith("Mistlands_DvergrTownEntrance", StringComparison.Ordinal) ? l != "Infested Mine"
                    : p.StartsWith("Mistlands_GuardTower", StringComparison.Ordinal) ? l != "Ruined Dvergr Tower" : l != "Rock Spire") gsNamed = false;
                if (SearchCatalog.WideLocation(p)) gsNamed = false;
            }
            Check("search: the Greatsword places are named (Infested Mine, Ruined Dvergr Tower, Rock Spire), and their chests are looked for within 64 m",
                gsNamed && SearchCatalog.WideLocations.Length == 5, "");

            // --- unique places, on the server
            List<SearchHit> hits = new List<SearchHit>();
            hits.Add(Hit("Vendor_BlackForest", 100, 0, false));
            hits.Add(Hit("Vendor_BlackForest", 200, 0, false));
            hits.Add(Hit("Ruin1", 5, 5, true));
            SearchRules.ResolveUniqueOnServer(hits);
            Check("search: server, nothing placed yet - every candidate is kept and marked possible",
                CountPrefab(hits, "Vendor_BlackForest") == 2 && hits[0].Possible && hits[1].Possible && !hits[2].Possible, "");

            hits.Clear();
            hits.Add(Hit("Vendor_BlackForest", 100, 0, false));
            hits.Add(Hit("Vendor_BlackForest", 200, 0, true));
            hits.Add(Hit("Hildir_camp", 50, 0, false));
            hits.Add(Hit("Hildir_camp", 60, 0, false));
            SearchRules.ResolveUniqueOnServer(hits);
            Check("search: server, one candidate placed - only the placed one stays, and it is not 'possible'",
                CountPrefab(hits, "Vendor_BlackForest") == 1 && hits[0].X == 200 && !hits[0].Possible
                && CountPrefab(hits, "Hildir_camp") == 2 && hits[1].Possible && hits[2].Possible, "");

            // A lone candidate that is not placed yet is the only place the location can go: the server calls it real,
            // as a client does for a single answer (1.2.0 marked it 'possible' on a host only).
            hits.Clear();
            hits.Add(Hit("BogWitch_Camp", 30, 0, false));
            hits.Add(Hit("BigRockClearing", 10, 0, false));
            hits.Add(Hit("BigRockClearing", 20, 0, false));
            SearchRules.ResolveUniqueOnServer(hits);
            List<SearchHit> same = new List<SearchHit>();
            same.Add(Hit("BogWitch_Camp", 30, 0, false));
            same.Add(Hit("BigRockClearing", 10, 0, false));
            same.Add(Hit("BigRockClearing", 20, 0, false));
            SearchRules.ResolveUniqueOnClient(same, null, true);
            Check("search: server, a lone unplaced candidate is the real place - and host and client agree",
                hits.Count == 3 && !hits[0].Possible && hits[1].Possible && hits[2].Possible
                && same.Count == 3 && same[0].Possible == hits[0].Possible && same[1].Possible == hits[1].Possible
                && same[2].Possible == hits[2].Possible, "");

            // --- unique places, on a client
            hits.Clear();
            hits.Add(Hit("BigRockClearing", 10, 0, false));
            hits.Add(Hit("BigRockClearing", 20, 0, false));
            hits.Add(Hit("BogWitch_Camp", 30, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, null, true);
            Check("search: client, several answers - all possible; a single answer is the real place",
                hits[0].Possible && hits[1].Possible && !hits[2].Possible, "");

            // Answers cut short by the timeout: the one answer that arrived may be one of several candidates.
            hits.Clear();
            hits.Add(Hit("BogWitch_Camp", 30, 0, false));
            hits.Add(Hit("Ruin1", 40, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, null, false);
            Check("search: client, answers incomplete - a lone answer stays 'possible' (other places unaffected)",
                hits.Count == 2 && hits[0].Possible && !hits[1].Possible, "");

            hits.Clear();
            hits.Add(Hit("Hildir_camp", 10, 0, false));
            hits.Add(Hit("Hildir_camp", 500, 0, false));
            Dictionary<string, float[]> icons = new Dictionary<string, float[]>();
            icons["Hildir_camp"] = new float[] { 499, 0, 1 };
            SearchRules.ResolveUniqueOnClient(hits, icons, true);
            Check("search: client, a merchant's placed icon picks the real one and drops the rest",
                hits.Count == 1 && hits[0].X == 500 && hits[0].Placed && !hits[0].Possible, "");

            hits.Clear();
            hits.Add(Hit("Hildir_camp", 500, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, icons, false);
            Check("search: client, answers incomplete - a merchant's placed icon still decides",
                hits.Count == 1 && hits[0].Placed && !hits[0].Possible, "");

            // --- resolve before the range cut: the real place outside range must not make a near candidate look real
            hits.Clear();
            hits.Add(Hit("Vendor_BlackForest", 100, 0, false));
            hits.Add(Hit("Vendor_BlackForest", 5000, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, null, true);
            SearchRules.KeepWithinRange(hits, 0, 0, 1000);
            Check("search: a candidate left in range after the cut is still 'possible'", hits.Count == 1 && hits[0].Possible, "");

            // --- range
            hits.Clear();
            hits.Add(Hit("Ruin1", 300, 400, true));   // 500 m
            hits.Add(Hit("Ruin1", 600, 800, true));   // 1000 m
            hits.Add(Hit("Ruin1", 601, 800, true));   // just over
            SearchRules.KeepWithinRange(hits, 0, 0, 1000);
            Check("search: the range is horizontal and inclusive", hits.Count == 2, hits.Count.ToString());

            // --- chests
            Check("search: chests - one filled chest without the item: skip the place", SearchRules.ChestsExhausted(1, 0, 0), "");
            Check("search: chests - no chest found: keep (cannot tell)", !SearchRules.ChestsExhausted(0, 0, 0), "");
            Check("search: chests - one chest not filled yet: keep", !SearchRules.ChestsExhausted(2, 1, 0), "");
            Check("search: chests - a chest still holding it: keep", !SearchRules.ChestsExhausted(2, 0, 1), "");

            // --- rocks of a clearing already listed
            hits.Clear();
            hits.Add(Hit("BigRockClearing", 0, 0, true));
            SearchHit rock1 = Hit("Pickable_StoneRock", 10, 10, true); rock1.IsObject = true; hits.Add(rock1);
            SearchHit rock2 = Hit("Pickable_StoneRock", 500, 0, true); rock2.IsObject = true; hits.Add(rock2);
            SearchRules.DropObjectsNear(hits, "BigRockClearing", 40f);
            Check("search: rocks inside a listed Big Rock Clearing are dropped, a scattered one stays",
                hits.Count == 2 && hits[1].X == 500, "");

            // --- Bee Nests (1.4.0): a nest grows only inside its place, in the place's own 64 m zone
            SearchQuery bees = SearchCatalog.ByName("Bee Nest");
            string[] beePlaces =
            {
                "WoodHouse1", "WoodHouse2", "WoodHouse3", "WoodHouse4", "WoodHouse5", "WoodHouse6", "WoodHouse7", "WoodHouse9",
                "WoodHouse10", "WoodHouse11", "WoodHouse13", "StoneTowerRuins03", "BearCave", "WoodFarm1", "WoodVillage1", "WoodVillage2"
            };
            bool beesListed = bees != null && bees.Locations.Length == beePlaces.Length;
            for (int i = 0; beesListed && i < beePlaces.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < bees.Locations.Length; j++) if (bees.Locations[j].Prefab == beePlaces[i]) found = true;
                if (!found) beesListed = false;
            }
            Check("search: Bee Nest covers the 16 location types whose 1.0.16 data holds a wild nest - not WoodHouse8 or WoodHouse12, which hold none",
                beesListed && bees.PlacesHoldObjects && bees.Items.Length == 0 && bees.Objects.Length == 1
                && bees.Objects[0].Prefab == "Beehive" && bees.Objects[0].Label == "Bee Nest", "");
            string[,] beeLabels =
            {
                { "WoodHouse1", "Abandoned House" }, { "WoodHouse13", "Abandoned House" }, { "StoneTowerRuins03", "Contested Tower" },
                { "BearCave", "Bear Cave" }, { "WoodFarm1", "Abandoned Village" }, { "WoodVillage1", "Draugr Village" },
                { "WoodVillage2", "Draugr Village" }
            };
            bool beeLabelled = bees != null;
            for (int i = 0; beeLabelled && i < beeLabels.GetLength(0); i++)
            {
                bool match = false;
                for (int j = 0; j < bees.Locations.Length; j++)
                    if (bees.Locations[j].Prefab == beeLabels[i, 0] && bees.Locations[j].Label == beeLabels[i, 1]) match = true;
                if (!match) beeLabelled = false;
            }
            Check("search: each kind of Bee Nest place has its name (Abandoned House, Contested Tower, Bear Cave, Abandoned Village, Draugr Village)",
                beeLabelled, "");
            int holding = 0;
            for (int i = 0; i < SearchCatalog.Queries.Length; i++) if (SearchCatalog.Queries[i].PlacesHoldObjects) holding++;
            Check("search: only the Bee Nest keeps its objects inside its places (the loose Rocks lie apart)",
                holding == 1 && !SearchCatalog.ByName("Mysterious Rock").PlacesHoldObjects, "");

            Check("search: zones are the game's 64 m squares centred on multiples of 64 (ZoneSystem.GetZone)",
                SearchRules.Zone(0f) == 0 && SearchRules.Zone(31.5f) == 0 && SearchRules.Zone(32.5f) == 1 && SearchRules.Zone(-31.5f) == 0
                && SearchRules.Zone(-32.5f) == -1 && SearchRules.Zone(100f) == 2 && SearchRules.Zone(-10000f) == -156
                && SearchRules.ZoneKey(10f, -40f) != SearchRules.ZoneKey(-40f, 10f), "");
            Check("search: a zone key tells apart zones sharing a column or a row",
                SearchRules.ZoneKey(100f, -100f) != SearchRules.ZoneKey(-100f, -100f) && SearchRules.ZoneKey(100f, -100f) != SearchRules.ZoneKey(100f, 100f)
                && SearchRules.ZoneKey(0f, 640f) != SearchRules.ZoneKey(5f, 0f) && SearchRules.ZoneKey(640f, 0f) != SearchRules.ZoneKey(0f, 640f), "");

            hits.Clear();
            SearchHit houseA = Hit("WoodHouse2", 20, 5, true); hits.Add(houseA);                              // zone (0, 0)
            SearchHit houseB = Hit("WoodHouse5", 40, 5, true); hits.Add(houseB);                              // zone (1, 0), 20 m away
            SearchHit village = Hit("WoodVillage1", 640, 0, true); hits.Add(village);                         // zone (10, 0)
            SearchHit nestA = Hit("Beehive", 26, 9, true); nestA.IsObject = true; hits.Add(nestA);             // house A's
            SearchHit nestV1 = Hit("Beehive", 666, 27, true); nestV1.IsObject = true; hits.Add(nestV1);        // 37 m out
            SearchHit nestV2 = Hit("Beehive", 620, -20, true); nestV2.IsObject = true; hits.Add(nestV2);
            SearchHit nestLone = Hit("Beehive", 2000, 0, true); nestLone.IsObject = true; hits.Add(nestLone);  // no place listed
            SearchRules.DropPlacesWithObjectsInZone(hits);
            Check("search: a nest found stands for its place - the place in its zone goes, a neighbour's place and every nest stay",
                hits.Count == 5 && !hits.Contains(houseA) && hits.Contains(houseB) && !hits.Contains(village)
                && hits.Contains(nestA) && hits.Contains(nestV1) && hits.Contains(nestV2) && hits.Contains(nestLone), hits.Count.ToString());

            hits.Clear();
            SearchHit withNest = Hit("WoodHouse3", 5, 5, true); hits.Add(withNest);
            SearchHit emptied = Hit("WoodHouse4", 200, 5, true); hits.Add(emptied);
            SearchHit notYet = Hit("WoodHouse7", 400, 5, false); hits.Add(notYet);
            SearchHit nestBeyond = Hit("WoodHouse9", 990, 0, true); hits.Add(nestBeyond);   // its nest lies just beyond the range
            SearchHit nestHit = Hit("Beehive", 8, 7, true); nestHit.IsObject = true; hits.Add(nestHit);
            HashSet<long> nestZones = new HashSet<long>();
            nestZones.Add(SearchRules.ZoneKey(8, 7));
            nestZones.Add(SearchRules.ZoneKey(991, 2));
            int emptyDropped;
            int beeChecked = SearchRules.DropPlacesKnownEmpty(hits, nestZones, out emptyDropped);
            Check("search: a generated place without a nest in its zone is left out - not one generated later, nor one whose nest is out of range",
                beeChecked == 3 && emptyDropped == 1 && !hits.Contains(emptied) && hits.Contains(withNest) && hits.Contains(notYet)
                && hits.Contains(nestBeyond) && hits.Contains(nestHit), beeChecked + "/" + emptyDropped);
            hits.Clear();
            SearchHit candidate = Hit("WoodHouse10", 5, 5, true); candidate.Possible = true; hits.Add(candidate);
            hits.Add(Hit("WoodHouse11", 300, 5, true));
            beeChecked = SearchRules.DropPlacesKnownEmpty(hits, null, out emptyDropped);
            Check("search: no nest known at all - every generated place goes, a candidate stays",
                beeChecked == 1 && emptyDropped == 1 && hits.Count == 1 && hits[0] == candidate, "");

            // --- the player's side once every answer is in (FinishHits), and the server's known-empty decisions
            hits.Clear();
            SearchHit seenHouse = Hit("WoodHouse1", 10, 10, true); hits.Add(seenHouse);
            SearchHit unseenNest = Hit("Beehive", 14, 12, true); unseenNest.IsObject = true; hits.Add(unseenNest);
            SearchRules.FinishHits(hits, bees, delegate (SearchHit h) { return !h.IsObject; }, 40f);
            Check("search: Wayfinder's filter comes before the nest merge - an unexplored nest leaves its explored place queued",
                hits.Count == 1 && hits[0] == seenHouse, hits.Count.ToString());

            hits.Clear();
            SearchHit someHouse = Hit("WoodHouse1", 10, 10, true); hits.Add(someHouse);
            SearchHit itsNest = Hit("Beehive", 14, 12, true); itsNest.IsObject = true; hits.Add(itsNest);
            SearchHit farHouse = Hit("WoodHouse5", 0, 640, true); hits.Add(farHouse);
            SearchHit farNest = Hit("Beehive", 5, 0, true); farNest.IsObject = true; hits.Add(farNest);
            SearchRules.FinishHits(hits, bees, null, 40f);
            Check("search: without a filter (TomTom) a nest found replaces the place in its zone, and only that one",
                hits.Count == 3 && !hits.Contains(someHouse) && hits.Contains(itsNest) && hits.Contains(farHouse) && hits.Contains(farNest),
                hits.Count.ToString());

            hits.Clear();
            SearchHit candidateSeen = Hit("BigRockClearing", 900, 900, false); candidateSeen.Possible = true; hits.Add(candidateSeen);
            SearchHit unexploredRuin = Hit("Ruin1", 300, 0, true); unexploredRuin.Label = "unexplored"; hits.Add(unexploredRuin);
            SearchHit exploredRuin = Hit("Ruin1", 400, 0, true); hits.Add(exploredRuin);
            SearchRules.FinishHits(hits, SearchCatalog.ByName("Wooden Spear"), delegate (SearchHit h) { return h.Label != "unexplored"; }, 40f);
            Check("search: Wayfinder's filter drops unexplored places and every unique candidate", hits.Count == 1 && hits[0] == exploredRuin, "");

            hits.Clear();
            SearchHit rockClearing = Hit("BigRockClearing", -20, -20, true); hits.Add(rockClearing);
            SearchHit nearRock = Hit("Pickable_StoneRock", -20, 10, true); nearRock.IsObject = true; hits.Add(nearRock);   // 30 m
            SearchHit zoneRock = Hit("Pickable_StoneRock", 20, 10, true); zoneRock.IsObject = true; hits.Add(zoneRock);    // 50 m, same zone
            SearchRules.FinishHits(hits, SearchCatalog.ByName("Mysterious Rock"), null, 40f);
            Check("search: a Mysterious Rock Find folds rocks within 40 m into their clearing, and never merges by zone",
                hits.Count == 2 && hits.Contains(rockClearing) && hits.Contains(zoneRock), hits.Count.ToString());

            Check("search: places known to hold no nest are left out only on a server, only when asked, and only for the Bee Nest",
                SearchRules.KnownEmptyApplies(true, true, bees) && !SearchRules.KnownEmptyApplies(false, true, bees)
                && !SearchRules.KnownEmptyApplies(true, false, bees) && !SearchRules.KnownEmptyApplies(true, true, SearchCatalog.ByName("Mysterious Rock"))
                && !SearchRules.KnownEmptyApplies(true, true, SearchCatalog.ByName("Wooden Spear"))
                && !SearchRules.KnownEmptyApplies(true, true, null), "");

            HashSet<long> noted = new HashSet<long>();
            bool beyond = SearchRules.NoteObject(noted, 1010, 0, 0, 0, 1000);
            bool inside = SearchRules.NoteObject(noted, 600, 800, 0, 0, 1000);
            bool noZones = SearchRules.NoteObject(null, 10, 0, 0, 0, 1000);
            Check("search: a nest read counts for its zone even beyond the range, and the range test is horizontal and inclusive",
                !beyond && inside && noZones && noted.Count == 2 && noted.Contains(SearchRules.ZoneKey(1010, 0))
                && noted.Contains(SearchRules.ZoneKey(600, 800)), "");

            // --- which location answers vanilla may turn into a (saved, shareable) pin
            Check("search: an answer to this mod never reaches vanilla - current, abandoned or malformed search number",
                !SearchRules.VanillaMayHandle(SearchRules.TokenPrefix + "7")
                && !SearchRules.VanillaMayHandle(SearchRules.TokenPrefix + "1")
                && !SearchRules.VanillaMayHandle(SearchRules.TokenPrefix)
                && !SearchRules.VanillaMayHandle(SearchRules.TokenPrefix + "not a number"), "");
            Check("search: other answers (a Vegvisir's, a runestone's) still reach vanilla",
                SearchRules.VanillaMayHandle("$enemy_eikthyr") && SearchRules.VanillaMayHandle("")
                && SearchRules.VanillaMayHandle(null) && SearchRules.VanillaMayHandle("WaypointerSearch#3"), "");
            Check("search: only this plugin's own answer prefix counts as the interception",
                SearchRules.IsOurPrefix("DoomMachine.TomTom", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch", "DoomMachine.TomTom", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch")
                && !SearchRules.IsOurPrefix("SomeOther.Mod", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch", "DoomMachine.TomTom", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch")
                && !SearchRules.IsOurPrefix("DoomMachine.TomTom", "Waypointer.Other_Patch", "DoomMachine.TomTom", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch")
                && !SearchRules.IsOurPrefix(null, null, "DoomMachine.TomTom", "Waypointer.Game_RPC_DiscoverLocationResponse_Patch"), "");
            bool requestsSafe = true;
            int[] ids = { 0, 1, 7, 42, 2147483647 };
            for (int i = 0; i < ids.Length; i++)
                if (SearchRules.VanillaMayHandle(SearchRules.RequestPinName(ids[i]))) requestsSafe = false;
            Check("search: every request name, and so every answer echoing it, is kept from vanilla", requestsSafe,
                SearchRules.RequestPinName(7));
            Check("search: the token starts with a control character no game text starts with",
                SearchRules.TokenPrefix.Length > 1 && SearchRules.TokenPrefix[0] == (char)1, "");

            // --- names
            SearchQuery axeHead = null;
            for (int i = 0; i < SearchCatalog.Queries.Length; i++) if (SearchCatalog.Queries[i].Name == "Curious Axe Head") axeHead = SearchCatalog.Queries[i];
            SearchHit house = Hit("WoodHouse6", 0, 0, true); house.Label = "Abandoned House";
            SearchHit witch = Hit("BogWitch_Camp", 0, 0, false); witch.Label = "Bog Witch"; witch.Possible = true;
            Check("search: names say what was searched for, and 'possible' for a candidate",
                SearchRules.WaypointName(house, axeHead) == "Abandoned House (Curious Axe Head)"
                && SearchRules.WaypointName(witch, SearchCatalog.ByName("Bog Witch")) == "Bog Witch (possible)",
                SearchRules.WaypointName(house, axeHead));
            SearchHit beeHouse = Hit("WoodHouse3", 0, 0, true); beeHouse.Label = "Abandoned House";
            SearchHit beeNest = Hit("Beehive", 0, 0, true); beeNest.Label = "Bee Nest"; beeNest.IsObject = true;
            SearchHit clearing = Hit("BigRockClearing", 0, 0, true); clearing.Label = "Big Rock Clearing";
            SearchQuery rocks = SearchCatalog.ByName("Mysterious Rock");
            SearchHit looseRock = Hit("Pickable_StoneRock", 0, 0, true); looseRock.Label = rocks.Objects[0].Label; looseRock.IsObject = true;
            Check("search: a place that may hold a nest says so, a nest found is just a Bee Nest, a loose rock is a Rock, a clearing a Big Rock Clearing",
                SearchRules.WaypointName(beeHouse, bees) == "Abandoned House (Bee Nest)" && SearchRules.WaypointName(beeNest, bees) == "Bee Nest"
                && SearchRules.WaypointName(clearing, rocks) == "Big Rock Clearing" && SearchRules.WaypointName(looseRock, rocks) == "Rock",
                SearchRules.WaypointName(looseRock, rocks));

            // --- the rocks (1.4.1): the game's name, and never a rock a player made
            Check("search: the rock Find keeps its key 'Mysterious Rock' (servers look it up by it) and shows the game's name, Rock",
                rocks != null && rocks.Title == "Rock" && rocks.Locations.Length == 1 && rocks.Locations[0].Prefab == "BigRockClearing"
                && rocks.Locations[0].Label == "Big Rock Clearing" && rocks.Objects.Length == 1 && rocks.Objects[0].Prefab == "Pickable_StoneRock"
                && rocks.Objects[0].Label == "Rock" && rocks.Items.Length == 0 && !rocks.PlacesHoldObjects, "");
            bool noPlayerRocks = true, titlesOk = true;
            HashSet<string> titles = new HashSet<string>();
            string[] playerMade = { "Placeable_HardRock", "Pickable_HardRockOffspring", "StoneRock" };
            for (int i = 0; i < SearchCatalog.Queries.Length; i++)
            {
                SearchQuery q = SearchCatalog.Queries[i];
                if (string.IsNullOrEmpty(q.Title) || !titles.Add(q.Title)) titlesOk = false;
                if (q != rocks && q.Title != q.Name) titlesOk = false;
                for (int k = 0; k < playerMade.Length; k++)
                {
                    for (int j = 0; j < q.Locations.Length; j++) if (q.Locations[j].Prefab == playerMade[k]) noPlayerRocks = false;
                    for (int j = 0; j < q.Objects.Length; j++) if (q.Objects[j].Prefab == playerMade[k]) noPlayerRocks = false;
                }
            }
            Check("search: no Find lists a player's pet rock (Placeable_HardRock), the stones it lays, or a dropped Rock item", noPlayerRocks, "");
            Check("search: every Find shows a unique title, its own name except the rocks'", titlesOk, "");
            Check("search: only what the world made is listed - not a piece a player placed (creator) nor a console spawn (cheated)",
                SearchRules.WorldMade(0L, false) && !SearchRules.WorldMade(123L, false) && !SearchRules.WorldMade(-5L, false)
                && !SearchRules.WorldMade(0L, true) && !SearchRules.WorldMade(7L, true), "");

            // --- the route
            float[] xs = { 100, 10, 50, -20, 200 };
            float[] zs = { 0, 0, 0, 0, 0 };
            int[] order = RoutePlanner.Plan(0, 0, xs, zs, 5, 50, 50.0);
            Check("search: the route starts at the stop nearest the player, then takes the shortest way on (10, -20, 50, 100, 200)",
                order.Length == 5 && order[0] == 1 && RoutePlanner.Length(0, 0, xs, zs, order) <= 260.01,
                string.Join(",", Array.ConvertAll<int, string>(order, delegate (int v) { return v.ToString(); })));

            // A case nearest-neighbour alone gets wrong: 2-opt must not make it worse, and must stay a permutation.
            System.Random rng = new System.Random(12345);
            int n = 200;
            float[] rx = new float[n], rz = new float[n];
            for (int i = 0; i < n; i++) { rx[i] = (float)(rng.NextDouble() * 4000 - 2000); rz[i] = (float)(rng.NextDouble() * 4000 - 2000); }
            int[] nnOnly = RoutePlanner.Plan(0, 0, rx, rz, n, n, 0.0);
            int[] opt = RoutePlanner.Plan(0, 0, rx, rz, n, n, 100.0);
            bool perm = opt.Length == n;
            bool[] seen = new bool[n];
            for (int i = 0; i < opt.Length && perm; i++) { if (opt[i] < 0 || opt[i] >= n || seen[opt[i]]) perm = false; else seen[opt[i]] = true; }
            double lnn = RoutePlanner.Length(0, 0, rx, rz, nnOnly), lopt = RoutePlanner.Length(0, 0, rx, rz, opt);
            Check("search: 2-opt keeps every stop once, keeps the nearest first, and does not lengthen the route", perm && opt[0] == nnOnly[0] && lopt <= lnn + 1e-6,
                string.Format("nn {0:0} m, optimised {1:0} m", lnn, lopt));

            int[] capped = RoutePlanner.Plan(0, 0, rx, rz, n, 10, 100.0);
            bool prefix = capped.Length == 10;
            for (int i = 0; i < capped.Length && prefix; i++) if (capped[i] != opt[i]) prefix = false;
            Check("search: the cap keeps the start of the optimised route", prefix, "");
            Check("search: an empty search plans an empty route", RoutePlanner.Plan(0, 0, new float[0], new float[0], 0, 10, 5.0).Length == 0, "");

            // --- trimming to the nearest (the player's side only)
            hits.Clear();
            hits.Add(Hit("Ruin1", 300, 0, true));
            hits.Add(Hit("Ruin1", 100, 0, true));
            hits.Add(Hit("Ruin1", -200, 0, true));
            SearchRules.KeepNearest(hits, 0, 0, 2);
            Check("search: keeping the nearest keeps those closest to the player, nearest first",
                hits.Count == 2 && hits[0].X == 100 && hits[1].X == -200, "");
        }

        // ---------------------------------------------------------------- the arrow's name caption (1.3.1)

        private static void CaptionTests()
        {
            // A stand-in for the font: 8 px per character.
            Func<string, float> m = delegate (string s) { return s.Length * 8f; };
            string name = "Abandoned House (Mysterious Axe Head)";
            string suffix = "  (2 left)";

            Check("caption: a name that fits is drawn whole, with the queue count",
                CaptionFit.Fit(name, suffix, 1000f, m) == name + suffix, "");

            string cut = CaptionFit.Fit(name, suffix, 300f, m);
            bool longest = cut.Length * 8f <= 300f
                && CaptionFit.Fit(name, suffix, 300f + 8f, m).Length > cut.Length;
            Check("caption: a name wider than the screen loses its end, not the queue count, and keeps all that fits",
                cut.EndsWith(CaptionFit.Ellipsis + suffix, StringComparison.Ordinal) && name.StartsWith(cut.Substring(0, cut.Length - CaptionFit.Ellipsis.Length - suffix.Length), StringComparison.Ordinal)
                && longest, cut);

            Check("caption: with a single waypoint there is no count, and the name alone is shortened",
                CaptionFit.Fit(name, "", 100f, m) == "Abandoned..." , CaptionFit.Fit(name, "", 100f, m));

            Check("caption: a space before the cut is not left before the ellipsis",
                CaptionFit.Fit("Abandoned House", "", 13 * 8f, m) == "Abandoned...", CaptionFit.Fit("Abandoned House", "", 13 * 8f, m));

            Check("caption: when not even the count fits, the ellipsis and the count remain (the box clips them)",
                CaptionFit.Fit(name, suffix, 10f, m) == CaptionFit.Ellipsis + suffix, CaptionFit.Fit(name, suffix, 10f, m));

            // Trailing spaces are not part of the name: one complete but for them is shown whole, without "...".
            Check("caption: a name complete but for trailing spaces gets no ellipsis",
                CaptionFit.Fit("Home     ", "  (3 left)", 140f, m) == "Home  (3 left)", CaptionFit.Fit("Home     ", "  (3 left)", 140f, m));

            // An emoji is one character in two UTF-16 halves; a cut between them would leave half of it.
            string emoji = "Ab\uD83D\uDE00cdefghij";
            string emojiCut = CaptionFit.Fit(emoji, "", 48f, m);
            Check("caption: a cut never splits a surrogate pair", emojiCut == "Ab..." && WholePairs(emojiCut), emojiCut);
            Check("caption: ...and keeps the pair whole when it fits",
                CaptionFit.Fit(emoji, "", 56f, m) == "Ab\uD83D\uDE00...", CaptionFit.Fit(emoji, "", 56f, m));

            // A width sweep against brute force, for the rules above: at
            // every width the longest start of the name that fits is kept. It catches the off-by-one cuts the checks
            // above let through (keeping one character fewer than fits).
            string[] names = { "Abandoned House (Mysterious Axe Head)", "Sealed Tower (Wooden Atgeir)", "", "Home     ", emoji, "A  B" };
            string[] suffixes = { "  (2 left)", "" };
            int bad = 0;
            string firstBad = null;
            for (int a = 0; a < names.Length; a++)
                for (int b = 0; b < suffixes.Length; b++)
                    for (int px = 0; px <= (names[a].Length + suffixes[b].Length + 1) * 8; px++)
                    {
                        string got = CaptionFit.Fit(names[a], suffixes[b], px, m);
                        string want = FitOracle(names[a], suffixes[b], px, m);
                        if (got != want || !WholePairs(got))
                        {
                            bad++;
                            if (firstBad == null) firstBad = "'" + names[a] + "' '" + suffixes[b] + "' " + px + " px: got '" + got + "', want '" + want + "'";
                        }
                    }
            Check("caption: at every width the longest start of the name that fits is kept (width sweep against brute force)",
                bad == 0, bad + " widths wrong; first " + firstBad);
        }

        // The caption rules by brute force, written apart from CaptionFit: the whole caption; else the name without its
        // trailing spaces; else the longest cut that fits, never inside a surrogate pair; else "..." and the suffix.
        private static string FitOracle(string name, string suffix, float px, Func<string, float> m)
        {
            if (m(name + suffix) <= px) return name + suffix;
            string trimmed = name.TrimEnd();
            if (m(trimmed + suffix) <= px) return trimmed + suffix;
            for (int j = trimmed.Length; j >= 0; j--)
            {
                if (j > 0 && j < trimmed.Length && char.IsHighSurrogate(trimmed[j - 1])) continue;
                string s = trimmed.Substring(0, j).TrimEnd() + CaptionFit.Ellipsis + suffix;
                if (m(s) <= px) return s;
            }
            return CaptionFit.Ellipsis + suffix;
        }

        private static bool WholePairs(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))) return false;
                if (char.IsLowSurrogate(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1]))) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- the arrow's caption cache (1.5.0)

        // CaptionCache and CaptionWidth, the arrow's caption caching moved out of ArrowHud: what a check outside this
        // repository used to cover (steady frames, count and name changes, a name that comes back as a new string every
        // frame, a measure that throws once).
        private static void CaptionCacheTests()
        {
            int calls = 0;
            int throwOn = -1;
            Func<string, float> m = delegate (string s)
            {
                if (throwOn == 0) { throwOn = -1; throw new InvalidOperationException("planted measure failure"); }
                if (throwOn > 0) throwOn--;
                calls++;
                return s.Length * 8f;
            };
            Check("caption cache: the count suffix - none for one waypoint, '  (N left)' for more",
                CaptionFit.Suffix(0) == "" && CaptionFit.Suffix(1) == "" && CaptionFit.Suffix(2) == "  (2 left)" && CaptionFit.Suffix(12) == "  (12 left)",
                CaptionFit.Suffix(2));

            CaptionCache cc = new CaptionCache(m);
            string name = "Abandoned House (Mysterious Axe Head)";
            float w;
            string first = cc.FittedName(name, 3, 1000f, out w);
            int after = calls;
            for (int i = 0; i < 50; i++) cc.FittedName(name, 3, 1000f, out w);
            Check("caption cache: steady frames measure nothing", calls == after && first == name + "  (3 left)" && w == first.Length * 8f + 2f, calls - after + " measures");

            string fresh = new string(name.ToCharArray());   // what Localization hands back for a name it does not cache
            after = calls;
            string again = cc.FittedName(fresh, 3, 1000f, out w);
            Check("caption cache: the same name as a new string is not measured again", calls == after && again == first, calls - after + " measures");

            string two = cc.FittedName(name, 2, 1000f, out w);
            string one = cc.FittedName(name, 1, 1000f, out w);
            Check("caption cache: the count changing rebuilds the caption, and one waypoint has no count",
                two == name + "  (2 left)" && one == name && w == name.Length * 8f + 2f, two + " / " + one);

            string cut = cc.FittedName(name, 2, 200f, out w);
            Check("caption cache: a narrower screen fits the caption again", cut == CaptionFit.Fit(name, "  (2 left)", 200f, m) && w == cut.Length * 8f + 2f, cut);

            cc.FittedName(name, 2, 1000f, out w);
            throwOn = 0;
            bool threw = false;
            try { cc.FittedName("Sealed Tower (Wooden Atgeir)", 2, 1000f, out w); } catch (InvalidOperationException) { threw = true; }
            string next = cc.FittedName("Sealed Tower (Wooden Atgeir)", 2, 1000f, out w);
            Check("caption cache: after a measure that throws, the next frame shows the current caption",
                threw && next == "Sealed Tower (Wooden Atgeir)  (2 left)" && w == next.Length * 8f + 2f, next);

            CaptionWidth cw = new CaptionWidth();
            after = calls;
            float a = cw.Of("123 m", m), b2 = cw.Of("123 m", m), c = cw.Of(new string("123 m".ToCharArray()), m);
            Check("caption cache: a caption's width is measured once while its text stays the same", calls == after + 1 && a == 42f && b2 == a && c == a, calls - after + " measures");
            throwOn = 0;
            threw = false;
            try { cw.Of("1.23 km", m); } catch (InvalidOperationException) { threw = true; }
            Check("caption cache: after a width measure that throws, the next call measures again", threw && cw.Of("1.23 km", m) == 7 * 8f + 2f, "");
        }

        // ---------------------------------------------------------------- the route file (1.5.0)

        private static void RouteFileTests()
        {
            RouteEntry e = new RouteEntry();
            e.X = 1234.5f; e.Y = 30.25f; e.Z = -567.75f; e.HasElevation = true; e.Borrowed = false; e.Name = "Camp";
            Check("route file: a waypoint is one line, x|altitude|z|hasElevation|ownsMarker|name", RouteFile.FormatLine(e) == "1234.5|30.25|-567.75|1|1|Camp", RouteFile.FormatLine(e));
            e.Borrowed = true; e.HasElevation = false; e.Name = "a|b\r\nc";
            Check("route file: a followed pin is ownsMarker 0, and a name cannot break the line", RouteFile.FormatLine(e) == "1234.5|30.25|-567.75|0|0|a b  c", RouteFile.FormatLine(e));
            Check("route file: the header names the edition", RouteFile.Header("TomTom") == "# TomTom queue - x|altitude|z|hasElevation|ownsMarker|name", RouteFile.Header("TomTom"));

            RouteEntry r;
            bool ok = RouteFile.TryParseLine("  1|2|3| 1 |0|Bed  ", out r);
            Check("route file: a line is read back (spaces around it and the flags trimmed)",
                ok && r.X == 1f && r.Y == 2f && r.Z == 3f && r.HasElevation && r.Borrowed && r.Name == "Bed", r.Name);
            ok = RouteFile.TryParseLine("1|2|3|1|Old name", out r);
            Check("route file: a 5-column line from before ownsMarker is a waypoint with its own marker", ok && !r.Borrowed && r.Name == "Old name", "");
            ok = RouteFile.TryParseLine("1|2|3|0", out r);
            Check("route file: 4 columns is a waypoint without a name", ok && !r.HasElevation && r.Name == "", "");
            ok = RouteFile.TryParseLine("1|NaN|3|1|1|x", out r);
            Check("route file: an unusable altitude is dropped, the waypoint kept", ok && r.Y == 0f && !r.HasElevation, "");
            Check("route file: blank lines, comments, short lines and unreadable or unreal positions are skipped",
                !RouteFile.TryParseLine("", out r) && !RouteFile.TryParseLine(null, out r) && !RouteFile.TryParseLine("# TomTom queue", out r)
                && !RouteFile.TryParseLine("1|2|3", out r) && !RouteFile.TryParseLine("a|2|3|1", out r)
                && !RouteFile.TryParseLine("NaN|2|3|1", out r) && !RouteFile.TryParseLine("1|2|Infinity|1", out r), "");

            // A round trip, with values the game's Mono prints exactly: it writes a float with 7 significant digits, so
            // one needing more comes back rounded there (and exactly on .NET).
            e.X = -10496.25f; e.Y = 0f; e.Z = 8192.5f; e.HasElevation = false; e.Borrowed = true; e.Name = "Den";
            ok = RouteFile.TryParseLine(RouteFile.FormatLine(e), out r);
            Check("route file: a followed pin survives the trip", ok && r.X == e.X && r.Z == e.Z && !r.HasElevation && r.Borrowed && r.Name == "Den", RouteFile.FormatLine(e));
            e.X = 1234.5678f;
            ok = RouteFile.TryParseLine(RouteFile.FormatLine(e), out r);
            Check("route file: any float comes back within 1 cm", ok && Math.Abs(r.X - e.X) < 0.01f, RouteFile.FormatLine(e));

            // A place Find found: its height is the world generator's estimate (1.5.0), written as 2; a reader older than
            // 1.5.0 takes anything but 1 as no height.
            e.X = 10f; e.Y = 42.5f; e.Z = -20f; e.HasElevation = true; e.HeightIsEstimate = true; e.Borrowed = false; e.Name = "Infested Mine";
            Check("route file: an estimated height is written as 2", RouteFile.FormatLine(e) == "10|42.5|-20|2|1|Infested Mine", RouteFile.FormatLine(e));
            ok = RouteFile.TryParseLine("10|42.5|-20|2|1|Infested Mine", out r);
            bool estimateRead = ok && r.HasElevation && r.HeightIsEstimate && r.Y == 42.5f;
            ok = RouteFile.TryParseLine("1|2|3|1|1|x", out r);
            Check("route file: 2 reads back as a height that is an estimate, 1 as a height of its own",
                estimateRead && ok && r.HasElevation && !r.HeightIsEstimate, "");
            ok = RouteFile.TryParseLine("1|NaN|3|2|1|x", out r);
            Check("route file: an unusable estimated altitude is dropped too", ok && !r.HasElevation && !r.HeightIsEstimate, "");
        }

        // ---------------------------------------------------------------- the 3D arrival test's altitude (1.5.0)

        private static void ArrivalTests()
        {
            Check("arrival: a height of the waypoint's own is used whatever the ground", ArrivalRules.TargetAltitude(true, false, 55f, true, 40f, 30f, 10f) == 55f, "");
            Check("arrival: no height, loaded land - the ground under the waypoint", ArrivalRules.TargetAltitude(false, false, 0f, true, 87f, 30f, 12f) == 87f, "");
            Check("arrival: no height, ground under water - the player's own altitude (horizontal)", ArrivalRules.TargetAltitude(false, false, 0f, true, 12f, 30f, 28.6f) == 28.6f, "");
            Check("arrival: ground exactly at the water level counts as land", ArrivalRules.TargetAltitude(false, false, 0f, true, 30f, 30f, 5f) == 30f, "");
            Check("arrival: no height and the ground not loaded - the player's own altitude", ArrivalRules.TargetAltitude(false, false, 0f, false, 0f, 30f, 44f) == 44f, "");
            Check("arrival: an unusable ground height is ignored",
                ArrivalRules.TargetAltitude(false, false, 0f, true, float.NaN, 30f, 7f) == 7f && ArrivalRules.TargetAltitude(false, false, 0f, true, float.PositiveInfinity, 30f, 7f) == 7f, "");
            Check("arrival: a Find place's estimated height gives way to the loaded ground", ArrivalRules.TargetAltitude(true, true, 60f, true, 48f, 30f, 10f) == 48f, "");
            Check("arrival: ...keeps its estimate where the ground is not loaded, and is horizontal over water",
                ArrivalRules.TargetAltitude(true, true, 60f, false, 0f, 30f, 10f) == 60f && ArrivalRules.TargetAltitude(true, true, 60f, true, 20f, 30f, 10f) == 10f, "");

            // The arrival test itself (1.5.1): armed once away, reached back within the radius, never while dead.
            Check("arrival: a waypoint is armed once the player is beyond the radius", ArrivalRules.Step(false, false, 10.5f, 10f) == ArrivalStep.Arm, "");
            Check("arrival: ...and not while still within it (a waypoint added where the player stands)",
                ArrivalRules.Step(false, false, 3f, 10f) == ArrivalStep.None && ArrivalRules.Step(false, false, 10f, 10f) == ArrivalStep.None, "");
            Check("arrival: an armed waypoint is reached within the radius, edge included",
                ArrivalRules.Step(true, false, 9.9f, 10f) == ArrivalStep.Reach && ArrivalRules.Step(true, false, 10f, 10f) == ArrivalStep.Reach, "");
            Check("arrival: ...and not beyond it", ArrivalRules.Step(true, false, 10.1f, 10f) == ArrivalStep.None, "");
            Check("arrival: dying next to the followed waypoint is not arriving (the 10 s before the respawn)",
                ArrivalRules.Step(true, true, 0f, 10f) == ArrivalStep.None && ArrivalRules.Step(true, true, 8f, 10f) == ArrivalStep.None, "");
            Check("arrival: ...nor is it arming while dead",
                ArrivalRules.Step(false, true, 500f, 10f) == ArrivalStep.None, "");
            Check("arrival: an unusable distance does nothing",
                ArrivalRules.Step(true, false, float.NaN, 10f) == ArrivalStep.None && ArrivalRules.Step(false, false, float.NaN, 10f) == ArrivalStep.None, "");
        }

        // ---------------------------------------------------------------- the map click (1.5.1)

        // MapClickRules.Decide: the pin nearest the click decides, whichever kind (a marker of ours, a followed pin, a pin
        // not yet followed, a ping, shout, other player's or event marker). The layouts: identical "Day 3" death pins close
        // together, one of them followed, the click aimed at another; a marker of ours next to a pin of the player's; and a
        // ping or another player's marker nearest the click. MapClickRules.PinShown: the pins the large map hides.
        private static void MapClickTests()
        {
            const float none = -1f;
            Check("map click: nothing within reach - a waypoint on the spot", MapClickRules.Decide(none, none, none, none) == MapClickAction.AddPoint, "");
            Check("map click: only a followed pin within reach - its waypoint is removed", MapClickRules.Decide(none, 12f, none, none) == MapClickAction.StopFollowing, "");
            Check("map click: only an unfollowed pin within reach - it is followed", MapClickRules.Decide(none, none, 40f, none) == MapClickAction.Follow, "");
            Check("map click: a click on a marker of ours removes its waypoint",
                MapClickRules.Decide(0f, none, none, none) == MapClickAction.RemoveOwnMarker && MapClickRules.Decide(2f, none, 30f, none) == MapClickAction.RemoveOwnMarker
                && MapClickRules.Decide(2f, 30f, 30f, none) == MapClickAction.RemoveOwnMarker, "");
            Check("map click: ...but a nearer pin wins over a marker of ours within reach (unlike a right-click delete)",
                MapClickRules.Decide(40f, none, 5f, none) == MapClickAction.Follow && MapClickRules.Decide(3f, none, 1f, none) == MapClickAction.Follow
                && MapClickRules.Decide(30f, 1f, 2f, none) == MapClickAction.StopFollowing, "");
            Check("map click: equally near - a marker of ours over a followed pin, and either over a pin not yet followed",
                MapClickRules.Decide(4f, 4f, none, none) == MapClickAction.RemoveOwnMarker && MapClickRules.Decide(4f, none, 4f, none) == MapClickAction.RemoveOwnMarker
                && MapClickRules.Decide(4f, 4f, 4f, none) == MapClickAction.RemoveOwnMarker, "");
            bool nearerWins = true;
            float[] followedAt = { 15f, 30f, 59f };
            for (int i = 0; i < followedAt.Length; i++)
                if (MapClickRules.Decide(none, followedAt[i], 0f, none) != MapClickAction.Follow || MapClickRules.Decide(none, followedAt[i], 2f, none) != MapClickAction.Follow) nearerWins = false;
            Check("map click: a click on a new pin follows it, though a followed pin is within reach (15, 30, 59 m away)", nearerWins, "");
            Check("map click: a click on the followed pin stops following it, though another pin is within reach",
                MapClickRules.Decide(none, 0f, 25f, none) == MapClickAction.StopFollowing && MapClickRules.Decide(none, 1f, 1.5f, none) == MapClickAction.StopFollowing, "");
            Check("map click: equally near - the followed pin decides (stopped, never followed twice)",
                MapClickRules.Decide(none, 5f, 5f, none) == MapClickAction.StopFollowing && MapClickRules.Decide(none, 0f, 0f, none) == MapClickAction.StopFollowing, "");
            Check("map click: a ping, shout, player or event marker nearest the click - a waypoint on the spot, the pins farther away left alone",
                MapClickRules.Decide(none, 40f, none, 3f) == MapClickAction.AddPoint && MapClickRules.Decide(40f, none, 30f, 3f) == MapClickAction.AddPoint
                && MapClickRules.Decide(none, none, none, 3f) == MapClickAction.AddPoint, "");
            Check("map click: ...but a pin nearer than it still decides, and equally near the pin does",
                MapClickRules.Decide(none, 2f, none, 3f) == MapClickAction.StopFollowing && MapClickRules.Decide(none, none, 1f, 3f) == MapClickAction.Follow
                && MapClickRules.Decide(3f, none, none, 3f) == MapClickAction.RemoveOwnMarker && MapClickRules.Decide(none, 3f, none, 3f) == MapClickAction.StopFollowing, "");
            Check("map click: equally near - a pin not yet followed over a ping, shout, player or event marker (followed)",
                MapClickRules.Decide(none, none, 3f, 3f) == MapClickAction.Follow && MapClickRules.Decide(none, none, 0f, 0f) == MapClickAction.Follow, "");
            Check("map click: an unusable distance counts as no pin",
                MapClickRules.Decide(float.NaN, float.NaN, 4f, none) == MapClickAction.Follow && MapClickRules.Decide(float.NaN, float.NaN, float.NaN, none) == MapClickAction.AddPoint
                && MapClickRules.Decide(float.NaN, 3f, float.NaN, none) == MapClickAction.StopFollowing, "");

            bool ordinals = MapClickRules.Ordinal(1) == "1st" && MapClickRules.Ordinal(2) == "2nd" && MapClickRules.Ordinal(3) == "3rd"
                && MapClickRules.Ordinal(4) == "4th" && MapClickRules.Ordinal(11) == "11th" && MapClickRules.Ordinal(12) == "12th"
                && MapClickRules.Ordinal(13) == "13th" && MapClickRules.Ordinal(21) == "21st" && MapClickRules.Ordinal(22) == "22nd"
                && MapClickRules.Ordinal(23) == "23rd" && MapClickRules.Ordinal(111) == "111th" && MapClickRules.Ordinal(101) == "101st";
            Check("map click: ordinals", ordinals, MapClickRules.Ordinal(1) + " " + MapClickRules.Ordinal(12) + " " + MapClickRules.Ordinal(22));
            Check("map click: a followed pin at the front of the route is 'set'",
                MapClickRules.FollowMessage("Day 3", 0) == "Waypoint set: Day 3", MapClickRules.FollowMessage("Day 3", 0));
            Check("map click: ...behind others it is 'queued', with its place",
                MapClickRules.FollowMessage("Day 3", 1) == "Waypoint queued (2nd): Day 3", MapClickRules.FollowMessage("Day 3", 1));
            Check("map click: an unnamed pin is 'marker'",
                MapClickRules.FollowMessage("", 0) == "Waypoint set: marker" && MapClickRules.FollowMessage(null, 2) == "Waypoint queued (3rd): marker", "");
            Check("map click: a point on the spot is 'added', or 'queued' behind others",
                MapClickRules.AddedMessage(0) == "Waypoint added" && MapClickRules.AddedMessage(3) == "Waypoint queued (4th)", MapClickRules.AddedMessage(3));

            // Which pins the large map shows (Minimap.UpdatePins): an icon type the filter hides, and a shared pin (an owner
            // other than 0) while shared pins are faded out, are hidden; a click weighs only the pins shown.
            bool[] filter = { true, false, true };
            Check("pins shown: every pin while the map hides nothing",
                MapClickRules.PinShown(filter, 0, 1f, 0L) && MapClickRules.PinShown(filter, 2, 1f, 12345L), "");
            Check("pins shown: a pin whose icon type the map's filter hides is not",
                !MapClickRules.PinShown(filter, 1, 1f, 0L) && !MapClickRules.PinShown(filter, 1, 1f, 12345L), "");
            Check("pins shown: a shared pin while shared pins are hidden is not, at any fade down to 0",
                !MapClickRules.PinShown(filter, 0, 0f, 12345L) && !MapClickRules.PinShown(filter, 0, -0.5f, -7L), "");
            Check("pins shown: ...but is while they fade in or out, and the player's own pins (owner 0) always are",
                MapClickRules.PinShown(filter, 0, 0.01f, 12345L) && MapClickRules.PinShown(filter, 0, 0f, 0L), "");
            Check("pins shown: an icon filter that could not be read, or a type it does not list, hides nothing",
                MapClickRules.PinShown(null, 1, 1f, 0L) && MapClickRules.PinShown(filter, 3, 1f, 0L) && MapClickRules.PinShown(filter, -1, 1f, 0L), "");
            Check("pins shown: an unusable fade hides shared pins, as the game's own test does",
                !MapClickRules.PinShown(filter, 0, float.NaN, 12345L) && MapClickRules.PinShown(filter, 0, float.NaN, 0L), "");
        }

        // ---------------------------------------------------------------- a route that could not be read (1.4.1)

        private static RouteEntry Entry(string name, float x, float z, bool borrowed)
        {
            RouteEntry e = new RouteEntry();
            e.Name = name; e.X = x; e.Z = z; e.Borrowed = borrowed;
            return e;
        }

        // RouteReadGate (saving refused while a route could not be read, the retry delays, one warning) and RouteMerge
        // (what was queued meanwhile stays first; the restored route follows, without twins).
        private static void RouteReadTests()
        {
            RouteReadGate g = new RouteReadGate();
            Check("route read: nothing pending at first - saving allowed, nothing to retry or warn about",
                !g.Pending && !g.Due(0f) && !g.WarnSaveRefused(), "");
            g.Failed(10f);
            Check("route read: after a failed read saving is refused, and the read is due again 2 s later",
                g.Pending && g.Failures == 1 && !g.Due(11.9f) && g.Due(12f), "");
            Check("route read: a refused save is warned about once", g.WarnSaveRefused() && !g.WarnSaveRefused() && !g.WarnSaveRefused(), "");
            g.Failed(12f);
            Check("route read: each further failure waits 2 s longer (4 s after the second)", g.Failures == 2 && !g.Due(15.9f) && g.Due(16f), "");
            for (int i = 0; i < 40; i++) g.Failed(100f);
            Check("route read: never more than 30 s between reads", !g.Due(129.9f) && g.Due(130f) && RouteReadGate.RetryDelay(1000) == 30f, "");
            Check("route read: still pending, and still warned about only once", g.Pending && !g.WarnSaveRefused(), "");
            g.Succeeded();
            Check("route read: a read that succeeds lets saving resume", !g.Pending && g.Failures == 0 && !g.Due(1000f), "");
            g.Failed(0f);
            Check("route read: a later failure is warned about again", g.WarnSaveRefused(), "");
            g.Reset();
            Check("route read: moving to another world forgets it", !g.Pending && !g.WarnSaveRefused(), "");
            g.NotRead(50f);
            Check("route read: a route not read at all (PersistWaypoints off) is not saved over, is read at once, and is no warning",
                g.Pending && g.Due(50f) && !g.WarnSaveRefused() && g.Failures == 0, "");
            g.Failed(50f);
            Check("route read: ...and a read of it that fails then counts as the first failure", g.Pending && g.Failures == 1 && g.Due(52f) && !g.Due(51.9f), "");
            g.Reset();

            Check("route read: two waypoints exactly 1 m apart are the same, a little more is not",
                RouteMerge.Same(Entry("a", 0, 0, false), Entry("a", 1, 0, false)) && !RouteMerge.Same(Entry("a", 0, 0, false), Entry("a", 1.01f, 0, false)), "");
            List<RouteEntry> queued = new List<RouteEntry>();
            List<RouteEntry> restored = new List<RouteEntry>();
            restored.Add(Entry("Camp", 100, 200, false));
            restored.Add(Entry("", 5, 5, false));
            restored.Add(Entry("Bed", -40, 30, true));
            List<int> add = RouteMerge.ToAppend(queued, restored);
            Check("route read: with nothing queued meanwhile, the whole route comes back, in order",
                add.Count == 3 && add[0] == 0 && add[1] == 1 && add[2] == 2, add.Count.ToString());
            queued.Add(Entry("Bed", -40.6f, 30.7f, true));    // the same followed pin, queued again meanwhile (0.92 m off)
            queued.Add(Entry("Camp", 100, 201.01f, false));    // same name, 1.01 m off: another place
            queued.Add(Entry("camp", 100, 200, false));        // another name (case counts)
            queued.Add(Entry("Camp", 100, 200, true));         // same spot and name, but a followed pin
            add = RouteMerge.ToAppend(queued, restored);
            Check("route read: a restored waypoint matching one queued meanwhile (same name and kind, within 1 m) is not added twice",
                add.Count == 2 && add[0] == 0 && add[1] == 1,
                string.Join(",", Array.ConvertAll<int, string>(add.ToArray(), delegate (int v) { return v.ToString(); })));
            restored.Add(Entry("Bed", -40, 30, true));         // the file held it twice
            add = RouteMerge.ToAppend(queued, restored);
            Check("route read: one queued waypoint stands in for one restored twin only", add.Count == 3 && add[2] == 3, add.Count.ToString());
            Check("route read: nothing restored, nothing added; no queue is the same as an empty one",
                RouteMerge.ToAppend(queued, new List<RouteEntry>()).Count == 0 && RouteMerge.ToAppend(null, restored).Count == restored.Count
                && RouteMerge.ToAppend(queued, null).Count == 0, "");

            // The case RouteReadGate is for: on Windows a route file another program holds with FileShare.None cannot be read.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                string dir = Path.Combine(Path.GetTempPath(), "waypointer-routeread-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string f = Path.Combine(dir, "waypoints_1.txt");
                    File.WriteAllText(f, "1|2|3|1|1|Camp\n");
                    string what = null;
                    using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { File.ReadAllLines(f); }
                        catch (Exception e) { what = e.GetType().Name; }
                    }
                    Check("route read: a route file held with FileShare.None cannot be read", what == "IOException", what ?? "it was read");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }
            }
        }

        // ---------------------------------------------------------------- the server protocol (1.3.0)

        private static void ProtocolTests()
        {
            Check("server: WhoMayFind - Everyone lets every player, AdminsOnly only admins, Nobody no one",
                FindProtocol.MayFind(FindPolicy.Everyone, false) && FindProtocol.MayFind(FindPolicy.AdminsOnly, true)
                && !FindProtocol.MayFind(FindPolicy.AdminsOnly, false) && !FindProtocol.MayFind(FindPolicy.Nobody, true), "");

            // The caller's side of WhoMayFind, for Vegvisir-style requests (contextKnown, fromConnection, knownPlayer, policy, isAdmin).
            Check("server: the host's own call is always let through; a connection that is not a player's never is",
                FindProtocol.CallerMayFind(true, false, false, FindPolicy.Nobody, false)
                && !FindProtocol.CallerMayFind(true, true, false, FindPolicy.Everyone, true)
                && !FindProtocol.CallerMayFind(true, true, false, FindPolicy.AdminsOnly, true), "");
            Check("server: a known player gets the policy - Everyone yes, AdminsOnly only an admin, Nobody no",
                FindProtocol.CallerMayFind(true, true, true, FindPolicy.Everyone, false)
                && FindProtocol.CallerMayFind(true, true, true, FindPolicy.AdminsOnly, true)
                && !FindProtocol.CallerMayFind(true, true, true, FindPolicy.AdminsOnly, false)
                && !FindProtocol.CallerMayFind(true, true, true, FindPolicy.Nobody, true), "");
            Check("server: without the connection patch nobody can be told apart - only Everyone lets a call through",
                FindProtocol.CallerMayFind(false, false, false, FindPolicy.Everyone, false)
                && !FindProtocol.CallerMayFind(false, false, false, FindPolicy.AdminsOnly, true)
                && !FindProtocol.CallerMayFind(false, false, false, FindPolicy.Nobody, false)
                && !FindProtocol.CallerMayFind(false, true, true, FindPolicy.AdminsOnly, true), "");

            Check("server: a hello carries the protocol version",
                FindProtocol.DecodeHello(FindProtocol.EncodeHello("TomTom 1.3.0")) == FindProtocol.Version
                && FindProtocol.DecodeHello(new byte[] { FindProtocol.KindHello }) == -1
                && FindProtocol.DecodeHello(null) == -1, "");

            FindRequest q = new FindRequest();
            q.SearchId = 7; q.Query = "Wooden Spear"; q.X = -1234.5f; q.Y = 31f; q.Z = 876.25f; q.Range = 2500f; q.CheckChests = true;
            FindRequest back = FindProtocol.DecodeFind(FindProtocol.EncodeFind(q));
            Check("server: a Find request survives the trip",
                back != null && back.SearchId == 7 && back.Query == "Wooden Spear" && back.X == -1234.5f && back.Y == 31f
                && back.Z == 876.25f && back.Range == 2500f && back.CheckChests, "");

            bool rejects = true;
            FindRequest bad = new FindRequest();
            bad.Query = "Wooden Spear"; bad.Range = 20000f;
            if (FindProtocol.DecodeFind(FindProtocol.EncodeFind(bad)) != null) rejects = false;       // range over 10 km
            bad.Range = 50f;
            if (FindProtocol.DecodeFind(FindProtocol.EncodeFind(bad)) != null) rejects = false;       // under 100 m
            bad.Range = float.NaN;
            if (FindProtocol.DecodeFind(FindProtocol.EncodeFind(bad)) != null) rejects = false;
            bad.Range = 1000f; bad.X = float.PositiveInfinity;
            if (FindProtocol.DecodeFind(FindProtocol.EncodeFind(bad)) != null) rejects = false;
            byte[] good = FindProtocol.EncodeFind(q);
            for (int cut = 0; cut < good.Length; cut++)
            {
                byte[] part = new byte[cut];
                Array.Copy(good, part, cut);
                if (FindProtocol.DecodeFind(part) != null) rejects = false;                            // every truncation
            }
            byte[] otherVersion = (byte[])good.Clone();
            otherVersion[1] = (byte)(FindProtocol.Version + 1);
            if (FindProtocol.DecodeFind(otherVersion) != null) rejects = false;
            byte[] hugeString = new byte[] { FindProtocol.KindFind, (byte)FindProtocol.Version, 0, 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
            if (FindProtocol.DecodeFind(hugeString) != null) rejects = false;
            Check("server: a malformed Find is refused, never thrown on (bad range or position, truncated, other version, huge string)", rejects, "");

            ServerInfo info = new ServerInfo();
            info.Server = "TomTom 1.3.0"; info.Policy = FindPolicy.AdminsOnly; info.MayFind = true;
            ServerInfo infoBack = FindProtocol.DecodeInfo(FindProtocol.EncodeInfo(info));
            Check("server: the server's info survives the trip",
                infoBack != null && infoBack.Protocol == FindProtocol.Version && infoBack.Server == "TomTom 1.3.0"
                && infoBack.Policy == FindPolicy.AdminsOnly && infoBack.MayFind, "");
            byte[] newer = FindProtocol.EncodeInfo(info);
            newer[1] = (byte)(FindProtocol.Version + 1);
            ServerInfo newerBack = FindProtocol.DecodeInfo(newer);
            Check("server: a server of another protocol version is read as one without the helper",
                newerBack != null && newerBack.Protocol != FindProtocol.Version && !newerBack.MayFind, "");

            // A result of 600 places travels as three Parts (256 + 256 + 88) and a Done, and comes back whole.
            List<SearchHit> many = new List<SearchHit>();
            for (int i = 0; i < 600; i++)
            {
                SearchHit h = Hit(i % 3 == 0 ? "Ruin1" : (i % 3 == 1 ? "SwampHut1" : "Pickable_StoneRock"), i * 10f, -i, i % 2 == 0);
                h.Label = h.Prefab + " label";
                h.IsObject = i % 3 == 2;
                h.Possible = i == 5;
                many.Add(h);
            }
            List<byte[]> messages = FindProtocol.EncodeResult(9, many, 1234, 56, 7);
            List<SearchHit> received = new List<SearchHit>();
            FoundMessage done = null;
            bool allFound = true;
            for (int i = 0; i < messages.Count; i++)
            {
                FoundMessage m = FindProtocol.DecodeFound(messages[i]);
                if (m == null || m.SearchId != 9) { allFound = false; continue; }
                if (m.Status == FindStatus.Part) received.AddRange(m.Hits);
                else if (m.Status == FindStatus.Done) done = m;
            }
            bool same = received.Count == many.Count;
            for (int i = 0; same && i < many.Count; i++)
            {
                SearchHit a = many[i], b = received[i];
                same = a.Prefab == b.Prefab && a.Label == b.Label && a.X == b.X && a.Y == b.Y && a.Z == b.Z
                    && a.Placed == b.Placed && a.Possible == b.Possible && a.IsObject == b.IsObject;
            }
            Check("server: every place found comes back, in order, flags and labels intact - nothing trimmed on the server",
                allFound && messages.Count == 4 && same && done != null && done.Total == 600 && done.ObjectsRead == 1234
                && done.ChestPlacesChecked == 56 && done.ChestPlacesSkipped == 7, messages.Count + " messages");

            List<byte[]> empty = FindProtocol.EncodeResult(3, new List<SearchHit>(), 0, 0, 0);
            FoundMessage emptyDone = empty.Count == 1 ? FindProtocol.DecodeFound(empty[0]) : null;
            Check("server: an empty result is a single Done with a count of 0",
                emptyDone != null && emptyDone.Status == FindStatus.Done && emptyDone.Total == 0, "");

            FoundMessage refused = FindProtocol.DecodeFound(FindProtocol.EncodeStatus(4, FindStatus.Refused));
            Check("server: a refusal says so", refused != null && refused.SearchId == 4 && refused.Status == FindStatus.Refused, "");

            bool partSafe = true;
            byte[] part0 = messages[0];
            for (int cut = 0; cut < part0.Length; cut += 7)
            {
                byte[] p = new byte[cut];
                Array.Copy(part0, p, cut);
                if (FindProtocol.DecodeFound(p) != null && cut < part0.Length) partSafe = false;
            }
            byte[] badIndex = (byte[])empty[0].Clone();
            badIndex[5] = 99;                                                                            // no such status
            if (FindProtocol.DecodeFound(badIndex) != null) partSafe = false;
            Check("server: a truncated or malformed answer is dropped, never thrown on", partSafe, "");
        }

        private static void Check(string label, bool condition, string detail)
        {
            if (condition) Console.WriteLine("  ok   " + label);
            else Fail(label, detail);
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            Console.WriteLine("  FAIL " + what + "  ->  " + detail);
        }
    }
}
