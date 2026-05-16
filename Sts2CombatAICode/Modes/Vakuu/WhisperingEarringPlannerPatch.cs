using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Relics;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Replaces Whispering Earring's dumb first-turn auto-play with the planner.
/// Delegates the heavy lifting to <see cref="VakuuExecutor"/> so the test button
/// (NEndTurnButton sibling) and the relic share the same loop.
///
/// On any unexpected exception the prefix falls through (returns true), letting vanilla
/// Vakuu handle the turn so the mod never breaks the relic.
/// </summary>
[HarmonyPatch(typeof(WhisperingEarring), nameof(WhisperingEarring.BeforePlayPhaseStartLate))]
internal static class WhisperingEarringPlannerPatch
{
    [HarmonyPrefix]
    public static bool Prefix(WhisperingEarring __instance, PlayerChoiceContext choiceContext, Player player, ref Task __result)
    {
        try
        {
            if (player != __instance.Owner) return true;
            var cs = player.Creature?.CombatState;
            if (cs == null) return true;
            if (cs.RoundNumber > 1) return true;

            __result = VakuuExecutor.RunPlannedTurn(player, choiceContext, __instance);
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] prefix failed, falling through to vanilla: {ex.Message}");
            return true;
        }
    }
}
