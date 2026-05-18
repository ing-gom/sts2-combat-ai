using System;

namespace Sts2CombatAI.Sim;

/// <summary>
/// STS canonical multipliers. Approximates STS2's resolution math; exact game uses
/// decimals but planner ordering only needs integer ranks, so float ops are fine.
///
/// Damage:
///   final = floor((base + attackerStrength + attackerVigor) × (defenderVulnerable ? 1.5 : 1.0)
///                                                         × (attackerWeak ? 0.75 : 1.0))
///   (Vigor applies to the FIRST attack only this turn — caller is responsible for
///   passing 0 for non-first attacks. EffectivePerEnemyTotal applies Vigor only on
///   the first hit; subsequent hits within the same multi-hit card use Strength only.)
/// Block:
///   final = floor((base + defenderDexterity) × (defenderFrail ? 0.75 : 1.0))
/// </summary>
internal static class StatusMath
{
    public const double VulnerableMult = 1.5;
    public const double WeakMult = 0.75;
    public const double FrailMult = 0.75;

    // v0.7.84 — Damage multiplier powers (Silent Volatile / passive).
    public const double LethalityMult = 1.5;   // first attack/turn (single-shot)
    public const double TrackingMult  = 2.0;   // vs Weak target (passive)
    public const double CrueltyMult   = 1.25;  // vs Vulnerable target (passive)

    /// <summary>
    /// v0.7.84 — Apply per-target conditional damage multipliers (Tracking,
    /// Cruelty, Lethality). Returns the multiplied damage. Lethality is
    /// single-shot: the caller MUST gate it (first attack of the turn) by
    /// passing <paramref name="lethalityActive"/>=true only for the first call.
    /// </summary>
    public static int ApplyDamageMultipliers(int damage, SimState state, bool defenderVulnerable,
        bool defenderWeak, bool lethalityActive)
    {
        if (damage <= 0) return 0;
        double v = damage;
        if (state.PlayerTracking > 0 && defenderWeak) v *= TrackingMult;
        if (state.PlayerCruelty > 0 && defenderVulnerable) v *= CrueltyMult;
        if (state.PlayerLethality > 0 && lethalityActive) v *= LethalityMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    /// <summary>
    /// v0.7.86 — Apply card-id-specific damage adders before the multiplier
    /// chain. Currently: AccuracyPower → +N damage per Shiv card. Returns
    /// adjusted base damage to feed into EffectiveAttackDmg / EffectivePerEnemyTotal.
    /// </summary>
    public static int ApplyCardSpecificDamageBonus(int baseDamage, string? cardId, SimState state)
    {
        if (string.IsNullOrEmpty(cardId)) return baseDamage;
        if (state.PlayerAccuracy > 0 && cardId == "SHIV")
            return baseDamage + state.PlayerAccuracy;
        return baseDamage;
    }

    // v0.7.82 — Vigor-aware overload. Adds attackerVigor to the additive part
    // (same step as Strength). 0-arg legacy overload preserved below for callers
    // that don't track Vigor.
    public static int EffectiveAttackDmg(int baseDamage, int attackerStrength,
        int attackerVigor, bool defenderVulnerable, bool attackerWeak)
    {
        if (baseDamage <= 0) return 0;
        double v = baseDamage + attackerStrength + attackerVigor;
        if (defenderVulnerable) v *= VulnerableMult;
        if (attackerWeak) v *= WeakMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    public static int EffectiveAttackDmg(int baseDamage, int attackerStrength,
        bool defenderVulnerable, bool attackerWeak)
        => EffectiveAttackDmg(baseDamage, attackerStrength, 0, defenderVulnerable, attackerWeak);

    public static int EffectiveBlock(int baseBlock, int defenderDexterity, bool defenderFrail)
    {
        if (baseBlock <= 0) return 0;
        double v = baseBlock + defenderDexterity;
        if (defenderFrail) v *= FrailMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    /// <summary>
    /// Per-hit attack damage clamped by the target's IntangiblePower / HardToKill
    /// cap (<see cref="SimEnemy.DamageCapPerHit"/>). 0 cap means uncapped.
    /// v0.7.82 — Vigor parameter. Caller passes 0 for non-first-attack contexts.
    /// </summary>
    public static int EffectivePerHitCapped(int baseDamage, int attackerStrength, int attackerVigor,
        SimEnemy target, bool attackerWeak)
    {
        int per = EffectiveAttackDmg(baseDamage, attackerStrength, attackerVigor,
            target.VulnerableAmount > 0, attackerWeak);
        if (target.DamageCapPerHit > 0 && per > target.DamageCapPerHit)
            per = target.DamageCapPerHit;
        return per;
    }

    public static int EffectivePerHitCapped(int baseDamage, int attackerStrength,
        SimEnemy target, bool attackerWeak)
        => EffectivePerHitCapped(baseDamage, attackerStrength, 0, target, attackerWeak);

    /// <summary>
    /// Per-target total attack damage: hits × per-hit (after Intangible cap), then
    /// clamped by HardenedShellRemaining. If the target has HardenedShellPower but
    /// the budget is fully spent (Remaining == 0), returns 0.
    /// v0.7.82 — Vigor is added to base damage on play (STS canonical: "your next
    /// attack deals additional damage equal to Vigor"). It applies to every hit of
    /// the card as a per-hit bonus, then consumed when the card resolves. Multi-card
    /// estimators (IsLethalThisTurn) must pass Vigor=0 for subsequent attacks.
    /// </summary>
    public static int EffectivePerEnemyTotal(int baseDamage, int hits, int attackerStrength,
        int attackerVigor, SimEnemy target, bool attackerWeak)
    {
        int perHit = EffectivePerHitCapped(baseDamage, attackerStrength, attackerVigor,
            target, attackerWeak);
        int hitsClamped = Math.Max(1, hits);
        int total = perHit * hitsClamped;
        if (target.HardenedShellRemaining > 0)
        {
            if (total > target.HardenedShellRemaining)
                total = target.HardenedShellRemaining;
        }
        else if (perHit > 0 && target.Powers.ContainsKey("HardenedShellPower"))
        {
            total = 0;
        }
        return total;
    }

    public static int EffectivePerEnemyTotal(int baseDamage, int hits, int attackerStrength,
        SimEnemy target, bool attackerWeak)
        => EffectivePerEnemyTotal(baseDamage, hits, attackerStrength, 0, target, attackerWeak);
}
