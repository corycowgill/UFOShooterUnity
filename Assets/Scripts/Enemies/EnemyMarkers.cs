using UnityEngine;

namespace UFO {

/// <summary>
/// The glowing bits on an alien: eyes, a chest core and vents down the back.
///
/// This is a readability fix before it is a decoration one. The enemies are dark, matte, roughly
/// human-sized models standing on a street that now has lit shopfronts, neon, marker lights on
/// every parked car and a fair amount of litter - and at twenty metres they simply disappeared
/// into it. A capture of the whole roster at twenty metres came back with no identifiable enemy in
/// any frame. Nothing about the city dressing can be undone to fix that, because the problem is
/// that the aliens are the only unlit thing in the picture.
///
/// So they light themselves, in the species colour that the threat light, the bolts and the field
/// guide already use. That does two jobs at once: it separates the unit from the background, and
/// it says *which* unit it is from further away than the silhouette does - which matters in a game
/// whose whole loop is target priority.
///
/// Every marker on one alien is a single mesh with a single additive material, so a wave of thirty
/// costs thirty draw calls, not two hundred.
/// </summary>
public static class EnemyMarkers {

    /// <summary>
    /// Build the marker mesh for one unit. Laid out from the catalogue's height and radius rather
    /// than from the model, because two of the six are not rigged and none of them share a
    /// skeleton - there is no "head bone" to hang these off that exists on every species.
    /// </summary>
    public static Mesh Build(EnemyType d) {
        var kit = new MeshKit();
        float h = d.Height, r = Mathf.Max(0.18f, d.Radius);

        // Eyes: a pair, forward-facing, at the height a head would be. The single most useful
        // marker - a pair of lights reads as something looking at you from much further away than
        // any single light reads as anything at all.
        float eyeY = h * (d.Role == EnemyRole.Aerial ? 0.62f : 0.80f);
        float eyeSep = r * 0.52f;
        float eye = Mathf.Clamp(h * 0.052f, 0.04f, 0.17f);
        for (float s = -1f; s <= 1f; s += 2f)
            kit.Box(new Vector3(eyeSep * s, eyeY, r * 0.80f), new Vector3(eye * 1.6f, eye, eye * 0.7f),
                    Quaternion.Euler(0f, 0f, 9f * s));

        // Chest core: bigger, slower, and the thing that carries at the longest range.
        float core = Mathf.Clamp(h * 0.090f, 0.07f, 0.30f);
        kit.Tube(new Vector3(0f, h * 0.55f, r * 0.55f), new Vector3(0f, h * 0.55f, r * 0.80f), core, 8, caps: true);

        // Vents down the back, which are what identify a unit that is running away from you.
        for (int i = 0; i < 3; i++) {
            float y = h * (0.40f + i * 0.14f);
            kit.Box(new Vector3(0f, y, -r * 0.75f), new Vector3(r * 0.70f, h * 0.022f, h * 0.014f));
        }

        // Shoulder lamps on the big ones. A Juggernaut is wide enough that eyes alone sit in the
        // middle of a silhouette three metres across and read as something much smaller.
        if (h >= 2.2f) {
            for (float s = -1f; s <= 1f; s += 2f)
                kit.Tube(new Vector3(r * 0.85f * s, h * 0.72f, 0f),
                         new Vector3(r * 0.85f * s, h * 0.72f, r * 0.35f), core * 0.6f, 6, caps: true);
        }
        return kit.Build("Markers_" + d.Key);
    }

    /// <summary>Attach the markers to a unit and hand back the pulse that drives them.</summary>
    public static MarkerPulse Attach(Transform parent, EnemyType d, bool elite, bool boss) {
        var go = new GameObject("Markers");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = Build(d);

        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        // Elites are orange whatever they are underneath, matching the elite tint and the threat
        // light - an elite has to be identifiable before its species is.
        var c = elite ? new Color(1f, 0.62f, 0.05f) : d.Color;
        // Measured off a 20 m roster capture rather than guessed: at 2.6 the markers were
        // findable once you knew where to look, which is not the same as readable.
        float gain = boss ? 5.2f : elite ? 4.2f : 3.3f;
        mr.material = Fx.AdditiveTinted(c * gain);

        var p = go.AddComponent<MarkerPulse>();
        p.Base = c * gain;
        p.Renderer = mr;
        p.Speed = boss ? 0.7f : elite ? 2.2f : 1.5f;
        return p;
    }
}

/// <summary>
/// Breathes the markers, and flares them when the unit is hit.
///
/// The flare is the useful half. A hit already flashes the body material white, which is invisible
/// at the range you usually shoot from; pushing the markers to three times brightness for a tenth
/// of a second is visible across the whole arena and is the clearest "yes, that connected" the
/// game has.
/// </summary>
public class MarkerPulse : MonoBehaviour {
    public Color Base = Color.white;
    public Renderer Renderer;
    public float Speed = 1.5f;

    float _phase, _flare;

    void Awake() { _phase = Random.value * 10f; }

    /// <summary>Called on damage. Decays on its own.</summary>
    public void Flare() => _flare = 1f;

    void LateUpdate() {
        if (Renderer == null) return;
        _phase += Time.deltaTime * Speed;
        float breathe = 0.84f + Mathf.Sin(_phase) * 0.16f;
        _flare = Mathf.Max(0f, _flare - Time.deltaTime * 6f);

        var c = Base * (breathe + _flare * 2.2f);
        var m = Renderer.material;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
    }
}

}
