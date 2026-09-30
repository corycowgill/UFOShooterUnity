using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// The four drops, as objects rather than as coloured balls.
///
/// They were all the same additive sphere in four colours, which is readable in a quiet room and
/// not readable at all in the middle of a wave: the plasma bolts, the shield bursts, the muzzle
/// flashes and the enemy threat lights are *also* saturated glowing blobs, and a health drop
/// twelve metres away looked exactly like a stray bolt. Giving each one a silhouette - a crate
/// with a cross, a cell, an ammo box, a grenade - means it can be identified by shape before its
/// colour has even registered, which is the whole point of a pickup.
///
/// Each is a body mesh plus a glow mesh, so the shape is lit and legible while the emissive part
/// still carries at distance and still feeds the bloom.
/// </summary>
public static class PickupProps {

    public struct Built {
        public Mesh Body, Glow;
        public Color Tint, Light;
    }

    static readonly Dictionary<string, Built> _cache = new Dictionary<string, Built>();

    public static Built For(string type) {
        if (_cache.TryGetValue(type, out var b)) return b;
        b = type switch {
            "health" => Health(),
            "shield" => Shield(),
            "ammo" => Ammo(),
            _ => Grenade(),
        };
        _cache[type] = b;
        return b;
    }

    // ------------------------------------------------------------------ the four

    /// <summary>A medical crate: a box with a chamfered lid and a cross cut into the lid in light.</summary>
    static Built Health() {
        var body = new MeshKit();
        body.Box(Vector3.zero, new Vector3(0.36f, 0.26f, 0.26f));
        body.Box(new Vector3(0f, 0.14f, 0f), new Vector3(0.30f, 0.04f, 0.22f));
        // Corner feet, so it reads as a container rather than as a cube.
        for (float sx = -1f; sx <= 1f; sx += 2f)
            for (float sz = -1f; sz <= 1f; sz += 2f)
                body.Box(new Vector3(0.16f * sx, -0.14f, 0.11f * sz), new Vector3(0.05f, 0.05f, 0.05f));

        var glow = new MeshKit();
        // The cross, on both long faces and on the lid, so it reads from any angle.
        for (float sz = -1f; sz <= 1f; sz += 2f) {
            glow.Box(new Vector3(0f, 0f, 0.135f * sz), new Vector3(0.20f, 0.055f, 0.005f));
            glow.Box(new Vector3(0f, 0f, 0.135f * sz), new Vector3(0.055f, 0.17f, 0.005f));
        }
        glow.Box(new Vector3(0f, 0.165f, 0f), new Vector3(0.17f, 0.005f, 0.05f));
        glow.Box(new Vector3(0f, 0.165f, 0f), new Vector3(0.05f, 0.005f, 0.15f));

        return new Built {
            Body = body.Build("Pickup_health"), Glow = glow.Build("Pickup_health_glow"),
            Tint = new Color(0.62f, 0.66f, 0.62f), Light = new Color(0.2f, 1f, 0.45f),
        };
    }

    /// <summary>A shield cell: a glowing core in a hexagonal steel cradle.</summary>
    static Built Shield() {
        var body = new MeshKit();
        body.Tube(new Vector3(0f, -0.17f, 0f), new Vector3(0f, 0.17f, 0f), 0.075f, 6, caps: true);
        // Cradle: three staves round the cell, and a cap at each end.
        for (int i = 0; i < 3; i++) {
            float a = i * Mathf.PI * 2f / 3f;
            var o = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.115f;
            body.Tube(o + Vector3.down * 0.17f, o + Vector3.up * 0.17f, 0.022f, 5);
        }
        body.Tube(new Vector3(0f, 0.17f, 0f), new Vector3(0f, 0.21f, 0f), 0.135f, 6, caps: true);
        body.Tube(new Vector3(0f, -0.21f, 0f), new Vector3(0f, -0.17f, 0f), 0.135f, 6, caps: true);

        var glow = new MeshKit();
        glow.Tube(new Vector3(0f, -0.145f, 0f), new Vector3(0f, 0.145f, 0f), 0.088f, 8, caps: true);

        return new Built {
            Body = body.Build("Pickup_shield"), Glow = glow.Build("Pickup_shield_glow"),
            Tint = new Color(0.45f, 0.50f, 0.58f), Light = new Color(0.15f, 0.55f, 1f),
        };
    }

