using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>Everything an Enemy needs from the rest of the game, handed in at spawn.</summary>
public class EnemyContext {
    public Arena Arena;
    public List<Enemy> Enemies;
    public EnemyProjectiles Bolts;
    public Fx Fx;
    public GameAudio Audio;
    public System.Action<Enemy, float> OnMelee;                      // enemy melee reached the player
    public System.Func<string, Vector3, bool, Enemy> Spawn;          // (type, pos, dropIn)
    public System.Func<Vector3> PlayerPos;
}

/// <summary>
/// One alien. AI, shields, animation state machine and steering, ported one-for-one from v2's
/// js/enemies.js. The per-role switch below is the whole behaviour model - there is no
/// behaviour tree and no NavMesh, and adding either would change how the game plays.
/// </summary>
public class Enemy : MonoBehaviour {

    public EnemyType Data { get; private set; }
    public EnemyContext Ctx { get; private set; }

    public bool Elite;
    public bool IsBoss => Data != null && Data.IsBoss;
    public bool Dead { get; private set; }
    public bool Ready { get; private set; }
    public bool Aggressive;

    public float Hp, MaxHp;
    public float Shield, MaxShield;
    public float FrontShield, FrontShieldMax;

    float _shieldTimer, _shieldFlash;
    float _speedMul = 1f, _dmgMul = 1f;
    float _attackCooldown, _burstTimer;
    int _burstLeft;
    float _strafeTimer, _panicTimer, _retreatTimer, _stunTimer;
    int _strafeDir;
    float _hitFlash, _deathTimer, _stateTime, _spawnTimer;
    float _hoverPhase;
    float _losTimer, _noLos;
    float _frontRegen;
    float _ghostTimer, _stuckAcc, _stuckX, _stuckZ, _pathAcc;
    float _pendingMelee = -1f;
    bool _diving, _crashed;
    float _yaw;

    Vector3 _detourDir; float _detourTime;
    Vector3 _knockback;

    GameObject _model;
    Vector3 _modelBaseScale = Vector3.one;
    EnemyAnimator _anim;
    Transform _shieldFx;
    readonly List<Material> _materials = new List<Material>();
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    // ---- diagnostics (read by the AI/animation audit) ----
    public bool HasRig => _anim != null && _anim.Valid;
    public string AnimClip => _anim != null ? _anim.CurrentClip : null;
    public string[] AnimClips => _anim != null ? _anim.ClipNames : new string[0];
    public int ShotsFired { get; private set; }
    public bool Hunting => Aggressive || _noLos > 2.5f;

    public Vector3 Position => transform.position;
    public float Yaw => _yaw;
    /// <summary>Bottom-centre of the hit capsule. Aerial units are offset by their hover height.</summary>
    public Vector3 CapsuleBase => transform.position;
    public float CapsuleRadius => Data.Radius * (Data.HoverHeight > 0f ? 1.6f : 1.15f);
    public float CapsuleHeight => Data.Height;

    // ------------------------------------------------------------------ setup

    public void Init(EnemyType type, Vector3 position, EnemyContext ctx, bool elite,
                     float hpMul, float speedMul, float dmgMul) {
        Data = type; Ctx = ctx; Elite = elite;

        float hm = hpMul * (elite ? 1.8f : 1f);
        MaxHp = Mathf.Round(type.Hp * hm);
        Hp = MaxHp;
        MaxShield = Mathf.Round(type.Shield * hpMul * (elite ? 1.5f : 1f));
        Shield = MaxShield;
        FrontShield = type.FrontShield > 0f ? type.FrontShield * hpMul : 0f;
        FrontShieldMax = FrontShield;

        _speedMul = speedMul * (elite ? 1.15f : 1f);
        _dmgMul = dmgMul * (elite ? 1.4f : 1f);

        _attackCooldown = 0.6f + Random.value * type.AttackRate;
        _strafeDir = Random.value < 0.5f ? -1 : 1;
        _strafeTimer = 1f + Random.value * 2f;
        _hoverPhase = Random.value * Mathf.PI * 2f;
        _spawnTimer = type.SpawnEvery;

        transform.position = new Vector3(position.x, type.HoverHeight > 0f ? type.HoverHeight : 0f, position.z);
        name = type.Name + (elite ? " (elite)" : "");

        BuildModel();
        BuildShieldFx();
        Ready = true;
    }

