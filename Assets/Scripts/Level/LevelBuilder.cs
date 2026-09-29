using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// Assembles a Chicago arena from LevelData.
///
/// A level is a placement list, not a generator - every building is an authored model dropped at
/// an explicit spot. But the list alone is only the skeleton. What makes it read as a city rather
/// than models on a plain is everything around it, and all of it is ported from v2's
/// buildCity/dressCity:
///
///   - buildings widened to fill their block, and their daylight-baked albedo pulled down to night
///   - a skyline of extra towers beyond the arena, so the city has a horizon
///   - street lamps at block corners (at night the pool of light sells it more than the pole)
///   - sidewalk furniture, intersection signals, parked cars along both kerbs, crash sites,
///     a tree-ringed central plaza, and dumpsters down the alleys
///   - spawn points on real street intersections rather than an abstract ring
///
/// Deco entries are placed without colliders, which is how the Lakefront pier stays walkable.
/// </summary>
public class LevelBuilder : MonoBehaviour {

    public Arena Arena;
    public Light Sun;

    // v2's light intensities are Three.js numbers and do not mean the same thing in URP's linear
    // pipeline; carried across literally they blow the night scene out to daylight. These two
    // factors are the whole calibration, so the per-level colour relationships stay intact.
    // Raised from 0.30 after the sky fix. The original value was chosen while a fogged sky dome
    // was blowing the scene out, so it was compensating for a bug rather than calibrating light.
    const float AmbientScale = 0.45f;
    const float SunScale = 0.60f;

    // Trellis bakes daylight into the albedo. v2 pulls buildings to 0.42 and vehicles to 0.6 so
    // towers read as night-lit concrete and glass instead of white blocks in the dark.
    const float BuildingAlbedo = 0.42f;
    const float VehicleAlbedo = 0.60f;

    GameObject _root;
    LevelDef _def;
    readonly List<Vector3> _spawnPoints = new List<Vector3>();
    readonly List<Vector3> _wreckFires = new List<Vector3>();
    public IReadOnlyList<Vector3> SpawnPoints => _spawnPoints;
    public LevelDef Current { get; private set; }

