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
            return wp;
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
            if (index < 0 || index >= _queue.Count) return false;
            Waypoint wp = _queue[index];
            ReleasePin(wp);
            _queue.RemoveAt(index);
            _dirty = true;
            ResetSpeedEstimate();
            return true;
        }

        public static bool Remove(Waypoint wp)
        {
            return RemoveAt(_queue.IndexOf(wp));
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
            return true;
        }

        public static void Clear()
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
            return true;
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
            return RemoveAt(0);
        }

        // ---------------------------------------------------------------- per-frame

        public static void Tick()
        {
            Player player = Player.m_localPlayer;
            Minimap mm = Minimap.instance;

            if (player == null || mm == null)
            {
                // Nothing to do without a player and a map - and deliberately nothing is reset here.
                //
                // The local player is destroyed and recreated on every death while the map, and our
                // markers on it, survive. Forgetting the marker references and reloading the queue at
                // that point (as an earlier version did) made every respawn create a second set of
                // markers and orphan the first, where neither this mod nor vanilla's right-click could
                // remove them. Seen in a live session log: "Local player destroyed" followed by a reload.
                //
                // A genuine change of world is detected by world UID in LoadForCurrentWorldIfNeeded,
                // and any marker that did not survive a map rebuild is recreated by EnsurePins.
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

            if (!active.Armed)
            {
                // Arm once, as soon as the player is genuinely away from it.
                if (checkDistance > arrival) active.Armed = true;
            }
            else if (checkDistance <= arrival)
            {
                OnReached(active);
            }

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
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text, 0, null, false, false);
        }

        /// <summary>
        /// When the player did not supply an altitude we cannot know the target height, so the target is
        /// treated as being at the player own altitude. That keeps 3D distance from being nonsense.
        /// </summary>
        private static Vector3 ResolveAltitude(Waypoint wp, Vector3 playerPos)
        {
            if (wp.HasElevation) return wp.Pos;
            return new Vector3(wp.Pos.x, playerPos.y, wp.Pos.z);
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
                    wp.Pin = null;
                    wp.OwnsPin = false;
                }

                if (wp.Borrowed && (wp.Pin == null || wp.OwnsPin))
                {
                    // Following a pin of the player's. Whenever we are showing a stand-in (or nothing),
                    // look for their pin again - it reappears after a relog once the map data loads.
                    Minimap.PinData theirs = MinimapAccess.GetClosestAdoptablePin(mm, wp.Pos, AdoptRadius);
                    if (theirs != null)
                    {
                        if (wp.Pin != null) RemoveOwnMarker(mm, wp.Pin);   // retire the stand-in
                        wp.Pin = theirs;
                        wp.OwnsPin = false;
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

        /// <summary>Drops every marker we own, e.g. when the plugin unloads.</summary>
        public static void ReleaseAllPins()
        {
            for (int i = 0; i < _queue.Count; i++) ReleasePin(_queue[i]);
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
            catch { }
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
            if (_loadedForThisWorld && _loadedWorldUid == uid) return;

            bool worldChanged = _loadedForThisWorld && _loadedWorldUid != uid;
            _loadedWorldUid = uid;
            _loadedForThisWorld = true;

            if (Plugin.PersistWaypoints.Value)
            {
                Load(uid);
            }
            else if (worldChanged)
            {
                // Different world, nothing saved to restore: just drop the stale route.
                ReleaseAllPins();
                _queue.Clear();
                ResetSpeedEstimate();
            }
        }

        private static void Load(long worldUid)
        {
            // Drop any markers we already own before replacing the queue, so none are orphaned on the map.
            ReleaseAllPins();
            _queue.Clear();
            string path = SavePath(worldUid);
            try
            {
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    // x|altitude|z|hasElevation|ownsMarker|name   (older files omit ownsMarker)
                    string[] parts = line.Split('|');
                    if (parts.Length < 4) continue;

                    float x, y, z;
                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;

                    bool hasElev = parts[3] == "1";

                    bool owns = true;
                    string name = "";
                    if (parts.Length >= 6)
                    {
                        owns = parts[4] == "1";
                        name = parts[5];
                    }
                    else if (parts.Length == 5)
                    {
                        name = parts[4];
                    }

                    Waypoint wp = new Waypoint();
                    wp.Name = name;
                    wp.Pos = new Vector3(x, y, z);
                    wp.HasElevation = hasElev;
                    // The file's ownsMarker column records intent: 0 means the waypoint follows one of
                    // the player's pins, and EnsurePins finds that pin again rather than adding a marker.
                    wp.Borrowed = !owns;
                    wp.OwnsPin = false;
                    _queue.Add(wp);
                }
                Plugin.Log.LogInfo(string.Format("Restored {0} waypoint(s) for world {1}", _queue.Count, worldUid));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read saved waypoints: " + e.Message);
            }
            _dirty = false;
        }

        public static void SaveIfDirty()
        {
            if (!_dirty) return;
            if (!Plugin.PersistWaypoints.Value) return;

            // The dirty flag is only cleared once the write actually succeeds, so a failure here
            // (or a world that is not ready yet) means we try again rather than losing the route.
            long uid = CurrentWorldUid();
            if (uid == 0L) return;

            try
            {
                Directory.CreateDirectory(SaveDirectory());
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# " + Edition.Name + " queue - x|altitude|z|hasElevation|ownsMarker|name");
                for (int i = 0; i < _queue.Count; i++)
                {
                    Waypoint wp = _queue[i];
                    // ownsMarker records intent (Borrowed), never the runtime stand-in state, so a
                    // followed pin that was briefly missing is still followed after the next load.
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}",
                        wp.Pos.x, wp.Pos.y, wp.Pos.z,
                        wp.HasElevation ? "1" : "0",
                        wp.Borrowed ? "0" : "1",
                        wp.Name == null ? "" : wp.Name.Replace("|", " ")));
                }
                File.WriteAllText(SavePath(uid), sb.ToString());
                _dirty = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save waypoints: " + e.Message);
            }
        }
    }
}
