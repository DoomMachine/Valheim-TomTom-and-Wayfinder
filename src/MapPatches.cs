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
        // Vanilla's own double-click test (Minimap.OnMapLeftUp) is two clicks inside 0.3 s and within
        // PinInteractRadius. Applying the same test stops a double click from adding a waypoint and then
        // removing it again with its second half.
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

            float radius = MinimapAccess.PinInteractRadius(minimap, MinimapAccess.FallbackPinInteractRadius);

            // Ignore the second half of a double click - consumed, but does nothing.
            if (Time.time - _lastClickTime < DoubleClickWindow
                && WaypointManager.HorizontalDistance(world, _lastClickWorld) < radius)
            {
                return true;
            }
            _lastClickTime = Time.time;
            _lastClickWorld = world;

            // Our own markers must win the gesture, and they can only be found by walking m_pins:
            // Valheim's hit tests skip pins with save:false, which ours deliberately are.
            Minimap.PinData pin = MinimapAccess.GetClosestWaypointPin(minimap, world, radius);
            if (pin != null)
            {
                // Clicking a marker that is already a waypoint clears it. If the marker was the
                // player's own, only the waypoint goes away - the marker stays on the map.
                WaypointManager.Remove(WaypointManager.FindByPin(pin));
                WaypointManager.Notify("Waypoint removed");
                return true;
            }

            // Transient pins (pings, shouts, other players, raid markers) are skipped: their marker moves
            // or expires on its own. No waypoint marker is in range here - GetClosestWaypointPin just
            // returned null for the same radius.
            pin = MinimapAccess.GetClosestAdoptablePin(minimap, world, radius);
            if (pin != null)
            {
                WaypointManager.AddFromPin(pin);
                WaypointManager.Notify("Waypoint set: " + (string.IsNullOrEmpty(pin.m_name) ? "marker" : GameText.Localize(pin.m_name)));
                return true;
            }

            Waypoint added = WaypointManager.Add(world, "", false);
#if WAYFINDER
            WaypointManager.Notify("Waypoint added");   // no coordinates: a readout would let clicks be steered
#else
            WaypointManager.Notify("Waypoint added at " + CoordinateFormat.Format(added.Pos, false));
