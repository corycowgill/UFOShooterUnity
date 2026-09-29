using UnityEngine;

namespace UFO {

public enum PrimKind { Sphere, Capsule, Cube, Quad, Plane, Cylinder }

/// <summary>
/// Renderer-only primitives and materials, loaded from baked assets in Resources.
///
/// This exists because two things that work fine in the Editor fail in a player build:
///
/// 1. <c>GameObject.CreatePrimitive</c> attaches a collider, and Unity's engine-code stripping
///    removes SphereCollider/CapsuleCollider when nothing else in the game references them - this
///    game uses no Unity physics at all. The call then logs
///    "Can't add component because class 'SphereCollider' doesn't exist".
/// 2. <c>Shader.Find</c> only sees shaders the build actually included. URP's Lit and Unlit are
///    not referenced by any asset here (every material is made at runtime), so they were stripped
///    and Shader.Find returned null - which threw out of GameManager.Awake and left the game with
///    a camera, a weapon and nothing else.
///
/// Baking meshes and material templates into Resources fixes both: the assets are hard references
/// so nothing is stripped, and no collider is ever created. ProjectSetup.CreateRuntimeAssets
/// generates them; see UFO/Setup/Create Runtime Assets.
/// </summary>
public static class Prim {

    const string MeshRoot = "Primitives/";
    const string MatRoot = "Materials/";

    static readonly Mesh[] _meshes = new Mesh[6];
    static Material _unlitTemplate, _litTemplate;

    public static Mesh Mesh(PrimKind kind) {
        int i = (int)kind;
        if (_meshes[i] == null) {
            _meshes[i] = Resources.Load<Mesh>(MeshRoot + kind);
            if (_meshes[i] == null) Debug.LogError($"[Prim] missing baked mesh Resources/{MeshRoot}{kind}");
        }
        return _meshes[i];
    }

    /// <summary>A GameObject with just MeshFilter + MeshRenderer. No collider, ever.</summary>
    public static GameObject Create(PrimKind kind, string name = null, Transform parent = null) {
        var go = new GameObject(name ?? kind.ToString());
        if (parent != null) go.transform.SetParent(parent, false);

        go.AddComponent<MeshFilter>().sharedMesh = Mesh(kind);
        var r = go.AddComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go;
    }

    // ---- material templates ----------------------------------------------
    // new Material(template) copies the template's shader and keywords, which is exactly what
    // new Material(Shader.Find(...)) used to do - but survives stripping.

    public static Material Unlit() {
        if (_unlitTemplate == null) {
            _unlitTemplate = Resources.Load<Material>(MatRoot + "UnlitTemplate");
            if (_unlitTemplate == null) Debug.LogError("[Prim] missing Resources/Materials/UnlitTemplate");
        }
        return _unlitTemplate != null ? new Material(_unlitTemplate) : null;
    }

    public static Material Lit() {
        if (_litTemplate == null) {
            _litTemplate = Resources.Load<Material>(MatRoot + "LitTemplate");
            if (_litTemplate == null) Debug.LogError("[Prim] missing Resources/Materials/LitTemplate");
        }
        return _litTemplate != null ? new Material(_litTemplate) : null;
    }
}

}
