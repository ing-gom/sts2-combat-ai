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
        // B5a follow-up — Boss encounter detection. SimEnemy.IsBoss is set
        // by the Snapshotter (boss-room top creature with the highest HP);
        // PlanScorer had no Boss-aware scoring path before this. Adds a
        // "weak-deck-vs-Boss should play defensive attrition" branch in
        // ContextBonus; the strong-deck baseline gets unchanged scoring
        // (the bonus only triggers when the deck's expected damage budget
        // can't outpace the Boss).
        public readonly bool HasBoss;

        public CombatProfile(bool multiHit, bool bigHit, bool aoe, bool buffing,
                              bool longFight, bool shortFight, bool artifact,
                              bool thorns, int alive, bool hasBoss)
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
            HasBoss = hasBoss;
        }
    }

    public static CombatProfile Profile(SimState state)
    {
        bool multiHit = false, bigHit = false, buffing = false;
        bool artifact = false, thorns = false;
        bool hasBoss = false;
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
            // B5a follow-up — any Boss in the roster flips the flag.
            // Snapshotter set IsBoss on the boss-room top creature (one
            // per encounter at most), so the flag is effectively per-fight.
            if (e.IsBoss) hasBoss = true;
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
            alive: alive,
            hasBoss: hasBoss);
    }

    /// <summary>
    /// Per-card situational bonus given the combat profile. Reads card axes
    /// and biases toward cards that are particularly effective for THIS
    /// encounter type.
    ///
    /// `throughput` is the deck's overall damage / cycling profile from
    /// `DeckThroughput.Compute`. Used to gate the Boss conservative bonus
    /// so strong decks (high AvgDamagePerTurn) don't lose finisher
    /// priority on Boss fights — only weak decks get the attrition lean.
    ///
    /// Magnitudes intentionally modest (30-150 points) so they nudge ties
    /// without overriding direct effect scoring.
    /// </summary>
    public static int ContextBonus(SimCard card, CombatProfile profile,
                                    DeckThroughput.Profile? throughput = null,
                                    int? finisherBudget = null)
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

        // B5a follow-up — Boss encounter biases. The B5a-tree planner-
        // bias-sweep showed PlanScorer drops to 22% on Boss with a sampled
        // deck vs 66% on the strong deck (44pp gap). The strong deck's
        // multi-turn burst combos (Inflame→DemonForm→Whirlwind) aren't
        // present on a random sample, so the Boss-side rule has to lean
        // toward attrition: defend first, scale gradually, conserve cycles.
        //
        // B-2: gate the bonus on a *weak-deck* flag — DeckThroughput's
        // AvgDamagePerTurn below WeakDeckDptThreshold means the deck
        // can't outpace the Boss with burst plays and benefits from the
        // attrition lean. Strong decks (Inflame + Whirlwind etc.) keep
        // their finisher priority on Boss fights, which was lost in B-1
        // (expensive-arm Boss WP dropped 6.3pp). Threshold tuned so the
        // strong-deck baseline keeps treating the bonus as inactive.
        if (profile.HasBoss && throughput.HasValue
            && throughput.Value.AvgDamagePerTurn < WeakDeckDptThreshold)
        {
            // Boss fights are long and HP-heavy. Powers/scaling pay off
            // over many turns; cantrip block stays useful for the whole
            // duration. Mirror the IsLongFight bias but keyed off Boss
            // identity rather than turn-count estimation.
            if (card.IsPower) bonus += 40;
            if (axes.Contains("SCALING")) bonus += 40;
            if (card.Block > 0) bonus += 30;

            // 2026-05-27 C-path — finisher budget overlay. When the deck
            // also lacks burst budget (top-K finisher sum below the
            // single-Boss-window threshold), the planner has no realistic
            // path to close the Boss in a couple of turns. Stack extra
            // attrition lean: prioritize Block accumulation and SCALING
            // setup that turns the long fight winnable instead of betting
            // on a finisher window that won't materialize.
            if (finisherBudget.HasValue
                && finisherBudget.Value < FinisherBudgetThreshold)
            {
                if (card.Block > 0) bonus += 20;
                if (axes.Contains("SCALING")) bonus += 20;
                // Powers already +40 above; another +10 nudges them past
                // mid-fight burst attacks when the burst can't finish.
                if (card.IsPower) bonus += 10;
            }
        }

        return bonus;
    }

    /// Threshold for the weak-deck Boss-attrition bias. AvgDamagePerTurn
    /// below this means the deck doesn't have the burst budget to race a
    /// Boss directly; defensive scaling is the better strategy. Tuned via
    /// planner-benchmark.ps1 (strong deck stays above this) + planner-
    /// bias-sweep (sampled deck stays below). Anything in this range
    /// gets the attrition lean.
    private const int WeakDeckDptThreshold = 30;

    /// Threshold for the C-path finisher-budget Boss-attrition overlay.
    /// Total top-K finisher damage below this means the deck cannot close
    /// a Boss inside a single burst window (typical Boss HP 150-250, so
    /// 80 represents "less than ~half a kill in one wave"). Tuned to
    /// avoid firing on strong decks (Whirlwind / KinglyPunch finisher
    /// stacks easily push budget past 80).
    private const int FinisherBudgetThreshold = 80;
}
