using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// The player arsenal: viewmodels on the camera, plus hitscan / projectile / melee resolution.
///
/// Hit detection is analytic - a ray against a vertical capsule per enemy - rather than physics
/// raycasts against mesh colliders. That is how v2 did it and it is the right call here too: it
/// behaves identically against every rig regardless of mesh complexity, costs nothing to
/// evaluate against 30 enemies, and needs no collider baking on 61 imported models.
/// </summary>
public class WeaponManager : MonoBehaviour {

    public struct HitInfo {
        public Enemy Enemy;
        public float Damage;
        public Vector3 Point;
        public Vector3 From;
        public bool Headshot, Explosive;
        public float Knockback;
        public string WeaponKey;
    }

    class WeaponState {
        public int Mag, Reserve;
        public float Heat, Overheated, Reloading;
    }

    public Camera Cam;
    public Arena Arena;
    public PlayerStats Stats;
    public Fx Fx;
    public GameAudio Audio;
    public PlayerController Controller;

    public System.Action<HitInfo> OnHit;
    public System.Action<Vector3, float, bool> OnExplosion;         // (pos, radius, isRocket)
    public System.Action<Vector3, Vector3, string> OnWallHit;       // (point, normal, kind)

    public string Current { get; private set; } = "rifle";
    public WeaponDef Weapon => Arsenal.Get(Current);
    public int Grenades { get; private set; } = 3;
    public bool Zoomed { get; private set; }

    /// <summary>Hip-fire field of view, owned by GameSettings; zoom interpolates away from it.</summary>
    public float BaseFov = 75f;

    readonly Dictionary<string, WeaponState> _state = new Dictionary<string, WeaponState>();
    readonly Dictionary<string, GameObject> _viewmodels = new Dictionary<string, GameObject>();

    float _cooldown;
    float _recoil, _swayX, _swayY, _bobT, _raiseT, _swingT;
    ViewHands _hands;
    MuzzleFlash _flash;
    bool _magDropped;
    float _pendingMeleeTime = -1f, _pendingMeleeDmgMul = 1f;
    Enemy _lungeTarget; float _lungeTimer;
    Transform _rig;
    Light _muzzleLight;

    // ---- projectiles ---------------------------------------------------------
    class Projectile {
        public Transform T;
        public Vector3 Vel;
        public float Life, Damage, Splash, Gravity;
        public string Kind;       // plasma | rocket | grenade
        public bool Bounce;
    }
    readonly List<Projectile> _projectiles = new List<Projectile>(32);
    Transform _projRoot;

    // ------------------------------------------------------------------ setup

    void Awake() {
        foreach (var k in Arsenal.Order) {
            var w = Arsenal.Get(k);
            _state[k] = new WeaponState {
                Mag = w.UsesMag ? w.Mag : int.MaxValue,
                Reserve = w.Reserve >= 0 ? w.Reserve : int.MaxValue,
            };
        }
        _projRoot = new GameObject("Projectiles").transform;
    }

    public void Build(Camera cam) {
        Cam = cam;
        var rigGo = new GameObject("WeaponRig");
        _rig = rigGo.transform;
        _rig.SetParent(cam.transform, false);

        _muzzleLight = new GameObject("MuzzleLight").AddComponent<Light>();
        _muzzleLight.transform.SetParent(_rig, false);
        // Pushed out past the muzzle. Sitting on top of the weapon it blew the near face of the
        // hands out to white on every shot, which was not read as a muzzle flash - it was read as
        // the arms flickering.
        _muzzleLight.transform.localPosition = new Vector3(0.22f, -0.12f, 1.15f);
        _muzzleLight.type = LightType.Point;
        _muzzleLight.range = 6f;
        _muzzleLight.intensity = 0f;
        _muzzleLight.shadows = LightShadows.None;

        // A light that exists only for the viewmodel.
        //
        // The weapon and the arms are half a metre from the eye, in a game where the world is lit
        // by street lamps twenty metres away - so how well the player can see their own hands is
        // decided by whatever they happen to be standing under. A short-range key light fixed to
        // the camera makes the rig read the same everywhere. The range is the whole trick: at 1.1 m
        // it cannot reach anything in the world, so it lights the viewmodel and nothing else.
        var viewKey = new GameObject("ViewKeyLight").AddComponent<Light>();
        viewKey.transform.SetParent(cam.transform, false);
        viewKey.transform.localPosition = new Vector3(-0.05f, 0.35f, 0.05f);
        viewKey.type = LightType.Point;
        viewKey.range = 1.1f;
        viewKey.intensity = 1.7f;
        viewKey.color = new Color(0.82f, 0.86f, 1f);
        viewKey.shadows = LightShadows.None;

        foreach (var k in Arsenal.Order) BuildViewmodel(k);
        SetVisible(Current);

        // The hands are built last and parented to the camera, not to the weapon rig: shoulders
        // belong to the body, and only the wrists follow the gun.
        _flash = ViewFx.Create(_rig, Color.white);

        _hands = gameObject.AddComponent<ViewHands>();
        _hands.Build(cam);
    }

