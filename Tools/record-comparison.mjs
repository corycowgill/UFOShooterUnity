// record-comparison.mjs - record v2 and v3 playing the same fight, then cut them side by side.
//
// Both panes are captured the same way so the comparison is fair: real-time CDP screencast at
// 1280x720, the same scripted bot driving both, both starting at wave 1 of The Loop, both
// restarted the moment they die so the tape never sits on a death screen.
//
// Frame timing comes from the screencast metadata rather than being assumed, because a screencast
// does not deliver a fixed rate - assembling those frames at a constant fps is what makes a
// recording run fast or slow. Each pane gets an ffmpeg concat file with real per-frame durations.
//
//   node Tools/record-comparison.mjs [--seconds 225] [--out ../UFO_v2_vs_v3.mp4]

import puppeteer from 'puppeteer-core';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };

const SECONDS = Number(argOf('--seconds', '225'));
const OUT = path.resolve(here, '..', argOf('--out', 'UFO_v2_vs_v3.mp4'));
const WORK = path.resolve(here, '..', 'video-work');

const V2_ROOT = path.resolve(argOf('--v2', 'C:/Users/coryc/ufoDemoVid1/build/v2snap_recorded'));
const V3_ROOT = path.resolve(here, '..', 'Build/web');
const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- static server
const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.mjs': 'application/javascript',
  '.json': 'application/json', '.css': 'text/css', '.glb': 'model/gltf-binary', '.wasm': 'application/wasm',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.mp3': 'audio/mpeg', '.ico': 'image/x-icon',
  '.data': 'application/octet-stream', '.unityweb': 'application/octet-stream', '.symbols': 'application/octet-stream',
};

function serve(root, port) {
  return http.createServer((req, res) => {
    let p = decodeURIComponent(req.url.split('?')[0]);
    if (p === '/') p = '/index.html';
    const file = path.join(root, path.normalize(p).replace(/^(\.\.[/\\])+/, ''));
    fs.readFile(file, (err, data) => {
      if (err) { res.writeHead(404).end('404'); return; }
      let ext = path.extname(file).toLowerCase();
      const headers = { 'Cache-Control': 'no-cache' };
      if (ext === '.br') { headers['Content-Encoding'] = 'br'; ext = path.extname(file.slice(0, -3)).toLowerCase(); }
      headers['Content-Type'] = TYPES[ext] || 'application/octet-stream';
      res.writeHead(200, headers);
      res.end(data);
    });
  }).listen(port);
}

// ---------------------------------------------------------------- capture
async function capture({ label, url, dir, onReady, drive }) {
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });

  const browser = await puppeteer.launch({
    executablePath: CHROME,
    headless: 'new',
    protocolTimeout: 900000,
    args: [
      '--use-gl=angle', '--use-angle=d3d11', '--enable-gpu-rasterization', '--headless=new',
      '--ignore-gpu-blocklist', '--window-size=1280,720', '--hide-scrollbars',
      '--autoplay-policy=no-user-gesture-required', '--mute-audio',
    ],
    defaultViewport: { width: 1280, height: 720 },
  });

  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String((e && e.message) || e)));

  console.log(`[${label}] loading ${url}`);
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 240000 });
  await onReady(page);
  console.log(`[${label}] ready, recording ${SECONDS}s`);

  const client = await page.createCDPSession();
  const frames = [];
  let n = 0;

  client.on('Page.screencastFrame', async ({ data, sessionId, metadata }) => {
    const file = path.join(dir, String(n++).padStart(6, '0') + '.jpg');
    fs.writeFileSync(file, Buffer.from(data, 'base64'));
    frames.push({ file, t: metadata.timestamp });
    try { await client.send('Page.screencastFrameAck', { sessionId }); } catch { /* shutting down */ }
  });

  await client.send('Page.startScreencast', { format: 'jpeg', quality: 80, maxWidth: 1280, maxHeight: 720, everyNthFrame: 1 });

  // Both games read mouse deltas from pointer lock. If a pane is not locked, its bot stares at
  // the spawn point for the whole take and the recording measures the harness, not the game.
  const locked = await page.evaluate(() => document.pointerLockElement !== null);
  console.log(`[${label}] pointer lock: ${locked ? 'ENGAGED' : 'NOT ENGAGED - bot cannot aim'}`);

  const t0 = Date.now();
  await drive(page, () => Date.now() - t0 < SECONDS * 1000);

  await client.send('Page.stopScreencast');
  await sleep(400);
  await browser.close();

  console.log(`[${label}] ${frames.length} frames, ${errors.length} errors`);
  for (const e of errors.slice(0, 3)) console.log(`   ! ${e.slice(0, 160)}`);
  return frames;
}

/// Build an ffmpeg concat list with the real duration of each frame.
function writeConcat(frames, listPath) {
  if (frames.length < 2) throw new Error('no frames captured');
  const lines = [];
  for (let i = 0; i < frames.length; i++) {
    const next = i + 1 < frames.length ? frames[i + 1].t : frames[i].t + 0.033;
    const dur = Math.min(Math.max(next - frames[i].t, 0.008), 0.5);
    lines.push(`file '${frames[i].file.replace(/\\/g, '/')}'`);
    lines.push(`duration ${dur.toFixed(4)}`);
  }
  lines.push(`file '${frames[frames.length - 1].file.replace(/\\/g, '/')}'`);
  fs.writeFileSync(listPath, lines.join('\n'));
}

