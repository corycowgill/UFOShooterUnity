using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UFO {

public enum GameState { Menu, Playing, Paused, PerkSelect, GameOver }

/// <summary>
/// The glue: owns every system, runs one update order, and turns hits into score, drops, feedback
/// and wave progression. Port of v2's js/main.js.
///
/// Everything the player sees is built at runtime from Resources - there is one scene, and it
/// contains only this component. That keeps the whole game reproducible from code, which is what
/// made a headless port of a 15-file JS game tractable in the first place.
/// </summary>
public class GameManager : MonoBehaviour {

    public static GameManager Instance { get; private set; }

    [Header("Tuning")]
    public float PickupHeal = 25f;
    public float PickupShield = 35f;
    public float PickupLifetime = 16f;
    public float PickupDropChance = 0.28f;
    public int MultiKillThreshold = 3;
    public float MultiKillWindow = 1.2f;

    static readonly string[] KillStreakNames = {
        "", "", "", "TRIPLE KILL", "OVERKILL", "KILLTACULAR", "KILLPOCALYPSE", "KILLIONAIRE",
    };

    public GameState State { get; private set; } = GameState.Menu;

    public PlayerStats Stats { get; private set; }
    public PlayerController Player { get; private set; }
    public WeaponManager Weapons { get; private set; }
    public WaveManager Waves { get; private set; }
    public LevelBuilder Level { get; private set; }
    public Arena Arena { get; private set; }
    public Fx Fx { get; private set; }
    public GameAudio Audio { get; private set; }
    public EnemyProjectiles Bolts { get; private set; }
    public Hud Hud { get; private set; }
    public Decals Decals { get; private set; }
    public Weather Weather { get; private set; }

    public int LevelIndex { get; private set; }

    EnemyContext _ctx;
    Camera _cam;
    Light _sun;

    // multi-kill / slow-mo
    int _recentKills;
    float _recentKillTimer, _killTimeScale = 1f, _killTimeScaleTimer;
    float _footstepTimer;
    bool _dashSoundPlayed;
    float _waveBannerTimer;

    // pickups
    class Pickup { public Transform T; public float Life; public string Type; }
    readonly List<Pickup> _pickups = new List<Pickup>(32);
    Transform _pickupRoot;

    bool _loadingLevel;

    // ------------------------------------------------------------------ boot

    void Awake() {
        Instance = this;
        Application.targetFrameRate = 60;
        Platform.ApplyUrlOverrides();

        Arena = new Arena();
        Stats = new PlayerStats();

        BuildRig();

        Fx = gameObject.AddComponent<Fx>();
        Fx.Cam = _cam;

        Audio = gameObject.AddComponent<GameAudio>();

        Decals = gameObject.AddComponent<Decals>();
        Weather = gameObject.AddComponent<Weather>();

        Bolts = gameObject.AddComponent<EnemyProjectiles>();
        Bolts.Init(Arena, Fx);
        Bolts.OnGroundHit = (pos, splash) => {
            Fx.Impact(pos, new Color(0.6f, 0.3f, 1f), splash ? 2f : 0.5f);
            // Alien plasma marks the road it misses you on.
            Decals.Place(DecalKind.Scorch, new Vector3(pos.x, 0.02f, pos.z), Vector3.up, splash ? 3.4f : 1.1f);
        };

        Level = gameObject.AddComponent<LevelBuilder>();
        Level.Arena = Arena;
        Level.Sun = _sun;

        _ctx = new EnemyContext {
            Arena = Arena, Bolts = Bolts, Fx = Fx, Audio = Audio,
            OnMelee = OnEnemyMelee,
            PlayerPos = () => Player.EyePosition,
        };

        var enemyRoot = new GameObject("Enemies").transform;
        Waves = new WaveManager(_ctx, enemyRoot);

        _pickupRoot = new GameObject("Pickups").transform;

        Weapons.Stats = Stats;
        Weapons.Arena = Arena;
        Weapons.Fx = Fx;
        Weapons.Audio = Audio;
        Weapons.Controller = Player;
        Weapons.OnHit = OnHit;
        Weapons.OnExplosion = (pos, radius, isRocket) => {
            // The blast hurts the player too, unless they dashed out of it.
            float d = Vector3.Distance(Player.EyePosition, pos);
            if (d < radius) Stats.TakeDamage(60f * (1f - d / radius));
            Fx.Shake(0.25f, 0.4f);
            Decals.Place(DecalKind.Scorch, new Vector3(pos.x, 0.02f, pos.z), Vector3.up, radius * 0.9f);
        };
        Weapons.OnWallHit = (point, normal, kind) => {
            Decals.Place(kind == "plasma" ? DecalKind.Scorch : DecalKind.Bullet, point, normal,
                         kind == "plasma" ? 0.9f : 0.28f);
        };

        Stats.IsInvulnerable = () => Player != null && Player.Invulnerable;
        Stats.OnDamaged += (amount, shieldOnly) => {
            Audio.PlayerHit();
            Fx.Shake(0.12f, 0.2f);
        };

        Hud = gameObject.AddComponent<Hud>();
        Hud.Game = this;

        // Settings are bound and applied before the first level builds, so a saved brightness or
        // render scale is in force on the very first frame rather than snapping in a moment later.
        var settings = GameSettings.Instance;
        settings.Cam = _cam;
        settings.Player = Player;
        settings.Audio = Audio;
        settings.Weapons = Weapons;
        settings.Load();
        settings.Apply();
    }

    void BuildRig() {
        var playerGo = new GameObject("Player");
        Player = playerGo.AddComponent<PlayerController>();
        Player.Arena = Arena;
        Player.Stats = Stats;

        var camGo = new GameObject("MainCamera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(playerGo.transform, false);
        _cam = camGo.AddComponent<Camera>();
        _cam.fieldOfView = 75f;
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = 600f;
        camGo.AddComponent<AudioListener>();

        // URP turns post-processing OFF by default on a camera created from script. Without this
        // the whole Volume stack - tonemapping, bloom, vignette, colour grading - never runs, and
        // the display settings have nothing to act on.
        var camData = _cam.GetUniversalAdditionalCameraData();
        camData.renderPostProcessing = true;
        camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        camData.antialiasingQuality = AntialiasingQuality.Medium;

        Player.Cam = _cam;

        var sunGo = new GameObject("Sun");
        _sun = sunGo.AddComponent<Light>();
        _sun.type = LightType.Directional;

        Weapons = gameObject.AddComponent<WeaponManager>();
        Weapons.Build(_cam);
    }

    void Start() {
        StartCoroutine(LoadLevel(0, firstTime: true));
    }

    // ------------------------------------------------------------------ flow

    public void StartGame() {
        Stats.Reset();
        Weapons.ResetAll();
        Waves.ResetAll();
        LevelIndex = 0;
        _recentKills = 0;
        ClearPickups();
        Bolts.Clear();
        StartCoroutine(LoadLevel(0, firstTime: false, thenPlay: true));
    }

    IEnumerator LoadLevel(int index, bool firstTime, bool thenPlay = false) {
        _loadingLevel = true;
        LevelIndex = index;
        Waves.Cleanup();
        ClearPickups();
        Bolts.Clear();
        Weapons.ClearProjectiles();

        Level.Build(index);
        Decals.Clear();
        Weather.Configure(Level.Current != null ? Level.Current.Weather : "clear", _cam.transform);
        Waves.SetSpawnPoints(new List<Vector3>(Level.SpawnPoints));
        Player.Teleport(Level.PlayerStart, Level.PlayerStartYaw);

        yield return null;                 // let the frame settle before unlocking input
        _loadingLevel = false;
        Debug.Log($"[UFO] player at {Player.transform.position} eye={Player.EyePosition} " +
                  $"fov={Player.Cam.fieldOfView} near={Player.Cam.nearClipPlane} far={Player.Cam.farClipPlane}");

        if (thenPlay) EnterPlaying();
        else if (firstTime) State = GameState.Menu;
    }

    void EnterPlaying() {
        State = GameState.Playing;
        Player.InputEnabled = true;
        Player.Lock();
        Waves.StartWave();
        Audio.WaveStart();
        _waveBannerTimer = 2.5f;
    }

    public void TogglePause() {
        if (State == GameState.Playing) {
            State = GameState.Paused;
            Player.InputEnabled = false;
            Player.Unlock();
        } else if (State == GameState.Paused) {
            State = GameState.Playing;
            Player.InputEnabled = true;
            Player.Lock();
        }
    }

    public void ChoosePerk(Perk p) {
        if (State != GameState.PerkSelect) return;
        Stats.AddPerk(p.Id);
        State = GameState.Playing;
        Player.InputEnabled = true;
        Player.Lock();

        if (Waves.ShouldChangeLevelAfterWave()) StartCoroutine(AdvanceLevel());
        else StartNextWave();
    }

    IEnumerator AdvanceLevel() {
        yield return LoadLevel(LevelIndex + 1, firstTime: false);
        StartNextWave();
    }

    void StartNextWave() {
        Waves.StartWave();
        Audio.WaveStart();
        _waveBannerTimer = 2.5f;
    }

    void GameOver() {
        State = GameState.GameOver;
        Player.InputEnabled = false;
        Player.Unlock();
    }

    // ------------------------------------------------------------------ loop

    void Update() {
        float dt = Time.deltaTime;

        // Publish the finger BEFORE anything reads input this frame.
        if (Hud != null && Hud.Touch != null) Hud.Touch.Tick();
        if (Platform.Diagnostics) TickDiagnostics(dt);

        // Tab opens the field guide from anywhere, including mid-fight - v2 binds it the same way.
        if (InputMap.Guide && Hud != null && Hud.Guide != null) {
            Hud.Guide.Toggle();
            // Reading the bestiary should not mean being shot while you do it.
            if (State == GameState.Playing) {
                Player.InputEnabled = !Hud.Guide.IsOpen;
                if (Hud.Guide.IsOpen) Player.Unlock(); else Player.Lock();
            }
        }

        if (InputMap.Pause) {
            // The overlays sit on top of whatever is underneath, so Escape backs out of them first.
            if (Hud != null && Hud.Guide != null && Hud.Guide.IsOpen) {
                Hud.Guide.Close();
                if (State == GameState.Playing) { Player.InputEnabled = true; Player.Lock(); }
            }
            else if (Hud != null && Hud.Settings != null && Hud.Settings.IsOpen) Hud.Settings.Close();
            else if (State == GameState.Menu) StartGame();
            else if (State == GameState.GameOver) StartGame();
            else TogglePause();
        }

        if (State == GameState.Menu || State == GameState.GameOver) {
            if (Input.anyKeyDown && !InputMap.Pause) { /* menu handled by Hud buttons */ }
            return;
        }
        if (State == GameState.Paused || State == GameState.PerkSelect || _loadingLevel) return;
        if (Hud != null && Hud.Guide != null && Hud.Guide.IsOpen) return;

        // Multi-kill slow motion.
        if (_killTimeScaleTimer > 0f) {
            _killTimeScaleTimer -= dt;
            if (_killTimeScaleTimer <= 0f) _killTimeScale = 1f;
        }
        float sim = dt * _killTimeScale;

        Step(sim);
    }

    void Step(float dt) {
        Player.Tick(dt);
        Stats.Tick(dt);

        HandleCombatInput();

        Vector3 eye = Player.EyePosition;
        Vector3 vel = Player.Velocity;

        Waves.Tick(dt, eye, vel);
        Weapons.Tick(dt, Waves.Enemies, eye);

        // Alien bolts that reached the player.
        var hits = Bolts.Tick(dt, eye);
        for (int i = 0; i < hits.Count; i++) {
            if (Player.Invulnerable) continue;             // dash i-frames
            Stats.TakeDamage(hits[i].Damage);
            Fx.Shake(hits[i].Splash ? 0.3f : 0.1f, 0.2f);
        }

        UpdatePickups(dt, eye);
        UpdateFootsteps(dt);

        if (_recentKillTimer > 0f) {
            _recentKillTimer -= dt;
            if (_recentKillTimer <= 0f) _recentKills = 0;
        }
        if (_waveBannerTimer > 0f) _waveBannerTimer -= dt;

        // Camera shake is applied on top of the controller's own camera placement.
        if (_cam != null) {
            var lp = _cam.transform.localPosition;
            _cam.transform.localPosition = lp + Fx.ShakeOffset;
        }

        if (Stats.Dead) { GameOver(); return; }

        // Wave finished: offer a perk, then either the next wave or the next city block.
        if (Waves.State == WaveState.Waiting && Waves.Wave > 0) {
            State = GameState.PerkSelect;
            Player.InputEnabled = false;
            Player.Unlock();
        }
    }

    void HandleCombatInput() {
        if (Stats.Dead) return;

        int slot = InputMap.WeaponSlot;
        if (slot >= 0) Weapons.SwitchWeapon(Arsenal.Order[slot]);
        int cycle = InputMap.WeaponCycle;
        if (cycle != 0) Weapons.CycleWeapon(cycle);

        if (InputMap.Reload) Weapons.Reload();
        if (InputMap.Grenade) Weapons.ThrowGrenade();
        if (InputMap.AltFirePressed) Weapons.FireAlt(Waves.Enemies);

        var w = Weapons.Weapon;
        bool wantFire = w.Auto ? InputMap.FireHeld : InputMap.FirePressed;
        if (wantFire && Weapons.Fire(Waves.Enemies)) Hud?.NotifyFired();
    }

    /// <summary>
    /// Narrate input and player state once a second under ?diag=1. Exists so the touch harness can
    /// assert that a drag actually turned the view rather than just that a widget was hit.
    /// </summary>
    float _diagTimer;
    void TickDiagnostics(float dt) {
        _diagTimer += dt;
        if (_diagTimer < 1f) return;
        _diagTimer = 0f;
        var p = Player != null ? Player.transform.position : Vector3.zero;
        var fwd = Player != null ? Player.transform.forward : Vector3.forward;
        Debug.Log($"[DIAG] state={State} touch={TouchState.Active} " +
                  $"pos=({p.x:F2},{p.z:F2}) yaw={Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg:F1} " +
                  $"move=({TouchState.Move.x:F2},{TouchState.Move.y:F2}) fire={TouchState.Fire} " +
                  $"hp={(Stats != null ? Stats.Hp : 0f):F0}");
    }

    void UpdateFootsteps(float dt) {
        if (Player.OnGround && Player.MoveDirection.sqrMagnitude > 0.01f && Player.DashTimer <= 0f) {
            _footstepTimer -= dt;
            if (_footstepTimer <= 0f) {
                Audio.Footstep(Player.Sprinting);
                _footstepTimer = Player.Sprinting ? 0.28f : 0.4f;
            }
        } else _footstepTimer = 0f;

        if (Player.DashTimer > 0f && !_dashSoundPlayed) { Audio.Dash(); _dashSoundPlayed = true; }
        if (Player.DashTimer <= 0f) _dashSoundPlayed = false;
    }

    // ------------------------------------------------------------------ hits & kills

    void OnHit(WeaponManager.HitInfo hit) {
        var e = hit.Enemy;
        if (e == null || e.Dead) return;

        var res = e.TakeDamage(hit.Damage, hit.From, hit.Point, hit.Explosive, hit.Knockback);

        if (hit.Headshot && !res.Absorbed) Audio.CritHit();
        else if (!res.Absorbed) Audio.AlienHit();

        Fx.Impact(hit.Point, res.Absorbed ? new Color(0.5f, 0.63f, 1f) : new Color(1f, 0.38f, 0.75f),
                  hit.Explosive ? 0.3f : 0.5f);

        Hud?.ShowHitMarker(res.Killed);
        Hud?.ShowDamageNumber(e.transform.position + Vector3.up * (e.Data.Height + 0.2f),
                              res.Absorbed ? "SHIELD" : Mathf.RoundToInt(hit.Damage).ToString(),
                              res.Killed, hit.Headshot);

        Fx.Shake(hit.WeaponKey == "rocketLauncher" ? 0.05f
               : hit.WeaponKey == "energySword" ? 0.04f : 0.015f, 0.06f);

        if (res.Killed) OnKill(e, hit);
    }

    void OnKill(Enemy e, WeaponManager.HitInfo hit) {
        Stats.AddKill();
        Stats.AddScore(e.Data.Points * (e.Elite ? 3 : 1));

        _recentKills++;
        _recentKillTimer = MultiKillWindow;
        if (_recentKills >= MultiKillThreshold) {
            _killTimeScale = 0.35f;
            _killTimeScaleTimer = 0.4f;
            Audio.MultiKill();
            string name = KillStreakNames[Mathf.Min(_recentKills, KillStreakNames.Length - 1)];
            if (!string.IsNullOrEmpty(name)) Hud?.ShowKillStreak(name, _recentKills >= 6);
            _recentKills = 0;
        }

        if (Stats.VampireHeal > 0f) Stats.Heal(Stats.VampireHeal);

        Hud?.AddKillFeed(e.Data.Name,
            Arsenal.All.ContainsKey(hit.WeaponKey) ? Arsenal.Get(hit.WeaponKey).Name : "GRENADE");
        Fx.DeathEffect(e.transform.position, e.Data.Color, e.Data.Height * 0.5f);
        if (e.Data.HoverHeight <= 0f)
            Decals.Splatter(new Vector3(e.transform.position.x, 0.02f, e.transform.position.z),
                            1.2f + e.Data.Height * 0.5f, e.IsBoss ? 6 : 3);

        float dropChance = PickupDropChance + (e.Data.Hp > 150f ? 0.2f : 0f) + Stats.DropRateBonus;
        if (e.Elite || e.IsBoss || Random.value < dropChance) {
            float r = Random.value;
            string type = e.IsBoss ? "shield"
                        : r < 0.45f ? "health"
                        : r < 0.70f ? "shield"
                        : r < 0.90f ? "ammo" : "grenade";
            SpawnPickup(e.transform.position, type);
        }

        if (e.IsBoss) Fx.MegaExplosion(e.transform.position, 8f);
    }

    void OnEnemyMelee(Enemy enemy, float damage) {
        if (Stats.Dead) return;
        Stats.TakeDamage(damage);
        Fx.Shake(0.35f, 0.3f);
    }

    // ------------------------------------------------------------------ pickups

    void SpawnPickup(Vector3 position, string type) {
        var go = Prim.Create(PrimKind.Sphere, "Pickup_" + type, _pickupRoot);
        go.transform.position = position + Vector3.up * 0.6f;
        go.transform.localScale = Vector3.one * 0.44f;

        Color c = type == "health" ? new Color(0f, 1f, 0.4f)
                : type == "shield" ? new Color(0f, 0.53f, 1f)
                : type == "ammo" ? new Color(1f, 0.67f, 0f)
                : new Color(0.27f, 1f, 0.27f);
        var r = go.GetComponent<Renderer>();
        r.material = Fx.AdditiveTinted(c * 3f);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        _pickups.Add(new Pickup { T = go.transform, Life = PickupLifetime, Type = type });
    }

    void UpdatePickups(float dt, Vector3 playerPos) {
        for (int i = _pickups.Count - 1; i >= 0; i--) {
            var p = _pickups[i];
            p.Life -= dt;

            p.T.Rotate(Vector3.up, 120f * dt, Space.World);
            var pos = p.T.position;
            pos.y = 0.6f + Mathf.Sin(Time.time * 3f + i) * 0.15f;
            p.T.position = pos;

            if (Vector3.Distance(pos, playerPos) < 2f) {
                switch (p.Type) {
                    case "health":  Stats.Heal(PickupHeal); break;
                    case "shield":  Stats.AddShield(PickupShield); break;
                    case "ammo":    Weapons.AddAmmo(40); break;
                    case "grenade": Weapons.AddGrenade(1); break;
                }
                Audio.Pickup();
                Destroy(p.T.gameObject);
                _pickups.RemoveAt(i);
                continue;
            }
            if (p.Life <= 0f) {
                Destroy(p.T.gameObject);
                _pickups.RemoveAt(i);
            }
        }
    }

    void ClearPickups() {
        foreach (var p in _pickups) if (p.T != null) Destroy(p.T.gameObject);
        _pickups.Clear();
    }

    // ------------------------------------------------------------------ HUD queries

    public float WaveBannerTimer => _waveBannerTimer;
    public bool Loading => _loadingLevel;
}

}
