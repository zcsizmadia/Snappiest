// Generates the README benchmark charts (light and dark SVG) from scripts/bench-remote.sh output.
//
// Usage: node scripts/bench-charts.mjs <single-threaded results> <parallel results> [output dir]
//   e.g. node scripts/bench-charts.mjs /tmp/readme-net10.0.txt /tmp/readme-par.txt docs/benchmarks
//   Several runs can be joined with "+" (run1.txt+run2.txt): each library gets its best (lowest) mean.
//
// Charts (each as <name>-light.svg and <name>-dark.svg, shown with <picture> in the README):
//   speedup       times faster than Snappier: block/stream x compress/decompress, one bar per file
//   small         times faster than Snappier by message size (64 B - 64 KB), compress and decompress
//   parallel      times faster than Snappier by thread count (opt-in parallel), json_api.json 16 MB
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';

const [singleFile, parallelFile, outDir = 'docs/benchmarks'] = process.argv.slice(2);

// Validated with the dataviz palette validator (3 slots, adjacent pairs, both modes)
const THEMES = {
  light: {
    surface: '#fcfcfb', ink: '#0b0b0b', secondary: '#52514e', muted: '#898781',
    grid: '#e1e0d9', axis: '#c3c2b7', series: ['#2a78d6', '#eb6834', '#1baf7a'],
  },
  dark: {
    surface: '#1a1a19', ink: '#ffffff', secondary: '#c3c2b7', muted: '#898781',
    grid: '#2c2c2a', axis: '#383835', series: ['#3987e5', '#d95926', '#199e70'],
  },
};
const FONT = "system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";

// ---------------------------------------------------------------- parsing

// Rows of BenchmarkDotNet GitHub tables, keyed by column name, tagged with the class heading
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
    const ns = toNs(row.Mean);
    const same = r => r.cls === cls && r.Method === row.Method && r.File === row.File && r.Size === row.Size && r.Threads === row.Threads;
    const previous = rows.findIndex(same);
    if (previous < 0) {
      rows.push({ cls, operation, library, ...row, ns });
    } else if (ns < rows[previous].ns) {
      rows[previous] = { cls, operation, library, ...row, ns };
    }
  }
  return rows;
}

function toNs(mean) {
  const m = mean.replace(/,/g, '').match(/([\d.]+)\s*(ns|μs|us|ms|s)/);
  return parseFloat(m[1]) * { ns: 1, 'μs': 1e3, us: 1e3, ms: 1e6, s: 1e9 }[m[2]];
}

// Snappier time / SnappySimd time for rows that match on everything but the library
function speedup(rows, filter, key) {
  const result = new Map();
  for (const r of rows.filter(r => filter(r) && r.library === 'SnappySimd')) {
    const base = rows.find(b => filter(b) && b.library === 'Snappier' && b.operation === r.operation && key(b) === key(r));
    if (base) result.set(key(r), base.ns / r.ns);
  }
  return result;
}

// ---------------------------------------------------------------- svg helpers

const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');
const fmt = x => `${x.toFixed(2)}×`;
// Rough width of system-ui text, for layout only
const textWidth = (s, size) => [...String(s)].length * size * 0.56;

