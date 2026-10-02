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
        private static Func<Minimap, Vector3, float, bool, Minimap.PinData> _closestPin;
        private static Func<Minimap, Vector3, bool> _isExplored;
        private static FieldInfo _pinsField;
        private static FieldInfo _visibleIconTypesField;
        private static FieldInfo _sharedMapDataFadeField;
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
                else Plugin.Log.LogWarning("Minimap.ScreenToWorldPoint not found: Alt-clicks on the map do nothing.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ScreenToWorldPoint unavailable: " + e.Message); }

            try
            {
                MethodInfo gcp = AccessTools.Method(typeof(Minimap), "GetClosestPin", new Type[] { typeof(Vector3), typeof(float), typeof(bool) });
                if (gcp != null)
                    _closestPin = AccessTools.MethodDelegate<Func<Minimap, Vector3, float, bool, Minimap.PinData>>(gcp);
                else Plugin.Log.LogWarning("Minimap.GetClosestPin not found: a map delete near a followed pin always removes "
                    + "its waypoint, even where the game would have deleted another pin nearer the pointer.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("GetClosestPin unavailable: " + e.Message); }

            try
            {
                MethodInfo ie = AccessTools.Method(typeof(Minimap), "IsExplored", new Type[] { typeof(Vector3) });
                if (ie != null)
                    _isExplored = AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(ie);
#if WAYFINDER
                else Plugin.Log.LogWarning("Minimap.IsExplored not found: Wayfinder cannot tell explored map from fog, so an "
                    + "Alt-click places no waypoint on open ground (it still follows a pin or removes a waypoint), and Find places none.");
#endif
            }
            catch (Exception e) { Plugin.Log.LogWarning("IsExplored unavailable: " + e.Message); }

            _pinsField = AccessTools.Field(typeof(Minimap), "m_pins");
            if (_pinsField == null) Plugin.Log.LogWarning("Minimap.m_pins not found.");

            _visibleIconTypesField = AccessTools.Field(typeof(Minimap), "m_visibleIconTypes");
            if (_visibleIconTypesField == null) Plugin.Log.LogWarning("Minimap.m_visibleIconTypes not found.");

            _sharedMapDataFadeField = AccessTools.Field(typeof(Minimap), "m_sharedMapDataFade");
            if (_sharedMapDataFadeField == null)
                Plugin.Log.LogWarning("Minimap.m_sharedMapDataFade not found: a map click also weighs shared pins while they are hidden.");

            PropertyInfo pir = AccessTools.Property(typeof(Minimap), "PinInteractRadius");
            if (pir != null) _pinInteractRadiusGetter = pir.GetGetMethod(true);
            if (_pinInteractRadiusGetter == null)
                Plugin.Log.LogWarning("Minimap.PinInteractRadius not found: map clicks reach as far as its public parts "
                    + "(m_removeRadius, LargeZoom) say instead.");
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

        /// <summary>
        /// True when the map shows this point as explored: explored by the player, or shared to them by a
        /// Cartography Table (Minimap.IsExplored reads m_explored, then m_exploredOthers, in 12 m map pixels).
        /// False when that cannot be read, so Wayfinder's search then places nothing rather than something the
        /// map does not show, and Wayfinder's map click places no waypoint on open ground anywhere, silently (following a
        /// pin and removing a waypoint still work) - after a game update that renames or removes Minimap.IsExplored.
        /// </summary>
        internal static bool IsExplored(Vector3 world)
        {
            Minimap mm = Minimap.instance;
            if (mm == null || _isExplored == null) return false;
            try
            {
                return _isExplored(mm, world);
            }
            catch (Exception e)
            {
                if (!_isExploredWarned)
                {
                    _isExploredWarned = true;
                    Plugin.Log.LogWarning("Minimap.IsExplored failed (" + e.GetType().Name + "): no waypoint is placed where "
                        + "the map cannot tell explored from fog. Later failures are not logged.");
                }
                return false;
            }
        }

        private static bool _isExploredWarned, _canAddPinsWarned, _radiusPartsWarned;

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
            catch (Exception e)
            {
                Plugin.WarnOnce(ref _canAddPinsWarned, "Could not tell whether the map is ready for markers", e,
                    "no waypoint markers are made while this fails");
                return false;
            }
        }

        /// <summary>True when the pin is still present in the minimap's own list (i.e. our reference is not stale).</summary>
        internal static bool PinIsAlive(Minimap mm, Minimap.PinData pin)
        {
            if (pin == null || mm == null) return false;
            return GetPins(mm).Contains(pin);
        }

        /// <summary>The last resort for PinInteractRadius, when neither its getter nor its public parts can be read.</summary>
        internal const float FallbackPinInteractRadius = 12f;

        private static bool _radiusWarned;

        /// <summary>
        /// How far from a map click a pin still counts as clicked: Valheim's own Minimap.PinInteractRadius (private),
        /// read through reflection; else worked out the same way from its public parts; else FallbackPinInteractRadius.
        /// </summary>
        internal static float PinInteractRadius(Minimap mm)
        {
            if (mm == null) return FallbackPinInteractRadius;
            if (_pinInteractRadiusGetter != null)
            {
                try
                {
                    object v = _pinInteractRadiusGetter.Invoke(mm, null);
                    if (v is float && (float)v > 0f) return (float)v;
                }
                catch (Exception e)
                {
                    if (!_radiusWarned)
                    {
                        _radiusWarned = true;
                        Plugin.Log.LogWarning("Minimap.PinInteractRadius could not be read (" + e.Message + "): map clicks "
                            + "reach as far as its public parts (m_removeRadius, LargeZoom) say instead.");
                    }
                }
            }
            try
            {
                float r = RadiusFromPublicParts(mm);
                if (r > 0f) return r;
            }
            catch (Exception e)
            {
                // A public member an update renamed: the last resort below.
                Plugin.WarnOnce(ref _radiusPartsWarned, "The map click's reach could not be worked out from the map's public members", e,
                    "a fixed reach is used instead");
            }
            return FallbackPinInteractRadius;
        }

        /// <summary>
        /// Minimap.PinInteractRadius as Valheim 1.0.16 works it out, from public members only: m_removeRadius *
        /// (LargeZoom * 2), 1.3 times that with touch input - so it follows the map's zoom. A method of its own, so that
        /// a member an update renames fails only here, when this method is compiled, and not the caller.
        /// </summary>
        private static float RadiusFromPublicParts(Minimap mm)
        {
            float r = mm.m_removeRadius * (mm.LargeZoom * 2f);
            if (ZInput.IsTouchActive()) r *= 1.3f;
            return r;
        }

        /// <summary>
        /// Nearest pin that one of our waypoints is using, shown on the map or not, so our own markers win the right-click
        /// delete, measured on the horizontal plane.
        ///
        /// This walks m_pins directly rather than using Valheim's own GetClosestPin, because that
        /// helper begins its loop with `if (!pin.m_save) continue;` - verified in IL. Our waypoint
        /// markers are deliberately created with save:false so they never end up in the player's saved
        /// map data, which makes them invisible to every vanilla hit test, including the one behind
        /// GetClosestPinToCursor and the delete gestures.
        /// </summary>
        internal static Minimap.PinData GetClosestWaypointPin(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, true, true, null, 1f);
        }

        /// <summary>
        /// The nearest pin of the player's (or a shared or vanilla one) that a waypoint follows and the large map shows,
        /// within radius, or null - never a marker this mod placed. The map click weighs it (MapClickRules.Decide).
        /// </summary>
        internal static Minimap.PinData GetClosestFollowedPin(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, false, true, ShownIconTypes(mm), SharedPinsFade(mm));
        }

        /// <summary>
        /// The pin Minimap.RemovePin(pos, radius) is about to delete: Valheim's own GetClosestPin with
        /// mustBeVisible, exactly as RemovePin calls it. Never one of our markers (save:false).
        /// </summary>
        internal static Minimap.PinData GetVanillaClosestPin(Minimap mm, Vector3 world, float radius)
        {
            if (mm == null || _closestPin == null) return null;
            try { return _closestPin(mm, world, radius, true); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("GetClosestPin failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The nearest pin the map click may follow (ClosestAdoptable) among the pins the large map shows: Valheim's own
        /// click leaves out a pin the map hides, so a hidden pin never takes a click aimed at a visible one.
        /// </summary>
        internal static Minimap.PinData GetClosestAdoptablePin(Minimap mm, Vector3 world, float radius)
        {
            return ClosestAdoptable(mm, world, radius, ShownIconTypes(mm), SharedPinsFade(mm));
        }

        /// <summary>
        /// The nearest pin a waypoint may attach itself to, shown on the map or not: a followed pin is found again
        /// (EnsurePins) whether or not the map hides it.
        /// </summary>
        internal static Minimap.PinData GetClosestAdoptablePinEvenHidden(Minimap mm, Vector3 world, float radius)
        {
            return ClosestAdoptable(mm, world, radius, null, 1f);
        }

        /// <summary>
        /// Nearest pin a borrowed waypoint may attach itself to: not already used by any waypoint (which
        /// also rules out our own stand-in marker sitting on the same spot), not a transient pin, and not one
        /// the given icon filter and shared-pin fade hide (MapClickRules.PinShown; null and 1 hide nothing).
        /// Deliberately NOT filtered on m_save: some vanilla markers a player might follow, such as the
        /// bed spawn point, are save:false too.
        /// </summary>
        private static Minimap.PinData ClosestAdoptable(Minimap mm, Vector3 world, float radius, bool[] shownIconTypes, float sharedPinsFade)
        {
            List<Minimap.PinData> pins = GetPins(mm);
            Minimap.PinData best = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData p = pins[i];
                if (p == null || IsTransient(p.m_type)) continue;
                if (!MapClickRules.PinShown(shownIconTypes, (int)p.m_type, sharedPinsFade, p.m_ownerID)) continue;
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

        /// <summary>
        /// The nearest transient pin (a ping, a shout, another player's marker, an event marker) the large map shows,
        /// within radius, or null. Such a pin is never followed; when it is the pin nearest a map click, the click places
        /// a waypoint on the spot.
        /// </summary>
        internal static Minimap.PinData GetClosestTransientPin(Minimap mm, Vector3 world, float radius)
        {
            List<Minimap.PinData> pins = GetPins(mm);
            bool[] shownIconTypes = ShownIconTypes(mm);
            float sharedPinsFade = SharedPinsFade(mm);
            Minimap.PinData best = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData p = pins[i];
                if (p == null || !IsTransient(p.m_type)) continue;
                if (!MapClickRules.PinShown(shownIconTypes, (int)p.m_type, sharedPinsFade, p.m_ownerID)) continue;

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

        /// <summary>
        /// The nearest waypoint marker the mod itself placed (not a pin of the player's that a waypoint
        /// follows) that the large map shows, within radius, or null. The map click weighs it by distance against the
        /// other pins (MapClickRules.Decide).
        /// </summary>
        internal static Minimap.PinData GetClosestOwnedWaypointPin(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, true, false, ShownIconTypes(mm), SharedPinsFade(mm));
        }

        /// <summary>
        /// The nearest waypoint marker the mod itself placed, shown on the map or not, within radius, or null. The
        /// right-click delete uses it, so that a marker of ours within reach always wins over a pin of the player's.
        /// </summary>
        internal static Minimap.PinData GetClosestOwnedWaypointPinEvenHidden(Minimap mm, Vector3 world, float radius)
        {
            return FindClosest(mm, world, radius, true, false, null, 1f);
        }

        /// <summary>
        /// The nearest pin a waypoint uses: a marker this mod placed (owned) and/or a pin it follows, leaving out one the
        /// given icon filter and shared-pin fade hide (MapClickRules.PinShown; null and 1 hide nothing).
        /// </summary>
        private static Minimap.PinData FindClosest(Minimap mm, Vector3 world, float radius, bool owned, bool followed,
            bool[] shownIconTypes, float sharedPinsFade)
        {
            List<Minimap.PinData> pins = GetPins(mm);
            Minimap.PinData best = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < pins.Count; i++)
            {
                Minimap.PinData p = pins[i];
                if (p == null) continue;
                Waypoint w = WaypointManager.FindByPin(p);
                if (w == null || (w.OwnsPin ? !owned : !followed)) continue;
                if (!MapClickRules.PinShown(shownIconTypes, (int)p.m_type, sharedPinsFade, p.m_ownerID)) continue;

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
        /// The large map's icon filter (Minimap.m_visibleIconTypes, indexed by pin type), or null when it cannot be
        /// read - then no icon type counts as hidden.
        /// </summary>
        private static bool[] ShownIconTypes(Minimap mm)
        {
            if (mm == null || _visibleIconTypesField == null) return null;
            try
            {
                return _visibleIconTypesField.GetValue(mm) as bool[];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// How far shared pins are faded in (Minimap.m_sharedMapDataFade: down to 0 while the map's shared-pins toggle
        /// hides them, up to 1 while it shows them), or 1 when it cannot be read - then no shared pin counts as hidden.
        /// </summary>
        private static float SharedPinsFade(Minimap mm)
        {
            if (mm == null || _sharedMapDataFadeField == null) return 1f;
            try
            {
                object v = _sharedMapDataFadeField.GetValue(mm);
                return v is float ? (float)v : 1f;
            }
            catch (Exception)
            {
                return 1f;
            }
        }
    }
}
