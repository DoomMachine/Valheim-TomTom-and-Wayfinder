# Wayfinder

Immersive waypoints for Valheim. Mark a spot on your world map — or pick one of your own map markers —
and an on-screen arrow guides you there, turning green as you line up with it. Arrive and the marker
and arrow clear themselves, **Location Reached** is announced, and the next waypoint takes over.

By **DoomMachine** · version 1.7.0 · a BepInEx 5 plugin.

**There are deliberately no coordinates — none to type in, and none shown.** You can only navigate to
places you have marked on your own map, are standing on, or that lie on land your map shows as explored (a click on
the map, or Find), so nothing can be looked up outside the game and walked straight to. Waypoints are shown by name and
distance only: a coordinate readout would let a map click be nudged, try by try, onto a looked-up
location. If you do want coordinates, that's **TomTom** — the same mod with them included. The two cannot
run together; install one.

---

## Quick start

1. Open the world map, hold **Left Alt** and click somewhere explored — or click one of your own markers.
2. Close the map and follow the arrow.

Press **F11** for the Wayfinder window. *(F11 is also Valheim's screenshot key — see Keys below.)*

## The world map

Hold **Left Alt** and left-click:

| You click | Result |
| --- | --- |
| an explored spot | a waypoint there (a click in unexplored fog does nothing) |
| a pin already on your map | that pin becomes a waypoint (the mod never changes or deletes it; if the pin isn't on the map, e.g. on another character, a stand-in marks the spot until it is). The pin nearest the click counts, including one shared to you through a Cartography Table, but not one the map hides; pings, shouts, other players' markers and event markers are never followed |
| a waypoint marker, or a pin you already follow | the waypoint is removed (a pin of yours stays on the map) |

When several pins or waypoint markers are within reach of the click, the one nearest the click counts (a
right-click delete is different: there a waypoint marker within reach always wins, since deleting one of your pins
cannot be undone). A ping, a shout, another player's marker or an event marker nearest the click puts a waypoint on
the spot, if it is explored. Pins the map hides don't count: one whose icon type you have turned off in the map's filter, and a shared pin while
you hide shared pins. The death pins of one in-game day all read the same ("Day 3"), so zoom in
to pick the one you mean. A new waypoint joins the end of
the route: the arrow keeps leading to the one in front, and the message says where the new one waits
("Waypoint queued (2nd): Day 3").

Queue up several and they are visited in order. Plain clicks keep Valheim's normal behaviour, and an
Alt-double-click places a single waypoint.
Right-click delete — and the controller's delete button on the big map — works on waypoint
markers too. When a waypoint marker is within reach, the waypoint is what gets removed, never one of
your own pins; deleting one of your own pins that a waypoint follows drops that waypoint as well.
Clicks on the window itself never reach the map underneath it.

Dying close to the waypoint you are heading for does not count as reaching it: it stays in the route until you
walk there. Each Alt-click the mod acts on gets a line in `BepInEx/LogOutput.log` ("Map Alt-click: ..."), and so
does each change you make to the route from the map, the window or the console ("Route: ..."), and each arrival
("Reached waypoint ..."); what a Find queues, and the route it replaces, are not logged. A bug report about a
waypoint that went missing is easiest to follow with that file.

If you have hidden the waypoint marker's icon type with the map's icon filter, adding a waypoint shows it
again: the game re-enables an icon type whenever a pin of that type is added. Pick a different
`PinType` in the config if you keep that icon hidden.

In a world with the **No Map** setting there is no map, so there are no markers to see and nothing to Alt-click;
the arrow still leads the way. Not tested: on some Linux desktops Alt+click is taken for moving windows - if
Alt-click on the map does nothing there, set `MapModifierKey` to another key.

## The window

- **Add my position** — marks where you stand. It won't count as reached until you've walked away once.
- **Nearest first** — jump to whichever queued waypoint is closest
- **Find** — places of one kind on land your map shows as explored, as a route (see Finding places)
- the **active target** with its distance, **Skip this one** and **Clear all**
- the **queue**, each entry with **Go** (make it active) and **X** (delete)

While it's open, the game ignores your keyboard: Tab doesn't open the inventory and the mouse wheel
scrolls the list instead of zooming the camera. Key bindings you made with the console's `bind` command
still run. **Esc** — or **B** on a controller — closes it. The window needs a keyboard and mouse. Other
mods' hotkeys may not work while it is open, and F11 may not open it while another mod's window has the keyboard.

## Finding places

The window's **Find** section queues places of one kind within a range of you, **but only on land your map
already shows as explored**: a place counts when its centre is explored, by you or through a Cartography Table.
Its own icon need not be on the map - a Skeleton Tower in explored forest is found. Pick what to
look for with **<** and **>**, set the range with the slider (100 m to 10 km), and press **Find (replace queue)**
or **Find (add to queue)**. The route starts at the nearest one and is then planned to keep the walk short.

| Find | Where it looks |
| --- | --- |
| Wooden Axe / Wooden Knife | Viking Graveyards |
| Wooden Mace | Combat Ruins and Draugr Villages |
| Wooden Sledge | Swamp Graves and Swamp Runestone Towers |
| Wooden Spear | Greydwarf Ruins and Towers, Skeleton Towers (sunken ones too), Contested Towers and Abandoned Huts |
| Wooden Battleaxe | Abandoned Cabins, Mountain Towers and Mountain Inverted Towers |
| Wooden Atgeir | Fuling Villages, Outposts, Ruins and Huts, and the Sealed Towers of Hildir's quest |
| Wooden Greatsword | Infested Mines (in their treasure rooms), ruined Dvergr towers and the Mistlands rock spires |
| Curious Axe Head, Mysterious Axe Head | the kind of Abandoned House that can hold it |
| Rock | Big Rock Clearings, and loose Rocks where the land has been generated |
| Bee Nest | most kinds of Abandoned House, Contested Towers, Bear Caves, Abandoned Villages (the fenced Meadows farms) and Draugr Villages |
| Haldor, Hildir, Bog Witch | the merchant |

Each place is one that **can** hold a chest with the item, or a Bee Nest; whether a given chest or nest is there
is chance. The lists come from Valheim 1.0.16's own data, including the chests and nests in the rooms a village
builds. They are built into the plugin, so Find needs nothing installed besides BepInEx; where the places are comes
from the world you are playing in, or from its server. A Valheim update that adds or moves such chests or nests
needs a plugin update.

- **Nothing is said.** There is no message and no count. If nothing that matches is explored within range,
  nothing happens.
- **Merchants and the Big Rock Clearing exist once per world.** The first of their possible spots anyone reaches
  becomes the real one. Wayfinder queues them only once they are fixed, or when only one spot is left - and when
  you join a game, only if the server answered every request in time or a merchant's map icon settles it.
  After a Valheim update, run Find again for the merchants: a saved spot may no longer be the place.
- **Loose Rocks** (the ones you pick up by hand) **exist only where the land has been generated**, and
  picked ones are left out. The Mysterious Rock a player builds and the stones it lays are never listed, nor is a
  rock or nest placed through the build system or spawned with the console's `spawn` command (when a server's plugin
  answers, only if that server runs 1.4.1 or later).
- **The Wooden Greatsword** is found in the Mistlands: in the treasure room of an Infested Mine - the waypoint is the
  mine's entrance, and a mine is not sure to have a treasure room - and in the chests of ruined Dvergr towers and of
  the rock spires (that chest sits on top of the spire, about 60 m up, and about 3 spires in 4 have one).
- **Bee Nests grow only inside those places, and not in every one**: about 1 Abandoned House in 4, about 1
  Contested Tower in 4, 1 Bear Cave in 2 (on its fir tree), and in some of the rooms of an Abandoned Village or a
  Draugr Village - decided when the place's area is first generated. A nest found where the land has been
  generated is queued as **Bee Nest**, in place of its place; a place not known to hold one is queued as, say,
  "Abandoned House (Bee Nest)", even when its area has been generated without one. Joining a server without this
  plugin, or with a version older than 1.4.0, only the nests your game has seen since you joined (near where you
  have been) are known.
- **It works whether you host or join.** When you join a server that runs this plugin too, the server answers
  from its own knowledge (see Servers); joining any other server, Wayfinder asks it with the same request a
  Vegvisir makes, and that server's log then records which kinds of place were asked for. Either way, the answers never become map pins (see Multiplayer), and only what your map shows
  as explored is queued.
- `MaxSearchWaypoints` (default 50) caps how many are queued; the first part of the route is kept.

## Install

Needs **BepInEx for Valheim**. Unpack this zip into a folder of its own under
`BepInEx/plugins/` (e.g. `BepInEx/plugins/DoomMachine-Wayfinder/`), or hand the zip to a mod manager.
It loads when `BepInEx/LogOutput.log` says `Loading [Wayfinder 1.7.0]`. TomTom and Wayfinder exclude each
other: install one. For a server, see Servers.

## Uninstall

Delete the plugin's folder (`BepInEx/plugins/DoomMachine-Wayfinder/`). Its settings
(`BepInEx/config/DoomMachine.Wayfinder.cfg`) and saved routes (`BepInEx/config/DoomMachine.Wayfinder/`, one file per world,
shared by every character on this PC) stay until you delete them as well, and so do its log files
(`BepInEx/Wayfinder.log`, `Wayfinder-prev.log` and any `Wayfinder.log.1` to `.4`). The markers are never saved into your map,
so nothing is left there.

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

Find's answers from the server never become pins either. A server that runs this plugin answers with messages
of its own, which the game has no pin for; any other server is asked the way a Vegvisir asks, and the plugin
catches those answers before the game would add them, and does not ask at all unless that catch is in place.

## Servers

Wayfinder is a player's mod and needs nothing on the server. Installing it on the server as well - a dedicated
server, or the game of whoever hosts with **Start Server** - adds two things:

- **The server answers Find itself.** A player who joins then gets the same answer the host gets: only the
  real merchant or Big Rock Clearing once it is fixed, loose Rocks anywhere already generated.
  Without it, a joining player's Find asks the server the way a Vegvisir does (see Finding places).
- **`WhoMayFind`**, the server's rule for which of the joining players may use Find: `Everyone` (the default),
  `AdminsOnly` (players in the server's `adminlist.txt`) or `Nobody`. The host's own Find is never limited. A
  player the server refuses sees it in the window's Find header ("Find - turned off on this server", or "Find -
  this server's admins only"); the server decides every request afresh, so a player added to the admin list can use
  Find within about 10 seconds, without reconnecting, and the header clears when they do.

Either edition on the server serves the players of both: a server with Wayfinder answers TomTom players too, and
Wayfinder players still see only what their map shows as explored. Players who join without the plugin are not
affected by it. This is a rule for players who use this plugin, not a lock: any game can ask a server for
locations the way a Vegvisir does.

**Start Server.** Nothing more to install: the host's own Wayfinder is the server's. Set `WhoMayFind` in-game through
ConfigurationManager, where it takes effect at once and the players' windows follow; or quit the game, edit
`BepInEx/config/DoomMachine.Wayfinder.cfg`, and start it again. (An edit made while the game runs is not read, and the
plugin rewrites its settings file, with the old value, whenever one of its settings changes - for example when you
start a Find with a new range.)

**A dedicated server on Windows**, step by step:

1. Stop the server.
2. Download **BepInExPack Valheim** (Thunderstore, *Manual Download*) and unpack it into a folder of its own.
3. Copy the contents of its `BepInExPack_Valheim` folder into the server's folder - the one with
   `valheim_server.exe` (with Steam, `steamapps\common\Valheim dedicated server`). `BepInEx`, `doorstop_libs`,
   `doorstop_config.ini` and `winhttp.dll` are now beside `valheim_server.exe`.
4. Unpack this zip into `BepInEx\plugins\DoomMachine-Wayfinder\` in the server's folder.
5. Start the server as you always do, for example with your copy of `start_headless_server.bat`. BepInEx comes
   in through `winhttp.dll`; nothing else changes.
6. Check `BepInEx\LogOutput.log` in the server's folder for these lines:
   ```
   Loading [Wayfinder 1.7.0]
   Applied 2 of 2 patches.
   Wayfinder 1.7.0 by DoomMachine loaded on a dedicated server: it answers players' Find (WhoMayFind = Everyone).
   Ready to answer players' Find (WhoMayFind = Everyone).
   ```
   The last one comes when the world loads; `WhoMayFind` shows your setting. The server's own log file
   (`-logFile`) does not have these lines. Wayfinder's own log, `BepInEx\Wayfinder.log`, is beside `LogOutput.log`
   (see Log file). No new `LogOutput.log` after a start means BepInEx did not start: check first that `winhttp.dll`
   and `doorstop_config.ini` sit right beside `valheim_server.exe`, not in a folder below it.
7. To change who may use Find: stop the server, open `BepInEx\config\DoomMachine.Wayfinder.cfg` (the first start writes
   it; on a server it holds only `[6 - Server]` and `[7 - Logging]`), set `WhoMayFind`, and start the server again.
   - `AdminsOnly` uses the game's own admin list, `adminlist.txt`, in the server's save folder: the `-savedir`
     folder if the server is started with one, otherwise `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim`. One
     ID per line, the same IDs as for the game's own admin commands (kick, ban), in the form Valheim's server manual
     gives, `<Platform>_<ID>`: for a Steam player `Steam_` and the SteamID64 (the 17-digit number; the number alone
     works too), and for a crossplay player on another platform that platform's prefix. A player's ID copied exactly
     as the game's F2 panel shows it works too (it writes a Steam player as `V_` and the SteamID64, and an Xbox,
     PlayStation or Nintendo player's number in a form of its own), and so does one from the server's log: the number
     after `Got connection SteamID`, or with `-crossplay` the ID after `received local Platform ID` - not a
     `playfab/` one. Lines starting with `//` are comments, and an ID must match exactly (capitals and all, with no
     space after it). The game re-reads the file at most every 10 seconds, so a change needs no restart.

**A dedicated server on Linux** (not tried yet): steps 1 to 4 the same, into the server's folder - the one with
Valheim's own `start_server.sh`; then make the pack's `start_server_bepinex.sh` executable
(`chmod u+x start_server_bepinex.sh`), edit it with your server's name, world and password as you would Valheim's own
start script, and start the server with it. The log and the config file are in the same places under `BepInEx/`.
Without `-savedir`, `adminlist.txt` is in Unity's data folder for Valheim
(usually `~/.config/unity3d/IronGate/Valheim`).

**A rented server** (not tried yet): many Valheim hosting services can install BepInEx for you (see your host's
help). Then upload this zip's contents to `BepInEx/plugins/DoomMachine-Wayfinder/`, and change `WhoMayFind` in
`BepInEx/config/DoomMachine.Wayfinder.cfg` through the host's file manager, then restart the server.

Players and the server need not run the same version: from 1.3.0 on, they understand each other. A Find for
something the server's plugin does not know yet (a Bee Nest needs 1.4.0 or later there, the Wooden Greatsword 1.5.0)
asks the server the way a Vegvisir does instead - so keep the server's plugin up to date. (Read from the code; not
tested yet.)

