using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// Finds every place of one kind (a SearchQuery) within a range of the player and queues them as waypoints,
    /// in an optimised walking route that starts at the nearest one.
    ///
    /// Where the places come from:
    /// - As the server (a host or single player) the game's own list of location instances, which knows which
    ///   unique candidate has been placed (ZoneSystem.GetLocationList).
    /// - As a client, the server is asked, one location type at a time and paced, with the same request a Vegvisir
    ///   makes (RPC_DiscoverClosestLocation with discoverAll). Vanilla would turn every answer into a saved map pin -
    ///   the kind a Cartography Table shares - so Game_RPC_DiscoverLocationResponse_Patch catches this mod's answers
    ///   first, and no request is ever sent unless that interception is confirmed active (AnswersIntercepted). A
    ///   final request with exactly one answer (the closest StartTemple) marks the end of the answers, since routed
    ///   calls keep their order.
    /// - World objects (Mysterious Rocks) from the ZDOs the game holds: everything generated, on a server; only what
    ///   is loaded near the player, on a client.
    ///
    /// Work that grows with the world (reading objects zone by zone) runs a little each frame, within FrameBudgetMs,
    /// so a large search takes longer rather than making the game stutter. TomTom logs how long each search took.
    /// Wayfinder keeps only places whose centre is explored on the map (by the player or a Cartography Table) and
    /// unique places that are already fixed, and says nothing about what it found - no message, no count, no log
    /// line (only a failure, such as a server that did not answer in time, is logged).
    /// </summary>
    internal static class LocationSearch
    {
        private const int SentinelType = int.MinValue;
        private const string SentinelLocation = "StartTemple";
        private const float RequestSpacing = 0.2f;
        private const float AnswerTimeout = 15f;
        private const double FrameBudgetMs = 1.5;
        private const double RouteBudgetMs = 5.0;
        private const float RockClearingRadius = 40f;

        private enum Phase { Idle, Asking, Scanning }

        private static Phase _phase = Phase.Idle;
        private static SearchQuery _query;
        private static bool _replace;
        private static float _range;
        private static Vector3 _origin;
        private static bool _onServer;
        private static readonly List<SearchHit> _hits = new List<SearchHit>();
        private static int _searchId;
        private static string _token = SearchRules.RequestPinName(0);
        private static int _nextAsk;
        private static float _nextAskTime;
        private static float _deadline;
        private static bool _allAnswered;
        private static IEnumerator _scan;

        private static readonly Stopwatch _frame = new Stopwatch();
        private static readonly Stopwatch _elapsed = new Stopwatch();
        private static double _workMs;
        private static int _workFrames;
        private static int _answers;
        private static int _objectsRead;
#if !WAYFINDER
        private static int _chestPlacesChecked;
        private static int _chestPlacesSkipped;
        private static HashSet<int> _lootChestPrefabs;
#endif

        private static MethodInfo _responseMethod;

        public static bool Busy { get { return _phase != Phase.Idle; } }

        /// <summary>Starts a search; false when one is already running or the player is not in a world.</summary>
        public static bool Start(SearchQuery query, float range, bool replace)
        {
            if (query == null || Busy) return false;
            Player player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null) return false;
            if (!WaypointManager.QueueBelongsToCurrentWorld) return false;

            _query = query;
            _range = range;
            _replace = replace;
            _origin = player.transform.position;
            _hits.Clear();
            _searchId++;
            _token = SearchRules.RequestPinName(_searchId);
            _answers = _objectsRead = 0;
#if !WAYFINDER
            _chestPlacesChecked = _chestPlacesSkipped = 0;
#endif
            _workMs = 0;
            _workFrames = 0;
            _elapsed.Reset();
            _elapsed.Start();
            _onServer = ZNet.instance.IsServer();

            if (_onServer)
            {
                CollectOnServer();
                BeginScan();
                return true;
            }

            if (!AnswersIntercepted())
            {
                // Fail closed: without the interception every answer would become a saved, shareable pin.
                Plugin.Log.LogWarning("Location search is unavailable here: its safety patch on Game.RPC_DiscoverLocationResponse is not active.");
#if !WAYFINDER
                WaypointWindow.SetStatus("Search is unavailable - see BepInEx/LogOutput.log.");
#endif
                _phase = Phase.Idle;
                return false;
            }
            _phase = Phase.Asking;
            _nextAsk = 0;
            _nextAskTime = 0f;
            _deadline = float.MaxValue;
            _allAnswered = false;
#if !WAYFINDER
            WaypointWindow.SetStatus("Searching - asking the server...");
#endif
            return true;
        }

        /// <summary>Stops a running search without placing anything (death, world change, plugin unload).</summary>
        public static void Cancel()
        {
#if !WAYFINDER
            if (_phase != Phase.Idle) WaypointWindow.SetStatus("Search stopped.");
#endif
            _phase = Phase.Idle;
            _scan = null;
            _hits.Clear();
        }

        /// <summary>Called every frame from Plugin.Update, in the same guarded block as the waypoint tick.</summary>
        public static void Tick()
        {
            if (_phase == Phase.Idle) return;
            if (Player.m_localPlayer == null || !WaypointManager.QueueBelongsToCurrentWorld)
            {
                Cancel();
                return;
            }

            _frame.Reset();
            _frame.Start();
            try
            {
                if (_phase == Phase.Asking) TickAsking();
                else if (_phase == Phase.Scanning) TickScanning();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Location search failed: " + e);
                Cancel();
#if !WAYFINDER
                WaypointWindow.SetStatus("Search failed - see BepInEx/LogOutput.log.");
#endif
            }
            finally
            {
                _frame.Stop();
            }
        }

        // ---------------------------------------------------------------- sources

        private static void CollectOnServer()
        {
            Dictionary<string, int> wanted = LocationIndex(_query);
            Dictionary<ZoneSystem.ZoneLocation, int> byLocation = new Dictionary<ZoneSystem.ZoneLocation, int>();
            foreach (ZoneSystem.LocationInstance li in ZoneSystem.instance.GetLocationList())
            {
                ZoneSystem.ZoneLocation loc = li.m_location;
                if (loc == null) continue;
                int index;
                if (!byLocation.TryGetValue(loc, out index))
                {
                    // m_prefabName equals the name the game derives from the prefab for every location that runs
                    // (checked against Valheim 1.0.16's own data), and reading it costs nothing.
                    if (loc.m_prefabName == null || !wanted.TryGetValue(loc.m_prefabName, out index)) index = -1;
                    byLocation[loc] = index;
                }
                if (index < 0) continue;
                AddLocationHit(index, li.m_position, li.m_placed);
            }
            // Unique places are resolved over every candidate first: the real one may lie outside the range.
            SearchRules.ResolveUniqueOnServer(_hits);
            SearchRules.KeepWithinRange(_hits, _origin.x, _origin.z, _range);
        }

        private static void TickAsking()
        {
            float now = Time.unscaledTime;
            int types = _query.Locations.Length;
            if (_nextAsk <= types && now >= _nextAskTime)
            {
                if (!Ask(_nextAsk))
                {
                    Cancel();
#if !WAYFINDER
                    WaypointWindow.SetStatus("Search is unavailable - see BepInEx/LogOutput.log.");
#endif
                    return;
                }
                _nextAsk++;
                _nextAskTime = now + RequestSpacing;
                if (_nextAsk > types) _deadline = now + AnswerTimeout;
            }

            if (!_allAnswered && now <= _deadline) return;

            bool complete = _allAnswered;
            // Unique places are resolved over every answer first: counting only those in range would take the
            // one candidate that happens to be near for the real place. With answers missing, a lone one is not proof.
            SearchRules.ResolveUniqueOnClient(_hits, PlacedIcons(), complete);
            SearchRules.KeepWithinRange(_hits, _origin.x, _origin.z, _range);
            if (!complete)
                Plugin.Log.LogWarning("Location search: the server did not answer every request in time; using what arrived.");
            BeginScan();
        }

        /// <summary>
        /// Sends one request: location type <paramref name="index"/>, or the end marker when index is past the
        /// last type. Refuses (false) unless this mod's interception of the answers is active.
        /// </summary>
        private static bool Ask(int index)
        {
            if (!AnswersIntercepted()) return false;
            bool sentinel = index >= _query.Locations.Length;
            string location = sentinel ? SentinelLocation : _query.Locations[index].Prefab;
            int pinType = sentinel ? SentinelType : index;
            ZRoutedRpc.instance.InvokeRoutedRPC("RPC_DiscoverClosestLocation",
                location, _origin, _token, pinType, false, !sentinel);
            return true;
        }

        /// <summary>
        /// Called by the prefix on Game.RPC_DiscoverLocationResponse with every location answer. Takes the ones meant
        /// for the running search and ignores everything else. Whether vanilla then handles the answer is not decided
        /// here but by SearchRules.VanillaMayHandle, from the pin name alone.
        /// </summary>
        internal static void OnServerAnswer(string pinName, int pinType, Vector3 pos)
        {
            if (SearchRules.VanillaMayHandle(pinName)) return;
            if (_phase != Phase.Asking || !string.Equals(pinName, _token, StringComparison.Ordinal)) return;
            _answers++;
            if (pinType == SentinelType)
            {
                _allAnswered = true;
                return;
            }
            if (pinType >= 0 && pinType < _query.Locations.Length)
                AddLocationHit(pinType, pos, false);
        }

        /// <summary>True when the prefix that keeps the server's answers from becoming pins is patched in.</summary>
        internal static bool AnswersIntercepted()
        {
            // One flag, set only from SearchRules.IsOurPrefix or to false, and one return (preflight checks both).
            bool patched = false;
            try
            {
                if (_responseMethod == null)
                    _responseMethod = AccessTools.Method(typeof(Game), "RPC_DiscoverLocationResponse",
                        new Type[] { typeof(long), typeof(string), typeof(int), typeof(Vector3), typeof(bool) });
                Patches info = _responseMethod != null ? Harmony.GetPatchInfo(_responseMethod) : null;
                if (info != null)
                {
                    string ours = typeof(Game_RPC_DiscoverLocationResponse_Patch).FullName;
                    foreach (Patch p in info.Prefixes)
                    {
                        string declaring = p.PatchMethod != null && p.PatchMethod.DeclaringType != null ? p.PatchMethod.DeclaringType.FullName : null;
                        patched = SearchRules.IsOurPrefix(p.owner, declaring, Plugin.GUID, ours);
                        if (patched) break;
                    }
                }
            }
            catch (Exception e)
            {
                patched = false;
                Plugin.Log.LogWarning("Could not confirm the location-answer patch: " + e.Message);
            }
            return patched;
        }

        /// <summary>Unique locations with a map icon the server has sent (the merchants, once placed), by prefab.</summary>
        private static Dictionary<string, float[]> PlacedIcons()
        {
            Dictionary<string, float[]> result = new Dictionary<string, float[]>();
            Dictionary<Vector3, string> icons = new Dictionary<Vector3, string>();
            ZoneSystem.instance.GetLocationIcons(icons);
            foreach (KeyValuePair<Vector3, string> kv in icons)
                if (kv.Value != null && SearchCatalog.IsUnique(kv.Value))
                    result[kv.Value] = new float[] { kv.Key.x, kv.Key.y, kv.Key.z };
            return result;
        }

        private static void AddLocationHit(int index, Vector3 pos, bool placed)
        {
            SearchTarget t = _query.Locations[index];
            SearchHit h = new SearchHit();
            h.Prefab = t.Prefab;
            h.Label = t.Label;
            h.X = pos.x; h.Y = pos.y; h.Z = pos.z;
            h.Placed = placed;
            _hits.Add(h);
        }

        private static Dictionary<string, int> LocationIndex(SearchQuery q)
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < q.Locations.Length; i++) d[q.Locations[i].Prefab] = i;
            return d;
        }

        // ---------------------------------------------------------------- the time-sliced part

        private static void BeginScan()
        {
            _phase = Phase.Scanning;
            _scan = Scan();
#if !WAYFINDER
            WaypointWindow.SetStatus("Searching...");
#endif
        }

        private static void TickScanning()
        {
            bool more = _scan.MoveNext();
            _workMs += _frame.Elapsed.TotalMilliseconds;
            _workFrames++;
            if (!more) Finish();
        }

        private static bool OverBudget()
        {
            return _frame.Elapsed.TotalMilliseconds > FrameBudgetMs;
        }

        private static IEnumerator Scan()
        {
            SimulationDistance oneZone = new SimulationDistance(0, 0, false);
            List<ZDO> zdos = new List<ZDO>();

            // World objects within range (the scattered Mysterious Rocks).
            for (int t = 0; t < _query.Objects.Length; t++)
            {
                SearchTarget target = _query.Objects[t];
                int hash = target.Prefab.GetStableHashCode();
                Vector2s centre = ZoneSystem.GetZone(_origin);
                int n = Mathf.CeilToInt(_range / 64f) + 1;
                for (int sy = centre.y - n; sy <= centre.y + n; sy++)
                {
                    for (int sx = centre.x - n; sx <= centre.x + n; sx++)
                    {
                        if (!InsideWorldGrid(sx, sy) || ZoneDistance(sx, sy) > _range) continue;
                        zdos.Clear();
                        ZDOMan.instance.FindSectorObjects(new Vector2s(sx, sy), oneZone, zdos, null);
                        for (int i = 0; i < zdos.Count; i++)
                        {
                            ZDO zdo = zdos[i];
                            _objectsRead++;
                            if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != hash) continue;
                            Vector3 p = zdo.GetPosition();
                            float dx = p.x - _origin.x, dz = p.z - _origin.z;
                            if (dx * dx + dz * dz > _range * _range) continue;
                            if (IsPicked(zdo, target.Prefab)) continue;
                            SearchHit h = new SearchHit();
                            h.Prefab = target.Prefab;
                            h.Label = target.Label;
                            h.X = p.x; h.Y = p.y; h.Z = p.z;
                            h.IsObject = true;
                            h.Placed = true;
                            _hits.Add(h);
                        }
                        if (OverBudget()) yield return null;
                    }
                }
            }

