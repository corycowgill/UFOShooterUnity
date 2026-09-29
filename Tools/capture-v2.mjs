// capture-v2.mjs - screenshot the v2 web build, to use as the visual reference for v3.
//
// Styling v3 "from memory" of v2 is how a port drifts. This drives the real v2 build through its
// own `window.__ufo` debug hook and captures the same situations v3's verifier captures, so the
// two can be compared frame to frame.
//
//   node Tools/capture-v2.mjs [--root <v2 build dir>] [--out shots-v2] [--port 8124]

import puppeteer from 'puppeteer-core';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };

const ROOT = path.resolve(argOf('--root', 'C:/Users/coryc/ufoDemoVid1/build/v2snap_recorded'));
const OUT = path.resolve(here, '..', argOf('--out', 'shots-v2'));
const PORT = Number(argOf('--port', '8124'));

fs.mkdirSync(OUT, { recursive: true });

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.mjs': 'application/javascript',
  '.json': 'application/json', '.css': 'text/css', '.glb': 'model/gltf-binary',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.mp3': 'audio/mpeg', '.ico': 'image/x-icon',
};

const server = http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/') p = '/index.html';
  const file = path.join(ROOT, path.normalize(p).replace(/^(\.\.[/\\])+/, ''));
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404).end('404'); return; }
    res.writeHead(200, { 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream' });
    res.end(data);
  });
}).listen(PORT);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await puppeteer.launch({
  executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe',
  headless: 'new',
  protocolTimeout: 600000,
  args: [
    '--use-gl=angle', '--use-angle=d3d11', '--enable-gpu-rasterization', '--headless=new',
    '--ignore-gpu-blocklist', '--window-size=1280,800',
    '--autoplay-policy=no-user-gesture-required', '--mute-audio',
  ],
  defaultViewport: { width: 1280, height: 800 },
});

const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(String(e && e.message || e)));

// ?debug installs the hook and skips the intro.
await page.goto(`http://127.0.0.1:${PORT}/?debug`, { waitUntil: 'networkidle2', timeout: 180000 });
await page.waitForFunction('window.__ufo && window.__ufo.startRun', { timeout: 180000 });
console.log('v2 loaded');

await page.screenshot({ path: path.join(OUT, '01-title.png') });

await page.evaluate(() => window.__ufo.startRun(0));
// Models stream in asynchronously; give the first wave time to arrive and animate.
await sleep(6000);

// v2 splits step and render, so the harness advances the sim on a fixed clock and then draws.
for (let i = 0; i < 4; i++) {
  await page.evaluate(() => { window.__ufo.step(4); window.__ufo.render(); });
  await sleep(700);
  await page.evaluate(() => window.__ufo.render());
  await page.screenshot({ path: path.join(OUT, `02-gameplay-${i + 1}.png`) });
  const counts = await page.evaluate(() => window.__ufo.counts());
  console.log(`  frame ${i + 1}:`, JSON.stringify(counts).slice(0, 180));
}

console.log('errors:', errors.length);
for (const e of errors.slice(0, 6)) console.log('  !', e.slice(0, 200));
console.log('screenshots:', OUT);

await browser.close();
server.close();
