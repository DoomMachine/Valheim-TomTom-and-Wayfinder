using System;
using System.Collections.Generic;
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

            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : (_failures + " TEST(S) FAILED"));
            return _failures == 0 ? 0 : 1;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.001f; }

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