    void BuildModel() {
        var prefab = ModelCache.Load(Data.ModelPath);
        if (prefab == null) { BuildPlaceholder(); return; }

        _model = Instantiate(prefab, transform);
        _model.transform.localPosition = Vector3.zero;
        _model.transform.localRotation = Quaternion.Euler(0f, ModelCache.ForwardYawOffset, 0f);

        ModelCache.NormalizeHeight(_model, Data.Height);

        // Clone materials per instance so hit-flash and the death fade do not bleed across a squad.
        foreach (var r in _model.GetComponentsInChildren<Renderer>()) {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = false;
            var mats = r.materials;                     // .materials already instantiates copies
            for (int i = 0; i < mats.Length; i++) {
                if (mats[i] == null) continue;
                _materials.Add(mats[i]);
                if (Elite) {
                    mats[i].EnableKeyword("_EMISSION");
                    mats[i].SetColor(EmissionColor, new Color(1f, 0.67f, 0f) * 0.35f);
                }
            }
            r.materials = mats;
        }

        _modelBaseScale = _model.transform.localScale;
        _anim = new EnemyAnimator(_model);
        _anim.Play(Clip.Idle, 0f);
    }

    /// <summary>A tinted capsule, so a missing asset never stops the game.</summary>
    void BuildPlaceholder() {
        var go = Prim.Create(PrimKind.Capsule, "Placeholder", transform);
        go.transform.localScale = new Vector3(Data.Radius * 2f, Data.Height * 0.5f, Data.Radius * 2f);
        go.transform.localPosition = new Vector3(0f, Data.Height * 0.5f, 0f);
        var mat = Fx.UnlitTinted(Data.Color);
        go.GetComponent<Renderer>().material = mat;
        _materials.Add(mat);
        _model = go;
        _anim = new EnemyAnimator(null);
    }

    void BuildShieldFx() {
        if (MaxShield <= 0f) return;
        var go = Prim.Create(PrimKind.Capsule, "ShieldFX", transform);
        go.transform.localScale = new Vector3(Data.Radius * 2.7f, Data.Height * 0.5f, Data.Radius * 2.7f);
        go.transform.localPosition = new Vector3(0f, Data.Height * 0.5f, 0f);
        var r = go.GetComponent<Renderer>();
        r.material = Fx.AdditiveTinted(new Color(0.4f, 0.67f, 1f));
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _shieldFx = go.transform;
        SetShieldAlpha(0f);
    }

    void SetShieldAlpha(float a) {
        if (_shieldFx == null) return;
        var r = _shieldFx.GetComponent<Renderer>();
        var c = r.material.color; c.a = a;
        r.material.color = c;
        _shieldFx.gameObject.SetActive(a > 0.01f);
    }

    // ------------------------------------------------------------------ damage

    public struct DamageResult { public bool Killed, Absorbed; }

    /// <summary>
    /// `from` is the world position the hit came from, used for the front-shield arc test.
    /// Energy shields eat damage first and restart the recharge delay; a physical front shield
    /// blocks the hit outright when it lands inside the facing cone.
    /// </summary>
    public DamageResult TakeDamage(float amount, Vector3 from, Vector3 point,
                                   bool explosive = false, float knockback = 0f, bool ignoreFrontShield = false) {
        var res = new DamageResult();
        if (Dead) return res;

        if (FrontShield > 0f && !ignoreFrontShield) {
            Vector3 toHit = from - transform.position; toHit.y = 0f;
            if (toHit.sqrMagnitude > 1e-6f) {
                toHit.Normalize();
                Vector3 facing = new Vector3(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(toHit, facing), -1f, 1f));
                if (ang < Data.FrontShieldArc) {
                    FrontShield -= amount * (explosive ? 1.5f : 1f);
                    _hitFlash = 0.1f;
                    Ctx.Fx?.Sparks(point, new Color(0.69f, 0.5f, 1f), 8);
                    Ctx.Audio?.ShieldHit();
                    if (FrontShield <= 0f) { FrontShield = 0f; OnFrontShieldBroken(); }
                    res.Absorbed = true;
                    return res;
                }
            }
        }

        _shieldTimer = Data.ShieldDelay;
        if (Shield > 0f) {
            float taken = Mathf.Min(Shield, amount);
            Shield -= taken; amount -= taken;
            _shieldFlash = 1f;
            Ctx.Audio?.ShieldHit();
            if (Shield <= 0f) OnShieldPop();
            if (amount <= 0f) { res.Absorbed = true; return res; }
        }

        Hp -= amount;
        _hitFlash = 0.12f;

        if (knockback > 0f) {
            Vector3 away = transform.position - from; away.y = 0f;
            if (away.sqrMagnitude > 1e-6f)
                _knockback += away.normalized * (knockback / Mathf.Max(1f, Data.Height));
        }

        if (Hp <= 0f) { Die(); res.Killed = true; return res; }

