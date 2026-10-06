// Writes a flat badge SVG (shields.io style) without any external service.
// Usage: node scripts/make-badge.mjs <label> <message> <color> <output.svg>
import { writeFileSync } from 'node:fs';

const [label, message, color, output] = process.argv.slice(2);

// Approximate Verdana 11px text width, the font badges are rendered with
const width = text => Math.round([...text].reduce((w, c) => w + (/[ilj.,|:]/.test(c) ? 3.5 : /[A-Z0-9]/.test(c) ? 7.5 : 6.5), 0)) + 10;
const lw = width(label);
const mw = width(message);
const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

writeFileSync(output, `<svg xmlns="http://www.w3.org/2000/svg" width="${lw + mw}" height="20" role="img" aria-label="${escape(label)}: ${escape(message)}">
  <title>${escape(label)}: ${escape(message)}</title>
  <linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient>
  <clipPath id="r"><rect width="${lw + mw}" height="20" rx="3" fill="#fff"/></clipPath>
  <g clip-path="url(#r)">
    <rect width="${lw}" height="20" fill="#555"/>
    <rect x="${lw}" width="${mw}" height="20" fill="${color}"/>
    <rect width="${lw + mw}" height="20" fill="url(#s)"/>
  </g>
  <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
    <text x="${lw / 2}" y="15" fill="#010101" fill-opacity=".3">${escape(label)}</text>
    <text x="${lw / 2}" y="14">${escape(label)}</text>
    <text x="${lw + mw / 2}" y="15" fill="#010101" fill-opacity=".3">${escape(message)}</text>
    <text x="${lw + mw / 2}" y="14">${escape(message)}</text>
  </g>
</svg>
`);
