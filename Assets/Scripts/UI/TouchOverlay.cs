using UnityEngine;
using UnityEngine.UI;

namespace UFO {

/// <summary>
/// The on-screen control surface for a touch device, built to the same neon language as the rest
/// of the HUD rather than dropped on top of it.
///
/// Layout follows from one constraint: a thumb parked on a control hides everything under it. So
/// the whole bottom band belongs to the controls and every readout moves to the top - which is
/// why this also reflows the weapon panel, vitals and kill feed, all of which live in the bottom
/// corners on desktop where nothing is covering them.
///
/// Insets are deliberately generous. iPhone in landscape puts the notch on one side and the home
/// indicator along the bottom, and Unity reports the full rect for Screen.safeArea under WebGL,
/// so the margin has to be built into the layout rather than read from the platform.
/// </summary>
public class TouchOverlay : MonoBehaviour {

    public GameManager Game;
    public Hud Hud;

    RectTransform _root;
    TouchStick _stick;
    TouchLook _look;
    TouchButton _fire, _jump, _dash, _reload, _grenade, _pause;
    readonly TouchButton[] _slots = new TouchButton[4];

    static Sprite _disc, _ring;

    static readonly Color Face = new Color(0.36f, 0.93f, 1f, 0.16f);
    static readonly Color FaceDown = new Color(0.36f, 0.93f, 1f, 0.42f);
    static readonly Color FireFace = new Color(1f, 0.42f, 0.34f, 0.20f);
    static readonly Color FireDown = new Color(1f, 0.42f, 0.34f, 0.50f);

    public void Build(Hud hud, GameManager game, Transform canvas) {
        Hud = hud; Game = game;
        EnsureSprites();

        _root = Hud.Panel("TouchControls", canvas, Vector2.zero, Vector2.one,
                          Vector2.zero, Vector2.zero, Color.clear);

        // The look region sits under everything else and covers the whole screen. Sibling order is
        // what makes that safe: uGUI gives a touch to the TOPMOST graphic under it, so the stick
        // and the buttons - added after this - win wherever they overlap, and a drag that starts
        // on bare screen turns the view.
        //
        // The alpha is not zero because a fully transparent Image still raycasts but is invisible
        // to anyone debugging the hierarchy; this is the smallest value that stays findable.
        var lookRect = Hud.Panel("LookArea", _root, Vector2.zero, Vector2.one,
                                 Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.004f));
        var lookImg = lookRect.GetComponent<Image>();
        if (lookImg != null) lookImg.raycastTarget = true;
        _look = lookRect.gameObject.AddComponent<TouchLook>();

        BuildStick();
        _fire    = Round("Fire",    new Vector2(1, 0), new Vector2(-330f, 290f), 140f, "FIRE", true);
        _jump    = Round("Jump",    new Vector2(1, 0), new Vector2(-230f, 570f), 80f,  "JUMP", false);
        _dash    = Round("Dash",    new Vector2(1, 0), new Vector2(-430f, 650f), 74f,  "DASH", false);
        _reload  = Round("Reload",  new Vector2(1, 0), new Vector2(-640f, 430f), 74f,  "RLD",  false);
        _grenade = Round("Grenade", new Vector2(1, 0), new Vector2(-660f, 210f), 74f,  "GRN",  false);
        // 64 reference units, not the 46 this started at. On an iPhone in landscape the canvas
        // scales at roughly 0.4 px per reference unit, which turned 46 into a 37 px target -
        // under Apple's 44 pt minimum, and it felt like it. Every other control already cleared
        // the bar; this one did not.
        _pause   = Round("Pause",   new Vector2(1, 1), new Vector2(-360f, -92f), 64f,  "II",   false);

        for (int i = 0; i < 4; i++) {
            int slot = i;
            _slots[i] = Round("Slot" + (i + 1), new Vector2(0.5f, 0f),
                              new Vector2(-234f + i * 156f, 150f), 56f, (i + 1).ToString(), false);
            _slots[i].OnPress = () => TouchState.WeaponSlot = slot;
        }

        _jump.OnPress    = () => TouchState.Jump = true;
        _dash.OnPress    = () => TouchState.Dash = true;
        _reload.OnPress  = () => TouchState.Reload = true;
        _grenade.OnPress = () => TouchState.Grenade = true;
        _pause.OnPress   = () => TouchState.Pause = true;

