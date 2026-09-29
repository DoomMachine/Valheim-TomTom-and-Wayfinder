# TomTom & Wayfinder — source

One code base, two Valheim plugins by **DoomMachine**:

| | **TomTom** | **Wayfinder** |
| --- | --- | --- |
| Waypoints from typed / pasted coordinates | yes | **no** |
| `waypoint <x> <y>` console command | yes | **no** (refused) |
| Numeric coordinates shown anywhere | yes | **no** — names and distances only |
| Waypoints placed on the world map | yes | yes |
| Following a pin already on your map | yes | yes |
| Marking where you stand (`Add my position`, `waypoint here`) | yes | yes |
| Arrow, map markers, queue, arrival, persistence | yes | yes |
| Find: every place of one kind within a range, as a route | yes | only places whose centre is on explored map, silently |
| On a server (dedicated, or the Start Server host): answers other players' Find, `WhoMayFind` | yes | yes (serves both editions' players) |
| BepInEx GUID | `DoomMachine.TomTom` | `DoomMachine.Wayfinder` |
| Installed into the game by `dotnet build` | yes | no — packaged only |

**TomTom** is named in honour of the World of Warcraft addon that inspired the project. **Wayfinder** is
the immersion edition: a location cannot be looked up outside the game and walked straight to (its Find places
only what lies on land the player's map shows as explored). That
takes removing coordinate *display* as well as entry — Alt-click works anywhere on the map, so a
readout like "Waypoint added at 3350, -1190" would let a player nudge clicks onto a looked-up spot.

The two declare each other `BepInIncompatibility`, so if both are ever installed BepInEx loads exactly
one and logs `Could not load [...] because it is incompatible with ...` for the other.

User documentation ships inside each package: [`package/TomTom/README.md`](package/TomTom/README.md) and
[`package/Wayfinder/README.md`](package/Wayfinder/README.md). This file is about building and
maintaining them.

## Installing

Both editions need [BepInEx for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
Install **one** of them — they exclude each other. Packaged builds, when there are any, are attached to
this repository's [releases](https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder/releases); otherwise
build them yourself (below), which leaves a ready-to-install zip in `dist/`. Unzip it into
`BepInEx/plugins/`, or hand the zip to a mod manager.

The same plugin also runs on a server: a dedicated server (`valheim_server`), or the game of whoever hosts with
Start Server. There it answers other players' Find from the server's own knowledge and applies the server's
`WhoMayFind` rule. Step-by-step setup for a dedicated server (Windows, Linux, rented) is in the **Servers** section
of each package README.

---

## Layout

```
src/                     the shared source for both plugins
  Edition.cs             everything that differs between the editions (name, GUID, window id)
  CoordinateParser.cs    TomTom only - reading coordinates. Absent from Wayfinder.
  CoordinateFormat.cs    TomTom only - showing coordinates. Absent from Wayfinder.
  LocationSearch.cs      Find, the player's side: where places come from (the server's own list, the server's
                         plugin, or asking the way a Vegvisir does), the explored filter (Wayfinder), the route
  SearchJob.cs           one search's work in the world: the location list on a server, then objects and chests,
                         a little each frame (SearchBudget) - run for the player and, on a server, for others
  FindServer.cs          the server side: answers players' Find, applies WhoMayFind, one search at a time
  FindLink.cs            the player side of the server messages: hello, the server's answer, Find requests
  FindProtocol.cs        those messages as bytes, and the WhoMayFind rule - Unity-free, tested
  SearchCatalog.cs       what can be searched for, and which location types hold it (from 1.0.16's data)
  SearchRules.cs         unique places (candidates vs the real one), range, chests, names - Unity-free
  RoutePlanner.cs        the route: nearest first, then 2-opt - Unity-free
  SearchPatches.cs       keeps the server's answers from becoming map pins; WhoMayFind for Vegvisir-style
                         requests; which connection a routed call came on
  ...
Plugin.props             build settings shared by both projects (references, packaging, deploy)
TomTom/TomTom.csproj     sets EditionName=TomTom, deploys by default
Wayfinder/Wayfinder.csproj  defines WAYFINDER, excludes both Coordinate*.cs files, packages only
package/<Edition>/       manifest.json, icon.png and README.md for each package
tests/                   parser, formatter, crash-safe save, key-read, search and server-message tests (built as TomTom)
preflight.ps1            checks a compiled plugin against the shipped game assemblies
build.sh                 SDK-free fallback compiler (C# 5)
Waypointer.slnx          the solution: both editions and the tests
```

Edition differences are compile-time (`#if WAYFINDER`), not a runtime switch, so Wayfinder's missing
features are **absent from its assembly**, not merely disabled. Both coordinate files are kept out of
Wayfinder twice: `Wayfinder.csproj` lists them in `ExcludedSource`, and each file is wrapped in
`#if !WAYFINDER` so it compiles to nothing even in a build that globs every source file (`build.sh`).
Because the files are gone, any call site left unguarded in Wayfinder is a compile error, not a leak.

## Building

Open `Waypointer.slnx` in Visual Studio, or from this folder:

```bash
dotnet build Waypointer.slnx
```

Each edition is compiled into `build/<Edition>/` and packaged into
`dist/DoomMachine-<Edition>-<version>/` plus a matching `.zip` (DLL, manifest, icon, README) — the layout a
mod manager or a manual install expects.

**TomTom** is also installed into `BepInEx/plugins/DoomMachine-TomTom/`. **Wayfinder** is not, unless you
ask for it — it can't run alongside TomTom anyway:

```bash
dotnet build Wayfinder/Wayfinder.csproj -p:DeployToGame=true    # install Wayfinder
dotnet build TomTom/TomTom.csproj -p:DeployToGame=false         # build TomTom without installing
dotnet build Waypointer.slnx -p:ValheimDir="D:\Games\Valheim"   # a different game folder
```

The default game folder is this machine's; every build sets it with `-p:ValheimDir=` (MSBuild) or
`VALHEIM_DIR=` (`build.sh`), and both say so plainly when the folder is not there.

Installing one edition while the other is present builds fine but prints a **warning** naming the other
folder to remove; nothing is deleted automatically. To switch, remove `BepInEx/plugins/DoomMachine-TomTom`
(or `-Wayfinder`) and install the other.

## Checking

```bash
./run-tests.sh                                                         # 292 tests (parser, crash-safe save, route reads, key reads, search, server messages and rules, captions), on .NET and on Mono; each run stops after TEST_TIMEOUT seconds (300)
powershell -ExecutionPolicy Bypass -File preflight.ps1                 # the installed TomTom
powershell -ExecutionPolicy Bypass -File preflight.ps1 -Edition Wayfinder -Plugin build/Wayfinder/Wayfinder.dll
powershell -ExecutionPolicy Bypass -File preflight.ps1 -ValheimDir "D:\Games\Valheim"   # a game folder elsewhere
```

`preflight.ps1` reads a compiled plugin and fails if:

- its `BepInPlugin` identity or its incompatibility with the other edition is wrong
- any Harmony patch target, or any private game member reached by reflection, has disappeared from the
  shipped game assemblies (the first thing a Valheim update breaks), or a `[HarmonyPatch]` class is not applied:
  `Plugin.ApplyPatches`' game array must name each once, and its dedicated-server array exactly the two server
  patches
- anything could create a **shareable** map marker — markers must be `save: false` with owner 0, passed
  to `AddPin` and re-asserted after it, which keeps them out of the Cartography Table
  (`Minimap.GetSharedMapData`) and the player profile (`Minimap.GetMapData`); `AddPin` must be referenced
  exactly once, from `CreateLocalOnlyPin`, and nothing else may make a pin
- anything could change or remove a pin the mod did not create (a `PinData` store, a direct edit of the
  map's pin list, a wipe, or `RemovePin` anywhere but `RemoveOwnMarker`)
- a game, Unity, BepInEx or Harmony type or member the plugin calls no longer exists with the same
  signature, or a Harmony patch no longer names the exact overload it targets
- the `Chat.HasFocus` postfix loses the `Priority.Last` it needs to run after other mods' postfixes
- the route save could swap in a file that has not been flushed to disk — the one step of the crash-safe
  save no test can observe: `SafeFile.WriteAllText` must write the new text, call `FileStream.Flush(true)`
  unconditionally, and only then `File.Move` it into place; the save must go through it, and nothing but that
  method's one `FileStream` may open a file for writing (whether the drive honours the flush is beyond any
  check); and a route that could not be read must not be saved over: `WaypointManager.SaveIfDirty` must ask
  `RouteReadGate.Pending` before `SafeFile.WriteAllText` and return while it is true, `Load` and `RetryRead` must
  record a failed read on the branch where `ReadRoute` returned false (and `RetryRead` return right after it),
  `ReadRoute`'s catch must return false, `LoadForCurrentWorldIfNeeded` must call `RouteReadGate.NotRead` (that it sits
  on the `PersistWaypoints`-off path is not checked), `RetryRead` must set `_dirty` from the count of waypoints queued
  before the merge being above 0, and `SaveIfDirty` must be the only caller of `SafeFile.WriteAllText`
- a key the player chooses could stop the waypoint tick: Valheim throws on every read of 30 of the keys
  BepInEx offers, so every configurable key must be read through `Hotkeys`, whose reads are caught (and not
  rethrown), a hard-coded key must be one the game can read (the 30 are worked out from the game itself),
  and `Plugin.Update` must call `WaypointManager.Tick` in a try block of its own that reads no key; and the
  keys the game cannot read must still be the 30 listed for Valheim 1.0.16 (after a game update that changes
  them, this fails until the list in `preflight.ps1` is updated; update the lists in `Hotkeys.cs` and both package
  READMEs with it - the check does not read them)
- **Wayfinder contains any piece of coordinate entry or display** (the parser and formatter types, the
  console add path, the window's text box, the bulk-add, the raw-order config key, any `{0:0}, {1:0}`
  coordinate format string, any method that turns a world x/z into text) — and, conversely, if TomTom is
  missing any of them, so the check can't pass vacuously
- a location search could turn the server's answers into map pins: the answer prefix must hand answers to
  `LocationSearch.OnServerAnswer` and return, once, `SearchRules.VanillaMayHandle(pinName)` - a decision on the
  pin name alone, unit-tested, on the same token the requests carry, and the requests must carry exactly the
  name `SearchRules.RequestPinName` builds (the server echoes it); the request may be sent only from
  `LocationSearch.Ask`, branching directly on `AnswersIntercepted()`, which asks Harmony and returns a flag set
  only from the unit-tested `SearchRules.IsOurPrefix`; and nothing may call `Game.DiscoverClosestLocation` or
  `Minimap.DiscoverLocation`, which make saved pins
- Wayfinder's search does not read `MinimapAccess.IsExplored` - and, conversely, TomTom's does, so the check
  can't pass vacuously (that Wayfinder's filter is the one handed to `SearchRules.FinishHits` is not checked yet)
