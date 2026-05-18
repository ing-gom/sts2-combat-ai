using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.59 — Multi-turn combat plan tracking. Persists a high-level "plan
/// stage" across turns so the AI commits to a strategy instead of re-
/// computing macro intent every turn.
///
/// Why: each turn currently re-evaluates from scratch via SurvivalProjection
/// + WinConditionInference. That's fine for tactical decisions but means
/// the AI can flip-flop on multi-turn setups. Example:
///   • Turn N: AI plays Inflame (Strength setup) because Sustain phase
///   • Turn N+1: deck cycles, race shifts to Tight → no longer prioritises
///     attacks ⇒ Inflame's investment partially wasted
///
/// CombatPlan tracks the LAST turn's intended stage; subsequent turns get
/// a "stay-the-course" bonus when their plays continue the previous stage.
/// Plan can rotate when state forces it (HP drops, lethal becomes possible).
///
/// Stages:
///   • Opening   — turn 1-2, mostly setup
///   • Setup     — building power/scaling
///   • Burst     — pushing damage
///   • Lockdown  — sustained defense + tick damage
///   • Cleanup   — finishing kills
///
/// State sits in a static field — single-combat session scope. Reset on
/// combat-end (DecisionLogPersister.FlushIfPending already handles this).
/// </summary>
internal static class CombatPlan
{
    public enum Stage
    {
        Opening,
        Setup,
        Burst,
        Lockdown,
        Cleanup,
    }

    // Hand-tracked from VakuuExecutor turn-start. -1 = uninitialised.
    private static int _externalTurn = -1;

    /// <summary>Called by VakuuExecutor at turn start so Classify knows
    /// which round we're on without plumbing it through PlanScorer.</summary>
    public static void NotifyTurn(int round)
    {
        _externalTurn = round;
    }

    /// <summary>Reset on combat start.</summary>
    public static void Reset()
    {
        _externalTurn = -1;
    }

    /// <summary>
    /// Determine the current stage from state + race projection. Pure
    /// function — no internal state mutation beyond the externally-tracked
    /// turn number.
    /// </summary>
    public static Stage Classify(SimState state, SurvivalProjection.Projection race)
    {
        int currentTurn = _externalTurn < 0 ? 99 : _externalTurn;
        if (currentTurn <= 2) return Stage.Opening;
        if (race.Race == SurvivalProjection.RaceOutcome.Decided) return Stage.Cleanup;

        // Cleanup: low enemy HP relative to our DPT
        int aliveHp = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveHp += e.Hp + e.Block;
        if (race.NetDamagePerTurn > 0 && aliveHp <= race.NetDamagePerTurn * 2)
            return Stage.Cleanup;

        // Burst: race is Tight or Losing AND we have attack output
        if ((race.Race == SurvivalProjection.RaceOutcome.Tight
             || race.Race == SurvivalProjection.RaceOutcome.Losing)
            && race.NetDamagePerTurn >= 10)
            return Stage.Burst;

        // Lockdown: sustained defense — many turns to death AND blocking heavily
        var urgency = EnemyTurnSimulator.GetSurvivalUrgency(state);
        if (urgency == SurvivalUrgency.Moderate && race.TurnsToDeath >= 4)
            return Stage.Lockdown;

        // Setup: comfortable race, time to invest
        if (race.Race == SurvivalProjection.RaceOutcome.Winning)
            return Stage.Setup;

        return Stage.Setup;
    }

    /// <summary>
    /// Per-card bonus aligned with the current plan stage. Modest magnitudes;
    /// existing layers (race / phase / archetype) cover the heavy lifting.
    /// This is mostly to dampen flip-flopping.
    /// </summary>
    public static int StageBonus(SimCard card, Stage stage)
    {
        if (card.IsCurseOrStatus) return 0;
        switch (stage)
        {
            case Stage.Opening:
                // Early turns: draw / energy / setup
                if (card.DrawCount > 0) return 30;
                if (card.EnergyGain > 0) return 30;
                if (card.IsPower) return 50;
                return 0;

            case Stage.Setup:
                // Mid-fight investment
                if (card.IsPower) return 70;
                if (card.Axes.Contains("SCALING")) return 40;
                if (card.Axes.Contains("STRENGTH_PRODUCER")
                    || card.Axes.Contains("DEXTERITY_PRODUCER")) return 50;
                return 0;

            case Stage.Burst:
                // Push damage
                if (card.IsAttack) return 40;
                if (card.IsPower && card.IsPlayable) return -50;  // too late for Powers
                return 0;

            case Stage.Lockdown:
                // Sustained defense + tick
                if (card.Block >= 6) return 50;
                if (card.Axes.Contains("POISON_PRODUCER")
                    || card.Axes.Contains("DOOM_PRODUCER")) return 60;
                return 0;

            case Stage.Cleanup:
                // Wrap it up — every hit counts, save no scaling
                if (card.IsAttack) return 60;
                if (card.IsPower) return -100;
                return 0;

            default:
                return 0;
        }
    }
}
