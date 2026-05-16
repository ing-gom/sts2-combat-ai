using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

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
    ///
    /// v0.5 — depth-2 lookahead double-count correction. ActionPlanner's lookahead
    /// already scores ONE next card with the buff applied, so HandSynergy would
    /// double-credit that single beneficiary if it counted every attack/skill in
    /// hand. Subtract one beneficiary so the lookahead's contribution plus this
    /// hand-wide bonus sum to the right total. When only 0–1 beneficiaries exist,
    /// the lookahead fully covers them and HandSynergy returns 0.
    /// </summary>
    public static int Compute(string powerName, int amount, SimCard self, SimState state)
    {
        if (amount <= 0) return 0;

        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsAttack);
        int remainingSelfBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsSkill && c.Block > 0
            && (c.Target == TargetType.Self || c.Target == TargetType.AnyPlayer));

        // Subtract the single beneficiary the depth-2 lookahead will independently
        // credit. Negative-clamp so a hand with 0–1 beneficiaries returns 0.
        int incrementalAtkBeneficiaries  = System.Math.Max(0, remainingAttacks    - 1);
        int incrementalBlockBeneficiaries = System.Math.Max(0, remainingSelfBlocks - 1);

        return powerName switch
        {
            // Self-buff synergies — multiplied by both stacks AND beneficiary count.
            "StrengthPower"          => incrementalAtkBeneficiaries  * amount * StrengthSynergyPerAttack,
            "TemporaryStrengthPower" => incrementalAtkBeneficiaries  * amount * StrengthSynergyPerAttack,
            "DexterityPower"         => incrementalBlockBeneficiaries * amount * DexteritySynergyPerSkill,
            "TemporaryDexterityPower"=> incrementalBlockBeneficiaries * amount * DexteritySynergyPerSkill,

            // Enemy-debuff synergies — Vulnerable/Weak amount = turn count, so don't
            // scale with stacks (just persistence). Scale only by remaining beneficiary cards.
            "VulnerablePower" => incrementalAtkBeneficiaries * VulnerableSynergyPerAttack,
            "WeakPower"       => incrementalAtkBeneficiaries * WeakSynergyPerAttacker,

            _ => 0,
        };
    }
}