    void BuildViewmodel(string key) {
        var w = Arsenal.Get(key);
        var holder = new GameObject("VM_" + key);
        holder.transform.SetParent(_rig, false);
        holder.transform.localPosition = w.Pos;
        holder.SetActive(false);
        _viewmodels[key] = holder;

        var prefab = ModelCache.Load(w.ModelPath);
        GameObject obj;
        if (prefab != null) {
            obj = Instantiate(prefab);
        } else {
            obj = Prim.Create(PrimKind.Cube, "PlaceholderWeapon");
            obj.transform.localScale = new Vector3(0.12f, 0.14f, 0.8f);
            obj.GetComponent<Renderer>().material = Fx.UnlitTinted(w.Color);
        }

        // Fit: longest dimension becomes 1 unit, then scaled by the weapon's own scale.
        var wrap = new GameObject("fit").transform;
        wrap.SetParent(holder.transform, false);
        obj.transform.SetParent(wrap, false);
        if (ModelCache.LocalBounds(obj, out var b)) {
            obj.transform.localPosition = -b.center;
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            wrap.localScale = Vector3.one * (w.Scale / Mathf.Max(longest, 1e-6f));
        }
        wrap.localRotation = Quaternion.Euler(w.Rot);

        foreach (var r in obj.GetComponentsInChildren<Renderer>()) {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // Viewmodels must never be clipped by geometry the player is standing against.
            r.material.renderQueue = 3100;
        }
        ModelCache.StripColliders(obj);

        if (key == "energySword") AddBladeGlow(holder.transform, w, prefab == null);
    }

    void AddBladeGlow(Transform holder, WeaponDef w, bool placeholder) {
        if (!placeholder) return;
        var blade = Prim.Create(PrimKind.Cube, "Blade", holder);
        blade.transform.localScale = new Vector3(0.05f, 0.16f, 0.7f);
        blade.transform.localPosition = new Vector3(0f, 0.02f, 0.35f);
        blade.GetComponent<Renderer>().material = Fx.AdditiveTinted(new Color(0.44f, 0.82f, 1f) * 3.5f);
    }

    void SetVisible(string key) {
        foreach (var kv in _viewmodels) kv.Value.SetActive(kv.Key == key);
    }

    // ------------------------------------------------------------------ state queries

    WeaponState St => _state[Current];

    public string AmmoText() {
        var w = Weapon; var s = St;
        if (w.Kind == WeaponKind.Melee) return "MELEE";
        if (w.UsesHeat) return s.Overheated > 0f ? "OVERHEAT" : "HEAT " + Mathf.RoundToInt(s.Heat * 100f) + "%";
        // v2 shows "32 / INF". Orbitron has no U+221E glyph, and a legacy Text component draws a
        // missing glyph as nothing at all, so the word is used rather than the symbol.
        if (s.Reserve == int.MaxValue) return s.Mag + " / INF";
        return s.Mag + " / " + s.Reserve;
    }

    public float CooldownPct() {
        var w = Weapon; var s = St;
        if (s.Reloading > 0f) return 1f - s.Reloading / w.Reload;
        if (w.UsesHeat) return 1f - s.Heat;
        return _cooldown > 0f ? 1f - _cooldown / w.FireRate : 1f;
    }

    public bool IsReloading => St.Reloading > 0f;
    public float ReloadPct => St.Reloading > 0f ? 1f - St.Reloading / Weapon.Reload : 0f;
    public int Ammo => Weapon.UsesMag ? St.Mag : -1;
    public int MaxAmmo => Weapon.UsesMag ? Weapon.Mag : -1;

    // ------------------------------------------------------------------ actions

    public void SwitchWeapon(string key) {
        if (!Arsenal.All.ContainsKey(key) || key == Current) return;
        Current = key;
        SetVisible(key);
        _cooldown = Mathf.Max(_cooldown, 0.3f);
        _raiseT = 0.3f;
        Zoomed = false;
        Audio?.WeaponSwitch();
    }

    public void CycleWeapon(int dir) {
        int i = System.Array.IndexOf(Arsenal.Order, Current);
        i = (i + dir + Arsenal.Order.Length) % Arsenal.Order.Length;
        SwitchWeapon(Arsenal.Order[i]);
    }

