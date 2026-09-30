using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// An append-only mesh builder: boxes, tubes, cones, discs and sagging wires accumulated into one
/// mesh, then handed to a single renderer.
///
/// This exists because the props this game wants next are *structural* - a lattice antenna mast, a
/// water tower's cross-braced legs, a catenary spanning a street - and each one is dozens of thin
/// members. Built as GameObjects they would be dozens of draw calls apiece; built here, every water
/// tower on every roof in the level is one mesh sharing one material.
///
/// Winding rule, so faces do not come back inside out: a face is given as an origin plus its two
/// edge vectors <c>u</c> and <c>w</c>, and its normal is <c>Cross(u, w)</c>. Pass them in the wrong
/// order and the quad faces away - which in URP means it simply is not there.
/// </summary>
public class MeshKit {

    readonly List<Vector3> _v = new List<Vector3>(1024);
    readonly List<Vector3> _n = new List<Vector3>(1024);
    readonly List<Vector2> _uv = new List<Vector2>(1024);
    readonly List<int> _t = new List<int>(2048);

    public int VertexCount => _v.Count;
    public bool Empty => _v.Count == 0;

    /// <summary>Metres of world space per UV tile. Keeps texture scale consistent across props.</summary>
    public float UvScale = 1f;

    // ------------------------------------------------------------------ faces

    /// <summary>
    /// One quad. <paramref name="origin"/> is a corner, <paramref name="u"/> and <paramref name="w"/>
    /// are the full edge vectors from it; the outward normal is Cross(u, w).
    /// </summary>
    public void Face(Vector3 origin, Vector3 u, Vector3 w) {
        var n = Vector3.Cross(u, w).normalized;
        int b = _v.Count;
        _v.Add(origin); _v.Add(origin + u); _v.Add(origin + u + w); _v.Add(origin + w);
        for (int i = 0; i < 4; i++) _n.Add(n);
        float su = u.magnitude * UvScale, sw = w.magnitude * UvScale;
        _uv.Add(new Vector2(0, 0)); _uv.Add(new Vector2(su, 0));
        _uv.Add(new Vector2(su, sw)); _uv.Add(new Vector2(0, sw));
        _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
        _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
    }

