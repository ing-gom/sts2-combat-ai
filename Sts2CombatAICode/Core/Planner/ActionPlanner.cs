using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Picks the next single card to play. v0.1 is a single-step scorer (no DFS lookahead)
/// since we re-snapshot after each play. Enumerates all (card, target) candidates given
/// energy + hand constraints, scores each via PlanScorer, returns the best.
///
/// v0.2 will add DFS lookahead over remaining-turn sequences for tiebreaking.
/// </summary>
internal static class ActionPlanner
{
    public readonly record struct PlanStep(SimCard Card, int TargetIdx, int Score, string Reason);

    /// <summary>
    /// Per-candidate trace from the most recent PlanNextStep call. <c>bestNextId</c>
    /// reveals which follow-up card the depth-2 lookahead picked as the "best second
    /// play" after this candidate — invaluable for explaining why a setup card won
    /// over a stronger-looking standalone play.
    /// </summary>
    public static System.Collections.Generic.List<(string id, int targetIdx, int firstScore, int secondScore, int total, string? bestNextId)>
        LastCandidates { get; } = new();

    public static PlanStep? PlanNextStep(SimState state)
    {
        var candidates = EnumerateCandidates(state).ToList();
        if (candidates.Count == 0) return null;

        // v0.2.5 — depth-2 lookahead: for each first-card candidate, simulate playing it
        // and score the best possible second card in the resulting state. Combined score
        // surfaces combos (Inflame → Strike, Vulnerable → big attack) the greedy step missed.
        PlanStep? bestPlan = null;
        int bestTotal = int.MinValue;
        int bestFirstScore = int.MinValue;
        LastCandidates.Clear();

        var planWeights = PlanScorerWeights.For(PlaystyleState.Current);
        foreach (var (card, targetIdx) in candidates)
        {
            // v0.5 — play-order biases (Retain defer / Ethereal play-now) live here, not
            // in PlanScorer.Score, so that discard/exhaust selectors see unbiased values.
            int firstScore = PlanScorer.Score(card, targetIdx, state)
                           + PlanScorer.PlayOrderBias(card, state, planWeights);

            // Simulate playing this card; find best card to follow.
            int secondScore = 0;
            string? bestNextId = null;
            try
            {
                var nextState = Sim.AnalyticalSimulator.ApplyCardPlay(state, card, targetIdx);
                foreach (var nextCand in EnumerateCandidates(nextState))
                {
                    int s = PlanScorer.Score(nextCand.card, nextCand.targetIdx, nextState)
                          + PlanScorer.PlayOrderBias(nextCand.card, nextState, planWeights);
                    if (s > secondScore || bestNextId == null)
                    {
                        secondScore = s;
                        bestNextId = nextCand.card.Id;
                    }
                }
                if (secondScore < 0) secondScore = 0; // never pessimize via bad fallback
            }
            catch
            {
                // Simulator error: fall back to single-step score
                secondScore = 0;
                bestNextId = null;
            }

            int total = firstScore + secondScore;
            LastCandidates.Add((card.Id, targetIdx, firstScore, secondScore, total, bestNextId));
            if (total > bestTotal)
            {
                bestTotal = total;
                bestFirstScore = firstScore;
                // PlanStep.Score is the first-card score, not lookahead total (kept for log clarity).
                bestPlan = new PlanStep(card, targetIdx, firstScore, Reason(card));
            }
        }

        // "Stop playing" floor: judge on the *first-card score* (the actual card we'd play),
        // not the lookahead total — a high-score follow-up shouldn't keep us spending energy
        // on a worthless first move.
        if (bestPlan != null && bestFirstScore < planWeights.MinPlayScore)
        {
            // v0.5 — 0-cost exemption. The floor exists to stop us spending ENERGY on a
            // weak play. A 0-cost card spends no energy, so any positive-score play is
            // strictly net positive — leaving it in hand wastes it at end-of-turn.
            //   • If best plan is itself a 0-cost positive play, take it.
            //   • If best plan is a paid card below floor, search the candidates for the
            //     best 0-cost positive alternative and play that instead.
            if (bestPlan.Value.Card.Cost == 0 && bestFirstScore > 0)
                return bestPlan;
            SimCard? freeCard = null;
            int freeIdx = -1;
            int freeScore = 0;
            foreach (var (c, t) in candidates)
            {
                if (c.Cost != 0) continue;
                int s = PlanScorer.Score(c, t, state)
                      + PlanScorer.PlayOrderBias(c, state, planWeights);
                if (s > freeScore) { freeScore = s; freeCard = c; freeIdx = t; }
            }
            if (freeCard != null)
                return new PlanStep(freeCard, freeIdx, freeScore, Reason(freeCard));
            return null;
        }

        return bestPlan;
    }

    public static IEnumerable<PlanStep> ScoreAll(SimState state)
    {
        foreach (var (card, targetIdx) in EnumerateCandidates(state))
        {
            yield return new PlanStep(card, targetIdx, PlanScorer.Score(card, targetIdx, state), Reason(card));
        }
    }

    private static IEnumerable<(SimCard card, int targetIdx)> EnumerateCandidates(SimState state)
    {
        foreach (var card in state.Hand)
        {
            if (!card.IsPlayable) continue;        // Unplayable (curse/status/conditional)
            if (card.Cost < 0) continue;           // Negative cost = X or unplayable signal
            if (card.Cost > state.PlayerEnergy) continue;
            // Note: star-cost cards are filtered by CanPlay() already if no stars; we trust it.

            // Energy-gain card is pointless if there's nothing left to spend the gained energy on.
            // Excluding here (vs penalising in PlanScorer) guarantees we never play it as a
            // "least bad" fallback when the hand has no other useful card.
            if (card.IsEnergyGainCard && !card.IsAttack && card.Damage == 0)
            {
                bool anyOtherUseful = false;
                foreach (var c in state.Hand)
                {
                    if (ReferenceEquals(c, card)) continue;
                    if (!c.IsPlayable) continue;
                    if (c.IsCurseOrStatus) continue;
                    if (c.Cost < 0) continue;
                    anyOtherUseful = true;
                    break;
                }
                if (!anyOtherUseful) continue;
            }

            // Orb-evoke / amplifier prerequisite: cards like DualCast / MultiCast / QuadCast
            // need at least one channeled orb to have any meaning. If orb slots are empty,
            // skip them — and any ORB_PRODUCER in hand becomes the natural top pick instead.
            //
            // Restricted to cards with no standalone damage (Thunder/Shatter etc. still
            // deal damage with 0 orbs, so they remain candidates).
            bool isOrbEvokeOnly =
                (card.Axes.Contains("ORB_AMPLIFIER") || card.Axes.Contains("ORB_EVOKE")
                 || card.Axes.Contains("LIGHTNING_EVOKE"))
                && !card.IsAttack && card.Damage == 0;
            if (isOrbEvokeOnly && state.PlayerOrbCount == 0)
                continue;

            switch (card.Target)
            {
                case TargetType.AnyEnemy:
                    for (int i = 0; i < state.Enemies.Count; i++)
                    {
                        if (state.Enemies[i].IsAlive) yield return (card, i);
                    }
                    break;
                default:
                    // Self / AllEnemies / RandomEnemy / AnyAlly / AnyPlayer / TargetedNoCreature / Osty / None
                    yield return (card, -1);
                    break;
            }
        }
    }

    private static string Reason(SimCard c)
    {
        if (c.IsPower) return "power-first";
        if (c.IsAttack) return "attack";
        if (c.IsSkill) return c.Target == TargetType.Self ? "skill-self" : "skill";
        return c.Kind.ToString();
    }
}
