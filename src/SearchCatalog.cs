using System;
using System.Collections.Generic;

namespace Waypointer
{
    /// <summary>One location type a search looks for: the game's prefab name and the name a waypoint gets.</summary>
    internal sealed class SearchTarget
    {
        public readonly string Prefab;
        public readonly string Label;
        public SearchTarget(string prefab, string label) { Prefab = prefab; Label = label; }
    }

    /// <summary>
    /// One thing the player can search for. Locations come from the game's list of placed location instances;
    /// Objects are world objects found by prefab (only where the game has already generated them); Items are the
    /// chest items the search is about, used to skip chests already opened without them.
    /// </summary>
    internal sealed class SearchQuery
    {
        public readonly string Name;
        public readonly SearchTarget[] Locations;
        public readonly SearchTarget[] Objects;
        public readonly string[] Items;

        public SearchQuery(string name, SearchTarget[] locations, SearchTarget[] objects, string[] items)
        {
            Name = name;
            Locations = locations ?? new SearchTarget[0];
            Objects = objects ?? new SearchTarget[0];
            Items = items ?? new string[0];
        }
    }

    /// <summary>
    /// What can be searched for, and where. Every list was read from a dump of Valheim 1.0.16's own prefabs - not
    /// from the wiki: the chests of every location prefab, and of the rooms its dungeon generator can build, whose
    /// loot table lists the item. Only chests that are enabled in the prefab count: ZoneSystem.SpawnLocation spawns
    /// only enabled children (Utils.GetEnabledComponentsInChildren), so a chest in a switched-off part never
    /// appears (the Troll Cave's spear chests, for one). A location type counts if it CAN hold such a chest;
    /// whether a given one does is left to chance.
    ///
    /// Unity-free, so the tests compile it directly.
    /// </summary>
    internal static class SearchCatalog
    {
        /// <summary>
        /// Location types placed once per world: the game registers up to 10 candidate spots, the first one anybody
        /// comes near becomes the real one and the others are discarded (ZoneSystem.PlaceLocations ->
        /// RemoveUnplacedLocations).
        /// </summary>
        public static readonly string[] UniqueLocations = { "BigRockClearing", "Vendor_BlackForest", "Hildir_camp", "BogWitch_Camp" };

        public static readonly SearchQuery[] Queries = Build();

        private static SearchTarget T(string prefab, string label) { return new SearchTarget(prefab, label); }

        private static SearchQuery[] Build()
        {
            return new SearchQuery[]
            {
                new SearchQuery("Wooden Axe / Wooden Knife",
                    new[] { T("ShipSetting01", "Viking Graveyard") }, null,
                    new[] { "AxeWood", "KnifeWood" }),
                new SearchQuery("Wooden Mace",
                    new[] { T("CombatRuin01", "Combat Ruin"), T("WoodVillage1", "Draugr Village"), T("WoodVillage2", "Draugr Village") }, null,
                    new[] { "MaceWood" }),
                new SearchQuery("Wooden Sledge",
                    new[] { T("Grave1", "Swamp Grave"), T("SwampRuin1", "Swamp Runestone Tower"), T("SwampRuin2", "Swamp Runestone Tower") }, null,
                    new[] { "SledgeWood" }),
                new SearchQuery("Wooden Spear",
                    new[]
                    {
                        T("Ruin1", "Greydwarf Ruins"), T("StoneHouse3", "Greydwarf Ruins"), T("Ruin2", "Greydwarf Tower"),
                        T("StoneTowerRuins07", "Skeleton Tower"), T("StoneTowerRuins08", "Skeleton Tower"),
                        T("StoneTowerRuins09", "Skeleton Tower"), T("StoneTowerRuins10", "Skeleton Tower"),
                        T("StoneTowerRuins09_sunk", "Sunken Skeleton Tower"), T("StoneTowerRuins10_sunk", "Sunken Skeleton Tower"),
                        T("StoneTowerRuins03", "Contested Tower"),
                        T("SwampHut1", "Abandoned Hut"), T("SwampHut2", "Abandoned Hut"), T("SwampHut3", "Abandoned Hut"),
                        T("SwampHut4", "Abandoned Hut"), T("SwampHut5", "Abandoned Hut"),
                        T("SwampHut1_1", "Abandoned Hut"), T("SwampHut2_1", "Abandoned Hut"), T("SwampHut3_1", "Abandoned Hut")
                    }, null,
                    new[] { "SpearWood" }),
                new SearchQuery("Wooden Battleaxe",
                    new[]
                    {
                        T("AbandonedLogCabin02", "Abandoned Cabin"), T("AbandonedLogCabin03", "Abandoned Cabin"),
                        T("AbandonedLogCabin04", "Abandoned Cabin"), T("StoneTowerRuins04", "Mountain Tower"),
                        T("StoneTowerRuins05", "Mountain Tower"), T("StoneTowerRuins05_leet", "Mountain Tower"),
                        T("MountainWell1", "Mountain Inverted Tower")
                    }, null,
                    new[] { "BattleaxeWood" }),
                new SearchQuery("Wooden Atgeir",
                    new[]
                    {
                        T("GoblinCamp2", "Fuling Village"), T("GoblinCamp2_1", "Fuling Village"),
                        T("StoneTower1", "Fuling Outpost"), T("StoneTower3", "Fuling Outpost"), T("Ruin3", "Fuling Ruin"),
                        T("GoblinHut02", "Fuling Hut"), T("GoblinHut03", "Fuling Hut"),
                        T("Hildir_plainsfortress", "Hildir's Plains Fortress")
                    }, null,
                    new[] { "AtgeirWood" }),
                new SearchQuery("Curious Axe Head",
                    new[] { T("WoodHouse6", "Abandoned House") }, null,
                    new[] { "AxeHead1" }),
                new SearchQuery("Mysterious Axe Head",
                    new[] { T("WoodHouse2", "Abandoned House") }, null,
                    new[] { "AxeHead2" }),
                new SearchQuery("Mysterious Rock",
                    new[] { T("BigRockClearing", "Big Rock Clearing") },
                    new[] { T("Pickable_StoneRock", "Mysterious Rock") },
                    null),
                new SearchQuery("Haldor", new[] { T("Vendor_BlackForest", "Haldor") }, null, null),
                new SearchQuery("Hildir", new[] { T("Hildir_camp", "Hildir") }, null, null),
                new SearchQuery("Bog Witch", new[] { T("BogWitch_Camp", "Bog Witch") }, null, null),
            };
        }

        /// <summary>
        /// Location types whose chests can stand further out: the ones a dungeon generator builds rooms for (the
        /// villages and Hildir's fortress). Their chests are looked for within 96 m of the location's centre instead
        /// of 64 m (their own extents reach 32 m).
        /// </summary>
        public static readonly string[] WideLocations = { "WoodVillage1", "WoodVillage2", "GoblinCamp2", "GoblinCamp2_1", "Hildir_plainsfortress" };

        /// <summary>The query with this name, or null (a server asked for a query of another catalogue version).</summary>
        public static SearchQuery ByName(string name)
        {
            for (int i = 0; i < Queries.Length; i++)
                if (string.Equals(Queries[i].Name, name, StringComparison.Ordinal)) return Queries[i];
            return null;
        }

        public static bool IsUnique(string locationPrefab)
        {
            return Array.IndexOf(UniqueLocations, locationPrefab) >= 0;
        }

        public static bool WideLocation(string locationPrefab)
        {
            return Array.IndexOf(WideLocations, locationPrefab) >= 0;
        }
    }
}
