using UnityEngine;
using UnityEngine.UI;

namespace UFO {

/// <summary>
/// The field guide: bestiary and armory, on Tab. A recreation of v2's help overlay.
///
/// The important property, carried over from v2, is that the cards are generated from the live
/// tables - EnemyRoster and Arsenal - rather than from copied text. Every stat shown is the stat
/// the game actually fights with, so rebalancing a weapon updates its card and the guide can
/// never quietly become a lie. Only the prose notes are authored.
///
/// Portraits are sliced from the same 3x2 sheet v2 uses.
/// </summary>
public class HelpGuide {

    readonly Hud _hud;
    GameObject _root;
    GameObject _covered;

    public bool IsOpen => _root != null && _root.activeSelf;

    /// <summary>Order matters: it indexes the portrait sheet, left to right, top row first.</summary>
    static readonly string[] Order = { "gnat", "skirmisher", "warlord", "juggernaut", "wasp", "overseer" };

    static readonly System.Collections.Generic.Dictionary<string, string> EnemyNotes =
        new System.Collections.Generic.Dictionary<string, string> {
            ["gnat"] = "Cannon fodder. Plinks from range, panics and runs when its Warlord dies or it gets hurt.",
            ["skirmisher"] = "Fast flanker. Its arm shield blocks shots from the front - flank it, or throw a grenade.",
            ["warlord"] = "Shielded commander. Plasma strips the shield fastest; it falls back to recharge, then lunges to melee.",
            ["juggernaut"] = "Walking siege engine. The tower shield stops everything head-on; the fuel rod arcs - keep moving.",
            ["wasp"] = "Aerial harasser. Dives to strafe. Track it with the rifle.",
            ["overseer"] = "Boss. Hovers, spawns Gnats and sweeps plasma. Break the shield, then pour rockets in.",
        };

    static readonly System.Collections.Generic.Dictionary<string, string> WeaponNotes =
        new System.Collections.Generic.Dictionary<string, string> {
            ["rifle"] = "Hitscan, 32-round mag, headshots deal double. Right-click to aim.",
            ["plasmaRifle"] = "Looted alien plasma. Melts energy shields (2.4x). Overheats if held down.",
            ["energySword"] = "Lunges to a target within 7.5 m. Right-click for a heavy swing.",
            ["rocketLauncher"] = "Two rockets, 6.5 m splash. Best against Juggernauts and the Overseer.",
        };

    public HelpGuide(Hud hud) { _hud = hud; }

    // ------------------------------------------------------------------ build

    public void Build() {
        _root = _hud.MakeScreen("HelpGuide", new Color(0.004f, 0.02f, 0.035f, 0.97f));

        var title = _hud.Label("HelpTitle", _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(-500f, -86f), new Vector2(500f, -34f), 36,
                               TextAnchor.MiddleCenter, Hud.CyanColor);
        title.text = "// FIELD GUIDE //";

        var sprites = LoadPortraits();

        Header("BESTIARY", -104f);
        const float cardW = 404f, cardH = 250f, gapX = 24f, gapY = 18f;
        for (int i = 0; i < Order.Length; i++) {
            int col = i % 3, row = i / 3;
            float x = (col - 1) * (cardW + gapX);
            float y = -140f - row * (cardH + gapY);
            EnemyCard(Order[i], sprites != null && i < sprites.Length ? sprites[i] : null, x, y, cardW, cardH);
        }

        float armoryY = -140f - 2f * (cardH + gapY) - 24f;
        Header("ARMORY", armoryY + 36f);
        const float wCardW = 306f, wCardH = 138f, wGap = 18f;
        for (int i = 0; i < Arsenal.Order.Length; i++) {
            float x = (i - 1.5f) * (wCardW + wGap);
            WeaponCard(Arsenal.Order[i], x, armoryY - 4f, wCardW, wCardH);
        }

        _hud.Btn(_root.transform, "CLOSE", new Vector2(0.5f, 0f), new Vector2(-130f, 26f), new Vector2(130f, 82f), Close);

        var hint = _hud.Label("HelpHint", _root.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(-500f, 2f), new Vector2(500f, 24f), 15,
                              TextAnchor.MiddleCenter, Hud.DimColor, false, false);
        hint.text = "TAB OR ESC TO CLOSE";

        _root.SetActive(false);
    }

    void Header(string text, float y) {
        var h = _hud.Label("hdr_" + text, _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                           new Vector2(-640f, y - 28f), new Vector2(640f, y), 20,
                           TextAnchor.MiddleLeft, Hud.NeonGreenColor, true, false);
        h.text = "// " + text;
    }

    RectTransform Card(string name, float x, float y, float w, float h) {
        var rt = _hud.Panel(name, _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                            new Vector2(x - w * 0.5f, y - h), new Vector2(x + w * 0.5f, y),
                            new Color(0.02f, 0.08f, 0.12f, 0.85f));
        _hud.Bracket(rt, new Color(Hud.CyanColor.r, Hud.CyanColor.g, Hud.CyanColor.b, 0.55f), 14f, 2f);
        return rt;
    }

