#!/bin/bash
# Runs the CoordinateParser tests.
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

if command -v dotnet >/dev/null 2>&1; then
  echo "== tests on .NET =="
  dotnet run --project "$HERE/tests/Waypointer.Tests.csproj" -v quiet || STATUS=1
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
    MONO_PATH="$(cygpath -w "$GAME/valheim_Data/Managed")" "$MONO" "$(cygpath -w "$OUT/partest.exe")" || STATUS=1
  else
    echo "== tests on Mono, with the Editor's mscorlib - game not found at '$GAME' ($MONO) =="
    "$MONO" "$(cygpath -w "$OUT/partest.exe")" || STATUS=1
  fi
elif ! command -v dotnet >/dev/null 2>&1; then
  echo "(no .NET SDK and no Unity Mono found - falling back to .NET Framework and the C# 5 compiler)"
  build_partest
  "$OUT/partest.exe" || STATUS=1
fi
exit $STATUS