function svg(width, height, theme, title, desc, body) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="t d" font-family="${FONT}">
<title id="t">${esc(title)}</title>
<desc id="d">${esc(desc)}</desc>
<rect width="${width}" height="${height}" rx="8" fill="${theme.surface}"/>
${body}
</svg>
`;
}

const text = (x, y, s, size, fill, extra = '') =>
  `<text x="${x}" y="${y}" font-size="${size}" fill="${fill}" ${extra}>${esc(s)}</text>`;

// Horizontal bar with a 4px rounded end, square at the baseline
function hbar(x0, y, length, thickness, fill) {
  if (length <= 0) return '';
  const r = Math.min(4, length, thickness / 2);
  const x1 = x0 + length;
  return `<path d="M${x0},${y} H${x1 - r} Q${x1},${y} ${x1},${y + r} V${y + thickness - r} Q${x1},${y + thickness} ${x1 - r},${y + thickness} H${x0} Z" fill="${fill}"/>`;
}

// ---------------------------------------------------------------- chart 1: speedup per file

function speedupChart(rows, theme) {
  const panels = [
    { title: 'Block compress', cls: 'BlockBenchmarks', op: 'Compress' },
    { title: 'Block decompress', cls: 'BlockBenchmarks', op: 'Decompress' },
    { title: 'Stream compress', cls: 'StreamBenchmarks', op: 'Compress' },
    { title: 'Stream decompress', cls: 'StreamBenchmarks', op: 'Decompress' },
  ].map(p => ({ ...p, data: [...speedup(rows, r => r.cls === p.cls && r.operation === p.op, r => r.File)] }));

  const maxValue = Math.ceil(Math.max(...panels.flatMap(p => p.data.map(d => d[1]))) * 2) / 2;
  const width = 960, gap = 32, margin = 24, labelWidth = 120, valueRoom = 52, rowHeight = 22, barThickness = 14;
  const panelWidth = (width - 2 * margin - gap) / 2;
  const plotWidth = panelWidth - labelWidth - valueRoom;
  const scale = v => (v / maxValue) * plotWidth;
  let body = '';
  let y = 64;

  body += text(margin, 32, 'How much faster than Snappier', 18, theme.ink, 'font-weight="600"');
  body += text(margin, 52, 'Snappier time ÷ SnappySimd time, whole files, single-threaded (line at 1× = Snappier; longer is faster)', 13, theme.secondary);

  for (let rowIndex = 0; rowIndex < 2; rowIndex++) {
    const pair = panels.slice(rowIndex * 2, rowIndex * 2 + 2);
    const rowsInPanel = Math.max(...pair.map(p => p.data.length));
    pair.forEach((panel, column) => {
      const x = margin + column * (panelWidth + gap);
      const plotX = x + labelWidth;
      body += text(x, y + 14, panel.title, 14, theme.ink, 'font-weight="600"');
      const top = y + 26;
      const plotHeight = rowsInPanel * rowHeight;

      // Recessive grid at whole multiples, the 1.0x reference line stronger
      for (let v = 0; v <= maxValue; v += 0.5) {
        const gx = plotX + scale(v);
        const strong = v === 1;
        body += `<line x1="${gx}" y1="${top}" x2="${gx}" y2="${top + plotHeight}" stroke="${strong ? theme.secondary : theme.grid}" stroke-width="1"/>`;
        if (Number.isInteger(v)) body += text(gx, top + plotHeight + 14, `${v}×`, 11, theme.muted, 'text-anchor="middle"');
      }

      panel.data.forEach(([file, value], i) => {
        const by = top + i * rowHeight + (rowHeight - barThickness) / 2;
        body += text(plotX - 8, by + barThickness - 3, file, 12, theme.secondary, 'text-anchor="end"');
        body += hbar(plotX, by, scale(value), barThickness, theme.series[0]);
        body += text(plotX + scale(value) + 6, by + barThickness - 3, fmt(value), 12, theme.ink);
      });
    });
    y += 26 + rowsInPanel * rowHeight + 36;
  }

  const height = y;
  const all = panels.map(p => `${p.title}: ${p.data.map(([f, v]) => `${f} ${fmt(v)}`).join(', ')}`).join('; ');
  return svg(width, height, theme, 'How much faster than Snappier (single-threaded)', all, body);
}

// ---------------------------------------------------------------- line charts

// x positions are categories (evenly spaced); y is the speedup with a 1.0x reference line
function lineChart({ width, height, theme, title, subtitle, xLabels, xTitle, series, panelsTitle, rightMargin = 170 }) {
  const margin = { left: 56, right: rightMargin, top: panelsTitle ? 96 : 76, bottom: 52 };
  const plotWidth = width - margin.left - margin.right;
  const plotHeight = height - margin.top - margin.bottom;
  const maxValue = Math.max(1.5, Math.ceil(Math.max(...series.flatMap(s => s.values.filter(v => v != null))) + 0.5));
  const step = maxValue > 6 ? 2 : maxValue > 3 ? 1 : 0.5;
  const x = i => margin.left + (xLabels.length === 1 ? plotWidth / 2 : (i / (xLabels.length - 1)) * plotWidth);
  const y = v => margin.top + plotHeight - (v / maxValue) * plotHeight;
  let body = '';

  body += text(24, 32, title, 18, theme.ink, 'font-weight="600"');
  body += text(24, 52, subtitle, 13, theme.secondary);
  if (panelsTitle) body += text(margin.left, margin.top - 10, panelsTitle, 14, theme.ink, 'font-weight="600"');

  for (let v = 0; v <= maxValue + 1e-9; v += step) {
    const gy = y(v);
    body += `<line x1="${margin.left}" y1="${gy}" x2="${margin.left + plotWidth}" y2="${gy}" stroke="${v === 0 ? theme.axis : theme.grid}" stroke-width="1"/>`;
    body += text(margin.left - 8, gy + 4, `${Number.isInteger(v) ? v : v.toFixed(1)}×`, 11, theme.muted, 'text-anchor="end"');
  }

  // 1.0x = Snappier reference (named in the subtitle, so the end labels have the right edge to themselves)
  body += `<line x1="${margin.left}" y1="${y(1)}" x2="${margin.left + plotWidth}" y2="${y(1)}" stroke="${theme.secondary}" stroke-width="1"/>`;

  xLabels.forEach((label, i) => body += text(x(i), margin.top + plotHeight + 18, label, 11, theme.muted, 'text-anchor="middle"'));
  body += text(margin.left + plotWidth / 2, height - 12, xTitle, 12, theme.secondary, 'text-anchor="middle"');

  // Lines, end markers with a surface ring, labels at the end (text in ink, identity from the marker)
  const ends = [];
  series.forEach((s, index) => {
    const color = theme.series[index];
    const points = s.values.map((v, i) => (v == null ? null : [x(i), y(v)])).filter(Boolean);
    body += `<polyline points="${points.map(p => p.join(',')).join(' ')}" fill="none" stroke="${color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>`;
    for (const [px, py] of points) body += `<circle cx="${px}" cy="${py}" r="4" fill="${color}" stroke="${theme.surface}" stroke-width="2"/>`;
    const last = points[points.length - 1];
    ends.push({ y: last[1], label: `${s.name} ${fmt(s.values[s.values.length - 1])}`, x: last[0], color });
  });

  // Keep end labels from colliding: spread them at least 16px apart, joined to their line ends by leader lines
  ends.sort((a, b) => a.y - b.y);
  for (let i = 1; i < ends.length; i++) ends[i].labelY = Math.max(ends[i].y, (ends[i - 1].labelY ?? ends[i - 1].y) + 16);
  ends[0].labelY = ends[0].y;
  for (const e of ends) {
    const lx = e.x + 12;
    if (Math.abs(e.labelY - e.y) > 1) body += `<line x1="${e.x + 5}" y1="${e.y}" x2="${lx - 2}" y2="${e.labelY}" stroke="${theme.muted}" stroke-width="1"/>`;
    body += `<circle cx="${lx + 4}" cy="${e.labelY}" r="4" fill="${e.color}"/>`;
    body += text(lx + 12, e.labelY + 4, e.label, 11, theme.ink);
  }

  return body;
}

