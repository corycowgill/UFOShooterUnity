using System.Collections.Generic;
using UnityEngine;

namespace UFO {

/// <summary>
/// The procedural prop library: everything the imported GLB catalogue does not have.
///
/// v2's 61 models cover the street - cars, benches, hydrants, buildings. What they never covered is
/// the two places the eye actually spends its time in a first-person night fight: **the roofline**,
/// where the city meets the sky, and **the overhead**, where a street should be crossed by wires and
/// signs instead of being an empty corridor. Nor is there anything that says the city is under
/// attack beyond a couple of burnt cars.
///
/// So these are built from code rather than modelled. Every prop appends into a shared
/// <see cref="MeshKit"/> per material, so the whole level's roof clutter - dozens of water towers,
/// hundreds of vents, every catenary - ends up as about six meshes and six draw calls. Adding a
/// water tower to every roof costs nothing that shows up in a frame time.
///
/// Call the prop methods, then <see cref="Flush"/> once.
/// </summary>
public class CityProps {

    readonly Transform _parent;
    readonly Arena _arena;

    // One kit per material. Which kit a member goes into is the only material decision each prop
    // makes, which is what keeps the whole library to a handful of draw calls.
    readonly MeshKit _steel = new MeshKit();        // poles, masts, ducts, railings
    // Cable goes in its own kit purely so it can be excluded from shadow casting. A 35 mm wire is
    // far thinner than one shadow-map texel at this cascade range, so its shadow resolves into a
    // dotted line - and a catenary running the length of a street laid two long strings of beads
    // down the middle of the road that looked exactly like a decal bug.
    readonly MeshKit _wire = new MeshKit();
    readonly MeshKit _concrete = new MeshKit();     // parapets, bollards, barriers, plinths
    readonly MeshKit _wood = new MeshKit();         // water tanks, hoarding, pallet timber
    readonly MeshKit _rust = new MeshKit();         // rooftop plant, skips, weathered sheet
    readonly MeshKit _scorch = new MeshKit();       // craters, blast scarring
    readonly MeshKit _wet = new MeshKit();          // puddles: dark and near-mirror
    readonly MeshKit _shaft = new MeshKit();        // additive light cones under the street lamps
    readonly MeshKit _bulb = new MeshKit();         // festoon bulbs and vehicle marker lights
    readonly MeshKit _glazing = new MeshKit();      // lit shopfront glass
    readonly MeshKit _spill = new MeshKit();        // the warm pool that glass throws on the pavement
    readonly MeshKit _litter = new MeshKit();       // paper and torn sheet: pale, not rubble-dark
    readonly MeshKit _hazard = new MeshKit();       // cordon tape and stakes: the one loud colour
    readonly MeshKit _tar = new MeshKit();          // resurfaced patches: a different asphalt
    readonly MeshKit _grime = new MeshKit();        // contact shading under every prop: outer ring
    readonly MeshKit _grimeInner = new MeshKit();   // and the darker core of it
    readonly MeshKit _paving = new MeshKit();       // plaza banding and the centre medallion
    readonly MeshKit _paint = new MeshKit();        // painted markings: helipad circle and H
    readonly MeshKit _crack = new MeshKit();        // cracks and skids: near black, and above
                                                    // the patches so the two never interleave

    readonly Dictionary<Color, Material> _glowCache = new Dictionary<Color, Material>();

    int _seed;

    public CityProps(Transform parent, Arena arena, int seed) {
        _parent = parent;
        _arena = arena;
        _seed = (seed * 9176 + 0x5f3a) & 0x7fffffff;
        _steel.UvScale = _concrete.UvScale = _wood.UvScale = _rust.UvScale = 0.5f;
    }

    /// <summary>Deterministic per level, so a rebuild of THE LOOP is the same THE LOOP.</summary>
    public float Rnd() { _seed = (int)((_seed * 1103515245L + 12345L) & 0x7fffffff); return _seed / (float)0x7fffffff; }
    float Rnd(float a, float b) => a + Rnd() * (b - a);
    bool Chance(float p) => Rnd() < p;

    // ================================================================= rooftops

    /// <summary>
    /// A Chicago rooftop water tower: staved timber tank on a braced steel frame, conical lid,
    /// access ladder. It is the single most recognisable thing on a Chicago roofline and the
    /// cheapest way to stop a flat-topped building reading as a box.
    /// </summary>
    public void WaterTower(Vector3 basePos, float scale = 1f) {
        float legH = 3.6f * scale, tankR = 1.9f * scale, tankH = 3.4f * scale;

        _steel.Lattice(basePos, legH, tankR * 0.78f, tankR * 0.62f, 2, 0.085f * scale);

        var tankBase = basePos + Vector3.up * legH;
        _wood.Tube(tankBase, tankBase + Vector3.up * tankH, tankR, 12, caps: true);
        // Two steel hoops around the staves: the detail that reads at distance.
        for (int i = 1; i <= 2; i++) {
            float y = tankH * (i / 3f);
            _steel.Tube(tankBase + Vector3.up * (y - 0.06f), tankBase + Vector3.up * (y + 0.06f), tankR * 1.03f, 12);
        }
        _wood.Cone(tankBase + Vector3.up * tankH, tankR * 1.08f, 1.3f * scale, 12);
        _steel.Tube(tankBase + Vector3.up * (tankH + 1.3f * scale),
                    tankBase + Vector3.up * (tankH + 2.0f * scale), 0.06f * scale, 5);

        // Ladder up one leg, rungs and all - it is what gives the frame its scale.
        var lx = basePos + new Vector3(tankR * 0.8f, 0f, 0f);
        _steel.Tube(lx + new Vector3(0f, 0f, -0.22f), lx + new Vector3(0f, legH + tankH, -0.22f), 0.035f, 4);
        _steel.Tube(lx + new Vector3(0f, 0f, 0.22f), lx + new Vector3(0f, legH + tankH, 0.22f), 0.035f, 4);
        for (float y = 0.4f; y < legH + tankH; y += 0.45f)
            _steel.Tube(lx + new Vector3(0f, y, -0.22f), lx + new Vector3(0f, y, 0.22f), 0.025f, 4);
    }

    /// <summary>
    /// Rooftop plant: a parapet round the edge, air handling units with fan hoods, ductwork, vent
    /// stacks and a stair bulkhead. Built to the roof's actual footprint so it never overhangs.
    /// </summary>
    public void RoofPlant(Vector3 center, Vector2 half, float roofY) {
        var c = new Vector3(center.x, roofY, center.z);

        // Air handling units, with a fan hood on top of each.
        int units = Mathf.Clamp(Mathf.RoundToInt(half.x * half.y / 22f), 1, 5);
        for (int i = 0; i < units; i++) {
            float w = Rnd(2.2f, 4.0f), d = Rnd(1.8f, 3.2f), h = Rnd(1.0f, 1.9f);
            var p = c + new Vector3(Rnd(-half.x + w, half.x - w), h * 0.5f, Rnd(-half.y + d, half.y - d));
            float yaw = Chance(0.5f) ? 0f : 90f;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            _rust.Box(p, new Vector3(w, h, d), rot);
            _steel.Tube(p + Vector3.up * (h * 0.5f), p + Vector3.up * (h * 0.5f + 0.35f), Mathf.Min(w, d) * 0.3f, 10, caps: true);
            // Louvre ribs down one face, so the unit is not a bare cube.
            for (float k = -0.35f; k <= 0.35f; k += 0.18f)
                _steel.Box(p + rot * new Vector3(w * 0.5f + 0.03f, h * k, 0f), new Vector3(0.05f, 0.09f, d * 0.8f), rot);
        }

        // Ductwork snaking between them, and a couple of vent stacks.
        for (int i = 0; i < 2; i++) {
            var a = c + new Vector3(Rnd(-half.x + 1f, half.x - 1f), Rnd(0.5f, 1.2f), Rnd(-half.y + 1f, half.y - 1f));
            var b = a + (Chance(0.5f) ? new Vector3(Rnd(3f, 8f), 0f, 0f) : new Vector3(0f, 0f, Rnd(3f, 8f)));
            b.x = Mathf.Clamp(b.x, c.x - half.x + 1f, c.x + half.x - 1f);
            b.z = Mathf.Clamp(b.z, c.z - half.y + 1f, c.z + half.y - 1f);
            _steel.Tube(a, b, Rnd(0.22f, 0.4f), 8, caps: true);
        }
        for (int i = 0; i < 3; i++) {
            var p = c + new Vector3(Rnd(-half.x + 1f, half.x - 1f), 0f, Rnd(-half.y + 1f, half.y - 1f));
            float h = Rnd(0.8f, 2.2f);
            _steel.Tube(p, p + Vector3.up * h, Rnd(0.09f, 0.18f), 6);
            _steel.Cone(p + Vector3.up * h, Rnd(0.2f, 0.3f), 0.25f, 8);
        }

        // Stair bulkhead: the little hut over the roof access door.
        var bw = new Vector3(Rnd(2.4f, 3.4f), 2.5f, Rnd(2.4f, 3.4f));
        var bp = c + new Vector3(Rnd(-half.x + bw.x, half.x - bw.x), bw.y * 0.5f, Rnd(-half.y + bw.z, half.y - bw.z));
        _concrete.Box(bp, bw);
        _rust.Box(bp + new Vector3(0f, bw.y * 0.5f + 0.06f, 0f), new Vector3(bw.x + 0.35f, 0.12f, bw.z + 0.35f));
    }

