# Open items and future work

The one list of what is untested, unverified, open or only an idea for TomTom and Wayfinder - so none of it has to be
worked out again. Keep it current: add an item when it comes up, and when one is done, move it to "Done" with the
version or commit that did it. How the plugins work, and why, is in the README's "Implementation notes"; what each
release changed is in its "History".

Last updated: 2026-10-04.

## Not yet tried in play

1.7.0 was played in single player on 2026-10-04, with no warning or error from the plugin: the saved route was read back
when the world loaded, but none of 1.7.0's on-screen changes was tried (no map click, finished Find or arrival, and
every waypoint in the route had a name). 1.6.0 was played in single player on 2026-10-03, over twelve game starts, with
no warning or error from the plugin: map clicks placed waypoints on the spot and queued a second one ("Waypoint queued
(2nd)"), waypoints were reached, a Bee Nest Find and a Wooden Axe Find ran, a route of 44 places was kept across a quit
from inside the world and a restart, and the log file was seen at work (below). 1.5.2 was played in two single-player
sessions on 2026-10-02, with no warning or error from the plugin: Alt-clicks followed pins (a death pin among them),
placed waypoints on the spot and removed markers, a second followed pin was queued behind the first, map deletes removed
waypoint markers, two Finds for axe heads ran, and waypoints were reached - the 1.5.1 Alt-click rule and its log lines
working. 1.5.1 was never played on its own; all its changes are in 1.5.2. 1.5.0 was played on 2026-09-30 and 2026-10-01,
in single player: its logs show waypoints reached, no Find run and no error from the plugin. 1.4.1 has not been played
on its own; all its changes are in 1.5.0. 1.4.0 was used in four short sessions, 1.3.1 in at least two and 1.3.0 in at
least two, all on Windows.

- **1.7.0:** an Alt-click on open ground saying just "Waypoint added", and the arrow calling such a waypoint
  "Waypoint", with `ShowCoordinates` off; the coordinates back with it on, and turning it on and off in a game; with
  `VerboseLog`, the "state now" line when it is turned on, "on screen: Location Reached" at an arrival, `VerboseLog`
  named once, and "player alive" written after a respawn rather than when the dead player is removed.
- **1.6.0, the log file:** seen on 2026-10-03 - its first lines at a game's start; "the game is quitting" then "log
  closed" at a quit from the main menu and from inside a world; the next start keeping the last game's file as
  `-prev`; `ErrorLog` turned off and on again in a game (the file closed, then its header written again, "turned on
  again"); `VerboseLog` turned on and off in a game, with its lines for map clicks, arrivals, two Finds and the route's
  saves; a line repeated 44 times in a row written once, with a count. Not yet: `VerboseLog` during a death; a game error
  caught with its stack trace; a second copy of the game writing `.log.1`; Wayfinder's file in a game; the file's
  closing lines on a dedicated server (the 1.7.0 server load test, below, saw TomTom's and Wayfinder's files start
  there; the test stops the server without a shutdown).

- **1.5.2:** an Alt-click near a pin whose icon type the map's filter hides, and near a shared pin while shared pins
  are hidden (the pin the map shows counts).
- **1.5.1:** an Alt-click with several pins within reach (the nearest one counts), and one on a ping or another
  player's marker (a waypoint on the spot); the on-screen "Waypoint queued (2nd): <name>" for a followed pin (a
  second pin was followed on 2026-10-02; the on-the-spot form was seen on 2026-10-03, and since 1.6.0 `VerboseLog`
  writes each message as "on screen: ..."); dying close to the waypoint being followed (it stays in the route).
- **1.5.0:** a Wooden Greatsword Find at an Infested Mine, a Ruined Dvergr Tower and a Rock Spire; a mine whose chests
  have all been emptied being left out, as the host; pasting Valheim's `pos` line and Server Devcommands' `pos` line;
  with `Use3DDistance` on, a clifftop map click (on the terrain and on a Mistlands rock), and a Find place still
  measured at the ground after closing the game and opening the world again (going to the main menu and back to the
  same world does not read the saved route). A Find place's mark as an estimate came back after a restart on
  2026-10-03; an arrival there after one was not tried.
- **1.4.1:** a saved route held open by another program while the world loads (the file must survive, and the plugin
  tries to read it again after waits of 2, 4, 6 ... seconds, then every 30 seconds until it can); a leftover `.old`
  file held open (the `.old2` spare); `PersistWaypoints` turned on in the middle of a world; the warning for a key the
  game cannot read, given per setting; the "Rock" label; a coordinate line refused as ambiguous; a rock or Bee Nest
  spawned with the console's `spawn` command (the game's cheat-check bypass off) left out of Find.
