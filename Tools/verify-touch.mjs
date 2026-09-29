// verify-touch.mjs - drive the build with real touch events and prove the controls do something.
//
// Tapping a widget and seeing it light up proves nothing; what matters is whether the stick moved
// the player and whether the drag turned the view. So this reads the ?diag=1 telemetry either side
// of each gesture and asserts on the numbers.
//
// Touches are dispatched through CDP Input.dispatchTouchEvent rather than puppeteer's touchscreen
// helper, because the helper is single-touch and the thing most likely to be broken here is
// exactly the case where a thumb is already on the stick when the other hand fires.
//
//   node Tools/verify-touch.mjs --url http://127.0.0.1:8150/ --out shots-touch

import puppeteer from 'puppeteer-core';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };

const BASE = argOf('--url', 'http://127.0.0.1:8150/');
const OUT = path.resolve(here, '..', argOf('--out', 'shots-touch'));
const PORTRAIT = args.includes('--portrait');

// iPhone 15 Pro, landscape - the orientation the game asks for.
const W = PORTRAIT ? 393 : 852, H = PORTRAIT ? 852 : 393;
const UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 '
         + '(KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1';

fs.mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function findChrome() {
  for (const c of ['C:/Program Files/Google/Chrome/Application/chrome.exe',
                   'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe'])
    if (fs.existsSync(c)) return c;
  throw new Error('Chrome not found');
}

const browser = await puppeteer.launch({
  executablePath: findChrome(),
  headless: 'new',
  protocolTimeout: 600000,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
         '--ignore-gpu-blocklist', '--enable-webgl', `--window-size=${W},${H}`,
         '--autoplay-policy=no-user-gesture-required', '--mute-audio'],
});

const page = await browser.newPage();
const errors = [], diag = [];
page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
page.on('console', (m) => {
  const s = m.text();
  if (m.type() === 'error') errors.push(s);
  if (s.startsWith('[DIAG]')) diag.push(s);
});

const cdp = await page.createCDPSession();
await page.setUserAgent(UA);
await page.setViewport({ width: W, height: H, deviceScaleFactor: 3, isMobile: true, hasTouch: true });
// Makes '(pointer: coarse)' and '(any-pointer: fine)' answer the way a phone does, which is what
// the build's own device detection keys off.
await cdp.send('Emulation.setEmitTouchEventsForMouse', { enabled: false });
await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
await cdp.send('Emulation.setDeviceMetricsOverride', {
  width: W, height: H, deviceScaleFactor: 3, mobile: true,
  screenOrientation: { angle: PORTRAIT ? 0 : 90, type: PORTRAIT ? 'portraitPrimary' : 'landscapePrimary' },
});

// Multi-touch primitives. Each point is {id, x, y}; the set passed is the full current state.
const touch = (type, points) => cdp.send('Input.dispatchTouchEvent', {
  type,
  touchPoints: points.map((p) => ({ x: p.x, y: p.y, id: p.id, radiusX: 12, radiusY: 12, force: 1 })),
});

async function tap(x, y, hold = 90) {
  await touch('touchStart', [{ id: 1, x, y }]);
  await sleep(hold);
  await touch('touchEnd', []);
  await sleep(140);
}

async function drag(id, from, to, steps, others = []) {
  await touch('touchStart', [...others, { id, ...from }]);
  for (let i = 1; i <= steps; i++) {
    const t = i / steps;
    await touch('touchMove', [...others, { id, x: from.x + (to.x - from.x) * t, y: from.y + (to.y - from.y) * t }]);
    await sleep(16);
  }
  return async () => { await touch('touchEnd', others); await sleep(60); };
}

const shot = async (name) => page.screenshot({ path: path.join(OUT, name) });
const lastDiag = () => (diag.length ? diag[diag.length - 1] : '');
const num = (line, key) => {
  const m = new RegExp(key + '=\\(?(-?[0-9.]+)').exec(line);
  return m ? parseFloat(m[1]) : NaN;
};
const pos = (line) => {
  const m = /pos=\((-?[0-9.]+),(-?[0-9.]+)\)/.exec(line);
  return m ? { x: parseFloat(m[1]), z: parseFloat(m[2]) } : null;
};

const url = BASE + (BASE.includes('?') ? '&' : '?') + 'touch=1&diag=1&nointro=1';
console.log(`loading ${url}  (${W}x${H}${PORTRAIT ? ' portrait' : ' landscape'})`);
await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 240000 });

