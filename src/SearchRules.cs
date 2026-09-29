using System;
using System.Collections.Generic;

namespace Waypointer
{
    /// <summary>One place a search found: a location instance, or a world object.</summary>
    internal sealed class SearchHit
    {
        public string Prefab;
        public string Label;
        public float X, Y, Z;
        /// <summary>Location instances only: the zone has been generated, so the location really exists there.
        /// Known on a server; a client does not get this flag.</summary>
        public bool Placed;
        /// <summary>A unique location's candidate spot that may never become the real one.</summary>
        public bool Possible;
        public bool IsObject;
    }

    /// <summary>
    /// The decisions a search makes about what it found, kept free of Unity so the tests can exercise them.
    /// </summary>
    internal static class SearchRules
    {
        /// <summary>
        /// The start of the pin name every request of this mod carries, then the search number. The server copies
        /// it into each answer, so an answer to this mod is known by its name alone. Nothing in the game makes a pin
        /// name starting with a control character.
        /// </summary>
        public const string TokenPrefix = "\u0001WaypointerSearch#";

        /// <summary>
        /// The pin name a search sends with its requests. The server echoes it in every answer, and VanillaMayHandle
        /// keeps any answer carrying it away from vanilla, so the name must always start with TokenPrefix (preflight
        /// checks that the requests carry exactly this).
        /// </summary>
        public static string RequestPinName(int searchId)
        {
            return TokenPrefix + searchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether vanilla may handle a location answer (Game.RPC_DiscoverLocationResponse, which makes a saved,
        /// shareable pin). Decided by the pin name alone, whatever a search is doing: an answer to this mod - current,
        /// abandoned or malformed - never reaches vanilla; every other answer (a Vegvisir's, a runestone's) does.
        /// </summary>
        public static bool VanillaMayHandle(string pinName)
        {
            return pinName == null || !pinName.StartsWith(TokenPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether a Harmony prefix (its owner id and the full name of the type declaring it) is this plugin's answer
        /// prefix. LocationSearch.AnswersIntercepted sends nothing unless one is found on the answer method.
        /// </summary>
        public static bool IsOurPrefix(string owner, string patchType, string ourOwner, string ourPatchType)
        {
            return owner != null && patchType != null && ourOwner != null && ourPatchType != null
                && string.Equals(owner, ourOwner, StringComparison.Ordinal)
                && string.Equals(patchType, ourPatchType, StringComparison.Ordinal);
        }

        /// <summary>
        /// Unique locations as seen by the server, which knows which candidate has been placed: once one is
        /// placed, the game has already discarded the other candidates, and only placed ones are kept here too.
        /// Before that, a lone candidate is the only place the location can go, so it counts as the real one (as it
        /// does on a client); with more than one, every candidate is kept and marked Possible.
        /// </summary>
        public static void ResolveUniqueOnServer(List<SearchHit> hits)
        {
            Dictionary<string, bool> anyPlaced = new Dictionary<string, bool>();
            Dictionary<string, int> counts = CountUnique(hits);
            for (int i = 0; i < hits.Count; i++)
            {
                SearchHit h = hits[i];
                if (h.IsObject || !SearchCatalog.IsUnique(h.Prefab)) continue;
                bool placed;
                anyPlaced.TryGetValue(h.Prefab, out placed);
                anyPlaced[h.Prefab] = placed || h.Placed;
            }
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                SearchHit h = hits[i];
                if (h.IsObject || !SearchCatalog.IsUnique(h.Prefab)) continue;
                if (anyPlaced[h.Prefab]) { if (!h.Placed) hits.RemoveAt(i); }
                else if (counts[h.Prefab] > 1) h.Possible = true;
            }
        }

        /// <summary>
        /// Unique locations as a client sees them: the server's list carries no placed flag. When a candidate is
        /// placed the game discards the others, so exactly one answer means the real spot (or the only candidate
        /// left, which becomes the real one); more than one means none is placed yet, and all are Possible. For the
        /// merchants the game also sends a map icon once placed (iconPlacedPositions, by prefab), which decides it.
        /// When the answers are incomplete (the server did not answer every request in time), a lone answer may be
        /// one of several candidates, so without an icon every answer is Possible.
        /// </summary>
        public static void ResolveUniqueOnClient(List<SearchHit> hits, Dictionary<string, float[]> iconPlacedPositions, bool complete)
        {
            Dictionary<string, int> counts = CountUnique(hits);
            foreach (KeyValuePair<string, int> kv in counts)
            {
                float[] icon;
                if (iconPlacedPositions != null && iconPlacedPositions.TryGetValue(kv.Key, out icon) && icon != null)
                {
                    // Keep the one answer at the icon (the real spot); drop the rest.
                    int keep = -1;
                    double best = double.MaxValue;
                    for (int i = 0; i < hits.Count; i++)
                    {
                        SearchHit h = hits[i];
                        if (h.IsObject || h.Prefab != kv.Key) continue;
                        double dx = h.X - icon[0], dz = h.Z - icon[2];
                        double d = dx * dx + dz * dz;
                        if (d < best) { best = d; keep = i; }
                    }
                    for (int i = hits.Count - 1; i >= 0; i--)
                    {
                        SearchHit h = hits[i];
                        if (h.IsObject || h.Prefab != kv.Key) continue;
                        if (i == keep) h.Placed = true; else hits.RemoveAt(i);
                    }
                    continue;
                }
                for (int i = 0; i < hits.Count; i++)
                {
                    SearchHit h = hits[i];
                    if (h.IsObject || h.Prefab != kv.Key) continue;
                    if (kv.Value > 1 || !complete) h.Possible = true;
                }
            }
        }

        /// <summary>How many location hits each unique location has.</summary>
        private static Dictionary<string, int> CountUnique(List<SearchHit> hits)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            for (int i = 0; i < hits.Count; i++)
            {
                SearchHit h = hits[i];
                if (h.IsObject || !SearchCatalog.IsUnique(h.Prefab)) continue;
                int c;
                counts.TryGetValue(h.Prefab, out c);
                counts[h.Prefab] = c + 1;
            }
            return counts;
        }

        /// <summary>
        /// Whether a location's chests are known not to hold the item any more. The caller counts every loot chest
        /// (a chest the game fills from a loot table) within the location's radius: at least one was found, the game
        /// has already filled every one of them, and none holds the item now. That is conservative - a neighbour's
        /// chest in reach can only keep a place, never drop one that still holds the item. No chest found means
        /// "cannot tell" (not spawned, or out of reach), and the location is kept.
        /// </summary>
        public static bool ChestsExhausted(int candidateChestsFound, int notYetFilled, int holdingItem)
        {
            return candidateChestsFound > 0 && notYetFilled == 0 && holdingItem == 0;
        }

        /// <summary>Removes objects within radius of a location hit with the given prefab (rocks of a clearing already listed).</summary>
        public static void DropObjectsNear(List<SearchHit> hits, string locationPrefab, float radius)
        {
            List<SearchHit> anchors = new List<SearchHit>();
            for (int i = 0; i < hits.Count; i++)
                if (!hits[i].IsObject && hits[i].Prefab == locationPrefab) anchors.Add(hits[i]);
            if (anchors.Count == 0) return;
            float r2 = radius * radius;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                SearchHit h = hits[i];
                if (!h.IsObject) continue;
                for (int a = 0; a < anchors.Count; a++)
                {
                    float dx = h.X - anchors[a].X, dz = h.Z - anchors[a].Z;
                    if (dx * dx + dz * dz <= r2) { hits.RemoveAt(i); break; }
                }
            }
        }

        /// <summary>
        /// The game's zone index of a world coordinate, computed as ZoneSystem.GetZone does (Valheim 1.0.16): the
        /// division in double, then Utils.FloorToInt, which is (int)(f + 64000f) - 64000. The same expression, so the
        /// runtime that runs both rounds them alike at a zone's edge.
        /// </summary>
        public static int Zone(float v)
        {
            float f = (float)(((double)v + 32.0) / 64.0);
            return (int)(f + 64000f) - 64000;
        }

        /// <summary>One key for the zone holding (x, z), for sets of zones.</summary>
        public static long ZoneKey(float x, float z)
        {
            return ((long)Zone(x) << 32) | (uint)Zone(z);
        }

        /// <summary>
        /// For a query whose objects grow inside its places (the Bee Nest): drops every place with an object found in its
        /// own 64 m zone. The object is the find, and it can stand well away from the place's centre (about 38 m in a
        /// village). The game places a location wholly inside one zone, so its nests share that zone, and a zone holds
        /// one location. Every object is kept: a village can hold several nests.
        /// </summary>
        public static void DropPlacesWithObjectsInZone(List<SearchHit> hits)
        {
            HashSet<long> zones = new HashSet<long>();
            for (int i = 0; i < hits.Count; i++) if (hits[i].IsObject) zones.Add(ZoneKey(hits[i].X, hits[i].Z));
            if (zones.Count == 0) return;
            for (int i = hits.Count - 1; i >= 0; i--)
                if (!hits[i].IsObject && zones.Contains(ZoneKey(hits[i].X, hits[i].Z))) hits.RemoveAt(i);
        }

        /// <summary>
        /// The player's side, once every answer is in, in this order: Wayfinder's explored filter (explored non-null:
        /// keep only the hits it accepts, and no unique place's candidate), then the rocks of a listed Big Rock Clearing,
        /// then - for a query whose objects grow inside its places - the places a found object stands for. The merge
        /// must come after the filter, or an unexplored nest would remove its explored place and then vanish itself.
        /// </summary>
        public static void FinishHits(List<SearchHit> hits, SearchQuery q, Predicate<SearchHit> explored, float rockClearingRadius)
        {
            if (explored != null)
            {
                for (int i = hits.Count - 1; i >= 0; i--)
                    if (hits[i].Possible || !explored(hits[i])) hits.RemoveAt(i);
            }
            DropObjectsNear(hits, "BigRockClearing", rockClearingRadius);
            if (q != null && q.PlacesHoldObjects) DropPlacesWithObjectsInZone(hits);
        }

        /// <summary>
        /// Whether a search leaves out the places known to hold none of its objects: only on a server (which holds every
        /// generated object), only when the player asked to skip places known not to hold what they look for (the
        /// request's CheckChests, TomTom's SkipCheckedChests), and only for a query whose objects grow inside its places.
        /// </summary>
        public static bool KnownEmptyApplies(bool onServer, bool checkChests, SearchQuery q)
        {
            return onServer && checkChests && q != null && q.PlacesHoldObjects;
        }

        /// <summary>
        /// Whether a world object counts: it was not placed through the build system (Piece.SetCreator writes
        /// ZDOVars.s_creator - the way mods make a pickable buildable) and not spawned with the console's spawn or
        /// location command (they set ZDOVars.s_cheated). World generation sets neither - nor does the console's
        /// vegetation command, whose objects cannot be told from the world's. Player IDs can be negative, so the test is
        /// != 0.
        /// </summary>
        public static bool WorldMade(long creator, bool cheated)
        {
            return creator == 0L && !cheated;
        }

        /// <summary>
        /// Notes the zone of an object read (when <paramref name="zones"/> is not null) before any range test - a place
        /// in range can hold a nest just beyond it - and returns whether the object lies within range (horizontal,
        /// inclusive) of (ox, oz).
        /// </summary>
        public static bool NoteObject(HashSet<long> zones, float x, float z, float ox, float oz, float range)
        {
            if (zones != null) zones.Add(ZoneKey(x, z));
            float dx = x - ox, dz = z - oz;
            return dx * dx + dz * dz <= range * range;
        }

        /// <summary>
        /// For a query whose objects grow inside its places, on a server only: drops every placed place (its zone has
        /// been generated, so its nests' chances have been rolled) with no object left in its zone - none grew there,
        /// or it was destroyed. <paramref name="objectZones"/> holds the zone of every such object the server holds
        /// around the search, in range or not (a place in range can hold a nest just beyond it); null means none. A
        /// place not generated yet, a unique place's candidate, and an object are kept. Returns how many places were
        /// checked; <paramref name="dropped"/> receives how many of them were dropped.
        /// </summary>
        public static int DropPlacesKnownEmpty(List<SearchHit> hits, HashSet<long> objectZones, out int dropped)
        {
            int checkedPlaces = 0;
            dropped = 0;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                SearchHit h = hits[i];
                if (h.IsObject || h.Possible || !h.Placed) continue;
                checkedPlaces++;
                if (objectZones == null || !objectZones.Contains(ZoneKey(h.X, h.Z)))
                {
                    hits.RemoveAt(i);
                    dropped++;
                }
            }
            return checkedPlaces;
        }

