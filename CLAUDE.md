# SnappySimd

High-performance Snappy (raw block + framing format) for .NET 8+, using SIMD and hardware intrinsics on x64 and arm64.

## Goals and rules

- **Must beat Snappier** (NuGet `Snappier`, the main competitor) on speed. Any change to a hot path needs a
  before/after benchmark against Snappier; never trade correctness for speed.
- **Drop-in migration from Snappier**: `SnappySimd.Snappy` and `SnappySimd.SnappyStream` keep Snappier's public type
  names, member signatures, parameter names and exception types. Changing `using Snappier;` to `using SnappySimd;`
  must be the only code change. New APIs may be added, existing ones must not diverge.
  This is **source portability, not byte-for-byte output compatibility**: compressed output only has to be valid
  Snappy (readable by Snappier and google/snappy), so the compression algorithm may differ from Snappier's. Keep the
  compressed size no worse than Snappier's (enforced by `InteropTests.Block_CompressionRatio_NotWorseThanSnappier`).
  Don't add hidden multithreading to the Snappier-compatible methods; parallelism would be an opt-in extra overload.
- **net8.0+ only** (targets `net8.0;net10.0`). No netstandard / .NET Framework code paths.
- **Tests: TUnit only**, coverage at least 90% (currently ~98% line), measured across CPU-feature configurations.
- License BSD-3-Clause; the hot loops are ports of google/snappy, keep the attribution in LICENSE.

## Layout

- `src/SnappySimd/` library. Hot paths are in `Internal/BlockDecompressor.cs` (port of Google's
  `DecompressBranchless`), `Internal/BlockCompressor.cs` (`CompressFragment`), `Internal/SimdCopy.cs`, `Internal/Crc32C.cs`.
- `tests/SnappySimd.Tests/` TUnit tests, including interop with the real Snappier package and guard-page tests.
- `benchmarks/SnappySimd.Benchmarks/` BenchmarkDotNet, Snappier as the baseline.
- `testdata/` shared corpus (see its README for provenance).

## Commands

The `dotnet` CLI is at `C:\Program Files\dotnet\dotnet.exe` (not on PATH in Claude Code shells).

- Build: `dotnet build -c Release`
- Tests: `dotnet test --project tests/SnappySimd.Tests -c Release`
- Coverage across CPU features: `scripts/coverage.sh` (merges default, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`)
- Benchmarks: **run on the remote host, not this machine** (the user works on this machine):
  `scripts/bench-remote.sh [host] [net10.0|net8.0] [Block,Stream,SmallBlock] [BenchmarkDotNet args]`.
  Default host `bench-host.example` (2x EPYC 7543, 128 threads, AVX2, no AVX-512). The script syncs the repo, builds
  once, and runs one in-process BenchmarkDotNet job per (class, file) pinned to its own physical core on NUMA node 1.
  `BENCH_JOB=short` for quick iteration (about +-10% noise), `medium` (default) for decisions.

## Performance notes (learned the hard way)

- Do not use `stackalloc` or `[MethodImpl(AggressiveOptimization)]` in hot loops: both stop tiered compilation, so
  the method never gets Tier-1 (no dynamic PGO, `static readonly` table pointers are not folded into constants).
- RyuJIT often turns `cond ? a : b` into branches inside big loops; write hot selects as mask arithmetic.
- Dynamic PGO profiles each method from its first calls. A hot-loop method that small inputs call and immediately
  return from gets profiled as "loop body cold", and every later large input runs slow code (measured: 64KB html
  decompression went from 16% faster than Snappier to 11% slower after a few 64 byte calls). Check entry conditions
  in the caller so the loop method is only called when it will run. Benchmark mixed sizes in one process
  (SmallBlockBenchmarks does) to catch this; single-size benchmarks hide it.
- Check codegen with `DOTNET_JitDisasm=<method> DOTNET_JitStdOutFile=/tmp/x.asm` on the remote host and look for
  the `Tier1` listing.
- Over-copying (16/32/64-byte vector stores past the end) is fine only inside the slop the callers guarantee; the
  guard-page tests catch violations.

## Git

GitHub repo `zcsizmadia/SnappySimd`; commits use `zcsizmadia@gmail.com` (set in this repo's config).
