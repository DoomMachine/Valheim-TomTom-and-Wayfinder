#!/bin/bash
# SDK-free fallback: compiles both editions with the legacy C# 5 compiler that ships with Windows.
#
#   ./build.sh                 # both editions
#   ./build.sh TomTom          # just one
#
# This only compiles, into build/fallback/<Edition>/. Packaging and installing into the game are done
# by the real build (dotnet build Waypointer.slnx), which is the one to use whenever the .NET SDK is
# available. This script exists so the plugins can still be built on a machine without it - which is
# also why the source is kept to C# 5.
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"
# Where Valheim is installed. Point it somewhere else with:  VALHEIM_DIR=/d/Games/Valheim ./build.sh
GAME="${VALHEIM_DIR:-E:/SteamLibrary/steamapps/common/Valheim}"
MG="$GAME/valheim_Data/Managed"
BE="$GAME/BepInEx/core"
if [ ! -d "$MG" ] || [ ! -d "$BE" ]; then
  echo "Valheim (with BepInEx) not found at '$GAME'." >&2
  echo "Set VALHEIM_DIR to the folder holding valheim.exe, e.g. VALHEIM_DIR=/d/Games/Valheim $0" >&2
  exit 2
fi
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

build_edition() {
  local edition="$1"
  local outdir="$HERE/build/fallback/$edition"
  mkdir -p "$outdir"
  local rsp="$outdir/_build.rsp"
  {
    echo "-nologo"
    echo "-nostdlib+"
    echo "-target:library"
    echo "-optimize+"
    echo "-langversion:5"
    echo "-warn:4"
    # Wayfinder is TomTom minus coordinate entry; the symbol compiles that path out (see src/Edition.cs).
    if [ "$edition" = "Wayfinder" ]; then echo "-define:WAYFINDER"; fi
    echo "-out:\"$(cygpath -w "$outdir/$edition.dll")\""
    for r in mscorlib System System.Core netstandard \
             UnityEngine UnityEngine.CoreModule UnityEngine.UI UnityEngine.UIModule \
             UnityEngine.TextRenderingModule UnityEngine.IMGUIModule UnityEngine.ImageConversionModule \
             UnityEngine.PhysicsModule UnityEngine.AnimationModule \
             Unity.InputSystem Unity.TextMeshPro \
             assembly_valheim assembly_utils assembly_guiutils gui_framework Splatform ; do
      if [ -f "$MG/$r.dll" ]; then echo "-r:\"$MG/$r.dll\""; fi
    done
    for r in BepInEx 0Harmony ; do
      if [ -f "$BE/$r.dll" ]; then echo "-r:\"$BE/$r.dll\""; fi
    done
    # Every source file is passed; CoordinateParser.cs and CoordinateFormat.cs compile to nothing under WAYFINDER.
    for f in "$HERE"/src/*.cs; do echo "\"$(cygpath -w "$f")\""; done
  } > "$rsp"

  echo "== compiling $edition (C# 5) =="
  "$CSC" -noconfig "@$(cygpath -w "$rsp")"
  echo "== built: $outdir/$edition.dll =="
}

if [ "$#" -eq 0 ]; then
  build_edition TomTom
  build_edition Wayfinder
else
  for e in "$@"; do
    case "$e" in
      TomTom|Wayfinder) build_edition "$e" ;;
      *) echo "unknown edition '$e' - use TomTom or Wayfinder" >&2; exit 2 ;;
    esac
  done
fi
