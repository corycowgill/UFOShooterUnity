using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// Dumps what the GPU actually gets for a model texture, so the source PNG and the imported
/// result can be compared pixel for pixel.
///
/// There are two lossy steps between the authored texture and the screen and neither is visible
/// from the asset: DXT1 block compression, and crunch on top of it at compressionQuality 75.
/// Crunch is what made the download smaller than the old uncompressed RGBA32 path, so the
/// question is what it cost. Blitting the imported texture through a RenderTexture and reading
/// it back gives exactly the decompressed texels the shader samples - including both lossy
/// steps - which a straight ReadPixels on the asset cannot, because the compressed texture is
/// not CPU-readable.
///
///   Unity.exe -batchmode -nographics -quit -projectPath . \
///             -executeMethod UFO.EditorTools.TextureQualityAudit.Run -logFile Logs/texq.log
///
/// Writes decompressed PNGs to texq/ for an external diff.
/// </summary>
public static class TextureQualityAudit {

    static readonly string[] Probe = {
        "Assets/Resources/Models/weapons/rifle_baseColor.png",
        "Assets/Resources/Models/props/building_hospital_baseColor.png",
        "Assets/Resources/Models/props/building_residential_013_baseColor.png",
        "Assets/Resources/Models/props/police_car_baseColor.png",
        "Assets/Resources/Models/enemies/gnat_baseColor.png",
    };

    [MenuItem("UFO/Diagnose/Texture Quality")]
    public static void Run() {
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "texq");
        Directory.CreateDirectory(outDir);

        foreach (var path in Probe) {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) { Debug.Log($"[TexQ] MISSING {path}"); continue; }

            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            long srcBytes = new FileInfo(path).Length;

            // Blit through a linear RenderTexture so we read the decompressed texels rather
            // than re-encoding anything. sRGB off on the RT keeps the values as stored.
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

            var name = Path.GetFileNameWithoutExtension(path);
            File.WriteAllBytes(Path.Combine(outDir, name + "_gpu.png"), flat.EncodeToPNG());
            Object.DestroyImmediate(flat);

            // Runtime VRAM footprint of the texture as imported, mip chain included.
            long vram = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);

            Debug.Log($"[TexQ] {name}\n" +
                      $"       imported : {tex.width}x{tex.height} {tex.format} mips={tex.mipmapCount}\n" +
                      $"       importer : maxSize={ti?.maxTextureSize} crunch={ti?.crunchedCompression} " +
                      $"quality={ti?.compressionQuality} sRGB={ti?.sRGBTexture}\n" +
                      $"       bytes    : source png {srcBytes / 1024} KB -> vram {vram / 1024} KB");
        }
        Debug.Log("[TexQ] decompressed PNGs written to texq/");
    }
}

}
