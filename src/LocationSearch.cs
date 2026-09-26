using System;
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
    ///   unique candidate has been placed, and every generated object and chest (SearchJob).
    /// - As a client of a server that runs this plugin too (a dedicated server, or a host with it installed): that
    ///   server's own answer, the same a host gets, over this plugin's messages (FindLink, FindServer).
    /// - As a client of any other server, the server is asked, one location type at a time and paced, with the
    ///   same request a Vegvisir makes (RPC_DiscoverClosestLocation with discoverAll). Vanilla would turn every
    ///   answer into a saved map pin - the kind a Cartography Table shares - so Game_RPC_DiscoverLocationResponse_Patch
    ///   catches this mod's answers first, and no request is ever sent unless that interception is confirmed active
    ///   (AnswersIntercepted). A final request with exactly one answer (the closest StartTemple) marks the end of the
    ///   answers, since routed calls keep their order. World objects then come from the ZDOs the client holds:
    ///   only what is loaded near the player.
    ///
    /// Work that grows with the world runs a little each frame (SearchBudget), so a large search takes longer rather
    /// than making the game stutter. TomTom logs how long each search took. Wayfinder keeps only places whose centre
    /// is explored on the map (by the player or a Cartography Table) and unique places that are already fixed, and
    /// says nothing about what it found - no message, no count, no log line (only a failure, such as a server that
    /// did not answer in time, is logged).
    /// </summary>
    internal static class LocationSearch
    {
        private const int SentinelType = int.MinValue;
        private const string SentinelLocation = "StartTemple";
        private const float RequestSpacing = 0.2f;
        private const float AnswerTimeout = 15f;
        /// <summary>How long a server with this plugin may take to accept a Find, and then to finish it.</summary>
        private const float HelperAcceptTimeout = 15f;
        private const float HelperResultTimeout = 120f;
        private const double RouteBudgetMs = 5.0;
        private const float RockClearingRadius = 40f;

        private enum Phase { Idle, WaitingForServerInfo, Asking, AskingServerPlugin, Scanning }

        private static Phase _phase = Phase.Idle;
        private static SearchQuery _query;
        private static bool _replace;
        private static float _range;
        private static Vector3 _origin;
        private static bool _onServer;
#if !WAYFINDER
        private static bool _answeredByServerPlugin;
#endif
        private static readonly List<SearchHit> _hits = new List<SearchHit>();
        private static SearchJob _job;
        private static int _searchId;
        private static string _token = SearchRules.RequestPinName(0);
        private static bool _serverPluginDone;
        private static int _nextAsk;
        private static float _nextAskTime;
        private static float _deadline;
        private static bool _allAnswered;

        private static readonly Stopwatch _frame = new Stopwatch();
        private static readonly Stopwatch _elapsed = new Stopwatch();
        private static double _workMs;
        private static int _workFrames;
        private static int _answers;
        private static int _objectsRead;
        private static int _chestPlacesChecked;
        private static int _chestPlacesSkipped;

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
            _answers = _objectsRead = _chestPlacesChecked = _chestPlacesSkipped = 0;
#if !WAYFINDER
            _answeredByServerPlugin = false;
#endif
            _workMs = 0;
            _workFrames = 0;
            _elapsed.Reset();
            _elapsed.Start();
            _onServer = ZNet.instance.IsServer();

            if (_onServer)
            {
                _job = new SearchJob(_query, _origin, _range, true, ChestCheckWanted(), _hits);
                _job.CollectLocationsOnServer();
                BeginScan();
                return true;
            }

            if (FindLink.ServerHasPlugin) return AskServerPlugin();
            if (FindLink.AwaitingServerInfo)
            {
                // Said hello a moment ago and the server has not answered yet: give it until the answer is due.
                _phase = Phase.WaitingForServerInfo;
                SetStatus("Searching - asking the server...");
                return true;
            }
            return StartAsking();
        }

        /// <summary>The Vegvisir-style path, for a server without this plugin.</summary>
        private static bool StartAsking()
        {
#if !WAYFINDER
            _answeredByServerPlugin = false;
#endif
            if (!AnswersIntercepted())
            {
                // Fail closed: without the interception every answer would become a saved, shareable pin.
                Plugin.Log.LogWarning("Location search is unavailable here: its safety patch on Game.RPC_DiscoverLocationResponse is not active.");
                SetStatus("Search is unavailable - see BepInEx/LogOutput.log.");
                _phase = Phase.Idle;
                return false;
            }
            _job = new SearchJob(_query, _origin, _range, false, false, _hits);
            _phase = Phase.Asking;
            _nextAsk = 0;
            _nextAskTime = 0f;
            _deadline = float.MaxValue;
            _allAnswered = false;
            SetStatus("Searching - asking the server...");
            return true;
        }

        /// <summary>The path for a server that runs this plugin: one request, and its own answer.</summary>
        private static bool AskServerPlugin()
        {
            FindRequest request = new FindRequest();
            request.SearchId = _searchId;
            request.Query = _query.Name;
            request.X = _origin.x; request.Y = _origin.y; request.Z = _origin.z;
            request.Range = Mathf.Clamp(_range, FindProtocol.MinRange, FindProtocol.MaxRange);
            request.CheckChests = ChestCheckWanted();
            if (!FindLink.SendFind(request))
            {
                SetStatus("Search failed - see BepInEx/LogOutput.log.");
                _phase = Phase.Idle;
                return false;
            }
            _job = null;
#if !WAYFINDER
            _answeredByServerPlugin = true;
#endif
            _serverPluginDone = false;
            _phase = Phase.AskingServerPlugin;
            _deadline = Time.unscaledTime + HelperAcceptTimeout;
            SetStatus("Searching - the server is looking...");
            return true;
        }

        private static bool ChestCheckWanted()
        {
#if WAYFINDER
            return false;
#else
            return Plugin.SkipCheckedChests.Value;
#endif
        }

        /// <summary>Stops a running search without placing anything (death, world change, plugin unload).</summary>
        public static void Cancel()
        {
            if (_phase != Phase.Idle) SetStatus("Search stopped.");
            _phase = Phase.Idle;
            _job = null;
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
                if (_phase == Phase.WaitingForServerInfo) TickWaitingForServerInfo();
                else if (_phase == Phase.Asking) TickAsking();
                else if (_phase == Phase.AskingServerPlugin) TickAskingServerPlugin();
                else if (_phase == Phase.Scanning) TickScanning();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Location search failed: " + e);
                Cancel();
                SetStatus("Search failed - see BepInEx/LogOutput.log.");
            }
            finally
            {
                _frame.Stop();
            }
        }

        // ---------------------------------------------------------------- sources

        private static void TickWaitingForServerInfo()
        {
            if (FindLink.AwaitingServerInfo) return;
            if (FindLink.ServerHasPlugin) AskServerPlugin();
            else StartAsking();
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
                    SetStatus("Search is unavailable - see BepInEx/LogOutput.log.");
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

        private static void TickAskingServerPlugin()
        {
            if (_serverPluginDone)
            {
                // The server has already resolved the unique places, read the objects and checked the chests; the
                // range was applied there, and is applied again here in case the server's differed. Finished here,
                // inside Tick's guards (the player still in this world), not in the network handler.
                SearchRules.KeepWithinRange(_hits, _origin.x, _origin.z, _range);
                Finish();
                return;
            }
            if (Time.unscaledTime <= _deadline) return;
            Plugin.Log.LogWarning("Location search: the server's plugin did not answer in time.");
            _phase = Phase.Idle;
            _hits.Clear();
            SetStatus("The server did not answer - try again.");
        }

        /// <summary>
        /// Called by FindLink with every answer the server's plugin sends. Places are added as they arrive; Done
        /// finishes the search with what arrived (all of it, unless a message was lost).
        /// </summary>
        internal static void OnServerPluginAnswer(FoundMessage m)
        {
            if (_phase != Phase.AskingServerPlugin || m == null || m.SearchId != _searchId) return;
            if (m.Status == FindStatus.Accepted)
            {
                FindLink.NoteAllowed();
                _deadline = Time.unscaledTime + HelperResultTimeout;
                return;
            }
            if (m.Status == FindStatus.Part)
            {
                _deadline = Time.unscaledTime + HelperResultTimeout;
                for (int i = 0; i < m.Hits.Count; i++)
                {
                    SearchHit h = m.Hits[i];
                    string own = OwnLabel(h.Prefab);
                    if (own != null) h.Label = own;
                    _hits.Add(h);
                }
                _answers += m.Hits.Count;
                return;
            }
            if (m.Status == FindStatus.Done)
            {
                if (m.Total != _answers) WarnMissing(m.Total, _answers);
                _objectsRead = m.ObjectsRead;
                _chestPlacesChecked = m.ChestPlacesChecked;
                _chestPlacesSkipped = m.ChestPlacesSkipped;
                _serverPluginDone = true;
                return;
            }

            _phase = Phase.Idle;
            _hits.Clear();
            if (m.Status == FindStatus.Refused)
            {
                FindLink.NoteRefused();
                SetStatus(FindLink.RefusalText);
            }
            else if (m.Status == FindStatus.Busy)
                SetStatus("The server is busy with other searches - try again shortly.");
            else if (m.Status == FindStatus.UnknownQuery)
            {
                // The server's plugin is another version with another list; ask the way a Vegvisir does instead.
                Plugin.Log.LogInfo("Location search: the server's plugin does not know '" + _query.Name + "'; asking the server directly.");
                _hits.Clear();
                StartAsking();
            }
        }

        /// <summary>
        /// Part of the server's answer did not arrive. Wayfinder says so without numbers, since a count of the places
        /// in range is what it never tells. (Logged apart from OnServerPluginAnswer, which reads the search origin's x
        /// and z: preflight's Wayfinder scan takes a method that reads x/z and turns a number into text for a readout.)
        /// </summary>
        private static void WarnMissing(int sent, int arrived)
        {
#if WAYFINDER
            Plugin.Log.LogWarning("Location search: part of the server's answer did not arrive; using what arrived.");
#else
            Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "Location search: the server's plugin sent {0} places but {1} arrived; using what arrived.", sent, arrived));
