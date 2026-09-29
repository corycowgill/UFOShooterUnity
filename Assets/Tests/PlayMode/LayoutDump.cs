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
/// Not an assertion test - a reporting one. It builds each level and dumps what actually landed
/// in the world: every placed object's catalogue key, the height the catalogue asked for, the
/// height and footprint it ended up with, and where it stands.
///
/// This exists because "the map looks wrong" is not actionable. A table of expected-vs-actual
/// turns it into one of three specific problems: the model is the wrong size, it is in the wrong
/// place, or the block grid itself is spaced wrong for the footprints it is holding.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.LayoutDump
/// </summary>
public class LayoutDump {

    GameManager _game;

    [UnitySetUp]
    public IEnumerator SetUp() {
        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    [UnityTest]
    public IEnumerator Dump_All_Levels() {
        for (int li = 0; li < LevelData.Levels.Length; li++) {
            _game.Level.Build(li);
            yield return null;

            var def = LevelData.Levels[li];
            var root = GameObject.Find("Level_" + def.Name);
            Assert.IsNotNull(root, "level root not found for " + def.Name);

            var sb = new StringBuilder();
            sb.AppendLine($"\n===== {def.Name} =====  arenaRadius={def.ArenaRadius}  gridPitch={LevelData.Pitch}");
            sb.AppendLine($"{"object",-26} {"want_h",7} {"got_h",7} {"foot_x",7} {"foot_z",7} {"pos",-24} {"scale",7}");

            var rows = new List<(string name, float wantH, float gotH, float fx, float fz, Vector3 pos, float scale)>();

            foreach (Transform child in root.transform) {
                var prop = LevelData.Find(child.name);
                var rs = child.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;

                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

                rows.Add((child.name, prop != null ? prop.Height : -1f, b.size.y, b.size.x, b.size.z,
                          child.position, child.localScale.x));
            }

            foreach (var r in rows.OrderByDescending(r => r.gotH).Take(40)) {
                string flag = "";
                if (r.wantH > 0f) {
                    float err = Mathf.Abs(r.gotH - r.wantH) / r.wantH;
                    if (err > 0.10f) flag = $"  << height off by {(r.gotH / r.wantH):F2}x";
                }
                // A building whose footprint is wider than the 44 m grid pitch will eat the street.
                if (Mathf.Max(r.fx, r.fz) > LevelData.Pitch) flag += $"  << footprint {Mathf.Max(r.fx, r.fz):F0}m > pitch {LevelData.Pitch:F0}m";

                sb.AppendLine($"{r.name,-26} {r.wantH,7:F1} {r.gotH,7:F1} {r.fx,7:F1} {r.fz,7:F1} " +
                              $"{r.pos.ToString("F0"),-24} {r.scale,7:F3}{flag}");
            }

            // Footprint-vs-spacing summary: the single number that says whether the city reads as
            // a city or as models scattered on a plain.
            var buildings = rows.Where(r => r.name.StartsWith("skyscraper") || r.name.StartsWith("residential")
                                         || r.name.StartsWith("commercial") || r.name.StartsWith("industrial")).ToList();
            if (buildings.Count > 0) {
                sb.AppendLine($"\nbuildings: {buildings.Count}  " +
                              $"mean footprint {buildings.Average(b => Mathf.Max(b.fx, b.fz)):F1}m  " +
                              $"mean height {buildings.Average(b => b.gotH):F1}m  " +
                              $"block is {LevelData.Block}m with {LevelData.Street}m streets (pitch {LevelData.Pitch}m)");
                sb.AppendLine($"  -> mean gap between adjacent buildings: " +
                              $"{LevelData.Pitch - buildings.Average(b => Mathf.Max(b.fx, b.fz)):F1}m");
            }

            Debug.Log(sb.ToString());
        }
    }
}

}
