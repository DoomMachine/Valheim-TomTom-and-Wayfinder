# Preflight check for the TomTom and Wayfinder plugins.
#
# Confirms, without launching the game, that:
#   1. the plugin identifies itself as the expected edition, and excludes its sibling edition
#   2. every [HarmonyPatch] target type and method still exists in the shipped game assemblies
#   3. every private member reached by reflection still exists
#   4. map markers can only ever be created local-only (never shared through a Cartography Table)
#   5. Wayfinder contains no coordinate-entry code at all; TomTom still does
#   6. every assembly the plugin references can be resolved from the game folder
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
$expectedVersion = "1.0.0"

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

$plug = [Mono.Cecil.ModuleDefinition]::ReadModule($Plugin)
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
        $hit = $gt.Methods | Where-Object { $_.Name -eq $methodName }
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
# (type, member, kind) - keep in step with MinimapAccess.
$reflected = @(
    @("Minimap", "ScreenToWorldPoint", "method"),
    @("Minimap", "GetClosestPinToCursor", "method"),
    @("Minimap", "HidePinTextInput", "method"),
    @("Minimap", "m_pins", "field"),
    @("Minimap", "PinInteractRadius", "property")
)
foreach ($r in $reflected) {
    $checks++
    $gt = Find-GameType $r[0]
    if (-not $gt) { Write-Output ("  FAIL  type {0} missing" -f $r[0]); $failures++; continue }

    $found = switch ($r[2]) {
        "method"   { @($gt.Methods    | Where-Object { $_.Name -eq $r[1] }).Count -gt 0 }
        "field"    { @($gt.Fields     | Where-Object { $_.Name -eq $r[1] }).Count -gt 0 }
        "property" { @($gt.Properties | Where-Object { $_.Name -eq $r[1] }).Count -gt 0 }
    }
    if ($found) { Write-Output ("  ok    {0}.{1}" -f $r[0], $r[1]) }
    else { Write-Output ("  FAIL  {0}.{1} not found" -f $r[0], $r[1]); $failures++ }
}

Write-Output ""
Write-Output "== multiplayer safety: markers must stay local-only =="
# Valheim shares pins through Minimap.GetSharedMapData (the Cartography Table) and saves them through
# Minimap.GetMapData, and both iterate ONLY over pins whose m_save flag is true. So a Waypointer marker
# is local-only precisely as long as it is created with save:false and never has m_save set to true.
#
# The plugin funnels all pin creation through one helper, so this checks two things:
#   - AddPin is called from exactly one place (a second call site means the guarantee has been forked)
#   - nothing anywhere in the plugin ever stores 'true' into PinData.m_save
$addPinCalls = 0
$setsSaveTrue = 0
foreach ($t in $plug.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $prev = $null
        foreach ($i in $m.Body.Instructions) {
            $op = "$($i.Operand)"
            if ($i.OpCode.Name -like "call*" -and $op -like "*Minimap::AddPin*") {
                $addPinCalls++
                Write-Output ("  note  AddPin called from {0}.{1}" -f $t.Name, $m.Name)
            }
            if ($i.OpCode.Name -eq "stfld" -and $op -like "*PinData::m_save*") {
                $checks++
                if ($prev -ne $null -and $prev.OpCode.Name -eq "ldc.i4.1") {
                    Write-Output ("  FAIL  {0}.{1} sets PinData.m_save = TRUE - markers would be shared!" -f $t.Name, $m.Name)
                    $setsSaveTrue++
                    $failures++
                } else {
                    Write-Output ("  ok    {0}.{1} sets PinData.m_save = false" -f $t.Name, $m.Name)
                }
            }
            $prev = $i
        }
    }
}

$checks++
if ($addPinCalls -eq 1) {
    Write-Output "  ok    exactly one AddPin call site (the local-only helper)"
} else {
    Write-Output ("  FAIL  expected exactly 1 AddPin call site, found {0} - review each for save:false" -f $addPinCalls)
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
    @("Waypointer.WaypointWindow", "ApplyInput")                     # its Add / Replace buttons
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
