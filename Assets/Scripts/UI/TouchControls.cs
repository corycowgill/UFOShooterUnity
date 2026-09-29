using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UFO {

/// <summary>
/// Where the game reads the finger. Written by the on-screen widgets below, read by InputMap and
/// PlayerController exactly the way a gamepad would be.
///
/// Routing touch through virtual axes rather than teaching the player controller about fingers is
/// what keeps this port small: the movement, firing and weapon code already handles a gamepad, so
/// it needs no notion that a touch scheme exists at all.
/// </summary>
public static class TouchState {

    public static bool Active;              // the overlay is up and should be believed

    public static Vector2 Move;             // left thumbstick, -1..1
    public static Vector2 LookDelta;        // degrees to apply this frame, consumed by the look code
    public static bool Sprint;              // implied by pushing the stick to its rim

    public static bool Fire;                // held
    public static bool Jump, Reload, Grenade, Dash, Pause;   // edges, cleared after one frame
    public static int WeaponSlot = -1;      // 0-3, cleared after one frame

    /// <summary>
    /// Clear the one-frame edges. Called at the very END of the frame that consumed them, so a
    /// tap registered during Update is still visible to code that runs later in the same frame -
    /// getting this backwards drops roughly half of all taps, and does it intermittently.
    /// </summary>
    public static void EndFrame() {
        Jump = Reload = Grenade = Dash = Pause = false;
        WeaponSlot = -1;
        LookDelta = Vector2.zero;
    }

    public static void Clear() {
        Move = Vector2.zero; LookDelta = Vector2.zero;
        Fire = Sprint = false;
        EndFrame();
    }
}

/// <summary>
/// A thumbstick that follows the finger. The base is drawn where it lives; the knob tracks the
/// drag out to Radius and clamps there.
/// </summary>
public class TouchStick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler {

    public RectTransform Knob;
    public float Radius = 130f;

    RectTransform _rt;
    int _finger = -1;

    public Vector2 Value { get; private set; }

    void Awake() { _rt = (RectTransform)transform; }

    public void OnPointerDown(PointerEventData e) {
        // First finger down owns the stick until it lifts. Without this a second finger landing
        // nearby would yank the stick out from under the thumb that is steering.
        if (_finger != -1) return;
        _finger = e.pointerId;
        Track(e);
    }

    public void OnDrag(PointerEventData e) { if (e.pointerId == _finger) Track(e); }

    public void OnPointerUp(PointerEventData e) {
        if (e.pointerId != _finger) return;
        _finger = -1;
        Value = Vector2.zero;
        if (Knob != null) Knob.anchoredPosition = Vector2.zero;
    }

    void Track(PointerEventData e) {
        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rt, e.position, e.pressEventCamera, out local)) return;
        var clamped = Vector2.ClampMagnitude(local, Radius);
        if (Knob != null) Knob.anchoredPosition = clamped;
        Value = clamped / Radius;
    }
}

/// <summary>
/// The look region: drag anywhere in it to turn. Relative movement, not a second stick - it is
/// what every phone shooter does because it lets the thumb reposition without the view snapping.
/// </summary>
public class TouchLook : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler {

    /// <summary>Degrees of turn per 1/1000th of the screen width dragged.</summary>
    public float Sensitivity = 0.16f;
    public bool InvertY;

    int _finger = -1;
    Vector2 _accum;

    public void OnPointerDown(PointerEventData e) { if (_finger == -1) _finger = e.pointerId; }
    public void OnPointerUp(PointerEventData e) { if (e.pointerId == _finger) _finger = -1; }

    public void OnDrag(PointerEventData e) {
        if (e.pointerId != _finger) return;
        // Normalised by screen width so the same swipe turns the same amount on any device; a
        // per-pixel constant would make a large phone far less sensitive than a small one.
        float k = 1000f / Mathf.Max(1f, Screen.width);
        _accum += e.delta * k * Sensitivity;
    }

    /// <summary>Degrees accumulated since the last call. Reading clears it.</summary>
    public Vector2 Consume() {
        var d = _accum;
        _accum = Vector2.zero;
        if (InvertY) d.y = -d.y;
        return d;
    }
}

/// <summary>A round button that reports both "held" and "pressed this frame".</summary>
public class TouchButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler {

    public System.Action OnPress;
    public bool Held { get; private set; }

    Image _img;
    Color _idle, _down;
    int _finger = -1;

    public void Style(Image img, Color idle, Color down) {
        _img = img; _idle = idle; _down = down;
        if (_img != null) _img.color = _idle;
    }

    public void OnPointerDown(PointerEventData e) {
        if (_finger != -1) return;
        _finger = e.pointerId;
        Held = true;
        if (_img != null) _img.color = _down;
        if (OnPress != null) OnPress();
    }

    public void OnPointerUp(PointerEventData e) {
        if (e.pointerId != _finger) return;
        _finger = -1;
        Held = false;
        if (_img != null) _img.color = _idle;
    }

    /// <summary>A finger can leave the screen without an up event if the browser steals it.</summary>
    void OnDisable() { _finger = -1; Held = false; if (_img != null) _img.color = _idle; }
}

}