    void EnemyCard(string key, Sprite portrait, float x, float y, float w, float h) {
        var data = EnemyRoster.Get(key);
        if (data == null) return;
        var card = Card("card_" + key, x, y, w, h);

        if (portrait != null) {
            var img = _hud.Img("portrait", card, new Vector2(0f, 1f), new Vector2(0f, 1f),
                               new Vector2(14f, -128f), new Vector2(128f, -14f), Color.white);
            img.sprite = portrait;
            img.preserveAspect = true;
        }

        var name = _hud.Label("name", card, new Vector2(0f, 1f), new Vector2(1f, 1f),
                              new Vector2(140f, -46f), new Vector2(-12f, -12f), 24,
                              TextAnchor.MiddleLeft, data.Color);
        name.text = data.Name;

        // Straight off the roster: what the card claims is what the AI fights with.
        string stats = $"HP {data.Hp:0}";
        if (data.Shield > 0f) stats += $"  .  SHIELD {data.Shield:0}";
        if (data.FrontShield > 0f) stats += $"  .  ARM {data.FrontShield:0}";
        // Three short lines rather than two long ones: the card is 404 px wide and the score was
        // running off the end of it.
        stats += $"\nSPEED {data.Speed:0.0}  .  DMG {data.Damage:0}  .  RANGE {data.AttackRange:0} m";
        if (data.Points > 0) stats += $"\n{data.Points} PTS";

        var st = _hud.Label("stats", card, new Vector2(0f, 1f), new Vector2(1f, 1f),
                            new Vector2(140f, -122f), new Vector2(-12f, -48f), 14,
                            TextAnchor.UpperLeft, Hud.NeonGreenColor, false, false);
        st.text = stats;

        var desc = _hud.Label("desc", card, new Vector2(0f, 0f), new Vector2(1f, 1f),
                              new Vector2(14f, 12f), new Vector2(-12f, -146f), 15,
                              TextAnchor.UpperLeft, Hud.DimColor, false, false);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.text = EnemyNotes.TryGetValue(key, out var note) ? note : "";
    }

    void WeaponCard(string key, float x, float y, float w, float h) {
        var def = Arsenal.Get(key);
        if (def == null) return;
        var card = Card("wcard_" + key, x, y, w, h);

        var name = _hud.Label("name", card, new Vector2(0f, 1f), new Vector2(1f, 1f),
                              new Vector2(12f, -36f), new Vector2(-12f, -8f), 18,
                              TextAnchor.MiddleLeft, Hud.CyanColor);
        int slot = System.Array.IndexOf(Arsenal.Order, key) + 1;
        name.text = $"[{slot}] {def.Name}";

        string stats = $"DMG {def.Damage:0}";
        if (def.Splash > 0f) stats += $"  .  SPLASH {def.Splash:0.0} m";
        stats += $"  .  {1f / def.FireRate:0.0}/s";
        if (def.ShieldMul != 1f) stats += $"\nVS SHIELD {def.ShieldMul:0.0}x";
        if (def.Headshot > 1f) stats += $"  .  HEADSHOT {def.Headshot:0.0}x";

        var st = _hud.Label("stats", card, new Vector2(0f, 1f), new Vector2(1f, 1f),
                            new Vector2(12f, -76f), new Vector2(-12f, -38f), 13,
                            TextAnchor.UpperLeft, Hud.NeonGreenColor, false, false);
        st.text = stats;

        var desc = _hud.Label("desc", card, new Vector2(0f, 0f), new Vector2(1f, 1f),
                              new Vector2(12f, 8f), new Vector2(-12f, -80f), 13,
                              TextAnchor.UpperLeft, Hud.DimColor, false, false);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        desc.text = WeaponNotes.TryGetValue(key, out var note) ? note : "";
    }

    /// <summary>Slice the 3x2 portrait sheet, left to right and top row first, matching Order.</summary>
    static Sprite[] LoadPortraits() {
        var sheet = Resources.Load<Texture2D>("UI/enemies");
        if (sheet == null) return null;

        float cw = sheet.width / 3f, ch = sheet.height / 2f;
        var sprites = new Sprite[6];
        for (int i = 0; i < 6; i++) {
            int col = i % 3, row = i / 3;
            // Sprite rects are bottom-up, so the sheet's top row sits at y = ch.
            var rect = new Rect(col * cw, row == 0 ? ch : 0f, cw, ch);
            sprites[i] = Sprite.Create(sheet, rect, new Vector2(0.5f, 0.5f), 100f);
        }
        return sprites;
    }

    // ------------------------------------------------------------------ control

    public void Open() {
        if (_root == null) return;
        _covered = _hud.ActiveScreen();
        if (_covered != null) _covered.SetActive(false);
        _root.SetActive(true);
    }

    public void Close() {
        if (_root == null) return;
        _root.SetActive(false);
        if (_covered != null) { _covered.SetActive(true); _covered = null; }
    }

    public void Toggle() { if (IsOpen) Close(); else Open(); }
}

}
