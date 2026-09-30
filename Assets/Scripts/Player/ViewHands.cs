using UnityEngine;

namespace UFO {

/// <summary>
/// The player's arms: a pair of two-bone IK limbs that hold whatever viewmodel is up, and act out
/// the reload.
///
/// There is no rigged arms asset in the catalogue - v2 never had one, and its weapons floated in
/// the corner of the screen unattached to anything. So these are built from primitives, the same
/// way the street props are, and they are driven by inverse kinematics rather than by animation
/// clips. That choice is what makes the reload possible at all: a reload is a script of **hand
/// positions** ("left hand to the mag well, down to the belt, back up, insert, charge"), and with
/// IK each of those is one target to move the wrist to. With forward kinematics it would be a set
/// of per-joint Euler curves that would have to be re-authored for every weapon.
///
/// Anchoring matters as much as the solve. The shoulders are fixed to the **camera**, because they
/// belong to the player's body; the hands are pinned to the **weapon**, which is already being
/// moved by sway, bob and recoil. The elbows then flex on their own, and the arms inherit every
/// bit of weapon motion for free without a line of code that knows about it.
/// </summary>
public class ViewHands : MonoBehaviour {

    /// <summary>Where each hand grips the weapon, in the viewmodel holder's own space.</summary>
    public struct Grip {
        public Vector3 Right, Left;
        public Vector3 RightAim, LeftAim;   // hand yaw/pitch/roll in the same space
        public bool TwoHanded;
    }

    // Tuned against the fitted viewmodels, which are normalised to WeaponDef.Scale - so these are
    // in metres on a weapon whose longest dimension is about half a metre.
    static Grip GripFor(string key) => key switch {
        // The y values sit the wrist BELOW the weapon body. A grip point on the model's centre
        // line puts the whole hand inside the mesh, which is where the first pass put it: the arms
        // were correct and completely invisible. The pitch curls the fingers up around the grip -
        // a hand whose fingers point forward is a hand held flat next to a gun, not one holding it.
        "rifle" => new Grip {
            Right = new Vector3(0.00f, -0.105f, -0.09f), RightAim = new Vector3(-62f, 0f, 0f),
            Left = new Vector3(-0.02f, -0.095f, 0.14f), LeftAim = new Vector3(-68f, 0f, 0f),
            TwoHanded = true,
        },
        "plasmaRifle" => new Grip {
            Right = new Vector3(0.00f, -0.110f, -0.09f), RightAim = new Vector3(-62f, 0f, 0f),
            Left = new Vector3(-0.02f, -0.100f, 0.12f), LeftAim = new Vector3(-68f, 0f, 0f),
            TwoHanded = true,
        },
        "rocketLauncher" => new Grip {
            Right = new Vector3(0.00f, -0.125f, -0.05f), RightAim = new Vector3(-60f, 0f, 0f),
            Left = new Vector3(-0.03f, -0.105f, 0.16f), LeftAim = new Vector3(-66f, 0f, 0f),
            TwoHanded = true,
        },
        // One-handed: the off hand is free, and a second hand on a sword hilt looks like a
        // two-handed grip on a one-handed weapon.
        _ => new Grip {
            Right = new Vector3(0.00f, -0.085f, -0.11f), RightAim = new Vector3(-58f, 0f, 0f),
            TwoHanded = false,
        },
    };

    // ------------------------------------------------------------------ rig

    class Arm {
        public Transform Upper, Fore, Hand, Elbow, Wrist;
        public Vector3 Shoulder;          // camera-local
        public float UpperLen = 0.26f, ForeLen = 0.28f;
        public Vector3 Pole;              // camera-local direction the elbow is pushed toward
        public Vector3 SmoothTarget;
        public bool Placed;
    }

    Arm _r, _l;
    Transform _root;
    Camera _cam;

    Material _sleeve, _glove, _cuff;

    /// <summary>Set by WeaponManager each frame: the live viewmodel holder and its weapon key.</summary>
    public Transform Weapon;
    public string WeaponKey = "rifle";

    /// <summary>0 when not reloading, else 0..1 through the reload.</summary>
    public float ReloadT;
    public bool Reloading;
    public bool Hidden;

