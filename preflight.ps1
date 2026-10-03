# Preflight check for the TomTom and Wayfinder plugins.
#
# Confirms, without launching the game, that:
#   1. the plugin identifies itself as the expected edition, and excludes its sibling edition
#   2. every [HarmonyPatch] target type and method still exists in the shipped game assemblies, and every [HarmonyPatch]
#      class is applied by Plugin.ApplyPatches
#   3. every private member reached by reflection still exists
#   4. map markers can only ever be created local-only (never shared through a Cartography Table)
#   5. a pin the player promotes is never modified or removed - only the mod's own markers are
#   6. Wayfinder contains no coordinate-entry code or coordinate readout at all; TomTom still does
#   7. every assembly the plugin references can be resolved from the game folder
#   8. every game, Unity, BepInEx and Harmony type and member the plugin uses resolves with its exact signature
#   9. the Chat.HasFocus postfix still runs last (Chatter's postfix overwrites the result and loads later)
#  10. routes are saved only through SafeFile.WriteAllText, which flushes the new file to disk before swapping it in,
#      and a route that could not be read when its world loaded is not saved over
#  11. a key Valheim cannot read cannot stop the waypoint tick: configurable keys are read only through Hotkeys,
#      whose reads are caught (and not rethrown), no literal key the game cannot read is used anywhere, and the
#      tick runs in a try block of its own that reads no key
#  12. a location search cannot make a shared pin: the server's answers are caught before vanilla turns them into
#      saved pins, and no request is sent unless that catch is in place
#  13. Wayfinder's search reads MinimapAccess.IsExplored and TomTom's does not; LocationSearch.Finish hands the search's
#      places and query to SearchRules.FinishHits once, TomTom's with no filter (13b); and the server's known-empty drop
#      in SearchJob's scan is wired to the request (13c), and the scan skips an object with a creator or the cheat
#      flag (13d)
#  16. Wayfinder places a waypoint from a map click only on explored land; TomTom's map click does not read it
#  17. every code path that makes a waypoint is a known one
#  18. a map click decides by the right pin (MapClickRules.Decide, given the distance to each of the four nearest pins
#      the large map shows, before any of WaypointManager's route-changing methods is called), and ArrivalRules.Step is
#      told whether the player is dead (Character.IsDead)
#  19. the Alt-click is the plugin's, and only the Alt-click: the map click's prefix leaves a click without the modifier to
#      the game and returns !held
#  20. the plugin's own log file is written beside LogOutput.log under the plugin's own names, and nowhere else
#  21. the log file cannot hurt the game: every way into it catches what goes wrong, it logs nothing through BepInEx but
#      one warning, under its lock it names nothing outside .NET's System types and its own code (not seen: a lock
#      released early on one path or across two helpers, a type of its own in a System namespace), and its defaults
#      are ErrorLog on and (TomTom) VerboseLog off
#  22. Wayfinder has no verbose log at all; TomTom's is built in; and no trace sits where checks read exact shapes
#  23. the arrow and the messages call no Waypoint text but ScreenName (and, in TomTom, CoordText), and in TomTom every
#      CoordText or CoordinateFormat call there goes to MapClickRules' two rules told ShowCoordinates (bound once, off
#      by default); Wayfinder hands them none
#  14. the server side: the plugin runs in valheim_server too; the server's WhoMayFind decides only this plugin's
#      requests, knows a player by the connection their call came on, and every place in range is sent back
#  15. when Valheim's dedicated server is installed beside the game (or at -ServerDir), every reference also
#      resolves against the server's own assemblies - a separate build of the game, not a copy
#
# A rename in a Valheim update shows up here as a failure instead of as a broken feature in-game.
#
#   powershell -ExecutionPolicy Bypass -File preflight.ps1                        # deployed TomTom
#   powershell -ExecutionPolicy Bypass -File preflight.ps1 -Edition Wayfinder -Plugin build\Wayfinder\Wayfinder.dll

param(
    [ValidateSet("TomTom", "Wayfinder")]
    [string]$Edition = "TomTom",
    [string]$ValheimDir = "E:\SteamLibrary\steamapps\common\Valheim",
    [string]$Plugin = "",
    [string]$ServerDir = ""
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $ValheimDir "valheim_Data\Managed"
$core = Join-Path $ValheimDir "BepInEx\core"
if ($Plugin -eq "") { $Plugin = Join-Path $ValheimDir "BepInEx\plugins\DoomMachine-$Edition\$Edition.dll" }
if ($ServerDir -eq "") { $ServerDir = Join-Path (Split-Path $ValheimDir -Parent) "Valheim dedicated server" }

$expectedGuid = "DoomMachine.$Edition"
$siblingGuid = if ($Edition -eq "TomTom") { "DoomMachine.Wayfinder" } else { "DoomMachine.TomTom" }
$expectedVersion = "1.7.0"

if (-not (Test-Path (Join-Path $core "Mono.Cecil.dll")) -or -not (Test-Path (Join-Path $managed "assembly_valheim.dll"))) {
    Write-Output ("FAIL  Valheim with BepInEx not found at '{0}' (it needs valheim_Data\Managed\assembly_valheim.dll and BepInEx\core\Mono.Cecil.dll)." -f $ValheimDir)
    Write-Output "      Pass the game folder: preflight.ps1 -ValheimDir <the folder holding valheim.exe>"
    exit 1
}
Add-Type -Path (Join-Path $core "Mono.Cecil.dll")

if (-not (Test-Path $Plugin)) { Write-Output "FAIL  plugin not found: $Plugin"; exit 1 }
Write-Output ("Checking {0} ({1})" -f $Edition, $Plugin)
Write-Output ""

# Load every game assembly once so patch targets can be looked up by type name.
$modules = @{}
foreach ($f in (Get-ChildItem $managed -Filter *.dll) + (Get-ChildItem $core -Filter *.dll)) {
    try { $modules[$f.FullName] = [Mono.Cecil.ModuleDefinition]::ReadModule($f.FullName) } catch { }
}

function Find-GameType([string]$name) {
    foreach ($m in $modules.Values) {
        $t = $m.GetType($name)
        if ($t) { return $t }
    }
    return $null
}

# Read with a resolver so the plugin's references can be resolved against the shipped game (check 8).
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$resolver.AddSearchDirectory($core)
$readerParams = New-Object Mono.Cecil.ReaderParameters
$readerParams.AssemblyResolver = $resolver
$plug = [Mono.Cecil.ModuleDefinition]::ReadModule($Plugin, $readerParams)
$failures = 0
$checks = 0

Write-Output "== plugin identity =="
$pluginType = $plug.GetType("Waypointer.Plugin")
$bepGuid = $null; $bepName = $null; $bepVersion = $null; $incompatible = @()
if ($pluginType) {
    foreach ($ca in $pluginType.CustomAttributes) {
        $a = @($ca.ConstructorArguments | ForEach-Object { "$($_.Value)" })
        if ($ca.AttributeType.Name -eq "BepInPlugin") { $bepGuid = $a[0]; $bepName = $a[1]; $bepVersion = $a[2] }
        if ($ca.AttributeType.Name -eq "BepInIncompatibility") { $incompatible += $a[0] }
    }
}
$checks++
if ($bepGuid -eq $expectedGuid -and $bepName -eq $Edition -and $bepVersion -eq $expectedVersion) {
    Write-Output ("  ok    BepInPlugin {0} / {1} / {2}" -f $bepGuid, $bepName, $bepVersion)
} else {
    Write-Output ("  FAIL  BepInPlugin is '{0} / {1} / {2}', expected '{3} / {4} / {5}'" -f $bepGuid, $bepName, $bepVersion, $expectedGuid, $Edition, $expectedVersion)
    $failures++
}
$checks++
if ($incompatible -contains $siblingGuid) {
    Write-Output ("  ok    declares BepInIncompatibility with {0} (never both loaded)" -f $siblingGuid)
} else {
    Write-Output ("  FAIL  missing BepInIncompatibility with {0}" -f $siblingGuid)
    $failures++
}
Write-Output ""

Write-Output "== Harmony patch targets =="
foreach ($t in $plug.GetTypes()) {
    foreach ($ca in $t.CustomAttributes) {
        if ($ca.AttributeType.Name -ne "HarmonyPatch") { continue }
        $args = @($ca.ConstructorArguments)
        if ($args.Count -lt 2) { continue }

        $typeName = $args[0].Value.ToString()
        $methodName = $args[1].Value.ToString()
        $checks++

        $gt = Find-GameType $typeName
        if (-not $gt) {
            Write-Output ("  FAIL  {0}: type '{1}' not found" -f $t.Name, $typeName)
            $failures++
            continue
        }
        # With an argument-type list, match the overload too (RemovePin has two).
        $want = $null
        if ($args.Count -ge 3) { $want = (@($args[2].Value | ForEach-Object { $_.Value.FullName }) -join ",") }
        $hit = $gt.Methods | Where-Object { $_.Name -eq $methodName -and
            ($want -eq $null -or (@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ",") -eq $want) }
        if (-not $hit) {
            Write-Output ("  FAIL  {0}: {1}.{2} not found" -f $t.Name, $typeName, $methodName)
            $failures++
        } else {
            $sig = ($hit | Select-Object -First 1)
            $vis = if ($sig.IsPublic) { "public" } else { "private" }
            Write-Output ("  ok    {0,-44} -> {1}.{2} ({3})" -f $t.Name, $typeName, $methodName, $vis)
        }
    }
}

# Every [HarmonyPatch] class must be applied. Plugin.ApplyPatches patches the classes named in two explicit arrays (the
# dedicated server's, then the game's), so a class left out would never run, and only the log line "Applied N of N
# patches." in play would show it. The game's array must name each such class once, the server's exactly the two server
# patches, and each array must be filled to its length (an empty slot would fail PatchAll).
$checks++
$hpClasses = @($plug.GetTypes() | Where-Object { @($_.CustomAttributes | Where-Object { $_.AttributeType.Name -eq "HarmonyPatch" }).Count -gt 0 } | ForEach-Object { $_.FullName } | Sort-Object)
$serverPatchClasses = @("Waypointer.Game_RPC_DiscoverClosestLocation_Patch", "Waypointer.ZRoutedRpc_RPC_RoutedRPC_Patch")
$apWhy = @()
$apm = $null
if ($pluginType) { $apm = $pluginType.Methods | Where-Object { $_.Name -eq "ApplyPatches" -and $_.HasBody } | Select-Object -First 1 }
if (-not $apm) { $apWhy += "Waypointer.Plugin.ApplyPatches not found" }
else {
    $ai = @($apm.Body.Instructions)
    $patchArrays = @(); $cur = $null
    for ($k = 1; $k -lt $ai.Count; $k++) {
        if ($ai[$k].OpCode.Name -eq "newarr" -and "$($ai[$k].Operand)" -eq "System.Type") {
            $p = $ai[$k - 1]; $size = $null
            if ($p.OpCode.Name -match '^ldc\.i4\.([0-8])$') { $size = [int]$Matches[1] }
            elseif ($p.OpCode.Name -eq "ldc.i4" -or $p.OpCode.Name -eq "ldc.i4.s") { $size = [int]"$($p.Operand)" }
            $cur = @{ Size = $size; Types = @() }; $patchArrays += ,$cur; continue
        }
        if ($cur -and ($k + 2) -lt $ai.Count -and $ai[$k].OpCode.Name -eq "ldtoken" -and $ai[$k + 1].OpCode.Name -like "call*" -and
            "$($ai[$k + 1].Operand)" -like "*Type::GetTypeFromHandle*" -and $ai[$k + 2].OpCode.Name -eq "stelem.ref") { $cur.Types += $ai[$k].Operand.FullName }
    }
    if ($patchArrays.Count -ne 2) { $apWhy += ("ApplyPatches builds {0} arrays of patch classes, expected 2 (the server's, then the game's)" -f $patchArrays.Count) }
    else {
        foreach ($a in $patchArrays) { if ($a.Size -ne $a.Types.Count) { $apWhy += ("an array of {0} patch classes is given {1}" -f $a.Size, $a.Types.Count) } }
        if ((@($patchArrays[0].Types | Sort-Object) -join ",") -ne (@($serverPatchClasses | Sort-Object) -join ",")) { $apWhy += ("the server's array names {0}, expected exactly {1}" -f ($patchArrays[0].Types -join ", "), ($serverPatchClasses -join ", ")) }
        $gameArray = $patchArrays[1].Types
        $twice = @($gameArray | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
        if ($twice.Count) { $apWhy += "named twice in the game's array: " + ($twice -join ", ") }
        $left = @($hpClasses | Where-Object { $gameArray -notcontains $_ })
        if ($left.Count) { $apWhy += "[HarmonyPatch] classes Plugin.ApplyPatches never applies: " + ($left -join ", ") }
        $notPatch = @($gameArray | Where-Object { $hpClasses -notcontains $_ })
        if ($notPatch.Count) { $apWhy += "in the game's array without [HarmonyPatch]: " + ($notPatch -join ", ") }
    }
}
if ($apWhy.Count -eq 0) { Write-Output ("  ok    Plugin.ApplyPatches applies all {0} [HarmonyPatch] classes; on a dedicated server only the 2 server patches" -f $hpClasses.Count) }
else { foreach ($w in $apWhy) { Write-Output "  FAIL  $w" }; $failures++ }

Write-Output ""
Write-Output "== private members reached by reflection =="
# Read from the IL, not listed by hand: AccessTools.X(typeof(T), "name"[, new Type[] { typeof(P) }]) compiles
# to  ldtoken T; call Type::GetTypeFromHandle; ldstr "name"; [ldtoken P; ...]; call AccessTools::X.
# Methods are matched on their exact parameter types, as AccessTools.Method(type, name, Type[]) matches them,
# and every member's type is compared with what MinimapAccess casts it to: a retyped m_pins passes a by-name
# check but reads back as an empty list, with nothing in the log.
# $reflectedTypes is the expected type of each member (a method's return type). A member reflected in the
# plugin but missing here, or listed here but no longer found in the IL, fails - so neither this list nor the
# IL pattern can drift from MinimapAccess without this section saying so.
$reflectedTypes = @{
    'Minimap.ScreenToWorldPoint'    = 'UnityEngine.Vector3'
    'Minimap.GetClosestPin'         = 'Minimap/PinData'
    'Minimap.m_pins'                = 'System.Collections.Generic.List`1<Minimap/PinData>'
    'Minimap.m_visibleIconTypes'    = 'System.Boolean[]'
    'Minimap.m_sharedMapDataFade'   = 'System.Single'
    'Minimap.PinInteractRadius'     = 'System.Single'
    'Minimap.IsExplored'            = 'System.Boolean'
    'Game.RPC_DiscoverLocationResponse' = 'System.Void'
}
$seen = @{}
$lookups = 0
$recognised = 0
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    for ($k = 0; $k -lt $ins.Count; $k++) {
        $o = $ins[$k].Operand
        if ($o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "HarmonyLib.AccessTools" -and $o.Name -ne "MethodDelegate") { $lookups++ }
        if ($ins[$k].OpCode.Name -ne "ldstr" -or $k -lt 2) { continue }
        if ($ins[$k-2].OpCode.Name -ne "ldtoken" -or "$($ins[$k-1].Operand)" -notlike "*Type::GetTypeFromHandle*") { continue }
        $ptypes = @(); $kind = $null
        # Stops at the first AccessTools call; 40 covers the legacy compiler's local-variable array setup.
        for ($j = $k + 1; $j -lt [Math]::Min($ins.Count, $k + 40); $j++) {
            $pj = $ins[$j].Operand
            if ($ins[$j].OpCode.Name -eq "ldtoken") { $ptypes += $pj.FullName }
            if ($pj -is [Mono.Cecil.MethodReference] -and $pj.DeclaringType.FullName -eq "HarmonyLib.AccessTools") { $kind = $pj.Name; break }
        }
        if (-not $kind) { continue }
        $recognised++
        $checks++
        $typeName = $ins[$k-2].Operand.FullName
        $name = "$($ins[$k].Operand)"
        $key = $typeName + "." + $name
        $seen[$key] = $true
        $gt = Find-GameType $typeName
        if (-not $gt) { Write-Output ("  FAIL  {0}: type {1} missing" -f $t.Name, $typeName); $failures++; continue }
        $found = $null; $type = $null
        if ($kind -like "*Field*") {
            $found = @($gt.Fields | Where-Object { $_.Name -eq $name })[0]
            if ($found) { $type = $found.FieldType.FullName }
        } elseif ($kind -like "*Property*") {
            $found = @($gt.Properties | Where-Object { $_.Name -eq $name })[0]
            if ($found) { $type = $found.PropertyType.FullName }
        } elseif ($kind -like "*Method*") {
            $want = $ptypes -join ","
            $found = @($gt.Methods | Where-Object { $_.Name -eq $name -and ((@($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ",") -eq $want) })[0]
            if ($found) { $type = $found.ReturnType.FullName }
        }
        if (-not $found) {
            $sig = if ($kind -like "*Method*") { "(" + ($ptypes -join ", ") + ")" } else { "" }
            Write-Output ("  FAIL  {0}: AccessTools.{1} finds no {2}{3} in the game" -f $t.Name, $kind, $key, $sig); $failures++; continue
        }
        if (-not $reflectedTypes.ContainsKey($key)) { Write-Output ("  FAIL  {0} is reflected but has no expected type in `$reflectedTypes (it is {1})" -f $key, $type); $failures++; continue }
        if ($reflectedTypes[$key] -ne $type) { Write-Output ("  FAIL  {0} is {1} in the game, the plugin expects {2}" -f $key, $type, $reflectedTypes[$key]); $failures++; continue }
        Write-Output ("  ok    {0,-30} {1,-8} {2}" -f $key, $kind, $type)
    }
} }
foreach ($key in @($reflectedTypes.Keys)) {
    if ($seen.ContainsKey($key)) { continue }
    $checks++
    Write-Output ("  FAIL  {0} is in `$reflectedTypes but no AccessTools(typeof(T), ""name"") lookup of it is in the plugin" -f $key)
    $failures++
}
$checks++
if ($lookups -ne $recognised) {
    Write-Output ("  FAIL  {0} AccessTools lookups in the plugin, {1} in the typeof(T), ""name"" shape this section checks" -f $lookups, $recognised)
    $failures++
}

Write-Output ""
Write-Output "== multiplayer safety: markers must stay local-only =="
# Valheim shares pins through Minimap.GetSharedMapData (the Cartography Table) and saves them through
# Minimap.GetMapData, and both iterate ONLY over pins whose m_save flag is true. A pin whose m_ownerID is
# not 0 counts as another player's: ResetSharedMapData and AddSharedMapData remove it, and UpdatePins hides
# it while shared-map fade is off. So a marker is local-only precisely as long as it is created with
# save:false and ownerID 0 and neither field is ever set to anything else.
#
# All pin creation goes through WaypointManager.CreateLocalOnlyPin. This checks VALUES, not just presence:
#   - Minimap.AddPin is referenced exactly once (call, ldftn, anything), by a call in CreateLocalOnlyPin,
#     and nothing builds a PinData, adds one to a PinData list, or names AddPin / m_save / m_ownerID
#     in a string (reflection). Renaming CreateLocalOnlyPin means changing it here too.
#   - that call passes a literal false for save and a literal 0 for ownerID
#   - after the call, CreateLocalOnlyPin re-asserts m_save = false and m_ownerID = 0 (a missing one fails)
#   - every store into PinData.m_save or PinData.m_ownerID anywhere stores a literal false / 0
# A value this cannot read as a literal 0 fails, so that a person looks at it.
function Test-LiteralZero($ins, [int]$at) {
    # True when $ins[$at] pushes a literal 0 (false, 0, 0L), looking through a conv.i8.
    if ($at -lt 0) { return $false }
    $p = $ins[$at]
    if ($p.OpCode.Name -eq "conv.i8") { if ($at -lt 1) { return $false }; $p = $ins[$at - 1] }
    $n = $p.OpCode.Name
    if ($n -eq "ldc.i4.0") { return $true }
    if ($n -eq "ldc.i4.s" -or $n -eq "ldc.i4" -or $n -eq "ldc.i8") { return ([long]"$($p.Operand)" -eq 0) }
    return $false
}
function Get-ArgumentSources($ins, [int]$callAt, $handlers = $null) {
    # Replays the evaluation stack over the code before a call and returns, for each value the call
    # consumes (the instance first), the index of the instruction that pushed it; $null if it does not add
    # up. A branch or return starts a new statement (compiled C# has an empty stack there); a branch
    # INSIDE the argument list (a ?: operand) leaves too few values, so it returns $null and the check fails.
    # Pass the method's ExceptionHandlers for a call that may follow a catch block: a catch (or filter) handler
    # starts with the exception object on an otherwise empty stack, which the straight replay never pushed.
    $stack = New-Object System.Collections.ArrayList
    for ($k = 0; $k -lt $callAt; $k++) {
        $i = $ins[$k]
        if ($handlers) {
            foreach ($h in $handlers) {
                $ht = "$($h.HandlerType)"
                if ((($ht -eq "Catch" -or $ht -eq "Filter") -and $h.HandlerStart -eq $i) -or ($ht -eq "Filter" -and $h.FilterStart -eq $i)) { $stack.Clear(); [void]$stack.Add($k) }
                elseif (($ht -eq "Finally" -or $ht -eq "Fault") -and $h.HandlerStart -eq $i) { $stack.Clear() }
            }
        }
        $pop = "$($i.OpCode.StackBehaviourPop)"; $push = "$($i.OpCode.StackBehaviourPush)"
        $flow = "$($i.OpCode.FlowControl)"
        if ($flow -eq "Branch" -or $flow -eq "Cond_Branch" -or $flow -eq "Return" -or $flow -eq "Throw") { $stack.Clear(); continue }
        $nPop = 0
        if ($pop -eq "Varpop") { $nPop = $i.Operand.Parameters.Count; if ($i.Operand.HasThis -and $i.OpCode.Name -ne "newobj") { $nPop++ } }
        elseif ($pop -ne "Pop0") { $nPop = @($pop -split "_").Count }
        if ($nPop -gt $stack.Count) { return $null }
        $src = $k
        if ($i.OpCode.Name -eq "conv.i8") { $src = $stack[$stack.Count - 1] }     # a widened literal is still that literal
        if ($nPop -gt 0) { $stack.RemoveRange($stack.Count - $nPop, $nPop) }
        $nPush = 1
        if ($push -eq "Push0") { $nPush = 0 }
        elseif ($push -eq "Push1_push1") { $nPush = 2 }
        elseif ($push -eq "Varpush" -and $i.Operand.ReturnType.FullName -eq "System.Void") { $nPush = 0 }
        for ($j = 0; $j -lt $nPush; $j++) { [void]$stack.Add($src) }
    }
    $c = $ins[$callAt].Operand; $n = $c.Parameters.Count; if ($c.HasThis) { $n++ }
    if ($stack.Count -lt $n) { return $null }
    return ,@($stack.GetRange($stack.Count - $n, $n))
}

$addPinRefs = @()
$badStores = @()
$bypass = @()
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = @($m.Body.Instructions)
        for ($k = 0; $k -lt $ins.Count; $k++) {
            $i = $ins[$k]; $op = $i.Operand
            if ($op -is [Mono.Cecil.MethodReference] -and $op.Name -eq "AddPin" -and $op.DeclaringType.FullName -eq "Minimap") {
                $addPinRefs += ,@($t, $m, $ins, $k)
                Write-Output ("  note  Minimap.AddPin referenced from {0}.{1} ({2})" -f $t.Name, $m.Name, $i.OpCode.Name)
            }
            # Other ways to make a pin without that call: build a PinData, put one into a PinData list
            # (MinimapAccess.GetPins hands out the live m_pins), or reach AddPin / the flags by name.
            if ($i.OpCode.Name -eq "newobj" -and "$op" -like "*Minimap/PinData::.ctor*") { $bypass += ("{0}.{1}: new PinData" -f $t.Name, $m.Name) }
            if ($i.OpCode.Name -like "call*" -and "$op" -match 'List`1<Minimap/PinData>::(Add|Insert|AddRange|InsertRange)\(') { $bypass += ("{0}.{1}: adds to a PinData list" -f $t.Name, $m.Name) }
            if ($i.OpCode.Name -eq "ldstr" -and @("AddPin", "m_save", "m_ownerID") -contains "$op") { $bypass += ("{0}.{1}: names '{2}' in a string (reflection)" -f $t.Name, $m.Name, $op) }
            if ($i.OpCode.Name -eq "stfld" -and ("$op" -like "*Minimap/PinData::m_save" -or "$op" -like "*Minimap/PinData::m_ownerID")) {
                if (-not (Test-LiteralZero $ins ($k - 1))) { $badStores += ("{0}.{1} -> PinData.{2}" -f $t.Name, $m.Name, $op.Name) }
            }
        }
    }
}

$checks++
if ($badStores.Count -eq 0) {
    Write-Output "  ok    every store into PinData.m_save / m_ownerID is a literal false / 0"
} else {
    Write-Output "  FAIL  a store into PinData.m_save / m_ownerID is not a literal false / 0 - markers could be shared:"
    $badStores | ForEach-Object { Write-Output ("          " + $_) }
    $failures++
}

$checks++
$site = $null
if ($addPinRefs.Count -eq 1 -and $addPinRefs[0][1].Name -eq "CreateLocalOnlyPin" -and $addPinRefs[0][2][$addPinRefs[0][3]].OpCode.Name -like "call*") {
    $site = $addPinRefs[0]
}
if ($site -and $bypass.Count -eq 0) {
    Write-Output "  ok    exactly one AddPin call site, in CreateLocalOnlyPin, and no other way to make a pin"
} else {
    Write-Output ("  FAIL  expected one AddPin call, in CreateLocalOnlyPin, and nothing else making pins; found {0} AddPin reference(s)" -f $addPinRefs.Count)
    $bypass | ForEach-Object { Write-Output ("          " + $_) }
    $failures++
}

$checks++
$argOk = $false
if ($site) {
    $ins = $site[2]; $at = $site[3]
    $src = Get-ArgumentSources $ins $at
    # A member reference carries no parameter names; take them from the game's own AddPin.
    $nArgs = $ins[$at].Operand.Parameters.Count
    $def = @((Find-GameType "Minimap").Methods | Where-Object { $_.Name -eq "AddPin" -and $_.Parameters.Count -eq $nArgs })
    $names = @(); if ($def.Count -eq 1) { $names = @($def[0].Parameters | ForEach-Object { $_.Name }) }
    $iSave = [array]::IndexOf($names, "save"); $iOwner = [array]::IndexOf($names, "ownerID")
    if ($src -and $iSave -ge 0 -and $iOwner -ge 0) {
        $argOk = (Test-LiteralZero $ins $src[$iSave + 1]) -and (Test-LiteralZero $ins $src[$iOwner + 1])
    }
}
if ($argOk) { Write-Output "  ok    CreateLocalOnlyPin calls AddPin with save: false, ownerID: 0" }
else { Write-Output "  FAIL  CreateLocalOnlyPin does not visibly call AddPin with save: false and ownerID: 0 (or the game renamed those parameters)"; $failures++ }

$checks++
$saveReset = $false; $ownerReset = $false
if ($site) {
    $ins = $site[2]
    for ($k = $site[3] + 1; $k -lt $ins.Count; $k++) {
        if ($ins[$k].OpCode.Name -ne "stfld" -or -not (Test-LiteralZero $ins ($k - 1))) { continue }
        if ("$($ins[$k].Operand)" -like "*Minimap/PinData::m_save") { $saveReset = $true }
        if ("$($ins[$k].Operand)" -like "*Minimap/PinData::m_ownerID") { $ownerReset = $true }
    }
}
if ($saveReset -and $ownerReset) { Write-Output "  ok    CreateLocalOnlyPin re-asserts m_save = false and m_ownerID = 0 after AddPin" }
else { Write-Output ("  FAIL  CreateLocalOnlyPin must re-assert both flags after AddPin (m_save = false: {0}, m_ownerID = 0: {1})" -f $saveReset, $ownerReset); $failures++ }

