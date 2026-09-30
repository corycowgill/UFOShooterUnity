using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UFO.Tests {

/// <summary>
/// The first-person rig, photographed from the player's own camera.
///
/// It needs its own suite because every other capture in the project builds a camera of its own,
/// and the viewmodel and the hands are parented to the *game* camera - so they are invisible in
/// every LayoutShots and CombatShots frame ever taken. This renders the real one.
///
///   Unity -batchmode -runTests -testPlatform PlayMode -testFilter UFO.Tests.HandShots
/// </summary>
public class HandShots {

    GameManager _game;
    string _outDir;

    [UnitySetUp]
    public IEnumerator SetUp() {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("needs a graphics device; run Unity without -nographics");

        _outDir = Path.Combine(Directory.GetCurrentDirectory(), "shots-hands");
        Directory.CreateDirectory(_outDir);

        var op = SceneManager.LoadSceneAsync("Assets/Scenes/Game.unity", LoadSceneMode.Single);
        while (op != null && !op.isDone) yield return null;
        for (int i = 0; i < 8; i++) yield return null;
        _game = Object.FindFirstObjectByType<GameManager>();
        Assert.IsNotNull(_game);
    }

    /// <summary>Render the player's camera, which is the only one the weapon rig is attached to.</summary>
    void Capture(string file, int w = 1280, int h = 800) {
        var cam = _game.Player.Cam;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2 };
        var prevTarget = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        File.WriteAllBytes(Path.Combine(_outDir, file), tex.EncodeToPNG());

        cam.targetTexture = prevTarget;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    /// <summary>
    /// Advance real frames, keeping the game out of the perk screen.
    ///
    /// Clearing the wave in <see cref="Ready"/> completes it, and a completed wave puts the game
    /// into PerkSelect - where GameManager.Step does not run at all. Every timer in the weapon rig
    /// then stops, which is why an instrumented reload sat at t = 0.00 for all eight captures while
    /// still reporting that it was reloading.
    /// </summary>
    IEnumerator Settle(float seconds) {
        float t = 0f;
        while (t < seconds) {
            if (_game.State == GameState.PerkSelect) _game.ChoosePerk(PlayerStats.Perks[0]);
            t += Time.deltaTime;
            yield return null;
        }
    }

    IEnumerator Ready() {
        _game.StartGame();
        yield return null;
        _game.Waves.Cleanup();
        _game.Stats.Hp = _game.Stats.MaxHp = 100000f;
        // Face down a street rather than into the plaza, so the arms read against buildings and
        // lit shopfronts instead of against an unbroken sheet of paving.
        _game.Player.Teleport(new Vector3(0f, 0f, 6f), 200f);
        yield return Settle(0.4f);
    }

    [UnityTest]
    public IEnumerator Render_Idle_Grip_Per_Weapon() {
        yield return Ready();

        foreach (var key in Arsenal.Order) {
            _game.Weapons.SwitchWeapon(key);
            // SwitchWeapon runs a 0.3 s raise; let it finish or every shot is of a lowered weapon.
            yield return Settle(0.8f);
            Capture($"grip-{key}.png");
            Debug.Log($"[Hands] grip {key}");
        }
    }

    [UnityTest]
    public IEnumerator Render_Reload_Sequence() {
        yield return Ready();

        foreach (var key in new[] { "rifle", "rocketLauncher" }) {
            _game.Weapons.SwitchWeapon(key);
            yield return Settle(0.8f);

            // Empty it. Firing the last round starts an automatic reload of its own, so wait that
            // one out before asking for the reload this test is actually here to photograph -
            // otherwise the capture window straddles the tail of a reload that began at an unknown
            // time, and every frame comes back looking identical.
            var none = new System.Collections.Generic.List<Enemy>();
            for (int i = 0; i < 200 && _game.Weapons.Ammo != 0; i++) {
                _game.Weapons.Fire(none);
                yield return Settle(0.02f);
            }
            while (_game.Weapons.IsReloading) yield return Settle(0.05f);
            yield return Settle(0.2f);

            // Empty it again without letting the auto-reload finish this time: fire the magazine
            // dry, then drive the reload from a known t = 0.
            for (int i = 0; i < 200 && _game.Weapons.Ammo != 0; i++) {
                _game.Weapons.Fire(none);
                yield return Settle(0.02f);
            }
            while (_game.Weapons.IsReloading) yield return Settle(0.05f);
            _game.Weapons.Reload();
            yield return null;
            Assert.IsTrue(_game.Weapons.IsReloading, $"{key} did not start a reload");

            int shot = 0;
            float total = Arsenal.Get(key).Reload;
            // Eight frames across the reload: enough to see the magazine leave, the hand go down
            // and come back, and the weapon settle.
            for (int i = 0; i < 8; i++) {
                Capture($"reload-{key}-{++shot}.png");
                Debug.Log($"[Hands] {key} t={_game.Weapons.ReloadPct:0.00} reloading={_game.Weapons.IsReloading}");
                yield return Settle(total / 8f);
            }
            Debug.Log($"[Hands] reload {key}: {shot} frames over {total:0.00}s");
        }
    }
}

}
