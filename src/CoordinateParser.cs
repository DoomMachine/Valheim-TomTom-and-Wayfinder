// TomTom only. Wayfinder is the edition WITHOUT coordinate entry, so this file compiles to nothing
// there - the guard is belt and braces on top of Wayfinder.csproj excluding the file outright, so the
// parser cannot reappear in Wayfinder even through a build that globs every source file.
#if !WAYFINDER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Waypointer
{
    /// <summary>One parsed coordinate entry, still expressed in the axis convention the player typed.</summary>
    public class ParsedCoord
    {
        public float A;            // first axis typed by the player  (X)
        public float B;            // second axis typed by the player (Y)
        public float Elevation;    // optional third axis             (Z)
        public bool HasElevation;
        public string Name;

        public ParsedCoord()
        {
            Name = "";
        }
    }

    /// <summary>
    /// Parses free-form coordinate text. Deliberately liberal: accepts commas, spaces, semicolons,
    /// tabs and newlines, tolerates brackets, and allows an optional per-entry name.
    /// </summary>
    public static class CoordinateParser
    {
        /// <summary>Sanity bound - the Valheim world is roughly 10,500 m in radius.</summary>
        public const float CoordinateLimit = 21000f;

        private static readonly char[] Separators = new char[] { ',', ';', ' ', '\t' };

        // Decorative characters that carry no meaning: brackets, equals, angle brackets, colon,
        // double quote (34) and apostrophe (39).
        private static readonly char[] Decoration = new char[]
        {
            '(', ')', '[', ']', '{', '}', '=', '<', '>', ':', (char)34, (char)39
        };

        // A digit followed by a comma and exactly three more digits: the signature of thousands
        // grouping. Fields the player separated deliberately are written with a space after the comma.
        private static readonly Regex DigitGrouping = new Regex(@"\d,\d{3}(?!\d)");

        private static string Strip(string text)
        {
            StringBuilder cleaned = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                cleaned.Append(Array.IndexOf(Decoration, c) >= 0 ? ' ' : c);
            }
            return cleaned.ToString();
        }

        /// <summary>
        /// Parses a whole block of text - one coordinate per line. A line that fails to parse is
        /// reported and skipped, so a single bad line never discards an otherwise good list.
        /// </summary>
        public static List<ParsedCoord> ParseList(string text, out List<string> errors)
        {
            List<ParsedCoord> result = new List<ParsedCoord>();
            errors = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("//")) continue;

                ParsedCoord pc;
                string error;
                if (TryParseOne(line, out pc, out error))
                    result.Add(pc);
                else
                    errors.Add(string.Format("line {0}: {1}", i + 1, error));
            }
            return result;
        }

        /// <summary>Parses a single entry such as "123, 456", "(123 456 -20)" or "Camp: 123,456".</summary>
        public static bool TryParseOne(string line, out ParsedCoord result, out string error)
        {
            result = null;
            error = null;
            if (line == null) { error = "empty"; return false; }

            string work = line.Trim();
            if (work.Length == 0) { error = "empty"; return false; }

            // Digit grouping such as "1,234,567" would otherwise be read as three separate fields and
            // send the player somewhere completely different, so it is rejected rather than guessed at.
            // Deliberate fields written with spaces ("1, 234, 567") do not match this pattern.
            if (DigitGrouping.IsMatch(work))
            {
                error = "looks like digit grouping (1,234) - remove the grouping and use a dot for decimals";
                return false;
            }

            // "Name: 1, 2" - a label before a colon. The label may itself be numeric ("42: 123,456"),
            // so the test is whether what follows still holds a usable coordinate pair.
            string leadingName = null;
            int colon = work.IndexOf(':');
            if (colon > 0)
            {
                string left = work.Substring(0, colon).Trim();
                string right = work.Substring(colon + 1);
                // An axis label is not a name - "x:-500, y:30, z:200" must keep its x.
                if (left.Length > 0 && !IsAxisLabel(left) && CountNumericTokens(right) >= 2)
                {
                    leadingName = left;
                    work = right.Trim();
                }
            }

            string[] tokens = Strip(work).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) { error = "no coordinates found"; return false; }

            List<float> numbers = new List<float>();
            List<string> leadingWords = new List<string>();
            List<string> trailingWords = new List<string>();
            bool numbersDone = false;

            // When the entry carries explicit axis labels we honour them instead of reading positionally,
            // because "X=-500 Y=30 Z=200" states plainly that 30 is the altitude - reading it as the
            // north/south axis would silently send the player to the wrong place.
            bool labelled = false;
            float labX = 0f, labY = 0f, labZ = 0f;
            bool hasX = false, hasY = false, hasZ = false;
            char pendingAxis = ' ';

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];

                if (!numbersDone && numbers.Count < 3 && IsAxisLabel(token))
                {
                    pendingAxis = char.ToLowerInvariant(token[0]);
                    continue;
                }

                float value;
                if (!numbersDone && numbers.Count < 3 && TryParseNumber(token, out value))
                {
                    if (pendingAxis != ' ')
                    {
                        labelled = true;
                        if (pendingAxis == 'x') { labX = value; hasX = true; }
                        else if (pendingAxis == 'y') { labY = value; hasY = true; }
                        else { labZ = value; hasZ = true; }
                        pendingAxis = ' ';
                    }
                    numbers.Add(value);
                }
                else if (numbers.Count == 0)
                {
                    // Words before any number are part of the name, not the end of the coordinates,
                    // so "Silver vein 1234 -567" reads correctly.
                    leadingWords.Add(token);
                }
                else
                {
                    // Once numbers have started, the first non-numeric token ends them.
                    numbersDone = true;
                    trailingWords.Add(token);
                }
            }

            if (numbers.Count < 2)
            {
                error = "need at least two numbers (X and Y)";
                return false;
            }

            ParsedCoord parsed = new ParsedCoord();

            if (labelled && hasX && hasZ)
            {
                // Labels name Valheim's own axes, where y is altitude.
                parsed.A = labX;
                parsed.B = labZ;
                parsed.Elevation = labY;
                parsed.HasElevation = hasY;
            }
            else
            {
                parsed.A = numbers[0];
                parsed.B = numbers[1];
                if (numbers.Count >= 3)
                {
                    parsed.Elevation = numbers[2];
                    parsed.HasElevation = true;
                }
            }

            string trailingName = string.Join(" ", trailingWords.ToArray()).Trim();
            string leadingWordName = string.Join(" ", leadingWords.ToArray()).Trim();
            if (!string.IsNullOrEmpty(leadingName))
                parsed.Name = leadingName;
            else if (trailingName.Length > 0)
                parsed.Name = trailingName;
            else if (leadingWordName.Length > 0)
                parsed.Name = leadingWordName;

            if (!IsSane(parsed.A) || !IsSane(parsed.B) || (parsed.HasElevation && !IsSane(parsed.Elevation)))
            {
                error = "coordinate out of range";
                return false;
            }

            result = parsed;
            return true;
        }

        /// <summary>A lone x, y or z, which people often paste as an axis label rather than a name.</summary>
        private static bool IsAxisLabel(string token)
        {
            if (token == null || token.Length != 1) return false;
            char c = char.ToLowerInvariant(token[0]);
            return c == 'x' || c == 'y' || c == 'z';
        }

        private static bool IsSane(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
            return Mathf.Abs(f) <= CoordinateLimit;
        }

        /// <summary>
        /// Parses with the invariant culture so that a dot is always the decimal separator, whatever the
        /// machine locale is. A comma is always a field separator here, never a decimal comma.
        ///
        /// Words like "NaN" and "Infinity" parse as floats under these rules, so they are rejected here
        /// and fall through to the name instead - otherwise a waypoint called "Infinity Tower" could
        /// never be created.
        /// </summary>
        private static bool TryParseNumber(string token, out float value)
        {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
                return false;
            }
            return true;
        }

        private static int CountNumericTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string[] parts = Strip(text).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                float ignored;
                if (TryParseNumber(parts[i], out ignored)) count++;
            }
            return count;
        }

        /// <summary>
        /// Converts a parsed entry into a Valheim world position, where a Vector3 is
        /// (east/west, ALTITUDE, north/south).
        ///
        /// Default convention: the player types two horizontal axes and an optional ELEVATION, so
        /// the third value becomes the altitude:            (A, elevation, B)
        ///
        /// With <paramref name="rawValheimOrder"/> the three values are Valheim's own x, y, z, where the
        /// SECOND value is already the altitude:             (A, B, third)
        /// Two values in raw mode are still read as the two horizontal axes, because that is the only
        /// sensible reading of a pair.
        /// </summary>
        public static Vector3 ToWorld(ParsedCoord pc, bool rawValheimOrder)
        {
            if (pc == null) return Vector3.zero;

            if (rawValheimOrder && pc.HasElevation)
                return new Vector3(pc.A, pc.B, pc.Elevation);

            float altitude = pc.HasElevation ? pc.Elevation : 0f;
            return new Vector3(pc.A, altitude, pc.B);
        }
    }
}
#endif
