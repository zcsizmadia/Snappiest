#!/usr/bin/env bash
# Runs the benchmarks on a remote Linux host, one in-process BenchmarkDotNet run per (class, file) pinned to its own
# physical core, then prints the combined result tables.
#
# Usage: scripts/bench-remote.sh [host] [framework] [classes] [extra BenchmarkDotNet args...]
#   host       default bench-host.example
#   framework  net10.0 (default) or net8.0
#   classes    comma separated: Block,Stream,SmallBlock,Corpus,Crc (default Block,Stream,SmallBlock,Corpus)
# Env: BENCH_CORES   physical cores to use (default "33-63", NUMA node 1 on the EPYC host)
#      BENCH_JOB     BenchmarkDotNet job (default "medium")
#      BENCH_ENV     extra environment for the benchmark processes, e.g. "DOTNET_EnableAVX2=0"
#      BENCH_LABEL   suffix for the results directory
#      BENCH_NOSYNC  1 to skip syncing and building (when running several configurations at once)
#      BENCH_REMOTE_DIR  remote working copy (default ~/snappysimd); use another for experiments
set -euo pipefail

HOST="${1:-bench-host.example}"
FRAMEWORK="${2:-net10.0}"
CLASSES="${3:-Block,Stream,SmallBlock,Corpus}"
shift $(( $# > 3 ? 3 : $# )) || true
EXTRA_ARGS="$*"
CORES="${BENCH_CORES:-33-63}"
JOB="${BENCH_JOB:-medium}"
BENCH_ENV="${BENCH_ENV:-}"
LABEL="${BENCH_LABEL:-}"
NOSYNC="${BENCH_NOSYNC:-0}"
REMOTE_DIR="${BENCH_REMOTE_DIR:-~/snappysimd}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "$NOSYNC" != 1 ]]; then
echo "Syncing to $HOST..."
tar -C "$ROOT" -czf - --exclude=.git --exclude='bin' --exclude='obj' --exclude='TestResults' \
    --exclude='BenchmarkDotNet.Artifacts' --exclude='bench-results' . \
  | ssh -o BatchMode=yes "$HOST" "mkdir -p $REMOTE_DIR && tar -xzf - -C $REMOTE_DIR"
fi

ssh -o BatchMode=yes "$HOST" bash -s -- "$FRAMEWORK" "$CLASSES" "$CORES" "$JOB" "${LABEL:-_}" "$NOSYNC" "${BENCH_ENV:-_}" "$REMOTE_DIR" "$EXTRA_ARGS" <<'REMOTE'
set -euo pipefail
# Core sets for multi-threaded benchmark jobs, one per job (16 physical cores each)
PARALLEL_CORE_SETS=(33-48 0-15 16-31)
PARALLEL_JOB=0
FRAMEWORK="$1"; CLASSES="$2"; CORES="$3"; JOB="$4"; LABEL="${5#_}"; NOSYNC="$6"; BENCH_ENV="${7#_}"; REMOTE_DIR="$8"; EXTRA_ARGS="${*:9}"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
eval cd "$REMOTE_DIR"

# net11.0 is preview: build it with the installed SDK 11 (the synced global.json pins SDK 10)
if [[ "$FRAMEWORK" == net11.0 && "$NOSYNC" != 1 ]]; then
  SDK11=$(dotnet --list-sdks | awk '/^11./{v=$1} END{print v}')
  printf '{ "sdk": { "version": "%s", "rollForward": "latestFeature", "allowPrerelease": true } }
' "$SDK11" > global.json
fi

[[ "$NOSYNC" == 1 ]] || dotnet build benchmarks/SnappySimd.Benchmarks -c Release -f "$FRAMEWORK" -v q -nologo | grep -E "error|Warn|Elapsed" || true
DLL="benchmarks/SnappySimd.Benchmarks/bin/Release/$FRAMEWORK/SnappySimd.Benchmarks.dll"

# Expand the core list ("33-63" or "33,35,40-45")
CORE_LIST=()
IFS=',' read -ra RANGES <<< "$CORES"
for r in "${RANGES[@]}"; do
  if [[ "$r" == *-* ]]; then for ((c=${r%-*}; c<=${r#*-}; c++)); do CORE_LIST+=("$c"); done; else CORE_LIST+=("$r"); fi
done

ALL_FILES="alice29.txt asyoulik.txt fireworks.jpeg geo.protodata html html_x_4 kppkn.gtb lcet10.txt paper-100k.pdf plrabn12.txt urls.10K json_api.json json_indented.json events.ndjson"
JOBS=()
IFS=',' read -ra CLS <<< "$CLASSES"
for cls in "${CLS[@]}"; do
  case "$cls" in
    Block)      for f in $ALL_FILES; do JOBS+=("BlockBenchmarks|$f"); done ;;
    Stream)     for f in alice29.txt fireworks.jpeg html_x_4 urls.10K json_api.json events.ndjson; do JOBS+=("StreamBenchmarks|$f"); done ;;
    SmallBlock) for f in html fireworks.jpeg; do JOBS+=("SmallBlockBenchmarks|$f"); done ;;
    Corpus)     JOBS+=("CorpusBenchmarks|") ;;
    Crc)        JOBS+=("Crc32CBenchmarks|") ;;
    Parallel)   for f in json_api.json html_x_4 urls.10K; do JOBS+=("ParallelBenchmarks|$f"); done ;;
    Tiering)    for w in none small large; do JOBS+=("TieringBenchmarks|$w"); done ;;
    *)          JOBS+=("$cls|") ;;
  esac