const PROJECT = path.resolve(here, '..');
const run = (cmd, argv) => new Promise((resolve, reject) => {
  const p = spawn(cmd, argv, { stdio: ['ignore', 'inherit', 'inherit'], cwd: PROJECT });
  p.on('close', (code) => (code === 0 ? resolve() : reject(new Error(`${cmd} exited ${code}`))));
});

// ---------------------------------------------------------------- bots
// The same routine drives both games: sweep the aim while firing, strafe, dash occasionally.
// `restart` is game-specific because only v3 needs a click on its own canvas.
async function botLoop(page, stillRunning, restart) {
  const keys = ['KeyW', 'KeyA', 'KeyD', 'KeyS'];
  let tick = 0;
  while (stillRunning()) {
    const t = Date.now() / 1000;
    // Aim BELOW the horizon, not at it. The player's eye is at 1.7 m and a Gnat is 1.25 m tall,
    // so a level shot sails clean over the head of the most common enemy in the game - the bot
    // fires all take and kills nothing, and the tape then reads as though the GAME is slow.
    // UFO.Tests.SmokeTests.Weapons_All_Fire_And_Damage documents the same trap. At 75 deg FOV
    // over 720 px a 1 m drop is ~46 px at 12 m and ~28 px at 20 m, so a band centred 38 px low
    // sweeps through where short enemies actually stand. Both panes get it, so the comparison
    // still measures the games rather than the harness.
    await page.mouse.move(640 + Math.sin(t * 0.9) * 300, 398 + Math.sin(t * 0.37) * 26);
    await page.mouse.down();
    await sleep(90);
    await page.mouse.up();

    const k = keys[Math.floor(t) % keys.length];
    await page.keyboard.down(k);
    await sleep(80);
    await page.keyboard.up(k);

    if (tick % 14 === 0) await page.keyboard.press('KeyE');
    if (tick % 40 === 0) await restart(page);
    tick++;
  }
}

// ---------------------------------------------------------------- main
fs.mkdirSync(WORK, { recursive: true });
const s2 = serve(V2_ROOT, 8231);
const s3 = serve(V3_ROOT, 8232);

const v2Frames = await capture({
  label: 'v2',
  url: 'http://127.0.0.1:8231/?debug',
  dir: path.join(WORK, 'v2'),
  onReady: async (page) => {
    await page.waitForFunction('window.__ufo && window.__ufo.startRun', { timeout: 240000 });
    await page.evaluate(() => window.__ufo.startRun(0));
    await sleep(5000);            // models stream in
  },
  // v2 exposes its own state, so a death can be detected properly rather than guessed at.
  drive: (page, still) => botLoop(page, still, async (p) => {
    const state = await p.evaluate(() => (window.__ufo ? window.__ufo.state() : 'x')).catch(() => 'x');
    if (state !== 'playing') await p.evaluate(() => window.__ufo.startRun(0)).catch(() => {});
  }),
});

const v3Frames = await capture({
  label: 'v3',
  url: 'http://127.0.0.1:8232/?nointro',
  dir: path.join(WORK, 'v3'),
  onReady: async (page) => {
    await page.waitForFunction('window.__gameReady === true', { timeout: 240000 });
    await sleep(2500);
    await page.mouse.click(640, 379);   // START, at 1280x720
    await sleep(1500);
  },
  // v3 has no JS bridge, so the restart is a click where TRY AGAIN sits. During play the same
  // click is just another trigger pull, which costs nothing.
  drive: (page, still) => botLoop(page, still, async (p) => { await p.mouse.click(640, 387); }),
});

s2.close(); s3.close();

const listV2 = path.join(WORK, 'v2.txt');
const listV3 = path.join(WORK, 'v3.txt');
writeConcat(v2Frames, listV2);
writeConcat(v3Frames, listV3);

console.log('\ncompositing...');
const FONT = 'Assets/Resources/Fonts/Orbitron-Bold.ttf';
const label = (text, x) =>
  `drawtext=fontfile='${FONT}':text='${text}':fontcolor=0x5bd8ff:fontsize=30:x=${x}:y=32:` +
  `box=1:boxcolor=0x000000@0.55:boxborderw=14`;

await run('ffmpeg', [
  '-v', 'error', '-y',
  '-f', 'concat', '-safe', '0', '-i', listV2,
  '-f', 'concat', '-safe', '0', '-i', listV3,
  '-filter_complex',
  `[0:v]scale=960:540,setsar=1,setpts=PTS-STARTPTS[l];` +
  `[1:v]scale=960:540,setsar=1,setpts=PTS-STARTPTS[r];` +
  `color=c=0x05070c:s=1920x640:r=30:d=${SECONDS}[bg];` +
  `[bg][l]overlay=0:100:shortest=1[x1];[x1][r]overlay=960:100[x2];` +
  `[x2]${label('v2  .  THREE.JS', 250)},${label('v3  .  UNITY 6 / WEBGL', 1180)},` +
  `format=yuv420p[v]`,
  '-map', '[v]', '-an',
  '-t', String(SECONDS),
  '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-movflags', '+faststart',
  OUT,
]);

const mb = (fs.statSync(OUT).size / 1048576).toFixed(1);
console.log(`\nwrote ${OUT}  (${mb} MB, ${SECONDS}s)`);