- the search's decisions are not wired as the tests assume: `LocationSearch.Finish` must hand the search's own
  places and query to `SearchRules.FinishHits` exactly once, TomTom's with no filter; `SearchJob`'s scan must gate
  the known-empty drop with `SearchRules.KnownEmptyApplies(OnServer, CheckChests, Query)` and give it the zone set
  `SearchRules.NoteObject` fills; and it must check `SearchRules.WorldMade` (the object's creator and cheat flag,
  read with the defaults 0 and false) before an object counts, skipping the object when it is false
- a waypoint could be made by a code path nobody checked: `WaypointManager.Add`, `AddFromPin`, `AddMany` and
  `AddRange` may be called only from the known places (the map click, the window's and the console's own-position
  and typed entries, Find), and a `Waypoint` may be constructed only in `WaypointManager`'s own `Add`, `AddMany`,
  `AddFromPin` and `FromEntry`
- Wayfinder's map click places a waypoint without asking `MinimapAccess.IsExplored` first (once, branching on the
  result at once, with the click's own position, before `WaypointManager.Add`) - and, conversely, TomTom's map click
  asks it
- a routed call is sent that is not on the list: `RPC_DiscoverClosestLocation` from `LocationSearch.Ask` only,
  and the plugin's own `DoomMachine.Waypointer.ToServer` (from `FindLink`) and `.ToClient` (from `FindServer`) -
  each checked by the literal name the call sends
