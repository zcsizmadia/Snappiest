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
physical core). Snappier 1.3.1 is the baseline; speedup is Snappier time / SnappySimd time. .NET 11 is RC 1.

SnappySimd is faster in 115 of 117 comparisons below; the two exceptions (net8.0, fireworks.jpeg compression at
0.95x and html decompression at 0.98x) are microsecond-scale inputs within run-to-run noise.

#### Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 367 µs | 316 µs | **1.16x** | 331 µs | 286 µs | **1.16x** | 332 µs | 290 µs | **1.15x** |
| asyoulik.txt | 310 µs | 279 µs | **1.11x** | 291 µs | 255 µs | **1.14x** | 290 µs | 242 µs | **1.20x** |
| fireworks.jpeg | 4.8 µs | 5.1 µs | **0.95x** | 5.1 µs | 4.5 µs | **1.15x** | 4.2 µs | 4.0 µs | **1.05x** |
| geo.protodata | 42.7 µs | 39.4 µs | **1.08x** | 41.3 µs | 38.2 µs | **1.08x** | 41.4 µs | 38.2 µs | **1.08x** |
| html | 45.6 µs | 43.8 µs | **1.04x** | 43.3 µs | 42.7 µs | **1.01x** | 43.3 µs | 42.5 µs | **1.02x** |
| html_x_4 | 320 µs | 291 µs | **1.10x** | 283 µs | 250 µs | **1.13x** | 282 µs | 249 µs | **1.13x** |
| kppkn.gtb | 280 µs | 258 µs | **1.09x** | 265 µs | 246 µs | **1.08x** | 264 µs | 243 µs | **1.09x** |
| lcet10.txt | 1.04 ms | 905 µs | **1.15x** | 966 µs | 865 µs | **1.12x** | 960 µs | 865 µs | **1.11x** |
| paper-100k.pdf | 7.4 µs | 7.0 µs | **1.07x** | 7.4 µs | 7.1 µs | **1.04x** | 7.4 µs | 7.0 µs | **1.06x** |
| plrabn12.txt | 1.35 ms | 1.22 ms | **1.11x** | 1.27 ms | 1.17 ms | **1.09x** | 1.28 ms | 1.15 ms | **1.11x** |
| urls.10K | 1.14 ms | 1.02 ms | **1.11x** | 1.10 ms | 1.04 ms | **1.05x** | 1.08 ms | 1.01 ms | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 116 µs | 90.2 µs | **1.29x** | 110 µs | 91.0 µs | **1.21x** | 112 µs | 103 µs | **1.09x** |
| asyoulik.txt | 106 µs | 83.5 µs | **1.27x** | 107 µs | 82.3 µs | **1.29x** | 103 µs | 82.7 µs | **1.25x** |
| fireworks.jpeg | 6.7 µs | 3.1 µs | **2.16x** | 7.9 µs | 2.5 µs | **3.21x** | 6.3 µs | 2.5 µs | **2.55x** |
| geo.protodata | 23.0 µs | 20.1 µs | **1.14x** | 21.9 µs | 19.0 µs | **1.15x** | 20.8 µs | 19.2 µs | **1.09x** |
| html | 22.4 µs | 22.7 µs | **0.98x** | 23.6 µs | 22.3 µs | **1.06x** | 22.3 µs | 21.8 µs | **1.02x** |
| html_x_4 | 126 µs | 92.7 µs | **1.36x** | 132 µs | 89.6 µs | **1.48x** | 113 µs | 89.2 µs | **1.26x** |
| kppkn.gtb | 109 µs | 96.3 µs | **1.13x** | 111 µs | 108 µs | **1.03x** | 105 µs | 104 µs | **1.01x** |
| lcet10.txt | 321 µs | 245 µs | **1.31x** | 312 µs | 233 µs | **1.34x** | 318 µs | 234 µs | **1.36x** |
| paper-100k.pdf | 6.9 µs | 3.9 µs | **1.77x** | 7.3 µs | 3.8 µs | **1.90x** | 7.0 µs | 3.8 µs | **1.83x** |
| plrabn12.txt | 467 µs | 343 µs | **1.36x** | 438 µs | 336 µs | **1.30x** | 438 µs | 335 µs | **1.31x** |
| urls.10K | 406 µs | 245 µs | **1.66x** | 388 µs | 240 µs | **1.62x** | 389 µs | 238 µs | **1.63x** |

#### Small messages (`Snappy`, slice of html)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 64 | 107 ns | 63 ns | **1.70x** | 89 ns | 58 ns | **1.54x** | 80 ns | 57 ns | **1.41x** |
| 512 | 482 ns | 391 ns | **1.23x** | 441 ns | 380 ns | **1.16x** | 417 ns | 373 ns | **1.12x** |
| 4096 | 3.3 µs | 3.1 µs | **1.07x** | 3.2 µs | 3.0 µs | **1.05x** | 3.1 µs | 3.1 µs | **1.02x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 64 | 66 ns | 15 ns | **4.45x** | 68 ns | 12 ns | **5.60x** | 45 ns | 12 ns | **3.86x** |
| 512 | 191 ns | 156 ns | **1.22x** | 179 ns | 152 ns | **1.17x** | 167 ns | 143 ns | **1.17x** |
| 4096 | 1.6 µs | 1.6 µs | **1.00x** | 1.6 µs | 1.6 µs | **1.03x** | 1.6 µs | 1.5 µs | **1.03x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 64 | 242 ns | 130 ns | **1.87x** | 202 ns | 131 ns | **1.54x** | 166 ns | 107 ns | **1.56x** |
| 512 | 779 ns | 645 ns | **1.21x** | 709 ns | 622 ns | **1.14x** | 673 ns | 613 ns | **1.10x** |
| 4096 | 5.4 µs | 5.1 µs | **1.07x** | 5.3 µs | 5.0 µs | **1.06x** | 5.0 µs | 4.9 µs | **1.03x** |

#### Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 388 µs | 326 µs | **1.19x** | 356 µs | 296 µs | **1.20x** | 359 µs | 303 µs | **1.18x** |
| fireworks.jpeg | 23.8 µs | 14.5 µs | **1.64x** | 23.8 µs | 13.8 µs | **1.73x** | 23.8 µs | 13.8 µs | **1.72x** |
| html_x_4 | 367 µs | 314 µs | **1.17x** | 342 µs | 265 µs | **1.29x** | 338 µs | 269 µs | **1.26x** |
| urls.10K | 1.22 ms | 1.07 ms | **1.15x** | 1.18 ms | 1.07 ms | **1.10x** | 1.17 ms | 1.05 ms | **1.12x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 141 µs | 100 µs | **1.41x** | 142 µs | 99.7 µs | **1.42x** | 141 µs | 96.2 µs | **1.46x** |
| fireworks.jpeg | 18.9 µs | 9.7 µs | **1.94x** | 18.9 µs | 9.6 µs | **1.96x** | 18.9 µs | 9.8 µs | **1.94x** |
| html_x_4 | 188 µs | 110 µs | **1.71x** | 183 µs | 106 µs | **1.72x** | 179 µs | 108 µs | **1.65x** |
| urls.10K | 492 µs | 298 µs | **1.65x** | 479 µs | 273 µs | **1.76x** | 466 µs | 277 µs | **1.68x** |

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
