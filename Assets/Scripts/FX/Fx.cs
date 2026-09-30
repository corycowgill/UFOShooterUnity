using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// All combat feedback: impacts, sparks, explosions, muzzle flashes, tracers, shield bursts,
/// ground decals and camera shake.
///
/// Everything is pooled and built from primitives at runtime, the same way v2 did it, so there
/// are no VFX assets to import and no per-shot allocation. Materials are URP/Unlit with additive
/// blending, which is cheap enough to spam on WebGL.
/// </summary>
public class Fx : MonoBehaviour {

    public Camera Cam;

    // ------------------------------------------------------------- shared materials
    // Materials come from baked templates, never Shader.Find - see Prim for why.

    public static Material UnlitTinted(Color c) {
        var m = Prim.Unlit();
        if (m == null) return null;
        SetColor(m, c);
        return m;
    }

    public static Material LitMaterial() => Prim.Lit();

    /// <summary>Additive, depth-write off - for shields, bolts, tracers and flashes.</summary>
    public static Material AdditiveTinted(Color c) {
        var m = Prim.Unlit();
        if (m == null) return null;
        m.SetFloat("_Surface", 1f);                 // transparent
        m.SetFloat("_Blend", 1f);                   // additive
        m.SetFloat("_ZWrite", 0f);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        SetColor(m, c);
        return m;
    }

    /// <summary>
    /// Turn an opaque runtime material into an alpha-blended one.
    ///
    /// **Everything built this way must also carry a texture and be created at scene start**, the
    /// way <see cref="Decals"/> does it - that is the only combination observed to survive the
    /// WebGL build. A textureless alpha-blended material built at runtime renders OPAQUE in the
    /// player and correctly in the Editor, and nothing about the material's state says so: surface,
    /// both blend factors, ZWrite, the transparent keyword and the render queue all read back
    /// exactly as they do on a decal that works.
    ///
    /// For anything else, use <see cref="AdditiveTinted"/> or opaque geometry. Smoke and steam are
    /// additive for this reason and no other; so is the levels' smoke column.
    /// </summary>
    public static void MakeTransparent(Material m) {
        if (m == null || m.GetFloat("_Surface") > 0.5f) return;
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_ZWrite", 0f);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
    }

    static void SetColor(Material m, Color c) {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
    }

    // ------------------------------------------------------------- shake
    float _shakeAmount, _shakeTime, _shakeTotal;
    Vector3 _shakeOffset;

    public void Shake(float amount, float duration) {
        if (amount <= _shakeAmount && _shakeTime > 0f) return;
        _shakeAmount = amount; _shakeTime = duration; _shakeTotal = duration;
    }

    public Vector3 ShakeOffset => _shakeOffset;

    // ------------------------------------------------------------- pools
    class Puff {
        public Transform T;
        public Renderer R;
        public Material M;
        public Vector3 Vel;
        public float Life, MaxLife, StartScale, EndScale;
        public Color Start;
        public bool Gravity;
        /// <summary>Fade out when close to the camera. Set on the big, slow, volumetric puffs.</summary>
        public bool SoftNear;
    }

    readonly List<Puff> _live = new List<Puff>(256);
    readonly Stack<Puff> _free = new Stack<Puff>(256);
    Transform _root;

    // Raised for the fires: a dozen wrecks emitting flame, smoke and embers continuously would
    // otherwise recycle the pool out from under impacts and explosions during a wave.
    const int PoolSize = 700;

    void Awake() {
        _root = new GameObject("FxPool").transform;
        _root.SetParent(transform, false);
        for (int i = 0; i < PoolSize; i++) _free.Push(NewPuff());
    }

    /// <summary>
    /// One pooled puff. Additive, always.
    ///
    /// Two attempts were made to give smoke and steam alpha blending so they could darken what is
    /// behind them, and both shipped broken: flipping `_DstBlend` on a live material left it opaque
    /// in the player, and a separate pool of alpha-blended puffs built in Awake did too. Every
    /// burning wreck and street vent then put a stack of solid grey spheres in the street. Additive
    /// is the mode this build honours, so smoke here is smoke *lit* from below rather than smoke
    /// that blocks light - which is what smoke over a burning city looks like anyway.
    /// </summary>
    Puff NewPuff() {
        var go = Prim.Create(PrimKind.Sphere, "Puff", _root);
        go.SetActive(false);
        var r = go.GetComponent<Renderer>();
        var m = AdditiveTinted(Color.white);
        r.material = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return new Puff { T = go.transform, R = r, M = m };
    }

