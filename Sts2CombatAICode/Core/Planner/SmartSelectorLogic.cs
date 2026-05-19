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
        => SortByScore(options, state, playingCardId: null).Take(maxSelect).Select(x => x.card).ToList();

    public static List<CardModel> SelectBest(IEnumerable<CardModel> options, int maxSelect, SimState state, string? playingCardId = null)
        => SortByScoreDesc(options, state, playingCardId).Take(maxSelect).Select(x => x.card).ToList();

    public static CardModel? SelectBestReward(IEnumerable<CardModel> options, SimState state)
        => SortByScoreDesc(options, state, playingCardId: null).Select(x => x.card).FirstOrDefault();

    /// <summary>
    /// Computes AI scores for every option without picking — used by
    /// non-decision-making code paths (UI overlays, decision log, audits)
    /// that need to inspect the per-card values. Result is ordered by score
    /// descending; identical to the internal sort used by SelectBest.
    /// </summary>
    public static IEnumerable<(CardModel card, int score)> ScoreAll(
        IEnumerable<CardModel> options, SimState state, string? playingCardId = null)
        => SortByScoreDesc(options, state, playingCardId);

    private static IEnumerable<(CardModel card, int score)> SortByScore(IEnumerable<CardModel> options, SimState state, string? playingCardId)
        => options.Select(c => (card: c, score: ScoreCardModel(c, state, playingCardId))).OrderBy(x => x.score);

    private static IEnumerable<(CardModel card, int score)> SortByScoreDesc(IEnumerable<CardModel> options, SimState state, string? playingCardId)
        => options.Select(c => (card: c, score: ScoreCardModel(c, state, playingCardId))).OrderByDescending(x => x.score);

    private static int ScoreCardModel(CardModel card, SimState state, string? playingCardId = null)
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
        // Mirror StateSnapshotter.BuildSimCard: runtime token-var augment so a
        // selection prompt scoring sees the same producer axes that the main
        // planner sees. Without this, LEADING_STRIKE etc. would score without
        // SHIV_PRODUCER inside the selector even after our catalog patch.
        axes = StateSnapshotter.AugmentTokenProducerAxes(axes, effect);
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
        int score = PlanScorer.Score(simCard, targetIdx, state);

        // Sly-aware discard. CUNNING_CONSUMER cards (SURVIVOR / ACROBATICS /
        // PREPARED / etc.) prompt the player to pick a card to discard. For
        // Sly cards (CUNNING axis, no producer/consumer suffix), discarding
        // auto-plays them at no energy cost — a strict win compared to either
        // keeping them in hand (must pay energy to play) or discarding a
        // generic card (pure loss this turn). Subtract a large constant so
        // Sly options sort to the FRONT of BURN's ascending order, getting
        // picked first as "best to lose" = actually "best to extract free
        // play from."
        var playingInfo = playingCardId != null
            ? Data.CardCatalog.Lookup(playingCardId)
            : null;
        if (playingInfo != null && playingInfo.Axes.Contains("CUNNING_CONSUMER"))
        {
            // Static Sly: card has CUNNING axis in catalog (TACTICIAN / REFLEX
            // / ABRASIVE / FLICK_FLACK / HAZE / RICOCHET / SNEAKY / UNTOUCHABLE).
            bool isSly = axes != null && axes.Contains("CUNNING");

            // HAND_TRICK temp-Sly: catalog axis is static, but the chosen
            // card carries the Sly CardKeyword at runtime. Reflection picks
            // it up; falls back to axis-only when keyword API is missing.
            if (!isSly && CardReflection.HasSlyKeyword(card))
                isSly = true;

            // MasterPlannerPower ("When you play a Skill, it gains Sly").
            // The card in hand doesn't carry the keyword YET — it gains it
            // on play. Discarding it before it's played means it doesn't
            // get the Sly buff and won't auto-play. So this branch is NOT
            // helpful for discard-triggered Sly. Leaving the check off so
            // we don't over-credit non-Sly Skills.

            if (isSly)
            {
                const int SlyDiscardPreference = 10000;
                score -= SlyDiscardPreference;
            }
        }

        // Playing-card-context adjustments. The score above evaluates the
        // option as a single-play; some prompts re-use the chosen card N times,
        // so the comparison should favor cards that compound on repeat
        // (Block, Inflame-style buffs) over one-shot transformers.
        if (playingCardId == "DECISIONS_DECISIONS")
        {
            // 3 plays × 0.7 diminishing-returns discount = 2.1 effective.
            // Mirrors ApplyDecisionsDecisionsRepeat's RepeatCount/Discount so
            // selector pick and main-card payoff use the same valuation.
            score = (int)(score * 2.1);
        }
        else if (playingCardId == "NIGHTMARE")
        {
            // NIGHTMARE adds 3 copies to NEXT-TURN hand. Energy gates how
            // many copies actually resolve next turn (3 energy default):
            //   0-cost   → all 3 plays viable
            //   1-cost   → 2 plays typically (room for one other card)
            //   2+ cost  → only 1 copy plays; the rest sit dead
            // 0.5 next-turn discount applies regardless (matches
            // ApplyNightmareChain's NextTurnDiscount). Net effect: selector
            // strongly prefers cheap-but-strong targets (Inflame, Limit Break,
            // Strike) over expensive bombs.
            int playsNextTurn = simCard.Cost switch
            {
                <= 0 => 3,
                1    => 2,
                _    => 1,
            };
            score = (int)(score * playsNextTurn * 0.5);
        }
        else if (playingCardId == "FOREGONE_CONCLUSION")
        {
            // FOREGONE_CONCLUSION pulls 2 cards from draw pile into NEXT
            // turn's hand. Single-play per card (no repeat), arrives at
            // normal cost. The uniform 0.85 next-turn discount preserves
            // ranking (we already pick best by base score) but documents
            // the delay so the score line items reflect actual EV.
            // Matches ApplyDrawPileSearch's FOREGONE handler 0.75 factor;
            // selector uses a slightly softer 0.85 because the card has
            // already cleared the "should we play foregone" check — the
            // pick is purely about WHICH cards to grab.
            score = (int)(score * 0.85);
        }
        return score;
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
