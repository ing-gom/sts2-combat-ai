using System;
using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// 2026-05-28 MCTS-P1 — Monte Carlo Tree Search planner on top of
/// AnalyticalSimulator. Branches through SimState (mod-side
/// lightweight clone) instead of sts2.dll Episode replay, so tree
/// expansion stays cheap (~ms per node) at the cost of simulator
/// accuracy. The parity probe pins mod sim at 41.8% step-level
/// agreement vs sts2.dll ground truth (commit 01a79b5 baseline),
/// so leaf values carry bias that compounds with depth — initial
/// rollout depth kept short (3-5) until simulator parity climbs.
///
/// Two configurable knobs the integration layer can tune:
///   N simulations (default 50) — wall-time / quality trade-off
///   c_puct exploration constant (default sqrt(2) classic UCB1)
///
/// Returns null when the root has no legal play (caller treats as
/// EndTurn, same convention as ActionPlanner.PlanNextStep).
internal static class MCTSPlanner
{
    public const int DefaultSimulations = 50;
    public const double DefaultC = 1.4142135;  // sqrt(2), classic UCB1
    public const int DefaultRolloutDepth = 0;  // 0 = no rollout, leaf heuristic only

    /// <summary>
    /// 2026-06-19 (path A → cross-turn pivot) — turn horizon for cross-turn search.
    /// EndTurn no longer dead-ends the tree: when a node is below the horizon, EndTurn
    /// transitions via AnalyticalSimulator.AdvanceTurnSampled (enemy intent resolves +
    /// stochastic redraw), so the tree reasons ACROSS turns. This is the ONLY regime where
    /// MCTS can structurally beat the beam: within a single turn the card sequence is
    /// deterministic single-agent (the beam's max-search is already near-optimal), but the
    /// draw + enemy intent across turns are stochastic/adversarial — exactly what MCTS's
    /// sample-and-average is for, and exactly the term the beam only crudely approximates
    /// (its N=3 next-turn Monte-Carlo projection). Horizon 0 = legacy within-turn search.
    /// CAVEAT: multi-turn rollouts compound AnalyticalSimulator's ~42% step parity — the
    /// deeper the horizon, the more leaf bias accumulates. Keep it short (2-3).
    /// </summary>
    public const int DefaultHorizonTurns = 3;