    public void Reload() {
        var w = Weapon; var s = St;
        if (!w.UsesMag || s.Reloading > 0f || s.Mag >= w.Mag || s.Reserve <= 0) return;
        s.Reloading = w.Reload;
        _magDropped = false;
        Audio?.Reload();
    }

    public void AddAmmo(int n) {
        foreach (var k in Arsenal.Order) {
            var w = Arsenal.Get(k);
            var s = _state[k];
            if (w.Reserve >= 0 && s.Reserve != int.MaxValue) s.Reserve = Mathf.Min(s.Reserve + n, 99);
            if (w.UsesHeat) { s.Heat = 0f; s.Overheated = 0f; }
        }
    }

    public void AddGrenade(int n) => Grenades = Mathf.Min(Grenades + n, 6);

    /// <summary>Try to fire. Returns true if a shot went out.</summary>
    public bool Fire(List<Enemy> enemies) {
        var w = Weapon; var s = St;
        if (_cooldown > 0f || s.Reloading > 0f || s.Overheated > 0f || (Stats != null && Stats.Dead)) return false;
        if (w.UsesMag && s.Mag <= 0) { Reload(); return false; }

        _cooldown = w.FireRate * (Stats != null ? Stats.FireRateMultiplier : 1f);
        float dmgMul = Stats != null ? Stats.DamageMultiplier : 1f;

        if (w.UsesMag) s.Mag--;
        if (w.UsesHeat) {
            s.Heat = Mathf.Min(1f, s.Heat + w.HeatPerShot);
            if (s.Heat >= 1f) { s.Overheated = w.OverheatTime; Audio?.ShieldHit(); }
        }
        _recoil = Mathf.Min(0.2f, _recoil + w.Recoil);
        _muzzleLight.color = w.Color;
        _muzzleLight.intensity = w.Kind == WeaponKind.Melee ? 0f : 6f;

        if (_flash != null && w.Kind != WeaponKind.Melee) {
            // At the muzzle in rig space, sized by the weapon: a rocket tube's flash should not be
            // the same size as a rifle's.
            float scale = w.Kind == WeaponKind.Rocket ? 1.8f : w.Kind == WeaponKind.Projectile ? 1.1f : 1f;
            _flash.Show(w.Pos + w.Muzzle, scale, w.Color);
        }
        // Brass, from anything that has a magazine to feed it. The plasma rifle vents rather than
        // ejects, and a rocket launcher has nothing to throw.
        if (w.Kind == WeaponKind.Hitscan)
            ViewFx.EjectCase(_rig.TransformPoint(w.Pos + new Vector3(0.05f, 0.03f, 0.02f)), Cam.transform.rotation);

        Vector3 origin = Cam.transform.position;
        Vector3 dir = Cam.transform.forward;
        Vector3 muzzle = MuzzleWorld();

        switch (w.Kind) {
            case WeaponKind.Hitscan: {
                float spread = w.Spread * (Zoomed ? 0.25f : 1f);
                dir = Scatter(dir, spread);
                float wallDist = Arena.RayWall(origin, dir, w.Range, out var wallPoint, out var wallNormal);
                var hit = RayEnemies(origin, dir, enemies, Mathf.Min(wallDist, w.Range), w, dmgMul);

                Vector3 end = hit.HasValue ? hit.Value.Point : origin + dir * Mathf.Min(wallDist, w.Range);
                Fx?.Tracer(muzzle, end, new Color(1f, 0.88f, 0.63f));

                if (!hit.HasValue && wallDist < w.Range) {
                    Fx?.Sparks(wallPoint, new Color(1f, 0.75f, 0.38f), 5);
                    OnWallHit?.Invoke(wallPoint, wallNormal, "bullet");
                }
                Audio?.LaserRifle();
                if (hit.HasValue) OnHit?.Invoke(hit.Value);
                break;
            }
            case WeaponKind.Projectile: {
                dir = Scatter(dir, 0.02f);
                SpawnProjectile(muzzle, dir, w, dmgMul, "plasma");
                Audio?.PlasmaShot();
                break;
            }
            case WeaponKind.Rocket: {
                SpawnProjectile(muzzle, dir, w, dmgMul, "rocket");
                Audio?.RocketLaunch();
                break;
            }
            case WeaponKind.Melee: {
                _swingT = 0.35f;
                // Halo-style lunge: an enemy inside lunge range in front pulls the player to it.
                var target = MeleeTarget(origin, dir, enemies, w.Lunge, w.Arc);
                if (target != null) { _lungeTarget = target; _lungeTimer = 0.14f; }
                Audio?.LaserSword();
                // Damage lands slightly after the swing starts, so the lunge arrives first.
                _pendingMeleeTime = 0.1f;
                _pendingMeleeDmgMul = dmgMul;
                break;
            }
        }
        return true;
    }

