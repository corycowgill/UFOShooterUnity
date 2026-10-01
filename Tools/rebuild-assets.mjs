/**
 * Rebuild every shipping model from its best available source with a sane budget.
 *
 * Why this exists
 * ---------------
 * decode-glb.mjs capped every texture at 256px (512px for heroes) because glTFast
 * imports GLB-*embedded* images as uncompressed RGBA32 - four bytes per texel, with
 * mipmaps on top - and that was the only lever on the WebGL download. The reasoning was
 * sound but the premise was incomplete: EditorDownloadProvider.SyncTextureLoader resolves
 * *external* image URIs through AssetDatabase.LoadAssetAtPath<Texture2D>, so a .gltf
 * sitting beside loose .png files yields ordinary Unity texture assets, and TextureImporter
 * compresses those to DXT1 at half a byte per texel. Eight times cheaper per texel means
 * the same budget buys sixteen times the texels.
 *
 * The old budget was also backwards: the four weapon viewmodels kept ~94k triangles each -
 * 23 MB of the build - and spent 512px on the texture, for objects that fill the screen.
 * A viewmodel wants texels, not triangles.
 *
 * Source selection, in order of preference:
 *   1. art/rigged_<n>.glb      gnat/skirmisher/warlord/juggernaut. Already 16-20k tris with
 *                              the seven clips and the 18-joint skin, at 2048px. Geometry is
 *                              NEVER touched for these, so this pass cannot damage a rig.
 *   2. art/raw_glb/props/<n>_lo.glb   36 props and vehicles that decimate-props.ps1 already
 *                              collapsed in Blender, still carrying their 2048px textures.
 *                              The shipped tri counts match these exactly, which is the proof
 *                              that the only thing lost downstream was texture resolution.
 *   3. the full-resolution raw (art/raw_glb/** or art/poc_src/**) plus a decimation pass, for
 *                              the 21 assets that never got an _lo: 4 weapons, wasp, overseer,
 *                              l_track and the 14 buildings.
 *
 * Decimation prefers meshopt and falls back to Blender collapse. That fallback is not
 * belt-and-braces: tools/blender/decimate.py already records why it exists - meshopt gives up
 * on Trellis blobs, which are many disconnected shells it will not collapse across. trash_can
 * stalls at 37k under meshopt at any error cap; Blender takes it to 5k.
 *
 *   node Tools/rebuild-assets.mjs [--out <dir>] [--only a,b] [--dry]
 */
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { prune, dedup, resample, weld, simplify } from '@gltf-transform/functions';
import { MeshoptSimplifier } from 'meshoptimizer';
import draco3d from 'draco3dgltf';
import sharp from 'sharp';

/**
 * Pull an emissive mask out of a base colour. The Chicago retrofit art direction paints
 * neon straight into the albedo, which in-engine is just bright paint - it casts no light
 * and never reaches the bloom in the URP post stack.
 *
 * Selecting those texels took three goes. Brightness plus saturation alone marked 30.9% of
 * a plain period building as emissive, because brick and tungsten window light are both.
 * Rejecting the warm band (15-70 degrees) fixed that building but then lit up a fire
 * hydrant at 16.2%, because red sits at hue 0 and slips underneath. So this allow-lists the
 * hues the art direction actually uses, and everything else stays dark however bright it is.
 *
 * The bands are per-class, because the civic buildings deliberately do not share the street
 * palette: a hospital lights itself clinical white and pale blue with a red cross, a firehouse
 * amber and red, a police station blue and white. A single cyan/magenta list would leave all
 * three of them dark. Anything not listed falls back to `street`.
 *
 * Returns null when nothing qualifies, so non-neon assets get no emissive map at all.
 */
const BANDS = {
  street:   [[160, 205], [230, 270], [280, 335]],              // neon cyan, violet, magenta
  tower:    [[160, 205], [200, 250]],                          // cool white-blue office light
  hospital: [[150, 210], [200, 240], [350, 360], [0, 8]],      // pale blue-green, blue, red cross
  police:   [[185, 250], [350, 360], [0, 8]],                  // police blue, red beacons
  fire:     [[0, 45], [350, 360]],                             // amber and red only
};
const CLASS_OF = (name) =>
  name.includes('hospital') ? 'hospital'
  : name.includes('police_station') ? 'police'
  : name.includes('fire_station') ? 'fire'
  : (name.includes('skyscraper') || name.includes('office_mid')) ? 'tower'
  : 'street';
