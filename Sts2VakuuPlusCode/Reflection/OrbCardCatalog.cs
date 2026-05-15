using Sts2VakuuPlus.Sim;

namespace Sts2VakuuPlus.Reflection;

/// <summary>
/// Sparse hand-coded catalog for orb-related cards whose evoke/channel semantics aren't
/// expressed cleanly through DynamicVars alone. Resolves:
///
///   • Dualcast    → evoke front orb ×2 (no RepeatVar, hardcoded in OnPlay)
///   • Quadcast    → evoke front orb ×4 (uses RepeatVar — but easier to special-case)
///   • MultiCast   → evoke front orb ×(X + upgrade), X = energy spent
///   • Capacitor   → channel X Lightning orbs (X-cost)
///   • Channel-axis cards → declare their orb kind via axes (DARK_ORB / FROST_ORB / ...)
///
/// Returns (evokeCount, channelCount, channelKind) for the given card id + cost.
/// Cost matters for X-cost cards where the player's current energy decides the count.
/// </summary>
internal static class OrbCardCatalog
{
    public readonly record struct OrbMeta(int EvokeCount, int ChannelCount, OrbKind ChannelKind);

    public static OrbMeta Lookup(string cardId, int costSpent, System.Collections.Generic.IReadOnlyList<string> axes)
    {
        int evokeCount = 0;
        int channelCount = 0;
        var kind = OrbKind.Unknown;

        // ---- Multi-evoke front-orb cards (orb amplifiers) ----
        switch (cardId)
        {
            case "CARD.DUALCAST":
                evokeCount = 2;
                break;
            case "CARD.QUADCAST":
                evokeCount = 4;
                channelCount = 0;
                break;
            case "CARD.MULTI_CAST":
            case "CARD.MULTICAST":
                // X-cost: evokes (X) times, +1 when upgraded. costSpent already reflects
                // the live spend (ResolveEnergyXValue at play time). We treat it as base
                // count — upgrade bump is handled in CardReflection by reading the card.
                evokeCount = System.Math.Max(0, costSpent);
                break;
        }

        // ---- X-cost channelers ----
        if (cardId == "CARD.CAPACITOR" || cardId == "CARD.CAPACITOR+1")
        {
            channelCount = System.Math.Max(0, costSpent);
            kind = OrbKind.Lightning; // Capacitor channels Lightning orbs
        }

        // ---- Multi-channel hardcoded cases (OnPlay loops the channel call) ----
        // Glacier: for (int i = 0; i < 2; i++) Channel<FrostOrb>  → 2 Frost orbs.
        // ConsumingShadow: RepeatVar(2) drives a 2× channel+evoke pair.
        // Refract: RepeatVar(2) — 2 Glass orbs.
        if (cardId == "CARD.GLACIER")
        {
            channelCount = 2;
            kind = OrbKind.Frost;
        }
        else if (cardId == "CARD.CONSUMING_SHADOW")
        {
            channelCount = 2;
            kind = OrbKind.Dark;
        }
        else if (cardId == "CARD.REFRACT")
        {
            channelCount = 2;
            kind = OrbKind.Glass;
        }

        // ---- Single-evoke / single-channel inference from axes ----
        if (evokeCount == 0 && (axes.Contains("ORB_EVOKE") || axes.Contains("LIGHTNING_EVOKE")))
            evokeCount = 1;

        // ORB_PRODUCER axis means at least 1 channel — but for X-cost cases we've already
        // set channelCount above and don't want to overwrite.
        if (channelCount == 0 && axes.Contains("ORB_PRODUCER"))
            channelCount = 1;

        if (kind == OrbKind.Unknown)
        {
            if (axes.Contains("DARK_ORB"))      kind = OrbKind.Dark;
            else if (axes.Contains("FROST_ORB")) kind = OrbKind.Frost;
            else if (axes.Contains("LIGHTNING_ORB")) kind = OrbKind.Lightning;
            else if (axes.Contains("PLASMA_ORB"))    kind = OrbKind.Plasma;
            else if (axes.Contains("GLASS_ORB"))     kind = OrbKind.Glass;
        }

        return new OrbMeta(evokeCount, channelCount, kind);
    }
}