    /// <summary>RMB: rifle zoom toggle; sword heavy overhead (1.8x cooldown, 1.6x damage).</summary>
    public void FireAlt(List<Enemy> enemies) {
        if (Current == "rifle") { Zoomed = !Zoomed; return; }
        if (Current == "energySword" && _cooldown <= 0f) {
            if (Fire(enemies)) {
                _cooldown *= 1.8f;
                _pendingMeleeDmgMul *= 1.6f;
                _swingT = 0.5f;
            }
        }
    }

    public void ThrowGrenade() {
        if (Grenades <= 0 || (Stats != null && Stats.Dead)) return;
        Grenades--;

        Vector3 origin = Cam.transform.position;
        Vector3 dir = Cam.transform.forward;
        dir.y += 0.25f;
        dir.Normalize();

        var go = Prim.Create(PrimKind.Sphere, "Grenade", _projRoot);
        go.transform.position = origin + dir * 0.6f;
        go.transform.localScale = Vector3.one * 0.26f;
        go.GetComponent<Renderer>().material = Fx.AdditiveTinted(new Color(0.2f, 0.32f, 1f) * 1.5f);

        _projectiles.Add(new Projectile {
            T = go.transform, Kind = "grenade", Vel = dir * 19f, Life = 2.2f,
            Damage = 190f * (Stats != null ? Stats.DamageMultiplier : 1f),
            Splash = 6f, Gravity = 16f, Bounce = true,
        });
        Audio?.GrenadeThrow();
    }

    static Vector3 Scatter(Vector3 dir, float spread) {
        dir.x += (Random.value - 0.5f) * spread;
        dir.y += (Random.value - 0.5f) * spread;
        dir.z += (Random.value - 0.5f) * spread;
        return dir.normalized;
    }

    Vector3 MuzzleWorld() {
        var w = Weapon;
        if (_viewmodels.TryGetValue(Current, out var vm) && vm != null)
            return vm.transform.TransformPoint(w.Muzzle);
        return Cam.transform.position + Cam.transform.forward * 0.6f;
    }

    // ------------------------------------------------------------------ hit resolution

    /// <summary>Ray against a vertical capsule standing on `c` with radius r and height h.</summary>
    static float RayCapsule(Vector3 origin, Vector3 dir, Vector3 c, float r, float h) {
        float ox = origin.x - c.x, oz = origin.z - c.z;
        float a = dir.x * dir.x + dir.z * dir.z;
        float b = 2f * (ox * dir.x + oz * dir.z);
        float cc = ox * ox + oz * oz - r * r;
        float t = -1f;

        if (a > 1e-8f) {
            float disc = b * b - 4f * a * cc;
            if (disc >= 0f) {
                float s = Mathf.Sqrt(disc);
                float t0 = (-b - s) / (2f * a), t1 = (-b + s) / (2f * a);
                for (int i = 0; i < 2; i++) {
                    float tt = i == 0 ? t0 : t1;
                    if (tt < 0f) continue;
                    float y = origin.y + dir.y * tt;
                    if (y >= c.y && y <= c.y + h) { t = tt; break; }
                }
            }
        } else if (cc <= 0f && Mathf.Abs(dir.y) > 1e-8f) {
            float ty0 = (c.y - origin.y) / dir.y, ty1 = (c.y + h - origin.y) / dir.y;
            float tt = Mathf.Min(ty0, ty1);
            if (tt >= 0f) t = tt;
        }
        if (t >= 0f) return t;

        // Spherical caps.
        for (int i = 0; i < 2; i++) {
            float cy = i == 0 ? c.y + r : c.y + h - r;
            Vector3 l = new Vector3(c.x, cy, c.z) - origin;
            float tca = Vector3.Dot(l, dir);
            float d2 = l.sqrMagnitude - tca * tca;
            if (d2 > r * r) continue;
            float thc = Mathf.Sqrt(r * r - d2);
            float tt = tca - thc;
            if (tt >= 0f && (t < 0f || tt < t)) t = tt;
        }
        return t;
    }

