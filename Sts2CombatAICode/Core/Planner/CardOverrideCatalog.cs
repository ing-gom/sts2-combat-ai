using System.Collections.Generic;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Sparse manual overrides for cards the algorithm consistently under- or over-values.
/// Applied as an additive bonus on top of the base PlanScorer output.
///
/// Keep this list short (~20 entries). Add only when game testing shows the algorithm
/// repeatedly misjudges a card, not as a substitute for general algorithm improvements.
///
/// Card IDs follow the catalog format ("CARD.ECHO_FORM"). Lookup is case-insensitive.
/// </summary>
internal static class CardOverrideCatalog
{
    private static readonly Dictionary<string, int> _bonuses = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // — Power scaling cards the algorithm tends to underestimate —
        ["CARD.ECHO_FORM"]    = 800,   // Watcher game-changer beyond raw PowerCatalog value
        ["CARD.BARRICADE"]    = 600,   // Permanent block carryover — Ironclad endgame
        ["CARD.DEMON_FORM"]   = 700,   // +N Strength every turn — runaway scaling
        ["CARD.WRAITH_FORM"]  = 600,   // Intangible scaling

        // — Fetch / dig cards that bring more cards into play —
        ["CARD.ANOINTED"]     = 300,   // pulls all rare from draw pile
        // HAVOC handled dynamically by EffectSynergy.ApplyAutoPlayFromPile —
        // expected value of top-of-pile minus keystone exhaust risk.
        ["CARD.ALL_FOR_ONE"]  = 400,   // damage 10 + reclaim ALL 0-cost from discard

        // — Big-cost finishers (algorithm under-values when energy is rare) —
        ["CARD.BANSHEES_CRY"] = 600,   // 9-cost, but cost reduces via volatile

        // — Conditional damage finishers —
        ["CARD.ASHEN_STRIKE"] = 200,   // Strength scales with exhaust pile
        ["CARD.PERFECTED_STRIKE"] = 200, // Scales with Strike-tagged cards

        // — Status / hostile self-effects the algorithm sometimes plays mistakenly —
        ["CARD.APPARITION"]   = 200,   // Intangible 1 — strong defense
        ["CARD.ALCHEMIZE"]    = 300,   // free potion

        // — Watcher / Regent stance / star-cost stronger than baseline —
        ["CARD.APOTHEOSIS"]   = 300,   // upgrade all — runs deep

        // v0.10 — Delayed payoff cards the base scorer underestimates.
        // BOMBARDMENT: cost 3 Attack, exhausts on play (18 dmg), but
        // `BeforeHandDraw` auto-replays from Exhaust on the NEXT turn for
        // another 18 free dmg. Effective output ~36 over 2 turns. Base
        // scorer credits only the immediate 18. Bonus ≈ 18 × 0.7 (1-turn
        // delay discount) × 50 (DamagePerPointBonus) = 630 → round to 600.
        ["CARD.BOMBARDMENT"]  = 600,
    };

    /// <summary>
    /// Bonus added to PlanScorer's score. Returns 0 for cards not in the override list.
    /// </summary>
    public static int Lookup(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return 0;
        return _bonuses.TryGetValue(cardId, out var b) ? b : 0;
    }
}
