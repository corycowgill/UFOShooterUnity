using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Verifies the one claim the whole asset rebuild rests on: that a .gltf referencing loose
/// .png files gets its textures as real Unity assets, so TextureImporter governs their size
/// and compression, instead of the uncompressed RGBA32 sub-assets a .glb produces.
///
/// Run headless:
///   Unity.exe -batchmode -nographics -quit -projectPath . \
///             -executeMethod UFO.EditorTools.AssetPilotCheck.Run -logFile Logs/pilot.log
/// </summary>
public static class AssetPilotCheck {

    const string PilotRoot = "Assets/_AssetPilot";

    public static void Run() {
        if (!Directory.Exists(PilotRoot)) { Debug.Log("[Pilot] no pilot folder"); return; }

        // Apply the same importer settings the real models get, so the measurement is honest.
        foreach (var png in Directory.GetFiles(PilotRoot, "*.png", SearchOption.AllDirectories)) {
            var ti = AssetImporter.GetAtPath(png.Replace('\\', '/')) as TextureImporter;
            if (ti == null) { Debug.LogWarning($"[Pilot] no TextureImporter for {png}"); continue; }
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.mipmapEnabled = true;
            ti.SaveAndReimport();
        }

        foreach (var gltf in Directory.GetFiles(PilotRoot, "*.gltf", SearchOption.AllDirectories)) {
            var p = gltf.Replace('\\', '/');
            var objs = AssetDatabase.LoadAllAssetsAtPath(p);
            if (objs == null || objs.Length == 0) {
                // glTFast stashes the reason in its serialized reportItems rather than the console.
                Debug.LogError($"[Pilot] FAILED TO IMPORT {p}{DumpReport(p)}");
                continue;
            }

            var root = objs.OfType<GameObject>().FirstOrDefault(o => o.transform.parent == null);
            var meshes = objs.OfType<Mesh>().ToList();
            var clips = objs.OfType<AnimationClip>().ToList();
            var embedded = objs.OfType<Texture2D>().ToList();

            int tris = meshes.Sum(m => m.triangles.Length / 3);
            bool skinned = root != null && root.GetComponentInChildren<SkinnedMeshRenderer>() != null;

            // The textures the materials actually sample. If the external-URI path works these
            // resolve to assets OUTSIDE this .gltf, and `embedded` stays empty.
            var mats = objs.OfType<Material>().ToList();
            var texInfo = "";
            foreach (var m in mats) {
                foreach (var id in m.GetTexturePropertyNames()) {
                    var t = m.GetTexture(id) as Texture2D;
                    if (t == null) continue;
                    var tp = AssetDatabase.GetAssetPath(t);
                    bool external = !string.IsNullOrEmpty(tp) && tp != p;
                    texInfo += $"\n      {id}: {t.width}x{t.height} {t.format} mips={t.mipmapCount} " +
                               $"{(external ? "EXTERNAL " + Path.GetFileName(tp) : "EMBEDDED (compression NOT applied)")}";
                }
            }

            Debug.Log($"[Pilot] {Path.GetFileName(p)}: tris={tris} meshes={meshes.Count} clips={clips.Count} " +
                      $"skinned={skinned} embeddedTex={embedded.Count}{texInfo}");
        }
    }

    static string DumpReport(string assetPath) {
        var importer = AssetImporter.GetAtPath(assetPath);
        if (importer == null) return "  (no importer)";
        var so = new SerializedObject(importer);
        var items = so.FindProperty("reportItems");
        if (items == null || !items.isArray) return "  (no reportItems)";
        var sb = "";
        for (int i = 0; i < items.arraySize; i++) {
            var it = items.GetArrayElementAtIndex(i);
            var type = it.FindPropertyRelative("type");
            var code = it.FindPropertyRelative("code");
            var msgs = it.FindPropertyRelative("messages");
            var text = "";
            if (msgs != null && msgs.isArray)
                for (int m = 0; m < msgs.arraySize; m++) text += " " + msgs.GetArrayElementAtIndex(m).stringValue;
            sb += $"\n      [{(type != null ? type.enumDisplayNames[type.enumValueIndex] : "?")}] " +
                  $"code={(code != null ? code.intValue.ToString() : "?")}{text}";
        }
        return sb;
    }
}

}
