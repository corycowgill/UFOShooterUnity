using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UFO.EditorTools {

/// <summary>
/// WebGL build entry point:
///
///   Unity -batchmode -quit -executeMethod UFO.EditorTools.BuildWebGL.Build
///
/// Optional args: -outputPath &lt;dir&gt;  -development
/// </summary>
public static class BuildWebGL {

    const string DefaultOutput = "Build/web";

    public static void Build() {
        string output = ArgValue("-outputPath") ?? DefaultOutput;
        bool development = HasArg("-development");

        if (!Path.IsPathRooted(output))
            output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));

        Directory.CreateDirectory(output);

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) {
            Debug.LogError("[Build] no scenes in build settings - run ProjectSetup.RunAll first");
            EditorApplication.Exit(2);
            return;
        }

        var options = new BuildPlayerOptions {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.WebGL,
            targetGroup = BuildTargetGroup.WebGL,
            options = development ? BuildOptions.Development : BuildOptions.None,
        };

        // Stamp a version that changes whenever the content does.
        //
        // This is load-bearing, not cosmetic. The template marks the payload "immutable" so that
        // returning players never re-download it, and the ONLY thing that can then invalidate a
        // player's cached copy is productVersion: Unity prunes every IndexedDB entry whose stored
        // version differs from the running one. A build that ships new content under an unchanged
        // version is a build nobody who has played before will ever see.
        //
        // Restored immediately afterwards so a build does not leave ProjectSettings.asset dirty.
        string stamp = ArgValue("-buildVersion");
        string previousVersion = PlayerSettings.bundleVersion;
        if (!string.IsNullOrEmpty(stamp)) PlayerSettings.bundleVersion = stamp;

        Debug.Log($"[Build] WebGL -> {output} (development={development}) version={PlayerSettings.bundleVersion}");
        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        if (!string.IsNullOrEmpty(stamp)) PlayerSettings.bundleVersion = previousVersion;

        Debug.Log($"[Build] result={summary.result} size={summary.totalSize / (1024 * 1024)}MB " +
                  $"time={summary.totalTime} errors={summary.totalErrors} warnings={summary.totalWarnings}");

        if (summary.result != BuildResult.Succeeded) {
            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"[Build] {step.name}: {msg.content}");
            EditorApplication.Exit(1);
            return;
        }

        WriteServeNote(output);
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// Brotli-compressed builds need the right Content-Encoding header, and every "my Unity WebGL
    /// build is blank" report starts here. Leave the reader a note next to the build.
    /// </summary>
    static void WriteServeNote(string output) {
        var note = @"# Serving this build

The player is Brotli-compressed. Because `decompressionFallback` is enabled, Unity emits the
payload as `.unityweb` files and decompresses them in JavaScript, so **this build runs on any
static host with no special headers**. That is the safe default and why it is on.

It is not the fast default. Decompressing in JS costs a second or two of startup. To let the
browser do it natively instead:

1. Set `PlayerSettings.WebGL.decompressionFallback = false` in ProjectSetup.
2. Rebuild - Unity then emits `.br` files.
3. Serve them with the original content type plus:

       Content-Encoding: br

   (`application/wasm` for the wasm, `application/javascript` for the framework.)

Getting step 3 wrong is the classic ""blank canvas"" failure, which is exactly why the fallback
is on until someone opts into the faster path.

## Caching

`Build/*` is marked `immutable` in the template, so a returning player reads the payload out of
IndexedDB and transfers nothing at all. The only thing that can invalidate that copy is the
product version, which `Tools/build-web.sh` derives from the commit and passes as
`-buildVersion`. Build through that script, or through anything else that passes the flag - a
build that ships new content under an old version is one that returning players never see.

`index.html` is deliberately excluded from this: it must stay revalidated, because it is what
carries the new version to the browser in the first place.

Local check, either way:

    node Tools/serve.mjs --dir Build/web --port 8123
";
        File.WriteAllText(Path.Combine(output, "SERVING.md"), note);
    }

    static string ArgValue(string name) {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);
}

}
