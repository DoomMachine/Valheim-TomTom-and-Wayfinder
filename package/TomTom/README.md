# TomTom

Waypoints for Valheim, named in honour of the World of Warcraft addon that inspired it. Queue up
coordinates or pick places on the world map, get temporary markers, and follow an on-screen arrow that
turns green as you line up with the target. Reach a waypoint and the marker and arrow clear themselves,
**Location Reached** is announced, and the next one in the list takes over.

By **DoomMachine** · version 1.2.1 · a BepInEx 5 plugin.

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
- Words after the numbers (or before a colon) become the waypoint's name. Words before the numbers also
  work without a colon, unless the name ends in a number: `Camp 2 1234, -567` is read as x 2, y 1234,
  elevation −567. Write `Camp 2: 1234, -567` or `1234, -567, Camp 2` instead.
- One waypoint per line — paste a whole list at once. A line that can't be read is skipped and reported.
- Use a **dot** for decimals (`12.5`); a comma separates fields. One exception: a comma followed by
  exactly three digits with no space (`1,234`) looks like digit grouping, so the line is refused rather
  than guessed at — write `1, 234` if you really mean two fields. A no-break or thin space inside a
  number (how some languages group thousands, often copied from web pages) is refused the same way; an
  ordinary space always separates two values, so `1 234` typed with the space bar is x 1, y 234.
- Text pasted from web pages and documents works: a minus sign, dash or hyphen in front of a number
  (`−1234`), full-width digits and punctuation, and no-break or thin spaces between values are read
  as their plain equivalents.
- Axis labels are honoured. With all three, `Y` is Valheim's **altitude**, so `X: 1234 Y: 56 Z: -789`
  means x 1234, z −789, 56 m up (with `InputIsRawValheimXYZ` on as well). With only `X` and `Y`, they are
  the two map axes, read by label in whichever order they are written.
- To paste Valheim's raw `x y z` instead, turn on `InputIsRawValheimXYZ`.

When you give no elevation, arrival is judged on horizontal distance, which is what you want for a far,
unexplored target whose height can't be known in advance.

## The window

- **Add to queue / Replace queue** for the pasted coordinates
- **Add my position** — marks where you stand. It won't count as reached until you've walked away once.
- **Nearest first** — jump to whichever queued waypoint is closest
- **Find** — every place of one kind within a range of you, as a route (see Finding places)
- the **active target** with its distance, **Skip this one** and **Clear all**
- the **queue**, each entry with **Go** (make it active) and **X** (delete)

While it's open, the game ignores your keyboard, so you can type freely: Tab doesn't open the
inventory and the mouse wheel scrolls the list instead of zooming the camera. The exception is key
bindings you made with the console's `bind` command: they still run, even while you type. **Esc** — or
**B** on a controller — closes it. The window needs a keyboard and mouse.

## Finding places

The window's **Find** section queues every place of one kind within a range of you, as a route that starts at
the nearest one and is then planned to keep the walk short (a good route, not always the shortest). Pick what to look for with **<** and **>**, set the range
with the slider (100 m to 10 km), and press **Find (replace queue)** or **Find (add to queue)**.

| Find | Where it looks |
| --- | --- |
| Wooden Axe / Wooden Knife | Viking Graveyards |
| Wooden Mace | Combat Ruins and Draugr Villages |
| Wooden Sledge | Swamp Graves and Swamp Runestone Towers |
| Wooden Spear | Greydwarf Ruins and Towers, Skeleton Towers (sunken ones too), Contested Towers and Abandoned Huts |
| Wooden Battleaxe | Abandoned Cabins, Mountain Towers and Mountain Inverted Towers |
| Wooden Atgeir | Fuling Villages, Outposts, Ruins and Huts, and Hildir's Plains fortress |
| Curious Axe Head, Mysterious Axe Head | the kind of Abandoned House that can hold it |
| Mysterious Rock | Big Rock Clearings, and loose Mysterious Rocks where the land has been generated |
| Haldor, Hildir, Bog Witch | the merchant |

Each place is one that **can** hold a chest with the item; whether a given chest is there, and holds it, is
chance. The lists come from Valheim 1.0.16's own data, including the chests in the rooms a village builds.

- **As the host (or in single player), places whose chests are known not to hold the item are left out**: the
  game fills a chest when its area is first generated, and a place whose chests have all been filled and none
  of which holds the item any more (emptied, or it never had it) is skipped. As a client this does nothing,
  since only chests near you are known. `SkipCheckedChests` turns it off.
- **Merchants and the Big Rock Clearing exist once per world.** Until someone comes near, the game keeps up to
  ten possible spots, and the first one anybody reaches becomes the real one. Those spots are queued as
  "(possible)". Once one is fixed - or only one spot is left - only that one is queued, as the real place. (When
  you join a game and the server does not answer every request in time, it stays "(possible)" unless a merchant's
  map icon settles it.)
- **Loose Mysterious Rocks exist only where the land has been generated**: anywhere someone has been, as the
  host; near you, as a client. Rocks already picked are left out.
