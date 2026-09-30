// Tile a directory of screenshots into one small JPEG contact sheet, with a caption per tile.
//   node Tools/contact-sheet.mjs <dir> <out.jpg> [--cols 3] [--width 520] [--match substr]
import sharp from 'sharp';
import fs from 'fs';
import path from 'path';

const a = process.argv.slice(2);
const dir = a[0], out = a[1] || 'sheet.jpg';
const arg = (k, d) => { const i = a.indexOf(k); return i < 0 ? d : a[i + 1]; };
const cols = +arg('--cols', 3), cw = +arg('--width', 520), match = arg('--match', '');

const files = fs.readdirSync(dir).filter(f => f.endsWith('.png') && f.includes(match)).sort();
if (!files.length) { console.error('no PNGs in ' + dir); process.exit(1); }

const tiles = await Promise.all(files.map(async f => {
  const img = sharp(path.join(dir, f)).resize(cw, null, { withoutEnlargement: false });
  const { data, info } = await img.jpeg().toBuffer({ resolveWithObject: true });
  return { f, buf: await sharp(data).toBuffer(), w: info.width, h: info.height };
}));

const rowH = Math.max(...tiles.map(t => t.h)) + 22;
const rows = Math.ceil(tiles.length / cols);
const W = cols * cw, H = rows * rowH;

const labels = tiles.map((t, i) => {
  const x = (i % cols) * cw + 6, y = Math.floor(i / cols) * rowH + t.h + 16;
  return `<text x="${x}" y="${y}" font-family="monospace" font-size="14" fill="#9fe">${t.f.replace('.png', '')}</text>`;
}).join('');

await sharp({ create: { width: W, height: H, channels: 3, background: '#101014' } })
  .composite([
    ...tiles.map((t, i) => ({ input: t.buf, left: (i % cols) * cw, top: Math.floor(i / cols) * rowH })),
    { input: Buffer.from(`<svg width="${W}" height="${H}">${labels}</svg>`), left: 0, top: 0 },
  ])
  .jpeg({ quality: 70 })
  .toFile(out);

console.log(`${out}  ${W}x${H}  ${tiles.length} tiles`);
