using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace UFO {

/// <summary>
/// What kind of machine is this running on, and therefore which control scheme the game should
/// present.
///
/// The question is deliberately "finger or mouse", not "phone or desktop". A phone in a desktop
/// browser's device-emulation mode is still a finger; a Windows laptop with a touchscreen is still
/// a mouse. Getting that distinction right is what stops the touch overlay appearing on machines
/// that do not want it.
///
/// Everything is answered once and cached: these facts cannot change within a session except
/// orientation, which is the one thing read live.
/// </summary>
public static class Platform {

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern int UFO_IsTouchPrimary();
    [DllImport("__Internal")] static extern int UFO_IsIOS();
    [DllImport("__Internal")] static extern int UFO_IsPortrait();
#endif

    static int _touch = -1, _ios = -1;

    /// <summary>
    /// Forces the touch scheme on regardless of hardware. Set from the "touch" URL parameter, so
    /// the mobile build can be driven and captured on a desktop without a phone in hand -
    /// ?touch=1 to force it on, ?touch=0 to force it off.
    /// </summary>
    public static int Override = -1;

    /// <summary>
    /// "diag" URL parameter. Makes the game narrate its own input state once a second, which is
    /// the only way an automated harness can tell a control that moved the player from one that
    /// merely lit up when pressed.
    /// </summary>
    public static bool Diagnostics;

    public static bool TouchPrimary {
        get {
            if (Override >= 0) return Override == 1;
            if (_touch < 0) {
#if UNITY_WEBGL && !UNITY_EDITOR
                try { _touch = UFO_IsTouchPrimary(); } catch { _touch = 0; }
#else
                // In the editor and in player tests, Unity's own answer is the honest one.
                _touch = (Application.isMobilePlatform || Input.touchSupported) ? 1 : 0;
#endif
            }
            return _touch == 1;
        }
    }

    public static bool IsIOS {
        get {
            if (_ios < 0) {
#if UNITY_WEBGL && !UNITY_EDITOR
                try { _ios = UFO_IsIOS(); } catch { _ios = 0; }
#else
                _ios = Application.platform == RuntimePlatform.IPhonePlayer ? 1 : 0;
#endif
            }
            return _ios == 1;
        }
    }

    /// <summary>
    /// Pointer lock is the desktop look mechanism and simply does not exist on iOS Safari. Asking
    /// for it there does nothing at best; the touch scheme has to not depend on it at all.
    /// </summary>
    public static bool SupportsPointerLock => !TouchPrimary;

    public static bool IsPortrait {
        get {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return UFO_IsPortrait() == 1; } catch { return false; }
#else
            return Screen.height > Screen.width;
#endif
        }
    }

    /// <summary>Read the "touch" URL parameter once at startup. No-op off the web.</summary>
    public static void ApplyUrlOverrides() {
        var url = Application.absoluteURL;
        if (string.IsNullOrEmpty(url)) return;
        int q = url.IndexOf('?');
        if (q < 0) return;
        foreach (var pair in url.Substring(q + 1).Split('&')) {
            var kv = pair.Split('=');
            if (kv.Length != 2) continue;
            if (kv[0] == "touch") Override = (kv[1] == "1" || kv[1] == "true") ? 1 : 0;
            if (kv[0] == "diag") Diagnostics = kv[1] == "1" || kv[1] == "true";
        }
    }
}

}