- **It works whether you host or join.** As a client, TomTom asks the server with the same request a Vegvisir
  makes, spaced out so as not to load the server, so a large search takes a few seconds. The server's answers
  never become map pins (see Multiplayer).
- `MaxSearchWaypoints` (default 50) caps how many are queued; the first part of the route is kept. The status
  line says what was found, and `BepInEx/LogOutput.log` how long it took.

## The world map

Hold **Left Alt** and left-click:

| You click | Result |
| --- | --- |
| an empty spot | a waypoint there |
| a pin already on your map | that pin becomes a waypoint (the mod never changes or deletes it; if the pin isn't on the map, e.g. on another character, a stand-in marks the spot until it is). Any pin within reach counts, including one shared to you through a Cartography Table; pings, shouts, player markers and event markers are ignored |
| a waypoint marker | the waypoint is removed |

Plain clicks keep Valheim's normal behaviour, and an Alt-double-click places a single waypoint.
Right-click delete — and the controller's delete button on the big map — works on waypoint
markers too. When a waypoint marker is within reach, the waypoint is what gets removed, never one of
your own pins; deleting one of your own pins that a waypoint follows drops that waypoint as well.
Clicks on the window itself never reach the map underneath it.

If you have hidden the waypoint marker's icon type with the map's icon filter, adding a waypoint shows it
again: the game re-enables an icon type whenever a pin of that type is added. Pick a different
`PinType` in the config if you keep that icon hidden.

## Install

Needs **BepInEx for Valheim**. Unpack this zip into a folder of its own under
`BepInEx/plugins/` (e.g. `BepInEx/plugins/DoomMachine-TomTom/`), or hand the zip to a mod manager.
It loads when `BepInEx/LogOutput.log` says `Loading [TomTom 1.2.1]`. TomTom and Wayfinder exclude each
other: install one.

## Console

Valheim's console is off unless you turn it on — the `-console` launch option, or the game's own
console setting; then **F5** opens it.

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

These commands work only inside a world.

## The arrow

Points toward the active waypoint relative to where the camera looks, coloured **green** when you're
heading straight at it, **yellow** off to the side and **red** facing away. Underneath: the name, the
distance and an estimated time of arrival from how fast you're actually closing in. It hides with the
HUD, while you sleep or watch a cutscene, while the pause menu, the inventory or a trader is open,
while the big map is open, and when you're dead or teleporting.

## Multiplayer

Waypoint markers are **local to you**. They are created with the game's `save: false` flag, which keeps
them out of both the Cartography Table (`Minimap.GetSharedMapData`) and your saved map
(`Minimap.GetMapData`) — both only export pins with that flag set. A pin of your own that you follow
keeps its normal flags and keeps syncing as usual.

Find's answers from the server never become pins either: the plugin catches them before the game would
add them, and does not ask at all unless that catch is in place.

## Keys

`ToggleWindowKey` defaults to **F11**. That is also Valheim's own **screenshot** key, so each press
also saves a picture to Valheim's screenshots folder (on Windows, `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\screenshots`). If you'd rather it
didn't, rebind it: vanilla Valheim also uses F2 (connect panel), F5 (console) and F9 (gamepad
layout), plus Ctrl+F1 (mouse capture) and Ctrl+F3 (hide HUD), and other mods can hold keys of
their own — choose one that nothing you run already uses.

Valheim (1.0.16) cannot read some keys the configuration offers: Clear, Help, SysReq, Break, F13 to
F15, the mouse wheel (`WheelUp`, `WheelDown`) and the symbol keys `Exclaim`, `DoubleQuote`, `Hash`,
`Dollar`, `Percent`, `Ampersand`, `LeftParen`, `RightParen`, `Asterisk`, `Plus`, `Colon`, `Less`,
`Greater`, `Question`, `At`, `Caret`, `Underscore`, `LeftCurlyBracket`, `Pipe`, `RightCurlyBracket` and
`Tilde`. Choose one of those and TomTom writes a warning to `BepInEx/LogOutput.log` and ignores that
key, as if it were unbound; everything else keeps working. Keys the game ignores outright - Mouse5,
Mouse6, F16 to F24 and the numbered-joystick buttons - never fire, with no warning.

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
| `SearchRange` | `1000` | metres Find looks around you (the slider sets it) |
| `MaxSearchWaypoints` | `50` | the most waypoints one Find queues |
| `SkipCheckedChests` | `true` | as the host: leave out places whose chests are known not to hold the item |

Saved queues: `BepInEx/config/DoomMachine.TomTom/waypoints_<worldUID>.txt`, one per world.
The file is replaced safely. If a save is interrupted, a `.new` or `.old` copy may be left beside it;
when the file itself is missing, the next start puts that copy back, and the next save tidies up.

---

Conceived and directed by DoomMachine, who plays it in their own game; written by Claude, Anthropic's AI
model, under DoomMachine's direction. MIT licensed — the LICENSE file in this package is the full text.
Source, issues and newer releases:
https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder
