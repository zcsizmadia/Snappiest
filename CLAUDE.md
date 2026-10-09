# Snappiest

High-performance Snappy (raw block + framing format) for .NET 8+, using SIMD and hardware intrinsics on x64 and arm64.

## Goals and rules

- **Must beat Snappier** (NuGet `Snappier`, the main competitor) on speed. Any change to a hot path needs a
  before/after benchmark against Snappier; never trade correctness for speed.
- **Drop-in migration from Snappier**: `Snappiest.Snappy` and `Snappiest.SnappyStream` keep Snappier's public type
  names, member signatures, parameter names and exception types. Changing `using Snappier;` to `using Snappiest;`
  must be the only code change. New APIs may be added, existing ones must not diverge.
  This is **source portability, not byte-for-byte output compatibility**: compressed output only has to be valid
  Snappy (readable by Snappier and google/snappy), so the compression algorithm may differ from Snappier's. Keep the
  compressed size no worse than Snappier's (enforced by `InteropTests.Block_CompressionRatio_NotWorseThanSnappier`).
  Don't add hidden multithreading to the Snappier-compatible methods; parallelism would be an opt-in extra overload.
- **net8.0+ only** (targets `net8.0;net10.0`). No netstandard / .NET Framework code paths.
- **Tests: TUnit only**, coverage at least 90% (currently ~98% line), measured across CPU-feature configurations.
- License BSD-3-Clause; the hot loops are ports of google/snappy, keep the attribution in LICENSE.

## Layout

- `src/Snappiest/` library. Hot paths are in `Internal/BlockDecompressor.cs` (port of Google's
  `DecompressBranchless`), `Internal/BlockCompressor.cs` (`CompressFragment`), `Internal/SimdCopy.cs`, `Internal/Crc32C.cs`.
- `tests/Snappiest.Tests/` TUnit tests, including interop with the real Snappier package and guard-page tests.
- `benchmarks/Snappiest.Benchmarks/` BenchmarkDotNet, Snappier as the baseline.
- `testdata/` shared corpus (see its README for provenance). Its CRLF and no-final-newline files are intentional
  (`-text` in `.gitattributes`: tests compare bytes).
- `scripts/`: benchmark runners and generators (`bench-remote.sh`, `bench-local.ps1`, `bench-table.mjs`,
  `bench-charts.mjs`), `coverage.sh`, `fuzz.sh`, and `make-badge.mjs` (writes the static badges in `.github/badges/`,
  e.g. `node scripts/make-badge.mjs .NET "8.0 | 10.0" "#512bd4" .github/badges/dotnet.svg`).

## Commands

The `dotnet` CLI is at `C:\Program Files\dotnet\dotnet.exe` (not on PATH in Claude Code shells).

- Build: `dotnet build -c Release`
- Releases: `docs/releasing.md`. Bump `<VersionPrefix>` in `src/Snappiest/Snappiest.csproj` through a PR, then push a
  `v<version>` tag; `.github/workflows/release.yml` publishes to nuget.org (trusted publishing) and creates the GitHub
  release.
