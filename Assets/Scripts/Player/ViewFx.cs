using UnityEngine;

namespace UFO {

/// <summary>
/// The two things that happen at the muzzle: the flash, and the case coming out of the ejection
/// port.
///
/// Both were missing entirely. Firing produced a point light, a tracer and a pooled sphere, so the
/// gun itself never did anything - the feedback was all downrange. A flash with a shape and a case
/// that tumbles out and bounces off the pavement are what make the weapon feel like it fired,
/// rather than like it emitted.
/// </summary>
public static class ViewFx {

    // ------------------------------------------------------------------ muzzle flash

    static Mesh _flashMesh;

    /// <summary>
    /// The flash: a short forward cone with a star of flat blades crossed through it.
    ///
    /// The cone alone is a glowing ice-cream cone from any angle; the blades are what read as a
    /// flash, because they catch the eye edge-on as the weapon moves. Built once and shared - a
    /// flash is on screen for three frames and there is only ever one.
    /// </summary>
    public static Mesh FlashMesh() {
        if (_flashMesh != null) return _flashMesh;
        var kit = new MeshKit();

        // Cone opening forward along +Z: built up +Y then rotated by the caller's transform is
        // more trouble than four explicit blades, so the body is a short tapered tube instead.
        kit.Tube(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0.17f), 0.035f, 7);
        kit.Tube(new Vector3(0f, 0f, 0.05f), new Vector3(0f, 0f, 0.13f), 0.075f, 7);

        // Four blades on the diagonals. Each is a thin box, so it is visible from both sides and
        // needs no double-sided material.
        for (int i = 0; i < 4; i++) {
            float a = i * 45f;
            kit.Box(new Vector3(0f, 0f, 0.06f), new Vector3(0.30f, 0.012f, 0.16f),
                    Quaternion.Euler(0f, 0f, a));
        }
        _flashMesh = kit.Build("MuzzleFlash");
        return _flashMesh;
    }

    /// <summary>A flash object under the weapon rig, hidden until <see cref="Flash"/> is called.</summary>
    public static MuzzleFlash Create(Transform parent, Color color) {
        var go = new GameObject("MuzzleFlash");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = FlashMesh();
        var r = go.AddComponent<MeshRenderer>();
        r.material = Fx.AdditiveTinted(color * 2.6f);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        go.SetActive(false);
        return go.AddComponent<MuzzleFlash>();
    }

    // ------------------------------------------------------------------ brass

    static Mesh _caseMesh;

    static Mesh CaseMesh() {
        if (_caseMesh != null) return _caseMesh;
        var kit = new MeshKit();
        kit.Tube(Vector3.zero, new Vector3(0f, 0f, 0.048f), 0.0065f, 6, caps: true);
        kit.Cone(new Vector3(0f, 0f, 0f), 0.0075f, 0.010f, 6);
        _caseMesh = kit.Build("Case");
        return _caseMesh;
    }

    static Material _brass;

    /// <summary>
    /// Throw a case out of the ejection port.
    ///
    /// Right and slightly back, which is where a real port puts it, and with enough spin that it
    /// catches the light on the way down. It lands, bounces once and is gone in two seconds - the
    /// point is the arc past the player's eye, not the brass on the floor.
    /// </summary>
    public static void EjectCase(Vector3 worldPos, Quaternion camRot) {
        if (_brass == null) {
            _brass = Prim.Lit();
            if (_brass != null) {
                var c = new Color(0.52f, 0.36f, 0.12f);
                if (_brass.HasProperty("_BaseColor")) _brass.SetColor("_BaseColor", c);
                _brass.color = c;
                if (_brass.HasProperty("_Smoothness")) _brass.SetFloat("_Smoothness", 0.72f);
                if (_brass.HasProperty("_Metallic")) _brass.SetFloat("_Metallic", 0.9f);
            }
        }

        var go = new GameObject("Case");
        go.transform.position = worldPos;
        go.transform.rotation = camRot;
        go.AddComponent<MeshFilter>().sharedMesh = CaseMesh();
        var r = go.AddComponent<MeshRenderer>();
        r.material = _brass;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        var f = go.AddComponent<FallingDebris>();
        f.Velocity = camRot * new Vector3(Random.Range(1.5f, 2.4f), Random.Range(0.9f, 1.6f), Random.Range(-0.5f, 0.2f));
        f.Spin = new Vector3(Random.Range(-900f, 900f), Random.Range(-900f, 900f), Random.Range(-900f, 900f));
        f.Life = 2.0f;
    }
}

/// <summary>Shows the flash for a few frames, at a random roll so repeats never look identical.</summary>
public class MuzzleFlash : MonoBehaviour {
    float _t;

    public void Show(Vector3 localPos, float scale, Color color) {
        transform.localPosition = localPos;
        transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        transform.localScale = Vector3.one * scale * Random.Range(0.85f, 1.2f);
        var r = GetComponent<Renderer>();
        if (r != null) {
            var c = color * 2.6f;
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
            r.material.color = c;
        }
        gameObject.SetActive(true);
        _t = 0.045f;
    }

    void LateUpdate() {
        if (!gameObject.activeSelf) return;
        _t -= Time.deltaTime;
        if (_t <= 0f) gameObject.SetActive(false);
    }
}

}
