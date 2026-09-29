namespace Waypointer
{
    /// <summary>
    /// Which altitude the 3D arrival test (Use3DDistance) measures a waypoint at. Unity-free, so the tests exercise it.
    /// </summary>
    internal static class ArrivalRules
    {
        /// <summary>
        /// A height of the waypoint's own (typed, the player's own position, an object Find found) is used as it is. A
        /// waypoint without one (a map click, two typed numbers, a pin placed on the map), or whose height is only the
        /// world generator's estimate (a place Find found), is measured at the loaded ground under it. Where that ground
        /// is under water, it is measured at the player's own altitude (nobody stands on the bottom); where the ground is
        /// not loaded, a waypoint without a height is too (horizontal, as before), and an estimate keeps its own.
        /// </summary>
        public static float TargetAltitude(bool hasElevation, bool heightIsEstimate, float ownY, bool groundKnown,
            float groundY, float waterLevel, float playerY)
        {
            if (hasElevation && !heightIsEstimate) return ownY;
            bool groundUsable = groundKnown && !float.IsNaN(groundY) && !float.IsInfinity(groundY);
            if (groundUsable && groundY >= waterLevel) return groundY;
            if (groundUsable) return playerY;
            return hasElevation ? ownY : playerY;
        }
    }
}
