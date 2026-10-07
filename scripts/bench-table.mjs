// Turns the output of scripts/bench-remote.sh (one file per runtime) into Markdown tables comparing Snappier and
// SnappySimd: one table per benchmark class, with a speedup column per runtime.
//
// Usage: node scripts/bench-table.mjs net8.0=out8.txt net10.0=out10.txt net11.0=out11.txt
// Several runs of one runtime can be joined with "+" (net8.0=run1.txt+run2.txt): each library then gets its best
// (lowest) mean across the runs.
import { readFileSync } from 'node:fs';

const runs = process.argv.slice(2).map(arg => {
  const [runtime, file] = arg.split('=');
  return { runtime, text: file.split('+').map(f => readFileSync(f, 'utf8')).join('\n') };
});

// Parses BenchmarkDotNet GitHub tables: "| Method | Categories | <Param> | Mean | ..."
function parse(text) {
  const rows = [];
  let columns = null;
  let cls = null;
  for (const raw of text.split(/\r?\n/)) {
    const heading = raw.match(/^### (\w+)/);
    if (heading) { cls = heading[1]; columns = null; continue; }
    if (!raw.startsWith('|')) continue;
    const cells = raw.split('|').slice(1, -1).map(c => c.replace(/\*\*/g, '').trim());
    if (cells[0] === 'Method') { columns = cells; continue; }
    if (!columns || !cells[0] || cells[0].startsWith('-')) continue;
    const row = Object.fromEntries(columns.map((c, i) => [c, cells[i]]));
    const [operation, library] = row.Method.split('_');
    const param = [row.File, row.Size && `${row.Size} B`].filter(Boolean).join(' ') || 'all files';
    const previous = rows.find(r => r.cls === cls && r.operation === operation && r.library === library && r.param === param);
    if (!previous) {
      rows.push({ cls, operation, library, param, mean: row.Mean });
    } else if (toNs(row.Mean) < toNs(previous.mean)) {
      previous.mean = row.Mean;
    }
  }
  return rows;
}

// "1,234.5 μs" -> nanoseconds
function toNs(mean) {
  const m = mean.replace(/,/g, '').match(/([\d.]+)\s*(ns|μs|us|ms|s)/);
  const scale = { ns: 1, 'μs': 1e3, us: 1e3, ms: 1e6, s: 1e9 }[m[2]];
  return parseFloat(m[1]) * scale;
}

function format(ns) {
  if (ns >= 1e6) return `${(ns / 1e6).toFixed(2)} ms`;
  if (ns >= 1e3) return `${(ns / 1e3).toFixed(ns >= 1e5 ? 0 : 1)} µs`;
  return `${ns.toFixed(0)} ns`;
}

const data = runs.map(r => ({ runtime: r.runtime, rows: parse(r.text) }));
const classes = [...new Set(data.flatMap(d => d.rows.map(r => r.cls)))];
const titles = {
  BlockBenchmarks: 'Raw blocks (`Snappy.Compress` / `Snappy.Decompress`, whole file)',
  StreamBenchmarks: 'Streams (`SnappyStream`, whole file)',
  SmallBlockBenchmarks: 'Small messages (`Snappy`, first N bytes of the file)',
  CorpusBenchmarks: 'Whole corpus in one operation (all files)',
  MessageMixBenchmarks: '256 different messages of each size, round-robin (time per message)',
};

for (const cls of classes) {
  const operations = [...new Set(data.flatMap(d => d.rows.filter(r => r.cls === cls).map(r => r.operation)))];
  console.log(`### ${titles[cls] ?? cls}\n`);
  for (const operation of operations) {
    const params = [...new Set(data.flatMap(d => d.rows.filter(r => r.cls === cls && r.operation === operation).map(r => r.param)))];
    const header = ['Input', ...data.flatMap(d => [`Snappier (${d.runtime})`, `SnappySimd (${d.runtime})`, 'Speedup'])];
    console.log(`**${operation}**\n`);
    console.log(`| ${header.join(' | ')} |`);
    console.log(`|${header.map((_, i) => (i === 0 ? ' --- ' : ' ---: ')).join('|')}|`);
    for (const param of params) {
      const cells = [param];
      for (const d of data) {
        const find = lib => d.rows.find(r => r.cls === cls && r.operation === operation && r.param === param && r.library === lib);
        const a = find('Snappier'), b = find('SnappySimd');
        if (!a || !b) { cells.push('', '', ''); continue; }
        const x = toNs(a.mean), y = toNs(b.mean);
        cells.push(format(x), format(y), `**${(x / y).toFixed(2)}x**`);
      }
      console.log(`| ${cells.join(' | ')} |`);
    }
    console.log();
  }
}