async function emissiveFrom(buf, res, cls) {
  const bands = BANDS[cls] || BANDS.street;
  const img = sharp(buf).resize(res, res, { fit: 'inside', withoutEnlargement: true });
  const { data, info } = await img.ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  let lit = 0;
  for (let i = 0; i < data.length; i += 4) {
    const r = data[i] / 255, g = data[i + 1] / 255, b = data[i + 2] / 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    const sat = max === 0 ? 0 : d / max;
    let hue = -1;
    if (d > 0) {
      hue = (max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4) * 60;
      if (hue < 0) hue += 360;
    }
    const neon = hue >= 0 && bands.some(([lo, hi]) => hue >= lo && hue <= hi);
    if (sat >= 0.5 && max >= 0.45 && neon) {
      const boost = Math.min(1, 0.45 + sat * 0.8);
      data[i] = Math.min(255, Math.round(r * 255 * boost + 255 * (boost - 0.45)));
      data[i + 1] = Math.min(255, Math.round(g * 255 * boost + 255 * (boost - 0.45) * 0.6));
      data[i + 2] = Math.min(255, Math.round(b * 255 * boost + 255 * (boost - 0.45)));
      lit++;
    } else {
      data[i] = data[i + 1] = data[i + 2] = 0;
    }
  }
  const frac = lit / (info.width * info.height);
  if (frac < 0.002) return null;   // nothing meaningful lit; skip the map entirely
  const png = await sharp(data, { raw: { width: info.width, height: info.height, channels: 4 } })
    .removeAlpha().blur(0.6).png({ compressionLevel: 9 }).toBuffer();
  return { png, frac };
}

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PROJECT = path.resolve(HERE, '..');
const ALIEN = 'C:/Users/coryc/alienGame';
const ART = ALIEN + '/art';
const BLENDER = process.env.LOCALAPPDATA + '/Microsoft/WindowsApps/blender-launcher.exe';
const CACHE = path.join(PROJECT, 'Tools', '.decimate-cache');

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };
const OUT = path.resolve(opt('out', path.join(PROJECT, 'Assets/Resources/Models')));
const ONLY = (opt('only', '') || '').split(',').filter(Boolean);
const DRY = args.includes('--dry');

// --- budget -----------------------------------------------------------------
// `res` is the baseColor size; metallicRoughness is a low-frequency mask and goes at half.
// `tris` of 0 means "take the source geometry as it is".
const SMALL = new Set(['barrier_jersey', 'bench', 'bus_shelter', 'cones', 'container', 'dumpster',
  'hydrant', 'mailbox', 'news_box', 'pallets', 'planter', 'sandbags', 'traffic_light', 'trash_can', 'tree']);
const RIGGED = ['gnat', 'skirmisher', 'warlord', 'juggernaut'];
const WEAPONS = ['rifle', 'plasma_rifle', 'rocket_launcher', 'energy_sword'];
const TRELLIS_PROPS = ['barrier_jersey', 'bench', 'bus_shelter', 'car_wreck', 'cones', 'container',
  'cta_bus', 'drop_pod', 'dumpster', 'ferris_wheel', 'hotdog_cart', 'hydrant', 'l_track', 'mailbox',
  'mothership', 'news_box', 'pallets', 'pier_kiosk', 'planter', 'police_car', 'rubble', 'sandbags',
  'taxi', 'traffic_light', 'trash_can', 'tree', 'yacht'];

const exists = (p) => p && fs.existsSync(p);

/** Pick the least-damaged source on disk, and say whether it still needs decimating. */
function resolveSource(name, cat) {
  if (RIGGED.includes(name)) return { src: ART + '/rigged_' + name + '.glb', tris: 0 };
  // The Chicago replacements win over everything: the 24 pack-sourced buildings and vehicles
  // they stand in for were never art-directed for this game, and the 4 civic buildings are new.
  // Hunyuan3D-2.1 emits ~40k tris, so they still want decimating to the building budget.
  const chi = ART + '/raw_glb/chicago_v2/' + name + '.glb';
  if (exists(chi)) return { src: chi, tris: 14000 };
  const lo = ART + '/raw_glb/props/' + name + '_lo.glb';
  if (exists(lo)) return { src: lo, tris: 0 };
  if (cat === 'weapons') return { src: ART + '/raw_glb/weapon_' + name + '.glb', tris: 24000 };
  if (cat === 'enemies') return { src: ART + '/raw_glb/' + name + '.glb', tris: 24000 };
  const poc = ART + '/poc_src/' + name + '.glb';
  if (exists(poc)) return { src: poc, tris: 14000 };
  return { src: ART + '/raw_glb/props/' + name + '.glb', tris: 20000 };
}

