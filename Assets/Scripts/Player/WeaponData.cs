using System.Collections.Generic;
using UnityEngine;

namespace UFO {

public enum WeaponKind { Hitscan, Projectile, Melee, Rocket }

/// <summary>
/// The arsenal. Numbers verbatim from v2's js/weapons.js WEAPONS.
/// ShieldMul is the multiplier applied against an ENERGY shield — 0.7 for bullets, 2.4 for
/// plasma. That split is the reason weapon swapping matters; do not flatten it.
/// </summary>
public class WeaponDef {
    public string Key, Name, ModelName;
    public WeaponKind Kind;
    public float Damage, FireRate, Range, Spread, Speed;
    public int Mag = -1, Reserve = -1;          // -1 = infinite
    public float Reload;
    public bool Auto;
    public float Headshot = 1f, ShieldMul = 1f, Recoil;
    public float Splash, Lunge, Arc;
    public float HeatPerShot = -1f, CooldownRate, OverheatTime;
    public Vector3 Pos, Rot, Muzzle;
    public float Scale;
    public Color Color;

    public bool UsesHeat => HeatPerShot > 0f;
    public bool UsesMag => Mag > 0;
    public string ModelPath => "Models/weapons/" + ModelName;
}

public static class Arsenal {
    static Color C(int hex) => new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);
    const float HALF_PI = Mathf.PI / 2f;

    public static readonly string[] Order = { "rifle", "plasmaRifle", "energySword", "rocketLauncher" };

    public static readonly Dictionary<string, WeaponDef> All = new Dictionary<string, WeaponDef> {
        ["rifle"] = new WeaponDef {
            Key = "rifle", Name = "MA-7 RIFLE", Kind = WeaponKind.Hitscan, ModelName = "rifle",
            Damage = 11, FireRate = 0.095f, Range = 140, Spread = 0.014f,
            Mag = 32, Reserve = -1, Reload = 1.7f, Auto = true,
            Headshot = 2.0f, ShieldMul = 0.7f, Recoil = 0.012f,
            Pos = new Vector3(0.28f, -0.26f, 0.55f), Rot = new Vector3(0, -90f, 0), Scale = 0.5f,
            Muzzle = new Vector3(0f, 0.05f, 0.55f), Color = C(0xffd080),
        },
        ["plasmaRifle"] = new WeaponDef {
            Key = "plasmaRifle", Name = "PLASMA RIFLE", Kind = WeaponKind.Projectile, ModelName = "plasma_rifle",
            Damage = 16, FireRate = 0.13f, Speed = 70, Range = 120,
            HeatPerShot = 0.065f, CooldownRate = 0.45f, OverheatTime = 1.8f, Auto = true,
            Headshot = 1.3f, ShieldMul = 2.4f, Recoil = 0.006f,
            Pos = new Vector3(0.3f, -0.27f, 0.5f), Rot = new Vector3(0, -90f, 0), Scale = 0.55f,
            Muzzle = new Vector3(0f, 0.04f, 0.6f), Color = C(0x40a0ff),
        },
        ["energySword"] = new WeaponDef {
            Key = "energySword", Name = "ENERGY SWORD", Kind = WeaponKind.Melee, ModelName = "energy_sword",
            Damage = 140, FireRate = 0.6f, Range = 3.8f, Lunge = 7.5f, Arc = 0.85f,
            Auto = false, Headshot = 1.0f, ShieldMul = 1.6f,
            Pos = new Vector3(0.34f, -0.3f, 0.45f), Rot = new Vector3(8.6f, -104.3f, 5.7f), Scale = 0.65f,
            Muzzle = new Vector3(0, 0, 0.5f), Color = C(0x60c0ff),
        },
        ["rocketLauncher"] = new WeaponDef {
            Key = "rocketLauncher", Name = "ROCKET LAUNCHER", Kind = WeaponKind.Rocket, ModelName = "rocket_launcher",
            Damage = 260, Splash = 6.5f, FireRate = 1.2f, Speed = 42, Range = 150,
            Mag = 2, Reserve = 8, Reload = 2.8f, Auto = false,
            Headshot = 1.0f, ShieldMul = 1.0f, Recoil = 0.06f,
            Pos = new Vector3(0.3f, -0.24f, 0.5f), Rot = new Vector3(0, -90f, 0), Scale = 0.6f,
            Muzzle = new Vector3(0f, 0.08f, 0.7f), Color = C(0xffa040),
        },
    };

    public static WeaponDef Get(string key) => All.TryGetValue(key, out var w) ? w : null;
}

}
