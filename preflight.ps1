# Preflight check for the TomTom and Wayfinder plugins.
#
# Confirms, without launching the game, that:
#   1. the plugin identifies itself as the expected edition, and excludes its sibling edition
#   2. every [HarmonyPatch] target type and method still exists in the shipped game assemblies
#   3. every private member reached by reflection still exists
#   4. map markers can only ever be created local-only (never shared through a Cartography Table)
#   5. a pin the player promotes is never modified or removed - only the mod's own markers are
#   6. Wayfinder contains no coordinate-entry code or coordinate readout at all; TomTom still does
#   7. every assembly the plugin references can be resolved from the game folder
#   8. every game, Unity, BepInEx and Harmony type and member the plugin uses resolves with its exact signature
#   9. the Chat.HasFocus postfix still runs last (Chatter's postfix overwrites the result and loads later)
#  10. routes are saved only through SafeFile.WriteAllText, which flushes the new file to disk before swapping it in
#  11. a key Valheim cannot read cannot stop the waypoint tick: configurable keys are read only through Hotkeys,
#      whose reads are caught (and not rethrown), no literal key the game cannot read is used anywhere, and the
#      tick runs in a try block of its own that reads no key
#  12. a location search cannot make a shared pin: the server's answers are caught before vanilla turns them into
#      saved pins, and no request is sent unless that catch is in place
#  13. Wayfinder's search keeps only places whose centre is explored; TomTom's keeps everything in range
#
# A rename in a Valheim update shows up here as a failure instead of as a broken feature in-game.
#
#   powershell -ExecutionPolicy Bypass -File preflight.ps1                        # deployed TomTom
#   powershell -ExecutionPolicy Bypass -File preflight.ps1 -Edition Wayfinder -Plugin build\Wayfinder\Wayfinder.dll

param(
    [ValidateSet("TomTom", "Wayfinder")]
    [string]$Edition = "TomTom",
    [string]$ValheimDir = "E:\SteamLibrary\steamapps\common\Valheim",
    [string]$Plugin = ""
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $ValheimDir "valheim_Data\Managed"
$core = Join-Path $ValheimDir "BepInEx\core"
if ($Plugin -eq "") { $Plugin = Join-Path $ValheimDir "BepInEx\plugins\DoomMachine-$Edition\$Edition.dll" }

$expectedGuid = "DoomMachine.$Edition"
$siblingGuid = if ($Edition -eq "TomTom") { "DoomMachine.Wayfinder" } else { "DoomMachine.TomTom" }
$expectedVersion = "1.2.0"

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
# the save file (SaveIfDirty) is the one sanctioned place. LocationSearch.Ask is exempt too: it boxes the search
# origin only because ZRoutedRpc.InvokeRoutedRPC takes params object[] - the vector goes to the server, never to
# text (check 12 pins down what Ask does). A tripwire, not a proof: a helper handed bare floats that formats them
# elsewhere is not caught. TomTom is checked the other way round (non-vacuous).
$numericText = @()
foreach ($t in $plug.GetTypes()) { foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $readsXZ = $false; $toText = $false; $vecText = $false
    foreach ($i in $m.Body.Instructions) {
        $n = $i.OpCode.Name; $op = "$($i.Operand)"
        if (($n -eq "ldfld" -or $n -eq "ldflda") -and $op -match "UnityEngine\.Vector3::(x|z)$") { $readsXZ = $true }
        if ($n -eq "box" -and $op -match "^System\.(Single|Double|Decimal|U?Int(16|32|64))$") { $toText = $true }
        if ($n -like "call*" -and $op -match "System\.(Single|Double|Decimal|Int32|Int64)::ToString|StringBuilder::Append\(System\.(Single|Double|Decimal|Int32|Int64)\)") { $toText = $true }
        if (($n -eq "box" -and $op -match "^UnityEngine\.Vector[234]$") -or ($n -like "call*" -and $op -match "UnityEngine\.Vector[234]::ToString")) { $vecText = $true }
    }
    $w = $t.Name + "." + $m.Name
    if ($w -ne "WaypointManager.SaveIfDirty" -and $w -ne "LocationSearch.Ask" -and ($vecText -or ($readsXZ -and $toText))) { $numericText += $w }
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
    $unreadableNames = @($unreadableKeys.Values | Sort-Object)
    if (Compare-Object @($documentedUnreadable | Sort-Object) $unreadableNames) {
        Write-Output ("  note  the game now cannot read {0} KeyCodes, not the 30 of Valheim 1.0.16 - update the Keys paragraph of both package READMEs and Hotkeys.cs: {1}" -f $unreadableNames.Count, ($unreadableNames -join ", "))
    } else {
        Write-Output "  note  the game cannot read the same 30 KeyCodes as Valheim 1.0.16 (the list in both package READMEs)"
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
#      result of LocationSearch.AnswersIntercepted is branched on directly, before the only
#      ZRoutedRpc.InvokeRoutedRPC call in the plugin; and AnswersIntercepted asks Harmony.GetPatchInfo about
#      Game_RPC_DiscoverLocationResponse_Patch and returns, once, a flag set only to false or from
#      SearchRules.IsOurPrefix (unit-tested) - no request is sent unless the prefix is confirmed patched
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
                if ($dt -eq "ZRoutedRpc" -and $op.Name -eq "InvokeRoutedRPC") { $invokeSites += ("{0}.{1}" -f $t.FullName, $m.Name) }
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
$otherInvokes = @($invokeSites | Where-Object { $_ -ne "Waypointer.LocationSearch.Ask" })
if ($otherInvokes.Count -gt 0) { $searchProblems += ("ZRoutedRpc.InvokeRoutedRPC is also called from {0}" -f ($otherInvokes -join ", ")) }
if ($searchProblems.Count -eq 0) {
    Write-Output "  ok    the server's answers to a search cannot become saved pins (prefix skips vanilla; no request without it; no DiscoverLocation)"
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
elseif ($Edition -eq "Wayfinder" -and $readsExplored) { Write-Output "  ok    Wayfinder's search keeps only places whose centre is explored (reads MinimapAccess.IsExplored)" }
elseif ($Edition -eq "Wayfinder") { Write-Output "  FAIL  Wayfinder's search does not read MinimapAccess.IsExplored - it would place unexplored places"; $failures++ }
elseif (-not $readsExplored) { Write-Output "  ok    TomTom's search places everything in range (does not read MinimapAccess.IsExplored)" }
else { Write-Output "  FAIL  TomTom's search reads MinimapAccess.IsExplored - the explored filter belongs to Wayfinder"; $failures++ }

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
