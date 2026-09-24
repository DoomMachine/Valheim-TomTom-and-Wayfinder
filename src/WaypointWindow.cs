using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The plugin window: review the queue, reorder or delete waypoints - and, in TomTom only, paste
    /// coordinates. Drawn with IMGUI so it works without any asset bundle.
    ///
    /// Every action that changes how many controls the window draws is deferred to the next Layout
    /// pass. Changing the control count part-way through an event pass makes the Layout and Repaint
    /// passes disagree, which is what produces Unity's "Mismatched LayoutGroup" errors.
    /// </summary>
    public static class WaypointWindow
    {
        private static bool _open;
#if WAYFINDER
        private static Rect _rect = new Rect(80f, 80f, 470f, 440f);
#else
        private static Rect _rect = new Rect(80f, 80f, 470f, 580f);
        private static string _input = "";
        private static List<string> _errors = new List<string>();
#endif
        private static Vector2 _listScroll;
        private static string _status = "";

        private static readonly List<Action> _pending = new List<Action>();

        private static bool _textFieldFocused;

        // Cached by hand: under C# 5 a method group passed as a delegate allocates a new delegate on every
        // call, and GUILayout.Window is called on every OnGUI event while the window is open.
        private static readonly GUI.WindowFunction DrawWindowFn = DrawWindow;

        // Layout options are cached: GUILayout.Width/Height allocate an option, a boxed float and a params
        // array on every call, and IMGUI only ever reads them (GUILayoutEntry.ApplyOptions).
#if !WAYFINDER
        private static readonly GUILayoutOption[] _inputHeight = new GUILayoutOption[] { GUILayout.Height(80f) };
#endif
        private static readonly GUILayoutOption[] _listHeight = new GUILayoutOption[] { GUILayout.Height(170f) };
        private static readonly GUILayoutOption[] _nameWidth = new GUILayoutOption[] { GUILayout.Width(200f) };
        private static readonly GUILayoutOption[] _infoWidth = new GUILayoutOption[] { GUILayout.Width(120f) };
        private static readonly GUILayoutOption[] _goWidth = new GUILayoutOption[] { GUILayout.Width(38f) };
        private static readonly GUILayoutOption[] _removeWidth = new GUILayoutOption[] { GUILayout.Width(26f) };

        // Text that only changes with the queue length or a setting, built once per change, not per pass.
        private static string _queueHeader;
        private static int _queueHeaderCount = -1;
        private static readonly List<string> _rowPrefixes = new List<string>();
        private static string _modifierHint, _modifierHintBefore, _modifierHintAfter;
        private static KeyCode _modifierHintKey = KeyCode.None;

        private static GUIStyle _headerStyle;
        private static GUIStyle _smallStyle;

        public static bool IsOpen { get { return _open; } }

        // Frame in which the window last closed. The key that closed it (Escape, gamepad B) is still
        // "down this frame" for any game gate that runs after us, so the gates stay shut until it ends.
        private static int _closedFrame = -1;

        /// <summary>True while open, and for the rest of the frame in which the window closed.</summary>
        public static bool BlocksGameInput
        {
            get { return _open || _closedFrame == Time.frameCount; }
        }

        /// <summary>True while one of our text fields owns the keyboard, so hotkeys must stand down.</summary>
        public static bool TextFieldHasFocus { get { return _open && _textFieldFocused; } }

        public static void Toggle()
        {
            if (_open) Close(); else Open();
        }

        public static void Open()
        {
            if (_open) return;
            _open = true;
        }

        public static void Close()
        {
            if (!_open) return;
            _open = false;
            _closedFrame = Time.frameCount;
            _textFieldFocused = false;
            _pending.Clear();
        }

        /// <summary>
        /// The cursor is released by the game itself: GameCamera.UpdateMouseCapture runs every
        /// LateUpdate and takes its "release the cursor" branch whenever TextInput.IsVisible() reports
        /// true, which our patch makes it do while this window is open. Releasing it again on the way
        /// out is therefore unnecessary - vanilla re-locks on the next frame by itself.
        ///
        /// This is only a safety net for the case where that patch failed to apply, and it goes through
        /// ZCursor rather than UnityEngine.Cursor because ZCursor tracks its own visibility state -
        /// writing Cursor directly would leave that state lying about what the cursor is doing.
        /// </summary>
        public static void UpdateCursorState()
        {
            if (!_open) return;

            // Close automatically if the player vanishes (main menu, world unload).
            if (Player.m_localPlayer == null)
            {
                Close();
                return;
            }

            if (ZCursor.LockState != CursorLockMode.None)
                ZCursor.LockState = CursorLockMode.None;
            if (!ZCursor.IsVisible)
                ZCursor.Show();
        }

        private static void Defer(Action action)
        {
            _pending.Add(action);
        }

        /// <summary>
        /// True when the mouse is over our window. Used so a click meant for the window does not also
        /// register on whatever game UI happens to be underneath it, such as the world map.
        /// Screen coordinates have y running up from the bottom, GUI coordinates run down from the top.
        /// </summary>
        public static bool PointerOverWindow()
        {
            if (!_open) return false;
            Vector3 pointer = ZInput.pointerPosition;
            return _rect.Contains(new Vector2(pointer.x, Screen.height - pointer.y));
        }

        public static void Draw()
        {
            if (!_open) return;
            if (Player.m_localPlayer == null) return;

            EnsureStyles();
            _rect = GUILayout.Window(Edition.WindowId, _rect, DrawWindowFn, Edition.Name);

            // Keep the window on screen if the resolution changes underneath it.
            _rect.x = Mathf.Clamp(_rect.x, -_rect.width + 60f, Screen.width - 60f);
            _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - 40f);
        }

        private static void EnsureStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label);
                _headerStyle.fontStyle = FontStyle.Bold;
                _headerStyle.fontSize = 13;
            }
            if (_smallStyle == null)
            {
                _smallStyle = new GUIStyle(GUI.skin.label);
                _smallStyle.fontSize = 11;
                _smallStyle.wordWrap = true;
            }
        }

        private static void DrawWindow(int id)
        {
            // Apply everything that was requested last pass, before any control is laid out.
            if (Event.current.type == EventType.Layout && _pending.Count > 0)
            {
                List<Action> queued = new List<Action>(_pending);
                _pending.Clear();
                for (int i = 0; i < queued.Count; i++)
                {
                    try { queued[i](); }
                    catch (Exception e) { Plugin.Log.LogError("Window action failed: " + e); }
                }
            }

            GUILayout.Space(4f);

#if WAYFINDER
            DrawMapGuidance();
#else
            DrawInputSection();
#endif
            GUILayout.Space(6f);
            DrawQuickActions();
            GUILayout.Space(6f);
            DrawActiveSection();
            GUILayout.Space(4f);
            DrawQueueSection();

            GUILayout.FlexibleSpace();
            GUILayout.Label(string.IsNullOrEmpty(_status) ? " " : _status, _smallStyle);

            if (GUILayout.Button("Close"))
                Defer(Close);

            if (Event.current.type == EventType.Repaint)
                _textFieldFocused = GUIUtility.keyboardControl != 0;

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

#if WAYFINDER
        /// <summary>
        /// Wayfinder's replacement for the coordinate box: waypoints come only from the world map.
        /// </summary>
        private static void DrawMapGuidance()
        {
            GUILayout.Label("Setting a waypoint", _headerStyle);
            GUILayout.Label(ModifierHint("Open the world map and hold ", " while you click:\n"
                + "  - an empty spot to place a waypoint there\n"
                + "  - one of your own markers to follow it\n"
                + "  - a waypoint marker to remove it"), _smallStyle);
        }
#else
        private static void DrawInputSection()
        {
            GUILayout.Label("Coordinates", _headerStyle);

            string hint = Plugin.RawValheimOrder.Value
                ? "Raw Valheim order: x y z, where y is altitude. One per line."
                : "Type X, Y and an optional third value for elevation. One per line. "
                  + "A trailing word becomes the name, e.g. 1234, -567, Silver vein";
            GUILayout.Label(hint, _smallStyle);

            _input = GUILayout.TextArea(_input, _inputHeight);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add to queue")) Defer(ApplyInputAdd);
            if (GUILayout.Button("Replace queue")) Defer(ApplyInputReplace);
            if (GUILayout.Button("Clear text")) Defer(ClearInput);
            GUILayout.EndHorizontal();

            // Drawn every pass so the control count never changes between Layout and Repaint.
            int shown = Mathf.Min(_errors.Count, 5);
            for (int i = 0; i < shown; i++)
            {
                Color previous = GUI.color;
                GUI.color = new Color(1f, 0.6f, 0.6f);
                GUILayout.Label(_errors[i], _smallStyle);
                GUI.color = previous;
            }
            if (_errors.Count > shown)
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "...and {0} more", _errors.Count - shown), _smallStyle);
        }

        private static void ClearInput()
        {
            _input = "";
            _errors.Clear();
            _status = "";
        }

        private static void ApplyInputAdd() { ApplyInput(false); }
        private static void ApplyInputReplace() { ApplyInput(true); }

        private static void ApplyInput(bool replace)
        {
            List<string> errors;
            List<ParsedCoord> parsed = CoordinateParser.ParseList(_input, out errors);
            _errors = errors;

            if (parsed.Count == 0)
            {
                _status = "Nothing added - no valid coordinates found.";
                return;
            }

            if (replace) WaypointManager.Clear();

            int added = WaypointManager.AddRange(parsed, Plugin.RawValheimOrder.Value);

            _status = string.Format(CultureInfo.InvariantCulture,
                "Added {0} waypoint{1}{2}.", added, added == 1 ? "" : "s",
                errors.Count > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0} line(s) skipped)", errors.Count) : "");
        }
