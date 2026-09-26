using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Waypointer
{
    /// <summary>
    /// The frame budget every search shares: the player's own (LocationSearch) and, on a server, the ones it runs
    /// for players who ask (FindServer). Plugin.Update starts it once a frame, before either runs.
    /// </summary>
    internal static class SearchBudget
    {
        public const double FrameBudgetMs = 1.5;
        private static readonly Stopwatch _frame = new Stopwatch();

        public static void StartFrame()
        {
            _frame.Reset();
            _frame.Start();
        }

        public static bool Over()
        {
            return _frame.Elapsed.TotalMilliseconds > FrameBudgetMs;
        }
    }

    /// <summary>
    /// One search's work in the world, without the window or the waypoint queue: the location list (on a server),
    /// then world objects and chests, read a little each frame within SearchBudget so a large search takes longer
    /// rather than making the game stutter. It fills the list it is given.
    ///
    /// On a server it knows everything: every location instance and whether it is placed, and every generated
    /// object and chest (ZDOMan holds them all). On a client only the objects synced near the player are known,
    /// and chests are not read at all: an unseen chest holding the item could not stop the skip.
    /// </summary>
    internal sealed class SearchJob
    {
        public readonly SearchQuery Query;
        public readonly Vector3 Origin;
        public readonly float Range;
        public readonly bool OnServer;
        public readonly bool CheckChests;
        public readonly List<SearchHit> Hits;

        public int ObjectsRead;
        public int ChestPlacesChecked;
        public int ChestPlacesSkipped;

        private IEnumerator _scan;
        private static HashSet<int> _lootChestPrefabs;

        public SearchJob(SearchQuery query, Vector3 origin, float range, bool onServer, bool checkChests, List<SearchHit> hits)
        {
            Query = query;
            Origin = origin;
            Range = range;
            OnServer = onServer;
            CheckChests = checkChests;
            Hits = hits;
        }

        /// <summary>
        /// The server's own list of location instances, which knows which unique candidate has been placed
        /// (ZoneSystem.GetLocationList). Unique places are resolved over every candidate first - the real one may
        /// lie outside the range - then the range is applied.
        /// </summary>
        public void CollectLocationsOnServer()
        {
            Dictionary<string, int> wanted = new Dictionary<string, int>(System.StringComparer.Ordinal);
            for (int i = 0; i < Query.Locations.Length; i++) wanted[Query.Locations[i].Prefab] = i;
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
            SearchRules.ResolveUniqueOnServer(Hits);
            SearchRules.KeepWithinRange(Hits, Origin.x, Origin.z, Range);
        }

        public void AddLocationHit(int index, Vector3 pos, bool placed)
        {
            SearchTarget t = Query.Locations[index];
            SearchHit h = new SearchHit();
            h.Prefab = t.Prefab;
            h.Label = t.Label;
            h.X = pos.x; h.Y = pos.y; h.Z = pos.z;
            h.Placed = placed;
            Hits.Add(h);
        }

        /// <summary>Does the next slice of the time-sliced part; false once it is finished.</summary>
        public bool Step()
        {
            if (_scan == null) _scan = Scan();
            return _scan.MoveNext();
        }

        private IEnumerator Scan()
        {
            SimulationDistance oneZone = new SimulationDistance(0, 0, false);
            List<ZDO> zdos = new List<ZDO>();

            // World objects within range (the scattered Mysterious Rocks).
            for (int t = 0; t < Query.Objects.Length; t++)
            {
                SearchTarget target = Query.Objects[t];
                int hash = target.Prefab.GetStableHashCode();
                Vector2s centre = ZoneSystem.GetZone(Origin);
                int n = Mathf.CeilToInt(Range / 64f) + 1;
                for (int sy = centre.y - n; sy <= centre.y + n; sy++)
                {
                    for (int sx = centre.x - n; sx <= centre.x + n; sx++)
                    {
                        if (!InsideWorldGrid(sx, sy) || ZoneDistance(sx, sy) > Range) continue;
                        zdos.Clear();
                        ZDOMan.instance.FindSectorObjects(new Vector2s(sx, sy), oneZone, zdos, null);
                        for (int i = 0; i < zdos.Count; i++)
                        {
                            ZDO zdo = zdos[i];
                            ObjectsRead++;
                            if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != hash) continue;
                            Vector3 p = zdo.GetPosition();
                            float dx = p.x - Origin.x, dz = p.z - Origin.z;
                            if (dx * dx + dz * dz > Range * Range) continue;
                            if (IsPicked(zdo, target.Prefab)) continue;
                            SearchHit h = new SearchHit();
                            h.Prefab = target.Prefab;
                            h.Label = target.Label;
                            h.X = p.x; h.Y = p.y; h.Z = p.z;
                            h.IsObject = true;
                            h.Placed = true;
                            Hits.Add(h);
                        }
                        if (SearchBudget.Over()) yield return null;
                    }
                }
            }

            // Places whose chests have all been filled already, and hold none of the items any more. On the server
            // only, which holds every generated chest (a client holds only those near it, so an unseen chest with the
            // item could not stop the skip), and only for placed locations: a place not generated yet has no chest of
            // its own, and a neighbour's chest must not decide for it.
            if (OnServer && CheckChests && Query.Items.Length > 0 && Hits.Count > 0)
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
                        if ((i & 63) == 0 && SearchBudget.Over()) yield return null;
                    }
                    _lootChestPrefabs = found;
                }

                List<string> itemNames = new List<string>();
                for (int i = 0; i < Query.Items.Length; i++)
                {
                    GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(Query.Items[i]) : null;
                    ItemDrop drop = item != null ? item.GetComponent<ItemDrop>() : null;
                    if (drop != null) itemNames.Add(drop.m_itemData.m_shared.m_name);
                }

                if (itemNames.Count > 0)
                {
                    Inventory scratch = new Inventory("WaypointerChestCheck", null, 8, 8);
                    for (int h = Hits.Count - 1; h >= 0; h--)
                    {
                        SearchHit hit = Hits[h];
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
                                    ObjectsRead++;
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
                                    if (SearchBudget.Over()) yield return null;
                                }
                                if (SearchBudget.Over()) yield return null;
                            }
                        }
                        ChestPlacesChecked++;
                        if (SearchRules.ChestsExhausted(chests, unfilled, holding))
                        {
                            Hits.RemoveAt(h);
                            ChestPlacesSkipped++;
                        }
                    }
                }
            }
            yield break;
        }

        private static bool IsPicked(ZDO zdo, string prefab)
        {
            if (!zdo.GetBool(ZDOVars.s_picked, false)) return false;
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            Pickable pickable = go != null ? go.GetComponent<Pickable>() : null;
            if (pickable == null || pickable.m_respawnTimeMinutes <= 0f) return true;
            // A pickable that grows back counts as available once its time is up, even if nobody has been near.
            System.DateTime pickedAt = new System.DateTime(zdo.GetLong(ZDOVars.s_pickedTime, 0L));
            return (ZNet.instance.GetTime() - pickedAt).TotalMinutes <= pickable.m_respawnTimeMinutes;
        }

        private static bool InsideWorldGrid(int sx, int sy)
        {
            // ZoneSystem.SectorToIndex maps sectors outside -256..255 to index 0, a catch-all bucket.
            return sx > -256 && sx < 256 && sy > -256 && sy < 256;
        }

        /// <summary>Horizontal distance from the search origin to the nearest point of a 64 m zone.</summary>
        private float ZoneDistance(int sx, int sy)
        {
            float minX = sx * 64f - 32f, maxX = sx * 64f + 32f;
            float minZ = sy * 64f - 32f, maxZ = sy * 64f + 32f;
            float dx = Mathf.Max(0f, Mathf.Max(minX - Origin.x, Origin.x - maxX));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - Origin.z, Origin.z - maxZ));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
