# Full benchmark results

AMD EPYC 7543 (Zen 3, AVX2), Ubuntu 22.04, BenchmarkDotNet 0.16 (medium job, in-process, one benchmark process per
physical core). Snappier 1.3.1 is the baseline; speedup is Snappier time / SnappySimd time. .NET 11 is RC 1. Raw block,
small-message and different-message results are the best of two runs for each library; streams and the corpus
are a single run.

Generated with `scripts/bench-table.mjs` (tables) and `scripts/bench-charts.mjs` (the SVG charts in this folder) from
`scripts/bench-remote.sh` output.

## Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 352 µs | 315 µs | **1.12x** | 331 µs | 287 µs | **1.15x** | 330 µs | 291 µs | **1.13x** |
| asyoulik.txt | 310 µs | 280 µs | **1.11x** | 296 µs | 254 µs | **1.16x** | 290 µs | 242 µs | **1.20x** |
| events.ndjson | 834 µs | 780 µs | **1.07x** | 784 µs | 745 µs | **1.05x** | 778 µs | 746 µs | **1.04x** |
| fireworks.jpeg | 4.1 µs | 4.1 µs | **1.01x** | 4.2 µs | 4.0 µs | **1.05x** | 4.3 µs | 4.0 µs | **1.09x** |
| geo.protodata | 42.5 µs | 38.8 µs | **1.10x** | 41.3 µs | 38.2 µs | **1.08x** | 41.2 µs | 37.8 µs | **1.09x** |
| html | 45.5 µs | 43.1 µs | **1.06x** | 43.3 µs | 42.9 µs | **1.01x** | 44.0 µs | 42.5 µs | **1.03x** |
| html_x_4 | 313 µs | 290 µs | **1.08x** | 285 µs | 246 µs | **1.16x** | 275 µs | 248 µs | **1.11x** |
| json_api.json | 839 µs | 779 µs | **1.08x** | 785 µs | 744 µs | **1.06x** | 783 µs | 738 µs | **1.06x** |
| json_indented.json | 467 µs | 446 µs | **1.05x** | 446 µs | 417 µs | **1.07x** | 438 µs | 417 µs | **1.05x** |
| kppkn.gtb | 276 µs | 253 µs | **1.09x** | 263 µs | 248 µs | **1.06x** | 263 µs | 242 µs | **1.09x** |
| lcet10.txt | 985 µs | 898 µs | **1.10x** | 951 µs | 868 µs | **1.10x** | 947 µs | 867 µs | **1.09x** |
| paper-100k.pdf | 7.4 µs | 7.0 µs | **1.07x** | 7.3 µs | 6.9 µs | **1.07x** | 7.3 µs | 6.9 µs | **1.06x** |
| plrabn12.txt | 1.33 ms | 1.21 ms | **1.10x** | 1.27 ms | 1.17 ms | **1.09x** | 1.28 ms | 1.15 ms | **1.11x** |
| urls.10K | 1.15 ms | 1.03 ms | **1.11x** | 1.09 ms | 1.04 ms | **1.05x** | 1.08 ms | 1.01 ms | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 114 µs | 85.1 µs | **1.34x** | 110 µs | 86.1 µs | **1.28x** | 108 µs | 86.1 µs | **1.26x** |
| asyoulik.txt | 106 µs | 76.7 µs | **1.38x** | 102 µs | 80.8 µs | **1.27x** | 102 µs | 80.8 µs | **1.26x** |
| events.ndjson | 358 µs | 224 µs | **1.60x** | 345 µs | 216 µs | **1.59x** | 386 µs | 218 µs | **1.77x** |
| fireworks.jpeg | 6.4 µs | 2.5 µs | **2.60x** | 6.4 µs | 2.5 µs | **2.60x** | 6.4 µs | 2.5 µs | **2.60x** |
| geo.protodata | 22.4 µs | 17.5 µs | **1.28x** | 22.5 µs | 18.0 µs | **1.25x** | 20.7 µs | 18.0 µs | **1.15x** |
| html | 22.3 µs | 21.0 µs | **1.06x** | 23.4 µs | 20.7 µs | **1.13x** | 22.2 µs | 20.8 µs | **1.07x** |
| html_x_4 | 122 µs | 84.2 µs | **1.44x** | 125 µs | 84.1 µs | **1.49x** | 116 µs | 84.2 µs | **1.38x** |
| json_api.json | 564 µs | 218 µs | **2.59x** | 564 µs | 217 µs | **2.60x** | 380 µs | 217 µs | **1.75x** |
| json_indented.json | 224 µs | 131 µs | **1.72x** | 220 µs | 124 µs | **1.77x** | 211 µs | 125 µs | **1.68x** |
| kppkn.gtb | 108 µs | 92.1 µs | **1.17x** | 112 µs | 92.7 µs | **1.21x** | 101 µs | 91.9 µs | **1.10x** |
| lcet10.txt | 323 µs | 228 µs | **1.42x** | 308 µs | 228 µs | **1.35x** | 317 µs | 228 µs | **1.39x** |
| paper-100k.pdf | 6.9 µs | 3.8 µs | **1.83x** | 7.2 µs | 3.8 µs | **1.91x** | 6.8 µs | 3.7 µs | **1.84x** |
| plrabn12.txt | 467 µs | 309 µs | **1.51x** | 436 µs | 319 µs | **1.37x** | 436 µs | 328 µs | **1.33x** |
| urls.10K | 398 µs | 224 µs | **1.78x** | 389 µs | 224 µs | **1.73x** | 383 µs | 228 µs | **1.68x** |

