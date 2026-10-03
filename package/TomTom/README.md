# TomTom

Waypoints for Valheim, named in honour of the World of Warcraft addon that inspired it. Queue up
coordinates or pick places on the world map, get temporary markers, and follow an on-screen arrow that
turns green as you line up with the target. Reach a waypoint and the marker and arrow clear themselves,
**Location Reached** is announced, and the next one in the list takes over.

By **DoomMachine** · version 1.7.0 · a BepInEx 5 plugin.

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
  elevation −567. Write `Camp 2: 1234, -567` or `1234, -567, Camp 2` instead. A first word that starts
  with a digit is refused (`2nd camp 1234, -567` could be a mistyped number such as `1234m`): write
  `2nd camp: 1234, -567`.
- A fourth number straight after the third is refused. A name that is a number goes before a colon (`7: 1234, -567, 30`).
- One waypoint per line — paste a whole list at once. A line that can't be read is skipped and reported.
- Use a **dot** for decimals (`12.5`); a comma separates fields. A comma with a digit on both sides
  (`12,5`) could also be a decimal comma, so a line that would mean another place read that way (`12,5 30`,
  `1234,5; -567,25`) is refused rather than guessed at: use a dot, or put a space after the comma
  (`12, 5 30`). A line that shows how it writes numbers - a number with a dot, or a comma with no space after it
  that is not between two digits (`12,5,-30` reads as 12, 5, −30; `Camp,12,5 30` as 12, 5, 30) - is read the usual
  way, and so are lines such as `12,34` and `1234,-567,30`. A comma followed by
  exactly three digits with no space (`1,234`) looks like digit grouping, so the line is refused as well
  — write `1, 234` if you really mean two fields. A no-break or thin space inside a
  number (how some languages group thousands, often copied from web pages) is refused the same way; an
  ordinary space always separates two values, so `1 234` typed with the space bar is x 1, y 234.
- Text pasted from web pages and documents works: a minus sign, dash or hyphen in front of a number
  (`−1234`), full-width digits and punctuation, and no-break or thin spaces between values are read
  as their plain equivalents.
- Axis labels are honoured. With all three, `Y` is Valheim's **altitude**, so `X: 1234 Y: 56 Z: -789`
  means x 1234, z −789, 56 m up (with `InputIsRawValheimXYZ` on as well). With only `X` and `Y`, they are
  the two map axes, read by label in whichever order they are written.