    // ------------------------------------------------------------------ build

    public void Build(Camera cam) {
        _cam = cam;
        _root = new GameObject("ViewHands").transform;
        _root.SetParent(cam.transform, false);

        _sleeve = Lit(new Color(0.118f, 0.128f, 0.112f), 0.09f);   // fatigue green, night-graded
        _glove = Lit(new Color(0.042f, 0.044f, 0.050f), 0.22f);    // black tactical glove
        _cuff = Lit(new Color(0.140f, 0.140f, 0.128f), 0.12f);     // the band between the two

        _r = MakeArm("R", 1f);
        _l = MakeArm("L", -1f);
    }

    static Material Lit(Color c, float smooth) {
        var m = Prim.Lit();
        if (m == null) return null;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        // Match the weapons: drawn after the world so they are not clipped by whatever the player
        // happens to be standing against.
        m.renderQueue = 3100;
        return m;
    }

    Arm MakeArm(string side, float sx) {
        var a = new Arm {
            // Just behind the near plane and well down, so the upper arm is clipped away and the
            // forearms enter from the bottom corners of the frame - which is where arms come from
            // in every first-person game, and is not where a literally-placed shoulder puts them.
            Shoulder = new Vector3(0.24f * sx, -0.40f, -0.06f),
            Pole = new Vector3(0.95f * sx, -0.75f, -0.15f).normalized,
        };
        if (sx < 0f) {
            // The off hand reaches across the body to a weapon held on the right, which is a
            // noticeably longer span than the firing hand's. Lengthening the bone is the honest
            // fix; the alternative is a forearm that visibly stretches every frame.
            a.UpperLen = 0.30f;
            a.ForeLen = 0.36f;
        }

        a.Upper = Segment(side + "Upper", 0.052f, 0.042f, _sleeve);
        // Thinner than the first pass. At 0.048 the forearm came out the diameter of a drainpipe
        // and the whole arm read as plumbing rather than as a limb.
        a.Fore = Segment(side + "Fore", 0.043f, 0.033f, _sleeve);
        a.Elbow = Joint(side + "Elbow", 0.049f, _sleeve);
        // A cuff at the wrist rather than a bare joint: it is the one silhouette break along an
        // otherwise smooth tube, and it is what says the arm is dressed.
        a.Wrist = Joint(side + "Wrist", 0.046f, _cuff);
        a.Hand = HandObject(side + "Hand", sx);
        return a;
    }

    /// <summary>A limb bone: a tapered tube one unit long down +Z, stretched by the transform.</summary>
    Transform Segment(string name, float r0, float r1, Material mat) {
        var kit = new MeshKit();
        const int rings = 5;
        for (int i = 0; i < rings; i++) {
            float k0 = i / (float)rings, k1 = (i + 1) / (float)rings;
            kit.Tube(new Vector3(0f, 0f, k0), new Vector3(0f, 0f, k1),
                     Mathf.Lerp(r0, r1, (k0 + k1) * 0.5f), 8);
        }
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        go.AddComponent<MeshFilter>().sharedMesh = kit.Build(name);
        Dress(go.AddComponent<MeshRenderer>(), mat);
        return go.transform;
    }

    Transform Joint(string name, float radius, Material mat) {
        var go = Prim.Create(PrimKind.Sphere, name, _root);
        go.transform.localScale = Vector3.one * (radius * 2f);
        Dress(go.GetComponent<MeshRenderer>(), mat);
        return go.transform;
    }