## Whole corpus in one operation (all files)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 7.60 ms | 7.03 ms | **1.08x** | 7.30 ms | 6.89 ms | **1.06x** | 7.30 ms | 6.91 ms | **1.06x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 3.02 ms | 1.85 ms | **1.63x** | 2.87 ms | 1.77 ms | **1.62x** | 2.77 ms | 1.82 ms | **1.53x** |

## 256 different messages of each size, round-robin (time per message)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 1.4 µs | 1.3 µs | **1.09x** | 1.3 µs | 1.2 µs | **1.08x** | 1.3 µs | 1.2 µs | **1.09x** |
| 4096 B | 6.3 µs | 6.0 µs | **1.05x** | 6.2 µs | 5.9 µs | **1.06x** | 6.1 µs | 5.8 µs | **1.05x** |
| 16384 B | 27.6 µs | 25.3 µs | **1.09x** | 26.9 µs | 24.7 µs | **1.09x** | 26.7 µs | 24.7 µs | **1.08x** |
| 65536 B | 109 µs | 100 µs | **1.08x** | 105 µs | 98.0 µs | **1.07x** | 105 µs | 97.5 µs | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 B | 518 ns | 434 ns | **1.19x** | 447 ns | 419 ns | **1.07x** | 422 ns | 423 ns | **1.00x** |
| 4096 B | 2.5 µs | 1.7 µs | **1.46x** | 2.2 µs | 1.7 µs | **1.34x** | 2.2 µs | 1.7 µs | **1.30x** |
| 16384 B | 10.6 µs | 6.5 µs | **1.64x** | 9.4 µs | 6.4 µs | **1.47x** | 9.3 µs | 6.4 µs | **1.44x** |
| 65536 B | 41.3 µs | 25.9 µs | **1.60x** | 36.9 µs | 25.4 µs | **1.45x** | 36.6 µs | 25.7 µs | **1.43x** |