    /// PlanNextStep — top-level entry point. Returns the (card,
    /// targetIdx) tuple at the root child with the highest visit
    /// count after N simulations, or null when no valid play exists
    /// (EndTurn-best → null → caller ends the turn).
    public static (SimCard card, int targetIdx)? PlanNextStep(
        SimState rootState,
        int simulations = DefaultSimulations,
        double cPuct = DefaultC,
        int rolloutDepth = DefaultRolloutDepth,
        int horizonTurns = DefaultHorizonTurns)
    {
        if (rootState == null) return null;
        var rootActions = EnumerateActions(rootState);
        if (rootActions.Count == 0) return null;

        // Deterministic RNG for the stochastic cross-turn (AdvanceTurnSampled) transitions,
        // seeded from the root board so the same decision point reproduces across full-run
        // reruns (the determinism harness depends on this). Mixed from hand size + player HP
        // + summed enemy HP — cheap and stable.
        int seed = rootState.Hand.Count * 31 + rootState.PlayerHp;
        foreach (var e in rootState.Enemies) if (e.IsAlive) seed = seed * 31 + Math.Max(0, e.Hp);
        var rng = new Random(seed);

        var root = new Node
        {
            State = rootState,
            TurnDepth = 0,
            UnexpandedActions = rootActions,
            IsTerminal = IsTerminal(rootState),
        };

        // Tree-wide value range for Q normalization (board-value leaf + large ± terminals)
        // so UCB1's exploration term stays on a comparable [0,1] scale.
        double valMin = double.MaxValue, valMax = double.MinValue;

        for (int i = 0; i < simulations; i++)
        {
            // 1. Selection — descend via UCB1 until a non-fully-expanded or terminal node.
            var node = root;
            while (!node.IsTerminal && node.IsFullyExpanded && node.Children.Count > 0)
            {
                node = SelectChild(node, cPuct, valMin, valMax);
            }

            // 2. Expansion — pop one unexpanded action, apply, attach.
            if (!node.IsTerminal && node.UnexpandedActions.Count > 0)
            {
                var action = node.UnexpandedActions[0];
                node.UnexpandedActions.RemoveAt(0);

                // Cross-turn: EndTurn below the horizon advances the enemy turn + redraws
                // (stochastic). At/above the horizon, EndTurn is a leaf evaluated on the
                // turn-ending board as-is (no further lookahead).
                bool advanced = action.IsEndTurn && horizonTurns > 0 && node.TurnDepth < horizonTurns;
                SimState nextState;
                try
                {
                    nextState = advanced
                        ? AnalyticalSimulator.AdvanceTurnSampled(node.State, rng)
                        : action.IsEndTurn
                            ? node.State
                            : AnalyticalSimulator.ApplyCardPlay(node.State, action.Card!, action.TargetIdx);
                }
                catch
                {
                    nextState = node.State;
                    advanced = false;
                }

                // A horizon-capped EndTurn (not advanced) is a terminal leaf; a death during
                // AdvanceTurn / a wipe also terminates. An advanced, still-live turn opens a
                // fresh player turn with new card actions.
                bool terminalLeaf = (action.IsEndTurn && !advanced) || IsTerminal(nextState);
                var child = new Node
                {
                    State = nextState,
                    IncomingAction = action,
                    Parent = node,
                    TurnDepth = node.TurnDepth + (action.IsEndTurn ? 1 : 0),
                    UnexpandedActions = terminalLeaf ? new List<MctsAction>() : EnumerateActions(nextState),
                    IsTerminal = terminalLeaf,
                };
                node.Children.Add(child);
                node = child;
            }

            // 3. Simulation — static board-value leaf (cross-turn advancement supplies the
            //    lookahead; the leaf must NOT reward "plays still available" or EndTurn would
            //    dominate — the floor-2 collapse the BestContinuation leaf caused).
            double value = rolloutDepth > 0
                ? Rollout(node.State, rolloutDepth)
                : EvaluateState(node.State);

            if (value < valMin) valMin = value;
            if (value > valMax) valMax = value;

            // 4. Backup — propagate value up to root.
            var bp = node;
            while (bp != null)
            {
                bp.Visits++;
                bp.TotalValue += value;
                bp = bp.Parent;
            }
        }

        // Pick root child with highest visit count.
        Node? best = null;
        int bestVisits = -1;
        foreach (var c in root.Children)
        {
            if (c.Visits > bestVisits) { bestVisits = c.Visits; best = c; }
        }
        if (best == null || best.IncomingAction.IsEndTurn) return null;
        return (best.IncomingAction.Card!, best.IncomingAction.TargetIdx);
    }

    private static Node SelectChild(Node parent, double cPuct, double valMin, double valMax)
    {
        double bestUcb = double.MinValue;
        Node? best = null;
        double logParent = Math.Log(Math.Max(1, parent.Visits));
        // Normalize Q (mean value) to [0,1] using the tree-wide value range so the UCB
        // exploration term is on a comparable scale. range<=0 (all leaves equal, or first
        // visits) → treat exploit as 0 so selection is driven purely by exploration.
        double range = valMax - valMin;
        foreach (var child in parent.Children)
        {
            double q = child.Visits > 0 ? child.TotalValue / child.Visits : 0;
            double exploit = range > 0 ? (q - valMin) / range : 0;
            double explore = cPuct * Math.Sqrt(logParent / Math.Max(1, child.Visits));
            double ucb = exploit + explore;
            if (ucb > bestUcb) { bestUcb = ucb; best = child; }
        }
        return best!;
    }