- the server side is off: the plugin must declare both `valheim.exe` and `valheim_server.exe`; the WhoMayFind
  prefix must leave every request without this plugin's token to vanilla (branching directly on
  `SearchRules.VanillaMayHandle(pinName)`, and the way it branches is followed to `return true`) and otherwise
  return only `FindServer.CallerMayFind()` or false; that decision must come from the unit-tested
  `FindProtocol.CallerMayFind` with no constant argument, the policy from `FindProtocol.MayFind`, and the admin
  check from the game's own `ZNet.IsAdmin`; `Plugin.ApplyPatches` must set `RoutedCallContext.Available` (without
  the connection patch only `Everyone` lets a caller through); a player must be known by the connection the call
  came on (`RoutedCallContext`, set from `ZRoutedRpc.RPC_RoutedRPC`'s `rpc` and cleared by a finalizer): the
  server's message handler takes no sender and asks `FindServer.CallingPeer`; `FindServer.OnMessage` must call
  `MayFind`, and `FindServer` must never call `SearchRules.KeepNearest` (Wayfinder filters on the player's side);
  and `FindLink.OnToClient` must compare its sender with the server peer's id. (These look at the compiled calls
  and comparisons; they do not prove what each result decides.)
- with Valheim's dedicated server installed beside the game (or `-ServerDir`), any reference fails to resolve
  against the server's own assemblies - a separate build of the game, not a copy (skipped, and not counted,
  without one)
- a referenced assembly can't be resolved from the game folder (a game folder that is not found at all is named,
  with `-ValheimDir` to point it elsewhere)

On a game build other than Valheim 1.0.16 it also prints a note: Find's catalogue was derived for 1.0.16, and no
check can see whether a newer build moved the chests or nests it lists.

Run it after every Valheim update.

## Conventions

- **Keep the source to C# 5.** `build.sh` compiles it with the legacy `csc.exe` that ships with Windows,
  so the plugins can still be built on a machine without the .NET SDK. The projects set `LangVersion=5`
  too, so the SDK build and the IDE reject newer syntax as it is typed. One consequence: C# 5 does not
  cache static method-group delegates, so a delegate passed every frame is cached by hand
  (`WaypointWindow.DrawWindowFn`).
- **Every map marker goes through `WaypointManager.CreateLocalOnlyPin`.** It is the single place the
  local-only guarantee lives; preflight enforces it.
- **A pin the player promotes is never modified.** Only markers the mod creates are forced local-only.
  A waypoint records that intent (`Borrowed`, persisted) separately from what is on the map right now
  (`OwnsPin`, runtime): when the player's pin is missing — another character, a looted tombstone — a
  local stand-in marks the spot, and the real pin is re-adopted as soon as it reappears.
