using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.23 Phase 9b — scoring for "what's the value of adding a copy of this card
/// to hand for FUTURE play" — distinct from PlanScorer's "what's the value of
/// PLAYING this card now". Used by Sts2CombatCore's PlannerCardSelector when
/// fetch-class cards (DUAL_WIELD copy a card; ARMAMENTS upgrade a card; the
/// Silent's SECRET_TECHNIQUE / SECRET_WEAPON draw-pile picks) need to choose
/// among candidate cards in hand.
///
/// Why PlanScorer.Score isn't the right answer for copy decisions:
///   • Lethal bonuses (+5000 for "this kills the target") are turn-specific —
///     a copy lands in hand for a future turn where the current target may
///     already be dead.
///   • Target-priority bonuses (high-Strength enemy, Ritual enemy) assume
///     this card is the IMMEDIATE play. The copy may face different enemies.
///   • Cost-3 attacks (BLUDGEON, HEAVY_BLADE) score high "now" because their
///     raw damage × DamagePerPointBonus dominates, but their FUTURE plays are
///     gated by the 3-energy budget — most turns won't accommodate them.
///
/// Model:
///   copyValue(card) = perPlayValue(card)
///                   × PlayabilityFactor(card.Cost)
///                   × min(1, RemainingTurns / 3)
///
///   • perPlayValue: for Attacks, EffectiveDmgPerEnergy × cost × 50 (matches
///     PlanScorer's DamagePerPointBonus scale, excludes lethal / target-specific
///     bonuses). For Powers / Skills, PlanScorer.Score / cost (cost-normalized
///     existing score so cost-3 doesn't dominate cost-1 cards purely by absolute
///     magnitude).
///   • PlayabilityFactor: cost-keyed lookup approximating P(playable on a
///     future turn). cost-0 = 1.0 (always plays), cost-1 = 0.95, cost-2 = 0.60,
///     cost-3 = 0.30, cost-4+ = 0.10. Calibrated to favor cheap-flexible copies
///     over rare-window expensive copies.
///   • Fight-length factor: shorter remaining fight → less time to play the
///     copy, lower expected value. Caps at 1.0 (no bonus past 3-turn fights).
/// </summary>
internal static class CopyValueScorer
{
    /// <summary>
    /// Probability proxy that a copy of cost-C card will be playable on a
    /// future turn given the typical 3-energy budget. Hand-tuned from
    /// observation that strong-deck Ironclad averages ~2.4 cards / turn
    /// (~1.25 mean cost) — most turns can absorb a cost-1 (~95%), often
    /// a cost-2 (~60%), occasionally a cost-3 (~30%).
    /// </summary>
    public static double PlayabilityFactor(int cost) => cost switch
    {
        <= 0 => 1.00,
        1    => 0.95,
        2    => 0.60,
        3    => 0.30,
        _    => 0.10,
    };

    public static double Score(SimCard card, int targetIdx, SimState state)
    {
        int rt = RemainingTurnsEstimator.From(state);
        double fightFactor = System.Math.Min(1.0, rt / 3.0);
        double playability = PlayabilityFactor(card.Cost);

        double perPlayValue;
        if (card.IsAttack && card.Damage > 0)
        {
            // EffectiveDmgPerEnergy already includes Strength, Weak,
            // Vulnerable, Vigor, X-cost scaling, Intangible / HardenedShell /
            // DamageCapPerHit caps, EchoForm, Thorns subtraction. Multiplied
            // back by cost gives "expected damage per play". Then × 50 to
            // match PlanScorer's DamagePerPointBonus units so cross-type
            // comparison with the Non-Attack branch stays meaningful.
            double dpe = targetIdx >= 0
                ? card.EffectiveDmgPerEnergy(state, targetIdx)
                : card.EffectiveDmgPerEnergy(state);
            perPlayValue = dpe * System.Math.Max(1, card.Cost) * 50.0;
        }
        else
        {
            // Powers / Skills — defer to existing PlanScorer scoring but
            // normalize by cost so the playability discount becomes the
            // decisive factor for cost-3 cards. PlanScorer.Score includes
            // the Power-card sequencing bonuses (Inflame-before-Strike etc.)
            // that ARE relevant to future plays of the copy.
            int rawScore = PlanScorer.Score(card, targetIdx, state);
            perPlayValue = (double)rawScore / System.Math.Max(1, card.Cost);
        }

        return perPlayValue * playability * fightFactor;
    }

    /// <summary>Convenience overload — no target context (caller picks best target).</summary>
    public static double Score(SimCard card, SimState state) => Score(card, -1, state);
}
