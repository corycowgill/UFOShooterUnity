using UnityEngine;

namespace UFO {

/// <summary>
/// First-person movement. Deliberately NOT a CharacterController: this is a direct port of v2's
/// js/controls.js, which sweeps an XZ circle against the flat ArenaBox list and slides on
/// blocked axes. Keeping the same algorithm keeps the same feel - the web build's movement is
/// snappy precisely because it has no physics step, no slope handling and no step offset.
/// </summary>
public class PlayerController : MonoBehaviour {

    [Header("Movement")]
    public float PlayerHeight = 1.7f;
    public float Speed = 12f;
    public float SprintMultiplier = 1.8f;
    public float JumpForce = 8f;
    public float Gravity = 20f;
    public float Radius = 0.42f;

    [Header("Dash")]
    public float DashSpeed = 50f;
    public float DashDuration = 0.15f;
    public float DashCooldownMax = 2.0f;

    [Header("Look")]
    public float Sensitivity = 0.002f;
    public float GamepadLookSpeed = 2.6f;
    public float MaxPitch = 89f;

    public Camera Cam;
    public Arena Arena;
    public PlayerStats Stats;

    // Runtime state, read by the HUD and the weapon rig.
    public bool OnGround { get; private set; } = true;
    public float VerticalVelocity { get; private set; }
    public float DashTimer { get; private set; }
    public float DashCooldown { get; private set; }
    public bool Sprinting { get; private set; }
    public Vector3 MoveDirection { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float HorizontalSpeed { get; private set; }
    public bool Locked => Cursor.lockState == CursorLockMode.Locked;

    float _yaw, _pitch;
    float _headBobPhase, _headBobAmount;
    Vector3 _prevPos;

    // Enabled only while the game is actually in play; menus and the perk card switch it off.
    public bool InputEnabled = true;

    void Awake() {
        if (Cam == null) Cam = GetComponentInChildren<Camera>();
        _prevPos = transform.position;
    }

    public void Teleport(Vector3 pos, float yawDegrees = 0f) {
        transform.position = new Vector3(pos.x, PlayerHeight, pos.z);
        _yaw = yawDegrees; _pitch = 0f;
        VerticalVelocity = 0f; OnGround = true;
        DashTimer = 0f; DashCooldown = 0f;
        _prevPos = transform.position;
        ApplyLook();
    }

    /// <summary>
    /// Aim at a world point. Used by the smoke-test harness and anything scripted; it writes the
    /// same yaw/pitch the mouse does, so aiming is never a special case that bypasses look state.
    /// </summary>
    public void LookAt(Vector3 worldPoint) {
        Vector3 d = worldPoint - EyePosition;
        if (d.sqrMagnitude < 1e-6f) return;
        _yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        _pitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        _pitch = Mathf.Clamp(_pitch, -MaxPitch, MaxPitch);
        ApplyLook();
    }

    public void Lock() { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    public void Unlock() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

    /// <summary>Driven from GameManager so the whole sim advances on one clock.</summary>
    public void Tick(float dt) {
        if (InputEnabled) ReadLook(dt);
        ApplyLook();
        ReadMove(dt);
        Integrate(dt);

        Velocity = (transform.position - _prevPos) / Mathf.Max(dt, 1e-5f);
        HorizontalSpeed = new Vector2(Velocity.x, Velocity.z).magnitude;
        _prevPos = transform.position;
    }

    void ReadLook(float dt) {
        if (Locked) {
            // Raw mouse delta, matched to the web build's 0.002 rad/px.
            float mx = Input.GetAxisRaw("Mouse X") * 10f;
            float my = Input.GetAxisRaw("Mouse Y") * 10f;
            _yaw += mx * Sensitivity * Mathf.Rad2Deg;
            _pitch -= my * Sensitivity * Mathf.Rad2Deg;
        }
        // Right stick. Dead zone kept generous; WebGL gamepad axes are noisy.
        float gx = AxisDead(InputMap.RightStickX, 0.18f);
        float gy = AxisDead(InputMap.RightStickY, 0.18f);
        if (gx != 0f || gy != 0f) {
            _yaw += gx * GamepadLookSpeed * 60f * dt;
            _pitch -= gy * GamepadLookSpeed * 60f * dt;
        }
        _pitch = Mathf.Clamp(_pitch, -MaxPitch, MaxPitch);
    }

    static float AxisDead(float v, float dead) {
        if (Mathf.Abs(v) < dead) return 0f;
        return (v - Mathf.Sign(v) * dead) / (1f - dead);
    }

    void ApplyLook() {
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        if (Cam != null) Cam.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    void ReadMove(float dt) {
        float x = 0f, z = 0f;
        if (InputEnabled) {
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;

            float lx = AxisDead(InputMap.LeftStickX, 0.18f);
            float lz = AxisDead(InputMap.LeftStickY, 0.18f);
            if (Mathf.Abs(lx) > Mathf.Abs(x)) x = lx;
            if (Mathf.Abs(lz) > Mathf.Abs(z)) z = lz;

            Sprinting = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || InputMap.SprintHeld;

            if ((Input.GetKeyDown(KeyCode.Space) || InputMap.JumpPressed) && OnGround) {
                VerticalVelocity = JumpForce;
                OnGround = false;
            }
            if ((Input.GetKeyDown(KeyCode.E) || InputMap.DashPressed) && DashCooldown <= 0f && DashTimer <= 0f) {
                DashTimer = DashDuration;
            }
        } else {
            Sprinting = false;
        }

        var dir = new Vector3(x, 0f, z);
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        MoveDirection = dir;
    }

    void Integrate(float dt) {
        float speed = Speed * (Stats != null ? Stats.SpeedMultiplier : 1f) * (Sprinting ? SprintMultiplier : 1f);

        if (DashTimer > 0f) {
            DashTimer -= dt;
            speed = DashSpeed;
            if (DashTimer <= 0f) DashCooldown = DashCooldownMax;
        }
        if (DashCooldown > 0f) DashCooldown -= dt;

        // World-space desired motion from the body yaw.
        Vector3 fwd = transform.forward, right = transform.right;
        Vector3 wish = fwd * MoveDirection.z + right * MoveDirection.x;
        // A dash with no stick input dashes forward.
        if (DashTimer > 0f && wish.sqrMagnitude < 1e-4f) wish = fwd;
        if (wish.sqrMagnitude > 1e-6f) wish.Normalize();

        float step = speed * dt;
        var p = transform.position;
        float nx = p.x + wish.x * step;
        float nz = p.z + wish.z * step;

        // Per-axis sweep against the collider list, so a blocked axis slides instead of stopping.
        bool okX = true, okZ = true;
        if (Arena != null) {
            var boxes = Arena.Boxes;
            for (int i = 0; i < boxes.Count; i++) {
                var b = boxes[i];
                if (b.max.y < 0.2f) continue;
                // Low enough to walk straight over.
                if (b.max.y - Mathf.Max(0f, b.min.y) < 0.35f && !b.isWater) continue;
                // Jumped clean above it.
                if (p.y - PlayerHeight > b.max.y - 0.05f) continue;

                if (b.OverlapsXZ(nx, p.z, Radius)) okX = false;
                if (b.OverlapsXZ(p.x, nz, Radius)) okZ = false;
                if (!okX && !okZ) break;
            }
        }
        if (okX) p.x = nx;
        if (okZ) p.z = nz;

        // Vertical
        VerticalVelocity -= Gravity * dt;
        p.y += VerticalVelocity * dt;

        float groundY = GroundHeightAt(p.x, p.z) + PlayerHeight;
        if (p.y <= groundY) {
            p.y = groundY;
            VerticalVelocity = 0f;
            OnGround = true;
        } else {
            OnGround = false;
        }

        // Arena bound
        float lim = Arena != null ? Arena.Radius : 92f;
        float d2 = p.x * p.x + p.z * p.z;
        if (d2 > lim * lim) {
            float s = lim / Mathf.Sqrt(d2);
            p.x *= s; p.z *= s;
        }

        transform.position = p;

        // Head bob - cosmetic, applied to the camera's local Y only.
        if (Cam != null) {
            bool bobbing = OnGround && MoveDirection.sqrMagnitude > 0.01f && DashTimer <= 0f;
            _headBobAmount = Mathf.Lerp(_headBobAmount, bobbing ? (Sprinting ? 0.075f : 0.045f) : 0f, 8f * dt);
            if (bobbing) _headBobPhase += dt * (Sprinting ? 13f : 9f);
            var lp = Cam.transform.localPosition;
            lp.x = 0f; lp.z = 0f;
            lp.y = Mathf.Sin(_headBobPhase) * _headBobAmount;
            Cam.transform.localPosition = lp;
        }
    }

    /// <summary>
    /// Ground height under a point: 0 by default, or the top of any walkable box (the pier, a
    /// container stack) the point sits inside.
    /// </summary>
    public float GroundHeightAt(float x, float z) {
        float best = 0f;
        if (Arena == null) return best;
        var boxes = Arena.Boxes;
        for (int i = 0; i < boxes.Count; i++) {
            var b = boxes[i];
            if (!b.isWater && x > b.min.x && x < b.max.x && z > b.min.z && z < b.max.z) {
                if (b.max.y > best && b.max.y < 6f) best = b.max.y;
            }
        }
        return best;
    }

    /// <summary>The dash's invulnerability window.</summary>
    public bool Invulnerable => DashTimer > 0f;

    public Vector3 EyePosition => Cam != null ? Cam.transform.position : transform.position;
}

}