Write-Output ""
Write-Output "== a pin the player promotes is never modified or deleted =="
# Only markers this mod created may be changed or removed. Structurally that means: no PinData field is
# written anywhere except CreateLocalOnlyPin (which only touches the pin AddPin just returned), Minimap's
# live pin list is never edited directly, Minimap.RemovePin is reached from exactly one place
# (RemoveOwnMarker), and nothing wipes or rewrites the map's pins wholesale. This cannot see the OwnsPin
# guards in ReleasePin/EnsurePins - those are logic, and a missing guard still passes here.
$pinTouches = @()
$removeSites = @()
$wipeMethods = @("ClearPins", "ResetSharedMapData", "SetMapData", "AddSharedMapData", "DestroyPinMarker", "OnPinTextEntered")
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $where = "{0}.{1}" -f $t.Name, $m.Name
        $isCreator = ($t.FullName -eq "Waypointer.WaypointManager" -and $m.Name -eq "CreateLocalOnlyPin")
        foreach ($i in $m.Body.Instructions) {
            $n = $i.OpCode.Name
            if ($i.Operand -is [Mono.Cecil.FieldReference] -and $i.Operand.DeclaringType.FullName -eq "Minimap/PinData" -and -not $isCreator) {
                # ldflda followed by ldfld is a read such as p.m_pos.x; any other use of the address can write.
                if ($n -eq "stfld" -or ($n -eq "ldflda" -and ($i.Next -eq $null -or $i.Next.OpCode.Name -ne "ldfld"))) {
                    $pinTouches += ("{0} writes PinData.{1} ({2})" -f $where, $i.Operand.Name, $n)
                }
                # Reads are limited to plain values: a handle such as m_uiElement or m_NamePinData changes the
                # pin on screen without any store to PinData. (m_ownerID since 1.5.2: a shared pin is hidden while
                # shared pins are, and a map click leaves it out then.)
                elseif (@("m_name", "m_pos", "m_type", "m_ownerID") -notcontains $i.Operand.Name) {
                    $pinTouches += ("{0} reads PinData.{1} (only m_name, m_pos, m_type and m_ownerID may be read)" -f $where, $i.Operand.Name)
                }
            }
            # Most of these are private, so a plugin can only reach them by name through reflection.
            if ($n -eq "ldstr" -and ($wipeMethods + "RemovePin") -contains "$($i.Operand)") {
                $pinTouches += ("{0} names Minimap.{1} in a string (reflection)" -f $where, $i.Operand)
            }
            if ($i.Operand -is [Mono.Cecil.MethodReference]) {
                $mr = $i.Operand
                $dt = $mr.DeclaringType.FullName
                if ($dt -eq "Minimap" -and $mr.Name -eq "RemovePin") { $removeSites += ("{0} {1}" -f $where, $mr.FullName) }
                if ($dt -eq "Minimap" -and $wipeMethods -contains $mr.Name) { $pinTouches += ("{0} calls Minimap.{1}" -f $where, $mr.Name) }
                if ($dt.Contains('List`1<Minimap/PinData>') -and $mr.Name -match '^(Add|AddRange|Insert|InsertRange|Remove|RemoveAt|RemoveAll|RemoveRange|Clear|set_Item|Reverse|Sort)$') {
                    $pinTouches += ("{0} edits a PinData list directly ({1})" -f $where, $mr.Name)
                }
            }
        }
    }
}
$checks++
if ($pinTouches.Count -eq 0) {
    Write-Output "  ok    no pin is written, unlisted or wiped outside CreateLocalOnlyPin"
} else {
    Write-Output "  FAIL  the plugin can change or remove a pin it did not create:"
    $pinTouches | ForEach-Object { Write-Output ("          " + $_) }
    $failures++
}
$checks++
if ($removeSites.Count -eq 1 -and $removeSites[0] -like "WaypointManager.RemoveOwnMarker *RemovePin(Minimap/PinData)") {
    Write-Output "  ok    exactly one RemovePin call site (RemoveOwnMarker, our own markers only)"
} else {
    Write-Output ("  FAIL  expected exactly 1 RemovePin(PinData) call site, in RemoveOwnMarker; found {0}:" -f $removeSites.Count)
    $removeSites | ForEach-Object { Write-Output ("          " + $_) }
    $failures++
}


Write-Output ""
Write-Output "== coordinate entry and display =="
# Wayfinder's defining rule is that a looked-up location cannot be navigated to. That means no way to
# enter coordinates AND no numeric coordinate readout: Alt-click works anywhere on the map, so a readout
# ("Waypoint added at 3350, -1190") would let a player steer clicks onto a looked-up spot by trial and
# error. Both are enforced at compile time and checked here against the compiled assembly.
# TomTom is checked the other way round - if these were missing there, this check would be vacuous.
$coordTypes = @("Waypointer.CoordinateParser", "Waypointer.ParsedCoord", "Waypointer.CoordinateFormat")
$coordMethods = @(
    @("Waypointer.Terminal_InitTerminal_Patch", "AddFromArgs"),     # console: waypoint <x> <y>
    @("Waypointer.WaypointManager", "AddRange"),                     # bulk add of parsed coordinates
    @("Waypointer.WaypointWindow", "DrawInputSection"),              # the coordinate text box
    @("Waypointer.WaypointWindow", "ApplyInput"),                    # its Add / Replace buttons
    @("Waypointer.Waypoint", "get_CoordText")                        # cached coordinate text for display
)
$coordConfigKeys = @("InputIsRawValheimXYZ", "ShowCoordinates")

$present = @()
foreach ($n in $coordTypes) { if ($plug.GetType($n)) { $present += $n } }
foreach ($pair in $coordMethods) {
    $t = $plug.GetType($pair[0])
    if ($t -and @($t.Methods | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0) { $present += ($pair[0] + "." + $pair[1]) }
}
$foundConfigKeys = @{}
$hasCoordFormatString = $false
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
        if ($i.OpCode.Name -ne "ldstr") { continue }
        $s = "$($i.Operand)"
        if ($coordConfigKeys -contains $s) { $foundConfigKeys[$s] = $true }
        # "{0:0}, {1:0}" is the shape of an x, z readout - it should exist only in CoordinateFormat.
        if ($s -like "*{0:0}, {1:0}*") { $hasCoordFormatString = $true }
    }
} }
foreach ($k in $coordConfigKeys) { if ($foundConfigKeys.ContainsKey($k)) { $present += ("config key " + $k) } }
if ($hasCoordFormatString) { $present += "a coordinate format string ""{0:0}, {1:0}""" }
$expectedCount = $coordTypes.Count + $coordMethods.Count + $coordConfigKeys.Count + 1

$checks++
if ($Edition -eq "Wayfinder") {
    if ($present.Count -eq 0) {
        Write-Output ("  ok    none of the {0} coordinate entry/display pieces exist in the assembly" -f $expectedCount)
    } else {
        Write-Output "  FAIL  Wayfinder must not contain coordinate entry or display, but found:"
        $present | ForEach-Object { Write-Output ("          " + $_) }
        $failures++
    }
} else {
    if ($present.Count -eq $expectedCount) {
        Write-Output ("  ok    all {0} coordinate entry/display pieces present" -f $expectedCount)
    } else {
        Write-Output ("  FAIL  TomTom should have all {0} coordinate entry/display pieces, found {1}" -f $expectedCount, $present.Count)
        $failures++
    }
}


# A coordinate readout has to read a world x or z and turn a number into text in the same method, or turn a
# whole vector into text. The distance readouts get a scalar from HorizontalDistance and never touch x/z;
# the save file (SaveIfDirty, through RouteFile.FormatLine, which nothing else may call) is the one sanctioned place. LocationSearch.Ask is exempt too: it boxes the search
# origin only because ZRoutedRpc.InvokeRoutedRPC takes params object[] - the vector goes to the server, never to
# text (check 12 pins down what Ask does). A tripwire, not a proof: a helper handed bare floats that formats them
# elsewhere is not caught. TomTom is checked the other way round (non-vacuous).
$numericText = @()
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $readsXZ = $false; $toText = $false; $vecText = $false
    foreach ($i in $m.Body.Instructions) {
        $n = $i.OpCode.Name; $op = "$($i.Operand)"
        if (($n -eq "ldfld" -or $n -eq "ldflda") -and $op -match "(UnityEngine\.Vector3::(x|z)|Waypointer\.RouteEntry::(X|Z))$") { $readsXZ = $true }
        if ($n -eq "box" -and $op -match "^System\.(Single|Double|Decimal|U?Int(16|32|64))$") { $toText = $true }
        if ($n -like "call*" -and $op -match "System\.(Single|Double|Decimal|Int32|Int64)::ToString|StringBuilder::Append\(System\.(Single|Double|Decimal|Int32|Int64)\)") { $toText = $true }
        if (($n -eq "box" -and $op -match "^UnityEngine\.Vector[234]$") -or ($n -like "call*" -and $op -match "UnityEngine\.Vector[234]::ToString")) { $vecText = $true }
    }
    $w = $t.Name + "." + $m.Name
    # RouteFile.FormatLine is the save file's own formatter (reading RouteEntry's X and Z); only SaveIfDirty may call it (below).
    if ($w -ne "WaypointManager.SaveIfDirty" -and $w -ne "RouteFile.FormatLine" -and $w -ne "LocationSearch.Ask" -and ($vecText -or ($readsXZ -and $toText))) { $numericText += $w }
    # The route file's own formatter turns bare floats into text (blind spot above), so only the save may use it.
    if ($w -ne "WaypointManager.SaveIfDirty" -and @($m.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.RouteFile" -and $_.Operand.Name -eq "FormatLine" }).Count -gt 0) { $numericText += ($w + " (calls RouteFile.FormatLine, the save file's formatter)") }
} }
$checks++
if ($Edition -eq "Wayfinder") {
    if ($numericText.Count -eq 0) {
        Write-Output "  ok    no method turns a world x/z or a vector into text (save file excepted)"
    } else {
        Write-Output "  FAIL  Wayfinder must not show coordinates, but these methods turn a world x/z or a vector into text:"
        $numericText | ForEach-Object { Write-Output ("          " + $_) }
        $failures++
    }
} else {
    if ($numericText -contains "CoordinateFormat.Format") {
        Write-Output ("  ok    the x/z-to-text scan finds TomTom's readout ({0})" -f ($numericText -join ", "))
    } else {
        Write-Output "  FAIL  the x/z-to-text scan no longer finds CoordinateFormat.Format in TomTom - the Wayfinder check would be vacuous"
        $failures++
    }
}

Write-Output ""
Write-Output "== patch ordering =="
# Chatter's Chat.HasFocus postfix assigns __result outright and loads after this plugin, so at equal
# priority it would run later and undo ours. Chat_HasFocus_Patch.Postfix must stay Priority.Last (0).
$checks++
$hf = $plug.GetType("Waypointer.Chat_HasFocus_Patch")
$pf = $null
if ($hf) { $pf = $hf.Methods | Where-Object { $_.Name -eq "Postfix" } | Select-Object -First 1 }
$prio = $null
if ($pf) { foreach ($ca in $pf.CustomAttributes) { if ($ca.AttributeType.Name -eq "HarmonyPriority") { $prio = [int]$ca.ConstructorArguments[0].Value } } }
if (-not $pf) { Write-Output "  FAIL  Waypointer.Chat_HasFocus_Patch.Postfix not found"; $failures++ }
elseif ($prio -eq 0) { Write-Output "  ok    Chat_HasFocus_Patch.Postfix runs last (HarmonyPriority 0 = Priority.Last)" }
else { Write-Output ("  FAIL  Chat_HasFocus_Patch.Postfix priority is '{0}', expected 0 (Priority.Last)" -f $prio); $failures++ }

Write-Output ""
Write-Output "== crash-safe route save =="
# A route file is replaced by writing <file>.new, flushing it to disk and then renaming it into place
# (SafeFile.WriteAllText). The flush is the one step no test can see: without it the swap still looks atomic,
# but after a power cut the renamed file can be empty or partly written, because the rename can reach the disk
# before the text does. So, in SafeFile.WriteAllText:
#   - the one FileStream it opens is flushed with FileStream.Flush(true) (flushToDisk), a literal true
#   - the flush comes after every write to that stream and always runs once the stream is open (no branch in
#     between)
#   - no File.Move runs between opening the stream and flushing it, and at least one follows the flush (the
#     swap). The File.Move that renames a leftover copy back into place comes before the stream is opened.
# And, so that this cannot pass while the save takes another path: WaypointManager.SaveIfDirty calls
# SafeFile.WriteAllText, and nothing opens a file for writing (File or FileInfo Write*/Append*/Create*/Open/
# OpenWrite/Replace/Copy*, or a new FileStream, StreamWriter or BinaryWriter) except the one FileStream in
# SafeFile.WriteAllText - not even the rest of that method, where a plain File.WriteAllText after the swap would
# pass every check above. A FileStream opened anywhere else fails even for reading, so that a person looks. This reads the IL in code
# order - a tripwire, not a proof - and whether the drive honours the flush is beyond any check.
# Since 1.6.0 the plugin also writes its own log file: LogFile.OpenWriter may open exactly one FileStream and one
# StreamWriter over a Stream (not over a path), and nothing else. File.Move and File.Delete (and FileInfo's) run only in
# SafeFile and LogRotation.Rotate; and nothing makes BepInEx open a file for it (a DiskLogListener, a ConfigFile of its
# own, Utility.TryOpenFileStream). Where those log files are is check 20.
function Get-LocalIndex($i) {
    # The local variable an ldloc/stloc reads or writes, or -1.
    $n = $i.OpCode.Name
    if ($n -match '^(ld|st)loc\.([0-3])$') { return [int]$Matches[2] }
    if ($n -eq "ldloc" -or $n -eq "ldloc.s" -or $n -eq "stloc" -or $n -eq "stloc.s") { return $i.Operand.Index }
    return -1
}
$checks++
$writers = @()
$saveUsesIt = $false
$logStreams = 0; $logWriters = 0
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
            $dt = $op.DeclaringType.FullName
            if ($t.FullName -eq "Waypointer.WaypointManager" -and $m.Name -eq "SaveIfDirty" -and $i.OpCode.Name -eq "call" -and $dt -eq "Waypointer.SafeFile" -and $op.Name -eq "WriteAllText") { $saveUsesIt = $true }
            $fileApi = ($dt -eq "System.IO.File" -or $dt -eq "System.IO.FileInfo")
            $opensFile = ($fileApi -and ($op.Name -match '^(Write|Append|Create|Open|Replace|Copy)') -and ($op.Name -notmatch '^Open(Read|Text)$'))
            $newWriter = (($dt -eq "System.IO.FileStream" -or $dt -eq "System.IO.StreamWriter" -or $dt -eq "System.IO.BinaryWriter") -and $op.Name -eq ".ctor")
            $theStream = ($t.FullName -eq "Waypointer.SafeFile" -and $m.Name -eq "WriteAllText" -and $newWriter -and $dt -eq "System.IO.FileStream")
            $logOpen = ($t.FullName -eq "Waypointer.LogFile" -and $m.Name -eq "OpenWriter" -and $i.OpCode.Name -eq "newobj")
            if ($logOpen -and $dt -eq "System.IO.FileStream") { $logStreams++ }
            elseif ($logOpen -and $dt -eq "System.IO.StreamWriter" -and $op.Parameters.Count -ge 1 -and $op.Parameters[0].ParameterType.FullName -eq "System.IO.Stream") { $logWriters++ }
            elseif (($opensFile -or $newWriter) -and -not $theStream) { $writers += ("{0}.{1} uses {2}::{3}" -f $t.Name, $m.Name, $op.DeclaringType.Name, $op.Name) }
            $bepOpener = ((($dt -eq "BepInEx.Logging.DiskLogListener" -or $dt -eq "BepInEx.Configuration.ConfigFile") -and $op.Name -eq ".ctor") -or ($dt -eq "BepInEx.Utility" -and $op.Name -eq "TryOpenFileStream"))
            if ($bepOpener) { $writers += ("{0}.{1} makes BepInEx open a file: {2}::{3}" -f $t.Name, $m.Name, $op.DeclaringType.Name, $op.Name) }
            $moveOrDelete = ($fileApi -and $op.Name -match '^(Move|MoveTo|Delete)$')
            $mayMove = ($t.FullName -eq "Waypointer.SafeFile" -or ($t.FullName -eq "Waypointer.LogRotation" -and $m.Name -eq "Rotate"))
            if ($moveOrDelete -and -not $mayMove) { $writers += ("{0}.{1} moves or deletes a file: {2}::{3}" -f $t.Name, $m.Name, $op.DeclaringType.Name, $op.Name) }
        }
    }
}
if ($logStreams -gt 1) { $writers += ("LogFile.OpenWriter opens {0} FileStreams, expected one" -f $logStreams) }
if ($logWriters -gt 1) { $writers += ("LogFile.OpenWriter makes {0} StreamWriters, expected one" -f $logWriters) }
if ($saveUsesIt -and $writers.Count -eq 0) {
    Write-Output "  ok    routes are saved only through SafeFile.WriteAllText (WaypointManager.SaveIfDirty calls it; nothing but its FileStream, and the log file's one FileStream and StreamWriter, opens a file for writing; files are moved or deleted only by SafeFile and LogRotation.Rotate)"
} else {
    if (-not $saveUsesIt) { Write-Output "  FAIL  WaypointManager.SaveIfDirty does not call SafeFile.WriteAllText" }
    foreach ($w in $writers) { Write-Output "  FAIL  a file is opened for writing other than through SafeFile.WriteAllText's FileStream: $w" }
    $failures++
}

$checks++
$sf = $plug.GetType("Waypointer.SafeFile")
$sw = $null
if ($sf) { $sw = $sf.Methods | Where-Object { $_.Name -eq "WriteAllText" -and $_.HasBody } | Select-Object -First 1 }
$why = $null
if (-not $sw) { $why = "Waypointer.SafeFile.WriteAllText not found" }
else {
    $ins = @($sw.Body.Instructions)
    $opened = @(); $flushes = @(); $writes = @(); $moves = @()
    for ($k = 0; $k -lt $ins.Count; $k++) {
        $op = $ins[$k].Operand
        if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
        $dt = $op.DeclaringType.FullName
        $stream = ($dt -eq "System.IO.FileStream" -or $dt -eq "System.IO.Stream")
        if ($ins[$k].OpCode.Name -eq "newobj" -and $dt -eq "System.IO.FileStream") { $opened += $k }
        elseif ($stream -and $op.Name -eq "Flush") { $flushes += $k }
        elseif ($stream -and $op.Name -like "Write*") { $writes += $k }
        elseif ($dt -eq "System.IO.File" -and $op.Name -eq "Move") { $moves += $k }
    }
    if ($opened.Count -ne 1) { $why = "SafeFile.WriteAllText opens {0} FileStreams, expected exactly one" -f $opened.Count }
    else {
        $at = $opened[0]
        $iFlush = -1
        foreach ($k in $flushes) {
            $f = $ins[$k].Operand
            if ($k -gt $at -and $f.DeclaringType.FullName -eq "System.IO.FileStream" -and $f.Parameters.Count -eq 1 -and $f.Parameters[0].ParameterType.FullName -eq "System.Boolean") { $iFlush = $k; break }
        }
        $stored = Get-LocalIndex ($ins[$at + 1])
        $src = $null
        if ($iFlush -ge 0) { $src = Get-ArgumentSources $ins $iFlush }
        $branches = @()
        if ($iFlush -ge 0) { for ($k = $at + 1; $k -lt $iFlush; $k++) { if ("$($ins[$k].OpCode.FlowControl)" -match '^(Branch|Cond_Branch|Return|Throw)$') { $branches += $k } } }
        if ($iFlush -lt 0) { $why = "the new file is never flushed to disk: no FileStream.Flush(bool) after the stream is opened" }
        elseif ($null -eq $src) { $why = "cannot tell what FileStream.Flush is called on and with (the evaluation stack does not add up)" }
        elseif ($ins[$src[1]].OpCode.Name -ne "ldc.i4.1") { $why = "FileStream.Flush is passed '{0}', expected a literal true (flushToDisk)" -f $ins[$src[1]].OpCode.Name }
        elseif ($ins[$at + 1].OpCode.Name -notlike "st*" -or $stored -lt 0 -or $ins[$src[0]].OpCode.Name -notlike "ld*" -or (Get-LocalIndex ($ins[$src[0]])) -ne $stored) { $why = "the stream that is flushed is not the one opened for the new file" }
        elseif (@($moves | Where-Object { $_ -gt $at -and $_ -lt $iFlush }).Count -gt 0) { $why = "a File.Move runs after the new file is opened and before it is flushed to disk" }
        elseif (@($writes | Where-Object { $_ -gt $iFlush }).Count -gt 0) { $why = "text is written to the stream after it is flushed to disk" }
        elseif (@($writes | Where-Object { $_ -gt $at -and $_ -lt $iFlush }).Count -eq 0) { $why = "nothing is written to the stream before it is flushed" }
        elseif ($branches.Count -gt 0) { $why = "the flush does not always run: '{0}' lies between opening the stream and flushing it" -f $ins[$branches[0]].OpCode.Name }
        elseif (@($moves | Where-Object { $_ -gt $iFlush }).Count -eq 0) { $why = "no File.Move follows the flush, so the flushed file is never swapped in" }
    }
}
if ($null -eq $why) { Write-Output "  ok    SafeFile.WriteAllText flushes the new file to disk (FileStream.Flush(true)) before any File.Move swaps it in" }
else { Write-Output "  FAIL  $why"; $failures++ }

# A route that could not be read when its world loaded (another program held it) is never saved over: SaveIfDirty asks
# RouteReadGate.Pending before SafeFile.WriteAllText and, when it is true, returns (the brfalse right after the call
# jumps over a block that holds a ret and no SafeFile call); Load and RetryRead record a failed read
# (RouteReadGate.Failed); and SaveIfDirty is the only caller of SafeFile.WriteAllText.
$checks++
$rrWhy = @()
$wmType = $plug.GetType("Waypointer.WaypointManager")
$sid = $null
if ($wmType) { $sid = $wmType.Methods | Where-Object { $_.Name -eq "SaveIfDirty" -and $_.HasBody } | Select-Object -First 1 }
if (-not $sid) { $rrWhy += "WaypointManager.SaveIfDirty not found" }
else {
    $si = @($sid.Body.Instructions)
    $wat = -1; $pat = -1
    for ($k = 0; $k -lt $si.Count; $k++) {
        $o = $si[$k].Operand
        if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
        if ($wat -lt 0 -and $o.DeclaringType.FullName -eq "Waypointer.SafeFile" -and $o.Name -eq "WriteAllText") { $wat = $k }
        if ($pat -lt 0 -and $o.DeclaringType.FullName -eq "Waypointer.RouteReadGate" -and $o.Name -eq "get_Pending") { $pat = $k }
    }
    if ($pat -lt 0 -or $wat -lt 0 -or $pat -gt $wat) { $rrWhy += "SaveIfDirty does not ask RouteReadGate.Pending before SafeFile.WriteAllText" }
    else {
        $br = $si[$pat + 1]
        $to = if ($br.OpCode.Name -like "brfalse*") { [array]::IndexOf($si, $br.Operand) } else { -1 }
        $rets = 0; $leaks = 0
        for ($k = $pat + 2; $k -lt $to; $k++) {
            if ($si[$k].OpCode.Name -eq "ret") { $rets++ }
            $o = $si[$k].Operand
            if ($o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.SafeFile") { $leaks++ }
        }
        if ($to -le $pat -or $to -gt $wat -or $rets -eq 0 -or $leaks -gt 0) { $rrWhy += "when RouteReadGate.Pending is true, SaveIfDirty does not return before SafeFile.WriteAllText" }
    }
}
foreach ($mn in @("Load", "RetryRead")) {
    $mm = $null
    if ($wmType) { $mm = $wmType.Methods | Where-Object { $_.Name -eq $mn -and $_.HasBody } | Select-Object -First 1 }
    $records = $mm -and @($mm.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.RouteReadGate" -and $_.Operand.Name -eq "Failed" }).Count -gt 0
    if (-not $records) { $rrWhy += ("WaypointManager.{0} does not record a failed read (RouteReadGate.Failed)" -f $mn) }
}
# The glue around the gate: RetryRead returns right after recording a failed read (a failed retry must not count as
# read); ReadRoute's catch returns false, never true (an unreadable file must not be taken for an empty route);
# LoadForCurrentWorldIfNeeded marks the route not read when PersistWaypoints is off (RouteReadGate.NotRead); and
# RetryRead sets _dirty only from a comparison (queued meanwhile > 0), so a merged route is saved.
$rri = $null; $rdr = $null; $lfc = $null
if ($wmType) {
    $rri = $wmType.Methods | Where-Object { $_.Name -eq "RetryRead" -and $_.HasBody } | Select-Object -First 1
    $rdr = $wmType.Methods | Where-Object { $_.Name -eq "ReadRoute" -and $_.HasBody } | Select-Object -First 1
    $lfc = $wmType.Methods | Where-Object { $_.Name -eq "LoadForCurrentWorldIfNeeded" -and $_.HasBody } | Select-Object -First 1
}
if (-not ($rri -and $rdr -and $lfc)) { $rrWhy += "WaypointManager.RetryRead, ReadRoute or LoadForCurrentWorldIfNeeded not found" }
else {
    $ri = @($rri.Body.Instructions)
    $failedCalls = 0; $retAfter = 0
    for ($k = 0; $k -lt $ri.Count - 1; $k++) {
        $o = $ri[$k].Operand
        if ($o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.RouteReadGate" -and $o.Name -eq "Failed") {
            $failedCalls++
            $nx = $ri[$k + 1]
            if ($nx.OpCode.Name -eq "ret" -or ("$($nx.OpCode.FlowControl)" -eq "Branch" -and $nx.Operand -is [Mono.Cecil.Cil.Instruction] -and $nx.Operand.OpCode.Name -eq "ret")) { $retAfter++ }
        }
    }
    if ($failedCalls -eq 0 -or $retAfter -ne $failedCalls) { $rrWhy += "RetryRead does not return right after recording a failed read (a failed retry would count as read)" }
    # _dirty = meanwhile > 0: "ldloc V; ldc.i4.0; cgt; stsfld _dirty", V stored only from _queue.Count.
    $dirtyOk = $true; $dirtySeen = 0
    for ($k = 3; $k -lt $ri.Count; $k++) {
        if (-not ($ri[$k].OpCode.Name -eq "stsfld" -and $ri[$k].Operand.Name -eq "_dirty")) { continue }
        $dirtySeen++
        $lv = $ri[$k - 3]
        if (-not ($ri[$k - 1].OpCode.Name -eq "cgt" -and $ri[$k - 2].OpCode.Name -eq "ldc.i4.0" -and $lv.OpCode.Name -like "ldloc*")) { $dirtyOk = $false; continue }
        $vi = Get-LocalIndex $lv; $stores = 0; $fromCount = 0
        for ($j = 2; $j -lt $ri.Count; $j++) {
            if ($ri[$j].OpCode.Name -like "stloc*" -and (Get-LocalIndex $ri[$j]) -eq $vi) {
                $stores++
                if ($ri[$j - 1].OpCode.Name -like "call*" -and $ri[$j - 1].Operand.Name -eq "get_Count" -and $ri[$j - 2].OpCode.Name -eq "ldsfld" -and $ri[$j - 2].Operand.Name -eq "_queue") { $fromCount++ }
            }
        }
        if ($stores -eq 0 -or $stores -ne $fromCount) { $dirtyOk = $false }
    }
    if ($dirtySeen -eq 0 -or -not $dirtyOk) { $rrWhy += "RetryRead does not set _dirty from '_queue.Count > 0' (a merged route would not be saved)" }
    # In Load and RetryRead, the branch on ReadRoute's result: its false edge records the failed read before any
    # ret, and its true edge does not.
    foreach ($bm in @(@("Load", $null), @("RetryRead", $rri))) {
        $meth = $bm[1]
        if (-not $meth) { $meth = $wmType.Methods | Where-Object { $_.Name -eq $bm[0] -and $_.HasBody } | Select-Object -First 1 }
        if (-not $meth) { $rrWhy += ("WaypointManager.{0} not found" -f $bm[0]); continue }
        $bi = @($meth.Body.Instructions)
        $rc = @(); for ($k = 0; $k -lt $bi.Count; $k++) { if ($bi[$k].OpCode.Name -like "call*" -and $bi[$k].Operand -is [Mono.Cecil.MethodReference] -and $bi[$k].Operand.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $bi[$k].Operand.Name -eq "ReadRoute") { $rc += $k } }
        if ($rc.Count -ne 1 -or "$($bi[$rc[0] + 1].OpCode.FlowControl)" -ne "Cond_Branch") { $rrWhy += ("WaypointManager.{0} does not branch at once on one call to ReadRoute" -f $bm[0]); continue }
        $cb = $bi[$rc[0] + 1]; $target = [array]::IndexOf($bi, $cb.Operand); $fall = $rc[0] + 2
        if ($cb.OpCode.Name -like "brfalse*") { $falseAt = $target; $trueAt = $fall } else { $falseAt = $fall; $trueAt = $target }
        $edge = @{}
        foreach ($pair in @(@("false", $falseAt), @("true", $trueAt))) {
            $calls = $false
            for ($k = $pair[1]; $k -ge 0 -and $k -lt $bi.Count; $k++) {
                $x = $bi[$k]
                if ($x.OpCode.Name -like "call*" -and $x.Operand -is [Mono.Cecil.MethodReference] -and $x.Operand.DeclaringType.FullName -eq "Waypointer.RouteReadGate" -and $x.Operand.Name -eq "Failed") { $calls = $true; break }
                if ($x.OpCode.Name -eq "ret" -or "$($x.OpCode.FlowControl)" -eq "Branch") { break }
            }
            $edge[$pair[0]] = $calls
        }
        if (-not $edge["false"] -or $edge["true"]) { $rrWhy += ("WaypointManager.{0} does not record a failed read exactly when ReadRoute returns false" -f $bm[0]) }
    }
    $rdi = @($rdr.Body.Instructions); $catches = 0
    foreach ($h in $rdr.Body.ExceptionHandlers) {
        if ("$($h.HandlerType)" -ne "Catch") { continue }
        $catches++
        $s0 = [array]::IndexOf($rdi, $h.HandlerStart); $e0 = if ($h.HandlerEnd) { [array]::IndexOf($rdi, $h.HandlerEnd) } else { $rdi.Count }
        $one = $false; $zero = $false
        for ($k = $s0; $k -lt $e0; $k++) { if ($rdi[$k].OpCode.Name -eq "ldc.i4.1") { $one = $true }; if ($rdi[$k].OpCode.Name -eq "ldc.i4.0") { $zero = $true } }
        if ($one -or -not $zero) { $rrWhy += "ReadRoute's catch does not return false (an unreadable file would be taken for an empty route)" }
    }
    if ($catches -eq 0) { $rrWhy += "ReadRoute catches nothing (a read that throws would not be recorded as failed)" }
    $notRead = @($lfc.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.RouteReadGate" -and $_.Operand.Name -eq "NotRead" }).Count
    if ($notRead -eq 0) { $rrWhy += "LoadForCurrentWorldIfNeeded does not mark the route not read when PersistWaypoints is off (RouteReadGate.NotRead)" }
}
$writeSites = @()
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
        if ($i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq "Waypointer.SafeFile" -and $i.Operand.Name -eq "WriteAllText") { $writeSites += ("{0}.{1}" -f $t.Name, $m.Name) }
    }
} }
if ($writeSites.Count -ne 1 -or $writeSites[0] -ne "WaypointManager.SaveIfDirty") { $rrWhy += ("SafeFile.WriteAllText must be called only from WaypointManager.SaveIfDirty; called from: {0}" -f ($writeSites -join ", ")) }
if ($rrWhy.Count -eq 0) { Write-Output "  ok    a route that could not be read is not saved over (SaveIfDirty returns while RouteReadGate.Pending; Load and RetryRead record the failure, RetryRead returns then, only on ReadRoute's false branch; ReadRoute's catch returns false; NotRead is called; _dirty is set from _queue.Count > 0)" }
else { foreach ($w in $rrWhy) { Write-Output "  FAIL  $w" }; $failures++ }

