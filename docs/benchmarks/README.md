# Full benchmark results

AMD EPYC 7543 (Zen 3, AVX2), Ubuntu 22.04, BenchmarkDotNet 0.16 (medium job, in-process, one benchmark process per
physical core). Snappier 1.3.1 is the baseline; speedup is Snappier time / SnappySimd time. .NET 11 is RC 1. Raw block
and small-message results are the best of two runs for each library; streams and the corpus are a single run.

Generated with `scripts/bench-table.mjs` (tables) and `scripts/bench-charts.mjs` (the SVG charts in this folder) from
`scripts/bench-remote.sh` output.

## Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)

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

## Whole corpus in one operation (all files)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 7.61 ms | 7.01 ms | **1.08x** | 7.33 ms | 6.88 ms | **1.07x** | 7.30 ms | 6.85 ms | **1.07x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| all files | 3.02 ms | 1.75 ms | **1.73x** | 2.85 ms | 1.82 ms | **1.57x** | 2.82 ms | 1.83 ms | **1.55x** |

## Small messages (`Snappy`, first N bytes of the file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 140 ns | 80 ns | **1.75x** | 114 ns | 75 ns | **1.51x** | 108 ns | 73 ns | **1.47x** |
| fireworks.jpeg 200 B | 280 ns | 186 ns | **1.51x** | 245 ns | 181 ns | **1.35x** | 232 ns | 177 ns | **1.31x** |
| fireworks.jpeg 256 B | 338 ns | 245 ns | **1.38x** | 304 ns | 242 ns | **1.26x** | 295 ns | 233 ns | **1.27x** |
| fireworks.jpeg 1024 B | 611 ns | 460 ns | **1.33x** | 575 ns | 454 ns | **1.27x** | 552 ns | 448 ns | **1.23x** |
| fireworks.jpeg 4096 B | 797 ns | 643 ns | **1.24x** | 773 ns | 643 ns | **1.20x** | 769 ns | 642 ns | **1.20x** |
| fireworks.jpeg 16384 B | 1.4 µs | 1.2 µs | **1.15x** | 1.3 µs | 1.2 µs | **1.12x** | 1.3 µs | 1.2 µs | **1.12x** |
| fireworks.jpeg 65536 B | 2.4 µs | 2.2 µs | **1.09x** | 2.4 µs | 2.2 µs | **1.08x** | 2.4 µs | 2.2 µs | **1.10x** |
| html 64 B | 110 ns | 64 ns | **1.71x** | 89 ns | 59 ns | **1.51x** | 84 ns | 58 ns | **1.44x** |
| html 200 B | 195 ns | 130 ns | **1.50x** | 170 ns | 125 ns | **1.35x** | 163 ns | 126 ns | **1.29x** |
| html 256 B | 213 ns | 140 ns | **1.52x** | 189 ns | 137 ns | **1.38x** | 184 ns | 137 ns | **1.35x** |
| html 1024 B | 910 ns | 745 ns | **1.22x** | 849 ns | 746 ns | **1.14x** | 846 ns | 747 ns | **1.13x** |
| html 4096 B | 3.3 µs | 3.0 µs | **1.12x** | 3.2 µs | 3.0 µs | **1.06x** | 3.2 µs | 3.0 µs | **1.06x** |
| html 16384 B | 10.4 µs | 9.6 µs | **1.08x** | 10.0 µs | 9.7 µs | **1.04x** | 10.0 µs | 9.6 µs | **1.05x** |
| html 65536 B | 32.7 µs | 31.2 µs | **1.05x** | 31.8 µs | 31.4 µs | **1.01x** | 31.9 µs | 30.8 µs | **1.04x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 85 ns | 36 ns | **2.38x** | 81 ns | 35 ns | **2.34x** | 65 ns | 33 ns | **1.96x** |
| fireworks.jpeg 200 B | 128 ns | 89 ns | **1.45x** | 125 ns | 88 ns | **1.42x** | 107 ns | 89 ns | **1.21x** |
| fireworks.jpeg 256 B | 147 ns | 99 ns | **1.49x** | 142 ns | 99 ns | **1.43x** | 123 ns | 99 ns | **1.24x** |
| fireworks.jpeg 1024 B | 195 ns | 122 ns | **1.60x** | 180 ns | 113 ns | **1.59x** | 162 ns | 124 ns | **1.31x** |
| fireworks.jpeg 4096 B | 337 ns | 168 ns | **2.01x** | 351 ns | 167 ns | **2.10x** | 334 ns | 167 ns | **2.00x** |
| fireworks.jpeg 16384 B | 886 ns | 338 ns | **2.62x** | 881 ns | 334 ns | **2.64x** | 887 ns | 329 ns | **2.70x** |
| fireworks.jpeg 65536 B | 3.4 µs | 1.4 µs | **2.49x** | 3.7 µs | 1.4 µs | **2.70x** | 3.4 µs | 1.4 µs | **2.48x** |
| html 64 B | 79 ns | 44 ns | **1.81x** | 77 ns | 40 ns | **1.91x** | 57 ns | 39 ns | **1.44x** |
| html 200 B | 85 ns | 35 ns | **2.43x** | 81 ns | 37 ns | **2.18x** | 62 ns | 37 ns | **1.69x** |
| html 256 B | 87 ns | 35 ns | **2.45x** | 83 ns | 38 ns | **2.17x** | 63 ns | 38 ns | **1.67x** |
| html 1024 B | 313 ns | 292 ns | **1.07x** | 295 ns | 302 ns | **0.98x** | 265 ns | 306 ns | **0.87x** |
| html 4096 B | 1.4 µs | 1.4 µs | **0.96x** | 1.4 µs | 1.4 µs | **0.97x** | 1.3 µs | 1.4 µs | **0.90x** |
| html 16384 B | 4.7 µs | 4.6 µs | **1.02x** | 4.7 µs | 4.5 µs | **1.04x** | 4.4 µs | 4.6 µs | **0.96x** |
| html 65536 B | 16.2 µs | 15.3 µs | **1.05x** | 16.2 µs | 15.3 µs | **1.06x** | 15.2 µs | 15.3 µs | **0.99x** |

