using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UFO {

/// <summary>
/// The settings screen, generated from GameSettings.Schema rather than hand-laid.
///
/// Built from the schema on purpose: adding a setting is one entry in the model, not a new block
/// of layout code here, so the two cannot drift apart. Rows apply live as they are dragged and
/// save on close, which is what makes a brightness slider usable - you judge it against the game,
/// not against a preview.
///
/// Styling comes from the Hud so this matches the rest of the interface instead of being a grey
/// Unity dialog bolted onto a neon game.
/// </summary>
public class SettingsPanel {

    readonly Hud _hud;
    readonly GameSettings _settings;

    GameObject _root;
    readonly List<(GameSettings.Row row, Slider slider, Text value)> _rows =
        new List<(GameSettings.Row, Slider, Text)>();

    public bool IsOpen => _root != null && _root.activeSelf;

    // The screen this was opened over, hidden while the settings are up and restored on close.
    GameObject _covered;

    public SettingsPanel(Hud hud, GameSettings settings) {
        _hud = hud;
        _settings = settings;
    }

    public void Build(Transform canvas) {
        _root = _hud.MakeScreen("SettingsScreen", new Color(0.01f, 0.03f, 0.06f, 0.95f));

        var title = _hud.Label("SettingsTitle", _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(-400f, -96f), new Vector2(400f, -40f), 40,
                               TextAnchor.MiddleCenter, Hud.CyanColor);
        title.text = "// SETTINGS //";

        // Two columns: four groups is too tall for 1080p in one.
        BuildColumn(new[] { "DISPLAY", "PERFORMANCE" }, -470f);
        BuildColumn(new[] { "CONTROLS", "AUDIO" }, 40f);

        _hud.Btn(_root.transform, "RESET", new Vector2(0.5f, 0f), new Vector2(-330f, 44f), new Vector2(-110f, 100f),
                 () => { _settings.ResetToDefaults(); _settings.Apply(); Refresh(); });
        _hud.Btn(_root.transform, "BACK", new Vector2(0.5f, 0f), new Vector2(110f, 44f), new Vector2(330f, 100f),
                 Close);

        var hint = _hud.Label("SettingsHint", _root.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(-500f, 12f), new Vector2(500f, 40f), 15,
                              TextAnchor.MiddleCenter, Hud.DimColor, false, false);
        hint.text = "CHANGES APPLY IMMEDIATELY  .  ESC TO GO BACK";

        _root.SetActive(false);
    }

    void BuildColumn(string[] groups, float x) {
        float y = -140f;
        const float rowH = 40f, groupGap = 30f;

        foreach (var group in groups) {
            var header = _hud.Label("grp_" + group, _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                    new Vector2(x, y - 26f), new Vector2(x + 430f, y), 18,
                                    TextAnchor.MiddleLeft, Hud.NeonGreenColor, true, false);
            header.text = "// " + group;
            y -= 34f;

            foreach (var row in GameSettings.Schema) {
                if (row.Group != group) continue;
                BuildRow(row, x, y, rowH);
                y -= rowH;
            }
            y -= groupGap;
        }
    }

    void BuildRow(GameSettings.Row row, float x, float y, float h) {
        var label = _hud.Label("lbl_" + row.Label, _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(x, y - h + 8f), new Vector2(x + 190f, y - 4f), 16,
                               TextAnchor.MiddleLeft, Hud.DimColor, false, false);
        label.text = row.Label;
        if (!string.IsNullOrEmpty(row.Tip)) label.text = row.Label;

        var slider = MakeSlider("sld_" + row.Label,
                                new Vector2(x + 196f, y - h + 14f), new Vector2(x + 366f, y - 10f),
                                row.Min, row.Max, row.Kind == GameSettings.Kind.Toggle);

        var value = _hud.Label("val_" + row.Label, _root.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(x + 372f, y - h + 8f), new Vector2(x + 456f, y - 4f), 16,
                               TextAnchor.MiddleRight, Hud.CyanColor, false, false);

        slider.value = row.Get(_settings);
        value.text = row.Format(slider.value);

        var captured = row;
        var capturedValue = value;
        slider.onValueChanged.AddListener(v => {
            captured.Set(_settings, v);
            capturedValue.text = captured.Format(v);
            // Live: the whole point of a display slider is judging it against the running game.
            _settings.Apply();
        });

        _rows.Add((row, slider, value));
    }

    /// <summary>
    /// A uGUI Slider built from scratch - background, fill and handle wired by hand, because
    /// there is no prefab to instantiate in a project that builds its entire UI from code.
    /// </summary>
    Slider MakeSlider(string name, Vector2 offsetMin, Vector2 offsetMax, float min, float max, bool whole) {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;

        var slider = go.AddComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = whole;

        var bg = _hud.Img("bg", rt, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                          new Vector2(0f, -3f), new Vector2(0f, 3f), new Color(0f, 0f, 0f, 0.55f));

        var fillArea = _hud.Panel("fillArea", rt, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                                  new Vector2(0f, -3f), new Vector2(0f, 3f), Color.clear);
        var fill = _hud.Img("fill", fillArea, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Hud.CyanColor);

        var handleArea = _hud.Panel("handleArea", rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.clear);
        var handle = _hud.Img("handle", handleArea, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(-6f, -9f), new Vector2(6f, 9f), Hud.NeonGreenColor);
        handle.raycastTarget = true;

        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.LeftToRight;

        var colors = slider.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.6f, 1f, 1f);
        colors.pressedColor = Hud.CyanColor;
        slider.colors = colors;

        bg.raycastTarget = true;
        return slider;
    }

    /// <summary>Pull the widgets back into line with the model, after a reset or a reload.</summary>
    public void Refresh() {
        foreach (var (row, slider, value) in _rows) {
            slider.SetValueWithoutNotify(row.Get(_settings));
            value.text = row.Format(slider.value);
        }
    }

    public void Open() {
        if (_root == null) return;
        Refresh();
        // Hide whatever is underneath rather than trying to cover it with opacity: bright glowing
        // text reads clearly through even a 95% panel.
        _covered = _hud.ActiveScreen();
        if (_covered != null) _covered.SetActive(false);
        _root.SetActive(true);
    }

    public void Close() {
        if (_root == null) return;
        _root.SetActive(false);
        if (_covered != null) { _covered.SetActive(true); _covered = null; }
        _settings.Save();
    }

    public void Toggle() { if (IsOpen) Close(); else Open(); }
}

}
