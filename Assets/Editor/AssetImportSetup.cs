using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Configures the importers for the 61 glTF models and the loose textures.
///
/// Two settings matter and both are easy to get wrong:
///
/// 1. **Animation method must be Legacy.** The enemy rigs carry seven named clips and the AI
///    picks between them imperatively (see EnemyAnimator). glTFast defaults to Mecanim, which
///    would hand us clips we cannot CrossFade by name without building an AnimatorController per
///    enemy for no benefit.
/// 2. **Texture budget.** The decoded PNGs are large; capping them at 1024 and compressing keeps
///    the WebGL download sane without a visible quality drop at the distances this game plays at.
///
/// glTFast's importer settings are reached through SerializedObject rather than a typed
/// reference, so a package upgrade that renames the C# class does not break the setup pass.
/// </summary>
public static class AssetImportSetup {

    const string ModelRoot = "Assets/Resources/Models";
    const string TextureRoot = "Assets/Resources/Textures";
    const string DecalRoot = "Assets/Resources/Decals";
    const string UiRoot = "Assets/Resources/UI";

    /// <summary>glTFast AnimationMethod enum: 0 = None, 1 = Legacy, 2 = Mecanim.</summary>
    const int AnimationMethodLegacy = 1;

    public static void RunAll() {
        ConfigureModels();
        ConfigureTextures();
        AssetDatabase.SaveAssets();
        Debug.Log("[Import] asset import settings applied");
    }

    [MenuItem("UFO/Setup/Configure Model Importers")]
    public static void ConfigureModels() {
        var files = Directory.GetFiles(ModelRoot, "*.glb", SearchOption.AllDirectories);
        int changed = 0, skipped = 0;

        foreach (var path in files) {
            var assetPath = path.Replace('\\', '/');
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null) { skipped++; continue; }

            var so = new SerializedObject(importer);
            bool dirty = false;

            // importSettings.animationMethod
            dirty |= SetInt(so, "importSettings.animationMethod", AnimationMethodLegacy);
            dirty |= SetBool(so, "importSettings.generateMipMaps", true);
            // Keep the source's own scale; the game normalises every model at spawn.
            dirty |= SetBool(so, "importSettings.anisotropicFilterLevel", false);

            if (dirty) {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                changed++;
            }
        }
        Debug.Log($"[Import] models: {files.Length} found, {changed} reconfigured, {skipped} had no importer");
    }

    static bool SetInt(SerializedObject so, string path, int value) {
        var p = so.FindProperty(path);
        if (p == null) return false;
        if (p.intValue == value) return false;
        p.intValue = value;
        return true;
    }

    static bool SetBool(SerializedObject so, string path, bool value) {
        var p = so.FindProperty(path);
        if (p == null || p.propertyType != SerializedPropertyType.Boolean) return false;
        if (p.boolValue == value) return false;
        p.boolValue = value;
        return true;
    }

    [MenuItem("UFO/Setup/Configure Texture Importers")]
    public static void ConfigureTextures() {
        var roots = new[] { TextureRoot, DecalRoot, UiRoot };
        int n = 0;

        foreach (var root in roots) {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)) {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                var assetPath = path.Replace('\\', '/');
                var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (ti == null) continue;

                ti.maxTextureSize = 1024;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.mipmapEnabled = true;
                ti.wrapMode = root == TextureRoot ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;

                // The ground textures are tiled dozens of times and then viewed at a grazing
                // angle, which drives the sampler into the smallest mip and returns one flat
                // average colour - the street rendered as featureless grey. Anisotropic
                // filtering is the fix; trilinear stops the mip transition banding.
                if (root == TextureRoot) {
                    ti.anisoLevel = 8;
                    ti.filterMode = FilterMode.Trilinear;
                }
                // Decals need their alpha; the tiling ground textures do not.
                // The HUD sheet is sliced into sprites at runtime, so it has to import as one.
                if (root == UiRoot) {
                    ti.textureType = TextureImporterType.Sprite;
                    ti.spriteImportMode = SpriteImportMode.Single;
                }
                ti.alphaIsTransparency = root != TextureRoot;
                ti.SaveAndReimport();
                n++;
            }
        }
        Debug.Log($"[Import] textures configured: {n}");
    }

    /// <summary>
    /// Diagnostic: prints what actually came out of the glTF import for each enemy - clip names,
    /// whether they are legacy, and whether an Animation component is present. Run this whenever
    /// enemies stop animating; it tells you immediately whether the importer or the code is wrong.
    /// </summary>
    [MenuItem("UFO/Diagnose/Enemy Rigs")]
    public static void DiagnoseEnemyRigs() {
        foreach (var key in new[] { "gnat", "skirmisher", "warlord", "juggernaut", "wasp", "overseer" }) {
            var path = $"{ModelRoot}/enemies/{key}.glb";
            var objs = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objs == null || objs.Length == 0) { Debug.LogWarning($"  {key}: NOT IMPORTED"); continue; }

            var root = objs.OfType<GameObject>().FirstOrDefault(o => o.transform.parent == null);
            var clips = objs.OfType<AnimationClip>().ToList();
            bool hasAnimComponent = root != null && root.GetComponentInChildren<Animation>() != null;
            bool hasSkinned = root != null && root.GetComponentInChildren<SkinnedMeshRenderer>() != null;

            Debug.Log($"  {key}: clips=[{string.Join(", ", clips.Select(c => c.name + (c.legacy ? "" : " (NOT legacy)")))}] " +
                      $"AnimationComponent={hasAnimComponent} Skinned={hasSkinned}");
        }
    }

    /// <summary>Diagnostic: confirms every catalogue model resolves to an asset on disk.</summary>
    [MenuItem("UFO/Diagnose/Catalogue Coverage")]
    public static void DiagnoseCatalogue() {
        var missing = new List<string>();
        foreach (var kv in LevelData.Catalog) {
            var path = "Assets/Resources/" + kv.Value.ResourcePath + ".glb";
            if (!File.Exists(path)) missing.Add($"{kv.Key} -> {kv.Value.ResourcePath}");
        }
        if (missing.Count == 0) Debug.Log($"[Diagnose] all {LevelData.Catalog.Count} catalogue entries resolve");
        else Debug.LogWarning($"[Diagnose] {missing.Count} missing:\n  " + string.Join("\n  ", missing));
    }
}

}
