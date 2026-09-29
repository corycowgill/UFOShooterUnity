// measure-bandwidth.mjs - what a real browser actually transfers to play this game.
//
// Bytes on the wire are the only number that matters for a hosting bill, and neither the build
// report nor the file listing gives them: the answer depends on compression, on cache headers,
// and on whether Unity's IndexedDB data cache is able to revalidate. So load the real site in a
// real Chrome and read Network.loadingFinished.encodedDataLength.
//
// The profile is PERSISTENT and reused between passes. That is the whole point - a cold pass on
// a throwaway profile measures nothing that a second visitor experiences.
//
//   node Tools/measure-bandwidth.mjs --url https://host/ --pass cold|warm
//
// --pass cold wipes the profile first; --pass warm keeps whatever the previous run left behind.

import puppeteer from 'puppeteer-core';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };

const URL_ = argOf('--url', 'https://ufoshooterunity.onrender.com/');
const PASS = argOf('--pass', 'cold');
const SECONDS = Number(argOf('--seconds', '75'));
const PROFILE = argOf('--profile', path.join(here, '..', '.bwprofile'));

if (PASS === 'cold') fs.rmSync(PROFILE, { recursive: true, force: true });
fs.mkdirSync(PROFILE, { recursive: true });

function findChrome() {
  for (const c of ['C:/Program Files/Google/Chrome/Application/chrome.exe',
                   'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe'])
    if (fs.existsSync(c)) return c;
  throw new Error('Chrome not found');
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await puppeteer.launch({
  executablePath: findChrome(),
  headless: 'new',
  userDataDir: PROFILE,
  protocolTimeout: 600000,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
         '--ignore-gpu-blocklist', '--enable-webgl', '--window-size=1280,800',
         '--autoplay-policy=no-user-gesture-required', '--mute-audio'],
  defaultViewport: { width: 1280, height: 800 },
});

const page = await browser.newPage();
const cdp = await page.createCDPSession();
await cdp.send('Network.enable');

// Deliberately NOT Network.setCacheDisabled - the cache is the thing under test.
const reqs = new Map();
const rows = [];
cdp.on('Network.requestWillBeSent', (e) => reqs.set(e.requestId, { url: e.request.url }));
cdp.on('Network.responseReceived', (e) => {
  const r = reqs.get(e.requestId); if (!r) return;
  r.status = e.response.status;
  r.fromDisk = e.response.fromDiskCache;
  r.type = e.response.headers['content-type'] || e.response.headers['Content-Type'] || '';
  r.enc = e.response.headers['content-encoding'] || e.response.headers['Content-Encoding'] || '-';
});
cdp.on('Network.requestServedFromCache', (e) => { const r = reqs.get(e.requestId); if (r) r.memCache = true; });
cdp.on('Network.loadingFinished', (e) => {
  const r = reqs.get(e.requestId); if (!r) return;
  r.bytes = e.encodedDataLength; rows.push(r);
});

const t0 = Date.now();
const logs = [];
page.on('console', (m) => logs.push(m.text()));
await page.goto(URL_, { waitUntil: 'domcontentloaded', timeout: 180000 });

// Wait until Unity reports the player is up, or the clock runs out.
let readyAt = null;
for (let i = 0; i < SECONDS * 2; i++) {
  const ok = await page.evaluate(() => !!(window.__gameReady || window.unityInstance)).catch(() => false);
  if (ok) { readyAt = Date.now(); break; }
  await sleep(500);
}
await sleep(3000);

const total = rows.reduce((s, r) => s + (r.bytes || 0), 0);
rows.sort((a, b) => (b.bytes || 0) - (a.bytes || 0));

const KB = (n) => (n / 1024).toFixed(1).padStart(10);
console.log(`\n===== ${PASS.toUpperCase()} PASS =====  ${URL_}`);
console.log(`ready: ${readyAt ? ((readyAt - t0) / 1000).toFixed(1) + ' s' : 'NOT READY within ' + SECONDS + ' s'}`);
console.log(`${'KB on wire'}  ${'st'.padStart(4)} ${'cache'.padStart(6)} ${'enc'.padStart(5)}  url`);
for (const r of rows.slice(0, 14))
  console.log(`${KB(r.bytes || 0)}  ${String(r.status || '').padStart(4)} ${(r.memCache ? 'mem' : r.fromDisk ? 'disk' : 'net').padStart(6)} ${String(r.enc).padStart(5)}  ${r.url.replace(URL_, '')}`);
console.log(`\nTOTAL TRANSFERRED: ${(total / 1048576).toFixed(2)} MB  across ${rows.length} requests`);

const unityLogs = logs.filter((l) => /cache|revalidat/i.test(l));
if (unityLogs.length) { console.log('\nUnity cache log:'); for (const l of unityLogs.slice(0, 10)) console.log('  ' + l); }

await browser.close();
