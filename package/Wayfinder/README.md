# Wayfinder

Immersive waypoints for Valheim. Mark a spot on your world map — or pick one of your own map markers —
and an on-screen arrow guides you there, turning green as you line up with it. Arrive and the marker
and arrow clear themselves, **Location Reached** is announced, and the next waypoint takes over.

By **DoomMachine** · version 1.1.1 · a BepInEx 5 plugin.

**There are deliberately no coordinates — none to type in, and none shown.** You can only navigate to
places you have marked on your own map or are standing on, so nothing can be looked up outside the game
and walked straight to. Waypoints are shown by name and distance only: a coordinate readout would let a
map click be nudged, try by try, onto a looked-up location. If you do want coordinates, that's
**TomTom** — the same mod with them included. The two cannot run together; install one.

---

## Quick start

1. Open the world map, hold **Left Alt** and click somewhere — or click one of your own markers.
2. Close the map and follow the arrow.

Press **F11** for the Wayfinder window. *(F11 is also Valheim's screenshot key — see Keys below.)*

## The world map

Hold **Left Alt** and left-click:

| You click | Result |
| --- | --- |
| an empty spot | a waypoint there |
| a pin already on your map | that pin becomes a waypoint (the mod never changes or deletes it; if the pin isn't on the map, e.g. on another character, a stand-in marks the spot until it is). Any pin within reach counts, including one shared to you through a Cartography Table; pings, shouts, player markers and event markers are ignored |
| a waypoint marker | the waypoint is removed |

Queue up several and they are visited in order. Plain clicks keep Valheim's normal behaviour, and an
Alt-double-click places a single waypoint.
Right-click delete — and the controller's delete button on the big map — works on waypoint
markers too. When a waypoint marker is within reach, the waypoint is what gets removed, never one of
your own pins; deleting one of your own pins that a waypoint follows drops that waypoint as well.
Clicks on the window itself never reach the map underneath it.

If you have hidden the waypoint marker's icon type with the map's icon filter, adding a waypoint shows it
again: the game re-enables an icon type whenever a pin of that type is added. Pick a different
`PinType` in the config if you keep that icon hidden.

## The window

- **Add my position** — marks where you stand. It won't count as reached until you've walked away once.
- **Nearest first** — jump to whichever queued waypoint is closest
- the **active target** with its distance, **Skip this one** and **Clear all**
- the **queue**, each entry with **Go** (make it active) and **X** (delete)

While it's open, the game ignores your keyboard: Tab doesn't open the inventory and the mouse wheel
scrolls the list instead of zooming the camera. Key bindings you made with the console's `bind` command
still run. **Esc** — or **B** on a controller — closes it. The window needs a keyboard and mouse.

## Install

Needs **BepInEx for Valheim**. Unpack this zip into a folder of its own under
`BepInEx/plugins/` (e.g. `BepInEx/plugins/DoomMachine-Wayfinder/`), or hand the zip to a mod manager.
It loads when `BepInEx/LogOutput.log` says `Loading [Wayfinder 1.1.1]`. TomTom and Wayfinder exclude each
other: install one.

## Console

Valheim's console is off unless you turn it on — the `-console` launch option, or the game's own
console setting; then **F5** opens it.

```
waypoint list              the queue, with names and distances
waypoint remove 3          delete entry 3
waypoint next              skip the active waypoint
waypoint here [name]       mark where you stand
waypoint closest           re-target the nearest queued waypoint
waypoint clear             empty the queue
waypoint gui               toggle the window
```

`waypoint 1234 -567` is refused — Wayfinder has no coordinate entry.

These commands work only inside a world.

## The arrow

Points toward the active waypoint relative to where the camera looks, coloured **green** when you're
heading straight at it, **yellow** off to the side and **red** facing away, with the name, distance and
an estimated time of arrival underneath. It hides with the HUD, while you sleep or watch a cutscene,
while the pause menu, the inventory or a trader is open, while the big map is open, and when you're dead
or teleporting.

## Multiplayer

Waypoint markers are **local to you**. They are created with the game's `save: false` flag, which keeps
them out of both the Cartography Table and your saved map. A pin of your own that you follow keeps its
normal flags and keeps syncing as usual.

## What Wayfinder does not police

Wayfinder removes coordinates from the mod itself. It can't stop what other mods add or deliberate
workarounds outside it:

- **A map-coordinate mod undoes it.** A mod that prints the cursor's coordinates on the map — such as
  **MapCoordinateDisplay** — lets you hover to a looked-up spot and click it. For Wayfinder to mean
  anything, turn that off (`ShowCursorCoordinates = false`, or disable the mod).
- Hand-editing Wayfinder's save file, or following a map pin that a dev-commands mod placed at typed
  coordinates. Those are the same kind of choice as using a teleport command.

## Keys

`ToggleWindowKey` defaults to **F11**. That is also Valheim's own **screenshot** key, so each press
also saves a picture to Valheim's screenshots folder (on Windows, `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\screenshots`). If you'd rather it
didn't, rebind it: vanilla Valheim also uses F2 (connect panel), F5 (console) and F9 (gamepad
layout), plus Ctrl+F1 (mouse capture) and Ctrl+F3 (hide HUD), and other mods can hold keys of
their own — choose one that nothing you run already uses.

## Configuration

`BepInEx/config/DoomMachine.Wayfinder.cfg`, also editable in-game through ConfigurationManager.

| Setting | Default | |
| --- | --- | --- |
| `ToggleWindowKey` | `F11` | see Keys |
| `MapModifierKey` | `LeftAlt` | hold while clicking the map |
| `SkipWaypointKey` | `None` | optional |
| `ArrivalRadius` | `10` | metres before a waypoint counts as reached |
| `Use3DDistance` | `false` | include altitude in the arrival test |
| `PersistWaypoints` | `true` | remember the queue per world |
| `PinType` / `PinLabel` | `Icon3` / `Waypoint` | marker icon and label |
| `ShowArrow`, `ArrowSize`, `ArrowScreenX/Y`, `ArrowOpacity` | | arrow placement |
| `ShowDistance`, `ShowWaypointName`, `ShowTimeToArrival` | `true` | captions |
| `HideArrowWhenMapOpen` | `true` | |
| `ColorFacingTarget/Sideways/FacingAway` | green/yellow/red | hex colours |

Saved queues: `BepInEx/config/DoomMachine.Wayfinder/waypoints_<worldUID>.txt`, one per world — kept
apart from TomTom's, so coordinates entered in TomTom never carry over into Wayfinder.
The file is replaced safely. If a save is interrupted, a `.new` or `.old` copy may be left beside it;
when the file itself is missing, the next start puts that copy back, and the next save tidies up.

---

Conceived and directed by DoomMachine; written by Claude, Anthropic's AI model, under DoomMachine's
direction. MIT licensed — the LICENSE file in this package is the full text. Source, issues and newer
releases:
https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder
