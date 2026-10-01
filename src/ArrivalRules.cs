namespace Waypointer
{
    /// <summary>What one arrival test of the active waypoint does (ArrivalRules.Step).</summary>
    internal enum ArrivalStep
    {
        None,
        /// <summary>The player is away from the waypoint for the first time: from now on, coming back reaches it.</summary>
        Arm,
        /// <summary>The armed waypoint is within the arrival radius: reached.</summary>
        Reach,
    }

    /// <summary>
    /// The arrival test, and which altitude the 3D arrival test (Use3DDistance) measures a waypoint at. Unity-free, so
    /// the tests exercise them.
    /// </summary>
    internal static class ArrivalRules
    {
        /// <summary>
        /// One test of the active waypoint. A waypoint is armed once the player is genuinely away from it (beyond the
        /// radius), so one added where the player stands is not reached at once, and an armed one is reached within the
        /// radius. Nothing happens while the player is dead: the body lies where it fell until the respawn, and dying next
        /// to the waypoint being followed is not arriving at it (until 1.5.1 it was, and the waypoint was gone).
        /// </summary>
        public static ArrivalStep Step(bool armed, bool playerDead, float distance, float radius)
        {
            if (playerDead) return ArrivalStep.None;
            if (!armed) return distance > radius ? ArrivalStep.Arm : ArrivalStep.None;
            return distance <= radius ? ArrivalStep.Reach : ArrivalStep.None;
        }

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
