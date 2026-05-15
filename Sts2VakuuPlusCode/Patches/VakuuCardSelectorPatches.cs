using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using Sts2VakuuPlus.Planner;

namespace Sts2VakuuPlus.Patches;

/// <summary>
/// Replaces VakuuCardSelector's dumb default (Take/First) with PlanScorer-aware selection.
/// Falls back to the original behavior when VakuuExecutor.CurrentSnapshot is null
/// (e.g., called outside Vakuu's auto-play window).
///
/// Default mode: "burn" — worst N for GetSelectedCards (discard / exhaust assumption).
/// Reward path: best 1 (combat-time card reward is always positive).
/// Mode inference (BurnMode vs BoostMode by LastInvolvedModel name) is a Step-5 extension.
/// </summary>
[HarmonyPatch(typeof(VakuuCardSelector))]
internal static class VakuuCardSelectorPatches
{
    [HarmonyPatch(nameof(VakuuCardSelector.GetSelectedCards))]
    [HarmonyPrefix]
    public static bool GetSelectedCards_Prefix(
        IEnumerable<CardModel> options, int minSelect, int maxSelect,
        ref Task<IEnumerable<CardModel>> __result)
    {
        var snapshot = VakuuExecutor.CurrentSnapshot;
        if (snapshot == null) return true; // fallback to vanilla

        try
        {
            var mode = SelectorModeCatalog.Infer(VakuuExecutor.CurrentPlayingCardId);
            List<CardModel> picked = mode == SelectorMode.Boost
                ? SmartSelectorLogic.SelectBest(options, maxSelect, snapshot)
                : SmartSelectorLogic.SelectWorst(options, maxSelect, snapshot);
            __result = Task.FromResult((IEnumerable<CardModel>)picked);
            MainFile.Logger.Info($"[VakuuPlus] selector({mode} x{maxSelect}) " +
                $"trig={VakuuExecutor.CurrentPlayingCardId ?? "?"}: " +
                $"picked=[{string.Join(",", picked.Select(c => c.Id.Entry))}]");
            return false;
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"[VakuuPlus] selector failed, falling back: {ex.Message}");
            return true;
        }
    }

    [HarmonyPatch(nameof(VakuuCardSelector.GetSelectedCardReward))]
    [HarmonyPrefix]
    public static bool GetSelectedCardReward_Prefix(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives,
        ref CardModel? __result)
    {
        var snapshot = VakuuExecutor.CurrentSnapshot;
        if (snapshot == null) return true;

        try
        {
            var cards = options.Select(o => o.Card).Where(c => c != null).Cast<CardModel>();
            __result = SmartSelectorLogic.SelectBestReward(cards, snapshot);
            MainFile.Logger.Info($"[VakuuPlus] selector(reward): picked={__result?.Id.Entry ?? "null"}");
            return false;
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"[VakuuPlus] reward selector failed, falling back: {ex.Message}");
            return true;
        }
    }
}