Write-Output ""
Write-Output "== a key Valheim cannot read cannot stop the waypoint tick =="
# Valheim 1.0.16's ZInput throws ArgumentOutOfRangeException on every read of 30 KeyCodes that BepInEx still
# offers as settings (Plus, F13-F15, WheelUp, ...): the KeyCode is missing from ZInput's KeyCode-to-Key table,
# the lookup yields Key.None and Keyboard.current[Key.None] throws. In 1.1.1 such a key skipped
# WaypointManager.Tick on every frame in which no chat, console or text field had the keyboard. So:
#   1. every ZInput.GetKey/GetKeyDown/GetKeyUp whose key is not a literal is in Hotkeys, which makes both kinds
#      of read (GetKey and GetKeyDown), and a literal key anywhere else must be one the game can read. That set
#      is worked out from the game itself - the KeyCode enum against the KeyCode-to-Key entries ZInput..cctor
#      adds, through the routing of ZInput.TryGetKeyStateLowLevel (IsKeyCodeValid, gamepad, mouse, keyboard) -
#      and a change in it is reported as a note, not a failure: both package READMEs and Hotkeys.cs list the 30
#   2. each of those reads in Hotkeys passes a literal false for logWarning (true would log a Unity warning on
#      every poll of a key the game cannot map, e.g. JoystickButton15) and lies inside a try whose
#      catch (System.Exception) does not rethrow
#   3. in Plugin.Update, WaypointManager.Tick is called once, inside at least one such try/catch, and no try
#      block around it reads a key (ZInput.GetKey/GetKeyDown/GetKeyUp, or anything in Hotkeys); every key read in
#      Update lies inside such a try/catch of its own, so it cannot escape Update either; and Update reads its
#      keys through Hotkeys.Pressed
# A catch whose handler contains throw or rethrow does not count as catching. A tripwire on the IL, not a proof:
# a key read hidden in a helper that Update calls inside the tick's block is not seen.
function Get-LiteralInt($i) {
    $n = $i.OpCode.Name
    if ($n -match '^ldc\.i4\.([0-8])$') { return [int]$Matches[1] }
    if ($n -eq "ldc.i4.m1") { return -1 }
    if ($n -eq "ldc.i4" -or $n -eq "ldc.i4.s") { return [int]"$($i.Operand)" }
    return $null
}
function Get-CatchTries($m, $i) {
    # The try/catch (System.Exception) blocks whose try range holds instruction $i and whose handler neither
    # throws nor rethrows, so that an exception thrown there really stops there.
    $found = @()
    $all = @($m.Body.Instructions)
    foreach ($h in $m.Body.ExceptionHandlers) {
        if ($h.HandlerType -ne [Mono.Cecil.Cil.ExceptionHandlerType]::Catch -or $h.CatchType.FullName -ne "System.Exception") { continue }
        $end = [int]::MaxValue
        if ($h.TryEnd) { $end = $h.TryEnd.Offset }
        if ($i.Offset -lt $h.TryStart.Offset -or $i.Offset -ge $end) { continue }
        $hEnd = [int]::MaxValue
        if ($h.HandlerEnd) { $hEnd = $h.HandlerEnd.Offset }
        $throws = @($all | Where-Object { $_.Offset -ge $h.HandlerStart.Offset -and $_.Offset -lt $hEnd -and ($_.OpCode.Name -eq "throw" -or $_.OpCode.Name -eq "rethrow") })
        if ($throws.Count -eq 0) { $found += ,$h }
    }
    return ,$found
}

# The KeyCodes the game cannot read, worked out from the shipped assemblies.
$unreadableKeys = @{}      # value -> name
$keyDerivation = $null     # why the set could not be worked out, when it could not
$kcType = Find-GameType "UnityEngine.KeyCode"
$zType = Find-GameType "ZInput"
if (-not $kcType -or -not $zType) { $keyDerivation = "UnityEngine.KeyCode or ZInput not found in the game" }
else {
    $kcName = @{}; $kcValue = @{}
    foreach ($f in $kcType.Fields) {
        if (-not $f.HasConstant) { continue }
        $v = [int]$f.Constant
        $kcValue[$f.Name] = $v
        if (-not $kcName.ContainsKey($v)) { $kcName[$v] = $f.Name }
    }
    $mapped = @{}
    $cctor = $zType.Methods | Where-Object { $_.Name -eq ".cctor" -and $_.HasBody } | Select-Object -First 1
    if ($cctor) {
        $ci = @($cctor.Body.Instructions)
        for ($k = 2; $k -lt $ci.Count; $k++) {
            $op = $ci[$k].Operand
            if (-not ($op -is [Mono.Cecil.MethodReference]) -or $op.Name -ne "Add") { continue }
            if (-not "$($op.DeclaringType)".Contains('Dictionary`2<UnityEngine.KeyCode,UnityEngine.InputSystem.Key>')) { continue }
            $v = Get-LiteralInt $ci[$k - 2]
            if ($null -ne $v) { $mapped[$v] = $true }
        }
    }
    $needed = @("JoystickButton0", "JoystickButton19", "Mouse0", "Mouse4", "Mouse5", "Mouse6")
    if (@($needed | Where-Object { -not $kcValue.ContainsKey($_) }).Count -gt 0) { $keyDerivation = "UnityEngine.KeyCode lacks one of " + ($needed -join ", ") }
    elseif ($mapped.Count -lt 50) { $keyDerivation = "found only {0} KeyCode-to-Key entries in ZInput..cctor (the IL pattern no longer matches)" -f $mapped.Count }
    else {
        foreach ($v in @($kcName.Keys)) {
            if ($v -eq 0 -or $v -gt $kcValue["JoystickButton19"] -or $v -eq $kcValue["Mouse5"] -or $v -eq $kcValue["Mouse6"]) { continue }   # IsKeyCodeValid: never fires
            if ($v -ge $kcValue["JoystickButton0"]) { continue }                                                                                # gamepad
            if ($v -ge $kcValue["Mouse0"] -and $v -le $kcValue["Mouse4"]) { continue }                                                          # mouse
            if (-not $mapped.ContainsKey($v)) { $unreadableKeys[$v] = $kcName[$v] }
        }
    }
}
$documentedUnreadable = @("Clear", "Exclaim", "DoubleQuote", "Hash", "Dollar", "Percent", "Ampersand", "LeftParen",
    "RightParen", "Asterisk", "Plus", "Colon", "Less", "Greater", "Question", "At", "Caret", "Underscore",
    "LeftCurlyBracket", "Pipe", "RightCurlyBracket", "Tilde", "F13", "F14", "F15", "Help", "SysReq", "Break",
    "WheelUp", "WheelDown")
if ($null -eq $keyDerivation) {
    # A failure, not a note, so the lists cannot drift after a game update: $documentedUnreadable, the Keys paragraph of
    # both package READMEs and Hotkeys.cs's summary are updated together. (Not counted when the set cannot be worked out:
    # the next check fails for that.)
    $checks++
    $unreadableNames = @($unreadableKeys.Values | Sort-Object)
    if (Compare-Object @($documentedUnreadable | Sort-Object) $unreadableNames) {
        Write-Output ("  FAIL  the KeyCodes the game cannot read are no longer the 30 listed for Valheim 1.0.16 (now {0}: {1}) - update `$documentedUnreadable here, the Keys paragraph of both package READMEs and Hotkeys.cs" -f $unreadableNames.Count, ($unreadableNames -join ", "))
        $failures++
    } else {
        Write-Output "  ok    the game cannot read the same 30 KeyCodes as Valheim 1.0.16 (the list this script holds; Hotkeys.cs and both package READMEs follow it)"
    }
}

$keyReadNames = @("GetKey", "GetKeyDown", "GetKeyUp")
$unguardedReads = @(); $unreadableLiterals = @(); $unprotectedReads = @(); $guardedKinds = @{}
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    for ($k = 0; $k -lt $ins.Count; $k++) {
        $op = $ins[$k].Operand
        if (-not ($op -is [Mono.Cecil.MethodReference]) -or $op.DeclaringType.FullName -ne "ZInput" -or $keyReadNames -notcontains $op.Name) { continue }
        if ($op.Parameters.Count -lt 1 -or $op.Parameters[0].ParameterType.FullName -ne "UnityEngine.KeyCode") { continue }
        $where = "{0}.{1} ZInput.{2}" -f $t.Name, $m.Name, $op.Name
        $src = Get-ArgumentSources $ins $k $m.Body.ExceptionHandlers
        if ($t.FullName -eq "Waypointer.Hotkeys") {
            $guardedKinds[$op.Name] = $true
            if ((Get-CatchTries $m $ins[$k]).Count -eq 0) { $unprotectedReads += ($where + " is not inside a try whose catch (System.Exception) does not rethrow") }
            if ($null -eq $src -or $src.Count -lt 2 -or $ins[$src[1]].OpCode.Name -ne "ldc.i4.0") { $unprotectedReads += ($where + " does not pass a literal false for logWarning") }
            continue
        }
        $lit = $null
        if ($null -ne $src) { $lit = Get-LiteralInt $ins[$src[0]] }
        if ($null -eq $lit) { $unguardedReads += $where }
        elseif ($unreadableKeys.ContainsKey($lit)) { $unreadableLiterals += ("{0}({1})" -f $where, $unreadableKeys[$lit]) }
    }
} }
$checks++
$bothKinds = $guardedKinds.ContainsKey("GetKey") -and $guardedKinds.ContainsKey("GetKeyDown")
if ($null -eq $keyDerivation -and $bothKinds -and $unguardedReads.Count -eq 0 -and $unreadableLiterals.Count -eq 0) {
    Write-Output "  ok    configurable keys are read only through Hotkeys (it reads both GetKey and GetKeyDown); elsewhere only literal keys the game can read"
} else {
    if ($null -ne $keyDerivation) { Write-Output "  FAIL  cannot work out which KeyCodes the game cannot read: $keyDerivation" }
    if (-not $bothKinds) { Write-Output "  FAIL  Hotkeys does not read both ZInput.GetKey and ZInput.GetKeyDown" }
    foreach ($w in $unguardedReads) { Write-Output "  FAIL  a key that is not a literal is read outside Hotkeys: $w" }
    foreach ($w in $unreadableLiterals) { Write-Output "  FAIL  a literal key the game cannot read (it throws on every read): $w" }
    $failures++
}
$checks++
if ($guardedKinds.Count -gt 0 -and $unprotectedReads.Count -eq 0) {
    Write-Output "  ok    every ZInput key read in Hotkeys passes logWarning: false and is caught (catch (System.Exception), no rethrow)"
} else {
    if ($guardedKinds.Count -eq 0) { Write-Output "  FAIL  Hotkeys makes no ZInput key read" }
    foreach ($w in $unprotectedReads) { Write-Output "  FAIL  a ZInput key read in Hotkeys: $w" }
    $failures++
}
$checks++
$whys = @()
$upd = $null
if ($pluginType) { $upd = $pluginType.Methods | Where-Object { $_.Name -eq "Update" -and $_.HasBody } | Select-Object -First 1 }
if (-not $upd) { $whys += "Waypointer.Plugin.Update not found" }
else {
    $ins = @($upd.Body.Instructions)
    $isCall = { param($i) $i.Operand -is [Mono.Cecil.MethodReference] -and $i.OpCode.Name -like "call*" }
    $tick = @($ins | Where-Object { (& $isCall $_) -and $_.Operand.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $_.Operand.Name -eq "Tick" })
    # Key reads: ZInput.GetKey/GetKeyDown/GetKeyUp with a KeyCode, and anything in Hotkeys.
    $keyCalls = @($ins | Where-Object { (& $isCall $_) -and (
        $_.Operand.DeclaringType.FullName -eq "Waypointer.Hotkeys" -or
        ($_.Operand.DeclaringType.FullName -eq "ZInput" -and $keyReadNames -contains $_.Operand.Name -and $_.Operand.Parameters.Count -ge 1 -and
         $_.Operand.Parameters[0].ParameterType.FullName -eq "UnityEngine.KeyCode")) })
    $pressed = @($keyCalls | Where-Object { $_.Operand.DeclaringType.FullName -eq "Waypointer.Hotkeys" -and $_.Operand.Name -eq "Pressed" })
    if ($tick.Count -ne 1) { $whys += ("Plugin.Update calls WaypointManager.Tick {0} times, expected once" -f $tick.Count) }
    else {
        $tickTries = Get-CatchTries $upd $tick[0]
        if ($tickTries.Count -eq 0) { $whys += "WaypointManager.Tick is not inside a try whose catch (System.Exception) does not rethrow, in Plugin.Update" }
        foreach ($h in $tickTries) {
            $end = [int]::MaxValue; if ($h.TryEnd) { $end = $h.TryEnd.Offset }
            $shared = @($keyCalls | Where-Object { $_.Offset -ge $h.TryStart.Offset -and $_.Offset -lt $end })
            if ($shared.Count -gt 0) {
                $whys += ("a try block around WaypointManager.Tick also reads a key ({0}.{1}), so a failing key read would skip the tick" -f $shared[0].Operand.DeclaringType.Name, $shared[0].Operand.Name)
                break
            }
        }
    }
    $loose = @($keyCalls | Where-Object { (Get-CatchTries $upd $_).Count -eq 0 } | ForEach-Object { "{0}.{1}" -f $_.Operand.DeclaringType.Name, $_.Operand.Name } | Select-Object -Unique)
    foreach ($c in $loose) { $whys += ("{0} in Plugin.Update is not inside a try whose catch (System.Exception) does not rethrow, so a throw would escape Update and skip the tick" -f $c) }
    if ($pressed.Count -eq 0) { $whys += "Plugin.Update reads no key through Hotkeys.Pressed" }
}
if ($whys.Count -eq 0) { Write-Output "  ok    Plugin.Update runs WaypointManager.Tick in a try block of its own that reads no key" }
else { foreach ($w in $whys) { Write-Output "  FAIL  $w" }; $failures++ }

