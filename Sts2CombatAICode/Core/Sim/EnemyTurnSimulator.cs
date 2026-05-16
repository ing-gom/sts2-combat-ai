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
                if (e.PoisonAmount > 0 && e.PoisonAmount >= e.Hp) continue;
                if (e.HasAttackIntent || e.HasDeathBlowIntent)
                    hits += Math.Max(1, e.IntentRepeats);
            }
            int intangibleBlock = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
            return Math.Max(0, hits - intangibleBlock);
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
            // v0.5 — DoT pre-kill. Poison ticks at start of enemy turn BEFORE the
            // enemy acts, so an enemy with PoisonAmount ≥ Hp dies before attacking
            // and contributes 0 threat. Without this, a turn that lethal-poisoned
            // the enemy still scored block cards as "tank the incoming hit", and
            // the lookahead never saw the threat drop after our poison play.
            if (e.PoisonAmount > 0 && e.PoisonAmount >= e.Hp) continue;
            // Strength stacks ride on every hit — the raw IntentDamage we get from
            // AttackIntent.DamageCalc isn't strength-adjusted in all cases, so add it
            // explicitly. Multi-hit attacks get strength per hit.
            int dmg = e.TotalIntentDamage + Math.Max(0, e.StrengthAmount) * Math.Max(1, e.IntentRepeats);
            // WeakPower on the enemy → their attacks deal ×0.75. Round down (canonical STS).
            if (e.WeakAmount > 0) dmg = (int)(dmg * 0.75);
            // Vulnerable on the player → ×1.5 incoming. Apply per-enemy AFTER their
            // own Weak so the multiplier chain matches in-game order.
            if (playerVulnerable) dmg = (int)(dmg * StatusMath.VulnerableMult);
            total += dmg;
        }
        // v0.5 — fold the end-of-turn block bonus (Metallicize + PlatedArmor) into the
        // effective block. Enemies attack AFTER our end-of-turn step adds these blocks,
        // so they cushion the leak before HP loss.
        int effectivePlayerBlock = s.PlayerBlock + s.PlayerEndOfTurnBlockBonus;
        return Math.Max(0, total - effectivePlayerBlock);
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
}
