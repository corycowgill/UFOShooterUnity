# UFO Invasion III

Unity 6 / URP rebuild of [UFO Invasion II](../alienGame), targeting the browser via WebGL.

Same game, third engine:

| | v1 | v2 | **v3** |
|---|---|---|---|
| Engine | Three.js r160 | Three.js r170 | **Unity 6000.6.3f1 + URP 17.6** |
| Art | 100% procedural | 61 authored GLBs | **the same 61 GLBs** |
| Ships as | static HTML | static HTML | **Unity WebGL** |
| Source | `C:\Users\coryc\UFOShooter` | `C:\Users\coryc\alienGame` | this repo |

The design did not change. Every tuning number — enemy HP, shield multipliers, wave composition,
the 0.15 s dash — was read out of v2's JavaScript and carried across verbatim.
**[`GAMEPLAY_GUIDE.md`](GAMEPLAY_GUIDE.md) is the full spec and the gameplay guide.**

---

## Build and run

```bash
# one-time project configuration (URP assets, importers, scene, WebGL player settings)
Tools/setup.sh

# WebGL build -> Build/web
Tools/build-web.sh

# serve it
cd Build/web && npx serve -l 8080 .
```

Open the project in the Unity Editor and press Play for the fast iteration loop; the game builds
itself at runtime, so there is nothing to wire up in the Inspector.

---

## How it is put together

The unusual thing about this project is that **there is one scene and it contains one object.**
`Assets/Scenes/Game.unity` holds a `GameRoot` with a `GameManager`, plus a post-processing
volume. Player, camera, weapon rig, HUD, the entire Chicago level and every enemy are built from
code at runtime out of `Assets/Resources`.

That is a deliberate trade. It means the whole game is reproducible from source with no scene
merge conflicts and no Inspector state to lose — which is what made porting a 15-file JavaScript
game tractable without hand-authoring scenes.

```
Assets/
  Scenes/Game.unity           the only scene: GameRoot + PostProcessing
  Settings/                   URP pipeline, renderer and post-FX profile (generated)
  Resources/
    Models/enemies|weapons|props   61 glTF models
    Textures/ Decals/ UI/ Audio/
  Scripts/
    Core/     Arena, GameManager, InputMap, ModelCache
    Player/   PlayerController, PlayerStats, WeaponManager, WeaponData, ViewHands, ViewFx
    Enemies/  Enemy, EnemyData, EnemyAnimator, EnemyProjectiles, WaveManager
    Level/    LevelData (generated), LevelBuilder, CityProps, MeshKit
    FX/       Fx, GameAudio, PickupProps, FallingDebris
    UI/       Hud
  Editor/
    ProjectSetup       URP + player settings + scene generation
    AssetImportSetup   glTF and texture importer configuration, plus diagnostics
    BuildWebGL         the build entry point
Tools/
  decode-glb.mjs      v2 web GLBs -> Unity-friendly glTF
  gen-levels.mjs      v2 js/level.js -> Assets/Scripts/Level/LevelData.cs
  setup.sh build-web.sh
```

### Design decisions worth knowing

**World collision is not Unity physics.** `Arena` is a flat list of AABBs, and the player,
enemies, projectiles and hitscan all query it directly. This is a straight port of v2's collider
list. Using `CharacterController` and mesh colliders instead would have changed how movement
feels and would have meant baking colliders on 61 imported models.

**Hit detection is analytic.** A ray against a vertical capsule per enemy, not a physics raycast.
Identical behaviour against every rig regardless of mesh complexity, and cheap against 30 enemies.

**Enemy animation is legacy, not Mecanim.** The rigs carry seven named clips (`Idle`, `Walk`,
`Run`, `Attack`, `Shoot`, `Hit`, `Death`) and the AI picks between them imperatively, scaling
playback rate by measured movement speed. An AnimatorController would be a state graph we only
ever bypass. `AssetImportSetup` sets glTFast's animation method to Legacy for this reason.

**Street dressing above the shopfronts is procedural.** v2's GLB catalogue has no rooftop
clutter, nothing overhead and nothing on the road surface, so `CityProps` builds them — water
towers, masts, billboards, catenary wires, lit storefronts, fire escapes, craters, contact
shading — appending into a `MeshKit` per material. Placing a water tower on every roof therefore
costs no draw calls at all; the whole library comes out as ~16 meshes and ~85k triangles.

Two rules that are easy to break there. Anything whose transform is animated needs a `NoBatch`
component, or `StaticBatchingUtility.Combine` bakes its transform and freezes it. And thin wire
goes in its own kit with shadow casting **off**: a 35 mm cable is far narrower than a shadow-map
texel, so its shadow resolves into a dotted line and lays a string of beads down the middle of
the road that looks exactly like a decal bug.

### Runtime transparency: additive works, alpha blending does not

The most expensive lesson in the street-dressing work, and it costs a six-minute WebGL build to
learn each time, so it is written down here.