Write-Output ""
Write-Output "== a location search cannot make a shared pin =="
# As a client, the search asks the server with the request a Vegvisir makes (RPC_DiscoverClosestLocation with
# discoverAll), and vanilla turns every answer into a save:true pin - the kind a Cartography Table shares - through
# Game.RPC_DiscoverLocationResponse -> Minimap.DiscoverLocation -> AddPin(save: true). So:
#   1. the plugin never references Game.DiscoverClosestLocation or Minimap.DiscoverLocation (both make such pins)
#   2. Waypointer.Game_RPC_DiscoverLocationResponse_Patch has a Prefix returning bool that hands the answer to
#      LocationSearch.OnServerAnswer, and whose one and only return is SearchRules.VanillaMayHandle(pinName) - the
#      decision rests on the pin name alone (unit-tested), and VanillaMayHandle tests (StartsWith) the very
#      control-character token SearchRules.RequestPinName builds request names from; and the requests carry exactly
#      that name - every write to LocationSearch._token comes from RequestPinName, and Ask passes _token as the
#      pin name (the server echoes it back in every answer)
#   3. the request name "RPC_DiscoverClosestLocation" appears in exactly one method, LocationSearch.Ask, where the
#      result of LocationSearch.AnswersIntercepted is branched on directly, before its ZRoutedRpc.InvokeRoutedRPC
#      call; and AnswersIntercepted asks Harmony.GetPatchInfo about Game_RPC_DiscoverLocationResponse_Patch and
#      returns, once, a flag set only to false or from SearchRules.IsOurPrefix (unit-tested) - no request is sent
#      unless the prefix is confirmed patched
#   4. every ZRoutedRpc.InvokeRoutedRPC call in the plugin sends a literal name from a fixed list, from a fixed
#      method: Ask's request, and the plugin's own two calls with a server that runs it (1.3.0) - DoomMachine.
#      Waypointer.ToServer from FindLink, DoomMachine.Waypointer.ToClient from FindServer. So no other code can
#      send a request or an answer the game would turn into a pin.
# And the editions' rule for what a search may place (check 13):
#   Wayfinder: LocationSearch reads MinimapAccess.IsExplored (only places whose centre is explored).
#   TomTom:    LocationSearch does not (it places everything in range) - so the Wayfinder check is not vacuous.
# A tripwire on the IL, not a proof: it cannot see what the prefix does with the answer beyond handing it over.
$checks++
$searchProblems = @()
$askMethods = @()
$invokeSites = @()
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($op -is [Mono.Cecil.MethodReference]) {
                $dt = $op.DeclaringType.FullName
                if (($dt -eq "Game" -and $op.Name -eq "DiscoverClosestLocation") -or ($dt -eq "Minimap" -and $op.Name -eq "DiscoverLocation")) {
                    $searchProblems += ("{0}.{1} references {2}.{3}, which makes a saved pin" -f $t.Name, $m.Name, $dt, $op.Name)
                }
                if ($dt -eq "ZRoutedRpc" -and $op.Name -eq "InvokeRoutedRPC") {
                    # The name is the call's second-to-last argument in every overload; it must be a literal.
                    $ins = @($m.Body.Instructions)
                    $at = [array]::IndexOf($ins, $i)
                    $src = Get-ArgumentSources $ins $at $m.Body.ExceptionHandlers
                    $name = "(not a literal)"
                    if ($src -and $src.Count -ge 2) {
                        $n = $ins[$src[$src.Count - 2]]
                        if ($n.OpCode.Name -eq "ldstr") { $name = "$($n.Operand)" }
                    }
                    $invokeSites += ,@(("{0}.{1}" -f $t.FullName, $m.Name), $name)
                }
            }
            if ($i.OpCode.Name -eq "ldstr" -and "$op" -eq "RPC_DiscoverClosestLocation") { $askMethods += $m }
        }
    }
}
$patchType = $plug.GetType("Waypointer.Game_RPC_DiscoverLocationResponse_Patch")
$prefix = $null
if ($patchType) { $prefix = $patchType.Methods | Where-Object { $_.Name -eq "Prefix" } | Select-Object -First 1 }
if (-not $prefix) { $searchProblems += "Waypointer.Game_RPC_DiscoverLocationResponse_Patch.Prefix not found" }
else {
    # The prefix's only return must be SearchRules.VanillaMayHandle(pinName): the decision then rests on the pin
    # name alone (unit-tested on both runtimes), not on what a search is doing or on an error path.
    $pins = @($prefix.Body.Instructions)
    if ($prefix.ReturnType.FullName -ne "System.Boolean") { $searchProblems += "the answer prefix does not return bool, so it cannot skip vanilla" }
    $rets = @()
    for ($k = 0; $k -lt $pins.Count; $k++) { if ($pins[$k].OpCode.Name -eq "ret") { $rets += $k } }
    if ($rets.Count -ne 1) { $searchProblems += ("the answer prefix has {0} returns; it must have exactly one, returning SearchRules.VanillaMayHandle(pinName)" -f $rets.Count) }
    else {
        $r = $rets[0]
        $prev = $null
        if ($r -gt 0) { $prev = $pins[$r - 1] }
        if (-not ($prev -and $prev.OpCode.Name -like "call*" -and $prev.Operand -is [Mono.Cecil.MethodReference] -and $prev.Operand.DeclaringType.FullName -eq "Waypointer.SearchRules" -and $prev.Operand.Name -eq "VanillaMayHandle")) {
            $searchProblems += "the answer prefix does not return SearchRules.VanillaMayHandle(...) directly"
        } else {
            $src = Get-ArgumentSources $pins ($r - 1) $prefix.Body.ExceptionHandlers
            $argName = ""
            if ($src) {
                $a = $pins[$src[0]]
                if ($a.OpCode.Name -match '^ldarg\.([0-3])$') { $argName = $prefix.Parameters[[int]$Matches[1]].Name }
                elseif ($a.OpCode.Name -like "ldarg*" -and $a.Operand -is [Mono.Cecil.ParameterDefinition]) { $argName = $a.Operand.Name }
            }
            if ($argName -ne "pinName") { $searchProblems += "the answer prefix does not pass its own pinName to SearchRules.VanillaMayHandle" }
        }
    }
    $hands = @($pins | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.LocationSearch" -and $_.Operand.Name -eq "OnServerAnswer" })
    if ($hands.Count -eq 0) { $searchProblems += "the answer prefix does not hand answers to LocationSearch.OnServerAnswer" }
}
# VanillaMayHandle must test the same token the requests carry (both compile the const to the same literal).
$sr = $plug.GetType("Waypointer.SearchRules")
$vmh = $null
if ($sr) { $vmh = $sr.Methods | Where-Object { $_.Name -eq "VanillaMayHandle" } | Select-Object -First 1 }
$lsStart = $null
$lsType = $plug.GetType("Waypointer.LocationSearch")
if ($lsType) { $lsStart = $lsType.Methods | Where-Object { $_.Name -eq "Start" } | Select-Object -First 1 }
$rpn = $null
if ($sr) { $rpn = $sr.Methods | Where-Object { $_.Name -eq "RequestPinName" } | Select-Object -First 1 }
if (-not $vmh -or -not $rpn) { $searchProblems += "SearchRules.VanillaMayHandle or SearchRules.RequestPinName not found" }
else {
    $ctl = [string][char]1
    $tokRule = @($vmh.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldstr" -and "$($_.Operand)".StartsWith($ctl) } | ForEach-Object { "$($_.Operand)" })
    $startsWith = @($vmh.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "System.String" -and $_.Operand.Name -eq "StartsWith" })
    $tokReq = @($rpn.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldstr" -and "$($_.Operand)".StartsWith($ctl) } | ForEach-Object { "$($_.Operand)" })
    if ($tokRule.Count -ne 1 -or $startsWith.Count -eq 0 -or $tokReq.Count -ne 1 -or $tokRule[0] -ne $tokReq[0]) {
        $searchProblems += "SearchRules.VanillaMayHandle does not test (StartsWith) the same control-character token SearchRules.RequestPinName builds request names from"
    }
}
# The request side: the server echoes the pin name the request carries, so the requests must carry exactly the
# name from RequestPinName - every write to LocationSearch._token comes from it, and Ask passes _token as the
# pin name (element 2 of InvokeRoutedRPC's argument array).
if ($lsType) {
    foreach ($m in $lsType.Methods) {
        if (-not $m.HasBody) { continue }
        $mi = @($m.Body.Instructions)
        for ($k = 0; $k -lt $mi.Count; $k++) {
            if ($mi[$k].OpCode.Name -eq "stsfld" -and "$($mi[$k].Operand)" -match "Waypointer\.LocationSearch::_token$") {
                $w = $null
                if ($k -gt 0) { $w = $mi[$k - 1] }
                if (-not ($w -and $w.Operand -is [Mono.Cecil.MethodReference] -and $w.Operand.DeclaringType.FullName -eq "Waypointer.SearchRules" -and $w.Operand.Name -eq "RequestPinName")) {
                    $searchProblems += ("LocationSearch.{0} sets the request pin name (_token) from something other than SearchRules.RequestPinName" -f $m.Name)
                }
            }
        }
    }
}
if ($askMethods.Count -eq 1) {
    $am = @($askMethods[0].Body.Instructions)
    $carries = $false
    for ($k = 1; $k -lt $am.Count - 1; $k++) {
        if ($am[$k].OpCode.Name -eq "ldsfld" -and "$($am[$k].Operand)" -match "Waypointer\.LocationSearch::_token$" -and $am[$k - 1].OpCode.Name -eq "ldc.i4.2" -and $am[$k + 1].OpCode.Name -eq "stelem.ref") { $carries = $true }
    }
    if (-not $carries) { $searchProblems += "LocationSearch.Ask does not send LocationSearch._token as the request's pin name (argument 3 of RPC_DiscoverClosestLocation)" }
}
# AnswersIntercepted must really ask Harmony whether this prefix is patched in.
$ai = $null
if ($lsType) { $ai = $lsType.Methods | Where-Object { $_.Name -eq "AnswersIntercepted" } | Select-Object -First 1 }
if (-not $ai) { $searchProblems += "LocationSearch.AnswersIntercepted not found" }
else {
    $gp = @($ai.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "HarmonyLib.Harmony" -and $_.Operand.Name -eq "GetPatchInfo" })
    $tk = @($ai.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldtoken" -and "$($_.Operand)" -eq "Waypointer.Game_RPC_DiscoverLocationResponse_Patch" })
    if ($gp.Count -eq 0 -or $tk.Count -eq 0) { $searchProblems += "LocationSearch.AnswersIntercepted does not ask Harmony.GetPatchInfo for Game_RPC_DiscoverLocationResponse_Patch" }
    # Its one return is a local flag, and that flag is only ever set to false or from SearchRules.IsOurPrefix
    # (unit-tested) - so it cannot say "patched" without finding this plugin's prefix.
    $aiIns = @($ai.Body.Instructions)
    $aiRets = @()
    for ($k = 0; $k -lt $aiIns.Count; $k++) { if ($aiIns[$k].OpCode.Name -eq "ret") { $aiRets += $k } }
    if ($aiRets.Count -ne 1) { $searchProblems += ("LocationSearch.AnswersIntercepted has {0} returns; it must have one, returning a flag set only from SearchRules.IsOurPrefix" -f $aiRets.Count) }
    else {
        $before = $null
        if ($aiRets[0] -gt 0) { $before = $aiIns[$aiRets[0] - 1] }
        $flag = -1
        if ($before -and $before.OpCode.Name -like "ldloc*") { $flag = Get-LocalIndex $before }
        if ($flag -lt 0) { $searchProblems += "LocationSearch.AnswersIntercepted does not return a local flag" }
        else {
            for ($k = 1; $k -lt $aiIns.Count; $k++) {
                if ($aiIns[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $aiIns[$k]) -eq $flag) {
                    $src = $aiIns[$k - 1]
                    $fromRule = $src.OpCode.Name -like "call*" -and $src.Operand -is [Mono.Cecil.MethodReference] -and $src.Operand.DeclaringType.FullName -eq "Waypointer.SearchRules" -and $src.Operand.Name -eq "IsOurPrefix"
                    if (-not ($src.OpCode.Name -eq "ldc.i4.0" -or $fromRule)) { $searchProblems += ("LocationSearch.AnswersIntercepted sets its result from '{0}', not from SearchRules.IsOurPrefix or false" -f $src.OpCode.Name) }
                }
            }
        }
    }
}
if ($askMethods.Count -ne 1) { $searchProblems += ("the request name appears in {0} methods, expected exactly one (LocationSearch.Ask)" -f $askMethods.Count) }
else {
    $ask = $askMethods[0]
    if ($ask.DeclaringType.FullName -ne "Waypointer.LocationSearch" -or $ask.Name -ne "Ask") { $searchProblems += ("the request is sent from {0}.{1}, expected LocationSearch.Ask" -f $ask.DeclaringType.Name, $ask.Name) }
    $ins = @($ask.Body.Instructions)
    $gate = -1; $invoke = -1
    for ($k = 0; $k -lt $ins.Count; $k++) {
        $o = $ins[$k].Operand
        if ($gate -lt 0 -and $o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.LocationSearch" -and $o.Name -eq "AnswersIntercepted") { $gate = $k; continue }
        if ($o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "ZRoutedRpc" -and $o.Name -eq "InvokeRoutedRPC") { $invoke = $k; break }
    }
    # The branch must consume the check's result directly (the instruction right after the call).
    $branchOnGate = $gate -ge 0 -and ($gate + 1) -lt $ins.Count -and "$($ins[$gate + 1].OpCode.FlowControl)" -eq "Cond_Branch"
    if ($gate -lt 0 -or $invoke -lt 0 -or -not $branchOnGate -or $gate -gt $invoke) {
        $searchProblems += "LocationSearch.Ask does not branch on AnswersIntercepted() before ZRoutedRpc.InvokeRoutedRPC"
    }
}
$allowedCalls = @{
    "Waypointer.LocationSearch.Ask" = "RPC_DiscoverClosestLocation"
    "Waypointer.FindLink.Tick" = "DoomMachine.Waypointer.ToServer"
    "Waypointer.FindLink.SendFind" = "DoomMachine.Waypointer.ToServer"
    "Waypointer.FindServer.Send" = "DoomMachine.Waypointer.ToClient"
}
foreach ($site in $invokeSites) {
    if (-not $allowedCalls.ContainsKey($site[0]) -or $allowedCalls[$site[0]] -ne $site[1]) {
        $searchProblems += ("ZRoutedRpc.InvokeRoutedRPC sends '{1}' from {0}, which is not on the list of routed calls the plugin may make" -f $site[0], $site[1])
    }
}
if ($searchProblems.Count -eq 0) {
    Write-Output ("  ok    the server's answers to a search cannot become saved pins (prefix skips vanilla; no request without it; no DiscoverLocation; {0} routed calls, all on the list)" -f $invokeSites.Count)
} else {
    foreach ($p in $searchProblems) { Write-Output "  FAIL  $p" }
    $failures++
}

$checks++
$ls = $plug.GetType("Waypointer.LocationSearch")
$readsExplored = $false
if ($ls) {
    foreach ($m in $ls.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            if ($i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq "Waypointer.MinimapAccess" -and $i.Operand.Name -eq "IsExplored") { $readsExplored = $true }
        }
    }
}
if (-not $ls) { Write-Output "  FAIL  Waypointer.LocationSearch not found"; $failures++ }
elseif ($Edition -eq "Wayfinder" -and $readsExplored) { Write-Output "  ok    Wayfinder's search reads MinimapAccess.IsExplored (that its filter reaches SearchRules.FinishHits is not checked yet)" }
elseif ($Edition -eq "Wayfinder") { Write-Output "  FAIL  Wayfinder's search does not read MinimapAccess.IsExplored - it would place unexplored places"; $failures++ }
elseif (-not $readsExplored) { Write-Output "  ok    TomTom's search places everything in range (does not read MinimapAccess.IsExplored)" }
else { Write-Output "  FAIL  TomTom's search reads MinimapAccess.IsExplored - the explored filter belongs to Wayfinder"; $failures++ }

# 13b and 13c follow what is handed to Find's Unity-free decisions (SearchRules), which the tests check only with
# stand-in arguments. Get-ArgumentSources gives up on a call that follows a ?: in the same method (SearchJob's scan makes
# its zone set with one), so these trace arguments with a tolerant replay instead: a value consumed across a ?: join
# starts a new statement rather than failing.
function Get-StackBefore($ins, [int]$upto, $handlers = $null) {
    $stack = New-Object System.Collections.ArrayList
    for ($k = 0; $k -lt $upto; $k++) {
        $i = $ins[$k]
        if ($handlers) {
            foreach ($h in $handlers) {
                $ht = "$($h.HandlerType)"
                if ((($ht -eq "Catch" -or $ht -eq "Filter") -and $h.HandlerStart -eq $i) -or ($ht -eq "Filter" -and $h.FilterStart -eq $i)) { $stack.Clear(); [void]$stack.Add($k) }
                elseif (($ht -eq "Finally" -or $ht -eq "Fault") -and $h.HandlerStart -eq $i) { $stack.Clear() }
            }
        }
        $pop = "$($i.OpCode.StackBehaviourPop)"; $push = "$($i.OpCode.StackBehaviourPush)"
        $flow = "$($i.OpCode.FlowControl)"
        if ($flow -eq "Branch" -or $flow -eq "Cond_Branch" -or $flow -eq "Return" -or $flow -eq "Throw") { $stack.Clear(); continue }
        $nPop = 0
        if ($pop -eq "Varpop") { $nPop = $i.Operand.Parameters.Count; if ($i.Operand.HasThis -and $i.OpCode.Name -ne "newobj") { $nPop++ } }
        elseif ($pop -ne "Pop0") { $nPop = @($pop -split "_").Count }
        if ($nPop -gt $stack.Count) { $stack.Clear(); $nPop = 0 }
        $src = $k
        if ($nPop -gt 0) { $stack.RemoveRange($stack.Count - $nPop, $nPop) }
        $nPush = 1
        if ($push -eq "Push0") { $nPush = 0 }
        elseif ($push -eq "Push1_push1") { $nPush = 2 }
        elseif ($push -eq "Varpush" -and $i.Operand.ReturnType.FullName -eq "System.Void") { $nPush = 0 }
        for ($j = 0; $j -lt $nPush; $j++) { [void]$stack.Add($src) }
    }
    return ,@($stack)
}
function Get-CallArgs($ins, [int]$callAt, $handlers = $null) {
    $st = Get-StackBefore $ins $callAt $handlers
    $c = $ins[$callAt].Operand; $n = $c.Parameters.Count; if ($c.HasThis) { $n++ }
    if ($st.Count -lt $n) { return $null }
    return ,@($st[($st.Count - $n)..($st.Count - 1)])
}
function Find-Calls($insList, [string]$declType, [string]$name) {
    $found = @()
    for ($k = 0; $k -lt $insList.Count; $k++) {
        $o = $insList[$k].Operand
        if ($insList[$k].OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq $declType -and $o.Name -eq $name) { $found += $k }
    }
    return ,$found
}
function Get-InstructionText($p) {
    if ($p.Operand -is [Mono.Cecil.MemberReference]) { return ("{0} {1}::{2}" -f $p.OpCode.Name, $p.Operand.DeclaringType.Name, $p.Operand.Name) }
    return $p.OpCode.Name
}
# The value an argument stands for: the instruction itself or, through a local, what every store into that local stores.
function Resolve-Value($insList, [int]$at, $handlers) {
    $p = $insList[$at]
    $li = Get-LocalIndex $p
    if ($p.OpCode.Name -like "ldloc*" -and $li -ge 0) {
        $vals = @()
        for ($k = 0; $k -lt $insList.Count; $k++) {
            if ($insList[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $insList[$k]) -eq $li) {
                $st = Get-StackBefore $insList $k $handlers
                if ($st.Count -lt 1) { return "(?)" }
                $vals += (Get-InstructionText $insList[$st[$st.Count - 1]])
            }
        }
        $u = @($vals | Select-Object -Unique)
        if ($u.Count -eq 1) { return $u[0] } else { return "(" + ($u -join " | ") + ")" }
    }
    return (Get-InstructionText $p)
}

# 13b (both editions): LocationSearch.Finish hands the search's own places (_hits) and query (_query) to
# SearchRules.FinishHits, once - without the query a nest found would no longer replace its place - and TomTom hands it
# no filter (ldnull), so TomTom keeps every place in range. Wayfinder's filter argument is not checked here yet.
# Find's catalogue (SearchCatalog) was derived from Valheim 1.0.16's own data; no check can see whether a newer build
# moved its chests or nests, so a different game build gets a note (not counted as a check).
$catalogueBuild = "96cfc004f7f4a6f30d070bef39eafd79c466a137121c4665a2f19fb9c15c6127"
try {
    $avPath = Join-Path $ValheimDir "valheim_Data\Managed\assembly_valheim.dll"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [IO.File]::OpenRead($avPath)
    try { $avHash = ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace "-", "").ToLowerInvariant() } finally { $fs.Dispose() }
    if ($avHash -ne $catalogueBuild) { Write-Output "  NOTE  this game is not Valheim 1.0.16 (assembly_valheim.dll differs): Find's catalogue was derived for 1.0.16 - derive it again for this build" }
} catch { Write-Output ("  NOTE  could not read assembly_valheim.dll to compare the game build: {0}" -f $_.Exception.Message) }

$checks++
$b13 = @()
$finish = $null
if ($ls) { $finish = $ls.Methods | Where-Object { $_.Name -eq "Finish" -and $_.HasBody } | Select-Object -First 1 }
if (-not $finish) { $b13 += "LocationSearch.Finish not found" }
else {
    $fins = @($finish.Body.Instructions); $fhd = $finish.Body.ExceptionHandlers
    $fh = Find-Calls $fins "Waypointer.SearchRules" "FinishHits"
    if ($fh.Count -ne 1) { $b13 += ("LocationSearch.Finish calls SearchRules.FinishHits {0} times, expected once" -f $fh.Count) }
    else {
        $a = Get-CallArgs $fins $fh[0] $fhd
        if ($null -eq $a) { $b13 += "FinishHits' arguments cannot be traced" }
        else {
            $v0 = Resolve-Value $fins $a[0] $fhd; $v1 = Resolve-Value $fins $a[1] $fhd; $v2 = Resolve-Value $fins $a[2] $fhd
            if ($v0 -ne "ldsfld LocationSearch::_hits") { $b13 += "FinishHits is handed '$v0', not LocationSearch._hits" }
            if ($v1 -ne "ldsfld LocationSearch::_query") { $b13 += "FinishHits' query is '$v1', not LocationSearch._query" }
            if ($Edition -eq "TomTom" -and $v2 -ne "ldnull") { $b13 += "TomTom's FinishHits filter is '$v2', not null" }
        }
    }
}
if ($b13.Count -eq 0) { Write-Output ("  ok    LocationSearch.Finish hands its places and query to SearchRules.FinishHits once{0}" -f $(if ($Edition -eq "TomTom") { ", with no filter" } else { " (Wayfinder's filter argument is not checked yet)" })) }
else { foreach ($p in $b13) { Write-Output "  FAIL  13b: $p" }; $failures++ }

# 13c: the server's known-empty drop (SkipCheckedChests for a Bee Nest) is wired in SearchJob's Scan iterator:
# KnownEmptyApplies gets OnServer, CheckChests and Query, and DropPlacesKnownEmpty is reached only when it is true;
# NoteObject gets the zone set and its false result skips the object; DropPlacesKnownEmpty gets that same zone set; and
# the zone set is made (a new HashSet<Int64>) under SearchQuery.PlacesHoldObjects.
$checks++
$c13 = @()
$sjType = $plug.GetType("Waypointer.SearchJob")
$scanIter = $null; if ($sjType) { $scanIter = $sjType.NestedTypes | Where-Object { $_.Name -like "<Scan>*" } | Select-Object -First 1 }
$mn = $null; if ($scanIter) { $mn = $scanIter.Methods | Where-Object { $_.Name -eq "MoveNext" } | Select-Object -First 1 }
if (-not $mn) { $c13 += "SearchJob's Scan iterator (MoveNext) not found" }
else {
    $si = @($mn.Body.Instructions); $hd = $mn.Body.ExceptionHandlers
    $kea = Find-Calls $si "Waypointer.SearchRules" "KnownEmptyApplies"
    $dke = Find-Calls $si "Waypointer.SearchRules" "DropPlacesKnownEmpty"
    $nob = Find-Calls $si "Waypointer.SearchRules" "NoteObject"
    if ($kea.Count -ne 1 -or $dke.Count -ne 1 -or $nob.Count -ne 1) { $c13 += ("Scan calls KnownEmptyApplies {0}x, DropPlacesKnownEmpty {1}x, NoteObject {2}x; expected once each" -f $kea.Count, $dke.Count, $nob.Count) }
    else {
        $a = Get-CallArgs $si $kea[0] $hd
        $want = @("ldfld SearchJob::OnServer", "ldfld SearchJob::CheckChests", "ldfld SearchJob::Query")
        for ($q = 0; $q -lt 3; $q++) {
            $got = if ($a) { Get-InstructionText $si[$a[$q]] } else { "(?)" }
            if ($got -ne $want[$q]) { $c13 += ("KnownEmptyApplies argument {0} is '{1}', not {2}" -f $q, $got, $want[$q]) }
        }
        $br = $si[$kea[0] + 1]
        if (-not ($br.OpCode.Name -like "brfalse*" -and $br.Operand.Offset -gt $si[$dke[0]].Offset -and $dke[0] -gt $kea[0])) { $c13 += "DropPlacesKnownEmpty is not reached only when KnownEmptyApplies is true (no brfalse past it right after the call)" }
        $na = Get-CallArgs $si $nob[0] $hd
        $da = Get-CallArgs $si $dke[0] $hd
        $zn = if ($na) { $si[$na[0]] } else { $null }
        $zd = if ($da) { $si[$da[1]] } else { $null }
        if (-not ($zn -and $zn.OpCode.Name -eq "ldfld" -and $zd -and $zd.OpCode.Name -eq "ldfld" -and "$($zn.Operand)" -eq "$($zd.Operand)")) {
            $c13 += ("NoteObject's zone set ('{0}') and DropPlacesKnownEmpty's ('{1}') are not the same field" -f $(if ($zn) { Get-InstructionText $zn } else { "?" }), $(if ($zd) { Get-InstructionText $zd } else { "?" }))
        }
        if (-not ($si[$nob[0] + 1].OpCode.Name -like "brfalse*")) { $c13 += "NoteObject's result is not branched on (brfalse, skip the object) right after the call" }
        if ($zn -and $zn.OpCode.Name -eq "ldfld") {
            $st = @(); for ($k = 0; $k -lt $si.Count; $k++) { if ($si[$k].OpCode.Name -eq "stfld" -and "$($si[$k].Operand)" -eq "$($zn.Operand)") { $st += $k } }
            if ($st.Count -ne 1) { $c13 += ("the zone set is stored {0} times, expected once" -f $st.Count) }
            else {
                $seenNew = $false; $seenGate = $false
                for ($k = $st[0] - 1; $k -ge [Math]::Max(0, $st[0] - 8); $k--) {
                    if ($si[$k].OpCode.Name -eq "newobj" -and "$($si[$k].Operand.DeclaringType)" -like "System.Collections.Generic.HashSet*Int64*") { $seenNew = $true }
                    if ($si[$k].OpCode.Name -eq "ldfld" -and $si[$k].Operand.Name -eq "PlacesHoldObjects") { $seenGate = $true; break }
                }
                if (-not ($seenNew -and $seenGate)) { $c13 += "the zone set is not made (a new HashSet<Int64>) under SearchQuery.PlacesHoldObjects" }
            }
        }
    }
}
if ($c13.Count -eq 0) { Write-Output "  ok    the server's known-empty drop is wired: KnownEmptyApplies(OnServer, CheckChests, Query) gates it, and it uses the zone set NoteObject fills" }
else { foreach ($p in $c13) { Write-Output "  FAIL  13c: $p" }; $failures++ }

# 13d (1.4.1): Find leaves out an object a player placed or spawned with `spawn`. SearchJob's Scan iterator calls SearchRules.WorldMade
# once, with ZDO.GetLong(ZDOVars.s_creator, 0) and ZDO.GetBool(ZDOVars.s_cheated, false), and its false result skips the
# object (a brfalse to where NoteObject's false result goes), before NoteObject (so a spawned nest neither lists nor
# counts for its place).
$checks++
$d13 = @()
if (-not $mn) { $d13 += "SearchJob's Scan iterator (MoveNext) not found" }
else {
    $si = @($mn.Body.Instructions); $hd = $mn.Body.ExceptionHandlers
    $wm = Find-Calls $si "Waypointer.SearchRules" "WorldMade"
    $nob = Find-Calls $si "Waypointer.SearchRules" "NoteObject"
    if ($wm.Count -ne 1) { $d13 += ("Scan calls SearchRules.WorldMade {0} times, expected once" -f $wm.Count) }
    else {
        $a = Get-CallArgs $si $wm[0] $hd
        $want = @(@("GetLong", "s_creator"), @("GetBool", "s_cheated"))
        $argsOk = $true
        for ($q = 0; $q -lt 2; $q++) {
            $ok = $false
            if ($a) {
                $ci = $a[$q]; $c = $si[$ci]
                if ($c.OpCode.Name -like "call*" -and $c.Operand -is [Mono.Cecil.MethodReference] -and $c.Operand.DeclaringType.Name -eq "ZDO" -and $c.Operand.Name -eq $want[$q][0]) {
                    $ca = Get-CallArgs $si $ci $hd
                    if ($ca -and (Get-InstructionText $si[$ca[1]]) -eq ("ldsfld ZDOVars::" + $want[$q][1])) { $ok = $true }
                }
            }
            if (-not $ok) { $d13 += ("WorldMade argument {0} is not ZDO.{1}(ZDOVars.{2}, ...)" -f $q, $want[$q][0], $want[$q][1]); $argsOk = $false }
        }
        if ("$($si[$wm[0] + 1].OpCode.FlowControl)" -ne "Cond_Branch") { $d13 += "WorldMade's result is not branched on right after the call" }
        if ($argsOk) {
            $gl = Get-CallArgs $si $a[0] $hd; $gb = Get-CallArgs $si $a[1] $hd
            $defOk = $false
            if ($gl -and $gb) {
                $d0 = $si[$gl[2]]; $d1 = $si[$gb[2]]
                $zeroLong = ($d0.OpCode.Name -eq "conv.i8" -and $si[$gl[2] - 1].OpCode.Name -eq "ldc.i4.0") -or ($d0.OpCode.Name -eq "ldc.i8" -and [long]$d0.Operand -eq 0)
                $defOk = $zeroLong -and $d1.OpCode.Name -eq "ldc.i4.0"
            }
            if (-not $defOk) { $d13 += "WorldMade's ZDO reads do not default to 0 and false" }
        }
        $wbr = $si[$wm[0] + 1]
        if (-not ($wbr.OpCode.Name -like "brfalse*" -and $nob.Count -eq 1 -and $si[$nob[0] + 1].OpCode.Name -like "brfalse*" -and [object]::ReferenceEquals($wbr.Operand, $si[$nob[0] + 1].Operand))) { $d13 += "WorldMade's false result does not skip the object (a brfalse to where NoteObject's false result goes)" }
        if ($nob.Count -eq 1 -and $nob[0] -lt $wm[0]) { $d13 += "WorldMade is called after NoteObject (a console-spawned nest would count for its place)" }
    }
}
if ($d13.Count -eq 0) { Write-Output "  ok    Find leaves out an object a player placed or spawned with spawn: SearchRules.WorldMade(creator, cheated) is checked before an object counts" }
else { foreach ($p in $d13) { Write-Output "  FAIL  13d: $p" }; $failures++ }

# 16 (1.5.0): Wayfinder places a waypoint from a map click only on explored land. In
# Minimap_OnMapLeftClick_Patch.HandleWaypointClick it calls MinimapAccess.IsExplored once, branches on the result at once,
# before its single call to WaypointManager.Add, and on the same local (the click's world position) that Add is given.
# Since 1.5.2 the branch is followed: where IsExplored is false, the straight-line code reached returns true (the click
# consumed) and calls nothing but Plugin.Log.LogInfo (no waypoint, nothing on screen), and where it is true the code goes
# on to Add; and the rule
# is asked after MapClickRules.Decide and AddFromPin, so following a pin or removing a waypoint works in the fog too.
# TomTom's does not call it there.
$checks++
$f16 = @()
# Where straight-line code (and unconditional branches) from $from ends: "ret:<n>" with the constant returned, "add" when
# it calls WaypointManager.Add or AddFromPin on the way, "call:<Type.Name>" when it calls anything else but a log line
# (ManualLogSource.LogInfo), "?" when it cannot be followed (a conditional branch, a throw).
function Get-PathEnd16($ins, [int]$from) {
    $k = $from; $consts = @{}; $top = $null; $steps = 0
    while ($k -ge 0 -and $k -lt $ins.Count -and $steps -lt 200) {
        $steps++
        $i = $ins[$k]; $n = $i.OpCode.Name; $f = "$($i.OpCode.FlowControl)"
        if ($n -like "call*" -and $i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and @("Add", "AddFromPin") -contains $i.Operand.Name) { return "add" }
        if ($n -like "call*" -and $i.Operand -is [Mono.Cecil.MethodReference] -and -not ($i.Operand.DeclaringType.FullName -eq "BepInEx.Logging.ManualLogSource" -and $i.Operand.Name -eq "LogInfo")) { return ("call:{0}.{1}" -f $i.Operand.DeclaringType.Name, $i.Operand.Name) }
        if ($n -match '^ldc\.i4\.([0-8])$') { $top = [int]$Matches[1] }
        elseif ($n -like "stloc*") { $consts[(Get-LocalIndex $i)] = $top; $top = $null }
        elseif ($n -like "ldloc*") { $li = Get-LocalIndex $i; $top = $(if ($consts.ContainsKey($li)) { $consts[$li] } else { $null }) }
        elseif ($f -eq "Return") { return ("ret:{0}" -f $top) }
        elseif ($f -eq "Branch") { $k = [array]::IndexOf($ins, $i.Operand); continue }
        elseif ($f -eq "Cond_Branch" -or $f -eq "Throw") { return "?" }
        else { $top = $null }
        $k++
    }
    return "?"
}
$mlc = $plug.GetType("Waypointer.Minimap_OnMapLeftClick_Patch")
$hwc = $null; if ($mlc) { $hwc = $mlc.Methods | Where-Object { $_.Name -eq "HandleWaypointClick" -and $_.HasBody } | Select-Object -First 1 }
if (-not $hwc) { $f16 += "Minimap_OnMapLeftClick_Patch.HandleWaypointClick not found" }
else {
    $hi = @($hwc.Body.Instructions); $hh = $hwc.Body.ExceptionHandlers
    $ie = Find-Calls $hi "Waypointer.MinimapAccess" "IsExplored"
    $ad = Find-Calls $hi "Waypointer.WaypointManager" "Add"
    if ($Edition -eq "TomTom") {
        if ($ie.Count -ne 0) { $f16 += "TomTom's map click reads MinimapAccess.IsExplored - the fog rule belongs to Wayfinder" }
    }
    elseif ($ie.Count -ne 1 -or $ad.Count -ne 1) { $f16 += ("HandleWaypointClick calls MinimapAccess.IsExplored {0}x and WaypointManager.Add {1}x; expected once each" -f $ie.Count, $ad.Count) }
    else {
        if ("$($hi[$ie[0] + 1].OpCode.FlowControl)" -ne "Cond_Branch") { $f16 += "IsExplored's result is not branched on right after the call" }
        if ($ie[0] -gt $ad[0]) { $f16 += "IsExplored is called after WaypointManager.Add" }
        $ea = Get-CallArgs $hi $ie[0] $hh; $aa = Get-CallArgs $hi $ad[0] $hh
        $el = -1; $al = -2
        if ($ea -and $hi[$ea[0]].OpCode.Name -like "ldloc*") { $el = Get-LocalIndex $hi[$ea[0]] }
        if ($aa -and $hi[$aa[0]].OpCode.Name -like "ldloc*") { $al = Get-LocalIndex $hi[$aa[0]] }
        if ($el -lt 0 -or $el -ne $al) { $f16 += "IsExplored is not given the same local (the click's position) as WaypointManager.Add" }
        $br = $hi[$ie[0] + 1]
        $refuseAt = -1; $goAt = -1
        if ($br.OpCode.Name -like "brtrue*") { $refuseAt = $ie[0] + 2; $goAt = [array]::IndexOf($hi, $br.Operand) }
        elseif ($br.OpCode.Name -like "brfalse*") { $refuseAt = [array]::IndexOf($hi, $br.Operand); $goAt = $ie[0] + 2 }
        $refuse = $(if ($refuseAt -ge 0) { Get-PathEnd16 $hi $refuseAt } else { "?" })
        if ($refuse -ne "ret:1") {
            $f16 += ("where IsExplored is false the click {0}, not a consumed refusal (return true, no waypoint, nothing but a log line)" -f $(if ($refuse -eq "add") { "places a waypoint" } elseif ($refuse -like "ret:*") { "returns " + $refuse.Substring(4) } elseif ($refuse -like "call:*") { "calls " + $refuse.Substring(5) } else { "cannot be followed" }))
        }
        if ($goAt -lt 0 -or $ad[0] -lt $goAt) { $f16 += "where IsExplored is true the click does not go on to WaypointManager.Add" }
        $dc16 = Find-Calls $hi "Waypointer.MapClickRules" "Decide"; $fp16 = Find-Calls $hi "Waypointer.WaypointManager" "AddFromPin"
        if ($dc16.Count -ne 1 -or $fp16.Count -ne 1 -or $ie[0] -lt $dc16[0] -or $ie[0] -lt $fp16[0]) {
            $f16 += "IsExplored is asked before MapClickRules.Decide or AddFromPin (following a pin and removing a waypoint must work in the fog)"
        }
    }
}
if ($f16.Count -eq 0) { Write-Output ("  ok    {0}" -f $(if ($Edition -eq "TomTom") { "TomTom's map click places a waypoint anywhere (does not read MinimapAccess.IsExplored)" } else { "Wayfinder's map click places a waypoint only where MinimapAccess.IsExplored says the map is explored (the branch followed to a consumed refusal), after the click's decision" })) }
else { foreach ($p in $f16) { Write-Output "  FAIL  16: $p" }; $failures++ }

# 17 (1.5.0): every code path that makes a waypoint is a known one. WaypointManager.Add, AddFromPin, AddMany and AddRange
# are called only from the methods listed here, and a Waypoint is constructed only in WaypointManager's Add, AddMany,
# AddFromPin and FromEntry. A new path must be added here, with a check of what it may create.
$checks++
$g17 = @()
$allowed17 = @{
    "Add" = @("Waypointer.Minimap_OnMapLeftClick_Patch::HandleWaypointClick", "Waypointer.WaypointWindow::AddHere", "Waypointer.Terminal_InitTerminal_Patch::AddHere")
    "AddFromPin" = @("Waypointer.Minimap_OnMapLeftClick_Patch::HandleWaypointClick")
    "AddMany" = @("Waypointer.LocationSearch::Finish")
    "AddRange" = @()
}
if ($Edition -eq "TomTom") {
    $allowed17["Add"] += @("Waypointer.WaypointManager::AddRange", "Waypointer.Terminal_InitTerminal_Patch::AddFromArgs")
    $allowed17["AddRange"] = @("Waypointer.WaypointWindow::ApplyInput")
}
$ctorAllowed17 = @("Waypointer.WaypointManager::Add", "Waypointer.WaypointManager::AddMany", "Waypointer.WaypointManager::AddFromPin", "Waypointer.WaypointManager::FromEntry")
function Get-TypesDeep17($t) { $t; foreach ($n in $t.NestedTypes) { Get-TypesDeep17 $n } }
$sites17 = @()
foreach ($t in @($plug.Types | ForEach-Object { Get-TypesDeep17 $_ })) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $site = "{0}::{1}" -f $t.FullName, $m.Name
        foreach ($i in $m.Body.Instructions) {
            $o = $i.Operand
            if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
            if ($i.OpCode.Name -like "call*" -and $o.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $allowed17.ContainsKey($o.Name)) {
                if ($allowed17[$o.Name] -notcontains $site) { $sites17 += ("WaypointManager.{0} is called from {1}, not a known place to make a waypoint" -f $o.Name, $site) }
            }
            if ($i.OpCode.Name -eq "newobj" -and $o.DeclaringType.FullName -eq "Waypointer.Waypoint") {
                if ($ctorAllowed17 -notcontains $site) { $sites17 += ("a Waypoint is constructed in {0}, outside WaypointManager's Add, AddMany, AddFromPin and FromEntry" -f $site) }
            }
        }
    }
}
$g17 = @($sites17 | Select-Object -Unique)
if ($g17.Count -eq 0) { Write-Output "  ok    every code path that makes a waypoint is a known one (WaypointManager.Add, AddFromPin, AddMany, AddRange and the Waypoint constructor)" }
else { foreach ($p in $g17) { Write-Output "  FAIL  17: $p" }; $failures++ }

