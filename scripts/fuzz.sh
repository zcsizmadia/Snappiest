#!/usr/bin/env bash
# Coverage-guided fuzzing with SharpFuzz and libFuzzer (libfuzzer-dotnet), Linux x64. Nothing needs root: the tools
# go into the work directory.
#
# Usage: scripts/fuzz.sh <command> [args]
#   setup                       install sharpfuzz (SharpFuzz.CommandLine) and libfuzzer-dotnet (checksum verified)
#   build                       publish tests/Snappiest.Fuzz and instrument its Snappiest.dll
#   seed                        write the seed corpus (testdata blocks and streams, edge cases) to $FUZZ_DIR/seeds
#   run <target> [seconds] [libFuzzer args...]
#                               fuzz block, stream or compress (default 600 s); new inputs go to $FUZZ_DIR/corpus,
#                               crashes to $FUZZ_DIR/crashes/<target>; exits non-zero if one was found
#   merge <target>              minimize the target's corpus (keeps only inputs that add coverage)
#   replay <target> <file|dir>...
#                               run inputs once without libFuzzer, e.g. a crash file (prints the exception)
#   all [seconds]               setup, build, seed, then run every target (default 600 s each)
#
# Env: FUZZ_DIR   work directory (default <repo>/.fuzz): tools/, bin/, seeds/, corpus/, crashes/, logs/
#      FUZZ_JOBS  libFuzzer worker processes per run (default 1); each also starts a .NET process
#      FUZZ_CPUS  CPU list to pin the run to with taskset, e.g. "48-63" (default: no pinning)
#      FUZZ_ENV   extra environment for the fuzzed process, e.g. "DOTNET_EnableAVX2=0"
#      DOTNET     dotnet executable (default: dotnet on PATH; DOTNET_ROOT must point at a .NET 10 runtime if it
#                 is not installed system-wide, for the fuzzing executable's apphost)
#      FUZZ_BUILD_DRIVER=1  build libfuzzer-dotnet from source with clang even where the prebuilt one runs
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"

SHARPFUZZ_VERSION=2.3.0
LIBFUZZER_DOTNET_RELEASE=v2025.05.02.0904
# The release publishes no checksums: these are the SHA-256 of its libfuzzer-dotnet-ubuntu and of the
# libfuzzer-dotnet.cc source at its tag
LIBFUZZER_DOTNET_SHA256=c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958
LIBFUZZER_DOTNET_SOURCE_SHA256=90f019e2e9ad3a0b93c7ecc2c5afb2fbfc8b5aab6aac51c7e0d349ec79354f36

FUZZ_DIR="${FUZZ_DIR:-$ROOT/.fuzz}"
TOOLS="$FUZZ_DIR/tools"
BIN="$FUZZ_DIR/bin"
DOTNET="${DOTNET:-dotnet}"
TARGETS=(block stream compress)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

die() { echo "error: $*" >&2; exit 1; }

check_target() {
  [[ " ${TARGETS[*]} " == *" $1 "* ]] || die "unknown target '$1' (expected: ${TARGETS[*]})"
}

# libFuzzer flags per target. -max_len: blocks up to 64KB reach every decoder path; streams need room for two
# chunks to exercise parallel batches; compress inputs cross the 64KB fragment boundary (and can repeat themselves).
target_flags() {
  case "$1" in
    block)    echo "-max_len=65536" ;;
    stream)   echo "-max_len=131072 -dict=$ROOT/tests/Snappiest.Fuzz/stream.dict" ;;
    compress) echo "-max_len=70000" ;;
  esac
}