    /// <summary>
    /// The aim-assist cushion, in metres, for a target this far away.
    ///
    /// It is an *angle*, not a distance: a fixed 0.4 m of slack is generous at 40 m and enormous at
    /// four, which turns into shooting round corners. Half a degree of cushion is about the width
    /// of the crosshair, which is exactly the forgiveness a player expects — a shot that looked
    /// like it was on the target counts as on the target.
    ///
    /// It is capped, because a Wasp at 90 m must still have to be aimed at.
    /// </summary>
    float AimCushion(float dist) {
        float k = GameSettings.Instance != null ? Mathf.Clamp01(GameSettings.Instance.AimAssist) : 0f;
        if (k <= 0f) return 0f;
        // Zooming is the player asking for precision; do not hand them more help on top of it.
        if (Zoomed) k *= 0.4f;
        return Mathf.Min(0.55f, dist * 0.009f) * k;
    }

    HitInfo? RayEnemies(Vector3 origin, Vector3 dir, List<Enemy> enemies, float maxDist,
                        WeaponDef w, float dmgMul) {
        Enemy best = null;
        float bestT = maxDist;

        for (int i = 0; i < enemies.Count; i++) {
            var e = enemies[i];
            if (e == null || e.Dead || !e.Ready) continue;
            // Widen the capsule rather than widening the ray: the ray is what the wall test used,
            // so a fattened ray could reach past a corner the bullet cannot.
            float approx = Vector3.Distance(origin, e.CapsuleBase);
            float r = e.CapsuleRadius + AimCushion(approx);
            float t = RayCapsule(origin, dir, e.CapsuleBase, r, e.CapsuleHeight);
            if (t >= 0f && t < bestT) { bestT = t; best = e; }
        }
        if (best == null) return null;

        Vector3 point = origin + dir * bestT;
        // Headshot: the top 22% of the capsule.
        bool headshot = point.y > best.CapsuleBase.y + best.CapsuleHeight * 0.78f;

        float damage = w.Damage * dmgMul;
        if (headshot) damage *= w.Headshot;
        if (best.Shield > 0f) damage *= w.ShieldMul;

        return new HitInfo {
            Enemy = best, Damage = Mathf.Round(damage), Point = point, From = origin,
            Headshot = headshot, WeaponKey = w.Key, Knockback = 0f,
        };
    }

    Enemy MeleeTarget(Vector3 origin, Vector3 dir, List<Enemy> enemies, float range, float arc) {
        Enemy best = null;
        float bestD = range;
        Vector3 flatDir = new Vector3(dir.x, 0f, dir.z).normalized;

        for (int i = 0; i < enemies.Count; i++) {
            var e = enemies[i];
            if (e == null || e.Dead || !e.Ready) continue;
            Vector3 to = e.transform.position - origin; to.y = 0f;
            float d = to.magnitude - e.Data.Radius;
            if (d > bestD) continue;
            if (Vector3.Dot(to.normalized, flatDir) < Mathf.Cos(arc)) continue;
            bestD = d; best = e;
        }
        return best;
    }

    void ApplyMelee(List<Enemy> enemies, float dmgMul) {
        var w = Arsenal.Get("energySword");
        Vector3 origin = Cam.transform.position;
        Vector3 flatDir = new Vector3(Cam.transform.forward.x, 0f, Cam.transform.forward.z).normalized;

        for (int i = 0; i < enemies.Count; i++) {
            var e = enemies[i];
            if (e == null || e.Dead || !e.Ready) continue;
            Vector3 to = e.transform.position - origin; to.y = 0f;
            float d = to.magnitude - e.Data.Radius;
            if (d > w.Range) continue;
            if (Vector3.Dot(to.normalized, flatDir) < Mathf.Cos(w.Arc)) continue;

            float damage = w.Damage * dmgMul;
            if (e.Shield > 0f) damage *= w.ShieldMul;
            var point = e.transform.position + Vector3.up * (e.Data.Height * 0.6f);

            OnHit?.Invoke(new HitInfo {
                Enemy = e, Damage = Mathf.Round(damage), Point = point, From = origin,
                Headshot = false, WeaponKey = "energySword", Knockback = 6f,
            });
        }
    }

    void SpawnProjectile(Vector3 from, Vector3 dir, WeaponDef w, float dmgMul, string kind) {
        var go = Prim.Create(PrimKind.Capsule, kind, _projRoot);
        go.transform.position = from;
        go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = kind == "rocket" ? new Vector3(0.2f, 0.35f, 0.2f) : new Vector3(0.14f, 0.27f, 0.14f);
        go.GetComponent<Renderer>().material = Fx.AdditiveTinted(
            kind == "rocket" ? new Color(1f, 0.63f, 0.25f) * 2f : new Color(0.25f, 0.63f, 1f) * 4f);

        _projectiles.Add(new Projectile {
            T = go.transform, Kind = kind, Vel = dir * w.Speed, Life = w.Range / w.Speed,
            Damage = w.Damage * dmgMul, Splash = w.Splash,
        });
    }

