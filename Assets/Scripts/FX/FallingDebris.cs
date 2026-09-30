using UnityEngine;

namespace UFO {

/// <summary>
/// A small thrown object that falls, tumbles and disappears: the spent magazine, and anything else
/// that wants to leave the viewmodel and land on the street.
///
/// It uses its own integration rather than a Rigidbody because this game has no Unity physics at
/// all - `Arena` is a flat AABB list and nothing else in the project has ever needed a collider.
/// Stopping at the ground plane is enough; a magazine that lands on a kerb instead of the road is
/// not a difference anyone will see in the second and a half it exists.
/// </summary>
public class FallingDebris : MonoBehaviour {

    public Vector3 Velocity;
    public Vector3 Spin;
    public float Gravity = 9.5f;
    public float Life = 2.2f;
    public float GroundY = 0.02f;

    float _fade;

    void Update() {
        float dt = Time.deltaTime;
        Life -= dt;

        if (transform.position.y > GroundY) {
            Velocity.y -= Gravity * dt;
            transform.position += Velocity * dt;
            transform.Rotate(Spin * dt, Space.Self);
            if (transform.position.y <= GroundY) {
                var p = transform.position;
                p.y = GroundY;
                transform.position = p;
                // One low bounce, then it stays put and the rest of its life is the fade.
                Velocity = new Vector3(Velocity.x * 0.25f, -Velocity.y * 0.22f, Velocity.z * 0.25f);
                Spin *= 0.2f;
            }
        }

        if (Life <= 0.5f) {
            _fade = Mathf.Clamp01(Life / 0.5f);
            transform.localScale *= Mathf.Lerp(1f, 0.986f, 1f - _fade);
        }
        if (Life <= 0f) Destroy(gameObject);
    }
}

}