    static Color Col(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    static float Num(object o) => o is float f ? f : (o is int i ? i : 0f);
    static string Str(object o) => o as string;

    // Deterministic LCG with v2's constants, so a level's dressing is identical every run.
    int _seed;
    void SeedFrom(string name, int salt) { _seed = name.Length * salt + 7; }
    float Rnd() { _seed = (int)((_seed * 1103515245L + 12345L) & 0x7fffffff); return _seed / (float)0x7fffffff; }

    bool OnLand(float x) => _def.Lake == null || x <= _def.Lake.X;

    public void Unload() {
        if (_root != null) Destroy(_root);
        _root = null;
        Arena.Clear();
        _spawnPoints.Clear();
        _wreckFires.Clear();
    }

    public void Build(int index) {
        Unload();

        int n = LevelData.Levels.Length;
        Current = _def = LevelData.Levels[((index % n) + n) % n];

        _root = new GameObject("Level_" + _def.Name);
        Arena.Radius = _def.ArenaRadius;

        BuildLighting();
        BuildSky();
        BuildGround();
        if (_def.Lake != null) BuildLake();

        BuildSkyline();
        BuildMothership();
        BuildBuildings();
        PlaceList(_def.Vehicles, collide: true);
        PlaceList(_def.Landmarks, collide: true);
        PlaceList(_def.Props, collide: true);
        PlaceList(_def.Deco, collide: false);
        DressCity();

        BuildSigns();
        BuildBeams();
        BuildStreetLamps();
        BuildFires();
        BuildSpawnPoints();

        // A dressed block is several hundred small static meshes. Combining collapses the draw
        // calls, which matters far more on WebGL than in the editor.
        // Combine only the static dressing. The mothership spins, so batching it would bake its
        // transform and freeze it in place.
        foreach (Transform child in _root.transform)
            if (child.GetComponent<SlowSpin>() == null && child.name != "Mothership")
                StaticBatchingUtility.Combine(child.gameObject);

        LogSummary();
    }

    // ---------------------------------------------------------------- lighting

    void BuildLighting() {
        var L = _def;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Col(L.HemiSky) * (L.HemiIntensity * AmbientScale);
        RenderSettings.ambientEquatorColor = Col(L.Ambient) * (L.AmbientIntensity * AmbientScale);
        RenderSettings.ambientGroundColor = Col(L.HemiGround) * (L.HemiIntensity * AmbientScale);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = Col(L.FogColor);
        // The level owns the base density; the player's Fog setting scales it, so a level change
        // does not silently undo their choice.
        GameSettings.Instance.BaseFogDensity = L.FogDensity;
        RenderSettings.fogDensity = L.FogDensity * GameSettings.Instance.FogScale;

        if (Sun != null) {
            Sun.color = Col(L.SunColor);
            Sun.intensity = L.SunIntensity * SunScale;
            Sun.transform.rotation = Quaternion.LookRotation(-L.SunDir.normalized, Vector3.up);
            Sun.shadows = LightShadows.Soft;
        }

        // v2 adds a dim violet rim from the north - the mothership's light on the far side of
        // every surface. It is most of why its buildings have a cool edge instead of reading flat.
        if (_rim == null) {
            var rimGo = new GameObject("RimLight");
            rimGo.transform.SetParent(transform, false);
            _rim = rimGo.AddComponent<Light>();
            _rim.type = LightType.Directional;
            _rim.shadows = LightShadows.None;
        }
        _rim.color = new Color(0.75f, 0.25f, 1f);
        // v2's 0.25 is a Three.js intensity. Carried across literally it washed every facade
        // lavender; URP needs roughly a quarter of it to read as a rim rather than a floodlight.
        _rim.intensity = 0.06f;
        _rim.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -0.4f, 0.9f).normalized, Vector3.up);
    }

    Light _rim;

    /// <summary>
    /// The sky is a real skybox, not a dome mesh, for one decisive reason: **fog does not touch a
    /// skybox**. As a 450 m sphere it sat under exp-squared fog at 0.0048 and came back 99%
    /// fog-coloured, so every level rendered the same flat slab instead of its own dusk.
    ///
    /// The gradient and the stars are baked into one equirectangular texture, which also saves a
    /// separate particle system for the starfield.
    /// </summary>
    void BuildSky() {
        // 2048x1024, because a star is a single texel: on a 512x256 sheet stretched over the
        // whole sky each one rendered as a fat white rectangle.
        const int W = 2048, H = 1024;
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[W * H];

        for (int y = 0; y < H; y++) {
            float t = y / (float)(H - 1);                  // 0 = nadir, 1 = zenith
            Color c = t < 0.5f
                ? Color.Lerp(_def.SkyGlow, _def.SkyHorizon, Mathf.InverseLerp(0f, 0.5f, t))
                : Color.Lerp(_def.SkyHorizon, _def.SkyTop, Mathf.InverseLerp(0.5f, 1f, t));
            for (int x = 0; x < W; x++) px[y * W + x] = c;
        }

        // Stars, thinning out toward the horizon so they read as sky rather than noise. Near the
        // zenith an equirectangular map is stretched hard in x, so the count is scaled by the
        // latitude to keep the density even instead of smearing them into streaks at the poles.
        SeedFrom(_def.Name, 5209);
        for (int i = 0; i < 2600; i++) {
            float v = Rnd();
            int y = Mathf.Clamp((int)(H * (0.52f + v * v * 0.48f)), 0, H - 1);
            float lat = (y / (float)H) * Mathf.PI;
            if (Rnd() > Mathf.Max(0.12f, Mathf.Sin(lat))) continue;
            int x = Mathf.Clamp((int)(Rnd() * W), 0, W - 1);
            float b = 0.30f + Rnd() * 0.70f;
            var c = px[y * W + x];
            px[y * W + x] = new Color(c.r + b, c.g + b, c.b + b * 1.05f, 1f);
        }

        tex.SetPixels(px);
        tex.Apply();

        var mat = Resources.Load<Material>("Materials/SkyboxTemplate");
        if (mat != null) {
            var inst = new Material(mat);
            if (inst.HasProperty("_MainTex")) inst.SetTexture("_MainTex", tex);
            if (inst.HasProperty("_Exposure")) inst.SetFloat("_Exposure", 1.0f);
            if (inst.HasProperty("_Mapping")) inst.SetFloat("_Mapping", 1f);   // latitude-longitude
            RenderSettings.skybox = inst;
        } else {
            Debug.LogWarning("[LevelBuilder] no skybox template; sky will fall back to solid colour");
            RenderSettings.skybox = null;
        }
    }

    void BuildGround() {
        var go = Prim.Create(PrimKind.Plane, "Ground", _root.transform);
        go.transform.localScale = Vector3.one * 60f;        // Unity plane is 10 m, so 600 m across

        var mat = Prim.Lit();
        var tex = Resources.Load<Texture2D>("Textures/asphalt");
        if (tex != null) {
            tex.wrapMode = TextureWrapMode.Repeat;
            // Tiled across 600 m and viewed at a grazing angle, the sampler drops to the smallest
            // mip and returns one flat colour unless anisotropic filtering is on.
            tex.anisoLevel = 8;
            tex.filterMode = FilterMode.Trilinear;
            if (mat.HasProperty("_BaseMap")) {
                mat.SetTexture("_BaseMap", tex);
                mat.SetTextureScale("_BaseMap", new Vector2(75f, 75f));
            }
            mat.mainTexture = tex;
            mat.mainTextureScale = new Vector2(75f, 75f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Col(_def.GroundColor) * 1.1f);
        } else if (mat.HasProperty("_BaseColor")) {
            mat.SetColor("_BaseColor", Col(_def.GroundColor));
        }
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", _def.Weather == "rain" ? 0.6f : 0.25f);

        var gr = go.GetComponent<Renderer>();
        gr.material = mat;
        gr.receiveShadows = true;     // the street is what the skyline casts onto

        BuildPaving();
    }

    /// <summary>
    /// A raised sidewalk slab on every block of the grid, and painted road markings between them.
    ///
    /// This is what the street furniture is positioned against. Without it the benches, trees and
    /// parked cars sit on one unbroken sheet of asphalt and read as scattered at random, however
    /// exactly their coordinates match v2 - there is simply no kerb line for the eye to line them
    /// up on. Adding the paving is what turns the placement list into a street.
    /// </summary>
    void BuildPaving() {
        // -- Sidewalk slabs: BLOCK + 3 square, 0.18 m proud of the road, on all 25 blocks.
        var walkMat = Prim.Lit();
        var walkTex = Resources.Load<Texture2D>("Textures/sidewalk");
        if (walkTex != null) {
            walkTex.wrapMode = TextureWrapMode.Repeat;
            walkTex.anisoLevel = 8;
            walkTex.filterMode = FilterMode.Trilinear;
            if (walkMat.HasProperty("_BaseMap")) {
                walkMat.SetTexture("_BaseMap", walkTex);
                walkMat.SetTextureScale("_BaseMap", new Vector2(LevelData.Block / 3f, LevelData.Block / 3f));
            }
            walkMat.mainTexture = walkTex;
            walkMat.mainTextureScale = new Vector2(LevelData.Block / 3f, LevelData.Block / 3f);
            if (walkMat.HasProperty("_BaseColor")) walkMat.SetColor("_BaseColor", new Color(0.69f, 0.69f, 0.67f));
        } else if (walkMat.HasProperty("_BaseColor")) {
            walkMat.SetColor("_BaseColor", new Color(0.43f, 0.43f, 0.42f));
        }
        if (walkMat.HasProperty("_Smoothness")) walkMat.SetFloat("_Smoothness", 0.05f);

        float slab = LevelData.Block + 3f;
        for (int gx = -2; gx <= 2; gx++) {
            for (int gz = -2; gz <= 2; gz++) {
                float x = gx * LevelData.Pitch, z = gz * LevelData.Pitch;
                if (!OnLand(x + LevelData.Block / 2f)) continue;
                var w = Prim.Create(PrimKind.Cube, "Sidewalk", _root.transform);
                w.transform.position = new Vector3(x, 0.09f, z);
                w.transform.localScale = new Vector3(slab, 0.18f, slab);
                var r = w.GetComponent<Renderer>();
                r.material = walkMat;
                r.receiveShadows = true;

                // Walkable, not an obstacle: at 0.18 m it is under the step threshold both the
                // player sweep and the enemy sweep use, so it lifts the ground instead of blocking.
                Arena.Add(new ArenaBox(
                    new Vector3(x - slab * 0.5f, 0f, z - slab * 0.5f),
                    new Vector3(x + slab * 0.5f, 0.18f, z + slab * 0.5f),
                    soft: true));
            }
        }

        BuildRoadMarkings();
    }

    /// <summary>
    /// Lane dashes down every street, plus a zebra crossing and stop line on all four approaches
    /// of every intersection - one combined mesh so the whole lot is a single draw call.
    /// </summary>
    void BuildRoadMarkings() {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        void Quad(float cx, float cz, float w, float l, bool rotated) {
            float hw = (rotated ? l : w) * 0.5f;
            float hl = (rotated ? w : l) * 0.5f;
            int b = verts.Count;
            const float y = 0.03f;
            verts.Add(new Vector3(cx - hw, y, cz - hl));
            verts.Add(new Vector3(cx + hw, y, cz - hl));
            verts.Add(new Vector3(cx + hw, y, cz + hl));
            verts.Add(new Vector3(cx - hw, y, cz + hl));
            uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
            uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
        }

        float[] streets = { -1.5f, -0.5f, 0.5f, 1.5f };
        for (int i = 0; i < streets.Length; i++) streets[i] *= LevelData.Pitch;

        foreach (var c in streets) {
            // Centre dashes, with a gap left clear across each intersection.
            for (float i = -120f; i <= 120f; i += 8f) {
                bool nearCross = false;
                foreach (var o in streets) if (Mathf.Abs(i - o) < LevelData.Street / 2f + 1f) nearCross = true;
                if (nearCross) continue;
                if (OnLand(c)) Quad(c, i, 0.22f, 3.2f, false);     // street running along z
                if (OnLand(i)) Quad(i, c, 0.22f, 3.2f, true);      // street running along x
            }

            foreach (var c2 in streets) {
                if (!OnLand(c + 6f)) continue;
                foreach (var (dx, dz, rotated) in new[] { (0f, 1f, false), (0f, -1f, false), (1f, 0f, true), (-1f, 0f, true) }) {
                    float ox = c + dx * (LevelData.Street / 2f + 1.2f);
                    float oz = c2 + dz * (LevelData.Street / 2f + 1.2f);
                    for (int k = -3; k <= 3; k++) {
                        float sx = rotated ? 0f : k * 1.2f;
                        float sz = rotated ? k * 1.2f : 0f;
                        Quad(ox + sx, oz + sz, rotated ? 2.2f : 0.6f, rotated ? 0.6f : 2.2f, false);
                    }
                    Quad(ox + dx * 1.8f, oz + dz * 1.8f, rotated ? 0.35f : 8f, rotated ? 8f : 0.35f, false);
                }
            }
        }

        if (verts.Count == 0) return;

        var mesh = new Mesh { name = "RoadMarkings" };
        mesh.indexFormat = verts.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject("RoadMarkings");
        go.transform.SetParent(_root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var mat = Prim.Lit();
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.85f, 0.82f, 0.69f));
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.3f);
        mr.material = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = true;
    }

    /// <summary>Lakefront: past lake.X is water except the pier corridor. Water stops feet, not bullets.</summary>
    void BuildLake() {
        var lake = _def.Lake;
        var go = Prim.Create(PrimKind.Plane, "Lake", _root.transform);
        go.transform.position = new Vector3(lake.X + 200f, -0.35f, 0f);
        go.transform.localScale = new Vector3(40f, 1f, 60f);

        var mat = Prim.Lit();
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.03f, 0.08f, 0.16f));
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.95f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.4f);
        var normal = Resources.Load<Texture2D>("Textures/waternormals");
        if (normal != null && mat.HasProperty("_BumpMap")) {
            mat.EnableKeyword("_NORMALMAP");
            mat.SetTexture("_BumpMap", normal);
            mat.SetTextureScale("_BumpMap", new Vector2(30f, 30f));
        }
        go.GetComponent<Renderer>().material = mat;

        const float far = 400f;
        Arena.Add(new ArenaBox(new Vector3(lake.X, -1f, lake.PierZ1), new Vector3(far, 0.1f, far), false, true));
        Arena.Add(new ArenaBox(new Vector3(lake.X, -1f, -far), new Vector3(far, 0.1f, lake.PierZ0), false, true));
        Arena.Add(new ArenaBox(new Vector3(lake.PierXEnd, -1f, lake.PierZ0), new Vector3(far, 0.1f, lake.PierZ1), false, true));

        var deck = Prim.Create(PrimKind.Cube, "Pier", _root.transform);
        float len = lake.PierXEnd - lake.X;
        deck.transform.position = new Vector3(lake.X + len * 0.5f, 0.05f, (lake.PierZ0 + lake.PierZ1) * 0.5f);
        deck.transform.localScale = new Vector3(len, 0.3f, lake.PierZ1 - lake.PierZ0);
        var dm = Prim.Lit();
        var sw = Resources.Load<Texture2D>("Textures/sidewalk");
        if (sw != null) {
            sw.anisoLevel = 8;
            if (dm.HasProperty("_BaseMap")) {
                dm.SetTexture("_BaseMap", sw);
                dm.SetTextureScale("_BaseMap", new Vector2(len / 4f, 4f));
            }
            dm.mainTexture = sw;
            dm.mainTextureScale = new Vector2(len / 4f, 4f);
        }
        var dr = deck.GetComponent<Renderer>();
        dr.material = dm;
        dr.receiveShadows = true;
    }

    // ---------------------------------------------------------------- placement

    struct PlaceOpts {
        public bool Collide;
        public bool IsBuilding;
        public float Footprint;     // 0 = no footprint fill
        public float YOff;
        public float Mul;
    }

    /// <summary>
    /// The single placement routine, ported from v2's placeProp. Everything on the street goes
    /// through here so the night grading, footprint fill and collider rules stay consistent.
    /// </summary>
    GameObject PlaceProp(string key, float x, float z, float rotDeg, PlaceOpts o) {
        var cat = LevelData.Find(key);
        if (cat == null) { Debug.LogWarning($"[LevelBuilder] unknown catalogue key '{key}'"); return null; }

        var prefab = ModelCache.Load(cat.ResourcePath);
        if (prefab == null) return null;                   // optional asset that was never generated

        float mul = o.Mul <= 0f ? 1f : o.Mul;

        var holder = new GameObject(key);
        holder.transform.SetParent(_root.transform, false);
        // Stand on whatever is actually underneath: the sidewalk slabs are 0.18 m proud of the
        // road, so a prop resting on y=0 in the middle of a block is buried to the ankles.
        holder.transform.position = new Vector3(x, SurfaceHeight(x, z) + cat.YOffset + o.YOff, z);
        holder.transform.rotation = Quaternion.Euler(0f, rotDeg, 0f);

        var obj = Instantiate(prefab, holder.transform);
        ModelCache.StripColliders(obj);
        ModelCache.NormalizeHeight(obj, cat.Height * mul);

        if (o.IsBuilding) {
            GradeMaterials(obj, BuildingAlbedo, 0.75f, 0.15f);
            // Fill the block: widen toward the block size, capped so facades do not smear.
            if (o.Footprint > 0f && ModelCache.LocalBounds(obj, out var bb)) {
                float want = o.Footprint - 6f;
                var sc = obj.transform.localScale;
                sc.x *= Mathf.Clamp(want / Mathf.Max(bb.size.x, 1f), 1f, 1.45f);
                sc.z *= Mathf.Clamp(want / Mathf.Max(bb.size.z, 1f), 1f, 1.45f);
                obj.transform.localScale = sc;
            }
        } else if (key == "tree" || key == "planter") {
            // Foliage bakes arrive in bright daylight green; pull it into the night palette.
            TintMaterials(obj, new Color(0.16f, 0.24f, 0.20f), 0.95f, 0f);
        } else if (IsVehicle(key)) {
            GradeMaterials(obj, VehicleAlbedo, 0.45f, 0.35f, clampOnly: true);
        }

        foreach (var r in obj.GetComponentsInChildren<Renderer>()) {
            r.shadowCastingMode = o.IsBuilding
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = true;
        }

        if (o.Collide) AddCollider(holder, cat, o);
        return holder;
    }

    /// <summary>
    /// Height of the walkable surface at a point: the sidewalk slab top on a block, else the road.
    /// Buildings are excluded by the caller - they are placed on the road grid and rise from it.
    /// </summary>
    float SurfaceHeight(float x, float z) {
        const float half = (LevelData.Block + 3f) * 0.5f;
        for (int gx = -2; gx <= 2; gx++) {
            for (int gz = -2; gz <= 2; gz++) {
                float cx = gx * LevelData.Pitch, cz = gz * LevelData.Pitch;
                if (!OnLand(cx + LevelData.Block / 2f)) continue;
                if (Mathf.Abs(x - cx) <= half && Mathf.Abs(z - cz) <= half) return 0.18f;
            }
        }
        return 0f;
    }

    static bool IsVehicle(string key) =>
        key.StartsWith("car") || key.StartsWith("van") || key.StartsWith("truck")
        || key.StartsWith("limo") || key.StartsWith("mixer") || key.StartsWith("hover")
        || key.StartsWith("excavator") || key == "taxi" || key == "police_car" || key == "cta_bus";

    void AddCollider(GameObject holder, Prop cat, PlaceOpts o) {
        var rs = holder.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;
        var box = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) box.Encapsulate(rs[i].bounds);

        if (o.IsBuilding && o.Footprint > 0f) {
            // Trellis buildings carry decorative overhangs; tighten the box so the player can hug
            // a wall instead of being held off it by a canopy.
            var c = box.center;
            var s = box.size;
            s.x = Mathf.Min(s.x, o.Footprint) * 0.92f;
            s.z = Mathf.Min(s.z, o.Footprint) * 0.92f;
            box = new Bounds(c, s);
        } else if (cat.Radius > 0f) {
            // Small props: a snug square around the base so the player slips past corners.
            var c = box.center;
            box = new Bounds(new Vector3(c.x, box.size.y * 0.5f, c.z),
                             new Vector3(cat.Radius * 2f, box.size.y, cat.Radius * 2f));
        } else if (cat.Radius == 0f) {
            return;                                        // explicitly non-colliding
        }
        Arena.Add(new ArenaBox(box.min, box.max, soft: box.size.y < 1.6f));
    }

    /// <summary>
    /// Multiply albedo down and adjust roughness/metallic. Negative metallic leaves it alone.
    ///
    /// `clampOnly` is the difference between v2's two calls: buildings get roughness SET to 0.75,
    /// while vehicles get Math.max(roughness, 0.45) - a floor, not an assignment. Setting it
    /// outright turned every parked car into chrome, because their source roughness is far higher.
    /// </summary>
    static void GradeMaterials(GameObject go, float albedo, float roughness, float metallic,
                               bool clampOnly = false) {
        foreach (var r in go.GetComponentsInChildren<Renderer>()) {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++) {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) {
                    var c = m.GetColor("_BaseColor");
                    m.SetColor("_BaseColor", new Color(c.r * albedo, c.g * albedo, c.b * albedo, c.a));
                }
                // URP smoothness is the inverse of roughness.
                if (m.HasProperty("_Smoothness")) {
                    float want = 1f - roughness;
                    m.SetFloat("_Smoothness", clampOnly ? Mathf.Min(m.GetFloat("_Smoothness"), want) : want);
                }
                if (metallic >= 0f && m.HasProperty("_Metallic")) {
                    m.SetFloat("_Metallic", clampOnly ? Mathf.Min(m.GetFloat("_Metallic"), metallic) : metallic);
                }
            }
            r.materials = mats;
        }
    }

    static void TintMaterials(GameObject go, Color tint, float roughness, float metallic) {
        foreach (var r in go.GetComponentsInChildren<Renderer>()) {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++) {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - roughness);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            }
            r.materials = mats;
        }
    }

    void BuildBuildings() {
        foreach (var row in _def.Buildings) {
            if (row.Length < 4) continue;
            float gx = Num(row[0]), gz = Num(row[1]);
            float mul = row.Length > 4 ? Num(row[4]) : 1f;
            PlaceProp(Str(row[2]), gx * LevelData.Pitch, gz * LevelData.Pitch, Num(row[3]),
                      new PlaceOpts { Collide = true, IsBuilding = true, Footprint = LevelData.Block - 2f, Mul = mul });
        }
    }

    void PlaceList(object[][] rows, bool collide) {
        if (rows == null) return;
        foreach (var row in rows) {
            if (row.Length < 3) continue;
            float x = Num(row[0]), z = Num(row[1]);
            float rot = row.Length > 3 ? Num(row[3]) : 0f;
            float extra = row.Length > 4 ? Num(row[4]) : 1f;
            // v2 overloads the fifth column: large values are a yaw/roll override, not a scale.
            float mul = (extra > 10f || extra < -10f) ? 1f : extra;
            PlaceProp(Str(row[2]), x, z, rot, new PlaceOpts { Collide = collide, Mul = mul });
        }
    }

    /// <summary>
    /// Rows 3-4 of the grid, filled with towers beyond the arena and given no colliders. Without
    /// this the city simply stops at the play area and the skyline is empty sky.
    /// </summary>
    void BuildSkyline() {
        SeedFrom(_def.Name, 7919);
        string[] keys = {
            "skyscraper_016", "skyscraper_020", "skyscraper_030", "skyscraper_044",
            "skyscraper_046", "skyscraper_048", "residential_018", "residential_022",
            "industrial_020",
        };
        for (int gx = -4; gx <= 4; gx++) {
            for (int gz = -4; gz <= 4; gz++) {
                if (Mathf.Max(Mathf.Abs(gx), Mathf.Abs(gz)) < 3) continue;
                if (Rnd() < 0.15f) continue;                      // gaps read as streets and lots
                float x = gx * LevelData.Pitch + (Rnd() - 0.5f) * 6f;
                if (_def.Lake != null && x > _def.Lake.X) continue;   // no towers in the lake
                float z = gz * LevelData.Pitch + (Rnd() - 0.5f) * 6f;
                bool far = Mathf.Max(Mathf.Abs(gx), Mathf.Abs(gz)) == 4;
                float mul = far ? 1.3f + Rnd() * 0.9f : 1f + Rnd() * 0.4f;
                int k = Mathf.Clamp(Mathf.FloorToInt(Rnd() * keys.Length), 0, keys.Length - 1);
                PlaceProp(keys[k], x, z, Mathf.Floor(Rnd() * 4f) * 90f,
                          new PlaceOpts { Collide = false, IsBuilding = true, Mul = mul });
            }
        }
    }

    /// <summary>
    /// The mothership. v2 hangs it at (0, 150, -190) at 110 m tall, heavily emissive, with a
    /// pulsing core beneath feeding the bloom - it is most of what fills v2's sky, and its absence
    /// is why v3's sky measured flat next to it.
    ///
    /// v2 also switches fog off on its materials. URP has no per-material fog toggle, so instead
    /// the emission is pushed hard enough to punch through the haze at that range.
    /// </summary>
    void BuildMothership() {
        var holder = new GameObject("Mothership");
        holder.transform.SetParent(_root.transform, false);
        holder.transform.position = new Vector3(0f, 150f, -190f);
        holder.AddComponent<SlowSpin>();

        var prefab = ModelCache.Load("Models/props/mothership");
        if (prefab != null) {
            var obj = Instantiate(prefab, holder.transform);
            ModelCache.StripColliders(obj);
            ModelCache.NormalizeHeight(obj, 110f);
            if (ModelCache.LocalBounds(obj, out var bb)) {
                var lp = obj.transform.localPosition;
                lp.y -= bb.size.y * 0.5f;                 // centre the hull on the holder
                obj.transform.localPosition = lp;
            }
            foreach (var r in obj.GetComponentsInChildren<Renderer>()) {
                var mats = r.materials;
                for (int i = 0; i < mats.Length; i++) {
                    var m = mats[i];
                    if (m == null) continue;
                    // URP skips emission entirely unless the material says it is not black. A
                    // material built at runtime defaults to EmissiveIsBlack, which is why the hull
                    // rendered as dark grey however high the emission colour was set.
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    var map = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                    if (map != null && m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", map);
                    // The emission map is the albedo, which is dark, so the colour carries the
                    // brightness; the map only supplies the panel detail.
                    m.SetColor("_EmissionColor", new Color(1f, 0.19f, 0.82f) * 7f);
                    if (m.HasProperty("_BaseColor")) {
                        var c = m.GetColor("_BaseColor");
                        m.SetColor("_BaseColor", new Color(c.r * 1.2f, c.g * 1.2f, c.b * 1.2f, c.a));
                    }
                }
                r.materials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        // The core: a small unlit sphere under the hull that the bloom latches onto.
        var core = Prim.Create(PrimKind.Sphere, "Core", holder.transform);
        core.transform.localPosition = new Vector3(0f, -14f, 0f);
        core.transform.localScale = Vector3.one * 18f;
        core.GetComponent<Renderer>().material = Fx.AdditiveTinted(new Color(1f, 0.25f, 0.88f) * 2.5f);

        var glowGo = new GameObject("CoreGlow");
        glowGo.transform.SetParent(holder.transform, false);
        glowGo.transform.localPosition = new Vector3(0f, -30f, 0f);
        var glow = glowGo.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = new Color(1f, 0.25f, 0.88f);
        glow.range = 170f;
        glow.intensity = 6f;
        glow.shadows = LightShadows.None;
    }

    // ---------------------------------------------------------------- street dressing

    /// <summary>
    /// Sidewalk furniture, intersection signals, kerbside parking, crash sites, the central plaza
    /// and the alleys. Port of v2's dressCity - this is the bulk of what makes the blocks read as
    /// a city, and the placement lists in LevelData deliberately leave it to code.
    /// </summary>
    void DressCity() {
        SeedFrom(_def.Name, 131);

        const float B = LevelData.Block / 2f;               // 17
        float[] streets = { -1.5f, -0.5f, 0.5f, 1.5f };
        float[] blocks = { -2f, -1f, 0f, 1f, 2f };
        for (int i = 0; i < streets.Length; i++) streets[i] *= LevelData.Pitch;
        for (int i = 0; i < blocks.Length; i++) blocks[i] *= LevelData.Pitch;

        void Put(string key, float x, float z, float rot, float mul = 1f) {
            if (!OnLand(x + 3f)) return;
            PlaceProp(key, x, z, rot, new PlaceOpts { Collide = true, Mul = mul });
        }

        // -- Sidewalks: a tree line near the kerb, benches and planters between, bins at corners.
        foreach (var cx in blocks) {
            foreach (var cz in blocks) {
                bool inPark = Mathf.Approximately(cx, 0f) && Mathf.Approximately(cz, 0f);
                if (!OnLand(cx + B + 2f) || inPark) continue;

                foreach (var (nx, nz) in new[] { (0f, 1f), (0f, -1f), (1f, 0f), (-1f, 0f) }) {
                    float tx = -nz, tz = nx;
                    float ex = cx + nx * 16.6f, ez = cz + nz * 16.6f;
                    foreach (var t in new[] { -10.5f, 10.5f }) {
                        float x = ex + tx * t, z = ez + tz * t;
                        if (Mathf.Abs(x) > 82f || Mathf.Abs(z) > 82f) continue;
                        Put("tree", x, z, Rnd() * 360f, 0.9f + Rnd() * 0.25f);
                    }
                    float facing = Mathf.Atan2(nx, nz) * Mathf.Rad2Deg;
                    Put("bench", ex + tx * -5f, ez + tz * -5f, facing);
                    Put(Rnd() < 0.5f ? "planter" : "bench", ex + tx * 5f, ez + tz * 5f, facing);
                    if (Rnd() < 0.5f) Put("planter", ex, ez, facing);
                    if (Rnd() < 0.35f) Put("trash_can", ex + tx * 14.2f, ez + tz * 14.2f, facing);
                }
            }
        }

        // -- Intersections: a signal on each corner facing the crossing, plus a street cluster.
        foreach (var sx in streets) {
            foreach (var sz in streets) {
                if (!OnLand(sx + 8f)) continue;
                int i = 0;
                foreach (var (dx, dz) in new[] { (1f, 1f), (-1f, 1f), (-1f, -1f), (1f, -1f) }) {
                    float x = sx + dx * 6.4f, z = sz + dz * 6.4f;
                    float rot = Mathf.Atan2(sx - x, sz - z) * Mathf.Rad2Deg;   // face the centre
                    Put("traffic_light", x, z, rot);
                    if (i == 0) Put("hydrant", x + dx * 1.6f, z - dz * 0.8f, rot);
                    if (i == 2) {
                        Put("mailbox", x + dx * 1.5f, z + dz * 0.4f, rot);
                        Put("news_box", x - dx * 0.2f, z + dz * 1.8f, rot);
                        Put("trash_can", x + dx * 1.9f, z + dz * 2.0f, rot);
                    }
                    i++;
                }
            }
        }

        // -- Parking: cars along both kerbs of every street segment, oriented with the street.
        string[] parked = { "car_001", "car_003", "taxi", "van_009", "car_003", "limo_014", "taxi", "hover_021", "car_001" };
        foreach (var c in streets) {
            foreach (var blk in blocks) {
                for (int axis = 0; axis < 2; axis++) {
                    for (float a = blk - 10f; a <= blk + 10f; a += 8.5f) {
                        foreach (var kerb in new[] { -1f, 1f }) {
                            if (Rnd() > 0.38f) continue;
                            float off = c + kerb * 3.0f;
                            float x = axis == 0 ? off : a;
                            float z = axis == 0 ? a : off;
                            if (Mathf.Abs(x) < 20f && Mathf.Abs(z) < 20f) continue;  // keep the plaza clear
                            if (Mathf.Abs(x) > 84f || Mathf.Abs(z) > 84f) continue;
                            bool wreck = Rnd() < 0.12f;
                            int k = Mathf.Clamp(Mathf.FloorToInt(Rnd() * parked.Length), 0, parked.Length - 1);
                            string key = wreck ? "car_wreck" : parked[k];
                            // Parked cars point along the street; direction flips with the kerb.
                            float rot = (axis == 0 ? 0f : 90f) + (kerb > 0f ? 0f : 180f) + (Rnd() - 0.5f) * 6f;
                            Put(key, x, z, rot);
                            if (wreck && _wreckFires.Count < 6) _wreckFires.Add(new Vector3(x, 0f, z));
                        }
                    }
                }
            }
        }

        // -- Crash sites: a pile-up at a couple of intersections, doubling as cover.
        var crashes = _def.Crashes != null && _def.Crashes.Length > 0
            ? _def.Crashes : new object[][] { new object[] { -22f, -22f }, new object[] { 22f, 22f } };
        foreach (var row in crashes) {
            if (row.Length < 2) continue;
            float x = Num(row[0]), z = Num(row[1]);
            if (!OnLand(x + 8f)) continue;
            Put("cta_bus", x + 1f, z - 1f, 35f + Rnd() * 20f);
            Put("car_wreck", x - 6f, z + 5f, 110f + Rnd() * 30f);
            Put("car_wreck", x + 7f, z + 6f, 200f + Rnd() * 30f);
            Put("police_car", x - 7f, z - 6f, 320f + Rnd() * 20f);
            Put("cones", x + 5f, z - 7f, Rnd() * 360f);
            Put("cones", x - 3f, z + 9f, Rnd() * 360f);
            Put("rubble", x + 9f, z - 3f, Rnd() * 360f);
        }

        // -- The park: the centre block is a plaza with a tree ring and benches facing in.
        if (_def.Lake == null || OnLand(B + 2f)) {
            for (int k = 0; k < 8; k++) {
                float a = k * Mathf.PI / 4f + Mathf.PI / 8f;
                Put("tree", Mathf.Cos(a) * 12.5f, Mathf.Sin(a) * 12.5f, Rnd() * 360f, 1.0f + Rnd() * 0.2f);
            }
            for (int k = 0; k < 6; k++) {
                float a = k * Mathf.PI / 3f;
                Put("planter", Mathf.Cos(a) * 8.5f, Mathf.Sin(a) * 8.5f, a * Mathf.Rad2Deg + 90f);
            }
            for (int k = 0; k < 4; k++) {
                float a = k * Mathf.PI / 2f + Mathf.PI / 4f;
                Put("bench", Mathf.Cos(a) * 10.5f, Mathf.Sin(a) * 10.5f,
                    Mathf.Atan2(-Mathf.Cos(a), -Mathf.Sin(a)) * Mathf.Rad2Deg);
            }
            for (int k = 0; k < 4; k++) {
                float a = k * Mathf.PI / 2f;
                Put("trash_can", Mathf.Cos(a) * 15.5f, Mathf.Sin(a) * 15.5f, 0f);
            }
            Put("hotdog_cart", 14.5f, -14.5f, 225f);
            Put("hotdog_cart", -14.5f, 14.5f, 45f);
        }

        // -- Alleys: a dumpster tucked against each block's back corner, sometimes pallets.
        foreach (var gxf in blocks) {
            foreach (var gzf in blocks) {
                bool inPark = Mathf.Approximately(gxf, 0f) && Mathf.Approximately(gzf, 0f);
                if (inPark || !OnLand(gxf + B + 2f)) continue;
                float sx = Mathf.Approximately(gxf, 0f) ? (Rnd() < 0.5f ? 1f : -1f) : Mathf.Sign(gxf);
                float sz = Mathf.Approximately(gzf, 0f) ? (Rnd() < 0.5f ? 1f : -1f) : Mathf.Sign(gzf);
                Put("dumpster", gxf + sx * 15.3f, gzf + sz * 12.5f, sx > 0f ? 90f : 270f);
                if (Rnd() < 0.5f) Put("pallets", gxf + sx * 15.3f, gzf + sz * 9.8f, Rnd() * 40f);
            }
        }
    }

    // ---------------------------------------------------------------- dressing lights

    /// <summary>Neon on the inner-ring building faces. Emissive quads plus a light that spills.</summary>
    void BuildSigns() {
        var decal = Resources.Load<Texture2D>("Decals/neon");
        foreach (var row in _def.Signs) {
            if (row.Length < 7) continue;
            float x = Num(row[0]), z = Num(row[1]), rot = Num(row[2]);
            float w = Num(row[4]), h = Num(row[5]);
            int color = Mathf.RoundToInt(Num(row[6]));

            var go = Prim.Create(PrimKind.Quad, "Neon", _root.transform);
            go.transform.position = new Vector3(x, 7f + h * 0.5f, z);
            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
            go.transform.localScale = new Vector3(w, h, 1f);

            var mat = Fx.AdditiveTinted(Col(color) * 1.6f);
            if (decal != null) {
                mat.mainTexture = decal;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", decal);
            }
            go.GetComponent<Renderer>().material = mat;

            var lightGo = new GameObject("NeonLight");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, -0.5f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Col(color);
            light.range = 18f;
            light.intensity = 2.5f;
            light.shadows = LightShadows.None;
        }
    }

    /// <summary>Abduction beams: tall additive columns dropping from the mothership onto the far city.</summary>
    void BuildBeams() {
        if (_def.Beams == null) return;
        foreach (var row in _def.Beams) {
            if (row.Length < 2) continue;
            float x = Num(row[0]), z = Num(row[1]);

            var go = Prim.Create(PrimKind.Cylinder, "Beam", _root.transform);
            go.transform.position = new Vector3(x, 80f, z);
            go.transform.localScale = new Vector3(9f, 80f, 9f);   // Unity cylinder is 2 units tall
            var mat = Fx.AdditiveTinted(new Color(0.25f, 0.75f, 1f) * 1.6f);
            var c = mat.color; c.a = 0.10f;
            mat.color = c;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            go.GetComponent<Renderer>().material = mat;
        }
    }

    /// <summary>
    /// Street lamps at alternating block corners. At night the pool of light does more for the
    /// street than the pole does, so the light is the point and the geometry is minimal.
    /// </summary>
    void BuildStreetLamps() {
        var poleMat = Prim.Lit();
        if (poleMat.HasProperty("_BaseColor")) poleMat.SetColor("_BaseColor", new Color(0.18f, 0.20f, 0.22f));
        if (poleMat.HasProperty("_Metallic")) poleMat.SetFloat("_Metallic", 0.7f);
        var lampMat = Fx.AdditiveTinted(new Color(1f, 0.88f, 0.63f) * 2.2f);

        for (int gx = -2; gx <= 2; gx++) {
            for (int gz = -2; gz <= 2; gz++) {
                // Every corner, not every other one: 13 lamps over 25 blocks left most of the
                // grid lit only by ambient, which is why the road kept measuring dark.
                _ = gx;
                float x = gx * LevelData.Pitch + LevelData.Pitch / 2f;
                float z = gz * LevelData.Pitch + LevelData.Pitch / 2f;
                if (!OnLand(x)) continue;

                var lightGo = new GameObject("StreetLamp");
                lightGo.transform.SetParent(_root.transform, false);
                lightGo.transform.position = new Vector3(x, 6.6f, z);
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.91f, 0.69f);
                l.range = 34f;
                // At night the street is lit by lamps, not the moon: this is most of the gap
                // between v3's road and v2's reference reading.
                l.intensity = 6.5f;
                l.shadows = LightShadows.None;

                var pole = Prim.Create(PrimKind.Cylinder, "LampPole", _root.transform);
                pole.transform.position = new Vector3(x - 1.2f, 3.5f, z);
                pole.transform.localScale = new Vector3(0.22f, 3.5f, 0.22f);
                pole.GetComponent<Renderer>().material = poleMat;

                var head = Prim.Create(PrimKind.Cube, "LampHead", _root.transform);
                head.transform.position = new Vector3(x, 6.8f, z);
                head.transform.localScale = new Vector3(0.7f, 0.18f, 0.32f);
                head.GetComponent<Renderer>().material = lampMat;
            }
        }
    }

    void BuildFires() {
        var spots = new List<Vector3>();
        foreach (var row in _def.Fires) if (row.Length >= 2) spots.Add(new Vector3(Num(row[0]), 0f, Num(row[1])));
        spots.AddRange(_wreckFires);

        foreach (var s in spots) {
            var go = new GameObject("Fire");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(s.x, SurfaceHeight(s.x, s.z) + 0.9f, s.z);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.44f, 0.13f);
            light.range = 22f;
            light.intensity = 3.2f;
            light.shadows = LightShadows.None;
            go.AddComponent<FlickerLight>();

            // The light alone was never the effect - a burning wreck needs flame, smoke and embers.
            var fire = go.AddComponent<FireEmitter>();
            fire.Scale = 1f;
        }
    }

    /// <summary>
    /// Street intersections on the outer ring, which are always on asphalt, inside the arena and
    /// never inside a building footprint. WaveManager scores these by distance, so what matters is
    /// that they are real places on the map rather than points on an abstract circle.
    /// </summary>
    void BuildSpawnPoints() {
        _spawnPoints.Clear();
        float[] lines = { -1.5f, -0.5f, 0.5f, 1.5f };
        foreach (var kx in lines) {
            foreach (var kz in lines) {
                float x = kx * LevelData.Pitch, z = kz * LevelData.Pitch;
                if (!OnLand(x)) continue;
                if (Arena.PointInSolid(new Vector3(x, 0.5f, z))) continue;
                _spawnPoints.Add(new Vector3(x, 0f, z));
            }
        }
        if (_spawnPoints.Count < 4) {
            float r = _def.ArenaRadius * 0.8f;
            for (int i = 0; i < 8; i++) {
                float a = i / 8f * Mathf.PI * 2f;
                _spawnPoints.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
        }
    }

    public Vector3 PlayerStart => Vector3.zero;

    /// <summary>
    /// Face north on spawn. The mothership hangs at z = -190 and is the single biggest thing in
    /// the sky; spawning with your back to it threw away the whole establishing shot.
    /// </summary>
    public float PlayerStartYaw => 180f;

    // ---------------------------------------------------------------- diagnostics

    /// <summary>
    /// One line per level build, into the browser console. Screenshots tell you something looks
    /// wrong; this tells you whether the geometry is wrong or the lighting is.
    /// </summary>
    void LogSummary() {
        int renderers = _root.GetComponentsInChildren<Renderer>().Length;
        Debug.Log($"[UFO] level={_def.Name} objects={_root.transform.childCount} renderers={renderers} " +
                  $"colliders={Arena.Boxes.Count} spawns={_spawnPoints.Count} " +
                  $"lights={_root.GetComponentsInChildren<Light>().Length} " +
                  $"ambientSky={RenderSettings.ambientSkyColor} sun={(Sun != null ? Sun.intensity : -1f):F2}");
    }
}

/// <summary>Cheap fire flicker. Deterministic enough to look alive, cheap enough to have ten.</summary>
public class FlickerLight : MonoBehaviour {
    Light _light;
    float _base, _seed;

    void Awake() {
        _light = GetComponent<Light>();
        _base = _light.intensity;
        _seed = Random.value * 100f;
    }

    void Update() {
        float t = Time.time + _seed;
        float n = Mathf.PerlinNoise(t * 4.5f, 0f) * 0.6f + Mathf.PerlinNoise(t * 11f, 5f) * 0.4f;
        _light.intensity = _base * (0.65f + n * 0.7f);
    }
}

}