Tested so far: Wayfinder itself has not been loaded on a server yet. TomTom 1.3.0 test builds, made just before that
release, loaded on a Windows dedicated server with BepInEx 5.4.23.3 (BepInEx's console window turned off, the server
started without `-crossplay`) and logged the lines of step 6 (as TomTom 1.3.0). No later version, no released zip, no
Linux, crossplay or rented server has been tried yet, and no player has joined a server that runs the plugin.

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

Valheim (1.0.16) cannot read some keys the configuration offers: Clear, Help, SysReq, Break, F13 to
F15, the mouse wheel (`WheelUp`, `WheelDown`) and the symbol keys `Exclaim`, `DoubleQuote`, `Hash`,
`Dollar`, `Percent`, `Ampersand`, `LeftParen`, `RightParen`, `Asterisk`, `Plus`, `Colon`, `Less`,
`Greater`, `Question`, `At`, `Caret`, `Underscore`, `LeftCurlyBracket`, `Pipe`, `RightCurlyBracket` and
`Tilde`. Choose one of those and Wayfinder writes a warning to `BepInEx/LogOutput.log` and ignores that
key, as if it were unbound; everything else keeps working. Keys the game ignores outright - Mouse5,
Mouse6, F16 to F24 and the numbered-joystick buttons - never fire, with no warning.

