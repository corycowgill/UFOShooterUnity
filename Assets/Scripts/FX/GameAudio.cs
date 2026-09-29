using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// Sound effects are synthesised into AudioClips at startup, exactly as v2 synthesised them with
/// WebAudio. Nothing here is a sample: the whole SFX bank is a few hundred KB of generated PCM,
/// which keeps the WebGL download to the two music tracks.
///
/// Music is the pair of MP3s that have shipped unchanged since v1.
/// </summary>
public class GameAudio : MonoBehaviour {

    const int Rate = 44100;

    AudioSource _music;
    AudioSource[] _voices;
    int _voice;

    readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
    AudioClip[] _tracks;
    int _track;

    public float SfxVolume = 0.6f;
    public float MusicVolume = 0.35f;

    void Awake() {
        _music = gameObject.AddComponent<AudioSource>();
        _music.loop = false;
        _music.playOnAwake = false;
        _music.volume = MusicVolume;

        // A small round-robin voice pool: combat fires a lot of one-shots per second and
        // PlayOneShot on a single source clips its own tail.
        _voices = new AudioSource[12];
        for (int i = 0; i < _voices.Length; i++) {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            _voices[i] = s;
        }

        BuildBank();
        _tracks = Resources.LoadAll<AudioClip>("Audio");
    }

    void Update() {
        if (_tracks != null && _tracks.Length > 0 && !_music.isPlaying) {
            _music.clip = _tracks[_track % _tracks.Length];
            _music.volume = MusicVolume;
            _music.Play();
            _track++;
        }
    }

    void Play(string key, float volume = 1f, float pitch = 1f) {
        if (!_clips.TryGetValue(key, out var clip) || clip == null) return;
        var s = _voices[_voice];
        _voice = (_voice + 1) % _voices.Length;
        s.pitch = pitch;
        s.PlayOneShot(clip, volume * SfxVolume);
    }

    // ------------------------------------------------------------------ the bank

    void BuildBank() {
        // name              len    build
        _clips["rifle"]      = Synth("rifle", 0.12f, (t, n) => {
            float env = Mathf.Exp(-t * 38f);
            return (Noise(n) * 0.55f + Saw(t, 180f) * 0.45f) * env;
        });
        _clips["plasma"]     = Synth("plasma", 0.22f, (t, n) => {
            float env = Mathf.Exp(-t * 16f);
            float f = Mathf.Lerp(900f, 220f, t / 0.22f);
            return (Sine(t, f) * 0.6f + Noise(n) * 0.2f) * env;
        });
        _clips["sword"]      = Synth("sword", 0.35f, (t, n) => {
            float env = Mathf.Exp(-t * 9f);
            float f = Mathf.Lerp(1400f, 300f, t / 0.35f);
            return (Sine(t, f) * 0.35f + Sine(t, f * 1.5f) * 0.25f + Noise(n) * 0.3f) * env;
        });
        _clips["rocket"]     = Synth("rocket", 0.5f, (t, n) => {
            float env = Mathf.Exp(-t * 6f);
            return (Noise(n) * 0.7f + Sine(t, Mathf.Lerp(120f, 40f, t / 0.5f)) * 0.5f) * env;
        });
        _clips["explosion"]  = Synth("explosion", 0.9f, (t, n) => {
            float env = Mathf.Exp(-t * 4.2f);
            return (Noise(n) * 0.8f + Sine(t, Mathf.Lerp(90f, 28f, t / 0.9f)) * 0.7f) * env;
        });
        _clips["alienShoot"] = Synth("alienShoot", 0.18f, (t, n) => {
            float env = Mathf.Exp(-t * 22f);
            float f = Mathf.Lerp(1500f, 500f, t / 0.18f);
            return (Square(t, f) * 0.4f + Noise(n) * 0.15f) * env;
        });
        _clips["alienHit"]   = Synth("alienHit", 0.1f, (t, n) => Mathf.Exp(-t * 40f) * (Noise(n) * 0.5f + Sine(t, 420f) * 0.4f));
        _clips["critHit"]    = Synth("critHit", 0.14f, (t, n) => Mathf.Exp(-t * 28f) * (Sine(t, 1400f) * 0.5f + Sine(t, 2100f) * 0.3f));
        _clips["alienDeath"] = Synth("alienDeath", 0.55f, (t, n) => {
            float env = Mathf.Exp(-t * 6f);
            float f = Mathf.Lerp(600f, 90f, t / 0.55f);
            return (Saw(t, f) * 0.45f + Noise(n) * 0.3f) * env;
        });
        _clips["shieldHit"]  = Synth("shieldHit", 0.16f, (t, n) => {
            float env = Mathf.Exp(-t * 20f);
            return (Sine(t, 2200f) * 0.35f + Sine(t, 3100f) * 0.25f + Noise(n) * 0.1f) * env;
        });
        _clips["playerHit"]  = Synth("playerHit", 0.3f, (t, n) => Mathf.Exp(-t * 11f) * (Noise(n) * 0.5f + Sine(t, 110f) * 0.6f));
        _clips["pickup"]     = Synth("pickup", 0.25f, (t, n) => {
            float env = Mathf.Exp(-t * 10f);
            float f = t < 0.08f ? 880f : (t < 0.16f ? 1174f : 1568f);
            return Sine(t, f) * 0.4f * env;
        });
        _clips["reload"]     = Synth("reload", 0.3f, (t, n) => {
            float a = t < 0.05f ? 1f : (t > 0.18f && t < 0.24f ? 1f : 0f);
            return Noise(n) * 0.45f * a * Mathf.Exp(-(t % 0.18f) * 30f);
        });
        _clips["switch"]     = Synth("switch", 0.12f, (t, n) => Mathf.Exp(-t * 30f) * Noise(n) * 0.35f);
        _clips["footstep"]   = Synth("footstep", 0.1f, (t, n) => Mathf.Exp(-t * 42f) * Noise(n) * 0.3f);
        _clips["dash"]       = Synth("dash", 0.28f, (t, n) => {
            float env = Mathf.Exp(-t * 12f);
            return (Noise(n) * 0.5f + Sine(t, Mathf.Lerp(260f, 900f, t / 0.28f)) * 0.3f) * env;
        });
        _clips["grenade"]    = Synth("grenade", 0.16f, (t, n) => Mathf.Exp(-t * 24f) * (Sine(t, 520f) * 0.4f + Noise(n) * 0.2f));
        _clips["multiKill"]  = Synth("multiKill", 0.45f, (t, n) => {
            float env = Mathf.Exp(-t * 6f);
            float f = t < 0.12f ? 523f : (t < 0.24f ? 659f : 784f);
            return (Sine(t, f) * 0.35f + Sine(t, f * 2f) * 0.18f) * env;
        });
        _clips["waveStart"]  = Synth("waveStart", 0.8f, (t, n) => {
            float env = Mathf.Exp(-t * 3.2f);
            return (Sine(t, 98f) * 0.5f + Sine(t, 147f) * 0.3f + Noise(n) * 0.08f) * env;
        });
    }