// ---------------------------------------------------------------- chart 2: small messages

function smallChart(rows, theme) {
  const small = rows.filter(r => r.cls === 'SmallBlockBenchmarks');
  const sizes = [...new Set(small.map(r => Number(r.Size)))].sort((a, b) => a - b);
  const label = s => (s >= 1024 ? `${s / 1024} KB` : `${s} B`);
  const width = 960, height = 380;
  const files = ['html', 'fireworks.jpeg'];
  let body = '';
  const descParts = [];

  files.forEach((file, index) => {
    const series = ['Decompress', 'Compress'].map(op => {
      const values = speedup(small, r => r.File === file && r.operation === op, r => Number(r.Size));
      descParts.push(`${file} ${op}: ${sizes.map(s => `${label(s)} ${fmt(values.get(s))}`).join(', ')}`);
      return { name: op, values: sizes.map(s => values.get(s) ?? null) };
    });
    const panel = lineChart({
      width: width / 2, height, theme,
      title: index === 0 ? 'Small messages' : '',
      subtitle: index === 0 ? 'Times faster than Snappier by message size (first N bytes of the file; line at 1× = Snappier)' : '',
      rightMargin: 140,
      xLabels: sizes.map(label), xTitle: 'Message size', series,
      panelsTitle: file === 'html' ? 'html (compressible)' : 'fireworks.jpeg (incompressible)',
    });
    body += `<g transform="translate(${index * width / 2},0)">${panel}</g>`;
  });

  return svg(width, height, theme, 'Small messages: times faster than Snappier by size', descParts.join('; '), body);
}

// ---------------------------------------------------------------- chart 3: parallel scaling

function parallelChart(rows, theme) {
  const file = 'json_api.json';
  const parallel = rows.filter(r => r.File === file);
  const threads = [...new Set(parallel.map(r => Number(r.Threads)))].sort((a, b) => a - b);
  const operations = [
    { name: 'Block compress', op: 'Compress' },
    { name: 'Stream compress', op: 'StreamCompress' },
    { name: 'Stream decompress', op: 'StreamDecompress' },
  ];
  const series = operations.map(({ name, op }) => {
    const at = (library, t) => parallel.find(r => r.operation === op && r.library === library && Number(r.Threads) === t)?.ns;
    const snappier = at('Snappier', threads[0]);
    const single = at('SnappySimd', threads[0]);
    return { name, values: [snappier / single, ...threads.map(t => snappier / at('SnappySimdParallel', t))] };
  });

  const body = lineChart({
    width: 960, height: 380, theme,
    title: 'Opt-in parallel (SnappyParallelOptions)',
    subtitle: `Times faster than Snappier by thread count, ${file} repeated to 16 MB (1 thread = single-threaded SnappySimd; line at 1× = Snappier)`,
    xLabels: ['1', ...threads.map(String)], xTitle: 'Threads (MaxDegreeOfParallelism)', series,
  });
  const desc = series.map(s => `${s.name}: ${s.values.map((v, i) => `${i === 0 ? 1 : threads[i - 1]} threads ${fmt(v)}`).join(', ')}`).join('; ');
  return svg(960, 380, theme, 'Opt-in parallel: times faster than Snappier by thread count', desc, body);
}

// ---------------------------------------------------------------- main

mkdirSync(outDir, { recursive: true });
const read = files => files.split('+').map(f => readFileSync(f, 'utf8')).join('\n');
const single = parse(read(singleFile));
const parallel = parse(read(parallelFile));
for (const [name, theme] of Object.entries(THEMES)) {
  writeFileSync(`${outDir}/speedup-${name}.svg`, speedupChart(single, theme));
  writeFileSync(`${outDir}/small-${name}.svg`, smallChart(single, theme));
  writeFileSync(`${outDir}/parallel-${name}.svg`, parallelChart(parallel, theme));
}
console.log(`Wrote speedup, small and parallel charts (light and dark) to ${outDir}`);