- To paste Valheim's raw `x y z` instead, turn on `InputIsRawValheimXYZ`.
- The `pos` console line can be pasted whole, in either mode, and is read in the axis order it names: Valheim's
  `Player position (X,Y,Z): (1234, 30, -567) , Zone: ...` and Server Devcommands' `Player position (X,Z,Y): (1234,
  -567, 30)`, a decimal comma in its values included.

When you give no elevation, arrival is judged on horizontal distance, which is what you want for a far,
unexplored target whose height can't be known in advance - unless `Use3DDistance` is on: then such a waypoint is
measured at the ground under it once that ground is loaded near you.

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
**B** on a controller — closes it. The window needs a keyboard and mouse. Other mods' hotkeys may not
work while it is open, and F11 may not open it while another mod's window has the keyboard.

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

- **Places whose chests are known not to hold the item are left out**: the game fills a chest when its area is
  first generated, and a place whose chests have all been filled and none of which holds the item any more
  (emptied, or it never had it) is skipped. This works as the host (or in single player), and when you join a
  server that runs this plugin too (for the Wooden Greatsword, 1.5.0 or later; see Servers); joining any other server it does nothing, since your game
  knows only the chests it has been sent since you joined. `SkipCheckedChests` turns it off.
- **The Wooden Greatsword** is found in the Mistlands: in the treasure room of an Infested Mine - the waypoint is the
  mine's entrance, and a mine is not sure to have a treasure room - and in the chests of ruined Dvergr towers and of
  the rock spires (that chest sits on top of the spire, about 60 m up, and about 3 spires in 4 have one).
- **Bee Nests grow only inside those places, and not in every one**: about 1 Abandoned House in 4, about 1
  Contested Tower in 4, 1 Bear Cave in 2 (on its fir tree), and in some of the rooms of an Abandoned Village or a
  Draugr Village - decided when the place's area is first generated. A nest found is queued as **Bee Nest**, in
  place of its place; a place not known to hold one is queued as, say, "Abandoned House (Bee Nest)". As the host
  (or in single player), or when you join a server that runs this plugin too (1.4.0 or later), nests are known
  wherever the land has been generated, and `SkipCheckedChests` also leaves out places whose area has been generated
  without a nest left in it (none grew there, or it was destroyed). Joining any other server, only the nests your
  game has seen since you joined (near where you have been) are known.
- **Merchants and the Big Rock Clearing exist once per world.** Until someone comes near, the game keeps up to
  ten possible spots, and the first one anybody reaches becomes the real one. Those spots are queued as
  "(possible)". Once one is fixed - or only one spot is left - only that one is queued, as the real place. (When
  you join a game and the server does not answer every request in time, it stays "(possible)" unless a merchant's
  map icon settles it.)
  After a Valheim update, run Find again for the merchants: a saved "(possible)" spot may no longer be one.
- **Loose Rocks** (the ones you pick up by hand) **exist only where the land has been generated**:
  anywhere someone has been, as the host or on a server with this plugin; when you join any other server, only
  the ones your game has seen since you joined (near where you have been). Rocks already picked are left out.
  The Mysterious Rock a player builds and the stones it lays are never listed, nor is a rock or nest placed through
  the build system or spawned with the console's `spawn` command (when a server's plugin answers, only if that server
  runs 1.4.1 or later).
- **It works whether you host or join.** When you join a server that runs this plugin too, the server answers
  from its own knowledge, as it would for the host (see Servers). Joining any other server, TomTom asks it with
  the same request a Vegvisir makes, spaced out so as not to load the server, so a large search takes a few
  seconds; that server's log then records which kinds of place were asked for (`Found 12 locations of type
  ...`). Either way, the server's answers never become map pins (see Multiplayer).
- `MaxSearchWaypoints` (default 50) caps how many are queued; the first part of the route is kept. The status
  line says what was found, and `BepInEx/LogOutput.log` how long it took.

## The world map

Hold **Left Alt** and left-click:

| You click | Result |
| --- | --- |
| an empty spot | a waypoint there |
| a pin already on your map | that pin becomes a waypoint (the mod never changes or deletes it; if the pin isn't on the map, e.g. on another character, a stand-in marks the spot until it is). The pin nearest the click counts, including one shared to you through a Cartography Table, but not one the map hides; pings, shouts, other players' markers and event markers are never followed |
| a waypoint marker, or a pin you already follow | the waypoint is removed (a pin of yours stays on the map) |

When several pins or waypoint markers are within reach of the click, the one nearest the click counts (a
right-click delete is different: there a waypoint marker within reach always wins, since deleting one of your pins
cannot be undone). A ping, a shout, another player's marker or an event marker nearest the click puts a waypoint on
the spot. Pins the map hides don't count: one whose icon type you have turned off in the map's filter, and a shared pin while
you hide shared pins. The death pins of one in-game day all read the same ("Day 3"), so zoom in to pick the one
you mean. A new waypoint joins the end of the route: the arrow keeps leading to the one in front, and the message says where the new one waits
("Waypoint queued (2nd): Day 3"). When the click puts a waypoint on the spot, the message is "Waypoint added" (or
"Waypoint queued (2nd)"); with `ShowCoordinates` on, the spot's coordinates follow it.

Plain clicks keep Valheim's normal behaviour, and an Alt-double-click places a single waypoint.
Right-click delete — and the controller's delete button on the big map — works on waypoint
markers too. When a waypoint marker is within reach, the waypoint is what gets removed, never one of
your own pins; deleting one of your own pins that a waypoint follows drops that waypoint as well.
Clicks on the window itself never reach the map underneath it.

Dying close to the waypoint you are heading for does not count as reaching it: it stays in the route until you
walk there. Each Alt-click the mod acts on gets a line in `BepInEx/LogOutput.log` ("Map Alt-click: ..."), and so
does each change you make to the route from the map, the window or the console ("Route: ..."), and each arrival
("Reached waypoint ..."); a Find logs one line for what it found and queued, and none for the route it replaces. A
bug report about a waypoint that went missing is easiest to follow with that file.

If you have hidden the waypoint marker's icon type with the map's icon filter, adding a waypoint shows it
again: the game re-enables an icon type whenever a pin of that type is added. Pick a different
`PinType` in the config if you keep that icon hidden.

In a world with the **No Map** setting there is no map, so there are no markers to see and nothing to Alt-click;
the arrow still leads the way. Not tested: on some Linux desktops Alt+click is taken for moving windows - if
Alt-click on the map does nothing there, set `MapModifierKey` to another key.

## Install

Needs **BepInEx for Valheim**. Unpack this zip into a folder of its own under
`BepInEx/plugins/` (e.g. `BepInEx/plugins/DoomMachine-TomTom/`), or hand the zip to a mod manager.
It loads when `BepInEx/LogOutput.log` says `Loading [TomTom 1.7.0]`. TomTom and Wayfinder exclude each
other: install one. For a server, see Servers.

## Uninstall

Delete the plugin's folder (`BepInEx/plugins/DoomMachine-TomTom/`). Its settings
(`BepInEx/config/DoomMachine.TomTom.cfg`) and saved routes (`BepInEx/config/DoomMachine.TomTom/`, one file per world,
shared by every character on this PC) stay until you delete them as well, and so do its log files
(`BepInEx/TomTom.log`, `TomTom-prev.log` and any `TomTom.log.1` to `.4`). The markers are never saved into your map,
so nothing is left there.

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

These commands work only inside a world. In the console, separate the values with commas or spaces: some
console mods (Server Devcommands) take `;` as the start of a new command.

## The arrow

Points toward the active waypoint relative to where the camera looks, coloured **green** when you're
heading straight at it, **yellow** off to the side and **red** facing away. Underneath: the name, the
distance and an estimated time of arrival from how fast you're actually closing in. A waypoint without a name - an
Alt-click on an empty spot, coordinates pasted without a name, a pin with no name - is called by your `PinLabel`
("Waypoint") there and in the "Next waypoint" message; turn `ShowCoordinates` on to see its coordinates instead.
The window and the console always show them. It hides with the
HUD, while you sleep or watch a cutscene, while the pause menu, the inventory or a trader is open,
while the big map is open, and when you're dead or teleporting.

## Multiplayer

Waypoint markers are **local to you**. They are created with the game's `save: false` flag, which keeps
them out of both the Cartography Table (`Minimap.GetSharedMapData`) and your saved map
(`Minimap.GetMapData`) — both only export pins with that flag set. A pin of your own that you follow
keeps its normal flags and keeps syncing as usual.

Find's answers from the server never become pins either. A server that runs this plugin answers with messages
of its own, which the game has no pin for; any other server is asked the way a Vegvisir asks, and the plugin
catches those answers before the game would add them, and does not ask at all unless that catch is in place.

## Servers

TomTom is a player's mod and needs nothing on the server. Installing it on the server as well - a dedicated
server, or the game of whoever hosts with **Start Server** - adds two things:

- **The server answers Find itself.** A player who joins then gets the same answer the host gets: only the
  real merchant or Big Rock Clearing once it is fixed, loose Rocks anywhere already generated, and
  places whose chests are known not to hold the item left out (`SkipCheckedChests`). Without it, a joining
  player's Find asks the server the way a Vegvisir does (see Finding places).
- **`WhoMayFind`**, the server's rule for which of the joining players may use Find: `Everyone` (the default),
  `AdminsOnly` (players in the server's `adminlist.txt`) or `Nobody`. The host's own Find is never limited. A
  player the server refuses sees it in the window's Find header ("Find - turned off on this server", or "Find -
  this server's admins only"); the server decides every request afresh, so a player added to the admin list can use
  Find within about 10 seconds, without reconnecting, and the header clears when they do.

Either edition on the server serves the players of both: a server with TomTom answers Wayfinder players too, and
Wayfinder players still see only what their map shows as explored. Players who join without the plugin are not
affected by it. This is a rule for players who use this plugin, not a lock: any game can ask a server for
locations the way a Vegvisir does.

**Start Server.** Nothing more to install: the host's own TomTom is the server's. Set `WhoMayFind` in-game through
ConfigurationManager, where it takes effect at once and the players' windows follow; or quit the game, edit
`BepInEx/config/DoomMachine.TomTom.cfg`, and start it again. (An edit made while the game runs is not read, and the
plugin rewrites its settings file, with the old value, whenever one of its settings changes - for example when you
start a Find with a new range.)

**A dedicated server on Windows**, step by step:

1. Stop the server.
2. Download **BepInExPack Valheim** (Thunderstore, *Manual Download*) and unpack it into a folder of its own.
3. Copy the contents of its `BepInExPack_Valheim` folder into the server's folder - the one with
   `valheim_server.exe` (with Steam, `steamapps\common\Valheim dedicated server`). `BepInEx`, `doorstop_libs`,
   `doorstop_config.ini` and `winhttp.dll` are now beside `valheim_server.exe`.
4. Unpack this zip into `BepInEx\plugins\DoomMachine-TomTom\` in the server's folder.
5. Start the server as you always do, for example with your copy of `start_headless_server.bat`. BepInEx comes
   in through `winhttp.dll`; nothing else changes.
6. Check `BepInEx\LogOutput.log` in the server's folder for these lines:
   ```
   Loading [TomTom 1.7.0]
   Applied 2 of 2 patches.
   TomTom 1.7.0 by DoomMachine loaded on a dedicated server: it answers players' Find (WhoMayFind = Everyone).
   Ready to answer players' Find (WhoMayFind = Everyone).
   ```
   The last one comes when the world loads; `WhoMayFind` shows your setting. The server's own log file
   (`-logFile`) does not have these lines. TomTom's own log, `BepInEx\TomTom.log`, is beside `LogOutput.log`
   (see Log file). No new `LogOutput.log` after a start means BepInEx did not start: check first that `winhttp.dll`
   and `doorstop_config.ini` sit right beside `valheim_server.exe`, not in a folder below it.
7. To change who may use Find: stop the server, open `BepInEx\config\DoomMachine.TomTom.cfg` (the first start writes
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
help). Then upload this zip's contents to `BepInEx/plugins/DoomMachine-TomTom/`, and change `WhoMayFind` in
`BepInEx/config/DoomMachine.TomTom.cfg` through the host's file manager, then restart the server.

Players and the server need not run the same version: from 1.3.0 on, they understand each other. A Find for
something the server's plugin does not know yet (a Bee Nest needs 1.4.0 or later there, the Wooden Greatsword 1.5.0)
asks the server the way a Vegvisir does instead, without the server's chest and nest checks - so keep the server's
plugin up to date. (Read from the code; not tested yet.)

Tested so far: loading only. TomTom 1.3.0 test builds, made just before that release, loaded on a Windows dedicated
server with BepInEx 5.4.23.3 (BepInEx's console window turned off, the server started without `-crossplay`), logged
the lines of step 6 (as 1.3.0) and wrote a settings file with only `[6 - Server]` (`[7 - Logging]` and `TomTom.log`
came with 1.6.0). No later version, no released zip, no Wayfinder build, no Linux, crossplay or rented server has been
tried yet, and no player has joined a server that runs the plugin.

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

## Log file

Besides `BepInEx/LogOutput.log`, TomTom keeps a log of its own beside it: `BepInEx/TomTom.log`. Each start of the
game normally begins a new file and keeps the last game's as `TomTom-prev.log` (older ones are replaced). On Windows,
if another program holds one of these files open, the new lines are added after the last game's in `TomTom.log` instead, or -
when that program holds `TomTom.log` itself and does not let others write to it - go to the first numbered file that
can be written, `TomTom.log.1` (up to `.4`). On Windows, while another copy of the game on this PC is
writing the file, that copy writes `TomTom.log.1` (up to `.4`), adding to what an earlier second copy wrote there. These
numbered files are kept until you delete them; once all four hold more than 8 MiB, a second copy writes none. You can
open the log while the game runs, as you can `LogOutput.log`. Each entry starts with the local time and `f` with the game's frame number (a stack trace's further lines are indented and start with `|`); `f-` marks the header lines and any line written off the game's main thread. Two settings in `[7 - Logging]` decide
what goes in. A
change in game through ConfigurationManager takes effect at once. To edit the `.cfg` file instead, quit the game (or
stop the server), edit it and start again: an edit made while the game runs is not read, and the plugin rewrites its
settings file, with the old value, whenever one of its settings changes (for example when you start a Find with a new
range).

- **`ErrorLog`** (on by default): TomTom's warnings and errors, and any game error whose stack trace runs through
  TomTom's code, with that trace - the file to send with a bug report. A game without problems leaves only a few lines,
  naming the versions of TomTom, Valheim, Unity and BepInEx.
- **`VerboseLog`** (off by default): all of that, every other line TomTom logs, the state of things when it is turned
  on, and a line for each event - map clicks
  and deletes, waypoints added (with their positions), reached and removed, the route read and saved, markers made,
  lost and found again, the arrow hidden or shown and why, deaths, the map and the window, the messages shown on
  screen, settings changed, the stages of a Find and, on a server or as the host, other players' Find requests (each
  player by a number). These new event lines go to this file only; the `Map Alt-click`, `Route` and `Reached waypoint`
  lines are in `LogOutput.log` too. Turn it on to watch a problem as it happens, then off again.

With both off nothing is written; the files already there stay. Warnings and errors are written at once, other lines
within a second. A line repeated many times in a row is written once, with a count; each stretch of `VerboseLog` stops
adding ordinary lines after 8 MiB, so warnings and errors still fit, and a file stops at 16 MiB.

What it holds: warnings can name a world by its ID number; with `VerboseLog`, also your waypoints' names and positions,
the names of your pins, of pins shared with you and of other players' markers near an Alt-click, and your settings, and
on a server or as the host the names of players refused Find (as `LogOutput.log` does). The config, BepInEx and game
folders and your user folder are written as `<config>`, `<BepInEx>`, `<game>` and `<home>` when a line names them. Read
it before you share it.

## Configuration

`BepInEx/config/DoomMachine.TomTom.cfg`, also editable in-game through ConfigurationManager.

| Setting | Default | |
| --- | --- | --- |
| `ToggleWindowKey` | `F11` | see Keys |
| `MapModifierKey` | `LeftAlt` | hold while clicking the map |
| `SkipWaypointKey` | `None` | optional |
| `ArrivalRadius` | `10` | metres before a waypoint counts as reached |
| `Use3DDistance` | `false` | include altitude in the arrival test: a waypoint with a height of its own uses it; a map click on open ground, a place Find found and a waypoint without a height use the ground under it once it is loaded near you (horizontal over water) |
| `InputIsRawValheimXYZ` | `false` | read input as Valheim's own x/y/z |
| `PersistWaypoints` | `true` | remember the queue per world |
| `PinType` / `PinLabel` | `Icon3` / `Waypoint` | marker icon and label |
| `ShowArrow`, `ArrowSize`, `ArrowScreenX/Y`, `ArrowOpacity` | | arrow placement |
| `ShowDistance`, `ShowWaypointName`, `ShowTimeToArrival` | `true` | captions |
| `ShowCoordinates` | `false` | a waypoint without a name shows its coordinates under the arrow, in the "Next waypoint" message and in a map click's "Waypoint added" message, instead of `PinLabel` (the window and the console show them either way) |
| `HideArrowWhenMapOpen` | `true` | |
| `ColorFacingTarget/Sideways/FacingAway` | green/yellow/red | hex colours |
| `SearchRange` | `1000` | metres Find looks around you (the slider sets it) |
| `MaxSearchWaypoints` | `50` | the most waypoints one Find queues |
| `SkipCheckedChests` | `true` | leave out places known not to hold what you look for: chests already filled without the item, and places generated without a Bee Nest (as the host, or joining a server with this plugin) |
| `WhoMayFind` | `Everyone` | used only when this game is the server: which joining players may use Find (see Servers) |
| `ErrorLog` | `true` | TomTom's own log of warnings and errors, `BepInEx/TomTom.log` (see Log file) |
| `VerboseLog` | `false` | also a line for each event in that log, to watch a problem as it happens (see Log file) |

Saved queues: `BepInEx/config/DoomMachine.TomTom/waypoints_<worldUID>.txt`, one per world.
The file is replaced safely. If a save is interrupted, a `.new`, `.old` or `.old2` copy may be left beside it;
when the file itself is missing, the next start puts that copy back, and the next save tidies up. If the file
cannot be read when you enter a world (another program, such as a backup or sync tool, holds it), it is never
saved over: your changes are kept (if it is still unreadable when you leave the world, they are not saved), the
file is read again every few seconds, and once it can be read what you
queued meanwhile comes first and the saved waypoints follow. Turning `PersistWaypoints` on in a world works the
same way: the saved route is read and joined with what is queued, not replaced by it.

---

Conceived and directed by DoomMachine, who plays it in their own game; written by Claude, Anthropic's AI
model, under DoomMachine's direction. MIT licensed — the LICENSE file in this package is the full text.
Source, issues and newer releases:
https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder
