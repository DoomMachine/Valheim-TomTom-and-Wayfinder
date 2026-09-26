namespace Waypointer
{
    /// <summary>
    /// Everything that distinguishes the two plugins built from this one code base.
    ///
    ///   TomTom    - the full feature set, including typing or pasting coordinates.
    ///   Wayfinder - the immersion edition: waypoints can only come from the world map (a click, or
    ///               one of your own markers). There is deliberately no way to enter coordinates, so a
    ///               location cannot be looked up outside the game and navigated to.
    ///
    /// The edition is chosen at compile time by the WAYFINDER symbol (set in Wayfinder.csproj), not by
    /// a runtime switch: in the Wayfinder build the coordinate parser and every path that feeds it are
    /// not merely disabled, they are absent from the assembly. preflight.ps1 checks exactly that.
    /// </summary>
    internal static class Edition
    {
#if WAYFINDER
        public const string Name = "Wayfinder";
        public const string GUID = "DoomMachine.Wayfinder";
        public const string OtherGUID = "DoomMachine.TomTom";
        public const int WindowId = 0x57464E44;   // "WFND"
#else
        public const string Name = "TomTom";
        public const string GUID = "DoomMachine.TomTom";
        public const string OtherGUID = "DoomMachine.Wayfinder";
        public const int WindowId = 0x544F4D54;   // "TOMT"
#endif

        public const string Author = "DoomMachine";
        public const string Version = "1.2.0";
    }
}