- **1.3.1:** the "Sealed Tower" label (a Wooden Atgeir Find); a caption too wide for the screen, whose name is
  shortened with "..." and its count kept; and a caption kept on screen when the arrow is moved toward an edge
  (`ArrowScreenX`). Long captions have been seen whole under the arrow in its default place.
- **Older checks never done:** Esc and a gamepad's B button closing the window; F11 not opening it over the pause menu;
  Tab and the mouse wheel doing nothing while it is open; deleting a marker and a followed pin with a gamepad; the
  right-click cases: on a marker, next to a followed pin, and with a followed pin nearer the pointer than a marker;
  Alt+double-click; the arrow hidden while sleeping and over the inventory, a trader and the pause menu; the route
  after a world switch; a single marker after a respawn; a click on the window never reaching the large map behind it.
- **Multiplayer:** a player joining a server that runs the plugin - a Find answered by the server's plugin, `WhoMayFind
  = AdminsOnly` refusing a player and then accepting them once they are an admin, a server without the plugin, a Start
  Server host with a player joining, a crossplay join, and a player and a server on different versions (a Find the
  server's plugin does not know - a Bee Nest on a 1.3.x server - asked the way a Vegvisir does). The server side has
  been seen only loading and logging `Ready to answer players' Find` on a Windows dedicated server, each time with
  BepInEx's console off and no `-crossplay`: TomTom 1.3.0 builds from before its release, and on 2026-10-04 the 1.7.0
  release zips of TomTom and of Wayfinder (each stopped while the server was still generating its new world, before it
  opened to players). No rented server has been tried, and a load test with BepInEx's console window left on, as the
  pack ships it, is still to do, as is following the package READMEs' server steps from a fresh download, with Valheim's
  own `start_headless_server.bat` (which passes `-crossplay`). Not measured: how long a Find takes on a real server,
  what it costs the server's frame rate, and whether a slow one can exceed the 120 seconds a player waits for its
  result.
- **Linux and Steam Deck** players and Linux servers. The package holds no native code.
- **BepInEx 5.4.23.5** (BepInExPack Valheim 5.4.2350 and later): the games and server tests so far ran BepInEx 5.4.23.3
  (pack 5.4.2333, the version both manifests name). The newer pack's changelog says its `Loading [...]` line now ends
  with the plugin's GUID, so step 6's first line would read `Loading [TomTom 1.7.0] (DoomMachine.TomTom)` - read from
  the changelog, not seen.
- **Wayfinder** is built and checked with every release but has not been played; its 1.7.0 release zip has been
  loaded on a dedicated server (above).

## Not verified

- **Bee Nest place names.** Only "Bear Cave" is the game's own name; "Abandoned House", "Contested Tower", "Draugr
  Village" and "Abandoned Village" are this plugin's words, and the place called "Abandoned Village" was named by
  elimination against the wiki's list. To check against the wiki.
- **How often places hold what Find looks for.** The chance that a village holds a Bee Nest comes from the game's
  generation rules only, not from worlds looked at; the chance that an Infested Mine's chests hold a Wooden Greatsword
  is not known. How often a wild Bee Nest has fallen or been broken by the time a player arrives is not known either.
- **Infested Mine chests and `SkipCheckedChests`:** the game usually generates an area first as saved data only - a
  little beyond the area loaded around you, or, on a server, around a player who joined - and that a mine's chests are
  filled then is worked out from the code, not seen in play.
- **Rock Spires:** whether a player standing at a spire's foot can get within the arrival radius of its centre.
- **Arrival on rocks and roofs:** with `Use3DDistance` on, the ground is the terrain, so a player on a big rock, a roof
  or a Mistlands rock at a map click's spot arrives only once within the arrival radius of the terrain beneath it;
  where that terrain lies under water, arrival is horizontal (worked out from the code, not seen).
- **Sealed Towers and `SkipCheckedChests`.** Which chests count as a Sealed Tower's assumes that each chest lies inside
  its room; where the chests sit in those rooms has not been read from the game's data.
- **Servers with other mods:** mods that rename or remove locations, anti-cheat plugins, limits on message size, and
  mods that change the game's admin-list check (on which `WhoMayFind = AdminsOnly` relies) have not been tried.
