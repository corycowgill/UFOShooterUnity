using System.Collections.Generic;
using UnityEngine;

namespace UFO {

public enum WaveState { Waiting, Spawning, Active, Complete }

/// <summary>
/// Wave composition, spawn placement and pacing. Port of v2's js/waves.js.
///
/// The two pacing rules near the bottom of Tick() are the important ones and exist because of a
/// real problem: ranged aliens hold at keepDistance and plink, so the last one or two of a squad
/// would stand off and stretch a wave to 60-100 seconds of nothing happening.
/// </summary>
public class WaveManager {

    class Theme {
        public string Name;
        public int MinWave;
        public (string type, float share)[] Mix;
    }

    static readonly Theme[] Themes = {
        new Theme { Name = "SCOUT LANCE",   MinWave = 1, Mix = new[] { ("gnat", 1.0f) } },
        new Theme { Name = "FLANKING PAIR", MinWave = 2, Mix = new[] { ("gnat", 0.6f), ("skirmisher", 0.4f) } },
        new Theme { Name = "COMMAND SQUAD", MinWave = 3, Mix = new[] { ("gnat", 0.55f), ("skirmisher", 0.25f), ("warlord", 0.2f) } },
        new Theme { Name = "AIR RAID",      MinWave = 4, Mix = new[] { ("wasp", 0.5f), ("gnat", 0.3f), ("skirmisher", 0.2f) } },
        new Theme { Name = "SIEGE LINE",    MinWave = 6, Mix = new[] { ("juggernaut", 0.15f), ("gnat", 0.45f), ("warlord", 0.2f), ("skirmisher", 0.2f) } },
        new Theme { Name = "SHADOW STRIKE", MinWave = 7, Mix = new[] { ("skirmisher", 0.5f), ("warlord", 0.3f), ("wasp", 0.2f) } },
        new Theme { Name = "FULL ASSAULT",  MinWave = 9, Mix = new[] { ("gnat", 0.3f), ("skirmisher", 0.2f), ("warlord", 0.2f), ("wasp", 0.15f), ("juggernaut", 0.15f) } },
    };

    struct Entry { public string Type; public bool Elite, IsBoss; }

    public int Wave { get; private set; }
    public WaveState State { get; private set; } = WaveState.Waiting;
    public string ThemeName { get; private set; }
    public readonly List<Enemy> Enemies = new List<Enemy>(64);

    public float HpMultiplier = 1f, SpeedMultiplier = 1f, DamageMultiplier = 1f;

    readonly EnemyContext _ctx;
    readonly Transform _parent;
    readonly List<Entry> _queue = new List<Entry>(40);
    List<Vector3> _spawnPoints = new List<Vector3>();

    float _stateTimer, _spawnTimer, _activeTime;
    int _aliveCount;
    Vector3? _lastLeaderPos;
    int _lastSpawnIndex = -1;

    public System.Action<Enemy, bool> OnSpawn;      // (enemy, wasDropIn)

    public WaveManager(EnemyContext ctx, Transform parent) {
        _ctx = ctx;
        _parent = parent;
        _ctx.Enemies = Enemies;
        _ctx.Spawn = (type, pos, dropIn) => SpawnAt(type, pos, false, dropIn);
    }

    public void SetSpawnPoints(List<Vector3> points) { _spawnPoints = points; _lastSpawnIndex = -1; }

    public void StartWave() {
        Wave++;
        State = WaveState.Spawning;
        Compose();
        _spawnTimer = 0.2f;
    }