## Log file

Besides `BepInEx/LogOutput.log`, Wayfinder keeps a log of its own beside it: `BepInEx/Wayfinder.log`. Each start of the
game normally begins a new file and keeps the last game's as `Wayfinder-prev.log` (older ones are replaced). On
Windows, if another program holds one of these files open, the new lines are added after the last game's in `Wayfinder.log` instead, or -
when that program holds `Wayfinder.log` itself and does not let others write to it - go to the first numbered file
that can be written, `Wayfinder.log.1` (up to `.4`). On Windows, while another copy of the game on this PC is
writing the file, that copy writes `Wayfinder.log.1` (up to `.4`), adding to what an earlier second copy wrote there.
These numbered files are kept until you delete them; once all four hold more than 8 MiB, a second copy writes none. You
can open the log while the game runs, as you can `LogOutput.log`. Each entry starts with the local time and `f` with the game's frame number (a stack trace's further lines are indented and start with `|`); `f-` marks the header lines and any line written off the game's main thread.

With **`ErrorLog`** (`[7 - Logging]`, on by default) it holds Wayfinder's warnings and errors, and any game error whose
stack trace runs through Wayfinder's code, with that trace - the file to send with a bug report. A game without
problems leaves only a few lines, naming the versions of Wayfinder, Valheim, Unity and BepInEx. Turned off, nothing is
written; the files already there stay. A change in game through ConfigurationManager takes effect at once. To edit the
`.cfg` file instead, quit the game (or stop the server), edit it and start again: an edit made while the game runs is
not read, and the plugin rewrites its settings file, with the old value, whenever one of its settings changes (for
example when you start a Find with a new range). Lines are written at once; a line repeated many times in a row is
written once, and a file stops at 16 MiB.

