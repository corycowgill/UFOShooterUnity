using UnityEngine;

namespace UFO {

/// <summary>
/// Rain for the levels whose data asks for it. River North is described as "warehouse district in
/// the rain" and its ground is already given a wet sheen, but without the rain itself the label
/// was the only evidence.
///
/// One ParticleSystem, parented to the camera and emitting from a box overhead, so the rain
/// follows the player and only ever simulates the volume they can actually see. That is far
/// cheaper than a world-sized emitter and looks identical from inside it.
/// </summary>
public class Weather : MonoBehaviour {

    ParticleSystem _rain;

    public void Configure(string weather, Transform followCamera) {
        bool wet = weather == "rain";

        if (_rain == null && wet) Build(followCamera);
        if (_rain != null) {
            var e = _rain.emission;
            e.enabled = wet;
            if (wet && !_rain.isPlaying) _rain.Play();
            if (!wet && _rain.isPlaying) _rain.Stop();
        }

    }

    void Build(Transform followCamera) {
        var go = new GameObject("Rain");
        go.transform.SetParent(followCamera, false);
        // Overhead and slightly ahead, so drops enter the view rather than popping into it.
        go.transform.localPosition = new Vector3(0f, 12f, 6f);
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        _rain = go.AddComponent<ParticleSystem>();
        var main = _rain.main;
        main.loop = true;
        main.startLifetime = 0.9f;
        main.startSpeed = 26f;
        main.startSize = 0.018f;
        main.startColor = new Color(0.66f, 0.76f, 0.88f, 0.20f);
        main.maxParticles = 1400;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 1.1f;

        var emission = _rain.emission;
        emission.rateOverTime = 520f;

        var shape = _rain.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(46f, 46f, 1f);

        // Stretched along travel: a round dot reads as snow, a streak reads as rain.
        var renderer = _rain.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.055f;
        renderer.lengthScale = 1.1f;
        // Alpha-blended rather than additive: an additive white streak saturates instantly and
        // reads as a solid bar rather than a drop.
        var rainMat = Prim.Unlit();
        Fx.MakeTransparent(rainMat);
        if (rainMat.HasProperty("_BaseColor")) rainMat.SetColor("_BaseColor", new Color(0.72f, 0.82f, 0.95f, 0.5f));
        rainMat.color = new Color(0.72f, 0.82f, 0.95f, 0.5f);
        rainMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        renderer.material = rainMat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortingOrder = 1;

        // No ground haze: billboarded quads that large read as glass panels hanging in the
        // street, which is worse than having no haze at all. The wet sheen on the road and the
        // fog already carry it.
    }
}

}
