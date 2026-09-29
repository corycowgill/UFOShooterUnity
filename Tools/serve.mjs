// serve.mjs - static server for the WebGL build that sets the headers Unity needs.
//
// Brotli-compressed Unity builds serve files as `*.br`, and the browser will only accept them if
// the server declares `Content-Encoding: br` alongside the ORIGINAL content type. Get that wrong
// and you get a blank canvas with a console error - the single most common Unity WebGL support
// question. `npx serve` does not do this.
//
//   node Tools/serve.mjs [--dir Build/web] [--port 8080]

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};

const root = path.resolve(here, '..', argOf('--dir', 'Build/web'));
const port = Number(argOf('--port', '8080'));

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript',
  '.wasm': 'application/wasm',
  '.json': 'application/json',
  '.css': 'text/css',
  '.data': 'application/octet-stream',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.symbols': 'application/octet-stream',
};

// `.br` and `.gz` are transfer encodings, not content types: strip the suffix to find the real
// type, and declare the encoding separately.
const ENCODINGS = { '.br': 'br', '.gz': 'gzip' };

http.createServer((req, res) => {
  let urlPath = decodeURIComponent(req.url.split('?')[0]);
  if (urlPath === '/') urlPath = '/index.html';

  const filePath = path.join(root, path.normalize(urlPath).replace(/^(\.\.[/\\])+/, ''));
  if (!filePath.startsWith(root)) { res.writeHead(403).end('forbidden'); return; }

  fs.stat(filePath, (err, stat) => {
    if (err || !stat.isFile()) {
      res.writeHead(404, { 'Content-Type': 'text/plain' }).end('404 ' + urlPath);
      return;
    }

    let ext = path.extname(filePath).toLowerCase();
    const headers = { 'Content-Length': stat.size, 'Cache-Control': 'no-cache' };

    if (ENCODINGS[ext]) {
      headers['Content-Encoding'] = ENCODINGS[ext];
      ext = path.extname(filePath.slice(0, -ext.length)).toLowerCase();
    }
    headers['Content-Type'] = TYPES[ext] || 'application/octet-stream';

    // Harmless when unused, and required the moment the build enables threads.
    headers['Cross-Origin-Opener-Policy'] = 'same-origin';
    headers['Cross-Origin-Embedder-Policy'] = 'require-corp';

    res.writeHead(200, headers);
    fs.createReadStream(filePath).pipe(res);
  });
}).listen(port, () => {
  console.log(`serving ${root}`);
  console.log(`  http://127.0.0.1:${port}/`);
});