const jobs = [];
for (const n of [...RIGGED, 'wasp', 'overseer'])
  jobs.push({ name: n, cat: 'enemies', res: 1024, ...resolveSource(n, 'enemies') });
for (const n of WEAPONS)
  jobs.push({ name: n, cat: 'weapons', res: 2048, ...resolveSource(n, 'weapons') });
for (const n of TRELLIS_PROPS)
  jobs.push({ name: n, cat: 'props', res: SMALL.has(n) ? 512 : 1024, ...resolveSource(n, 'props') });
for (const f of fs.readdirSync(ART + '/poc_src').filter((f) => f.endsWith('.glb'))) {
  const n = path.basename(f, '.glb');
  jobs.push({ name: n, cat: 'props', res: 1024, ...resolveSource(n, 'props') });
}
// The civic buildings are new catalogue entries, so they have no pack predecessor to replace.
for (const n of ['building_hospital', 'building_office_mid', 'building_police_station', 'building_fire_station'])
  jobs.push({ name: n, cat: 'props', res: 1024, ...resolveSource(n, 'props') });

const io = new NodeIO().registerExtensions(ALL_EXTENSIONS).registerDependencies({
  'draco3d.decoder': await draco3d.createDecoderModule(),
  'draco3d.encoder': await draco3d.createEncoderModule(),
});

const triCount = (doc) => doc.getRoot().listMeshes().flatMap((m) => m.listPrimitives())
  .reduce((n, p) => {
    const i = p.getIndices(), a = p.getAttribute('POSITION');
    return n + Math.floor((i ? i.getCount() : a ? a.getCount() : 0) / 3);
  }, 0);

/** meshopt, escalating the error cap only as far as this mesh actually needs. */
async function meshoptTo(doc, target) {
  const start = triCount(doc);
  if (start <= target) return start;
  await doc.transform(weld({ tolerance: 0.0001 }));
  const welded = triCount(doc);
  for (const error of [0.002, 0.005, 0.01, 0.02, 0.05]) {
    const probe = await io.readBinary(await io.writeBinary(doc));
    await probe.transform(simplify({ simplifier: MeshoptSimplifier, ratio: target / welded, error, lockBorder: false }));
    if (triCount(probe) <= target * 1.15) {
      await doc.transform(simplify({ simplifier: MeshoptSimplifier, ratio: target / welded, error, lockBorder: false }));
      return triCount(doc);
    }
  }
  return start;
}

/** Blender collapse decimation. Only ever reached for unrigged assets - decimate.py
 *  exports with export_animations=False, which would silently strip a rig. */
function blenderTo(src, target, name) {
  fs.mkdirSync(CACHE, { recursive: true });
  const out = path.join(CACHE, name + '_' + target + '.glb');
  if (exists(out)) return out;
  execFileSync(BLENDER, ['-b', '--python', ALIEN + '/tools/blender/decimate.py', '--',
    src, out, '--faces', String(target), '--log', path.join(CACHE, name + '.log')],
    { stdio: 'ignore', timeout: 600000 });
  if (!exists(out)) throw new Error('blender produced nothing for ' + name);
  return out;
}