done

if (( ${#JOBS[@]} > ${#CORE_LIST[@]} )); then
  echo "More jobs (${#JOBS[@]}) than cores (${#CORE_LIST[@]}); they will share cores round-robin." >&2
fi

RUN="bench-results/$(date +%Y%m%d-%H%M%S)-$FRAMEWORK${LABEL:+-$LABEL}"
mkdir -p "$RUN"
echo "Running ${#JOBS[@]} jobs on cores ${CORES} (job=$JOB) -> $RUN"

i=0
for j in "${JOBS[@]}"; do
  cls="${j%%|*}"; file="${j#*|}"
  core="${CORE_LIST[$(( i % ${#CORE_LIST[@]} ))]}"
  # Multi-threaded benchmarks get a core range instead of a single core
  if [[ "$cls" == ParallelBenchmarks ]]; then
    core="${PARALLEL_CORE_SETS[$(( PARALLEL_JOB % ${#PARALLEL_CORE_SETS[@]} ))]}"
    PARALLEL_JOB=$((PARALLEL_JOB + 1))
  fi
  name="$cls${file:+-$file}"
  ( env $BENCH_ENV BENCH_FILES="$file" BENCH_WARMUP="$file" taskset -c "$core" dotnet "$DLL" --filter "SnappySimd.Benchmarks.$cls.*" \
      --inProcess --job "$JOB" --artifacts "$RUN/$name" --exporters github $EXTRA_ARGS > "$RUN/$name.log" 2>&1 \
    || echo "FAILED: $name (see $RUN/$name.log)" ) &
  i=$((i + 1))
done
wait

# Print one combined table per class, rows only (BenchmarkDotNet writes a header per run)
for cls in $(printf '%s\n' "${JOBS[@]}" | cut -d'|' -f1 | sort -u); do
  echo; echo "### $cls ($FRAMEWORK)"
  first=1
  for f in $(ls "$RUN"/$cls*/results/*-report-github.md 2>/dev/null | sort); do
    if (( first )); then sed -n '/^| Method/,$p' "$f" | sed -n '1,2p'; first=0; fi
    sed -n '/^| Method/,$p' "$f" | tail -n +3 | grep -v '^$' || true
  done
done
echo; echo "Environment:"; ls "$RUN"/*/results/*-report-github.md | head -1 | xargs sed -n '/^```/,/^```/p' | head -12
REMOTE
