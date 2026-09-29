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
        /// <summary>The key a server looks a Find up by (FindServer, SearchCatalog.ByName): never rename one.</summary>
        public readonly string Name;
        /// <summary>What the window and the waypoint names show: Name, unless the game's own word differs.</summary>
        public readonly string Title;
        public readonly SearchTarget[] Locations;
        public readonly SearchTarget[] Objects;
        public readonly string[] Items;

        /// <summary>
        /// The Objects grow only inside the Locations (a Bee Nest in an Abandoned House), so a place and the objects
        /// in its 64 m zone are one find: an object found replaces its place (SearchRules.DropPlacesWithObjectsInZone),
        /// and a place whose zone the game has generated without one holds none (SearchRules.DropPlacesKnownEmpty).
        /// False when the objects are found apart from the places (the loose Rocks).
        /// </summary>
        public readonly bool PlacesHoldObjects;

        public SearchQuery(string name, SearchTarget[] locations, SearchTarget[] objects, string[] items)
            : this(name, null, locations, objects, items, false)
        {
        }

        public SearchQuery(string name, SearchTarget[] locations, SearchTarget[] objects, string[] items, bool placesHoldObjects)
            : this(name, null, locations, objects, items, placesHoldObjects)
        {
        }

        public SearchQuery(string name, string title, SearchTarget[] locations, SearchTarget[] objects, string[] items, bool placesHoldObjects)
        {
            Name = name;
            Title = string.IsNullOrEmpty(title) ? name : title;
            Locations = locations ?? new SearchTarget[0];
            Objects = objects ?? new SearchTarget[0];
            Items = items ?? new string[0];
            PlacesHoldObjects = placesHoldObjects;
        }
    }

    /// <summary>
    /// What can be searched for, and where. Every list was read from a dump of Valheim 1.0.16's own prefabs - not
    /// from the wiki: the chests of every location prefab, and of the rooms its dungeon generator can build, whose
    /// loot table lists the item. Only chests that are enabled in the prefab count: ZoneSystem.SpawnLocation spawns
    /// only enabled children (Utils.GetEnabledComponentsInChildren), so a chest in a switched-off part never
    /// appears (the Troll Cave's spear chests, for one). A location type counts if it CAN hold such a chest;
    /// whether a given one does is left to chance. The Bee Nest's places were read the same way: every location
    /// prefab, and every room its generator can build, with an enabled wild nest (Beehive) among its children.
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
                        T("Hildir_plainsfortress", "Sealed Tower")
                    }, null,
                    new[] { "AtgeirWood" }),
                // The Wooden Greatsword (THSwordWood) is in two loot tables: the Dvergr treasure chest of three ruined
                // Mistlands guard towers and of the rock spire (TreasureChest_dvergrtower; the spire's sits on its top),
                // and the one in an Infested Mine's treasure rooms (TreasureChest_dvergrtown in dvergr_room_TREASURE and
                // _TREASURE2, which only the mines' generators build). A mine's rooms stand about 5000 m above its entrance
                // but inside the entrance's own 64 m zone - the entrance is placed at its zone's centre (its radius 32
                // leaves no room to move) and the generator keeps every room it tests inside that zone
                // (DungeonGenerator.IsInsideDungeon) - so their chests are within 45.3 m horizontally: the usual 64 m.
                // The game names the mines only; the towers and the spire have no name of their own.
                new SearchQuery("Wooden Greatsword",
                    new[]
                    {
                        T("Mistlands_DvergrTownEntrance1", "Infested Mine"), T("Mistlands_DvergrTownEntrance2", "Infested Mine"),
                        T("Mistlands_GuardTower1_ruined_new", "Ruined Dvergr Tower"), T("Mistlands_GuardTower1_ruined_new2", "Ruined Dvergr Tower"),
                        T("Mistlands_GuardTower3_ruined_new", "Ruined Dvergr Tower"), T("Mistlands_RockSpire1", "Rock Spire")
                    }, null,
                    new[] { "THSwordWood" }),
                new SearchQuery("Curious Axe Head",
                    new[] { T("WoodHouse6", "Abandoned House") }, null,
                    new[] { "AxeHead1" }),
                new SearchQuery("Mysterious Axe Head",
                    new[] { T("WoodHouse2", "Abandoned House") }, null,
                    new[] { "AxeHead2" }),
                // The key "Mysterious Rock" stays: servers look a Find up by it. The game calls these rocks "Rock":
                // Pickable_StoneRock gives the StoneRock item ($item_stonerock) and has no m_overrideName. The game's
                // "Mysterious Rock" ($item_hardrock) is the pet rock a player builds (Placeable_HardRock), which lays
                // Pickable_HardRockOffspring; neither is ever listed (other prefabs, and SearchRules.WorldMade).
                new SearchQuery("Mysterious Rock", "Rock",
                    new[] { T("BigRockClearing", "Big Rock Clearing") },
                    new[] { T("Pickable_StoneRock", "Rock") },
                    null, false),
                // A wild nest (Beehive, not the player's own piece_beehive) grows only as a child of these places,
                // each with a chance rolled when its zone is generated: eleven Abandoned Houses (25%; WoodHouse8 and
                // WoodHouse12 hold none), the Contested Tower (81.8% x 28.1%), the Bear Cave (50%, on its fir tree),
                // and rooms the generators of the fenced Meadows farm (WoodFarm1, which has no name in the game) and of
                // the Draugr Villages can build. Nothing else in world generation makes one: no vegetation carries it.
                new SearchQuery("Bee Nest",
                    new[]
                    {
                        T("WoodHouse1", "Abandoned House"), T("WoodHouse2", "Abandoned House"), T("WoodHouse3", "Abandoned House"),
                        T("WoodHouse4", "Abandoned House"), T("WoodHouse5", "Abandoned House"), T("WoodHouse6", "Abandoned House"),
                        T("WoodHouse7", "Abandoned House"), T("WoodHouse9", "Abandoned House"), T("WoodHouse10", "Abandoned House"),
                        T("WoodHouse11", "Abandoned House"), T("WoodHouse13", "Abandoned House"),
                        T("StoneTowerRuins03", "Contested Tower"), T("BearCave", "Bear Cave"),
                        T("WoodFarm1", "Abandoned Village"),
                        T("WoodVillage1", "Draugr Village"), T("WoodVillage2", "Draugr Village")
                    },
                    new[] { T("Beehive", "Bee Nest") },
                    null, true),
                new SearchQuery("Haldor", new[] { T("Vendor_BlackForest", "Haldor") }, null, null),
                new SearchQuery("Hildir", new[] { T("Hildir_camp", "Hildir") }, null, null),
                new SearchQuery("Bog Witch", new[] { T("BogWitch_Camp", "Bog Witch") }, null, null),
            };
        }

        /// <summary>
        /// Location types whose chests can stand further out: the ones a dungeon generator builds rooms for (the
        /// villages and Hildir's Sealed Tower). Their chests are looked for within 96 m of the location's centre instead
        /// of 64 m. That is generous: the generator keeps every room it tests inside its zone
        /// (DungeonGenerator.IsInsideDungeon), about 48 m from the centre at most, and the ones it does not test (a
        /// dungeon's start room, zero-depth end caps) sit at the centre or attach to tested rooms. A neighbour's chest in
        /// reach can never drop a place whose own chest still holds, or may still hold, the item. Not the Infested
        /// Mines: their generator builds rooms high above the entrance but inside the entrance's own 64 m zone, so their
        /// chests are within 45.3 m.
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
