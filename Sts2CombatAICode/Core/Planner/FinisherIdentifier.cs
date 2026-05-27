using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.61 — Identifies "finisher" cards based on CURRENT deck state (not
/// archetype). A finisher is a card whose effective damage in the current
/// state is high enough to close out the fight (or significantly advance
/// it) when played.
///
/// Different from DeckThroughput.CoreAttackers — that ranks by per-energy
/// efficiency. A finisher is ranked by ABSOLUTE damage potential given:
///   • Current Strength
///   • Current Vulnerable on best target
///   • X-cost cards with current energy
///   • Self-growing cards that have already grown (CLAW, MAUL, KINGLY_PUNCH)
///
/// Used to:
///   • Bias scoring of the finisher card when conditions to use it are right
///   • Penalize wasting the finisher prematurely
///   • Surface "expected finisher: KINGLY_PUNCH (28 dmg)" in logs
///
/// Pure state observation — no future-sim.
/// </summary>
internal static class FinisherIdentifier
{
    /// <summary>Minimum effective damage to qualify as a finisher.</summary>
    private const int MinFinisherDamage = 10;

    /// <summary>How many top finishers to track.</summary>
    private const int TopK = 3;

    public readonly struct Finisher
    {
        public readonly string CardId;
        public readonly int EstimatedDamage;
        public readonly int Cost;
        public readonly bool InHand;
        public readonly int PileLocation;  // 0=hand, 1=draw, 2=discard
        public Finisher(string id, int dmg, int cost, bool inHand, int loc)
        {
            CardId = id; EstimatedDamage = dmg; Cost = cost;
            InHand = inHand; PileLocation = loc;
        }
    }

    public readonly struct Result
    {
        public readonly List<Finisher> TopFinishers;
        public readonly HashSet<string> FinisherIds;
        // 2026-05-27 C-path — total burst budget across top-K finishers
        // (state-resolved damage, post-Vuln/Str/Weak). Used by
        // CombatContext.ContextBonus to gate the Boss-attrition lean
        // independently of DPT: a deck can have moderate sustained DPT
        // yet still lack the burst budget to close a Boss in a window.
        public readonly int TotalBudget;
        public Result(List<Finisher> top, HashSet<string> ids, int totalBudget)
        {
            TopFinishers = top;
            FinisherIds = ids;
            TotalBudget = totalBudget;
        }
    }

    public static Result Identify(SimState state)
    {
        var candidates = new List<Finisher>();

        Scan(state.Hand, state, candidates, location: 0);
        Scan(state.DrawPile, state, candidates, location: 1);
        Scan(state.DiscardPile, state, candidates, location: 2);

        var top = candidates
            .Where(f => f.EstimatedDamage >= MinFinisherDamage)
            .OrderByDescending(f => f.EstimatedDamage)
            .Take(TopK)
            .ToList();

        var ids = new HashSet<string>(top.Select(f => f.CardId));
        int totalBudget = top.Sum(f => f.EstimatedDamage);
        return new Result(top, ids, totalBudget);
    }

    private static void Scan(IReadOnlyList<SimCard> pile, SimState state,
                              List<Finisher> candidates, int location)
    {
        foreach (var c in pile)
        {
            if (c.IsCurseOrStatus) continue;
            if (!c.IsAttack) continue;
            if (c.Id == null) continue;

            int dmg = EstimateEffectiveDamage(c, state);
            if (dmg <= 0) continue;

            candidates.Add(new Finisher(c.Id, dmg, c.Cost,
                inHand: location == 0, location));
        }
    }

    /// <summary>
    /// Effective damage of this card if played now. Factors:
    ///   • Card.TotalDamage (CardReflection already resolves CalculatedDamage)
    ///   • PlayerStrength bonus per hit
    ///   • Best alive enemy's VulnerableAmount × 1.5 multiplier
    ///   • X-cost cards: per-hit × current energy (if enough energy)
    ///   • PlayerWeak reduces per-hit by 25%
    /// </summary>
    private static int EstimateEffectiveDamage(SimCard card, SimState state)
    {
        int baseDmg = card.Damage;
        if (baseDmg <= 0) return 0;

        // Strength bonus per hit.
        int perHit = baseDmg + System.Math.Max(0, state.PlayerStrength);
        // Player Weak: outgoing damage × 0.75.
        if (state.PlayerWeak > 0) perHit = (int)(perHit * 0.75);
        // Best target Vulnerable amp.
        bool anyVuln = false;
        foreach (var e in state.Enemies)
            if (e.IsAlive && e.VulnerableAmount > 0) { anyVuln = true; break; }
        if (anyVuln) perHit = (int)(perHit * StatusMath.VulnerableMult);

        int hits = System.Math.Max(1, card.Hits);
        // X-cost attacks: hits scale with player energy.
        if (card.Axes.Contains("X_COST"))
            hits = System.Math.Max(1, state.PlayerEnergy);

        return perHit * hits;
    }

    /// <summary>
    /// Per-card bonus for playing a finisher. Context-aware: huge bonus when
    /// the play actually closes the fight, modest bonus in cleanup, penalty
    /// when prematurely played in setup/opening phase.
    /// </summary>
    public static int FinisherBonus(SimCard card, Result finishers,
                                     CombatPlan.Stage stage,
                                     SurvivalProjection.Projection race)
    {
        if (card.Id == null) return 0;
        if (!finishers.FinisherIds.Contains(card.Id)) return 0;

        // Lethal-this-turn already gives massive bonus via PlanScorer's
        // RealLethalKillBonus. Add a moderate nudge for finishers in burst/
        // cleanup phases.
        switch (stage)
        {
            case CombatPlan.Stage.Cleanup:
                return 120;
            case CombatPlan.Stage.Burst:
                return 80;
            case CombatPlan.Stage.Lockdown:
                return 30;
            case CombatPlan.Stage.Setup:
                // Premature finisher use — losing potential when state will
                // improve (Vuln/Str applied later). Only mild penalty since
                // the card still deals damage now.
                return race.Race == SurvivalProjection.RaceOutcome.Losing
                    ? 50    // we're losing — play it anyway
                    : -40;
            case CombatPlan.Stage.Opening:
                return -30; // hold for later
            default:
                return 0;
        }
    }
}
