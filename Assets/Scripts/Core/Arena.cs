using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// One axis-aligned box in the collision world. `soft` props can be walked through by a stuck
/// enemy; `isWater` stops feet but not bullets. This mirrors v2's collider list exactly — the
/// port deliberately does NOT use Unity physics colliders for world collision, because every
/// system (player sweep, enemy steering, projectiles, hitscan) reads the same flat array and
/// the behaviour is identical to the web build.
/// </summary>
public struct ArenaBox {
    public Vector3 min, max;
    public bool soft;
    public bool isWater;
    /// <summary>
    /// A surface that raises the walking height - pavement, the pier deck. Everything else is an
    /// obstacle, however low: a bench is something to walk around, not something to stand on top
    /// of, and treating every short collider as ground is what made enemies hover over the street
    /// furniture.
    /// </summary>
    public bool walkable;

    public ArenaBox(Vector3 min, Vector3 max, bool soft = false, bool isWater = false, bool walkable = false) {
        this.min = min; this.max = max; this.soft = soft; this.isWater = isWater; this.walkable = walkable;
    }

    public static ArenaBox FromCenter(Vector3 center, Vector3 size, bool soft = false, bool isWater = false) {
        var h = size * 0.5f;
        return new ArenaBox(center - h, center + h, soft, isWater);
    }

    public bool ContainsPoint(Vector3 p) =>
        p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && p.z >= min.z && p.z <= max.z;

    /// <summary>XZ overlap test against a circle of radius r at (x,z). Ignores height.</summary>
    public bool OverlapsXZ(float x, float z, float r) =>
        x + r > min.x && x - r < max.x && z + r > min.z && z - r < max.z;

    /// <summary>Slab test. Returns distance along the ray to the near face, or -1 for a miss.</summary>
    public float RayDistance(Vector3 origin, Vector3 dir) {
        float tmin = 0f, tmax = float.PositiveInfinity;
        for (int a = 0; a < 3; a++) {
            float o = origin[a], d = dir[a], lo = min[a], hi = max[a];
            if (Mathf.Abs(d) < 1e-8f) { if (o < lo || o > hi) return -1f; continue; }
            float inv = 1f / d;
            float t1 = (lo - o) * inv, t2 = (hi - o) * inv;
            if (t1 > t2) { var t = t1; t1 = t2; t2 = t; }
            if (t1 > tmin) tmin = t1;
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return -1f;
        }
        return tmin;
    }
}

/// <summary>
/// The flat collision world for the current level, plus the queries every other system needs.
/// Rebuilt from scratch by LevelBuilder on each level load.
/// </summary>
public class Arena {
    public readonly List<ArenaBox> Boxes = new List<ArenaBox>(512);
    public float Radius = 92f;

    public void Clear() { Boxes.Clear(); }
    public void Add(ArenaBox b) { Boxes.Add(b); }

    public void AddFromRenderers(GameObject go, bool soft = false) {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        Boxes.Add(new ArenaBox(b.min, b.max, soft));
    }

    /// <summary>True when a straight line from `from` to `to` crosses a non-water collider.</summary>
    public bool LineBlocked(Vector3 from, Vector3 to) {
        var d = to - from;
        float len = d.magnitude;
        if (len < 1e-3f) return false;
        d /= len;
        for (int i = 0; i < Boxes.Count; i++) {
            var b = Boxes[i];
            if (b.isWater) continue;
            float t = b.RayDistance(from, d);
            if (t >= 0f && t < len) return true;
        }
        return false;
    }

    /// <summary>
    /// Nearest wall along a ray. Returns the distance (or maxDist), and fills the hit point and
    /// face normal for decal placement.
    /// </summary>
    public float RayWall(Vector3 origin, Vector3 dir, float maxDist, out Vector3 point, out Vector3 normal) {
        float best = maxDist;
        point = origin + dir * maxDist;
        normal = -dir;
        for (int i = 0; i < Boxes.Count; i++) {
            var b = Boxes[i];
            if (b.isWater) continue;
            float t = b.RayDistance(origin, dir);
            if (t < 0f || t >= best) continue;
            best = t;
            point = origin + dir * t;
            normal = FaceNormal(b, point);
        }
        return best;
    }

    static Vector3 FaceNormal(ArenaBox b, Vector3 p) {
        const float e = 1e-3f;
        if (Mathf.Abs(p.x - b.min.x) < e) return Vector3.left;
        if (Mathf.Abs(p.x - b.max.x) < e) return Vector3.right;
        if (Mathf.Abs(p.z - b.min.z) < e) return Vector3.back;
        if (Mathf.Abs(p.z - b.max.z) < e) return Vector3.forward;
        if (Mathf.Abs(p.y - b.max.y) < e) return Vector3.up;
        return Vector3.down;
    }

    public bool PointInSolid(Vector3 p) {
        for (int i = 0; i < Boxes.Count; i++) {
            var b = Boxes[i];
            if (!b.isWater && b.ContainsPoint(p)) return true;
        }
        return false;
    }
}

}