- **Each edition keeps its own save folder** (`BepInEx/config/<GUID>/`), so coordinates entered in TomTom
  never carry over into Wayfinder.

## Implementation notes

- The arrow and window are **IMGUI**, and the arrow texture is rasterised at runtime (once per game
  launch). An AssetBundle was considered once a Unity Editor was available and turned down. A bundle
  must be built with a Unity no newer than the game's (6000.0.75f1) and re-checked whenever the game
  moves to a new engine version; it would add a binary that neither build path can reproduce, and it
  would change nothing per frame.
- Private game members (`Minimap.ScreenToWorldPoint`, `m_pins`, `PinInteractRadius`, `GetClosestPin`,
  `m_visibleIconTypes`, and in Wayfinder `IsExplored`) are reached with `HarmonyLib.AccessTools` reflection, so
  the plugins depend only on the shipped DLLs. If `PinInteractRadius` cannot be read, map clicks reach as far as
  its public parts say (`m_removeRadius` times the zoom), with one warning.
- Input is taken over by making `TextInput.IsVisible` report `true` while the window is open. That one
  flag is what `Player.TakeInput` and `GameCamera.UpdateMouseCapture` consult, so it releases the cursor
  and blocks movement, the hotbar and Use together — the same approach ConfigurationManager and
  MeasurementTracker take. The inventory key, camera zoom and gamepad hotbar check `Chat.HasFocus`
  instead, so that is forced `true` too (a postfix at `Priority.Last`, because Chatter also sets it), and
  `ZInput.GetMouseScrollWheel` reads 0, so the wheel only scrolls the list. Console key bindings still fire
  while the window is open. Clicks on the window are kept from reaching the large map underneath it
  (`OnMapLeftDown`, `OnMapDblClick` and the map image's `UIInputHandler.OnPointerClick`).
- Valheim's pin hit-tests (`GetClosestPin`, `HavePinInRange`) skip `save: false` pins, so the mod finds its
  own markers by walking `m_pins` itself. Deletion is intercepted at `Minimap.RemovePin(Vector3, float)`,
  where the mouse right-click, touch long-press and the gamepad delete button all end up: a waypoint
  marker within reach wins, so a delete aimed at one can never remove the player's own pin.
- Waypoints are saved to the world they were loaded for, a pending change is written before the next
  world's queue replaces it, and a failed write is retried after a growing delay rather than every frame.
  The route file is replaced crash-safely (`SafeFile`, the pattern of the game's own
  `FileHelpers.ReplaceOldFile`): the new text goes to `.new` and is flushed to disk, the current file steps
  aside as `.old` (or as `.old2` while another program holds a `.old` left by an earlier save), `.new` takes its
  name, and the copy goes. Once a route file exists, a crash or a power cut
  during a save leaves the previous or the new text complete on disk - in the file itself or in the
  `.new`/`.old` beside it (after a power cut, provided the drive honours the flush to disk) - because `.new`
  is then only written while that file exists. When the file is missing, the next start renames the copy
  back rather than rewriting it (and reads it in place if it cannot be moved yet). A stray `.new` beside an
  intact file is an unfinished save and is ignored. The first save of a world has nothing older to protect.
  A route that could not be read when its world loaded (another program held it) is never saved over:
  `SaveIfDirty` returns while `RouteReadGate` is pending, the read is tried again after a growing delay (2, 4, 6 s
  ... at most 30 s) and once more when another world loads or the game closes, and once it succeeds what was queued
  meanwhile comes first
  and the saved waypoints follow (`RouteMerge` leaves out a restored waypoint that matches a queued one). The same
  happens when `PersistWaypoints` is turned on in a world that was entered with it off.
- The local player is destroyed and recreated on every death while the map survives, so nothing is reset
  when the player is briefly missing; a change of world is detected by world UID instead.
- **Find** takes its places from the game's own list of location instances on a server
  (`ZoneSystem.GetLocationList`, which knows which candidate of a unique location is placed). On a client it
  asks the server, one location type every 0.2 s, with the request a Vegvisir makes
  (`RPC_DiscoverClosestLocation` with `discoverAll`, which checks no permission), then one request with a
  single guaranteed answer (the closest `StartTemple`) that marks the end, since routed calls keep their order.
  Vanilla would turn every answer into a `save: true` pin through `Minimap.DiscoverLocation`, so a prefix on
  `Game.RPC_DiscoverLocationResponse` catches this mod's answers first, and no request is sent unless
  `Harmony.GetPatchInfo` shows that prefix in place. Loose objects and chests are read from the ZDOs the game
  holds, one 64 m zone at a time through `ZDOMan.FindSectorObjects`, stopping for the frame once about 1.5 ms of
  work is done (checked after each zone and after each chest read). A chest's contents are a byte array
  (`ZDO.GetByteArray(s_items)`, as `Container.Load` reads them); the chest check runs only on a server and only
  for placed locations. A unique location is resolved over every candidate before the range cut. The route is
  planned over the nearest few hundred places (four times `MaxSearchWaypoints`, at least 100): nearest-neighbour
  from the nearest spot, then 2-opt with that first stop fixed. A **Bee Nest** grows only inside its place, and the
  game places a location wholly inside one 64 m zone, so a nest found stands for the place in its zone
  (`SearchRules.DropPlacesWithObjectsInZone`, on the player's side after Wayfinder's filter: `SearchRules.FinishHits`
  keeps that order), and a server asked to skip places known not to hold what is looked for (`SkipCheckedChests`)
  leaves out a place whose zone it has generated with no nest left in it (`SearchRules.DropPlacesKnownEmpty`, from
  the zones of every nest it read, in range or not). These decisions are Unity-free, so the tests check their
  order and rules; preflight checks what the game code hands them. An object is left out when a player placed it
  through the build system (`s_creator`) or spawned it with the console's `spawn` command (`s_cheated`)
  (`SearchRules.WorldMade`). The `location` command marks only a place's own objects, not the rooms its generator
  adds (a nest in an Abandoned Village or Draugr Village room counts), and the `vegetation` command marks nothing, so
  what they make counts. The rock Find keeps its key, `Mysterious Rock`, which servers look a Find up by, and shows the game's own
  name, Rock (`SearchQuery.Title`); the game's "Mysterious Rock" is the pet rock a player builds, never listed.
  The Wooden Greatsword's Infested Mines keep their chests in treasure rooms the generator builds about 5000 m above
  the entrance but inside the entrance's own 64 m zone, so the chest check's horizontal 64 m covers them.
- **The 3D arrival test** (`Use3DDistance`) measures a waypoint that has a height of its own at that height. One
  without (a map click, two typed numbers, a pin placed on the map) and a place Find found - whose height is the world
  generator's estimate, before the terrain is built and levelled, and more than 10 m off the ground at about 1% of
  places - is measured at the loaded ground under it (`Heightmap.GetHeight`, terrain edits included; the rule is the
  Unity-free `ArrivalRules`), and horizontally over water or where the ground is not loaded. Only arrival is affected:
  nothing is stored, and TomTom's readout is unchanged. A Find place's height is saved as `2` in the route file's
  hasElevation column, which a plugin before 1.5.0 reads as no height.
- **TomTom reads the game's `pos` console line** (Valheim's `Player position (X,Y,Z): ...` and Server Devcommands'
  `(X,Z,Y)`) in the axis order its header names, before the digit-grouping checks its zone and distance would trip;
  only inside its vector, whose values both write with `", "` between them, is a decimal comma read.
