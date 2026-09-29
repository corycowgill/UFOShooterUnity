using System.Collections.Generic;
using UnityEngine;

namespace UFO {

public enum EnemyRole { Ranged, Skirmisher, Commander, Tank, Aerial, Boss }

/// <summary>
/// Static roster. Every number here is lifted verbatim from v2's js/enemies.js ENEMY_TYPES so
/// the Unity build fights identically to the web build. See GAMEPLAY_GUIDE.md section 5.
/// </summary>
public class EnemyType {
    public string Key, Name;
    public EnemyRole Role;
    public float Height, Hp, Shield, ShieldDelay, ShieldRegen, Speed, Damage;
    public float AttackRate, AttackRange, KeepDistance, Radius;
    public int Burst;
    public float MeleeRange, MeleeDamage;
    public float FrontShield, FrontShieldArc;   // arc in radians
    public float HoverHeight, Splash, SpawnEvery;
    public int Points;
    public Color Color, BoltColor;
    public bool Panics, IsBoss;

    public string ModelPath => "Models/enemies/" + Key;
}

public static class EnemyRoster {
    static Color C(int hex) => new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

    public static readonly Dictionary<string, EnemyType> All = new Dictionary<string, EnemyType> {
        ["gnat"] = new EnemyType {
            Key = "gnat", Name = "GNAT", Role = EnemyRole.Ranged, Height = 1.25f, Hp = 45, Shield = 0,
            Speed = 4.2f, Damage = 5, AttackRate = 1.1f, AttackRange = 18, KeepDistance = 10,
            Points = 100, Color = C(0xff8a2a), BoltColor = C(0x33ddff), Panics = true, Radius = 0.45f,
        },
        ["skirmisher"] = new EnemyType {
            Key = "skirmisher", Name = "SKIRMISHER", Role = EnemyRole.Skirmisher, Height = 1.8f, Hp = 70, Shield = 0,
            Speed = 7.5f, Damage = 6, AttackRate = 0.35f, Burst = 3, AttackRange = 26, KeepDistance = 14,
            Points = 175, Color = C(0x2ab7a0), BoltColor = C(0xd070ff),
            FrontShield = 160, FrontShieldArc = 0.9f, Radius = 0.5f,
        },
        ["warlord"] = new EnemyType {
            Key = "warlord", Name = "WARLORD", Role = EnemyRole.Commander, Height = 2.4f, Hp = 180, Shield = 220,
            ShieldDelay = 4, ShieldRegen = 60, Speed = 5.5f, Damage = 8, AttackRate = 0.5f, Burst = 4,
            AttackRange = 30, KeepDistance = 9, MeleeRange = 3.2f, MeleeDamage = 40,
            Points = 400, Color = C(0x2244cc), BoltColor = C(0x8866ff), Radius = 0.7f,
        },
        ["juggernaut"] = new EnemyType {
            Key = "juggernaut", Name = "JUGGERNAUT", Role = EnemyRole.Tank, Height = 3.5f, Hp = 900, Shield = 0,
            Speed = 2.4f, Damage = 55, AttackRate = 2.6f, AttackRange = 40, KeepDistance = 12,
            Points = 1200, Color = C(0xff6a00), BoltColor = C(0x66ff44),
            FrontShield = 600, FrontShieldArc = 0.75f, Radius = 1.3f, Splash = 5,
        },
        ["wasp"] = new EnemyType {
            Key = "wasp", Name = "WASP", Role = EnemyRole.Aerial, Height = 1.0f, Hp = 50, Shield = 0,
            Speed = 9, Damage = 4, AttackRate = 0.6f, AttackRange = 22, KeepDistance = 12, HoverHeight = 5,
            Points = 150, Color = C(0x9be24a), BoltColor = C(0xd0ff40), Radius = 0.5f,
        },
        ["overseer"] = new EnemyType {
            Key = "overseer", Name = "OVERSEER", Role = EnemyRole.Boss, Height = 5.0f, Hp = 2600, Shield = 900,
            ShieldDelay = 6, ShieldRegen = 120, Speed = 3.5f, Damage = 16, AttackRate = 0.18f,
            AttackRange = 60, KeepDistance = 18, HoverHeight = 7,
            Points = 6000, Color = C(0x8a2be2), BoltColor = C(0xff40e0),
            SpawnEvery = 12, Radius = 2.2f, IsBoss = true,
        },
    };

    public static EnemyType Get(string key) => All.TryGetValue(key, out var t) ? t : null;
}

/// <summary>Clip names produced by v2's tools/blender/rig_biped.py. Bound by name at runtime.</summary>
public static class Clip {
    public const string Idle = "Idle", Walk = "Walk", Run = "Run";
    public const string Attack = "Attack", Shoot = "Shoot", Hit = "Hit", Death = "Death";
}

}