#if !WAYFINDER
            // Places whose chests have all been filled already, and hold none of the items any more. On the server
            // only, which holds every generated chest (a client holds only those near it, so an unseen chest with the
            // item could not stop the skip), and only for placed locations: a place not generated yet has no chest of
            // its own, and a neighbour's chest must not decide for it.
            if (_onServer && Plugin.SkipCheckedChests.Value && _query.Items.Length > 0 && _hits.Count > 0)
            {
                if (_lootChestPrefabs == null)
                {
                    HashSet<int> found = new HashSet<int>();
                    List<GameObject> prefabs = ZNetScene.instance.m_prefabs;
                    for (int i = 0; i < prefabs.Count; i++)
                    {
                        GameObject go = prefabs[i];
                        if (go == null) continue;
                        Container c = go.GetComponent<Container>();
                        if (c != null && c.m_defaultItems != null && c.m_defaultItems.m_drops != null && c.m_defaultItems.m_drops.Count > 0)
                            found.Add(go.name.GetStableHashCode());
                        if ((i & 63) == 0 && OverBudget()) yield return null;
                    }
                    _lootChestPrefabs = found;
                }

                List<string> itemNames = new List<string>();
                for (int i = 0; i < _query.Items.Length; i++)
                {
                    GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(_query.Items[i]) : null;
                    ItemDrop drop = item != null ? item.GetComponent<ItemDrop>() : null;
                    if (drop != null) itemNames.Add(drop.m_itemData.m_shared.m_name);
                }

                if (itemNames.Count > 0)
                {
                    Inventory scratch = new Inventory("WaypointerChestCheck", null, 8, 8);
                    for (int h = _hits.Count - 1; h >= 0; h--)
                    {
                        SearchHit hit = _hits[h];
                        if (hit.IsObject || hit.Possible || !hit.Placed) continue;
                        float radius = SearchCatalog.WideLocation(hit.Prefab) ? 96f : 64f;
                        int n = radius > 64f ? 2 : 1;
                        Vector2s centre = ZoneSystem.GetZone(new Vector3(hit.X, 0f, hit.Z));
                        int chests = 0, unfilled = 0, holding = 0;
                        for (int sy = centre.y - n; sy <= centre.y + n; sy++)
                        {
                            for (int sx = centre.x - n; sx <= centre.x + n; sx++)
                            {
                                if (!InsideWorldGrid(sx, sy)) continue;
                                zdos.Clear();
                                ZDOMan.instance.FindSectorObjects(new Vector2s(sx, sy), oneZone, zdos, null);
                                for (int i = 0; i < zdos.Count; i++)
                                {
                                    ZDO zdo = zdos[i];
                                    _objectsRead++;
                                    if (zdo == null || !zdo.IsValid() || !_lootChestPrefabs.Contains(zdo.GetPrefab())) continue;
                                    Vector3 p = zdo.GetPosition();
                                    float dx = p.x - hit.X, dz = p.z - hit.Z;
                                    if (dx * dx + dz * dz > radius * radius) continue;
                                    chests++;
                                    if (!zdo.GetBool(ZDOVars.s_addedDefaultItems, false)) { unfilled++; continue; }
                                    // A chest keeps its items as a byte array (Container.Save / Container.Load on
                                    // 1.0.16); none means empty.
                                    byte[] data = zdo.GetByteArray(ZDOVars.s_items, null);
                                    if (data != null && data.Length > 0)
                                    {
                                        scratch.Load(new ZPackage(data));
                                        for (int k = 0; k < itemNames.Count; k++)
                                            if (scratch.ContainsItemByName(itemNames[k])) { holding++; break; }
                                    }
                                    // Loading a chest makes and destroys an object per item, so the budget is
                                    // checked after every chest, not only after every zone.
                                    if (OverBudget()) yield return null;
                                }
                                if (OverBudget()) yield return null;
                            }
                        }
                        _chestPlacesChecked++;
                        if (SearchRules.ChestsExhausted(chests, unfilled, holding))
                        {
                            _hits.RemoveAt(h);
                            _chestPlacesSkipped++;
                        }
                    }
                }
            }