#endif
            return true;
        }
    }

    /// <summary>
    /// Minimap.OnMapLeftUp runs its own double-click test after OnMapLeftClick, whatever our prefix on
    /// that method did, and OnMapDblClick places a saved vanilla pin (save:true, so it reaches the
    /// Cartography Table) or sends a ping. Neither may happen for a double click on our window, nor while
    /// the waypoint modifier is held - that gesture belongs to us.
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "OnMapDblClick")]
    public static class Minimap_OnMapDblClick_Patch
    {
        private static bool Prefix()
        {
            try
            {
                if (WaypointWindow.PointerOverWindow()) return false;
                KeyCode modifier = Plugin.MapModifierKey.Value;
                if (modifier == KeyCode.None) return true;
                return !ZInput.GetKey(modifier, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("OnMapDblClick prefix failed: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// The large map's uGUI handlers still fire underneath our IMGUI window, because uGUI raycasts cannot
    /// see it. This covers the press that starts a map drag. OnMapLeftUp is deliberately NOT patched:
    /// skipping it when a drag is released over the window would leave m_dragView stuck true.
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "OnMapLeftDown")]
    public static class Minimap_OnMapLeftDown_Patch
    {
        private static bool Prefix()
        {
            return !WaypointWindow.PointerOverWindow();
        }
    }

    /// <summary>
    /// Right and middle clicks on our window must not reach the large map under it (a right click
    /// deletes a pin, a middle click pings every player). Patched on the uGUI handler rather than on
    /// Minimap.OnMapMiddleClick because Server Devcommands replaces that method with a prefix that sends
    /// the ping itself and returns false, and HarmonyX runs every prefix, so a Minimap-level prefix
    /// could not stop the ping.
    /// </summary>
    [HarmonyPatch(typeof(UIInputHandler), "OnPointerClick")]
    public static class UIInputHandler_OnPointerClick_Patch
    {
        private static bool Prefix(UIInputHandler __instance)
        {
            if (!WaypointWindow.PointerOverWindow()) return true;
            Minimap map = Minimap.instance;
            if (map == null || map.m_mapImageLarge == null || __instance == null) return true;
            return __instance.gameObject != map.m_mapImageLarge.gameObject;
        }
    }

    /// <summary>
    /// Keeps the queue honest when the player deletes a pin with Valheim's own gesture. Right click and
    /// touch long-press (through RemovePinUnderPointer) and the gamepad remove button (straight from
    /// UpdateMap) all end in the public Minimap.RemovePin(Vector3, float), so that is what is patched;
    /// the callers hide the pin-name box themselves around it.
    ///
    /// Vanilla deletes the nearest SAVED, visible pin and cannot see our markers (save:false). When one
    /// of our waypoints wins the gesture it is removed here and the original skipped - letting it run
    /// would delete whichever saved pin is nearest instead. Otherwise vanilla deletes its own pick, and a
    /// waypoint that follows a pin is forgotten only if that pin really left the map (the postfix).
    /// </summary>
    [HarmonyPatch(typeof(Minimap), "RemovePin", new Type[] { typeof(Vector3), typeof(float) })]
    public static class Minimap_RemovePin_Patch
    {
        private static bool Prefix(Minimap __instance, Vector3 pos, float radius, ref bool __result, ref Minimap.PinData[] __state)
        {
            __state = null;
            try
            {
                if (__instance == null || !WaypointManager.HasActive) return true;

                // A marker of ours within reach always wins, even when a followed pin or one of the
                // player's own pins is nearer the pointer: deleting a player's pin cannot be undone, a
                // waypoint is quickly re-added. Remove deletes only a marker this mod owns.
                Waypoint own = WaypointManager.FindByPin(MinimapAccess.GetClosestOwnedWaypointPin(__instance, pos, radius));
                if (own != null)
                {
                    WaypointManager.Remove(own);
                    WaypointManager.Notify("Waypoint removed");
                    __result = true;
                    return false;
                }

                // From here on the only waypoint pins in reach are pins of the player's that a waypoint follows.
                Minimap.PinData mine = MinimapAccess.GetClosestWaypointPin(__instance, pos, radius);
                Waypoint wp = WaypointManager.FindByPin(mine);
                if (wp != null)
                {
                    // What vanilla is about to delete: the nearest saved, visible pin.
                    Minimap.PinData target = MinimapAccess.GetVanillaClosestPin(__instance, pos, radius);

                    // A followed pin wins only when it is nearer than vanilla's pick and vanilla would not
                    // delete it anyway - the bed, a trader icon, a filtered-out pin. (wp.OwnsPin covers a
                    // stand-in marker that appeared after the check above.)
                    bool handleHere = !ReferenceEquals(mine, target)
                        && (wp.OwnsPin || target == null
                            || WaypointManager.HorizontalDistance(mine.m_pos, pos) <= WaypointManager.HorizontalDistance(target.m_pos, pos));
                    if (handleHere)
                    {
                        // Remove deletes only a marker this mod owns; a pin of the player's is never touched.
                        WaypointManager.Remove(wp);
                        WaypointManager.Notify("Waypoint removed");
                        __result = true;
                        return false;
                    }
                }

                // Vanilla deletes its own pick. Remember which followed pins exist now, so the postfix
                // drops exactly the waypoint whose pin it removed.
                __state = WaypointManager.LiveBorrowedPins(__instance);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("RemovePin prefix failed: " + e.Message);
                return true;
            }
        }

        private static void Postfix(Minimap __instance, Minimap.PinData[] __state)
        {
            try
            {
                if (__state == null) return;
                for (int i = 0; i < __state.Length; i++)
                {
                    if (!MinimapAccess.PinIsAlive(__instance, __state[i]) && WaypointManager.ForgetByPin(__state[i]))
                        WaypointManager.Notify("Waypoint removed");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("RemovePin postfix failed: " + e.Message);
            }
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
    /// Reports that a text field is active while our window is open, and for the rest of the frame in
    /// which it closed (so the Escape that closed it cannot also open the pause menu).
    ///
    /// PlayerController.TakeInput only covers movement and look. Valheim gates a lot of other input
    /// (building placement, the map, the pause menu, opening chat, the cursor lock) on
    /// TextInput.IsVisible instead, which is why IMGUI mods such as ConfigurationManager patch exactly
    /// this pair. Following the same pattern keeps us consistent with them.
    /// </summary>
    [HarmonyPatch(typeof(TextInput), "IsVisible")]
    public static class TextInput_IsVisible_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (WaypointWindow.BlocksGameInput) __result = true;
        }
    }

    /// <summary>
    /// The second half of the same signal. InventoryGui.Update (Tab / gamepad Y), GameCamera.UpdateCamera
    /// (wheel and gamepad zoom), HotkeyBar.Update (gamepad hotbar) and Player.UpdatePlacementGhost (snap
    /// cycling) do not consult TextInput.IsVisible, but all of them consult Chat.HasFocus.
    /// </summary>
    [HarmonyPatch(typeof(Chat), "HasFocus")]
    public static class Chat_HasFocus_Patch
    {
        // Last: Chatter's own HasFocus postfix assigns __result outright at default priority and loads
        // after this plugin, so at equal priority it would run later and undo this.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref bool __result)
        {
            if (WaypointWindow.BlocksGameInput) __result = true;
        }
    }

    /// <summary>
    /// Stops the mouse wheel zooming the camera behind our window, and wheel console binds firing. A
    /// postfix, not a prefix, so ZInput still runs its input-source switch. IMGUI's scroll view reads
    /// Event.current, not ZInput, so the queue list still scrolls. Priority.Last lets other mods'
    /// postfixes (MeasurementTracker records the raw value) see the real wheel first.
    /// </summary>
    [HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel")]
    public static class ZInput_GetMouseScrollWheel_Patch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref float __result)
        {
            if (WaypointWindow.IsOpen) __result = 0f;
        }
    }
}
