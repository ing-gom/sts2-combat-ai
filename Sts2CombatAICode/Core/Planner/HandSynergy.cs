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
///   Bash (Vulnerable on enemy) with Twin Strike (2 hits) + Strike (1 hit) in hand
///       → 3 hits × 40 = +120 (per-hit, not per-attack — Vuln amplifies every hit)
///   Weak on a 5dmg×3hit enemy → per-hit savings 2 × 3 hits × 2 turns = 12 HP saved
///       (Weak rounds down per hit, so multi-hit enemies lose proportionally more
///        damage than the flat-percentage shortcut suggests)
/// </summary>
internal static class HandSynergy
{
    private const int StrengthSynergyPerAttack = 50;     // damage point ≈ DamagePerPointBonus
    private const int DexteritySynergyPerSkill = 30;     // block point
    private const int VulnerableSynergyPerHit  = 40;     // per OUR hit (Vuln × +50% per hit)
    private const int WeakSavingsPerHpPoint    = 30;     // score per HP of enemy damage prevented
    private const int WeakSavingsTurnCap       = 2;      // future turns to count

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
            "StrengthPower" => remainingAttacks * amount * StrengthSynergyPerAttack,
            "TemporaryStrengthPower" => remainingAttacks * amount * StrengthSynergyPerAttack,
            "DexterityPower" => remainingSelfBlocks * amount * DexteritySynergyPerSkill,
            "TemporaryDexterityPower" => remainingSelfBlocks * amount * DexteritySynergyPerSkill,

            // Vuln amplifies +50% per HIT (not per attack). Twin Strike (2 hits) gets
            // double the Vuln payoff of Strike (1 hit).
            "VulnerablePower" => RemainingHits(self, state) * VulnerableSynergyPerHit,

            // Weak savings scale with enemy hit-count × per-hit rounding. Multi-hit
            // enemies lose proportionally more damage than the flat estimate.
            "WeakPower" => ComputeWeakSavings(amount, state),

            _ => 0,
        };
    }

    private static int RemainingHits(SimCard self, SimState state)
    {
        int total = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self) || c.Played || !c.IsAttack) continue;
            total += System.Math.Max(1, c.Hits);
        }
        return total;
    }

    /// <summary>
    /// Estimated HP saved by applying Weak this turn. Per-hit STS-accurate model:
    /// each enemy attack hit deals floor((IntentDamage + Strength) × 0.75), so
    /// savings per hit = perHit − floor(perHit × 0.75) ≈ ceil(perHit × 0.25).
    /// Multi-hit enemies multiply this savings by IntentRepeats; Weak persisting
    /// over multiple turns multiplies again, capped at WeakSavingsTurnCap.
    /// </summary>
    private static int ComputeWeakSavings(int weakStacks, SimState state)
    {
        int hpSaved = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive || !e.HasAttackIntent || e.IsInert) continue;
            int perHit = e.IntentDamage + System.Math.Max(0, e.StrengthAmount);
            int perHitSavings = perHit - (int)(perHit * 0.75);
            if (perHitSavings <= 0) continue;
            int turnSavings = perHitSavings * System.Math.Max(1, e.IntentRepeats);
            int effectiveTurns = System.Math.Min(weakStacks, WeakSavingsTurnCap);
            hpSaved += turnSavings * effectiveTurns;
        }
        return hpSaved * WeakSavingsPerHpPoint;
    }
}
