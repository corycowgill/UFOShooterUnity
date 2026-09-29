using UnityEngine;

namespace UFO {

/// <summary>
/// Thin wrapper over Unity's legacy Animation component so the AI can drive clips by name with
/// crossfades and playback-rate scaling, exactly like Three.js AnimationMixer did in v2.
///
/// Legacy (rather than Mecanim) is a deliberate choice: the enemy rigs ship seven named clips
/// and the AI picks between them imperatively from measured movement speed. An AnimatorController
/// would add a state graph we would only ever bypass. glTFast imports these as legacy clips when
/// the importer's Animation Method is set to Legacy (see Editor/GlbImportSetup.cs).
///
/// Every call degrades to a no-op if the model has no animation, so an un-rigged prop or a
/// placeholder capsule still works.
/// </summary>
public class EnemyAnimator {

    readonly Animation _anim;
    string _current;
    string _returnTo;
    float _oneShotLeft;

    public bool Valid => _anim != null;
    /// <summary>The clip currently driving the rig, for diagnostics.</summary>
    public string CurrentClip => _current;
    /// <summary>Names of every clip the import produced.</summary>
    public string[] ClipNames {
        get {
            if (_anim == null) return new string[0];
            var names = new System.Collections.Generic.List<string>();
            foreach (AnimationState st in _anim) names.Add(st.name);
            return names.ToArray();
        }
    }
    public bool OneShotPlaying => _oneShotLeft > 0f;

    public EnemyAnimator(GameObject model) {
        if (model == null) return;
        _anim = model.GetComponentInChildren<Animation>();
        if (_anim == null) return;

        _anim.playAutomatically = false;
        foreach (AnimationState st in _anim) {
            bool oneShot = st.name == Clip.Attack || st.name == Clip.Shoot
                        || st.name == Clip.Hit || st.name == Clip.Death;
            st.wrapMode = oneShot ? WrapMode.ClampForever : WrapMode.Loop;
        }
    }

    public bool Has(string clip) => _anim != null && _anim[clip] != null;

    public void Play(string clip, float fade = 0.15f, float timeScale = 1f) {
        if (_anim == null || _anim[clip] == null) return;
        _anim[clip].speed = timeScale;
        if (_current == clip) return;
        _anim.CrossFade(clip, fade);
        _current = clip;
    }

    /// <summary>Play once, then fall back to `then`. Returns the clip length (0 if missing).</summary>
    public float PlayOnce(string clip, string then = Clip.Idle) {
        if (_anim == null || _anim[clip] == null) return 0f;
        _anim[clip].speed = 1f;
        _anim[clip].time = 0f;
        _anim.CrossFade(clip, 0.08f);
        _current = clip;
        _returnTo = then;
        _oneShotLeft = _anim[clip].length;
        return _oneShotLeft;
    }

    /// <summary>Call every frame. Returns true while a one-shot still owns the rig.</summary>
    public bool Tick(float dt) {
        if (_oneShotLeft <= 0f) return false;
        _oneShotLeft -= dt;
        if (_oneShotLeft <= 0f && _returnTo != null) {
            var to = _returnTo;
            _returnTo = null;
            Play(to, 0.12f);
        }
        return _oneShotLeft > 0f;
    }

    public void Stop() { if (_anim != null) _anim.Stop(); }
}

}
