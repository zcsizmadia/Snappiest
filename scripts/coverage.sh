#!/usr/bin/env bash
# Runs the tests under several CPU feature configurations with coverlet and merges the results, so the SIMD paths
# and their scalar fallbacks are all measured. Coverlet merges branch coverage per branch (Cobertura merges cannot),
# and the result can be chained across machines, e.g. x64 then arm64 in CI.
#
# Usage: scripts/coverage.sh [framework] [coverage.json to merge with]
# Output: TestResults/coverage/coverage.json, coverage.cobertura.xml, Summary.txt
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="/c/Program Files/dotnet/dotnet.exe"
FRAMEWORK="${1:-net10.0}"
MERGE_WITH="${2:-}"

OUT="$ROOT/TestResults/coverage"
rm -rf "$OUT"
mkdir -p "$OUT"
[[ -n "$MERGE_WITH" ]] && cp "$MERGE_WITH" "$OUT/coverage.json"

"$DOTNET" tool restore >/dev/null
"$DOTNET" build tests/SnappySimd.Tests -c Release -f "$FRAMEWORK" -v q -nologo

BIN="$ROOT/tests/SnappySimd.Tests/bin/Release/$FRAMEWORK"
EXE="$BIN/SnappySimd.Tests"
[[ -f "$EXE.exe" ]] && EXE="$EXE.exe"
if command -v cygpath >/dev/null 2>&1; then
  BIN="$(cygpath -w "$BIN")"; EXE="$(cygpath -w "$EXE")"; OUTW="$(cygpath -w "$OUT")\\"
else
  OUTW="$OUT/"
fi

for cfg in default:_ noavx2:DOTNET_EnableAVX2=0 nohw:DOTNET_EnableHWIntrinsic=0; do
  name="${cfg%%:*}"; envv="${cfg#*:}"; [[ "$envv" == _ ]] && envv=""
  merge=()
  [[ -f "$OUT/coverage.json" ]] && merge=(--merge-with "$OUT/coverage.json")
  echo "== $name"
  env $envv "$DOTNET" tool run coverlet "$BIN" --target "$EXE" --include "[SnappySimd]*" \
    --format json --format cobertura --output "$OUTW" "${merge[@]}" | grep -E "failed:|succeeded:|^\| Total"
done

"$DOTNET" tool run reportgenerator -reports:"$OUT/coverage.cobertura.xml" -targetdir:"$OUT" \
  -reporttypes:"TextSummary;Html;Badges" >/dev/null
sed -n '1,20p' "$OUT/Summary.txt"
