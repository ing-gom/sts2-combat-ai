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
            int hits = 0;
            foreach (var e in s.Enemies)
            {
                if (!e.IsAlive) continue;
                // DoT pre-kill: Poison + Constrict tick before any intent fires.
                int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
                if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
                if (e.HasAttackIntent || e.HasDeathBlowIntent)
                    hits += Math.Max(1, e.IntentRepeats);
            }
            int intangibleBlock = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
            // v0.7.35 — Player DoT bypasses Intangible (not "hit damage").
            return Math.Max(0, hits - intangibleBlock) + s.PlayerBurn + s.PlayerConstrict;
        }

        int total = 0;
        // v0.5 — incoming damage is amplified ×1.5 if the player is Vulnerable.
        // PredictPlayerDmg used to skip this multiplier entirely, so the threat
        // estimate undercounted damage on turns where we'd been Vulnerabled by an
        // enemy debuff intent (Cultist's Dark Strike with Vuln rider, etc.).
        // Pre-compute once outside the loop so per-enemy cost stays cheap.
        bool playerVulnerable = s.PlayerVulnerable > 0;
        foreach (var e in s.Enemies)
        {
            if (!e.IsAlive) continue;
            // v0.5 — DoT pre-kill: Poison + Constrict tick at start of enemy turn
            // before any intent fires, so enemies whose DoT covers their HP die
            // before attacking and contribute 0 threat. Burn left out because its
            // tick timing varies between STS variants.
            int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
            if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;

            // Per-hit base = IntentDamage + Strength (Strength rides on every hit).
            // Weak rounds DOWN per hit in STS — multi-hit attacks lose proportionally
            // more — so apply ×0.75 BEFORE multiplying by IntentRepeats.
            int perHit = e.IntentDamage + Math.Max(0, e.StrengthAmount);
            if (e.WeakAmount > 0) perHit = (int)(perHit * 0.75);
            int dmg = perHit * Math.Max(1, e.IntentRepeats);

            // v0.5 — Vulnerable on the player ×1.5 incoming. Applied after per-enemy
            // Weak so the multiplier chain matches in-game order.
            if (playerVulnerable) dmg = (int)(dmg * StatusMath.VulnerableMult);
            total += dmg;
        }
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
            int hits = 0;
            foreach (var e in s.Enemies)
            {
                if (!e.IsAlive) continue;
                int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
                if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
                if (e.HasAttackIntent || e.HasDeathBlowIntent)
                    hits += Math.Max(1, e.IntentRepeats);
            }
            int blkIntangible = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
            return Math.Max(0, hits - blkIntangible) + s.PlayerBurn + s.PlayerConstrict;
        }

        int total = 0;
        bool playerVuln = s.PlayerVulnerable > 0;
        foreach (var e in s.Enemies)
        {
            if (!e.IsAlive) continue;
            int preTurnDot = e.PoisonAmount + e.ConstrictAmount;
            if (preTurnDot > 0 && preTurnDot >= e.Hp) continue;
            int perHit = e.IntentDamage + Math.Max(0, e.StrengthAmount);
            if (e.WeakAmount > 0) perHit = (int)(perHit * 0.75);
            int dmg = perHit * Math.Max(1, e.IntentRepeats);
            if (playerVuln) dmg = (int)(dmg * StatusMath.VulnerableMult);
            total += dmg;
        }
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