    /// <summary>
    /// The low wall round a flat roof. Only worth adding where the model really is a box to the
    /// top - on a tapered or terraced tower a full-footprint parapet hangs in mid air.
    /// </summary>
    public void Parapet(Vector3 center, Vector2 half, float roofY) {
        var c = new Vector3(center.x, roofY, center.z);
        const float pt = 0.36f, ph = 0.9f;
        _concrete.Box(c + new Vector3(0f, ph * 0.5f, half.y - pt * 0.5f), new Vector3(half.x * 2f, ph, pt));
        _concrete.Box(c + new Vector3(0f, ph * 0.5f, -half.y + pt * 0.5f), new Vector3(half.x * 2f, ph, pt));
        _concrete.Box(c + new Vector3(half.x - pt * 0.5f, ph * 0.5f, 0f), new Vector3(pt, ph, half.y * 2f - pt));
        _concrete.Box(c + new Vector3(-half.x + pt * 0.5f, ph * 0.5f, 0f), new Vector3(pt, ph, half.y * 2f - pt));
    }

    /// <summary>
    /// A rooftop helipad: the deck, its perimeter lights and the painted circle and H.
    ///
    /// Worth one per level rather than one per roof. It is the only rooftop prop that is read from
    /// *above* as well as in silhouette, which matters here because the player spends the whole
    /// game with a mothership overhead and every aerial enemy looking down.
    /// </summary>
    public void Helipad(Vector3 center, float radius) {
        _concrete.Tube(center, center + Vector3.up * 0.28f, radius, 20, caps: true);
        // The painted circle, as a ring of short bars, and the H inside it.
        int segs = 28;
        for (int i = 0; i < segs; i++) {
            float a0 = i / (float)segs * Mathf.PI * 2f, a1 = (i + 1) / (float)segs * Mathf.PI * 2f;
            var p0 = center + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * (radius * 0.72f);
            var p1 = center + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * (radius * 0.72f);
            _paint.GroundQuad((p0 + p1) * 0.5f + Vector3.up * 0.30f, 0.3f, Vector3.Distance(p0, p1) * 1.1f,
                              Mathf.Atan2(p1.x - p0.x, p1.z - p0.z) * Mathf.Rad2Deg);
        }
        float h = radius * 0.5f;
        _paint.GroundQuad(center + new Vector3(-h * 0.5f, 0.30f, 0f), 0.34f, h * 1.4f, 0f);
        _paint.GroundQuad(center + new Vector3(h * 0.5f, 0.30f, 0f), 0.34f, h * 1.4f, 0f);
        _paint.GroundQuad(center + new Vector3(0f, 0.30f, 0f), h, 0.34f, 0f);

        for (int i = 0; i < 8; i++) {
            float a = i / 8f * Mathf.PI * 2f;
            Beacon(center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius + Vector3.up * 0.4f,
                   new Color(0.25f, 0.85f, 1f), 0.18f);
        }
    }

    /// <summary>A bank of solar panels on a tilted frame.</summary>
    public void SolarArray(Vector3 basePos, float yaw, int rows) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var fwd = rot * Vector3.forward;
        for (int r = 0; r < rows; r++) {
            var c = basePos + fwd * (r * 2.4f);
            for (float s = -1f; s <= 1f; s += 2f) {
                var foot = c + right * (2.2f * s);
                _steel.Tube(foot, foot + Vector3.up * 0.45f, 0.045f, 4);
                _steel.Tube(foot + fwd * 1.1f, foot + fwd * 1.1f + Vector3.up * 1.1f, 0.045f, 4);
            }
            _steel.Box(c + fwd * 0.55f + Vector3.up * 0.8f, new Vector3(4.8f, 0.09f, 1.7f),
                       rot * Quaternion.Euler(-24f, 0f, 0f));
        }
    }

    /// <summary>A satellite dish farm: three dishes on a shared frame, all pointed the same way.</summary>
    public void DishFarm(Vector3 basePos, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var aim = (rot * new Vector3(0f, 0.55f, 1f)).normalized;
        for (int i = 0; i < 3; i++) {
            var c = basePos + right * ((i - 1) * 2.2f);
            float h = Rnd(0.8f, 1.6f);
            _steel.Tube(c, c + Vector3.up * h, 0.09f, 6);
            _steel.Disc(c + Vector3.up * h + aim * 0.15f, aim, Rnd(0.7f, 1.1f), 14);
            _steel.Tube(c + Vector3.up * h, c + Vector3.up * h + aim * 0.8f, 0.035f, 4);
        }
    }

    /// <summary>
    /// A lattice communications mast with a red aircraft beacon. Tall buildings get one; it is a
    /// silhouette against the sky for the price of forty triangles.
    /// </summary>
    public void AntennaMast(Vector3 basePos, float height) {
        _steel.Lattice(basePos, height, 0.75f, 0.28f, Mathf.Max(3, Mathf.RoundToInt(height / 3f)), 0.07f);
        var top = basePos + Vector3.up * height;
        _steel.Tube(top, top + Vector3.up * (height * 0.22f), 0.045f, 4);
        // Dishes and whips hung off the mast, at a believable height.
        for (int i = 0; i < 2; i++) {
            float y = height * Rnd(0.45f, 0.85f);
            float a = Rnd(0f, Mathf.PI * 2f);
            var arm = basePos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.9f + Vector3.up * y;
            _steel.Tube(basePos + Vector3.up * y, arm, 0.05f, 4);
            _steel.Disc(arm, new Vector3(Mathf.Cos(a), 0.2f, Mathf.Sin(a)), 0.55f, 10);
        }
        Beacon(top + Vector3.up * (height * 0.22f + 0.2f), new Color(1f, 0.12f, 0.1f), 0.28f, blink: true);
    }

    /// <summary>
    /// A rooftop billboard. Emissive face, steel frame, back bracing and a catwalk - the back is
    /// seen as often as the front from inside a city block, so it is not a floating quad.
    /// </summary>
    public void Billboard(Vector3 basePos, float yaw, float width, float height, Color glow) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var back = rot * Vector3.back;
        float legH = Rnd(1.6f, 3.0f);
        var faceC = basePos + Vector3.up * (legH + height * 0.5f);

        for (float s = -1f; s <= 1f; s += 2f) {
            var foot = basePos + right * (width * 0.42f * s);
            _steel.Tube(foot, foot + Vector3.up * (legH + height), 0.11f, 5);
            _steel.Tube(foot + Vector3.up * (legH + height * 0.9f), foot + back * (height * 0.45f), 0.07f, 4);
            _steel.Tube(foot + Vector3.up * (legH + height * 0.35f), foot + back * (height * 0.2f), 0.06f, 4);
        }
        _steel.Box(faceC + back * 0.12f, new Vector3(width, height, 0.18f), rot);
        for (float k = -0.3f; k <= 0.3f; k += 0.3f)
            _steel.Tube(faceC + right * (width * 0.5f) + Vector3.up * (height * k),
                        faceC - right * (width * 0.5f) + Vector3.up * (height * k), 0.05f, 4);

        GlowQuad(faceC + (rot * Vector3.forward) * 0.02f, rot, width * 0.96f, height * 0.9f, glow);

