# UFO Shooter — Gameplay Guide & Port Spec

This document is two things at once:

1. **A gameplay guide** — what UFO Shooter *is*, how it plays, and every tuning number that
   makes it feel the way it does.
2. **The port spec for v3 (Unity)** — the authoritative source of truth for the C# rewrite.
   Every constant below was read out of the v2 JavaScript, not remembered, so the Unity build
   can match the web build's feel exactly.

Version history:

| Version | Where | Engine | Art |
|---|---|---|---|
| v1 — *UFO Invasion* | `C:\Users\coryc\UFOShooter` | Three.js r160 | 100% procedural (`THREE.*Geometry`) |
| v2 — *UFO Invasion II* | `C:\Users\coryc\alienGame` | Three.js r170 | 61 authored GLBs, rigged, 7 clips each |
| **v3 — this project** | `C:\Users\coryc\ufoUnityShooter` | **Unity 6000.6.3f1 / URP** | v2's assets, imported |

v3 keeps v2's **art and gameplay** and replaces the **engine**. Target is still the browser —
Unity WebGL, so the game stays a link you can send someone.

---

## 1. The core loop

You are one soldier, on foot, in a Chicago that is being invaded. Waves of aliens drop in from
orbit around you. Kill everything in a wave, get a breather, next wave, forever. Every fifth
wave a boss arrives; every fifth wave you also move to a new part of the city.

```
    spawn wave  ->  fight  ->  wave clear  ->  [perk choice]  ->  next wave
                                                     |
                                       every 5th wave: boss, then new level
```

There is no ammo economy to speak of, no cover system, no objectives. The entire game is
**positioning + target priority + weapon choice**. It is fast: you move at 12 m/s, sprint at
21.6 m/s, and can dash 50 m/s for 0.15 s with i-frames.

### What makes it feel good (keep these in the port)

- **Shields are a separate health bar with different rules.** Plasma strips shields at 2.4x,
  bullets barely dent them (0.7x). This forces weapon swapping mid-fight rather than picking a
  favourite.
- **Front shields punish lazy aim.** Skirmishers and Juggernauts block anything arriving inside
  a cone of their facing. You *must* flank them. Explosives get 1.5x against a front shield.
- **The dash has i-frames.** 0.15 s of invulnerability on a 2 s cooldown is the panic button
  that makes the 100 HP pool survivable.
- **Combo multiplier on score.** Kills inside a 3 s rolling window multiply score by the combo
  count, so aggression pays.
- **Everything drops.** 28% base drop chance, higher on big enemies, guaranteed on elites and
  bosses. You are rewarded for pushing into the fight.
- **Multi-kills slow time.** 3+ kills inside the window drops timescale to 0.35 for 0.4 s and
  throws a kill-streak banner. Cheap, and it lands every time.

---

## 2. Controls

| Action | Keyboard/Mouse | Xbox |
|---|---|---|
| Move | `WASD` | Left stick |
| Look | Mouse | Right stick |
| Fire | `LMB` | RT |
| Alt fire / aim | `RMB` | LT |
| Weapon 1–4 | `1` `2` `3` `4` | LB / RB cycle |
| Grenade | `Q` | Y |
| Reload | `R` | X |
| Sprint | `Shift` | B |
| Jump | `Space` | A |
| Dash | `E` | Left stick click |
| Field guide | `Tab` | — |
| Pause | `Esc` | Start |

iOS/touch: twin virtual sticks plus a fire button.

**Alt-fire is weapon-specific.** On the MA-7 it toggles a zoom (spread drops to 25%). On the
Energy Sword it is a heavy overhead: 1.8x cooldown, 1.6x damage. On the other two it does
nothing.

---

## 3. Player

| Stat | Value |
|---|---|
| Max HP | 100 |
| Max shield | 50 (pickup only, does not regenerate) |
| Health regen | 6 HP/s, starting 4 s after the last damage taken |
| Eye height | 1.7 m |
| Walk speed | 12 m/s |
| Sprint multiplier | 1.8x (= 21.6 m/s) |
| Jump force | 8 m/s |
| Gravity | 20 m/s² |
| Dash speed / duration / cooldown | 50 m/s · 0.15 s · 2.0 s |
| Mouse sensitivity | 0.002 rad/px |
| Grenades | 3, max 6 |

Damage order is **shield first, then HP**. Taking any damage resets the 4 s regen delay. The
dash grants full damage immunity for its 0.15 s.

### Combo

Every kill sets a 3.0 s timer and increments the combo. Score from a kill is
`points × max(1, combo)`. The timer expiring resets the combo to 0. `COMBO MASTER` adds 1.5 s
to the window.

### Perks

One perk is offered after every wave (three random cards, pick one). Perks stack — picking the
same one twice doubles it.

