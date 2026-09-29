// verify-web.mjs - load the built game in a real browser and prove it runs.
//
// This is the check the headless play-mode tests cannot make: that the WebGL player boots in a
// browser, the studio ident plays, the canvas actually renders, and nothing throws. It captures
// the ident mid-play and the game after load so the frames can be eyeballed.
//
//   node Tools/verify-web.mjs [--url http://127.0.0.1:8123/] [--out shots] [--seconds 25]

import puppeteer from 'puppeteer-core';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };

const URL_ = argOf('--url', 'http://127.0.0.1:8123/');
const OUT = path.resolve(here, '..', argOf('--out', 'shots'));
const SECONDS = Number(argOf('--seconds', '30'));
const GPU = args.includes('--gpu');

fs.mkdirSync(OUT, { recursive: true });

function findChrome() {
  const candidates = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  ];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  throw new Error('Chrome not found');
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await puppeteer.launch({
  executablePath: findChrome(),
  headless: 'new',
  protocolTimeout: 600000,
  args: [
    // SwiftShader (the default here) is software GL: correct enough to prove the game boots, but
    // it renders large flat surfaces with visible tile seams and collapses their shading, which
    // reads exactly like a lighting bug in the game. --gpu switches to the real D3D11 backend
    // when a frame needs to be judged on how it LOOKS rather than whether it drew.
    '--use-gl=angle',
    ...(GPU
      ? ['--use-angle=d3d11', '--enable-gpu-rasterization', '--headless=new']
      : ['--use-angle=swiftshader', '--enable-unsafe-swiftshader']),
    '--ignore-gpu-blocklist', '--enable-webgl', '--window-size=1280,800',
    '--autoplay-policy=no-user-gesture-required', '--mute-audio',
  ],
  defaultViewport: { width: 1280, height: 800 },
});

const page = await browser.newPage();
const errors = [], warnings = [], logs = [];
page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));
page.on('console', (m) => {
  const t = m.type(), s = m.text();
  if (t === 'error') errors.push(s);
  else if (t === 'warning') warnings.push(s);
  else logs.push(s);
});
page.on('requestfailed', (r) => errors.push(`request failed: ${r.url()} ${r.failure()?.errorText}`));

console.log('loading', URL_);
const t0 = Date.now();
await page.goto(URL_, { waitUntil: 'domcontentloaded', timeout: 120000 });

// The ident starts on the first frame; catch it while it is still on screen.
await sleep(1600);
await page.screenshot({ path: path.join(OUT, '01-intro.png') });
console.log('  captured ident at 1.6s');

// The template sets window.__gameReady once BOTH the ident and the player are done.
let ready = false;
try {
  await page.waitForFunction('window.__gameReady === true', { timeout: SECONDS * 1000, polling: 500 });
  ready = true;
} catch (e) {
  console.log('  !! __gameReady never became true');
}
const loadMs = Date.now() - t0;
console.log(`  ready=${ready} after ${(loadMs / 1000).toFixed(1)}s`);

await sleep(2500);
await page.screenshot({ path: path.join(OUT, '02-title.png') });
console.log('  captured title screen');

// Click START. It is a uGUI button drawn inside the canvas, so this is a real click at the
// button's screen position - there is no DOM node to query. At 1280x800 with the CanvasScaler
// set to a 1920x1080 reference and match 0.5, it lands just below centre.
await page.mouse.click(640, 421);
await sleep(1200);
await page.screenshot({ path: path.join(OUT, '03-after-start.png') });

// Play. Aliens drop in 30-55 m out and converge, so this sweeps the view around and holds the
// trigger, capturing frames across the whole first wave rather than one arbitrary instant.
await page.mouse.move(640, 400);
// A stationary bot is dead inside twenty seconds, and the later frames were all game-over
// screens - which quietly poisoned every measurement taken from them. So: capture early and
// often, and keep moving between shots.
const KEYS = ['KeyW', 'KeyA', 'KeyD', 'KeyS'];
for (let shot = 0; shot < 4; shot++) {
  const t = Date.now();
  const dwell = shot === 0 ? 2500 : 5000;
  while (Date.now() - t < dwell) {
    await page.mouse.move(640 + Math.sin(Date.now() / 1100) * 260, 400);
    await page.mouse.down();
    await sleep(110);
    await page.mouse.up();
    // Strafe and dash so the bot survives long enough to photograph a real fight.
    const k = KEYS[Math.floor(Date.now() / 900) % KEYS.length];
    await page.keyboard.down(k);
    await sleep(90);
    await page.keyboard.up(k);
    if (Math.random() < 0.15) await page.keyboard.press('KeyE');
  }
  await page.screenshot({ path: path.join(OUT, `04-gameplay-${shot + 1}.png`) });
  console.log(`  captured gameplay frame ${shot + 1}`);
}

// Frame rate: 850 objects and 31 lights on WebGL is not free, and a web game that runs at 20fps
// is not shipped. Measured over 3s of real gameplay, not a menu.
const fps = await page.evaluate(() => new Promise((resolve) => {
  let frames = 0;
  const t0 = performance.now();
  const tick = () => {
    frames++;
    if (performance.now() - t0 < 3000) requestAnimationFrame(tick);
    else resolve(Math.round((frames * 1000) / (performance.now() - t0)));
  };
  requestAnimationFrame(tick);
}));
console.log('fps:           ', fps, '(software GL unless --gpu)');

// Prove the canvas is not a black rectangle: sample the pixels.
const stats = await page.evaluate(() => {
  const c = document.querySelector('#unity-canvas');
  if (!c) return { error: 'no canvas' };
  const gl = c.getContext('webgl2') || c.getContext('webgl');
  const w = 64, h = 64;
  const px = new Uint8Array(w * h * 4);
  try {
    gl.readPixels(
      Math.floor(c.width / 2 - w / 2), Math.floor(c.height / 2 - h / 2),
      w, h, gl.RGBA, gl.UNSIGNED_BYTE, px);
  } catch (e) {
    return { error: 'readPixels failed: ' + e.message };
  }
  let sum = 0, distinct = new Set();
  for (let i = 0; i < px.length; i += 4) {
    sum += px[i] + px[i + 1] + px[i + 2];
    distinct.add(`${px[i]},${px[i + 1]},${px[i + 2]}`);
  }
  return {
    canvasSize: `${c.width}x${c.height}`,
    meanLuma: (sum / (px.length / 4) / 3).toFixed(1),
    distinctColours: distinct.size,
    contextLost: gl.isContextLost(),
  };
});

console.log('\n--- result ---');
console.log('ready:        ', ready);
console.log('load time:    ', (loadMs / 1000).toFixed(1) + 's');
console.log('canvas:       ', JSON.stringify(stats));
console.log('errors:       ', errors.length);
for (const e of errors.slice(0, 12)) console.log('   !', e.slice(0, 220));
console.log('warnings:     ', warnings.length);
for (const w of warnings.slice(0, 5)) console.log('   ~', w.slice(0, 160));
console.log('screenshots:  ', OUT);
const ufo = logs.filter((l) => l.includes('[UFO]'));
if (ufo.length) { console.log('\n--- game diagnostics ---'); for (const l of ufo) console.log('  ' + l); }

await browser.close();
process.exit(ready && errors.length === 0 ? 0 : 1);
