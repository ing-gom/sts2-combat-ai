using System;

namespace Sts2CombatAI.Sim;

/// <summary>
/// STS canonical multipliers. Approximates STS2's resolution math; exact game uses
/// decimals but planner ordering only needs integer ranks, so float ops are fine.
///
/// Damage:
///   final = floor((base + attackerStrength) × (defenderVulnerable ? 1.5 : 1.0)
///                                          × (attackerWeak ? 0.75 : 1.0))
/// Block:
///   final = floor((base + defenderDexterity) × (defenderFrail ? 0.75 : 1.0))
/// </summary>
internal static class StatusMath
{
    public const double VulnerableMult = 1.5;
    public const double WeakMult = 0.75;
    public const double FrailMult = 0.75;

    public static int EffectiveAttackDmg(int baseDamage, int attackerStrength,
        bool defenderVulnerable, bool attackerWeak)
    {
        if (baseDamage <= 0) return 0;
        double v = baseDamage + attackerStrength;
        if (defenderVulnerable) v *= VulnerableMult;
        if (attackerWeak) v *= WeakMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

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
    /// </summary>
    public static int EffectivePerHitCapped(int baseDamage, int attackerStrength,
        SimEnemy target, bool attackerWeak)
    {
        int per = EffectiveAttackDmg(baseDamage, attackerStrength,
            target.VulnerableAmount > 0, attackerWeak);
        if (target.DamageCapPerHit > 0 && per > target.DamageCapPerHit)
            per = target.DamageCapPerHit;
        return per;
    }

    /// <summary>
    /// Per-target total attack damage: hits × per-hit (after Intangible cap), then
    /// clamped by HardenedShellRemaining. If the target has HardenedShellPower but
    /// the budget is fully spent (Remaining == 0), returns 0.
    /// </summary>
    public static int EffectivePerEnemyTotal(int baseDamage, int hits, int attackerStrength,
        SimEnemy target, bool attackerWeak)
    {
        int perHit = EffectivePerHitCapped(baseDamage, attackerStrength, target, attackerWeak);
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
}
