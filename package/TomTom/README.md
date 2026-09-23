# TomTom

Waypoints for Valheim, named in honour of the World of Warcraft addon that inspired it. Queue up
coordinates or pick places on the world map, get temporary markers, and follow an on-screen arrow that
turns green as you line up with the target. Reach a waypoint and the marker and arrow clear themselves,
**Location Reached** is announced, and the next one in the list takes over.

By **DoomMachine** · version 1.0.0 · a BepInEx 5 plugin.

> Prefer not to be able to look locations up at all? **Wayfinder** is the same mod without coordinates —
> waypoints come only from the world map or from where you stand, and no coordinates are ever shown.
> The two cannot run together; install one.

---

## Quick start

1. Press **F11** to open the window. *(F11 is also Valheim's screenshot key — see Keys below.)*
2. Paste coordinates, one per line, and press **Add to queue**.
3. Follow the arrow.

Or open the world map, hold **Left Alt** and click.

## Coordinates

Type **X, Y** and an **optional third value for elevation**:

```
1234, -567
1234, -567, 30
1234 -567 Silver vein
Stone circle: 1234, -567
X: 1234  Y: 56  Z: -789
```

- Two numbers are required; a third is the elevation.
- Words after the numbers (or before a colon) become the waypoint's name.
- One waypoint per line — paste a whole list at once. A line that can't be read is skipped and reported.
- Use a **dot** for decimals (`12.5`); a comma separates fields. One exception: a comma followed by
  exactly three digits with no space (`1,234`) looks like digit grouping, so the line is refused rather
  than guessed at — write `1, 234` if you really mean two fields.
- Axis labels are honoured literally: `Y` is Valheim's **altitude**, so `X: 1234 Y: 56 Z: -789` means
  x 1234, z −789, 56 m up.
- To paste Valheim's raw `x y z` instead, turn on `InputIsRawValheimXYZ`.

When you give no elevation, arrival is judged on horizontal distance, which is what you want for a far,
unexplored target whose height can't be known in advance.

## The window

- **Add to queue / Replace queue** for the pasted coordinates
- **Add my position** — marks where you stand. It won't count as reached until you've walked away once.
- **Nearest first** — jump to whichever queued waypoint is closest
- the **active target** with its distance, **Skip this one** and **Clear all**
- the **queue**, each entry with **Go** (make it active) and **X** (delete)

While it's open your character ignores the keyboard, so you can type freely.

## The world map

Hold **Left Alt** and left-click:

| You click | Result |
| --- | --- |
| an empty spot | a waypoint there |
| one of your own pins | that pin becomes a waypoint (the mod never changes or deletes it; if the pin isn't on the map, e.g. on another character, a stand-in marks the spot until it is) |
| a waypoint marker | the waypoint is removed |

Plain clicks keep Valheim's normal behaviour. Vanilla right-click-delete works on waypoint markers too.

## Console

```
waypoint 1234 -567 Silver vein     add a waypoint
waypoint list                      the queue, with distances
waypoint remove 3                  delete entry 3
waypoint next                      skip the active waypoint
waypoint here [name]               mark where you stand
waypoint closest                   re-target the nearest queued waypoint
waypoint clear                     empty the queue
waypoint gui                       toggle the window
```

## The arrow

Points toward the active waypoint relative to where the camera looks, coloured **green** when you're
heading straight at it, **yellow** off to the side and **red** facing away. Underneath: the name, the
distance and an estimated time of arrival from how fast you're actually closing in. It hides with the
HUD, while the big map is open, and when you're dead or teleporting.

## Multiplayer

Waypoint markers are **local to you**. They are created with the game's `save: false` flag, which keeps
them out of both the Cartography Table (`Minimap.GetSharedMapData`) and your saved map
(`Minimap.GetMapData`) — both only export pins with that flag set. A pin of your own that you follow
keeps its normal flags and keeps syncing as usual.

## Keys

`ToggleWindowKey` defaults to **F11**. That is also Valheim's own **screenshot** key, so each press
also saves a picture to `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\screenshots`. If you'd rather
not, **F4** is free; **F3** is too, except that the game hides the HUD on Ctrl+F3. Everything else is
taken by the game (F2 connect panel, F5 console, F9 gamepad layout) or, in the original install, by
other mods (F1 ConfigurationManager, F6/F8/F10 PlantEasily, F7 MobTracker).

## Configuration

`BepInEx/config/DoomMachine.TomTom.cfg`, also editable in-game through ConfigurationManager.

| Setting | Default | |
| --- | --- | --- |
| `ToggleWindowKey` | `F11` | see Keys |
| `MapModifierKey` | `LeftAlt` | hold while clicking the map |
| `SkipWaypointKey` | `None` | optional |
| `ArrivalRadius` | `10` | metres before a waypoint counts as reached |
| `Use3DDistance` | `false` | include altitude in the arrival test |
| `InputIsRawValheimXYZ` | `false` | read input as Valheim's own x/y/z |
| `PersistWaypoints` | `true` | remember the queue per world |
| `PinType` / `PinLabel` | `Icon3` / `Waypoint` | marker icon and label |
| `ShowArrow`, `ArrowSize`, `ArrowScreenX/Y`, `ArrowOpacity` | | arrow placement |
| `ShowDistance`, `ShowWaypointName`, `ShowTimeToArrival` | `true` | captions |
| `HideArrowWhenMapOpen` | `true` | |
| `ColorFacingTarget/Sideways/FacingAway` | green/yellow/red | hex colours |

Saved queues: `BepInEx/config/DoomMachine.TomTom/waypoints_<worldUID>.txt`, one per world.
