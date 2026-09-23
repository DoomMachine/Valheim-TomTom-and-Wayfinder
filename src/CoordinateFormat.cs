// TomTom only. Wayfinder shows no numeric world coordinates anywhere, because a readout turns its
// map click into coordinate entry: Alt-click works anywhere on the map, fog included, so a player who
// looked a location up could click, read "Waypoint added at 3350, -1190", adjust and click again until
// the numbers matched. Excluded from Wayfinder.csproj and compiled out here as well, so any call site
// left unguarded in the Wayfinder build is a compile error rather than a quiet leak.
#if !WAYFINDER
using System.Globalization;
using UnityEngine;

namespace Waypointer
{
    /// <summary>Turns a world position into text for display.</summary>
    public static class CoordinateFormat
    {
        /// <summary>
        /// Formats a world position in the convention the player types, so what is shown matches what
        /// they would type back in: X, Y (the two horizontal axes) and, when known, the elevation.
        /// </summary>
        public static string Format(Vector3 world, bool hasElevation)
        {
            if (!hasElevation)
                return string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}", world.x, world.z);

            // TomTom can be told to read raw Valheim x, y, z (y = altitude); display follows suit.
            if (Plugin.RawValheimOrder != null && Plugin.RawValheimOrder.Value)
                return string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", world.x, world.y, world.z);

            return string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", world.x, world.z, world.y);
        }
    }
}
#endif