        _root.gameObject.SetActive(false);
    }

    void BuildStick() {
        var baseRt = Hud.Panel("MoveStick", _root, Vector2.zero, Vector2.zero,
                               new Vector2(150f, 170f), new Vector2(450f, 470f), Color.clear);
        var ring = baseRt.gameObject.AddComponent<Image>();
        ring.sprite = _ring;
        ring.color = new Color(0.36f, 0.93f, 1f, 0.18f);
        ring.raycastTarget = true;

        var knobGo = new GameObject("Knob");
        knobGo.transform.SetParent(baseRt, false);
        var knobRt = knobGo.AddComponent<RectTransform>();
        knobRt.anchorMin = knobRt.anchorMax = new Vector2(0.5f, 0.5f);
        knobRt.sizeDelta = new Vector2(120f, 120f);
        knobRt.anchoredPosition = Vector2.zero;
        var knobImg = knobGo.AddComponent<Image>();
        knobImg.sprite = _disc;
        knobImg.color = new Color(0.36f, 0.93f, 1f, 0.38f);
        knobImg.raycastTarget = false;

        _stick = baseRt.gameObject.AddComponent<TouchStick>();
        _stick.Knob = knobRt;
        _stick.Radius = 150f;
    }

    TouchButton Round(string name, Vector2 anchor, Vector2 centre, float radius, string label, bool hot) {
        var rt = Hud.Panel(name, _root, anchor, anchor,
                           new Vector2(centre.x - radius, centre.y - radius),
                           new Vector2(centre.x + radius, centre.y + radius), Color.clear);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = _disc;
        img.raycastTarget = true;

        var t = Hud.Label(name + "Label", rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                          radius > 100f ? 34 : (radius > 60f ? 22 : 26), TextAnchor.MiddleCenter,
                          new Color(0.80f, 0.95f, 1f, 0.85f), true, false);
        t.text = label;
        t.raycastTarget = false;

        var btn = rt.gameObject.AddComponent<TouchButton>();
        btn.Style(img, hot ? FireFace : Face, hot ? FireDown : FaceDown);
        return btn;
    }

    /// <summary>A filled disc and a ring, drawn once into two small textures.</summary>
    static void EnsureSprites() {
        if (_disc != null) return;
        _disc = MakeCircle(128, 0f);
        _ring = MakeCircle(128, 0.86f);
    }

    static Sprite MakeCircle(int size, float innerFraction) {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size * 0.5f, inner = r * innerFraction;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float dx = x + 0.5f - r, dy = y + 0.5f - r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // One pixel of feather on each edge. Without it these read as visibly jagged at
                // the size they are actually drawn.
                float a = Mathf.Clamp01(r - d);
                if (innerFraction > 0f) a = Mathf.Min(a, Mathf.Clamp01(d - inner));
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>Publish this frame's finger state, then show or hide with the game state.</summary>
    public void Tick() {
        bool playing = Game != null && Game.State == GameState.Playing;
        bool want = Platform.TouchPrimary;

        if (_root.gameObject.activeSelf != (want && playing)) {
            _root.gameObject.SetActive(want && playing);
            if (!(want && playing)) TouchState.Clear();
        }
        if (!want) { TouchState.Active = false; return; }

        TouchState.Active = true;
        if (!playing) return;

        var mv = _stick.Value;
        TouchState.Move = mv;
        // Sprint without a button: shove the stick to the rim. One less thing under the thumb, and
        // it maps onto what a player already does when they want to run.
        TouchState.Sprint = mv.magnitude > 0.85f;
        TouchState.Fire = _fire.Held;
        TouchState.LookDelta = _look.Consume();
    }

    /// <summary>
    /// Retire this frame's taps.
    ///
    /// LateUpdate, so every Update that wanted to see the tap has already run. Clearing at the
    /// start of the next frame instead would work too, but clearing anywhere inside Update drops
    /// whichever readers happen to be ordered after it - intermittently, which is the worst way
    /// for an input bug to present.
    /// </summary>
    void LateUpdate() { TouchState.EndFrame(); }

    public void SetInvertY(bool invert) { if (_look != null) _look.InvertY = invert; }
    public void SetSensitivity(float s) { if (_look != null) _look.Sensitivity = Mathf.Clamp(s, 0.04f, 0.6f); }
}

}