cmd_setup() {
  mkdir -p "$TOOLS"
  if [[ ! -x "$TOOLS/sharpfuzz" ]] || ! "$DOTNET" tool list --tool-path "$TOOLS" | grep -qi "sharpfuzz.commandline *$SHARPFUZZ_VERSION"; then
    "$DOTNET" tool install SharpFuzz.CommandLine --version "$SHARPFUZZ_VERSION" --tool-path "$TOOLS" 2>/dev/null \
      || "$DOTNET" tool update SharpFuzz.CommandLine --version "$SHARPFUZZ_VERSION" --tool-path "$TOOLS"
  fi

  # The prebuilt driver needs glibc 2.38 (Ubuntu 24.04); on older systems it is built from the same release's
  # source with clang (-fsanitize=fuzzer, e.g. Ubuntu's clang package)
  local mode=prebuilt glibc
  glibc="$(getconf GNU_LIBC_VERSION | awk '{print $2}')"
  if [[ "${FUZZ_BUILD_DRIVER:-0}" == 1 || "$(printf '%s\n' 2.38 "$glibc" | sort -V | head -1)" != 2.38 ]]; then
    mode=source
  fi

  local stamp="$mode $LIBFUZZER_DOTNET_RELEASE" base="https://github.com/Metalnem/libfuzzer-dotnet"
  if [[ ! -x "$TOOLS/libfuzzer-dotnet" || "$(cat "$TOOLS/libfuzzer-dotnet.stamp" 2>/dev/null)" != "$stamp" ]]; then
    rm -f "$TOOLS/libfuzzer-dotnet" "$TOOLS/libfuzzer-dotnet.stamp"
    if [[ "$mode" == prebuilt ]]; then
      download "$base/releases/download/$LIBFUZZER_DOTNET_RELEASE/libfuzzer-dotnet-ubuntu" "$LIBFUZZER_DOTNET_SHA256" "$TOOLS/libfuzzer-dotnet"
      chmod +x "$TOOLS/libfuzzer-dotnet"
    else
      download "https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/$LIBFUZZER_DOTNET_RELEASE/libfuzzer-dotnet.cc" \
        "$LIBFUZZER_DOTNET_SOURCE_SHA256" "$TOOLS/libfuzzer-dotnet.cc"
      "${CC:-clang}" -O2 -fsanitize=fuzzer "$TOOLS/libfuzzer-dotnet.cc" -o "$TOOLS/libfuzzer-dotnet"
    fi
    echo "$stamp" > "$TOOLS/libfuzzer-dotnet.stamp"
  fi
  echo "Tools in $TOOLS: sharpfuzz $SHARPFUZZ_VERSION, libfuzzer-dotnet $LIBFUZZER_DOTNET_RELEASE ($mode)"
}

# download <url> <sha256> <file>: fails (and leaves nothing behind) unless the checksum matches
download() {
  curl -fsSL --retry 3 -o "$3.download" "$1"
  echo "$2  $3.download" | sha256sum -c --quiet || { rm -f "$3.download"; die "checksum mismatch for $1"; }
  mv "$3.download" "$3"
}

cmd_build() {
  [[ -x "$TOOLS/sharpfuzz" ]] || die "run '$0 setup' first"
  # Instrumentation rewrites the published Snappiest.dll in place: always publish afresh
  rm -rf "$BIN"
  "$DOTNET" publish tests/Snappiest.Fuzz -c Release -o "$BIN" -v q -nologo
  # The tool may target an older runtime than the installed one
  DOTNET_ROLL_FORWARD=Major "$TOOLS/sharpfuzz" "$BIN/Snappiest.dll"
  echo "Instrumented $BIN/Snappiest.dll"
}

