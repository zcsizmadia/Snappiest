# Full benchmark results

AMD EPYC 7543 (Zen 3, AVX2), Ubuntu 22.04, BenchmarkDotNet 0.16 (medium job, in-process, one benchmark process per
physical core). Snappier 1.3.1 is the baseline; speedup is Snappier time / SnappySimd time.
Small-message results are the best of two runs for each library; everything else is a single run.

Generated with `scripts/bench-table.mjs` (tables) and `scripts/bench-charts.mjs` (the SVG charts in this folder) from
`scripts/bench-remote.sh` output.

## Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 353 µs | 292 µs | **1.21x** | 332 µs | 267 µs | **1.24x** |
| asyoulik.txt | 316 µs | 267 µs | **1.19x** | 292 µs | 226 µs | **1.29x** |
| events.ndjson | 836 µs | 701 µs | **1.19x** | 780 µs | 684 µs | **1.14x** |
| fireworks.jpeg | 4.2 µs | 4.0 µs | **1.05x** | 4.2 µs | 4.0 µs | **1.06x** |
| geo.protodata | 42.9 µs | 36.9 µs | **1.16x** | 41.2 µs | 36.9 µs | **1.12x** |
| html | 45.5 µs | 43.1 µs | **1.06x** | 43.4 µs | 41.7 µs | **1.04x** |
| html_x_4 | 310 µs | 224 µs | **1.38x** | 290 µs | 197 µs | **1.47x** |
| json_api.json | 844 µs | 695 µs | **1.21x** | 785 µs | 684 µs | **1.15x** |
| json_indented.json | 467 µs | 388 µs | **1.20x** | 444 µs | 376 µs | **1.18x** |
| kppkn.gtb | 280 µs | 229 µs | **1.22x** | 263 µs | 218 µs | **1.21x** |
| lcet10.txt | 994 µs | 838 µs | **1.19x** | 953 µs | 825 µs | **1.16x** |
| paper-100k.pdf | 7.4 µs | 7.0 µs | **1.05x** | 7.3 µs | 6.9 µs | **1.05x** |
| plrabn12.txt | 1.32 ms | 1.17 ms | **1.13x** | 1.28 ms | 1.12 ms | **1.14x** |
| urls.10K | 1.13 ms | 942 µs | **1.20x** | 1.10 ms | 935 µs | **1.18x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 117 µs | 87.1 µs | **1.34x** | 113 µs | 89.9 µs | **1.26x** |
| asyoulik.txt | 105 µs | 78.3 µs | **1.34x** | 102 µs | 81.0 µs | **1.26x** |
| events.ndjson | 356 µs | 221 µs | **1.61x** | 345 µs | 219 µs | **1.57x** |
| fireworks.jpeg | 7.1 µs | 2.6 µs | **2.74x** | 6.3 µs | 2.5 µs | **2.56x** |
| geo.protodata | 22.5 µs | 18.1 µs | **1.25x** | 21.6 µs | 18.2 µs | **1.19x** |
| html | 22.5 µs | 20.9 µs | **1.07x** | 23.4 µs | 20.9 µs | **1.12x** |
| html_x_4 | 126 µs | 85.5 µs | **1.47x** | 128 µs | 84.6 µs | **1.52x** |
| json_api.json | 570 µs | 223 µs | **2.55x** | 557 µs | 219 µs | **2.54x** |
| json_indented.json | 220 µs | 127 µs | **1.73x** | 222 µs | 128 µs | **1.74x** |
| kppkn.gtb | 110 µs | 91.4 µs | **1.21x** | 113 µs | 92.5 µs | **1.22x** |
| lcet10.txt | 324 µs | 229 µs | **1.42x** | 312 µs | 238 µs | **1.31x** |
| paper-100k.pdf | 7.4 µs | 3.7 µs | **1.99x** | 7.1 µs | 3.8 µs | **1.90x** |
| plrabn12.txt | 468 µs | 316 µs | **1.48x** | 441 µs | 329 µs | **1.34x** |
| urls.10K | 400 µs | 230 µs | **1.74x** | 388 µs | 229 µs | **1.69x** |