# 18 (1.5.1): a map click decides by the right pin, and no arrival counts while the player is dead. The rules are
# Unity-free and tested (MapClickRules.Decide, ArrivalRules.Step); this checks the wiring the tests cannot see.
# In HandleWaypointClick: the four nearest pins are asked for (MinimapAccess.GetClosestOwnedWaypointPin,
# GetClosestFollowedPin, GetClosestAdoptablePin, GetClosestTransientPin), once each and with the same reach local, before
# MapClickRules.Decide; Decide is called once, and each of its four arguments, in that order, is a local with one store
# instruction, whose value is exactly WaypointManager.HorizontalDistance(<that lookup's pin>.m_pos, <the position local
# the lookup is given>) or -1, chosen by one conditional branch on that pin's local; its result is kept in a local with no
# other store; no WaypointManager call that changes the route (the Queue list included) comes before it; the waypoint is
# removed (WaypointManager.Remove) once, and AddFromPin is called once, its argument resolving to GetClosestAdoptablePin's
# result with no ?: or ?? joining at the call. Since 1.5.2 the four lookups leave out the pins the large map hides, as
# Valheim's own click does: each hands its walk over the pins (MinimapAccess.FindClosest or ClosestAdoptable, or its own
# loop in GetClosestTransientPin) MinimapAccess.ShownIconTypes and SharedPinsFade, called in that lookup, and each walk
# asks MapClickRules.PinShown once, with those two as its first and third arguments and one pin local's m_type and
# m_ownerID as its second and fourth - since 1.6.0, the local the walk stores pins[i] into - and branches on the result at
# once; ShownIconTypes reads _visibleIconTypesField and
# SharedPinsFade _sharedMapDataFadeField (not the other's), each set in Init from AccessTools.Field with the game's name;
# and the right-click delete (Minimap_RemovePin_Patch.Prefix) and WaypointManager.EnsurePins keep counting hidden pins:
# they ask GetClosestOwnedWaypointPinEvenHidden and GetClosestAdoptablePinEvenHidden (never the click's lookups), which
# hand their walk null and 1. Not seen: which pin Remove is given (chosen with a ?: on the decision), which arm of a
# distance's ?: is which, which way PinShown's branch goes or another test joined to it, which way the helpers' guards
# go, a write through a reference (ref, Interlocked), a change to the position or reach local
# between the lookups and the distances, and anything done to the pins themselves before the lookups. Until 1.5.1 a
# followed pin anywhere within reach won over the pin under the pointer. In
# WaypointManager.Tick: ArrivalRules.Step is called once, its playerDead argument is Character.IsDead, its result is
# kept the same way, and OnReached comes after it. Which branch each result picks is logic - not seen here.
$checks++
$h18 = @()
# The local a decision's result goes to: its index when the call is followed at once by a store into a local that no
# other instruction stores into; -1 when no local is stored right after the call, -2 when the local is also set elsewhere.
function Get-ResultLocal18($ins, [int]$callAt, [string]$name) {
    if ($callAt + 1 -ge $ins.Count -or $ins[$callAt + 1].OpCode.Name -notlike "stloc*") { return -1 }
    $li = Get-LocalIndex $ins[$callAt + 1]
    if ($li -lt 0) { return -1 }
    for ($k = 1; $k -lt $ins.Count; $k++) {
        if ($ins[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $ins[$k]) -eq $li) {
            $prev = $ins[$k - 1]
            if (-not ($prev.OpCode.Name -like "call*" -and $prev.Operand -is [Mono.Cecil.MethodReference] -and $prev.Operand.Name -eq $name)) { return -2 }
        }
    }
    return $li
}
# "" when the argument at $argAt is a local set once, from WaypointManager.HorizontalDistance(<the pin MinimapAccess.$lookup
# returned>.m_pos, <the position that lookup is given>); otherwise what it is instead.
function Test-Distance18($ins, $handlers, [int]$argAt, [string]$lookup) {
    $p = $ins[$argAt]
    if ($p.OpCode.Name -notlike "ldloc*") { return ("is not a local (it is {0})" -f (Get-InstructionText $p)) }
    $li = Get-LocalIndex $p
    $stores = @(); for ($k = 0; $k -lt $ins.Count; $k++) { if ($ins[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $ins[$k]) -eq $li) { $stores += $k } }
    if ($stores.Count -ne 1) { return ("is a local set {0} times" -f $stores.Count) }
    $s0 = $stores[0]
    # Every value that reaches the store - the instruction before it, and the one before each branch that jumps to it -
    # is the HorizontalDistance call or the literal -1 (no pin): "pin != null ? HorizontalDistance(...) : -1f".
    $ends = @($s0 - 1)
    for ($k = 0; $k -lt $s0; $k++) { if ("$($ins[$k].OpCode.FlowControl)" -eq "Branch" -and $ins[$k].Operand -eq $ins[$s0]) { $ends += ($k - 1) } }
    $hd = -1; $bad = @()
    foreach ($e in $ends) {
        $i = $ins[$e]
        if ($i.OpCode.Name -like "call*" -and $i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $i.Operand.Name -eq "HorizontalDistance") {
            if ($hd -ge 0) { $bad += "a second distance" } else { $hd = $e }
        }
        elseif (-not ($i.OpCode.Name -eq "ldc.r4" -and [single]$i.Operand -eq [single]-1)) { $bad += (Get-InstructionText $i) }
    }
    if ($hd -lt 0) { return "is not set from WaypointManager.HorizontalDistance" }
    if ($bad.Count -gt 0) { return ("is not exactly HorizontalDistance(...) or -1 (a value reaching it is {0})" -f ($bad -join ", ")) }
    $ha = Get-CallArgs $ins $hd $handlers
    if (-not $ha -or $ha.Count -ne 2) { return "comes from a HorizontalDistance call whose arguments cannot be traced" }
    $a0 = $ins[$ha[0]]
    if (-not ($a0.OpCode.Name -eq "ldfld" -and $a0.Operand.Name -eq "m_pos") -or $ha[0] -lt 1) { return ("is not measured from a pin's position (HorizontalDistance's first argument is {0})" -f (Get-InstructionText $a0)) }
    $pin = Resolve-Value $ins ($ha[0] - 1) $handlers
    if ($pin -ne ("call MinimapAccess::" + $lookup)) { return ("is measured from the pin of {0}, not of MinimapAccess.{1}" -f $pin, $lookup) }
    # The choice between the two is one test of that pin and nothing else ("pin != null"): a single conditional branch in
    # the expression, right after the pin is loaded (a null comparison's ldnull/ceq/cgt.un in between is allowed).
    $pinLocal = Get-LocalIndex $ins[$ha[0] - 1]
    $from = 0; for ($k = $s0 - 1; $k -ge 0; $k--) { if ($ins[$k].OpCode.Name -like "stloc*" -or $ins[$k].OpCode.Name -like "starg*") { $from = $k + 1; break } }
    $conds = @(); for ($k = $from; $k -lt $s0; $k++) { if ("$($ins[$k].OpCode.FlowControl)" -eq "Cond_Branch") { $conds += $k } }
    if ($conds.Count -ne 1) { return ("chooses between the distance and -1 with {0} tests, not one test of the pin" -f $conds.Count) }
    $t = $conds[0] - 1
    while ($t -ge $from -and @("ldnull", "ceq", "cgt.un", "ldc.i4.0") -contains $ins[$t].OpCode.Name) { $t-- }
    if ($t -lt $from -or $ins[$t].OpCode.Name -notlike "ldloc*" -or (Get-LocalIndex $ins[$t]) -ne $pinLocal) { return "chooses between the distance and -1 by something other than whether that pin was found" }
    $lk = Find-Calls $ins "Waypointer.MinimapAccess" $lookup
    $la = $null; if ($lk.Count -eq 1) { $la = Get-CallArgs $ins $lk[0] $handlers }
    $a1 = $ins[$ha[1]]
    if (-not $la -or $la.Count -lt 2 -or $a1.OpCode.Name -notlike "ldloc*" -or $ins[$la[1]].OpCode.Name -notlike "ldloc*" -or (Get-LocalIndex $a1) -ne (Get-LocalIndex $ins[$la[1]])) {
        return ("is not measured from the click's position (the position MinimapAccess.{0} is given)" -f $lookup)
    }
    return ""
}
if ($hwc) {
    $hi = @($hwc.Body.Instructions); $hh = $hwc.Body.ExceptionHandlers
    $dc = Find-Calls $hi "Waypointer.MapClickRules" "Decide"
    $lookups18 = @("GetClosestOwnedWaypointPin", "GetClosestFollowedPin", "GetClosestAdoptablePin", "GetClosestTransientPin")   # Decide's argument order
    if ($dc.Count -ne 1) { $h18 += ("HandleWaypointClick calls MapClickRules.Decide {0}x; expected once" -f $dc.Count) }
    else {
        $d0 = $dc[0]
        $reach18 = @()
        foreach ($lk in $lookups18) {
            $q = Find-Calls $hi "Waypointer.MinimapAccess" $lk
            if ($q.Count -ne 1) { $h18 += ("HandleWaypointClick calls MinimapAccess.{0} {1}x; expected once" -f $lk, $q.Count) }
            else {
                if ($q[0] -gt $d0) { $h18 += ("MinimapAccess.{0} is asked after MapClickRules.Decide" -f $lk) }
                $qa = Get-CallArgs $hi $q[0] $hh
                if ($qa -and $qa.Count -eq 3 -and $hi[$qa[2]].OpCode.Name -like "ldloc*") { $reach18 += (Get-LocalIndex $hi[$qa[2]]) } else { $reach18 += -1 }
            }
        }
        if ($reach18.Count -eq 4 -and (@($reach18 | Select-Object -Unique).Count -ne 1 -or $reach18[0] -lt 0)) { $h18 += "the four nearest pins are not looked for with the same reach local" }
        # Route changes, the queue list's own included (WaypointManager.Queue).
        $mutators18 = @("Add", "AddFromPin", "AddMany", "AddRange", "Remove", "RemoveAt", "SkipActive", "Clear", "MakeActive", "ActivateClosest", "ForgetByPin", "get_Queue")
        for ($k = 0; $k -lt $d0; $k++) {
            $o = $hi[$k].Operand
            if ($hi[$k].OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $mutators18 -contains $o.Name) {
                $h18 += ("WaypointManager.{0} is called before MapClickRules.Decide (the route changes before the nearest pin decides)" -f $o.Name)
            }
        }
        $rm = Find-Calls $hi "Waypointer.WaypointManager" "Remove"
        $fp = Find-Calls $hi "Waypointer.WaypointManager" "AddFromPin"
        if ($rm.Count -ne 1 -or $fp.Count -ne 1) { $h18 += ("HandleWaypointClick calls WaypointManager.Remove {0}x and AddFromPin {1}x; expected once each" -f $rm.Count, $fp.Count) }
        else {
            # The pin followed is the nearest pin not yet followed, as is: no ?: or ?? joins at the call.
            $fa = Get-CallArgs $hi $fp[0] $hh
            $joins = @(); for ($k = 0; $k -lt $hi.Count; $k++) { if (("$($hi[$k].OpCode.FlowControl)" -eq "Branch" -or "$($hi[$k].OpCode.FlowControl)" -eq "Cond_Branch") -and $hi[$k].Operand -eq $hi[$fp[0]]) { $joins += $k } }
            if (-not $fa -or $fa.Count -ne 1 -or $joins.Count -gt 0 -or (Resolve-Value $hi $fa[0] $hh) -ne "call MinimapAccess::GetClosestAdoptablePin") { $h18 += "WaypointManager.AddFromPin is not given the nearest pin not yet followed (MinimapAccess.GetClosestAdoptablePin's)" }
        }
        $dl = Get-ResultLocal18 $hi $d0 "Decide"
        if ($dl -eq -1) { $h18 += "MapClickRules.Decide's result is not stored in a local right after the call" }
        elseif ($dl -eq -2) { $h18 += "the local holding MapClickRules.Decide's result is also set from something else (an override of the decision)" }
        $da = Get-CallArgs $hi $d0 $hh
        if (-not $da -or $da.Count -ne 4) { $h18 += "MapClickRules.Decide's arguments cannot be traced" }
        else {
            for ($n = 0; $n -lt 4; $n++) {
                $why = Test-Distance18 $hi $hh $da[$n] $lookups18[$n]
                if ($why -ne "") { $h18 += ("MapClickRules.Decide's argument {0} {1}" -f ($n + 1), $why) }
            }
        }
    }
}
else { $h18 += "Minimap_OnMapLeftClick_Patch.HandleWaypointClick not found" }
# 1.5.2: the four lookups leave out the pins the large map hides.
$maT = $plug.GetType("Waypointer.MinimapAccess")
function Get-Method18($t, [string]$name, [int]$argc) {
    if (-not $t) { return $null }
    return $t.Methods | Where-Object { $_.Name -eq $name -and $_.HasBody -and $_.Parameters.Count -eq $argc } | Select-Object -First 1
}
# "" when the walk asks MapClickRules.PinShown once, branches on its result at once, and gives it, as its first and
# third arguments, values that resolve to $filter and $fade (a parameter's name, or the call that set a local).
function Test-PinShown18($m, [string]$filter, [string]$fade) {
    $wi = @($m.Body.Instructions); $wh = $m.Body.ExceptionHandlers
    $ps = Find-Calls $wi "Waypointer.MapClickRules" "PinShown"
    if ($ps.Count -ne 1) { return ("asks MapClickRules.PinShown {0}x, expected once" -f $ps.Count) }
    if ("$($wi[$ps[0] + 1].OpCode.FlowControl)" -ne "Cond_Branch") { return "does not branch on MapClickRules.PinShown's result right after the call" }
    $pa = Get-CallArgs $wi $ps[0] $wh
    if (-not $pa -or $pa.Count -ne 4) { return "gives MapClickRules.PinShown arguments that cannot be traced" }
    $got = @()
    foreach ($n in @(0, 2)) {
        $a = $wi[$pa[$n]]
        if ($a.OpCode.Name -like "ldarg*") {
            $ix = -1
            if ($a.OpCode.Name -match '^ldarg\.([0-3])$') { $ix = [int]$Matches[1] } elseif ($a.Operand -is [Mono.Cecil.ParameterDefinition]) { $ix = $a.Operand.Index }
            if ($m.HasThis) { $ix-- }
            $got += $(if ($ix -ge 0 -and $ix -lt $m.Parameters.Count) { "parameter " + $m.Parameters[$ix].Name } else { "an argument" })
        }
        else { $got += (Resolve-Value $wi $pa[$n] $wh) }
    }
    if ($got[0] -ne $filter -or $got[1] -ne $fade) { return ("gives MapClickRules.PinShown {0} and {1}, not {2} and {3}" -f $got[0], $got[1], $filter, $fade) }
    $t1 = $wi[$pa[1]]; $t3 = $wi[$pa[3]]
    $pinOk = $t1.OpCode.Name -eq "ldfld" -and $t1.Operand.Name -eq "m_type" -and $t3.OpCode.Name -eq "ldfld" -and $t3.Operand.Name -eq "m_ownerID" -and $pa[1] -ge 1 -and $pa[3] -ge 1 -and $wi[$pa[1] - 1].OpCode.Name -like "ldloc*" -and $wi[$pa[3] - 1].OpCode.Name -like "ldloc*" -and (Get-LocalIndex $wi[$pa[1] - 1]) -eq (Get-LocalIndex $wi[$pa[3] - 1])
    if (-not $pinOk) { return ("gives MapClickRules.PinShown {0} and {1} as the pin's type and owner, not one pin's m_type and m_ownerID" -f (Get-InstructionText $t1), (Get-InstructionText $t3)) }
    # That pin is the one the walk is at: the one local a list's get_Item (pins[i]) is stored into (since 1.6.0).
    $gi = @(); for ($k = 0; $k -lt $wi.Count - 1; $k++) { if ($wi[$k].OpCode.Name -like "call*" -and $wi[$k].Operand -is [Mono.Cecil.MethodReference] -and $wi[$k].Operand.Name -eq "get_Item" -and $wi[$k + 1].OpCode.Name -like "stloc*") { $gi += (Get-LocalIndex $wi[$k + 1]) } }
    if ($gi.Count -ne 1 -or (Get-LocalIndex $wi[$pa[1] - 1]) -ne $gi[0]) { return "gives MapClickRules.PinShown the type and owner of a pin other than the one the walk is at (pins[i])" }
    return ""
}
$walks18 = @(@("GetClosestOwnedWaypointPin", "FindClosest", 7, 5, 6), @("GetClosestFollowedPin", "FindClosest", 7, 5, 6), @("GetClosestAdoptablePin", "ClosestAdoptable", 5, 3, 4))
foreach ($w in $walks18) {
    $lm = Get-Method18 $maT $w[0] 3
    if (-not $lm) { $h18 += ("MinimapAccess.{0} not found" -f $w[0]); continue }
    $li = @($lm.Body.Instructions); $lh = $lm.Body.ExceptionHandlers
    $wc = Find-Calls $li "Waypointer.MinimapAccess" $w[1]
    $wa = $null; if ($wc.Count -eq 1) { $wa = Get-CallArgs $li $wc[0] $lh }
    if (-not $wa -or $wa.Count -ne $w[2]) { $h18 += ("MinimapAccess.{0} does not hand its walk to MinimapAccess.{1} once" -f $w[0], $w[1]); continue }
    $vf = Resolve-Value $li $wa[$w[3]] $lh; $vd = Resolve-Value $li $wa[$w[4]] $lh
    if ($vf -ne "call MinimapAccess::ShownIconTypes" -or $vd -ne "call MinimapAccess::SharedPinsFade") {
        $h18 += ("MinimapAccess.{0} does not leave out the pins the map hides (it hands {1} {2} and {3}, not ShownIconTypes and SharedPinsFade)" -f $w[0], $w[1], $vf, $vd)
    }
}
foreach ($wk in @(@("FindClosest", 7), @("ClosestAdoptable", 5))) {
    $wm18 = Get-Method18 $maT $wk[0] $wk[1]
    if (-not $wm18) { $h18 += ("MinimapAccess.{0} not found" -f $wk[0]); continue }
    $why = Test-PinShown18 $wm18 "parameter shownIconTypes" "parameter sharedPinsFade"
    if ($why -ne "") { $h18 += ("MinimapAccess.{0} {1}" -f $wk[0], $why) }
}
$tp18 = Get-Method18 $maT "GetClosestTransientPin" 3
if (-not $tp18) { $h18 += "MinimapAccess.GetClosestTransientPin not found" }
else {
    $why = Test-PinShown18 $tp18 "call MinimapAccess::ShownIconTypes" "call MinimapAccess::SharedPinsFade"
    if ($why -ne "") { $h18 += ("MinimapAccess.GetClosestTransientPin {0}" -f $why) }
}
$init18 = Get-Method18 $maT "Init" 0
$ii18 = $(if ($init18) { @($init18.Body.Instructions) } else { @() })
foreach ($hp in @(@("ShownIconTypes", "_visibleIconTypesField", "_sharedMapDataFadeField", "m_visibleIconTypes"), @("SharedPinsFade", "_sharedMapDataFadeField", "_visibleIconTypesField", "m_sharedMapDataFade"))) {
    $hm = Get-Method18 $maT $hp[0] 1
    if (-not $hm) { $h18 += ("MinimapAccess.{0} not found" -f $hp[0]); continue }
    $reads = @($hm.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldsfld" -and $_.Operand.DeclaringType.FullName -eq "Waypointer.MinimapAccess" } | ForEach-Object { $_.Operand.Name })
    if ($reads -notcontains $hp[1] -or $reads -contains $hp[2]) { $h18 += ("MinimapAccess.{0} does not read {1} alone (it reads {2})" -f $hp[0], $hp[1], ($reads -join ", ")) }
    $set = $false
    for ($k = 0; $k -lt $ii18.Count; $k++) {
        if ($ii18[$k].OpCode.Name -eq "stsfld" -and $ii18[$k].Operand.Name -eq $hp[1]) {
            for ($j = $k - 1; $j -ge [Math]::Max(0, $k - 6); $j--) { if ($ii18[$j].OpCode.Name -eq "ldstr" -and "$($ii18[$j].Operand)" -eq $hp[3]) { $set = $true } }
        }
    }
    if (-not $set) { $h18 += ("MinimapAccess.Init does not set {0} from AccessTools.Field(typeof(Minimap), '{1}')" -f $hp[1], $hp[3]) }
}
foreach ($kp in @(@("Waypointer.Minimap_RemovePin_Patch", "Prefix", "GetClosestOwnedWaypointPinEvenHidden", "GetClosestOwnedWaypointPin"), @("Waypointer.WaypointManager", "EnsurePins", "GetClosestAdoptablePinEvenHidden", "GetClosestAdoptablePin"))) {
    $kt = $plug.GetType($kp[0]); $km = $null; if ($kt) { $km = $kt.Methods | Where-Object { $_.Name -eq $kp[1] -and $_.HasBody } | Select-Object -First 1 }
    if (-not $km) { $h18 += ("{0}.{1} not found" -f $kp[0], $kp[1]); continue }
    $ki = @($km.Body.Instructions)
    $good = Find-Calls $ki "Waypointer.MinimapAccess" $kp[2]; $bad = Find-Calls $ki "Waypointer.MinimapAccess" $kp[3]
    if ($good.Count -ne 1 -or $bad.Count -ne 0) { $h18 += ("{0}.{1} calls MinimapAccess.{2} {3}x and {4} {5}x; expected once and never (pins the map hides must still count there)" -f $kp[0].Split('.')[-1], $kp[1], $kp[2], $good.Count, $kp[3], $bad.Count) }
}
foreach ($w in @(@("GetClosestOwnedWaypointPinEvenHidden", "FindClosest", 7, 5, 6), @("GetClosestAdoptablePinEvenHidden", "ClosestAdoptable", 5, 3, 4))) {
    $lm = Get-Method18 $maT $w[0] 3
    if (-not $lm) { $h18 += ("MinimapAccess.{0} not found" -f $w[0]); continue }
    $li = @($lm.Body.Instructions); $lh = $lm.Body.ExceptionHandlers
    $wc = Find-Calls $li "Waypointer.MinimapAccess" $w[1]
    $wa = $null; if ($wc.Count -eq 1) { $wa = Get-CallArgs $li $wc[0] $lh }
    $nf = $null; $nd = $null
    if ($wa -and $wa.Count -eq $w[2]) { $nf = $li[$wa[$w[3]]]; $nd = $li[$wa[$w[4]]] }
    if (-not ($nf -and $nf.OpCode.Name -eq "ldnull" -and $nd -and $nd.OpCode.Name -eq "ldc.r4" -and [single]$nd.Operand -eq [single]1)) { $h18 += ("MinimapAccess.{0} does not hand its walk null and 1 (every pin, shown or not)" -f $w[0]) }
}
$wmT = $plug.GetType("Waypointer.WaypointManager")
$tk = $null; if ($wmT) { $tk = $wmT.Methods | Where-Object { $_.Name -eq "Tick" -and $_.HasBody } | Select-Object -First 1 }
if (-not $tk) { $h18 += "WaypointManager.Tick not found" }
else {
    $ti = @($tk.Body.Instructions); $th = $tk.Body.ExceptionHandlers
    $st18 = Find-Calls $ti "Waypointer.ArrivalRules" "Step"
    $or18 = Find-Calls $ti "Waypointer.WaypointManager" "OnReached"
    if ($st18.Count -ne 1 -or $or18.Count -ne 1) { $h18 += ("WaypointManager.Tick calls ArrivalRules.Step {0}x and OnReached {1}x; expected once each" -f $st18.Count, $or18.Count) }
    else {
        if ($or18[0] -lt $st18[0]) { $h18 += "WaypointManager.Tick calls OnReached before ArrivalRules.Step" }
        $sl = Get-ResultLocal18 $ti $st18[0] "Step"
        if ($sl -eq -1) { $h18 += "ArrivalRules.Step's result is not stored in a local right after the call" }
        elseif ($sl -eq -2) { $h18 += "the local holding ArrivalRules.Step's result is also set from something else (an arrival decided around it)" }
        $sa = Get-CallArgs $ti $st18[0] $th
        if (-not $sa -or $sa.Count -ne 4) { $h18 += "ArrivalRules.Step's arguments cannot be traced" }
        else {
            $v = Resolve-Value $ti $sa[1] $th
            if ($v -notlike "*Character::IsDead") { $h18 += ("ArrivalRules.Step is not told whether the player is dead (its playerDead argument is {0}, not Character.IsDead)" -f $v) }
        }
    }
}
if ($h18.Count -eq 0) { Write-Output "  ok    a map click decides by the right pin (the four nearest pins the map shows asked, MapClickRules.Decide given each one's distance, before WaypointManager's route-changing methods), and ArrivalRules.Step is told whether the player is dead (its playerDead is Character.IsDead)" }
else { foreach ($p in $h18) { Write-Output "  FAIL  18: $p" }; $failures++ }

# 19 (1.5.2; stricter since 1.6.0): the Alt-click is the plugin's, and only the Alt-click. Minimap_OnMapLeftClick_Patch.Prefix
# reads Hotkeys.Held(Plugin.MapModifierKey) once into a local set nowhere else but to false, and never stored again after
# the call; where that local is false the straight-line code reached returns true (the click left to the game, calling
# nothing but an info log line on the way); the method has one "return !held" (ldloc; ldc.i4.0; ceq; ret); and after the
# modifier is read it sets its return value once (the not-held path's) and returns no constant. Not seen: the window's
# early return, and a throw before the modifier is read.
$checks++
$p19 = @()
$mlc19 = $plug.GetType("Waypointer.Minimap_OnMapLeftClick_Patch")
$pre19 = $null; if ($mlc19) { $pre19 = $mlc19.Methods | Where-Object { $_.Name -eq "Prefix" -and $_.HasBody } | Select-Object -First 1 }
if (-not $pre19) { $p19 += "Minimap_OnMapLeftClick_Patch.Prefix not found" }
else {
    $pi19 = @($pre19.Body.Instructions); $ph19 = $pre19.Body.ExceptionHandlers
    $hd19 = Find-Calls $pi19 "Waypointer.Hotkeys" "Held"
    if ($hd19.Count -ne 1) { $p19 += ("Prefix calls Hotkeys.Held {0}x; expected once" -f $hd19.Count) }
    else {
        $ha19 = Get-CallArgs $pi19 $hd19[0] $ph19
        if (-not $ha19 -or (Get-InstructionText $pi19[$ha19[0]]) -ne "ldsfld Plugin::MapModifierKey") { $p19 += "Prefix does not ask Hotkeys.Held about Plugin.MapModifierKey" }
        if ($pi19[$hd19[0] + 1].OpCode.Name -notlike "stloc*") { $p19 += "Hotkeys.Held's result is not kept in a local right after the call" }
        else {
            $hl19 = Get-LocalIndex $pi19[$hd19[0] + 1]
            for ($k = 0; $k -lt $pi19.Count; $k++) {
                if ($k -ne $hd19[0] + 1 -and $pi19[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $pi19[$k]) -eq $hl19 -and -not ($k -ge 1 -and $pi19[$k - 1].OpCode.Name -eq "ldc.i4.0")) { $p19 += "the local holding Hotkeys.Held's result is also set from something else" }
            }
            $br19 = -1
            for ($k = $hd19[0] + 2; $k -lt $pi19.Count - 1; $k++) { if ($pi19[$k].OpCode.Name -like "ldloc*" -and (Get-LocalIndex $pi19[$k]) -eq $hl19 -and "$($pi19[$k + 1].OpCode.FlowControl)" -eq "Cond_Branch") { $br19 = $k + 1; break } }
            if ($br19 -lt 0) { $p19 += "Prefix does not branch on whether the modifier is held (a plain click would be the plugin's)" }
            else {
                $b19 = $pi19[$br19]; $notHeld = -1
                if ($b19.OpCode.Name -like "brtrue*") { $notHeld = $br19 + 1 } elseif ($b19.OpCode.Name -like "brfalse*") { $notHeld = [array]::IndexOf($pi19, $b19.Operand) }
                $end19 = $(if ($notHeld -ge 0) { Get-PathEnd16 $pi19 $notHeld } else { "?" })
                if ($end19 -ne "ret:1") { $p19 += ("where the modifier is not held the click is not left to the game at once (the code reached ends {0})" -f $end19) }
            }
            $neg19 = 0
            for ($k = 3; $k -lt $pi19.Count; $k++) {
                if ($pi19[$k].OpCode.Name -eq "ret" -and $pi19[$k - 1].OpCode.Name -eq "ceq" -and $pi19[$k - 2].OpCode.Name -eq "ldc.i4.0" -and $pi19[$k - 3].OpCode.Name -like "ldloc*" -and (Get-LocalIndex $pi19[$k - 3]) -eq $hl19) { $neg19++ }
            }
            if ($neg19 -ne 1) { $p19 += "Prefix does not return '!held' once (with the modifier held, Valheim's own click could run)" }
            # Since 1.6.0: after the modifier is read, held is never stored again, and the prefix sets its return value only
            # once - the not-held path's "return true" - and returns no constant.
            for ($k = $hd19[0] + 2; $k -lt $pi19.Count; $k++) {
                if ($pi19[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $pi19[$k]) -eq $hl19) { $p19 += "the local holding Hotkeys.Held's result is set again after the call (with the modifier held, Valheim's own click could run)" }
            }
            $rl19 = @(); for ($k = 1; $k -lt $pi19.Count; $k++) { if ($pi19[$k].OpCode.Name -eq "ret" -and $pi19[$k - 1].OpCode.Name -like "ldloc*" -and (Get-LocalIndex $pi19[$k - 1]) -ne $hl19) { $rl19 += (Get-LocalIndex $pi19[$k - 1]) } }
            $rs19 = 0; $rc19 = 0
            for ($k = $hd19[0] + 2; $k -lt $pi19.Count; $k++) {
                if ($pi19[$k].OpCode.Name -like "stloc*" -and $rl19 -contains (Get-LocalIndex $pi19[$k])) { $rs19++ }
                if ($pi19[$k].OpCode.Name -eq "ret" -and $pi19[$k - 1].OpCode.Name -like "ldc.i4*") { $rc19++ }
            }
            if ($rs19 -ne 1 -or $rc19 -ne 0) { $p19 += ("after the modifier is read the prefix sets its return value {0}x and returns a constant {1}x; expected once (the not-held path) and never" -f $rs19, $rc19) }
        }
    }
}
if ($p19.Count -eq 0) { Write-Output "  ok    the Alt-click is the plugin's: without the modifier a click is left to the game, and Prefix returns !Hotkeys.Held(MapModifierKey)" }
else { foreach ($p in $p19) { Write-Output "  FAIL  19: $p" }; $failures++ }

Write-Output ""
Write-Output "== the plugin's own log file =="
# 20 (1.6.0): the log file is written beside LogOutput.log under the plugin's own names, and nowhere else.
# LogFile._folder is set once, in LogFile.Open, straight from BepInEx.Paths.BepInExRootPath, and _edition once, there,
# to the edition's name; LogFile.PathOf is Path.Combine(_folder, its argument); OpenWriter's one FileStream opens
# PathOf(its name argument), and only LogFile.OpenLocked calls OpenWriter, giving it _sessionName or a local set only
# from LogRules.FileName or FallbackName of _edition; _sessionName is set only from OpenWriter's name argument;
# LogRotation.Rotate is called once, from LogFile.OpenLocked, with _folder and _edition, and every path it gives
# File.Exists, Move or Delete is Path.Combine(its folder, LogRules.FileName, StageName or FallbackName of its edition);
# and the only text in those three names is ".log", "-prev.log", ".log." and "-prev.log.new" - so never LogOutput.log,
# a .cfg or a waypoints_ file. Not seen: which of the plugin's own files is opened when, or with which FileMode (the unit
# tests run the rotation on a folder of their own).
$checks++
$c20 = @()
$lf20 = $plug.GetType("Waypointer.LogFile"); $lr20 = $plug.GetType("Waypointer.LogRules"); $rot20 = $plug.GetType("Waypointer.LogRotation")
function Get-Body20($t, [string]$name) {
    if (-not $t) { return $null }
    return $t.Methods | Where-Object { $_.Name -eq $name -and $_.HasBody } | Select-Object -First 1
}
# The instructions that push a newobj's arguments (Get-CallArgs counts a constructor's "this", which newobj does not take).
function Get-NewArgs20($ins, [int]$at, $handlers) {
    $st = Get-StackBefore $ins $at $handlers
    $n = $ins[$at].Operand.Parameters.Count
    if ($st.Count -lt $n) { return $null }
    if ($n -eq 0) { return ,@() }
    return ,@($st[($st.Count - $n)..($st.Count - 1)])
}
# A path Rotate uses: Path.Combine(its folder argument, LogRules.FileName/StageName/FallbackName(its edition argument, ...)),
# directly or through a local every store of which is one.
function Test-RotPathValue20($ins, $handlers, [int]$at) {
    if ((Get-InstructionText $ins[$at]) -ne "call Path::Combine") { return $false }
    $a = Get-CallArgs $ins $at $handlers
    if (-not $a -or $a.Count -ne 2 -or $ins[$a[0]].OpCode.Name -ne "ldarg.0") { return $false }
    $nm = Get-InstructionText $ins[$a[1]]
    if ($nm -ne "call LogRules::FileName" -and $nm -ne "call LogRules::StageName" -and $nm -ne "call LogRules::FallbackName") { return $false }
    $na = Get-CallArgs $ins $a[1] $handlers
    return ($na -and $na.Count -ge 1 -and $ins[$na[0]].OpCode.Name -eq "ldarg.1")
}
function Test-RotPath20($ins, $handlers, [int]$at) {
    $v = $ins[$at]
    $li = Get-LocalIndex $v
    if ($v.OpCode.Name -notlike "ldloc*" -or $li -lt 0) { return (Test-RotPathValue20 $ins $handlers $at) }
    $n = 0
    for ($k = 0; $k -lt $ins.Count; $k++) {
        if ($ins[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $ins[$k]) -eq $li) {
            $st = Get-StackBefore $ins $k $handlers
            if ($st.Count -lt 1 -or -not (Test-RotPathValue20 $ins $handlers $st[$st.Count - 1])) { return $false }
            $n++
        }
    }
    return ($n -gt 0)
}
if (-not $lf20 -or -not $lr20 -or -not $rot20) { $c20 += "Waypointer.LogFile, LogRules or LogRotation not found" }
else {
    # Every store into LogFile's static fields: field name -> list of @(method name, index, instructions).
    $stores20 = @{}
    foreach ($m in $lf20.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = @($m.Body.Instructions)
        for ($k = 1; $k -lt $ins.Count; $k++) {
            if ($ins[$k].OpCode.Name -eq "stsfld" -and $ins[$k].Operand.DeclaringType.FullName -eq "Waypointer.LogFile") {
                $fn = $ins[$k].Operand.Name
                if (-not $stores20.ContainsKey($fn)) { $stores20[$fn] = New-Object System.Collections.ArrayList }
                [void]$stores20[$fn].Add(@($m.Name, $k, $ins))
            }
        }
    }
    $fs = $stores20["_folder"]
    if (-not $fs -or $fs.Count -ne 1 -or $fs[0][0] -ne "Open" -or (Get-InstructionText $fs[0][2][$fs[0][1] - 1]) -ne "call Paths::get_BepInExRootPath") {
        $c20 += "LogFile._folder is not set once, in Open, from BepInEx.Paths.BepInExRootPath"
    }
    $es = $stores20["_edition"]
    if (-not $es -or $es.Count -ne 1 -or $es[0][0] -ne "Open" -or $es[0][2][$es[0][1] - 1].OpCode.Name -ne "ldstr" -or "$($es[0][2][$es[0][1] - 1].Operand)" -ne $Edition) {
        $c20 += "LogFile._edition is not set once, in Open, to '$Edition'"
    }
    $ss = $stores20["_sessionName"]
    $ssOk = ($null -ne $ss -and $ss.Count -ge 1)
    if ($ssOk) { foreach ($s in $ss) { if ($s[0] -ne "OpenWriter" -or $s[2][$s[1] - 1].OpCode.Name -ne "ldarg.0") { $ssOk = $false } } }
    if (-not $ssOk) { $c20 += "LogFile._sessionName is set from something other than OpenWriter's name argument" }

    $po = Get-Body20 $lf20 "PathOf"
    $poOk = $false
    if ($po) {
        $pi = @($po.Body.Instructions)
        $pc = @(); for ($k = 0; $k -lt $pi.Count; $k++) { if ($pi[$k].Operand -is [Mono.Cecil.MethodReference]) { $pc += $k } }
        if ($pc.Count -eq 1 -and (Get-InstructionText $pi[$pc[0]]) -eq "call Path::Combine" -and $pi[$pc[0]].Operand.Parameters.Count -eq 2) {
            $a = Get-CallArgs $pi $pc[0]
            $poOk = ($a -and (Get-InstructionText $pi[$a[0]]) -eq "ldsfld LogFile::_folder" -and $pi[$a[1]].OpCode.Name -eq "ldarg.0")
        }
    }
    if (-not $poOk) { $c20 += "LogFile.PathOf is not Path.Combine(_folder, its argument)" }

    $ow = Get-Body20 $lf20 "OpenWriter"
    if (-not $ow) { $c20 += "LogFile.OpenWriter not found" }
    else {
        $wi = @($ow.Body.Instructions); $wh = $ow.Body.ExceptionHandlers
        $fsAt = @(); for ($k = 0; $k -lt $wi.Count; $k++) { if ($wi[$k].OpCode.Name -eq "newobj" -and $wi[$k].Operand.DeclaringType.FullName -eq "System.IO.FileStream") { $fsAt += $k } }
        $owOk = $false
        if ($fsAt.Count -eq 1) {
            $a = Get-NewArgs20 $wi $fsAt[0] $wh
            if ($a -and $a.Count -ge 1 -and (Get-InstructionText $wi[$a[0]]) -eq "call LogFile::PathOf") {
                $pa = Get-CallArgs $wi $a[0] $wh
                $owOk = ($pa -and $wi[$pa[0]].OpCode.Name -eq "ldarg.0")
            }
        }
        if (-not $owOk) { $c20 += "LogFile.OpenWriter's FileStream does not open PathOf(its name argument)" }
    }

    # Who calls OpenWriter and LogRotation.Rotate, and with what.
    $owCallers = @(); $rotCalls = New-Object System.Collections.ArrayList
    foreach ($t in $plug.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            $ins = @($m.Body.Instructions)
            for ($k = 0; $k -lt $ins.Count; $k++) {
                $o = $ins[$k].Operand
                if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
                if ($o.DeclaringType.FullName -eq "Waypointer.LogFile" -and $o.Name -eq "OpenWriter") { $owCallers += ("{0}.{1}" -f $t.Name, $m.Name) }
                if ($o.DeclaringType.FullName -eq "Waypointer.LogRotation" -and $o.Name -eq "Rotate") { [void]$rotCalls.Add(@(("{0}.{1}" -f $t.Name, $m.Name), $k, $ins, $m.Body.ExceptionHandlers)) }
            }
        }
    }
    if (@($owCallers | Where-Object { $_ -ne "LogFile.OpenLocked" }).Count -gt 0 -or $owCallers.Count -eq 0) {
        $c20 += ("LogFile.OpenWriter is called from {0}; expected LogFile.OpenLocked only" -f (($owCallers | Select-Object -Unique) -join ", "))
    }
    $ol = Get-Body20 $lf20 "OpenLocked"
    if ($ol) {
        $oi = @($ol.Body.Instructions); $oh = $ol.Body.ExceptionHandlers
        foreach ($c in (Find-Calls $oi "Waypointer.LogFile" "OpenWriter")) {
            $a = Get-CallArgs $oi $c $oh
            if (-not $a) { $c20 += "a name OpenLocked gives OpenWriter cannot be traced"; continue }
            $v = $oi[$a[0]]
            if ((Get-InstructionText $v) -eq "ldsfld LogFile::_sessionName") { continue }
            $li = Get-LocalIndex $v
            $nOk = ($v.OpCode.Name -like "ldloc*" -and $li -ge 0)
            $nStores = 0
            if ($nOk) {
                for ($k = 0; $k -lt $oi.Count; $k++) {
                    if ($oi[$k].OpCode.Name -like "stloc*" -and (Get-LocalIndex $oi[$k]) -eq $li) {
                        $nStores++
                        $st = Get-StackBefore $oi $k $oh
                        if ($st.Count -lt 1) { $nOk = $false; continue }
                        $src = $st[$st.Count - 1]
                        $txt = Get-InstructionText $oi[$src]
                        if ($txt -ne "call LogRules::FileName" -and $txt -ne "call LogRules::FallbackName") { $nOk = $false; continue }
                        $ea = Get-CallArgs $oi $src $oh
                        if (-not $ea -or (Get-InstructionText $oi[$ea[0]]) -ne "ldsfld LogFile::_edition") { $nOk = $false }
                    }
                }
            }
            if (-not $nOk -or $nStores -eq 0) { $c20 += "OpenLocked gives OpenWriter a name that is not _sessionName or LogRules.FileName/FallbackName of _edition" }
        }
    }
    if ($rotCalls.Count -ne 1 -or $rotCalls[0][0] -ne "LogFile.OpenLocked") { $c20 += ("LogRotation.Rotate is called {0}x; expected once, from LogFile.OpenLocked" -f $rotCalls.Count) }
    else {
        $a = Get-CallArgs $rotCalls[0][2] $rotCalls[0][1] $rotCalls[0][3]
        if (-not $a -or (Get-InstructionText $rotCalls[0][2][$a[0]]) -ne "ldsfld LogFile::_folder" -or (Get-InstructionText $rotCalls[0][2][$a[1]]) -ne "ldsfld LogFile::_edition") {
            $c20 += "LogFile.OpenLocked does not give LogRotation.Rotate _folder and _edition"
        }
    }
    $rm = Get-Body20 $rot20 "Rotate"
    if (-not $rm) { $c20 += "LogRotation.Rotate not found" }
    else {
        $ri = @($rm.Body.Instructions); $rh = $rm.Body.ExceptionHandlers
        $fileCalls = 0
        for ($k = 0; $k -lt $ri.Count; $k++) {
            $o = $ri[$k].Operand
            if (-not ($o -is [Mono.Cecil.MethodReference]) -or $o.DeclaringType.FullName -ne "System.IO.File") { continue }
            $fileCalls++
            if ($o.Name -ne "Exists" -and $o.Name -ne "Move" -and $o.Name -ne "Delete") { $c20 += ("LogRotation.Rotate calls File.{0}" -f $o.Name); continue }
            $a = Get-CallArgs $ri $k $rh
            if (-not $a) { $c20 += ("a path LogRotation.Rotate gives File.{0} cannot be traced" -f $o.Name); continue }
            foreach ($x in $a) { if (-not (Test-RotPath20 $ri $rh $x)) { $c20 += ("LogRotation.Rotate gives File.{0} a path that is not Path.Combine(folder, one of LogRules' names for its edition)" -f $o.Name) } }
        }
        if ($fileCalls -eq 0) { $c20 += "LogRotation.Rotate calls no File method (nothing to check)" }
    }
    foreach ($pair in @(@("FileName", ".log|-prev.log"), @("FallbackName", ".log."), @("StageName", "-prev.log.new"))) {
        $nm = Get-Body20 $lr20 $pair[0]
        if (-not $nm) { $c20 += ("LogRules.{0} not found" -f $pair[0]); continue }
        $lits = @(); foreach ($i in $nm.Body.Instructions) { if ($i.OpCode.Name -eq "ldstr") { $lits += "$($i.Operand)" } }
        $got = (@($lits | Sort-Object -Unique) -join "|"); $want = (@($pair[1] -split '\|' | Sort-Object -Unique) -join "|")
        if ($got -ne $want) { $c20 += ("LogRules.{0} holds the text '{1}', expected '{2}'" -f $pair[0], $got, $want) }
    }
}
if ($c20.Count -eq 0) { Write-Output "  ok    the log file is written beside LogOutput.log under the plugin's own names only (BepInEx root, LogRules' .log names of this edition, the rotation inside them)" }
else { foreach ($p in $c20) { Write-Output "  FAIL  20: $p" }; $failures++ }