        // Floodlights on a gantry under the face: the light is what sells it as lit signage.
        var lamp = new GameObject("BillboardLight");
        lamp.transform.SetParent(_parent, false);
        lamp.transform.position = faceC + (rot * Vector3.forward) * 1.2f - Vector3.up * (height * 0.45f);
        var l = lamp.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = glow;
        l.range = 16f;
        l.intensity = 2.2f;
        l.shadows = LightShadows.None;
    }

    /// <summary>
    /// A sweeping searchlight: the cone is geometry, the light is real, and it turns. Two or three
    /// of these raking across a night skyline do more for "the city is under attack" than any
    /// amount of ground dressing.
    /// </summary>
    public void SearchLight(Vector3 basePos, float speed) {
        var holder = new GameObject("SearchLight");
        holder.transform.SetParent(_parent, false);
        holder.transform.position = basePos;
        holder.AddComponent<NoBatch>();                     // it rotates; batching would freeze it
        var sweep = holder.AddComponent<SweepLight>();
        sweep.DegreesPerSecond = speed;
        sweep.Pitch = Rnd(22f, 40f);

        // The plinth stays put, so it goes in the shared kit rather than under the rotating holder.
        _concrete.Box(basePos + Vector3.up * 0.35f, new Vector3(1.6f, 0.7f, 1.6f));
        _steel.Tube(basePos + Vector3.up * 0.7f, basePos + Vector3.up * 1.5f, 0.16f, 6);

        var head = new GameObject("Head");
        head.transform.SetParent(holder.transform, false);
        head.transform.localPosition = Vector3.up * 1.6f;
        sweep.Head = head.transform;

        var kit = new MeshKit();
        kit.Cone(Vector3.zero, 3.5f, 150f, 14);
        var beam = new GameObject("Beam");
        beam.transform.SetParent(head.transform, false);
        // Cone() opens upward FROM its base, and a beam has to be the other way round: narrow at
        // the lamp, wide at the far end. So the cone is turned to run backwards along the head's
        // forward axis and then pushed out by its own length, which lands the apex on the lamp.
        beam.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        beam.transform.localPosition = new Vector3(0f, 0f, 150f);
        beam.AddComponent<MeshFilter>().sharedMesh = kit.Build("SearchBeam");
        var mr = beam.AddComponent<MeshRenderer>();
        mr.material = Fx.AdditiveTinted(new Color(0.75f, 0.85f, 1f, 0.10f) * 0.34f);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        var spot = head.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.spotAngle = 26f;
        spot.range = 120f;
        spot.intensity = 12f;
        spot.color = new Color(0.82f, 0.9f, 1f);
        spot.shadows = LightShadows.None;
    }

    /// <summary>A column of smoke off a burning building, far enough away to be geometry not particles.</summary>
    public void SmokeColumn(Vector3 basePos, float height, float radius) {
        var kit = new MeshKit();
        // A tall, narrow, leaning plume rather than a cloud sitting on a chimney. The first version
        // widened to 2.2x its base radius over seven short segments and came back as a grey
        // mushroom pasted on the skyline; smoke at this distance reads as a *line* that drifts, and
        // it has to be much taller than it is wide before it reads as smoke at all.
        //
        // Single-sided cones would vanish when seen from inside, so it is built from tubes, which
        // is also cheaper.
        const int segs = 11;
        float lean = radius * 5f;
        for (int i = 0; i < segs; i++) {
            float k0 = i / (float)segs, k1 = (i + 1) / (float)segs;
            Vector3 At(float k) => new Vector3(
                lean * k * k + Mathf.Sin(k * 3.1f) * radius * 0.6f,
                height * k,
                lean * 0.35f * k * k + Mathf.Cos(k * 2.3f) * radius * 0.5f);
            kit.Tube(At(k0), At(k1), Mathf.Lerp(radius * 0.4f, radius * 1.35f, k1), 9);
        }
        var go = new GameObject("SmokeColumn");
        go.transform.SetParent(_parent, false);
        go.transform.position = basePos;
        go.AddComponent<MeshFilter>().sharedMesh = kit.Build("SmokeColumn");
        var mr = go.AddComponent<MeshRenderer>();
        // Additive, and therefore a plume that is *lit* rather than one that blocks light. That is
        // both the honest look for smoke over a city burning at night and the only transparency
        // this build reliably honours: the alpha-blended version rendered opaque in the player and
        // hung a solid dark mushroom over the skyline.
        mr.material = Fx.AdditiveTinted(new Color(0.42f, 0.40f, 0.46f) * 0.085f);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        // The fire underneath it, so the column has a source.
        var fireGo = new GameObject("RoofFireGlow");
        fireGo.transform.SetParent(_parent, false);
        fireGo.transform.position = basePos + Vector3.up * 1.5f;
        var fl = fireGo.AddComponent<Light>();
        fl.type = LightType.Point;
        fl.color = new Color(1f, 0.42f, 0.14f);
        fl.range = 45f;
        fl.intensity = 5f;
        fl.shadows = LightShadows.None;
        fireGo.AddComponent<FlickerLight>();
        fireGo.AddComponent<NoBatch>();
    }

    // ================================================================= overhead

    /// <summary>A utility pole: shaft, crossarm, insulators, and a guy wire if it is on a corner.</summary>
    public Vector3 UtilityPole(Vector3 basePos, float yaw, float height) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var arm = rot * Vector3.right;
        _wood.Tube(basePos, basePos + Vector3.up * height, 0.17f, 7);

        var armY = basePos + Vector3.up * (height - 0.7f);
        _wood.Box(armY, new Vector3(2.4f, 0.16f, 0.16f), rot);
        _wood.Box(armY - Vector3.up * 0.9f, new Vector3(1.8f, 0.14f, 0.14f), rot);
        for (float s = -1f; s <= 1f; s += 1f) {
            if (Mathf.Approximately(s, 0f)) continue;
            _steel.Tube(armY + arm * (1.05f * s), armY + arm * (1.05f * s) + Vector3.up * 0.22f, 0.07f, 5);
        }
        // A transformer can on one in four: the drum is instantly readable as a utility pole.
        if (Chance(0.25f))
            _steel.Tube(basePos + Vector3.up * (height - 2.6f) + arm * 0.3f,
                        basePos + Vector3.up * (height - 1.8f) + arm * 0.3f, 0.36f, 9, caps: true);

        if (_arena != null)
            _arena.Add(ArenaBox.FromCenter(basePos + Vector3.up * (height * 0.5f),
                                           new Vector3(0.5f, height, 0.5f)));
        return armY;
    }

    /// <summary>
    /// The wires themselves - three sagging lines between two crossarms, plus a heavier service
    /// drop. This is the prop that changes a street the most: an empty gap of sky above the road is
    /// what makes a game city look like a game city.
    /// </summary>
    public void WireSpan(Vector3 a, Vector3 b, float yaw) {
        var arm = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        float span = Vector3.Distance(a, b);
        float sag = Mathf.Clamp(span * 0.035f, 0.3f, 1.6f);
        for (float s = -1f; s <= 1f; s += 1f)
            _wire.Wire(a + arm * (1.05f * s) + Vector3.up * 0.22f,
                       b + arm * (1.05f * s) + Vector3.up * 0.22f, sag, 0.035f);
        _wire.Wire(a - Vector3.up * 0.9f, b - Vector3.up * 0.9f, sag * 1.25f, 0.05f);
    }

    /// <summary>
    /// A fire escape: stacked landings with railings, a run of stairs between each pair and the
    /// drop ladder hanging off the bottom one.
    ///
    /// Worth its triangles because of where it sits. It is the only thing in this game that hangs
    /// on a wall between two and eight metres up - the exact band a player's eye sweeps through
    /// while backing down a street - and it casts a real shadow across the facade behind it, which
    /// is what breaks the imported buildings' flat baked albedo more effectively than any grading.
    /// </summary>
    public void FireEscape(Vector3 wallPoint, float yaw, float width, int floors, float floorHeight) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var outward = rot * Vector3.forward;
        const float depth = 1.5f, railH = 1.05f;
        float firstY = floorHeight;          // the lowest landing; below it is the drop ladder

        for (float s = -1f; s <= 1f; s += 2f) {
            var post = wallPoint + right * (width * 0.5f * s) + outward * depth;
            _steel.Tube(post + Vector3.up * firstY, post + Vector3.up * (firstY + floors * floorHeight), 0.05f, 5);
        }

        for (int f = 0; f < floors; f++) {
            float y = firstY + f * floorHeight;
            var mid = wallPoint + outward * (depth * 0.5f) + Vector3.up * y;

            // The landing, and the grating bars you can see through from underneath.
            _steel.Box(mid, new Vector3(width, 0.07f, depth), rot);
            for (float k = -0.4f; k <= 0.4f; k += 0.16f)
                _steel.Box(mid + outward * (depth * k) - Vector3.up * 0.09f,
                           new Vector3(width, 0.05f, 0.05f), rot);

            // Railing along the outer edge and both returns.
            var outer = wallPoint + outward * depth + Vector3.up * y;
            _steel.Tube(outer + right * (width * 0.5f) + Vector3.up * railH,
                        outer - right * (width * 0.5f) + Vector3.up * railH, 0.035f, 4);
            for (float k = -0.5f; k <= 0.5f; k += 0.125f)
                _steel.Tube(outer + right * (width * k), outer + right * (width * k) + Vector3.up * railH, 0.02f, 4);
            for (float s = -1f; s <= 1f; s += 2f) {
                var c = wallPoint + right * (width * 0.5f * s) + Vector3.up * y;
                _steel.Tube(c + outward * depth + Vector3.up * railH, c + Vector3.up * railH, 0.03f, 4);
            }

            // Stairs up to the next landing, switching sides each floor the way a real one does.
            if (f + 1 < floors) {
                float side = (f % 2 == 0) ? 0.3f : -0.3f;
                var a = wallPoint + right * (width * side) + outward * (depth * 0.85f) + Vector3.up * y;
                var b = wallPoint + right * (width * -side) + outward * (depth * 0.85f) + Vector3.up * (y + floorHeight);
                _steel.Box((a + b) * 0.5f, new Vector3(0.8f, 0.06f, Vector3.Distance(a, b)),
                           Quaternion.LookRotation((b - a).normalized, Vector3.up));
                _steel.Tube(a + Vector3.up * railH, b + Vector3.up * railH, 0.028f, 4);
            }
        }

        // The counterweighted drop ladder, hanging short of the pavement.
        var lad = wallPoint + outward * (depth * 0.8f) + right * (width * 0.25f);
        for (float s = -1f; s <= 1f; s += 2f) {
            var rail = lad + right * (0.22f * s);
            _steel.Tube(rail + Vector3.up * 2.4f, rail + Vector3.up * firstY, 0.028f, 4);
        }
        for (float y = 2.6f; y < firstY; y += 0.4f)
            _steel.Tube(lad + right * -0.22f + Vector3.up * y, lad + right * 0.22f + Vector3.up * y, 0.02f, 4);
    }

    /// <summary>
    /// A lit ground floor: bays of glazing with mullions between them, and the warm pool the glass
    /// throws across the pavement in front of it.
    ///
    /// This is the last big gap at street level, and it is a lighting one. Every building in this
    /// city meets the pavement with a blank wall, so a street at night is lit from above by lamps
    /// and from nowhere else - which is why the lower two metres of every frame went to black. Real
    /// night streets are lit from the *side*, by shops.
    ///
    /// The spill is a flat additive quad rather than a real light. Thirty more point lights would
    /// blow through URP's per-object additional-light budget on WebGL for an effect that only ever
    /// has to appear on one flat surface.
    /// </summary>
    public void Storefront(Vector3 wallCentre, float yaw, float length) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var outward = rot * Vector3.forward;

        const float sill = 0.45f, head = 2.9f, bay = 2.6f, mullion = 0.45f;
        int bays = Mathf.Max(1, Mathf.FloorToInt(length / (bay + mullion)));
        float used = bays * (bay + mullion) - mullion;
        var start = wallCentre - right * (used * 0.5f);

        for (int i = 0; i < bays; i++) {
            // Not every unit is trading. A dark bay between two lit ones is what makes the run
            // read as separate shops instead of one long light box.
            if (Chance(0.22f)) continue;
            var c = start + right * (i * (bay + mullion) + bay * 0.5f)
                  + outward * 0.06f + Vector3.up * ((sill + head) * 0.5f);
            _glazing.GroundQuadUpright(c, bay, head - sill, yaw);

            // Stall riser under the glass, and a fascia over it.
            _concrete.Box(c - Vector3.up * ((head - sill) * 0.5f + sill * 0.5f) + outward * 0.09f,
                          new Vector3(bay + mullion, sill, 0.18f), rot);
            _concrete.Box(c + Vector3.up * ((head - sill) * 0.5f + 0.25f) + outward * 0.09f,
                          new Vector3(bay + mullion, 0.5f, 0.22f), rot);
        }

        // The pool on the pavement. It starts at the glass and reaches about two metres out.
        _spill.GroundDecal(wallCentre + outward * 1.5f + Vector3.up * 0.026f, used, 3.4f, yaw);
    }

    /// <summary>A shop awning with a lit sign band under it, hung off a facade at street level.</summary>
    public void Awning(Vector3 wallPoint, float yaw, float width, Color glow) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var outward = rot * Vector3.forward;
        var right = rot * Vector3.right;
        const float depth = 1.9f, top = 3.5f, drop = 0.55f;

        // The canopy, tilted down away from the wall.
        var mid = wallPoint + outward * (depth * 0.5f) + Vector3.up * (top - drop * 0.5f);
        _rust.Box(mid, new Vector3(width, 0.1f, depth), Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(16f, 0f, 0f));
        // Valance: the short vertical skirt at the outer edge.
        _rust.Box(wallPoint + outward * depth + Vector3.up * (top - drop - 0.2f), new Vector3(width, 0.42f, 0.06f), rot);
        for (float s = -1f; s <= 1f; s += 2f) {
            var p = wallPoint + right * (width * 0.5f * s);
            _steel.Tube(p + Vector3.up * top, p + outward * depth + Vector3.up * (top - drop), 0.05f, 4);
            _steel.Tube(p + outward * depth + Vector3.up * (top - drop), p + Vector3.up * (top - 1.4f), 0.04f, 4);
        }

        GlowQuad(wallPoint + outward * 0.06f + Vector3.up * (top + 0.75f), rot, width * 0.85f, 0.85f, glow);
    }

    /// <summary>A street banner strung across a road between two poles. Cloth, not light.</summary>
    public void StreetBanner(Vector3 a, Vector3 b, Color color) {
        var dir = (b - a);
        float span = dir.magnitude;
        if (span < 2f) return;
        var mid = (a + b) * 0.5f - Vector3.up * (span * 0.02f);
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        _wire.Wire(a, b, span * 0.02f, 0.03f);
        GlowQuad(mid, Quaternion.Euler(0f, yaw + 90f, 0f), span * 0.55f, 1.5f, color * 0.55f);
    }

    // ================================================================= waterfront

    /// <summary>
    /// A railing: posts, a top rail and a mid rail.
    ///
    /// The one prop the Lakefront could not do without. A pier is a fourteen-metre deck with a
    /// forty-metre drop either side of it, and with nothing along the edge it read as a strip of
    /// pavement floating on a dark plane - there was no line anywhere in the frame to tell you
    /// where the deck stopped. A railing is also what makes the corridor read *as* a corridor,
    /// which is the whole reason the pier is shaped the way it is.
    /// </summary>
    public void Railing(Vector3 from, Vector3 to, float height = 1.1f, float postSpacing = 2.4f) {
        float len = Vector3.Distance(from, to);
        if (len < 0.5f) return;
        int posts = Mathf.Max(2, Mathf.RoundToInt(len / postSpacing));
        for (int i = 0; i <= posts; i++) {
            var p = Vector3.Lerp(from, to, i / (float)posts);
            _steel.Tube(p, p + Vector3.up * height, 0.045f, 5);
        }
        _steel.Tube(from + Vector3.up * height, to + Vector3.up * height, 0.045f, 5);
        _steel.Tube(from + Vector3.up * height * 0.55f, to + Vector3.up * height * 0.55f, 0.03f, 4);
    }

    /// <summary>
    /// The seawall along the shoreline: a capped concrete revetment with a railing on top.
    ///
    /// Without it the land simply stops and the lake plane begins, on one hard straight line at
    /// exactly ground level - which from the shore reads as the level running out rather than as a
    /// waterfront. The cap also gives the lamps and bollards something to stand on.
    /// </summary>
    public void Seawall(Vector3 from, Vector3 to, float drop = 1.6f) {
        var dir = to - from;
        float len = dir.magnitude;
        if (len < 1f) return;
        dir /= len;
        var outward = Vector3.Cross(Vector3.up, dir).normalized;
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var mid = (from + to) * 0.5f;

        // The wall face, and a coping course proud of it.
        _concrete.Box(mid + outward * 0.55f - Vector3.up * (drop * 0.5f - 0.1f),
                      new Vector3(1.1f, drop, len), rot);
        _concrete.Box(mid + outward * 0.55f + Vector3.up * 0.22f, new Vector3(1.5f, 0.28f, len), rot);
        Railing(from + outward * 0.55f + Vector3.up * 0.36f, to + outward * 0.55f + Vector3.up * 0.36f);
    }

    /// <summary>A plain mast with a warm lamp on top, and the pole's own contact patch.</summary>
    public Vector3 Mast(Vector3 basePos, float height, float radius = 0.08f, bool lamp = true) {
        var top = basePos + Vector3.up * height;
        _steel.Tube(basePos, top, radius, 6);
        if (lamp) {
            _steel.Disc(top + Vector3.up * 0.06f, Vector3.up, radius * 2.6f, 8);
            _bulb.Box(top - Vector3.up * 0.06f, Vector3.one * 0.26f);
        }
        Contact(basePos, 0.35f);
        return top;
    }

    /// <summary>A mooring bollard - a squat bitt on the quayside, with a rope eye.</summary>
    public void MooringBollard(Vector3 pos) {
        _steel.Tube(pos, pos + Vector3.up * 0.55f, 0.19f, 9, caps: true);
        _steel.Tube(pos + Vector3.up * 0.5f, pos + Vector3.up * 0.68f, 0.27f, 9, caps: true);
        Contact(pos, 0.45f);
    }

    /// <summary>
    /// A string of festoon bulbs between two points. Navy Pier in one prop: the bulbs are tiny
    /// additive boxes in a shared mesh, so a whole pier's worth of them is one draw call.
    /// </summary>
    public void Festoon(Vector3 a, Vector3 b, float sag, int bulbs) {
        _wire.Wire(a, b, sag, 0.025f);
        for (int i = 1; i < bulbs; i++) {
            float k = i / (float)bulbs;
            var p = Vector3.Lerp(a, b, k);
            p.y -= sag * 4f * k * (1f - k) + 0.18f;
            _bulb.Box(p, Vector3.one * 0.16f, Quaternion.Euler(Rnd(0f, 40f), Rnd(0f, 360f), 0f));
        }
    }

    /// <summary>
    /// A navigation buoy: a float, a cage and a blinking lamp. Sitting out on an otherwise empty
    /// plane of water, three of these do more to establish scale and distance than any amount of
    /// surface detail on the water itself.
    /// </summary>
    public void Buoy(Vector3 pos, Color light) {
        _rust.Tube(pos - Vector3.up * 0.6f, pos + Vector3.up * 0.9f, 0.55f, 10, caps: true);
        _rust.Cone(pos + Vector3.up * 0.9f, 0.55f, 0.45f, 10);
        _steel.Lattice(pos + Vector3.up * 1.3f, 1.5f, 0.32f, 0.12f, 2, 0.04f);
        Beacon(pos + Vector3.up * 3.0f, light, 0.3f, blink: true);
    }

    /// <summary>A breakwater: a low run of armour blocks with a beacon on the head of it.</summary>
    public void Breakwater(Vector3 from, Vector3 to, Color light) {
        float len = Vector3.Distance(from, to);
        int blocks = Mathf.Max(4, Mathf.RoundToInt(len / 3.2f));
        for (int i = 0; i <= blocks; i++) {
            var p = Vector3.Lerp(from, to, i / (float)blocks);
            for (int j = 0; j < 3; j++) {
                var o = new Vector3(Rnd(-1.8f, 1.8f), Rnd(-0.4f, 0.9f), Rnd(-1.8f, 1.8f));
                _concrete.Box(p + o, new Vector3(Rnd(1.4f, 2.6f), Rnd(1.2f, 2.0f), Rnd(1.4f, 2.6f)),
                              Quaternion.Euler(Rnd(-18f, 18f), Rnd(0f, 360f), Rnd(-18f, 18f)));
            }
        }
        _concrete.Tube(to, to + Vector3.up * 3.2f, 0.7f, 10, caps: true);
        Beacon(to + Vector3.up * 3.6f, light, 0.34f, blink: true);
    }

    /// <summary>
    /// Marker lights on a parked vehicle, at all four corners.
    ///
    /// All four, rather than red at the back and white at the front, because the parking code
    /// orients a car along its street without knowing which end of the model is the front - and a
    /// headlight on the boot is a worse error than a marker light that is simply lit. At night a
    /// kerb full of these is most of what gives a street its sparkle.
    /// </summary>
    public void VehicleLights(Vector3 pos, float yaw, Vector3 half) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        for (float sx = -1f; sx <= 1f; sx += 2f) {
            for (float sz = -1f; sz <= 1f; sz += 2f) {
                var p = pos + rot * new Vector3(half.x * 0.82f * sx, half.y * 0.72f, half.z * 0.92f * sz);
                _bulb.Box(p, Vector3.one * 0.13f, rot);
            }
        }
    }

    // ================================================================= street level

    /// <summary>
    /// Banding and a centre medallion for the plaza.
    ///
    /// The centre block is the largest unbroken surface in the game and the one the player looks
    /// across for the whole first wave. It is a single tiled concrete texture, and no amount of
    /// litter fixes the fact that it has no *design* - real civic squares are laid out, with a
    /// darker border course and something in the middle. Flat quads in a second stone tone are
    /// enough to give it one.
    /// </summary>
    public void PlazaPaving(Vector3 center, float half, float y) {
        // A border course just inside the edge, in four runs.
        const float band = 1.5f;
        float outer = half - 0.8f;
        _paving.GroundQuad(center + new Vector3(0f, y, outer - band * 0.5f), outer * 2f, band, 0f);
        _paving.GroundQuad(center + new Vector3(0f, y, -outer + band * 0.5f), outer * 2f, band, 0f);
        _paving.GroundQuad(center + new Vector3(outer - band * 0.5f, y, 0f), band, outer * 2f, 0f);
        _paving.GroundQuad(center + new Vector3(-outer + band * 0.5f, y, 0f), band, outer * 2f, 0f);

        // A cross of paths quartering the square, which is what the benches and planters already
        // sit between - the banding just makes the arrangement deliberate instead of scattered.
        _paving.GroundQuad(center + new Vector3(0f, y, 0f), 2.6f, outer * 2f, 0f);
        _paving.GroundQuad(center + new Vector3(0f, y, 0f), outer * 2f, 2.6f, 0f);

        // The medallion: concentric rings of short arc segments, centred on the spawn point.
        for (int ring = 0; ring < 3; ring++) {
            float r = 3.4f + ring * 1.5f;
            int segs = 26 + ring * 10;
            for (int i = 0; i < segs; i++) {
                if ((i & 1) == 1 && ring != 1) continue;         // dashed outer and inner courses
                float a0 = i / (float)segs * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)segs * Mathf.PI * 2f;
                var p0 = center + new Vector3(Mathf.Cos(a0) * r, y, Mathf.Sin(a0) * r);
                var p1 = center + new Vector3(Mathf.Cos(a1) * r, y, Mathf.Sin(a1) * r);
                var mid = (p0 + p1) * 0.5f;
                float len = Vector3.Distance(p0, p1);
                _paving.GroundQuad(mid, 0.55f, len * 1.05f,
                                   Mathf.Atan2(p1.x - p0.x, p1.z - p0.z) * Mathf.Rad2Deg);
            }
        }
    }

    /// <summary>
    /// A parking meter: a post, a head and a coin slot. One of the small things that only registers
    /// by its absence - a kerb with cars parked along it and nothing to pay is subtly wrong.
    /// </summary>
    public void ParkingMeter(Vector3 basePos, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        _steel.Tube(basePos, basePos + Vector3.up * 1.05f, 0.05f, 6);
        _steel.Box(basePos + Vector3.up * 1.22f, new Vector3(0.22f, 0.34f, 0.16f), rot);
        _steel.Box(basePos + Vector3.up * 1.42f, new Vector3(0.26f, 0.06f, 0.2f), rot);
        Contact(basePos, 0.3f);
    }

    /// <summary>A bike rack: two hoops and the frames still locked to them.</summary>
    public void BikeRack(Vector3 basePos, float yaw, int hoops) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        for (int i = 0; i < hoops; i++) {
            var c = basePos + right * ((i - (hoops - 1) * 0.5f) * 1.0f);
            // A staple hoop: two uprights and a curve, approximated by three members.
            _steel.Tube(c - right * 0.32f, c - right * 0.32f + Vector3.up * 0.7f, 0.035f, 5);
            _steel.Tube(c + right * 0.32f, c + right * 0.32f + Vector3.up * 0.7f, 0.035f, 5);
            _steel.Tube(c - right * 0.32f + Vector3.up * 0.7f, c + right * 0.32f + Vector3.up * 0.7f, 0.035f, 5);
        }
        Contact(basePos, hoops * 0.55f, yaw);
    }

    /// <summary>Anti-ram bollards along a kerb. Short, so they read as detail rather than cover.</summary>
    public void Bollards(Vector3 from, Vector3 to, float spacing) {
        float len = Vector3.Distance(from, to);
        int n = Mathf.Max(2, Mathf.RoundToInt(len / spacing));
        for (int i = 0; i <= n; i++) {
            var p = Vector3.Lerp(from, to, i / (float)n);
            _steel.Tube(p, p + Vector3.up * 0.92f, 0.11f, 8);
            _steel.Disc(p + Vector3.up * 0.95f, Vector3.up, 0.13f, 8);
            _steel.Box(p + Vector3.up * 0.72f, new Vector3(0.26f, 0.07f, 0.26f));
        }
    }

    /// <summary>
    /// Hazard tape strung between stakes around something you are not meant to walk into. The one
    /// deliberately loud colour in the level: a crater on its own is a dark patch that reads as a
    /// texture, and a cordon round it reads as damage somebody arrived to deal with.
    /// </summary>
    public void Cordon(Vector3 center, float radius, int posts) {
        var pts = new Vector3[posts];
        for (int i = 0; i < posts; i++) {
            float a = i / (float)posts * Mathf.PI * 2f + Rnd(-0.12f, 0.12f);
            pts[i] = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius * Rnd(0.92f, 1.1f);
            _wire.Tube(pts[i], pts[i] + Vector3.up * 1.05f, 0.03f, 4);
        }
        for (int i = 0; i < posts; i++) {
            var a = pts[i] + Vector3.up * 0.9f;
            var b = pts[(i + 1) % posts] + Vector3.up * 0.9f;
            // A ribbon, not a wire: a flat strip catches the light and reads as tape.
            var dir = (b - a);
            float len = dir.magnitude;
            if (len < 0.1f) continue;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            _hazard.Box((a + b) * 0.5f - Vector3.up * (len * 0.02f),
                        new Vector3(0.012f, 0.1f, len), Quaternion.Euler(0f, yaw, 0f));
        }
    }

    /// <summary>A manhole cover, flush with the road. Cheap, and it breaks up bare asphalt.</summary>
    public void Manhole(Vector3 pos) {
        _steel.Disc(pos + Vector3.up * 0.035f, Vector3.up, 0.42f, 12);
        _steel.Tube(pos + Vector3.up * 0.005f, pos + Vector3.up * 0.035f, 0.44f, 12);
    }

    /// <summary>
    /// The soft dark patch where a prop meets the ground.
    ///
    /// This is the cheapest large improvement available to the whole level, and it is not really
    /// about dirt. Nothing here casts a contact shadow: the sun is a single low directional light
    /// and SSAO only bites in creases, so a parked car, a bench and a planter all sit on the paving
    /// with a hard silhouette edge and nothing under them, and the eye reads that as *floating*.
    /// One darkened blob under each of them puts the whole street back on the ground.
    ///
    /// All of them share one mesh and one texture, so several hundred cost a single draw call.
    /// </summary>
    public void Contact(Vector3 pos, float radius, float yaw = 0f) {
        if (radius <= 0.05f) return;
        // Two concentric discs of plain lit geometry, a darker one inside a lighter one. A single
        // textured quad with a radial alpha would be softer, and that is what this was; it is not
        // what it is any more, because alpha-blended materials built during the level build render
        // **opaque** in the WebGL player however they are constructed, and a soft shadow that comes
        // out as a hard black rectangle is worse than no shadow at all. Opaque geometry cannot fail
        // that way, and two rings are enough of a gradient to read as contact.
        _grime.Disc(pos + Vector3.up * 0.017f, Vector3.up, radius, 14);
        _grimeInner.Disc(pos + Vector3.up * 0.019f, Vector3.up, radius * 0.58f, 12);
    }

    /// <summary>
    /// A radial falloff, opaque black at the centre and clear at the rim: the alpha mask behind
    /// every contact patch and every pavement spill.
    ///
    /// It is a shipped PNG rather than a Texture2D built at runtime, and that is not a style
    /// preference. Generated at runtime it bound cleanly - the diagnostic line printed `tex=bound`
    /// in the player - and then rendered in the WebGL build as though the sampler were returning
    /// solid white: every soft blob came back a hard black rectangle, and two overlapping ones came
    /// back opaque. In the Editor the same code was correct. An imported asset takes the same path
    /// as the bullet and scorch sheets, which have always been right in the build.
    /// </summary>
    static Texture2D Falloff() => Resources.Load<Texture2D>("Decals/falloff");

    /// <summary>
    /// Road surface history: a patched trench, a crack running off it, a skid.
    ///
    /// The asphalt is one tiled texture stretched over 600 metres, and at the grazing angle you
    /// actually look down a street at, it samples into a single flat grey. Everything else in the
    /// level got detail and the road got none, which is why the bottom third of a street frame was
    /// empty. These are flat quads a few millimetres proud of it, so they cost nothing and they
    /// give the surface a history.
    /// </summary>
    public void RoadPatch(Vector3 pos, float yaw) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        float w = Rnd(1.2f, 3.4f), l = Rnd(2.5f, 9f);
        _tar.GroundQuad(pos + Vector3.up * 0.020f, w, l, yaw);

        // A crack wandering out of one end, in short straight runs.
        var p = pos + rot * new Vector3(0f, 0f, l * 0.5f);
        float dir = yaw + Rnd(-40f, 40f);
        int runs = Mathf.RoundToInt(Rnd(2f, 5f));
        for (int i = 0; i < runs; i++) {
            float len = Rnd(0.8f, 2.6f);
            var d = Quaternion.Euler(0f, dir, 0f) * Vector3.forward;
            _crack.GroundQuad(p + d * (len * 0.5f) + Vector3.up * 0.026f, Rnd(0.05f, 0.13f), len, dir);
            p += d * len;
            dir += Rnd(-45f, 45f);
        }
    }

    /// <summary>A pair of skid marks, from something that stopped hard or never stopped at all.</summary>
    public void Skid(Vector3 pos, float yaw, float length) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        for (float s = -1f; s <= 1f; s += 2f)
            _crack.GroundQuad(pos + rot * new Vector3(0.78f * s, 0f, 0f) + Vector3.up * 0.023f,
                              0.22f, length, yaw);
    }

    /// <summary>
    /// A puddle: dark, damp, and irregular.
    ///
    /// Two things had to be given up here. A near-mirror finish came back as a flat pale ellipse,
    /// because there is no reflection probe in this scene and the only thing a smooth surface has
    /// to reflect is the uniform ambient - so instead of a puddle it rendered as a spotlight on the
    /// floor. And a perfect circle reads as a decal however it is shaded. Damp and ragged catches
    /// the lamps and the neon without pretending to be water it cannot render.
    /// </summary>
    public void Puddle(Vector3 pos, float radius) {
        var c = pos + Vector3.up * 0.032f;
        const int sides = 13;
        var rim = new Vector3[sides];
        for (int i = 0; i < sides; i++) {
            float a = i / (float)sides * Mathf.PI * 2f;
            float r = radius * Rnd(0.55f, 1f);
            rim[i] = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r * Rnd(0.6f, 1f));
        }
        // Fan blades wound centre - next - current, which is what faces the result upward for a
        // rim generated anticlockwise in (cos, sin).
        for (int i = 0; i < sides; i++) _wet.Triangle(c, rim[(i + 1) % sides], rim[i]);
    }

    /// <summary>
    /// A blast crater: scorched ground, a ring of thrown-up spoil, and debris chunks around the rim.
    /// The scorch is a flat disc rather than a decal so it survives on any surface height.
    /// </summary>
    public void Crater(Vector3 pos, float radius) {
        _scorch.Disc(pos + Vector3.up * 0.04f, Vector3.up, radius, 20);
        int chunks = Mathf.RoundToInt(radius * 4f);
        for (int i = 0; i < chunks; i++) {
            float a = i / (float)chunks * Mathf.PI * 2f + Rnd(-0.2f, 0.2f);
            float r = radius * Rnd(0.72f, 1.0f);
            var p = pos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
            var size = new Vector3(Rnd(0.3f, 0.8f), Rnd(0.15f, 0.45f), Rnd(0.3f, 0.8f));
            _concrete.Box(p + Vector3.up * (size.y * 0.4f), size,
                          Quaternion.Euler(Rnd(-25f, 25f), Rnd(0f, 360f), Rnd(-25f, 25f)));
        }
        for (int i = 0; i < chunks / 2; i++) {
            var p = pos + new Vector3(Rnd(-radius * 1.6f, radius * 1.6f), 0f, Rnd(-radius * 1.6f, radius * 1.6f));
            _concrete.Box(p + Vector3.up * 0.08f, Vector3.one * Rnd(0.14f, 0.36f),
                          Quaternion.Euler(Rnd(0f, 360f), Rnd(0f, 360f), Rnd(0f, 360f)));
        }
    }

    /// <summary>
    /// Wind-blown litter and blast debris over an area: chips of masonry, kerbstones, sheets of
    /// paper lying flat.
    ///
    /// The paving textures already carry joints and grain, so what a bare slab is missing is not
    /// detail but *incident* - something with its own silhouette and its own shadow, at a scale the
    /// eye can measure the ground against. A dozen 20 cm chunks does more for a plaza than any
    /// amount of extra texture resolution.
    /// </summary>
    public void Debris(Vector3 center, float radius, int count, float y = 0f) {
        for (int i = 0; i < count; i++) {
            float a = Rnd(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(Rnd(0f, 1f)) * radius;          // even over the disc, not bunched
            var p = center + new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            if (Chance(0.35f)) {
                // A flat sheet: paper, a torn poster, a panel off something. Pale, because a dark
                // rectangle lying flat on pale paving reads as a hole rather than as litter.
                _litter.Box(p + Vector3.up * 0.026f, new Vector3(Rnd(0.18f, 0.42f), 0.01f, Rnd(0.16f, 0.36f)),
                            Quaternion.Euler(0f, Rnd(0f, 360f), 0f));
            } else {
                var size = new Vector3(Rnd(0.09f, 0.26f), Rnd(0.05f, 0.16f), Rnd(0.09f, 0.26f));
                _concrete.Box(p + Vector3.up * (size.y * 0.45f), size,
                              Quaternion.Euler(Rnd(-20f, 20f), Rnd(0f, 360f), Rnd(-20f, 20f)));
            }
        }
    }

    /// <summary>A scorch mark with no crater under it - a stray round, a burnt-out fire.</summary>
    public void ScorchPatch(Vector3 pos, float radius) {
        _scorch.Disc(pos + Vector3.up * 0.038f, Vector3.up, radius, 12);
    }

    /// <summary>
    /// A razor wire coil along the top of a barricade line: a flattened helix. It is the detail that
    /// turns a row of concrete blocks into a defensive position.
    /// </summary>
    public void RazorWire(Vector3 from, Vector3 to, float radius) {
        var dir = to - from;
        float len = dir.magnitude;
        if (len < 0.5f) return;
        dir /= len;
        var side = Vector3.Cross(Vector3.up, dir).normalized;

        // Turns close enough together to read as a coil. At one turn per 1.5 radii the loops are
        // further apart than they are wide and the result is a row of hoops; real concertina is
        // wound at roughly one turn per radius, and each loop needs ten-plus segments or it comes
        // out as a visible heptagon.
        int turns = Mathf.Max(4, Mathf.RoundToInt(len / (radius * 0.7f)));
        int steps = turns * 10;
        var prev = from;
        for (int i = 1; i <= steps; i++) {
            float k = i / (float)steps;
            float ang = k * turns * Mathf.PI * 2f;
            var p = from + dir * (len * k)
                  + Vector3.up * (radius + Mathf.Sin(ang) * radius)
                  + side * (Mathf.Cos(ang) * radius);
            _wire.Tube(prev, p, 0.022f, 3);
            prev = p;
        }
    }

    /// <summary>Scaffolding against a facade: standards, ledgers, boards and a debris net.</summary>
    public void Scaffold(Vector3 basePos, float yaw, float width, float height, int lifts) {
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var right = rot * Vector3.right;
        var outward = rot * Vector3.forward;
        int bays = Mathf.Max(1, Mathf.RoundToInt(width / 2.4f));
        float lift = height / lifts;

        for (int b = 0; b <= bays; b++) {
            var x = right * (width * (b / (float)bays - 0.5f));
            for (int d = 0; d < 2; d++) {
                var p = basePos + x + outward * (d * 1.25f);
                _steel.Tube(p, p + Vector3.up * height, 0.05f, 5);
            }
        }
        for (int l = 1; l <= lifts; l++) {
            float y = l * lift;
            var a = basePos + right * (-width * 0.5f) + Vector3.up * y;
            var bEnd = basePos + right * (width * 0.5f) + Vector3.up * y;
            _steel.Tube(a, bEnd, 0.045f, 5);
            _steel.Tube(a + outward * 1.25f, bEnd + outward * 1.25f, 0.045f, 5);
            _steel.Tube(a + Vector3.up * 0.5f, bEnd + Vector3.up * 0.5f, 0.04f, 4);
            // Boards along the working lift.
            _wood.Box(basePos + Vector3.up * (y + 0.06f) + outward * 0.62f,
                      new Vector3(width, 0.06f, 1.1f), rot);
            for (int b = 0; b < bays; b++) {
                var p0 = basePos + right * (width * (b / (float)bays - 0.5f)) + Vector3.up * (y - lift);
                var p1 = basePos + right * (width * ((b + 1) / (float)bays - 0.5f)) + Vector3.up * y;
                _steel.Tube(p0, p1, 0.035f, 4);
            }
        }
        if (_arena != null)
            _arena.Add(ArenaBox.FromCenter(basePos + outward * 0.6f + Vector3.up * (height * 0.5f),
                                           new Vector3(width, height, 1.6f)));
    }

    /// <summary>Construction hoarding / site fence: plywood panels on a stud frame.</summary>
    public void Hoarding(Vector3 from, Vector3 to, float height) {
        var dir = to - from;
        float len = dir.magnitude;
        if (len < 1f) return;
        dir /= len;
        var side = Vector3.Cross(Vector3.up, dir).normalized;
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(0f, yaw, 0f);

        int panels = Mathf.Max(1, Mathf.RoundToInt(len / 2.4f));
        for (int i = 0; i < panels; i++) {
            var c = Vector3.Lerp(from, to, (i + 0.5f) / panels);
            _wood.Box(c + Vector3.up * (height * 0.5f), new Vector3(len / panels - 0.05f, height, 0.09f), rot);
            // A raking prop behind every other panel.
            if (i % 2 == 0)
                _wood.Tube(c + Vector3.up * height * 0.85f, c + side * 1.1f, 0.06f, 4);
        }
        if (_arena != null) {
            var mn = Vector3.Min(from, to) - side * 0.3f;
            var mx = Vector3.Max(from, to) + side * 0.3f;
            _arena.Add(new ArenaBox(new Vector3(mn.x, 0f, mn.z), new Vector3(mx.x, height, mx.z)));
        }
    }

    /// <summary>A steam plume out of a vent or manhole. Quietly the most "city at night" thing here.</summary>
    public void SteamVent(Vector3 pos) {
        _steel.Tube(pos + Vector3.up * 0.02f, pos + Vector3.up * 0.55f, 0.36f, 10);
        _steel.Disc(pos + Vector3.up * 0.55f, Vector3.up, 0.36f, 10);

        var go = new GameObject("SteamVent");
        go.transform.SetParent(_parent, false);
        go.transform.position = pos + Vector3.up * 0.6f;
        go.AddComponent<NoBatch>();
        go.AddComponent<SteamEmitter>();
    }

    /// <summary>
    /// The visible cone under a street lamp. The light itself already pools on the road; the cone is
    /// what makes a lamp read at a distance, where the pool is too small and too dim to see.
    /// </summary>
    public void LightShaft(Vector3 groundPos, float radius, float height) {
        _shaft.Cone(groundPos, radius, height, 10);
    }

    // ================================================================= helpers

    /// <summary>An emissive panel. Its own object because each one carries its own colour.</summary>
    void GlowQuad(Vector3 center, Quaternion rot, float width, float height, Color glow) {
        var go = Prim.Create(PrimKind.Quad, "Glow", _parent);
        go.transform.position = center;
        go.transform.rotation = rot;
        go.transform.localScale = new Vector3(width, height, 1f);
        go.GetComponent<Renderer>().material = GlowMaterial(glow);
    }

    /// <summary>
    /// A small emissive marker - an aircraft beacon, a warning lamp. <paramref name="blink"/> gives
    /// it its own material and a pulse: a mast beacon that does not blink is a red dot, and one
    /// that does is the only thing moving on a skyline otherwise frozen in place.
    /// </summary>
    void Beacon(Vector3 pos, Color color, float size, bool blink = false) {
        var go = Prim.Create(PrimKind.Sphere, "Beacon", _parent);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * size;
        var r = go.GetComponent<Renderer>();
        if (!blink) { r.material = GlowMaterial(color * 2.5f); return; }

        r.material = Fx.AdditiveTinted(color * 2.5f);
        var pulse = go.AddComponent<BeaconPulse>();
        pulse.Bright = color * 3.2f;
        pulse.Dim = color * 0.25f;
        pulse.Period = Rnd(1.6f, 2.8f);
        pulse.Phase = Rnd(0f, 10f);
    }

    Material GlowMaterial(Color c) {
        if (_glowCache.TryGetValue(c, out var m)) return m;
        m = Fx.AdditiveTinted(c);
        // Double-sided: these panels hang off facades and gantries at rotations the placement code
        // works out, and a one-sided quad that guessed wrong is simply not there. Culling nothing
        // costs nothing on a handful of signs and removes the whole class of bug.
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        m.doubleSidedGI = true;
        _glowCache[c] = m;
        return m;
    }

    // ================================================================= output

    /// <summary>Turn every accumulated kit into one renderer apiece. Call once, after all placement.</summary>
    public void Flush() {
        Emit(_steel, "Props_Steel", MakeMat(new Color(0.20f, 0.21f, 0.24f), 0.28f, 0.85f), shadows: true);
        Emit(_wire, "Props_Wire", MakeMat(new Color(0.09f, 0.09f, 0.10f), 0.30f, 0.6f), shadows: false);
        Emit(_concrete, "Props_Concrete", MakeMat(new Color(0.42f, 0.41f, 0.39f), 0.06f, 0f), shadows: true);
        Emit(_wood, "Props_Wood", MakeMat(new Color(0.22f, 0.15f, 0.11f), 0.08f, 0f), shadows: true);
        Emit(_rust, "Props_Rust", MakeMat(new Color(0.27f, 0.20f, 0.16f), 0.18f, 0.5f), shadows: true);
        // These four all lie flat on paving that sits at 0.62, and all four started out near black
        // because that is what scorching and standing water "are". On a pale plaza floor a 0.03
        // albedo is not a mark on the ground, it is a hole cut in it - and the holes were only
        // obvious from the player's own camera, never from the angles the shot suites use. Wet
        // concrete is about half the albedo of dry concrete, and a burn is a stain, not a void.
        Emit(_scorch, "Props_Scorch", MakeMat(new Color(0.130f, 0.120f, 0.120f), 0.10f, 0f), shadows: false);
        Emit(_wet, "Props_Wet", MakeMat(new Color(0.160f, 0.170f, 0.190f), 0.85f, 0f), shadows: false);
        Emit(_litter, "Props_Litter", MakeMat(new Color(0.55f, 0.53f, 0.48f), 0.05f, 0f), shadows: false);
        Emit(_hazard, "Props_Hazard", MakeMat(new Color(0.85f, 0.62f, 0.05f), 0.25f, 0f), shadows: false);
        Emit(_tar, "Props_Tarmac", MakeMat(new Color(0.105f, 0.104f, 0.110f), 0.14f, 0f), shadows: false);
        Emit(_crack, "Props_Cracks", MakeMat(new Color(0.045f, 0.045f, 0.048f), 0.06f, 0f), shadows: false);
        // Granite against the pale concrete. Both ends of this were wrong once: at 0.34 the banding
        // was within a few percent of the paving it was laid into and the pattern did not read at
        // all, and at 0.19 - chosen off a deliberately brightened plan view - it read at eye level
        // as black holes cut in the plaza floor. The plan view is a diagnostic; the number belongs
        // to the eye-level frame.
        Emit(_paving, "Props_Paving", MakeMat(new Color(0.33f, 0.33f, 0.315f), 0.16f, 0f), shadows: false);
        Emit(_paint, "Props_Paint", MakeMat(new Color(0.72f, 0.71f, 0.66f), 0.10f, 0f), shadows: false);
        Emit(_grime, "Props_Contact", MakeMat(new Color(0.205f, 0.205f, 0.200f), 0.04f, 0f), shadows: false);
        Emit(_grimeInner, "Props_ContactCore", MakeMat(new Color(0.135f, 0.135f, 0.133f), 0.04f, 0f), shadows: false);
        Emit(_shaft, "Props_LightShafts", Fx.AdditiveTinted(new Color(1f, 0.87f, 0.60f) * 0.055f), shadows: false);
        Emit(_bulb, "Props_Bulbs", Fx.AdditiveTinted(new Color(1f, 0.78f, 0.45f) * 1.9f), shadows: false);
        Emit(_glazing, "Props_Glazing", Fx.AdditiveTinted(new Color(1f, 0.80f, 0.52f) * 0.85f), shadows: false);
        Emit(_spill, "Props_Spill", SpillMaterial(), shadows: false);

    }

    /// <summary>
    /// The warm pool a shopfront throws on the pavement.
    ///
    /// Additive, and softened by the falloff sheet - which is why that sheet is a white radial
    /// gradient on black rather than black with a radial alpha. Additive blending multiplies by the
    /// texture's **colour**, so a black sheet with a perfect alpha ramp adds exactly nothing, which
    /// is what the first version of this did.
    /// </summary>
    static Material SpillMaterial() {
        // Additive is the one blend mode that is demonstrably intact in the build - every neon sign
        // and glow panel uses it - so the spill keeps it.
        var m = Fx.AdditiveTinted(new Color(1f, 0.74f, 0.42f, 1f) * 0.22f);
        if (m == null) return null;
        var tex = Falloff();
        m.mainTexture = tex;
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 4;
        return m;
    }

    static Material MakeMat(Color baseColor, float smoothness, float metallic) {
        var m = Prim.Lit();
        if (m == null) return null;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseColor);
        m.color = baseColor;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        return m;
    }

    void Emit(MeshKit kit, string name, Material mat, bool shadows) {
        if (kit.Empty) return;
        var go = new GameObject(name);
        go.transform.SetParent(_parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = kit.Build(name);
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = mat;
        mr.shadowCastingMode = shadows
            ? UnityEngine.Rendering.ShadowCastingMode.On
            : UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = true;
    }
}