    void Compose() {
        int w = Wave;
        int count = Mathf.Min(4 + Mathf.FloorToInt(w * 1.8f), 32);

        var pool = new List<Theme>();
        foreach (var t in Themes) if (t.MinWave <= w) pool.Add(t);

        // Waves 1-3 are fixed so the roster is introduced one species at a time.
        var theme = w <= 3 ? pool[Mathf.Min(w - 1, pool.Count - 1)] : pool[Random.Range(0, pool.Count)];
        ThemeName = theme.Name;

        _queue.Clear();
        int remaining = count;
        for (int i = 0; i < theme.Mix.Length; i++) {
            var (type, share) = theme.Mix[i];
            int n = i == theme.Mix.Length - 1 ? remaining : Mathf.RoundToInt(count * share);
            if (type == "juggernaut") n = Mathf.Min(n, 1 + w / 6);
            if (type == "warlord")
                n = Mathf.Min(Mathf.Max(n, w >= 3 ? 1 : 0), w < 6 ? 1 : 2 + w / 6);
            n = Mathf.Max(0, Mathf.Min(n, remaining));
            for (int k = 0; k < n; k++) _queue.Add(new Entry { Type = type });
            remaining -= n;
        }
        while (remaining-- > 0) _queue.Add(new Entry { Type = "gnat" });

        // Elites every third wave, boss every fifth.
        if (w >= 3 && w % 3 == 0) {
            int e = Mathf.Min(3, w / 3);
            for (int i = _queue.Count - 1; i >= 0 && e > 0; i--) {
                if (_queue[i].Type == "juggernaut") continue;
                var q = _queue[i]; q.Elite = true; _queue[i] = q; e--;
            }
        }
        if (w >= 5 && w % 5 == 0) _queue.Add(new Entry { Type = "overseer", IsBoss = true });

        HpMultiplier     = 1f + Mathf.Pow(w - 1, 1.12f) * 0.07f;
        // 0.02, down from v2's 0.03. HP and damage still scale on v2's curves; speed is the one
        // that outruns the player rather than out-statting them, and by wave 15 the original put
        // Skirmishers at 10.5 m/s against a 12 m/s walk - close enough that they could not be
        // broken away from and could barely be led.
        SpeedMultiplier  = 1f + (w - 1) * 0.02f;
        DamageMultiplier = 1f + Mathf.Pow(w - 1, 1.05f) * 0.035f;

        // Commanders spawn first so their squads form around them; shuffle the rest.
        var leaders = new List<Entry>();
        var rest = new List<Entry>();
        foreach (var q in _queue) {
            if (q.Type == "warlord" || q.IsBoss) leaders.Add(q); else rest.Add(q);
        }
        for (int i = rest.Count - 1; i > 0; i--) {
            int j = Random.Range(0, i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        _queue.Clear();
        _queue.AddRange(leaders);
        _queue.AddRange(rest);
    }

    public void Tick(float dt, Vector3 playerPos, Vector3 playerVel) {
        if (State == WaveState.Spawning) {
            _spawnTimer -= dt;
            if (_spawnTimer <= 0f && _queue.Count > 0) {
                var entry = _queue[0];
                _queue.RemoveAt(0);
                SpawnEntry(entry, playerPos);
                _spawnTimer = entry.IsBoss ? 1.5f : 0.45f;
            }
            if (_queue.Count == 0) State = WaveState.Active;
        }

        _aliveCount = 0;
        for (int i = Enemies.Count - 1; i >= 0; i--) {
            var e = Enemies[i];
            if (e == null) { Enemies.RemoveAt(i); continue; }
            if (e.Tick(dt, playerPos, playerVel)) {
                Object.Destroy(e.gameObject);
                Enemies.RemoveAt(i);
            } else if (!e.Dead) _aliveCount++;
        }

        if (State == WaveState.Active) {
            _activeTime += dt;
            // Global fallback: nobody hides for more than 75 seconds.
            if (_activeTime > 75f) foreach (var e in Enemies) e.Aggressive = true;
            // And the tail of a wave commits rather than standing off and plinking.
            if (_aliveCount > 0 && _aliveCount <= 2) foreach (var e in Enemies) e.Aggressive = true;
        } else {
            _activeTime = 0f;
        }

        if (State == WaveState.Active && _aliveCount == 0) { State = WaveState.Complete; _stateTimer = 3f; }
        if (State == WaveState.Complete) {
            _stateTimer -= dt;
            if (_stateTimer <= 0f) State = WaveState.Waiting;
        }
    }

    /// <summary>
    /// Graded spawn-point scoring, not a binary near/far test. 30-55 m is the sweet spot: a
    /// threat within a few seconds, but you still see the drop. Far points stay competitive,
    /// because a flat near-beats-far rule funnels every alien in through the same two corners
    /// and reads as a pile-on rather than an assault converging on you.
    /// </summary>
    Vector3 PickSpawn(Vector3 playerPos) {
        if (_spawnPoints.Count == 0) return playerPos + new Vector3(30f, 0f, 30f);

        int bestIndex = 0;
        float bestScore = -1f;
        for (int i = 0; i < _spawnPoints.Count; i++) {
            float d = Vector3.Distance(_spawnPoints[i], playerPos);
            float base_;
            if (d < 18f) base_ = 0f;              // never materialise on top of the player
            else if (d < 30f) base_ = 0.7f;
            else if (d <= 55f) base_ = 1f;
            else if (d <= 75f) base_ = 0.85f;
            else base_ = 0.6f;
            if (i == _lastSpawnIndex) base_ *= 0.45f;
            float score = base_ > 0f ? base_ + Random.value * 0.5f : 0f;
            if (score > bestScore) { bestScore = score; bestIndex = i; }
        }
        _lastSpawnIndex = bestIndex;
        return _spawnPoints[bestIndex]
             + new Vector3((Random.value - 0.5f) * 8f, 0f, (Random.value - 0.5f) * 8f);
    }

    void SpawnEntry(Entry entry, Vector3 playerPos) {
        var pos = PickSpawn(playerPos);

        // Squads land together: a Warlord's next few followers spawn near it.
        if (entry.Type == "warlord") _lastLeaderPos = pos;
        else if (_lastLeaderPos.HasValue
                 && (entry.Type == "gnat" || entry.Type == "skirmisher")
                 && Random.value < 0.6f) {
            pos = _lastLeaderPos.Value
                + new Vector3((Random.value - 0.5f) * 7f, 0f, (Random.value - 0.5f) * 7f);
        }
        SpawnAt(entry.Type, pos, entry.Elite, true);
    }

    /// <summary>
    /// Nudge a spawn out of solid geometry. The city now carries ~700 colliders - street
    /// furniture, parked cars, planters - so an unchecked drop point lands inside something
    /// often enough to matter. Spirals outward for the nearest clear spot and gives up
    /// gracefully rather than refusing to spawn.
    /// </summary>
    Vector3 ResolveSpawn(Vector3 pos, float radius) {
        var arena = _ctx.Arena;
        if (arena == null) return pos;

        bool Blocked(Vector3 c) {
            for (int i = 0; i < arena.Boxes.Count; i++) {
                var b = arena.Boxes[i];
                if (b.isWater || b.max.y < 0.2f) continue;
                if (b.OverlapsXZ(c.x, c.z, radius)) return true;
            }
            return false;
        }

        if (!Blocked(pos)) return pos;
        for (float ring = 2f; ring <= 10f; ring += 2f) {
            for (int i = 0; i < 8; i++) {
                float a = i / 8f * Mathf.PI * 2f;
                var c = new Vector3(pos.x + Mathf.Cos(a) * ring, pos.y, pos.z + Mathf.Sin(a) * ring);
                if (!Blocked(c)) return c;
            }
        }
        return pos;
    }

    public Enemy SpawnAt(string type, Vector3 pos, bool elite, bool dropIn) {
        var data = EnemyRoster.Get(type);
        if (data == null) return null;

        // Aerial units drop in above the street and are never blocked by it.
        if (data.HoverHeight <= 0f) pos = ResolveSpawn(pos, data.Radius + 0.4f);

        var go = new GameObject(type);
        go.transform.SetParent(_parent, false);
        var e = go.AddComponent<Enemy>();
        e.Init(data, pos, _ctx, elite, HpMultiplier, SpeedMultiplier, DamageMultiplier);
        Enemies.Add(e);

        if (dropIn) _ctx.Fx?.SpawnBeam(pos, data.Color);
        OnSpawn?.Invoke(e, dropIn);
        return e;
    }

    /// <summary>
    /// The only correct way to remove a live enemy. Destroying one directly leaves it in the
    /// shared list, where the next separation pass dereferences it and throws.
    /// </summary>
    public void Despawn(Enemy e) {
        if (e == null) return;
        Enemies.Remove(e);
        Object.Destroy(e.gameObject);
    }

    public int AliveCount => _aliveCount;
    public int TotalCount => Enemies.Count;

    public Enemy GetBoss() {
        foreach (var e in Enemies) if (e.IsBoss && !e.Dead) return e;
        return null;
    }

    public void Cleanup() {
        foreach (var e in Enemies) if (e != null) Object.Destroy(e.gameObject);
        Enemies.Clear();
        _queue.Clear();
        _aliveCount = 0;
        _lastLeaderPos = null;
        _lastSpawnIndex = -1;
        State = WaveState.Waiting;
    }

    public void ResetAll() { Cleanup(); Wave = 0; }

    public bool ShouldChangeLevelAfterWave() => Wave > 0 && Wave % 5 == 0;
}

}
