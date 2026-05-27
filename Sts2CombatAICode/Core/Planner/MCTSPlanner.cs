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

    /// PlanNextStep — top-level entry point. Returns the (card,
    /// targetIdx) tuple at the root child with the highest visit
    /// count after N simulations, or null when no valid play exists.
    /// Mirrors ActionPlanner.PlanNextStep's return signature so the
    /// integration layer can drop MCTSPlanner in as an alternative
    /// driver without changing the consumer surface.
    public static (SimCard card, int targetIdx)? PlanNextStep(
        SimState rootState,
        int simulations = DefaultSimulations,
        double cPuct = DefaultC,
        int rolloutDepth = DefaultRolloutDepth)
    {
        if (rootState == null) return null;
        var rootActions = EnumerateActions(rootState);
        if (rootActions.Count == 0) return null;

        var root = new Node
        {
            State = rootState,
            UnexpandedActions = rootActions,
            IsTerminal = IsTerminal(rootState),
        };

        for (int i = 0; i < simulations; i++)
        {
            // 1. Selection — descend tree via UCB1 until a non-fully-
            //    expanded node or a terminal leaf.
            var node = root;
            while (!node.IsTerminal && node.IsFullyExpanded && node.Children.Count > 0)
            {
                node = SelectChild(node, cPuct);
            }

            // 2. Expansion — pop one unexpanded action, apply, attach.
            if (!node.IsTerminal && node.UnexpandedActions.Count > 0)
            {
                var action = node.UnexpandedActions[0];
                node.UnexpandedActions.RemoveAt(0);
                SimState nextState;
                try
                {
                    if (action.IsEndTurn)
                    {
                        // EndTurn — mod sim doesn't currently advance the
                        // turn, so treat it as terminal-for-MCTS (caller
                        // will compare visit counts including EndTurn).
                        nextState = rootState; // sentinel; value comes from leaf
                    }
                    else
                    {
                        nextState = AnalyticalSimulator.ApplyCardPlay(
                            node.State, action.Card!, action.TargetIdx);
                    }
                }
                catch
                {
                    // Simulator threw — treat as terminal with no value.
                    nextState = node.State;
                }
                var child = new Node
                {
                    State = nextState,
                    IncomingAction = action,
                    Parent = node,
                    UnexpandedActions = action.IsEndTurn
                        ? new List<MctsAction>()
                        : EnumerateActions(nextState),
                    IsTerminal = action.IsEndTurn || IsTerminal(nextState),
                };
                node.Children.Add(child);
                node = child;
            }

            // 3. Simulation — random rollout when depth > 0, leaf
            //    heuristic only when 0. Mod sim at 41.8% parity means
            //    rolling out makes leaf value worse, not better; start
            //    at depth 0 and increase as parity climbs.
            double value = rolloutDepth > 0
                ? Rollout(node.State, rolloutDepth)
                : EvaluateState(node.State);

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

    private static Node SelectChild(Node parent, double cPuct)
    {
        double bestUcb = double.MinValue;
        Node? best = null;
        double logParent = Math.Log(Math.Max(1, parent.Visits));
        foreach (var child in parent.Children)
        {
            double exploit = child.Visits > 0 ? child.TotalValue / child.Visits : 0;
            double explore = cPuct * Math.Sqrt(logParent / Math.Max(1, child.Visits));
            double ucb = exploit + explore;
            if (ucb > bestUcb) { bestUcb = ucb; best = child; }
        }
        return best!;
    }

    /// Returns all legal (card, target) plays at the given state plus
    /// an EndTurn sentinel. Mirrors env.py action_masks and
    /// SimStateAdapter.BuildMask's expanded-layout logic so MCTS
    /// branching has the same action surface PPO learned against.
    private static List<MctsAction> EnumerateActions(SimState state)
    {
        var actions = new List<MctsAction>();
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (c.Cost > state.PlayerEnergy) continue;
            if (c.IsAttack)
            {
                // Attack — branch per alive enemy target.
                for (int j = 0; j < state.Enemies.Count; j++)
                {
                    if (state.Enemies[j].IsAlive)
                        actions.Add(new MctsAction(c, j, false));
                }
            }
            else
            {
                // Non-targeted — single branch with targetIdx=-1.
                actions.Add(new MctsAction(c, -1, false));
            }
        }
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

    /// Heuristic leaf value — captures the surface signals a player
    /// optimizes for: high own HP+block, dead/low-HP enemies. Linear
    /// blend keeps the value bounded so UCB exploration term stays
    /// proportional. Same shape as Episode.ComputeRewardBasis on
    /// the train side, minus the per-step deltas (MCTS only needs
    /// terminal/leaf totals).
    private static double EvaluateState(SimState state)
    {
        double v = state.PlayerHp + 0.5 * state.PlayerBlock;
        int enemyHp = 0;
        foreach (var e in state.Enemies)
        {
            if (e.IsAlive) enemyHp += Math.Max(0, e.Hp);
        }
        v -= enemyHp;
        if (state.PlayerHp <= 0) v -= 50;
        bool anyAlive = false;
        foreach (var e in state.Enemies) if (e.IsAlive) { anyAlive = true; break; }
        if (!anyAlive) v += 50;
        return v;
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
        public bool IsFullyExpanded => UnexpandedActions.Count == 0;
    }
}
