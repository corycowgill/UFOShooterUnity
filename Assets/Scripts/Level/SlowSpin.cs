using UnityEngine;

namespace UFO {

/// <summary>Slow yaw drift, for the mothership. v2 spins its holder at 0.015 rad/s.</summary>
public class SlowSpin : MonoBehaviour {
    public float RadiansPerSecond = 0.015f;
    void Update() => transform.Rotate(Vector3.up, RadiansPerSecond * Mathf.Rad2Deg * Time.deltaTime, Space.World);
}

}