/// <summary>
/// Marks an object whose transform is animated, so <c>LevelBuilder</c> leaves it out of static
/// batching. Batching bakes the transform into the combined mesh, which silently freezes anything
/// that moves - the same trap the mothership's spin hit.
/// </summary>
public class NoBatch : MonoBehaviour { }

/// <summary>An aircraft warning beacon, on and off on its own clock.</summary>
public class BeaconPulse : MonoBehaviour {
    public Color Bright = Color.red, Dim = Color.black;
    public float Period = 2f, Phase;
    Material _mat;

    void Awake() {
        var r = GetComponent<Renderer>();
        if (r != null) _mat = r.material;
    }

    void Update() {
        if (_mat == null) return;
        // A short flash and a long gap, not a sine: that is what an obstruction light does.
        float t = Mathf.Repeat((Time.time + Phase) / Mathf.Max(0.1f, Period), 1f);
        float k = t < 0.18f ? Mathf.Sin(t / 0.18f * Mathf.PI) : 0f;
        var c = Color.Lerp(Dim, Bright, k);
        if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", c);
        _mat.color = c;
    }
}

/// <summary>A searchlight head panning back and forth across the sky.</summary>
public class SweepLight : MonoBehaviour {
    public Transform Head;
    public float DegreesPerSecond = 18f;
    public float Pitch = 30f;
    public float Arc = 110f;
    float _phase;