- Work on a branch and open a PR; don't commit straight to `main`.
- Tests: `dotnet test --project tests/Snappiest.Tests -c Release`
- Coverage across CPU features: `scripts/coverage.sh` (merges default, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`)
- Coverage-guided fuzzing (SharpFuzz + libFuzzer, Linux only; `tests/Snappiest.Fuzz`, targets `block`, `stream`,
  `compress`): `scripts/fuzz.sh setup|build|seed`, then `scripts/fuzz.sh run <target> <seconds>`; work dir `.fuzz/`.
  **Run it on the remote host, not this machine**: sync like `bench-remote.sh` does to a separate directory (e.g.
  `~/snappiest-fuzz`), `export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet`, and pin with
  `FUZZ_CPUS=48-63 FUZZ_JOBS=<n>` so benchmarks on other cores are not disturbed. The host's glibc is too old for the
  prebuilt libfuzzer-dotnet, so `setup` builds it from source with clang there. `cov:` in libFuzzer's output is
  meaningless with this driver (coverage arrives as extra counters); watch `ft:`. A crash file replays with
  `scripts/fuzz.sh replay <target> <file>`; fix the bug and add a TUnit regression test. Parallel paths run with
  coverage recording off (worker threads make edges random), but their results are still checked.
  Workflow: `.github/workflows/fuzz-coverage.yml` (weekly + manual, corpus in the Actions cache).
- Benchmarks: **run on the remote host, not this machine** (the user works on this machine):
  `scripts/bench-remote.sh [host] [net10.0|net8.0] [Block,Stream,SmallBlock] [BenchmarkDotNet args]`.
  The host comes from the first argument or `BENCH_HOST` (the reference host is 2x EPYC 7543, 128 threads, AVX2, no AVX-512). The script syncs the repo, builds
  once, and runs one in-process BenchmarkDotNet job per (class, file) pinned to its own physical core on NUMA node 1.
  `BENCH_JOB=short` for quick iteration (about +-10% noise), `medium` (default) for decisions.
  `scripts/bench-local.ps1` (PowerShell 7) is the Windows equivalent for the user's own runs: sequential, one process
  per file, each pinned to a P-core (an unpinned thread can land on an E-core, where Snappier compression is 3.5x
  slower). Local numbers differ from the README's EPYC ones (Snappier is ~35% slower on the i7-12800H), so label them.
- .NET 11 benchmarks are left out until .NET 11 is released (the README covers .NET 8 and .NET 10). To bring them
  back: add `net11.0` to the benchmark project's TargetFrameworks (with an SDK 11 condition while it is a preview),
  and in `scripts/bench-remote.sh` select the installed SDK 11 for `net11.0` (the synced global.json pins SDK 10).
  The commit that removed them shows both parts.

## Performance notes (learned the hard way)

- Do not use `stackalloc` or `[MethodImpl(AggressiveOptimization)]` in hot loops: both stop tiered compilation, so
  the method never gets Tier-1 (no dynamic PGO, `static readonly` table pointers are not folded into constants).
- RyuJIT often turns `cond ? a : b` into branches inside big loops; write hot selects as mask arithmetic.
- Dynamic PGO profiles each method from its first calls. A hot-loop method that small inputs call and immediately
  return from gets profiled as "loop body cold", and every later large input runs slow code (measured: 64KB html
  decompression went from 16% faster than Snappier to 11% slower after a few 64 byte calls). Check entry conditions
  in the caller so the loop method is only called when it will run. Benchmark mixed sizes in one process
  (SmallBlockBenchmarks does) to catch this; single-size benchmarks hide it.
- Blocks whose compressed tags are too short for the fast loop (<= 130 bytes) go to their own `DecompressSmall`, so
  streams of tiny messages get their own profile (64 B: 48 -> 40 ns). `TieringBenchmarks` (runner class `Tiering`)
  measures decoding after large-only, tiny-only and no warmup, each in its own process.
- **Never pass the decoder's `ip`/`op` by `ref` to a method that might not be inlined.** Whether the JIT inlines
  depends on the PGO profile (after a history of tiny blocks the fast-loop call is "rare" and stays a call), and a
  `ref` local of a non-inlined callee is address-exposed: the caller's whole slow path then keeps `ip`/`op` in
  memory (`[rbp-0x30]`). This was the cause of the old "40% slower with a ref helper" and of the 1-4 KB html losses
  on .NET 11 (1 KB: 236 -> 309-378 ns). `DecompressBranchless` takes them by value and returns a `Positions` struct.
  Inline-only helpers (`AggressiveInlining`, like `DecodeTagChecked`) are fine.
- Check codegen with `DOTNET_JitDisasm=<method> DOTNET_JitStdOutFile=/tmp/x.asm` on the remote host and look for
  the `Tier1` listing; `DOTNET_JitDisasmSummary=1` shows which methods tiered and whether they were inlined (a
  much smaller Tier1 code size for the caller means a callee stopped being inlined). `perf` works on the remote host
  (`perf stat -D <ms>` to skip warmup).
- Benchmarks that decode **one block repeatedly** let the CPU's branch predictor learn its whole tag sequence:
  Snappier's branchy decoder then gets ~2 mispredictions per 1500 tags and ties us on 4-64 KB html, while with 256
  distinct blocks we are 1.3-1.6x faster. Don't chase repeated-block ties in the main loop; judge with distinct blocks.
- Over-copying (16/32/64-byte vector stores past the end) is fine only inside the slop the callers guarantee; the
  guard-page tests catch violations.

## Measured dead ends (don't redo without a new angle)

Decoder (`BlockDecompressor`, about 11 cycles/tag, of which about 8 are the tag-to-tag dependency chain):
- Removing the one-tag deferred copy: 3-6% slower. Always copying 64 bytes instead of 32 or 64: up to 10% slower.
- `?:` selects instead of mask arithmetic: the JIT emits branches, up to 32% slower on text.
- Fewer instructions for the next-tag advance (single load, other ip formulas): within noise.
- Handling long literals inside the fast loop: only 1-3 per block, nothing to gain.
- Slow-path (input/output tail) tags decoded like the fast loop (table + masks + one 64 byte copy, no `switch`):
  on 256 distinct messages 1 KB 11-16% and 4 KB 3-8% faster (the switch's jump table mispredicts), but on a
  repeated html block 1 KB 11-16% and 4 KB 5-12% slower. Replacing only the slow path's literal `Memmove` call
  with a 64 byte copy: within noise. (Superseded: the fast loop now covers the tail, see below.)
- Branch-free second half of `MemCopy64` (overlapping store instead of the `size > 32` branch): html +10%, alice29
  +3%, 1 KB distinct +9%; the branch is well predicted (google/snappy keeps it too).
- Decoding whole small blocks (<= 2-4 KB) into a padded scratch and copying out: repeated 1 KB html +10-14%, worse
  than tail redirection. Tail scratch of 256 B / 1 KB instead of 128 B, or copying a short input whole (256 B):
  slower (more live values per tag in the tail loop, bigger copy-out).

The fast loop covers both tails. The last <= 130 input bytes are copied to a scratch followed by 0xFF sentinel
tags (copy-4, exceptional for the fast loop), so the loop stops at the real end and a tag running past it is
rejected as before. The last <= 127 output bytes are decoded by a second instantiation of the loop
(`DecompressBranchless<Redirected>`) into a scratch holding the preceding 64 output bytes; copy sources before
that prefix are redirected to the real output. 256 distinct 1 KB messages: 20% faster (net11 0.99x -> 1.21x
Snappier); repeated tiny blocks pay (jpeg 200-256 B ~25% slower, repeated 1 KB html ~6%), because a perfectly
predicted branchy slow path beats any branchless loop on repeated data. The earlier "padded tail buffer: no gain"
verdict was wrong because it was judged on a repeated block; judge tail changes with distinct messages.
- The scratch (704 B) lives in the `NoInlining` wrapper `DecompressTags`, not in the hot `DecompressCore`: a fixed
  buffer in the hot method added a GS cookie check and stack copies of its pointer parameters (html +7%). The
  wrapper must stay `NoInlining`: inlined into a caller without `SkipLocalsInit` the scratch was zeroed on every
  call (+16-40 ns).

Compressor (`BlockCompressor`, same algorithm and output size as Snappier):
- klauspost/s2-style match finder: faster on text but 2-7% larger output and 43% slower on incompressible data.
- Backward match extension / repeat-offset check: speed-neutral, 0.1% smaller. Not worth the code.
- `AggressiveOptimization`: 2-12% slower (loses dynamic PGO). Branch-free copy emit for lengths 12-64: 1-2% slower.
- Reusing the hash table between calls: clearing is about 2% of a 4KB message.
- Vector (2x32 B) copy for the final long literal instead of `Buffer.MemoryCopy`: 20% slower on jpeg (glibc's
  `rep movsb` wins at 64 KB). Reusing `data >> 8` as the first probe after a match ("preload"): no gain, and keeping
  `data` live made the JIT spill it. Plain static instead of `[ThreadStatic]` hash table: no change.
- Getting a cmov for the match-end select (any `?:` form) or dynamic PGO for `CompressFragment` (any runtime knob):
  not possible; it always tiers with "Synthesized PGO". The select is mask arithmetic, which helps text and
  distinct messages 7-15% but costs 2-4% on one repeated 16-64 KB html block (the predictor learns the branch).

Compressor notes: the hot path is the match-to-match chain (hash -> table load -> candidate load -> compare,
~30 cycles per match on html). The table keeps indices and candidates are addressed as `[baseIp + index]` (one add
less on the chain), and the ref/inlining rule above applies here too (a `ref data` to the non-inlined tail helper
made the JIT spill `data` before every hash). The method uses all 15 GPRs: count spills in the Tier1 listing
(`grep -c rbp-`) after every change.

Benchmark harness trap: a process pinned to one core sees one processor, and the runtime multiplies the 100 ms
call-counting delay by 10, so hot methods run Tier0/OSR code for seconds. Set `DOTNET_TC_CallCountingDelayMs=0` or
warm up for many seconds (BenchmarkDotNet's warmup hides it).

CRC: 128-bit PCLMULQDQ folding is 1.8x slower than the CRC32 instruction on Zen 3; computing the CRC during decode
gives no cache benefit (the output is L2-resident anyway).

Parallel paths (`ParallelWork`, `SlotPacker`, the stream pipelines; 16 MB json on a 16-core set, .NET 10):
- Batches with a barrier and a serial copy/pack on the calling thread were the cost: block compression went from
  7.6/1.48 ms (2/16 threads) to 5.8/0.87 ms with one work loop over all fragments and workers packing fragments into
  place as their predecessors complete; stream compression from 8.3/1.9 to 6.6/1.3 ms with the stream write of a
  batch overlapping the next batch (two output buffers); decompression with 80 KB reads from 2.2/2.3 ms (8/16
  threads) to 1.7/1.45 ms with the next batch decoded while the slots of the previous one are copied out (two
  slot sets of 2 chunks per thread; 4 per thread measured the same at twice the memory). 1 MB reads, decoded
  straight into the caller's buffer, are unchanged (1.07/1.07 ms).
- Size sweep (128 KB - 64 MB, parallel forced): block and stream compression beat single-threaded from 128 KB
  (2 fragments: 35 vs 62 us); stream decompression with 80 KB reads is slower than single-threaded below 512 KB
  on 2 threads (256 KB: 103 vs 64 us, thread wake-ups dominate) and even at 1 MB reads, so the 256 KB default of
  `MinimumParallelLength` (block APIs only; streams do not know their length) stays. Stream compression of
  <= 2 MB gains nothing beyond 8 threads (one batch, no overlap).
- The thread pool's hill climbing throttles CPU-bound workers: with 16 threads on a 16-core set, about every
  second process (after a sweep of 2-16 thread phases) settled into a state where batches of 16-64 chunks took 2x
  longer for the rest of the process (workers arrive late; the number that run per batch is the same). With
  `DOTNET_HillClimbing_Disable=1` (runtimeconfig `System.Threading.ThreadPool.HillClimbing.Disable`) it never
  happened, and 16-thread small-read decoding went from 2.0-2.3 to 1.5-1.8 ms even in the good state. A library
  cannot set it; the batches of `ParallelWork.For` use `Parallel.For`, which showed the slow state less often than
  workers we start ourselves (all at once with `Task.Run`, a chain where each starts the next, or a tree), and the
  chain is only for `Start`/`Join`, whose batches overlap with the caller's write or copy. Judge 16-thread results
  over several processes.
- Stream compression with 2 chunks per thread per batch (to halve the two output buffers): 16 threads 1.8-2.4 ms
  instead of 1.3 ms. Direct decoding only when every worker gets at least 2 chunks: slower.
- Small reads (Stream.CopyTo uses 80 KB) are bound by the reading thread copying every chunk out of the slots
  (16 MB is about 1.6 ms of memcpy); decoding the next batch in the background only hides the decode time.

## Git

GitHub repo `zcsizmadia/Snappiest`; commits use `zcsizmadia@gmail.com` (set in this repo's config).