| Perk | Effect |
|---|---|
| RAPID FIRE | Fire interval × 0.8 (stacking, multiplicative) |
| TOUGH SKIN | +25 max HP, and +25 current HP |
| QUICK FEET | +15% movement speed |
| VAMPIRE | Kills restore 5 HP |
| BLAST RADIUS | +40% explosion radius |
| SHARPSHOOTER | +15% weapon damage |
| COMBO MASTER | +1.5 s combo window |
| SCAVENGER | +25% pickup drop rate |

---

## 4. Weapons

All four are always carried; there is no pick-up-and-drop. Hit detection is **analytic** — a ray
against a vertical capsule per enemy — not mesh colliders. Keep it that way in Unity; it is
cheaper and behaves identically against every rig.

| | MA-7 Rifle | Plasma Rifle | Energy Sword | Rocket Launcher |
|---|---|---|---|---|
| Key | `1` | `2` | `3` | `4` |
| Kind | hitscan | projectile | melee | rocket |
| Damage | 11 | 16 | 140 | 260 |
| Fire interval | 0.095 s | 0.13 s | 0.6 s | 1.2 s |
| Range | 140 m | 120 m | 3.8 m | 150 m |
| Projectile speed | — | 70 m/s | — | 42 m/s |
| Spread | 0.014 | 0.02 | — | — |
| Magazine | 32 | heat | ∞ | 2 |
| Reserve | ∞ | — | ∞ | 8 |
| Reload | 1.7 s | — | — | 2.8 s |
| Automatic | yes | yes | no | no |
| Headshot multiplier | **2.0x** | 1.3x | 1.0x | 1.0x |
| **Shield multiplier** | **0.7x** | **2.4x** | 1.6x | 1.0x |
| Splash | — | — | — | 6.5 m |
| Recoil | 0.012 | 0.006 | — | 0.06 |
| Lunge | — | — | 7.5 m | — |
| Melee arc | — | — | 0.85 rad | — |

**Plasma Rifle heat:** +0.065 per shot, cools at 0.45/s. At 1.0 it overheats and locks out for
1.8 s. That is ~15 shots before a forced cool-down.

**Energy Sword lunge** is the Halo trick: on swing, if an enemy is within 7.5 m inside the
0.85 rad arc, the player is *pulled to them* over 0.14 s, then damage lands at t=0.1 s. Without
the lunge the sword is unusable (v1's sword had no lunge and a test bot swung at nothing for 38
straight seconds — see the v2 handoff notes).

**Grenade:** 190 damage, 6 m splash, 19 m/s throw with +0.25 upward bias, 16 m/s² gravity,
bounces, 2.2 s fuse.

---

## 5. Enemies

Six types. HP is body health; shield is a *recharging* energy shield; `frontShield` is a
physical arm/tower shield that only blocks hits arriving inside `frontShieldArc` of facing.

| | Gnat | Skirmisher | Warlord | Juggernaut | Wasp | Overseer |
|---|---|---|---|---|---|---|
| Role | ranged | skirmisher | commander | tank | aerial | **boss** |
| Height | 1.25 m | 1.8 m | 2.4 m | 3.5 m | 1.0 m | 5.0 m |
| HP | 45 | 70 | 180 | 900 | 50 | 2600 |
| Energy shield | — | — | 220 | — | — | 900 |
| Shield delay / regen | — | — | 4 s / 60 per s | — | — | 6 s / 120 per s |
| Front shield / arc | — | 160 / 0.9 rad | — | 600 / 0.75 rad | — | — |
| Speed | 4.2 | 7.5 | 5.5 | 2.4 | 9.0 | 3.5 |
| Damage | 5 | 6 | 8 | 55 | 4 | 16 |
| Attack interval | 1.1 s | 0.35 s ×3 burst | 0.5 s ×4 burst | 2.6 s | 0.6 s | 0.18 s |
| Attack range | 18 m | 26 m | 30 m | 40 m | 22 m | 60 m |
| Keep distance | 10 m | 14 m | 9 m | 12 m | 12 m | 18 m |
| Hover height | — | — | — | — | 5 m | 7 m |
| Radius | 0.45 | 0.5 | 0.7 | 1.3 | 0.5 | 2.2 |
| Points | 100 | 175 | 400 | 1200 | 150 | 6000 |
| Splash | — | — | — | 5 m | — | — |
| Melee | — | — | 40 dmg @ 3.2 m | — | — | — |

### AI behaviours

- **Gnat** — holds at 85% of attack range and plinks. Panics below 40% HP (flees zig-zagging at
  1.5x speed) and panics for 5 s if a nearby Warlord dies within 25 m. Faces its actual heading
  while running, snaps to face the player only to shoot.