    /// <summary>
    /// One triangle. Front face is the side from which <c>a, b, c</c> run the way the winding rule
    /// above describes - the normal is Cross(b - a, c - a).
    /// </summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c) {
        var n = Vector3.Cross(b - a, c - a).normalized;
        int i = _v.Count;
        _v.Add(a); _v.Add(b); _v.Add(c);
        _n.Add(n); _n.Add(n); _n.Add(n);
        _uv.Add(new Vector2(0f, 0f)); _uv.Add(new Vector2(1f, 0f)); _uv.Add(new Vector2(0f, 1f));
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
    }

    /// <summary>A box, centred on <paramref name="center"/>, sized and oriented as given.</summary>
    public void Box(Vector3 center, Vector3 size, Quaternion rot) {
        var x = rot * new Vector3(size.x, 0f, 0f);
        var y = rot * new Vector3(0f, size.y, 0f);
        var z = rot * new Vector3(0f, 0f, size.z);
        var c = center - (x + y + z) * 0.5f;              // the -x-y-z corner

        Face(c + y, z, x);                                 // +Y
        Face(c, x, z);                                     // -Y
        Face(c + x, y, z);                                 // +X
        Face(c, z, y);                                     // -X
        Face(c + z, x, y);                                 // +Z
        Face(c, y, x);                                     // -Z
    }

    public void Box(Vector3 center, Vector3 size) => Box(center, size, Quaternion.identity);

    /// <summary>A box from its min/max corners, axis aligned.</summary>
    public void BoxMinMax(Vector3 min, Vector3 max) => Box((min + max) * 0.5f, max - min, Quaternion.identity);

    /// <summary>
    /// A flat, upward-facing rectangle for something painted on the ground.
    ///
    /// Deliberately a single quad rather than a very thin box. A box's underside sits a couple of
    /// millimetres below its top, so two of them laid on the same road overlap within the depth
    /// buffer's ability to separate them and the pair renders as a stipple - which is exactly what
    /// a run of skid marks came back as. One quad has nothing to fight with.
    /// </summary>
    public void GroundQuad(Vector3 center, float width, float length, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var u = rot * new Vector3(0f, 0f, length);
        var w = rot * new Vector3(width, 0f, 0f);
        Face(center - (u + w) * 0.5f, u, w);
    }

    /// <summary>
    /// A flat upward rectangle carrying a full 0..1 UV square, so a texture maps across it exactly
    /// once. <see cref="GroundQuad"/> tiles by world size instead, which is right for asphalt and
    /// wrong for anything that is one image.
    /// </summary>
    public void GroundDecal(Vector3 center, float width, float length, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var u = rot * new Vector3(0f, 0f, length);
        var w = rot * new Vector3(width, 0f, 0f);
        var o = center - (u + w) * 0.5f;
        var n = Vector3.Cross(u, w).normalized;

        int b = _v.Count;
        _v.Add(o); _v.Add(o + u); _v.Add(o + u + w); _v.Add(o + w);
        for (int i = 0; i < 4; i++) _n.Add(n);
        _uv.Add(new Vector2(0, 0)); _uv.Add(new Vector2(0, 1));
        _uv.Add(new Vector2(1, 1)); _uv.Add(new Vector2(1, 0));
        _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
        _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
    }

    /// <summary>
    /// A vertical rectangle facing outward along <paramref name="yaw"/>, centred on
    /// <paramref name="center"/>. Shopfront glass, wall panels, anything hung flat on a facade.
    /// </summary>
    public void GroundQuadUpright(Vector3 center, float width, float height, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var u = rot * new Vector3(width, 0f, 0f);
        var w = new Vector3(0f, height, 0f);
        // Cross(u, w) is the outward normal: Cross(right, up) is forward, which is the facing
        // direction. Swapping the two turns the panel round and it vanishes.
        Face(center - (u + w) * 0.5f, u, w);
    }

    // ------------------------------------------------------------------ round

    /// <summary>
    /// A tube between two points. Used for everything cylindrical - legs, pipes, poles, bracing.
    /// Six sides is plenty for a 60 mm brace and keeps a lattice mast affordable.
    /// </summary>
    public void Tube(Vector3 a, Vector3 b, float radius, int sides = 6, bool caps = false) {
        var axis = b - a;
        float len = axis.magnitude;
        if (len < 1e-4f || radius <= 0f) return;
        axis /= len;

        var up = Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up;
        var t = Vector3.Cross(up, axis).normalized;
        var s = Vector3.Cross(axis, t);

        int baseIdx = _v.Count;
        for (int i = 0; i <= sides; i++) {
            float ang = i / (float)sides * Mathf.PI * 2f;
            var r = (t * Mathf.Cos(ang) + s * Mathf.Sin(ang));
            _v.Add(a + r * radius); _n.Add(r); _uv.Add(new Vector2(i / (float)sides, 0f));
            _v.Add(b + r * radius); _n.Add(r); _uv.Add(new Vector2(i / (float)sides, len * UvScale));
        }
        for (int i = 0; i < sides; i++) {
            int p = baseIdx + i * 2;
            _t.Add(p); _t.Add(p + 3); _t.Add(p + 1);
            _t.Add(p); _t.Add(p + 2); _t.Add(p + 3);
        }
        if (caps) { Disc(b, axis, radius, sides); Disc(a, -axis, radius, sides); }
    }

    /// <summary>A flat disc facing <paramref name="normal"/>. Craters, tank lids, manhole covers.</summary>
    public void Disc(Vector3 center, Vector3 normal, float radius, int sides = 16) {
        normal = normal.normalized;
        var up = Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up;
        var t = Vector3.Cross(up, normal).normalized;
        var s = Vector3.Cross(normal, t);

        int c = _v.Count;
        _v.Add(center); _n.Add(normal); _uv.Add(new Vector2(0.5f, 0.5f));
        for (int i = 0; i <= sides; i++) {
            float ang = i / (float)sides * Mathf.PI * 2f;
            float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
            _v.Add(center + (t * ca + s * sa) * radius);
            _n.Add(normal);
            _uv.Add(new Vector2(0.5f + ca * 0.5f, 0.5f + sa * 0.5f));
        }
        for (int i = 0; i < sides; i++) { _t.Add(c); _t.Add(c + i + 1); _t.Add(c + i + 2); }
    }

    /// <summary>A cone standing on <paramref name="baseCenter"/>. Tank roofs, spoil rims, plumes.</summary>
    public void Cone(Vector3 baseCenter, float radius, float height, int sides = 12) {
        var apex = baseCenter + Vector3.up * height;
        int b = _v.Count;
        for (int i = 0; i <= sides; i++) {
            float ang = i / (float)sides * Mathf.PI * 2f;
            float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
            var rim = baseCenter + new Vector3(ca, 0f, sa) * radius;
            // The slope normal, not the radial one, or the cone shades like a cylinder.
            var nrm = new Vector3(ca * height, radius, sa * height).normalized;
            _v.Add(rim); _n.Add(nrm); _uv.Add(new Vector2(i / (float)sides, 0f));
            _v.Add(apex); _n.Add(nrm); _uv.Add(new Vector2(i / (float)sides, 1f));
        }
        for (int i = 0; i < sides; i++) {
            int p = b + i * 2;                             // rim_i, apex, rim_i+1
            _t.Add(p); _t.Add(p + 1); _t.Add(p + 2);
        }
    }

    // ------------------------------------------------------------------ composites

    /// <summary>
    /// A hanging wire between two points, sagging by <paramref name="sag"/> metres at midspan.
    /// Approximated with a parabola, which is indistinguishable from a real catenary at these spans
    /// and needs no numerics.
    /// </summary>
    public void Wire(Vector3 a, Vector3 b, float sag, float radius, int segments = 8) {
        var prev = a;
        for (int i = 1; i <= segments; i++) {
            float k = i / (float)segments;
            var p = Vector3.Lerp(a, b, k);
            p.y -= sag * 4f * k * (1f - k);               // 0 at both ends, `sag` at midspan
            Tube(prev, p, radius, 4);
            prev = p;
        }
    }

    /// <summary>
    /// A four-legged braced lattice - a mast, a water tower's frame, a scaffold bay. Legs taper
    /// from <paramref name="baseHalf"/> to <paramref name="topHalf"/> and every level is X-braced.
    /// </summary>
    public void Lattice(Vector3 basePos, float height, float baseHalf, float topHalf,
                        int levels, float memberRadius) {
        var corner = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };

        Vector3 At(int c, float k) {
            float half = Mathf.Lerp(baseHalf, topHalf, k);
            return basePos + new Vector3(corner[c].x * half, height * k, corner[c].y * half);
        }

        for (int c = 0; c < 4; c++) Tube(At(c, 0f), At(c, 1f), memberRadius, 5);

        for (int l = 0; l <= levels; l++) {
            float k = l / (float)levels;
            for (int c = 0; c < 4; c++) Tube(At(c, k), At((c + 1) % 4, k), memberRadius * 0.7f, 4);
            if (l == levels) break;
            float k2 = (l + 1) / (float)levels;
            for (int c = 0; c < 4; c++) Tube(At(c, k), At((c + 1) % 4, k2), memberRadius * 0.55f, 4);
        }
    }

    // ------------------------------------------------------------------ output

    public Mesh Build(string name) {
        var mesh = new Mesh { name = name };
        mesh.indexFormat = _v.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(_v);
        mesh.SetNormals(_n);
        mesh.SetUVs(0, _uv);
        mesh.SetTriangles(_t, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

}
