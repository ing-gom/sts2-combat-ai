using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.54 — Strategic win-condition inference. Explicitly classifies the
/// current combat into one of a few categories and biases the planner
/// toward the matching strategy.
///
/// Categories:
///   • LethalThisTurn  — sum of in-hand damage ≥ alive enemy HP. Attack-only mode.
///   • LethalSoon      — RemainingTurns ≤ 2 + sufficient projected damage.
///                       Burst attacks, ignore long-term setups.
///   • Sustain         — RemainingTurns ≥ 5 + survivable next turn. Setup
///                       cards (Powers, scaling, persistent block) shine.
///   • Survival        — Fatal / Heavy urgency. Block everything; attack only
///                       if part of a kill plan.
///   • Standard        — Default mid-game. No bias.
///
/// LethalThisTurn already exists as a runtime flag inside PlanScorer.Breakdown
/// (`lethalThisTurn`); this layer adds finer category routing and modest
/// per-card biases (50-200 magnitude). The existing survival-urgency penalty
/// and lethal kill bonus dominate when those situations apply — this is
/// secondary nudging for the gray-zone turns in between.
/// </summary>
internal static class WinConditionInference
{
    public enum Phase
    {
        Standard,
        LethalThisTurn,
        LethalSoon,
        Sustain,
        Survival,
    }

    /// <summary>
    /// Classify the current state into one of the strategic phases.
    /// </summary>
    public static Phase Classify(SimState state)
    {
        // Survival takes precedence — if we're about to die, that overrides
        // everything else.
        var urgency = EnemyTurnSimulator.GetSurvivalUrgency(state);
        if (urgency == SurvivalUrgency.Fatal || urgency == SurvivalUrgency.Heavy)
            return Phase.Survival;

        int turns = RemainingTurnsEstimator.From(state);

        // Aggregate alive enemy HP.
        int aliveHp = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveHp += e.Hp + e.Block;
        if (aliveHp <= 0) return Phase.Standard;

        // Aggregate in-hand attack damage.
        int handDamage = 0;
        foreach (var c in state.Hand)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            handDamage += c.TotalDamage;
        }
        // Crude lethal-this-turn detection: hand damage ≥ aliveHp AND enough
        // energy to play attacks.
        if (handDamage >= aliveHp && state.PlayerEnergy >= 2)
            return Phase.LethalThisTurn;

        if (turns <= 2) return Phase.LethalSoon;
        if (turns >= 5) return Phase.Sustain;
        return Phase.Standard;
    }

    /// <summary>
    /// Per-card bonus based on the inferred phase. Modest magnitudes
    /// (50-180); intended as secondary nudging.
    /// </summary>
    public static int PhaseBonus(SimCard card, Phase phase)
    {
        if (card.IsCurseOrStatus) return 0;
        var axes = card.Axes;

        switch (phase)
        {
            case Phase.LethalThisTurn:
                // Attack-only mode. Penalize anything that doesn't deal damage
                // or set up the kill. Already handled by lethalSetupPenalty +
                // lethalMode in PlanScorer; this is just a small nudge.
                if (card.IsAttack) return 50;
                return 0;

            case Phase.LethalSoon:
                // Burst attacks, debuffs preempt-kill. Setup powers waste turns.
                if (card.IsAttack && card.TotalDamage >= 12) return 100;
                if (card.IsPower) return -100;
                if (axes.Contains("SCALING") && !card.IsAttack) return -60;
                return 0;

            case Phase.Sustain:
                // Long fight — invest in setups + scaling.
                if (card.IsPower) return 120;
                if (axes.Contains("SCALING")) return 60;
                if (axes.Contains("POISON_PRODUCER") || axes.Contains("POISON_AMPLIFIER")) return 80;
                if (axes.Contains("DOOM_PRODUCER") || axes.Contains("DOOM_AMPLIFIER")) return 80;
                if (axes.Contains("FORGE_PRODUCER") || axes.Contains("FORGE_AMPLIFIER")) return 60;
                return 0;

            case Phase.Survival:
                // Block everything. Damage only matters if it kills.
                if (card.Block >= 8) return 180;
                if (card.Block >= 4) return 100;
                if (card.IsAttack) return -80;  // attack defers to survival
                if (axes.Contains("HEAL")) return 150;
                if (card.IsPower) return -120;
                return 0;

            case Phase.Standard:
            default:
                return 0;
        }
    }
}
