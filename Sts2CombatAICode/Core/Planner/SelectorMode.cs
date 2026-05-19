using Sts2CombatAI.Data;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Intent of a card-selection prompt — informs whether the selector returns the
/// worst N (burn — discard/exhaust default) or the best N (boost — upgrade/keep).
/// </summary>
internal enum SelectorMode
{
    Burn,   // discard / exhaust — keep good cards, sacrifice junk
    Boost,  // upgrade / keep / move-to-hand — pick the best
}

/// <summary>
/// Infers <see cref="SelectorMode"/> from the *playing card* (the one that triggered
/// the prompt) via the embedded card catalog. No card-id hardcoding here — all
/// boost-vs-burn classification flows from cards_catalog.json axes + description
/// keywords extracted by extract_card_triggers.py.
///
/// Burn is the default — most STS2 prompts are subtractive (discard/exhaust). A
/// card switches to Boost when its catalog entry indicates upgrade or fetch
/// semantics (description contains "강화" or "가져옵니다", or axes include
/// DRAW_PILE_SEARCH / CARD_RETURN).
/// </summary>
internal static class SelectorModeCatalog
{
    public static SelectorMode Infer(string? playingCardId)
    {
        if (string.IsNullOrEmpty(playingCardId)) return SelectorMode.Burn;

        // Catalog stores IDs as "CARD.APOTHEOSIS"; live ID may already be in that form.
        var info = CardCatalog.Lookup(playingCardId);
        if (info == null) return SelectorMode.Burn; // unknown card → default Burn

        // Transform-replace prompts (CHARGE → Minion Dive Bombs+, BEGONE →
        // Minion Strike+). The chosen card is REPLACED, so picking the WORST
        // maximizes the upgrade. Checked first so it overrides Boost-leaning
        // DRAW_PILE_SEARCH / fetch_trigger / etc. axes that some transform
        // cards happen to carry (CHARGE has DRAW_PILE_SEARCH).
        if (info.TransformTrigger) return SelectorMode.Burn;

        if (info.UpgradeTrigger) return SelectorMode.Boost;
        if (info.FetchTrigger) return SelectorMode.Boost;
        // "Select a card to play/copy" prompts (DECISIONS_DECISIONS — choose
        // 1 Skill in hand and play it 3 times). The chosen card becomes the
        // payoff, so pick the BEST candidate instead of the worst.
        if (info.SelectPlayTrigger) return SelectorMode.Boost;
        // "Select a hand card to top-deck" (GLIMMER, PHOTON_CUT). The chosen
        // card is drawn first next turn — pick BEST to lock in a high-value
        // play. Without this branch both cards hit the default Burn.
        if (info.SelectTopdeckTrigger) return SelectorMode.Boost;
        foreach (var ax in info.Axes)
        {
            if (ax == "DRAW_PILE_SEARCH" || ax == "CARD_RETURN")
                return SelectorMode.Boost;
        }
        return SelectorMode.Burn;
    }
}