#endif

        private static void DrawQuickActions()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add my position")) Defer(AddHere);
            if (GUILayout.Button("Nearest first")) Defer(ActivateNearest);
            GUILayout.EndHorizontal();
        }

        private static void AddHere()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;
            WaypointManager.Add(player.transform.position, "Marked spot", true);
            _status = "Added your current position.";
        }

        private static void ActivateNearest()
        {
            Player player = Player.m_localPlayer;
            if (player == null || !WaypointManager.HasActive) return;
            WaypointManager.ActivateClosest(player.transform.position);
            _status = "Switched to the nearest waypoint.";
        }

        private static void DrawActiveSection()
        {
            GUILayout.Label("Active target", _headerStyle);

            Waypoint active = WaypointManager.Active;
            if (active == null)
            {
#if WAYFINDER
                GUILayout.Label("None yet - place one on the world map.", _smallStyle);
#else
                GUILayout.Label(ModifierHint("None. Add coordinates above, or hold ", " and click the world map."), _smallStyle);
#endif
                return;
            }

            Player player = Player.m_localPlayer;
            string distanceText = "";
            if (player != null)
            {
                float d = WaypointManager.HorizontalDistance(player.transform.position, active.Pos);
                distanceText = string.Format(CultureInfo.InvariantCulture, "   {0:0} m away", d);
            }

            GUILayout.Label(active.DisplayName + distanceText);
#if !WAYFINDER
            GUILayout.Label(active.CoordText, _smallStyle);
#endif

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Skip this one")) Defer(SkipActive);
            if (GUILayout.Button("Clear all")) Defer(ClearAll);
            GUILayout.EndHorizontal();
        }

        private static void SkipActive()
        {
            WaypointManager.SkipActive();
            _status = "Skipped.";
        }

        private static void ClearAll()
        {
            WaypointManager.Clear();
            _status = "Queue cleared.";
        }

        private static void DrawQueueSection()
        {
            List<Waypoint> queue = WaypointManager.Queue;
            if (_queueHeader == null || queue.Count != _queueHeaderCount)
            {
                _queueHeaderCount = queue.Count;
                _queueHeader = string.Format(CultureInfo.InvariantCulture, "Queue ({0})", queue.Count);
            }
            GUILayout.Label(_queueHeader, _headerStyle);

            _listScroll = GUILayout.BeginScrollView(_listScroll, _listHeight);

            for (int i = 0; i < queue.Count; i++)
            {
                Waypoint wp = queue[i];

                GUILayout.BeginHorizontal();

                GUILayout.Label(RowPrefix(i) + wp.DisplayName, _nameWidth);
#if WAYFINDER
                // Distance rather than coordinates: relative, so it can't be matched to a looked-up place.
                GUILayout.Label(DistanceTo(wp), _smallStyle, _infoWidth);
#else
                GUILayout.Label(wp.CoordText, _smallStyle, _infoWidth);
#endif

                GUI.enabled = i != 0;
                if (GUILayout.Button("Go", _goWidth)) Defer(ActivateLater(wp));
                GUI.enabled = true;

                if (GUILayout.Button("X", _removeWidth)) Defer(RemoveLater(wp));

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
        }

        // The waypoint itself is captured, not its row number: the queue can change between the click and
        // the deferred action running (an arrival removes entry 0), and an index captured now could by then
        // point at a different waypoint. The closures are built here, only when a button is clicked -
        // written inline in the row loop, their capture object is allocated for every row on every pass.
        private static Action ActivateLater(Waypoint wp) { return delegate { Activate(wp); }; }
        private static Action RemoveLater(Waypoint wp) { return delegate { RemoveWaypoint(wp); }; }

        /// <summary>"> " for the active row, otherwise "2. ", "3. " ... - built once per row number.</summary>
        private static string RowPrefix(int i)
        {
            if (i == 0) return "> ";
            while (_rowPrefixes.Count <= i)
                _rowPrefixes.Add(string.Format(CultureInfo.InvariantCulture, "{0}. ", _rowPrefixes.Count + 1));
            return _rowPrefixes[i];
        }

        /// <summary>before + the map modifier key + after, rebuilt only when one of them changes.</summary>
        private static string ModifierHint(string before, string after)
        {
            KeyCode key = Plugin.MapModifierKey.Value;
            if (_modifierHint == null || key != _modifierHintKey
                || !ReferenceEquals(before, _modifierHintBefore) || !ReferenceEquals(after, _modifierHintAfter))
            {
                _modifierHintKey = key;
                _modifierHintBefore = before;
                _modifierHintAfter = after;
                _modifierHint = before + key + after;
            }
            return _modifierHint;
        }

#if WAYFINDER
        private static string DistanceTo(Waypoint wp)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return "";
            float d = WaypointManager.HorizontalDistance(player.transform.position, wp.Pos);
            return string.Format(CultureInfo.InvariantCulture, "{0:0} m", d);
        }
#endif

        private static void Activate(Waypoint wp)
        {
            if (WaypointManager.MakeActive(WaypointManager.IndexOf(wp)))
                _status = "Target switched.";
        }

        private static void RemoveWaypoint(Waypoint wp)
        {
            if (WaypointManager.Remove(wp))
                _status = "Waypoint deleted.";
        }
    }
}
