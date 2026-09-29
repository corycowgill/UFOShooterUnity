using System.Collections.Generic;
using UnityEngine;

namespace UFO {

public enum DecalKind { Bullet, Scorch, Blood }

/// <summary>
/// Pooled surface decals: bullet holes on walls, plasma scorches on the road, alien blood where
/// something died.
///
/// v2 puts these on the real building meshes and the ground, and their absence was one of the
/// larger remaining gaps in v3 - a firefight left no trace at all, so the city never accumulated
/// evidence that anything had happened in it.
///
/// A fixed pool of quads, oldest recycled. No projection, no render feature: a quad offset a
/// centimetre along the surface normal is enough for flat walls and roads, which is all this city
/// has, and it costs one draw call per material instead of a decal pass.
/// </summary>
public class Decals : MonoBehaviour {

    class Decal {
        public Transform T;
        public Renderer R;
        public Material M;
        public float Life, MaxLife;
        public Color Start;
    }

    // Blood outlives the rest: a wave should leave the plaza visibly fought-over.
    const int PoolPerKind = 64;
    static readonly float[] Lifetimes = { 26f, 30f, 45f };

    readonly Dictionary<DecalKind, List<Decal>> _live = new Dictionary<DecalKind, List<Decal>>();
    readonly Dictionary<DecalKind, Queue<Decal>> _free = new Dictionary<DecalKind, Queue<Decal>>();
    Transform _root;

    void Awake() {
        _root = new GameObject("DecalPool").transform;
        _root.SetParent(transform, false);

        foreach (DecalKind kind in System.Enum.GetValues(typeof(DecalKind))) {
            _live[kind] = new List<Decal>(PoolPerKind);
            _free[kind] = new Queue<Decal>(PoolPerKind);
            var tex = Resources.Load<Texture2D>("Decals/" + TextureName(kind));
            for (int i = 0; i < PoolPerKind; i++) _free[kind].Enqueue(NewDecal(kind, tex));
        }
    }

    static string TextureName(DecalKind kind) => kind switch {
        DecalKind.Bullet => "bullet",
        DecalKind.Scorch => "scorch",
        _ => "blood",
    };

    static Color TintFor(DecalKind kind) => kind switch {
        DecalKind.Bullet => new Color(0.06f, 0.06f, 0.07f, 0.95f),
        DecalKind.Scorch => new Color(0.10f, 0.08f, 0.07f, 0.9f),
        // v2's aliens bleed violet; it has to read against wet asphalt at night.
        _ => new Color(0.62f, 0.25f, 0.95f, 0.85f),
    };

    Decal NewDecal(DecalKind kind, Texture2D tex) {
        var go = Prim.Create(PrimKind.Quad, kind + "Decal", _root);
        go.SetActive(false);

        var mat = Prim.Unlit();
        Fx.MakeTransparent(mat);
        if (tex != null) {
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        }
        // Sit just in front of the surface in the transparent queue so it never z-fights the wall.
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;

        var r = go.GetComponent<Renderer>();
        r.material = mat;
        return new Decal { T = go.transform, R = r, M = mat };
    }

    /// <summary>Place a decal on a surface. `normal` is the surface normal at the hit point.</summary>
    public void Place(DecalKind kind, Vector3 point, Vector3 normal, float size) {
        if (!_free.TryGetValue(kind, out var free)) return;

        Decal d;
        if (free.Count > 0) d = free.Dequeue();
        else {
            // Recycle the oldest rather than growing: a long wave would otherwise leak quads.
            var live = _live[kind];
            if (live.Count == 0) return;
            d = live[0];
            live.RemoveAt(0);
        }

        if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
        normal.Normalize();

        d.T.position = point + normal * 0.02f;
        // A quad faces -Z, so look along the inverted normal; the random roll stops repeated hits
        // on one wall from reading as a pattern.
        d.T.rotation = Quaternion.LookRotation(-normal, Vector3.up)
                     * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        d.T.localScale = Vector3.one * size * Random.Range(0.8f, 1.25f);

        d.Start = TintFor(kind);
        SetColor(d.M, d.Start);
        d.MaxLife = d.Life = Lifetimes[(int)kind];
        d.T.gameObject.SetActive(true);
        _live[kind].Add(d);
    }

    /// <summary>A splatter of blood on the ground under something that just died.</summary>
    public void Splatter(Vector3 groundPoint, float size, int count = 3) {
        for (int i = 0; i < count; i++) {
            var p = groundPoint + new Vector3(Random.Range(-size, size) * 0.4f, 0.01f,
                                              Random.Range(-size, size) * 0.4f);
            Place(DecalKind.Blood, p, Vector3.up, size * Random.Range(0.5f, 1f));
        }
    }

    static void SetColor(Material m, Color c) {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
    }

    void Update() {
        float dt = Time.deltaTime;
        foreach (var kv in _live) {
            var live = kv.Value;
            for (int i = live.Count - 1; i >= 0; i--) {
                var d = live[i];
                d.Life -= dt;
                if (d.Life <= 0f) {
                    d.T.gameObject.SetActive(false);
                    live.RemoveAt(i);
                    _free[kv.Key].Enqueue(d);
                    continue;
                }
                // Hold full opacity, then fade over the last quarter of the life.
                float k = d.Life / d.MaxLife;
                var c = d.Start;
                c.a = d.Start.a * Mathf.Clamp01(k * 4f);
                SetColor(d.M, c);
            }
        }
    }

    public void Clear() {
        foreach (var kv in _live) {
            foreach (var d in kv.Value) {
                d.T.gameObject.SetActive(false);
                _free[kv.Key].Enqueue(d);
            }
            kv.Value.Clear();
        }
    }
}

}
