using System;
using System.Linq;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Predicts player damage taken if the enemies execute their declared intents this turn.
/// v0.1.1 also exposes intent-aware composition helpers used by PlanScorer to pick
/// strategies (buff-defying kills, defend-aware skip, all-inert-Power-windows).
/// </summary>
internal static class EnemyTurnSimulator
{
    public static int PredictPlayerDmg(SimState s)
    {
        // v0.5 — IntangiblePower on the player caps every incoming hit at 1.
        // Short-circuit: total threat = sum of enemy hit counts − block. Skips the
        // whole per-enemy Vulnerable/Weak chain because none of it matters when each
        // hit lands as exactly 1 damage. Without this, the planner over-defends on
        // Apparition / WraithForm turns where damage is effectively negligible.
        if (s.PlayerIntangible > 0)
        {
            // v0.7.96 — Even under Intangible, Thorns reflects per hit (canonical STS:
            // Thorns triggers on receiving damage, including the 1-capped hit).
            // v0.8.0 — FlameBarrierPower folds into the same reflect total.
            int hits = 0;
            int thornsAmtIntan = s.PlayerThorns + s.PlayerFlameBarrier;
            foreach (var e in s.Enemies)
            {
                if (!e.IsAlive) continue;
                int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
                if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
                if (!(e.HasAttackIntent || e.HasDeathBlowIntent)) continue;
                int repeats = Math.Max(1, e.IntentRepeats);
                int maxHits = repeats;
                if (thornsAmtIntan > 0)
                {
                    int hpRemaining = Math.Max(0, e.Hp - preTurnDot);
                    int killAfterHits = (hpRemaining + thornsAmtIntan - 1) / thornsAmtIntan;
                    if (killAfterHits < maxHits) maxHits = killAfterHits;
                }
                hits += maxHits;
            }
            // v0.7.83 — Buffer cancels hits before block applies.
            int hitsAfterBuffer = Math.Max(0, hits - s.PlayerBuffer);
            int intangibleBlock = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
            // v0.7.35 — Player DoT bypasses Intangible (not "hit damage").
            return Math.Max(0, hitsAfterBuffer - intangibleBlock) + s.PlayerBurn + s.PlayerConstrict;
        }

        // v0.7.83 — Collect per-enemy attack damage instances so Buffer can
        // cancel the LARGEST hits first (canonical STS Buffer behavior: negates
        // a damage instance regardless of magnitude).
        // v0.7.96 — Player Thorns reflects damage per incoming hit. Each hit
        // received costs the enemy ThornsAmount HP; an enemy may die mid-attack
        // sequence, cutting remaining hits.
        // v0.8.0 — FlameBarrierPower folds in.
        var dmgInstances = new System.Collections.Generic.List<int>();
        bool playerVulnerable = s.PlayerVulnerable > 0;
        int playerThornsAmt = s.PlayerThorns + s.PlayerFlameBarrier;
        foreach (var e in s.Enemies)
        {
            if (!e.IsAlive) continue;
            int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
            if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;

            int perHit = e.IntentDamage + Math.Max(0, e.StrengthAmount);
            if (e.WeakAmount > 0) perHit = (int)(perHit * 0.75);
            int repeats = Math.Max(1, e.IntentRepeats);
            // v0.7.96 — Cap hits at enemy survival under player Thorns reflect.
            // Enemy HP after DoT pre-tick = e.Hp - preTurnDot (already filtered
            // above for full kill). Remaining HP / thornsAmt = max hits that
            // can land before enemy dies.
            int maxThornsHits = repeats;
            if (playerThornsAmt > 0)
            {
                int hpRemaining = Math.Max(0, e.Hp - preTurnDot);
                int killAfterHits = (hpRemaining + playerThornsAmt - 1) / playerThornsAmt;
                if (killAfterHits < maxThornsHits) maxThornsHits = killAfterHits;
            }
            for (int r = 0; r < maxThornsHits; r++)
            {
                // v0.9 — DebilitatePower (Tier B): when player is debuffed
                // with Debilitate, incoming Vuln amplifier goes from 1.5 to 2.0
                // (decompile sts2.decompiled.cs:ModifyVulnerableMultiplier).
                double vulnMult = s.PlayerDebilitate > 0
                    ? 2.0
                    : StatusMath.VulnerableMult;
                int dmg = playerVulnerable ? (int)(perHit * vulnMult) : perHit;
                if (dmg > 0) dmgInstances.Add(dmg);
            }
        }
        // v0.7.83 — Buffer cancels largest instances first. Greedy + sort.
        int bufferLeft = s.PlayerBuffer;
        if (bufferLeft > 0 && dmgInstances.Count > 0)
        {
            dmgInstances.Sort((a, b) => b.CompareTo(a));
            int absorb = Math.Min(bufferLeft, dmgInstances.Count);
            dmgInstances.RemoveRange(0, absorb);
        }
        int total = 0;
        foreach (var d in dmgInstances) total += d;
        // v0.5 — fold the end-of-turn block bonus (Metallicize + PlatedArmor) into the
        // effective block. Enemies attack AFTER our end-of-turn step adds these blocks,
        // so they cushion the leak before HP loss.
        int effectivePlayerBlock = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
        int rawLeak = Math.Max(0, total - effectivePlayerBlock);

        // v0.7.35 — Player-side DoT bypasses block. Burn ticks at end of OUR
        // turn (before enemies attack); Constrict ticks at start of enemy turn.
        // Both are inevitable HP loss this turn, visible from current state.
        rawLeak += s.PlayerBurn + s.PlayerConstrict;

        // v0.7.12 — ally split-fire absorption (Necrobinder skeletons).
        // Allies share the aggro with the player: per-leak point, the chance
        // of landing on an ally is approximately #allies / (1 + #allies),
        // capped at the allies' combined HP. Reduces the threat we surface
        // to the planner / survival-urgency math.
        int allyAbsorbed = ComputeAllyAbsorption(s, rawLeak);
        return rawLeak - allyAbsorbed;
    }

