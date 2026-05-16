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
    /// </summary>
    public static int PassiveValue(OrbKind kind, SimState state, int aliveEnemies)
    {
        int turns = EstimateTurnsLeft(state);
        return kind switch
        {
            OrbKind.Lightning => 3 * DmgScore * turns,                       // 3 dmg/turn
            OrbKind.Frost     => 2 * BlockScore * turns,                     // 2 block/turn
            // Dark passive raises the orb's own evokeVal — value materialises only on evoke;
            // attribute roughly half the accumulated value here so channeling early Dark
            // doesn't look worse than evoking immediately.
            OrbKind.Dark      => 6 * DmgScore * turns / 2,
            OrbKind.Plasma    => 1 * EnergyScore * turns,                    // 1 energy/turn
            // Glass passive decays 4→3→2→1→0 over 4 turns. AOE multiplies by alive enemies.
            OrbKind.Glass     => GlassPassiveSum(turns) * DmgScore * System.Math.Max(1, aliveEnemies),
            _ => 0,
        };
    }

    /// <summary>
    /// Value of evoking an orb of this kind once. For Dark, callers should pass the
    /// accumulated evokeVal explicitly (default 6 is the initial value).
    /// </summary>
    public static int EvokeValue(OrbKind kind, int aliveEnemies, int darkAccumulated = 6)
    {
        return kind switch
        {
            OrbKind.Lightning => 8 * DmgScore,                                            // 8 dmg
            OrbKind.Frost     => 5 * BlockScore,                                          // 5 block
            OrbKind.Dark      => System.Math.Max(6, darkAccumulated) * DmgScore,          // accumulated dmg
            OrbKind.Plasma    => 2 * EnergyScore,                                         // 2 energy
            OrbKind.Glass     => 8 * DmgScore * System.Math.Max(1, aliveEnemies),         // AOE 8 (passive×2 from initial)
            _ => 0,
        };
    }

    // Glass passive sum over the next `turns` turns, accounting for decay.
    // Starting at 4 the sum is 4+3+2+1=10 over 4 turns. After 4 turns no more value.
    private static int GlassPassiveSum(int turns)
    {
        int start = 4, sum = 0;
        for (int i = 0; i < turns && start > 0; i++)
        {
            sum += start;
            start--;
        }
        return sum;
    }
}