    /// <summary>
    /// A gloved hand, in hand space: the wrist at the origin, +Z along the fingers, +Y out of the
    /// back of the hand, +X toward the thumb. The left hand is the same mesh mirrored in X.
    /// </summary>
    Transform HandObject(string name, float sx) {
        var kit = new MeshKit();
        kit.Box(new Vector3(0f, 0f, 0.055f), new Vector3(0.085f, 0.048f, 0.105f));

        // Four fingers as one curled block, which is what a gloved hand round a grip actually
        // looks like; modelling four separate fingers at this size only produces four slivers.
        kit.Box(new Vector3(0f, -0.012f, 0.125f), new Vector3(0.082f, 0.040f, 0.055f),
                Quaternion.Euler(-38f, 0f, 0f));
        kit.Box(new Vector3(0f, -0.038f, 0.132f), new Vector3(0.078f, 0.036f, 0.048f),
                Quaternion.Euler(-78f, 0f, 0f));
        // Thumb, wrapped the other way round the grip.
        kit.Tube(new Vector3(0.042f, 0.004f, 0.055f), new Vector3(0.030f, -0.030f, 0.110f), 0.020f, 6, caps: true);

        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        go.AddComponent<MeshFilter>().sharedMesh = kit.Build(name);
        Dress(go.AddComponent<MeshRenderer>(), _glove);
        // Mirroring flips the winding, so the left hand would render inside out; scaling the parent
        // is not an option either. Rotating 180 degrees about Z gets a usable left hand out of the
        // same mesh and keeps the winding intact.
        if (sx < 0f) go.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
        return go.transform;
    }