    // ------------------------------------------------------------------ per-frame

    public void Tick(float dt, List<Enemy> enemies, Vector3 playerPos) {
        var s = St;
        var w = Weapon;

        if (_cooldown > 0f) _cooldown -= dt;
        if (s.Overheated > 0f) s.Overheated -= dt;
        if (w.UsesHeat && s.Overheated <= 0f) s.Heat = Mathf.Max(0f, s.Heat - w.CooldownRate * dt);

        if (s.Reloading > 0f) {
            s.Reloading -= dt;
            if (s.Reloading <= 0f) {
                int need = w.Mag - s.Mag;
                int take = s.Reserve == int.MaxValue ? need : Mathf.Min(need, s.Reserve);
                s.Mag += take;
                if (s.Reserve != int.MaxValue) s.Reserve -= take;
            }
        }

        // Melee lunge then damage.
        if (_lungeTimer > 0f) {
            _lungeTimer -= dt;
            if (_lungeTarget != null && !_lungeTarget.Dead && Controller != null) {
                Vector3 to = _lungeTarget.transform.position - Controller.transform.position;
                to.y = 0f;
                float dist = to.magnitude;
                if (dist > _lungeTarget.Data.Radius + 1.2f) {
                    Controller.transform.position += to.normalized * Mathf.Min(dist - 1.2f, 60f * dt);
                }
            }
            if (_lungeTimer <= 0f) _lungeTarget = null;
        }
        if (_pendingMeleeTime >= 0f) {
            _pendingMeleeTime -= dt;
            if (_pendingMeleeTime <= 0f) {
                _pendingMeleeTime = -1f;
                ApplyMelee(enemies, _pendingMeleeDmgMul);
            }
        }

        TickProjectiles(dt, enemies);
        TickRig(dt);
    }

    void TickProjectiles(float dt, List<Enemy> enemies) {
        for (int i = _projectiles.Count - 1; i >= 0; i--) {
            var p = _projectiles[i];
            p.Life -= dt;
            if (p.Gravity > 0f) p.Vel.y -= p.Gravity * dt;

            Vector3 prev = p.T.position;
            p.T.position += p.Vel * dt;
            if (p.Vel.sqrMagnitude > 1e-4f)
                p.T.rotation = Quaternion.LookRotation(p.Vel.normalized, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);

            bool dead = p.Life <= 0f;
            Vector3 pos = p.T.position;

            // Ground / wall
            if (!dead && pos.y <= 0.05f && !p.Bounce) {
                OnWallHit?.Invoke(new Vector3(pos.x, 0.02f, pos.z), Vector3.up,
                                  p.Kind == "rocket" ? "blast" : "plasma");
            }
            if (!dead && pos.y <= 0.05f) {
                if (p.Bounce && p.Life > 0.15f) {
                    pos.y = 0.05f;
                    p.Vel.y = -p.Vel.y * 0.4f;
                    p.Vel.x *= 0.7f; p.Vel.z *= 0.7f;
                    p.T.position = pos;
                } else dead = true;
            }
            if (!dead && Arena != null && Arena.PointInSolid(pos)) {
                if (p.Bounce) { p.T.position = prev; p.Vel = Vector3.Reflect(p.Vel, Vector3.up) * 0.4f; }
                else {
                    dead = true;
                    // Mark the wall the plasma or rocket actually hit.
                    var dir = p.Vel.sqrMagnitude > 1e-4f ? p.Vel.normalized : Vector3.forward;
                    Arena.RayWall(prev, dir, (pos - prev).magnitude + 1f, out var wp, out var wn);
                    OnWallHit?.Invoke(wp, wn, p.Kind == "rocket" ? "blast" : "plasma");
                }
            }

            // Direct enemy contact (rockets and plasma only).
            //
            // Swept along the step, not tested at the end of it. A plasma bolt travels 70 m/s, so
            // at 60 fps it jumps 1.2 m per frame - comfortably more than a Gnat is wide - and a
            // point test at the new position simply misses every enemy the bolt passed clean
            // through. That is most of "I hit it and nothing happened".
            if (!dead && p.Kind != "grenade") {
                var step = pos - prev;
                float stepLen = step.magnitude;
                var stepDir = stepLen > 1e-5f ? step / stepLen : Vector3.forward;
                for (int e = 0; e < enemies.Count; e++) {
                    var en = enemies[e];
                    if (en == null || en.Dead || !en.Ready) continue;
                    float r = en.CapsuleRadius + 0.2f + AimCushion(Vector3.Distance(prev, en.CapsuleBase));
                    float hitT = RayCapsule(prev, stepDir, en.CapsuleBase, r, en.CapsuleHeight);
                    if (hitT >= 0f && hitT <= stepLen + 0.05f) {
                        if (p.Splash <= 0f) {
                            float dmg = p.Damage;
                            var wdef = Arsenal.Get(p.Kind == "rocket" ? "rocketLauncher" : "plasmaRifle");
                            if (en.Shield > 0f) dmg *= wdef.ShieldMul;
                            OnHit?.Invoke(new HitInfo {
                                Enemy = en, Damage = Mathf.Round(dmg), Point = pos, From = prev,
                                WeaponKey = wdef.Key,
                            });
                        }
                        dead = true;
                        break;
                    }
                }
            }

            if (dead) {
                if (p.Splash > 0f) Detonate(pos, p, enemies);
                else Fx?.Impact(pos, new Color(0.25f, 0.63f, 1f), 0.6f);
                Destroy(p.T.gameObject);
                _projectiles.RemoveAt(i);
            }
        }
    }

