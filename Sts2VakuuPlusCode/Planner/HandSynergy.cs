using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2VakuuPlus.Sim;

namespace Sts2VakuuPlus.Planner;

/// <summary>
/// Hand-composition heuristics. Approximates what a 2-step lookahead would discover
/// (Inflame → 3 Strikes combo) without building a real forward simulator. Power
/// applications get a bonus proportional to the number of cards in hand that
/// benefit from the buff/debuff.
///
/// Examples:
///   Inflame (Strength+2) with 3 attacks in hand → +3 × 2 × 50 = +300 score
///       (each attack gains ~2 damage ≈ ~50 score; 3 attacks remain)
///   Bash (Vulnerable on enemy) with 3 attacks in hand → +3 × 50 = +150 score
///       (each remaining attack does ~50% more damage to the vulnerable enemy)
/// </summary>
internal static class HandSynergy
{
    private const int StrengthSynergyPerAttack = 50;     // damage point ≈ DamagePerPointBonus
    private const int DexteritySynergyPerSkill = 30;     // block point
    private const int VulnerableSynergyPerAttack = 50;
    private const int WeakSynergyPerAttacker = 30;       // less impactful but still scales

    /// <summary>
    /// Bonus added to a power-apply's value based on hand composition.
    /// <paramref name="self"/> is the card being scored (excluded from hand counting).
    /// <paramref name="amount"/> is the power stack count.
    /// </summary>
    public static int Compute(string powerName, int amount, SimCard self, SimState state)
    {
        if (amount <= 0) return 0;

        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsAttack);
        int remainingSelfBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsSkill && c.Block > 0
            && (c.Target == TargetType.Self || c.Target == TargetType.AnyPlayer));

        return powerName switch
        {
            // Self-buff synergies — multiplied by both stacks AND beneficiary count.
            "StrengthPower" => remainingAttacks * amount * StrengthSynergyPerAttack,
            "TemporaryStrengthPower" => remainingAttacks * amount * StrengthSynergyPerAttack,
            "DexterityPower" => remainingSelfBlocks * amount * DexteritySynergyPerSkill,
            "TemporaryDexterityPower" => remainingSelfBlocks * amount * DexteritySynergyPerSkill,

            // Enemy-debuff synergies — Vulnerable/Weak amount = turn count, so don't
            // scale with stacks (just persistence). Scale only by remaining beneficiary cards.
            "VulnerablePower" => remainingAttacks * VulnerableSynergyPerAttack,
            "WeakPower" => remainingAttacks * WeakSynergyPerAttacker,

            _ => 0,
        };
    }
}
