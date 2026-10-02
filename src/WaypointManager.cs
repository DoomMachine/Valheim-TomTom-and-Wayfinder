using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Waypointer
{
    /// <summary>A single navigation target.</summary>
    public class Waypoint
    {
        public string Name;

        /// <summary>World position: (x = east/west, y = altitude, z = north/south).</summary>
        public Vector3 Pos;

        /// <summary>True when the player supplied an altitude, so it is trustworthy for display.</summary>
        public bool HasElevation;

        /// <summary>
        /// The altitude is the world generator's estimate, not a measured one: a place Find found (its location's
        /// height before the terrain is built and levelled). The 3D arrival test measures such a waypoint at the loaded
        /// ground instead (ArrivalRules). Saved as hasElevation 2.
        /// </summary>
        public bool HeightIsEstimate;

        /// <summary>
        /// Persisted intent: true when the waypoint follows a pin that was already on the map (the
        /// player's own, one shared through a Cartography Table, or a vanilla marker such as the bed
        /// spawn point) rather than having a marker of its own. That pin is never modified or removed
        /// by the mod.
        /// Never changes after creation - see OwnsPin for what is on the map right now.
        /// </summary>
        public bool Borrowed;

        /// <summary>
        /// Runtime state: true when Pin is a marker this mod created, which must be removed again when
        /// the waypoint is reached or deleted. Always true for a waypoint with its own marker. For a
        /// borrowed one it is true only while the player's pin can't be found on this map (a looted
        /// tombstone, or another character's pin) and a local stand-in marks the spot instead - the
        /// player's pin is re-adopted the moment it turns up again.
        ///
        /// These used to be one flag that flipped permanently to "owned" whenever the player's pin was
        /// missing for a moment, so after a relog or a character switch the mod stacked its own marker
        /// on top of the player's returning pin, and right-click then removed only the waypoint.
        /// </summary>
        public bool OwnsPin;

        /// <summary>The map marker, if one exists yet. Recreated automatically if the map is rebuilt.</summary>
        public Minimap.PinData Pin;

        /// <summary>
        /// False until the player has been further away than the arrival radius at least once.
        /// Without this a waypoint placed where the player is standing - "Add my position", or
        /// "waypoint here" - would be reached on the very next frame and delete itself instantly.
        /// Deliberately not persisted: after a reload it re-arms on the first frame that is far enough.
        /// </summary>
        public bool Armed;

#if !WAYFINDER
        // The coordinate text is read every frame (arrow caption, window rows) but only changes when the
        // position or the axis-order setting does, so it is built once instead of on every frame.
        private string _coordText;
        private Vector3 _coordTextPos;
        private bool _coordTextElevation;
        private bool _coordTextRaw;

        /// <summary>CoordinateFormat.Format(Pos, HasElevation), cached. TomTom only - Wayfinder shows no coordinates.</summary>
        public string CoordText
        {
            get
            {
                bool raw = Plugin.RawValheimOrder != null && Plugin.RawValheimOrder.Value;
                if (_coordText == null || raw != _coordTextRaw || HasElevation != _coordTextElevation
                    || Pos.x != _coordTextPos.x || Pos.y != _coordTextPos.y || Pos.z != _coordTextPos.z)
                {
                    _coordText = CoordinateFormat.Format(Pos, HasElevation);
                    _coordTextPos = Pos;
                    _coordTextElevation = HasElevation;
                    _coordTextRaw = raw;
                }
                return _coordText;
            }
        }
#endif

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return GameText.Localize(Name);
#if WAYFINDER
                // A map-click waypoint has no name. Wayfinder must not fall back to coordinates (see
                // CoordinateFormat.cs), so it uses the marker label instead.
                string label = Plugin.PinLabel != null ? Plugin.PinLabel.Value : null;
                return string.IsNullOrEmpty(label) ? "Waypoint" : label;
#else
                return CoordText;
#endif
            }
        }
    }

    /// <summary>
    /// Owns the waypoint queue, the temporary map markers, arrival detection and persistence.
    /// The queue is strictly sequential: index 0 is the active target the arrow points at.
    /// </summary>
    public static class WaypointManager
    {
        private static readonly List<Waypoint> _queue = new List<Waypoint>();
        private static bool _dirty;
        private static long _loadedWorldUid;
        private static bool _loadedForThisWorld;

        // A failed save is retried after a growing delay instead of every frame (a read-only file or a
        // full disk would otherwise throw and log 60 times a second), and only the first few failures
        // are logged. Both are reset whenever the queue moves to another world.
        private static float _nextSaveAttempt;
        private static int _saveFailures;

        // Whether the route of the world the queue belongs to has been read. One that could not be read when its world
        // loaded (another program held it) is never saved over, and is read again after a growing delay (Load,
        // RetryRead, SaveIfDirty). Reset whenever the queue moves to another world.
        private static readonly RouteReadGate _read = new RouteReadGate();

        // Speed estimate used for the time-to-arrival readout, smoothed to stop it flickering.
        private static float _lastDistance = -1f;
        private static float _smoothedSpeed;
        private static float _speedTimer;
        private static float _pinTimer;

        public static List<Waypoint> Queue { get { return _queue; } }

        public static Waypoint Active
        {
            get { return _queue.Count > 0 ? _queue[0] : null; }
        }

        public static bool HasActive
        {
            get { return _queue.Count > 0; }
        }

        /// <summary>
        /// True once the queue has been loaded for the world the player is in now. False at the main menu
        /// and on a loading screen, where the queue still holds the previous world's route.
        /// </summary>
        public static bool QueueBelongsToCurrentWorld
        {
            get
            {
                if (!_loadedForThisWorld) return false;
                long uid = CurrentWorldUid();
                return uid != 0L && uid == _loadedWorldUid;
            }
        }

        /// <summary>Metres per second, smoothed. Zero when unknown.</summary>
        public static float SmoothedSpeed { get { return _smoothedSpeed; } }

        // ---------------------------------------------------------------- mutation

        /// <summary>Queues a waypoint with a marker of its own, which EnsurePins creates.</summary>
        public static Waypoint Add(Vector3 pos, string name, bool hasElevation)
        {
            Waypoint wp = new Waypoint();
            wp.Name = name == null ? "" : name.Trim();
            wp.Pos = pos;
            wp.HasElevation = hasElevation;
            wp.Borrowed = false;
            _queue.Add(wp);
            _dirty = true;
            EnsurePins();
            LogAdded("added", wp);
            TraceAdded(pos, hasElevation);
            return wp;
        }

        /// <summary>
        /// Queues several waypoints with markers of their own, in the order given, and makes their markers once at
        /// the end (a search can queue dozens). With replace, the queue is cleared first - only when there is
        /// something to put in its place, so a search that finds nothing leaves the queue alone.
        /// </summary>
        public static int AddMany(List<Vector3> positions, List<string> names, List<bool> heightIsEstimate, bool replace)
        {
            if (positions == null || positions.Count == 0) return 0;
            // Not logged here: TomTom's search logs what it queued, and Wayfinder's says nothing about what it found.
            TraceAddMany(positions.Count, replace);
            if (replace) ClearQueue();
            for (int i = 0; i < positions.Count; i++)
            {
                string name = names != null && i < names.Count ? names[i] : null;
                Waypoint wp = new Waypoint();
                wp.Name = name == null ? "" : name.Trim();
                wp.Pos = positions[i];
                wp.HasElevation = true;
                wp.HeightIsEstimate = heightIsEstimate != null && i < heightIsEstimate.Count && heightIsEstimate[i];
                wp.Borrowed = false;
                _queue.Add(wp);
            }
            _dirty = true;
            ResetSpeedEstimate();
            EnsurePins();
            return positions.Count;
        }

        /// <summary>
        /// Promotes a pin already on the map into a waypoint, without taking ownership of it. It may be
        /// the player's own, one shared through a Cartography Table, or a vanilla marker such as the bed
        /// spawn point.
        ///
        /// The pin is deliberately NOT modified - in particular its m_save flag is left alone. Whether it
        /// saves and whether it is shared are the game's business, and navigating to it must not quietly
        /// change that. Only markers this mod creates are forced local-only; see CreateLocalOnlyPin.
        /// </summary>
        public static Waypoint AddFromPin(Minimap.PinData pin)
        {
            if (pin == null) return null;

            Waypoint existing = FindByPin(pin);
            if (existing != null) return existing;

            Waypoint wp = new Waypoint();
            wp.Name = string.IsNullOrEmpty(pin.m_name) ? "" : pin.m_name;
            wp.Pos = pin.m_pos;
            wp.HasElevation = Mathf.Abs(pin.m_pos.y) > 0.01f;
            wp.Borrowed = true;
            wp.OwnsPin = false;
            wp.Pin = pin;
            _queue.Add(wp);
            _dirty = true;
            LogAdded("following", wp);
            TraceFollowing(pin);
            return wp;
        }

#if !WAYFINDER
        /// <summary>Queues typed coordinates. TomTom only - Wayfinder has no coordinate entry.</summary>
        public static int AddRange(List<ParsedCoord> coords, bool rawOrder)
        {
            int added = 0;
            if (coords == null) return 0;
            for (int i = 0; i < coords.Count; i++)
            {
                ParsedCoord pc = coords[i];
                Vector3 world = CoordinateParser.ToWorld(pc, rawOrder);
                Add(world, pc.Name, pc.HasElevation);
                added++;
            }
            return added;
        }
#endif

        public static Waypoint FindByPin(Minimap.PinData pin)
        {
            if (pin == null) return null;
            for (int i = 0; i < _queue.Count; i++)
                if (ReferenceEquals(_queue[i].Pin, pin)) return _queue[i];
            return null;
        }

        public static int IndexOf(Waypoint wp)
        {
            return _queue.IndexOf(wp);
        }

        public static bool RemoveAt(int index)
        {
            Waypoint wp = RemoveFromQueue(index);
            if (wp == null) return false;
            LogRemoved("removed", wp);
            return true;
        }

        public static bool Remove(Waypoint wp)
        {
            return RemoveAt(_queue.IndexOf(wp));
        }

        /// <summary>Takes a waypoint out of the queue and releases its marker; the removed waypoint, or null.</summary>
        private static Waypoint RemoveFromQueue(int index)
        {
            if (index < 0 || index >= _queue.Count) return null;
            Waypoint wp = _queue[index];
            ReleasePin(wp);
            _queue.RemoveAt(index);
            _dirty = true;
            ResetSpeedEstimate();
            return wp;
        }

        /// <summary>
        /// Drops the waypoint that owns this marker WITHOUT deleting the marker itself. Used when the
        /// game is already in the middle of deleting that marker, so we must not remove it a second
        /// time and destroy its UI element twice.
        /// </summary>
        public static bool ForgetByPin(Minimap.PinData pin)
        {
            Waypoint wp = FindByPin(pin);
            if (wp == null) return false;
            wp.Pin = null;
            _queue.Remove(wp);
            _dirty = true;
            ResetSpeedEstimate();
            LogRemoved("dropped (its pin was deleted)", wp);
            return true;
        }

        public static void Clear()
        {
            int count = _queue.Count;
            ClearQueue();
            Plugin.Log.LogInfo("Route: cleared (" + count.ToString(CultureInfo.InvariantCulture) + " waypoint(s))");
        }

        private static void ClearQueue()
        {
            ReleaseAllPins();
            _queue.Clear();
            _dirty = true;
            ResetSpeedEstimate();
        }

        /// <summary>Moves a waypoint to the front so the arrow points at it.</summary>
        public static bool MakeActive(int index)
        {
            if (index <= 0 || index >= _queue.Count) return false;
            Waypoint wp = _queue[index];
            _queue.RemoveAt(index);
            _queue.Insert(0, wp);
            _dirty = true;
            ResetSpeedEstimate();
            Plugin.Log.LogInfo("Route: " + LogLabel(wp) + " moved to the front (" + _queue.Count.ToString(CultureInfo.InvariantCulture) + " in the route)");
            return true;
        }

        // One log line per change to the route, so a waypoint that went missing in play can be traced from the log.
        // Names and places in the route only - no positions: Wayfinder shows none, and
        // TomTom's map click logs its own.
        private static void LogAdded(string what, Waypoint wp)
        {
            int index = _queue.IndexOf(wp);
            Plugin.Log.LogInfo("Route: " + what + " " + LogLabel(wp) + " (" + MapClickRules.Ordinal(index + 1) + " of "
                + _queue.Count.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private static void LogRemoved(string what, Waypoint wp)
        {
            Plugin.Log.LogInfo("Route: " + what + " " + LogLabel(wp) + " (" + _queue.Count.ToString(CultureInfo.InvariantCulture) + " left)");
        }

        private static string LogLabel(Waypoint wp)
        {
            string name = wp == null || string.IsNullOrEmpty(wp.Name) ? null : GameText.Localize(wp.Name);
            if (!string.IsNullOrEmpty(name)) return "'" + name + "'";
            return wp != null && wp.Borrowed ? "an unnamed pin" : "an unnamed point";   // the bed's spawn pin has no name
        }

        /// <summary>Activates whichever queued waypoint is nearest the player (TomTom "closest waypoint").</summary>
        public static bool ActivateClosest(Vector3 playerPos)
        {
            int best = -1;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _queue.Count; i++)
            {
                float d = HorizontalSqrDistance(playerPos, _queue[i].Pos);
                if (d < bestSqr) { bestSqr = d; best = i; }
            }
            if (best <= 0) return best == 0;
            return MakeActive(best);
        }

        /// <summary>Drops the active waypoint without treating it as reached.</summary>
        public static bool SkipActive()
        {
            Waypoint wp = RemoveFromQueue(0);
            if (wp == null) return false;
            LogRemoved("skipped", wp);
            return true;
        }

        // ---------------------------------------------------------------- per-frame

        public static void Tick()
        {
            Player player = Player.m_localPlayer;
            Minimap mm = Minimap.instance;
            TracePresence(player, mm);

            if (player == null || mm == null)
            {
                // Nothing to do without a player and a map - and deliberately nothing but the ETA
                // estimate is reset here. That estimate is dropped because the next sample would
                // otherwise divide the whole gap's change in distance (death spot to bed, or one
                // world to the next) by half a second and show an arrival time while standing still.
                //
                // The local player is destroyed and recreated on every death while the map, and our
                // markers on it, survive. Forgetting the marker references and reloading the queue at
                // that point (as an earlier version did) made every respawn create a second set of
                // markers and orphan the first, where neither this mod nor vanilla's right-click could
                // remove them. Seen in a live session log: "Local player destroyed" followed by a reload.
                //
                // A genuine change of world is detected by world UID in LoadForCurrentWorldIfNeeded,
                // and any marker that did not survive a map rebuild is recreated by EnsurePins.
                //
                // Saving still happens here: SaveIfDirty writes to the world the queue was loaded for,
                // so a pending change is not held back until the next world is up, or lost on a quit.
                ResetSpeedEstimate();
                SaveIfDirty();
                return;
            }

            LoadForCurrentWorldIfNeeded();

            // Repairing markers walks the whole pin list, and players routinely have hundreds of pins,
            // so this is throttled. New waypoints still get their marker immediately, from Add().
            _pinTimer += Time.deltaTime;
            if (_pinTimer >= 0.5f)
            {
                _pinTimer = 0f;
                EnsurePins();
            }

            if (_queue.Count == 0)
            {
                ResetSpeedEstimate();
                SaveIfDirty();
                return;
            }

            Vector3 playerPos = player.transform.position;
            Waypoint active = _queue[0];
            float distance = HorizontalDistance(playerPos, active.Pos);

            UpdateSpeedEstimate(distance);

            float arrival = Plugin.ArrivalRadius.Value;
            if (arrival < 0.5f) arrival = 0.5f;

            float checkDistance = Plugin.Use3DDistance.Value
                ? Vector3.Distance(playerPos, ResolveAltitude(active, playerPos))
                : distance;

            // Armed once the player is genuinely away from it, reached when back within the radius - never while the
            // player is dead (the body lies where it fell for the seconds before the respawn).
            ArrivalStep step = ArrivalRules.Step(active.Armed, player.IsDead(), checkDistance, arrival);
            if (step == ArrivalStep.Arm) active.Armed = true;
            else if (step == ArrivalStep.Reach) OnReached(active);
            TraceStep(step, active, distance, checkDistance, arrival);

            SaveIfDirty();
        }

        private static void OnReached(Waypoint wp)
        {
            ReleasePin(wp);
            _queue.Remove(wp);
            _dirty = true;
            ResetSpeedEstimate();

            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "Location Reached", 0, null, false, false);

            Plugin.Log.LogInfo("Reached waypoint " + wp.DisplayName);

            Waypoint next = Active;
            if (next != null) Notify("Next waypoint: " + next.DisplayName);
        }

        /// <summary>Shows a short message in the top-left corner of the HUD, if the HUD exists.</summary>
        internal static void Notify(string text)
        {
            TraceHud(text);
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text, 0, null, false, false);
        }

        /// <summary>
        /// When the player did not supply an altitude we cannot know the target height, so the target is
        /// treated as being at the player own altitude. That keeps 3D distance from being nonsense.
        /// </summary>
        private static Vector3 ResolveAltitude(Waypoint wp, Vector3 playerPos)
        {
            if (wp.HasElevation && !wp.HeightIsEstimate) return wp.Pos;
            float ground;
            bool known = TryLoadedGroundHeight(wp.Pos, out ground);
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float y = ArrivalRules.TargetAltitude(wp.HasElevation, wp.HeightIsEstimate, wp.Pos.y, known, ground, water, playerPos.y);
            return new Vector3(wp.Pos.x, y, wp.Pos.z);
        }

        private static bool _groundWarned;

        /// <summary>
        /// The built terrain's height under a point where its zone is loaded near the player (Heightmap.GetHeight: the
        /// loaded heightmaps, terrain edits included; false where none covers the point). No numbers are logged:
        /// Wayfinder shows none.
        /// </summary>
        private static bool TryLoadedGroundHeight(Vector3 pos, out float height)
        {
            height = 0f;
            try
            {
                return Heightmap.GetHeight(pos, out height);
            }
            catch (Exception e)
            {
                // A heightmap joins the list before its heights are filled; never let that stop the tick.
                if (!_groundWarned)
                {
                    _groundWarned = true;
                    Plugin.Log.LogWarning("Ground height unavailable: " + e.Message);
                }
                height = 0f;
                return false;
            }
        }

        private static void UpdateSpeedEstimate(float distance)
        {
            _speedTimer += Time.deltaTime;
            if (_speedTimer < 0.5f) return;

            if (_lastDistance >= 0f)
            {
                float closing = (_lastDistance - distance) / _speedTimer;

                // A portal, a teleport or a world reload moves the player further in one sample than
                // any real travel could. Treat that as a discontinuity and restart the estimate rather
                // than letting it report an absurd speed for the next few seconds.
                if (Mathf.Abs(closing) > 40f)
                {
                    _smoothedSpeed = 0f;
                    _lastDistance = distance;
                    _speedTimer = 0f;
                    return;
                }

                _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, closing, 0.35f);
            }
            _lastDistance = distance;
            _speedTimer = 0f;
        }

        private static void ResetSpeedEstimate()
        {
            _lastDistance = -1f;
            _smoothedSpeed = 0f;
            _speedTimer = 0f;
        }

        // ---------------------------------------------------------------- markers

        /// <summary>Creates any missing markers and repairs references invalidated by a map rebuild.</summary>
        public static void EnsurePins()
        {
            Minimap mm = Minimap.instance;
            if (mm == null) return;

            // Minimap.Start has to have run before AddPin is safe to call.
            if (!MinimapAccess.CanAddPins(mm)) return;

            // Without the pin list a live marker cannot be told from a stale one, so every pass would add
            // another marker and orphan the last. No markers at all is the safe way to degrade.
            if (MinimapAccess.TryGetPins(mm) == null) return;

            Minimap.PinType type = Plugin.PinTypeSetting.Value;

            for (int i = 0; i < _queue.Count; i++)
            {
                Waypoint wp = _queue[i];

                if (wp.Pin != null && !MinimapAccess.PinIsAlive(mm, wp.Pin))
                {
                    TraceMarker(wp, ": its marker is no longer on the map; it is made again or found again");
                    wp.Pin = null;
                    wp.OwnsPin = false;
                }

                if (wp.Borrowed && (wp.Pin == null || wp.OwnsPin))
                {
                    // Following a pin of the player's. Whenever we are showing a stand-in (or nothing),
                    // look for their pin again - it reappears after a relog once the map data loads - whether or not
                    // the map hides it.
                    Minimap.PinData theirs = MinimapAccess.GetClosestAdoptablePinEvenHidden(mm, wp.Pos, AdoptRadius);
                    if (theirs != null)
                    {
                        if (wp.Pin != null) RemoveOwnMarker(mm, wp.Pin);   // retire the stand-in
                        wp.Pin = theirs;
                        wp.OwnsPin = false;
                        TraceMarker(wp, ": the followed pin is on the map again");
                        continue;
                    }
                }

                if (wp.Pin != null) continue;

                string label = string.IsNullOrEmpty(wp.Name)
                    ? Plugin.PinLabel.Value
                    : Plugin.PinLabel.Value + ": " + GameText.Localize(wp.Name);

                // Our own marker - or, for a borrowed waypoint whose pin isn't on this map, a stand-in.
                wp.Pin = CreateLocalOnlyPin(mm, wp.Pos, type, label);
                wp.OwnsPin = wp.Pin != null;
                TraceMarkerMade(wp);
                if (wp.Pin == null)
                    Plugin.WarnOnce(ref _markerWarned, "A waypoint's map marker could not be made", null,
                        "the game's map gave no pin, so that waypoint has no marker; the waypoint itself works");
            }
        }

        /// <summary>
        /// The ONLY place this mod creates a map marker, so that the local-only guarantee lives in
        /// exactly one line that can be reviewed at a glance.
        ///
        /// Waypoint markers must never reach another player. Valheim shares and saves pins through
        /// Minimap.GetSharedMapData (what the Cartography Table transmits) and Minimap.GetMapData
        /// (the player profile), and BOTH iterate only over pins where m_save is true. Passing
        /// save:false therefore keeps a marker out of the Cartography Table, out of the save file,
        /// and out of HavePinInRange/GetClosestPin, so it cannot interfere with a real shared pin.
        ///
        /// The flags are then re-asserted on the returned object rather than trusted: this stays
        /// correct even if a future game version changes what AddPin does with its arguments, or
        /// another mod patches AddPin. m_ownerID is pinned to 0 for the same reason - AddSharedMapData
        /// treats a non-zero owner as somebody else's pin and deletes it during a sync.
        /// </summary>
        private static Minimap.PinData CreateLocalOnlyPin(Minimap mm, Vector3 pos, Minimap.PinType type, string label)
        {
            Minimap.PinData pin = mm.AddPin(pos, type, label, false, false, 0L, default(Splatform.PlatformUserID));
            if (pin != null)
            {
                pin.m_save = false;     // never shared, never written to the profile
                pin.m_ownerID = 0L;     // never attributed to a player
            }
            return pin;
        }

        /// <summary>
        /// Removes the marker if we created it; leaves the player's own markers alone.
        /// A marker that is no longer on the current map (it belonged to a map that has since been
        /// rebuilt, e.g. after changing world) is simply forgotten - there is nothing left to remove,
        /// and handing the new map a pin it has never seen would be asking for trouble.
        /// </summary>
        private static void ReleasePin(Waypoint wp)
        {
            if (wp == null || wp.Pin == null) return;
            if (wp.OwnsPin) RemoveOwnMarker(Minimap.instance, wp.Pin);
            wp.Pin = null;
            wp.OwnsPin = false;
        }

        /// <summary>Removes a marker this mod created, if it is still on the current map.</summary>
        private static void RemoveOwnMarker(Minimap mm, Minimap.PinData pin)
        {
            if (mm == null || pin == null || !MinimapAccess.PinIsAlive(mm, pin)) return;
            try { mm.RemovePin(pin); }
            catch (Exception e) { Plugin.Log.LogWarning("RemovePin failed: " + e.Message); }
        }

        /// <summary>
        /// How far from a borrowed waypoint's recorded position the player's pin may be when it is looked
        /// up again. The position was copied from the pin itself, so it matches exactly unless the pin was
        /// moved; 1 m is the same tolerance Valheim uses to recognise a duplicate pin during a map sync.
        /// </summary>
        private const float AdoptRadius = 1f;

        /// <summary>
        /// Followed pins that are on the map right now; null when there are none. Filled into an array
        /// rather than a List of PinData, which preflight reserves for the map's own pin list.
        /// </summary>
        public static Minimap.PinData[] LiveBorrowedPins(Minimap mm)
        {
            int count = 0;
            for (int i = 0; i < _queue.Count; i++)
                if (IsLiveBorrowed(mm, _queue[i])) count++;
            if (count == 0) return null;

            Minimap.PinData[] live = new Minimap.PinData[count];
            int n = 0;
            for (int i = 0; i < _queue.Count && n < count; i++)
                if (IsLiveBorrowed(mm, _queue[i])) live[n++] = _queue[i].Pin;
            return live;
        }

        private static bool IsLiveBorrowed(Minimap mm, Waypoint wp)
        {
            return wp.Pin != null && !wp.OwnsPin && MinimapAccess.PinIsAlive(mm, wp.Pin);
        }

        /// <summary>Drops every marker we own, e.g. when the plugin unloads.</summary>
        public static void ReleaseAllPins()
        {
            for (int i = 0; i < _queue.Count; i++) ReleasePin(_queue[i]);
        }

        // ---------------------------------------------------------------- the log file

        private static bool _markerWarned;
        private static bool _worldUidWarned;
        private static bool _tracedPlayer, _tracedDead, _tracedMap;
        private static int _tracedMapMode = -1;

        /// <summary>Saved-route lines that could not be read are left out. Never quotes a line: it holds a position.</summary>
        private static void WarnUnreadableLines(long worldUid, int count)
        {
            try
            {
#if WAYFINDER
                Plugin.Log.LogWarning("Saved route of world " + worldUid.ToString(CultureInfo.InvariantCulture)
                    + ": some lines could not be read and are left out; the next save writes the route without them.");
#else
                Plugin.Log.LogWarning("Saved route of world " + worldUid.ToString(CultureInfo.InvariantCulture) + ": "
                    + count.ToString(CultureInfo.InvariantCulture) + " line(s) could not be read and are left out; the next "
                    + "save writes the route without them.");
#endif
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TracePresence(Player player, Minimap mm)
        {
            if (!Diag.On) return;
            try
            {
                bool hasPlayer = player != null;
                if (hasPlayer != _tracedPlayer)
                {
                    _tracedPlayer = hasPlayer;
                    Diag.Trace(hasPlayer
                        ? "local player appeared (world " + CurrentWorldUid().ToString(CultureInfo.InvariantCulture) + ", "
                          + Diag.N(_queue.Count) + " waypoint(s) queued" + (QueueBelongsToCurrentWorld ? ", this world's)" : ", not this world's yet)")
                        : "local player gone");
                }
                bool dead = hasPlayer && player.IsDead();
                if (dead != _tracedDead)
                {
                    _tracedDead = dead;
                    Diag.Trace(dead ? "player died: no arming or arrival until alive" : "player alive");
                }
                bool hasMap = mm != null;
                if (hasMap != _tracedMap)
                {
                    _tracedMap = hasMap;
                    Diag.Trace(hasMap ? "map present" : "no map");
                }
                int mode = hasMap ? (int)mm.m_mode : -1;
                if (mode != _tracedMapMode)
                {
                    _tracedMapMode = mode;
                    if (hasMap) Diag.Trace("map mode: " + mm.m_mode);
                }
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceStep(ArrivalStep step, Waypoint wp, float distance, float checkDistance, float radius)
        {
#if !WAYFINDER
            if (!Diag.On || step == ArrivalStep.None) return;
            try
            {
                string how = !Plugin.Use3DDistance.Value ? "horizontal"
                    : wp.HasElevation && !wp.HeightIsEstimate ? "3D, to its own height"
                    : wp.HeightIsEstimate ? "3D, to the loaded ground or the generator's estimate"
                    : "3D, to the loaded ground or the player's height";
                Diag.Trace((step == ArrivalStep.Arm ? "armed " : "reached ") + Diag.Wp(wp) + ": " + Diag.M(distance)
                    + " away, measured " + Diag.M(checkDistance) + " (" + how + "), radius " + Diag.M(radius));
            }
            catch (Exception) { }
#endif
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceWorld(long uid, bool worldChanged)
        {
            if (!Diag.On) return;
            try
            {
                Diag.Trace("world " + uid.ToString(CultureInfo.InvariantCulture)
                    + (worldChanged ? ": another world loaded; the last world's route was saved first" : ": loaded")
                    + (Plugin.PersistWaypoints.Value ? "; its saved route is read" : "; PersistWaypoints is off, its saved route is not read"));
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceRouteRead(long worldUid, int waypoints, int unreadable)
        {
            if (!Diag.On) return;
            try
            {
                Diag.Trace("route of world " + worldUid.ToString(CultureInfo.InvariantCulture) + " read: " + Diag.N(waypoints)
                    + " waypoint(s), " + Diag.N(unreadable) + " unreadable line(s)");
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceSaved()
        {
            if (!Diag.On) return;
            try
            {
                Diag.Trace("route of world " + _loadedWorldUid.ToString(CultureInfo.InvariantCulture) + " saved: "
                    + Diag.N(_queue.Count) + " waypoint(s)");
            }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceAdded(Vector3 pos, bool hasElevation)
        {
#if !WAYFINDER
            if (!Diag.On) return;
            try { Diag.Trace("added at " + CoordinateFormat.Format(pos, hasElevation) + (hasElevation ? " (with its height)" : " (no height)")); }
            catch (Exception) { }
#endif
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceFollowing(Minimap.PinData pin)
        {
#if !WAYFINDER
            if (!Diag.On || pin == null) return;
            try
            {
                // Copied first: "..." + pin.m_type compiles to ldflda with Roslyn, which preflight check 5 reads as a write.
                Minimap.PinType type = pin.m_type;
                bool shared = pin.m_ownerID != 0L;
                Vector3 at = pin.m_pos;
                Diag.Trace("following " + Diag.Pin(pin) + " (" + type + (shared ? ", another player's" : "") + ") at "
                    + CoordinateFormat.Format(at, Mathf.Abs(at.y) > 0.01f));
            }
            catch (Exception) { }
#endif
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceAddMany(int count, bool replace)
        {
#if !WAYFINDER
            if (!Diag.On) return;
            try
            {
                Diag.Trace("Find queues " + Diag.N(count) + " waypoint(s)" + (replace ? ", replacing the " : ", after the ")
                    + Diag.N(_queue.Count) + " queued");
            }
            catch (Exception) { }
#endif
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceHud(string text)
        {
            if (!Diag.On) return;
            try { Diag.Trace("on screen: " + text); }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceMarker(Waypoint wp, string what)
        {
            if (!Diag.On) return;
            try { Diag.Trace("marker of " + Diag.Wp(wp) + what); }
            catch (Exception) { }
        }

        [System.Diagnostics.Conditional("TOMTOM")]
        private static void TraceMarkerMade(Waypoint wp)
        {
            if (!Diag.On) return;
            try
            {
                Diag.Trace((wp.Pin == null ? "no marker could be made for " : wp.Borrowed ? "stand-in marker made for " : "marker made for ")
                    + Diag.Wp(wp));
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- geometry

        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float HorizontalSqrDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        // ---------------------------------------------------------------- persistence

        /// <summary>
        /// Each edition keeps its routes in its own folder, named after its plugin GUID. Sharing one
        /// would hand Wayfinder any coordinates typed into TomTom - enter them there, switch edition,
        /// and the no-coordinates rule would be sidestepped.
        /// </summary>
        private static string SaveDirectory()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, Edition.GUID);
        }

        private static string SavePath(long worldUid)
        {
            return Path.Combine(SaveDirectory(),
                string.Format(CultureInfo.InvariantCulture, "waypoints_{0}.txt", worldUid));
        }

        private static long CurrentWorldUid()
        {
            try
            {
                if (ZNet.instance != null) return ZNet.instance.GetWorldUID();
            }
            catch (Exception e)
            {
                Plugin.WarnOnce(ref _worldUidWarned, "The world's id could not be read", e,
                    "no route is loaded or saved until it can be");
            }
            return 0L;
        }

        /// <summary>
        /// Keeps the queue tied to the world it belongs to. This runs whether or not persistence is
        /// enabled, because waypoints must never follow the player into a different world - the
        /// coordinates would point at unrelated terrain.
        /// </summary>
        private static void LoadForCurrentWorldIfNeeded()
        {
            long uid = CurrentWorldUid();
            if (uid == 0L) return;
            if (_loadedForThisWorld && _loadedWorldUid == uid)
            {
                // A route that could not be read when this world loaded is tried again, after a growing delay.
                if (_read.Due(Time.unscaledTime) && Plugin.PersistWaypoints.Value) RetryRead(false);
                return;
            }

            bool worldChanged = _loadedForThisWorld && _loadedWorldUid != uid;
            if (worldChanged)
            {
                // Anything not yet written belongs to the world being left: write it there now, whatever
                // the retry delay says, before the queue is replaced by the next world's.
                SaveNow();
            }
            _loadedWorldUid = uid;
            _loadedForThisWorld = true;
            _saveFailures = 0;
            _nextSaveAttempt = 0f;
            _read.Reset();
            TraceWorld(uid, worldChanged);

            if (Plugin.PersistWaypoints.Value)
            {
                Load(uid);
                return;
            }
            // Not read, so not to be saved over: if PersistWaypoints is turned on in this world, the saved route is read
            // on the next tick and joined with what is queued by then, instead of the next change replacing it.
            _read.NotRead(Time.unscaledTime);
            if (worldChanged)
            {
                // Different world, nothing saved to restore: just drop the stale route.
                ReleaseAllPins();
                _queue.Clear();
                _dirty = false;   // the pending change belonged to the world just left, and persistence is off
                ResetSpeedEstimate();
                Diag.Trace("PersistWaypoints is off: the previous world's route is dropped from memory");
            }
        }

        private static void Load(long worldUid)
        {
            // Drop any markers we already own before replacing the queue, so none are orphaned on the map.
            ReleaseAllPins();
            _queue.Clear();
            _dirty = false;   // the queue now matches the file; nothing is written back
            List<Waypoint> restored = new List<Waypoint>();
            string recoveredFrom;
            if (ReadRoute(worldUid, restored, out recoveredFrom))
            {
                _queue.AddRange(restored);
                Plugin.Log.LogInfo(string.Format("Restored {0} waypoint(s) for world {1}{2}", _queue.Count, worldUid,
                    recoveredFrom != null ? " from " + recoveredFrom + ", left by an interrupted save" : ""));
            }
            else
            {
                // A route that is there but could not be read (another program - a backup, sync or antivirus tool - held
                // it) must not be replaced by the empty queue: nothing is saved until a later read succeeds (RetryRead),
                // and what the player queues meanwhile is kept, and put in front of it then.
                _read.Failed(Time.unscaledTime);
            }
        }

        /// <summary>
        /// Reads the world's saved route into <paramref name="into"/>: true when it was read or there is none yet, false
        /// when a file is there but could not be read (warned about once per world). Nothing is added unless the whole
        /// file was read.
        /// </summary>
        private static bool ReadRoute(long worldUid, List<Waypoint> into, out string recoveredFrom)
        {
            string path = SavePath(worldUid);
            // A save that was cut short leaves a copy beside the missing route file - complete, unless it was
            // that world's first save. It is renamed
            // back - never rewritten from memory, which could replace it with an empty list if it could not be
            // read - and read where it is when it cannot be moved right now.
            string survivor = SafeFile.ReadablePath(path);
            recoveredFrom = survivor != null && survivor != path ? Path.GetFileName(survivor) : null;
            string readPath = SafeFile.RecoverInterrupted(path);
            if (readPath == null) return true;   // nothing saved for this world yet
            try
            {
                string[] lines = File.ReadAllLines(readPath);
                int unreadable = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    RouteEntry entry;
                    if (RouteFile.TryParseLine(lines[i], out entry)) into.Add(FromEntry(entry));
                    else if (RouteFile.IsDataLine(lines[i])) unreadable++;
                }
                if (unreadable > 0) WarnUnreadableLines(worldUid, unreadable);
                TraceRouteRead(worldUid, into.Count, unreadable);
                return true;
            }
            catch (Exception e)
            {
                into.Clear();
                if (_read.Failures == 0)
                    Plugin.Log.LogWarning("Could not read saved waypoints: " + e.Message + " They are not saved over; "
                        + "reading them again every few seconds (at most every 30 s).");
                return false;
            }
        }

        /// <summary>
        /// Tries again to read a route that could not be read when its world loaded. Once it can be, its waypoints follow
        /// the ones queued meanwhile, which stay first so the arrow keeps its target (RouteMerge: a restored waypoint that
        /// matches a queued one is not added twice), and saving resumes. With lastChance (leaving the world, shutting
        /// down) no marker is made: the map may already be the next world's.
        /// </summary>
        private static void RetryRead(bool lastChance)
        {
            List<Waypoint> restored = new List<Waypoint>();
            string recoveredFrom;
            if (!ReadRoute(_loadedWorldUid, restored, out recoveredFrom))
            {
                _read.Failed(Time.unscaledTime);
                return;
            }
            int attempt = _read.Failures + 1;
            int meanwhile = _queue.Count;
            List<int> append = RouteMerge.ToAppend(Entries(_queue), Entries(restored));
            for (int i = 0; i < append.Count; i++) _queue.Add(restored[append[i]]);
            _read.Succeeded();
            // With nothing queued meanwhile the queue is the file again; otherwise the merged route is saved.
            _dirty = meanwhile > 0;
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Read the saved waypoints of world {0} at attempt {1}: {2} restored{3}{4}.", _loadedWorldUid, attempt,
                append.Count,
                meanwhile > 0 ? string.Format(CultureInfo.InvariantCulture, ", after the {0} queued meanwhile", meanwhile) : "",
                recoveredFrom != null ? " (from " + recoveredFrom + ", left by an interrupted save)" : ""));
            if (!lastChance) EnsurePins();
        }

        /// <summary>The queue as route entries, for RouteMerge. Kept apart from any text: preflight's readout scan.</summary>
        private static List<RouteEntry> Entries(List<Waypoint> waypoints)
        {
            List<RouteEntry> list = new List<RouteEntry>(waypoints.Count);
            for (int i = 0; i < waypoints.Count; i++) list.Add(ToEntry(waypoints[i]));
            return list;
        }

        private static RouteEntry ToEntry(Waypoint wp)
        {
            RouteEntry e = new RouteEntry();
            e.X = wp.Pos.x; e.Y = wp.Pos.y; e.Z = wp.Pos.z;
            e.HasElevation = wp.HasElevation;
            e.HeightIsEstimate = wp.HeightIsEstimate;
            e.Borrowed = wp.Borrowed;
            e.Name = wp.Name;
            return e;
        }

        private static Waypoint FromEntry(RouteEntry e)
        {
            Waypoint wp = new Waypoint();
            wp.Name = e.Name;
            wp.Pos = new Vector3(e.X, e.Y, e.Z);
            wp.HasElevation = e.HasElevation;
            wp.HeightIsEstimate = e.HeightIsEstimate;
            // The file's ownsMarker column records intent: a followed pin is found again by EnsurePins rather than
            // getting a marker of its own.
            wp.Borrowed = e.Borrowed;
            wp.OwnsPin = false;
            return wp;
        }

        public static void SaveIfDirty()
        {
            if (!_dirty) return;
            if (!Plugin.PersistWaypoints.Value) return;

            // The queue is written to the world it was loaded for - never to whatever world happens to be
            // current, which during a world change is already the next one. The dirty flag is only cleared
            // once the write succeeds; a failed write is retried after a growing delay.
            if (!_loadedForThisWorld || _loadedWorldUid == 0L) return;

            // A route that could not be read is never written over. What changed meanwhile stays in the queue, and is
            // saved together with it once it has been read (RetryRead).
            if (_read.Pending)
            {
                if (_read.WarnSaveRefused())
                    Plugin.Log.LogWarning("Waypoints not saved: the saved route of world "
                        + _loadedWorldUid.ToString(CultureInfo.InvariantCulture) + " could not be read yet, and is not "
                        + "written over. Changes are kept, and saved with it once it can be read.");
                return;
            }
            if (Time.unscaledTime < _nextSaveAttempt) return;

            try
            {
                Directory.CreateDirectory(SaveDirectory());
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(RouteFile.Header(Edition.Name));
                for (int i = 0; i < _queue.Count; i++) sb.AppendLine(RouteFile.FormatLine(ToEntry(_queue[i])));
                SafeFile.WriteAllText(SavePath(_loadedWorldUid), sb.ToString());
                _dirty = false;
                TraceSaved();
                if (_saveFailures > 0)
                    Plugin.Log.LogInfo("Waypoints saved after " + _saveFailures.ToString(CultureInfo.InvariantCulture)
                        + " failed attempt(s).");
                _saveFailures = 0;
            }
            catch (Exception e)
            {
                _saveFailures++;
                _nextSaveAttempt = Time.unscaledTime + Mathf.Min(2f * _saveFailures, 30f);
                if (_saveFailures <= 3)
                    Plugin.Log.LogWarning("Could not save waypoints: " + e.Message);
                else if (_saveFailures == 4)
                    Plugin.Log.LogWarning("Could not save waypoints: still failing; retrying every few seconds "
                        + "(at most every 30 s) without further warnings.");
            }
        }

        /// <summary>
        /// Saves now if anything changed, ignoring the retry delay - for the moments with no later chance:
        /// leaving a world and shutting down.
        /// </summary>
        public static void SaveNow()
        {
            // A last try at a route that could not be read, so what was queued meanwhile can be saved with it.
            if (_read.Pending && Plugin.PersistWaypoints.Value && _loadedForThisWorld && _loadedWorldUid != 0L)
                RetryRead(true);
            _nextSaveAttempt = 0f;
            SaveIfDirty();
            // Both callers are about to replace or drop the queue, so an unsaved change is gone for good.
            if (_dirty && Plugin.PersistWaypoints.Value && _loadedForThisWorld && _loadedWorldUid != 0L)
                Plugin.Log.LogWarning(_read.Pending
                    ? "The saved route of world " + _loadedWorldUid.ToString(CultureInfo.InvariantCulture) + " could still "
                        + "not be read. It is left as it was; the waypoints changed since it was loaded are not saved."
                    : "Could not save the waypoints of world "
                        + _loadedWorldUid.ToString(CultureInfo.InvariantCulture) + "; the last change is lost.");
        }

    }
}