    void Detonate(Vector3 pos, Projectile p, List<Enemy> enemies) {
        float radius = p.Splash * (Stats != null ? Stats.ExplosionRadiusMultiplier : 1f);
        Fx?.Explosion(pos, radius / 6f);
        Audio?.Explosion();
        OnExplosion?.Invoke(pos, radius, p.Kind == "rocket");

        for (int e = 0; e < enemies.Count; e++) {
            var en = enemies[e];
            if (en == null || en.Dead || !en.Ready) continue;
            float d = Vector3.Distance(en.transform.position + Vector3.up * (en.Data.Height * 0.4f), pos);
            if (d > radius) continue;
            float falloff = 1f - d / radius;
            OnHit?.Invoke(new HitInfo {
                Enemy = en, Damage = Mathf.Round(p.Damage * falloff), Point = pos, From = pos,
                Explosive = true, Knockback = 8f * falloff,
                WeaponKey = p.Kind == "rocket" ? "rocketLauncher" : "grenade",
            });
        }
    }

    /// <summary>Viewmodel sway, bob, recoil kick, swing and weapon raise.</summary>
    void TickRig(float dt) {
        if (_rig == null) return;

        _recoil = Mathf.Lerp(_recoil, 0f, 10f * dt);
        _swayX = Mathf.Lerp(_swayX, 0f, 6f * dt);
        _swayY = Mathf.Lerp(_swayY, 0f, 6f * dt);
        if (_raiseT > 0f) _raiseT -= dt;
        if (_swingT > 0f) _swingT -= dt;

        float moveSpeed = Controller != null ? Controller.HorizontalSpeed : 0f;
        if (moveSpeed > 0.5f && Controller != null && Controller.OnGround)
            _bobT += dt * (Controller.Sprinting ? 11f : 7.5f);

        float bobX = Mathf.Sin(_bobT) * 0.012f * Mathf.Clamp01(moveSpeed / 12f);
        float bobY = Mathf.Abs(Mathf.Cos(_bobT)) * 0.010f * Mathf.Clamp01(moveSpeed / 12f);

        float raise = _raiseT > 0f ? -(_raiseT / 0.3f) * 0.25f : 0f;
        float swing = _swingT > 0f ? Mathf.Sin((1f - _swingT / 0.35f) * Mathf.PI) : 0f;

        // Zoom pulls the rifle toward the centre of the screen.
        float zoom = (Zoomed && Current == "rifle") ? 1f : 0f;
        // Reload pose: the weapon comes down and cants inward so the magazine well is actually in
        // view and the off hand has somewhere to work. Without it the hands act out a reload on a
        // gun that has not moved, which reads as the arms glitching rather than as a reload.
        var reloadPos = Vector3.zero;
        var reloadRot = Vector3.zero;
        float rt = ReloadPct;
        if (IsReloading) {
            float bell = Mathf.Sin(Mathf.Clamp01(rt) * Mathf.PI);
            // Mostly roll, barely any drop. The first version dipped the weapon 7 cm and it left
            // the bottom of the frame completely for half the reload - the animation was playing
            // perfectly somewhere the player could not see it. Canting the receiver toward the
            // camera shows the magazine well, which is the whole point, and keeps the gun on screen.
            if (Current == "rocketLauncher") {
                // Tipped back over the shoulder, the way a tube is actually loaded.
                reloadPos = new Vector3(0.01f, 0.01f, -0.07f) * bell;
                reloadRot = new Vector3(-16f, 9f, -6f) * bell;
            } else {
                // Nose UP, not down. The rig rotates about the camera, so the weapon sits about
                // 0.6 m out on the end of that lever: nine degrees of nose-down pitch drops it
                // eight centimetres, which is most of the way off the bottom of the frame before
                // the roll has even been applied.
                reloadPos = new Vector3(0.01f, 0.015f, -0.01f) * bell;
                reloadRot = new Vector3(-6f, 10f, -20f) * bell;
            }
            if (!_magDropped && rt >= ViewHands.MagDropAt) {
                _magDropped = true;
                DropMagazine();
            }
        }

        var basePos = new Vector3(_swayX + bobX, _swayY + bobY + raise - _recoil * 0.4f, -_recoil * 0.5f)
                    + reloadPos;
        basePos.x = Mathf.Lerp(basePos.x, basePos.x - Arsenal.Get("rifle").Pos.x + 0.02f, zoom);
        basePos.y = Mathf.Lerp(basePos.y, basePos.y + 0.10f, zoom);

        _rig.localPosition = Vector3.Lerp(_rig.localPosition, basePos, 1f - Mathf.Exp(-18f * dt));
        _rig.localRotation = Quaternion.Slerp(_rig.localRotation,
            Quaternion.Euler(-_recoil * 90f - swing * 45f + reloadRot.x,
                             _swayX * 200f + reloadRot.y,
                             swing * 20f + reloadRot.z),
            1f - Mathf.Exp(-18f * dt));

        // Hands last, so they read the rig after this frame's pose has been written to it.
        if (_hands != null) {
            _viewmodels.TryGetValue(Current, out var vm);
            _hands.Weapon = vm != null ? vm.transform : null;
            _hands.WeaponKey = Current;
            _hands.Reloading = IsReloading;
            _hands.ReloadT = rt;
            _hands.Hidden = Stats != null && Stats.Dead;
            _hands.Tick(dt);
        }

        if (_muzzleLight.intensity > 0f)
            _muzzleLight.intensity = Mathf.Max(0f, _muzzleLight.intensity - 40f * dt);

        // Field of view breathes with the zoom.
        if (Cam != null) {
            float wantFov = zoom > 0.5f ? BaseFov * 0.6f : BaseFov;
            Cam.fieldOfView = Mathf.Lerp(Cam.fieldOfView, wantFov, 1f - Mathf.Exp(-12f * dt));
        }
    }

