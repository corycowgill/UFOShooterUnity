using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UFO {

/// <summary>
/// The interface, built from code at runtime and styled to match v2's HUD rather than Unity's
/// defaults. v2's look is a specific thing and the port has to hit it deliberately:
///
///   - Orbitron, wide and squared, never a system sans
///   - a cyan/green neon palette with a glow, on near-black panels
///   - bracketed framing: "// THE LOOP //", "[ WAVE 1 ]", a boxed weapon panel
///   - a SEGMENTED health bar that runs red to green, not a smooth fill
///   - a radar in the top-right, which is how you find the squad that is flanking you
///
/// uGUI with the legacy Text component rather than TextMeshPro, because TMP needs its essential
/// resources imported interactively and this project is generated headlessly.
/// </summary>
public class Hud : MonoBehaviour {

    public GameManager Game;

    // v2's palette, read off its CSS variables.
    static readonly Color Cyan = new Color(0.36f, 0.93f, 1f);
    static readonly Color NeonGreen = new Color(0.20f, 1f, 0.72f);
    static readonly Color Amber = new Color(1f, 0.78f, 0.22f);
    static readonly Color Danger = new Color(1f, 0.30f, 0.30f);
    static readonly Color Dim = new Color(0.62f, 0.86f, 0.95f, 0.65f);
    static readonly Color PanelBg = new Color(0.02f, 0.08f, 0.11f, 0.55f);

    // Shared with SettingsPanel so the two screens cannot drift apart visually.
    public static Color CyanColor => Cyan;
    public static Color NeonGreenColor => NeonGreen;
    public static Color DimColor => Dim;
    public static Color AmberColor => Amber;
    public static Color DangerColor => Danger;

    public SettingsPanel Settings { get; private set; }
    public HelpGuide Guide { get; private set; }

    Canvas _canvas;
    Font _font, _fontBold;

    // crosshair
    RectTransform _crosshair;
    Image _dot;
    readonly Image[] _brackets = new Image[4];
    float _fireT, _hitT;

    // top block
    Text _levelLabel, _waveText, _enemiesText, _scoreText, _comboText;

    // vitals
    RectTransform _vitalsRoot;
    Text _hpText;
    Image[] _hpSegments;
    Image _shieldFill, _dashFill;

    // weapon panel
    RectTransform _weaponRoot;
    Text _weaponName, _ammoText, _grenadeText;
    Image _reloadFill, _heatFill, _weaponIcon;
    Sprite[] _weaponSprites;

    // radar
    RectTransform _radarRoot;
    readonly List<Image> _blips = new List<Image>();

    // boss
    RectTransform _bossBar;
    Image _bossHpFill, _bossShieldFill;
    Text _bossName;

    // kill feed + floaters
    RectTransform _feedRoot, _floatRoot;
    readonly List<Text> _feed = new List<Text>();
    readonly List<float> _feedLife = new List<float>();
    class FloatText { public Text T; public Vector3 World; public float Life, MaxLife; }
    readonly List<FloatText> _floats = new List<FloatText>();
    readonly Stack<Text> _floatPool = new Stack<Text>();

    // banners
    Text _killStreak, _waveBanner;
    float _killStreakTimer;
    Image _damageVignette;

    // screens
    GameObject _menuScreen, _pauseScreen, _gameOverScreen, _perkScreen;
    Text _gameOverStats;
    RectTransform _perkCards;

    readonly List<GameObject> _inGameOnly = new List<GameObject>();

    const int HpSegments = 14;
    const float RadarRange = 70f;

    void Start() { Build(); }

    // ------------------------------------------------------------------ helpers

    public RectTransform Panel(string name, Transform parent, Vector2 aMin, Vector2 aMax,
                        Vector2 oMin, Vector2 oMax, Color bg) {
        var go = new GameObject(name);
        go.transform.SetParent(parent != null ? parent : _canvas.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = oMin; rt.offsetMax = oMax;
        if (bg.a > 0f) {
            var img = go.AddComponent<Image>();
            img.color = bg;
            img.raycastTarget = false;
        }
        return rt;
    }

    public Image Img(string name, Transform parent, Vector2 aMin, Vector2 aMax,
              Vector2 oMin, Vector2 oMax, Color c) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = oMin; rt.offsetMax = oMax;
        var img = go.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>
    /// A HUD label. `glow` adds an outline in the text's own colour, which is what sells the neon
    /// look on a legacy Text component - there is no bloom on a screen-space overlay canvas.
    /// </summary>
    public Text Label(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax,
               int size, TextAnchor align, Color color, bool bold = true, bool glow = true) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = oMin; rt.offsetMax = oMax;

        var t = go.AddComponent<Text>();
        t.font = bold ? _fontBold : _font;
        t.fontSize = size;
        t.alignment = align;
        t.color = color;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.supportRichText = true;

        var shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(2f, -2f);

        if (glow) {
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(color.r, color.g, color.b, 0.35f);
            outline.effectDistance = new Vector2(2f, 2f);
        }
        return t;
    }

    /// <summary>Four 2px corner brackets - the frame v2 draws round its panels.</summary>
    public void Bracket(RectTransform parent, Color c, float len = 14f, float w = 2f) {
        Img("bracket-tl-h", parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -w), new Vector2(len, 0), c);
        Img("bracket-tl-v", parent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -len), new Vector2(w, 0), c);
        Img("bracket-tr-h", parent, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-len, -w), new Vector2(0, 0), c);
        Img("bracket-tr-v", parent, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-w, -len), new Vector2(0, 0), c);
        Img("bracket-bl-h", parent, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(len, w), c);
        Img("bracket-bl-v", parent, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(w, len), c);
        Img("bracket-br-h", parent, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-len, 0), new Vector2(0, w), c);
        Img("bracket-br-v", parent, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-w, 0), new Vector2(0, len), c);
    }

    // ------------------------------------------------------------------ build

    void Build() {
        _fontBold = Resources.Load<Font>("Fonts/Orbitron-Bold");
        _font = Resources.Load<Font>("Fonts/Orbitron-Regular");
        if (_fontBold == null) _fontBold = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = _fontBold;

        var go = new GameObject("HUD");
        go.transform.SetParent(transform, false);
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();

        if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null) {
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        _damageVignette = Img("DamageFlash", _canvas.transform, Vector2.zero, Vector2.one,
                              Vector2.zero, Vector2.zero, new Color(1f, 0.1f, 0.1f, 0f));
        // A radial mask, not a flat fill. A full-screen red tint over the whole frame is what v2
        // deliberately avoids with a radial gradient: it hides the fight you are trying to survive.
        _damageVignette.sprite = BuildVignetteSprite();
        _damageVignette.type = Image.Type.Simple;

        BuildCrosshair();
        BuildTopBlock();
        BuildVitals();
        BuildWeaponPanel();
        BuildRadar();
        BuildBossBar();

        _feedRoot = Panel("KillFeed", _canvas.transform, new Vector2(0, 1), new Vector2(0, 1),
                          new Vector2(48f, -320f), new Vector2(560f, -120f), Color.clear);
        _floatRoot = Panel("Floats", _canvas.transform, Vector2.zero, Vector2.one,
                           Vector2.zero, Vector2.zero, Color.clear);

        BuildBanners();

        _inGameOnly.Add(_crosshair.gameObject);
        _inGameOnly.Add(_vitalsRoot.gameObject);
        _inGameOnly.Add(_weaponRoot.gameObject);
        _inGameOnly.Add(_radarRoot.gameObject);
        _inGameOnly.Add(_levelLabel.transform.parent.gameObject);
        _inGameOnly.Add(_feedRoot.gameObject);

        BuildMenus();

        Settings = new SettingsPanel(this, GameSettings.Instance);
        Settings.Build(_canvas.transform);

        Guide = new HelpGuide(this);
        Guide.Build();
    }

    /// <summary>
    /// Clear in the middle, opaque at the corners. Generated rather than shipped: it is a gradient,
    /// and a 128 px one costs nothing next to another texture in the build.
    /// </summary>
    static Sprite BuildVignetteSprite() {
        const int N = 128;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[N * N];
        for (int y = 0; y < N; y++) {
            for (int x = 0; x < N; x++) {
                float dx = (x / (float)(N - 1)) * 2f - 1f;
                float dy = (y / (float)(N - 1)) * 2f - 1f;
                // Elliptical falloff: screens are wider than they are tall.
                float d = Mathf.Sqrt(dx * dx * 0.72f + dy * dy);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.60f, 1.20f, d));
                px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
    }

    void BuildCrosshair() {
        _crosshair = Panel("Crosshair", _canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           new Vector2(-22f, -22f), new Vector2(22f, 22f), Color.clear);

        _dot = Img("dot", _crosshair, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                   new Vector2(-2f, -2f), new Vector2(2f, 2f), NeonGreen);

        // Corner brackets, which spring outward while firing - v2's `.firing` state.
        _brackets[0] = Img("tl", _crosshair, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -10f), new Vector2(10f, 0f), Cyan);
        _brackets[1] = Img("tr", _crosshair, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10f, -10f), new Vector2(0f, 0f), Cyan);
        _brackets[2] = Img("bl", _crosshair, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0f, 0f), new Vector2(10f, 10f), Cyan);
        _brackets[3] = Img("br", _crosshair, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-10f, 0f), new Vector2(0f, 10f), Cyan);
        foreach (var b in _brackets) b.color = new Color(Cyan.r, Cyan.g, Cyan.b, 0.75f);
    }

    void BuildTopBlock() {
        var root = Panel("TopBlock", _canvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                         new Vector2(-300f, -190f), new Vector2(300f, -10f), Color.clear);

        _levelLabel = Label("LevelLabel", root, new Vector2(0, 1), new Vector2(1, 1),
                            new Vector2(0f, -30f), new Vector2(0f, -6f), 18, TextAnchor.MiddleCenter, Dim, false);
        _waveText = Label("Wave", root, new Vector2(0, 1), new Vector2(1, 1),
                          new Vector2(0f, -86f), new Vector2(0f, -30f), 46, TextAnchor.MiddleCenter, Cyan);
        _enemiesText = Label("Enemies", root, new Vector2(0, 1), new Vector2(1, 1),
                             new Vector2(0f, -116f), new Vector2(0f, -86f), 20, TextAnchor.MiddleCenter, Danger, false);
        _scoreText = Label("Score", root, new Vector2(0, 1), new Vector2(1, 1),
                           new Vector2(0f, -146f), new Vector2(0f, -116f), 20, TextAnchor.MiddleCenter, Amber, false);
        _comboText = Label("Combo", root, new Vector2(0, 1), new Vector2(1, 1),
                           new Vector2(0f, -176f), new Vector2(0f, -146f), 20, TextAnchor.MiddleCenter, NeonGreen, false);
    }

    void BuildVitals() {
        _vitalsRoot = Panel("Vitals", _canvas.transform, new Vector2(0, 0), new Vector2(0, 0),
                            new Vector2(40f, 34f), new Vector2(420f, 130f), Color.clear);

        _hpText = Label("HpText", _vitalsRoot, new Vector2(0, 0), new Vector2(1, 0),
                        new Vector2(2f, 66f), new Vector2(0f, 94f), 22, TextAnchor.LowerLeft, NeonGreen, false);

        // Segmented, not a smooth fill: v2's bar is a row of blocks running red to green, so the
        // colour tells you how badly you are hurt even at a glance.
        _hpSegments = new Image[HpSegments];
        const float segGap = 3f;
        for (int i = 0; i < HpSegments; i++) {
            float t = i / (float)(HpSegments - 1);
            var c = t < 0.5f
                ? Color.Lerp(new Color(1f, 0.15f, 0.1f), new Color(1f, 0.82f, 0.15f), t * 2f)
                : Color.Lerp(new Color(1f, 0.82f, 0.15f), new Color(0.25f, 1f, 0.35f), (t - 0.5f) * 2f);
            float x0 = i / (float)HpSegments, x1 = (i + 1) / (float)HpSegments;
            _hpSegments[i] = Img($"hp{i}", _vitalsRoot, new Vector2(x0, 0f), new Vector2(x1, 0f),
                                 new Vector2(0f, 34f), new Vector2(-segGap, 56f), c);
        }

        Img("ShieldBg", _vitalsRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0f, 20f), new Vector2(0f, 28f),
            new Color(0f, 0f, 0f, 0.5f));
        _shieldFill = Img("ShieldFill", _vitalsRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0f, 20f), new Vector2(0f, 28f),
                          new Color(0.25f, 0.62f, 1f));
        _shieldFill.type = Image.Type.Filled;
        _shieldFill.fillMethod = Image.FillMethod.Horizontal;

        Img("DashBg", _vitalsRoot, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0f, 8f), new Vector2(150f, 12f),
            new Color(0f, 0f, 0f, 0.5f));
        _dashFill = Img("DashFill", _vitalsRoot, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0f, 8f), new Vector2(150f, 12f),
                        Cyan);
        _dashFill.type = Image.Type.Filled;
        _dashFill.fillMethod = Image.FillMethod.Horizontal;
    }

    void BuildWeaponPanel() {
        _weaponRoot = Panel("WeaponPanel", _canvas.transform, new Vector2(1, 0), new Vector2(1, 0),
                            new Vector2(-470f, 34f), new Vector2(-40f, 168f), PanelBg);
        Bracket(_weaponRoot, new Color(Cyan.r, Cyan.g, Cyan.b, 0.8f), 18f, 2f);

        // v2's panel carries a silhouette of the weapon, sliced from a 2x2 sheet in the order
        // the arsenal is listed: rifle, plasma, sword, rocket.
        _weaponSprites = LoadWeaponSprites();
        _weaponIcon = Img("WeaponIcon", _weaponRoot, new Vector2(0, 0), new Vector2(0, 0),
                          new Vector2(16f, 74f), new Vector2(132f, 122f), new Color(1f, 1f, 1f, 0.95f));
        _weaponIcon.preserveAspect = true;

        _weaponName = Label("WeaponName", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0),
                            new Vector2(140f, 74f), new Vector2(-16f, 112f), 28, TextAnchor.MiddleRight, Cyan);
        _ammoText = Label("Ammo", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0),
                          new Vector2(16f, 34f), new Vector2(-16f, 72f), 28, TextAnchor.MiddleRight, NeonGreen, false);
        _grenadeText = Label("Grenades", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0),
                             new Vector2(16f, 10f), new Vector2(-16f, 32f), 17, TextAnchor.MiddleRight, Dim, false);

        Img("ReloadBg", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16f, 66f), new Vector2(-16f, 70f),
            new Color(0f, 0f, 0f, 0.6f));
        _reloadFill = Img("ReloadFill", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16f, 66f), new Vector2(-16f, 70f),
                          Cyan);
        _reloadFill.type = Image.Type.Filled;
        _reloadFill.fillMethod = Image.FillMethod.Horizontal;

        _heatFill = Img("HeatFill", _weaponRoot, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16f, 60f), new Vector2(-16f, 64f),
                        new Color(1f, 0.5f, 0.15f));
        _heatFill.type = Image.Type.Filled;
        _heatFill.fillMethod = Image.FillMethod.Horizontal;
    }

    /// <summary>
    /// Top-right radar. Not decoration: the roster flanks, and without it a Skirmisher orbiting
    /// behind you is invisible until it fires.
    /// </summary>
    void BuildRadar() {
        _radarRoot = Panel("Radar", _canvas.transform, new Vector2(1, 1), new Vector2(1, 1),
                           new Vector2(-230f, -230f), new Vector2(-40f, -40f), new Color(0.01f, 0.05f, 0.03f, 0.55f));
        Bracket(_radarRoot, new Color(NeonGreen.r, NeonGreen.g, NeonGreen.b, 0.85f), 16f, 2f);

        var title = Label("RadarTitle", _radarRoot, new Vector2(0, 1), new Vector2(1, 1),
                          new Vector2(0f, 2f), new Vector2(0f, 22f), 14, TextAnchor.MiddleCenter, NeonGreen, false);
        title.text = "RADAR";

        // Grid: a cross plus a mid-range ring drawn as four ticks.
        Img("gridH", _radarRoot, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(6f, -0.5f), new Vector2(-6f, 0.5f),
            new Color(NeonGreen.r, NeonGreen.g, NeonGreen.b, 0.22f));
        Img("gridV", _radarRoot, new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-0.5f, 6f), new Vector2(0.5f, -6f),
            new Color(NeonGreen.r, NeonGreen.g, NeonGreen.b, 0.22f));

        // The player: a fixed pip at centre. The radar is player-relative and rotates with them.
        Img("self", _radarRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(-2.5f, -2.5f), new Vector2(2.5f, 2.5f), NeonGreen);
    }

    void BuildBossBar() {
        _bossBar = Panel("BossBar", _canvas.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                         new Vector2(-420f, -250f), new Vector2(420f, -196f), Color.clear);

        _bossName = Label("BossName", _bossBar, Vector2.zero, Vector2.one, new Vector2(0f, 26f), new Vector2(0f, 0f),
                          20, TextAnchor.LowerCenter, new Color(1f, 0.45f, 0.95f));
        Img("BossBg", _bossBar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0f, 0f), new Vector2(0f, 12f),
            new Color(0f, 0f, 0f, 0.6f));
        _bossHpFill = Img("BossHp", _bossBar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(2f, 2f), new Vector2(-2f, 10f),
                          new Color(1f, 0.25f, 0.55f));
        _bossHpFill.type = Image.Type.Filled;
        _bossHpFill.fillMethod = Image.FillMethod.Horizontal;
        _bossShieldFill = Img("BossShield", _bossBar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(2f, 12f), new Vector2(-2f, 17f),
                              new Color(0.45f, 0.8f, 1f));
        _bossShieldFill.type = Image.Type.Filled;
        _bossShieldFill.fillMethod = Image.FillMethod.Horizontal;
        _bossBar.gameObject.SetActive(false);
    }

    void BuildBanners() {
        _killStreak = Label("KillStreak", _canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            new Vector2(-460f, 96f), new Vector2(460f, 176f), 52, TextAnchor.MiddleCenter,
                            new Color(Amber.r, Amber.g, Amber.b, 0f));
        _waveBanner = Label("WaveBanner", _canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                            new Vector2(-620f, -190f), new Vector2(620f, -80f), 38, TextAnchor.MiddleCenter,
                            new Color(1f, 1f, 1f, 0f));
    }

    /// <summary>Slice the 2x2 icon sheet into one sprite per weapon, in Arsenal.Order.</summary>
    Sprite[] LoadWeaponSprites() {
        var sheet = Resources.Load<Texture2D>("UI/weapons");
        if (sheet == null) return null;
        float hw = sheet.width * 0.5f, hh = sheet.height * 0.5f;
        // Sheet rows run top-down; Sprite rects are bottom-up, so the top row is y = hh.
        var rects = new[] {
            new Rect(0f, hh, hw, hh),   // rifle
            new Rect(hw, hh, hw, hh),   // plasma rifle
            new Rect(0f, 0f, hw, hh),   // energy sword
            new Rect(hw, 0f, hw, hh),   // rocket launcher
        };
        var sprites = new Sprite[4];
        for (int i = 0; i < 4; i++)
            sprites[i] = Sprite.Create(sheet, rects[i], new Vector2(0.5f, 0.5f), 100f);
        return sprites;
    }

    // ------------------------------------------------------------------ screens

    public GameObject MakeScreen(string name, Color bg) {
        var rt = Panel(name, _canvas.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, bg);
        var img = rt.GetComponent<Image>();
        if (img != null) img.raycastTarget = true;
        rt.gameObject.SetActive(false);
        return rt.gameObject;
    }

    public Button Btn(Transform parent, string label, Vector2 anchor, Vector2 oMin, Vector2 oMax, System.Action onClick) {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.offsetMin = oMin; rt.offsetMax = oMax;
        var img = go.AddComponent<Image>();
        img.color = new Color(0.03f, 0.14f, 0.18f, 0.95f);
        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.4f, 0.9f, 1f);
        colors.pressedColor = Cyan;
        btn.colors = colors;
        btn.onClick.AddListener(() => onClick());
        Bracket(rt, new Color(Cyan.r, Cyan.g, Cyan.b, 0.9f), 14f, 2f);

        var t = Label("Label", rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 26,
                      TextAnchor.MiddleCenter, Cyan);
        t.text = label;
        return btn;
    }

    void BuildMenus() {
        _menuScreen = MakeScreen("MenuScreen", new Color(0.01f, 0.02f, 0.05f, 0.94f));
        var title = Label("Title", _menuScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                          new Vector2(-700f, 130f), new Vector2(700f, 250f), 76, TextAnchor.MiddleCenter, Cyan);
        title.text = "UFO INVASION III";
        var sub = Label("Sub", _menuScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(-700f, 74f), new Vector2(700f, 124f), 24, TextAnchor.MiddleCenter, NeonGreen, false);
        sub.text = "// CHICAGO HAS FALLEN . HOLD THE LINE //";
        var help = Label("Help", _menuScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                         new Vector2(-700f, -370f), new Vector2(700f, -240f), 18, TextAnchor.UpperCenter, Dim, false, false);
        help.text = "WASD MOVE   .   MOUSE LOOK   .   LMB FIRE   .   RMB AIM / HEAVY\n" +
                    "1-4 WEAPONS   .   Q GRENADE   .   R RELOAD   .   SHIFT SPRINT   .   SPACE JUMP   .   E DASH\n\n" +
                    "PLASMA STRIPS SHIELDS  .  BULLETS HURT FLESH  .  ROCKETS SOLVE JUGGERNAUTS  .  THE SWORD LUNGES";
        Btn(_menuScreen.transform, "START", new Vector2(0.5f, 0.5f), new Vector2(-160f, -60f), new Vector2(160f, 4f),
            () => Game.StartGame());
        Btn(_menuScreen.transform, "SETTINGS", new Vector2(0.5f, 0.5f), new Vector2(-160f, -132f), new Vector2(160f, -70f),
            () => Settings.Open());
        Btn(_menuScreen.transform, "FIELD GUIDE", new Vector2(0.5f, 0.5f), new Vector2(-160f, -204f), new Vector2(160f, -142f),
            () => Guide.Open());

        _pauseScreen = MakeScreen("PauseScreen", new Color(0.01f, 0.02f, 0.05f, 0.8f));
        var pt = Label("PausedTitle", _pauseScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                       new Vector2(-400f, 60f), new Vector2(400f, 160f), 58, TextAnchor.MiddleCenter, Cyan);
        pt.text = "[ PAUSED ]";
        Btn(_pauseScreen.transform, "RESUME", new Vector2(0.5f, 0.5f), new Vector2(-160f, -34f), new Vector2(160f, 30f),
            () => Game.TogglePause());
        Btn(_pauseScreen.transform, "SETTINGS", new Vector2(0.5f, 0.5f), new Vector2(-160f, -106f), new Vector2(160f, -44f),
            () => Settings.Open());

        _gameOverScreen = MakeScreen("GameOverScreen", new Color(0.10f, 0.01f, 0.03f, 0.92f));
        var got = Label("GameOverTitle", _gameOverScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(-600f, 120f), new Vector2(600f, 240f), 66, TextAnchor.MiddleCenter, Danger);
        got.text = "YOU ARE DOWN";
        _gameOverStats = Label("GameOverStats", _gameOverScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                               new Vector2(-600f, 6f), new Vector2(600f, 116f), 26, TextAnchor.MiddleCenter, Cyan, false);
        Btn(_gameOverScreen.transform, "TRY AGAIN", new Vector2(0.5f, 0.5f), new Vector2(-180f, -74f), new Vector2(180f, -10f),
            () => Game.StartGame());

        _perkScreen = MakeScreen("PerkScreen", new Color(0.01f, 0.03f, 0.07f, 0.88f));
        var perkTitle = Label("PerkTitle", _perkScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              new Vector2(-600f, 150f), new Vector2(600f, 230f), 40, TextAnchor.MiddleCenter, Cyan);
        perkTitle.text = "// CHOOSE AN UPGRADE //";
        _perkCards = Panel("PerkCards", _perkScreen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           new Vector2(-680f, -120f), new Vector2(680f, 120f), Color.clear);
    }

    /// <summary>The menu/pause/game-over screen currently showing, if any.</summary>
    public GameObject ActiveScreen() {
        if (_menuScreen != null && _menuScreen.activeSelf) return _menuScreen;
        if (_pauseScreen != null && _pauseScreen.activeSelf) return _pauseScreen;
        if (_gameOverScreen != null && _gameOverScreen.activeSelf) return _gameOverScreen;
        return null;
    }

    void ShowPerkCards() {
        foreach (Transform c in _perkCards) Destroy(c.gameObject);
        var choices = PlayerStats.RollChoices(3);
        for (int i = 0; i < choices.Count; i++) {
            var perk = choices[i];
            const float w = 400f, gap = 32f;
            float x = (i - (choices.Count - 1) * 0.5f) * (w + gap);

            var go = new GameObject("Card_" + perk.Id);
            go.transform.SetParent(_perkCards, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(x - w * 0.5f, -110f);
            rt.offsetMax = new Vector2(x + w * 0.5f, 110f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.02f, 0.09f, 0.13f, 0.95f);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.45f, 0.9f, 1f);
            btn.colors = colors;
            var captured = perk;
            btn.onClick.AddListener(() => Game.ChoosePerk(captured));
            Bracket(rt, new Color(Cyan.r, Cyan.g, Cyan.b, 0.85f), 18f, 2f);

            var n = Label("Name", rt, Vector2.zero, Vector2.one, new Vector2(14f, 44f), new Vector2(-14f, -26f),
                          28, TextAnchor.UpperCenter, Cyan);
            n.text = perk.Name;
            var d = Label("Desc", rt, Vector2.zero, Vector2.one, new Vector2(20f, 16f), new Vector2(-20f, -84f),
                          19, TextAnchor.UpperCenter, Dim, false, false);
            d.text = perk.Desc;
            d.horizontalOverflow = HorizontalWrapMode.Wrap;
        }
    }

    // ------------------------------------------------------------------ per-frame

    GameState _lastState = (GameState)(-1);

    void LateUpdate() {
        if (Game == null || _canvas == null) return;
        float dt = Time.deltaTime;

        if (Game.State != _lastState) {
            _lastState = Game.State;
            if (Settings != null && Settings.IsOpen) Settings.Close();
            if (Guide != null && Guide.IsOpen) Guide.Close();
            _menuScreen.SetActive(Game.State == GameState.Menu);
            _pauseScreen.SetActive(Game.State == GameState.Paused);
            _gameOverScreen.SetActive(Game.State == GameState.GameOver);
            _perkScreen.SetActive(Game.State == GameState.PerkSelect);

            if (Game.State == GameState.PerkSelect) ShowPerkCards();
            if (Game.State == GameState.GameOver) {
                _gameOverStats.text =
                    $"WAVE {Game.Waves.Wave}     SCORE {Game.Stats.Score:N0}\n" +
                    $"{Game.Stats.Kills} KILLS     BEST COMBO x{Game.Stats.BestCombo}";
            }
        }

        bool inRun = Game.State == GameState.Playing || Game.State == GameState.Paused
                  || Game.State == GameState.PerkSelect;
        for (int i = 0; i < _inGameOnly.Count; i++)
            if (_inGameOnly[i].activeSelf != inRun) _inGameOnly[i].SetActive(inRun);
        _crosshair.gameObject.SetActive(Game.State == GameState.Playing);

        UpdateBanners(dt);
        if (!inRun) return;

        UpdateCrosshair(dt);
        UpdateVitals(dt);
        UpdateWeapon();
        UpdateTopBlock();
        UpdateRadar();
        UpdateBoss();
        UpdateFeed(dt);
        UpdateFloats(dt);
    }

    void UpdateCrosshair(float dt) {
        if (_fireT > 0f) _fireT -= dt;
        if (_hitT > 0f) _hitT -= dt;

        float spread = _fireT > 0f ? 6f : 0f;
        _brackets[0].rectTransform.offsetMin = new Vector2(-spread, -10f - spread);
        _brackets[0].rectTransform.offsetMax = new Vector2(10f - spread, -spread);
        _brackets[1].rectTransform.offsetMin = new Vector2(-10f + spread, -10f - spread);
        _brackets[1].rectTransform.offsetMax = new Vector2(spread, -spread);
        _brackets[2].rectTransform.offsetMin = new Vector2(-spread, spread);
        _brackets[2].rectTransform.offsetMax = new Vector2(10f - spread, 10f + spread);
        _brackets[3].rectTransform.offsetMin = new Vector2(-10f + spread, spread);
        _brackets[3].rectTransform.offsetMax = new Vector2(spread, 10f + spread);

        _dot.color = _hitT > 0f ? Danger : NeonGreen;
    }

    void UpdateVitals(float dt) {
        var s = Game.Stats;
        float frac = Mathf.Clamp01(s.Hp / s.MaxHp);
        int lit = Mathf.CeilToInt(frac * HpSegments);
        for (int i = 0; i < HpSegments; i++) {
            var c = _hpSegments[i].color;
            c.a = i < lit ? 1f : 0.12f;
            _hpSegments[i].color = c;
        }
        _hpText.text = $"HP {Mathf.CeilToInt(s.Hp)}/{Mathf.CeilToInt(s.MaxHp)}";
        _hpText.color = frac < 0.3f ? Danger : NeonGreen;

        _shieldFill.fillAmount = Mathf.Clamp01(s.Shield / s.MaxShield);

        float ready = Game.Player.DashCooldown <= 0f ? 1f
                    : 1f - Game.Player.DashCooldown / Game.Player.DashCooldownMax;
        _dashFill.fillAmount = ready;
        _dashFill.color = ready >= 1f ? Cyan : new Color(Cyan.r, Cyan.g, Cyan.b, 0.3f);

        var vc = _damageVignette.color;
        // Also rises as health falls, so being nearly dead is legible without watching the bar.
        float hurt = 1f - Mathf.Clamp01(s.Hp / Mathf.Max(1f, s.MaxHp));
        float flash = Mathf.Max(0f, s.DamageFlashTimer / 0.2f);
        // Only the last quarter of the health bar tints at all, and never past half opacity:
        // this is a warning at the edge of vision, not a filter over the game.
        vc.a = Mathf.Min(0.62f, flash * 0.5f + Mathf.Max(0f, hurt - 0.75f) * 1.2f);
        _damageVignette.color = vc;
    }

    void UpdateWeapon() {
        var w = Game.Weapons;
        _weaponName.text = w.Weapon.Name;
        _ammoText.text = w.AmmoText();
        _grenadeText.text = "GRENADES  " + w.Grenades;
        _reloadFill.fillAmount = w.IsReloading ? w.ReloadPct : 0f;
        _heatFill.fillAmount = w.Weapon.UsesHeat ? 1f - w.CooldownPct() : 0f;

        bool low = w.Weapon.UsesMag && w.Ammo >= 0 && w.MaxAmmo > 0 && w.Ammo <= w.MaxAmmo * 0.25f;
        _ammoText.color = low ? Danger : NeonGreen;

        if (_weaponSprites != null) {
            int slot = System.Array.IndexOf(Arsenal.Order, w.Current);
            if (slot >= 0 && slot < _weaponSprites.Length) {
                _weaponIcon.sprite = _weaponSprites[slot];
                _weaponIcon.enabled = true;
            }
        } else _weaponIcon.enabled = false;
    }

    void UpdateTopBlock() {
        var wv = Game.Waves;
        _levelLabel.text = Game.Level.Current != null ? $"// {Game.Level.Current.Name} //" : "";
        _waveText.text = $"[ WAVE {wv.Wave} ]";
        _enemiesText.text = wv.AliveCount > 0 ? $"ENEMIES: {wv.AliveCount}" : "WAVE CLEAR";
        _enemiesText.color = wv.AliveCount > 0 ? Danger : NeonGreen;
        _scoreText.text = $"SCORE: {Game.Stats.Score:N0}";
        _comboText.text = Game.Stats.Combo > 1 ? $"x{Game.Stats.Combo} COMBO" : "";
    }

    /// <summary>Player-relative, rotated into the player's facing so up on the radar is forward.</summary>
    void UpdateRadar() {
        var enemies = Game.Waves.Enemies;
        var cam = Game.Player.Cam;
        Vector3 origin = Game.Player.transform.position;
        float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
        float cos = Mathf.Cos(yaw * Mathf.Deg2Rad), sin = Mathf.Sin(yaw * Mathf.Deg2Rad);

        int used = 0;
        for (int i = 0; i < enemies.Count; i++) {
            var e = enemies[i];
            if (e == null || e.Dead) continue;

            Vector3 d = e.transform.position - origin;
            float dist = new Vector2(d.x, d.z).magnitude;
            if (dist > RadarRange) continue;

            // Rotate world offset into view space: forward becomes +Y on the radar.
            float rx = d.x * cos - d.z * sin;
            float rz = d.x * sin + d.z * cos;

            var blip = BlipAt(used++);
            float nx = Mathf.Clamp(rx / RadarRange, -1f, 1f) * 0.45f;
            float nz = Mathf.Clamp(rz / RadarRange, -1f, 1f) * 0.45f;
            var rt = blip.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f + nx, 0.5f + nz);
            float size = e.IsBoss ? 6f : (e.Elite ? 5f : 3.5f);
            rt.offsetMin = new Vector2(-size, -size);
            rt.offsetMax = new Vector2(size, size);
            blip.color = e.IsBoss ? new Color(1f, 0.3f, 0.95f)
                       : e.Elite ? Amber
                       : e.Data.HoverHeight > 0f ? new Color(0.6f, 1f, 0.4f) : Danger;
            blip.gameObject.SetActive(true);
        }
        for (int i = used; i < _blips.Count; i++) _blips[i].gameObject.SetActive(false);
    }

    Image BlipAt(int i) {
        while (_blips.Count <= i) {
            _blips.Add(Img("blip" + _blips.Count, _radarRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                           new Vector2(-3f, -3f), new Vector2(3f, 3f), Danger));
        }
        return _blips[i];
    }

    void UpdateBoss() {
        var boss = Game.Waves.GetBoss();
        if (boss == null) { if (_bossBar.gameObject.activeSelf) _bossBar.gameObject.SetActive(false); return; }
        if (!_bossBar.gameObject.activeSelf) _bossBar.gameObject.SetActive(true);
        _bossName.text = $"// {boss.Data.Name} //";
        _bossHpFill.fillAmount = Mathf.Clamp01(boss.Hp / boss.MaxHp);
        _bossShieldFill.fillAmount = boss.MaxShield > 0f ? Mathf.Clamp01(boss.Shield / boss.MaxShield) : 0f;
    }

    void UpdateFeed(float dt) {
        for (int i = _feedLife.Count - 1; i >= 0; i--) {
            _feedLife[i] -= dt;
            var t = _feed[i];
            var c = t.color; c.a = Mathf.Clamp01(_feedLife[i] / 1.2f); t.color = c;
            if (_feedLife[i] <= 0f) {
                Destroy(t.gameObject);
                _feed.RemoveAt(i); _feedLife.RemoveAt(i);
            }
        }
        for (int i = 0; i < _feed.Count; i++) {
            var rt = _feed[i].rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(0f, -28f - i * 28f);
            rt.offsetMax = new Vector2(520f, -2f - i * 28f);
        }
    }

    void UpdateFloats(float dt) {
        var cam = Game.Player.Cam;
        for (int i = _floats.Count - 1; i >= 0; i--) {
            var f = _floats[i];
            f.Life -= dt;
            f.World += Vector3.up * (1.6f * dt);

            var vp = cam.WorldToViewportPoint(f.World);
            bool visible = vp.z > 0f;
            f.T.gameObject.SetActive(visible);
            if (visible) {
                var rt = f.T.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(vp.x, vp.y);
                rt.offsetMin = new Vector2(-70f, -20f);
                rt.offsetMax = new Vector2(70f, 20f);
                var c = f.T.color; c.a = Mathf.Clamp01(f.Life / f.MaxLife); f.T.color = c;
            }
            if (f.Life <= 0f) {
                f.T.gameObject.SetActive(false);
                _floatPool.Push(f.T);
                _floats.RemoveAt(i);
            }
        }
    }

    void UpdateBanners(float dt) {
        if (_killStreakTimer > 0f) {
            _killStreakTimer -= dt;
            var c = _killStreak.color; c.a = Mathf.Clamp01(_killStreakTimer / 0.5f); _killStreak.color = c;
        }
        float wb = Game.WaveBannerTimer;
        var bc = _waveBanner.color; bc.a = Mathf.Clamp01(wb / 0.8f); _waveBanner.color = bc;
        if (wb > 0f && Game.Level.Current != null)
            _waveBanner.text = $"// {Game.Level.Current.Name} //\n<size=20>{Game.Level.Current.Subtitle.ToUpperInvariant()}</size>";
    }

    // ------------------------------------------------------------------ API

    public void ShowHitMarker(bool killed) {
        _hitT = 0.15f;
        _dot.color = killed ? Danger : Color.white;
    }

    public void NotifyFired() { _fireT = 0.09f; }

    public void ShowDamageNumber(Vector3 world, string text, bool killed, bool headshot) {
        Text t = _floatPool.Count > 0 ? _floatPool.Pop() : null;
        if (t == null) {
            t = Label("Dmg", _floatRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(-70f, -20f), new Vector2(70f, 20f), 22, TextAnchor.MiddleCenter, Color.white, false);
        }
        t.gameObject.SetActive(true);
        t.text = text;
        t.fontSize = killed ? 32 : (headshot ? 28 : 22);
        t.color = killed ? Danger
                : headshot ? Amber
                : text == "SHIELD" ? new Color(0.45f, 0.75f, 1f) : Color.white;
        _floats.Add(new FloatText { T = t, World = world, Life = 0.9f, MaxLife = 0.9f });
    }

    public void ShowKillStreak(string name, bool mega) {
        _killStreak.text = name;
        _killStreak.fontSize = mega ? 68 : 52;
        _killStreak.color = mega ? new Color(1f, 0.4f, 0.1f, 1f) : new Color(Amber.r, Amber.g, Amber.b, 1f);
        _killStreakTimer = 1.5f;
    }

    public void AddKillFeed(string enemyName, string weaponName) {
        var t = Label("Feed", _feedRoot, new Vector2(0, 1), new Vector2(0, 1),
                      new Vector2(0f, -28f), new Vector2(520f, -2f), 17, TextAnchor.MiddleLeft, Color.white, false, false);
        t.text = $"<color=#5bd8ff>{weaponName}</color>  >>  <color=#ff6a6a>{enemyName}</color>";

        _feed.Insert(0, t);
        _feedLife.Insert(0, 4f);
        while (_feed.Count > 6) {
            Destroy(_feed[_feed.Count - 1].gameObject);
            _feed.RemoveAt(_feed.Count - 1);
            _feedLife.RemoveAt(_feedLife.Count - 1);
        }
    }
}

}
