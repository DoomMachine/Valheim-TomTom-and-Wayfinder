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
| BepInEx GUID | `DoomMachine.TomTom` | `DoomMachine.Wayfinder` |
| Installed into the game by `dotnet build` | yes | no — packaged only |

**TomTom** is named in honour of the World of Warcraft addon that inspired the project. **Wayfinder** is
the immersion edition: a location cannot be looked up outside the game and walked straight to. That
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

---

## Layout

```
src/                     the shared source for both plugins
  Edition.cs             everything that differs between the editions (name, GUID, window id)
  CoordinateParser.cs    TomTom only - reading coordinates. Absent from Wayfinder.
  CoordinateFormat.cs    TomTom only - showing coordinates. Absent from Wayfinder.
  ...
Plugin.props             build settings shared by both projects (references, packaging, deploy)
TomTom/TomTom.csproj     sets EditionName=TomTom, deploys by default
Wayfinder/Wayfinder.csproj  defines WAYFINDER, excludes both Coordinate*.cs files, packages only
package/<Edition>/       manifest.json, icon.png and README.md for each package
tests/                   parser tests (built as TomTom, the only edition with a parser)
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
./run-tests.sh                                                         # 71 parser tests, on .NET and on Mono
powershell -ExecutionPolicy Bypass -File preflight.ps1                 # the installed TomTom
powershell -ExecutionPolicy Bypass -File preflight.ps1 -Edition Wayfinder -Plugin build/Wayfinder/Wayfinder.dll
```

`preflight.ps1` reads a compiled plugin and fails if:

- its `BepInPlugin` identity or its incompatibility with the other edition is wrong
- any Harmony patch target, or any private game member reached by reflection, has disappeared from the
  shipped game assemblies (the first thing a Valheim update breaks)
- anything could create a **shareable** map marker — markers must be `save: false` with owner 0, passed
  to `AddPin` and re-asserted after it, which keeps them out of the Cartography Table
  (`Minimap.GetSharedMapData`) and the player profile (`Minimap.GetMapData`); `AddPin` must be referenced
  exactly once, from `CreateLocalOnlyPin`, and nothing else may make a pin
- anything could change or remove a pin the mod did not create (a `PinData` store, a direct edit of the
  map's pin list, a wipe, or `RemovePin` anywhere but `RemoveOwnMarker`)
- a game, Unity, BepInEx or Harmony type or member the plugin calls no longer exists with the same
  signature, or a Harmony patch no longer names the exact overload it targets
- the `Chat.HasFocus` postfix loses the `Priority.Last` it needs to run after other mods' postfixes
- **Wayfinder contains any piece of coordinate entry or display** (the parser and formatter types, the
  console add path, the window's text box, the bulk-add, the raw-order config key, any `{0:0}, {1:0}`
  coordinate format string, any method that turns a world x/z into text) — and, conversely, if TomTom is
  missing any of them, so the check can't pass vacuously
- a referenced assembly can't be resolved from the game folder

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
  `m_visibleIconTypes`) are reached with `HarmonyLib.AccessTools` reflection, so the
  plugins depend only on the shipped DLLs.
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
- The local player is destroyed and recreated on every death while the map survives, so nothing is reset
  when the player is briefly missing; a change of world is detected by world UID instead.

## History

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

## License and naming

MIT — see [LICENSE](LICENSE).

*TomTom* is named in honour of the World of Warcraft addon of that name; this project is not affiliated
with that addon, with TomTom N.V., or with Iron Gate AB.