# 21 (1.6.0): the log file cannot hurt the game. Every LogFile method the rest of the plugin calls (Open, Write,
# SetLevel, NoteQuit, Close, EmitParkedFailure), its hooks (each method a delegate is made of), Fail, LogRotation.Rotate
# and every Diag method run wholly inside a catch (System.Exception) that does not rethrow and whose handler calls
# nothing but LogFile.Fail; every [Conditional("TOMTOM")] trace helper makes its calls inside such a try; OnOwnEvent
# asks LogRules.Admits once and returns at once when it says no; the log file's code (LogFile, LogRules, LogRotation,
# LineBudget, RepeatCollapse and Diag) calls no other code of the plugin (Diag may read a name through GameText and a
# Waypoint's getters), and of BepInEx's logging only its own log event's add and remove, a log event's level and data,
# and one ManualLogSource call - EmitParkedFailure's LogWarning; nothing calls ManualLogSource.LogDebug or Log(LogLevel,
# ...); the hooks are added only in LogFile.Open and removed only in LogFile.Close, nothing adds a listener or a source
# to BepInEx's Logger (Listeners, Sources, CreateLogSource), and nothing hooks Unity's threaded log event; the code that
# runs under the log file's lock - every region between Monitor.Enter (or TryEnter) and its Monitor.Exit in a LogFile
# method, nested ones too, LogFile's *Locked methods, OpenWriter, Park, OnFlushTimer and LogRotation.Rotate, and every
# method of the log file's code they call by name (LogFile, LogRules, LogRotation, LineBudget, RepeatCollapse; each
# overload), followed to the end - names (calls, constructs or takes the address of) no method outside .NET's System
# types and the log file's own, and nothing named Invoke, BeginInvoke, DynamicInvoke or InvokeMember; no log type has a
# virtual method; the static initialisers of LogFile, LogRules, LogRotation, LineBudget and RepeatCollapse only make
# objects, delegates and arrays, and Diag has none; a Timer is made only in StartTimerLocked and never disposed with a
# WaitHandle; Update starts with LogFile.EmitParkedFailure, OnApplicationQuit calls NoteQuit, OnDestroy calls
# LogFile.Close on both of its paths, and Awake opens the log before it binds the other settings; ErrorLog's default is
# true and TomTom's VerboseLog's false; LogRules' level numbers are BepInEx's LogLevel values; and Diag.Trace carries
# [Conditional("TOMTOM")]. Not seen: code run on the lock's behalf rather than named there (a delegate handed to a
# library method, a call through reflection other than Invoke/InvokeMember), a lock released early on one path or
# taken and released in two helpers (the rest of its region is not read), a type of the plugin's own declared in a
# System namespace, what a caught failure leaves behind (the file is closed; a live session shows it), and the order
# of the steps under the lock.
$checks++
$c21 = @()
$diag21 = $plug.GetType("Waypointer.Diag")
function Test-Caught21($m) {
    # "" when every instruction of $m is in the try or the handler of a catch (System.Exception) that does not rethrow and
    # whose handler calls nothing but LogFile.Fail - except a closing ret, or ldloc; ret.
    $ins = @($m.Body.Instructions)
    $hs = @($m.Body.ExceptionHandlers | Where-Object { $_.HandlerType -eq [Mono.Cecil.Cil.ExceptionHandlerType]::Catch -and $_.CatchType.FullName -eq "System.Exception" })
    if ($hs.Count -eq 0) { return "has no catch (System.Exception)" }
    for ($k = 0; $k -lt $ins.Count; $k++) {
        $i = $ins[$k]
        $covered = $false
        foreach ($h in $hs) {
            $tEnd = [int]::MaxValue; if ($h.TryEnd) { $tEnd = $h.TryEnd.Offset }
            $hEnd = [int]::MaxValue; if ($h.HandlerEnd) { $hEnd = $h.HandlerEnd.Offset }
            if ($i.Offset -ge $h.TryStart.Offset -and $i.Offset -lt $tEnd) { $covered = $true; break }
            if ($i.Offset -ge $h.HandlerStart.Offset -and $i.Offset -lt $hEnd) {
                if ($i.OpCode.Name -eq "throw" -or $i.OpCode.Name -eq "rethrow") { return "rethrows from its catch" }
                if ($i.Operand -is [Mono.Cecil.MethodReference] -and -not ($i.Operand.DeclaringType.FullName -eq "Waypointer.LogFile" -and $i.Operand.Name -eq "Fail")) { return ("its catch calls {0}" -f (Get-InstructionText $i)) }
                $covered = $true; break
            }
        }
        if ($covered) { continue }
        $tail = (($k -eq $ins.Count - 1 -and $i.OpCode.Name -eq "ret") -or ($k -eq $ins.Count - 2 -and $i.OpCode.Name -like "ldloc*" -and $ins[$k + 1].OpCode.Name -eq "ret"))
        if (-not $tail) { return ("{0} at IL_{1:x4} is outside its try" -f (Get-InstructionText $i), $i.Offset) }
    }
    return ""
}
if (-not $lf20 -or -not $lr20 -or -not $rot20 -or -not $diag21) { $c21 += "Waypointer.LogFile, LogRules, LogRotation or Diag not found" }
else {
    $guarded = @("Open", "Write", "SetLevel", "NoteQuit", "Close", "EmitParkedFailure", "Fail")
    $logFamily21 = @("Waypointer.LogFile", "Waypointer.LogRules", "Waypointer.LogRotation", "Waypointer.LineBudget", "Waypointer.RepeatCollapse", "Waypointer.Diag", "Waypointer.LogDetail", "Waypointer.LineDecision", "Waypointer.LogRotation/Outcome")
    $okLogging21 = @("BepInEx.Logging.ManualLogSource::add_LogEvent", "BepInEx.Logging.ManualLogSource::remove_LogEvent", "BepInEx.Logging.ManualLogSource::LogWarning", "BepInEx.Logging.LogEventArgs::get_Level", "BepInEx.Logging.LogEventArgs::get_Data")
    $hooks21 = @(); $outsideCalls21 = @()
    $logCalls21 = @(); $hookSites21 = @(); $timerSites21 = @()
    $condCalls21 = 0
    foreach ($t in $plug.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            $where = "{0}.{1}" -f $t.Name, $m.Name
            $isCond = $false
            foreach ($ca in $m.CustomAttributes) { if ($ca.AttributeType.FullName -eq "System.Diagnostics.ConditionalAttribute" -and "$($ca.ConstructorArguments[0].Value)" -eq "TOMTOM") { $isCond = $true } }
            $ins = @($m.Body.Instructions)
            for ($k = 0; $k -lt $ins.Count; $k++) {
                $i = $ins[$k]; $o = $i.Operand
                if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
                $dt = $o.DeclaringType.FullName
                if ($dt -eq "Waypointer.LogFile" -and $i.OpCode.Name -eq "ldftn") { $hooks21 += $o.Name }
                if ($dt -eq "Waypointer.LogFile" -and $t.FullName -ne "Waypointer.LogFile" -and $i.OpCode.Name -like "call*") { $outsideCalls21 += $o.Name }
                if ($dt -eq "BepInEx.Logging.ManualLogSource" -and $o.Name -match '^Log') {
                    if ($o.Name -eq "LogDebug" -or $o.Name -eq "Log") { $c21 += ("{0} calls ManualLogSource.{1}" -f $where, $o.Name) }
                    if ($logFamily21 -contains $t.FullName) { $logCalls21 += ("{0}:{1}" -f $where, $o.Name) }
                }
                $hookName = "{0}::{1}" -f $dt, $o.Name
                if (@("UnityEngine.Application::add_logMessageReceived", "UnityEngine.Application::remove_logMessageReceived", "BepInEx.Logging.ManualLogSource::add_LogEvent", "BepInEx.Logging.ManualLogSource::remove_LogEvent") -contains $hookName) { $hookSites21 += ("{0}|{1}" -f $hookName, $where) }
                if ($hookName -eq "UnityEngine.Application::add_logMessageReceivedThreaded" -or $hookName -eq "BepInEx.Logging.Logger::get_Listeners" -or $hookName -eq "BepInEx.Logging.Logger::get_Sources" -or $hookName -eq "BepInEx.Logging.Logger::CreateLogSource") { $c21 += ("{0} calls {1}" -f $where, $hookName) }
                # The log file's own code: no other plugin code, and of BepInEx's logging only what it needs.
                if ($logFamily21 -contains $t.FullName) {
                    if ($dt -like "Waypointer.*" -and $logFamily21 -notcontains $o.DeclaringType.GetElementType().FullName -and -not ($t.FullName -eq "Waypointer.Diag" -and ($dt -eq "Waypointer.GameText" -or ($dt -eq "Waypointer.Waypoint" -and $o.Name -like "get_*")))) { $c21 += ("{0} calls {1} - the log file's code calls no other code of the plugin" -f $where, (Get-InstructionText $i)) }
                    if ($dt -like "BepInEx.Logging.*" -and $okLogging21 -notcontains $hookName) { $c21 += ("{0} calls {1}" -f $where, $hookName) }
                }
                if ($dt -eq "System.Threading.Timer" -and $o.Name -eq "Dispose" -and $o.Parameters.Count -gt 0) { $c21 += ("{0} disposes a Timer with a WaitHandle" -f $where) }
                if ($dt -eq "System.Threading.Timer" -and $o.Name -eq ".ctor") { $timerSites21 += $where }
                if ($isCond -and -not ($t.FullName -eq "Waypointer.Diag" -and $m.Name -eq "Trace") -and $i.OpCode.Name -ne "ldftn") {
                    $condCalls21++
                    if ((Get-CatchTries $m $i).Count -eq 0) { $c21 += ("the trace helper {0} calls {1} outside a try that catches it" -f $where, (Get-InstructionText $i)) }
                }
            }
        }
    }
    foreach ($n in @($outsideCalls21 | Select-Object -Unique)) { if ($guarded -notcontains $n) { $c21 += ("the plugin calls LogFile.{0}, which is not one of the guarded entry points" -f $n) } }
    $toTest = @($guarded + $hooks21 | Select-Object -Unique)
    if ($hooks21.Count -lt 3) { $c21 += ("LogFile makes {0} delegates of its own methods; expected its three hooks" -f $hooks21.Count) }
    foreach ($n in $toTest) {
        $m = Get-Body20 $lf20 $n
        if (-not $m) { $c21 += "LogFile.$n not found"; continue }
        $why = Test-Caught21 $m
        if ($why -ne "") { $c21 += "LogFile.${n}: $why" }
    }
    $rm = Get-Body20 $rot20 "Rotate"
    if ($rm) { $why = Test-Caught21 $rm; if ($why -ne "") { $c21 += "LogRotation.Rotate: $why" } }
    foreach ($m in $diag21.Methods) {
        if (-not $m.HasBody) { continue }
        if ($m.Name -eq ".cctor") { $c21 += "Diag has a static initialiser"; continue }
        $why = Test-Caught21 $m
        if ($why -ne "") { $c21 += ("Diag.{0}: {1}" -f $m.Name, $why) }
    }
    if ($logCalls21.Count -ne 1 -or $logCalls21[0] -ne "LogFile.EmitParkedFailure:LogWarning") { $c21 += ("the log file calls BepInEx's logging at {0}; expected once, EmitParkedFailure's LogWarning" -f ($logCalls21 -join ", ")) }
    $wantHooks = @("UnityEngine.Application::add_logMessageReceived|LogFile.Open", "BepInEx.Logging.ManualLogSource::add_LogEvent|LogFile.Open", "UnityEngine.Application::remove_logMessageReceived|LogFile.Close", "BepInEx.Logging.ManualLogSource::remove_LogEvent|LogFile.Close")
    $gotHooks = @($hookSites21 | Sort-Object -Unique)
    if (($gotHooks -join ",") -ne (@($wantHooks | Sort-Object) -join ",") -or $hookSites21.Count -ne 4) { $c21 += ("the log hooks are added or removed at {0}; expected each once, added in LogFile.Open and removed in LogFile.Close" -f ($hookSites21 -join ", ")) }
    if ($timerSites21.Count -ne 1 -or $timerSites21[0] -ne "LogFile.StartTimerLocked") { $c21 += ("a Timer is made in {0}; expected LogFile.StartTimerLocked only" -f ($timerSites21 -join ", ")) }

    # OnOwnEvent: Admits, then straight out when it says no.
    $oe = Get-Body20 $lf20 "OnOwnEvent"
    $gateOk = $false
    if ($oe) {
        $ei = @($oe.Body.Instructions)
        $ad = Find-Calls $ei "Waypointer.LogRules" "Admits"
        $wc = Find-Calls $ei "Waypointer.LogFile" "WriteCore"
        if ($ad.Count -eq 1 -and $wc.Count -eq 1 -and $ad[0] + 2 -lt $ei.Count -and $ei[$ad[0] + 1].OpCode.Name -like "brtrue*" -and ($ei[$ad[0] + 2].OpCode.Name -like "leave*" -or $ei[$ad[0] + 2].OpCode.Name -eq "ret") -and $wc[0] -gt $ad[0] + 2) {
            $target = [array]::IndexOf($ei, $ei[$ad[0] + 1].Operand)
            $gateOk = ($target -gt $ad[0] + 2 -and $target -le $wc[0])
        }
    }
    if (-not $gateOk) { $c21 += "LogFile.OnOwnEvent does not return at once when LogRules.Admits says a line is not wanted" }

    # Under the lock: only .NET's System types and the log file's own code (an allow-list). The roots are every region
    # between Monitor.Enter (or TryEnter) and its Monitor.Exit in a LogFile method, nested ones too, and the methods
    # named to run under the lock; every LogFile or LogRotation method they call is followed, to the end.
    $lockedOk = @("Waypointer.LogFile", "Waypointer.LogRules", "Waypointer.LogRotation", "Waypointer.LineBudget", "Waypointer.RepeatCollapse")
    $work21 = New-Object System.Collections.ArrayList     # @(label, method, first index, end index)
    $regions21 = 0
    foreach ($m in $lf20.Methods) {
        if (-not $m.HasBody) { continue }
        $mi = @($m.Body.Instructions)
        if ($m.Name -like "*Locked" -or @("OpenWriter", "Park", "OnFlushTimer") -contains $m.Name) { [void]$work21.Add(@(("LogFile." + $m.Name), $m, 0, $mi.Count)) }
        $enters = New-Object System.Collections.ArrayList   # nested locks: every Enter or TryEnter until its Exit
        for ($k = 0; $k -lt $mi.Count; $k++) {
            $o = $mi[$k].Operand
            if (-not ($o -is [Mono.Cecil.MethodReference]) -or $o.DeclaringType.FullName -ne "System.Threading.Monitor") { continue }
            if ($o.Name -eq "Enter" -or $o.Name -eq "TryEnter") { [void]$enters.Add($k) }
            elseif ($o.Name -eq "Exit" -and $enters.Count -gt 0) {
                $from = $enters[$enters.Count - 1]
                $enters.RemoveAt($enters.Count - 1)
                [void]$work21.Add(@(("LogFile." + $m.Name + " (inside lock)"), $m, $from, $k)); $regions21++
            }
        }
    }
    if ($rm) { [void]$work21.Add(@("LogRotation.Rotate", $rm, 0, @($rm.Body.Instructions).Count)) }
    if ($regions21 -lt 7) { $c21 += ("only {0} lock regions found in LogFile; expected one in each entry point at least" -f $regions21) }
    $seen21 = @{}
    for ($w = 0; $w -lt $work21.Count; $w++) {
        $u = $work21[$w]
        $ui = @($u[1].Body.Instructions)
        for ($k = $u[2]; $k -lt $u[3]; $k++) {
            $o = $ui[$k].Operand
            if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
            $dt = $o.DeclaringType.GetElementType().FullName
            $mine = @($lockedOk | Where-Object { $dt -eq $_ -or $dt -like ($_ + "/*") }).Count -gt 0
            if (-not ($dt -like "System.*" -or $mine)) { $c21 += ("{0}, which runs under the log file's lock, calls {1} - only .NET's System types and the log file's own may be named there" -f $u[0], (Get-InstructionText $ui[$k])) }
            if (@("Invoke", "BeginInvoke", "DynamicInvoke", "InvokeMember") -contains $o.Name) { $c21 += ("{0}, which runs under the log file's lock, invokes {1} (code of someone else's choosing)" -f $u[0], (Get-InstructionText $ui[$k])) }
            if ($lockedOk -contains $dt) {
                $key = $o.FullName
                if ($seen21.ContainsKey($key)) { continue }
                $seen21[$key] = $true
                $callee = $null
                try { $callee = $o.Resolve() } catch { }
                if ($callee -and $callee.HasBody) { [void]$work21.Add(@(($o.DeclaringType.Name + "." + $o.Name + " (called under the lock)"), $callee, 0, @($callee.Body.Instructions).Count)) }
            }
        }
    }

    # Static initialisers: objects, delegates and arrays only.
    $okNew = @("System.Object", "System.Text.UTF8Encoding", "System.Threading.TimerCallback", "System.EventHandler``1", "UnityEngine.Application/LogCallback")
    $lb21 = $plug.GetType("Waypointer.LineBudget"); $rc21 = $plug.GetType("Waypointer.RepeatCollapse")
    foreach ($t in @($lf20, $lr20, $rot20, $lb21, $rc21)) {
        if (-not $t) { continue }
        foreach ($m in $t.Methods) { if ($m.IsVirtual) { $c21 += ("{0}.{1} is virtual: code named Object::{1} under the lock could run it" -f $t.Name, $m.Name) } }
    }
    foreach ($t in @($lf20, $lr20, $rot20, $lb21, $rc21)) {
        if (-not $t) { continue }
        foreach ($m in $t.Methods) {
            if ($m.Name -ne ".cctor" -or -not $m.HasBody) { continue }
            foreach ($i in $m.Body.Instructions) {
                $o = $i.Operand
                if (-not ($o -is [Mono.Cecil.MethodReference])) { continue }
                $dt = $o.DeclaringType.GetElementType().FullName
                $fine = ($i.OpCode.Name -eq "ldftn" -and $dt -eq $t.FullName) -or ($i.OpCode.Name -eq "newobj" -and $okNew -contains $dt) -or ($dt -eq "System.Runtime.CompilerServices.RuntimeHelpers" -and $o.Name -eq "InitializeArray")
                if (-not $fine) { $c21 += ("{0}'s static initialiser calls {1}" -f $t.Name, (Get-InstructionText $i)) }
            }
        }
    }

    # The plugin's own wiring.
    $up = Get-Body20 $pluginType "Update"
    if (-not $up -or (Get-InstructionText @($up.Body.Instructions)[0]) -ne "call LogFile::EmitParkedFailure") { $c21 += "Plugin.Update does not start with LogFile.EmitParkedFailure" }
    $oq = Get-Body20 $pluginType "OnApplicationQuit"
    if (-not $oq -or (Find-Calls @($oq.Body.Instructions) "Waypointer.LogFile" "NoteQuit").Count -ne 1) { $c21 += "Plugin.OnApplicationQuit does not call LogFile.NoteQuit" }
    $od = Get-Body20 $pluginType "OnDestroy"
    if (-not $od -or (Find-Calls @($od.Body.Instructions) "Waypointer.LogFile" "Close").Count -ne 2) { $c21 += "Plugin.OnDestroy does not call LogFile.Close on both of its paths" }
    $aw = Get-Body20 $pluginType "Awake"
    $openAt = @(); $bindAt = @()
    if ($aw) { $ai = @($aw.Body.Instructions); $openAt = Find-Calls $ai "Waypointer.LogFile" "Open"; $bindAt = Find-Calls $ai "Waypointer.Plugin" "BindServerConfig" }
    if ($openAt.Count -ne 1 -or $bindAt.Count -ne 1 -or $openAt[0] -gt $bindAt[0]) { $c21 += "Plugin.Awake does not open the log file once, before it binds the other settings" }

    # Defaults, level numbers, the trace's attribute.
    $defaults = @{}
    foreach ($m in $pluginType.Methods) {
        if (-not $m.HasBody) { continue }
        $bi = @($m.Body.Instructions)
        for ($k = 0; $k -lt $bi.Count; $k++) {
            $o = $bi[$k].Operand
            if (-not ($o -is [Mono.Cecil.MethodReference]) -or $o.DeclaringType.FullName -ne "BepInEx.Configuration.ConfigFile" -or $o.Name -ne "Bind") { continue }
            $a = Get-CallArgs $bi $k $m.Body.ExceptionHandlers
            if (-not $a -or $a.Count -lt 4 -or $bi[$a[2]].OpCode.Name -ne "ldstr") { continue }
            $key = "$($bi[$a[2]].Operand)"
            if ($key -eq "ErrorLog" -or $key -eq "VerboseLog") { $defaults[$key] = $bi[$a[3]].OpCode.Name }
        }
    }
    if ($defaults["ErrorLog"] -ne "ldc.i4.1") { $c21 += ("ErrorLog's default is '{0}', expected true" -f $defaults["ErrorLog"]) }
    if ($Edition -eq "TomTom" -and $defaults["VerboseLog"] -ne "ldc.i4.0") { $c21 += ("VerboseLog's default is '{0}', expected false" -f $defaults["VerboseLog"]) }
    $ll21 = Find-GameType "BepInEx.Logging.LogLevel"
    if (-not $ll21) { $c21 += "BepInEx.Logging.LogLevel not found" }
    else {
        foreach ($n in @("Fatal", "Error", "Warning", "Message", "Info", "Debug", "All")) {
            $f = $lr20.Fields | Where-Object { $_.Name -eq $n -and $_.HasConstant } | Select-Object -First 1
            $g = $ll21.Fields | Where-Object { $_.Name -eq $n -and $_.HasConstant } | Select-Object -First 1
            if (-not $f -or -not $g -or [int]$f.Constant -ne [int]$g.Constant) { $c21 += ("LogRules.{0} is not BepInEx's LogLevel.{0}" -f $n) }
        }
    }
    $tr = Get-Body20 $diag21 "Trace"
    $hasCond = $false
    if ($tr) { foreach ($ca in $tr.CustomAttributes) { if ($ca.AttributeType.FullName -eq "System.Diagnostics.ConditionalAttribute" -and "$($ca.ConstructorArguments[0].Value)" -eq "TOMTOM") { $hasCond = $true } } }
    if (-not $hasCond) { $c21 += "Diag.Trace does not carry [Conditional(""TOMTOM"")]" }
}
if ($c21.Count -eq 0) { Write-Output "  ok    the log file cannot hurt the game (guarded entry points and trace helpers, the level gate, one log call of its own, its hooks, a leaf lock, plain initialisers, the plugin's wiring, its defaults)" }
else { foreach ($p in $c21) { Write-Output "  FAIL  21: $p" }; $failures++ }