- **The log file and other programs:** which programs can open it while the game runs (it is shared for reading the
  way BepInEx shares `LogOutput.log`); and two copies of the game started from one folder have not been tried with it
  (on Windows the second copy adds to `.log.1`; on Linux, where an open file can still be renamed, it would move the
  first copy's file aside to `-prev` - read from the code, not tried).

## Next release (small, planned)

- **Preflight:** in the map click's test of a hidden pin, which way it branches, another test joined to it, and which
  way the two helpers that read the map's icon filter and shared-pin fade test the map, and the type the fade helper
  expects its value to have, are not checked; nor are the pin
  lookups' owned and followed flags and the right-click's second lookup, or the map click prefix's early return over the
  window. For the log file, which of its own files is opened when, and with which file mode, is not checked: the unit
  tests run the rotation's choice for the plain file only; the numbered files' mode (appended to) and a reopen's were
  tried with a test harness, not by a test. Check 21 does not see code run on the log file's lock's behalf rather than
  named there (a delegate handed to a library method, reflection by other means), a lock released early on one path
  or taken and released in two helpers, or a type of the plugin's own declared in a `System` namespace; nor check 22
  a trace inside a lambda written in one of the methods it guards. Check 23 (coordinates on screen) does not see a
  position formatted in place (a waypoint's position turned into text without its coordinate text), text built in
  another method or kept in a field and then shown, a message shown through a delegate, the setting's value changed
  after it is bound, other ways of putting text on screen, or which waypoint's coordinates are passed; what its two
  rules decide is left to the unit tests.
- **`VerboseLog`'s 3D arrival line** does not say whether it measured to the loaded ground or to the player's height.
- **Saved routes in a mod manager's profile export:** r2modman's export (a profile as a code or as a file) copies the
  whole `BepInEx/config` folder, so the saved routes (`waypoints_<world>.txt`, with positions) go to whoever imports the
  profile - for Wayfinder, a friend would get the sharer's waypoints for the same world. To decide: say so in the
  package READMEs, or keep the routes where an export does not copy them (existing routes would have to move with
  them). Seen in r2modman's source (its development branch, 2026-10-02); its released build, Thunderstore Mod Manager
  and Gale were not checked. The log file is kept beside `LogOutput.log` for this reason.
- **Wayfinder's "part of the server's answer did not arrive" warning** is written only when the server's plugin found
  at least one place in range, before the explored filter, so its presence hints that something is there. It needs a
  lost network message, which a player cannot cause. To decide: keep it (a server failure) or leave it out of Wayfinder.
- **Tests:** a Find place's height is marked as an estimate and carried into and out of the route file; only the
  route file's format and the arrival rule are tested, not the code that marks the flag in Find and carries it to and
  from the queue and the file. Two more planted defects pass every check and test: a chest check measured in 3D (it
  fails safe) and the order of the arrow's static fields. Some conditions around reading an unreadable route again
  (the last try when another world loads or the game closes, the reset when another world loads, and the timed retry
  during play) are caught by no test or check in this repository.
- **Performance:** the plugin's `OnGUI` costs about 360 bytes and two calls per frame while the window is closed (worked
  out from Unity's code, not measured); turning Unity's `useGUILayout` off while it is closed would cut that, after a
  test in play.
- **The package READMEs inside the 1.7.0 zips** keep their Servers section as released: its "Tested so far" was
  written before those zips were load-tested on a dedicated server (2026-10-04). The READMEs in this repository say
  what was tested; the next release's zips will carry them.
- **Wording:** in TomTom's settings table, `ShowCoordinates` says the map click's "Waypoint added" message shows the
  coordinates "instead of `PinLabel`"; that message never names `PinLabel` - with the setting on, the coordinates
  follow it. To decide: an unnamed pin you follow is announced as "Waypoint set: marker" while the arrow calls it by
  `PinLabel`, and TomTom's window shows an unnamed waypoint's coordinates twice (in the name column and the coordinate
  column).
- **Preflight** reads the plugin's path, the game folder and the dedicated server's folder as wildcard patterns. In
  the plugin's path or the game folder, square brackets around something in a folder's name (`[1]`) make it report the
  file missing, unless another folder the pattern matches exists; a lone `]` in a folder above the plugin's own (or
  anywhere in the game folder's path) does the same, and a lone `[` or an empty pair `[]` stops it with a wildcard
  error. Either way it fails. In the server's folder the same brackets make it skip the server's assemblies, as if no
  server were installed, and pass with one check fewer - or stop with the same error.
- **The world's ID in `LogOutput.log`:** both editions write a world's ID number there in the info line about reading
  its saved route (`Restored <n> waypoint(s) for world <ID>`, and the line for a read that succeeds only on a later
  try), as well as in the warnings about that route; TomTom's package README says what its own log holds, not
  `LogOutput.log`. To decide: leave the ID out of those info lines, or say so in the package READMEs.
- The Bee Nest names and the Sealed Tower rooms under "Not verified".

## Put off for now

Multiplayer:
- **Stronger preflight checks of the server side.** Today they check that the right calls are made, not the decisions
  made with them: that the plugin counts the connection patch as applied only when it is, how the plugin tells which
  player a call came from, the inputs to the `WhoMayFind` decision, that a refused request gets the refusal and
  nothing else, the error handling around the server's search step, and the player's check that an answer came from
  the server.
- **A test of the round trip** in which an older server says it does not know a query - what lets players and servers
  on different versions work together.
- **The live multiplayer tests** above.
- **To make more precise, or add:** the README's wait before a Find on a server without the plugin (only a Find
  started in the first 5 seconds after your character first appears on a server waits), and a sentence, in no README
  yet, on what a forged server answer can do.

Wayfinder:
- A preflight check of the explored-land filter Wayfinder's Find passes (today the check covers the call both editions
  make, not that filter).
