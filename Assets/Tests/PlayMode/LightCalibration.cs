using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Renders the same frame at several ambient levels so the right one can be picked by measurement
/// instead of by argument.
///
/// The plaza has measured roughly half of v2's brightness through every pass, and each attempt to
/// close it was a guess followed by a full rebuild. Sweeping the parameter in one run and
/// measuring the frames afterwards turns that into a single decision.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.LightCalibration
/// </summary>
public class LightCalibration {

    GameManager _game;
    string _outDir;

    [UnitySetUp]
    public IEnumerator SetUp() {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("needs a graphics device; run Unity without -nographics");

        _outDir = Path.Combine(Directory.GetCurrentDirectory(), "shots-calib");
        Directory.CreateDirectory(_outDir);

        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    [UnityTest]
    public IEnumerator Sweep_Ambient() {
        var cam = new GameObject("CalibCam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = 75f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 900f;
        // The plaza, looking across it: the exact view the reference frame was measured on.
        cam.transform.position = new Vector3(0f, 1.7f, 0f);
        cam.transform.rotation = Quaternion.Euler(3f, 0f, 0f);

        float[] scales = { 0.45f, 0.70f, 0.95f, 1.20f };
        foreach (var scale in scales) {
            LevelBuilder.AmbientScaleOverride = scale;
            _game.Level.Build(0);
            for (int i = 0; i < 4; i++) yield return null;

            Capture(cam, $"ambient-{scale:0.00}.png");
            Debug.Log($"[Calib] ambient scale {scale:0.00} -> ambient-{scale:0.00}.png " +
                      $"(sky={RenderSettings.ambientSkyColor}, eq={RenderSettings.ambientEquatorColor})");
        }

        LevelBuilder.AmbientScaleOverride = -1f;
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>
    /// Sweep exposure. The ambient sweep showed that tripling ambient buys almost nothing, and
    /// that the ground AND the buildings are both about half v2's reading - which is the signature
    /// of a global exposure difference rather than a light that is missing from the scene.
    /// </summary>
    [UnityTest]
    public IEnumerator Sweep_Exposure() {
        var cam = new GameObject("CalibCam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = 75f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 900f;
        // URP disables post-processing on any camera created in code, so a shot camera renders
        // with no tonemapping, bloom, vignette or colour grading - nothing like what the player
        // sees. Every frame judged from these suites before this was missing the whole post stack.
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        cam.transform.position = new Vector3(0f, 1.7f, 0f);
        cam.transform.rotation = Quaternion.Euler(3f, 0f, 0f);

        _game.Level.Build(0);
        for (int i = 0; i < 4; i++) yield return null;

        float restore = GameSettings.Instance.Brightness;
        float[] ev = { 0.05f, 0.45f, 0.75f, 1.05f, 1.35f };
        foreach (var e in ev) {
            GameSettings.Instance.Brightness = e;
            GameSettings.Instance.Apply();
            for (int i = 0; i < 3; i++) yield return null;
            Capture(cam, $"exposure-{e:0.00}.png");
            Debug.Log($"[Calib] exposure {e:0.00} EV");
        }

        GameSettings.Instance.Brightness = restore;
        GameSettings.Instance.Apply();
        Object.DestroyImmediate(cam.gameObject);
    }

    void Capture(Camera cam, string file) {
        const int w = 1280, h = 800;
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
}

}