    /// Returns all legal (card, target) plays at the given state plus an
    /// EndTurn sentinel.
    ///
    /// 2026-06-19 (path A) — delegates to ActionPlanner.EnumerateCandidates so MCTS
    /// branches over the SAME legal-move surface as the beam search. The old local
    /// filter only checked cost+target and let illegal/wasted plays into the tree
    /// (star-cost cards with no stars, a 2nd Bound card, Smogged skills, orb-evoke
    /// with no orbs, dead-end energy-gain) — handicapping MCTS in the A/B for reasons
    /// unrelated to the algorithm. EnumerateCandidates already encodes all of that.
    private static List<MctsAction> EnumerateActions(SimState state)
    {
        var actions = new List<MctsAction>();
        foreach (var (card, targetIdx) in ActionPlanner.EnumerateCandidates(state))
            actions.Add(new MctsAction(card, targetIdx, false));
        // EndTurn always available as a leaf option.
        actions.Add(new MctsAction(null, -1, true));
        return actions;
    }

    private static bool IsTerminal(SimState state)
    {
        if (state.PlayerHp <= 0) return true;
        bool anyEnemyAlive = false;
        foreach (var e in state.Enemies) if (e.IsAlive) { anyEnemyAlive = true; break; }
        if (!anyEnemyAlive) return true;
        return false;
    }

    /// Static board-value leaf, in board units (HP / block / enemy-HP), NOT play-score units.
    ///
    /// 2026-06-19 (cross-turn pivot) — the lookahead now lives in the TREE (cross-turn
    /// advancement), so the leaf must be a STATIC valuation of the board, not "best play
    /// available from here." The earlier BestContinuation leaf rewarded potential, which made
    /// the do-nothing root the highest-scoring state → EndTurn won every search → the AI ended
    /// every turn with full energy and bled out at floor 2 (measured: 19 plays vs 893 end-turns
    /// over 100 games). A static board value can't have that pathology: a played card lowers
    /// enemy HP / raises block (board improves), and EndTurn now advances the enemy turn (board
    /// worsens by the incoming hit), so plays and ends are valued on the same realized basis.
    /// PlanScorer's card knowledge is intentionally NOT in the leaf here; if this regime shows
    /// promise the next lever is a PlanScorer-derived PUCT action prior (focuses the search),
    /// which composes cleanly with a static value leaf.
    private static double EvaluateState(SimState state)
    {
        if (state.PlayerHp <= 0) return -100000;
        int enemyHp = 0;
        bool anyAlive = false;
        foreach (var e in state.Enemies)
            if (e.IsAlive) { anyAlive = true; enemyHp += Math.Max(0, e.Hp); }
        if (!anyAlive) return 100000;
        return state.PlayerHp + 0.5 * state.PlayerBlock - enemyHp;
    }

    /// Random-rollout from the leaf for `depth` steps then evaluate.
    /// Each step picks a uniformly random legal action (mirroring the
    /// random baseline policy that's the universal floor). Useful
    /// once simulator parity is high enough that the rollout doesn't
    /// drift too far from sts2 ground truth (>= 65-70% target).
    private static double Rollout(SimState state, int depth)
    {
        var rng = new Random();
        var cur = state;
        for (int i = 0; i < depth; i++)
        {
            if (IsTerminal(cur)) break;
            var actions = EnumerateActions(cur);
            if (actions.Count == 0) break;
            var pick = actions[rng.Next(actions.Count)];
            if (pick.IsEndTurn) break;
            try { cur = AnalyticalSimulator.ApplyCardPlay(cur, pick.Card!, pick.TargetIdx); }
            catch { break; }
        }
        return EvaluateState(cur);
    }

    public readonly struct MctsAction
    {
        public readonly SimCard? Card;
        public readonly int TargetIdx;
        public readonly bool IsEndTurn;
        public MctsAction(SimCard? card, int targetIdx, bool isEndTurn)
        {
            Card = card; TargetIdx = targetIdx; IsEndTurn = isEndTurn;
        }
    }

    public sealed class Node
    {
        public SimState State = null!;
        public MctsAction IncomingAction;
        public Node? Parent;
        public List<Node> Children = new();
        public List<MctsAction> UnexpandedActions = new();
        public int Visits = 0;
        public double TotalValue = 0;
        public bool IsTerminal;
        /// Number of EndTurn transitions from the root to this node (cross-turn horizon depth).
        public int TurnDepth = 0;
        public bool IsFullyExpanded => UnexpandedActions.Count == 0;
    }
}
