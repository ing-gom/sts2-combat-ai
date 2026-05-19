using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Fallback poller for the Vakuu test button attachment. If the Harmony _Ready hook
/// on NEndTurnButton misses (timing issue, scene reload, etc.), this node scans the
/// scene tree every 500ms looking for an attached NEndTurnButton and force-attaches
/// the test buttons.
///
/// Idempotent — the button-attach code already checks parent.HasNode(name) before
/// adding, so multiple invocations are safe.
/// </summary>
internal sealed partial class TestButtonPoller : Node
{
    private double _accumulator = 0;
    private const double IntervalSeconds = 0.5;

    public static void Install(SceneTree tree)
    {
        try
        {
            var node = new TestButtonPoller { Name = "Sts2CombatAITestButtonPoller" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, node);
            MainFile.Logger.Info("[CombatAI] test-button poller installed");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] poller install failed: {ex.Message}");
        }
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < IntervalSeconds) return;
        _accumulator = 0;

        try
        {
            var endTurn = FindEndTurnButton(GetTree().Root);
            if (endTurn == null)
            {
                // No combat scene → reset so the next combat starts re-armed.
                VakuuTestButtonPatch.ResetForNewCombat();
                VakuuExecutor.ResetForNewCombat();
                // v0.10 — Close any leftover streaming decision-log file
                // (e.g. crash-out, victory-screen exit without CloseForCombat
                // hook firing). CloseForCombat is idempotent; safe to call
                // every tick we're out of combat.
                Sts2CombatAI.Diagnostics.DecisionLogPersister.CloseForCombat();
                return;
            }

            VakuuTestButtonPatch.AttachIfMissing(endTurn);

            // Compute the shared "combat + player phase" gate once.
            var cm = CombatManager.Instance;
            int round = cm.IsInProgress ? (cm.DebugOnlyGetState()?.RoundNumber ?? -1) : -1;
            bool combatActive = cm.IsInProgress && !cm.IsOverOrEnding;
            bool playerPhase = cm.IsPlayPhase && !cm.EndingPlayerTurnPhaseOne && !cm.EndingPlayerTurnPhaseTwo;

            // Play button: combat+phase gate plus once-per-turn lock.
            if (endTurn.GetNodeOrNull("VakuuTestButton") is Button vakuuBtn)
            {
                bool alreadyUsed = round > 0 && VakuuTestButtonPatch.LastUsedRound == round;
                vakuuBtn.Disabled = !(combatActive && playerPhase) || alreadyUsed;
            }

            // Step button: combat+phase gate only. No round lock — the user is expected
            // to click it repeatedly to play one card at a time. StepInFlight blocks
            // overlapping clicks while a play is resolving.
            if (endTurn.GetNodeOrNull("VakuuStepButton") is Button stepBtn)
            {
                stepBtn.Disabled = !(combatActive && playerPhase) || VakuuTestButtonPatch.StepInFlight;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] poller tick failed: {ex.Message}");
        }
    }

    private static NEndTurnButton? FindEndTurnButton(Node root)
    {
        if (root is NEndTurnButton b) return b;
        foreach (var child in root.GetChildren())
        {
            var found = FindEndTurnButton(child);
            if (found != null) return found;
        }
        return null;
    }
}
