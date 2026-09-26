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
    /// </summary>
    [BepInPlugin(Edition.GUID, Edition.Name, Edition.Version)]
    [BepInProcess("valheim.exe")]
    [BepInIncompatibility(Edition.OtherGUID)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = Edition.GUID;
        public const string NAME = Edition.Name;
        public const string VERSION = Edition.Version;

        public static ManualLogSource Log;

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

        private void Awake()
        {
            Log = Logger;

            // A plugin that throws in Awake is dropped by BepInEx, so config and reflection setup are
            // guarded separately from patching: a failure in one should not silently remove the rest.
            try
            {
                BindConfig();
            }
            catch (Exception e)
            {
                Log.LogError("Configuration failed to bind: " + e);
                // Every feature reads config, so there is nothing safe to do without it. Disabling the
                // component stops Update and OnGUI, which would otherwise hit the unbound entries every frame.
                enabled = false;
                return;
            }

            try
            {
                MinimapAccess.Init();
            }
            catch (Exception e)
            {
                Log.LogError("Minimap reflection setup failed, map features will be limited: " + e);
            }

            _harmony = new Harmony(GUID);
            ApplyPatches();

            Log.LogInfo(NAME + " " + VERSION + " by " + Edition.Author + " loaded. Press "
                        + ToggleWindowKey.Value + " to open the waypoint window.");
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
            Type[] patchClasses = new Type[]
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
                typeof(Terminal_InitTerminal_Patch)         // console command
            };

            for (int i = 0; i < patchClasses.Length; i++)
            {
                try
                {
                    _harmony.PatchAll(patchClasses[i]);
                    applied++;
                }
                catch (Exception e)
                {
                    Log.LogError("Could not apply patch " + patchClasses[i].Name
                                 + " - that feature will be unavailable. " + e.Message);
                }
            }

            Log.LogInfo(string.Format("Applied {0} of {1} patches.", applied, patchClasses.Length));
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
                "Off by default: arrival is measured on the horizontal plane, which is what you want " +
                "when no elevation was supplied. Turn on to include altitude in the distance.");
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
                WaypointManager.Tick();
                WaypointWindow.UpdateCursorState();
            }
            catch (Exception e)
            {
                LogThrottled(ref _updateErrors, "Waypoint update failed", e);
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
            try
            {
                WaypointWindow.Close();
                WaypointManager.SaveNow();   // last chance: ignores any retry delay
                WaypointManager.ReleaseAllPins();
                ArrowHud.InvalidateTextures();
                if (_harmony != null) _harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                Log.LogWarning("Shutdown cleanup failed: " + e.Message);
            }
        }
    }
}