## Small messages (`Snappy`, first N bytes of the file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 137 ns | 82 ns | **1.68x** | 115 ns | 73 ns | **1.57x** | 111 ns | 74 ns | **1.49x** |
| fireworks.jpeg 200 B | 277 ns | 186 ns | **1.49x** | 239 ns | 181 ns | **1.32x** | 235 ns | 180 ns | **1.31x** |
| fireworks.jpeg 256 B | 342 ns | 257 ns | **1.33x** | 309 ns | 240 ns | **1.29x** | 299 ns | 234 ns | **1.28x** |
| fireworks.jpeg 1024 B | 607 ns | 458 ns | **1.32x** | 563 ns | 455 ns | **1.24x** | 558 ns | 447 ns | **1.25x** |
| fireworks.jpeg 4096 B | 799 ns | 650 ns | **1.23x** | 777 ns | 648 ns | **1.20x** | 774 ns | 642 ns | **1.21x** |
| fireworks.jpeg 16384 B | 1.4 µs | 1.2 µs | **1.14x** | 1.3 µs | 1.2 µs | **1.12x** | 1.3 µs | 1.2 µs | **1.12x** |
| fireworks.jpeg 65536 B | 2.4 µs | 2.2 µs | **1.08x** | 2.3 µs | 2.2 µs | **1.07x** | 2.3 µs | 2.2 µs | **1.07x** |
| html 64 B | 110 ns | 64 ns | **1.72x** | 89 ns | 58 ns | **1.55x** | 84 ns | 58 ns | **1.44x** |
| html 200 B | 195 ns | 131 ns | **1.49x** | 169 ns | 125 ns | **1.35x** | 163 ns | 127 ns | **1.29x** |
| html 256 B | 209 ns | 140 ns | **1.49x** | 188 ns | 138 ns | **1.37x** | 185 ns | 136 ns | **1.36x** |
| html 1024 B | 916 ns | 742 ns | **1.24x** | 855 ns | 765 ns | **1.12x** | 848 ns | 746 ns | **1.14x** |
| html 4096 B | 3.4 µs | 3.0 µs | **1.11x** | 3.2 µs | 3.1 µs | **1.03x** | 3.2 µs | 3.0 µs | **1.06x** |
| html 16384 B | 10.4 µs | 9.6 µs | **1.08x** | 10.0 µs | 9.8 µs | **1.02x** | 10.2 µs | 9.6 µs | **1.07x** |
| html 65536 B | 32.9 µs | 31.1 µs | **1.06x** | 31.8 µs | 31.7 µs | **1.00x** | 32.9 µs | 30.8 µs | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 91 ns | 36 ns | **2.51x** | 83 ns | 34 ns | **2.42x** | 66 ns | 34 ns | **1.91x** |
| fireworks.jpeg 200 B | 131 ns | 75 ns | **1.76x** | 127 ns | 68 ns | **1.85x** | 109 ns | 69 ns | **1.58x** |
| fireworks.jpeg 256 B | 149 ns | 82 ns | **1.81x** | 145 ns | 78 ns | **1.85x** | 124 ns | 79 ns | **1.56x** |
| fireworks.jpeg 1024 B | 192 ns | 111 ns | **1.73x** | 180 ns | 105 ns | **1.72x** | 163 ns | 117 ns | **1.39x** |
| fireworks.jpeg 4096 B | 342 ns | 163 ns | **2.09x** | 350 ns | 156 ns | **2.25x** | 330 ns | 156 ns | **2.12x** |
| fireworks.jpeg 16384 B | 885 ns | 326 ns | **2.71x** | 879 ns | 327 ns | **2.69x** | 890 ns | 323 ns | **2.75x** |
| fireworks.jpeg 65536 B | 3.4 µs | 1.4 µs | **2.50x** | 3.4 µs | 1.3 µs | **2.51x** | 3.3 µs | 1.4 µs | **2.47x** |
| html 64 B | 80 ns | 42 ns | **1.91x** | 75 ns | 38 ns | **1.98x** | 58 ns | 39 ns | **1.48x** |
| html 200 B | 86 ns | 31 ns | **2.79x** | 81 ns | 29 ns | **2.77x** | 64 ns | 30 ns | **2.15x** |
| html 256 B | 86 ns | 31 ns | **2.76x** | 83 ns | 30 ns | **2.76x** | 65 ns | 30 ns | **2.16x** |
| html 1024 B | 306 ns | 255 ns | **1.20x** | 293 ns | 247 ns | **1.19x** | 265 ns | 260 ns | **1.02x** |
| html 4096 B | 1.4 µs | 1.3 µs | **1.04x** | 1.4 µs | 1.3 µs | **1.04x** | 1.3 µs | 1.3 µs | **0.98x** |
| html 16384 B | 4.7 µs | 4.5 µs | **1.04x** | 4.7 µs | 4.5 µs | **1.04x** | 4.4 µs | 4.5 µs | **0.97x** |
| html 65536 B | 16.1 µs | 15.2 µs | **1.06x** | 16.3 µs | 15.1 µs | **1.08x** | 15.3 µs | 15.1 µs | **1.01x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 299 ns | 175 ns | **1.71x** | 248 ns | 158 ns | **1.57x** | 220 ns | 147 ns | **1.50x** |
| fireworks.jpeg 200 B | 491 ns | 335 ns | **1.47x** | 438 ns | 304 ns | **1.44x** | 403 ns | 314 ns | **1.28x** |
| fireworks.jpeg 256 B | 580 ns | 414 ns | **1.40x** | 525 ns | 381 ns | **1.38x** | 492 ns | 385 ns | **1.28x** |
| fireworks.jpeg 1024 B | 1.0 µs | 718 ns | **1.40x** | 895 ns | 699 ns | **1.28x** | 876 ns | 702 ns | **1.25x** |
| fireworks.jpeg 4096 B | 1.7 µs | 1.3 µs | **1.26x** | 1.6 µs | 1.3 µs | **1.24x** | 1.6 µs | 1.3 µs | **1.25x** |
| fireworks.jpeg 16384 B | 4.0 µs | 3.1 µs | **1.31x** | 3.9 µs | 3.0 µs | **1.29x** | 3.9 µs | 3.0 µs | **1.29x** |
| fireworks.jpeg 65536 B | 13.0 µs | 8.9 µs | **1.46x** | 11.7 µs | 7.8 µs | **1.50x** | 11.7 µs | 7.7 µs | **1.53x** |
| html 64 B | 251 ns | 161 ns | **1.56x** | 210 ns | 145 ns | **1.45x** | 181 ns | 137 ns | **1.32x** |
| html 200 B | 377 ns | 231 ns | **1.63x** | 317 ns | 219 ns | **1.45x** | 289 ns | 211 ns | **1.37x** |
| html 256 B | 393 ns | 248 ns | **1.59x** | 342 ns | 236 ns | **1.45x** | 322 ns | 225 ns | **1.43x** |
| html 1024 B | 1.4 µs | 1.2 µs | **1.17x** | 1.3 µs | 1.2 µs | **1.13x** | 1.3 µs | 1.2 µs | **1.09x** |
| html 4096 B | 5.3 µs | 4.9 µs | **1.08x** | 5.1 µs | 4.8 µs | **1.04x** | 5.0 µs | 4.9 µs | **1.02x** |
| html 16384 B | 17.0 µs | 15.4 µs | **1.10x** | 15.9 µs | 15.2 µs | **1.05x** | 16.2 µs | 15.1 µs | **1.07x** |
| html 65536 B | 55.2 µs | 49.4 µs | **1.12x** | 53.3 µs | 48.4 µs | **1.10x** | 54.6 µs | 48.5 µs | **1.13x** |

## Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 373 µs | 325 µs | **1.15x** | 356 µs | 298 µs | **1.19x** | 358 µs | 300 µs | **1.19x** |
| events.ndjson | 972 µs | 832 µs | **1.17x** | 917 µs | 777 µs | **1.18x** | 913 µs | 782 µs | **1.17x** |
| fireworks.jpeg | 24.8 µs | 14.2 µs | **1.74x** | 25.5 µs | 13.2 µs | **1.93x** | 24.2 µs | 12.2 µs | **1.98x** |
| html_x_4 | 367 µs | 314 µs | **1.17x** | 331 µs | 260 µs | **1.27x** | 340 µs | 262 µs | **1.30x** |
| json_api.json | 983 µs | 833 µs | **1.18x** | 915 µs | 776 µs | **1.18x** | 908 µs | 776 µs | **1.17x** |
| urls.10K | 1.27 ms | 1.07 ms | **1.19x** | 1.18 ms | 1.06 ms | **1.11x** | 1.18 ms | 1.05 ms | **1.13x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 143 µs | 92.9 µs | **1.53x** | 143 µs | 91.7 µs | **1.56x** | 138 µs | 91.8 µs | **1.50x** |
| events.ndjson | 461 µs | 267 µs | **1.73x** | 450 µs | 251 µs | **1.79x** | 438 µs | 251 µs | **1.74x** |
| fireworks.jpeg | 19.7 µs | 10.0 µs | **1.97x** | 19.1 µs | 8.7 µs | **2.18x** | 19.2 µs | 8.1 µs | **2.36x** |
| html_x_4 | 184 µs | 102 µs | **1.81x** | 186 µs | 95.7 µs | **1.94x** | 177 µs | 95.6 µs | **1.85x** |
| json_api.json | 456 µs | 274 µs | **1.67x** | 455 µs | 252 µs | **1.81x** | 436 µs | 249 µs | **1.75x** |
| urls.10K | 494 µs | 265 µs | **1.86x** | 475 µs | 251 µs | **1.90x** | 470 µs | 250 µs | **1.88x** |