# 22 (1.6.0): Wayfinder has no verbose log, and no trace sits in code the checks above read by its exact shape. In
# Wayfinder: LogDetail is exactly Off = 0 and Errors = 1, LogRules.IsVerbose returns false, nothing binds a VerboseLog
# setting, and no method outside Diag calls Diag or a [Conditional("TOMTOM")] method (its compiler dropped every trace).
# In TomTom: LogDetail also has Verbose = 2, VerboseLog is bound, and Diag.Trace and the trace helpers are called (so
# TOMTOM was defined for its build). In both: no call to Diag, LogFile or a [Conditional("TOMTOM")] method, and no
# read of Diag.On, in Minimap_OnMapLeftClick_Patch.Prefix, in HandleWaypointClick before MapClickRules.Decide, in
# WaypointManager.CreateLocalOnlyPin, in SafeFile, in LocationSearch.Ask or AnswersIntercepted, in the answer and
# WhoMayFind patches (Game_RPC_DiscoverLocationResponse_Patch, Game_RPC_DiscoverClosestLocation_Patch,
# ZRoutedRpc_RPC_RoutedRPC_Patch), in FindServer.CallerMayFind, Allowed, IsAdmin or CallingPeer, or in FindLink.SendFind
# or OnToClient. Not seen: what a trace's text holds.
$checks++
$c22 = @()
$cond22 = @{}
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        foreach ($ca in $m.CustomAttributes) {
            if ($ca.AttributeType.FullName -eq "System.Diagnostics.ConditionalAttribute" -and "$($ca.ConstructorArguments[0].Value)" -eq "TOMTOM") { $cond22[$t.FullName + "::" + $m.Name] = $true }
        }
    }
}
function Test-Trace22($i, [bool]$withOn) {
    $o = $i.Operand
    if ($o -is [Mono.Cecil.MethodReference]) {
        $dt = $o.DeclaringType.FullName
        if ($dt -eq "Waypointer.Diag") { return $true }
        if ($cond22.ContainsKey($dt + "::" + $o.Name)) { return $true }
    }
    if ($withOn -and $o -is [Mono.Cecil.FieldReference] -and $o.DeclaringType.FullName -eq "Waypointer.Diag" -and $i.OpCode.Name -like "ldsfld*") { return $true }
    return $false
}
$traceCalls22 = 0; $helperCalls22 = 0
foreach ($t in $plug.GetTypes()) {
    if ($t.FullName -eq "Waypointer.Diag") { continue }
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            if (-not (Test-Trace22 $i $false)) { continue }
            if ($i.Operand.DeclaringType.FullName -eq "Waypointer.Diag") { $traceCalls22++ } else { $helperCalls22++ }
        }
    }
}
$ld22 = $plug.GetType("Waypointer.LogDetail")
$members22 = @(); if ($ld22) { foreach ($f in $ld22.Fields) { if ($f.HasConstant) { $members22 += ("{0}={1}" -f $f.Name, $f.Constant) } } }
$verboseBinds22 = 0
foreach ($m in $pluginType.Methods) {
    if (-not $m.HasBody) { continue }
    $bi = @($m.Body.Instructions)
    for ($k = 0; $k -lt $bi.Count; $k++) {
        $o = $bi[$k].Operand
        if (-not ($o -is [Mono.Cecil.MethodReference]) -or $o.DeclaringType.FullName -ne "BepInEx.Configuration.ConfigFile" -or $o.Name -ne "Bind") { continue }
        $a = Get-CallArgs $bi $k $m.Body.ExceptionHandlers
        if ($a -and $a.Count -ge 3 -and $bi[$a[2]].OpCode.Name -eq "ldstr" -and "$($bi[$a[2]].Operand)" -eq "VerboseLog") { $verboseBinds22++ }
    }
}
if ($Edition -eq "Wayfinder") {
    if ((@($members22 | Sort-Object) -join ",") -ne "Errors=1,Off=0") { $c22 += ("LogDetail is {0}; expected exactly Off=0 and Errors=1" -f ($members22 -join ", ")) }
    $iv = $null; if ($lr20) { $iv = Get-Body20 $lr20 "IsVerbose" }
    $ivIns = @(); if ($iv) { $ivIns = @($iv.Body.Instructions) }
    if ($ivIns.Count -ne 2 -or $ivIns[0].OpCode.Name -ne "ldc.i4.0" -or $ivIns[1].OpCode.Name -ne "ret") { $c22 += "LogRules.IsVerbose does not return false outright" }
    if ($verboseBinds22 -ne 0) { $c22 += "a VerboseLog setting is bound" }
    if ($traceCalls22 + $helperCalls22 -ne 0) { $c22 += ("{0} calls to Diag and {1} to trace helpers are left (the verbose log is TomTom's only)" -f $traceCalls22, $helperCalls22) }
} else {
    if ((@($members22 | Sort-Object) -join ",") -ne "Errors=1,Off=0,Verbose=2") { $c22 += ("LogDetail is {0}; expected Off=0, Errors=1 and Verbose=2" -f ($members22 -join ", ")) }
    if ($verboseBinds22 -ne 1) { $c22 += ("VerboseLog is bound {0}x, expected once" -f $verboseBinds22) }
    if ($traceCalls22 -eq 0 -or $helperCalls22 -eq 0) { $c22 += ("Diag.Trace is called {0}x and the trace helpers {1}x - TOMTOM was not defined for this build" -f $traceCalls22, $helperCalls22) }
}
# The exclusion zones: type, method ("*" = every method), and whether only the part before MapClickRules.Decide counts.
$zones22 = @(
    @("Waypointer.Minimap_OnMapLeftClick_Patch", "Prefix", $false), @("Waypointer.Minimap_OnMapLeftClick_Patch", "HandleWaypointClick", $true),
    @("Waypointer.WaypointManager", "CreateLocalOnlyPin", $false), @("Waypointer.SafeFile", "*", $false),
    @("Waypointer.LocationSearch", "Ask", $false), @("Waypointer.LocationSearch", "AnswersIntercepted", $false),
    @("Waypointer.Game_RPC_DiscoverLocationResponse_Patch", "*", $false), @("Waypointer.Game_RPC_DiscoverClosestLocation_Patch", "*", $false),
    @("Waypointer.ZRoutedRpc_RPC_RoutedRPC_Patch", "*", $false), @("Waypointer.FindServer", "CallerMayFind", $false),
    @("Waypointer.FindServer", "Allowed", $false), @("Waypointer.FindServer", "IsAdmin", $false), @("Waypointer.FindServer", "CallingPeer", $false),
    @("Waypointer.FindLink", "SendFind", $false), @("Waypointer.FindLink", "OnToClient", $false))
foreach ($z in $zones22) {
    $zt = $plug.GetType($z[0])
    $ms = @(); if ($zt) { $ms = @($zt.Methods | Where-Object { $_.HasBody -and ($z[1] -eq "*" -or $_.Name -eq $z[1]) }) }
    if ($ms.Count -eq 0) { $c22 += ("{0}.{1} not found (a trace exclusion zone)" -f $z[0], $z[1]); continue }
    foreach ($m in $ms) {
        $zi = @($m.Body.Instructions)
        $end = $zi.Count
        if ($z[2]) {
            $dc = Find-Calls $zi "Waypointer.MapClickRules" "Decide"
            if ($dc.Count -ne 1) { $c22 += ("{0}.{1} does not call MapClickRules.Decide once" -f $z[0], $m.Name); continue }
            $end = $dc[0]
        }
        for ($k = 0; $k -lt $end; $k++) {
            $zo = $zi[$k].Operand
            $direct = ($zo -is [Mono.Cecil.MethodReference] -and $zo.DeclaringType.FullName -eq "Waypointer.LogFile")
            if ($direct -or (Test-Trace22 $zi[$k] $true)) { $c22 += ("{0}.{1} traces ({2}) where checks read its exact shape" -f $zt.Name, $m.Name, (Get-InstructionText $zi[$k])); break }
        }
    }
}
if ($c22.Count -eq 0) {
    if ($Edition -eq "Wayfinder") { Write-Output "  ok    Wayfinder has no verbose log (LogDetail Off/Errors, no VerboseLog, no trace left), and no trace sits where checks read exact shapes" }
    else { Write-Output ("  ok    TomTom's verbose log is built in ({0} Diag calls, {1} trace-helper calls), and no trace sits where checks read exact shapes" -f $traceCalls22, $helperCalls22) }
}
else { foreach ($p in $c22) { Write-Output "  FAIL  22: $p" }; $failures++ }

