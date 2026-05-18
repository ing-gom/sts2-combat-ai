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

        // v0.7.21 — Effective enemy HP now factors in:
        //   • Block as ~50% of an HP slice (it absorbs once per round but
        //     enemies reset / regen block — averages out to ~half value).
        //   • Per-turn DoT (Poison + Constrict + Doom) is treated as a
        //     parallel damage stream added to playerDpt downstream.
        int effectiveEnemyHp = 0;
        int totalDotPerTurn = 0;
        foreach (var e in state.Enemies)
        {
            if (e.Hp <= 0) continue;
            effectiveEnemyHp += e.Hp + e.Block / 2;
            totalDotPerTurn += e.PoisonAmount + e.ConstrictAmount + e.DoomAmount;
        }
        if (effectiveEnemyHp <= 0) return MinTurns;

        int playerDpt = EstimatePlayerDpt(state);
        int totalDpt = playerDpt + totalDotPerTurn;
        // 0-dpt = Power-only opener / pure-block turn AND no DoT active.
        // Hand will draw attacks next turn so MaxTurns over-credits passives
        // here — fall back to the historical static value instead.
        if (totalDpt <= 0) return FallbackTurns;

        int estimate = effectiveEnemyHp / totalDpt;
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
        //
        // v0.7.21 refinements:
        //   • Project Strength growth from DemonFormPower / RitualPower /
        //     ArsenalPower — these add +1 Str per turn. Use 1.5-turn lookahead
        //     average (half of current + projected future).
        //   • Apply Vulnerable multiplier when alive enemies have Vuln stacks
        //     (1.5× damage). Averaged across alive enemies so multi-enemy
        //     fights with partial Vuln coverage scale proportionally.
        //   • Apply Weak self-debuff (player Weak → outgoing ×0.75).
        int handAttackDamage = 0;
        foreach (var c in state.Hand)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            handAttackDamage += c.TotalDamage;
        }

        // Strength growth projection — for permanent-stacking Power passives.
        int strengthProjection = Math.Max(0, state.PlayerStrength);
        if (state.PlayerPowers != null)
        {
            if (state.PlayerPowers.TryGetValue("DemonFormPower", out var df) && df > 0)
                strengthProjection += df;        // +N already accumulated next turn
            if (state.PlayerPowers.TryGetValue("RitualPower", out var rit) && rit > 0)
                strengthProjection += rit / 2;   // smaller per-turn buff (Ritual is enemy variant typically)
            if (state.PlayerPowers.TryGetValue("ArsenalPower", out var arsenal) && arsenal > 0)
                strengthProjection += arsenal;   // Regent +1/card-generated
        }
        int strengthBonus = strengthProjection * 2;

        int allyDamage = 0;
        foreach (var ally in state.Allies)
        {
            if (!ally.IsAlive || !ally.HasAttackIntent) continue;
            allyDamage += ally.TotalIntentDamage;
        }

        int baseDpt = handAttackDamage / 2 + strengthBonus + allyDamage;

        // Vulnerable multiplier — fraction of alive enemies with Vuln.
        int aliveCount = 0, vulnCount = 0;
        foreach (var e in state.Enemies)
        {
            if (e.Hp <= 0) continue;
            aliveCount++;
            if (e.VulnerableAmount > 0) vulnCount++;
        }
        if (aliveCount > 0 && vulnCount > 0)
        {
            // 1 of 1 → 1.5×, 1 of 2 → 1.25×, etc.
            double vulnMul = 1.0 + 0.5 * vulnCount / aliveCount;
            baseDpt = (int)(baseDpt * vulnMul);
        }

        // Player Weak → outgoing damage ×0.75 (rounded down per-hit in STS).
        if (state.PlayerWeak > 0)
            baseDpt = (int)(baseDpt * 0.75);

        return baseDpt;
    }
}