    /// <summary>An ammo box: a steel case with a carry handle and a lit charge strip.</summary>
    static Built Ammo() {
        var body = new MeshKit();
        body.Box(Vector3.zero, new Vector3(0.34f, 0.22f, 0.24f));
        body.Box(new Vector3(0f, 0.12f, 0f), new Vector3(0.36f, 0.03f, 0.26f));
        // Handle.
        body.Tube(new Vector3(-0.08f, 0.135f, 0f), new Vector3(-0.08f, 0.20f, 0f), 0.014f, 5);
        body.Tube(new Vector3(0.08f, 0.135f, 0f), new Vector3(0.08f, 0.20f, 0f), 0.014f, 5);
        body.Tube(new Vector3(-0.08f, 0.20f, 0f), new Vector3(0.08f, 0.20f, 0f), 0.014f, 5);

        var glow = new MeshKit();
        for (float sz = -1f; sz <= 1f; sz += 2f)
            for (int i = -1; i <= 1; i++)
                glow.Box(new Vector3(i * 0.09f, -0.02f, 0.125f * sz), new Vector3(0.045f, 0.10f, 0.005f));

        return new Built {
            Body = body.Build("Pickup_ammo"), Glow = glow.Build("Pickup_ammo_glow"),
            Tint = new Color(0.40f, 0.38f, 0.30f), Light = new Color(1f, 0.70f, 0.10f),
        };
    }

    /// <summary>A grenade: body, collar, spoon and a lit arming band.</summary>
    static Built Grenade() {
        var body = new MeshKit();
        body.Tube(new Vector3(0f, -0.11f, 0f), new Vector3(0f, 0.09f, 0f), 0.085f, 9, caps: true);
        body.Cone(new Vector3(0f, 0.09f, 0f), 0.085f, 0.05f, 9);
        body.Tube(new Vector3(0f, 0.12f, 0f), new Vector3(0f, 0.19f, 0f), 0.034f, 6, caps: true);
        // Spoon down one side.
        body.Box(new Vector3(0.05f, 0.10f, 0f), new Vector3(0.022f, 0.16f, 0.05f),
                 Quaternion.Euler(0f, 0f, -8f));

        var glow = new MeshKit();
        glow.Tube(new Vector3(0f, -0.03f, 0f), new Vector3(0f, 0.01f, 0f), 0.089f, 9);
        glow.Tube(new Vector3(0f, -0.075f, 0f), new Vector3(0f, -0.055f, 0f), 0.089f, 9);

        return new Built {
            Body = body.Build("Pickup_grenade"), Glow = glow.Build("Pickup_grenade_glow"),
            Tint = new Color(0.20f, 0.24f, 0.18f), Light = new Color(0.35f, 1f, 0.35f),
        };
    }

    // ------------------------------------------------------------------ instancing

    /// <summary>
    /// Build one pickup: the lit body, the emissive part, and a small light so it throws its own
    /// colour onto the road around it. That pool of colour is what catches the eye in peripheral
    /// vision, which is where a pickup is usually first seen.
    /// </summary>
    public static GameObject Spawn(string type, Transform parent) {
        var b = For(type);

        var root = new GameObject("Pickup_" + type);
        if (parent != null) root.transform.SetParent(parent, false);

        Add(root.transform, b.Body, LitMat(b.Tint), "Body");
        Add(root.transform, b.Glow, Fx.AdditiveTinted(b.Light * 2.4f), "Glow");

        var lightGo = new GameObject("Light");
        lightGo.transform.SetParent(root.transform, false);
        var l = lightGo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = b.Light;
        l.range = 5.5f;
        l.intensity = 2.2f;
        l.shadows = LightShadows.None;
        return root;
    }

    static void Add(Transform parent, Mesh mesh, Material mat, string name) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = true;
    }

    static Material LitMat(Color c) {
        var m = Prim.Lit();
        if (m == null) return null;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.35f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.45f);
        return m;
    }
}

}