cmd_seed() {
  [[ -x "$BIN/Snappiest.Fuzz" ]] || die "run '$0 build' first"
  rm -rf "$FUZZ_DIR/seeds"
  "$BIN/Snappiest.Fuzz" seeds "$ROOT/testdata" "$FUZZ_DIR/seeds"
  # Inputs saved by the unit-test fuzzers (tests/Snappiest.Tests/bin/*/*/fuzz-failures) are worth keeping too
  local file
  for file in tests/Snappiest.Tests/bin/*/*/fuzz-failures/*.bin; do
    [[ -f "$file" ]] || continue
    case "$(basename "$file")" in
      RoundTrip*) cp "$file" "$FUZZ_DIR/seeds/compress/" ;;
      *Stream*)   cp "$file" "$FUZZ_DIR/seeds/stream/" ;;
      *)          cp "$file" "$FUZZ_DIR/seeds/block/" ;;
    esac
  done
  for target in "${TARGETS[@]}"; do
    echo "$target: $(find "$FUZZ_DIR/seeds/$target" -type f | wc -l) seed(s)"
  done
}

cmd_run() {
  local target="${1:?target}"; shift
  check_target "$target"
  local seconds="${1:-600}"; shift || true
  [[ -x "$TOOLS/libfuzzer-dotnet" ]] || die "run '$0 setup' first"
  [[ -x "$BIN/Snappiest.Fuzz" ]] || die "run '$0 build' first"
  [[ -d "$FUZZ_DIR/seeds/$target" ]] || die "run '$0 seed' first"

  local corpus="$FUZZ_DIR/corpus/$target" crashes="$FUZZ_DIR/crashes/$target" logs="$FUZZ_DIR/logs/$target"
  mkdir -p "$corpus" "$crashes" "$logs"
  local jobs="${FUZZ_JOBS:-1}" status
  local pin=() parallel=()
  [[ -n "${FUZZ_CPUS:-}" ]] && pin=(taskset -c "$FUZZ_CPUS")
  # Several workers: each logs to $FUZZ_DIR/logs/<target>/fuzz-<n>.log and they share the corpus directory
  (( jobs > 1 )) && parallel=(-jobs="$jobs" -workers="$jobs")

  # libFuzzer's -rss_limit_mb only sees its own (driver) process: cap the .NET heap separately, so a runaway
  # allocation fails as an OutOfMemoryException crash instead of taking the machine down
  # shellcheck disable=SC2046,SC2086
  (cd "$logs" && env DOTNET_GCHeapHardLimit=0x80000000 ${FUZZ_ENV:-} "${pin[@]}" "$TOOLS/libfuzzer-dotnet" \
    --target_path="$BIN/Snappiest.Fuzz" --target_arg="$target" \
    $(target_flags "$target") -timeout=30 -rss_limit_mb=4096 -max_total_time="$seconds" \
    -print_final_stats=1 -artifact_prefix="$crashes/" "${parallel[@]}" "$@" \
    "$corpus" "$FUZZ_DIR/seeds/$target") \
    && status=0 || status=$?

  if (( jobs > 1 )); then
    # Per worker: final features (ft) and corpus, executions and speed
    grep -h -E "^#[0-9]+ +DONE|stat::number_of_executed_units|stat::average_exec_per_sec|ERROR|Test unit written" \
      "$logs"/fuzz-*.log 2>/dev/null || true
  fi
  echo "$target: corpus $(find "$corpus" -type f | wc -l) file(s), crashes $(find "$crashes" -type f | wc -l)"
  return "$status"
}

cmd_merge() {
  local target="${1:?target}"
  check_target "$target"
  local corpus="$FUZZ_DIR/corpus/$target" merged="$FUZZ_DIR/corpus/$target.merged"
  if [[ -z "$(ls -A "$corpus" 2>/dev/null)" ]]; then
    echo "$target: no corpus to merge"
    return 0
  fi
  [[ -x "$BIN/Snappiest.Fuzz" ]] || die "run '$0 build' first"
  rm -rf "$merged"; mkdir -p "$merged"
  # shellcheck disable=SC2046
  "$TOOLS/libfuzzer-dotnet" --target_path="$BIN/Snappiest.Fuzz" --target_arg="$target" \
    $(target_flags "$target") -timeout=30 -merge=1 "$merged" "$corpus"
  rm -rf "$corpus"; mv "$merged" "$corpus"
  echo "$target: corpus $(find "$corpus" -type f | wc -l) file(s)"
}

cmd_replay() {
  local target="${1:?target}"; shift
  check_target "$target"
  (( $# > 0 )) || die "no inputs given"
  "$BIN/Snappiest.Fuzz" "$target" "$@"
}

command="${1:-}"; shift || true
case "$command" in
  setup)  cmd_setup ;;
  build)  cmd_build ;;
  seed)   cmd_seed ;;
  run)    cmd_run "$@" ;;
  merge)  cmd_merge "$@" ;;
  replay) cmd_replay "$@" ;;
  all)
    cmd_setup; cmd_build; cmd_seed
    failed=0
    for target in "${TARGETS[@]}"; do cmd_run "$target" "${1:-600}" || failed=1; done
    exit "$failed" ;;
  *) awk 'NR > 1 && /^set -euo/ { exit } NR > 1' "$0"; exit 2 ;;
esac