- **Skirmisher** — orbits at `keepDistance`, strafe direction flips every 0.8–2.2 s. Closes in
  while its arm shield is up, backs off to 1.6x distance once broken. Fires 3-round needle
  bursts at 0.09 s spacing then waits 4.5x the attack interval. Arm shield regrows to 60% after
  8 s down.
- **Warlord** — presses the attack while shielded. Lunges in at 1.6x speed inside 7 m, melees
  inside 3.2 m (damage lands 0.28 s into the swing). When its energy shield pops it **retreats
  for 2.2 s to recharge** — that retreat is the window you kill it in. 4-round plasma bursts at
  0.14 s spacing.
- **Juggernaut** — slow, relentless, turns at 3 rad/s. Lobs a **ballistic** fuel rod with 14
  m/s² gravity, led onto where you will be, 5 m proximity splash. The arc is what makes it
  dodgeable. Tower shield blocks a 0.75 rad frontal cone — flank or rocket it.
- **Wasp** — circles overhead at 5 m, randomly *dives* to ~2.2 m to strafe, banks into turns.
  Cannot be body-blocked; dies and falls spinning.
- **Overseer** (boss, every 5th wave) — hovers at 7 m, sweeps plasma, **spawns 3 Gnats every 12
  s**, slowly rotates. Fires 3x slower while its 900-point shield is up, so stripping the shield
  with plasma is the whole fight.

### Shared rules

- **Elites** every 3rd wave (up to 3 of them): 1.8x HP, 1.5x shield, 1.15x speed, 1.4x damage,
  orange emissive tint, 3x score, guaranteed drop.
- **Line of sight** is sampled twice a second. A ground enemy that cannot see you for **2.5 s**
  switches to `hunting` and comes to find you.
- **Global aggression fallback:** after **75 s** in a wave, every enemy turns aggressive. Also,
  once **2 or fewer** enemies remain they all turn aggressive — this was the real pacing fix,
  because ranged units standing off at `keepDistance` could stretch a wave to 60–100 s of
  nothing happening.
- **Separation steering** pushes same-layer enemies apart so packs don't interpenetrate. Ground
  and air units ignore each other.
- **Stuck detection:** moving less than 0.6 m in 1.0 s turns on `ghostTimer` for 2.5 s, letting
  the unit walk through *soft* props. Cornered units commit to a detour direction for 0.9 s
  instead of re-deciding every frame (per-frame re-evaluation oscillates against box corners).

---

## 6. Waves

Count is `min(4 + floor(wave × 1.8), 32)`.

Waves 1–3 are fixed so the roster is introduced one species at a time; wave 4+ picks randomly
from every theme unlocked so far.

| Theme | From wave | Mix |
|---|---|---|
| SCOUT LANCE | 1 | 100% Gnat |
| FLANKING PAIR | 2 | 60% Gnat, 40% Skirmisher |
| COMMAND SQUAD | 3 | 55% Gnat, 25% Skirmisher, 20% Warlord |
| AIR RAID | 4 | 50% Wasp, 30% Gnat, 20% Skirmisher |
| SIEGE LINE | 6 | 15% Juggernaut, 45% Gnat, 20% Warlord, 20% Skirmisher |
| SHADOW STRIKE | 7 | 50% Skirmisher, 30% Warlord, 20% Wasp |
| FULL ASSAULT | 9 | 30/20/20/15/15 Gnat/Skirm/Warlord/Wasp/Jugg |

Caps: Juggernauts `1 + floor(wave/6)`. Warlords at least 1 from wave 3, capped at 1 below wave
6 then `2 + floor(wave/6)`.

**Scaling** — HP `1 + (w-1)^1.12 × 0.07`, speed `1 + (w-1) × 0.03`, damage `1 + (w-1)^1.05 × 0.035`.

**Cadence** — elites on every 3rd wave, Overseer on every 5th, level change after every 5th.

**Spawning** — commanders spawn first so squads form around them, then the rest shuffled.
Followers have a 60% chance to spawn within 7 m of the last Warlord. Spawn point selection is
*graded*, not binary: <18 m scores 0 (never materialise on the player), <30 m scores 0.7,
30–55 m scores 1.0, 55–75 m scores 0.85, beyond that 0.6, plus up to 0.5 random. The last-used
point is penalised to 0.45x so arrivals come from spread directions instead of all funnelling
through the same two near corners. Spawn interval 0.45 s, 1.5 s for a boss.

---

## 7. Pickups

Dropped on death: 28% base, +20% if the enemy's base HP > 150, +25% per SCAVENGER perk.
Elites and bosses always drop. Lifetime 16 s, 2 m pickup radius.

| Type | Roll | Effect |
|---|---|---|
| Health | <0.45 | +25 HP |
| Shield | 0.45–0.70 | +35 shield (caps at 50) |
| Ammo | 0.70–0.90 | refills reserve |
| Grenade | >0.90 | +1 grenade (max 6) |