- **Wayfinder's map click places a waypoint only on explored land** (`MinimapAccess.IsExplored`, as Find uses): a
  click in the fog is consumed and does nothing, with no message. Removing a marker and following a pin work
  anywhere.
- **Find needs nothing beyond BepInEx and the running game; it does not use SeedLab.** Every result comes
  from the game as it runs: the location list (as the host) or the server's answers (when joining), the world
  objects the game has loaded, and the chests' saved contents. What is fixed in the plugin is the catalogue
  (`src/SearchCatalog.cs`) - which kinds of place can hold a chest with each item or a Bee Nest, and which places
  are the merchants and the rocks - and the distances Find looks within (a place's chests within 64 m of it, or 96 m for
  the places a dungeon generator builds; loose Rocks within 40 m of a Big Rock Clearing count as the
  clearing). The catalogue was derived during development from a dump of Valheim 1.0.16's own prefab data -
  every location's children, their chests and loot tables, and the rooms dungeon generators build - taken from
  the running game with SeedLab's game-data dumper (https://github.com/DoomMachine/Valheim-SeedLab,
  `tools/SeedLab.Dumper`; the dump itself is not published), and checked again against the same dump and the
  decompiled game code on 2026-09-28; the Bee Nest's places were derived the same way on 2026-09-29, and also
  checked against every object in the game's asset bundles. So a Valheim update that adds or moves such chests or
  nests leaves the catalogue behind until its lists are derived again from the new build: preflight catches game methods that were
  renamed, removed or changed their signature, not changed chest lists.
