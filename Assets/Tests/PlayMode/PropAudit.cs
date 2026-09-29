using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// Audits where every placed prop actually sits relative to the surface under it.
///
/// "Props look cut off or below the floor" has three distinct causes and they need separating:
///   1. the prop rests on y=0 but stands on a sidewalk slab whose top is 0.18 - it is buried
///   2. the model's own bounds do not start at its base, so normalising to y=0 sinks it
///   3. the prop is simply intersecting something else
/// This reports 1 and 2 as numbers so the fix can be aimed at the right one.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.PropAudit
/// </summary>
public class PropAudit {

    GameManager _game;

    [UnitySetUp]
    public IEnumerator SetUp() {
        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    /// <summary>Height of the walkable surface under a point: sidewalk slab top, or the road.</summary>
    static float SurfaceAt(float x, float z) {
        const float half = (LevelData.Block + 3f) * 0.5f;
        for (int gx = -2; gx <= 2; gx++) {
            for (int gz = -2; gz <= 2; gz++) {
                float cx = gx * LevelData.Pitch, cz = gz * LevelData.Pitch;
                if (Mathf.Abs(x - cx) <= half && Mathf.Abs(z - cz) <= half) return 0.18f;
            }
        }
        return 0f;
    }

    [UnityTest]
    public IEnumerator Report_Sunken_Props() {
        _game.Level.Build(0);
        for (int i = 0; i < 3; i++) yield return null;

        var root = GameObject.Find("Level_THE LOOP");
        Assert.IsNotNull(root);

        var sunk = new List<(string name, float baseY, float surface, float sink, Vector3 pos)>();
        var counts = new Dictionary<string, int>();
        int total = 0;

        foreach (Transform child in root.transform) {
            // Only audit catalogue props; ground, sky, markings and lights are not "props".
            if (LevelData.Find(child.name) == null) continue;
            var rs = child.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) continue;

            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

            total++;
            float surface = SurfaceAt(child.position.x, child.position.z);
            float sink = surface - b.min.y;          // positive = buried below the surface
            if (sink > 0.05f) {
                sunk.Add((child.name, b.min.y, surface, sink, child.position));
                counts[child.name] = counts.TryGetValue(child.name, out var c) ? c + 1 : 1;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"\n===== PROP AUDIT: THE LOOP =====  {total} catalogue props placed");
        sb.AppendLine($"buried below their surface: {sunk.Count} ({(100f * sunk.Count / Mathf.Max(1, total)):F0}%)");
        sb.AppendLine("\nby type (count, worst sink):");
        foreach (var kv in counts.OrderByDescending(k => k.Value)) {
            float worst = sunk.Where(s => s.name == kv.Key).Max(s => s.sink);
            sb.AppendLine($"  {kv.Key,-20} x{kv.Value,-4} worst {worst:F2} m");
        }
        sb.AppendLine("\nworst offenders:");
        foreach (var s in sunk.OrderByDescending(s => s.sink).Take(12))
            sb.AppendLine($"  {s.name,-20} baseY={s.baseY,7:F2}  surface={s.surface:F2}  sink={s.sink,6:F2}  at {s.pos.ToString("F0")}");

        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Second cause, measured in isolation: instantiate each catalogue model on flat ground and
    /// check it actually rests on zero. Anything off means the normalise step is wrong for it,
    /// not that the surface underneath is raised.
    /// </summary>
    [UnityTest]
    public IEnumerator Report_Model_Base_Offsets() {
        var sb = new StringBuilder();
        sb.AppendLine("\n===== MODEL BASE OFFSETS =====  (should all be 0.00)");
        int bad = 0;

        foreach (var kv in LevelData.Catalog) {
            var prefab = ModelCache.Load(kv.Value.ResourcePath);
            if (prefab == null) continue;

            var holder = new GameObject("audit_" + kv.Key);
            holder.transform.position = new Vector3(500f, 0f, 500f);   // off the map
            var obj = Object.Instantiate(prefab, holder.transform);
            ModelCache.StripColliders(obj);
            ModelCache.NormalizeHeight(obj, kv.Value.Height);
            yield return null;

            var rs = holder.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0) {
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                float baseY = b.min.y;
                if (Mathf.Abs(baseY) > 0.05f) {
                    sb.AppendLine($"  {kv.Key,-22} baseY={baseY,7:F2}  h={b.size.y:F1} (want {kv.Value.Height})");
                    bad++;
                }
            }
            Object.DestroyImmediate(holder);
        }
        sb.AppendLine(bad == 0 ? "  all models rest on zero" : $"  {bad} models do not rest on zero");
        Debug.Log(sb.ToString());
    }
}

}
