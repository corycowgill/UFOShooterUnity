using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UFO {

/// <summary>
/// Player-facing display, audio and control settings: the model, its persistence, and the code
/// that pushes it into the renderer.
///
/// Two decisions worth knowing:
///
/// 1. **Everything is applied live.** Apply() writes straight into the post-process volume, the
///    URP asset and the camera, so a slider moves the picture while the player is looking at it.
///    A settings screen you have to close before you can judge is a settings screen nobody tunes.
///
/// 2. **PlayerPrefs, not a file.** On WebGL that is backed by IndexedDB and survives a reload,
///    which is the only persistence this game needs and the only one available in a browser
///    without a backend.
///
/// The defaults are the values the game was calibrated at, so "Reset" returns to the tuned look
/// rather than to Unity's.
/// </summary>
public class GameSettings {

    public static GameSettings Instance { get; private set; } = new GameSettings();

    // ---- display ----------------------------------------------------------
    public float Brightness = 0.55f;    // post exposure, EV, set by the calibration sweep
    public float Contrast = 8f;
    public float Saturation = 6f;
    public float Bloom = 0.75f;
    public float Vignette = 0.28f;
    public float FogScale = 1f;         // multiplier on the level's own fog density

    // ---- performance ------------------------------------------------------
    public float RenderScale = 1f;      // the biggest single perf lever on WebGL
    public float ShadowDistance = 90f;  // 0 turns shadows off entirely

    // ---- camera / controls -----------------------------------------------
    public float Fov = 75f;
    public float LookSensitivity = 1f;  // multiplier on the base 0.002 rad/px
    public bool InvertY = false;

    // ---- audio ------------------------------------------------------------
    public float MasterVolume = 1f;
    public float SfxVolume = 0.6f;
    public float MusicVolume = 0.35f;

    const string Prefix = "ufo.";

    // Bound by GameManager so Apply() has something to write to.
    public Camera Cam;
    public PlayerController Player;
    public GameAudio Audio;
    public WeaponManager Weapons;

    Volume _volume;
    ColorAdjustments _grade;
    Bloom _bloom;
    Vignette _vignette;

    /// <summary>Set by LevelBuilder each time a level loads, so FogScale has a base to scale.</summary>
    public float BaseFogDensity = 0.0048f;

    // ------------------------------------------------------------------ persistence

    public void Load() {
        Brightness      = PlayerPrefs.GetFloat(Prefix + "brightness", Brightness);
        Contrast        = PlayerPrefs.GetFloat(Prefix + "contrast", Contrast);
        Saturation      = PlayerPrefs.GetFloat(Prefix + "saturation", Saturation);
        Bloom           = PlayerPrefs.GetFloat(Prefix + "bloom", Bloom);
        Vignette        = PlayerPrefs.GetFloat(Prefix + "vignette", Vignette);
        FogScale        = PlayerPrefs.GetFloat(Prefix + "fog", FogScale);
        RenderScale     = PlayerPrefs.GetFloat(Prefix + "renderScale", RenderScale);
        ShadowDistance  = PlayerPrefs.GetFloat(Prefix + "shadowDistance", ShadowDistance);
        Fov             = PlayerPrefs.GetFloat(Prefix + "fov", Fov);
        LookSensitivity = PlayerPrefs.GetFloat(Prefix + "sensitivity", LookSensitivity);
        InvertY         = PlayerPrefs.GetInt(Prefix + "invertY", InvertY ? 1 : 0) != 0;
        MasterVolume    = PlayerPrefs.GetFloat(Prefix + "master", MasterVolume);
        SfxVolume       = PlayerPrefs.GetFloat(Prefix + "sfx", SfxVolume);
        MusicVolume     = PlayerPrefs.GetFloat(Prefix + "music", MusicVolume);
    }

    public void Save() {
        PlayerPrefs.SetFloat(Prefix + "brightness", Brightness);
        PlayerPrefs.SetFloat(Prefix + "contrast", Contrast);
        PlayerPrefs.SetFloat(Prefix + "saturation", Saturation);
        PlayerPrefs.SetFloat(Prefix + "bloom", Bloom);
        PlayerPrefs.SetFloat(Prefix + "vignette", Vignette);
        PlayerPrefs.SetFloat(Prefix + "fog", FogScale);
        PlayerPrefs.SetFloat(Prefix + "renderScale", RenderScale);
        PlayerPrefs.SetFloat(Prefix + "shadowDistance", ShadowDistance);
        PlayerPrefs.SetFloat(Prefix + "fov", Fov);
        PlayerPrefs.SetFloat(Prefix + "sensitivity", LookSensitivity);
        PlayerPrefs.SetInt(Prefix + "invertY", InvertY ? 1 : 0);
        PlayerPrefs.SetFloat(Prefix + "master", MasterVolume);
        PlayerPrefs.SetFloat(Prefix + "sfx", SfxVolume);
        PlayerPrefs.SetFloat(Prefix + "music", MusicVolume);
        PlayerPrefs.Save();
    }

    public void ResetToDefaults() {
        var d = new GameSettings();
        Brightness = d.Brightness; Contrast = d.Contrast; Saturation = d.Saturation;
        Bloom = d.Bloom; Vignette = d.Vignette; FogScale = d.FogScale;
        RenderScale = d.RenderScale; ShadowDistance = d.ShadowDistance;
        Fov = d.Fov; LookSensitivity = d.LookSensitivity; InvertY = d.InvertY;
        MasterVolume = d.MasterVolume; SfxVolume = d.SfxVolume; MusicVolume = d.MusicVolume;
    }

    // ------------------------------------------------------------------ apply

    void BindVolume() {
        if (_volume != null && _grade != null) return;

        _volume = Object.FindFirstObjectByType<Volume>();
        if (_volume == null) return;

        // `.profile` rather than `.sharedProfile`: sharedProfile is the asset on disk, and writing
        // to it from play mode would edit the project's own tuning permanently.
        var profile = _volume.profile;
        if (profile == null) return;

        if (!profile.TryGet(out _grade)) _grade = profile.Add<ColorAdjustments>(true);
        if (!profile.TryGet(out _bloom)) _bloom = profile.Add<Bloom>(true);
        if (!profile.TryGet(out _vignette)) _vignette = profile.Add<Vignette>(true);
    }

    public void Apply() {
        BindVolume();

        if (_grade != null) {
            _grade.postExposure.overrideState = true; _grade.postExposure.value = Brightness;
            _grade.contrast.overrideState = true;     _grade.contrast.value = Contrast;
            _grade.saturation.overrideState = true;   _grade.saturation.value = Saturation;
        }
        if (_bloom != null) {
            _bloom.intensity.overrideState = true; _bloom.intensity.value = Bloom;
        }
        if (_vignette != null) {
            _vignette.intensity.overrideState = true; _vignette.intensity.value = Vignette;
        }

        RenderSettings.fogDensity = BaseFogDensity * FogScale;

        // URP asset: render scale is the cheapest way to buy frame rate on a weak GPU, and the
        // shadow distance at 0 is a genuine "shadows off" for the same reason.
        var urp = UniversalRenderPipeline.asset;
        if (urp != null) {
            urp.renderScale = Mathf.Clamp(RenderScale, 0.4f, 1.5f);
            urp.shadowDistance = Mathf.Max(0f, ShadowDistance);
        }

        if (Weapons != null) Weapons.BaseFov = Fov;
        else if (Cam != null) Cam.fieldOfView = Fov;

        if (Player != null) {
            Player.Sensitivity = 0.002f * Mathf.Max(0.05f, LookSensitivity);
            Player.InvertY = InvertY;
        }

        AudioListener.volume = Mathf.Clamp01(MasterVolume);
        if (Audio != null) Audio.ApplyVolumes(SfxVolume, MusicVolume);
    }

    // ------------------------------------------------------------------ schema for the UI

    public enum Kind { Slider, Toggle }

    /// <summary>
    /// One row in the settings screen. Keeping the schema next to the model means the UI is
    /// generated from it - adding a setting is one entry here, not a new block of layout code.
    /// </summary>
    public class Row {
        public string Group, Label, Tip;
        public Kind Kind = Kind.Slider;
        public float Min, Max;
        public System.Func<GameSettings, float> Get;
        public System.Action<GameSettings, float> Set;
        public System.Func<float, string> Format = v => v.ToString("0.00");
    }

    static string Pct(float v) => Mathf.RoundToInt(v * 100f) + "%";
    static string Num(float v) => v.ToString("0.0");
    static string OnOff(float v) => v > 0.5f ? "ON" : "OFF";

    public static readonly Row[] Schema = {
        new Row { Group = "DISPLAY", Label = "BRIGHTNESS", Min = -1.5f, Max = 1.5f, Format = Num,
                  Tip = "Post-exposure in EV",
                  Get = s => s.Brightness, Set = (s, v) => s.Brightness = v },
        new Row { Group = "DISPLAY", Label = "CONTRAST", Min = -50f, Max = 60f, Format = Num,
                  Get = s => s.Contrast, Set = (s, v) => s.Contrast = v },
        new Row { Group = "DISPLAY", Label = "SATURATION", Min = -60f, Max = 60f, Format = Num,
                  Get = s => s.Saturation, Set = (s, v) => s.Saturation = v },
        new Row { Group = "DISPLAY", Label = "BLOOM", Min = 0f, Max = 2.5f, Format = Num,
                  Get = s => s.Bloom, Set = (s, v) => s.Bloom = v },
        new Row { Group = "DISPLAY", Label = "VIGNETTE", Min = 0f, Max = 0.6f, Format = Num,
                  Get = s => s.Vignette, Set = (s, v) => s.Vignette = v },
        new Row { Group = "DISPLAY", Label = "FOG", Min = 0f, Max = 2f, Format = Pct,
                  Get = s => s.FogScale, Set = (s, v) => s.FogScale = v },

        new Row { Group = "PERFORMANCE", Label = "RENDER SCALE", Min = 0.5f, Max = 1.2f, Format = Pct,
                  Tip = "Lower renders fewer pixels: the cheapest frame rate",
                  Get = s => s.RenderScale, Set = (s, v) => s.RenderScale = v },
        new Row { Group = "PERFORMANCE", Label = "SHADOW DISTANCE", Min = 0f, Max = 160f, Format = v => Mathf.RoundToInt(v) + " m",
                  Tip = "0 turns shadows off",
                  Get = s => s.ShadowDistance, Set = (s, v) => s.ShadowDistance = v },

        new Row { Group = "CONTROLS", Label = "FIELD OF VIEW", Min = 60f, Max = 110f, Format = v => Mathf.RoundToInt(v).ToString(),
                  Get = s => s.Fov, Set = (s, v) => s.Fov = v },
        new Row { Group = "CONTROLS", Label = "LOOK SENSITIVITY", Min = 0.2f, Max = 3f, Format = Num,
                  Get = s => s.LookSensitivity, Set = (s, v) => s.LookSensitivity = v },
        new Row { Group = "CONTROLS", Label = "INVERT Y", Kind = Kind.Toggle, Min = 0f, Max = 1f, Format = OnOff,
                  Get = s => s.InvertY ? 1f : 0f, Set = (s, v) => s.InvertY = v > 0.5f },

        new Row { Group = "AUDIO", Label = "MASTER", Min = 0f, Max = 1f, Format = Pct,
                  Get = s => s.MasterVolume, Set = (s, v) => s.MasterVolume = v },
        new Row { Group = "AUDIO", Label = "EFFECTS", Min = 0f, Max = 1f, Format = Pct,
                  Get = s => s.SfxVolume, Set = (s, v) => s.SfxVolume = v },
        new Row { Group = "AUDIO", Label = "MUSIC", Min = 0f, Max = 1f, Format = Pct,
                  Get = s => s.MusicVolume, Set = (s, v) => s.MusicVolume = v },
    };
}

}