Bosses always drop shield.

---

## 8. Levels

Three Chicago arenas, `arenaRadius` 92 m, built from a block grid — 34 m blocks with 10 m
streets (pitch 44 m).

1. **THE LOOP** — *Downtown Chicago, first contact.* Skyline of towers, the L track running
   along the north edge, neon signs on the inner ring, barricaded central plaza. Clear weather.
2. **RIVER NORTH** — *Warehouse district, the counter-attack.* Rain. Industrial blocks, a
   container yard on the east side (stacked, with pallets) that is the best cover in the game,
   overturned wrecks and burning cars.
3. **LAKEFRONT** — *Navy Pier, hold the shoreline.* Everything east of x=22 is Lake Michigan
   (reflective water). The pier runs out along z ∈ [-7, 7] to x=80 with the ferris wheel at the
   end. Fighting down the pier is a deliberate corridor; drops land on the pier head.

Each level supplies fog, a three-colour sky gradient, ambient/hemi/sun lighting, a building
grid, scattered vehicles, landmarks (a downed dropship, a tripod), fires, orbital beams, neon
signs, and prop/deco lists. `deco:` items are **visual only, no collider** — used on the pier so
the narrow deck never traps anyone.

---

## 9. Asset inventory (all reused in v3)

61 GLB models, all Draco-compressed with WebP textures in v2, all decoded to plain glTF for
Unity.

- **6 enemies** — rigged bipeds, 7 animation clips each: `Idle`, `Walk`, `Run`, `Attack`,
  `Shoot`, `Hit`, `Death`.
- **4 weapons** — first-person viewmodels: `rifle`, `plasma_rifle`, `energy_sword`,
  `rocket_launcher`.
- **14 buildings** — skyscraper / residential / commercial / industrial variants.
- **11 civilian + 2 alien vehicles**, plus taxi, police car, CTA bus, car wreck.
- **~20 street props** — jersey barrier, sandbags, hydrant, traffic light, dumpster, bench,
  planter, mailbox, news box, trash can, tree, cones, pallets, container, bus shelter, drop
  pod, rubble, hotdog cart, L track, pier kiosk, ferris wheel, yacht, mothership.
- **3 textures** (asphalt, sidewalk, water normals), **4 decals** (blood, bullet, neon, scorch),
  **3 UI images**, **2 music tracks**.

Both music tracks are byte-identical to v1's — they have survived every rewrite.

### Animation clip contract

`tools/blender/rig_biped.py` produced exactly these seven clips on every enemy rig. The Unity
`EnemyAnimator` binds to them by name. `Attack`, `Shoot`, `Hit` and `Death` are **one-shot**
(clamp when finished); `Idle`, `Walk`, `Run` loop. Locomotion picks Walk vs Run from actual
movement speed: run when `moveSpeed > 0.95 × baseSpeed`, and the clip's playback rate is scaled
by `moveSpeed / baseSpeed` so feet don't skate.

---

## 10. What v3 changes

Nothing about the *design*. The rewrite is an engine swap:

| v2 (Three.js) | v3 (Unity) |
|---|---|
| Hand-rolled AABB sweep in `_move()` | Same analytic approach, kept — **not** Unity CharacterController/NavMesh |
| `THREE.AnimationMixer` + clip names | `Animator` with a per-enemy controller, same clip names |
| DOM HUD in `index.html` | uGUI Canvas + TextMeshPro |
| `EffectComposer` bloom + SMAA | URP Volume: Bloom + Tonemapping, SMAA on the camera |
| GLB loaded at runtime via `GLTFLoader` | GLBs imported at build time by glTFast |
| Pointer Lock API | `Cursor.lockState = Locked` |
| ES modules served by `tools/serve.mjs` | Unity WebGL build, Brotli-compressed |

Deliberately kept identical: every number in this document, the analytic ray-vs-capsule hit
detection, the graded spawn scoring, the 2-enemies-left aggression rule, and the sword lunge.

---

## 11. Known traps (learned the hard way in v2)

1. **Don't drive the game with a bot that clicks the perk card blindly.** The v2 playtest bot
   clicked a hidden perk card every frame and inflated HP to 16450. Only interact with the perk
   UI when it is actually visible.
2. **The sword needs its lunge.** Without it, melee is dead weight.
3. **Ranged enemies will stall a wave.** The 75 s global and 2-left aggression rules exist
   because of this. Port both.
4. **Per-frame pathing re-evaluation oscillates in corners.** Commit to a detour for ~0.9 s.
5. **Aim height matters.** A generic 1.8 m aim point shoots clean over a 1.0 m Wasp or a 1.25 m
   Gnat. Use the per-type height table.
