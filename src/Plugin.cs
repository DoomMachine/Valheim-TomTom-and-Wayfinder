using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Entry point shared by both editions (see Edition.cs): TomTom-style waypoints for Valheim, with
    /// temporary map markers and an on-screen arrow that clear themselves the moment you arrive.
    ///
    /// The two editions declare each other incompatible. BepInEx then loads exactly one of them when
    /// both are installed and logs "Could not load [...] because it is incompatible with ..." for the
    /// other - they would otherwise both patch the same methods and both draw an arrow.
    ///
    /// The same plugin also runs on a dedicated server (valheim_server), where it has no window, arrow or map and
    /// applies only its server side: answering players' Find (FindServer) under the server's WhoMayFind. The host of
    /// a game started with Start Server runs both sides in one game.
    /// </summary>
    [BepInPlugin(Edition.GUID, Edition.Name, Edition.Version)]
    [BepInProcess("valheim.exe")]
    [BepInProcess("valheim_server.exe")]
    [BepInIncompatibility(Edition.OtherGUID)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = Edition.GUID;
        public const string NAME = Edition.Name;
        public const string VERSION = Edition.Version;

        public static ManualLogSource Log;

        /// <summary>Running in a dedicated server: only the server side is set up.</summary>
        public static bool Dedicated;

        private Harmony _harmony;

        // ---- keys
        public static ConfigEntry<KeyCode> ToggleWindowKey;
        public static ConfigEntry<KeyCode> MapModifierKey;
        public static ConfigEntry<KeyCode> SkipWaypointKey;

        // ---- behaviour
        public static ConfigEntry<float> ArrivalRadius;
        public static ConfigEntry<bool> Use3DDistance;
#if !WAYFINDER
        public static ConfigEntry<bool> RawValheimOrder;
#endif
        public static ConfigEntry<bool> PersistWaypoints;

        // ---- markers
        public static ConfigEntry<Minimap.PinType> PinTypeSetting;
        public static ConfigEntry<string> PinLabel;

        // ---- arrow
        public static ConfigEntry<bool> ShowArrow;
        public static ConfigEntry<float> ArrowSize;
        public static ConfigEntry<float> ArrowScreenX;
        public static ConfigEntry<float> ArrowScreenY;
        public static ConfigEntry<float> ArrowOpacity;
        public static ConfigEntry<bool> ShowDistance;
        public static ConfigEntry<bool> ShowWaypointName;
        public static ConfigEntry<bool> ShowEta;
        public static ConfigEntry<bool> HideArrowWhenMapOpen;
        public static ConfigEntry<string> ArrowColorGood;
        public static ConfigEntry<string> ArrowColorMiddle;
        public static ConfigEntry<string> ArrowColorBad;

        // ---- search
        public static ConfigEntry<float> SearchRange;
        public static ConfigEntry<int> MaxSearchWaypoints;
#if !WAYFINDER
        public static ConfigEntry<bool> SkipCheckedChests;
#endif

        // ---- server (a dedicated server, or the host of a Start Server game)
        public static ConfigEntry<FindPolicy> WhoMayFind;

        // ---- logging: the plugin's own log file, on a player's game and on a dedicated server (Wayfinder: no VerboseLog)
        internal static ConfigEntry<bool> ErrorLog;
#if !WAYFINDER
        internal static ConfigEntry<bool> VerboseLog;
#endif

        private void Awake()
        {
            Log = Logger;
            Dedicated = IsDedicatedServerProcess();

            // The plugin's own log file first, so that whatever goes wrong below is in it. LogFile never throws.
            Exception logBindError = null;
            try
            {
                BindLogConfig();
            }
            catch (Exception e)
            {
                logBindError = e;
            }
            LogFile.Open(LogDetailSetting(), HeaderFacts());
            LogFile.EmitParkedFailure();   // an open that failed says so now: Update never runs if Awake stops below
            if (logBindError != null)
                Log.LogError("The [7 - Logging] settings failed to bind, so the log file keeps warnings and errors: " + logBindError);

            // A plugin that throws in Awake is dropped by BepInEx, so config and reflection setup are
            // guarded separately from patching: a failure in one should not silently remove the rest.
            try
            {
                BindServerConfig();
                if (!Dedicated) BindConfig();
            }
            catch (Exception e)
            {
                Log.LogError("Configuration failed to bind: " + e);
                // Every feature reads config, so there is nothing safe to do without it. Disabling the
                // component stops Update and OnGUI, which would otherwise hit the unbound entries every frame.
                enabled = false;
                return;
            }

            if (!Dedicated)
            {
                try
                {
                    MinimapAccess.Init();
                }
                catch (Exception e)
                {
                    Log.LogError("Minimap reflection setup failed, map features will be limited: " + e);
                }
            }

            _harmony = new Harmony(GUID);
            ApplyPatches();

            if (Dedicated)
                Log.LogInfo(NAME + " " + VERSION + " by " + Edition.Author + " loaded on a dedicated server: it answers "
                            + "players' Find (WhoMayFind = " + WhoMayFind.Value + ").");
            else
                Log.LogInfo(NAME + " " + VERSION + " by " + Edition.Author + " loaded. Press "
                            + ToggleWindowKey.Value + " to open the waypoint window.");
            TraceSettings();
        }

        /// <summary>
        /// Whether this is Valheim's dedicated server (valheim_server.exe on Windows, valheim_server.x86_64 on Linux:
        /// BepInEx's process name drops the extension either way).
        /// </summary>
        private static bool IsDedicatedServerProcess()
        {
            return string.Equals(Paths.ProcessName, "valheim_server", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies each patch class on its own rather than through a single PatchAll.
        ///
        /// PatchAll aborts on the first target it cannot resolve, which would take every other patch
        /// down with it - so one renamed method in a future Valheim update would disable the whole
        /// plugin. Patched individually, a broken patch costs only its own feature and says so in the
        /// log, and the mod keeps working otherwise.
        /// </summary>
        private void ApplyPatches()
        {
            int applied = 0;
            Type[] serverPatches = new Type[]
            {
                typeof(Game_RPC_DiscoverClosestLocation_Patch), // WhoMayFind for Vegvisir-style requests from plugins without the helper
                typeof(ZRoutedRpc_RPC_RoutedRPC_Patch)          // which connection a player's call came on (for WhoMayFind and answers)
            };
            Type[] patchClasses = Dedicated ? serverPatches : new Type[]
            {
                typeof(TextInput_IsVisible_Patch),          // cursor release + input blocking
                typeof(Chat_HasFocus_Patch),                // inventory key, camera zoom, gamepad hotbar
                typeof(ZInput_GetMouseScrollWheel_Patch),   // wheel does not zoom the camera behind the window
                typeof(PlayerController_TakeInput_Patch),   // movement blocking
                typeof(Minimap_OnMapLeftClick_Patch),       // place / promote / clear from the map
                typeof(Minimap_OnMapDblClick_Patch),        // no vanilla pin from a double click on the window / with the modifier
                typeof(Minimap_OnMapLeftDown_Patch),        // no map drag from a press on the window
                typeof(UIInputHandler_OnPointerClick_Patch),// no ping or pin delete from a click on the window
                typeof(Minimap_RemovePin_Patch),            // vanilla delete: right click, long press, gamepad
                typeof(Terminal_InitTerminal_Patch),        // console command
                typeof(Game_RPC_DiscoverLocationResponse_Patch), // location search: the server's answers never become pins
                typeof(Game_RPC_DiscoverClosestLocation_Patch),  // as the host: WhoMayFind for requests from plugins without the helper
                typeof(ZRoutedRpc_RPC_RoutedRPC_Patch)           // as the host: which connection a player's call came on
            };

            for (int i = 0; i < patchClasses.Length; i++)
            {
                try
                {
                    _harmony.PatchAll(patchClasses[i]);
                    applied++;
                    // WhoMayFind can tell callers apart only through this patch (FindProtocol.CallerMayFind).
                    if (patchClasses[i] == typeof(ZRoutedRpc_RPC_RoutedRPC_Patch)) RoutedCallContext.Available = true;
                }
                catch (Exception e)
                {
                    Log.LogError("Could not apply patch " + patchClasses[i].Name
                                 + " - that feature will be unavailable. " + e.Message);
                }
            }

            Log.LogInfo(string.Format("Applied {0} of {1} patches.", applied, patchClasses.Length));
        }

        private void BindLogConfig()
        {
#if WAYFINDER
            ErrorLog = Config.Bind("7 - Logging", "ErrorLog", true,
                "Writes Wayfinder's own log file, BepInEx\\Wayfinder.log beside LogOutput.log (the last game's is kept as "
                + "Wayfinder-prev.log): Wayfinder's warnings and errors, and any game error whose stack trace runs through "
                + "Wayfinder's code, with that trace - the file to send with a bug report. Numbers in it that could be a "
                + "position are written as #. LogOutput.log is written as before. Off: no file. A change in game "
                + "(ConfigurationManager) takes effect at once. To edit the .cfg file instead, quit the game or stop the "
                + "server first: an edit made while it runs is not read, and the plugin rewrites the file, with the old "
                + "value, whenever one of its settings changes.");
            ErrorLog.SettingChanged += OnLogSettingChanged;
#else
            ErrorLog = Config.Bind("7 - Logging", "ErrorLog", true,
                "Writes TomTom's own log file, BepInEx\\TomTom.log beside LogOutput.log (the last game's is kept as "
                + "TomTom-prev.log): TomTom's warnings and errors, and any game error whose stack trace runs through "
                + "TomTom's code, with that trace - the file to send with a bug report. LogOutput.log is written as "
                + "before. Off: no file, unless VerboseLog is on. A change in game (ConfigurationManager) takes effect "
                + "at once. To edit the .cfg file instead, quit the game or stop the server first: an edit made while it "
                + "runs is not read, and the plugin rewrites the file, with the old value, whenever one of its settings "
                + "changes.");
            ErrorLog.SettingChanged += OnLogSettingChanged;
            VerboseLog = Config.Bind("7 - Logging", "VerboseLog", false,
                "Also writes every line TomTom logs to TomTom.log, and a line for each event: map clicks and deletes, "
                + "waypoints added (with their positions), reached and removed, the route read and saved, markers made, "
                + "lost and found again, the arrow hidden or shown and why, deaths, the map and the window, messages on "
                + "screen, settings changed, the stages of a Find and, on a server or as the host, other players' Find "
                + "requests (each player by a number). The new event lines go to TomTom.log only; the map-click, route "
                + "and arrival lines TomTom already writes stay in LogOutput.log as well. Turn it on to watch a problem as "
                + "it happens, then off again. The file then holds positions, pin names (yours, pins shared with you, and "
                + "other players' markers near an Alt-click) and, on a server or as the host, the names of players refused "
                + "Find: read it before you share it. A change in game (ConfigurationManager) takes effect at once. To "
                + "edit the .cfg file instead, quit the game or stop the server first: an edit made while it runs is not "
                + "read, and the plugin rewrites the file, with the old value, whenever one of its settings changes.");
            VerboseLog.SettingChanged += OnLogSettingChanged;
#endif
        }

        private void OnLogSettingChanged(object sender, EventArgs e)
        {
            LogFile.SetLevel(LogDetailSetting());   // never throws
            TraceChangedSettings();
        }

        private static LogDetail LogDetailSetting()
        {
            bool errors = ErrorLog == null || ErrorLog.Value;
#if WAYFINDER
            return LogRules.Detail(errors, false);
#else
            return LogRules.Detail(errors, VerboseLog != null && VerboseLog.Value);
#endif
        }

        /// <summary>The first line of the log file: versions and process, gathered here so the log file itself calls no
        /// game or Unity code while it holds its lock. Never a path, a name, an id or an address.</summary>
        private static string HeaderFacts()
        {
            string version = VERSION, game = "?", unity = "?", bepinex = "?";
            try
            {
                object[] a = typeof(Plugin).Assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (a.Length > 0) version = ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion;
            }
            catch (Exception) { }
            try { game = global::Version.GetVersionString(false); } catch (Exception) { }
            try { unity = Application.unityVersion; } catch (Exception) { }
            try { bepinex = typeof(Paths).Assembly.GetName().Version.ToString(); } catch (Exception) { }
            return NAME + " " + version + " log - Valheim " + game + " - Unity " + unity + " - BepInEx " + bepinex
                   + (Dedicated ? " - dedicated server" : " - player's game");
        }

        /// <summary>VerboseLog: the settings that differ from their defaults, then every change (TomTom; Wayfinder's
        /// compiler drops the call, so it never subscribes).</summary>
        [System.Diagnostics.Conditional("TOMTOM")]
        private void TraceSettings()
        {
            try
            {
                Config.SettingChanged += OnAnySettingChanged;
                TraceChangedSettings();
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private void TraceChangedSettings()
        {
            if (!Diag.On) return;
            try
            {
                foreach (ConfigDefinition d in Config.Keys)
                {
                    ConfigEntryBase s = Config[d];
                    if (s != null && !Equals(s.BoxedValue, s.DefaultValue))
                        Diag.Trace("setting: [" + d.Section + "] " + d.Key + " = " + s.BoxedValue + " (not the default)");
                }
            }
            catch (Exception) { }
        }

        // A slider dragged in ConfigurationManager changes its setting every frame: the first change is written, then that
        // setting's changes within SettledTicks of the last one written are held, and the last of them is written when
        // another setting changes, when the setting changes again later, or when the plugin closes.
        private const long SettledTicks = TimeSpan.TicksPerSecond / 2;
        private static readonly object SettingTraceLock = new object();
        private static ConfigEntryBase _tracedSetting, _heldSetting;
        private static long _tracedSettingAt;
        private static int _heldChanges;

        private static void OnAnySettingChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                if (!Diag.On || e == null || e.ChangedSetting == null) return;
                ConfigEntryBase s = e.ChangedSetting;
                string held = null, now = null;
                lock (SettingTraceLock)                    // any thread: a configuration reload runs this on its own
                {
                    long t = DateTime.UtcNow.Ticks;
                    if (ReferenceEquals(s, _tracedSetting) && t - _tracedSettingAt < SettledTicks)
                    {
                        _heldSetting = s;
                        _heldChanges++;
                        return;
                    }
                    if (_heldSetting != null && !ReferenceEquals(_heldSetting, s)) held = SettingText(_heldSetting, _heldChanges);
                    now = SettingText(s, ReferenceEquals(_heldSetting, s) ? _heldChanges : 0);
                    _heldSetting = null;
                    _heldChanges = 0;
                    _tracedSetting = s;
                    _tracedSettingAt = t;
                }
                if (held != null) Diag.Trace(held);
                Diag.Trace(now);
            }
            catch (Exception) { }
        }

        /// <summary>Writes a setting change still held back (OnDestroy, before the log file closes).</summary>
        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceHeldSetting()
        {
            if (!Diag.On) return;
            try
            {
                string held = null;
                lock (SettingTraceLock)
                {
                    if (_heldSetting != null) held = SettingText(_heldSetting, _heldChanges);
                    _heldSetting = null;
                    _heldChanges = 0;
                }
                if (held != null) Diag.Trace(held);
            }
            catch (Exception) { }
        }

        private static string SettingText(ConfigEntryBase s, int heldChanges)
        {
            ConfigDefinition d = s.Definition;
            return "setting changed: [" + d.Section + "] " + d.Key + " = " + s.BoxedValue
                   + (heldChanges > 0 ? " (after " + heldChanges.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + " quicker changes)" : "");
        }

        /// <summary>One warning for a fault that would otherwise go unnoticed or repeat; later ones are not logged.</summary>
        internal static void WarnOnce(ref bool warned, string what, Exception e, string effect)
        {
            if (warned) return;
            warned = true;
            try
            {
                Log.LogWarning(what + (e != null ? " (" + e.GetType().Name + ": " + e.Message + ")" : "") + ": " + effect
                               + ". Later failures are not logged.");
            }
            catch (Exception) { }
        }

        /// <summary>The settings a server uses; bound on players too, since the host of a Start Server game is one.</summary>
        private void BindServerConfig()
        {
            WhoMayFind = Config.Bind("6 - Server", "WhoMayFind", FindPolicy.Everyone,
                "Only used when this game is the server: a dedicated server, or the host of a game started with Start "
                + "Server. Which of the players who join may use Find: Everyone, AdminsOnly (players in the server's "
                + "adminlist.txt) or Nobody. The host's own Find is never limited. A refused player whose plugin is "
                + "older than 1.3.0 gets no answer from the server: after 15 to 20 seconds their Find keeps only what "
                + "their own game has been sent since they joined (loose Rocks near where they have been). This is a rule "
                + "for players who use this plugin, not a lock: any game can ask a server for locations the way a "
                + "Vegvisir does.");
            WhoMayFind.SettingChanged += OnPolicyChanged;
        }

        private static void OnPolicyChanged(object sender, EventArgs e)
        {
            FindServer.PolicyChanged();
        }

        private void BindConfig()
        {
            // Deliberately recommends no particular key: the mod cannot know what else is installed.
            ToggleWindowKey = Config.Bind("1 - Keys", "ToggleWindowKey", KeyCode.F11,
                "Opens and closes the " + NAME + " window. F11 is also Valheim's own screenshot key, so "
                + "with this default each press also saves a screenshot to the game's screenshots folder. "
                + "Vanilla Valheim also uses F2 (connect panel), F5 (console) and F9 (gamepad layout), "
                + "plus Ctrl+F1 (mouse capture) and Ctrl+F3 (hide HUD). Other mods can hold keys of their "
                + "own, so choose one that nothing you run already uses.");
            MapModifierKey = Config.Bind("1 - Keys", "MapModifierKey", KeyCode.LeftAlt,
                "Hold this while left-clicking the world map to add, promote or delete a waypoint. " +
                "Clicking without it keeps Valheim's normal pin behaviour.");
            SkipWaypointKey = Config.Bind("1 - Keys", "SkipWaypointKey", KeyCode.None,
                "Optional key that skips the active waypoint and moves to the next one in the list.");

            // A key Valheim cannot read is ignored after one warning (Hotkeys); a new choice is tried afresh.
            ToggleWindowKey.SettingChanged += OnKeySettingChanged;
            MapModifierKey.SettingChanged += OnKeySettingChanged;
            SkipWaypointKey.SettingChanged += OnKeySettingChanged;

            ArrivalRadius = Config.Bind("2 - Behaviour", "ArrivalRadius", 10f,
                new ConfigDescription(
                    "How close you must get, in metres, before a waypoint counts as reached.",
                    new AcceptableValueRange<float>(1f, 100f)));
            Use3DDistance = Config.Bind("2 - Behaviour", "Use3DDistance", false,
#if WAYFINDER
                "Off by default: arrival is measured on the horizontal plane. Turn on to include altitude: a waypoint " +
                "with a height of its own (your own position, an object Find found) uses it; one without (a map click on " +
                "open ground, a pin placed on the map), and a place Find found, uses the ground under it once that ground " +
                "is loaded near you. Over water it stays horizontal.");
#else
                "Off by default: arrival is measured on the horizontal plane. Turn on to include altitude: a waypoint " +
                "with a height of its own (typed, your own position, an object Find found) uses it; one without (a map " +
                "click on open ground, two typed numbers, a pin placed on the map), and a place Find found, uses the ground under it " +
                "once that ground is loaded near you. Over water it stays horizontal.");
#endif
#if !WAYFINDER
            RawValheimOrder = Config.Bind("2 - Behaviour", "InputIsRawValheimXYZ", false,
                "Off (default): you type X, Y and an optional third value that means ELEVATION. " +
                "On: the three values are read as Valheim's own x, y, z where y is altitude.");
#endif
            PersistWaypoints = Config.Bind("2 - Behaviour", "PersistWaypoints", true,
                "Remember the waypoint queue per world between sessions.");

            PinTypeSetting = Config.Bind("3 - Markers", "PinType", Minimap.PinType.Icon3,
                "Which map icon the temporary waypoint markers use.");
            PinLabel = Config.Bind("3 - Markers", "PinLabel", "Waypoint",
                "Label shown on the temporary map markers.");

            ShowArrow = Config.Bind("4 - Arrow", "ShowArrow", true,
                "Show the on-screen direction arrow.");
            ArrowSize = Config.Bind("4 - Arrow", "ArrowSize", 110f,
                new ConfigDescription("Arrow size in pixels.", new AcceptableValueRange<float>(40f, 400f)));
            ArrowScreenX = Config.Bind("4 - Arrow", "ArrowScreenX", 0.5f,
                new ConfigDescription("Horizontal position, 0 = left edge, 1 = right edge.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ArrowScreenY = Config.Bind("4 - Arrow", "ArrowScreenY", 0.22f,
                new ConfigDescription("Vertical position, 0 = top, 1 = bottom.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ArrowOpacity = Config.Bind("4 - Arrow", "ArrowOpacity", 0.9f,
                new ConfigDescription("Arrow opacity.", new AcceptableValueRange<float>(0.1f, 1f)));
            ShowDistance = Config.Bind("4 - Arrow", "ShowDistance", true, "Show the distance under the arrow.");
            ShowWaypointName = Config.Bind("4 - Arrow", "ShowWaypointName", true, "Show the waypoint name under the arrow.");
            ShowEta = Config.Bind("4 - Arrow", "ShowTimeToArrival", true,
                "Show an estimated time of arrival based on how fast you are closing in.");
            HideArrowWhenMapOpen = Config.Bind("4 - Arrow", "HideArrowWhenMapOpen", true,
                "Hide the arrow while the full world map is open.");

            ArrowColorGood = Config.Bind("4 - Arrow", "ColorFacingTarget", "#4CE04C",
                "Arrow colour when you are facing straight at the waypoint.");
            ArrowColorMiddle = Config.Bind("4 - Arrow", "ColorSideways", "#E8D24A",
                "Arrow colour when the waypoint is off to your side.");
            ArrowColorBad = Config.Bind("4 - Arrow", "ColorFacingAway", "#E05050",
                "Arrow colour when you are facing away from the waypoint.");

            SearchRange = Config.Bind("5 - Search", "SearchRange", 1000f,
                new ConfigDescription(
                    "How far from you, in metres, the window's Find looks. The window's slider changes it too.",
                    new AcceptableValueRange<float>(100f, 10000f)));
            MaxSearchWaypoints = Config.Bind("5 - Search", "MaxSearchWaypoints", 50,
                new ConfigDescription(
                    "The most waypoints one Find queues. The route is planned over the nearest places found, and its "
                    + "first part is kept.",
                    new AcceptableValueRange<int>(1, 200)));
#if !WAYFINDER
            SkipCheckedChests = Config.Bind("5 - Search", "SkipCheckedChests", true,
                "When looking for a chest item, leave out places whose chests the game has already filled and none of "
                + "which holds the item any more (emptied, or it never had it); when looking for a Bee Nest, leave out "
                + "places whose area the game has generated without a nest left in it (none grew there, or it was "
                + "destroyed). The game fills a chest, and rolls whether a nest grows, when an area is first generated. "
                + "Works as the host (or in single player), and when you join a server that runs this plugin too (for "
                + "a Bee Nest, 1.4.0 or later; for the Wooden Greatsword, 1.5.0 or later), which checks for you; when you join any other server it does nothing, "
                + "since your game knows only what it has seen since you joined. Reading chests runs a little each frame, so it makes a search take longer rather than making "
                + "the game stutter; turn it off if a search feels slow.");
#endif

            // Deliberately no SettingChanged handlers for ArrowSize or ArrowOpacity: the arrow texture is
            // rasterised once at a fixed resolution and then scaled and tinted at draw time, so neither
            // setting affects its pixels. Rebuilding it on every change would stall the game for the
            // whole of a slider drag.
        }

        private static void OnKeySettingChanged(object sender, EventArgs e)
        {
            Hotkeys.Forget();
        }

        private void Update()
        {
            LogFile.EmitParkedFailure();   // first, outside the tries: it catches everything itself
            if (Dedicated)
            {
                TickServer();
                return;
            }

            // This runs every frame inside the game's own update loop, so nothing here may escape:
            // an unhandled exception would surface as a Unity error every single frame. The keys and the
            // waypoint tick are guarded separately, so that nothing going wrong with a key can stop arrival
            // checks, markers and saving (preflight checks that the tick's try block reads no key).
            try
            {
                bool consoleVisible = Console.IsVisible();

                // Never steal keys while the player is typing into chat, the console or a text field.
                if (!IsTypingElsewhere())
                {
                    // Escape and the gamepad's B close the window: Menu.Update cannot open the pause menu
                    // while TextInput.IsVisible is true, so without this Escape does nothing at all. Not
                    // while the console is (or was, last frame) open - Console.Update closes itself on the
                    // same press, and after "waypoint window" that press is meant for the console only.
                    if (WaypointWindow.IsOpen && !consoleVisible && !_consoleWasVisible
                        && (ZInput.GetKeyDown(KeyCode.Escape, false) || ZInput.GetButtonDown("JoyButtonB")))
                    {
                        // Consume B so a map underneath does not close with it (InventoryGui does the same),
                        // and pause controls briefly as Menu.Hide does, since B is also Jump on a gamepad.
                        ZInput.ResetButtonStatus("JoyButtonB");
                        if (ZInput.IsGamepadActive()) PlayerController.SetTakeInputDelay(0.1f);
                        WaypointWindow.Close();
                    }
                    // Not opened over the pause menu: Menu.Update closes it on the same Escape that closes our
                    // window, so one press would close both and unpause. Closing is always allowed.
                    else if (IsHotkeyAvailable(ToggleWindowKey.Value) && Hotkeys.Pressed(ToggleWindowKey)
                             && (WaypointWindow.IsOpen || !Menu.IsVisible()))
                        WaypointWindow.Toggle();

                    if (IsHotkeyAvailable(SkipWaypointKey.Value) && Hotkeys.Pressed(SkipWaypointKey)
                        && WaypointManager.HasActive && WaypointManager.QueueBelongsToCurrentWorld)
                    {
                        WaypointManager.SkipActive();
                    }
                }

                _consoleWasVisible = consoleVisible;
            }
            catch (Exception e)
            {
                LogThrottled(ref _keyErrors, "Waypoint key handling failed", e);
            }

            try
            {
                SearchBudget.StartFrame();
                WaypointManager.Tick();
                LocationSearch.Tick();
                WaypointWindow.UpdateCursorState();
            }
            catch (Exception e)
            {
                LogThrottled(ref _updateErrors, "Waypoint update failed", e);
            }

            TickServer();
        }

        /// <summary>
        /// This plugin's messages with the server (FindLink) and, when this game is the server, the searches it runs
        /// for players (FindServer). Guarded on its own, so a fault here cannot stop the waypoint tick.
        /// </summary>
        private void TickServer()
        {
            try
            {
                if (Dedicated) SearchBudget.StartFrame();
                FindLink.Tick();
                FindServer.Tick();
            }
            catch (Exception e)
            {
                LogThrottled(ref _serverErrors, "Server-side Find failed", e);
            }
        }

        /// <summary>
        /// A hotkey is available unless it is unbound, or it is a key that types or edits text while
        /// one of our own text boxes has the keyboard. That way an F-key still closes the window while
        /// it is being typed in, but a hotkey rebound to a letter never fires mid-word.
        /// </summary>
        private static bool IsHotkeyAvailable(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            if (!WaypointWindow.TextFieldHasFocus) return true;
            return !IsTextEditingKey(key);
        }

        private static bool IsTextEditingKey(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z) return true;
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return true;
            if (key >= KeyCode.Keypad0 && key <= KeyCode.KeypadEquals) return true;   // includes KeypadEnter

            switch (key)
            {
                case KeyCode.Space:
                case KeyCode.Comma:
                case KeyCode.Period:
                case KeyCode.Minus:
                case KeyCode.Plus:
                case KeyCode.Equals:
                case KeyCode.Semicolon:
                case KeyCode.Colon:
                case KeyCode.Slash:
                case KeyCode.Backslash:
                case KeyCode.Quote:
                case KeyCode.BackQuote:
                case KeyCode.LeftBracket:
                case KeyCode.RightBracket:
                case KeyCode.Backspace:
                case KeyCode.Delete:
                case KeyCode.Return:
                case KeyCode.Tab:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True when a game text field owns the keyboard, so our hotkeys must stand down.</summary>
        public static bool IsTypingElsewhere()
        {
            // While our own window is open we are the thing forcing TextInput.IsVisible to report true,
            // so consulting these gates would lock out the very key that closes the window again.
            if (WaypointWindow.IsOpen) return false;

            try
            {
                if (TextInput.IsVisible()) return true;
                if (Chat.instance != null && Chat.instance.HasFocus()) return true;
                if (Minimap.InTextInput()) return true;
                if (Console.IsVisible()) return true;
            }
            catch { }
            return false;
        }

        private void OnGUI()
        {
            if (Dedicated) return;
            try
            {
                ArrowHud.Draw();
                WaypointWindow.Draw();
            }
            catch (Exception e)
            {
                LogThrottled(ref _guiErrors, "GUI draw failed", e);
            }
        }

        // Console visibility as sampled by the previous Update. Unity does not order our Update against
        // Console.Update, so on the Escape frame the console may already have closed itself.
        private static bool _consoleWasVisible;

        private static int _keyErrors;
        private static int _updateErrors;
        private static int _guiErrors;
        private static int _serverErrors;
        private const int MaxLoggedErrors = 3;

        /// <summary>
        /// Update and OnGUI run every frame, so an error there would otherwise write thousands of log
        /// lines a minute and cost more performance than the fault itself. Log a few, then fall silent.
        /// </summary>
        private static void LogThrottled(ref int counter, string context, Exception e)
        {
            counter++;
            if (counter <= MaxLoggedErrors)
                Log.LogError(context + ": " + e);
            else if (counter == MaxLoggedErrors + 1)
                Log.LogError(context + ": repeating, further occurrences will not be logged.");
        }

        private void OnDestroy()
        {
            if (Dedicated)
            {
                try
                {
                    if (_harmony != null) _harmony.UnpatchSelf();
                }
                finally
                {
                    LogFile.Close();
                    LogFile.EmitParkedFailure();
                }
                return;
            }
            try
            {
                WaypointWindow.Close();
                LocationSearch.Cancel();
                WaypointManager.SaveNow();   // last chance: ignores any retry delay
                WaypointManager.ReleaseAllPins();
                ArrowHud.InvalidateTextures();
                if (_harmony != null) _harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.LogWarning("Shutdown cleanup failed: " + e.Message);
            }
            TraceHeldSetting();
            LogFile.Close();
            LogFile.EmitParkedFailure();
        }

        private void OnApplicationQuit()
        {
            LogFile.NoteQuit();
        }
    }
}
