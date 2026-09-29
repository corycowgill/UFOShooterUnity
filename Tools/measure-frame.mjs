// measure-frame.mjs - objective readings from a screenshot, so "looks too bright" becomes a number.
import sharp from 'sharp';
import path from 'node:path';

const file = process.argv[2] || '../shots/04-gameplay.png';

const REGIONS = {
  sky:       { left: 100, top: 100, width: 300, height: 120 },
  building:  { left: 810, top: 270, width: 200, height: 100 },
  groundNear:{ left: 200, top: 700, width: 300, height: 80 },
  groundMid: { left: 200, top: 470, width: 300, height: 80 },
};

const img = sharp(path.resolve(file));
const meta = await img.metadata();
console.log(`${path.basename(file)}  ${meta.width}x${meta.height}`);

for (const [name, r] of Object.entries(REGIONS)) {
  const { data, info } = await sharp(path.resolve(file)).extract(r).raw().toBuffer({ resolveWithObject: true });
  let sum = 0, min = 255, max = 0;
  const n = info.width * info.height;
  // Standard deviation of luminance is the useful number for "is there any texture detail here".
  const lums = new Float64Array(n);
  for (let i = 0, p = 0; i < data.length; i += info.channels, p++) {
    const l = 0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2];
    lums[p] = l; sum += l;
    if (l < min) min = l;
    if (l > max) max = l;
  }
  const mean = sum / n;
  let varsum = 0;
  for (let i = 0; i < n; i++) varsum += (lums[i] - mean) ** 2;
  const sd = Math.sqrt(varsum / n);
  console.log(`  ${name.padEnd(11)} mean=${mean.toFixed(1).padStart(5)}  sd=${sd.toFixed(1).padStart(5)}  min=${min.toFixed(0).padStart(3)}  max=${max.toFixed(0).padStart(3)}`);
}
