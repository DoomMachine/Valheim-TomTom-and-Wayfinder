# Open items and future work

The one list of what is untested, unverified, open or only an idea for TomTom and Wayfinder - so none of it has to be
worked out again. Keep it current: add an item when it comes up, and when one is done, move it to "Done" with the
version or commit that did it. How the plugins work, and why, is in the README's "Implementation notes"; what each
release changed is in its "History".

Last updated: 2026-09-30, after v1.5.0.

## Not yet tried in play

1.4.1 has not been played on its own; all its changes are in 1.5.0. 1.5.0 was first played on 2026-09-30, in single
player, and that session is not yet reflected in the lists below. 1.4.0 was used in at least three short sessions,
1.3.1 in at least two and 1.3.0 in at least two, all on Windows.

- **1.5.0:** a Wooden Greatsword Find at an Infested Mine, a Ruined Dvergr Tower and a Rock Spire; a mine whose chests
  have all been emptied being left out, as the host; pasting Valheim's `pos` line and Server Devcommands' `pos` line;
  with `Use3DDistance` on, a clifftop map click (on the terrain and on a Mistlands rock), and a Find place still
  measured at the ground after closing the game and opening the world again (going to the main menu and back to the
  same world does not read the saved route).
- **1.4.1:** a saved route held open by another program while the world loads (the file must survive, and the plugin
  tries to read it again after waits of 2, 4, 6 ... seconds, then every 30 seconds until it can); a leftover `.old`
  file held open (the `.old2` spare); `PersistWaypoints` turned on in the middle of a world; the warning for a key the
  game cannot read, given per setting; the "Rock" label; a coordinate line refused as ambiguous; a rock or Bee Nest
  spawned with the console's `spawn` command (the game's cheat-check bypass off) left out of Find.
- **1.4.0:** a Bee Nest Find.
- **1.3.1:** the "Sealed Tower" label (a Wooden Atgeir Find); a caption too wide for the screen, whose name is
  shortened with "..." and its count kept; and a caption kept on screen when the arrow is moved toward an edge
  (`ArrowScreenX`). Long captions have been seen whole under the arrow in its default place.
- **Older checks never done:** Esc and a gamepad's B button closing the window; F11 not opening it over the pause menu;
  Tab and the mouse wheel doing nothing while it is open; deleting a marker and a followed pin with a gamepad; the
  right-click cases: on a marker, next to a followed pin, and with a followed pin nearer the pointer than a marker;
  Alt+double-click; the arrow hidden while sleeping and over the inventory, a trader and the pause menu; the route
  after a world switch; a single marker after a respawn; a click on the window never reaching the large map behind it.
- **Multiplayer:** a player joining a server that runs the plugin - a Find answered by the server's plugin,
  `WhoMayFind = AdminsOnly` refusing a player and then accepting them once they are an admin, a server without the
  plugin, a Start Server host with a player joining, and a crossplay join. The server side has been seen only loading
  and getting ready on a Windows dedicated server, with 1.3.0 builds from before its release and BepInEx's console
  off; no later version has been loaded on a server, and a load test with a released zip and the default console
  setting is still to do. Not measured: how long a Find takes on a real server, and whether a slow one can exceed the
  120 seconds a player waits for its result.
- **Linux and Steam Deck** players and Linux servers. The package holds no native code.
- **Wayfinder** is built and checked with every release but has not been played.

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
- **Servers with other mods:** mods that rename or remove locations, anti-cheat plugins, and limits on message size
  have not been tried.

## Next release (small, planned)

- **Preflight:** the check of Wayfinder's fog rule finds the rule but not which way it branches; make it follow the
  branch, and require the rule to come after the paths that follow a pin.
- **Tests:** a Find place's height is marked as an estimate and carried into and out of the route file; only the
  route file's format and the arrival rule are tested, not the code that marks the flag in Find and carries it to and
  from the queue and the file. Two more planted defects pass every check and test: a chest check measured in 3D (it
  fails safe) and the order of the arrow's static fields. Some conditions around reading an unreadable route again
  (the last try when another world loads or the game closes, the reset when another world loads, and the timed retry
  during play) are caught by no test or check in this repository.
- **Wording:**
  - the package READMEs' "not every mine has a treasure room" and "only some spires have one" - better: a mine is not
    sure to have one, and about 3 spires in 4 have one;
  - the README says a Find place is measured horizontally where the ground is not loaded, but it keeps its estimated
    height;
  - the README's "more than 10 m off the ground at about 1% of places" is from one seed, before a place's own
    levelling;
  - the README's "nothing is stored" should be "nothing measured is stored";
  - `SkipCheckedChests`' description and the TomTom README should say that, joined to a server, the Wooden
    Greatsword's chest check needs the server on 1.5.0;
  - Wayfinder's `Use3DDistance` description mentions typed values, which Wayfinder does not take;
  - the README's History and the fog-rule check's message claim more than the check tests;
  - the README's route check says "before the merge", which the check does not test.
- **Docs:** if a game update removes the game's explored-map test that Wayfinder reads, its map click places no
  waypoint anywhere, with nothing on screen to say why (following a pin and removing a marker still work). Say so in
  the README.
- **Repository:** `run-tests.sh` and `build.sh` are not marked executable.
- **Performance:** the plugin's `OnGUI` costs about 360 bytes and two calls per frame while the window is closed (worked
  out from Unity's code, not measured); turning Unity's `useGUILayout` off while it is closed would cut that, after a
  test in play.
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

## Watching the game

- **Find's lists** of places, chests and nests come from Valheim 1.0.16's data; preflight prints a note on any other
  game build. After a game update, check them, and run Find again for "(possible)" merchant spots saved before it.
- **Private game members** reached by reflection (such as the map's pin list, and the game's handler for a
  Vegvisir-style answer, which Find patches): if an update changes one, the feature that uses it works less well or
  not at all (Wayfinder's map click, for one; see "Next release").

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
  defects known to pass every check are listed under "Next release" and "Put off for now".

## Decided or not planned - reopen only with a new reason

- **Markers are yours alone:** every marker is made with the game's `save: false` flag and no owner, so it never
  reaches the Cartography Table or your saved map.
- **Wayfinder shows and takes no coordinates**, in or out.
- **Removing a marker wins:** when one of this mod's markers is within reach of a right-click, or of the controller's
  delete button on the big map, the waypoint is what gets removed, never one of your own pins.
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