- **On a server** the same DLL runs its server side. On a dedicated server (`Paths.ProcessName` is
  `valheim_server`; the game's own `ZNet.IsDedicated()` needs a `ZNet` that does not exist yet in `Awake`) it binds
  only `[6 - Server]` and applies two patches. A player's plugin says hello once per connection over a routed call
  of its own; a server with the plugin answers with its version and whether that player may use Find, and then
  answers each Find with one `SearchJob` run on its own data - exactly the host's answer - sent back in messages of
  256 places at most (about 4 KB; Steam's limit per message is 512 KB), everything in range, since Wayfinder filters
  by exploration afterwards. A vanilla server drops the unknown call silently, and after 5 s the player's Find asks
  the way a Vegvisir does, as before 1.3.0. **A routed call's sender field is not checked by the game**
  (`ZRoutedRpc.RPC_RoutedRPC` copies it from the packet), so a player could name an admin, or the host: the server
  therefore knows a player by the connection the call came on, taken from `RPC_RoutedRPC`'s own argument, and runs
  the game's admin check (`ZNet.IsAdmin`, `adminlist.txt`) on that connection's host name. `WhoMayFind` is a rule
  for players who use this plugin: any game can already send the Vegvisir request itself and get vanilla pins.

## History

**1.5.0** — Find: the Wooden Greatsword; `pos` lines; arrival on the ground; Wayfinder clicks only on explored land.

- new: Find looks for the **Wooden Greatsword**, in the chests of the six kinds of Mistlands place Valheim 1.0.16's
  own data lists: the two kinds of Infested Mine (their treasure rooms, built high above the entrance - the waypoint
  is the entrance), three kinds of ruined Dvergr tower and the rock spire. The game names only the mines; the others
  are "Ruined Dvergr Tower" and "Rock Spire" here
- new (TomTom): the game's `pos` console line can be pasted whole - Valheim's and Server Devcommands' - and is read
  in the axis order its header names, a decimal comma in its values included
- new: with `Use3DDistance` on, a waypoint without a height of its own (a map click, two typed numbers, a pin placed
  on the map) and a place Find found are measured at the ground under them once it is loaded near the player, and
  stay horizontal over water; before, such a waypoint was always horizontal, and a Find place's generator height could
  sit more than 10 m from the real ground
- new (Wayfinder): a map click places a waypoint only on land the map shows as explored; a click in the fog does
  nothing. Removing a marker and following a pin work anywhere
- refactored: the arrow's caption cache (`CaptionCache`, `CaptionWidth`) and the route file's reader and writer
  (`RouteFile`) are Unity-free, so the tests reach them
- 44 new tests (248 → 292), and preflight checks that every code path making a waypoint is a known one and Wayfinder's
  fog rule for a map click (59 → 61 checks per edition)

**1.4.1** — fixes: an unreadable saved route is never overwritten, rocks have their own name, safer coordinate input.

- fixed: when a world's saved route could not be read as the world loaded (another program - a backup, sync or
  antivirus tool - held the file), the queue started empty and the next change replaced the saved route. Now the
  file is never saved over while it cannot be read: changes are kept, the read is tried again every few seconds (at
  most every 30 s), and once it succeeds what was queued meanwhile comes first and the saved waypoints follow.
  Turning `PersistWaypoints` on in a world works the same way: the saved route is joined, not replaced
- fixed: a leftover `.old` copy held open by another program no longer stops saving: the current file steps aside
  as `.old2` instead
- fixed: the loose rocks Find lists are named "Rock", the game's own name; they were called "Mysterious Rock", which
  in the game is the pet rock a player builds. The Find entry reads "Rock", and its key stays "Mysterious Rock", so
  servers and players on other versions still agree. Waypoints already queued keep their old name. A rock or nest
  a player placed through the build system or spawned with the console's `spawn` command is left out (on a server,
  when it runs 1.4.1 or later)
- fixed (TomTom): coordinate lines that were silently misread are refused, remedy first: a comma between two digits
  where reading it as a decimal comma would name another place (`12,5 30`, `1234,5; -567,25`), more than three
  numbers, and a first word that starts like a number (`1234m, -567, 20`). A decimal comma is not accepted: it would
  silently move lines that work today
- fixed: the arrow's captions: a name the game cannot translate is no longer measured again every frame, a caption
  is measured again if measuring ever fails, a character is never cut in half before "...", and trailing spaces
  never get "..."; the arrow's rotation and colour are restored even if drawing fails
- fixed: if a game update renamed the private `PinInteractRadius`, map clicks would have reached only 12 m; they now
  reach as far as its public parts say, with one warning
- fixed: a key Valheim cannot read is now named in the warning for every setting that uses it, and the window's map
  hint says when its modifier key cannot be read
- docs: No Map worlds, an Uninstall section, a Linux Alt-click note (not tested), `Use3DDistance` for map clicks, run
  Find again for the merchants after a game update, and the server texts (`WhoMayFind`'s 15 to 20 seconds, restart a
  rented server after changing it, who rewrites the settings file, what a server's log records, `;` in the console
  with Server Devcommands)
- 76 new tests (172 → 248), and preflight checks that every patch class is applied, that an unread route is
  not saved over, how the search's decisions are wired, and that the game's unreadable keys are still the 30 known
  ones (53 → 59 checks per edition); `run-tests.sh` stops a run after `TEST_TIMEOUT` seconds

**1.4.0** — Find: Bee Nests.

- new: Find looks for **Bee Nests** (the wild nest, `Beehive`, not a beehive a player builds). A nest grows only
  inside certain places, each with its own chance rolled when the place's area is first generated: eleven kinds of
  Abandoned House (1 in 4), the Contested Tower (about 23%), the Bear Cave (1 in 2, on its fir tree), and some of
  the rooms the fenced Meadows farms ("Abandoned Village"; the game gives them no name) and the Draugr Villages
  build. These 16 location types were read from Valheim 1.0.16's own data; an inventory of every object in the
  game's asset bundles found no other source (no tree or other vegetation carries a nest outside these places)
- a nest found is queued as "Bee Nest" in place of its place; a place not known to hold one is queued as, say,
  "Abandoned House (Bee Nest)". Nests are known wherever the land has been generated as the host or with a server
  that runs the plugin (1.4.0 or later), and otherwise only where the player has been since joining
