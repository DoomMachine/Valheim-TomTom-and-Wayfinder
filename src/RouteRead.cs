using System;
using System.Collections.Generic;
using System.Globalization;

namespace Waypointer
{
    /// <summary>One waypoint as the route file holds it, without Unity types (x = east/west, y = altitude, z = north/south).</summary>
    internal struct RouteEntry
    {
        public float X, Y, Z;
        public bool HasElevation;
        public bool HeightIsEstimate;
        public bool Borrowed;
        public string Name;
    }

    /// <summary>
    /// The route file's lines: a header, then one waypoint per line, x|altitude|z|hasElevation|ownsMarker|name, numbers
    /// in the invariant culture (older files have no ownsMarker column). Unity-free, so the tests read and write it.
    /// </summary>
    internal static class RouteFile
    {
        public static string Header(string edition)
        {
            return "# " + edition + " queue - x|altitude|z|hasElevation|ownsMarker|name";
        }

        /// <summary>
        /// ownsMarker records intent (Borrowed), never whether a stand-in marker is showing now, so a followed pin that
        /// was briefly missing is still followed after the next load.
        /// </summary>
        public static string FormatLine(RouteEntry e)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}",
                e.X, e.Y, e.Z, e.HasElevation ? (e.HeightIsEstimate ? "2" : "1") : "0", e.Borrowed ? "0" : "1", SanitizeName(e.Name));
        }

        /// <summary>A line that holds a waypoint, or was meant to: not blank and not a comment (#), like the header.</summary>
        public static bool IsDataLine(string raw)
        {
            if (raw == null) return false;
            string line = raw.Trim();
            return line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal);
        }

        /// <summary>False for a blank line, a comment (#), and a line that cannot be read, which is skipped.</summary>
        public static bool TryParseLine(string raw, out RouteEntry e)
        {
            e = new RouteEntry();
            string line = raw == null ? "" : raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) return false;

            string[] parts = line.Split('|');
            if (parts.Length < 4) return false;

            float x, y, z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;

            // "NaN" and "Infinity" parse as floats; a position that is not a real place is skipped like any other
            // unreadable line (CoordinateParser refuses them for the same reason).
            if (!IsFinite(x) || !IsFinite(z)) return false;

            // 1: a height of its own; 2 (since 1.5.0): the world generator's estimate, a place Find found. A reader
            // before 1.5.0 takes 2 as no height, which only makes its arrival test horizontal.
            string heightFlag = parts[3].Trim();
            bool hasElev = heightFlag == "1" || heightFlag == "2";
            bool estimate = heightFlag == "2";
            if (!IsFinite(y)) { y = 0f; hasElev = false; estimate = false; }   // altitude unusable: treat as not given

            bool owns = true;
            string name = "";
            if (parts.Length >= 6)
            {
                owns = parts[4].Trim() != "0";   // only an explicit 0 means "follows a player's pin"
                name = parts[5];
            }
            else if (parts.Length == 5)
            {
                name = parts[4];
            }

            e.X = x; e.Y = y; e.Z = z;
            e.HasElevation = hasElev;
            e.HeightIsEstimate = estimate;
            e.Borrowed = !owns;
            e.Name = name;
            return true;
        }

        /// <summary>One waypoint per line, '|'-separated: a name must not contain either separator.</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    }

    /// <summary>
    /// Whether the saved route of the world the queue belongs to has been read. A route that is there but could not be
    /// read when the world loaded (another program - a backup, sync or antivirus tool - held it) must never be replaced
    /// by the queue the player builds meanwhile: WaypointManager.SaveIfDirty refuses while Pending, and the read is tried
    /// again after a growing delay. Unity-free, so the tests drive it with their own clock.
    /// </summary>
    internal sealed class RouteReadGate
    {
        public const float MaxRetryDelay = 30f;

        private bool _pending;
        private int _failures;
        private float _nextAttempt;
        private bool _saveRefusalWarned;

        /// <summary>True while the route has not been read: from a failed read (or NotRead) until a read succeeds or Reset.</summary>
        public bool Pending { get { return _pending; } }

        /// <summary>Failed reads since the last success or reset.</summary>
        public int Failures { get { return _failures; } }

        /// <summary>Seconds to wait after that many failed reads: 2, 4, 6 ... and at most 30.</summary>
        public static float RetryDelay(int failures)
        {
            return Math.Min(2f * failures, MaxRetryDelay);
        }

        public void Reset()
        {
            _pending = false;
            _failures = 0;
            _nextAttempt = 0f;
            _saveRefusalWarned = false;
        }

        public void Failed(float now)
        {
            _pending = true;
            _failures++;
            _nextAttempt = now + RetryDelay(_failures);
        }

        /// <summary>
        /// Not read at all (PersistWaypoints was off when the world loaded): pending and due at once, and a refused save
        /// is not warned about - nothing is wrong with the file.
        /// </summary>
        public void NotRead(float now)
        {
            _pending = true;
            _nextAttempt = now;
            _saveRefusalWarned = true;
        }

        public void Succeeded()
        {
            Reset();
        }

        /// <summary>True when a read is pending and its delay has run out.</summary>
        public bool Due(float now)
        {
            return _pending && now >= _nextAttempt;
        }

        /// <summary>True the first time a save is refused while a read is pending; false after that, until Reset.</summary>
        public bool WarnSaveRefused()
        {
            if (!_pending || _saveRefusalWarned) return false;
            _saveRefusalWarned = true;
            return true;
        }
    }

    /// <summary>
    /// How a route read late joins the waypoints queued meanwhile: those stay first (the arrow keeps its target), and the
    /// restored ones follow in file order - except one that matches a queued waypoint (same name, same kind: own marker
    /// or followed pin, within SameSpot on the map), which is left out once per match, so a followed pin is not claimed
    /// twice. Unity-free.
    /// </summary>
    internal static class RouteMerge
    {
        /// <summary>1 m: the tolerance Valheim uses to recognise a duplicate pin in a map sync (WaypointManager.AdoptRadius).</summary>
        public const float SameSpot = 1f;

        /// <summary>The indices into <paramref name="restored"/> to append after <paramref name="queued"/>, in file order.</summary>
        public static List<int> ToAppend(List<RouteEntry> queued, List<RouteEntry> restored)
        {
            List<int> result = new List<int>();
            if (restored == null) return result;
            bool[] used = new bool[queued == null ? 0 : queued.Count];
            for (int r = 0; r < restored.Count; r++)
            {
                int match = -1;
                for (int q = 0; q < used.Length && match < 0; q++)
                    if (!used[q] && Same(queued[q], restored[r])) match = q;
                if (match >= 0) used[match] = true;
                else result.Add(r);
            }
            return result;
        }

        /// <summary>Same name (ordinal), same kind, and within SameSpot of each other on the map.</summary>
        public static bool Same(RouteEntry a, RouteEntry b)
        {
            if (a.Borrowed != b.Borrowed) return false;
            if (!string.Equals(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal)) return false;
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz <= SameSpot * SameSpot;
        }
    }
}