Unlike TomTom, Wayfinder has no more detailed log: a line for every click and Find would tell where things are. In its
file, the config, BepInEx and game folders and your user folder are written as `<config>`, `<BepInEx>`, `<game>` and
`<home>`, and numbers that look like a position - a number with a fraction, or two or three whole numbers joined by
commas - as `#`.

## Configuration

`BepInEx/config/DoomMachine.Wayfinder.cfg`, also editable in-game through ConfigurationManager.

| Setting | Default | |
| --- | --- | --- |
| `ToggleWindowKey` | `F11` | see Keys |
| `MapModifierKey` | `LeftAlt` | hold while clicking the map |
| `SkipWaypointKey` | `None` | optional |
| `ArrivalRadius` | `10` | metres before a waypoint counts as reached |
| `Use3DDistance` | `false` | include altitude in the arrival test: a waypoint with a height of its own uses it; a map click on open ground, a place Find found and a waypoint without a height use the ground under it once it is loaded near you (horizontal over water) |
| `PersistWaypoints` | `true` | remember the queue per world |
| `PinType` / `PinLabel` | `Icon3` / `Waypoint` | marker icon and label |
| `ShowArrow`, `ArrowSize`, `ArrowScreenX/Y`, `ArrowOpacity` | | arrow placement |
| `ShowDistance`, `ShowWaypointName`, `ShowTimeToArrival` | `true` | captions |
| `HideArrowWhenMapOpen` | `true` | |
| `ColorFacingTarget/Sideways/FacingAway` | green/yellow/red | hex colours |
| `SearchRange` | `1000` | metres Find looks around you (the slider sets it) |
| `MaxSearchWaypoints` | `50` | the most waypoints one Find queues |
| `WhoMayFind` | `Everyone` | used only when this game is the server: which joining players may use Find (see Servers) |
| `ErrorLog` | `true` | Wayfinder's own log of warnings and errors, `BepInEx/Wayfinder.log` (see Log file) |

Saved queues: `BepInEx/config/DoomMachine.Wayfinder/waypoints_<worldUID>.txt`, one per world — kept
apart from TomTom's, so coordinates entered in TomTom never carry over into Wayfinder.
The file is replaced safely. If a save is interrupted, a `.new`, `.old` or `.old2` copy may be left beside it;
when the file itself is missing, the next start puts that copy back, and the next save tidies up. If the file
cannot be read when you enter a world (another program, such as a backup or sync tool, holds it), it is never
saved over: your changes are kept (if it is still unreadable when you leave the world, they are not saved), the
file is read again every few seconds, and once it can be read what you
queued meanwhile comes first and the saved waypoints follow. Turning `PersistWaypoints` on in a world works the
same way: the saved route is read and joined with what is queued, not replaced by it.

---

Conceived and directed by DoomMachine; written by Claude, Anthropic's AI model, under DoomMachine's
direction. MIT licensed — the LICENSE file in this package is the full text. Source, issues and newer
releases:
https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder
