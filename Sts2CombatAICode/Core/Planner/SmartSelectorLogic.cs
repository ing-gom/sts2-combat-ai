using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using Sts2CombatAI.Reflection;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Smart selector for Vakuu's mid-card prompts (discard X, exhaust X, choose 1 reward).
/// PlanScorer-based — sorts options by score, picks worst N (discard/exhaust default)
/// or best N/1 (keep/reward).
///
/// SimCard overloads exposed for unit testing without game-runtime CardModel instances.
/// </summary>
internal static class SmartSelectorLogic
{
    // ─── CardModel overloads (production — called from Harmony patches) ───

    public static List<CardModel> SelectWorst(IEnumerable<CardModel> options, int maxSelect, SimState state)
        => SortByScore(options, state).Take(maxSelect).Select(x => x.card).ToList();

    public static List<CardModel> SelectBest(IEnumerable<CardModel> options, int maxSelect, SimState state)
        => SortByScoreDesc(options, state).Take(maxSelect).Select(x => x.card).ToList();

    public static CardModel? SelectBestReward(IEnumerable<CardModel> options, SimState state)
        => SortByScoreDesc(options, state).Select(x => x.card).FirstOrDefault();

    private static IEnumerable<(CardModel card, int score)> SortByScore(IEnumerable<CardModel> options, SimState state)
        => options.Select(c => (card: c, score: ScoreCardModel(c, state))).OrderBy(x => x.score);

    private static IEnumerable<(CardModel card, int score)> SortByScoreDesc(IEnumerable<CardModel> options, SimState state)
        => options.Select(c => (card: c, score: ScoreCardModel(c, state))).OrderByDescending(x => x.score);

    private static int ScoreCardModel(CardModel card, SimState state)
    {
        var summary = CardReflection.GetEffectSummary(card);
        // v0.5 — pull catalog metadata so BuildSynergy / orb axes / retain logic see
        // the same data as the in-hand planner path. Previously these options scored
        // as if every card had no axes / no build tags / no orb metadata, so:
        //   • Discard prompts could mark a high-synergy build card as "worst" purely
        //     because its raw damage was lower than a generic Strike.
        //   • Orb-related selection (Dualcast / Quadcast / Capacitor in a reward)
        //     missed their evoke/channel score contribution entirely.
        var id = card.Id.Entry;
        var catalogInfo = Data.CardCatalog.Lookup(id);
        var axes = catalogInfo?.Axes ?? System.Array.Empty<string>();
        int costSpent = CardReflection.GetCost(card);
        var orbMeta = Reflection.OrbCardCatalog.Lookup(id, costSpent, axes);
        var effect = summary with {
            EvokeCount = orbMeta.EvokeCount,
            ChannelCount = orbMeta.ChannelCount,
            ChannelKind = orbMeta.ChannelKind,
        };
        var simCard = new SimCard
        {
            Id = id,
            Cost = costSpent,
            Kind = card.Type,
            Target = card.TargetType,
            SourceRef = card,
            Effect = effect,
            Axes = axes,
            PrimaryBuildTags = catalogInfo?.PrimaryBuildTags ?? System.Array.Empty<string>(),
            IsRetain = catalogInfo?.Retain ?? false,
            IsEthereal = catalogInfo?.Ethereal ?? false,
            IsInnate = catalogInfo?.Innate ?? false,
            IsExhaust = catalogInfo?.Exhaust ?? false,
        };
        int targetIdx = -1;
        if (simCard.IsAttack && simCard.Target == TargetType.AnyEnemy)
            targetIdx = state.Enemies.FindIndex(e => e.IsAlive);
        return PlanScorer.Score(simCard, targetIdx, state);
    }

    // ─── SimCard overloads (unit-test path) ─────────────────────────────

    public static List<SimCard> SelectWorstSimCards(IEnumerable<SimCard> options, int maxSelect, SimState state)
        => options
            .Select(c => (card: c, score: ScoreSimCard(c, state)))
            .OrderBy(x => x.score)
            .Take(maxSelect)
            .Select(x => x.card)
            .ToList();

    public static List<SimCard> SelectBestSimCards(IEnumerable<SimCard> options, int maxSelect, SimState state)
        => options
            .Select(c => (card: c, score: ScoreSimCard(c, state)))
            .OrderByDescending(x => x.score)
            .Take(maxSelect)
            .Select(x => x.card)
            .ToList();

    private static int ScoreSimCard(SimCard card, SimState state)
    {
        int targetIdx = -1;
        if (card.IsAttack && card.Target == TargetType.AnyEnemy)
            targetIdx = state.Enemies.FindIndex(e => e.IsAlive);
        return PlanScorer.Score(card, targetIdx, state);
    }
}
