using Sts2VakuuPlus.Data;

namespace Sts2VakuuPlus.Planner;

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

        if (info.UpgradeTrigger) return SelectorMode.Boost;
        if (info.FetchTrigger) return SelectorMode.Boost;
        foreach (var ax in info.Axes)
        {
            if (ax == "DRAW_PILE_SEARCH" || ax == "CARD_RETURN")
                return SelectorMode.Boost;
        }
        return SelectorMode.Burn;
    }
}