        /// <summary>
        /// Keeps the <paramref name="keep"/> hits nearest (x, z), horizontally. Only the player's own plugin trims:
        /// a server answering a Find sends every place in range, since Wayfinder's exploration filter runs after it
        /// on the player's side.
        /// </summary>
        public static void KeepNearest(List<SearchHit> hits, float x, float z, int keep)
        {
            if (hits.Count <= keep) return;
            float[] keys = new float[hits.Count];
            SearchHit[] items = new SearchHit[hits.Count];
            for (int i = 0; i < hits.Count; i++)
            {
                float dx = hits[i].X - x, dz = hits[i].Z - z;
                keys[i] = dx * dx + dz * dz;
                items[i] = hits[i];
            }
            Array.Sort(keys, items);
            hits.Clear();
            for (int i = 0; i < keep; i++) hits.Add(items[i]);
        }

        /// <summary>Keeps hits within range (horizontal) of the start point.</summary>
        public static void KeepWithinRange(List<SearchHit> hits, float x, float z, float range)
        {
            float r2 = range * range;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                float dx = hits[i].X - x, dz = hits[i].Z - z;
                if (dx * dx + dz * dz > r2) hits.RemoveAt(i);
            }
        }

        /// <summary>
        /// The waypoint name for a hit: its label, then what the search was for when that is a chest item
        /// ("Skeleton Tower (Wooden Spear)") or what a place may hold ("Abandoned House (Bee Nest)"; a nest found is
        /// just "Bee Nest"), and "(possible)" for a unique candidate that may never be the real one.
        /// </summary>
        public static string WaypointName(SearchHit h, SearchQuery q)
        {
            string name = h.Label;
            if (q != null && (q.Items.Length > 0 || (q.PlacesHoldObjects && !h.IsObject))) name += " (" + q.Title + ")";
            if (h.Possible) name += " (possible)";
            return name;
        }
    }
}