- as the host, or with a server that runs the plugin (1.4.0 or later), TomTom's `SkipCheckedChests` now also leaves
  out places whose area has been generated without a nest left in it (none grew there, or it was destroyed)
- the server messages are unchanged: a server on 1.3.x answers a Bee Nest Find as a query it does not know, and the
  player's Find then asks it the way a Vegvisir does
- the search's decisions on the player's side (the explored filter, then the rocks of a clearing, then the nest
  merge) and the server's known-empty decisions are Unity-free SearchRules members, so the tests reach them
- 15 new tests (157 → 172)

**1.3.1** — the arrow's captions are no longer cut off, and the Sealed Tower has its name.

- fixed: a long caption under the arrow was cut off at both ends ("Abandoned House (Mysterious Axe Head)  (2
  left)"), because every caption was drawn in a box of a fixed width. Each caption's box is now as wide as its
  text, centred under the arrow and moved sideways only as far as needed to stay on screen; a name wider than the
  screen is shortened at its end with "...", and the count of waypoints left is kept
- fixed: Find for the Wooden Atgeir named Hildir's Plains location "Hildir's Plains Fortress"; the game calls it
  the Sealed Tower, and so does Find now (found by checking every place and item of Find's catalogue against the
  game's own 1.0.16 data again)
- changed: the window's queue and the active target's line now wrap a long waypoint name onto a second line by
  their own style, rather than leaving it to the default label style
- 5 new tests (152 → 157)

**1.3.0** — servers: the plugin on a dedicated server or as the host answers other players' Find.

- new: the same plugin runs on a dedicated server (`valheim_server`), with only its server side: no window, arrow
  or map, and a config file that holds only `[6 - Server]`. As the host of a Start Server game, the player's own
  plugin is the server's
- new: a player who joins such a server gets the host's answer to Find - only the real merchant or Big Rock
  Clearing once fixed, loose Mysterious Rocks anywhere already generated, and `SkipCheckedChests` - in one request
  instead of one per location type. Either edition on the server serves both editions' players; with any other
  server, Find asks the way a Vegvisir does, as before
- new: `WhoMayFind` (Everyone, AdminsOnly, Nobody), the server's rule for which joining players may use Find; it
  also applies to plugins older than 1.3.0, whose requests carry the plugin's token. A refused player sees it in
  the window's Find header. The server knows a player by the connection, not by the call's forgeable sender field
- an adversarial review before release found no broken invariant, and had fixed: a server search that throws
  was sent as if complete (it is now dropped and the player told the server is busy); WhoMayFind trusted any call
  if the connection patch failed to apply (now only `Everyone` does then); a refusal cached at join time kept the
  buttons off after the player was made an admin (the server now decides every request); searches kept running
  for players who had left or were no longer allowed; Wayfinder could log a count of places; and preflight's
  first server checks let six plausible regressions through (an inverted branch among them) - all six now fail it
- 14 new tests (138 → 152): the server messages - round trips, and truncated, oversized or foreign input decoded
  as nothing rather than thrown on - the WhoMayFind decision, and keeping the nearest places; preflight gains
  the routed-call list, the server side and the check against the dedicated server's own assemblies (49 → 53
  checks with a dedicated server installed); every one of 36 deliberately broken builds fails it, 17 of them new
  for the server side
- checked on a real dedicated server: it loads the plugin and gets ready (`Applied 2 of 2 patches.`, `Ready to
  answer players' Find`); a player joining such a server has not been tested in play yet

**1.2.1** — two fixes to Find's "(possible)" spots.

- fixed: a merchant or Big Rock Clearing with a single possible spot left, not placed yet, was treated as
  "(possible)" by the host (TomTom marked it so, Wayfinder left it out) but as the real place by a player who
  joined; it is the real place for both now, since it is the only place the game can put it
- fixed: when the server did not answer every request of a joining player's Find in time, a merchant or Big Rock
  Clearing of which only one answer had arrived was taken as the real place; now TomTom keeps it "(possible)" and
  Wayfinder leaves it out, unless the merchant's map icon settles it (once a spot is placed the game drops every
  other one at once)
- 3 new tests (135 → 138)

**1.2.0** — Find: every place of one kind within a range, as a route.

- new: the window's Find section queues every place that can hold a chest with a wooden weapon or an axe head,
  every Big Rock Clearing and loose Mysterious Rock, or a merchant (Haldor, Hildir, the Bog Witch), within a
  range of the player (100 m to 10 km), as a route that starts at the nearest and is then shortened (2-opt).
  The location lists come from Valheim 1.0.16's own data, including chests in the rooms villages build
- the unique places (the merchants, the Big Rock Clearing) are queued as "(possible)" spots until one is fixed
  in the world, then only the real one
- TomTom, as the host, leaves out places whose chests are known to be filled without the item (`SkipCheckedChests`)
- Wayfinder queues only places whose centre lies on explored map (explored by the player or through a
  Cartography Table), and unique places only once fixed, and says nothing either way
