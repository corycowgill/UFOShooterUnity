using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Reports what glTFast actually produced for a model's materials.
///
/// This exists because LevelBuilder re-grades every building at spawn: GradeMaterials multiplies
/// _BaseColor by a random stone/brick/glass tint, and LightWindows overwrites _EmissionMap with
/// the albedo under a random warm/cold tint. Both were written for the old pack models, which
/// carried no art direction of their own and needed procedural variety to stop a block reading
/// as one asset repeated. The Chicago assets DO carry their own art direction - a white hospital,
/// a blue police station, a red firehouse, each with a real emissive map - so if those two passes
/// still apply, they will grey out the palettes and throw the emissive maps away.
///
/// Whether they apply at all depends on which shader glTFast picks and which properties it
/// exposes, which is not worth guessing at. This prints it.
///
///   Unity.exe -batchmode -nographics -quit -projectPath . \
///             -executeMethod UFO.EditorTools.MaterialDiagnose.Run -logFile Logs/mat.log
/// </summary>
public static class MaterialDiagnose {

    static readonly string[] Probe = {
        "Assets/Resources/Models/props/building_hospital.gltf",
        "Assets/Resources/Models/props/building_police_station.gltf",
        "Assets/Resources/Models/props/building_fire_station.gltf",
        "Assets/Resources/Models/props/building_residential_013.gltf",
        "Assets/Resources/Models/props/hydrant.gltf",
    };

    [MenuItem("UFO/Diagnose/Model Materials")]
    public static void Run() {
        foreach (var path in Probe) {
            if (!File.Exists(path)) { Debug.Log($"[Mat] MISSING {path}"); continue; }
            var mats = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().ToList();
            if (mats.Count == 0) { Debug.Log($"[Mat] {Path.GetFileName(path)}: no materials"); continue; }

            foreach (var m in mats) {
                // The two properties LevelBuilder keys off, plus glTFast's own names.
                string Has(string p) => m.HasProperty(p) ? "yes" : "no";
                string Tex(string p) {
                    if (!m.HasProperty(p)) return "-";
                    var t = m.GetTexture(p);
                    return t == null ? "null" : $"{t.name} {((Texture2D)t).width}px";
                }
                Debug.Log(
                    $"[Mat] {Path.GetFileName(path)} / {m.name}\n" +
                    $"      shader        : {m.shader.name}\n" +
                    $"      _BaseColor    : {Has("_BaseColor")}   _BaseMap: {Tex("_BaseMap")}\n" +
                    $"      _EmissionMap  : {Tex("_EmissionMap")}   _EMISSION keyword: {m.IsKeywordEnabled("_EMISSION")}\n" +
                    $"      emissiveTexture (glTFast): {Tex("emissiveTexture")}\n" +
                    $"      emissiveFactor: {(m.HasProperty("emissiveFactor") ? m.GetColor("emissiveFactor").ToString() : "-")}   " +
                    $"_EmissionColor: {(m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor").ToString() : "-")}");
            }
        }
    }
}

}
