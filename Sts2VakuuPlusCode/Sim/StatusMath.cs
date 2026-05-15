using System;

namespace Sts2VakuuPlus.Sim;

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
}