Write-Output ""
Write-Output "== coordinates on screen =="
# 23. The methods read are every method of ArrowHud, Waypoint.get_ScreenName, and every method that calls
# WaypointManager.Notify or a MessageHud method. In both editions they call no Waypoint member that returns text other
# than ScreenName and CoordText (DisplayName, the window's and the log's name, is never called there - a log line that
# names a waypoint goes in a helper, as LogReached does); ArrowHud.DrawCaptions and WaypointManager.OnReached call
# ScreenName; Waypoint.ScreenName calls MapClickRules.ScreenName once and returns its result. In TomTom every call of
# Waypoint.CoordText or CoordinateFormat.Format in those methods is the coordinates argument (second to last) of
# MapClickRules.ScreenName(String,String,String,Boolean) or AddedMessage(Int32,String,Boolean), whose last argument is
# Plugin.ShowCoordinates' value read right there; HandleWaypointClick makes one such AddedMessage call; ShowCoordinates
# is bound once, default false. In Wayfinder those calls hand the rule null and false. Not seen: a position formatted in
# place (Waypoint.Pos or any vector turned into text without CoordText or CoordinateFormat), text built in another
# method or kept in a field and then shown, a message shown through a delegate, ShowCoordinates' value set after its
# bind, a Waypoint field read into a message (a name never holds coordinates), other ways of putting text on screen
# (Player.Message, the chat, a GUI label outside ArrowHud), which waypoint's CoordText is passed, what the two rules
# decide (the unit tests check that), and the window and the console, which show coordinates on purpose.
$checks++
$c23 = @()
function Test-Show23($ins, $hd, [int]$at) {
    $i = $ins[$at]; $o = $i.Operand
    if (-not ($i.OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference] -and $o.Name -eq "get_Value" -and $o.DeclaringType.GetElementType().FullName -eq 'BepInEx.Configuration.ConfigEntry`1')) { return $false }
    $a = Get-CallArgs $ins $at $hd
    return ($null -ne $a -and (Get-InstructionText $ins[$a[0]]) -eq "ldsfld Plugin::ShowCoordinates")
}
function Get-Sig23($o) { "{0}({1})" -f $o.Name, (@($o.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ",") }
$gate23 = @("ScreenName(System.String,System.String,System.String,System.Boolean)", "AddedMessage(System.Int32,System.String,System.Boolean)")
$sinks23 = @()
foreach ($t in @($plug.Types | ForEach-Object { Get-TypesDeep17 $_ })) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $is = ($t.FullName -like "Waypointer.ArrowHud*") -or ($t.FullName -eq "Waypointer.Waypoint" -and $m.Name -eq "get_ScreenName")
    foreach ($i in $m.Body.Instructions) { $o = $i.Operand
        if ($i.OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference] -and (($o.DeclaringType.FullName -eq "Waypointer.WaypointManager" -and $o.Name -eq "Notify") -or $o.DeclaringType.FullName -eq "MessageHud")) { $is = $true } }
    if ($is) { $sinks23 += $m }
} }
$gated23 = 0; $clickGate23 = 0
foreach ($m in $sinks23) {
    $ins = @($m.Body.Instructions); $hd = $m.Body.ExceptionHandlers; $w = $m.DeclaringType.Name + "." + $m.Name; $ok = @{}
    for ($k = 0; $k -lt $ins.Count; $k++) { $o = $ins[$k].Operand
        if (-not ($ins[$k].OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.MapClickRules" -and $gate23 -contains (Get-Sig23 $o))) { continue }
        $a = Get-CallArgs $ins $k $hd
        if ($null -eq $a) { $c23 += "${w}: MapClickRules.$($o.Name)'s arguments cannot be traced"; continue }
        $coord = $a[$a.Count - 2]; $show = $a[$a.Count - 1]
        if ($Edition -eq "TomTom") {
            if (Test-Show23 $ins $hd $show) { $ok[$coord] = $true; $gated23++; if ($o.Name -eq "AddedMessage" -and $m.Name -eq "HandleWaypointClick") { $clickGate23++ } }
            else { $c23 += ("{0}: MapClickRules.{1} is told '{2}', not ShowCoordinates' value" -f $w, $o.Name, (Get-InstructionText $ins[$show])) }
        } elseif ($ins[$coord].OpCode.Name -ne "ldnull" -or $ins[$show].OpCode.Name -ne "ldc.i4.0") { $c23 += "${w}: Wayfinder hands MapClickRules.$($o.Name) coordinates or true" }
    }
    for ($k = 0; $k -lt $ins.Count; $k++) { $o = $ins[$k].Operand
        if (-not ($ins[$k].OpCode.Name -like "call*" -and $o -is [Mono.Cecil.MethodReference])) { continue }
        $dt = $o.DeclaringType.FullName
        if (($dt -eq "Waypointer.Waypoint" -and $o.Name -eq "get_CoordText") -or $dt -eq "Waypointer.CoordinateFormat") {
            if (-not $ok.ContainsKey($k)) { $c23 += ("{0} shows {1}.{2} without ShowCoordinates" -f $w, $o.DeclaringType.Name, $o.Name) } }
        elseif ($dt -eq "Waypointer.Waypoint" -and $o.ReturnType.FullName -eq "System.String" -and $o.Name -ne "get_ScreenName") { $c23 += ("{0} calls Waypoint.{1}: the screen names a waypoint through ScreenName" -f $w, $o.Name) }
    }
}
$wt23 = $plug.GetType("Waypointer.Waypoint"); $sn23 = $null
if ($wt23) { $sn23 = $wt23.Methods | Where-Object { $_.Name -eq "get_ScreenName" -and $_.HasBody } | Select-Object -First 1 }
if (-not $sn23) { $c23 += "Waypoint.ScreenName not found" }
else { $si = @($sn23.Body.Instructions); $sh = $sn23.Body.ExceptionHandlers
    $sc = Find-Calls $si "Waypointer.MapClickRules" "ScreenName"; $rets = @(); for ($k = 0; $k -lt $si.Count; $k++) { if ($si[$k].OpCode.Name -eq "ret") { $rets += $k } }
    if ($sc.Count -ne 1 -or $rets.Count -ne 1) { $c23 += ("Waypoint.ScreenName calls MapClickRules.ScreenName {0}x and returns in {1} places; expected once each" -f $sc.Count, $rets.Count) }
    else { $st = Get-StackBefore $si $rets[0] $sh
        if ($st.Count -lt 1 -or (Resolve-Value $si $st[$st.Count - 1] $sh) -ne "call MapClickRules::ScreenName") { $c23 += "Waypoint.ScreenName does not return what MapClickRules.ScreenName returns" } } }
foreach ($need in @(@("Waypointer.ArrowHud", "DrawCaptions"), @("Waypointer.WaypointManager", "OnReached"))) {
    $nt = $plug.GetType($need[0]); $nm = $null; if ($nt) { $nm = $nt.Methods | Where-Object { $_.Name -eq $need[1] -and $_.HasBody } | Select-Object -First 1 }
    if (-not $nm -or (Find-Calls @($nm.Body.Instructions) "Waypointer.Waypoint" "get_ScreenName").Count -eq 0) { $c23 += ("{0}.{1} does not ask Waypoint.ScreenName" -f $need[0], $need[1]) } }
if ($Edition -eq "TomTom") {
    if ($clickGate23 -ne 1) { $c23 += ("HandleWaypointClick calls MapClickRules.AddedMessage(Int32, String, Boolean) told ShowCoordinates {0}x; expected once" -f $clickGate23) }
    $binds23 = 0; $def23 = $null
    foreach ($m in $pluginType.Methods) { if (-not $m.HasBody) { continue }; $bi = @($m.Body.Instructions)
        for ($k = 0; $k -lt $bi.Count; $k++) { $o = $bi[$k].Operand
            if (-not ($o -is [Mono.Cecil.MethodReference]) -or $o.DeclaringType.FullName -ne "BepInEx.Configuration.ConfigFile" -or $o.Name -ne "Bind") { continue }
            $a = Get-CallArgs $bi $k $m.Body.ExceptionHandlers
            if ($a -and $a.Count -ge 4 -and $bi[$a[2]].OpCode.Name -eq "ldstr" -and "$($bi[$a[2]].Operand)" -eq "ShowCoordinates") { $binds23++; $def23 = $bi[$a[3]].OpCode.Name } } }
    if ($binds23 -ne 1 -or $def23 -ne "ldc.i4.0") { $c23 += ("ShowCoordinates is bound {0}x, default '{1}'; expected once, false" -f $binds23, $def23) }
}
if ($c23.Count -eq 0) { Write-Output $(if ($Edition -eq "TomTom") { "  ok    the arrow and the messages call no Waypoint text but ScreenName, and their coordinate calls are told ShowCoordinates (off by default; $gated23 gated calls)" } else { "  ok    the arrow and the messages call no Waypoint text but ScreenName, which Wayfinder hands no coordinates" }) }
else { foreach ($p in $c23) { Write-Output "  FAIL  23: $p" }; $failures++ }

Write-Output ""
Write-Output "== the server side =="
# Since 1.3.0 the plugin also runs on a dedicated server, and as the host it answers other players' Find:
#   1. it declares both processes, valheim.exe and valheim_server.exe (BepInEx skips a plugin elsewhere)
#   2. Game_RPC_DiscoverClosestLocation_Patch applies WhoMayFind to Vegvisir-style requests that carry this plugin's
#      token and leaves every other request to vanilla: it branches directly on SearchRules.VanillaMayHandle(pinName)
#      - and the way it branches is followed: when that is true, the code reached is "return true" - returns true
#      (vanilla runs) exactly once, and otherwise returns a local set only to false or from FindServer.CallerMayFind()
#      (an error refuses). CallerMayFind returns only the unit-tested FindProtocol.CallerMayFind(...), none of
#      whose arguments is a constant; FindServer.Allowed returns only
#      FindProtocol.MayFind (unit-tested); FindServer.IsAdmin returns only false or ZNet.IsAdmin; and
#      Plugin.ApplyPatches records whether the connection patch applied (RoutedCallContext.Available), without
#      which only Everyone lets a caller through
#   3. a player is known by the connection the call came on, never by the call's sender field (which the sending
#      game writes itself): ZRoutedRpc_RPC_RoutedRPC_Patch sets RoutedCallContext.Current from RPC_RoutedRPC's rpc
#      argument and a Finalizer clears it; FindServer.OnMessage takes no sender, and it and CallerMayFind get the
#      player from FindServer.CallingPeer
#   4. FindServer.OnMessage asks FindServer.MayFind before it accepts a Find, and FindServer never trims what it
#      sends (no SearchRules.KeepNearest): Wayfinder's exploration filter runs on the player's side, after it
#   5. FindLink takes answers only from the server peer: it compares its sender argument with ZNet.GetServerPeer's
#      m_uid
$serverProblems = @()
$checks++
$processes = @()
if ($pluginType) {
    foreach ($ca in $pluginType.CustomAttributes) {
        if ($ca.AttributeType.Name -eq "BepInProcess") { $processes += "$($ca.ConstructorArguments[0].Value)" }
    }
}
if (-not ($processes -contains "valheim.exe" -and $processes -contains "valheim_server.exe")) {
    $serverProblems += ("BepInProcess lists '{0}', expected valheim.exe and valheim_server.exe" -f ($processes -join ", "))
}
$gpType = $plug.GetType("Waypointer.Game_RPC_DiscoverClosestLocation_Patch")
$gpPrefix = $null
if ($gpType) { $gpPrefix = $gpType.Methods | Where-Object { $_.Name -eq "Prefix" } | Select-Object -First 1 }
if (-not $gpPrefix) { $serverProblems += "Waypointer.Game_RPC_DiscoverClosestLocation_Patch.Prefix not found" }
else {
    $gi = @($gpPrefix.Body.Instructions)
    $pnames = @($gpPrefix.Parameters | ForEach-Object { $_.Name })
    if ($gpPrefix.ReturnType.FullName -ne "System.Boolean") { $serverProblems += "the WhoMayFind prefix does not return bool, so it cannot refuse" }
    if (-not ($pnames -contains "pinName")) { $serverProblems += "the WhoMayFind prefix does not take the request's pinName" }
    $vmhAt = -1
    for ($k = 0; $k -lt $gi.Count; $k++) {
        $o = $gi[$k].Operand
        if ($o -is [Mono.Cecil.MethodReference] -and $o.DeclaringType.FullName -eq "Waypointer.SearchRules" -and $o.Name -eq "VanillaMayHandle") { $vmhAt = $k; break }
    }
    $argOk = $false
    if ($vmhAt -ge 1) {
        $a = $gi[$vmhAt - 1]
        $an = ""
        if ($a.OpCode.Name -match '^ldarg\.([0-3])$') { $an = $gpPrefix.Parameters[[int]$Matches[1]].Name }
        elseif ($a.OpCode.Name -like "ldarg*" -and $a.Operand -is [Mono.Cecil.ParameterDefinition]) { $an = $a.Operand.Name }
        $argOk = $an -eq "pinName"
    }
    $branches = $vmhAt -ge 0 -and ($vmhAt + 1) -lt $gi.Count -and "$($gi[$vmhAt + 1].OpCode.FlowControl)" -eq "Cond_Branch"
    if (-not ($argOk -and $branches)) { $serverProblems += "the WhoMayFind prefix does not branch directly on SearchRules.VanillaMayHandle(pinName)" }
    else {
        # Which way: brtrue jumps when it is true, brfalse falls through when it is true. Either way the code reached
        # must be "return true", or a request without this plugin's token would go through WhoMayFind.
        $br = $gi[$vmhAt + 1]
        $trueWay = -1
        if ($br.OpCode.Name -like "brtrue*") { $trueWay = [array]::IndexOf($gi, $br.Operand) }
        elseif ($br.OpCode.Name -like "brfalse*") { $trueWay = $vmhAt + 2 }
        $returnsTrue = $trueWay -ge 0 -and ($trueWay + 1) -lt $gi.Count -and $gi[$trueWay].OpCode.Name -eq "ldc.i4.1" -and $gi[$trueWay + 1].OpCode.Name -eq "ret"
        if (-not $returnsTrue) { $serverProblems += "when SearchRules.VanillaMayHandle(pinName) is true, the WhoMayFind prefix does not return true (vanilla requests would be judged by WhoMayFind)" }
    }
    $trueRets = 0; $flagRets = @(); $otherRets = 0
    for ($k = 1; $k -lt $gi.Count; $k++) {
        if ($gi[$k].OpCode.Name -ne "ret") { continue }
        $b = $gi[$k - 1]
        if ($b.OpCode.Name -eq "ldc.i4.1") { $trueRets++ }
        elseif ($b.OpCode.Name -like "ldloc*") { $flagRets += (Get-LocalIndex $b) }
        else { $otherRets++ }
    }
    if ($trueRets -ne 1 -or $otherRets -ne 0 -or $flagRets.Count -eq 0) {
        $serverProblems += ("the WhoMayFind prefix must return true once (vanilla requests) and otherwise a flag; found {0} true, {1} flag and {2} other returns" -f $trueRets, $flagRets.Count, $otherRets)
    } else {
        $fromMayFind = $false
        for ($k = 1; $k -lt $gi.Count; $k++) {
            if (-not ($gi[$k].OpCode.Name -like "stloc*" -and $flagRets -contains (Get-LocalIndex $gi[$k]))) { continue }
            $src = $gi[$k - 1]
            $isRule = $src.OpCode.Name -like "call*" -and $src.Operand -is [Mono.Cecil.MethodReference] -and $src.Operand.DeclaringType.FullName -eq "Waypointer.FindServer" -and $src.Operand.Name -eq "CallerMayFind"
            if ($isRule) { $fromMayFind = $true }
            elseif ($src.OpCode.Name -ne "ldc.i4.0") { $serverProblems += ("the WhoMayFind prefix sets its answer from '{0}', not from FindServer.CallerMayFind or false" -f $src.OpCode.Name) }
        }
        if (-not $fromMayFind) { $serverProblems += "the WhoMayFind prefix never asks FindServer.CallerMayFind" }
    }
}
# The connection, not the sender field.
$ctxPatch = $plug.GetType("Waypointer.ZRoutedRpc_RPC_RoutedRPC_Patch")
$ctxOk = $false
if ($ctxPatch) {
    $ctxTarget = @($ctxPatch.CustomAttributes | Where-Object { $_.AttributeType.Name -eq "HarmonyPatch" } | ForEach-Object { @($_.ConstructorArguments | ForEach-Object { "$($_.Value)" }) -join "." })
    $cp = $ctxPatch.Methods | Where-Object { $_.Name -eq "Prefix" } | Select-Object -First 1
    $cf = $ctxPatch.Methods | Where-Object { $_.Name -eq "Finalizer" } | Select-Object -First 1
    $setsFromRpc = $false; $clears = $false
    if ($cp) {
        $ci = @($cp.Body.Instructions)
        for ($k = 1; $k -lt $ci.Count; $k++) {
            if ($ci[$k].OpCode.Name -eq "stsfld" -and "$($ci[$k].Operand)" -match "Waypointer\.RoutedCallContext::Current$") {
                $a = $ci[$k - 1]
                $an = ""
                if ($a.OpCode.Name -match '^ldarg\.([0-3])$') { $an = $cp.Parameters[[int]$Matches[1]].Name }
                elseif ($a.OpCode.Name -like "ldarg*" -and $a.Operand -is [Mono.Cecil.ParameterDefinition]) { $an = $a.Operand.Name }
                if ($an -eq "rpc") { $setsFromRpc = $true }
            }
        }
    }
    if ($cf) {
        $fi = @($cf.Body.Instructions)
        for ($k = 1; $k -lt $fi.Count; $k++) {
            if ($fi[$k].OpCode.Name -eq "stsfld" -and "$($fi[$k].Operand)" -match "Waypointer\.RoutedCallContext::Current$" -and $fi[$k - 1].OpCode.Name -eq "ldnull") { $clears = $true }
        }
    }
    $ctxOk = ($ctxTarget -contains "ZRoutedRpc.RPC_RoutedRPC") -and $setsFromRpc -and $clears
}
if (-not $ctxOk) { $serverProblems += "ZRoutedRpc_RPC_RoutedRPC_Patch must set RoutedCallContext.Current from RPC_RoutedRPC's rpc in a Prefix and clear it in a Finalizer" }
$apm = $null
if ($pluginType) { $apm = $pluginType.Methods | Where-Object { $_.Name -eq "ApplyPatches" } | Select-Object -First 1 }
$records = $false
if ($apm) {
    $pi = @($apm.Body.Instructions)
    for ($k = 1; $k -lt $pi.Count; $k++) {
        if ($pi[$k].OpCode.Name -eq "stsfld" -and "$($pi[$k].Operand)" -match "Waypointer\.RoutedCallContext::Available$" -and $pi[$k - 1].OpCode.Name -eq "ldc.i4.1") { $records = $true }
    }
}
if (-not $records) { $serverProblems += "Plugin.ApplyPatches does not record that the connection patch applied (RoutedCallContext.Available)" }
$fs = $plug.GetType("Waypointer.FindServer")
if (-not $fs) { $serverProblems += "Waypointer.FindServer not found" }
else {
    foreach ($mn in @("OnMessage", "CallerMayFind")) {
        $mm = $fs.Methods | Where-Object { $_.Name -eq $mn } | Select-Object -First 1
        if (-not $mm) { $serverProblems += "FindServer.$mn not found"; continue }
        if (@($mm.Parameters | Where-Object { $_.ParameterType.FullName -eq "System.Int64" }).Count -gt 0) { $serverProblems += "FindServer.$mn takes a sender id; it must know the player by the connection" }
        $usesConn = @($mm.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.FindServer" -and $_.Operand.Name -eq "CallingPeer" }).Count -gt 0
        if (-not $usesConn) { $serverProblems += "FindServer.$mn does not get the player from FindServer.CallingPeer" }
    }
    # The decision: CallerMayFind returns only FindProtocol.CallerMayFind(...) (unit-tested), whose arguments are
    # all worked out, none a constant.
    $cmf = $fs.Methods | Where-Object { $_.Name -eq "CallerMayFind" } | Select-Object -First 1
    $decisionOk = $false
    if ($cmf) {
        $ci2 = @($cmf.Body.Instructions)
        $decisionOk = $true
        $rets2 = 0
        for ($k = 1; $k -lt $ci2.Count; $k++) {
            if ($ci2[$k].OpCode.Name -ne "ret") { continue }
            $rets2++
            $src = $ci2[$k - 1]
            $isRule = $src.OpCode.Name -like "call*" -and $src.Operand -is [Mono.Cecil.MethodReference] -and $src.Operand.DeclaringType.FullName -eq "Waypointer.FindProtocol" -and $src.Operand.Name -eq "CallerMayFind"
            if (-not $isRule) { $decisionOk = $false; continue }
            $args2 = Get-ArgumentSources $ci2 ($k - 1) $cmf.Body.ExceptionHandlers
            if (-not $args2) { $decisionOk = $false; continue }
            foreach ($a in $args2) { if ($ci2[$a].OpCode.Name -like "ldc.i4*") { $decisionOk = $false } }
        }
        if ($rets2 -eq 0) { $decisionOk = $false }
    }
    if (-not $decisionOk) { $serverProblems += "FindServer.CallerMayFind must return only FindProtocol.CallerMayFind(...), with no constant argument" }
    $alw = $fs.Methods | Where-Object { $_.Name -eq "Allowed" } | Select-Object -First 1
    $alwOk = $false
    if ($alw) {
        $ai3 = @($alw.Body.Instructions)
        $alwOk = $true
        for ($k = 1; $k -lt $ai3.Count; $k++) {
            if ($ai3[$k].OpCode.Name -ne "ret") { continue }
            $b = $ai3[$k - 1]
            if (-not ($b.OpCode.Name -like "call*" -and $b.Operand -is [Mono.Cecil.MethodReference] -and $b.Operand.DeclaringType.FullName -eq "Waypointer.FindProtocol" -and $b.Operand.Name -eq "MayFind")) { $alwOk = $false }
        }
    }
    if (-not $alwOk) { $serverProblems += "FindServer.Allowed must return only FindProtocol.MayFind(...)" }
    $iad = $fs.Methods | Where-Object { $_.Name -eq "IsAdmin" } | Select-Object -First 1
    $iadOk = $false
    if ($iad) {
        $ii = @($iad.Body.Instructions)
        $asks2 = @($ii | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "ZNet" -and $_.Operand.Name -eq "IsAdmin" }).Count -gt 0
        $iadOk = $asks2
        for ($k = 1; $k -lt $ii.Count; $k++) {
            if ($ii[$k].OpCode.Name -ne "ret") { continue }
            $b = $ii[$k - 1]
            $fromGame = $b.Operand -is [Mono.Cecil.MethodReference] -and $b.Operand.DeclaringType.FullName -eq "ZNet" -and $b.Operand.Name -eq "IsAdmin"
            if (-not ($fromGame -or $b.OpCode.Name -eq "ldc.i4.0")) { $iadOk = $false }
        }
    }
    if (-not $iadOk) { $serverProblems += "FindServer.IsAdmin must return only false or the game's own ZNet.IsAdmin" }
    $cpm = $fs.Methods | Where-Object { $_.Name -eq "CallingPeer" } | Select-Object -First 1
    $readsCtx = $false
    if ($cpm) { $readsCtx = @($cpm.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldsfld" -and "$($_.Operand)" -match "Waypointer\.RoutedCallContext::Current$" }).Count -gt 0 }
    if (-not $readsCtx) { $serverProblems += "FindServer.CallingPeer does not read RoutedCallContext.Current" }
    $om = $fs.Methods | Where-Object { $_.Name -eq "OnMessage" } | Select-Object -First 1
    $asks = @()
    if ($om) { $asks = @($om.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "Waypointer.FindServer" -and $_.Operand.Name -eq "MayFind" }) }
    if ($asks.Count -eq 0) { $serverProblems += "FindServer.OnMessage does not ask FindServer.MayFind before accepting a Find" }
    foreach ($m in $fs.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            if ($i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq "Waypointer.SearchRules" -and $i.Operand.Name -eq "KeepNearest") {
                $serverProblems += ("FindServer.{0} trims the places it sends (SearchRules.KeepNearest); Wayfinder's filter needs every place in range" -f $m.Name)
            }
        }
    }
}
$fl = $plug.GetType("Waypointer.FindLink")
$checksSender = $false
if ($fl) {
    $oc = $fl.Methods | Where-Object { $_.Name -eq "OnToClient" } | Select-Object -First 1
    if ($oc) {
        $oi = @($oc.Body.Instructions)
        $asksServer = @($oi | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq "ZNet" -and $_.Operand.Name -eq "GetServerPeer" }).Count -gt 0
        # sender compared with the server peer's m_uid: ldarg sender ... ldfld ZNetPeer::m_uid, then a compare.
        $compares = $false
        for ($k = 1; $k -lt $oi.Count - 1; $k++) {
            if (-not ($oi[$k].OpCode.Name -eq "ldfld" -and "$($oi[$k].Operand)" -match "ZNetPeer::m_uid$")) { continue }
            if (-not ($oi[$k + 1].OpCode.Name -match '^(beq|bne\.un|ceq)')) { continue }
            for ($j = [Math]::Max(0, $k - 3); $j -lt $k; $j++) {
                $an = ""
                if ($oi[$j].OpCode.Name -match '^ldarg\.([0-3])$') { $an = $oc.Parameters[[int]$Matches[1]].Name }
                elseif ($oi[$j].OpCode.Name -like "ldarg*" -and $oi[$j].Operand -is [Mono.Cecil.ParameterDefinition]) { $an = $oi[$j].Operand.Name }
                if ($an -eq "sender") { $compares = $true }
            }
        }
        $checksSender = $asksServer -and $compares
    }
}
if (-not $checksSender) { $serverProblems += "FindLink.OnToClient does not compare its sender with the server peer (ZNet.GetServerPeer().m_uid)" }
if ($serverProblems.Count -eq 0) {
    Write-Output "  ok    runs on valheim.exe and valheim_server.exe; WhoMayFind decides only this plugin's requests, by the calling connection; the server never trims; only its answers count"
} else {
    foreach ($p in $serverProblems) { Write-Output "  FAIL  $p" }
    $failures++
}

Write-Output ""
Write-Output "== game types and members the plugin uses =="
# The runtime binds each reference by its exact signature, so a Valheim update that keeps a name but changes
# its parameters (AddPin once gained a PlatformUserID argument) passes every name check above and then
# throws MissingMethodException in game. Resolve every reference into the game, Unity, BepInEx and Harmony
# against the shipped assemblies; framework references (netstandard) are not checked.
$gameScopes = @("assembly_valheim", "assembly_utils", "assembly_guiutils", "Splatform", "BepInEx", "0Harmony")
$refs = @()
foreach ($tr in $plug.GetTypeReferences()) { $refs += ,@($tr, $tr) }
foreach ($mr in $plug.GetMemberReferences()) { $refs += ,@($mr, $mr.DeclaringType) }
$resolved = 0
$unresolved = 0
foreach ($pair in $refs) {
    $dt = $pair[1]
    while ($dt.IsNested) { $dt = $dt.DeclaringType }
    $scope = $dt.Scope.Name
    if (-not ($gameScopes -contains $scope -or $scope -like "UnityEngine*")) { continue }
    $r = $null
    try { $r = $pair[0].Resolve() } catch { }
    if ($r -eq $null) {
        Write-Output ("  FAIL  {0} does not resolve in {1}" -f $pair[0].FullName, $scope)
        $unresolved++
    } else { $resolved++ }
}
$checks++
if ($unresolved -eq 0) {
    Write-Output ("  ok    all {0} type and member references into the game, Unity, BepInEx and Harmony resolve" -f $resolved)
} else {
    $failures++
}

Write-Output ""
Write-Output "== the dedicated server's own assemblies =="
# The dedicated server ships its own build of assembly_valheim.dll (and of most other assemblies): the same
# source compiled for the server, with some method bodies changed. Resolve every reference again against it, the
# way the server's runtime will bind them. BepInEx and Harmony come from this game's BepInEx\core (the same pack
# goes on a server). Skipped, and not counted, when no dedicated server is installed.
$serverManaged = Join-Path $ServerDir "valheim_server_Data\Managed"
if (-not (Test-Path (Join-Path $serverManaged "assembly_valheim.dll"))) {
    Write-Output ("  skip  no dedicated server at {0}; the server's own assemblies were not checked" -f $ServerDir)
} else {
    $sResolver = New-Object Mono.Cecil.DefaultAssemblyResolver
    $sResolver.AddSearchDirectory($serverManaged)
    $sResolver.AddSearchDirectory($core)
    $sParams = New-Object Mono.Cecil.ReaderParameters
    $sParams.AssemblyResolver = $sResolver
    $sPlug = [Mono.Cecil.ModuleDefinition]::ReadModule($Plugin, $sParams)
    $sRefs = @()
    foreach ($tr in $sPlug.GetTypeReferences()) { $sRefs += ,@($tr, $tr) }
    foreach ($mr in $sPlug.GetMemberReferences()) { $sRefs += ,@($mr, $mr.DeclaringType) }
    $sResolved = 0; $sMissing = @()
    foreach ($pair in $sRefs) {
        $dt = $pair[1]
        while ($dt.IsNested) { $dt = $dt.DeclaringType }
        $scope = $dt.Scope.Name
        if (-not ($gameScopes -contains $scope -or $scope -like "UnityEngine*")) { continue }
        $r = $null
        try { $r = $pair[0].Resolve() } catch { }
        if ($r -eq $null) { $sMissing += ("{0} ({1})" -f $pair[0].FullName, $scope) } else { $sResolved++ }
    }
    $sFile = $null
    try { $sFile = $sPlug.AssemblyResolver.Resolve((New-Object Mono.Cecil.AssemblyNameReference("assembly_valheim", (New-Object Version)))).MainModule.FileName } catch { }
    $checks++
    if ($sMissing.Count -eq 0 -and $sFile -and $sFile.StartsWith($serverManaged, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output ("  ok    all {0} references resolve against the dedicated server's own assemblies ({1})" -f $sResolved, $serverManaged)
    } else {
        if (-not ($sFile -and $sFile.StartsWith($serverManaged, [StringComparison]::OrdinalIgnoreCase))) { Write-Output ("  FAIL  assembly_valheim resolved from '{0}', not from the server" -f $sFile) }
        foreach ($x in $sMissing) { Write-Output ("  FAIL  {0} does not resolve on the dedicated server" -f $x) }
        $failures++
    }
}

Write-Output ""
Write-Output "== assembly references =="
foreach ($r in $plug.AssemblyReferences) {
    $checks++
    $name = $r.Name + ".dll"
    if ((Test-Path (Join-Path $managed $name)) -or (Test-Path (Join-Path $core $name))) {
        Write-Output ("  ok    {0}" -f $r.Name)
    } else {
        Write-Output ("  FAIL  {0} cannot be resolved from the game folder" -f $r.Name)
        $failures++
    }
}

Write-Output ""
if ($failures -eq 0) {
    Write-Output "PREFLIGHT PASSED - $checks checks, 0 failures."
    exit 0
}
Write-Output "PREFLIGHT FAILED - $failures of $checks checks failed."
exit 1