    void Awake() { _phase = Random.value * Mathf.PI * 2f; }

    void Update() {
        if (Head == null) return;
        _phase += DegreesPerSecond * Mathf.Deg2Rad * Time.deltaTime;
        float yaw = Mathf.Sin(_phase) * Arc * 0.5f;
        Head.localRotation = Quaternion.Euler(-Pitch + Mathf.Sin(_phase * 1.7f) * 6f, yaw, 0f);
    }
}

/// <summary>
/// A steam plume from a street vent, driven off the shared Fx pool. Rates are low on purpose: the
/// pool also serves combat, and a plume only has to be a suggestion.
/// </summary>
public class SteamEmitter : MonoBehaviour {
    public float Rate = 13f;
    public float MaxDistance = 55f;
    Fx _fx;
    Transform _viewer;
    float _acc;

    void Start() {
        if (GameManager.Instance != null) {
            _fx = GameManager.Instance.Fx;
            if (GameManager.Instance.Player != null) _viewer = GameManager.Instance.Player.transform;
        }
    }

    void Update() {
        if (_fx == null) return;
        if (_viewer != null && Vector3.Distance(_viewer.position, transform.position) > MaxDistance) return;
        _acc += Rate * Time.deltaTime;
        while (_acc >= 1f) {
            _acc -= 1f;
            var o = new Vector3(Random.Range(-0.2f, 0.2f), 0f, Random.Range(-0.2f, 0.2f));
            _fx.Steam(transform.position + o);
        }
    }
}

}