- works as host and as client; as a client it asks the server, and the answers never become map pins
- 25 tests for the search's rules and route, on both runtimes (110 → 135); preflight checks that the server's
  answers cannot become pins and that only Wayfinder filters by exploration (44 → 49 checks); every one of 19
  deliberately broken builds fails it - a tripwire on the compiled code, not a proof

**1.1.2** — a key Valheim cannot read no longer stops the mod.

- fixed: Valheim throws on every read of 30 of the keys the configuration offers (symbol keys such as
  `Plus` and `Hash`, F13 to F15, the mouse wheel and a few others). Set as `ToggleWindowKey` or
  `SkipWaypointKey`, such a key stopped the waypoint tick on almost every frame (all but those spent typing
  in chat, the console or a text field): a world's saved route was not loaded, and no waypoint was reached,
  repaired or saved - a waypoint added on the map still got its marker. As `MapModifierKey`, Alt-click
  stopped working and every left click on the map logged an error. Now the key is ignored after one warning
  in the log, as if it were unbound, and nothing else is affected; choosing another key tries it afresh
- 13 tests for reading such keys, on both runtimes; preflight checks that configurable keys are read only
  through the guarded reader, that no hard-coded key is one the game cannot read, and that the waypoint tick
  runs apart from the keys (41 → 44 checks)

**1.1.1** — the route file is saved safely.

- fixed: saving a route overwrote the file in place, so a crash or power cut during the write could leave
  it empty and lose that world's waypoints. It is now written to a temporary file, flushed to disk and
  swapped in; once a world has a route file, an interrupted save leaves the previous or the new list
  complete on disk (after a power cut, provided the drive honours the flush), and the next start renames
  it back into place
- 19 tests for the crash-safe save and every state an interrupted save can leave, on both runtimes

**1.1.0** — fixes and a code review.

- fixed: with a controller, deleting a waypoint marker on the big map deleted the nearest of your own
  saved pins instead
- fixed: clicks on the window reached the large map underneath it — a double-click placed a saved,
  shareable pin, a middle-click pinged everyone, a right-click deleted a pin
- fixed: Alt-click followed pings, shouts, other players' markers and event markers
- fixed: Tab opened the inventory and the mouse wheel zoomed the camera while the window was open;
  Esc and a controller's B now close it
- fixed: the arrow showed while sleeping, in cutscenes and over the pause menu, inventory and traders
- fixed: a failed save was retried, and logged, every frame; a quit while the next world loaded could write
  one world's route into the other's file
- fixed: `waypoint` commands at the main menu changed the previous world's list
- fixed (TomTom): a pasted typographic minus, dash or hyphen, full-width digits or no-break spaces silently moved the
  waypoint; `Y=30 X=-500` was read in written order rather than by label; labelled input was remapped in
  raw-order mode
- smaller fixes: an Alt-double-click, the arrow's first moments at a new waypoint, the arrival-time
  estimate right after a respawn, hand-edited route files
- about 1 KB less garbage per frame while navigating and about 28 KB less while the window is open
- preflight checks the local-only guarantee by value, guards the player's own pins, and resolves every game
  member against the shipped assemblies (25 → 39 checks); tests also run on the game's own mscorlib
- removed public members nothing in the mod used: `Plugin.Instance`, `Waypoint.Id`,
  `WaypointManager.MarkDirty`, `WaypointWindow.SetStatus` and `ForceClose`, and the
  `Minimap_RemovePinUnderPointer_Patch` class (its job is now done by `Minimap_RemovePin_Patch`)
- the window no longer opens on top of the pause menu
- behaviour otherwise unchanged; the config file needs no changes

**1.0.0 — first release of TomTom and Wayfinder.** Both grew out of a single plugin called *Waypointer*,
built under an earlier GUID and played live for several sessions but never released. They keep
version 1.0.0 because each is a new plugin with its own identity. Changes from Waypointer:

- split into the two editions above, published by DoomMachine
- window key **F11** by default (was F9 — which turned out to be Valheim's gamepad-layout key)
- arrival radius **10 m** by default (was 5)
- fixed: dying with waypoints queued duplicated their map markers on respawn, leaving orphans that
  neither the mod nor right-click could remove
- fixed: markers named with game tokens (the tombstone's `$hud_mapday 9`) now show translated text
- fixed: following one of your own pins could permanently turn into a mod marker after a relog or a
  character switch, stacking a duplicate on top of your pin
- Wayfinder shows no coordinates at all (found in review: the map-click readout made coordinate entry
  possible by trial and error)

## Credits

TomTom and Wayfinder were conceived and directed by DoomMachine, who set out what the mods should do, chose
between the designs and plays TomTom in their own game.

The code, tests and documentation were written by Claude, Anthropic's AI model, working in Claude Code under
DoomMachine's direction. Commits are authored by DoomMachine; Claude is credited here rather than as a
co-author.

## License and naming

MIT — see [LICENSE](LICENSE).

*TomTom* is named in honour of the World of Warcraft addon of that name; this project is not affiliated
with that addon, with TomTom N.V., or with Iron Gate AB.