        if (Data.Role != EnemyRole.Tank && Data.Role != EnemyRole.Boss
            && Random.value < 0.35f && !_anim.OneShotPlaying) {
            _anim.PlayOnce(Clip.Hit);
            _stunTimer = 0.25f;
        }
        if (Data.Panics && Hp < MaxHp * 0.4f) _panicTimer = Mathf.Max(_panicTimer, 3f);
        return res;
    }

    void OnShieldPop() {
        Ctx.Fx?.ShieldBurst(transform.position, Data.Height, new Color(0.4f, 0.67f, 1f));
        // Commanders duck out to recharge. That retreat is the window the player kills them in.
        if (Data.Role == EnemyRole.Commander) _retreatTimer = 2.2f;
    }

    void OnFrontShieldBroken() {
        Ctx.Fx?.ShieldBurst(transform.position, Data.Height, new Color(0.69f, 0.5f, 1f));
        _stunTimer = 0.6f;
        _anim.PlayOnce(Clip.Hit);
    }

    void Die() {
        Dead = true;
        Shield = 0f;
        SetShieldAlpha(0f);
        Ctx.Audio?.AlienDeath();
        float dur = _anim.PlayOnce(Clip.Death, null);
        _deathTimer = Mathf.Max(1.6f, dur + 0.6f);

        // Nearby Gnats panic when their commander falls.
        if (Data.Role == EnemyRole.Commander && Ctx.Enemies != null) {
            foreach (var e in Ctx.Enemies) {
                if (e != null && e != this && !e.Dead && e.Data.Panics
                    && Vector3.Distance(e.transform.position, transform.position) < 25f)
                    e._panicTimer = 5f;
            }
        }
    }

    // ------------------------------------------------------------------ steering

    void TurnToward(float want, float dt, float rate) {
        float d = Mathf.DeltaAngle(_yaw * Mathf.Rad2Deg, want * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        _yaw += d * Mathf.Min(1f, rate * dt);
        transform.rotation = Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg, 0f);
    }

    void Face(Vector3 target, float dt, float rate = 8f) {
        Vector3 d = target - transform.position; d.y = 0f;
        if (d.sqrMagnitude < 1e-4f) return;
        TurnToward(Mathf.Atan2(d.x, d.z), dt, rate);
    }

    /// <summary>
    /// Face the direction actually being travelled, so the walk/run stride matches the motion
    /// instead of moonwalking while strafing. Callers snap back to Face(player) for the instant
    /// of a shot or a swing so it still lands where aimed.
    /// </summary>
    void FaceHeading(Vector3 dir, float dt, float rate = 9f) {
        if (dir.sqrMagnitude < 1e-4f) return;
        TurnToward(Mathf.Atan2(dir.x, dir.z), dt, rate);
    }

    /// <summary>Move along a normalized XZ direction, sliding along colliders.</summary>
    void Move(Vector3 dir, float speed, float dt) {
        var p = transform.position;
        float step = speed * dt;

        if (_detourTime > 0f) { _detourTime -= dt; dir = _detourDir; }

        var boxes = Ctx.Arena.Boxes;
        float r = Data.Radius;
        bool ghost = _ghostTimer > 0f;

        // Unstick: a bad spawn or a knockback can leave us inside a box. Push out the short way.
        // While ghosting, soft props are ignored here too - otherwise this pass shoves the unit
        // out every frame, the AI walks straight back in, and it oscillates in place forever
        // while its odometer climbs. That is exactly what the audit caught.
        for (int i = 0; i < boxes.Count; i++) {
            var b = boxes[i];
            if (ghost && b.soft) continue;
            if (b.max.y < 0.2f || p.x <= b.min.x || p.x >= b.max.x || p.z <= b.min.z || p.z >= b.max.z) continue;
            float[] ex = { b.min.x - p.x - r, b.max.x - p.x + r, b.min.z - p.z - r, b.max.z - p.z + r };
            int k = 0;
            for (int j = 1; j < 4; j++) if (Mathf.Abs(ex[j]) < Mathf.Abs(ex[k])) k = j;
            if (k < 2) p.x += ex[k]; else p.z += ex[k];
            break;
        }

        float nx = p.x + dir.x * step, nz = p.z + dir.z * step;
        bool okX = true, okZ = true;

        for (int i = 0; i < boxes.Count; i++) {
            var b = boxes[i];
            if (b.max.y < 0.2f || (ghost && b.soft)) continue;
            if (b.OverlapsXZ(nx, p.z, r)) okX = false;
            if (b.OverlapsXZ(p.x, nz, r)) okZ = false;
            if (!okX && !okZ) break;
        }
        if (okX) p.x = nx;
        if (okZ) p.z = nz;

        if (!okX && !okZ) {
            // Boxed in on both axes: try sliding sideways, whichever way is open.
            bool slid = false;
            for (int s = 0; s < 2 && !slid; s++) {
                float sgn = s == 0 ? 1f : -1f;
                float sx = p.x + -dir.z * sgn * step, sz = p.z + dir.x * sgn * step;
                bool free = true;
                for (int i = 0; i < boxes.Count; i++) {
                    var b = boxes[i];
                    if (b.max.y >= 0.2f && b.OverlapsXZ(sx, sz, r)) { free = false; break; }
                }
                if (free) { p.x = sx; p.z = sz; slid = true; }
            }
            if (!slid) {
                // Cornered. Commit to a detour for a moment - re-deciding every frame oscillates
                // in place against a box corner and the unit never gets out.
                float sgn = Random.value < 0.5f ? 1f : -1f;
                _detourDir = new Vector3(-dir.z * sgn, 0f, dir.x * sgn);
                _detourTime = 0.9f;
                p.x -= dir.x * step; p.z -= dir.z * step;
            }
        }

        // Stuck detector. Two ways to be stuck, and the second is the one that was being missed:
        //   - barely moving at all
        //   - covering ground fast while getting nowhere, i.e. oscillating against a collider
        _pathAcc += step;
        _stuckAcc += dt;
        if (_stuckAcc > 1f) {
            float net = Mathf.Sqrt((p.x - _stuckX) * (p.x - _stuckX) + (p.z - _stuckZ) * (p.z - _stuckZ));
            // Travelling more than three times the net displacement is not progress.
            bool oscillating = _pathAcc > 1.5f && net < _pathAcc * 0.33f;
            if (net < 0.6f || oscillating) _ghostTimer = 2.5f;
            _stuckX = p.x; _stuckZ = p.z; _stuckAcc = 0f; _pathAcc = 0f;
        }
        if (_ghostTimer > 0f) _ghostTimer -= dt;

        // Keep inside the arena.
        float lim = Ctx.Arena.Radius;
        float d2 = p.x * p.x + p.z * p.z;
        if (d2 > lim * lim) { float s = lim / Mathf.Sqrt(d2); p.x *= s; p.z *= s; }

        // Ground units follow the ground height (the pier, container tops).
        if (Data.HoverHeight <= 0f) p.y = GroundAt(p.x, p.z);

        transform.position = p;
    }

    /// <summary>
    /// Walking height at a point. Only surfaces marked walkable count: every low prop collider
    /// used to qualify, so a Gnat crossing a bench or a parked car was lifted on top of it and
    /// appeared to hover over the street.
    /// </summary>
    float GroundAt(float x, float z) {
        float best = 0f;
        var boxes = Ctx.Arena.Boxes;
        for (int i = 0; i < boxes.Count; i++) {
            var b = boxes[i];
            if (!b.walkable) continue;
            if (x > b.min.x && x < b.max.x && z > b.min.z && z < b.max.z && b.max.y > best) best = b.max.y;
        }
        return best;
    }

    /// <summary>Push apart from same-layer neighbours so packs do not interpenetrate.</summary>
    void Separate(float dt) {
        var list = Ctx.Enemies;
        if (list == null) return;
        var p = transform.position;
        for (int i = 0; i < list.Count; i++) {
            var o = list[i];
            if (o == null || o == this || o.Dead) continue;
            if ((o.Data.HoverHeight > 0f) != (Data.HoverHeight > 0f)) continue;   // air ignores ground
            float dx = p.x - o.transform.position.x, dz = p.z - o.transform.position.z;
            float d2 = dx * dx + dz * dz;
            float minD = Data.Radius + o.Data.Radius + 0.3f;
            if (d2 < minD * minD && d2 > 1e-4f) {
                float d = Mathf.Sqrt(d2), push = (minD - d) * 2.5f * dt;
                p.x += dx / d * push; p.z += dz / d * push;
            }
        }
        transform.position = p;
    }

    // ------------------------------------------------------------------ shooting

    void Shoot(Vector3 playerPos, float dmg, float speed, float spread, float scale = 1f,
               Vector3 lead = default, float splash = 0f) {
        var facing = new Vector3(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
        Vector3 from = transform.position + facing * (Data.Radius + 0.2f);
        from.y += Data.HoverHeight > 0f ? 0f : Data.Height * 0.62f;

        Vector3 target = playerPos;
        target.y -= 0.35f;
        target += lead * 0.25f;

        Vector3 dir = (target - from).normalized;
        float s = spread * (Elite ? 0.5f : 1f);
        dir.x += (Random.value - 0.5f) * s;
        dir.y += (Random.value - 0.5f) * s * 0.5f;
        dir.z += (Random.value - 0.5f) * s;
        dir.Normalize();

        Ctx.Bolts.Fire(from, dir, speed, Data.BoltColor, Mathf.Round(dmg * _dmgMul), splash, 0f, scale);
        ShotsFired++;
        Ctx.Audio?.AlienShoot();
        if (_anim.Has(Clip.Shoot) && !_anim.OneShotPlaying) _anim.PlayOnce(Clip.Shoot);
        Ctx.Fx?.MuzzleFlash(from, Data.BoltColor);
    }

    // ------------------------------------------------------------------ update

    /// <summary>Returns true when the corpse should be removed.</summary>
    public bool Tick(float dt, Vector3 playerPos, Vector3 playerVel) {
        if (_hitFlash > 0f) {
            _hitFlash -= dt;
            float k = _hitFlash > 0f ? 1f : 0f;
            for (int i = 0; i < _materials.Count; i++) {
                var m = _materials[i];
                if (m == null) continue;
                m.EnableKeyword("_EMISSION");
                m.SetColor(EmissionColor, k > 0f ? new Color(1f, 0.6f, 0.3f) * 0.9f
                            : (Elite ? new Color(1f, 0.67f, 0f) * 0.35f : Color.black));
            }
        }

        if (Dead) return TickDead(dt);
        if (!Ready) return false;

        _anim.Tick(dt);
        _stateTime += dt;

        // Energy shield recharge
        if (MaxShield > 0f) {
            if (_shieldTimer > 0f) _shieldTimer -= dt;
            else if (Shield < MaxShield) Shield = Mathf.Min(MaxShield, Shield + Data.ShieldRegen * dt);
            _shieldFlash = Mathf.Max(0f, _shieldFlash - dt * 3f);
            float baseA = Shield > 0f ? 0.012f + 0.012f * Mathf.Sin(_stateTime * 6f) : 0f;
            SetShieldAlpha(baseA + _shieldFlash * 0.35f);
        }

        // Knockback decay
        if (_knockback.sqrMagnitude > 1e-4f) {
            Move(_knockback.normalized, _knockback.magnitude, dt);
            _knockback *= Mathf.Max(0f, 1f - 8f * dt);
        }

        if (_stunTimer > 0f) { _stunTimer -= dt; return false; }
        if (_retreatTimer > 0f) _retreatTimer -= dt;
        if (_panicTimer > 0f) _panicTimer -= dt;
        if (_attackCooldown > 0f) _attackCooldown -= dt;

        Vector3 flat = playerPos - transform.position; flat.y = 0f;
        float dist = flat.magnitude;
        Vector3 toPlayer = dist > 1e-4f ? flat / dist : Vector3.forward;
        var d = Data;

        // Line of sight, sampled twice a second. A ground unit that cannot see the player for a
        // few seconds stops holding position and hunts - a squad never waits behind a wall forever.
        _losTimer -= dt;
        if (_losTimer <= 0f && d.HoverHeight <= 0f) {
            _losTimer = 0.5f;
            var eye = transform.position + Vector3.up * (d.Height * 0.6f);
            _noLos = Ctx.Arena.LineBlocked(eye, playerPos) ? _noLos + 0.5f : 0f;
        }
        bool hunting = Aggressive || _noLos > 2.5f;
        float speed = d.Speed * _speedMul;
        bool moved = false; float moveSpeed = 0f;

        switch (d.Role) {

            // ---- Gnat: hold range, plink, panic-run when hurt or leaderless -----------
            case EnemyRole.Ranged: {
                if (hunting) _panicTimer = 0f;
                Vector3 faceDir = toPlayer;
                if (hunting && _noLos > 2.5f) {
                    Move(toPlayer, speed * 1.2f, dt); moved = true; moveSpeed = speed * 1.2f;
                } else if (_panicTimer > 0f) {
                    var flee = new Vector3(-toPlayer.x + Mathf.Sin(_stateTime * 7f) * 0.6f, 0f,
                                           -toPlayer.z + Mathf.Cos(_stateTime * 5f) * 0.6f).normalized;
                    Move(flee, speed * 1.5f, dt); moved = true; moveSpeed = speed * 1.5f; faceDir = flee;
                } else if (dist > (Aggressive ? 5f : d.AttackRange * 0.85f)) {
                    Move(toPlayer, speed * (Aggressive ? 1.3f : 1f), dt); moved = true; moveSpeed = speed;
                } else if (dist < d.KeepDistance * 0.6f && !Aggressive) {
                    var back = -toPlayer;
                    Move(back, speed * 0.8f, dt); moved = true; moveSpeed = speed * 0.8f; faceDir = back;
                } else {
                    _strafeTimer -= dt;
                    if (_strafeTimer <= 0f) { _strafeDir *= -1; _strafeTimer = 1.5f + Random.value * 2f; }
                    var st = new Vector3(-toPlayer.z * _strafeDir, 0f, toPlayer.x * _strafeDir);
                    Move(st, speed * 0.5f, dt); moved = true; moveSpeed = speed * 0.5f; faceDir = st;
                }
                FaceHeading(faceDir, dt, _panicTimer > 0f ? 12f : 7f);
                if (_panicTimer <= 0f && dist < d.AttackRange && _attackCooldown <= 0f && _noLos <= 0f) {
                    Face(playerPos, dt, 16f);                    // plant and aim for the shot
                    Shoot(playerPos, d.Damage, 26f, 0.05f, 1f, playerVel);
                    _attackCooldown = d.AttackRate * (0.8f + Random.value * 0.5f);
                }
                break;
            }

            // ---- Skirmisher: lateral flanker, arm shield toward the player ------------
            case EnemyRole.Skirmisher: {
                _strafeTimer -= dt;
                if (_strafeTimer <= 0f) { _strafeDir *= -1; _strafeTimer = 0.8f + Random.value * 1.4f; }
                // Orbit at keepDistance; close in while the arm shield is up, back off once broken.
                float want = FrontShield > 0f ? d.KeepDistance : d.KeepDistance * 1.6f;
                Vector3 dir = hunting ? Vector3.zero
                                      : new Vector3(-toPlayer.z * _strafeDir, 0f, toPlayer.x * _strafeDir);
                if (dist > want + 3f || hunting) dir += toPlayer * 1.2f;
                else if (dist < want - 3f) dir -= toPlayer * 1.2f;
                dir.Normalize();
                Move(dir, speed, dt); moved = true; moveSpeed = speed;

                bool bursting = dist < d.AttackRange && _attackCooldown <= 0f && _noLos <= 0f;
                if (bursting) Face(playerPos, dt, 14f); else FaceHeading(dir, dt, 11f);

                if (bursting) {
                    if (_burstLeft <= 0) _burstLeft = d.Burst;
                    _burstTimer -= dt;
                    if (_burstTimer <= 0f) {
                        Shoot(playerPos, d.Damage, 40f, 0.035f, 0.6f, playerVel);
                        _burstLeft--; _burstTimer = 0.09f;
                        if (_burstLeft <= 0) _attackCooldown = d.AttackRate * 4.5f;
                    }
                }
                // The arm shield slowly regrows once it has been down a while.
                if (FrontShield <= 0f) {
                    _frontRegen += dt;
                    if (_frontRegen > 8f) { FrontShield = FrontShieldMax * 0.6f; _frontRegen = 0f; }
                }
                break;
            }

            // ---- Warlord: press while shielded, melee up close, fall back to recharge --
            case EnemyRole.Commander: {
                Vector3 faceDir = Vector3.zero;
                if (_retreatTimer > 0f || (Shield <= 0f && MaxShield > 0f && dist < 12f)) {
                    var back = -toPlayer;
                    back.x += Mathf.Sin(_stateTime * 3f) * 0.5f;
                    back.Normalize();
                    Move(back, speed * 1.2f, dt); moved = true; moveSpeed = speed * 1.2f; faceDir = back;
                } else if (dist < d.MeleeRange + 0.3f) {
                    if (_attackCooldown <= 0f) {
                        _anim.PlayOnce(Clip.Attack);
                        _pendingMelee = 0.28f;                   // lands mid-swing
                        _attackCooldown = 1.1f;
                    }
                } else if (dist < 7f) {
                    Move(toPlayer, speed * 1.6f, dt); moved = true; moveSpeed = speed * 1.6f; faceDir = toPlayer;
                } else if (dist > d.KeepDistance || hunting) {
                    Move(toPlayer, speed, dt); moved = true; moveSpeed = speed; faceDir = toPlayer;
                } else {
                    _strafeTimer -= dt;
                    if (_strafeTimer <= 0f) { _strafeDir *= -1; _strafeTimer = 1.2f + Random.value * 1.5f; }
                    var st = new Vector3(-toPlayer.z * _strafeDir, 0f, toPlayer.x * _strafeDir);
                    Move(st, speed * 0.6f, dt); moved = true; moveSpeed = speed * 0.6f; faceDir = st;
                }

                bool inBurst = dist >= d.MeleeRange + 0.3f && dist < d.AttackRange
                               && _attackCooldown <= 0f && _retreatTimer <= 0f;
                if (dist < d.MeleeRange + 0.3f) Face(playerPos, dt, 10f);
                else if (inBurst) Face(playerPos, dt, 14f);
                else FaceHeading(faceDir, dt, 9f);

                if (_pendingMelee >= 0f) {
                    _pendingMelee -= dt;
                    if (_pendingMelee <= 0f) {
                        _pendingMelee = -1f;
                        if (dist < d.MeleeRange + 0.8f)
                            Ctx.OnMelee?.Invoke(this, Mathf.Round(d.MeleeDamage * _dmgMul));
                    }
                }
                if (inBurst) {
                    if (_burstLeft <= 0) _burstLeft = d.Burst;
                    _burstTimer -= dt;
                    if (_burstTimer <= 0f) {
                        Shoot(playerPos, d.Damage, 34f, 0.045f, 0.9f, playerVel);
                        _burstLeft--; _burstTimer = 0.14f;
                        if (_burstLeft <= 0) _attackCooldown = d.AttackRate * 3.2f;
                    }
                }
                break;
            }

            // ---- Juggernaut: slow advance behind the tower shield, lobbed fuel rods ----
            case EnemyRole.Tank: {
                Face(playerPos, dt, 3f);
                if (dist > d.KeepDistance || hunting) { Move(toPlayer, speed, dt); moved = true; moveSpeed = speed; }
                if (dist < d.AttackRange && _attackCooldown <= 0f && _noLos <= 0f) {
                    // Arc the shot so it can be dodged; gravity pulls it down onto the player.
                    float t = Mathf.Max(0.6f, dist / 22f);
                    Vector3 lead = playerPos + playerVel * (t * 0.6f);
                    Vector3 from = transform.position
                                 + new Vector3(Mathf.Sin(_yaw) * 1.2f, d.Height * 0.7f, Mathf.Cos(_yaw) * 1.2f);
                    const float g = 14f;
                    Vector3 delta = lead - from;
                    float vy = (delta.y + 0.5f * g * t * t) / t;
                    Vector3 vel = new Vector3(delta.x / t, vy, delta.z / t);
                    float sp = vel.magnitude;
                    Ctx.Bolts.Fire(from, vel / sp, sp, d.BoltColor, Mathf.Round(d.Damage * _dmgMul),
                                   d.Splash, g, 2.2f);
                    Ctx.Audio?.RocketLaunch();
                    ShotsFired++;
                    _anim.PlayOnce(Clip.Shoot);
                    _attackCooldown = d.AttackRate;
                }
                break;
            }

            // ---- Wasp: circle overhead, dive to strafe, climb away ---------------------
            case EnemyRole.Aerial: {
                float hover = d.HoverHeight + Mathf.Sin(_stateTime * 2f + _hoverPhase) * 1.2f;
                _strafeTimer -= dt;
                if (_strafeTimer <= 0f) {
                    _strafeDir *= -1; _strafeTimer = 2f + Random.value * 2f; _diving = Random.value < 0.5f;
                }
                Vector3 dir = new Vector3(-toPlayer.z * _strafeDir, 0f, toPlayer.x * _strafeDir);
                if (dist > d.KeepDistance + 4f) dir += toPlayer;
                else if (dist < d.KeepDistance - 4f) dir -= toPlayer;
                dir.Normalize();

                var p = transform.position;
                p.x += dir.x * speed * dt; p.z += dir.z * speed * dt;
                float targetY = _diving ? Mathf.Max(2.2f, hover * 0.45f) : hover;
                p.y += (targetY - p.y) * Mathf.Min(1f, 3f * dt);
                transform.position = p;

                if (dist < d.AttackRange && _attackCooldown <= 0f) Face(playerPos, dt, 10f);
                else FaceHeading(dir, dt, 6f);

                // Bank into the turn, and shudder: an insect drone that holds a rigid pose reads
                // as a prop. There is no rig here, so the motion has to come from the transform.
                if (_model != null) {
                    float flutter = Mathf.Sin(_stateTime * 34f) * 1.6f;
                    float bob = Mathf.Sin(_stateTime * 9f) * 0.9f;
                    _model.transform.localRotation = Quaternion.Euler(
                        (_diving ? 14f : 0f) + bob + flutter * 0.35f,
                        ModelCache.ForwardYawOffset,
                        -_strafeDir * 20f + flutter);
                    var lp = _model.transform.localPosition;
                    lp.y = Mathf.Sin(_stateTime * 11f) * 0.06f;
                    _model.transform.localPosition = lp;
                }

                if (dist < d.AttackRange && _attackCooldown <= 0f) {
                    Shoot(playerPos, d.Damage, 32f, 0.04f, 0.7f, playerVel);
                    _attackCooldown = d.AttackRate;
                }
                moved = true; moveSpeed = speed;
                break;
            }

            // ---- Overseer: hover, sweep bolts, spawn Gnats -----------------------------
            case EnemyRole.Boss: {
                float hover = d.HoverHeight + Mathf.Sin(_stateTime * 1.2f) * 1.5f;
                Face(playerPos, dt, 2.5f);
                _strafeTimer -= dt;
                if (_strafeTimer <= 0f) { _strafeDir *= -1; _strafeTimer = 3f + Random.value * 3f; }
                Vector3 dir = new Vector3(-toPlayer.z * _strafeDir, 0f, toPlayer.x * _strafeDir);
                if (dist > d.KeepDistance + 6f) dir += toPlayer;
                else if (dist < d.KeepDistance - 6f) dir -= toPlayer;
                dir.Normalize();

                var p = transform.position;
                p.x += dir.x * speed * dt; p.z += dir.z * speed * dt;
                p.y += (hover - p.y) * Mathf.Min(1f, 2f * dt);
                transform.position = p;

                // The hull turns slowly and breathes: a 5 m boss that is perfectly rigid looks
                // like scenery, and it is the one enemy the player stares at for a whole minute.
                if (_model != null) {
                    _model.transform.Rotate(Vector3.up, dt * 23f, Space.Self);
                    float breathe = 1f + Mathf.Sin(_stateTime * 1.6f) * 0.02f;
                    _model.transform.localScale = _modelBaseScale * breathe;
                    _model.transform.localRotation = Quaternion.Euler(
                        Mathf.Sin(_stateTime * 0.9f) * 2.5f,
                        _model.transform.localEulerAngles.y,
                        Mathf.Cos(_stateTime * 0.7f) * 2.5f);
                }

                if (dist < d.AttackRange && _attackCooldown <= 0f) {
                    Shoot(playerPos, d.Damage, 30f, 0.06f, 1.2f, playerVel);
                    // Fires three times slower while shielded, so stripping the shield is the fight.
                    _attackCooldown = d.AttackRate * (Shield > 0f ? 3f : 2f);
                }
                _spawnTimer -= dt;
                if (_spawnTimer <= 0f && Ctx.Spawn != null) {
                    _spawnTimer = d.SpawnEvery;
                    for (int i = 0; i < 3; i++) {
                        float a = Random.value * Mathf.PI * 2f;
                        Ctx.Spawn("gnat", new Vector3(p.x + Mathf.Cos(a) * 4f, 0f, p.z + Mathf.Sin(a) * 4f), true);
                    }
                }
                moved = true; moveSpeed = speed;
                break;
            }
        }

        if (d.HoverHeight <= 0f) Separate(dt);

        // Locomotion clip chosen from how fast the unit is actually moving, with the playback
        // rate scaled to match so the feet do not skate.
        if (!_anim.OneShotPlaying) {
            if (moved && moveSpeed > 0.1f) {
                bool run = moveSpeed > d.Speed * 0.95f || _panicTimer > 0f;
                _anim.Play(run ? Clip.Run : Clip.Walk, 0.15f,
                           run ? Mathf.Max(0.8f, moveSpeed / (d.Speed * 1.5f))
                               : Mathf.Max(0.7f, moveSpeed / d.Speed));
            } else {
                _anim.Play(Clip.Idle, 0.25f);
            }
        }
        return false;
    }

    bool TickDead(float dt) {
        _anim.Tick(dt);
        _deathTimer -= dt;

        if (Data.HoverHeight > 0f) {                       // aircraft fall and spin in
            var p = transform.position;
            p.y = Mathf.Max(0.3f, p.y - 6f * dt);
            transform.position = p;
            transform.Rotate(1.5f * dt * Mathf.Rad2Deg * 0.3f, 0f, 3f * dt * Mathf.Rad2Deg * 0.3f, Space.Self);
            if (p.y <= 0.31f && !_crashed) {
                _crashed = true;
                Ctx.Fx?.Explosion(p, 0.8f);
            }
        }
        if (_deathTimer < 0.7f) {
            float a = Mathf.Max(0f, _deathTimer / 0.7f);
            for (int i = 0; i < _materials.Count; i++) {
                var m = _materials[i];
                if (m == null) continue;
                Fx.MakeTransparent(m);
                var c = m.HasProperty(BaseColor) ? m.GetColor(BaseColor) : m.color;
                c.a = a;
                if (m.HasProperty(BaseColor)) m.SetColor(BaseColor, c); else m.color = c;
            }
        }
        return _deathTimer <= 0f;
    }

    void OnDestroy() {
        for (int i = 0; i < _materials.Count; i++) if (_materials[i] != null) Destroy(_materials[i]);
    }
}

}
