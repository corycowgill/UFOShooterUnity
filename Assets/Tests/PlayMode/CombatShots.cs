using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Photographs the enemies, from the player's eye, while they are actually fighting.
///
/// This suite exists because of a hole in the others. LayoutShots renders the level empty,
/// PropAudit measures props, and AiAudit measures behaviour as numbers - so every existing check
/// on the aliens is either arithmetic or a picture with no alien in it. The browser capture bot
/// was supposed to cover the rest, and it cannot: it sweeps a 60 degree arc while Gnats converge
/// from 30-55 m and rarely lands a kill, so nothing ever gets close enough to see. The result is
/// that the enemies - the thing a shooter is mostly looking at - have never been looked at.
///
/// Numbers will not catch a model facing backwards, a walk cycle sliding over the ground, a death
/// that pops out of existence, or a silhouette that reads as a smudge at 20 m. Those are the
/// defects that live here.
///
/// Like LayoutShots this is a reporting tool, not part of the pass/fail gate, and it needs a real
/// graphics device:
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.CombatShots
/// </summary>
public class CombatShots {

    GameManager _game;
    string _outDir;

    [UnitySetUp]
    public IEnumerator SetUp() {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("needs a graphics device; run Unity without -nographics");

        _outDir = Path.Combine(Directory.GetCurrentDirectory(), "shots-combat");
        Directory.CreateDirectory(_outDir);

        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    Camera MakeShotCamera() {
        var go = new GameObject("CombatShotCam");
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = 75f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 600f;
        // URP leaves post-processing OFF on any camera built in code, which would render these
        // without tonemapping, bloom or grading - not what the player sees, and not worth judging.
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        return cam;
    }

    void Capture(Camera cam, string file, int w = 1280, int h = 800) {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2 };
        cam.targetTexture = rt;
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        File.WriteAllBytes(Path.Combine(_outDir, file), tex.EncodeToPNG());

        cam.targetTexture = null;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    /// <summary>Park the game's own state machine so this suite drives the sim itself.</summary>
    IEnumerator Isolate() {
        _game.StartGame();
        yield return null;
        _game.Waves.Cleanup();
        yield return null;
        // Invulnerable: a dead player ends the run halfway through the roster.
        _game.Stats.Hp = _game.Stats.MaxHp = 100000f;
    }

    void Aim(Camera cam, Enemy e) {
        var eye = _game.Player.EyePosition;
        cam.transform.position = eye;
        var look = e.CapsuleBase + Vector3.up * (e.CapsuleHeight * 0.55f);
        var dir = look - eye;
        if (dir.sqrMagnitude > 1e-6f) cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
    }

    /// <summary>
    /// One enemy at a time, walking in from 30 m, photographed twice: once at 20 m, where the
    /// question is whether the silhouette reads against a busy city at all, and once at whatever
    /// range the type settles at, which is the distance the player will really be fighting it from.
    /// </summary>
    [UnityTest]
    public IEnumerator Render_Enemy_Approach() {
        yield return Isolate();
        var cam = MakeShotCamera();
        var log = new StringBuilder();
        log.AppendLine("\n===== COMBAT SHOTS: approach =====");

        // Fixed ranges do not work here: every ranged role stops at its own keepDistance and
        // holds - a Gnat at 10 m, a Skirmisher at 14, an Overseer at 18 - so asking for a 4 m
        // shot of them is asking for a frame that will never happen. Only the Warlord closes to
        // melee. So: one shot at 20 m, one at the range the type actually chooses to fight from.

        foreach (var key in EnemyRoster.All.Keys) {
            var start = _game.Player.transform.position + new Vector3(0f, 0f, 30f);
            var e = _game.Waves.SpawnAt(key, start, false, false);
            if (e == null) { log.AppendLine($"  {key,-12} FAILED TO SPAWN"); continue; }
            for (int i = 0; i < 6; i++) yield return null;

            bool shotFar = false;
            float closest = float.MaxValue;
            float t = 0f;
            while (t < 22f && e != null && !e.Dead) {
                float dt = Time.deltaTime;
                t += dt;
                _game.Waves.Tick(dt, _game.Player.EyePosition, Vector3.zero);
                _game.Bolts.Tick(dt, _game.Player.EyePosition);
                yield return null;
                if (e == null) break;

                var flat = e.transform.position - _game.Player.transform.position;
                flat.y = 0f;
                float d = flat.magnitude;
                closest = Mathf.Min(closest, d);

                if (!shotFar && d <= 20f) {
                    Aim(cam, e);
                    Capture(cam, $"{key}-far.png");
                    log.AppendLine($"  {key,-12} far   {d,5:0.0} m  clip={e.AnimClip ?? "-",-8} " +
                                   $"rig={(e.HasRig ? "yes" : "NO"),-3} y={e.transform.position.y:0.00}");
                    shotFar = true;
                }
            }

            // Whatever range it settled at, photograph it there. This is the shot that matters:
            // it is the distance the player will actually be looking at this thing from.
            if (e != null && !e.Dead) {
                var flat = e.transform.position - _game.Player.transform.position;
                flat.y = 0f;
                Aim(cam, e);
                Capture(cam, $"{key}-settled.png");
                log.AppendLine($"  {key,-12} held  {flat.magnitude,5:0.0} m  clip={e.AnimClip ?? "-",-8} " +
                               $"keepDist={EnemyRoster.Get(key).KeepDistance:0} closest={closest:0.0}");
            }

            if (e != null) _game.Waves.Despawn(e);
            yield return null;
        }

        Debug.Log(log.ToString());
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>
    /// Death is the animation a player sees most often and the one no numeric audit covers. Kill
    /// each type at close range and photograph the moment after, looking for models that vanish
    /// instantly, fall through the pavement, or freeze upright.
    /// </summary>
    [UnityTest]
    public IEnumerator Render_Deaths() {
        yield return Isolate();
        var cam = MakeShotCamera();
        var log = new StringBuilder();
        log.AppendLine("\n===== COMBAT SHOTS: deaths =====");

        foreach (var key in EnemyRoster.All.Keys) {
            var pos = _game.Player.transform.position + _game.Player.transform.forward * 7f;
            var e = _game.Waves.SpawnAt(key, pos, false, false);
            if (e == null) continue;
            for (int i = 0; i < 6; i++) yield return null;

            Aim(cam, e);
            // ignoreFrontShield, or this photographs the wrong thing entirely. A Skirmisher's arm
            // shield and a Juggernaut's tower shield absorb a frontal hit WHOLE - no overflow to
            // the body, however large it is (v2 does the same; it is the design, not a bug). The
            // first run of this suite hit them head-on and logged them still playing Run and Idle
            // a second later, which reads exactly like a broken death animation and is not.
            e.TakeDamage(999999f, _game.Player.EyePosition, e.CapsuleBase + Vector3.up,
                         explosive: false, knockback: 0f, ignoreFrontShield: true);

            // Sample the corpse over the first second: the fall is where it looks wrong.
            foreach (var wait in new[] { 0.15f, 0.55f, 1.1f }) {
                float t = 0f;
                while (t < wait) {
                    float dt = Time.deltaTime;
                    t += dt;
                    _game.Waves.Tick(dt, _game.Player.EyePosition, Vector3.zero);
                    yield return null;
                }
                if (e == null) break;
                Capture(cam, $"death-{key}-{wait:0.00}s.png");
                log.AppendLine($"  {key,-12} +{wait:0.00}s  clip={e.AnimClip ?? "-",-8} y={e.transform.position.y:0.00}");
            }

            _game.Waves.Cleanup();
            yield return null;
        }

        Debug.Log(log.ToString());
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>
    /// What shader do the imported enemy materials actually use, and do they expose the emission
    /// property the code has been setting? The emission sweep produced identical frames for every
    /// non-zero intensity, which is the signature of a SetColor call landing on a property that
    /// does not exist - Unity silently ignores those.
    /// </summary>
    [UnityTest]
    public IEnumerator Report_Enemy_Materials() {
        yield return Isolate();
        var log = new StringBuilder();
        log.AppendLine();
        log.AppendLine("===== ENEMY MATERIALS =====");

        foreach (var key in EnemyRoster.All.Keys) {
            var e = _game.Waves.SpawnAt(key, _game.Player.transform.position + new Vector3(0f, 0f, 15f),
                                        false, false);
            if (e == null) continue;
            for (int i = 0; i < 6; i++) yield return null;

            var seen = new HashSet<string>();
            foreach (var r in e.GetComponentsInChildren<Renderer>()) {
                foreach (var m in r.materials) {
                    if (m == null || !seen.Add(m.shader.name)) continue;
                    log.AppendLine($"  {key,-12} shader={m.shader.name}");
                    log.AppendLine($"  {"",-12}   _EmissionColor={m.HasProperty("_EmissionColor")}  " +
                                   $"baseColorFactor={m.HasProperty("baseColorFactor")}  " +
                                   $"_BaseColor={m.HasProperty("_BaseColor")}");
                    // The properties exist but setting them changes nothing, so the graph must
                    // gate emission behind a keyword. Print the whole keyword space rather than
                    // guessing names one rebuild at a time.
                    log.AppendLine($"  {"",-12}   keywords available: " +
                                   string.Join(", ", m.shader.keywordSpace.keywordNames));
                    log.AppendLine($"  {"",-12}   keywords enabled  : " +
                                   (m.shaderKeywords.Length == 0 ? "(none)" : string.Join(", ", m.shaderKeywords)));
                    log.AppendLine($"  {"",-12}   emission now      : {m.GetColor("_EmissionColor")}");
                }
            }
            _game.Waves.Despawn(e);
            yield return null;
        }

        Debug.Log(log.ToString());
    }

    /// <summary>
    /// Sweeps the threat light so its brightness gets chosen by looking at frames rather than by
    /// picking a number that sounds about right.
    ///
    /// Two bugs in the previous version of this test are worth not repeating. It built its spot
    /// as `Player.transform.position + (0,0,15)`, and the player's transform origin sits at
    /// y=1.88 - so it hung the alien nearly two metres in the air and then measured a crop of
    /// empty pavement, reporting four different intensities as identical for three runs running.
    /// WaveManager.Init already grounds a spawn, so the y must come from the ground, not the
    /// player. And it derived crop coordinates from WorldToScreenPoint, which reports in the
    /// camera's own pixel rect - a different size AND aspect from the RenderTexture being
    /// captured. Aim() frames the enemy reliably; trust it instead.
    /// </summary>
    [UnityTest]
    public IEnumerator Sweep_Threat_Light() {
        yield return Isolate();
        var cam = MakeShotCamera();
        var log = new StringBuilder();
        log.AppendLine();
        log.AppendLine("===== COMBAT SHOTS: threat light sweep =====  gnat at 15 m");

        var p = _game.Player.transform.position;
        var spot = new Vector3(p.x, 0f, p.z + 15f);

        foreach (var k in new[] { 0f, 0.8f, 1.6f, 3.0f }) {
            Enemy.ThreatLightOverride = k;
            var e = _game.Waves.SpawnAt("gnat", spot, false, false);
            if (e == null) { log.AppendLine($"  k={k:0.0}  FAILED TO SPAWN"); continue; }
            for (int f = 0; f < 8; f++) yield return null;

            Aim(cam, e);
            Capture(cam, $"light-{k:0.0}.png");
            log.AppendLine($"  k={k:0.0}  enemy at y={e.transform.position.y:0.00}");

            _game.Waves.Despawn(e);
            yield return null;
        }

        Enemy.ThreatLightOverride = -1f;
        Debug.Log(log.ToString());
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>
    /// A real wave, photographed from the player's eye while it closes. This is the frame the
    /// browser bot was always supposed to produce and never did - several enemies at mixed ranges
    /// at once, which is where readability problems actually show up.
    /// </summary>
    [UnityTest]
    public IEnumerator Render_Firefight() {
        yield return Isolate();
        var cam = MakeShotCamera();
        var log = new StringBuilder();
        log.AppendLine("\n===== COMBAT SHOTS: firefight =====");

        // A mix with something short, something fast, something airborne and something big, so
        // the frame has to cope with the whole silhouette range at once.
        var mix = new (string key, Vector3 at)[] {
            ("gnat",       new Vector3(-6f, 0f, 16f)),
            ("gnat",       new Vector3( 7f, 0f, 21f)),
            ("skirmisher", new Vector3( 2f, 0f, 13f)),
            ("wasp",       new Vector3(-9f, 0f, 24f)),
            ("juggernaut", new Vector3(11f, 0f, 27f)),
        };
        var live = new List<Enemy>();
        foreach (var (key, at) in mix) {
            var e = _game.Waves.SpawnAt(key, _game.Player.transform.position + at, false, false);
            if (e != null) live.Add(e);
        }
        for (int i = 0; i < 6; i++) yield return null;

        cam.transform.position = _game.Player.EyePosition;
        cam.transform.rotation = Quaternion.Euler(2f, 0f, 0f);   // level, facing +Z into the mix

        float elapsed = 0f;
        int shot = 0;
        foreach (var mark in new[] { 0.2f, 2.5f, 5f, 8f }) {
            while (elapsed < mark) {
                float dt = Time.deltaTime;
                elapsed += dt;
                _game.Waves.Tick(dt, _game.Player.EyePosition, Vector3.zero);
                _game.Bolts.Tick(dt, _game.Player.EyePosition);
                yield return null;
            }
            cam.transform.position = _game.Player.EyePosition;
            Capture(cam, $"firefight-{++shot}.png");

            int alive = 0;
            foreach (var e in live) if (e != null && !e.Dead) alive++;
            log.AppendLine($"  t={mark:0.0}s  alive={alive}/{live.Count}  shot {shot}");
        }

        Debug.Log(log.ToString());
        Object.DestroyImmediate(cam.gameObject);
    }
}

}
