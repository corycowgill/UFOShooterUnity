using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Reimports a few probe textures at several compression settings and dumps what the GPU would
/// sample for each, so the quality/size trade can be measured rather than guessed.
///
/// The shipping setting is DXT1 + crunch at quality 75. Crunch is what made the WebGL payload
/// smaller than the old uncompressed RGBA32 path, but it is lossy on top of DXT1 and the quality
/// dial was never justified against anything. The alternatives worth pricing:
///
///   crunch 75   what ships now
///   crunch 100  same format, the encoder trying harder; bigger on disk, same VRAM
///   no crunch   plain DXT1 - the best this format does; much bigger download
///   BC7         1 byte/texel instead of DXT1's 0.5, so double the VRAM, far better gradients
///
/// Restores the original importer settings on the way out.
///
///   Unity.exe -batchmode -quit -projectPath . \
///             -executeMethod UFO.EditorTools.TextureSettingSweep.Run -logFile Logs/sweep.log
/// </summary>
public static class TextureSettingSweep {

    static readonly string[] Probe = {
        "Assets/Resources/Models/props/building_hospital_baseColor.png",
        "Assets/Resources/Models/props/police_car_baseColor.png",
        "Assets/Resources/Models/enemies/gnat_baseColor.png",
    };

    class Variant {
        public string Name;
        public bool Crunch;
        public int Quality;
        public TextureImporterFormat Format;   // Automatic = let the platform pick (DXT1)
    }

    static readonly Variant[] Variants = {
        new Variant { Name = "crunch75",  Crunch = true,  Quality = 75,  Format = TextureImporterFormat.Automatic },
        new Variant { Name = "crunch100", Crunch = true,  Quality = 100, Format = TextureImporterFormat.Automatic },
        new Variant { Name = "dxt1",      Crunch = false, Quality = 100, Format = TextureImporterFormat.Automatic },
        new Variant { Name = "bc7",       Crunch = false, Quality = 100, Format = TextureImporterFormat.BC7 },
    };

    [MenuItem("UFO/Diagnose/Texture Setting Sweep")]
    public static void Run() {
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "texsweep");
        Directory.CreateDirectory(outDir);

        foreach (var path in Probe) {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) { Debug.Log($"[Sweep] no importer {path}"); continue; }

            // Remember what ships, so an interrupted run cannot leave the project altered.
            bool wasCrunch = ti.crunchedCompression;
            int wasQuality = ti.compressionQuality;
            var wasOverride = ti.GetPlatformTextureSettings("WebGL");

            foreach (var v in Variants) {
                ti.crunchedCompression = v.Crunch;
                ti.compressionQuality = v.Quality;
                if (v.Format != TextureImporterFormat.Automatic) {
                    var ps = ti.GetPlatformTextureSettings("WebGL");
                    ps.overridden = true; ps.format = v.Format; ps.crunchedCompression = v.Crunch;
                    ti.SetPlatformTextureSettings(ps);
                } else {
                    var ps = ti.GetPlatformTextureSettings("WebGL");
                    ps.overridden = false;
                    ti.SetPlatformTextureSettings(ps);
                }
                ti.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var name = Path.GetFileNameWithoutExtension(path);
                Dump(tex, Path.Combine(outDir, $"{name}__{v.Name}.png"));
                long vram = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
                Debug.Log($"[Sweep] {name} / {v.Name}: {tex.format} vram={vram / 1024} KB");
            }

            ti.crunchedCompression = wasCrunch;
            ti.compressionQuality = wasQuality;
            ti.SetPlatformTextureSettings(wasOverride);
            ti.SaveAndReimport();
        }
        Debug.Log("[Sweep] done; settings restored");
    }

    static void Dump(Texture2D tex, string outPath) {
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;
        Graphics.Blit(tex, rt);
        RenderTexture.active = rt;
        var flat = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
        flat.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        flat.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        File.WriteAllBytes(outPath, flat.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(flat);
    }
}

}
