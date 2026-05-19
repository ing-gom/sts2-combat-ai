using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.63 — Per-card variance score. Some cards produce non-deterministic
/// outcomes (random target, random pool card, variable hit count). Existing
/// AI handlers ALREADY model these via binomial probability / mean / pool
/// means — this module adds an explicit variance tag for two purposes:
///
///   1. Diagnostic — surface "this card is high-variance" in DecisionLog
///   2. Decision-time — modest penalty in CRITICAL situations (lethal-this-
///      turn or survival-tight) where deterministic cards are safer
///
/// Levels:
///   • None     — fully deterministic (STRIKE: fixed dmg, fixed target)
///   • Low      — minor variance (X-cost where energy is known)
///   • Medium   — RANDOM target attacks (binomial spread)
///   • High     — random card generation / random pile auto-play
///
/// Modest scoring impact (max -100). Won't override clear plays; just nudges
/// tie-breaks toward reliability when stakes are highest.
/// </summary>
internal static class CardVariance
{
    public enum Level
    {
        None,
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// v0.7.64 — Classify takes SimState so RANDOM-target attacks correctly
    /// collapse to Level.None when only 1 alive enemy remains (all hits land
    /// on the same target = deterministic damage).
    /// </summary>
    public static Level Classify(SimCard card, SimState state)
    {
        if (card.IsCurseOrStatus) return Level.None;
        var axes = card.Axes;

        // High variance: random card generation, random auto-play, random
        // pool pulls. Outcome can be a curse or a power — wide spread.
        if (axes.Contains("CARD_GEN") && axes.Contains("RANDOM"))
            return Level.High;
        // Specific high-variance cards
        // v0.7.87 — Strip CARD. prefix; SimCard.Id is the short entry name.
        if (card.Id == "CASCADE" || card.Id == "CATASTROPHE"
            || card.Id == "UPROAR" || card.Id == "BEAT_DOWN"
            || card.Id == "WISH" || card.Id == "LARGESSE"
            || card.Id == "DISCOVERY" || card.Id == "DISTRACTION"
            || card.Id == "WHITE_NOISE" || card.Id == "SPLASH"
            || card.Id == "HIDDEN_GEM")
            return Level.High;

        // Medium: random-target attacks — but only when there are 2+ alive
        // enemies. With 1 alive enemy, every hit lands on the same target =
        // deterministic damage.
        bool isRandomTargetAttack = card.Target == TargetType.RandomEnemy
                                   || (axes.Contains("RANDOM") && card.IsAttack);
        if (isRandomTargetAttack)
        {
            int aliveEnemies = 0;
            foreach (var e in state.Enemies)
                if (e.IsAlive) aliveEnemies++;
            if (aliveEnemies <= 1) return Level.None;  // deterministic single-target
            return Level.Medium;
        }

        // Low: X-cost (variance bounded by energy)
        if (axes.Contains("X_COST")) return Level.Low;
        // EXHAUST_TARGET_RANDOM: lose a random hand card on play
        if (axes.Contains("EXHAUST_TARGET_RANDOM")) return Level.Low;

        return Level.None;
    }

    /// <summary>
    /// Backward-compat overload — assumes worst case (2+ enemies). Prefer
    /// the SimState-aware overload.
    /// </summary>
    public static Level Classify(SimCard card)
    {
        if (card.IsCurseOrStatus) return Level.None;
        var axes = card.Axes;
        if (axes.Contains("CARD_GEN") && axes.Contains("RANDOM")) return Level.High;
        if (card.Target == TargetType.RandomEnemy) return Level.Medium;
        if (axes.Contains("RANDOM") && card.IsAttack) return Level.Medium;
        if (axes.Contains("X_COST")) return Level.Low;
        if (axes.Contains("EXHAUST_TARGET_RANDOM")) return Level.Low;
        return Level.None;
    }

    /// <summary>
    /// Per-card variance penalty. Applied modestly — high-variance cards
    /// stay competitive in normal play but lose ties in critical moments.
    /// </summary>
    public static int ReliabilityPenalty(SimCard card, SimState state,
                                          SurvivalProjection.Projection race,
                                          CombatPlan.Stage stage)
    {
        var level = Classify(card, state);
        if (level == Level.None) return 0;

        // Critical situations: lethal-soon (Tight race) or low-HP — prefer
        // deterministic cards. In all-clear situations (Sustain phase,
        // Winning race), variance is fine.
        bool critical = race.Race == SurvivalProjection.RaceOutcome.Tight
                      || race.Race == SurvivalProjection.RaceOutcome.Losing
                      || stage == CombatPlan.Stage.Cleanup;

        if (!critical) return 0;  // variance OK in non-critical

        switch (level)
        {
            case Level.High: return -100;
            case Level.Medium: return -50;
            case Level.Low: return -20;
            default: return 0;
        }
    }
}
