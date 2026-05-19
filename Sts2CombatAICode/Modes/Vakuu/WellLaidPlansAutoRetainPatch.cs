using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Auto-picks which cards WellLaidPlansPower retains at end-of-turn so the
/// player isn't prompted while Vakuu is driving the turn.
///
/// Activation gate: <see cref="VakuuExecutor.LastPlannedTurnRound"/> equals the
/// current round. This means Vakuu (button or relic) drove cards this turn.
/// When the human is playing manually the prefix returns true and vanilla's
/// CardSelectCmd.FromHand UI prompt fires as usual.
///
/// On unexpected exception we fall through to vanilla so a heuristic bug never
/// strands the WLP prompt.
/// </summary>
[HarmonyPatch(typeof(WellLaidPlansPower), nameof(WellLaidPlansPower.BeforeFlushLate))]
internal static class WellLaidPlansAutoRetainPatch
{
    [HarmonyPrefix]
    public static bool Prefix(WellLaidPlansPower __instance, PlayerChoiceContext choiceContext,
                              Player player, ref Task __result)
    {
        try
        {
            if (player != __instance.Owner?.Player) return true;

            var cs = player.Creature?.CombatState;
            if (cs == null) return true;
            if (VakuuExecutor.LastPlannedTurnRound != cs.RoundNumber) return true;

            int amount = __instance.Amount;
            if (amount <= 0) return true;

            __result = ApplyAutoRetain(__instance, player, amount);
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] WLP prefix failed, falling through to vanilla: {ex.Message}");
            return true;
        }
    }

    private static Task ApplyAutoRetain(WellLaidPlansPower power, Player player, int amount)
    {
        var hand = PileType.Hand.GetPile(player).Cards
                    .Where(c => !c.ShouldRetainThisTurn)
                    .ToList();
        if (hand.Count == 0)
        {
            MainFile.Logger.Info("[CombatAI] WLP auto-retain: no eligible cards");
            return Task.CompletedTask;
        }

        int n = Math.Min(amount, hand.Count);
        var picked = RetainPicker.PickTopN(hand, n);
        foreach (var c in picked) c.GiveSingleTurnRetain();

        MainFile.Logger.Info(
            $"[CombatAI] WLP auto-retain x{n}: " +
            $"picked=[{string.Join(",", picked.Select(c => c.Id.Entry))}] " +
            $"from hand=[{string.Join(",", hand.Select(c => c.Id.Entry))}]");

        return Task.CompletedTask;
    }
}