- Leaving out places whose chests or nests are known not to hold the item, as TomTom's `SkipCheckedChests` does;
  Wayfinder never does, so most places it queues for a Bee Nest hold none.
- "Explored" is tested at each place's own position.
- Find is silent, and places nothing if the game's explored-map test cannot be read.
- What Wayfinder cannot police: other mods that show coordinates, edits to its save file, and pins made with dev
  commands.
- A preflight check that Wayfinder's Find stays out of the log (today only the code keeps it out: what a Find queues,
  and the route it replaces, are not logged).

## Watching the game

- **Find's lists** of places, chests and nests come from Valheim 1.0.16's data; preflight prints a note on any other
  game build. After a game update, check them, and run Find again for "(possible)" merchant spots saved before it.
- **Private game members** reached by reflection (such as the map's pin list, and the game's handler for a
  Vegvisir-style answer, which Find patches): if an update changes one, the feature that uses it works less well or
  not at all (Wayfinder's map click, for one: the README's implementation notes say what it does then).

## Ideas (not planned)

- Controller support for the window: a uGUI window with controller navigation, and a controller gesture for the map.
- **Followed pins the game puts at a place's recorded position** (such as Vegvisir, runestone and boss pins): with
  `Use3DDistance` on, such a waypoint is measured at the pin's height, the world generator's estimate, which is not
  the ground (how far off it is at those places has not been measured); only a place Find found is marked as an
  estimate and measured at the ground. Measuring these pins the same way is an idea.
- A narrower digit-grouping rule, so that fewer lines such as `500,300` are refused: considered, not decided.
- Server options looked at and not built: a vanilla global key as the Find rule, a larger simulation distance on the
  server, and settings sync (not needed: the server tells each player its rule).
- A mutation-test script in this repository that plants a defect for each of preflight's checks and shows the check
  fails. Releases that change the checks are tried that way, with a script kept outside the repository; the planted
  defects known to pass every check are listed under "Next release" and "Put off for now", and for the map click's check
  in the README's Checking section.

## Decided or not planned - reopen only with a new reason

- **Markers are yours alone:** every marker is made with the game's `save: false` flag and no owner, so it never
  reaches the Cartography Table or your saved map.
- **Wayfinder shows and takes no coordinates**, in or out.
- **TomTom shows coordinates in its window and its console;** under the arrow and in the messages at the top left only
  with `ShowCoordinates` (off by default since 1.7.0) - without it, a waypoint without a name is called by the marker
  label there.
- **Removing a marker wins:** when one of this mod's markers is within reach of a right-click, or of the controller's
  delete button on the big map, the waypoint is what gets removed, never one of your own pins - even while the map
  hides the marker's icon type.
- **The pin nearest an Alt-click decides**, among the pins the map shows, a waypoint marker of this mod's included;
  equally near, a waypoint's pin (its waypoint is removed). While the modifier is held the game's own left click never
  runs. A ping, a shout, another player's marker or an event marker is never followed: one nearest
  the click puts a waypoint on the spot. A right-click delete keeps its own rule (above).
- **A new waypoint joins the end of the route**: the arrow stays on the one in front, and an Alt-click's message says
  where the new one waits.