    /// <summary>
    /// v0.7.12 — Pre-block raw leak (post-block, pre-ally-absorption). Used by
    /// AdvanceTurn so the simulator knows how much damage is *available* to
    /// distribute between player and allies before they actually share it.
    /// </summary>
    public static int PredictRawLeak(SimState s)
    {
        // Mirror of PredictPlayerDmg without the ally-absorption tail. The
        // duplication is intentional — both call paths must stay in lockstep,
        // and inlining is cheaper than a flag parameter.
        if (s.PlayerIntangible > 0)
        {
            // v0.7.96 — Thorns also caps hits under Intangible (mirror PredictPlayerDmg).
            // v0.8.0 — FlameBarrier folded in.
            int hits = 0;
            int thornsAmtIntan = s.PlayerThorns + s.PlayerFlameBarrier;
            foreach (var e in s.Enemies)
            {
                if (!e.IsAlive) continue;
                int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
                if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
                if (!(e.HasAttackIntent || e.HasDeathBlowIntent)) continue;
                int repeats = Math.Max(1, e.IntentRepeats);
                int maxHits = repeats;
                if (thornsAmtIntan > 0)
                {
                    int hpRemaining = Math.Max(0, e.Hp - preTurnDot);
                    int killAfterHits = (hpRemaining + thornsAmtIntan - 1) / thornsAmtIntan;
                    if (killAfterHits < maxHits) maxHits = killAfterHits;
                }
                hits += maxHits;
            }
            int hitsAfterBuffer = Math.Max(0, hits - s.PlayerBuffer);
            int blkIntangible = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
            return Math.Max(0, hitsAfterBuffer - blkIntangible) + s.PlayerBurn + s.PlayerConstrict;
        }

        // v0.7.83 — Collect instances for Buffer to cancel the largest first.
        // v0.7.96 — Thorns reflect caps hits per attacker (mirror of PredictPlayerDmg).
        // v0.8.0 — FlameBarrier folded in.
        var dmgInstances = new System.Collections.Generic.List<int>();
        bool playerVuln = s.PlayerVulnerable > 0;
        int playerThornsAmt = s.PlayerThorns + s.PlayerFlameBarrier;
        foreach (var e in s.Enemies)
        {
            if (!e.IsAlive) continue;
            int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
            if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
            int perHit = e.IntentDamage + Math.Max(0, e.StrengthAmount);
            if (e.WeakAmount > 0) perHit = (int)(perHit * 0.75);
            int repeats = Math.Max(1, e.IntentRepeats);
            int maxThornsHits = repeats;
            if (playerThornsAmt > 0)
            {
                int hpRemaining = Math.Max(0, e.Hp - preTurnDot);
                int killAfterHits = (hpRemaining + playerThornsAmt - 1) / playerThornsAmt;
                if (killAfterHits < maxThornsHits) maxThornsHits = killAfterHits;
            }
            for (int r = 0; r < maxThornsHits; r++)
            {
                int dmg = playerVuln ? (int)(perHit * StatusMath.VulnerableMult) : perHit;
                if (dmg > 0) dmgInstances.Add(dmg);
            }
        }
        int bufferLeft = s.PlayerBuffer;
        if (bufferLeft > 0 && dmgInstances.Count > 0)
        {
            dmgInstances.Sort((a, b) => b.CompareTo(a));
            int absorb = Math.Min(bufferLeft, dmgInstances.Count);
            dmgInstances.RemoveRange(0, absorb);
        }
        int total = 0;
        foreach (var d in dmgInstances) total += d;
        int blk = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
        // v0.7.35 — Player-side DoT bypasses block (same as PredictPlayerDmg).
        return Math.Max(0, total - blk) + s.PlayerBurn + s.PlayerConstrict;
    }

