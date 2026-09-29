using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Puts one of each enemy in a controlled fight and reports what it actually did.
///
/// "The AI feels off" is not actionable, and neither is watching a wave from the inside. This
/// spawns a single enemy at a fixed distance from a stationary player, runs it for a fixed time,
/// and reports the things that separate a working role from a broken one: did it close, did it
/// hold its range, did it strafe, did it shoot, did the rig ever leave Idle.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.AiAudit
/// </summary>
public class AiAudit {

    GameManager _game;

    [UnitySetUp]
    public IEnumerator SetUp() {
        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    [UnityTest]
    public IEnumerator Report_Role_Behaviour() {
        _game.StartGame();
        yield return null;
        // One species at a time: the wave StartGame launched would pollute the reading.
        //
        // Emptying the wave puts GameManager into PerkSelect, which stops its Step() - so the
        // audit drives WaveManager and the bolt pool itself. That is the right shape anyway:
        // it isolates the AI from the game's state machine instead of measuring both at once.
        _game.Waves.Cleanup();
        yield return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n===== AI AUDIT =====  one enemy, stationary player, 18 s each");
        sb.AppendLine($"{"type",-12} {"rig",-4} {"clips seen",-26} {"start",5} {"min",5} {"max",5} {"end",5} " +
                      $"{"moved",6} {"strafe",6} {"shots",5} {"dmg",5}");

        foreach (var key in EnemyRoster.All.Keys) {
            var data = EnemyRoster.Get(key);
            var start = _game.Player.transform.position + new Vector3(0f, 0f, 28f);
            var e = _game.Waves.SpawnAt(key, start, false, false);
            Assert.IsNotNull(e, key + " failed to spawn");
            for (int i = 0; i < 4; i++) yield return null;

            float hp0 = _game.Stats.Hp;
            _game.Stats.Hp = _game.Stats.MaxHp = 100000f;      // survive the whole sample

            var clips = new HashSet<string>();
            float minD = float.MaxValue, maxD = 0f, pathLen = 0f, lateral = 0f;
            var prev = e.transform.position;
            float t = 0f;

            float dealt = 0f;
            while (t < 18f && e != null && !e.Dead) {
                float dt = Time.deltaTime;
                t += dt;

                // Drive the sim directly; GameManager is parked in PerkSelect.
                _game.Waves.Tick(dt, _game.Player.EyePosition, Vector3.zero);
                var hits = _game.Bolts.Tick(dt, _game.Player.EyePosition);
                for (int h = 0; h < hits.Count; h++) dealt += hits[h].Damage;

                yield return null;
                if (e == null) break;

                if (!string.IsNullOrEmpty(e.AnimClip)) clips.Add(e.AnimClip);

                var p = e.transform.position;
                var toPlayer = _game.Player.transform.position - p;
                toPlayer.y = 0f;
                float d = toPlayer.magnitude;
                minD = Mathf.Min(minD, d);
                maxD = Mathf.Max(maxD, d);

                var step = p - prev;
                step.y = 0f;
                pathLen += step.magnitude;
                // Movement perpendicular to the player direction: what strafing actually is.
                if (toPlayer.sqrMagnitude > 1e-4f) {
                    var side = Vector3.Cross(Vector3.up, toPlayer.normalized);
                    lateral += Mathf.Abs(Vector3.Dot(step, side));
                }
                prev = p;
            }

            float endD = e != null ? Vector3.Distance(
                new Vector3(e.transform.position.x, 0f, e.transform.position.z),
                new Vector3(_game.Player.transform.position.x, 0f, _game.Player.transform.position.z)) : -1f;
            sb.AppendLine($"{key,-12} {(e != null && e.HasRig ? "yes" : "NO"),-4} " +
                          $"{string.Join("/", clips),-26} {28f,5:F0} {minD,5:F1} {maxD,5:F1} {endD,5:F1} " +
                          $"{pathLen,6:F1} {lateral,6:F1} {(e != null ? e.ShotsFired : 0),5} {dealt,5:F0}");

            if (e != null) _game.Waves.Despawn(e);
            _game.Stats.MaxHp = 100f;
            _game.Stats.Hp = hp0;
            yield return null;
        }

        sb.AppendLine("\nreference: keepDistance / attackRange per type");
        foreach (var kv in EnemyRoster.All)
            sb.AppendLine($"  {kv.Key,-12} keep={kv.Value.KeepDistance,4:F0} range={kv.Value.AttackRange,4:F0} " +
                          $"speed={kv.Value.Speed,4:F1} rate={kv.Value.AttackRate:F2}");

        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Do ground enemies actually stand on the ground?
    ///
    /// They are walked across the dressed city - past benches, planters and parked cars - and the
    /// gap between the bottom of the model and the surface under it is measured every frame. Any
    /// persistent positive gap is the enemy standing on top of a prop rather than walking round it.
    /// </summary>
    [UnityTest]
    public IEnumerator Report_Foot_Contact() {
        _game.StartGame();
        yield return null;
        _game.Waves.Cleanup();
        yield return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n===== FOOT CONTACT =====  ground enemies only, 14 s each");
        sb.AppendLine($"{"type",-12} {"worst gap",10} {"mean gap",10} {"frames > 0.15 m",16}");

        foreach (var key in new[] { "gnat", "skirmisher", "warlord", "juggernaut" }) {
            // Start out among the street furniture rather than on clean pavement.
            var e = _game.Waves.SpawnAt(key, new Vector3(26f, 0f, 30f), false, false);
            Assert.IsNotNull(e, key);
            for (int i = 0; i < 4; i++) yield return null;

            float worst = 0f, sum = 0f;
            int n = 0, bad = 0, t = 0;
            while (t < 14 * 60 && e != null && !e.Dead) {
                float dt = Time.deltaTime;
                _game.Waves.Tick(dt, _game.Player.EyePosition, Vector3.zero);
                _game.Bolts.Tick(dt, _game.Player.EyePosition);
                yield return null;
                t++;
                if (e == null) break;

                var rs = e.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

                // Everything walks on y = 0 except the raised pavement slabs.
                float surface = 0f;
                foreach (var box in _game.Arena.Boxes) {
                    if (!box.walkable) continue;
                    var p2 = e.transform.position;
                    if (p2.x > box.min.x && p2.x < box.max.x && p2.z > box.min.z && p2.z < box.max.z)
                        surface = Mathf.Max(surface, box.max.y);
                }

                float gap = b.min.y - surface;
                worst = Mathf.Max(worst, gap);
                sum += gap; n++;
                if (gap > 0.15f) bad++;
            }

            sb.AppendLine($"{key,-12} {worst,10:F2} {(n > 0 ? sum / n : 0f),10:F2} {bad + " / " + n,16}");
            if (e != null) _game.Waves.Despawn(e);
            yield return null;
        }
        Debug.Log(sb.ToString());
    }

    /// <summary>What the rigs can actually play, independent of whether the AI drives them.</summary>
    [UnityTest]
    public IEnumerator Report_Rig_Clips() {
        _game.StartGame();
        yield return null;
        _game.Waves.Cleanup();
        yield return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n===== RIG CLIPS =====");
        foreach (var key in EnemyRoster.All.Keys) {
            var e = _game.Waves.SpawnAt(key, _game.Player.transform.position + new Vector3(40f, 0f, 40f), false, false);
            for (int i = 0; i < 4; i++) yield return null;
            sb.AppendLine($"  {key,-12} rig={(e.HasRig ? "yes" : "NO ")}  clips=[{string.Join(", ", e.AnimClips)}]");
            _game.Waves.Despawn(e);
            yield return null;
        }
        Debug.Log(sb.ToString());
    }
}

}