**RoundTripArray**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fireworks.jpeg 64 B | 301 ns | 176 ns | **1.71x** | 253 ns | 167 ns | **1.52x** | 221 ns | 144 ns | **1.54x** |
| fireworks.jpeg 200 B | 492 ns | 351 ns | **1.40x** | 440 ns | 343 ns | **1.28x** | 399 ns | 317 ns | **1.26x** |
| fireworks.jpeg 256 B | 582 ns | 425 ns | **1.37x** | 530 ns | 418 ns | **1.27x** | 477 ns | 384 ns | **1.24x** |
| fireworks.jpeg 1024 B | 995 ns | 734 ns | **1.35x** | 903 ns | 734 ns | **1.23x** | 840 ns | 685 ns | **1.23x** |
| fireworks.jpeg 4096 B | 1.7 µs | 1.4 µs | **1.24x** | 1.7 µs | 1.4 µs | **1.17x** | 1.6 µs | 1.2 µs | **1.26x** |
| fireworks.jpeg 16384 B | 4.1 µs | 3.2 µs | **1.30x** | 4.0 µs | 3.1 µs | **1.27x** | 3.7 µs | 2.9 µs | **1.27x** |
| fireworks.jpeg 65536 B | 11.9 µs | 8.4 µs | **1.42x** | 11.6 µs | 7.8 µs | **1.49x** | 11.3 µs | 7.6 µs | **1.49x** |
| html 64 B | 251 ns | 164 ns | **1.53x** | 215 ns | 154 ns | **1.40x** | 184 ns | 134 ns | **1.37x** |
| html 200 B | 365 ns | 236 ns | **1.55x** | 318 ns | 233 ns | **1.36x** | 278 ns | 212 ns | **1.31x** |
| html 256 B | 384 ns | 255 ns | **1.51x** | 345 ns | 251 ns | **1.38x** | 310 ns | 230 ns | **1.35x** |
| html 1024 B | 1.4 µs | 1.2 µs | **1.16x** | 1.3 µs | 1.2 µs | **1.08x** | 1.2 µs | 1.2 µs | **1.04x** |
| html 4096 B | 5.3 µs | 5.0 µs | **1.07x** | 5.1 µs | 5.0 µs | **1.02x** | 4.9 µs | 4.9 µs | **1.01x** |
| html 16384 B | 16.9 µs | 15.3 µs | **1.10x** | 16.4 µs | 15.3 µs | **1.07x** | 15.5 µs | 15.0 µs | **1.03x** |
| html 65536 B | 56.8 µs | 49.2 µs | **1.15x** | 54.2 µs | 48.7 µs | **1.11x** | 52.2 µs | 48.4 µs | **1.08x** |

## Streams (`SnappyStream`, whole file)

**Compress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 374 µs | 322 µs | **1.16x** | 357 µs | 293 µs | **1.22x** | 358 µs | 300 µs | **1.19x** |
| events.ndjson | 973 µs | 831 µs | **1.17x** | 911 µs | 778 µs | **1.17x** | 905 µs | 771 µs | **1.17x** |
| fireworks.jpeg | 23.9 µs | 14.9 µs | **1.60x** | 23.9 µs | 12.9 µs | **1.86x** | 24.1 µs | 12.2 µs | **1.98x** |
| html_x_4 | 369 µs | 313 µs | **1.18x** | 329 µs | 258 µs | **1.27x** | 340 µs | 265 µs | **1.28x** |
| json_api.json | 982 µs | 830 µs | **1.18x** | 915 µs | 780 µs | **1.17x** | 910 µs | 769 µs | **1.18x** |
| urls.10K | 1.23 ms | 1.08 ms | **1.13x** | 1.18 ms | 1.05 ms | **1.12x** | 1.18 ms | 1.04 ms | **1.13x** |

**Decompress**

| Input | Snappier (net8.0) | SnappySimd (net8.0) | Speedup | Snappier (net10.0) | SnappySimd (net10.0) | Speedup | Snappier (net11.0) | SnappySimd (net11.0) | Speedup |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| alice29.txt | 140 µs | 92.9 µs | **1.51x** | 143 µs | 91.5 µs | **1.56x** | 140 µs | 95.5 µs | **1.47x** |
| events.ndjson | 462 µs | 274 µs | **1.69x** | 454 µs | 250 µs | **1.82x** | 443 µs | 251 µs | **1.77x** |
| fireworks.jpeg | 19.0 µs | 11.4 µs | **1.66x** | 18.3 µs | 8.0 µs | **2.29x** | 18.9 µs | 8.1 µs | **2.34x** |
| html_x_4 | 183 µs | 102 µs | **1.79x** | 188 µs | 96.3 µs | **1.95x** | 178 µs | 97.7 µs | **1.82x** |
| json_api.json | 461 µs | 267 µs | **1.73x** | 459 µs | 250 µs | **1.84x** | 436 µs | 249 µs | **1.75x** |
| urls.10K | 491 µs | 275 µs | **1.79x** | 474 µs | 253 µs | **1.88x** | 473 µs | 254 µs | **1.86x** |

