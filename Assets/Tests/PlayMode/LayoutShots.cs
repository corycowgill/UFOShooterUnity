using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Renders each level from directly overhead and from a few eye-level positions, straight to PNG.
///
/// A first-person screenshot is a bad way to judge a city block: you see four props and a wall.
/// A top-down orthographic render shows the grid, and anything misaligned - a tree inside a
/// building, cars off the kerb, furniture in the road - is obvious at a glance.
///
/// Runs in batchmode WITHOUT -nographics so there is a real graphics device to render with:
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.LayoutShots
/// </summary>
public class LayoutShots {

    GameManager _game;
    string _outDir;

    [UnitySetUp]
    public IEnumerator SetUp() {
        // These tests render to a RenderTexture, which needs a real graphics device. The normal
        // test run uses -nographics, so skip rather than fail: this suite is a reporting tool, run
        // deliberately without -nographics, not part of the pass/fail gate.
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("needs a graphics device; run Unity without -nographics");

        _outDir = Path.Combine(Directory.GetCurrentDirectory(), "shots-layout");
        Directory.CreateDirectory(_outDir);

        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    Camera MakeShotCamera() {
        var go = new GameObject("ShotCam");
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 900f;
        // URP disables post-processing on any camera created in code, so a shot camera renders
        // with no tonemapping, bloom, vignette or colour grading - nothing like what the player
        // sees. Every frame judged from these suites before this was missing the whole post stack.
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;

        // Effects that depend on where the viewer is - the particle near-fade, the emitters'
        // distance culling - read Fx.Cam, which is the game camera. For a shot suite the shot
        // camera IS the viewer, so point it here or every frame is judged against a viewpoint
        // nobody is looking from.
        if (_game != null && _game.Fx != null) _game.Fx.Cam = cam;
        return cam;
    }

    void Capture(Camera cam, string file, int w = 1400, int h = 1400) {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 2;
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

    [UnityTest]
    public IEnumerator Render_Level_Maps() {
        var cam = MakeShotCamera();

        for (int li = 0; li < LevelData.Levels.Length; li++) {
            _game.Level.Build(li);
            // Weather is normally configured by GameManager.LoadLevel; building the level
            // directly bypasses it, and River North without its rain is the wrong picture.
            _game.Weather.Configure(LevelData.Levels[li].Weather, _game.Player.Cam.transform);
            for (int i = 0; i < 3; i++) yield return null;

            var def = LevelData.Levels[li];
            string slug = def.Name.ToLowerInvariant().Replace(' ', '-');

            // Straight down, orthographic: the block grid as a plan view.
            cam.orthographic = true;
            cam.orthographicSize = 120f;
            cam.transform.position = new Vector3(0f, 400f, 0f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Capture(cam, $"{li}-{slug}-plan.png");

            // Tighter plan over the centre four blocks, where the player actually fights.
            cam.orthographicSize = 55f;
            Capture(cam, $"{li}-{slug}-plan-close.png");

            // A raised three-quarter view: shows building heights and the skyline together.
            cam.orthographic = false;
            cam.fieldOfView = 60f;
            cam.transform.position = new Vector3(-110f, 85f, -110f);
            cam.transform.rotation = Quaternion.LookRotation(new Vector3(1f, -0.45f, 1f).normalized, Vector3.up);
            Capture(cam, $"{li}-{slug}-aerial.png", 1400, 800);

            // One eye-level frame per level: the plan views cannot show weather or lighting.
            cam.fieldOfView = 75f;
            cam.transform.position = new Vector3(0f, 1.7f, 4f);
            cam.transform.rotation = Quaternion.Euler(2f, 200f, 0f);
            float dwell = 0f;
            while (dwell < 1.4f) { dwell += Time.deltaTime; yield return null; }
            Capture(cam, $"{li}-{slug}-eye.png", 1280, 800);

            Debug.Log($"[Shots] {def.Name}: plan, plan-close, aerial, eye ({def.Weather})");
        }
        Object.DestroyImmediate(cam.gameObject);
    }

    [UnityTest]
    public IEnumerator Render_Eye_Level_Tour() {
        var cam = MakeShotCamera();
        cam.orthographic = false;
        cam.fieldOfView = 75f;

        _game.Level.Build(0);
        for (int i = 0; i < 3; i++) yield return null;

        // A tour of the places a player actually stands: the plaza, a street, an intersection,
        // an alley, and the kerb beside the parked cars.
        var stops = new (string name, Vector3 pos, float yaw, float pitch)[] {
            ("plaza-centre",   new Vector3(0f, 1.7f, 0f),     0f,   4f),
            ("plaza-corner",   new Vector3(14f, 1.7f, 14f),   215f, 4f),
            ("street-ns",      new Vector3(22f, 1.7f, 0f),    0f,   4f),
            ("street-ew",      new Vector3(0f, 1.7f, 22f),    90f,  4f),
            ("intersection",   new Vector3(22f, 1.7f, 22f),   225f, 4f),
            ("alley",          new Vector3(59f, 1.7f, 56f),   180f, 4f),
            ("kerb",           new Vector3(19f, 1.7f, 40f),   90f,  4f),
            ("looking-north",  new Vector3(0f, 1.7f, 30f),    180f, 4f),
            // The crash site and a burning wreck: the only places the fire VFX can be judged.
            ("crash-site",     new Vector3(-16f, 1.7f, -34f), 25f,  4f),
            ("fire",           new Vector3(-6f, 1.7f, -20f),  185f, 4f),
            // Two stops that look UP. Everything above the shopfronts - rooflines, masts, water
            // towers, wires, billboards and the sky itself - is invisible in a level-pitch frame,
            // and it is half of what the player sees while backing away from something.
            ("rooftops",       new Vector3(22f, 1.7f, -8f),   135f, -26f),
            ("sky",            new Vector3(0f, 1.7f, 6f),     170f, -34f),
            // Straight down at the road, from head height: the only way to see what the surface
            // dressing actually is rather than what it looks like at a grazing angle.
            ("road-close",     new Vector3(22f, 9f, -6f),     0f,   88f),
        };

        foreach (var (name, pos, yaw, pitch) in stops) {
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            // Continuous effects need time on the clock to build up: capturing after three frames
            // catches roughly one flame puff and makes a working fire look like nothing at all.
            float t = 0f;
            while (t < 1.2f) { t += Time.deltaTime; yield return null; }
            Capture(cam, $"tour-{name}.png", 1280, 800);
            Debug.Log($"[Shots] tour {name} at {pos} yaw {yaw} pitch {pitch}");
        }
        Object.DestroyImmediate(cam.gameObject);
    }
}

}
