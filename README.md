# SnappySimd

[![CI](https://github.com/zcsizmadia/SnappySimd/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zcsizmadia/SnappySimd/actions/workflows/ci.yml)
[![Line coverage](https://zcsizmadia.github.io/SnappySimd/badge_linecoverage.svg)](https://zcsizmadia.github.io/SnappySimd/)
[![Branch coverage](https://zcsizmadia.github.io/SnappySimd/badge_branchcoverage.svg)](https://zcsizmadia.github.io/SnappySimd/)
[![NuGet](.github/badges/nuget.svg)](https://www.nuget.org/packages/SnappySimd)
[![.NET 8.0 | 10.0](.github/badges/dotnet.svg)](https://dotnet.microsoft.com/download)
[![License: BSD-3-Clause](.github/badges/license.svg)](LICENSE)

High-performance [Snappy](https://github.com/google/snappy) compression for .NET 8 and later, built on SIMD and
hardware intrinsics for x64 and arm64.

- **Faster than [Snappier](https://github.com/brantburnett/Snappier)** for both compression and decompression on the
  standard Snappy corpus (see [Benchmarks](#benchmarks)).
- **Drop-in replacement for Snappier**: same type names, members and exceptions. Change the namespace and you're done.
- Raw block format (`Snappy`) and framing/stream format (`SnappyStream`), interoperable with Snappier, google/snappy
  and every other conforming implementation.
- Pure managed code, no native dependencies, trimming and Native AOT compatible.

## Migrating from Snappier

```diff
- <PackageReference Include="Snappier" Version="1.3.1" />
+ <PackageReference Include="SnappySimd" Version="1.0.0" />
```

```diff
- using Snappier;
+ using SnappySimd;
```

That's all: `Snappy` and `SnappyStream` have the same members, parameter names and exception types as in Snappier.
Data compressed by either library decompresses with the other. The compressed bytes themselves are not guaranteed to
be identical to Snappier's (the compressed size is the same or smaller). They can also differ between machines: CPUs with a
CRC32 instruction use it as the hash function, others use a multiplicative hash. Every output is valid Snappy and
decodes everywhere.

Small behaviour differences, all on invalid input or edge cases:

| Situation | Snappier | SnappySimd |
| --- | --- | --- |
| Stream ends in the middle of a chunk | returns the partial data | throws `InvalidDataException` |
| Stream identifier chunk with wrong contents | ignored | throws `InvalidDataException` |
| `SnappyStream.Flush()` | writes buffered data | writes buffered data and flushes the base stream |
| Block header claims far more output than the input can produce | allocates, then fails | fails before allocating |
| Block whose tags overrun the declared length or the input (corrupt data) | may return data | throws `InvalidDataException`, like google/snappy |
| `SnappyStream.Read` | fills the whole buffer when possible | may return fewer bytes, like `DeflateStream` |

## Usage

### Blocks

```csharp
using SnappySimd;

byte[] compressed = Snappy.CompressToArray(data);
byte[] restored = Snappy.DecompressToArray(compressed);

// Span based, no allocations
Span<byte> buffer = new byte[Snappy.GetMaxCompressedLength(data.Length)];
int length = Snappy.Compress(data, buffer);

Span<byte> output = new byte[Snappy.GetUncompressedLength(buffer[..length])];
Snappy.Decompress(buffer[..length], output);

// Pooled buffers
using IMemoryOwner<byte> owner = Snappy.CompressToMemory(data);
```

### Streams

```csharp
using System.IO.Compression;
using SnappySimd;

using (var compressor = new SnappyStream(fileStream, CompressionMode.Compress))
{
    source.CopyTo(compressor);
}

using var decompressor = new SnappyStream(compressedStream, CompressionMode.Decompress);
decompressor.CopyTo(destination);
```

### Parallel compression and decompression (opt-in)

Snappy compresses 64KB fragments (blocks) and chunks (streams) independently, so large inputs can use several
threads. Pass `SnappyParallelOptions` to the extra overloads; the Snappier-compatible members never use more than the
calling thread. The output is **byte-for-byte identical** to the single-threaded output, so anything that reads
Snappy (Snappier, google/snappy, snappy-java, ...) reads it.

```csharp
var options = new SnappyParallelOptions { MaxDegreeOfParallelism = 8 }; // default: all processors

byte[] compressed = Snappy.CompressToArray(largeData, options);       // blocks: compression
int length = Snappy.Compress(largeData, outputBuffer, options);

using var writer = new SnappyStream(file, CompressionMode.Compress, leaveOpen: false, options);  // streams:
using var reader = new SnappyStream(file, CompressionMode.Decompress, leaveOpen: false, options); // both ways
```

Inputs below `MinimumParallelLength` (default 256KB) are processed on the calling thread. Raw block decompression is
always single-threaded: other encoders may emit copies that cross fragment boundaries. Neither Snappier nor
google/snappy has a parallel mode.

## How it is fast

- **Decompression** is a port of the branchless decoder from google/snappy 1.3: tags decode through a lookup table
  without data dependent branches, and each copy is deferred by one tag so its loads overlap with decoding the next.
  Output is written straight into the caller's buffer in a single pass (Snappier decodes into an internal buffer and
  copies). Overlapping copies expand their pattern with a byte shuffle (`PSHUFB` on x64, `TBL` on arm64).
- **Compression** follows google/snappy's `CompressFragment`, with branch-free tag emission, CRC32C hashing, and
  32-byte vector comparison for long matches.
- **Streams** decode each chunk directly into the caller's buffer when it fits, and verify checksums with hardware
  CRC-32C running three independent streams in parallel (about 3x the throughput of a single chain).

All SIMD paths have scalar fallbacks; the test suite runs with AVX2 disabled and with all hardware intrinsics
disabled to cover them.

## Benchmarks

AMD EPYC 7543 (Zen 3, AVX2), Ubuntu 22.04, BenchmarkDotNet 0.16 (medium job, in-process, one benchmark process per
physical core). Snappier 1.3.1 is the baseline; speedup is Snappier time / SnappySimd time. .NET 11 is RC 1. Raw block
and small-message results are the best of two runs for each library.

SnappySimd is faster in 150 of the 156 comparisons below (245 of 252 in the
[full results](docs/benchmarks/README.md)). The exceptions are all decompression of 1-64 KB html messages: 0.96-0.99x
on .NET 8 and 10, and 0.87-0.99x on .NET 11 RC 1, where Snappier's decoder got faster on small compressible inputs.
Both libraries finish those in 0.3-15 µs.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/speedup-dark.svg">
  <img alt="Bar charts: SnappySimd speedup over Snappier per file for block and stream compression and decompression on .NET 10" src="docs/benchmarks/speedup-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/small-dark.svg">
  <img alt="Line charts: SnappySimd speedup over Snappier by message size, 64 B to 64 KB, for html and fireworks.jpeg on .NET 10" src="docs/benchmarks/small-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/parallel-dark.svg">
  <img alt="Line chart: opt-in parallel speedup over Snappier by thread count for a 16 MB JSON input on .NET 10" src="docs/benchmarks/parallel-light.svg">
</picture>

Charts are .NET 10; the tables cover all three runtimes.

#### Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 351 µs | 314 µs | **1.12x** | 332 µs | 287 µs | **1.16x** | 333 µs | 290 µs | **1.15x** |
| asyoulik.txt | 310 µs | 273 µs | **1.13x** | 297 µs | 254 µs | **1.17x** | 290 µs | 245 µs | **1.19x** |
| events.ndjson | 847 µs | 779 µs | **1.09x** | 780 µs | 739 µs | **1.05x** | 776 µs | 740 µs | **1.05x** |
| fireworks.jpeg | 4.4 µs | 4.2 µs | **1.05x** | 4.2 µs | 4.1 µs | **1.02x** | 4.2 µs | 4.1 µs | **1.03x** |
| geo.protodata | 42.6 µs | 38.7 µs | **1.10x** | 41.3 µs | 38.2 µs | **1.08x** | 41.2 µs | 37.8 µs | **1.09x** |
| html | 45.4 µs | 43.8 µs | **1.04x** | 43.3 µs | 42.9 µs | **1.01x** | 43.3 µs | 42.7 µs | **1.01x** |
| html_x_4 | 313 µs | 291 µs | **1.08x** | 273 µs | 248 µs | **1.10x** | 282 µs | 249 µs | **1.13x** |
| json_api.json | 845 µs | 781 µs | **1.08x** | 786 µs | 740 µs | **1.06x** | 778 µs | 740 µs | **1.05x** |
| json_indented.json | 472 µs | 448 µs | **1.05x** | 443 µs | 426 µs | **1.04x** | 438 µs | 415 µs | **1.06x** |
| kppkn.gtb | 279 µs | 255 µs | **1.09x** | 264 µs | 246 µs | **1.07x** | 267 µs | 242 µs | **1.10x** |
| lcet10.txt | 987 µs | 903 µs | **1.09x** | 944 µs | 873 µs | **1.08x** | 948 µs | 873 µs | **1.09x** |
| paper-100k.pdf | 7.4 µs | 7.0 µs | **1.06x** | 7.3 µs | 7.0 µs | **1.04x** | 7.3 µs | 6.9 µs | **1.05x** |
| plrabn12.txt | 1.32 ms | 1.20 ms | **1.10x** | 1.27 ms | 1.17 ms | **1.08x** | 1.28 ms | 1.15 ms | **1.11x** |
| urls.10K | 1.13 ms | 1.04 ms | **1.09x** | 1.08 ms | 1.03 ms | **1.06x** | 1.08 ms | 1.01 ms | **1.06x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 114 µs | 85.0 µs | **1.34x** | 111 µs | 86.1 µs | **1.29x** | 112 µs | 90.0 µs | **1.25x** |
| asyoulik.txt | 106 µs | 78.7 µs | **1.35x** | 105 µs | 77.4 µs | **1.36x** | 101 µs | 77.3 µs | **1.31x** |
| events.ndjson | 354 µs | 219 µs | **1.62x** | 345 µs | 219 µs | **1.58x** | 417 µs | 218 µs | **1.91x** |
| fireworks.jpeg | 6.4 µs | 2.5 µs | **2.61x** | 6.5 µs | 2.5 µs | **2.65x** | 6.2 µs | 2.5 µs | **2.53x** |
| geo.protodata | 22.9 µs | 17.6 µs | **1.30x** | 22.1 µs | 18.0 µs | **1.23x** | 20.8 µs | 17.8 µs | **1.17x** |
| html | 22.5 µs | 20.4 µs | **1.10x** | 23.4 µs | 20.7 µs | **1.13x** | 22.1 µs | 20.7 µs | **1.07x** |
| html_x_4 | 125 µs | 85.0 µs | **1.47x** | 127 µs | 84.7 µs | **1.50x** | 113 µs | 83.8 µs | **1.34x** |
| json_api.json | 581 µs | 219 µs | **2.65x** | 562 µs | 221 µs | **2.55x** | 396 µs | 219 µs | **1.81x** |
| json_indented.json | 220 µs | 130 µs | **1.69x** | 221 µs | 125 µs | **1.77x** | 211 µs | 127 µs | **1.67x** |
| kppkn.gtb | 109 µs | 92.0 µs | **1.18x** | 111 µs | 92.8 µs | **1.19x** | 102 µs | 91.9 µs | **1.11x** |
| lcet10.txt | 321 µs | 225 µs | **1.43x** | 312 µs | 227 µs | **1.37x** | 317 µs | 237 µs | **1.34x** |
| paper-100k.pdf | 7.1 µs | 3.8 µs | **1.85x** | 7.3 µs | 3.8 µs | **1.94x** | 7.0 µs | 3.8 µs | **1.86x** |
| plrabn12.txt | 475 µs | 312 µs | **1.52x** | 437 µs | 314 µs | **1.39x** | 432 µs | 314 µs | **1.38x** |
| urls.10K | 397 µs | 228 µs | **1.74x** | 383 µs | 226 µs | **1.70x** | 387 µs | 224 µs | **1.73x** |

#### Whole corpus in one operation (all files)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 7.61 ms | 7.01 ms | **1.08x** | 7.33 ms | 6.88 ms | **1.07x** | 7.30 ms | 6.85 ms | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 3.02 ms | 1.75 ms | **1.73x** | 2.85 ms | 1.82 ms | **1.57x** | 2.82 ms | 1.83 ms | **1.55x** |

#### Small messages (`Snappy`, first N bytes of the file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 110 ns | 64 ns | **1.71x** | 89 ns | 59 ns | **1.51x** | 84 ns | 58 ns | **1.44x** |
| html 1024 B | 910 ns | 745 ns | **1.22x** | 849 ns | 746 ns | **1.14x** | 846 ns | 747 ns | **1.13x** |
| html 4096 B | 3.3 µs | 3.0 µs | **1.12x** | 3.2 µs | 3.0 µs | **1.06x** | 3.2 µs | 3.0 µs | **1.06x** |
| html 65536 B | 32.7 µs | 31.2 µs | **1.05x** | 31.8 µs | 31.4 µs | **1.01x** | 31.9 µs | 30.8 µs | **1.04x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 79 ns | 44 ns | **1.81x** | 77 ns | 40 ns | **1.91x** | 57 ns | 39 ns | **1.44x** |
| html 1024 B | 313 ns | 292 ns | **1.07x** | 295 ns | 302 ns | **0.98x** | 265 ns | 306 ns | **0.87x** |
| html 4096 B | 1.4 µs | 1.4 µs | **0.96x** | 1.4 µs | 1.4 µs | **0.97x** | 1.3 µs | 1.4 µs | **0.90x** |
| html 65536 B | 16.2 µs | 15.3 µs | **1.05x** | 16.2 µs | 15.3 µs | **1.06x** | 15.2 µs | 15.3 µs | **0.99x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 251 ns | 164 ns | **1.53x** | 215 ns | 154 ns | **1.40x** | 184 ns | 134 ns | **1.37x** |
| html 1024 B | 1.4 µs | 1.2 µs | **1.16x** | 1.3 µs | 1.2 µs | **1.08x** | 1.2 µs | 1.2 µs | **1.04x** |
| html 4096 B | 5.3 µs | 5.0 µs | **1.07x** | 5.1 µs | 5.0 µs | **1.02x** | 4.9 µs | 4.9 µs | **1.01x** |
| html 65536 B | 56.8 µs | 49.2 µs | **1.15x** | 54.2 µs | 48.7 µs | **1.11x** | 52.2 µs | 48.4 µs | **1.08x** |

#### Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 374 µs | 322 µs | **1.16x** | 357 µs | 293 µs | **1.22x** | 358 µs | 300 µs | **1.19x** |
| fireworks.jpeg | 23.9 µs | 14.9 µs | **1.60x** | 23.9 µs | 12.9 µs | **1.86x** | 24.1 µs | 12.2 µs | **1.98x** |
| html_x_4 | 369 µs | 313 µs | **1.18x** | 329 µs | 258 µs | **1.27x** | 340 µs | 265 µs | **1.28x** |
| json_api.json | 982 µs | 830 µs | **1.18x** | 915 µs | 780 µs | **1.17x** | 910 µs | 769 µs | **1.18x** |
| urls.10K | 1.23 ms | 1.08 ms | **1.13x** | 1.18 ms | 1.05 ms | **1.12x** | 1.18 ms | 1.04 ms | **1.13x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 140 µs | 92.9 µs | **1.51x** | 143 µs | 91.5 µs | **1.56x** | 140 µs | 95.5 µs | **1.47x** |
| fireworks.jpeg | 19.0 µs | 11.4 µs | **1.66x** | 18.3 µs | 8.0 µs | **2.29x** | 18.9 µs | 8.1 µs | **2.34x** |
| html_x_4 | 183 µs | 102 µs | **1.79x** | 188 µs | 96.3 µs | **1.95x** | 178 µs | 97.7 µs | **1.82x** |
| json_api.json | 461 µs | 267 µs | **1.73x** | 459 µs | 250 µs | **1.84x** | 436 µs | 249 µs | **1.75x** |
| urls.10K | 491 µs | 275 µs | **1.79x** | 474 µs | 253 µs | **1.88x** | 473 µs | 254 µs | **1.86x** |

Reproduce with `scripts/bench-remote.sh` (or run `benchmarks/SnappySimd.Benchmarks` directly with BenchmarkDotNet).

## Building and testing

```shell
dotnet build -c Release
dotnet test --project tests/SnappySimd.Tests -c Release
scripts/coverage.sh        # coverage across CPU feature configurations
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) and include interop tests against Snappier, corruption fuzzing,
and guard-page tests that crash on any out-of-bounds access.

The fuzz tests (`tests/SnappySimd.Tests/Fuzz`) generate realistic and corrupt inputs and compare SnappySimd with
Snappier and a plain spec decoder. They run for about a second each in normal test runs; set `SNAPPY_FUZZ_SECONDS`
for longer runs (the nightly workflow does) and `SNAPPY_FUZZ_SEED` to replay a reported failure:

```shell
SNAPPY_FUZZ_SECONDS=300 dotnet test --project tests/SnappySimd.Tests -c Release -- --treenode-filter "/*/*/*FuzzTests/*"
```

## License

BSD-3-Clause. The compression and decompression loops are ported from
[google/snappy](https://github.com/google/snappy) (BSD-3-Clause, copyright Google Inc.).
