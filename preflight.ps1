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
$expectedVersion = "1.1.1"

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
function Get-ArgumentSources($ins, [int]$callAt) {
    # Replays the evaluation stack over the code before a call and returns, for each value the call
    # consumes (the instance first), the index of the instruction that pushed it; $null if it does not add
    # up. A branch or return starts a new statement (compiled C# has an empty stack there); a branch
    # INSIDE the argument list (a ?: operand) leaves too few values, so it returns $null and the check fails.
    $stack = New-Object System.Collections.ArrayList
    for ($k = 0; $k -lt $callAt; $k++) {
        $i = $ins[$k]; $pop = "$($i.OpCode.StackBehaviourPop)"; $push = "$($i.OpCode.StackBehaviourPush)"
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
# the save file (SaveIfDirty) is the one sanctioned place. A tripwire, not a proof: a helper handed bare
# floats that formats them elsewhere is not caught. TomTom is checked the other way round (non-vacuous).
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
    if ($w -ne "WaypointManager.SaveIfDirty" -and ($vecText -or ($readsXZ -and $toText))) { $numericText += $w }
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
# SafeFile.WriteAllText, and nothing else in the plugin opens a file for writing (File or FileInfo
# Write*/Append*/Create*/Open/OpenWrite/Replace/Copy*, or a new FileStream, StreamWriter or BinaryWriter). A
# FileStream opened anywhere else fails even for reading, so that a person looks. This reads the IL in code
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
        $inSafeWrite = ($t.FullName -eq "Waypointer.SafeFile" -and $m.Name -eq "WriteAllText")
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if (-not ($op -is [Mono.Cecil.MethodReference])) { continue }
            $dt = $op.DeclaringType.FullName
            if ($t.FullName -eq "Waypointer.WaypointManager" -and $m.Name -eq "SaveIfDirty" -and $i.OpCode.Name -eq "call" -and $dt -eq "Waypointer.SafeFile" -and $op.Name -eq "WriteAllText") { $saveUsesIt = $true }
            $fileApi = ($dt -eq "System.IO.File" -or $dt -eq "System.IO.FileInfo")
            $opensFile = ($fileApi -and ($op.Name -match '^(Write|Append|Create|Open|Replace|Copy)') -and ($op.Name -notmatch '^Open(Read|Text)$'))
            $newWriter = (($dt -eq "System.IO.FileStream" -or $dt -eq "System.IO.StreamWriter" -or $dt -eq "System.IO.BinaryWriter") -and $op.Name -eq ".ctor")
            if (($opensFile -or $newWriter) -and -not $inSafeWrite) { $writers += ("{0}.{1} uses {2}::{3}" -f $t.Name, $m.Name, $op.DeclaringType.Name, $op.Name) }
        }
    }
}
if ($saveUsesIt -and $writers.Count -eq 0) {
    Write-Output "  ok    routes are saved only through SafeFile.WriteAllText (WaypointManager.SaveIfDirty calls it; nothing else opens a file for writing)"
} else {
    if (-not $saveUsesIt) { Write-Output "  FAIL  WaypointManager.SaveIfDirty does not call SafeFile.WriteAllText" }
    foreach ($w in $writers) { Write-Output "  FAIL  a file is opened for writing outside SafeFile.WriteAllText: $w" }
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
