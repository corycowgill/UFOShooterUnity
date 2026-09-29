using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UFO.EditorTools {

/// <summary>
/// One-shot project configuration, driven from the command line so the whole project is
/// reproducible from source:
///
///   Unity -batchmode -quit -executeMethod UFO.EditorTools.ProjectSetup.RunAll
///
/// It creates the URP assets and wires them into Graphics/Quality settings, sets the glTF
/// importers to the settings the game expects (legacy animation clips, sane texture sizes),
/// generates the single bootstrap scene, and configures the WebGL player settings.
/// </summary>
public static class ProjectSetup {

    const string SettingsDir = "Assets/Settings";
    const string ScenePath = "Assets/Scenes/Game.unity";

    public static void RunAll() {
        Debug.Log("[Setup] begin");
        SetupUrp();
        CreateRuntimeAssets();
        SetupPlayerSettings();
        AssetImportSetup.RunAll();
        SetupScene();
        SetupBuildSettings();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Setup] complete");
    }

    // ---------------------------------------------------------------- URP

    [MenuItem("UFO/Setup/Create URP Assets")]
    public static void SetupUrp() {
        Directory.CreateDirectory(SettingsDir);

        var rendererPath = SettingsDir + "/UFO_Renderer.asset";
        var pipelinePath = SettingsDir + "/UFO_URP.asset";

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
        if (renderer == null) {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, rendererPath);
        }
        renderer.postProcessData = FindPostProcessData();

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
        if (pipeline == null) {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, pipelinePath);
        }

        // WebGL-friendly defaults: one shadow cascade, modest shadow distance, HDR on for bloom.
        var so = new SerializedObject(pipeline);
        SetProp(so, "m_SupportsHDR", true);
        SetProp(so, "m_MainLightShadowmapResolution", 1024);
        SetProp(so, "m_ShadowDistance", 90f);
        SetProp(so, "m_ShadowCascadeCount", 1);
        SetProp(so, "m_MSAA", 1);                     // SMAA does the anti-aliasing instead
        SetProp(so, "m_SupportsCameraDepthTexture", true);
        SetProp(so, "m_SupportsCameraOpaqueTexture", false);
        SetProp(so, "m_AdditionalLightsPerObjectLimit", 4);
        so.ApplyModifiedPropertiesWithoutUndo();

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        for (int i = 0; i < QualitySettings.count; i++) {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = pipeline;
        }

        EditorUtility.SetDirty(renderer);
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        Debug.Log("[Setup] URP assets created and assigned");
    }

    static PostProcessData FindPostProcessData() {
        var guid = AssetDatabase.FindAssets("t:PostProcessData").FirstOrDefault();
        if (string.IsNullOrEmpty(guid)) return null;
        return AssetDatabase.LoadAssetAtPath<PostProcessData>(AssetDatabase.GUIDToAssetPath(guid));
    }

    static void SetProp(SerializedObject so, string name, object value) {
        var p = so.FindProperty(name);
        if (p == null) return;
        switch (value) {
            case bool b: p.boolValue = b; break;
            case int i: p.intValue = i; break;
            case float f: p.floatValue = f; break;
        }
    }

    // ---------------------------------------------------------------- runtime assets

    /// <summary>
    /// Bakes the primitive meshes and shader material templates the game builds itself out of.
    ///
    /// Both exist to survive a player build. Engine-code stripping drops SphereCollider and
    /// CapsuleCollider because this game uses no Unity physics, which makes
    /// GameObject.CreatePrimitive fail; and URP's Lit/Unlit shaders are stripped because no asset
    /// references them, which makes Shader.Find return null. Saving real assets fixes both at the
    /// source: the shaders now have a hard reference, and no collider is ever constructed.
    /// </summary>
    [MenuItem("UFO/Setup/Create Runtime Assets")]
    public static void CreateRuntimeAssets() {
        Directory.CreateDirectory("Assets/Resources/Primitives");
        Directory.CreateDirectory("Assets/Resources/Materials");

        var kinds = new (PrimitiveType type, string name)[] {
            (PrimitiveType.Sphere, "Sphere"), (PrimitiveType.Capsule, "Capsule"),
            (PrimitiveType.Cube, "Cube"), (PrimitiveType.Quad, "Quad"), (PrimitiveType.Plane, "Plane"),
            (PrimitiveType.Cylinder, "Cylinder"),
        };
        foreach (var (type, name) in kinds) {
            var path = $"Assets/Resources/Primitives/{name}.asset";
            var temp = GameObject.CreatePrimitive(type);
            var src = temp.GetComponent<MeshFilter>().sharedMesh;
            var copy = Object.Instantiate(src);
            copy.name = name;
            Object.DestroyImmediate(temp);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) AssetDatabase.CreateAsset(copy, path);
            else { EditorUtility.CopySerialized(copy, existing); EditorUtility.SetDirty(existing); }
        }

        SaveMaterialTemplate("UnlitTemplate", "Universal Render Pipeline/Unlit");
        SaveMaterialTemplate("LitTemplate", "Universal Render Pipeline/Lit");
        // A real skybox rather than a dome mesh: skyboxes are never touched by fog, and at 450 m
        // an exp-squared fog of 0.0048 leaves a dome 99% fog-coloured - which is why the sky read
        // as a flat dead slab instead of a gradient.
        SaveMaterialTemplate("SkyboxTemplate", "Skybox/Panoramic");

        AssetDatabase.SaveAssets();
        Debug.Log("[Setup] runtime assets baked (6 meshes, 2 material templates)");
    }

    static void SaveMaterialTemplate(string name, string shaderName) {
        var shader = Shader.Find(shaderName);
        if (shader == null) { Debug.LogError($"[Setup] shader not found: {shaderName}"); return; }

        var path = $"Assets/Resources/Materials/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) AssetDatabase.CreateAsset(new Material(shader), path);
        else { mat.shader = shader; EditorUtility.SetDirty(mat); }

        // Belt and braces: also pin the shader in Always Included so anything still using
        // Shader.Find keeps working.
        AlwaysInclude(shader);
    }

    /// <summary>Adds a shader to Graphics Settings' Always Included list if not already there.</summary>
    static void AlwaysInclude(Shader shader) {
        var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphics == null || graphics.Length == 0) return;
        var so = new SerializedObject(graphics[0]);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        if (list == null) return;

        for (int i = 0; i < list.arraySize; i++)
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;

        list.InsertArrayElementAtIndex(list.arraySize);
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---------------------------------------------------------------- player settings

    [MenuItem("UFO/Setup/Player Settings")]
    public static void SetupPlayerSettings() {
        PlayerSettings.companyName = "Cory Cowgill";
        PlayerSettings.productName = "UFO Invasion III";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.defaultWebScreenWidth = 1280;
        PlayerSettings.defaultWebScreenHeight = 720;
        PlayerSettings.runInBackground = true;

        // Without this the per-texture aniso level is only a hint; the streets need it forced.
        for (int i = 0; i < QualitySettings.count; i++) {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        }

        // WebGL: Brotli on release, no exceptions (smaller + faster), threads off for widest support.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
        PlayerSettings.WebGL.memorySize = 512;
        // Custom template: plays the Hallucinated Games studio ident while the player
        // downloads. PROJECT: resolves to Assets/WebGLTemplates/<name>.
        PlayerSettings.WebGL.template = "PROJECT:Hallucinated";

        PlayerSettings.SetManagedStrippingLevel(
            UnityEditor.Build.NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);
        PlayerSettings.SetScriptingBackend(
            UnityEditor.Build.NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetIl2CppCompilerConfiguration(
            UnityEditor.Build.NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Master);

        Debug.Log("[Setup] player settings applied");
    }

    // ---------------------------------------------------------------- scene

    [MenuItem("UFO/Setup/Create Scene")]
    public static void SetupScene() {
        Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The scene holds exactly one object. Everything else - player, camera, level, HUD - is
        // constructed by GameManager at runtime from Resources, which is what makes the whole
        // game reproducible from code alone.
        var root = new GameObject("GameRoot");
        root.AddComponent<GameManager>();

        // A post-process volume for bloom + tonemapping, the URP equivalent of v2's EffectComposer.
        var volumeGo = new GameObject("PostProcessing");
        var volume = volumeGo.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 1f;

        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, SettingsDir + "/UFO_PostFX.asset");

        var tonemap = profile.Add<Tonemapping>(true);
        tonemap.mode.overrideState = true;
        tonemap.mode.value = TonemappingMode.ACES;

        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.overrideState = true;  bloom.intensity.value = 0.9f;
        bloom.threshold.overrideState = true;  bloom.threshold.value = 0.85f;
        bloom.scatter.overrideState = true;    bloom.scatter.value = 0.62f;

        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.overrideState = true; vignette.intensity.value = 0.28f;
        vignette.smoothness.overrideState = true; vignette.smoothness.value = 0.45f;

        var ca = profile.Add<ChromaticAberration>(true);
        ca.intensity.overrideState = true; ca.intensity.value = 0.08f;

        var grade = profile.Add<ColorAdjustments>(true);
        grade.postExposure.overrideState = true; grade.postExposure.value = 0.05f;
        grade.contrast.overrideState = true;     grade.contrast.value = 8f;
        grade.saturation.overrideState = true;   grade.saturation.value = 6f;

        EditorUtility.SetDirty(profile);
        volume.sharedProfile = profile;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Setup] scene created at " + ScenePath);
    }

    // ---------------------------------------------------------------- build settings

    [MenuItem("UFO/Setup/Build Settings")]
    public static void SetupBuildSettings() {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[Setup] build scene list set");
    }
}

}