**A material built at runtime and set to alpha blending renders OPAQUE in the WebGL player.** It is
correct in the Editor. It is correct in a play-mode capture. It ships wrong. It produced, in turn:

- contact shadows as hard black rectangles under every bench and car,
- smoke columns as solid black mushrooms hanging over the skyline,
- a street vent's steam as a stack of grey billiard balls in the middle of the road.

Each of those was inspected in the Editor and pronounced fine. What settled it was tinting every
ground layer a different flat colour, building, and looking: the offenders were the two layers that
came back **black instead of their debug colour** — the only two that were alpha blended.

Nothing about the material says so. `_Surface`, `_SrcBlend`, `_DstBlend`, `_ZWrite`, the
`_SURFACE_TYPE_TRANSPARENT` keyword and the render queue all read back in the player exactly as
they do on a `Decals` quad that renders correctly. Four separate constructions were tried — setting
the state by hand, going through `Fx.MakeTransparent`, building the material in `Awake` rather than
during the level build, and forcing the render queue — and all four shipped opaque.

So the rule is empirical, not theoretical:

- **`Fx.AdditiveTinted` works.** Every neon sign, glow panel, beam, light shaft, shopfront and
  particle uses it. Smoke and steam are additive for this reason and no other — which is no great
  loss, because smoke over a city that is on fire is lit from below anyway.
- **Opaque geometry always works.** The contact shading under props is two concentric lit discs
  rather than a textured quad with a radial alpha, for exactly this reason.
- **`Decals` is the one alpha-blended thing that ships correctly**, and it is textured and built in
  `Awake`. Do not assume a new alpha-blended material will behave like it; verify in a build.

And a trap inside the trap: additive blending multiplies by the texture's **colour**, so the falloff
sheet is a white radial gradient on black. The first version was black with a radial alpha — right
for alpha blending, and it adds precisely nothing when blended additively.

**Sound effects are synthesised at startup.** `GameAudio` generates the whole SFX bank as PCM in
C#, the same way v2 generated it with WebAudio. The only audio assets shipped are the two music
tracks — which are byte-identical to the ones v1 shipped in April.

**Levels are generated, not transcribed.** `Tools/gen-levels.mjs` lifts the catalogue and the
three level layouts straight out of v2's `js/level.js` and emits `LevelData.cs`. Re-run it rather
than hand-editing, so Chicago stays the same Chicago block for block.

---

## The asset pipeline

v2's GLBs are Draco-compressed with WebP textures, which is right for the web and wrong for
Unity — glTFast would need extra packages for Draco and has no WebP decoder.
`Tools/decode-glb.mjs` decodes both:

```
alienGame/assets/models/**.glb          Draco + WebP,  ~1 MB each
        |  node Tools/decode-glb.mjs
        v
Assets/Resources/Models/**.glb          plain glTF + PNG, ~4-8 MB each
        |  Unity import (max texture 1024, compressed)
        v
Runtime                                 back to a few hundred KB each in the build
```

The intermediate files are large; that is fine, because Unity recompresses on build. It is the
importer settings, not the source size, that determine the download.

To regenerate everything from v2:

```bash
cd Tools
node decode-glb.mjs C:/Users/coryc/alienGame/assets/models ../Assets/Resources/Models
node gen-levels.mjs
```

`Tools/node_modules` is a junction to `alienGame/node_modules` (gltf-transform, sharp, draco).
If that project moves, run `npm i @gltf-transform/core @gltf-transform/extensions draco3dgltf sharp`
in `Tools/` instead.

---

## The studio ident

The game opens with the **Hallucinated Games** ident - the same logo castleSurvivor and
aliendigger play, vendored from `gameCentral/intro/hallucinated-intro.js` into
`Assets/WebGLTemplates/Hallucinated/TemplateData/`.

It lives in a **custom WebGL template** rather than a patched `index.html`, so it survives every
rebuild. `ProjectSetup` selects it with `PlayerSettings.WebGL.template = "PROJECT:Hallucinated"`.

The important detail is that the ident and the download run **concurrently**. A Unity WebGL build
is tens of megabytes; playing the logo first and then starting the download would add its 4.6
seconds to the wait. Started together, the ident covers load time instead of adding to it, and
the canvas is revealed when both finish - whichever is last. If the ident ends first, a thin
progress bar carries the rest; on a warm cache it never appears.

`?nointro` or `?debug` skips it, which is what the automated checks use.

Sound stays off: the sting needs a prior user gesture and a cold page load has none, so it would
be dropped silently. That matches how the other games call it.

To pick up a new cut of the ident:

```bash
cp ../gameCentral/intro/hallucinated-intro.js Assets/WebGLTemplates/Hallucinated/TemplateData/
```

---

## Shipping to the web

