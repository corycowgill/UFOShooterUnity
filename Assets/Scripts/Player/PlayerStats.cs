using System.Collections.Generic;
using UnityEngine;

namespace UFO {

public class Perk {
    public string Id, Name, Desc;
    public Perk(string id, string name, string desc) { Id = id; Name = name; Desc = desc; }
}

/// <summary>Player health, shield, score, combo and perks. Port of v2's js/player.js.</summary>
public class PlayerStats {
    public static readonly Perk[] Perks = {
        new Perk("rapidFire",    "RAPID FIRE",    "Fire 20% faster"),
        new Perk("toughSkin",    "TOUGH SKIN",    "+25 max HP"),
        new Perk("quickFeet",    "QUICK FEET",    "15% faster movement"),
        new Perk("vampire",      "VAMPIRE",       "Kills restore 5 HP"),
        new Perk("blastRadius",  "BLAST RADIUS",  "+40% explosion radius"),
        new Perk("sharpshooter", "SHARPSHOOTER",  "+15% weapon damage"),
        new Perk("comboMaster",  "COMBO MASTER",  "+1.5s combo window"),
        new Perk("scavenger",    "SCAVENGER",     "+25% pickup drop rate"),
    };

    public float MaxHp = 100f, Hp = 100f;
    public float Shield = 0f, MaxShield = 50f;
    public int Score, Kills;
    public bool Dead;

    public float DamageFlashTimer;
    public float RegenTimer, RegenDelay = 4f, RegenRate = 6f;

    public int Combo, BestCombo;
    public float ComboTimer, ComboWindow = 3.0f;

    readonly List<string> _perks = new List<string>();
    public IReadOnlyList<string> Taken => _perks;

    // Set by the controller so damage can be ignored during the dash's i-frames.
    public System.Func<bool> IsInvulnerable;

    public event System.Action<float, bool> OnDamaged;   // (amount, absorbedByShield)

    public void TakeDamage(float amount) {
        if (Dead) return;
        if (IsInvulnerable != null && IsInvulnerable()) return;

        float remaining = amount;
        bool shieldAte = false;
        if (Shield > 0f) {
            float absorbed = Mathf.Min(Shield, remaining);
            Shield -= absorbed;
            remaining -= absorbed;
            shieldAte = true;
        }
        if (remaining > 0f) Hp -= remaining;

        DamageFlashTimer = 0.2f;
        RegenTimer = RegenDelay;
        OnDamaged?.Invoke(amount, shieldAte && remaining <= 0f);

        if (Hp <= 0f) { Hp = 0f; Dead = true; }
    }

    public void AddShield(float amount) => Shield = Mathf.Min(MaxShield, Shield + amount);
    public void Heal(float amount) { if (!Dead) Hp = Mathf.Min(MaxHp, Hp + amount); }

    public void AddScore(int points) => Score += points * Mathf.Max(1, Combo);

    public void AddKill() {
        Kills++;
        Combo++;
        ComboTimer = ComboWindow;
        if (Combo > BestCombo) BestCombo = Combo;
    }

    public void AddPerk(string id) {
        _perks.Add(id);
        if (id == "toughSkin") { MaxHp += 25f; Hp += 25f; }
        if (id == "comboMaster") ComboWindow += 1.5f;
    }

    public int PerkCount(string id) {
        int n = 0;
        for (int i = 0; i < _perks.Count; i++) if (_perks[i] == id) n++;
        return n;
    }

    public float FireRateMultiplier        => Mathf.Pow(0.8f, PerkCount("rapidFire"));
    public float DamageMultiplier          => 1f + PerkCount("sharpshooter") * 0.15f;
    public float SpeedMultiplier           => 1f + PerkCount("quickFeet") * 0.15f;
    public float ExplosionRadiusMultiplier => 1f + PerkCount("blastRadius") * 0.4f;
    public float VampireHeal               => PerkCount("vampire") * 5f;
    public float DropRateBonus             => PerkCount("scavenger") * 0.25f;

    public void Tick(float dt) {
        if (RegenTimer > 0f) RegenTimer -= dt;
        else if (Hp < MaxHp && !Dead) Hp = Mathf.Min(MaxHp, Hp + RegenRate * dt);

        if (DamageFlashTimer > 0f) DamageFlashTimer -= dt;

        if (ComboTimer > 0f) {
            ComboTimer -= dt;
            if (ComboTimer <= 0f) Combo = 0;
        }
    }

    public void Reset() {
        MaxHp = 100f; Hp = MaxHp;
        Score = 0; Kills = 0; Dead = false;
        DamageFlashTimer = 0f; RegenTimer = 0f;
        Combo = 0; ComboTimer = 0f; ComboWindow = 3.0f; BestCombo = 0;
        Shield = 0f;
        _perks.Clear();
    }

    /// <summary>Three distinct random perks for the post-wave card choice.</summary>
    public static List<Perk> RollChoices(int n = 3) {
        var pool = new List<Perk>(Perks);
        var outp = new List<Perk>(n);
        for (int i = 0; i < n && pool.Count > 0; i++) {
            int k = Random.Range(0, pool.Count);
            outp.Add(pool[k]);
            pool.RemoveAt(k);
        }
        return outp;
    }
}

}
