using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.Rendering.Universal;

namespace UFO.Tests {

/// <summary>
/// Renders one fixed eye-level view of THE LOOP at a range of fog densities.
///
/// The skyline towers sit 132-176 m out (BuildSkyline fills grid rows 3-4 at a 44 m pitch), and
/// ExponentialSquared fog at the shipping density of 0.0048 leaves roughly half of that distance
/// as atmosphere - which is most of why the city reads flatter in game than the models do on a
/// turntable. Texture resolution is not the lever at that range; a building 44 m away already
/// samples mip 2-3, so it sees 128-256 px of its 1024 px map whatever the source is.
///
/// Same camera, same frame, only fogDensity changes, so the frames are directly comparable.
///
///   Tools/shots.sh FogSweep.Render_Fog_Ladder      -> shots-fog/
/// </summary>
public class FogSweep {

    // Shipping is 0.0048. Below ~0.002 the arena stops reading as night city at all.
    static readonly float[] Densities = { 0.0048f, 0.0040f, 0.0032f, 0.0024f, 0.0016f };

    GameManager _game;
    string _outDir;

    [UnitySetUp]
    public IEnumerator SetUp() {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("needs a graphics device; run Unity without -nographics");

        _outDir = Path.Combine(Directory.GetCurrentDirectory(), "shots-fog");
        Directory.CreateDirectory(_outDir);

        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    [UnityTest]
    public IEnumerator Render_Fog_Ladder() {
        _game.StartGame();
        for (int i = 0; i < 60; i++) yield return null;

        var go = new GameObject("FogCam");
        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 900f;
        cam.fieldOfView = 75f;
        // Post has to stay on or the frame is nothing like what a player sees - no tonemapping,
        // no bloom, so none of the emissive neon that is meant to punch through the haze.
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        if (_game.Fx != null) _game.Fx.Cam = cam;

        // Eye level, looking down the street at the skyline rather than across the plaza.
        cam.transform.position = new Vector3(0f, 1.7f, 0f);
        cam.transform.rotation = Quaternion.Euler(-2f, 0f, 0f);
        yield return null;

        float restore = RenderSettings.fogDensity;
        foreach (var d in Densities) {
            RenderSettings.fogDensity = d;
            for (int i = 0; i < 3; i++) yield return null;

            var rt = new RenderTexture(1280, 800, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var shot = new Texture2D(1280, 800, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0);
            shot.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            var tag = d.ToString("0.0000").Replace("0.", "");
            File.WriteAllBytes(Path.Combine(_outDir, $"fog-{tag}.png"), shot.EncodeToPNG());
            Object.DestroyImmediate(shot);
            rt.Release(); Object.DestroyImmediate(rt);
            Debug.Log($"[Fog] density {d:0.0000} -> fog-{tag}.png");
        }
        RenderSettings.fogDensity = restore;
        Object.DestroyImmediate(go);
    }
}

}