    /// <summary>
    /// Throw the spent magazine clear.
    ///
    /// It is a box, it falls, and it is gone in a second and a half - but it is the only part of
    /// the reload that leaves the viewmodel, and it is what tells the player the reload has
    /// actually started rather than that the gun has stopped working.
    /// </summary>
    void DropMagazine() {
        if (_rig == null || Cam == null) return;
        var w = Weapon;
        var go = Prim.Create(PrimKind.Cube, "SpentMag");
        go.transform.position = _rig.TransformPoint(w.Pos + new Vector3(-0.03f, -0.12f, 0.02f));
        go.transform.rotation = Cam.transform.rotation;
        go.transform.localScale = Current == "rocketLauncher"
            ? new Vector3(0.09f, 0.09f, 0.32f)
            : new Vector3(0.05f, 0.17f, 0.10f);

        var mat = Prim.Lit();
        if (mat != null) {
            var c = new Color(0.075f, 0.080f, 0.090f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            mat.color = c;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.25f);
        }
        var r = go.GetComponent<Renderer>();
        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var f = go.AddComponent<FallingDebris>();
        f.Velocity = Cam.transform.TransformDirection(new Vector3(-0.6f, -0.2f, 0.25f));
        f.Spin = new Vector3(Random.Range(-260f, 260f), Random.Range(-260f, 260f), Random.Range(-260f, 260f));
    }

    public void AddSway(float dx, float dy) {
        _swayX = Mathf.Clamp(_swayX - dx * 0.00025f, -0.03f, 0.03f);
        _swayY = Mathf.Clamp(_swayY - dy * 0.00025f, -0.03f, 0.03f);
    }

    public void ClearProjectiles() {
        foreach (var p in _projectiles) if (p.T != null) Destroy(p.T.gameObject);
        _projectiles.Clear();
    }

    public void ResetAll() {
        ClearProjectiles();
        foreach (var k in Arsenal.Order) {
            var w = Arsenal.Get(k);
            var s = _state[k];
            s.Mag = w.UsesMag ? w.Mag : int.MaxValue;
            s.Reserve = w.Reserve >= 0 ? w.Reserve : int.MaxValue;
            s.Heat = 0f; s.Overheated = 0f; s.Reloading = 0f;
        }
        Grenades = 3;
        Zoomed = false;
        _cooldown = 0f;
        SwitchWeapon("rifle");
        SetVisible("rifle");
    }
}

}
