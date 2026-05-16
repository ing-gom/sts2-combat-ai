using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Numeric value of orb passive / evoke effects, in PlanScorer score units (≈ same scale
/// as DamagePerPointBonus 50 / BlockPerPointBonus 30 / 1 energy ≈ 500).
///
/// Passive values are per-turn — multiply by remaining-turn estimate to get total value.
/// Evoke values are one-shot.
/// </summary>
internal static class OrbValueCatalog
{
    // Per-point conversion constants — match PlanScorerWeights conventions.
    private const int DmgScore   = 50;   // 1 damage   ≈ 50 score
    private const int BlockScore = 30;   // 1 block    ≈ 30 score
    private const int EnergyScore = 500; // 1 energy   ≈ 500 score

    /// <summary>
    /// Estimated turns the fight will still last. Used to multiply per-turn passive value.
    /// Cap at 5 — past that, plans are too uncertain to weight more heavily.
    /// Falls back to 1 turn when the simulation can't estimate (very short fight or no data).
    /// </summary>
    public static int EstimateTurnsLeft(SimState state)
    {
        int totalHp = 0;
        int dpsThisTurn = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            totalHp += e.Hp + e.Block;
        }
        // Crude dps proxy: hand average damage. Min 5 so we don't divide by ~0.
        foreach (var c in state.Hand)
        {
            if (c.IsAttack) dpsThisTurn += c.TotalDamage;
        }
        if (dpsThisTurn < 5) dpsThisTurn = 5;
        if (totalHp <= 0) return 1;
        int turns = (totalHp + dpsThisTurn - 1) / dpsThisTurn;
        if (turns < 1) turns = 1;
        if (turns > 5) turns = 5;
        return turns;
    }

    /// <summary>
    /// Value of channeling an orb of this kind (passive contribution over the remaining fight).
    /// v0.5 — Focus scaling: every per-turn passive tick gains +PlayerFocus damage/block
    /// (Plasma untouched since energy isn't Focus-scaled). Negative Focus is honored
    /// (over-debuffed orbs deal less); per-tick value floored at 0 to match the game's
    /// damage-can't-go-negative clamp.
    /// </summary>
    public static int PassiveValue(OrbKind kind, SimState state, int aliveEnemies)
    {
        int turns = EstimateTurnsLeft(state);
        int focus = state.PlayerFocus;
        int lightningPer = System.Math.Max(0, 3 + focus);
        int frostPer     = System.Math.Max(0, 2 + focus);
        int darkPerTick  = System.Math.Max(0, 6 + focus);
        return kind switch
        {
            OrbKind.Lightning => lightningPer * DmgScore * turns,
            OrbKind.Frost     => frostPer * BlockScore * turns,
            // Dark passive raises the orb's own evokeVal — value materialises only on evoke;
            // attribute roughly half the accumulated value here so channeling early Dark
            // doesn't look worse than evoking immediately. Focus adds to the tick value too.
            OrbKind.Dark      => darkPerTick * DmgScore * turns / 2,
            OrbKind.Plasma    => 1 * EnergyScore * turns,                    // 1 energy/turn (no Focus)
            // Glass passive decays 4→3→2→1→0 over 4 turns. AOE multiplies by alive enemies.
            // Focus adds a flat per-tick delta on top of the decay curve, floored at 0.
            OrbKind.Glass     => GlassPassiveSumWithFocus(turns, focus) * DmgScore
                                * System.Math.Max(1, aliveEnemies),
            _ => 0,
        };
    }

    /// <summary>
    /// Value of evoking an orb of this kind once. For Dark, callers should pass the
    /// accumulated evokeVal explicitly (default 6 is the initial value).
    /// v0.5 — Focus scaling: +PlayerFocus on damage/block evokes, floored at 0 to
    /// match game clamping. Plasma untouched.
    /// </summary>
    public static int EvokeValue(OrbKind kind, int aliveEnemies, int darkAccumulated = 6, int focus = 0)
    {
        int lightningEvoke = System.Math.Max(0, 8 + focus);
        int frostEvoke     = System.Math.Max(0, 5 + focus);
        int glassEvoke     = System.Math.Max(0, 8 + focus);
        return kind switch
        {
            OrbKind.Lightning => lightningEvoke * DmgScore,
            OrbKind.Frost     => frostEvoke * BlockScore,
            OrbKind.Dark      => System.Math.Max(6, darkAccumulated) * DmgScore,
                                                                                              // (Dark accumulator already absorbed Focus per tick.)
            OrbKind.Plasma    => 2 * EnergyScore,                                             // 2 energy (no Focus)
            OrbKind.Glass     => glassEvoke * DmgScore * System.Math.Max(1, aliveEnemies),
            _ => 0,
        };
    }

    // Glass passive sum over the next `turns` turns, accounting for decay AND Focus.
    // Each tick value is max(0, start + focus); start decays 4→3→2→1→0.
    private static int GlassPassiveSumWithFocus(int turns, int focus)
    {
        int start = 4, sum = 0;
        for (int i = 0; i < turns; i++)
        {
            int tick = System.Math.Max(0, start + focus);
            sum += tick;
            if (start > 0) start--;
            // Stop early when ticks would be 0 forever (start hit 0, focus ≤ 0).
            if (start == 0 && focus <= 0) break;
        }
        return sum;
    }
}