const results = [];
for (const j of jobs) {
  if (ONLY.length && !ONLY.includes(j.name)) continue;
  if (!exists(j.src)) { console.error('MISSING SOURCE ' + j.name + ': ' + j.src); continue; }

  let srcPath = j.src, via = 'as-is';
  let doc = await io.read(srcPath);
  const t0 = triCount(doc);

  if (j.tris && t0 > j.tris * 1.15) {
    await doc.transform(dedup(), prune(), resample());
    const got = await meshoptTo(doc, j.tris);
    via = 'meshopt';
    if (got > j.tris * 1.3) {
      srcPath = blenderTo(j.src, j.tris, j.name);
      doc = await io.read(srcPath);
      await doc.transform(dedup(), prune(), resample());
      via = 'blender';
    }
  } else {
    await doc.transform(dedup(), prune(), resample());
  }
  const t1 = triCount(doc);

  const root = doc.getRoot();

  // The v2 web pipeline encoded these textures as WebP and left EXT_texture_webp in
  // extensionsRequired. Swapping the image bytes back to PNG does not retract that
  // declaration, and glTFast refuses the whole file over it (log code 20). The only
  // asset that imported in the first pilot was the one pack model that never went
  // through optimize-glb.mjs. Draco goes for the same reason: no decoder package here.
  for (const ext of root.listExtensionsUsed())
    if (ext.extensionName === 'EXT_texture_webp' || ext.extensionName === 'KHR_draco_mesh_compression')
      ext.dispose();

  const baseTex = new Set();
  const opaque = root.listMaterials().every((m) => m.getAlphaMode() === 'OPAQUE');
  for (const m of root.listMaterials()) { const t = m.getBaseColorTexture(); if (t) baseTex.add(t); }
  const sizes = [];
  let emissive = null;
  for (const tex of root.listTextures()) {
    const isBase = baseTex.has(tex);
    const res = isBase ? j.res : Math.max(256, j.res / 2);
    let pipe = sharp(Buffer.from(tex.getImage()))
      .resize(res, res, { fit: 'inside', withoutEnlargement: true });
    // An alpha channel costs a DXT5 import at 1 byte/texel; without one Unity picks DXT1
    // at half that. Trellis bakes an opaque alpha, so dropping it is free - but only when
    // every material says OPAQUE, so a cutout like the tree keeps its mask.
    if (opaque) pipe = pipe.removeAlpha();
    const img = await pipe.png({ compressionLevel: 9 }).toBuffer();
    // The URI *is* the written filename. Leave it unset and the writer invents
    // "baseColor_1.png" for every model in the folder, so the last asset written wins and
    // all 51 props end up sharing one hydrant texture. The pilot run caught exactly that.
    const slot = isBase ? '_baseColor' : '_metallicRoughness';
    tex.setName(j.name + slot).setURI(j.name + slot + '.png');
    tex.setImage(img).setMimeType('image/png');
    sizes.push(res);
    if (isBase && !emissive) emissive = await emissiveFrom(img, res, CLASS_OF(j.name));
  }

  // Attach the emissive map, if this asset has anything that actually glows. Half res:
  // it drives bloom, which is low-frequency, so full res buys nothing.
  if (emissive) {
    const et = doc.createTexture(j.name + '_emissive')
      .setURI(j.name + '_emissive.png')
      .setMimeType('image/png')
      .setImage(await sharp(emissive.png).resize(Math.max(256, j.res / 2), Math.max(256, j.res / 2),
        { fit: 'inside', withoutEnlargement: true }).png({ compressionLevel: 9 }).toBuffer());
    for (const m of root.listMaterials()) {
      m.setEmissiveTexture(et);
      m.setEmissiveFactor([1, 1, 1]);
    }
  }

  const dstDir = path.join(OUT, j.cat);
  let bytes = 0;
  if (!DRY) {
    fs.mkdirSync(dstDir, { recursive: true });
    // .gltf + loose .png: the textures become Unity assets, which is the entire point.
    await io.write(path.join(dstDir, j.name + '.gltf'), doc);
    bytes = fs.readdirSync(dstDir)
      .filter((f) => f === j.name + '.gltf' || f === j.name + '.bin' || f.startsWith(j.name + '_'))
      .reduce((n, f) => n + fs.statSync(path.join(dstDir, f)).size, 0);
  }
  const anims = root.listAnimations().length, skins = root.listSkins().length;
  results.push({ ...j, t0, t1, via, sizes, bytes, anims, skins,
    emissivePct: emissive ? +(100 * emissive.frac).toFixed(1) : 0, source: path.basename(srcPath) });
  console.log(j.name.padEnd(26) + String(t0).padStart(6) + ' -> ' + String(t1).padStart(6) +
    ' tris  ' + via.padEnd(8) + ' tex ' + sizes.join('/').padEnd(10) +
    (emissive ? ('emis ' + (100 * emissive.frac).toFixed(1) + '%').padEnd(12) : ''.padEnd(12)) +
    (anims ? ' anims ' + anims + '/skins ' + skins : '').padEnd(18) + (bytes / 1e6).toFixed(2) + ' MB');
}

fs.writeFileSync(path.join(PROJECT, 'Tools', 'rebuild-report.json'),
  JSON.stringify({ generated: new Date().toISOString(), results }, null, 2));
console.log('\n' + results.length + ' assets rebuilt into ' + OUT);
