using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// Loads the imported glTF assets out of Resources and hands back prefabs to instantiate.
///
/// Resources (rather than Addressables) is deliberate for a WebGL single-page build: the game
/// ships every model anyway, there is no streaming or DLC story, and Resources means no catalog
/// fetch before the first frame.
/// </summary>
public static class ModelCache {

    /// <summary>
    /// glTF is right-handed with -Z forward; Unity is left-handed with +Z forward. glTFast
    /// resolves the handedness, which leaves the rigs facing away from their travel direction.
    /// One yaw offset here fixes every model at once rather than per-asset.
    /// </summary>
    public const float ForwardYawOffset = 180f;

    static readonly Dictionary<string, GameObject> _cache = new Dictionary<string, GameObject>();
    static readonly HashSet<string> _missing = new HashSet<string>();

    public static GameObject Load(string resourcePath) {
        if (_cache.TryGetValue(resourcePath, out var cached)) return cached;
        if (_missing.Contains(resourcePath)) return null;

        var go = Resources.Load<GameObject>(resourcePath);
        if (go == null) {
            _missing.Add(resourcePath);
            Debug.LogWarning($"[ModelCache] missing model: {resourcePath}");
            return null;
        }
        _cache[resourcePath] = go;
        return go;
    }

    /// <summary>Renderer bounds of an instantiated hierarchy, in its own local space.</summary>
    public static bool LocalBounds(GameObject instance, out Bounds bounds) {
        bounds = new Bounds();
        var rs = instance.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return false;

        // Measure in the instance's local space so a parent scale does not feed back into itself.
        var toLocal = instance.transform.worldToLocalMatrix;
        bool first = true;
        foreach (var r in rs) {
            var b = r.bounds;
            // Transform the world AABB's eight corners into local space.
            for (int i = 0; i < 8; i++) {
                var c = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                var lp = toLocal.MultiplyPoint3x4(c);
                if (first) { bounds = new Bounds(lp, Vector3.zero); first = false; }
                else bounds.Encapsulate(lp);
            }
        }
        return !first;
    }

    /// <summary>World-space renderer bounds of an instantiated hierarchy.</summary>
    public static bool WorldBounds(GameObject instance, out Bounds bounds) {
        bounds = new Bounds();
        var rs = instance.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return false;
        bounds = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) bounds.Encapsulate(rs[i].bounds);
        return true;
    }

    /// <summary>
    /// Scale a model so it stands exactly `targetHeight` metres tall, resting on its parent's
    /// origin and centred on it horizontally. Matches v2's assets.js normalize(). Every gameplay
    /// number - hit capsules, muzzle heights, the shield bubble - assumes this has been applied.
    ///
    /// Everything here is done in WORLD space on purpose. Measuring in the instance's own local
    /// space and then correcting localPosition mixes two coordinate systems: local bounds do not
    /// change when the instance is scaled, so the correction comes out short by exactly the scale
    /// factor and the model ends up buried by roughly half its height. That bug sank all 764
    /// props and put the skyscrapers 48 m into the ground.
    /// </summary>
    public static void NormalizeHeight(GameObject instance, float targetHeight) {
        if (!WorldBounds(instance, out var b) || b.size.y < 1e-5f) return;
        instance.transform.localScale *= targetHeight / b.size.y;

        if (!WorldBounds(instance, out b)) return;
        var anchor = instance.transform.parent != null ? instance.transform.parent.position : Vector3.zero;
        instance.transform.position += new Vector3(
            anchor.x - b.center.x,
            anchor.y - b.min.y,
            anchor.z - b.center.z);
    }

    /// <summary>Scale so the longest dimension matches `targetSize`. Same world-space rule as above.</summary>
    public static void NormalizeSize(GameObject instance, float targetSize) {
        if (!WorldBounds(instance, out var b)) return;
        float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (longest < 1e-5f) return;
        instance.transform.localScale *= targetSize / longest;

        if (!WorldBounds(instance, out b)) return;
        var anchor = instance.transform.parent != null ? instance.transform.parent.position : Vector3.zero;
        instance.transform.position += new Vector3(0f, anchor.y - b.min.y, 0f);
    }

    /// <summary>Strip any colliders glTFast produced; world collision comes from Arena, not physics.</summary>
    public static void StripColliders(GameObject instance) {
        foreach (var c in instance.GetComponentsInChildren<Collider>()) Object.Destroy(c);
    }
}

}
