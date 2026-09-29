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
    Player/   PlayerController, PlayerStats, WeaponManager, WeaponData
    Enemies/  Enemy, EnemyData, EnemyAnimator, EnemyProjectiles, WaveManager
    Level/    LevelData (generated), LevelBuilder
    FX/       Fx, GameAudio
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

`verify-web.mjs` boots the build in headless Chrome, captures the ident mid-play, clicks START,
sweeps the view while firing, and fails on any console error. `measure-frame.mjs` turns a
screenshot into numbers (mean luminance and standard deviation per region), which is how you tell
"too bright" from "not rendering" without guessing.

**Pass `--gpu` whenever you are judging how a frame looks.** Without it the harness runs on
SwiftShader, Chrome's software GL. SwiftShader proves the game boots, but it renders large flat
surfaces with tile seams and collapses their shading to a single flat colour — a textured asphalt
street comes back with a luminance standard deviation of exactly **0.0**. That reads precisely
like a lighting or material bug in the game, and it cost several rebuilds chasing a renderer
artifact. On D3D11 the same frame measures sd 2.9 and shows the texture. If a surface looks
impossibly flat, re-render with `--gpu` before changing any material code.

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
