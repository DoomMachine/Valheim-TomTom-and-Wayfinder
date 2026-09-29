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
$expectedVersion = "1.5.0"

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
                # pin on screen without any store to PinData.
                elseif (@("m_name", "m_pos", "m_type") -notcontains $i.Operand.Name) {
                    $pinTouches += ("{0} reads PinData.{1} (only m_name, m_pos and m_type may be read)" -f $where, $i.Operand.Name)
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
$coordConfigKey = "InputIsRawValheimXYZ"

$present = @()
foreach ($n in $coordTypes) { if ($plug.GetType($n)) { $present += $n } }
foreach ($pair in $coordMethods) {
    $t = $plug.GetType($pair[0])
    if ($t -and @($t.Methods | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0) { $present += ($pair[0] + "." + $pair[1]) }
}
$hasConfigKey = $false
$hasCoordFormatString = $false
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
        if ($i.OpCode.Name -ne "ldstr") { continue }
        $s = "$($i.Operand)"
        if ($s -eq $coordConfigKey) { $hasConfigKey = $true }
        # "{0:0}, {1:0}" is the shape of an x, z readout - it should exist only in CoordinateFormat.
        if ($s -like "*{0:0}, {1:0}*") { $hasCoordFormatString = $true }
    }
} }
if ($hasConfigKey) { $present += ("config key " + $coordConfigKey) }
if ($hasCoordFormatString) { $present += "a coordinate format string ""{0:0}, {1:0}""" }
$expectedCount = $coordTypes.Count + $coordMethods.Count + 2

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
            if (($opensFile -or $newWriter) -and -not $theStream) { $writers += ("{0}.{1} uses {2}::{3}" -f $t.Name, $m.Name, $op.DeclaringType.Name, $op.Name) }
        }
    }
}
if ($saveUsesIt -and $writers.Count -eq 0) {
    Write-Output "  ok    routes are saved only through SafeFile.WriteAllText (WaypointManager.SaveIfDirty calls it; nothing but its FileStream opens a file for writing)"
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
    if ($dirtySeen -eq 0 -or -not $dirtyOk) { $rrWhy += "RetryRead does not set _dirty from 'the waypoints queued before the merge > 0' (a merged route would not be saved)" }
    # m1: in Load and RetryRead, the branch on ReadRoute's result: its false edge records the failed read before any
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
if ($rrWhy.Count -eq 0) { Write-Output "  ok    a route that could not be read is not saved over (SaveIfDirty returns while RouteReadGate.Pending; Load and RetryRead record the failure, RetryRead returns then, only on ReadRoute's false branch; ReadRoute's catch returns false; NotRead is called; _dirty is set from the count queued before the merge)" }
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
# TomTom's does not call it there.
$checks++
$f16 = @()
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
    }
}
if ($f16.Count -eq 0) { Write-Output ("  ok    {0}" -f $(if ($Edition -eq "TomTom") { "TomTom's map click places a waypoint anywhere (does not read MinimapAccess.IsExplored)" } else { "Wayfinder's map click places a waypoint only where MinimapAccess.IsExplored says the map is explored" })) }
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
