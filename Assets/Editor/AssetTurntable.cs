using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Renders single models on a neutral stage, deliberately matching the three.js viewer in
/// alienGame/tools/compare-models.mjs frame for frame: same 32 degree FOV, same camera at
/// (0.95, 0.72, 1.45) * radius looking at the centred model, same hemisphere + key + fill rig,
/// same background, no fog.
///
/// The point is to separate two questions that got conflated. "Does the asset look good" is
/// about the model and its textures. "Does the city look good" is about fog, distance, mip
/// selection, night grading and the 1.45x block stretch LevelBuilder applies - none of which a
/// hero-framed preview can predict, which is exactly how a preview came to be presented as
/// evidence of in-game quality when it was nothing of the sort.
///
/// Matching the viewer means any difference in these frames is Unity's import and shading, not
/// the scene. Run WITHOUT -nographics; it needs a real device to render.
///
///   Unity.exe -batchmode -quit -projectPath . \
///             -executeMethod UFO.EditorTools.AssetTurntable.Run -logFile Logs/turn.log
/// </summary>
public static class AssetTurntable {

    const int Size = 500;

    static readonly string[] Models = {
        "Models/props/building_residential_013",
        "Models/props/building_hospital",
        "Models/props/police_car",
        "Models/weapons/rifle",
    };

    [MenuItem("UFO/Diagnose/Asset Turntable")]
    public static void Run() {
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "turntable");
        Directory.CreateDirectory(outDir);

        var stage = new GameObject("TurntableStage");
        try {
            // Same three-light rig as the viewer, same intensities.
            var hemi = new GameObject("Hemi").AddComponent<Light>();
            hemi.transform.SetParent(stage.transform);
            hemi.type = LightType.Directional; hemi.intensity = 0f;   // ambient stands in below
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(1f, 1f, 1f) * 0.95f;
            RenderSettings.ambientEquatorColor = new Color(0.68f, 0.74f, 0.84f);
            RenderSettings.ambientGroundColor = new Color(0.10f, 0.12f, 0.16f);
            RenderSettings.fog = false;

            var key = new GameObject("Key").AddComponent<Light>();
            key.transform.SetParent(stage.transform);
            key.type = LightType.Directional; key.intensity = 1.5f;
            key.color = new Color(1f, 0.95f, 0.88f);
            key.transform.rotation = Quaternion.LookRotation(new Vector3(-3f, -6f, -4f));

            var fill = new GameObject("Fill").AddComponent<Light>();
            fill.transform.SetParent(stage.transform);
            fill.type = LightType.Directional; fill.intensity = 0.6f;
            fill.color = new Color(0.62f, 0.73f, 0.90f);
            fill.transform.rotation = Quaternion.LookRotation(new Vector3(4f, -2f, 3f));

            var camGo = new GameObject("TurnCam");
            camGo.transform.SetParent(stage.transform);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 32f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(0x20, 0x28, 0x30, 0xff);   // viewer's #202830
            cam.allowHDR = true;

            foreach (var res in Models) {
                var prefab = Resources.Load<GameObject>(res);
                if (prefab == null) { Debug.Log($"[Turn] MISSING {res}"); continue; }
                var obj = Object.Instantiate(prefab, stage.transform);

                // Centre on the origin and frame exactly as the viewer does.
                var bounds = Bounds(obj);
                obj.transform.position -= bounds.center;
                float r = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (r <= 0f) r = 1f;
                cam.transform.position = new Vector3(r * 0.95f, r * 0.72f, r * 1.45f);
                cam.transform.LookAt(Vector3.zero);
                cam.nearClipPlane = r / 200f; cam.farClipPlane = r * 200f;

                var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32,
                                           RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var shot = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                shot.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;

                var name = res.Substring(res.LastIndexOf('/') + 1);
                File.WriteAllBytes(Path.Combine(outDir, name + "_unity.png"), shot.EncodeToPNG());
                Object.DestroyImmediate(shot);
                rt.Release(); Object.DestroyImmediate(rt);
                Object.DestroyImmediate(obj);
                Debug.Log($"[Turn] {name}: radius {r:F2}");
            }
        } finally {
            Object.DestroyImmediate(stage);
        }
        Debug.Log("[Turn] frames written to turntable/");
    }

    static Bounds Bounds(GameObject go) {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }
}

}
