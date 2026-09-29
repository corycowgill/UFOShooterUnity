// mock-render.mjs - a static server that behaves exactly like the live Render static site.
//
// Measured from https://ufoshooterunity.onrender.com on 2026-09-29:
//   cache-control: public, max-age=0, s-maxage=300
//   etag + last-modified both sent
//   If-None-Match  -> 304
//   If-Modified-Since -> IGNORED, answers 200 with the whole body
//
// That last line is the defect under test, so it is reproduced faithfully rather than fixed.
//
//   node Tools/mock-render.mjs --dir Build/web --port 8131
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto';
const a = process.argv.slice(2); const of_ = (n,d)=>{const i=a.indexOf(n);return i>=0&&a[i+1]?a[i+1]:d;};
const DIR = path.resolve(of_('--dir','Build/web')), PORT = Number(of_('--port','8131'));
const TYPES = {'.html':'text/html; charset=utf-8','.js':'application/javascript','.json':'application/json',
               '.css':'text/css','.unityweb':'binary/octet-stream','.wasm':'application/wasm','.md':'text/markdown'};
const etags = new Map();
http.createServer((req,res)=>{
  let p = decodeURIComponent(req.url.split('?')[0]); if (p==='/') p='/index.html';
  const f = path.join(DIR, p);
  if (!f.startsWith(DIR) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) { res.writeHead(404); return res.end('404'); }
  const st = fs.statSync(f);
  let tag = etags.get(f);
  if (!tag || tag.m !== st.mtimeMs) { tag = {m:st.mtimeMs, v:'"'+crypto.createHash('md5').update(fs.readFileSync(f)).digest('hex')+'"'}; etags.set(f,tag); }
  const h = { 'Content-Type': TYPES[path.extname(f)]||'application/octet-stream',
              'cache-control':'public, max-age=0, s-maxage=300',
              'etag': tag.v, 'last-modified': new Date(st.mtimeMs).toUTCString().replace('GMT','UTC') };
  if (req.headers['if-none-match'] === tag.v) { res.writeHead(304,h); return res.end(); }
  // If-Modified-Since deliberately NOT honoured - this is what Render does.
  h['Content-Length'] = st.size;
  res.writeHead(200,h);
  if (req.method==='HEAD') return res.end();
  fs.createReadStream(f).pipe(res);
}).listen(PORT, ()=>console.log('mock-render on http://127.0.0.1:'+PORT+' serving '+DIR));
