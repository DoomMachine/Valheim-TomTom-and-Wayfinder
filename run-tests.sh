#!/bin/bash
# Runs the CoordinateParser tests.
#
# Prefers the .NET SDK (modern Roslyn, so the tests keep working whatever C# version the source uses).
# Falls back to the legacy csc.exe that ships with Windows, which needs no SDK but only speaks C# 5.
set -eu
HERE="$(cd "$(dirname "$0")" && pwd)"

if command -v dotnet >/dev/null 2>&1; then
  exec dotnet run --project "$HERE/tests/Waypointer.Tests.csproj" -v quiet
fi

echo "(no .NET SDK found - falling back to the legacy C# 5 compiler)"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
OUT="$HERE/build/tests"
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
"$OUT/partest.exe"