The build is Brotli-compressed with `decompressionFallback` on, so Unity emits `.unityweb` files
and decompresses them in JavaScript. **It therefore runs on any static host with no special
headers** — that is the safe default. The faster path (native browser Brotli, `.br` files,
`Content-Encoding: br`) needs the fallback turned off and the headers set correctly; getting them
wrong is the classic blank-canvas failure. `Build/web/SERVING.md` is written next to each build
with both routes.

WebGL settings chosen for this game: IL2CPP, Master configuration, managed stripping Medium,
exceptions off, 512 MB heap, data caching on, no threads (widest browser support).

---

## Verifying a build

```bash
node Tools/serve.mjs --dir Build/web --port 8123 &
node Tools/verify-web.mjs --gpu          # loads it in real Chrome, plays it, screenshots to shots/
node Tools/measure-frame.mjs shots/04-gameplay-2.png
```

For a look at the level itself, without a web build in the loop:

```bash
Tools/shots.sh                          # both suites -> shots-layout/
Tools/shots.sh Render_Eye_Level_Tour    # just the walk-around, ~40 s
node Tools/contact-sheet.mjs shots-layout sheet.jpg --match tour- --cols 3
```

`shots.sh` runs the `LayoutShots` suites in batchmode **without** `-nographics`, which is what
gives them a graphics device; they `Assert.Ignore` themselves when there is none, so they stay out
of the pass/fail gate. `contact-sheet.mjs` tiles the output into one labelled image, which is how
a change to the street dressing gets judged in one look rather than thirteen.

`verify-web.mjs` boots the build in headless Chrome, captures the ident mid-play, clicks START,
sweeps the view while firing, and fails on any console error. `measure-frame.mjs` turns a
screenshot into numbers (mean luminance and standard deviation per region), which is how you tell
"too bright" from "not rendering" without guessing.

### Cameras created in code have post-processing OFF

URP sets `renderPostProcessing = false` on any camera built from script. The game camera is built
from script, so for a long stretch the shipped build ran with **no tonemapping, bloom, vignette or
colour grading at all** — and the entire display group in the settings screen was inert, because
the Volume it writes to was never sampled. The capture suites had the same problem, so frames
judged from them were missing the post stack too.

If a build looks flat, or a Volume override appears to do nothing, check this first:

```csharp
var camData = cam.GetUniversalAdditionalCameraData();
camData.renderPostProcessing = true;
camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
```

**Pass `--gpu` whenever you are judging how a frame looks.** Without it the harness runs on
SwiftShader, Chrome's software GL. SwiftShader proves the game boots, but it renders large flat
surfaces with tile seams and collapses their shading to a single flat colour — a textured asphalt
street comes back with a luminance standard deviation of exactly **0.0**. That reads precisely
like a lighting or material bug in the game, and it cost several rebuilds chasing a renderer
artifact. On D3D11 the same frame measures sd 2.9 and shows the texture. If a surface looks
impossibly flat, re-render with `--gpu` before changing any material code.

---

## Comparing against v2

```bash
node Tools/capture-v2.mjs                       # stills from the real v2 build
node Tools/record-comparison.mjs --seconds 225  # side-by-side video, v2 | v3
```

`record-comparison.mjs` serves both builds, drives them with the *same* scripted bot from the same
starting wave, captures each with a CDP screencast, and cuts them side by side with ffmpeg.

Frame timing comes from the screencast metadata rather than being assumed. A screencast does not
deliver a fixed rate, and assembling its frames at a constant fps is what makes a recording play
back fast or slow; each pane gets an ffmpeg concat list with real per-frame durations instead.

`drawtext` needs an explicit `fontfile` on Windows — without one it falls back to fontconfig,
finds no config, and crashes rather than failing gracefully.

---

## Calibrating the look

```bash
Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.LightCalibration
```

Renders the same frame across a sweep of one parameter in a single run, so a lighting decision is
made by measuring rather than by rebuilding and squinting. It is how the plaza gap was finally
closed: the sweep showed **ambient was a weak lever** (0.45 → 1.20 moved the plaza only 70 → 83
against v2's 112) and that exposure was the real difference.

---

## Diagnostics

Two menu items exist because they answer the two questions that actually come up:

- **UFO ▸ Diagnose ▸ Enemy Rigs** — prints each enemy's imported clip names, whether they are
  legacy, and whether an `Animation` component survived the import. Run this the moment enemies
  stop animating; it tells you immediately whether the importer or the code is at fault.
- **UFO ▸ Diagnose ▸ Catalogue Coverage** — confirms every catalogue key resolves to a model on
  disk, so a missing prop shows up as a log line rather than an empty street.

The game also logs a one-line summary to the browser console on every level build — renderer and
collider counts, the tallest building and where it stands, the ground material's shader, tint,
bound texture and tiling, plus ambient, sun and fog. `verify-web.mjs` prints those lines. They are
what separated "the level is not building" from "the level is fine and the renderer is lying".