#endif
            yield break;
        }

        /// <summary>Keeps the <paramref name="keep"/> hits nearest the search origin (horizontally).</summary>
        private static void KeepNearest(int keep)
        {
            if (_hits.Count <= keep) return;
            float[] keys = new float[_hits.Count];
            SearchHit[] items = new SearchHit[_hits.Count];
            for (int i = 0; i < _hits.Count; i++)
            {
                float dx = _hits[i].X - _origin.x, dz = _hits[i].Z - _origin.z;
                keys[i] = dx * dx + dz * dz;
                items[i] = _hits[i];
            }
            Array.Sort(keys, items);
            _hits.Clear();
            for (int i = 0; i < keep; i++) _hits.Add(items[i]);
        }

        private static bool IsPicked(ZDO zdo, string prefab)
        {
            if (!zdo.GetBool(ZDOVars.s_picked, false)) return false;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            Pickable pickable = go != null ? go.GetComponent<Pickable>() : null;
            if (pickable == null || pickable.m_respawnTimeMinutes <= 0f) return true;
            // A pickable that grows back counts as available once its time is up, even if nobody has been near.
            DateTime pickedAt = new DateTime(zdo.GetLong(ZDOVars.s_pickedTime, 0L));
            return (ZNet.instance.GetTime() - pickedAt).TotalMinutes <= pickable.m_respawnTimeMinutes;
        }

        private static bool InsideWorldGrid(int sx, int sy)
        {
            // ZoneSystem.SectorToIndex maps sectors outside -256..255 to index 0, a catch-all bucket.
            return sx > -256 && sx < 256 && sy > -256 && sy < 256;
        }

        /// <summary>Horizontal distance from the search origin to the nearest point of a 64 m zone.</summary>
        private static float ZoneDistance(int sx, int sy)
        {
            float minX = sx * 64f - 32f, maxX = sx * 64f + 32f;
            float minZ = sy * 64f - 32f, maxZ = sy * 64f + 32f;
            float dx = Mathf.Max(0f, Mathf.Max(minX - _origin.x, _origin.x - maxX));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - _origin.z, _origin.z - maxZ));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ---------------------------------------------------------------- the result

        private static void Finish()
        {
            _phase = Phase.Idle;
            _scan = null;
            _elapsed.Stop();

#if WAYFINDER
            // Only what the map already shows: a place whose centre is explored (by the player, or shared by a
            // Cartography Table), and a unique place only once it is fixed.
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                SearchHit h = _hits[i];
                if (h.Possible || !MinimapAccess.IsExplored(new Vector3(h.X, h.Y, h.Z))) _hits.RemoveAt(i);
            }