    static void Dress(MeshRenderer r, Material mat) {
        r.material = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    // ------------------------------------------------------------------ solve

    /// <summary>
    /// Two-bone IK. Returns the elbow: the point at <paramref name="upper"/> from the shoulder, at
    /// the angle the law of cosines gives for a triangle closing on the wrist, swung into the plane
    /// the pole picks out. Everything is in camera space.
    /// </summary>
    static Vector3 SolveElbow(Vector3 shoulder, Vector3 wrist, float upper, float fore, Vector3 pole) {
        var axis = wrist - shoulder;
        float d = axis.magnitude;
        if (d < 1e-4f) { axis = Vector3.forward; d = 1e-4f; }
        axis /= d;
        // Clamped so a fully extended or fully folded arm still has a defined triangle.
        d = Mathf.Clamp(d, Mathf.Abs(upper - fore) + 1e-3f, upper + fore - 1e-3f);

        float cos = (upper * upper + d * d - fore * fore) / (2f * upper * d);
        float ang = Mathf.Acos(Mathf.Clamp(cos, -1f, 1f));

        var perp = pole - axis * Vector3.Dot(pole, axis);
        if (perp.sqrMagnitude < 1e-6f) perp = Vector3.Cross(axis, Vector3.up);
        perp.Normalize();

        return shoulder + upper * (Mathf.Cos(ang) * axis + Mathf.Sin(ang) * perp);
    }

    void Place(Arm a, Vector3 wrist, Quaternion handRot, float dt) {
        // The wrist is smoothed, not the joints. Smoothing the solved elbow makes the arm stretch
        // and snap; smoothing the target keeps both bones exactly their own length at all times.
        a.SmoothTarget = a.Placed
            ? Vector3.Lerp(a.SmoothTarget, wrist, 1f - Mathf.Exp(-26f * dt))
            : wrist;
        a.Placed = true;

        var elbow = SolveElbow(a.Shoulder, a.SmoothTarget, a.UpperLen, a.ForeLen, a.Pole);

        Stretch(a.Upper, a.Shoulder, elbow);
        Stretch(a.Fore, elbow, a.SmoothTarget);
        a.Elbow.localPosition = elbow;
        a.Wrist.localPosition = a.SmoothTarget;
        a.Hand.localPosition = a.SmoothTarget;
        a.Hand.localRotation = handRot;
    }

    static void Stretch(Transform t, Vector3 from, Vector3 to) {
        var d = to - from;
        float len = d.magnitude;
        t.localPosition = from;
        if (len > 1e-4f) t.localRotation = Quaternion.LookRotation(d / len, Vector3.up);
        t.localScale = new Vector3(1f, 1f, Mathf.Max(len, 1e-3f));
    }

    static void SetActive(Arm a, bool on) {
        a.Upper.gameObject.SetActive(on);
        a.Fore.gameObject.SetActive(on);
        a.Elbow.gameObject.SetActive(on);
        a.Wrist.gameObject.SetActive(on);
        a.Hand.gameObject.SetActive(on);
    }

    // ------------------------------------------------------------------ per frame

    public void Tick(float dt) {
        if (_root == null || _cam == null) return;
        bool show = !Hidden && Weapon != null && Weapon.gameObject.activeInHierarchy;
        SetActive(_r, show);
        SetActive(_l, show && (GripFor(WeaponKey).TwoHanded || Reloading));
        if (!show) return;

        var g = GripFor(WeaponKey);
        var camT = _cam.transform;

        Vector3 ToLocal(Vector3 gripLocal) =>
            camT.InverseTransformPoint(Weapon.TransformPoint(gripLocal));
        Quaternion RotFor(Vector3 aim) =>
            Quaternion.Inverse(camT.rotation) * Weapon.rotation * Quaternion.Euler(aim);

        Place(_r, ToLocal(g.Right), RotFor(g.RightAim), dt);
        if (Reloading && ReloadPath(g, out var lp, out var lr)) {
            Place(_l, lp, lr, dt);
        } else if (g.TwoHanded) {
            Place(_l, ToLocal(g.Left), RotFor(g.LeftAim), dt);
        }
    }

    /// <summary>
    /// The left hand's path through a reload, in camera space.
    ///
    /// Written as a sequence of holds and moves rather than as a curve, because that is how the
    /// action actually reads: the hand is *somewhere* for a moment - on the mag well, at the belt,
    /// on the charging handle - and the eye follows those stops, not the interpolation between
    /// them. Each weapon gets its own script; anything without a magazine has none.
    /// </summary>
    bool ReloadPath(Grip g, out Vector3 pos, out Quaternion rot) {
        pos = default; rot = default;
        if (Weapon == null) return false;

        var camT = _cam.transform;
        float t = Mathf.Clamp01(ReloadT);

        Vector3 W(Vector3 local) => camT.InverseTransformPoint(Weapon.TransformPoint(local));
        Quaternion R(Vector3 aim) => Quaternion.Inverse(camT.rotation) * Weapon.rotation * Quaternion.Euler(aim);

        if (WeaponKey == "rocketLauncher") {
            // Tube reload: reach back for a round, bring it up, push it in.
            var tube = W(new Vector3(-0.04f, 0.02f, 0.16f));
            var back = W(new Vector3(-0.22f, -0.34f, -0.18f));
            var lifted = W(new Vector3(-0.14f, -0.06f, 0.02f));
            pos = t < 0.22f ? Vector3.Lerp(tube, back, Ease(t / 0.22f))
                : t < 0.42f ? back
                : t < 0.62f ? Vector3.Lerp(back, lifted, Ease((t - 0.42f) / 0.20f))
                : t < 0.84f ? Vector3.Lerp(lifted, tube, Ease((t - 0.62f) / 0.22f))
                            : tube;
            rot = R(new Vector3(-34f, 0f, 0f));
            return true;
        }

        if (WeaponKey == "rifle") {
            var fore = W(g.Left);
            var well = W(new Vector3(-0.03f, -0.10f, 0.02f));     // the magazine housing
            var belt = W(new Vector3(-0.16f, -0.42f, -0.10f));    // down out of frame, for a fresh mag
            var charge = W(new Vector3(-0.05f, 0.02f, -0.02f));   // the charging handle

            pos = t < 0.14f ? Vector3.Lerp(fore, well, Ease(t / 0.14f))
                : t < 0.26f ? well                                  // release, mag drops
                : t < 0.44f ? Vector3.Lerp(well, belt, Ease((t - 0.26f) / 0.18f))
                : t < 0.56f ? belt
                : t < 0.74f ? Vector3.Lerp(belt, well, Ease((t - 0.56f) / 0.18f))
                : t < 0.82f ? Vector3.Lerp(well, charge, Ease((t - 0.74f) / 0.08f))
                : t < 0.90f ? charge
                            : Vector3.Lerp(charge, fore, Ease((t - 0.90f) / 0.10f));
            rot = R(new Vector3(-30f, 0f, 0f));
            return true;
        }
        return false;
    }

    static float Ease(float k) {
        k = Mathf.Clamp01(k);
        return k * k * (3f - 2f * k);
    }

    /// <summary>The instant the spent magazine should be let go of, so it can be spawned falling.</summary>
    public const float MagDropAt = 0.26f;
}

}
