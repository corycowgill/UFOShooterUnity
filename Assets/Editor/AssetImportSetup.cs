using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Configures the importers for the 61 glTF models and every loose texture.
///
/// Two settings matter and both are easy to get wrong:
///
/// 1. **Animation method must be Legacy.** The enemy rigs carry seven named clips and the AI
///    picks between them imperatively (see EnemyAnimator). glTFast defaults to Mecanim, which
///    would hand us clips we cannot CrossFade by name without building an AnimatorController per
///    enemy for no benefit.
/// 2. **Texture budget.** Model textures are loose .png files beside each .gltf, not images
///    embedded in a .glb, so TextureImporter governs them and DXT1 costs an eighth of RGBA32
///    per texel. See ConfigureModelTextures - that is what pays for 1024px props.
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
        // Textures first: the .gltf importer resolves them as assets, so they have to exist
        // and be configured before the models that reference them are imported.
        ConfigureModelTextures();
        ConfigureModels();
        ConfigureTextures();
        AssetDatabase.SaveAssets();
        Debug.Log("[Import] asset import settings applied");
    }

    [MenuItem("UFO/Setup/Configure Model Importers")]
    public static void ConfigureModels() {
        var files = Directory.GetFiles(ModelRoot, "*.gltf", SearchOption.AllDirectories);
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

    /// <summary>
    /// The model textures. These are loose .png files beside each .gltf rather than images
    /// embedded in a .glb, and that is deliberate: glTFast's SyncTextureLoader resolves an
    /// external image URI through AssetDatabase.LoadAssetAtPath&lt;Texture2D&gt;, so these are
    /// ordinary Unity texture assets and everything below actually applies to them. Embedded
    /// GLB images bypass TextureImporter entirely and land as uncompressed RGBA32, which is
    /// why the old pipeline had to pre-shrink every prop to 256px to keep the build shippable.
    /// DXT1 costs half a byte per texel against RGBA32's four, so the same budget buys 1024px.
    /// </summary>
    [MenuItem("UFO/Setup/Configure Model Textures")]
    public static void ConfigureModelTextures() {
        int n = 0;
        foreach (var path in Directory.GetFiles(ModelRoot, "*.png", SearchOption.AllDirectories)) {
            var assetPath = path.Replace('\\', '/');
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti == null) continue;

            // Each file is already authored at its budget (2048 weapons, 1024 heroes and large
            // props, 512 small props); this is only a ceiling, so it must not undercut them.
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.alphaIsTransparency = false;

            // DXT alone shrinks VRAM but not the download: the WebGL data file is Brotli'd, and
            // Brotli squeezed the old uncompressed RGBA32 hard while DXT blocks are already dense
            // enough to resist it. Measured: switching to DXT cut uncompressed assets 20% and still
            // pushed the payload from 77.3 to 79.3 MB. Crunch is the piece that pays on the wire -
            // it compresses the DXT blocks for storage and decodes back to DXT at load, so VRAM is
            // unchanged. It is lossy on top of DXT and slow to import, which is the trade.
            ti.crunchedCompression = true;
            // 75, and measured rather than picked. Assets/Editor/TextureQualityAudit.cs blits the
            // imported texture back out and compares it to the source PNG; the sweep in
            // TextureSettingSweep.cs prices the alternatives:
            //
            //   crunch 75   31.5-32.7 dB   931 KB vram   64.5 MB download   <- shipping
            //   crunch 100  31.9-33.3 dB   953 KB vram   66.5 MB download
            //   DXT1        34.0-35.6 dB  1388 KB vram   ~19 MB more again
            //   BC7         38.4-42.7 dB  2774 KB vram   cannot be crunched
            //
            // 100 looked near-free on VRAM alone (+2%), which is why it was tried - but crunch
            // quality lands on the compressed payload, and it cost 2.0 MB of download for 0.5 dB
            // that is not visible at 1:1. Not worth it. VRAM is not the wire.
            ti.compressionQuality = 75;

            // A metallic/roughness map holds measurements, not colour. Importing it as sRGB
            // puts a gamma curve through the roughness and every surface reads too glossy.
            // glTFast flags this itself for embedded images; for external ones it is on us.
            // Emissive maps DO carry colour and stay sRGB - the neon has to come back out
            // the hue it went in as.
            ti.sRGBTexture = !assetPath.EndsWith("_metallicRoughness.png");

            ti.SaveAndReimport();
            n++;
        }
        Debug.Log($"[Import] model textures configured: {n}");
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
                // Decals have the same problem for the same reason: blood, scorch and the contact
                // falloff all lie flat on the road and are therefore always seen edge-on. Without
                // anisotropy the falloff sheet in particular samples down to its average alpha and
                // a soft shadow under a bench renders as a hard-edged rectangle.
                if (root == DecalRoot) {
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
            var path = $"{ModelRoot}/enemies/{key}.gltf";
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
            var path = "Assets/Resources/" + kv.Value.ResourcePath + ".gltf";
            if (!File.Exists(path)) missing.Add($"{kv.Key} -> {kv.Value.ResourcePath}");
        }
        if (missing.Count == 0) Debug.Log($"[Diagnose] all {LevelData.Catalog.Count} catalogue entries resolve");
        else Debug.LogWarning($"[Diagnose] {missing.Count} missing:\n  " + string.Join("\n  ", missing));
    }
}

}