#endif
            SearchRules.DropObjectsNear(_hits, "BigRockClearing", RockClearingRadius);

            int count = _hits.Count;
            int possible = 0;
            for (int i = 0; i < _hits.Count; i++) if (_hits[i].Possible) possible++;
            int placed = 0;
            if (count > 0)
            {
                // The route is cut to MaxSearchWaypoints stops anyway, so it is planned over the nearest few hundred
                // places only: planning thousands would stall a frame (10 ms for 2,000, 130 ms for 10,000 on Mono).
                int cap = Plugin.MaxSearchWaypoints.Value;
                KeepNearest(Math.Max(4 * cap, 100));
                int n = _hits.Count;
                float[] xs = new float[n], zs = new float[n];
                for (int i = 0; i < n; i++) { xs[i] = _hits[i].X; zs[i] = _hits[i].Z; }
                int[] order = RoutePlanner.Plan(_origin.x, _origin.z, xs, zs, n, cap, RouteBudgetMs);
                List<Vector3> positions = new List<Vector3>(order.Length);
                List<string> names = new List<string>(order.Length);
                for (int i = 0; i < order.Length; i++)
                {
                    SearchHit h = _hits[order[i]];
                    positions.Add(new Vector3(h.X, h.Y, h.Z));
                    names.Add(SearchRules.WaypointName(h, _query));
                }
                placed = WaypointManager.AddMany(positions, names, _replace);
            }

#if !WAYFINDER
            string what = count == 0
                ? string.Format(CultureInfo.InvariantCulture, "Nothing found within {0:0} m.", _range)
                : string.Format(CultureInfo.InvariantCulture, "Found {0} place{1}{2}{3}.", count, count == 1 ? "" : "s",
                    placed < count ? string.Format(CultureInfo.InvariantCulture, " - queued the first {0} of the route", placed) : "",
                    possible > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0} possible spot{1})", possible, possible == 1 ? "" : "s") : "");
            WaypointWindow.SetStatus(what);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Search '{0}' within {1:0} m: {2} found, {3} queued, {4} possible; {5}; {6} objects read, {7} places' chests checked, {8} skipped; {9:0.0} ms of work over {10} frames, {11:0.00} s in all.",
                _query.Name, _range, count, placed, possible,
                _onServer ? "read the server's own list" : string.Format(CultureInfo.InvariantCulture, "{0} answers from the server", _answers),
                _objectsRead, _chestPlacesChecked, _chestPlacesSkipped, _workMs, _workFrames, _elapsed.Elapsed.TotalSeconds));
#endif
            _hits.Clear();
        }
    }
}