#endif
        }

        /// <summary>This plugin's own name for a prefab the query looks for, or null.</summary>
        private static string OwnLabel(string prefab)
        {
            for (int i = 0; i < _query.Locations.Length; i++) if (_query.Locations[i].Prefab == prefab) return _query.Locations[i].Label;
            for (int i = 0; i < _query.Objects.Length; i++) if (_query.Objects[i].Prefab == prefab) return _query.Objects[i].Label;
            return null;
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

        private static void AddLocationHit(int index, Vector3 pos, bool placed)
        {
            if (_job != null) _job.AddLocationHit(index, pos, placed);
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

        // ---------------------------------------------------------------- the time-sliced part

        private static void BeginScan()
        {
            _phase = Phase.Scanning;
            SetStatus("Searching...");
        }

        private static void TickScanning()
        {
            bool more = _job.Step();
            _workMs += _frame.Elapsed.TotalMilliseconds;
            _workFrames++;
            if (!more)
            {
                _objectsRead = _job.ObjectsRead;
                _chestPlacesChecked = _job.ChestPlacesChecked;
                _chestPlacesSkipped = _job.ChestPlacesSkipped;
                Finish();
            }
        }

        // ---------------------------------------------------------------- the result

        private static void Finish()
        {
            _phase = Phase.Idle;
            _job = null;
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
                SearchRules.KeepNearest(_hits, _origin.x, _origin.z, Math.Max(4 * cap, 100));
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
            string source = _onServer ? "read the server's own list"
                : _answeredByServerPlugin ? string.Format(CultureInfo.InvariantCulture, "{0} places from the server's plugin", _answers)
                : string.Format(CultureInfo.InvariantCulture, "{0} answers from the server", _answers);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "Search '{0}' within {1:0} m: {2} found, {3} queued, {4} possible; {5}; {6} objects read, {7} places' chests checked, {8} skipped; {9:0.0} ms of work over {10} frames, {11:0.00} s in all.",
                _query.Name, _range, count, placed, possible, source,
                _objectsRead, _chestPlacesChecked, _chestPlacesSkipped, _workMs, _workFrames, _elapsed.Elapsed.TotalSeconds));
#endif
            _hits.Clear();
        }

        /// <summary>
        /// The window's status line, TomTom only: Wayfinder's search says nothing, as before 1.3.0. (A server that does
        /// not let the player use Find shows in the window's Find section in both editions - a rule, not a result.)
        /// </summary>
        private static void SetStatus(string text)
        {
#if !WAYFINDER
            WaypointWindow.SetStatus(text);
#endif
        }
    }
}
