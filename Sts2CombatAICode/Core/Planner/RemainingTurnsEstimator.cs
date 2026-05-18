using System;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.6 — Heuristic remaining-combat-length estimator. Power-passive
/// handlers (MAYHEM / AGGRESSION / STAMPEDE / NOSTALGIA / JUGGLING /
/// CREATIVE_AI / HELLO_WORLD / SPECTRUM_SHIFT) previously used a hard-coded
/// <c>RemainingTurnsProxy = 3</c>; this estimator replaces that with a state-
/// derived ratio so a 1-turn-remaining combat correctly devalues passives and
/// a long boss fight correctly inflates them.
///
/// The math is intentionally O(1): sum of alive enemy HP divided by the
/// player's hand-derived damage-per-turn estimate, clamped to [1, 10]. No
/// forward simulation — the estimator is called per scoring (≈200×/turn) and
/// must stay cheap. The crude playerDpt proxy (sum of hand Attack damage / 2)
/// approximates "energy/draw dilution": with 3 energy and ~1.5-cost mean
/// attacks, you play roughly half your hand's attacks per turn.
/// </summary>
internal static class RemainingTurnsEstimator
{
    /// <summary>Lower bound — there's always at least 1 turn to consider.</summary>
    public const int MinTurns = 1;

    /// <summary>
    /// Upper bound — caps absurd long-fight estimates from very low playerDpt
    /// (e.g. opening hand with no attacks). 10 is generous: real STS2 boss
    /// fights resolve in 6-9 turns.
    /// </summary>
    public const int MaxTurns = 10;

    /// <summary>
    /// Fallback when <paramref name="state"/> is unavailable. Matches the
    /// historical <c>RemainingTurnsProxy</c> constant the v0.7.x handlers used.
    /// </summary>
    public const int FallbackTurns = 3;

    public static int From(SimState? state)
    {
        if (state == null) return FallbackTurns;

        int enemyHp = 0;
        foreach (var e in state.Enemies)
        {
            if (e.Hp <= 0) continue;   // already dead — skip
            enemyHp += e.Hp;
        }
        if (enemyHp <= 0) return MinTurns;

        int playerDpt = EstimatePlayerDpt(state);
        // 0-dpt = Power-only opener / pure-block turn. Hand will draw attacks
        // next turn so MaxTurns over-credits passives here — fall back to the
        // historical static value instead.
        if (playerDpt <= 0) return FallbackTurns;

        int estimate = enemyHp / playerDpt;
        if (estimate < MinTurns) return MinTurns;
        if (estimate > MaxTurns) return MaxTurns;
        return estimate;
    }

    private static int EstimatePlayerDpt(SimState state)
    {
        // Sum of TotalDamage across hand Attacks. Divide by 2 to account for
        // energy / draw dilution — you can't play every hand attack each turn.
        // Add player Strength × 2 (rough proxy for ~2 attacks/turn benefiting
        // from each Strength point). v0.7.11 — also add ally per-turn damage
        // (Necrobinder skeletons): each alive ally contributes its declared
        // intent damage every turn for free.
        int handAttackDamage = 0;
        foreach (var c in state.Hand)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            handAttackDamage += c.TotalDamage;
        }
        int strengthBonus = Math.Max(0, state.PlayerStrength) * 2;
        int allyDamage = 0;
        foreach (var ally in state.Allies)
        {
            if (!ally.IsAlive || !ally.HasAttackIntent) continue;
            allyDamage += ally.TotalIntentDamage;
        }
        return handAttackDamage / 2 + strengthBonus + allyDamage;
    }
}
