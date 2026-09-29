using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// The shared alien plasma bolt pool. One set of 96 for every enemy on the field, exactly as in
/// v2 - allocating a mesh per bolt is what made the original hitch during a Siege Line wave.
///
/// Bolts test against the player as a capsule from feet to head, against level colliders as
/// points, and fuel rods (splash > 0) detonate on proximity instead of contact.
/// </summary>
public class EnemyProjectiles : MonoBehaviour {

    public struct Hit {
        public float Damage;
        public Vector3 Position;
        public bool Splash;
    }

    class Bolt {
        public Transform T;
        public Renderer R;
        public Material M;
        public Vector3 Vel;
        public float Life, Damage, Splash, Gravity;
        public Color Color;
    }

    const int PoolSize = 96;

    readonly List<Bolt> _active = new List<Bolt>(PoolSize);
    readonly Stack<Bolt> _free = new Stack<Bolt>(PoolSize);
    readonly List<Hit> _hits = new List<Hit>(8);

    Arena _arena;
    Fx _fx;
    Transform _root;

    public System.Action<Vector3, bool> OnGroundHit;   // (position, wasSplash) - for scorch decals

    public void Init(Arena arena, Fx fx) {
        _arena = arena; _fx = fx;
        if (_root != null) return;

        _root = new GameObject("BoltPool").transform;
        _root.SetParent(transform, false);
        for (int i = 0; i < PoolSize; i++) _free.Push(NewBolt());
    }

    Bolt NewBolt() {
        var go = Prim.Create(PrimKind.Capsule, "BoltMesh", _root);
        // Unity capsules stand along Y; the bolt travels along its local +Z.
        go.transform.localScale = new Vector3(0.18f, 0.34f, 0.18f);
        go.SetActive(false);

        var holder = new GameObject("Bolt").transform;
        holder.SetParent(_root, false);
        go.transform.SetParent(holder, false);
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        holder.gameObject.SetActive(false);

        var r = go.GetComponent<Renderer>();
        var m = Fx.AdditiveTinted(Color.cyan);
        r.material = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        return new Bolt { T = holder, R = r, M = m };
    }

    public void Fire(Vector3 from, Vector3 dir, float speed, Color color, float damage,
                     float splash = 0f, float gravity = 0f, float scale = 1f) {
        if (_free.Count == 0) return;
        var b = _free.Pop();
        b.T.position = from;
        b.T.rotation = Quaternion.LookRotation(dir, Vector3.up);
        b.T.localScale = Vector3.one * scale;
        b.Vel = dir * speed;
        b.Life = 4f;
        b.Damage = damage;
        b.Splash = splash;
        b.Gravity = gravity;
        b.Color = color * 2.2f;
        b.M.color = b.Color;
        if (b.M.HasProperty("_BaseColor")) b.M.SetColor("_BaseColor", b.Color);
        b.T.gameObject.SetActive(true);
        _active.Add(b);
    }

    /// <summary>Advance every bolt. Returns the hits that reached the player this frame.</summary>
    public List<Hit> Tick(float dt, Vector3 playerPos) {
        _hits.Clear();

        for (int i = _active.Count - 1; i >= 0; i--) {
            var b = _active[i];
            b.Life -= dt;
            if (b.Gravity > 0f) b.Vel.y -= b.Gravity * dt;
            b.T.position += b.Vel * dt;
            if (b.Gravity > 0f && b.Vel.sqrMagnitude > 1e-4f)
                b.T.rotation = Quaternion.LookRotation(b.Vel.normalized, Vector3.up);

            var p = b.T.position;
            bool dead = b.Life <= 0f || p.y < 0f;

            // Player capsule, feet to head. playerPos is the eye, so feet are 0.9 below it.
            float dx = p.x - playerPos.x, dz = p.z - playerPos.z;
            float dy = p.y - (playerPos.y - 0.9f);
            if (!dead && dx * dx + dz * dz < 0.45f && dy > -0.3f && dy < 2.0f) {
                _hits.Add(new Hit { Damage = b.Damage, Position = p });
                dead = true;
            }
            // Fuel rod: proximity detonation, damage falls off with distance.
            if (!dead && b.Splash > 0f && dx * dx + dz * dz < b.Splash * b.Splash && p.y < 1.2f) {
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                _hits.Add(new Hit { Damage = b.Damage * (1f - d / b.Splash), Position = p, Splash = true });
                dead = true;
            }
            if (!dead && _arena != null && _arena.PointInSolid(p)) dead = true;

            if (dead) {
                _fx?.Impact(p, b.Color, b.Splash > 0f ? 2.5f : 0.6f);
                if (b.Splash > 0f) { _fx?.Explosion(p, 1.0f); }
                if (p.y < 0.6f) OnGroundHit?.Invoke(p, b.Splash > 0f);
                b.T.gameObject.SetActive(false);
                _active.RemoveAt(i);
                _free.Push(b);
            }
        }
        return _hits;
    }

    public void Clear() {
        for (int i = 0; i < _active.Count; i++) {
            _active[i].T.gameObject.SetActive(false);
            _free.Push(_active[i]);
        }
        _active.Clear();
    }
}

}