let ready = false;
for (let i = 0; i < 240; i++) {
  ready = await page.evaluate(() => !!window.__gameReady).catch(() => false);
  if (ready) break;
  await sleep(500);
}
console.log('ready:', ready);
await sleep(2500);
await shot('01-menu.png');

const results = [];
const check = (name, ok, detail) => { results.push({ name, ok, detail }); console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}  ${detail}`); };

if (PORTRAIT) {
  const shown = await page.evaluate(() => {
    const r = document.getElementById('rotate');
    return !!r && getComputedStyle(r).display !== 'none';
  });
  check('portrait shows rotate prompt', shown, shown ? 'visible' : 'hidden');
  await shot('02-portrait.png');
  await browser.close();
  process.exit(results.every((r) => r.ok) ? 0 : 1);
}

// START sits at the centre of the menu.
await tap(W / 2, H / 2 + 6);
await sleep(2500);
await shot('02-playing.png');

const playing = /state=Playing/.test(lastDiag());
check('START begins play on tap', playing, lastDiag().slice(0, 64));

const overlayUp = await page.evaluate(() => !!window.__gameReady);
check('touch scheme detected', /touch=True/.test(lastDiag()), lastDiag().match(/touch=\w+/)?.[0] || '(no diag)');

// --- look: drag across the right half ---------------------------------------
const yaw0 = num(lastDiag(), 'yaw');
let release = await drag(1, { x: W * 0.72, y: H * 0.45 }, { x: W * 0.36, y: H * 0.45 }, 14);
await release();
await sleep(1400);
const yaw1 = num(lastDiag(), 'yaw');
const turned = Math.abs(((yaw1 - yaw0 + 540) % 360) - 180);
check('drag turns the view', turned > 12, `yaw ${yaw0.toFixed(1)} -> ${yaw1.toFixed(1)} (${turned.toFixed(1)} deg)`);
await shot('03-after-look.png');

// --- move: hold the stick ---------------------------------------------------
const p0 = pos(lastDiag());
const stickX = W * 0.155, stickY = H * 0.68;
await touch('touchStart', [{ id: 2, x: stickX, y: stickY }]);
for (let i = 0; i < 6; i++) { await touch('touchMove', [{ id: 2, x: stickX, y: stickY - 55 }]); await sleep(60); }
await sleep(1600);
const dMove = lastDiag();
await touch('touchEnd', []);
await sleep(900);
const p1 = pos(lastDiag());
const moved = p0 && p1 ? Math.hypot(p1.x - p0.x, p1.z - p0.z) : 0;
check('stick moves the player', moved > 1.0, `moved ${moved.toFixed(2)} m, stick=${dMove.match(/move=\([^)]*\)/)?.[0]}`);
await shot('04-after-move.png');

// --- multi-touch: steer and fire at once ------------------------------------
const before = diag.length;
await touch('touchStart', [{ id: 2, x: stickX, y: stickY }, { id: 3, x: W * 0.86, y: H * 0.74 }]);
for (let i = 0; i < 8; i++) {
  await touch('touchMove', [{ id: 2, x: stickX + 50, y: stickY - 40 }, { id: 3, x: W * 0.86, y: H * 0.74 }]);
  await sleep(90);
}
await sleep(900);
const both = diag.slice(before).find((l) => /fire=True/.test(l) && /move=\((?!0\.00,0\.00)/.test(l));
await touch('touchEnd', []);
await sleep(600);
check('fire while steering (multi-touch)', !!both, both ? both.match(/move=\([^)]*\) fire=\w+/)[0] : 'never saw both at once');
await shot('05-fire-and-move.png');

// --- pause ------------------------------------------------------------------
// Derive the button centre the way the CanvasScaler does rather than guessing a corner: uGUI
// scales by the geometric mean of the width and height ratios when matchWidthOrHeight is 0.5.
const k = Math.sqrt((W / 1920) * (H / 1080));
const pauseX = W - 360 * k, pauseY = 92 * k;
console.log(`  (pause target ${pauseX.toFixed(0)},${pauseY.toFixed(0)} radius ${(64 * k).toFixed(0)}px)`);
await tap(pauseX, pauseY);
await sleep(900);
check('pause button reaches the game', /state=Paused/.test(lastDiag()), lastDiag().match(/state=\w+/)?.[0] || '');
await shot('06-pause.png');

console.log(`\nerrors: ${errors.length}`);
for (const e of errors.slice(0, 6)) console.log('  ' + e);
const failed = results.filter((r) => !r.ok);
console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
await browser.close();
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);
