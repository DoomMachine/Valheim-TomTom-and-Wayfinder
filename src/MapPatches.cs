using System;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// World-map interaction. Holding the modifier key while left-clicking the map adds, promotes or
    /// deletes a waypoint; without the modifier Valheim keeps its normal pin behaviour untouched.
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "OnMapLeftClick")]
    public static class Minimap_OnMapLeftClick_Patch
    {
        // Vanilla treats two clicks inside 0.3 s as a double click; matching that window stops a
        // double click from creating two waypoints on the same spot.
        private const float DoubleClickWindow = 0.3f;
        private static float _lastClickTime = -10f;
        private static Vector3 _lastClickWorld;

        private static bool Prefix(Minimap __instance)
        {
            try
            {
                // Swallow clicks that were aimed at our own window sitting over the map.
                if (WaypointWindow.PointerOverWindow()) return false;

                KeyCode modifier = Plugin.MapModifierKey.Value;
                if (modifier == KeyCode.None) return true;
                if (!ZInput.GetKey(modifier, false)) return true;

                // Suppressed only when we actually acted on the click.
                return !HandleWaypointClick(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Map click handling failed: " + e);
                return true;
            }
        }

        /// <summary>Returns true when the click was consumed and vanilla should be skipped.</summary>
        private static bool HandleWaypointClick(Minimap minimap)
        {
            if (minimap == null) return false;

            Vector3 world;
            if (!MinimapAccess.TryScreenToWorld(minimap, ZInput.pointerPosition, out world))
            {
                // We cannot tell where the click landed, so leave it to the game rather than eat it.
                return false;
            }

            // Ignore the second half of a double click on the same spot - consumed, but does nothing.
            if (Time.time - _lastClickTime < DoubleClickWindow
                && WaypointManager.HorizontalDistance(world, _lastClickWorld) < 1f)
            {
                return true;
            }
            _lastClickTime = Time.time;
            _lastClickWorld = world;

            float radius = MinimapAccess.PinInteractRadius(minimap, 12f);

            // Our own markers must win the gesture, and they can only be found by walking m_pins:
            // Valheim's hit tests skip pins with save:false, which ours deliberately are.
            Minimap.PinData pin = MinimapAccess.GetClosestWaypointPin(minimap, world, radius);
            if (pin != null)
            {
                // Clicking a marker that is already a waypoint clears it. If the marker was the
                // player's own, only the waypoint goes away - the marker stays on the map.
                WaypointManager.Remove(WaypointManager.FindByPin(pin));
                Notify("Waypoint removed");
                return true;
            }

            pin = MinimapAccess.GetClosestPinToWorldPos(minimap, world, radius);
            if (pin != null)
            {
                WaypointManager.AddFromPin(pin);
                Notify("Waypoint set: " + (string.IsNullOrEmpty(pin.m_name) ? "marker" : GameText.Localize(pin.m_name)));
                return true;
            }

            Waypoint added = WaypointManager.Add(world, "", false);
#if WAYFINDER
            Notify("Waypoint added");   // no coordinates: a readout would let clicks be steered
#else
            Notify("Waypoint added at " + CoordinateFormat.Format(added.Pos, false));
#endif
            return true;
        }

        private static void Notify(string text)
        {
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text, 0, null, false, false);
        }
    }

    /// <summary>
    /// Keeps the queue honest when the player deletes one of our markers using Valheim's own
    /// remove-pin gesture. Without this the marker would simply be recreated moments later, because the
    /// waypoint that owns it is still queued.
    ///
    /// The waypoint is forgotten WITHOUT removing the marker ourselves - vanilla is about to remove it
    /// on its own, and removing it twice would destroy its UI element twice.
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "RemovePinUnderPointer")]
    public static class Minimap_RemovePinUnderPointer_Patch
    {
        private static bool Prefix(Minimap __instance)
        {
            try
            {
                if (__instance == null) return true;

                Vector3 world;
                if (!MinimapAccess.TryScreenToWorld(__instance, ZInput.pointerPosition, out world))
                    return true;

                float radius = MinimapAccess.PinInteractRadius(__instance, 12f);
                Minimap.PinData pin = MinimapAccess.GetClosestWaypointPin(__instance, world, radius);
                if (pin == null) return true;

                Waypoint wp = WaypointManager.FindByPin(pin);
                if (wp == null) return true;

                if (wp.OwnsPin)
                {
                    // Vanilla cannot see this marker at all (save:false), so if we let the original run
                    // it would delete whichever saved pin happens to be nearest instead - losing the
                    // player's own map data. Handle it here and skip the original entirely.
                    MinimapAccess.HidePinTextInput(__instance);
                    WaypointManager.Remove(wp);
                    Notify("Waypoint removed");
                    return false;
                }

                // The marker belongs to the player. Let vanilla delete it, and just drop our waypoint.
                WaypointManager.ForgetByPin(pin);
                Notify("Waypoint removed");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("RemovePinUnderPointer prefix failed: " + e.Message);
                return true;
            }
        }

        private static void Notify(string text)
        {
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text, 0, null, false, false);
        }
    }

    /// <summary>
    /// Stops the character reacting to keys while the plugin window has the keyboard.
    /// Only blocks while our window is actually open, so other mods that patch the same method
    /// keep working normally.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    public static class PlayerController_TakeInput_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!WaypointWindow.IsOpen) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Reports that a text field is active while our window is open.
    ///
    /// PlayerController.TakeInput only covers movement and look. Valheim gates a lot of other input
    /// (inventory, placement, the console bind dispatcher) on TextInput.IsVisible instead, which is why
    /// both ConfigurationManager and MeasurementTracker - the two IMGUI mods already installed here -
    /// patch exactly this pair. Following the same pattern keeps us consistent with them.
    /// </summary>
    [HarmonyPatch(typeof(TextInput), "IsVisible")]
    public static class TextInput_IsVisible_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (WaypointWindow.IsOpen) __result = true;
        }
    }
}