    Puff Spawn(Vector3 pos, Color color, float scale, float endScale, float life, Vector3 vel,
               bool gravity, bool softNear = false) {
        if (_free.Count == 0) {
            // Recycle the oldest rather than growing without bound.
            if (_live.Count == 0) return null;
            var oldest = _live[0];
            _live.RemoveAt(0);
            _free.Push(oldest);
        }
        var p = _free.Pop();
        p.T.position = pos;
        p.T.localScale = Vector3.one * scale;
        p.StartScale = scale; p.EndScale = endScale;
        p.Life = p.MaxLife = life;
        p.Vel = vel; p.Gravity = gravity;
        p.SoftNear = softNear;
        p.Start = color;
        SetColor(p.M, color);
        p.T.gameObject.SetActive(true);
        _live.Add(p);
        return p;
    }

    void Update() {
        float dt = Time.deltaTime;

        for (int i = _live.Count - 1; i >= 0; i--) {
            var p = _live[i];
            p.Life -= dt;
            if (p.Life <= 0f) {
                p.T.gameObject.SetActive(false);
                _live.RemoveAt(i);
                _free.Push(p);
                continue;
            }
            if (p.Gravity) p.Vel.y -= 22f * dt;
            p.T.position += p.Vel * dt;
            float k = 1f - p.Life / p.MaxLife;
            p.T.localScale = Vector3.one * Mathf.Lerp(p.StartScale, p.EndScale, k);
            var c = p.Start;
            c.a = p.Start.a * (1f - k);

            // Near fade, for the big slow puffs only.
            //
            // These are spheres, and there is no soft-particle depth fade in this pipeline, so a
            // two-metre smoke or steam puff a metre from the camera stops reading as vapour and
            // reads as a glass ball with a hard silhouette - which is exactly what a street vent
            // looked like when you walked past it. Impacts, sparks and muzzle flashes are exempt:
            // a muzzle flash is spawned a hand's width from the camera on purpose.
            if (p.SoftNear && Cam != null) {
                float d = Vector3.Distance(Cam.transform.position, p.T.position);
                c.a *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.0f, 7.0f, d));
            }
            SetColor(p.M, c);
        }

        if (_shakeTime > 0f) {
            _shakeTime -= dt;
            float k = Mathf.Max(0f, _shakeTime / _shakeTotal);
            float a = _shakeAmount * k;
            _shakeOffset = new Vector3(Random.Range(-a, a), Random.Range(-a, a), 0f);
            if (_shakeTime <= 0f) { _shakeOffset = Vector3.zero; _shakeAmount = 0f; }
        }
    }

    // ------------------------------------------------------------- effects

    public void Impact(Vector3 pos, Color color, float size = 0.5f) {
        Spawn(pos, color, 0.12f * size, 0.5f * size, 0.18f, Vector3.zero, false);
        for (int i = 0; i < 4; i++) {
            var v = Random.onUnitSphere * (2.5f * size);
            Spawn(pos, color, 0.05f * size, 0.01f, 0.25f, v, true);
        }
    }

    public void Sparks(Vector3 pos, Color color, int count) {
        for (int i = 0; i < count; i++) {
            var v = Random.onUnitSphere * Random.Range(2f, 6f);
            Spawn(pos, color, 0.045f, 0.005f, Random.Range(0.15f, 0.35f), v, true);
        }
    }

    /// <summary>A flame tongue: bright, short-lived, rising and shrinking as it burns out.</summary>
    public void Flame(Vector3 pos, float scale = 1f) {
        // Warm core through to a redder tip, so a cluster reads as fire rather than orange dots.
        float k = Random.value;
        var c = Color.Lerp(new Color(1f, 0.78f, 0.28f), new Color(1f, 0.30f, 0.06f), k);
        var vel = new Vector3(Random.Range(-0.45f, 0.45f), Random.Range(1.7f, 3.0f), Random.Range(-0.45f, 0.45f)) * scale;
        // Small and short-lived: a few big spheres read as orange balls, a lot of small ones
        // that shrink quickly read as flame.
        Spawn(pos, c, Random.Range(0.14f, 0.26f) * scale, 0.02f * scale, Random.Range(0.28f, 0.48f), vel, false);
    }

    /// <summary>Smoke: slower and larger, rising off a fire and catching its light.</summary>
    public void Smoke(Vector3 pos, float scale = 1f) {
        // Additive, so this is the fire's light *in* the smoke rather than the smoke itself - warm
        // at the base, falling away to a cold grey as it rises and cools.
        var c = new Color(0.22f, 0.17f, 0.14f, 0.20f);
        var vel = new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(1.0f, 1.7f), Random.Range(-0.3f, 0.3f)) * scale;
        Spawn(pos, c, 0.5f * scale, 2.6f * scale, Random.Range(1.6f, 2.6f), vel, false, softNear: true);
    }

    /// <summary>
    /// Steam off a street vent: pale, slow, and much longer-lived than smoke, because a plume that
    /// dissipates in a second reads as a puff of dust instead.
    /// </summary>
    public void Steam(Vector3 pos, float scale = 1f) {
        // Additive suits steam anyway: a plume off a street vent at night is lit by the lamp above
        // it, and what you see is the light in it rather than the shape of it.
        var c = new Color(0.30f, 0.33f, 0.38f, 0.10f);
        var vel = new Vector3(Random.Range(-0.2f, 0.2f), Random.Range(0.8f, 1.4f), Random.Range(-0.2f, 0.2f)) * scale;
        // Small and numerous, and short-lived. A few big spheres read as balloons; the plume has
        // to be made of pieces smaller than itself, and it has to thin out before it gets tall
        // enough to stand between the player and anything.
        Spawn(pos, c, 0.20f * scale, 0.85f * scale, Random.Range(1.3f, 2.1f), vel, false, softNear: true);
    }

    /// <summary>An ember: small, bright, thrown up and falling back under gravity.</summary>
    public void Ember(Vector3 pos, float scale = 1f) {
        var vel = new Vector3(Random.Range(-1.2f, 1.2f), Random.Range(2.5f, 4.5f), Random.Range(-1.2f, 1.2f)) * scale;
        Spawn(pos, new Color(1f, 0.62f, 0.22f), 0.06f * scale, 0.012f * scale, Random.Range(0.8f, 1.6f), vel, true);
    }

    public void MuzzleFlash(Vector3 pos, Color color) {
        Spawn(pos, color * 2f, 0.28f, 0.02f, 0.06f, Vector3.zero, false);
    }

    public void Explosion(Vector3 pos, float scale) {
        Spawn(pos, new Color(1f, 0.75f, 0.35f), 0.6f * scale, 4.5f * scale, 0.45f, Vector3.zero, false);
        Spawn(pos, new Color(1f, 0.35f, 0.1f), 0.3f * scale, 3f * scale, 0.6f, Vector3.zero, false);
        for (int i = 0; i < 14; i++) {
            var v = Random.onUnitSphere * Random.Range(4f, 14f) * scale;
            v.y = Mathf.Abs(v.y);
            Spawn(pos, new Color(1f, 0.6f, 0.2f), 0.2f * scale, 0.02f, Random.Range(0.3f, 0.7f), v, true);
        }
        Shake(0.12f * scale, 0.35f);
    }

    public void MegaExplosion(Vector3 pos, float scale) {
        Explosion(pos, scale);
        Explosion(pos + Vector3.up * scale, scale * 0.7f);
        Shake(0.6f, 1.2f);
    }

    public void ShieldBurst(Vector3 pos, float height, Color color) {
        var c = pos + Vector3.up * (height * 0.5f);
        Spawn(c, color, height * 0.6f, height * 2.2f, 0.35f, Vector3.zero, false);
        Sparks(c, color, 12);
    }

    public void DeathEffect(Vector3 pos, Color color, float scale) {
        var c = pos + Vector3.up * scale;
        Spawn(c, color, scale * 0.5f, scale * 2.5f, 0.4f, Vector3.zero, false);
        for (int i = 0; i < 10; i++) {
            var v = Random.onUnitSphere * Random.Range(2f, 7f);
            Spawn(c, color, 0.1f * scale, 0.01f, Random.Range(0.3f, 0.6f), v, true);
        }
    }

    public void SpawnBeam(Vector3 pos, Color color) {
        for (int i = 0; i < 10; i++) {
            Spawn(pos + Vector3.up * (i * 1.4f), color, 0.7f, 0.05f, 0.5f + i * 0.02f, Vector3.up * 3f, false);
        }
    }

    // ------------------------------------------------------------- tracers
    class TracerLine { public LineRenderer L; public float Life, MaxLife; }
    readonly List<TracerLine> _tracers = new List<TracerLine>(32);
    readonly Stack<TracerLine> _tracerFree = new Stack<TracerLine>(32);

    public void Tracer(Vector3 from, Vector3 to, Color color, float life = 0.05f, float width = 0.012f) {
        TracerLine t = _tracerFree.Count > 0 ? _tracerFree.Pop() : NewTracer();
        t.L.SetPosition(0, from);
        t.L.SetPosition(1, to);
        t.L.startWidth = t.L.endWidth = width;
        t.L.material.SetColor("_BaseColor", color);
        t.L.material.color = color;
        t.L.gameObject.SetActive(true);
        t.Life = t.MaxLife = life;
        _tracers.Add(t);
    }

    TracerLine NewTracer() {
        var go = new GameObject("Tracer");
        go.transform.SetParent(_root, false);
        var l = go.AddComponent<LineRenderer>();
        l.positionCount = 2;
        l.useWorldSpace = true;
        l.material = AdditiveTinted(Color.white);
        l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        l.receiveShadows = false;
        go.SetActive(false);
        return new TracerLine { L = l };
    }

    void LateUpdate() {
        float dt = Time.deltaTime;
        for (int i = _tracers.Count - 1; i >= 0; i--) {
            var t = _tracers[i];
            t.Life -= dt;
            if (t.Life <= 0f) {
                t.L.gameObject.SetActive(false);
                _tracers.RemoveAt(i);
                _tracerFree.Push(t);
            }
        }
    }
}

}
