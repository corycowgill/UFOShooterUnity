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
    const float AmbientScale = 0.60f;

    /// <summary>
    /// Calibration hook: a negative value means "use the constant". Exists so the ambient level can
    /// be swept and measured in one run rather than guessed at across several rebuilds.
    /// </summary>
    public static float AmbientScaleOverride = -1f;
    static float Ambient => AmbientScaleOverride >= 0f ? AmbientScaleOverride : AmbientScale;
    const float SunScale = 0.60f;

    // Trellis bakes daylight into the albedo. v2 pulls buildings to 0.42 and vehicles to 0.6 so
    // towers read as night-lit concrete and glass instead of white blocks in the dark.
    const float BuildingAlbedo = 0.42f;
    const float VehicleAlbedo = 0.60f;

    GameObject _root;

    CityProps _props;

    /// <summary>A placed building's roof, as the rooftop dressing pass sees it.</summary>
    struct RoofSite {
        public Vector3 Center;     // world XZ of the footprint centre
        public Vector2 Half;       // footprint half extents
        public float Y;            // the top of the model
        public bool Boxy;          // low-rise: the model really is a box to the top
    }
    readonly List<RoofSite> _roofs = new List<RoofSite>();
    readonly List<RoofSite> _skylineRoofs = new List<RoofSite>();
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
        _roofs.Clear();
        _skylineRoofs.Clear();
        _props = null;
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

        // Everything procedural shares one CityProps, so the level's roof clutter, wires, craters
        // and puddles collapse into a handful of meshes however many of them get placed.
        _props = new CityProps(_root.transform, Arena, _def.Name.Length * 977 + 13);

        BuildSkyline();
        BuildMothership();
        BuildBuildings();
        PlaceList(_def.Vehicles, collide: true);
        PlaceList(_def.Landmarks, collide: true);
        PlaceList(_def.Props, collide: true);
        PlaceList(_def.Deco, collide: false);
        DressCity();

        DressRooftops();
        DressOverhead();
        DressStreetDetail();
        DressBattleDamage();
        if (_def.Lake != null) DressWaterfront();

        BuildSigns();
        BuildBeams();
        BuildStreetLamps();
        BuildFires();
        BuildSpawnPoints();

        _props.Flush();

        // A dressed block is several hundred small static meshes. Combining collapses the draw
        // calls, which matters far more on WebGL than in the editor.
        // Combine only the static dressing. The mothership spins, so batching it would bake its
        // transform and freeze it in place.
        foreach (Transform child in _root.transform)
            if (child.GetComponent<SlowSpin>() == null && child.GetComponent<NoBatch>() == null
                && child.name != "Mothership")
                StaticBatchingUtility.Combine(child.gameObject);

        LogSummary();
    }

    // ---------------------------------------------------------------- lighting

    void BuildLighting() {
        var L = _def;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = Col(L.HemiSky) * (L.HemiIntensity * Ambient);
        RenderSettings.ambientEquatorColor = Col(L.Ambient) * (L.AmbientIntensity * Ambient);
        RenderSettings.ambientGroundColor = Col(L.HemiGround) * (L.HemiIntensity * Ambient);

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

        // ---- cloud deck and the glow of a city burning past the horizon ----------------------
        //
        // The sky was a clean vertical gradient with stars in it, which is exactly as much sky as a
        // colour picker gives you: from the ground it read as an empty violet field over the whole
        // top half of the frame. A broken cloud deck low down gives the fog something to sit under
        // and gives the mothership something to hang in front of.
        //
        // The noise is sampled from a 256x128 fBm field rather than evaluated per texel. Four
        // octaves across two million texels is a visible hitch on every level load, and stretched
        // over a sky nobody can tell the difference.
        const int NW = 256, NH = 128;
        var field = new float[NW * NH];
        for (int y = 0; y < NH; y++) {
            for (int x = 0; x < NW; x++) {
                float u = x / (float)NW * 13f, v = y / (float)NH * 6f;
                float n = 0f, amp = 0.55f, f = 1f;
                for (int o = 0; o < 5; o++) {
                    n += Mathf.PerlinNoise(u * f + 13.7f, v * f + 4.1f) * amp;
                    amp *= 0.5f; f *= 2.13f;
                }
                field[y * NW + x] = n;
            }
        }

        // The cloud has to sit a little above the sky it covers and no more. At 2.4x the glow
        // colour the deck came back as a sheet of orange lava filling the upper half of the frame
        // and fighting the mothership for the eye; at 1.2x it is weather.
        var cloudCol = Color.Lerp(_def.SkyHorizon, _def.SkyGlow, 0.4f) * 1.2f;
        for (int y = 0; y < H; y++) {
            float t = y / (float)(H - 1);
            // A band starting just above the horizon and thinning out by the zenith. It has to reach
            // well up the dome: from the ground, the strip of sky a player actually sees between the
            // rooflines starts around 20 degrees of elevation, which is already t = 0.6.
            float band = Mathf.Clamp01(Mathf.InverseLerp(0.48f, 0.56f, t))
                       * Mathf.Clamp01(Mathf.InverseLerp(1.0f, 0.70f, t));
            // The horizon glow: the rest of the city, on fire, below the cloud.
            float glow = Mathf.Clamp01(Mathf.InverseLerp(0.40f, 0.505f, t))
                       * Mathf.Clamp01(Mathf.InverseLerp(0.58f, 0.515f, t));
            if (band <= 0.001f && glow <= 0.001f) continue;

            for (int x = 0; x < W; x++) {
                int i = y * W + x;
                if (glow > 0.001f) {
                    // Two broad lobes rather than an even ring: an evenly lit horizon is a haze bug,
                    // an uneven one is a fire.
                    float az = x / (float)W * Mathf.PI * 2f;
                    float lobe = Mathf.Max(0f, Mathf.Sin(az * 1f + 0.9f)) * 0.7f
                               + Mathf.Max(0f, Mathf.Sin(az * 3f + 2.2f)) * 0.3f;
                    px[i] += _def.SkyGlow * (glow * lobe * 0.38f);
                }
                if (band > 0.001f) {
                    float n = SampleField(field, NW, NH, x / (float)W, t);
                    // A hard-ish threshold, so the deck has edges. A gentle ramp over the whole
                    // field just brightens the band evenly and reads as haze, not cloud.
                    float d = Mathf.Clamp01((n - 0.47f) * 4.5f) * band;
                    if (d > 0.001f) px[i] = Color.Lerp(px[i], cloudCol, Mathf.Min(0.62f, d));
                }
            }
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

    /// <summary>Bilinear sample of the low-resolution cloud field, wrapping in azimuth.</summary>
    static float SampleField(float[] field, int nw, int nh, float u, float v) {
        float fx = u * nw, fy = Mathf.Clamp01(v) * (nh - 1);
        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, nh - 1);
        int x1 = (x0 + 1) % nw, y1 = Mathf.Min(y0 + 1, nh - 1);
        x0 = ((x0 % nw) + nw) % nw;
        float tx = fx - Mathf.Floor(fx), ty = fy - y0;
        float a = Mathf.Lerp(field[y0 * nw + x0], field[y0 * nw + x1], tx);
        float b = Mathf.Lerp(field[y1 * nw + x0], field[y1 * nw + x1], tx);
        return Mathf.Lerp(a, b, ty);
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
            // 0.84 was a daylight value. Under the street lamps it turned every block into a sheet
            // of near-white that took the bottom third of every frame and pulled the eye down out
            // of the fight; the road beside it sits at 0.16-0.20.
            if (walkMat.HasProperty("_BaseColor")) walkMat.SetColor("_BaseColor", new Color(0.62f, 0.62f, 0.60f));
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
                    soft: true, walkable: true));
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

        // The pier deck is the one other surface you stand on rather than walk around.
        Arena.Add(new ArenaBox(
            new Vector3(lake.X, 0f, lake.PierZ0),
            new Vector3(lake.PierXEnd, 0.2f, lake.PierZ1),
            soft: true, walkable: true));
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
            // A tint per building on top of the shared night albedo. The catalogue has fourteen
            // building models for a hundred-odd placements, so the same facade appears five or six
            // times in one frame; giving each one a stone, concrete, brick or glass cast is what
            // stops a block reading as the same asset repeated down the street.
            GradeMaterials(obj, BuildingAlbedo, 0.75f, 0.15f, tint: BuildingTint());
            LightWindows(obj);
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

        // Ground the prop, but only where it stands on paving.
        //
        // Buildings are excluded because a blob under a 30 m tower is nonsense. The road is
        // excluded for a subtler reason: the patch is a fixed dark tone, and the two surfaces it
        // could land on are three stops apart. A tone dark enough to shade the pale sidewalk is
        // *lighter* than asphalt, so a parked car on the road got a pale halo instead of a shadow.
        // The sidewalk is also where it matters - asphalt is dark enough that props read as
        // grounded on it already.
        if (!o.IsBuilding && _props != null && SurfaceHeight(x, z) > 0f
            && ModelCache.WorldBounds(obj, out var cb)) {
            // Capped: the patch is meant to read as the shading under a bench or a car, and a
            // twenty-metre blob under a bus is a stain on the street rather than a contact shadow.
            float r = Mathf.Min(4.5f, Mathf.Max(cb.extents.x, cb.extents.z) * 1.15f);
            if (r > 0.25f) _props.Contact(new Vector3(x, SurfaceHeight(x, z), z), r, rotDeg);
        }

        // Marker lights on anything with wheels. A kerb lined with dark car-shaped lumps is the
        // one place a night street loses all its sparkle, and four 13 cm glows per vehicle put it
        // back for a handful of triangles in a mesh that was already being drawn.
        if (IsVehicle(key) && _props != null && ModelCache.WorldBounds(obj, out var vb)) {
            _props.VehicleLights(new Vector3(x, SurfaceHeight(x, z), z), rotDeg, vb.extents);
        }
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

    /// <summary>
    /// Make the bright parts of a building's own texture emit. Windows light up; walls do not,
    /// because their albedo is already dark. Bloom then picks the windows out against the night.
    /// </summary>
    void LightWindows(GameObject go) {
        // One colour and one level per building, not per level. Every tower emitting the same warm
        // white made a night skyline of twenty buildings read as one repeated asset; a mix of warm
        // sodium, cold fluorescent and the occasional evacuated, mostly dark block reads as a city.
        float pick = Rnd();
        var tint = pick < 0.5f ? new Color(1f, 0.90f, 0.70f)          // warm office / sodium
                 : pick < 0.82f ? new Color(0.70f, 0.83f, 1f)         // cold fluorescent
                                : new Color(0.58f, 1f, 0.84f);        // green-shifted
        bool evacuated = Rnd() < 0.18f;
        float strength = evacuated ? 0.10f : 0.35f + Rnd() * 0.55f;

        foreach (var r in go.GetComponentsInChildren<Renderer>()) {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++) {
                var m = mats[i];
                if (m == null || !m.HasProperty("_EmissionMap")) continue;
                var map = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                if (map == null) continue;

                m.EnableKeyword("_EMISSION");
                // Runtime materials default to EmissiveIsBlack, which makes URP skip emission.
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetTexture("_EmissionMap", map);
                m.SetColor("_EmissionColor", tint * strength);
            }
            r.materials = mats;
        }
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
        } else if (cat.Key == "tree") {
            // The catalogue's 0.45 m is a sapling's trunk. This model is a multi-stemmed tree whose
            // trunk cluster measures roughly a quarter of its canopy, so at 0.45 the player stands
            // *inside* the wood: a wall of bark fills the frame with nothing there to walk around.
            // Sizing the collider off the model instead makes the tree an obstacle you can see.
            float r = Mathf.Max(cat.Radius, Mathf.Min(box.size.x, box.size.z) * 0.13f);
            var c = box.center;
            box = new Bounds(new Vector3(c.x, box.size.y * 0.5f, c.z),
                             new Vector3(r * 2f, box.size.y, r * 2f));
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
    // Muted casts only. Anything saturated stops reading as a material and starts reading as a
    // coloured light on a white building.
    static readonly Color[] BuildingTints = {
        new Color(1.00f, 0.99f, 0.96f),   // pale concrete
        new Color(1.00f, 0.91f, 0.80f),   // warm limestone
        new Color(0.94f, 0.82f, 0.74f),   // brick
        new Color(0.82f, 0.88f, 1.00f),   // cold glass curtain wall
        new Color(0.78f, 0.80f, 0.85f),   // dark slate
        new Color(0.88f, 0.95f, 0.94f),   // green-tinted glazing
    };

    Color BuildingTint() {
        var c = BuildingTints[Mathf.Clamp(Mathf.FloorToInt(Rnd() * BuildingTints.Length), 0, BuildingTints.Length - 1)];
        // A little extra spread in value, so two limestone towers side by side are not identical.
        float v = 0.82f + Rnd() * 0.36f;
        return new Color(c.r * v, c.g * v, c.b * v);
    }

    static void GradeMaterials(GameObject go, float albedo, float roughness, float metallic,
                               bool clampOnly = false, Color? tint = null) {
        foreach (var r in go.GetComponentsInChildren<Renderer>()) {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++) {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) {
                    var c = m.GetColor("_BaseColor");
                    var k = tint ?? Color.white;
                    m.SetColor("_BaseColor", new Color(c.r * albedo * k.r, c.g * albedo * k.g, c.b * albedo * k.b, c.a));
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
            var holder = PlaceProp(Str(row[2]), gx * LevelData.Pitch, gz * LevelData.Pitch, Num(row[3]),
                      new PlaceOpts { Collide = true, IsBuilding = true, Footprint = LevelData.Block - 2f, Mul = mul });
            Record(_roofs, holder);
        }
    }

    /// <summary>
    /// Note where a building's roof is, for the rooftop dressing pass.
    ///
    /// The height is taken from the model's own bounds rather than the catalogue entry, because
    /// PlaceProp widens buildings to fill their block and the fifth column scales some of them.
    /// </summary>
    static void Record(List<RoofSite> into, GameObject holder) {
        if (holder == null || !ModelCache.WorldBounds(holder, out var b)) return;
        into.Add(new RoofSite {
            Center = new Vector3(b.center.x, 0f, b.center.z),
            Half = new Vector2(b.extents.x, b.extents.z),
            Y = b.max.y,
            // Only the low-rises are honestly boxes to the top. The towers are terraced and
            // tapered, and anything sized to their full footprint would hang off them in mid air.
            Boxy = b.size.y < 14f,
        });
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
                var holder = PlaceProp(keys[k], x, z, Mathf.Floor(Rnd() * 4f) * 90f,
                          new PlaceOpts { Collide = false, IsBuilding = true, Mul = mul });
                Record(_skylineRoofs, holder);
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
                    // A metre back off the kerb: the tree line still reads, but the canopies stop
                    // closing over the street sightlines the player actually fights down.
                    float ex = cx + nx * 15.4f, ez = cz + nz * 15.4f;
                    foreach (var t in new[] { -10.5f, 10.5f }) {
                        float x = ex + tx * t, z = ez + tz * t;
                        if (Mathf.Abs(x) > 82f || Mathf.Abs(z) > 82f) continue;
                        // A gap in the tree line every so often. A solid rank of eight canopies per
                        // block is a hedge, and it closes the very sightlines the street is for.
                        if (Rnd() < 0.28f) continue;
                        // 0.62-0.78, down from 0.90-1.15. The tree model is a broad-canopied thing
                        // whose crown is wider than it is tall, so at the catalogue's 9 m it spans
                        // most of a 10 m street: standing on any sidewalk put a wall of leaves
                        // across the frame, and the alley and plaza-corner views were nothing else.
                        // Street trees are meant to frame the sightline, not be it.
                        Put("tree", x, z, Rnd() * 360f, 0.62f + Rnd() * 0.16f);
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
            // Six trees at 17 m rather than eight at 12.5 m, and a little smaller. The player
            // spawns in the middle of this ring and looks out through it, so the radius is really
            // a decision about how much of their first view is trunk. At 12.5 m two canopies sat
            // either side of the sightline to the mothership and ate a third of the frame.
            for (int k = 0; k < 6; k++) {
                float a = k * Mathf.PI / 3f + Mathf.PI / 6f;
                Put("tree", Mathf.Cos(a) * 17f, Mathf.Sin(a) * 17f, Rnd() * 360f, 0.66f + Rnd() * 0.14f);
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

    // ---------------------------------------------------------------- procedural dressing

    // Sign and billboard colours. Deliberately the saturated neon end of the palette: these are the
    // only warm-and-bright things in a level otherwise made of grey concrete under a violet sky.
    static readonly Color[] NeonPalette = {
        new Color(1.00f, 0.22f, 0.55f),   // magenta
        new Color(0.25f, 0.95f, 1.00f),   // cyan
        new Color(1.00f, 0.72f, 0.18f),   // amber
        new Color(0.55f, 0.35f, 1.00f),   // violet
        new Color(0.30f, 1.00f, 0.55f),   // green
    };

    Color Neon() => NeonPalette[Mathf.Clamp(Mathf.FloorToInt(Rnd() * NeonPalette.Length), 0, NeonPalette.Length - 1)];

    /// <summary>
    /// The roofline: water towers, plant, masts, billboards and a couple of sweeping searchlights.
    ///
    /// This is the pass that changes the picture the most, and the reason is geometric rather than
    /// artistic. In a first-person fight down a 10 m street the buildings occupy the sides of the
    /// frame and their tops occupy the middle, right where the eye rests - and until now those tops
    /// were bare silhouettes against an empty sky. Nothing at ground level can fix that.
    ///
    /// Everything is clustered well inside the footprint. The towers are terraced and tapered, so a
    /// prop placed at the edge of a roof's bounding box is as likely to be hanging over the street
    /// as standing on anything; only <c>Boxy</c> low-rises get a full-footprint parapet.
    /// </summary>
    void DressRooftops() {
        SeedFrom(_def.Name, 3313);

        foreach (var r in _roofs) {
            float hx = Mathf.Max(1.6f, r.Half.x * 0.42f);
            float hz = Mathf.Max(1.6f, r.Half.y * 0.42f);
            var top = new Vector3(r.Center.x, r.Y, r.Center.z);

            if (r.Boxy) _props.Parapet(r.Center, r.Half * 0.97f, r.Y);
            _props.RoofPlant(r.Center, new Vector2(hx, hz), r.Y);

            if (Rnd() < 0.6f)
                _props.WaterTower(top + Spread(hx, hz), 0.85f + Rnd() * 0.4f);

            if (r.Y > 26f && Rnd() < 0.75f)
                _props.AntennaMast(top + Spread(hx * 0.6f, hz * 0.6f), 8f + Rnd() * 12f);

            if (Rnd() < 0.3f && hx > 4f && hz > 4f)
                _props.SolarArray(top + Spread(hx * 0.5f, hz * 0.5f), Rnd() * 360f, 2);
            else if (Rnd() < 0.3f)
                _props.DishFarm(top + Spread(hx * 0.6f, hz * 0.6f), Rnd() * 360f);

            // Billboards face the plaza. One turned away from the play area is geometry nobody sees.
            if (r.Y > 10f && r.Y < 42f && Rnd() < 0.4f) {
                float yaw = Mathf.Atan2(-r.Center.x, -r.Center.z) * Mathf.Rad2Deg;
                _props.Billboard(top + Spread(hx, hz), yaw, 9f + Rnd() * 7f, 4f + Rnd() * 2.5f, Neon());
            }
        }

        // Searchlights on the three tallest roofs in the arena, and smoke off a few of the towers
        // beyond it: the invasion has to be visible from inside the block, not only overhead.
        var tall = new List<RoofSite>(_roofs);
        tall.Sort((a, b) => b.Y.CompareTo(a.Y));
        for (int i = 0; i < Mathf.Min(3, tall.Count); i++)
            _props.SearchLight(new Vector3(tall[i].Center.x, tall[i].Y, tall[i].Center.z), 14f + Rnd() * 14f);

        // One helipad per level, on the widest roof rather than the tallest - it needs the deck.
        int widest = -1;
        float bestArea = 0f;
        for (int i = 0; i < _roofs.Count; i++) {
            float area = _roofs[i].Half.x * _roofs[i].Half.y;
            if (_roofs[i].Y > 12f && area > bestArea) { bestArea = area; widest = i; }
        }
        if (widest >= 0) {
            var r = _roofs[widest];
            _props.Helipad(new Vector3(r.Center.x, r.Y + 0.02f, r.Center.z),
                           Mathf.Min(7f, Mathf.Min(r.Half.x, r.Half.y) * 0.45f));
        }

        for (int i = 0; i < _skylineRoofs.Count; i++) {
            var r = _skylineRoofs[i];
            if (Rnd() < 0.3f) _props.WaterTower(new Vector3(r.Center.x, r.Y, r.Center.z), 1.1f);
            else if (r.Y > 40f && Rnd() < 0.35f)
                _props.AntennaMast(new Vector3(r.Center.x, r.Y, r.Center.z), 10f + Rnd() * 14f);
            if (Rnd() < 0.08f)
                _props.SmokeColumn(new Vector3(r.Center.x, r.Y, r.Center.z), 95f + Rnd() * 70f, 2.4f);
        }
    }

    Vector3 Spread(float hx, float hz) => new Vector3((Rnd() - 0.5f) * 2f * hx, 0f, (Rnd() - 0.5f) * 2f * hz);

    /// <summary>
    /// Utility poles down both kerbs and the wires between them.
    ///
    /// A street in this game is a 10 m slot between two 30 m walls, and every one of them read as an
    /// empty corridor because there was nothing above head height. Wires close the gap without
    /// putting anything in it: they cross the sightline, they catch the lamps, and they cost one
    /// span of tube each.
    /// </summary>
    void DressOverhead() {
        SeedFrom(_def.Name, 6151);

        const float kerb = 4.3f, poleH = 9.4f;
        float[] streets = { -1.5f, -0.5f, 0.5f, 1.5f };
        float[] rows = { -2f, -1f, 0f, 1f, 2f };
        for (int i = 0; i < streets.Length; i++) streets[i] *= LevelData.Pitch;
        // Offset the poles a quarter block along the street rather than putting them on block
        // centres. On centres, one lands at x=0 and one at z=0 - dead on the plaza's two cardinal
        // sightlines, which is the exact axis the player spawns facing down.
        for (int i = 0; i < rows.Length; i++) rows[i] = rows[i] * LevelData.Pitch + 11f;

        foreach (var c in streets) {
            for (int axis = 0; axis < 2; axis++) {
                // axis 0: the street runs along z at x = c, so the crossarms lie along x (yaw 0).
                float yaw = axis == 0 ? 0f : 90f;
                for (int side = -1; side <= 1; side += 2) {
                    var arms = new List<Vector3>();
                    foreach (var r in rows) {
                        float ox = axis == 0 ? c + side * kerb : r;
                        float oz = axis == 0 ? r : c + side * kerb;
                        // A gap here and there, so the line reads as a real street rather than a comb.
                        if (!OnLand(ox + 1f) || Rnd() < 0.18f) { arms.Add(Vector3.zero); continue; }
                        var b = new Vector3(ox, SurfaceHeight(ox, oz), oz);
                        arms.Add(_props.UtilityPole(b, yaw, poleH));
                    }
                    for (int i = 0; i + 1 < arms.Count; i++)
                        if (arms[i] != Vector3.zero && arms[i + 1] != Vector3.zero)
                            _props.WireSpan(arms[i], arms[i + 1], yaw);
                }

                // A span straight across the road on some rows: the one that actually crosses the
                // player's sightline down the street.
                foreach (var r in rows) {
                    if (Rnd() > 0.45f) continue;
                    float ax = axis == 0 ? c - kerb : r, az = axis == 0 ? r : c - kerb;
                    float bx = axis == 0 ? c + kerb : r, bz = axis == 0 ? r : c + kerb;
                    if (!OnLand(Mathf.Max(ax, bx) + 1f)) continue;
                    var a = new Vector3(ax, SurfaceHeight(ax, az) + poleH - 1.4f, az);
                    var b = new Vector3(bx, SurfaceHeight(bx, bz) + poleH - 1.4f, bz);
                    if (Rnd() < 0.3f) _props.StreetBanner(a, b, Neon());
                    else _props.WireSpan(a, b, yaw + 90f);
                }
            }
        }
    }

    /// <summary>
    /// The things underfoot and at shoulder height: shop awnings, bollards, manholes, steam and wet
    /// patches. Individually none of them is noticed; together they are the difference between a
    /// street you walk down and a corridor between two textures.
    ///
    /// Facade positions come from the roof sites rather than the block grid, because PlaceProp
    /// widens each building toward the block and the fifth column scales some of them - the wall is
    /// wherever the model's bounds actually ended up, not at a nominal half-block.
    /// </summary>
    void DressStreetDetail() {
        SeedFrom(_def.Name, 8837);

        foreach (var r in _roofs) {
            foreach (var (nx, nz) in new[] { (0f, 1f), (0f, -1f), (1f, 0f), (-1f, 0f) }) {
                float ext = nx != 0f ? r.Half.x : r.Half.y;
                float along = nx != 0f ? r.Half.y : r.Half.x;
                // Offset along the wall so a block does not get four awnings all dead centre.
                float t = (Rnd() - 0.5f) * Mathf.Max(0f, along - 5f);
                var p = r.Center + new Vector3(nx * ext + nz * t, 0f, nz * ext + nx * t);
                if (!OnLand(p.x + 1f) || Mathf.Abs(p.x) > 84f || Mathf.Abs(p.z) > 84f) continue;
                p.y = SurfaceHeight(p.x, p.z);
                float wallYaw = Mathf.Atan2(nx, nz) * Mathf.Rad2Deg;

                // A lit ground floor on nearly every street-facing wall. This one is not dressing,
                // it is the street's light source, and a block with three dark sides puts the
                // player in a pool of black for a quarter of every turn.
                if (Rnd() < 0.85f) {
                    var faceMid = r.Center + new Vector3(nx * ext, SurfaceHeight(p.x, p.z), nz * ext);
                    _props.Storefront(faceMid, wallYaw, Mathf.Min(24f, along * 1.7f));
                }

                if (Rnd() < 0.45f) _props.Awning(p, wallYaw, 3.5f + Rnd() * 3f, Neon());

                // A fire escape on the walls of the low and mid rises. Skipped on the towers: their
                // facades step back every few floors, so a straight stack of landings would leave
                // the building behind it somewhere around the fourth.
                if (r.Y > 8f && r.Y < 26f && Rnd() < 0.45f) {
                    int floors = Mathf.Clamp(Mathf.FloorToInt((r.Y - 4.5f) / 3.2f), 2, 6);
                    _props.FireEscape(p + new Vector3(nx, 0f, nz) * 0.2f, wallYaw,
                                      2.6f + Rnd() * 1.2f, floors, 3.2f);
                }
            }
        }

        // The plaza's own paving: a border course, a quartering cross and a centre medallion.
        if (_def.Lake == null || OnLand(LevelData.Block * 0.5f + 2f))
            _props.PlazaPaving(Vector3.zero, LevelData.Block * 0.5f + 1.2f, 0.20f);

        // Bollards round the plaza: they give the open centre block an edge to read against.
        for (int side = 0; side < 4; side++) {
            float a = side * Mathf.PI / 2f;
            var n = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var t = new Vector3(-n.z, 0f, n.x);
            if (!OnLand(n.x * 16.5f + 1f)) continue;
            _props.Bollards(n * 16.5f - t * 11f + Vector3.up * 0.18f,
                            n * 16.5f - t * 4f + Vector3.up * 0.18f, 2.2f);
            _props.Bollards(n * 16.5f + t * 4f + Vector3.up * 0.18f,
                            n * 16.5f + t * 11f + Vector3.up * 0.18f, 2.2f);
        }

        // Manholes, puddles and steam, out on the road where the asphalt is otherwise bare.
        float[] streets = { -1.5f, -0.5f, 0.5f, 1.5f };
        for (int i = 0; i < streets.Length; i++) streets[i] *= LevelData.Pitch;
        // A wet level wants standing water everywhere; a dry one wants the odd patch by a hydrant.
        float puddleChance = _def.Weather == "rain" ? 0.55f : 0.16f;

        foreach (var c in streets) {
            for (float a = -88f; a <= 88f; a += 9f) {
                for (int axis = 0; axis < 2; axis++) {
                    float x = axis == 0 ? c + (Rnd() - 0.5f) * 5f : a;
                    float z = axis == 0 ? a : c + (Rnd() - 0.5f) * 5f;
                    if (!OnLand(x + 1f)) continue;
                    if (Rnd() < 0.14f) _props.Manhole(new Vector3(x, 0f, z));
                    if (Rnd() < puddleChance) _props.Puddle(new Vector3(x, 0f, z), 0.8f + Rnd() * 2.6f);
                    // Patches and cracks run with the street, skids across it.
                    float lane = axis == 0 ? 0f : 90f;
                    if (Rnd() < 0.34f) _props.RoadPatch(new Vector3(x, 0f, z), lane + (Rnd() - 0.5f) * 14f);
                    if (Rnd() < 0.10f) _props.Skid(new Vector3(x, 0f, z), lane + (Rnd() - 0.5f) * 40f, 3f + Rnd() * 7f);
                }
            }
        }

        for (int i = 0; i < 7; i++) {
            float c = streets[Mathf.Clamp(Mathf.FloorToInt(Rnd() * 4f), 0, 3)];
            float a = (Rnd() - 0.5f) * 150f;
            var p = Rnd() < 0.5f
                ? new Vector3(c + (Rnd() - 0.5f) * 4f, 0f, a)
                : new Vector3(a, 0f, c + (Rnd() - 0.5f) * 4f);
            if (!OnLand(p.x + 1f)) continue;
            _props.SteamVent(p);
        }

        // Kerbside furniture. A kerb lined with parked cars and nothing to pay, nowhere to wait for
        // a bus and nowhere to leave a bike is a street nobody ever used.
        float kerbLine = LevelData.Block * 0.5f + 1.4f;      // just inside the sidewalk edge
        foreach (var bx in new[] { -2f, -1f, 0f, 1f, 2f }) {
            foreach (var bz in new[] { -2f, -1f, 0f, 1f, 2f }) {
                float cx = bx * LevelData.Pitch, cz = bz * LevelData.Pitch;
                if (Mathf.Approximately(bx, 0f) && Mathf.Approximately(bz, 0f)) continue;
                foreach (var (nx, nz) in new[] { (0f, 1f), (0f, -1f), (1f, 0f), (-1f, 0f) }) {
                    float tx = -nz, tz = nx;
                    float ex = cx + nx * kerbLine, ez = cz + nz * kerbLine;
                    if (!OnLand(ex + 1f) || Mathf.Abs(ex) > 84f || Mathf.Abs(ez) > 84f) continue;
                    float facing = Mathf.Atan2(-nx, -nz) * Mathf.Rad2Deg;   // face in off the kerb

                    if (Rnd() < 0.22f)
                        PlaceProp("bus_shelter", ex + tx * 8f, ez + tz * 8f, facing,
                                  new PlaceOpts { Collide = true });
                    if (Rnd() < 0.5f)
                        _props.BikeRack(new Vector3(ex + tx * -8f, SurfaceHeight(ex, ez), ez + tz * -8f),
                                        facing + 90f, 2 + Mathf.FloorToInt(Rnd() * 3f));
                    // Meters run along the kerb between the trees, one per parking bay.
                    if (Rnd() < 0.55f)
                        for (float t = -12f; t <= 12f; t += 6f)
                            _props.ParkingMeter(new Vector3(ex + tx * t, SurfaceHeight(ex, ez), ez + tz * t), facing);
                }
            }
        }

        // Litter and blast chips, spread over the paving. Weighted onto the block the player spends
        // most of the game standing on: an empty plaza floor is the single largest flat area in the
        // frame from the spawn point, and it had nothing on it at all.
        float[] blocks = { -2f, -1f, 0f, 1f, 2f };
        for (int i = 0; i < blocks.Length; i++) blocks[i] *= LevelData.Pitch;
        foreach (var bx in blocks) {
            foreach (var bz in blocks) {
                if (!OnLand(bx + LevelData.Block * 0.5f)) continue;
                bool plaza = Mathf.Approximately(bx, 0f) && Mathf.Approximately(bz, 0f);
                var c = new Vector3(bx, 0.18f, bz);
                _props.Debris(c, 15f, plaza ? 44 : 14, 0f);
                if (Rnd() < 0.5f)
                    _props.ScorchPatch(c + new Vector3((Rnd() - 0.5f) * 24f, 0f, (Rnd() - 0.5f) * 24f),
                                       1.2f + Rnd() * 2.2f);
                if (Rnd() < (plaza ? 1f : 0.45f))
                    _props.Puddle(c + new Vector3((Rnd() - 0.5f) * 26f, 0f, (Rnd() - 0.5f) * 26f),
                                  0.9f + Rnd() * 2.2f);
            }
        }
    }

    /// <summary>
    /// What the invasion left behind: blast craters, a defended plaza, hoarding round a collapsed
    /// lot and scaffolding on a damaged facade.
    ///
    /// The plaza emplacements are cover as well as dressing. The centre block is deliberately open -
    /// it is where the player spawns and it wants sightlines - but open and *empty* is a kill box,
    /// so the cover is waist-high and set out at 13 m, where it breaks up an approach without
    /// closing the view across the square.
    /// </summary>
    void DressBattleDamage() {
        SeedFrom(_def.Name, 4409);

        // Craters out on the street grid, never in the plaza and never inside a building.
        for (int i = 0; i < 9; i++) {
            float x = (Rnd() - 0.5f) * 170f, z = (Rnd() - 0.5f) * 170f;
            if (new Vector2(x, z).magnitude < 26f) continue;
            if (!OnLand(x + 2f)) continue;
            if (Arena.PointInSolid(new Vector3(x, 0.6f, z))) continue;
            float cr = 2.2f + Rnd() * 3.4f;
            var at = new Vector3(x, SurfaceHeight(x, z), z);
            _props.Crater(at, cr);
            _props.Debris(at, cr * 2f, 10);
            if (Rnd() < 0.55f) _props.Cordon(at, cr * 1.35f, 6);
            else {
                PlaceProp("cones", x + cr, z, Rnd() * 360f, new PlaceOpts { Collide = false });
                PlaceProp("cones", x - cr * 0.7f, z + cr * 0.8f, Rnd() * 360f, new PlaceOpts { Collide = false });
            }
        }

        // The plaza: four sandbagged emplacements on the diagonals, wired.
        if (_def.Lake == null || OnLand(LevelData.Block * 0.5f + 2f)) {
            for (int k = 0; k < 4; k++) {
                float a = k * Mathf.PI / 2f + Mathf.PI / 4f;
                var n = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var t = new Vector3(-n.z, 0f, n.x);
                var c = n * 13f;
                float facing = Mathf.Atan2(n.x, n.z) * Mathf.Rad2Deg;

                PlaceProp("sandbags", c.x - t.x * 1.6f, c.z - t.z * 1.6f, facing, new PlaceOpts { Collide = true });
                PlaceProp("sandbags", c.x + t.x * 1.6f, c.z + t.z * 1.6f, facing, new PlaceOpts { Collide = true });
                PlaceProp("barrier_jersey", (c + n * 2.2f).x, (c + n * 2.2f).z, facing, new PlaceOpts { Collide = true });
                _props.RazorWire(c + n * 3.2f - t * 3.2f + Vector3.up * 0.18f,
                                 c + n * 3.2f + t * 3.2f + Vector3.up * 0.18f, 0.42f);
            }
            // Something to look AT. The plaza is deliberately open, which left the middle distance
            // with nothing in it from any angle; a drop pod buried in its own crater gives the
            // square a subject and explains why it is barricaded, without blocking a sightline.
            _props.Crater(new Vector3(-11f, 0.18f, 12f), 4.2f);
            PlaceProp("drop_pod", -11f, 12f, 38f, new PlaceOpts { Collide = true, Mul = 1.25f });
            _props.Debris(new Vector3(-11f, 0.18f, 12f), 7f, 26);
            _props.ScorchPatch(new Vector3(-7f, 0.18f, 15f), 2.4f);
            _props.Cordon(new Vector3(-11f, 0.18f, 12f), 6.2f, 8);
        }

        // Scaffolding on one damaged facade.
        if (_roofs.Count > 0) {
            var r = _roofs[Mathf.Clamp(Mathf.FloorToInt(Rnd() * _roofs.Count), 0, _roofs.Count - 1)];
            float yaw = Mathf.Atan2(-r.Center.x, -r.Center.z) * Mathf.Rad2Deg;
            var outward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var wall = r.Center + outward * (Mathf.Abs(outward.x) > 0.5f ? r.Half.x : r.Half.y);
            if (OnLand(wall.x + 2f)) {
                wall.y = SurfaceHeight(wall.x, wall.z);
                _props.Scaffold(wall + outward * 0.6f, yaw, Mathf.Min(14f, r.Half.x * 1.4f),
                                Mathf.Min(16f, Mathf.Max(6f, r.Y * 0.6f)), 4);
            }
        }

        // A crater and a run of site hoarding at each crash site: the pile-ups get a reason.
        var crashes = _def.Crashes ?? new object[0][];
        foreach (var row in crashes) {
            if (row.Length < 2) continue;
            float x = Num(row[0]), z = Num(row[1]);
            if (!OnLand(x + 6f)) continue;
            _props.Crater(new Vector3(x + 4f, SurfaceHeight(x + 4f, z - 4f), z - 4f), 3.4f);
            _props.Hoarding(new Vector3(x - 12f, SurfaceHeight(x - 12f, z - 10f), z - 10f),
                            new Vector3(x - 12f, SurfaceHeight(x - 12f, z + 8f), z + 8f), 2.4f);
        }
    }

    /// <summary>
    /// The Lakefront's shoreline and pier.
    ///
    /// Everything east of the lake line was a flat dark plane meeting the land on one hard straight
    /// edge, and the pier - the corridor the whole level is designed around - was a bare strip of
    /// paving over it with no edge at all. Neither of those is fixable with the city dressing: a
    /// waterfront is made of the things that hold the water back and the things that mark where it
    /// is safe to walk.
    /// </summary>
    void DressWaterfront() {
        SeedFrom(_def.Name, 2749);
        var lake = _def.Lake;
        float x = lake.X;

        // Seawall either side of the pier mouth, with bitts along it.
        _props.Seawall(new Vector3(x, 0.18f, -92f), new Vector3(x, 0.18f, lake.PierZ0));
        _props.Seawall(new Vector3(x, 0.18f, lake.PierZ1), new Vector3(x, 0.18f, 92f));
        for (float z = -88f; z <= 88f; z += 11f) {
            if (z > lake.PierZ0 - 4f && z < lake.PierZ1 + 4f) continue;
            _props.MooringBollard(new Vector3(x - 1.6f, 0.18f, z));
        }

        // The pier: railings both sides, masts down each edge, and festoon lights across it.
        float deck = 0.2f;
        _props.Railing(new Vector3(x, deck, lake.PierZ0), new Vector3(lake.PierXEnd, deck, lake.PierZ0));
        _props.Railing(new Vector3(x, deck, lake.PierZ1), new Vector3(lake.PierXEnd, deck, lake.PierZ1));

        Vector3 prevN = Vector3.zero, prevS = Vector3.zero;
        for (float px = x + 6f; px <= lake.PierXEnd - 3f; px += 11f) {
            var n = _props.Mast(new Vector3(px, deck, lake.PierZ1 - 0.9f), 4.6f);
            var so = _props.Mast(new Vector3(px, deck, lake.PierZ0 + 0.9f), 4.6f);
            // Strung across the deck, and along it between the masts. Both, because one string is
            // a wire and a grid of them is a pier.
            _props.Festoon(n, so, 0.9f, 7);
            if (prevN != Vector3.zero) {
                _props.Festoon(prevN, n, 0.8f, 6);
                _props.Festoon(prevS, so, 0.8f, 6);
            }
            prevN = n; prevS = so;
        }

        // Navigation buoys and a breakwater out on the water, which is the only thing that gives
        // the empty half of the map any distance to read against.
        _props.Buoy(new Vector3(x + 34f, 0f, -26f), new Color(0.2f, 1f, 0.35f));
        _props.Buoy(new Vector3(x + 52f, 0f, 30f), new Color(1f, 0.25f, 0.2f));
        _props.Buoy(new Vector3(x + 22f, 0f, 48f), new Color(1f, 0.85f, 0.2f));
        _props.Breakwater(new Vector3(x + 96f, 0.2f, -70f), new Vector3(x + 96f, 0.2f, 26f),
                          new Color(0.25f, 0.9f, 1f));
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
                //
                // On the *corner*, though, not in the middle of the crossing. These coordinates are
                // intersection centres, and they are also the wave spawn points - so every lamp
                // post stood in the middle of a junction with enemies materialising inside it.
                // 5.8 m out on a diagonal puts the pole on the sidewalk where a lamp belongs and
                // leaves the crossing clear; the light barely moves, so the road reads the same.
                float cx = gx * LevelData.Pitch + LevelData.Pitch / 2f;
                float cz = gz * LevelData.Pitch + LevelData.Pitch / 2f;
                // Alternate the corner so the grid does not line every lamp up on one diagonal.
                float sx = ((gx + gz) & 1) == 0 ? 1f : -1f;
                float sz = (gz & 1) == 0 ? 1f : -1f;
                float x = cx + sx * 5.8f;
                float z = cz + sz * 5.8f;
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

                // The pole stands back from the lamp and a short arm reaches out to it, which is
                // what a street lamp actually looks like and reads far better in silhouette than a
                // post with a box balanced on top.
                float px = x + sx * 1.3f, pz = z + sz * 1.3f;
                var pole = Prim.Create(PrimKind.Cylinder, "LampPole", _root.transform);
                pole.transform.position = new Vector3(px, 3.4f + SurfaceHeight(px, pz), pz);
                pole.transform.localScale = new Vector3(0.22f, 3.4f, 0.22f);
                pole.GetComponent<Renderer>().material = poleMat;

                var arm = Prim.Create(PrimKind.Cube, "LampArm", _root.transform);
                arm.transform.position = new Vector3((px + x) * 0.5f, 6.75f, (pz + z) * 0.5f);
                arm.transform.rotation = Quaternion.LookRotation(new Vector3(x - px, -0.35f, z - pz).normalized, Vector3.up);
                arm.transform.localScale = new Vector3(0.14f, 0.14f, 2.0f);
                arm.GetComponent<Renderer>().material = poleMat;

                var head = Prim.Create(PrimKind.Cube, "LampHead", _root.transform);
                head.transform.position = new Vector3(x, 6.6f, z);
                head.transform.localScale = new Vector3(0.75f, 0.16f, 0.36f);
                head.GetComponent<Renderer>().material = lampMat;

                // The cone of light itself. The point light already pools on the road, but a pool
                // is invisible from 40 m down the street - the cone is what makes a row of lamps
                // recede into the fog instead of the street simply going dark.
                _props.LightShaft(new Vector3(x, SurfaceHeight(x, z), z), 4.6f, 6.7f);
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

        // The procedural props are cheap per prop but there are thousands of them, and the cost is
        // invisible in a screenshot. Count the triangles they actually added, so "the roofline got
        // busier" and "the level got 200k triangles heavier" are separate observations.
        int propTris = 0, propMeshes = 0;
        foreach (var mf in _root.GetComponentsInChildren<MeshFilter>()) {
            if (mf.sharedMesh == null || !mf.gameObject.name.StartsWith("Props_")) continue;
            propTris += mf.sharedMesh.triangles.Length / 3;
            propMeshes++;
        }
        Debug.Log($"[UFO] props meshes={propMeshes} tris={propTris}");

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
