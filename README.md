# SnappySimd

[![CI](https://github.com/zcsizmadia/SnappySimd/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zcsizmadia/SnappySimd/actions/workflows/ci.yml)
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
Small-message results are the best of two runs for each library.

SnappySimd is at least as fast as Snappier in 176 of the 180 comparisons below (270 of 276 in the
[full results](docs/benchmarks/README.md)). Every exception is one html block of 1-64 KB processed over and over
(0.94-0.99x), mostly decompression on .NET 11 RC 1; see the next paragraph.

**Repeated block vs. different messages.** Most benchmarks here, like Snappier's and google/snappy's, decode one block
millions of times. The CPU's branch predictor then learns that block's whole tag sequence, which makes a branchy
decoder like Snappier's nearly mispredict-free (measured with `perf`: ~2 mispredictions per 1500 tags) and hides the
advantage of SnappySimd's branchless decoder. With 256 different messages per size, as in real traffic, SnappySimd
decompresses 1.2-1.7x and compresses 1.14-1.23x faster than Snappier at every size from 1 KB to 64 KB, on all three
runtimes.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/speedup-dark.svg">
  <img alt="Bar charts: SnappySimd speedup over Snappier per file for block and stream compression and decompression on .NET 10" src="docs/benchmarks/speedup-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/messages-dark.svg">
  <img alt="Line chart: SnappySimd speedup over Snappier for 256 different messages per size, 1 KB to 64 KB, on .NET 10" src="docs/benchmarks/messages-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/small-dark.svg">
  <img alt="Line charts: SnappySimd speedup over Snappier by message size, 64 B to 64 KB, for one repeated html or fireworks.jpeg block on .NET 10" src="docs/benchmarks/small-light.svg">
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
| alice29.txt | 353 µs | 292 µs | **1.21x** | 332 µs | 267 µs | **1.24x** | 334 µs | 262 µs | **1.27x** |
| asyoulik.txt | 316 µs | 267 µs | **1.19x** | 292 µs | 226 µs | **1.29x** | 292 µs | 215 µs | **1.36x** |
| events.ndjson | 836 µs | 701 µs | **1.19x** | 780 µs | 684 µs | **1.14x** | 792 µs | 679 µs | **1.17x** |
| fireworks.jpeg | 4.2 µs | 4.0 µs | **1.05x** | 4.2 µs | 4.0 µs | **1.06x** | 4.2 µs | 4.0 µs | **1.06x** |
| geo.protodata | 42.9 µs | 36.9 µs | **1.16x** | 41.2 µs | 36.9 µs | **1.12x** | 41.2 µs | 37.1 µs | **1.11x** |
| html | 45.5 µs | 43.1 µs | **1.06x** | 43.4 µs | 41.7 µs | **1.04x** | 43.8 µs | 42.6 µs | **1.03x** |
| html_x_4 | 310 µs | 224 µs | **1.38x** | 290 µs | 197 µs | **1.47x** | 283 µs | 190 µs | **1.49x** |
| json_api.json | 844 µs | 695 µs | **1.21x** | 785 µs | 684 µs | **1.15x** | 780 µs | 677 µs | **1.15x** |
| json_indented.json | 467 µs | 388 µs | **1.20x** | 444 µs | 376 µs | **1.18x** | 440 µs | 374 µs | **1.18x** |
| kppkn.gtb | 280 µs | 229 µs | **1.22x** | 263 µs | 218 µs | **1.21x** | 262 µs | 219 µs | **1.20x** |
| lcet10.txt | 994 µs | 838 µs | **1.19x** | 953 µs | 825 µs | **1.16x** | 943 µs | 820 µs | **1.15x** |
| paper-100k.pdf | 7.4 µs | 7.0 µs | **1.05x** | 7.3 µs | 6.9 µs | **1.05x** | 7.4 µs | 6.8 µs | **1.09x** |
| plrabn12.txt | 1.32 ms | 1.17 ms | **1.13x** | 1.28 ms | 1.12 ms | **1.14x** | 1.28 ms | 1.13 ms | **1.13x** |
| urls.10K | 1.13 ms | 942 µs | **1.20x** | 1.10 ms | 935 µs | **1.18x** | 1.08 ms | 941 µs | **1.15x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 117 µs | 87.1 µs | **1.34x** | 113 µs | 89.9 µs | **1.26x** | 108 µs | 90.3 µs | **1.19x** |
| asyoulik.txt | 105 µs | 78.3 µs | **1.34x** | 102 µs | 81.0 µs | **1.26x** | 100 µs | 77.5 µs | **1.30x** |
| events.ndjson | 356 µs | 221 µs | **1.61x** | 345 µs | 219 µs | **1.57x** | 407 µs | 220 µs | **1.85x** |
| fireworks.jpeg | 7.1 µs | 2.6 µs | **2.74x** | 6.3 µs | 2.5 µs | **2.56x** | 7.0 µs | 2.5 µs | **2.77x** |
| geo.protodata | 22.5 µs | 18.1 µs | **1.25x** | 21.6 µs | 18.2 µs | **1.19x** | 20.7 µs | 18.4 µs | **1.13x** |
| html | 22.5 µs | 20.9 µs | **1.07x** | 23.4 µs | 20.9 µs | **1.12x** | 22.1 µs | 20.9 µs | **1.06x** |
| html_x_4 | 126 µs | 85.5 µs | **1.47x** | 128 µs | 84.6 µs | **1.52x** | 110 µs | 85.8 µs | **1.29x** |
| json_api.json | 570 µs | 223 µs | **2.55x** | 557 µs | 219 µs | **2.54x** | 545 µs | 222 µs | **2.46x** |
| json_indented.json | 220 µs | 127 µs | **1.73x** | 222 µs | 128 µs | **1.74x** | 234 µs | 128 µs | **1.83x** |
| kppkn.gtb | 110 µs | 91.4 µs | **1.21x** | 113 µs | 92.5 µs | **1.22x** | 106 µs | 94.3 µs | **1.12x** |
| lcet10.txt | 324 µs | 229 µs | **1.42x** | 312 µs | 238 µs | **1.31x** | 318 µs | 238 µs | **1.34x** |
| paper-100k.pdf | 7.4 µs | 3.7 µs | **1.99x** | 7.1 µs | 3.8 µs | **1.90x** | 6.9 µs | 3.7 µs | **1.84x** |
| plrabn12.txt | 468 µs | 316 µs | **1.48x** | 441 µs | 329 µs | **1.34x** | 439 µs | 328 µs | **1.34x** |
| urls.10K | 400 µs | 230 µs | **1.74x** | 388 µs | 229 µs | **1.69x** | 384 µs | 231 µs | **1.66x** |

#### Whole corpus in one operation (all files)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 7.62 ms | 6.52 ms | **1.17x** | 7.36 ms | 6.39 ms | **1.15x** | 7.30 ms | 6.43 ms | **1.14x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 3.02 ms | 1.79 ms | **1.69x** | 2.90 ms | 1.78 ms | **1.63x** | 2.83 ms | 1.78 ms | **1.59x** |

#### 256 different messages of each size, round-robin (time per message)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 1.4 µs | 1.2 µs | **1.19x** | 1.4 µs | 1.1 µs | **1.23x** | 1.4 µs | 1.1 µs | **1.23x** |
| 4096 B | 6.3 µs | 5.5 µs | **1.16x** | 6.2 µs | 5.4 µs | **1.15x** | 6.1 µs | 5.3 µs | **1.15x** |
| 16384 B | 27.8 µs | 23.2 µs | **1.20x** | 26.9 µs | 23.2 µs | **1.16x** | 26.7 µs | 23.0 µs | **1.16x** |
| 65536 B | 109 µs | 93.2 µs | **1.17x** | 106 µs | 92.8 µs | **1.14x** | 105 µs | 91.7 µs | **1.14x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 518 ns | 356 ns | **1.46x** | 461 ns | 343 ns | **1.34x** | 429 ns | 357 ns | **1.20x** |
| 4096 B | 2.5 µs | 1.5 µs | **1.61x** | 2.3 µs | 1.5 µs | **1.49x** | 2.2 µs | 1.5 µs | **1.44x** |
| 16384 B | 10.7 µs | 6.3 µs | **1.70x** | 9.6 µs | 6.3 µs | **1.53x** | 9.3 µs | 6.2 µs | **1.48x** |
| 65536 B | 41.7 µs | 25.7 µs | **1.63x** | 37.8 µs | 25.6 µs | **1.48x** | 36.6 µs | 25.4 µs | **1.44x** |

#### Small messages (`Snappy`, first N bytes of the file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 112 ns | 62 ns | **1.82x** | 89 ns | 56 ns | **1.60x** | 83 ns | 56 ns | **1.49x** |
| html 1024 B | 910 ns | 736 ns | **1.24x** | 855 ns | 732 ns | **1.17x** | 862 ns | 713 ns | **1.21x** |
| html 4096 B | 3.4 µs | 3.0 µs | **1.12x** | 3.2 µs | 3.0 µs | **1.06x** | 3.2 µs | 2.9 µs | **1.09x** |
| html 65536 B | 33.6 µs | 32.0 µs | **1.05x** | 31.8 µs | 32.4 µs | **0.98x** | 31.8 µs | 31.4 µs | **1.01x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 80 ns | 41 ns | **1.95x** | 77 ns | 39 ns | **2.00x** | 58 ns | 39 ns | **1.49x** |
| html 1024 B | 309 ns | 270 ns | **1.14x** | 290 ns | 264 ns | **1.10x** | 266 ns | 269 ns | **0.99x** |
| html 4096 B | 1.4 µs | 1.4 µs | **1.00x** | 1.4 µs | 1.3 µs | **1.02x** | 1.3 µs | 1.3 µs | **0.94x** |
| html 65536 B | 16.2 µs | 15.8 µs | **1.03x** | 16.2 µs | 15.5 µs | **1.05x** | 15.2 µs | 15.5 µs | **0.98x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| html 64 B | 254 ns | 156 ns | **1.62x** | 220 ns | 148 ns | **1.49x** | 183 ns | 131 ns | **1.40x** |
| html 1024 B | 1.4 µs | 1.2 µs | **1.21x** | 1.3 µs | 1.2 µs | **1.15x** | 1.3 µs | 1.1 µs | **1.11x** |
| html 4096 B | 5.4 µs | 5.0 µs | **1.08x** | 5.1 µs | 5.0 µs | **1.03x** | 5.1 µs | 4.8 µs | **1.05x** |
| html 65536 B | 56.3 µs | 50.5 µs | **1.11x** | 55.0 µs | 50.7 µs | **1.09x** | 53.2 µs | 49.4 µs | **1.08x** |

#### Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 384 µs | 302 µs | **1.27x** | 357 µs | 275 µs | **1.30x** | 359 µs | 265 µs | **1.35x** |
| fireworks.jpeg | 23.6 µs | 14.2 µs | **1.66x** | 24.0 µs | 13.2 µs | **1.81x** | 23.9 µs | 12.3 µs | **1.94x** |
| html_x_4 | 371 µs | 262 µs | **1.41x** | 342 µs | 198 µs | **1.73x** | 331 µs | 208 µs | **1.59x** |
| json_api.json | 981 µs | 762 µs | **1.29x** | 913 µs | 725 µs | **1.26x** | 910 µs | 714 µs | **1.28x** |
| urls.10K | 1.28 ms | 986 µs | **1.30x** | 1.18 ms | 961 µs | **1.23x** | 1.18 ms | 962 µs | **1.23x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 142 µs | 94.9 µs | **1.50x** | 140 µs | 93.8 µs | **1.49x** | 140 µs | 93.2 µs | **1.51x** |
| fireworks.jpeg | 18.9 µs | 10.3 µs | **1.84x** | 18.8 µs | 8.0 µs | **2.34x** | 18.7 µs | 8.3 µs | **2.26x** |
| html_x_4 | 191 µs | 103 µs | **1.85x** | 184 µs | 97.0 µs | **1.89x** | 188 µs | 96.8 µs | **1.95x** |
| json_api.json | 461 µs | 276 µs | **1.67x** | 457 µs | 254 µs | **1.80x** | 436 µs | 256 µs | **1.71x** |
| urls.10K | 491 µs | 265 µs | **1.85x** | 477 µs | 257 µs | **1.86x** | 464 µs | 256 µs | **1.81x** |

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
