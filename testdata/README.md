# Test data

The corpus files (`alice29.txt`, `asyoulik.txt`, `fireworks.jpeg`, `geo.protodata`, `html`, `html_x_4`,
`kppkn.gtb`, `lcet10.txt`, `paper-100k.pdf`, `plrabn12.txt`, `urls.10K`) are the standard Snappy benchmark set
from [google/snappy](https://github.com/google/snappy/tree/main/testdata). The `.snappy` files,
`baddata*.snappy` and `streamerrorsequence.txt` come from the
[Snappier](https://github.com/brantburnett/Snappier) test suite (BSD-3-Clause).

`json_api.json` (minified array of event records), `json_indented.json` (the first half of the same records,
indented) and `events.ndjson` (the same records, one per line) are synthetic, deterministic JSON generated for this
project to represent typical API, cache and message-queue payloads.
