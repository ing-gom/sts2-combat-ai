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
    /// v0.7.10 — discount applied to the next-turn opening score when projecting
    /// multi-turn value. 0.30 = "30% of next turn's best first-card play counts
    /// toward this turn's first-card score". Conservative because the projection
    /// uses AdvanceTurn from after just the first card (no full-turn sim), and
    /// next-turn draw is a synthetic average (RNG noise).
    /// </summary>
    private const double NextTurnDiscount = 0.30;

    /// <summary>
    /// v0.7.14 — Monte Carlo sample count for next-turn hand projection.
    /// N=3 is a perf/variance trade-off: enough samples to spread the deck
    /// distribution without exploding PlanScorer call count. With ~50 calls
    /// per depth=1 lookahead, 3 samples cost +150 calls per first-card
    /// candidate (~3x legacy when including the depth=2 main beam).
    /// </summary>
    private const int MonteCarloSamples = 3;

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

            // v0.7.9 — depth=3 beam search (K=3, single-turn). Simulate playing
            // this card, then evaluate the best 2-card continuation via top-K
            // beam pruning. depth=3 lets the planner see 2-step combos post-
            // this-card (Inflame → Strike → Bash, Bodyslam-after-block etc.).
            //
            // v0.7.10 — Phase 2a multi-turn signal: after the 1-card simulation,
            // run AdvanceTurn to project the next-turn state (enemy intents
            // resolve, hand redraws, energy refills) and add the best single
            // first-card play from there as a discounted bonus. Lets the
            // planner value Power cards / setups whose payoff is next turn,
            // not just this one.
            int secondScore = 0;
            int nextTurnBonus = 0;
            string? bestNextId = null;
            try
            {
                var nextState = Sim.AnalyticalSimulator.ApplyCardPlay(state, card, targetIdx);
                secondScore = BestContinuation(nextState, depth: 2, planWeights, beamK: 3, out bestNextId);
                if (secondScore < 0) secondScore = 0; // never pessimize via bad fallback

                // Next-turn projection — discounted because the projection
                // assumes the rest of this turn is forfeit (we only advance
                // from after the first card, not after the full 3-card seq).
                //
                // v0.7.14 — Monte Carlo over N=3 sampled next-turn hands.
                // Replaces the single synthetic-avg AdvanceTurn call with an
                // average across 3 actually-drawn hands so variance in the
                // deck pool ("might draw Strikes / might draw curses") shows
                // up in the next-turn signal instead of being smoothed away.
                try
                {
                    // Seed deterministically per state so the same scoring
                    // run reproduces. The seed mixes hand size + player HP +
                    // a candidate-specific salt to avoid all first-card
                    // candidates sharing identical samples.
                    int seed = nextState.Hand.Count * 31 + nextState.PlayerHp + card.Id.GetHashCode();
                    var rng = new System.Random(seed);
                    int sampleTotal = 0;
                    int sampleCount = 0;
                    for (int s = 0; s < MonteCarloSamples; s++)
                    {
                        try
                        {
                            var nextTurnState = Sim.AnalyticalSimulator.AdvanceTurnSampled(nextState, rng);
                            int singleStep = BestContinuation(nextTurnState, depth: 1, planWeights, beamK: 3, out _);
                            if (singleStep < 0) singleStep = 0;
                            sampleTotal += singleStep;
                            sampleCount++;
                        }
                        catch { /* skip sample on sim failure */ }
                    }
                    int nextTurnAvg = sampleCount > 0 ? sampleTotal / sampleCount : 0;

                    // v0.7.15 — EchoFormPower next-turn first-card 2x play.
                    // We only evaluate depth=1 next turn (single first card),
                    // so any positive EchoForm stack saturates: the projected
                    // first card plays twice → score doubles. Higher stacks
                    // (2nd/3rd cards echoed too) would matter at depth>=2
                    // but we don't drill that deep in the next-turn projection.
                    double echoMultiplier = 1.0;
                    if (nextState.PlayerPowers != null
                        && nextState.PlayerPowers.TryGetValue("EchoFormPower", out var ef)
                        && ef > 0)
                    {
                        echoMultiplier = 2.0;
                    }
                    nextTurnBonus = (int)(nextTurnAvg * echoMultiplier * NextTurnDiscount);
                    if (nextTurnBonus < 0) nextTurnBonus = 0;
                }
                catch { /* AdvanceTurn failure → no next-turn signal */ }
            }
            catch
            {
                // Simulator error: fall back to single-step score
                secondScore = 0;
                bestNextId = null;
            }

            int total = firstScore + secondScore + nextTurnBonus;
            LastCandidates.Add((card.Id, targetIdx, firstScore, secondScore, total, bestNextId));
            // v0.5 — tie-break on first-card score so identical totals (e.g., A first=1000
            // second=200 vs B first=600 second=600) prefer the candidate with higher
            // immediate value. Resolves the case where iteration order alone determined
            // the winner of equal-total candidates.
            bool wins = total > bestTotal
                     || (total == bestTotal && firstScore > bestFirstScore);
            if (wins)
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

    /// <summary>
    /// v0.7.9 — depth-N beam search returning the best continuation score
    /// from <paramref name="state"/>. At each layer scores all candidates, keeps
    /// the top <paramref name="beamK"/> by single-step score, simulates each via
    /// AnalyticalSimulator.ApplyCardPlay, and recurses depth-1. Pure single-turn
    /// search — no AdvanceTurn / EOT processing (Phase 1 scope).
    ///
    /// <paramref name="depth"/> = remaining cards to consider after this one.
    /// depth=0 returns 0; depth=1 = single-step max; depth=2 = best 2-card
    /// continuation; etc.
    ///
    /// <paramref name="firstCardId"/> exits the id of the top-scoring candidate
    /// at this layer (for DecisionLog trace).
    /// </summary>
    private static int BestContinuation(SimState state, int depth, PlanScorerWeights w, int beamK, out string? firstCardId)
    {
        firstCardId = null;
        if (depth <= 0) return 0;

        var candidates = EnumerateCandidates(state).ToList();
        if (candidates.Count == 0) return 0;

        // Score all candidates at this layer once.
        var scored = new List<(int Score, SimCard Card, int TargetIdx)>(candidates.Count);
        foreach (var (card, targetIdx) in candidates)
        {
            int s = PlanScorer.Score(card, targetIdx, state)
                  + PlanScorer.PlayOrderBias(card, state, w);
            scored.Add((s, card, targetIdx));
        }
        // Top-K beam: keep only the best `beamK` by single-step score.
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        int keep = System.Math.Min(beamK, scored.Count);

        int best = int.MinValue;
        for (int i = 0; i < keep; i++)
        {
            var (s, card, targetIdx) = scored[i];
            int total = s;
            if (depth > 1)
            {
                try
                {
                    var nextState = Sim.AnalyticalSimulator.ApplyCardPlay(state, card, targetIdx);
                    total += BestContinuation(nextState, depth - 1, w, beamK, out _);
                }
                catch { /* sim failure: use single-step score only */ }
            }
            if (total > best || firstCardId == null)
            {
                best = total;
                firstCardId = card.Id;
            }
        }
        return best < 0 ? 0 : best;
    }

    private static IEnumerable<(SimCard card, int targetIdx)> EnumerateCandidates(SimState state)
    {
        foreach (var card in state.Hand)
        {
            if (!card.IsPlayable) continue;        // Unplayable (curse/status/conditional)
            if (card.Cost < 0) continue;           // Negative cost = X or unplayable signal
            // v0.5 — Free*Power lets us play expensive cards over the energy budget.
            // v0.7.21 — CorruptionPower makes Skill cards combat-wide 0-cost.
            bool corruptionFreeSkill = card.IsSkill
                && state.PlayerPowers != null
                && state.PlayerPowers.TryGetValue("CorruptionPower", out var corStack)
                && corStack > 0;
            bool freeCovers =
                (card.IsAttack && state.PlayerFreeAttacks > 0) ||
                (card.IsSkill && state.PlayerFreeSkills > 0) ||
                (card.IsPower && state.PlayerFreePowers > 0) ||
                corruptionFreeSkill;
            if (!freeCovers && card.Cost > state.PlayerEnergy) continue;
            // Note: star-cost cards are filtered by CanPlay() already if no stars; we trust it.

            // Energy-gain card is pointless if there's nothing left to spend the gained energy on.
            // Excluding here (vs penalising in PlanScorer) guarantees we never play it as a
            // "least bad" fallback when the hand has no other useful card.
            // v0.5 — only THIS-turn energy gain (Effect.EnergyGain > 0, Adrenaline-style)
            // qualifies. NEXT-turn variants (EnergyNextTurnPower like Berserk) gain energy
            // on subsequent turns and stay valuable even when this hand is empty.
            // Also exempt cards that DRAW (Skim-style) — drawing produces new candidates,
            // so an isolated Skim is still worth playing for the draw.
            if (card.IsEnergyGainCard && card.EnergyGain > 0
                && !card.IsAttack && card.Damage == 0 && card.DrawCount == 0)
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