    delegate float Voice(float t, int sampleIndex);

    static AudioClip Synth(string name, float seconds, Voice fn) {
        int n = Mathf.CeilToInt(seconds * Rate);
        var data = new float[n];
        for (int i = 0; i < n; i++) {
            float t = i / (float)Rate;
            data[i] = Mathf.Clamp(fn(t, i), -1f, 1f) * 0.85f;
        }
        var clip = AudioClip.Create(name, n, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    static float Sine(float t, float f) => Mathf.Sin(t * f * 2f * Mathf.PI);
    static float Square(float t, float f) => Mathf.Sign(Mathf.Sin(t * f * 2f * Mathf.PI));
    static float Saw(float t, float f) { float p = t * f; return 2f * (p - Mathf.Floor(p + 0.5f)); }

    // Deterministic hash noise - cheaper than Random and reproducible across runs.
    static float Noise(int i) {
        uint x = (uint)i * 1664525u + 1013904223u;
        x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
        return (x / (float)uint.MaxValue) * 2f - 1f;
    }

    // ------------------------------------------------------------------ API
    public void LaserRifle()   => Play("rifle", 0.35f, Random.Range(0.95f, 1.06f));
    public void PlasmaShot()   => Play("plasma", 0.4f, Random.Range(0.95f, 1.05f));
    public void LaserSword()   => Play("sword", 0.5f);
    public void RocketLaunch() => Play("rocket", 0.55f);
    public void Explosion()    => Play("explosion", 0.6f, Random.Range(0.9f, 1.1f));
    public void AlienShoot()   => Play("alienShoot", 0.22f, Random.Range(0.9f, 1.12f));
    public void AlienHit()     => Play("alienHit", 0.3f, Random.Range(0.95f, 1.1f));
    public void CritHit()      => Play("critHit", 0.45f);
    public void AlienDeath()   => Play("alienDeath", 0.4f, Random.Range(0.9f, 1.1f));
    public void ShieldHit()    => Play("shieldHit", 0.3f, Random.Range(0.95f, 1.1f));
    public void PlayerHit()    => Play("playerHit", 0.55f);
    public void Pickup()       => Play("pickup", 0.45f);
    public void Reload()       => Play("reload", 0.4f);
    public void WeaponSwitch() => Play("switch", 0.35f);
    public void Footstep(bool sprint) => Play("footstep", sprint ? 0.22f : 0.15f, Random.Range(0.9f, 1.15f));
    public void Dash()         => Play("dash", 0.4f);
    public void GrenadeThrow() => Play("grenade", 0.35f);
    public void MultiKill()    => Play("multiKill", 0.5f);
    public void WaveStart()    => Play("waveStart", 0.6f);
}

}
