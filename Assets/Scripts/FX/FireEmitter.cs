using UnityEngine;

namespace UFO {

/// <summary>
/// A burning wreck: flame tongues, rising smoke and the odd ember, driven off the shared Fx pool.
///
/// v2 calls particles.spawnFire() every frame at each fire spot; the lights alone were never the
/// effect. Rates are deliberately modest because the same pool serves combat - a dozen fires
/// emitting flat out would starve impacts and explosions of puffs exactly when a wave lands.
/// </summary>
public class FireEmitter : MonoBehaviour {

    public Fx Fx;
    public float Scale = 1f;

    [Tooltip("Flames per second.")]
    public float FlameRate = 34f;
    [Tooltip("Smoke puffs per second.")]
    public float SmokeRate = 3.5f;
    [Tooltip("Embers per second.")]
    public float EmberRate = 4f;

    float _flameAcc, _smokeAcc, _emberAcc;

    /// <summary>Only emit when the player can actually see it - a fire 90 m away is just a light.</summary>
    public float MaxDistance = 60f;

    Transform _viewer;

    void Start() {
        if (Fx == null && GameManager.Instance != null) Fx = GameManager.Instance.Fx;
        if (GameManager.Instance != null && GameManager.Instance.Player != null)
            _viewer = GameManager.Instance.Player.transform;
    }

    void Update() {
        if (Fx == null) return;
        if (_viewer != null && Vector3.Distance(_viewer.position, transform.position) > MaxDistance) return;

        float dt = Time.deltaTime;
        var p = transform.position;

        _flameAcc += FlameRate * dt;
        while (_flameAcc >= 1f) {
            _flameAcc -= 1f;
            var o = new Vector3(Random.Range(-0.5f, 0.5f), Random.Range(-0.2f, 0.3f), Random.Range(-0.5f, 0.5f)) * Scale;
            Fx.Flame(p + o, Scale);
        }

        _smokeAcc += SmokeRate * dt;
        while (_smokeAcc >= 1f) {
            _smokeAcc -= 1f;
            var o = new Vector3(Random.Range(-0.4f, 0.4f), 0.8f, Random.Range(-0.4f, 0.4f)) * Scale;
            Fx.Smoke(p + o, Scale);
        }

        _emberAcc += EmberRate * dt;
        while (_emberAcc >= 1f) {
            _emberAcc -= 1f;
            Fx.Ember(p + Vector3.up * (0.4f * Scale), Scale);
        }
    }
}

}