## Whole corpus in one operation (all files)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 7.62 ms | 6.52 ms | **1.17x** | 7.36 ms | 6.39 ms | **1.15x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 3.02 ms | 1.79 ms | **1.69x** | 2.90 ms | 1.78 ms | **1.63x** |

## 256 different messages of each size, round-robin (time per message)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 1.4 µs | 1.2 µs | **1.19x** | 1.4 µs | 1.1 µs | **1.23x** |
| 4096 B | 6.3 µs | 5.5 µs | **1.16x** | 6.2 µs | 5.4 µs | **1.15x** |
| 16384 B | 27.8 µs | 23.2 µs | **1.20x** | 26.9 µs | 23.2 µs | **1.16x** |
| 65536 B | 109 µs | 93.2 µs | **1.17x** | 106 µs | 92.8 µs | **1.14x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 518 ns | 356 ns | **1.46x** | 461 ns | 343 ns | **1.34x** |
| 4096 B | 2.5 µs | 1.5 µs | **1.61x** | 2.3 µs | 1.5 µs | **1.49x** |
| 16384 B | 10.7 µs | 6.3 µs | **1.70x** | 9.6 µs | 6.3 µs | **1.53x** |
| 65536 B | 41.7 µs | 25.7 µs | **1.63x** | 37.8 µs | 25.6 µs | **1.48x** |

## Small messages (`Snappy`, first N bytes of the file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 137 ns | 78 ns | **1.75x** | 115 ns | 71 ns | **1.61x** |
| fireworks.jpeg 200 B | 272 ns | 177 ns | **1.54x** | 238 ns | 171 ns | **1.39x** |
| fireworks.jpeg 256 B | 341 ns | 234 ns | **1.46x** | 307 ns | 225 ns | **1.37x** |
| fireworks.jpeg 1024 B | 609 ns | 437 ns | **1.39x** | 561 ns | 423 ns | **1.33x** |
| fireworks.jpeg 4096 B | 803 ns | 619 ns | **1.30x** | 774 ns | 613 ns | **1.26x** |
| fireworks.jpeg 16384 B | 1.4 µs | 1.2 µs | **1.16x** | 1.3 µs | 1.2 µs | **1.15x** |
| fireworks.jpeg 65536 B | 2.4 µs | 2.2 µs | **1.11x** | 2.3 µs | 2.1 µs | **1.10x** |
| html 64 B | 112 ns | 62 ns | **1.82x** | 89 ns | 56 ns | **1.60x** |
| html 200 B | 195 ns | 124 ns | **1.57x** | 169 ns | 120 ns | **1.41x** |
| html 256 B | 216 ns | 132 ns | **1.63x** | 187 ns | 131 ns | **1.43x** |
| html 1024 B | 910 ns | 736 ns | **1.24x** | 855 ns | 732 ns | **1.17x** |
| html 4096 B | 3.4 µs | 3.0 µs | **1.12x** | 3.2 µs | 3.0 µs | **1.06x** |
| html 16384 B | 10.5 µs | 9.8 µs | **1.08x** | 10.0 µs | 9.8 µs | **1.02x** |
| html 65536 B | 33.6 µs | 32.0 µs | **1.05x** | 31.8 µs | 32.4 µs | **0.98x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 89 ns | 35 ns | **2.56x** | 84 ns | 34 ns | **2.51x** |
| fireworks.jpeg 200 B | 131 ns | 91 ns | **1.45x** | 127 ns | 89 ns | **1.42x** |
| fireworks.jpeg 256 B | 148 ns | 107 ns | **1.39x** | 150 ns | 108 ns | **1.39x** |
| fireworks.jpeg 1024 B | 200 ns | 121 ns | **1.66x** | 184 ns | 110 ns | **1.68x** |
| fireworks.jpeg 4096 B | 327 ns | 171 ns | **1.91x** | 353 ns | 168 ns | **2.11x** |
| fireworks.jpeg 16384 B | 895 ns | 343 ns | **2.61x** | 893 ns | 335 ns | **2.67x** |
| fireworks.jpeg 65536 B | 3.4 µs | 1.4 µs | **2.50x** | 3.4 µs | 1.4 µs | **2.49x** |
| html 64 B | 80 ns | 41 ns | **1.95x** | 77 ns | 39 ns | **2.00x** |
| html 200 B | 87 ns | 31 ns | **2.78x** | 83 ns | 29 ns | **2.87x** |
| html 256 B | 88 ns | 31 ns | **2.80x** | 82 ns | 29 ns | **2.80x** |
| html 1024 B | 309 ns | 270 ns | **1.14x** | 290 ns | 264 ns | **1.10x** |
| html 4096 B | 1.4 µs | 1.4 µs | **1.00x** | 1.4 µs | 1.3 µs | **1.02x** |
| html 16384 B | 4.7 µs | 4.7 µs | **0.99x** | 4.7 µs | 4.6 µs | **1.02x** |
| html 65536 B | 16.2 µs | 15.8 µs | **1.03x** | 16.2 µs | 15.5 µs | **1.05x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 298 ns | 173 ns | **1.73x** | 251 ns | 157 ns | **1.60x** |
| fireworks.jpeg 200 B | 500 ns | 345 ns | **1.45x** | 447 ns | 341 ns | **1.31x** |
| fireworks.jpeg 256 B | 580 ns | 418 ns | **1.39x** | 547 ns | 421 ns | **1.30x** |
| fireworks.jpeg 1024 B | 983 ns | 712 ns | **1.38x** | 940 ns | 711 ns | **1.32x** |
| fireworks.jpeg 4096 B | 1.7 µs | 1.4 µs | **1.27x** | 1.7 µs | 1.3 µs | **1.28x** |
| fireworks.jpeg 16384 B | 4.1 µs | 3.1 µs | **1.31x** | 4.0 µs | 3.1 µs | **1.32x** |
| fireworks.jpeg 65536 B | 11.4 µs | 8.0 µs | **1.43x** | 11.2 µs | 7.9 µs | **1.42x** |
| html 64 B | 254 ns | 156 ns | **1.62x** | 220 ns | 148 ns | **1.49x** |
| html 200 B | 374 ns | 228 ns | **1.64x** | 315 ns | 217 ns | **1.45x** |
| html 256 B | 397 ns | 248 ns | **1.60x** | 345 ns | 234 ns | **1.48x** |
| html 1024 B | 1.4 µs | 1.2 µs | **1.21x** | 1.3 µs | 1.2 µs | **1.15x** |
| html 4096 B | 5.4 µs | 5.0 µs | **1.08x** | 5.1 µs | 5.0 µs | **1.03x** |
| html 16384 B | 17.0 µs | 15.6 µs | **1.09x** | 16.5 µs | 15.6 µs | **1.06x** |
| html 65536 B | 56.3 µs | 50.5 µs | **1.11x** | 55.0 µs | 50.7 µs | **1.09x** |

## Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 384 µs | 302 µs | **1.27x** | 357 µs | 275 µs | **1.30x** |
| events.ndjson | 970 µs | 751 µs | **1.29x** | 919 µs | 717 µs | **1.28x** |
| fireworks.jpeg | 23.6 µs | 14.2 µs | **1.66x** | 24.0 µs | 13.2 µs | **1.81x** |
| html_x_4 | 371 µs | 262 µs | **1.41x** | 342 µs | 198 µs | **1.73x** |
| json_api.json | 981 µs | 762 µs | **1.29x** | 913 µs | 725 µs | **1.26x** |
| urls.10K | 1.28 ms | 986 µs | **1.30x** | 1.18 ms | 961 µs | **1.23x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 142 µs | 94.9 µs | **1.50x** | 140 µs | 93.8 µs | **1.49x** |
| events.ndjson | 456 µs | 276 µs | **1.65x** | 459 µs | 253 µs | **1.82x** |
| fireworks.jpeg | 18.9 µs | 10.3 µs | **1.84x** | 18.8 µs | 8.0 µs | **2.34x** |
| html_x_4 | 191 µs | 103 µs | **1.85x** | 184 µs | 97.0 µs | **1.89x** |
| json_api.json | 461 µs | 276 µs | **1.67x** | 457 µs | 254 µs | **1.80x** |
| urls.10K | 491 µs | 265 µs | **1.85x** | 477 µs | 257 µs | **1.86x** |

