using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Reflection bridge to the private members of <see cref="Minimap"/>.
    /// These members are private in the shipped assembly, and HarmonyLib's FieldRef helpers use
    /// ref-returning delegates which the C# 5 compiler cannot express, so plain reflection is used
    /// for fields and a cached open-instance delegate for the hot method.
    /// </summary>
    internal static class MinimapAccess
    {
        private static bool _initialised;
        private static Func<Minimap, Vector3, Vector3> _screenToWorld;
        private static FieldInfo _pinsField;
        private static FieldInfo _visibleIconTypesField;
        private static MethodInfo _pinInteractRadiusGetter;

        internal static void Init()
        {
            if (_initialised) return;
            _initialised = true;

            try
            {
                MethodInfo stw = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new Type[] { typeof(Vector3) });
                if (stw != null)
                    _screenToWorld = AccessTools.MethodDelegate<Func<Minimap, Vector3, Vector3>>(stw);
            }
            catch (Exception e) { Plugin.Log.LogWarning("ScreenToWorldPoint unavailable: " + e.Message); }

            _pinsField = AccessTools.Field(typeof(Minimap), "m_pins");
            if (_pinsField == null) Plugin.Log.LogWarning("Minimap.m_pins not found.");

            _visibleIconTypesField = AccessTools.Field(typeof(Minimap), "m_visibleIconTypes");
            if (_visibleIconTypesField == null) Plugin.Log.LogWarning("Minimap.m_visibleIconTypes not found.");

            PropertyInfo pir = AccessTools.Property(typeof(Minimap), "PinInteractRadius");
            if (pir != null) _pinInteractRadiusGetter = pir.GetGetMethod(true);
        }

        /// <summary>Converts a screen/cursor position into a world position on the map. Returns false if unavailable.</summary>
        internal static bool TryScreenToWorld(Minimap mm, Vector3 screenPos, out Vector3 world)
        {
            world = Vector3.zero;
            if (mm == null || _screenToWorld == null) return false;
            try
            {
                world = _screenToWorld(mm, screenPos);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ScreenToWorldPoint failed: " + e.Message);
                return false;
            }
        }

        private static readonly List<Minimap.PinData> NoPins = new List<Minimap.PinData>();
        private static bool _pinsWarned;

        /// <summary>
        /// The live list of map pins, or null when this game version no longer exposes it (renamed,
        /// retyped or unreadable). Callers that would read "not in the list" as "gone" must stop on null.
        /// </summary>
        internal static List<Minimap.PinData> TryGetPins(Minimap mm)
        {
            if (mm == null) return null;
            try
            {
                List<Minimap.PinData> pins = _pinsField != null ? _pinsField.GetValue(mm) as List<Minimap.PinData> : null;
                if (pins != null) return pins;
            }
            catch (Exception e)
            {
                if (!_pinsWarned)
                {
                    _pinsWarned = true;
                    Plugin.Log.LogWarning("Minimap.m_pins could not be read (" + e.Message + "): waypoint map markers are disabled.");
                }
                return null;
            }
            if (!_pinsWarned)
            {
                _pinsWarned = true;
                Plugin.Log.LogWarning("Minimap.m_pins is missing or no longer a List<PinData>: waypoint map markers are disabled.");
            }
            return null;
        }

        /// <summary>The live list of map pins. Never null - a shared empty list (read only) when unavailable.</summary>
        internal static List<Minimap.PinData> GetPins(Minimap mm)
        {
            List<Minimap.PinData> pins = TryGetPins(mm);
            return pins ?? NoPins;
        }

        /// <summary>
        /// True once the minimap can actually accept a pin.
        ///
        /// Minimap.AddPin indexes m_visibleIconTypes, which is only allocated in Minimap.Start - so
        /// calling it between Awake and Start throws a NullReferenceException. Checking the array
        /// itself is the honest test for "has Start run yet".
        /// </summary>
        internal static bool CanAddPins(Minimap mm)
        {
            if (mm == null) return false;
            if (_visibleIconTypesField == null) return true;   // unknown field: assume the game is fine
            try
            {
                return _visibleIconTypesField.GetValue(mm) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the pin is still present in the minimap's own list (i.e. our reference is not stale).</summary>
        internal static bool PinIsAlive(Minimap mm, Minimap.PinData pin)
        {
            if (pin == null || mm == null) return false;
            return GetPins(mm).Contains(pin);
        }

        /// <summary>Stand-in for PinInteractRadius when its private getter cannot be reached.</summary>
        internal const float FallbackPinInteractRadius = 12f;

        internal static float PinInteractRadius(Minimap mm, float fallback)
        {
            if (mm == null || _pinInteractRadiusGetter == null) return fallback;
            try
            {
                object v = _pinInteractRadiusGetter.Invoke(mm, null);
                if (v is float)
                {
                    float f = (float)v;
                    if (f > 0f) return f;
                }
            }
            catch { /* fall through to the fallback */ }
            return fallback;
        }

        /// <summary>
        /// Nearest pin to a world position, measured on the horizontal plane.
        ///
        /// This walks m_pins directly rather than using Valheim's own GetClosestPin, because that
        /// helper begins its loop with `if (!pin.m_save) continue;` - verified in IL. Our waypoint
        /// markers are deliberately created with save:false so they never end up in the player's saved
        /// map data, which makes them invisible to every vanilla hit test, including the one behind
        /// GetClosestPinToCursor and the right-click delete gesture.
        /// </summary>
        internal static Minimap.PinData GetClosestPinToWorldPos(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, false);
        }

        /// <summary>Nearest pin that one of our waypoints is using, so our own markers win the gesture.</summary>
        internal static Minimap.PinData GetClosestWaypointPin(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, true);
        }

        /// <summary>
        /// Nearest pin a borrowed waypoint may attach itself to: not already used by any waypoint (which
        /// also rules out our own stand-in marker sitting on the same spot) and not a transient pin.
        /// Deliberately NOT filtered on m_save: some vanilla markers a player might follow, such as the
        /// bed spawn point, are save:false too.
        /// </summary>
        internal static Minimap.PinData GetClosestAdoptablePin(Minimap mm, Vector3 world, float radius)
        {
            List<Minimap.PinData> pins = GetPins(mm);
            Minimap.PinData best = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData p = pins[i];
                if (p == null || IsTransient(p.m_type)) continue;
                if (WaypointManager.FindByPin(p) != null) continue;

                float dx = p.m_pos.x - world.x;
                float dz = p.m_pos.z - world.z;
                float sqr = dx * dx + dz * dz;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Pins that come and go on their own: pings, shouts, other players, raid events.</summary>
        private static bool IsTransient(Minimap.PinType type)
        {
            return type == Minimap.PinType.Ping || type == Minimap.PinType.Shout
                || type == Minimap.PinType.Player || type == Minimap.PinType.RandomEvent
                || type == Minimap.PinType.EventArea;
        }

        private static Minimap.PinData FindClosest(Minimap mm, Vector3 world, float radius, bool waypointsOnly)
        {
            List<Minimap.PinData> pins = GetPins(mm);
            Minimap.PinData best = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData p = pins[i];
                if (p == null) continue;
                if (waypointsOnly && WaypointManager.FindByPin(p) == null) continue;

                float dx = p.m_pos.x - world.x;
                float dz = p.m_pos.z - world.z;
                float sqr = dx * dx + dz * dz;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>
        /// Mirrors the housekeeping Valheim does at the start of its own delete gesture, for the case
        /// where we handle that gesture ourselves and skip the original method.
        /// </summary>
        internal static void HidePinTextInput(Minimap mm)
        {
            if (mm == null) return;
            try
            {
                MethodInfo mi = AccessTools.Method(typeof(Minimap), "HidePinTextInput", new Type[] { typeof(bool) });
                if (mi != null) mi.Invoke(mm, new object[] { false });
            }
            catch { /* cosmetic only - never let this break the delete */ }
        }
    }
}
