using System;
using System.Collections.Generic;

namespace Waypointer
{
    /// <summary>
    /// What RouteMerge compares of a waypoint, without Unity types: its place on the map (x = east/west, z =
    /// north/south), its kind (Borrowed: it follows a pin of the player's) and its name.
    /// </summary>
    internal struct RouteEntry
    {
        public float X, Z;
        public bool Borrowed;
        public string Name;
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
