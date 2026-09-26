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
            HotkeysTests();
            SearchTests();

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

                skip.Value = KeyCode.Plus;
                reads = ZInput.Reads;
                Check("keys: the unreadable key is skipped in every setting", !Hotkeys.Pressed(skip) && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 1,
                    "reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);

                toggle.Value = KeyCode.F11;
                ZInput.Down.Add(KeyCode.F11);
                Check("keys: a setting changed to a readable key works at once", Hotkeys.Pressed(toggle) && ZInput.LastRead == "GetKeyDown:F11:False", ZInput.LastRead);

                // Plugin.BindConfig calls Forget when any key setting changes.
                Hotkeys.Forget();
                reads = ZInput.Reads;
                bool retried = Hotkeys.Pressed(skip);
                Check("keys: after Forget an unreadable key is tried once more and warned about once more",
                    !retried && ZInput.Reads == reads + 1 && Plugin.Log.Warnings.Count == 2,
                    "reads " + (ZInput.Reads - reads) + ", warnings " + Plugin.Log.Warnings.Count);

                modifier.Value = KeyCode.WheelUp;
                threw = false;
                held = true;
                try { held = Hotkeys.Held(modifier); }
                catch (Exception) { threw = true; }
                Check("keys: an unreadable modifier reads as not held, with one warning",
                    !threw && !held && Plugin.Log.Warnings.Count == 3 && Plugin.Log.Warnings[2].Contains("MapModifierKey"),
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
                    !threw && !first && !later && ZInput.Reads == reads && Plugin.Log.Warnings.Count == 4
                        && Plugin.Log.Warnings[3].Contains("InvalidOperationException"),
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
                && !File.Exists(f + SafeFile.NewSuffix) && !File.Exists(f + SafeFile.OldSuffix);
        }

        private static string Leftovers(string f)
        {
            return "file=" + (File.Exists(f) ? File.ReadAllText(f) : "(none)")
                + " new=" + File.Exists(f + SafeFile.NewSuffix) + " old=" + File.Exists(f + SafeFile.OldSuffix);
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
            }
            Check("search: every query has a unique name, something to look for, and no location listed twice", uniqueNames && nonEmpty, "");
            Check("search: every location has a prefab and a label", labelled, "");
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
            SearchRules.ResolveUniqueOnServer(hits);
            Check("search: server, one candidate placed - only the placed one stays, and it is not 'possible'",
                CountPrefab(hits, "Vendor_BlackForest") == 1 && hits[0].X == 200 && !hits[0].Possible
                && CountPrefab(hits, "Hildir_camp") == 1 && hits[1].Possible, "");

            // --- unique places, on a client
            hits.Clear();
            hits.Add(Hit("BigRockClearing", 10, 0, false));
            hits.Add(Hit("BigRockClearing", 20, 0, false));
            hits.Add(Hit("BogWitch_Camp", 30, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, null);
            Check("search: client, several answers - all possible; a single answer is the real place",
                hits[0].Possible && hits[1].Possible && !hits[2].Possible, "");

            hits.Clear();
            hits.Add(Hit("Hildir_camp", 10, 0, false));
            hits.Add(Hit("Hildir_camp", 500, 0, false));
            Dictionary<string, float[]> icons = new Dictionary<string, float[]>();
            icons["Hildir_camp"] = new float[] { 499, 0, 1 };
            SearchRules.ResolveUniqueOnClient(hits, icons);
            Check("search: client, a merchant's placed icon picks the real one and drops the rest",
                hits.Count == 1 && hits[0].X == 500 && hits[0].Placed && !hits[0].Possible, "");

            // --- resolve before the range cut: the real place outside range must not make a near candidate look real
            hits.Clear();
            hits.Add(Hit("Vendor_BlackForest", 100, 0, false));
            hits.Add(Hit("Vendor_BlackForest", 5000, 0, false));
            SearchRules.ResolveUniqueOnClient(hits, null);
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
                && SearchRules.WaypointName(witch, SearchCatalog.Queries[SearchCatalog.Queries.Length - 1]) == "Bog Witch (possible)",
                SearchRules.WaypointName(house, axeHead));

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
