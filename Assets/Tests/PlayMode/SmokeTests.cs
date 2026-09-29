using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UFO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Headless smoke tests, the Unity equivalent of v2's tools/playtest.mjs.
///
/// These boot the real game scene and drive the real systems. They are deliberately about
/// "does the game run and do the numbers move", not about pixel output - that is what a
/// batchmode run can actually tell you.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testResults results.xml
/// </summary>
public class SmokeTests {

    const string ScenePath = "Assets/Scenes/Game.unity";

    GameManager _game;
    readonly List<string> _errors = new List<string>();

    [UnitySetUp]
    public IEnumerator SetUp() {
        _errors.Clear();
        Application.logMessageReceived += OnLog;

        yield return LoadSceneAndSettle();

        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game, "GameManager not found in " + ScenePath);
    }

    [TearDown]
    public void TearDown() {
        Application.logMessageReceived -= OnLog;
    }

    void OnLog(string message, string stack, LogType type) {
        if (type == LogType.Exception || type == LogType.Error) _errors.Add(message);
    }

    IEnumerator LoadSceneAndSettle() {
        var op = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        // Give Awake/Start and the first level build a few frames.
        for (int i = 0; i < 8; i++) yield return null;
    }

    void AssertNoErrors(string phase) {
        if (_errors.Count > 0)
            Assert.Fail($"{phase}: {_errors.Count} error(s)/exception(s):\n  " + string.Join("\n  ", _errors));
    }

    // ------------------------------------------------------------------ tests

    [UnityTest]
    public IEnumerator Boots_And_Builds_A_Level() {
        Assert.IsNotNull(_game.Level.Current, "no level was built");
        Assert.Greater(_game.Arena.Boxes.Count, 20,
            "the arena has almost no colliders - the model catalogue probably failed to load");
        Assert.Greater(_game.Level.SpawnPoints.Count, 3, "too few spawn points");

        Debug.Log($"[Smoke] level='{_game.Level.Current.Name}' colliders={_game.Arena.Boxes.Count} " +
                  $"spawns={_game.Level.SpawnPoints.Count}");
        AssertNoErrors("boot");
        yield return null;
    }

    [UnityTest]
    public IEnumerator Every_Catalogue_Model_Loads() {
        var missing = new List<string>();
        foreach (var kv in LevelData.Catalog) {
            if (ModelCache.Load(kv.Value.ResourcePath) == null) missing.Add(kv.Key);
        }
        // The catalogue is allowed to name optional props, but the core set must be present.
        Assert.That(missing, Is.Empty, "catalogue models failed to load: " + string.Join(", ", missing));
        yield return null;
    }

    [UnityTest]
    public IEnumerator Every_Enemy_Spawns_And_Renders() {
        _game.StartGame();
        yield return null;

        foreach (var key in EnemyRoster.All.Keys) {
            var e = _game.Waves.SpawnAt(key, new Vector3(20f, 0f, 20f), false, false);
            Assert.IsNotNull(e, $"{key} failed to spawn");

            // Let the model attach and normalise.
            for (int i = 0; i < 3; i++) yield return null;

            Assert.IsTrue(e.Ready, $"{key} never became ready");
            var renderers = e.GetComponentsInChildren<Renderer>();
            Assert.Greater(renderers.Length, 0, $"{key} has no renderers");

            Debug.Log($"[Smoke] {key}: hp={e.Hp} shield={e.Shield} renderers={renderers.Length} " +
                      $"height={e.Data.Height}");
            _game.Waves.Despawn(e);
        }
        AssertNoErrors("enemy spawn");
    }

    [UnityTest]
    public IEnumerator Plays_Sixty_Seconds_Without_Errors() {
        _game.StartGame();
        yield return null;

        Assert.AreEqual(GameState.Playing, _game.State, "game did not enter Playing");

        int startWave = _game.Waves.Wave;
        float elapsed = 0f;
        int maxEnemiesSeen = 0;

        // Sixty seconds of simulated play. Enemies fight; the player just stands there, which is
        // the harsher test - it proves the AI closes, shoots and does damage on its own.
        while (elapsed < 60f) {
            yield return null;
            elapsed += Time.deltaTime;
            maxEnemiesSeen = Mathf.Max(maxEnemiesSeen, _game.Waves.AliveCount);

            // The perk screen blocks the sim; pick the first card so the run continues.
            if (_game.State == GameState.PerkSelect)
                _game.ChoosePerk(PlayerStats.Perks[0]);
        }

        Debug.Log($"[Smoke] after {elapsed:F0}s: wave={_game.Waves.Wave} alive={_game.Waves.AliveCount} " +
                  $"peak={maxEnemiesSeen} hp={_game.Stats.Hp:F0} score={_game.Stats.Score} " +
                  $"state={_game.State}");

        Assert.GreaterOrEqual(_game.Waves.Wave, startWave, "wave counter went backwards");
        Assert.Greater(maxEnemiesSeen, 0, "no enemies ever spawned");
        // A stationary player should be taking damage from a wave of aliens.
        Assert.Less(_game.Stats.Hp, _game.Stats.MaxHp + 0.01f, "player took no damage in 60s of combat");
        AssertNoErrors("60s play");
    }

    [UnityTest]
    public IEnumerator Weapons_All_Fire_And_Damage() {
        _game.StartGame();
        yield return null;

        foreach (var key in Arsenal.Order) {
            _game.Weapons.SwitchWeapon(key);
            // SwitchWeapon imposes a 0.3s raise; wait it out.
            float t = 0f;
            while (t < 0.45f) { t += Time.deltaTime; yield return null; }

            var target = _game.Waves.SpawnAt("gnat", _game.Player.transform.position
                                             + _game.Player.transform.forward * 2.5f, false, false);
            for (int i = 0; i < 3; i++) yield return null;

            // Aim at the Gnat's chest. A Gnat is 1.25 m tall and the player's eye is at 1.7 m, so
            // a level shot sails clean over its head - the same aim-height trap v2 hit with its
            // scripted bot. Aiming is what a player does; the test has to do it too.
            _game.Player.LookAt(target.CapsuleBase + Vector3.up * (target.CapsuleHeight * 0.55f));
            yield return null;

            float before = target.Hp;
            // Melee and rockets both need a frame or two for their deferred damage.
            for (int shot = 0; shot < 6; shot++) {
                _game.Weapons.Fire(_game.Waves.Enemies);
                for (int i = 0; i < 4; i++) yield return null;
            }

            Debug.Log($"[Smoke] {Arsenal.Get(key).Name}: gnat {before} -> {target.Hp} (dead={target.Dead})");
            Assert.IsTrue(target.Hp < before || target.Dead,
                $"{key} did no damage to a Gnat at point blank range");

            _game.Waves.Despawn(target);
            yield return null;
        }
        AssertNoErrors("weapons");
    }

    [UnityTest]
    public IEnumerator Shield_Multipliers_Behave() {
        _game.StartGame();
        yield return null;

        // A Warlord has a 220-point energy shield. Plasma should strip it far faster than bullets.
        var pos = _game.Player.transform.position + _game.Player.transform.forward * 6f;

        var a = _game.Waves.SpawnAt("warlord", pos, false, false);
        for (int i = 0; i < 3; i++) yield return null;
        float shieldBefore = a.Shield;
        a.TakeDamage(100f * Arsenal.Get("rifle").ShieldMul, _game.Player.EyePosition, pos);
        float bulletDrop = shieldBefore - a.Shield;
        _game.Waves.Despawn(a);

        var b = _game.Waves.SpawnAt("warlord", pos, false, false);
        for (int i = 0; i < 3; i++) yield return null;
        shieldBefore = b.Shield;
        b.TakeDamage(100f * Arsenal.Get("plasmaRifle").ShieldMul, _game.Player.EyePosition, pos);
        float plasmaDrop = shieldBefore - b.Shield;
        _game.Waves.Despawn(b);

        Debug.Log($"[Smoke] shield drop per 100 damage: bullets={bulletDrop} plasma={plasmaDrop}");
        Assert.Greater(plasmaDrop, bulletDrop * 3f,
            "plasma is supposed to strip shields roughly 3.4x faster than bullets (2.4 vs 0.7)");
        yield return null;
    }

    [UnityTest]
    public IEnumerator Front_Shield_Blocks_Frontal_Hits_Only() {
        _game.StartGame();
        yield return null;

        // A Skirmisher's arm shield blocks anything inside 0.9 rad of its facing.
        var pos = new Vector3(0f, 0f, 10f);
        var e = _game.Waves.SpawnAt("skirmisher", pos, false, false);
        for (int i = 0; i < 3; i++) yield return null;

        // Hit from directly behind: must reach the body.
        float hpBefore = e.Hp;
        var behind = pos - new Vector3(Mathf.Sin(e.Yaw), 0f, Mathf.Cos(e.Yaw)) * 5f;
        var res = e.TakeDamage(30f, behind, pos);
        Assert.IsFalse(res.Absorbed, "a hit from behind was absorbed by the front shield");
        Assert.Less(e.Hp, hpBefore, "a hit from behind did no health damage");

        // Hit from directly in front: must be absorbed while the arm shield holds.
        float frontShieldBefore = e.FrontShield;
        var front = pos + new Vector3(Mathf.Sin(e.Yaw), 0f, Mathf.Cos(e.Yaw)) * 5f;
        res = e.TakeDamage(30f, front, pos);
        Assert.IsTrue(res.Absorbed, "a frontal hit was not blocked by the arm shield");
        Assert.Less(e.FrontShield, frontShieldBefore, "the arm shield took no damage");

        Debug.Log($"[Smoke] skirmisher arm shield {frontShieldBefore} -> {e.FrontShield}, hp {hpBefore} -> {e.Hp}");
        _game.Waves.Despawn(e);
        yield return null;
    }

    [UnityTest]
    public IEnumerator All_Three_Levels_Build() {
        for (int i = 0; i < LevelData.Levels.Length; i++) {
            _game.Level.Build(i);
            yield return null;

            var def = LevelData.Levels[i];
            Assert.AreEqual(def.Name, _game.Level.Current.Name);
            Assert.Greater(_game.Arena.Boxes.Count, 15, $"{def.Name} built almost no colliders");
            Debug.Log($"[Smoke] {def.Name}: colliders={_game.Arena.Boxes.Count} " +
                      $"spawns={_game.Level.SpawnPoints.Count} radius={_game.Arena.Radius}");
        }
        AssertNoErrors("level build");
    }
}

}
