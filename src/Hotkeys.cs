using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Reads the configurable keys (ToggleWindowKey, SkipWaypointKey, MapModifierKey) so that a key Valheim
    /// cannot read does nothing, instead of breaking whatever runs after it.
    ///
    /// Valheim's ZInput accepts every KeyCode up to JoystickButton19 except Mouse5 and Mouse6
    /// (ZInput.IsKeyCodeValid; the rest silently never fire) and then looks a keyboard key up in its own
    /// KeyCode-to-Key table. Thirty KeyCodes that BepInEx offers as valid settings are missing from that table on
    /// 1.0.16 - Clear, Exclaim, DoubleQuote, Hash, Dollar, Percent, Ampersand, LeftParen, RightParen, Asterisk,
    /// Plus, Colon, Less, Greater, Question, At, Caret, Underscore, LeftCurlyBracket, Pipe, RightCurlyBracket,
    /// Tilde, F13, F14, F15, Help, SysReq, Break, WheelUp and WheelDown - so the lookup yields Key.None and
    /// Keyboard.current[Key.None] throws ArgumentOutOfRangeException on every read. In 1.1.1's Plugin.Update that
    /// throw skipped the waypoint tick on every frame in which no chat, console or text field had the keyboard (the
    /// saved route did not load; arrival, marker repair and saving stopped); in the map patches it logged a stack
    /// trace on every click. preflight.ps1 works the same 30 out from the game and reports a change.
    ///
    /// The first failed read of a key is caught here: the key is remembered as unreadable, one warning naming
    /// the setting is logged, and from then on that key reads as "not pressed" without asking the game - the
    /// same as an unbound key. Changing any key setting forgets what was remembered, so every key is tried
    /// again (Plugin.BindConfig).
    /// </summary>
    internal static class Hotkeys
    {
        // KeyCodes whose read threw. Kept as ints: List<int>.Contains compares without boxing, which a
        // List<KeyCode> is not guaranteed to do on Mono - and this is checked on every read, every frame.
        private static readonly List<int> _unreadable = new List<int>();

        /// <summary>True on the frame the setting's key goes down (ZInput.GetKeyDown).</summary>
        public static bool Pressed(ConfigEntry<KeyCode> setting)
        {
            return Read(setting, false);
        }

        /// <summary>True while the setting's key is held (ZInput.GetKey).</summary>
        public static bool Held(ConfigEntry<KeyCode> setting)
        {
            return Read(setting, true);
        }

        /// <summary>Tries every key again, and warns again about one that still cannot be read.</summary>
        public static void Forget()
        {
            _unreadable.Clear();
        }

        private static bool Read(ConfigEntry<KeyCode> setting, bool held)
        {
            KeyCode key = setting.Value;
            if (key == KeyCode.None) return false;
            if (_unreadable.Count > 0 && _unreadable.Contains((int)key)) return false;

            try
            {
                return held ? ZInput.GetKey(key, false) : ZInput.GetKeyDown(key, false);
            }
            catch (Exception e)
            {
                _unreadable.Add((int)key);
                Plugin.Log.LogWarning(setting.Definition.Key + " = " + key + ": Valheim cannot read this key ("
                    + e.GetType().Name + "), so it will do nothing. Choose another key for "
                    + setting.Definition.Key + ".");
                return false;
            }
        }
    }
}
