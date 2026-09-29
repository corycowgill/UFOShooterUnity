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

        Debug.Log($"[Build] WebGL -> {output} (development={development})");
        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

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
