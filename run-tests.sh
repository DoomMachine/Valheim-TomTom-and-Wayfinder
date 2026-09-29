#!/bin/bash
# Runs the tests: the coordinate parser and formatter, the crash-safe route save (SafeFile), the guarded
# key reads (Hotkeys), and the location search's Unity-free rules and route (SearchRules, SearchCatalog,
# RoutePlanner).
#
# 1. On .NET (modern Roslyn), when the SDK is installed.
# 2. On Mono with the game's own mscorlib.dll, when a Unity Editor is installed. The game runs Mono, and
#    Mono's number formatting differs from .NET's in places (-0.4 formats as "0" in the game and "-0" on
#    .NET), so this is the run that stands for the game. UNITY_MONO picks a mono.exe explicitly;
#    VALHEIM_DIR points at the game, as for build.sh.
# 3. With neither, on .NET Framework via the legacy csc.exe that ships with Windows (C# 5 only).
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
GAME="${VALHEIM_DIR:-E:/SteamLibrary/steamapps/common/Valheim}"
STATUS=0

# A test that never ends would hang the run, so each run gets TEST_TIMEOUT seconds (default 300); running out is a
# failure. GNU timeout exits 124 and also ends the program's own children. /usr/bin/timeout is named outright:
# Windows' own timeout.exe (a "wait N seconds" prompt) can come first on PATH. Without it, runs are not limited.
LIMIT="${TEST_TIMEOUT:-300}"
TIMEOUT_BIN=""
if [ -x /usr/bin/timeout ]; then TIMEOUT_BIN=/usr/bin/timeout; fi
limited() {
  if [ -z "$TIMEOUT_BIN" ]; then "$@"; return $?; fi
  local rc=0
  "$TIMEOUT_BIN" "$LIMIT" "$@" || rc=$?
  if [ "$rc" -eq 124 ]; then echo "  FAIL timed out after $LIMIT s (TEST_TIMEOUT) - a test that never ends?"; fi
  return $rc
}

if command -v dotnet >/dev/null 2>&1; then
  echo "== tests on .NET =="
  limited dotnet run --project "$HERE/tests/Waypointer.Tests.csproj" -v quiet || STATUS=1
fi

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
OUT="$HERE/build/tests"
build_partest() {
  mkdir -p "$OUT"
  RSP="$OUT/_tests.rsp"
  {
    echo "-nologo"
    echo "-target:exe"
    echo "-langversion:5"
    echo "-out:\"$(cygpath -w "$OUT/partest.exe")\""
    echo "\"$(cygpath -w "$HERE/tests/Shims.cs")\""
    echo "\"$(cygpath -w "$HERE/tests/TestMain.cs")\""
    echo "\"$(cygpath -w "$HERE/src/CoordinateParser.cs")\""
    echo "\"$(cygpath -w "$HERE/src/CoordinateFormat.cs")\""
    echo "\"$(cygpath -w "$HERE/src/SafeFile.cs")\""
    echo "\"$(cygpath -w "$HERE/src/Hotkeys.cs")\""
    echo "\"$(cygpath -w "$HERE/src/RoutePlanner.cs")\""
    echo "\"$(cygpath -w "$HERE/src/SearchCatalog.cs")\""
    echo "\"$(cygpath -w "$HERE/src/SearchRules.cs")\""
    echo "\"$(cygpath -w "$HERE/src/FindProtocol.cs")\""
    echo "\"$(cygpath -w "$HERE/src/CaptionFit.cs")\""
    echo "\"$(cygpath -w "$HERE/src/RouteRead.cs")\""
    echo "\"$(cygpath -w "$HERE/src/ArrivalRules.cs")\""
  } > "$RSP"
  "$CSC" "@$(cygpath -w "$RSP")"
}

# The Editor that matches the game's Unity version first; the first match wins.
MONO="${UNITY_MONO:-}"
if [ -z "$MONO" ]; then
  for m in "/c/Program Files/Unity/Hub/Editor/6000.0.75"*/Editor/Data/MonoBleedingEdge/bin/mono.exe \
           "/c/Program Files/Unity/Hub/Editor/"*/Editor/Data/MonoBleedingEdge/bin/mono.exe \
           "/c/Program Files/Unity "*/Editor/Data/MonoBleedingEdge/bin/mono.exe; do
    if [ -f "$m" ]; then MONO="$m"; break; fi
  done
fi

if [ -n "$MONO" ]; then
  build_partest
  if [ -f "$GAME/valheim_Data/Managed/mscorlib.dll" ]; then
    echo "== tests on Mono, with the game's mscorlib ($MONO) =="
    MONO_PATH="$(cygpath -w "$GAME/valheim_Data/Managed")" limited "$MONO" "$(cygpath -w "$OUT/partest.exe")" || STATUS=1
  else
    echo "== tests on Mono, with the Editor's mscorlib - game not found at '$GAME' ($MONO) =="
    limited "$MONO" "$(cygpath -w "$OUT/partest.exe")" || STATUS=1
  fi
elif ! command -v dotnet >/dev/null 2>&1; then
  echo "(no .NET SDK and no Unity Mono found - falling back to .NET Framework and the C# 5 compiler)"
  build_partest
  limited "$OUT/partest.exe" || STATUS=1
fi
exit $STATUS