    /// <summary>
    /// v0.7.12 — How much of <paramref name="rawLeak"/> alive allies will soak
    /// before it reaches the player. Heuristic: aggro is split evenly among
    /// player + alive allies, capped at combined ally HP. Returns 0 when no
    /// allies are alive or leak is zero.
    /// </summary>
    public static int ComputeAllyAbsorption(SimState s, int rawLeak)
    {
        if (rawLeak <= 0) return 0;
        int aliveAllies = 0;
        int totalAllyHp = 0;
        foreach (var a in s.Allies)
        {
            if (!a.IsAlive) continue;
            aliveAllies++;
            totalAllyHp += a.Hp;
        }
        if (aliveAllies == 0) return 0;

        // Absorption fraction = allies / (1 + allies). 1 ally → 50%, 2 → 67%, 3 → 75%.
        double absorption = aliveAllies / (1.0 + aliveAllies);
        int pool = (int)(rawLeak * absorption);
        return Math.Min(pool, totalAllyHp);
    }

    public static int CountIncomingAttackers(SimState s) =>
        s.Enemies.Count(e => e.IsAlive && e.HasAttackIntent && e.TotalIntentDamage > 0);

    /// <summary>
    /// Threat ratio in [0, ∞): predicted-damage / current-hp. > threshold = "consider blocking".
    /// </summary>
    public static double ThreatRatio(SimState s)
    {
        if (s.PlayerHp <= 0) return 0;
        return (double)PredictPlayerDmg(s) / s.PlayerHp;
    }

    // Composition helpers — intent-aware shortcuts.
    public static bool AnyBuffing(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.HasBuffIntent);

    public static bool AnyHealing(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.HasHealIntent);

    public static bool AnySummoning(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.HasSummonIntent);

    public static bool AnyDeathBlow(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.HasDeathBlowIntent);

    public static bool AllInert(SimState s)
    {
        var alive = s.Enemies.Where(e => e.IsAlive).ToList();
        return alive.Count > 0 && alive.All(e => e.IsInert);
    }

    /// <summary>
    /// True when next-turn threat is amplified (buff present) — planner should be more
    /// defensive than the raw attack damage alone suggests.
    /// </summary>
    public static bool NextTurnThreatAmplified(SimState s) =>
        AnyBuffing(s);

    public static bool AnyMinionAlive(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.IsMinion);

    public static bool AnyBossAlive(SimState s) =>
        s.Enemies.Any(e => e.IsAlive && e.IsBoss);

    /// <summary>
    /// Total HP across all alive enemies (HP + Block). Used to estimate fight length:
    /// large value → long fight → Power cards' scaling value matters; small value →
    /// short fight → just kill the remaining enemies.
    /// </summary>
    public static int TotalAliveEnemyHp(SimState s) =>
        s.Enemies.Where(e => e.IsAlive).Sum(e => e.Hp + e.Block);

    /// <summary>
    /// Survival urgency = how badly the player needs to defend this turn. Driven by
    /// predicted leak (PredictPlayerDmg already subtracts current block) over current
    /// HP. Used by planner to suppress non-defensive plays when survival is at stake.
    ///
    ///   Fatal     leak ≥ HP            → die this turn without intervention
    ///   Heavy     leak ≥ HP × 0.5      → lose half HP, set up future Fatal
    ///   Moderate  leak ≥ HP × 0.2      → notable but recoverable
    ///   None      everything else
    /// </summary>
    public static SurvivalUrgency GetSurvivalUrgency(SimState s)
    {
        if (s.PlayerHp <= 0) return SurvivalUrgency.None;
        if (AllInert(s)) return SurvivalUrgency.None;
        int leak = PredictPlayerDmg(s);
        if (leak <= 0) return SurvivalUrgency.None;
        if (leak >= s.PlayerHp) return SurvivalUrgency.Fatal;
        double ratio = (double)leak / s.PlayerHp;
        if (ratio >= 0.5) return SurvivalUrgency.Heavy;
        if (ratio >= 0.2) return SurvivalUrgency.Moderate;
        return SurvivalUrgency.None;
    }
}

/// <summary>
/// Threat severity expressed as an ordered enum so callers branch on tiers rather
/// than re-implementing the threshold math. Higher = more urgent.
/// </summary>
internal enum SurvivalUrgency
{
    None     = 0,
    Moderate = 1,
    Heavy    = 2,
    Fatal    = 3,
}
