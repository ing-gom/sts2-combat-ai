using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.53 — Combat-specific context classifier. Inspects the current enemy
/// roster and returns flags describing the encounter shape so PlanScorer can
/// upweight situationally-useful axes.
///
/// Examples of what this captures:
///   • Multi-hit attacker present → Weak / Frail more valuable
///   • Single big-hit boss → Block / Vuln more valuable
///   • AOE swarm (3+ enemies) → AOE attacks more valuable
///   • Buffing enemy ↑ next-turn threat → preempt-kill or block-up
///   • DoT-friendly fight (long boss) → Poison / Doom scaling
///   • Burst-friendly fight (low total HP) → upfront damage
///
/// Output is a CombatProfile struct consumed by PlanScorer to add a
/// per-card situational bonus (typically 50-200 points).
///
/// Pure observation of current visible state. No future-sim.
/// </summary>
internal static class CombatContext
{
    public readonly struct CombatProfile
    {
        public readonly bool HasMultiHitAttacker;   // enemy with IntentRepeats ≥ 2
        public readonly bool HasSingleBigHitter;    // enemy with IntentDamage ≥ 20 × 1 hit
        public readonly bool IsAoeEncounter;        // ≥ 3 alive non-inert enemies
        public readonly bool HasBuffingEnemy;       // enemy with Buff intent (Vigor/Strength gain)
        public readonly bool IsLongFight;           // RemainingTurns ≥ 5
        public readonly bool IsShortFight;          // RemainingTurns ≤ 2
        public readonly bool HasArtifactEnemy;      // enemy with ArtifactAmount > 0 (blocks debuffs)
        public readonly bool HasThornsEnemy;        // enemy with ThornsAmount > 0
        public readonly int AliveEnemies;

        public CombatProfile(bool multiHit, bool bigHit, bool aoe, bool buffing,
                              bool longFight, bool shortFight, bool artifact,
                              bool thorns, int alive)
        {
            HasMultiHitAttacker = multiHit;
            HasSingleBigHitter = bigHit;
            IsAoeEncounter = aoe;
            HasBuffingEnemy = buffing;
            IsLongFight = longFight;
            IsShortFight = shortFight;
            HasArtifactEnemy = artifact;
            HasThornsEnemy = thorns;
            AliveEnemies = alive;
        }
    }

    public static CombatProfile Profile(SimState state)
    {
        bool multiHit = false, bigHit = false, buffing = false;
        bool artifact = false, thorns = false;
        int alive = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            if (e.IsInert) continue;
            alive++;
            if (e.HasAttackIntent && e.IntentRepeats >= 2) multiHit = true;
            if (e.HasAttackIntent && e.IntentRepeats == 1 && e.IntentDamage >= 20) bigHit = true;
            if (e.HasBuffIntent) buffing = true;
            if (e.ArtifactAmount > 0) artifact = true;
            if (e.ThornsAmount > 0) thorns = true;
        }
        int turns = RemainingTurnsEstimator.From(state);
        return new CombatProfile(
            multiHit: multiHit,
            bigHit: bigHit,
            aoe: alive >= 3,
            buffing: buffing,
            longFight: turns >= 5,
            shortFight: turns <= 2,
            artifact: artifact,
            thorns: thorns,
            alive: alive);
    }

    /// <summary>
    /// Per-card situational bonus given the combat profile. Reads card axes
    /// and biases toward cards that are particularly effective for THIS
    /// encounter type.
    ///
    /// Magnitudes intentionally modest (30-150 points) so they nudge ties
    /// without overriding direct effect scoring.
    /// </summary>
    public static int ContextBonus(SimCard card, CombatProfile profile)
    {
        if (card.IsCurseOrStatus) return 0;
        var axes = card.Axes;
        int bonus = 0;

        // Multi-hit attacker → Weak/Frail/StrengthDown shines
        if (profile.HasMultiHitAttacker)
        {
            if (axes.Contains("WEAK_PRODUCER") || card.PowerApps.ContainsKey("WeakPower"))
                bonus += 120;
            if (axes.Contains("STRENGTH_DOWN"))
                bonus += 100;
        }

        // Single big hitter → Block / Vuln-applier (Vuln on +50% to kill them fast)
        if (profile.HasSingleBigHitter)
        {
            if (card.Block > 0) bonus += 80;
            if (axes.Contains("VULN_PRODUCER") || card.PowerApps.ContainsKey("VulnerablePower"))
                bonus += 80;
        }

        // AOE encounter → AOE attacks shine
        if (profile.IsAoeEncounter)
        {
            if (card.Target == TargetType.AllEnemies) bonus += 100;
            if (axes.Contains("ATTACK_AOE") || axes.Contains("AOE")) bonus += 80;
        }

        // Long fight → scaling / DoT
        if (profile.IsLongFight)
        {
            if (axes.Contains("POISON_PRODUCER") || axes.Contains("POISON_AMPLIFIER")) bonus += 80;
            if (axes.Contains("DOOM_PRODUCER") || axes.Contains("DOOM_AMPLIFIER")) bonus += 80;
            if (card.IsPower) bonus += 60;  // Powers pay off over many turns
            if (axes.Contains("SCALING")) bonus += 60;
        }

        // Short fight → burst > setup
        if (profile.IsShortFight)
        {
            if (card.IsPower) bonus -= 100;  // Powers don't tick fast enough
            if (axes.Contains("SCALING")) bonus -= 60;
            if (card.IsAttack && card.TotalDamage >= 15) bonus += 80;  // burst attack
        }

        // Artifact enemy → debuff apply mostly wasted (1-2 wasted on artifact)
        if (profile.HasArtifactEnemy)
        {
            if (axes.Contains("VULN_PRODUCER") || axes.Contains("WEAK_PRODUCER")
                || axes.Contains("FRAIL_PRODUCER") || axes.Contains("DEBUFF"))
                bonus -= 60;
        }

        // Thorns enemy → multi-hit / cantrip attacks are self-harming
        if (profile.HasThornsEnemy)
        {
            if (card.IsAttack && card.Hits >= 2) bonus -= 80;
        }

        // Buffing enemy → kill or block hard NOW (next turn they hit harder)
        if (profile.HasBuffingEnemy)
        {
            if (card.IsAttack && card.TotalDamage >= 10) bonus += 50;  // preempt kill bias
            if (card.Block >= 8) bonus += 50;
        }

        return bonus;
    }
}
