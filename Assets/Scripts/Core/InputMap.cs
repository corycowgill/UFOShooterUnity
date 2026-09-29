using UnityEngine;

namespace UFO {

/// <summary>
/// One place that knows which physical control does what, so the rest of the game never touches
/// UnityEngine.Input directly. Legacy Input is deliberate: it needs no package, works in WebGL
/// with no extra plumbing, and Xbox pads enumerate correctly through the browser Gamepad API.
///
/// XInput mapping on Windows/WebGL: A=0 B=1 X=2 Y=3 LB=4 RB=5 Back=6 Start=7 LS=8 RS=9.
/// Right stick and triggers come from the four axes appended to InputManager.asset.
/// </summary>
public static class InputMap {

    // --- axes -------------------------------------------------------------
    public static float LeftStickX  => Axis("Horizontal");
    public static float LeftStickY  => Axis("Vertical");
    public static float RightStickX => Axis("PadRightX");
    public static float RightStickY => Axis("PadRightY");
    public static float TriggerLeft  => Mathf.Max(0f, Axis("PadTriggerL"));
    public static float TriggerRight => Mathf.Max(0f, Axis("PadTriggerR"));

    static float Axis(string name) {
        try { return Input.GetAxisRaw(name); } catch { return 0f; }
    }

    // --- buttons ----------------------------------------------------------
    public static bool JumpPressed   => Input.GetKeyDown(KeyCode.JoystickButton0);
    public static bool SprintHeld    => Input.GetKey(KeyCode.JoystickButton1);
    public static bool ReloadPressed => Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.JoystickButton2);
    public static bool GrenadePressed=> Input.GetKeyDown(KeyCode.JoystickButton3);
    public static bool DashPressed   => Input.GetKeyDown(KeyCode.JoystickButton8);
    public static bool PrevWeapon    => Input.GetKeyDown(KeyCode.JoystickButton4);
    public static bool NextWeapon    => Input.GetKeyDown(KeyCode.JoystickButton5);
    public static bool StartPressed  => Input.GetKeyDown(KeyCode.JoystickButton7);

    // --- combined (keyboard + pad) ----------------------------------------
    public static bool FireHeld     => Input.GetMouseButton(0) || TriggerRight > 0.4f;
    public static bool FirePressed  => Input.GetMouseButtonDown(0) || TriggerRightPressed;
    public static bool AltFirePressed => Input.GetMouseButtonDown(1) || TriggerLeftPressed;

    public static bool Reload  => Input.GetKeyDown(KeyCode.R) || ReloadPressed;
    public static bool Grenade => Input.GetKeyDown(KeyCode.Q) || GrenadePressed;
    public static bool Pause   => Input.GetKeyDown(KeyCode.Escape) || StartPressed;
    public static bool Guide   => Input.GetKeyDown(KeyCode.Tab);

    /// <summary>Weapon index 0-3 from the number row, or -1. Shoulder buttons cycle instead.</summary>
    public static int WeaponSlot {
        get {
            if (Input.GetKeyDown(KeyCode.Alpha1)) return 0;
            if (Input.GetKeyDown(KeyCode.Alpha2)) return 1;
            if (Input.GetKeyDown(KeyCode.Alpha3)) return 2;
            if (Input.GetKeyDown(KeyCode.Alpha4)) return 3;
            return -1;
        }
    }

    public static int WeaponCycle => NextWeapon ? 1 : (PrevWeapon ? -1 : 0);

    // Triggers are analog, so edge detection has to be done by hand.
    static bool _ltWas, _rtWas;
    static bool _ltEdge, _rtEdge;
    static int _edgeFrame = -1;

    static void PumpEdges() {
        if (_edgeFrame == Time.frameCount) return;
        _edgeFrame = Time.frameCount;
        bool lt = TriggerLeft > 0.4f, rt = TriggerRight > 0.4f;
        _ltEdge = lt && !_ltWas;
        _rtEdge = rt && !_rtWas;
        _ltWas = lt; _rtWas = rt;
    }

    static bool TriggerLeftPressed  { get { PumpEdges(); return _ltEdge; } }
    static bool TriggerRightPressed { get { PumpEdges(); return _rtEdge; } }
}

}
