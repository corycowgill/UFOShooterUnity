// decode-glb.mjs — turn the v2 web GLBs (Draco geometry + WebP textures) into plain GLBs that
// Unity's glTFast importer reads without any extra packages.
//   node decode-glb.mjs <srcRoot> <dstRoot>
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import draco3d from 'draco3dgltf';
import sharp from 'sharp';
import fs from 'node:fs';
import path from 'node:path';

const [srcRoot, dstRoot] = process.argv.slice(2);

// Texture budget. glTFast's importer exposes NO texture size or compression setting - the images
// embedded in a .glb are imported at whatever resolution they carry. So the source resolution set
// here is the only lever on the WebGL download, and it is the difference between a 260 MB data
// file and a shippable one. Heroes (enemies, weapons) are seen from two metres and keep more
// pixels; buildings and street props are seen from twenty and do not.
const TEXTURE_BUDGET = [
  { match: /^(enemies|weapons)\//i, size: 512 },
  { match: /./, size: 256 },
];
// path.relative gives backslashes on Windows; normalise so one pattern works on either platform.
const budgetFor = (rel) => {
  const key = rel.split(path.sep).join('/');
  return (TEXTURE_BUDGET.find((b) => b.match.test(key)) || { size: 256 }).size;
};
if (!srcRoot || !dstRoot) { console.error('usage: decode-glb.mjs <srcRoot> <dstRoot>'); process.exit(1); }

const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({
    'draco3d.decoder': await draco3d.createDecoderModule(),
    'draco3d.encoder': await draco3d.createEncoderModule(),
  });

function walk(dir) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) out.push(...walk(p));
    else if (e.name.toLowerCase().endsWith('.glb')) out.push(p);
  }
  return out;
}

const files = walk(srcRoot);
console.log(`${files.length} GLB files`);
let ok = 0, fail = 0;

for (const src of files) {
  const rel = path.relative(srcRoot, src);
  const dst = path.join(dstRoot, rel);
  fs.mkdirSync(path.dirname(dst), { recursive: true });
  try {
    const doc = await io.read(src);

    // WebP -> PNG (Unity/glTFast has no WebP decoder), and downscale to the budget.
    const maxSize = budgetFor(rel);
    for (const tex of doc.getRoot().listTextures()) {
      const img = sharp(Buffer.from(tex.getImage()));
      const meta = await img.metadata();
      const needsResize = Math.max(meta.width || 0, meta.height || 0) > maxSize;
      if (tex.getMimeType() !== 'image/webp' && !needsResize) continue;

      let pipeline = img;
      if (needsResize) pipeline = pipeline.resize(maxSize, maxSize, { fit: 'inside' });
      const png = await pipeline.png({ compressionLevel: 9 }).toBuffer();

      tex.setImage(new Uint8Array(png));
      tex.setMimeType('image/png');
      const uri = tex.getURI();
      if (uri) tex.setURI(uri.replace(/\.webp$/i, '.png'));
    }

    // Dropping the extensions makes the writer emit uncompressed buffer views.
    for (const ext of doc.getRoot().listExtensionsUsed()) {
      if (ext.extensionName === 'KHR_draco_mesh_compression' || ext.extensionName === 'EXT_texture_webp') ext.dispose();
    }

    await io.write(dst, doc);
    const a = fs.statSync(src).size, b = fs.statSync(dst).size;
    console.log(`  ok  ${rel}  ${(a / 1024).toFixed(0)}KB -> ${(b / 1024).toFixed(0)}KB  (tex <= ${maxSize}px)`);
    ok++;
  } catch (e) {
    console.log(`  FAIL ${rel}: ${e.message}`);
    fail++;
  }
}
console.log(`done: ${ok} ok, ${fail} failed`);