- **Typed coordinates are read with a decimal dot only** (a pasted `pos` line's decimal commas are read). Reading a
  decimal comma would silently move lines that work today, such as `12,5,-30` (read as 12, 5, -30), so a comma between
  two digits is never read as one: when nothing else on the line shows how it writes numbers and reading it that way
  would name another place (`12,5 30`), the line is refused, with the way to write it. A comma followed by exactly
  three digits with no space (`1,234`) looks like digit grouping, and that line is refused too.
- **Console key bindings still run while the window is open.** Stopping them would mean hooking another mod's internals
  or blocking console commands broadly.
- **Other mods' hotkeys** may not work while the window is open, and F11 may not open it while another mod's window has
  the keyboard.
- **The window needs a keyboard and mouse** (see Ideas).
- **F11 is the default window key**, though it is also Valheim's screenshot key: rebind `ToggleWindowKey`.
- **No debug symbols ship**, so an error in the log has no line numbers: a symbol file would carry the build machine's
  paths.
- **New Find items do not change the server protocol's version.** A server on an older version answers a query it does
  not know as unknown, and the player's plugin then asks the way a Vegvisir does.
- **`SkipCheckedChests` is on by default** and can be turned off.
- **Wayfinder refuses only a bare map click on unexplored land;** following a pin works anywhere.
- **Find leaves out what players make:** an object placed through the build system, or spawned with the console's
  `spawn` or `location` command while the game's cheat-check bypass is off, is not listed. The game marks nothing the
  `vegetation` command makes, nor the rooms a `location` command's generator builds, so those still count; and a
  server on a plugin older than 1.4.1 answers without this filter.
- **With `Use3DDistance` on, a waypoint without a height of its own (a map click, two typed numbers, a pin placed on
  the map) and a place Find found are measured at the ground once it is loaded near you**, and horizontally over
  water; a height of the waypoint's own is used as it is. A waypoint without a height gets none from the world
  generator when it is added, and a Find place's generator height counts only until the ground is loaded: it is the
  generator's estimate, before the terrain is built and levelled, not the ground.
- **Not built:** a 3D distance in the arrow's caption; an on-screen notice when a saved route cannot be read (the log
  says so, and the route is kept); a `SkipCheckedChests` switch in the window; a wider search radius around Infested
  Mines (their chests lie within the usual one).
- **One Fuling camp kind stays in the Wooden Atgeir list** though the game does not place it today: harmless, and it
  covers an update that does.

## Done

Everything up to v1.5.0 is in the README's History. From here on, a finished item moves here with its version.

- **1.7.0:** TomTom's arrow and top-left messages no longer show a waypoint's coordinates unless `ShowCoordinates`
  is on (a waypoint without a name is called by `PinLabel`); `VerboseLog` writes the player's and the map's state in
  one line when it is turned on, logs
  "Location Reached", names itself once, writes setting values the same on every PC, and writes "player alive"
  once the new player is there; the log header says what `f-` means, and the package READMEs which file a held log
  goes to; preflight check 23 (coordinates on screen), and check 6 counts the new setting; `run-tests.sh` runs under a
  folder name with brackets.
- **1.6.0:** a log file of the plugin's own, `BepInEx/TomTom.log` or `BepInEx/Wayfinder.log`, with the last game's
  kept as `-prev.log`: warnings and errors and the game errors that run through the plugin's code (`ErrorLog`, on by
  default), and in TomTom a line for each event (`VerboseLog`, off by default); warnings where something failed
  silently; the explored-map test's failure and an unreadable message from the server's plugin are logged once a game
  session instead of once each; preflight checks where the log is written, that it cannot hurt the game and that
  Wayfinder has no verbose log, and the stricter map-click checks (a return value set twice or the modifier reset in
  the click's prefix, the pin a hidden-pin test is given).
- **1.5.2:** pins the map hides (an icon type filtered out, shared pins while hidden) no longer take an Alt-click; the
  game's own left click never runs while the modifier is held, even when the plugin cannot tell where the click
  landed;
  preflight follows the branch of Wayfinder's fog rule and requires it after the paths that follow a pin; the wording
  items of 1.5.0 (mines and spires, the Wooden Greatsword's chest check on a server, Wayfinder's `Use3DDistance`
  description, the README's arrival and checking notes); the README says what happens to Wayfinder's map click if a
  game update removes the explored-map test; `run-tests.sh` and `build.sh` are marked executable.
- **1.5.1:** an Alt-click no longer lets a pin you follow anywhere within reach win over the pin under the pointer,
  and a click on a ping or another player's marker puts a waypoint there; a waypoint an Alt-click adds behind others
  says so; dying close to the waypoint being followed no longer counts as
  reaching it; each Alt-click and each change you make to the route from the map, the window or the console is logged
  (a Find aside: TomTom logs one summary line, Wayfinder nothing about what it found); the package READMEs say that
  Alt-clicking a pin you follow stops following it.
