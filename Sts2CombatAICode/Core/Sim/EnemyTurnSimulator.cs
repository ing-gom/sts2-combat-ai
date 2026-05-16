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
        int total = 0;
        foreach (var e in s.Enemies)
        {
            if (!e.IsAlive) continue;
            // Per-hit base = IntentDamage + Strength (Strength rides on every hit).
            // Weak rounds DOWN per hit in STS, so multi-hit attacks lose proportionally
            // more — apply ×0.75 before multiplying by IntentRepeats, not after.
            int perHit = e.IntentDamage + Math.Max(0, e.StrengthAmount);
            if (e.WeakAmount > 0) perHit = (int)(perHit * 0.75);
            int dmg = perHit * Math.Max(1, e.IntentRepeats);
            total += dmg;
        }
        return Math.Max(0, total - s.PlayerBlock);
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
