using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using Sts2CombatAI.Data;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Picks the top-N cards a player wants to keep past end-of-turn for retain
/// effects (WellLaidPlansPower today; reusable for any "save N cards" prompt).
///
/// First-pass heuristic — scores standalone "next-turn carry value" without
/// next-turn simulation. Priority signals:
///   • cost weight (heavy cards benefit most from skipping a draw)
///   • Power type (unplayed setup that wants more energy turns)
///   • Upgraded (premium copy)
///   • X-cost (accumulated energy payoff)
///   • Cantrip penalty (Exhaust+Draw cycles back anyway)
///   • Innate penalty (first-hand guarantee makes retain redundant)
///   • Ethereal heavy penalty (exhausts at EOT regardless of retain)
///   • Already-retains penalty (would waste the WLP slot)
///
/// v2 candidates (deferred): EnemyTurnSimulator-driven next-turn block/damage
/// need projection, finisher detection, mid-build power synergies.
/// </summary>
internal static class RetainPicker
{
    public static List<CardModel> PickTopN(IReadOnlyList<CardModel> candidates, int n)
    {
        if (n <= 0 || candidates.Count == 0) return new List<CardModel>();
        return candidates
            .Select(c => (card: c, score: Score(c)))
            .OrderByDescending(t => t.score)
            .Take(n)
            .Select(t => t.card)
            .ToList();
    }

    public static int Score(CardModel card)
    {
        int score = 0;

        if (card.HasStarCostX)
        {
            score += 350;
        }
        else
        {
            int cost = card.CurrentStarCost;
            if (cost <= 0)        score += 0;
            else if (cost == 1)   score += 100;
            else if (cost == 2)   score += 250;
            else                  score += 400 + (cost - 3) * 100;
        }

        switch (card.Type)
        {
            case CardType.Power:  score += 200; break;
            case CardType.Skill:  score += 50;  break;
            case CardType.Curse:  score -= 400; break;
            case CardType.Status: score -= 300; break;
        }

        if (card.IsUpgraded) score += 80;

        var info = CardCatalog.Lookup(card.Id.Entry);
        if (info != null)
        {
            if (info.Innate)   score -= 120;
            if (info.Ethereal) score -= 600;
            if (info.Retain)   score -= 150;

            if (card.CurrentStarCost == 0 && info.Exhaust && info.Axes.Contains("DRAW"))
                score -= 200;
        }

        return score;
    }
}
