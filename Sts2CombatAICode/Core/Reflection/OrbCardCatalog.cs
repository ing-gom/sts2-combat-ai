using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Reflection;

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

    public static OrbMeta Lookup(string cardId, int costSpent, System.Collections.Generic.IReadOnlyList<string> axes,
        bool isPower = false)
    {
        int evokeCount = 0;
        int channelCount = 0;
        var kind = OrbKind.Unknown;

        // ---- Multi-evoke front-orb cards (orb amplifiers) ----
        switch (cardId)
        {
            case "DUALCAST":
                evokeCount = 2;
                break;
            case "QUADCAST":
                evokeCount = 4;
                channelCount = 0;
                break;
            case "MULTI_CAST":
            case "MULTICAST":
                // X-cost: evokes (X) times, +1 when upgraded. costSpent already reflects
                // the live spend (ResolveEnergyXValue at play time). We treat it as base
                // count — upgrade bump is handled in CardReflection by reading the card.
                evokeCount = System.Math.Max(0, costSpent);
                break;
        }

        // ---- X-cost channelers ----
        if (cardId == "CAPACITOR")
        {
            // Upgrade variant tracked via Enchantment, not Id suffix —
            // base check covers both forms.
            channelCount = System.Math.Max(0, costSpent);
            kind = OrbKind.Lightning; // Capacitor channels Lightning orbs
        }

        // ---- Multi-channel hardcoded cases (OnPlay loops the channel call) ----
        // Glacier: for (int i = 0; i < 2; i++) Channel<FrostOrb>  → 2 Frost orbs.
        // ConsumingShadow: RepeatVar(2) drives a 2× channel+evoke pair.
        // Refract: RepeatVar(2) — 2 Glass orbs.
        if (cardId == "GLACIER")
        {
            channelCount = 2;
            kind = OrbKind.Frost;
        }
        else if (cardId == "CONSUMING_SHADOW")
        {
            channelCount = 2;
            kind = OrbKind.Dark;
        }
        else if (cardId == "REFRACT")
        {
            channelCount = 2;
            kind = OrbKind.Glass;
        }

        // ---- Single-evoke / single-channel inference from axes ----
        // 2026-05-29 — Power cards never evoke on play. THUNDER (applies
        // ThunderPower) and similar evoke-BOOST powers carry ORB_EVOKE /
        // LIGHTNING_EVOKE axes because their *power* fires on Lightning evoke,
        // but the card itself doesn't consume the front orb. Without this guard
        // the sim evoked the front orb when THUNDER was played (Frost→block 5+focus,
        // Lightning→8+focus dmg) — pure phantom damage/block (THUNDER enemy_hp
        // and player_block divergences on Defect). Evoke is an Attack/Skill action.
        if (evokeCount == 0 && !isPower && (axes.Contains("ORB_EVOKE") || axes.Contains("LIGHTNING_EVOKE")))
            evokeCount = 1;

        // ORB_PRODUCER axis means at least 1 channel — but for X-cost cases we've already
        // set channelCount above and don't want to overwrite.
        // v0.5 — also skip if the card already has an evoke contribution. Shatter and
        // similar attack-evokers are tagged with both ORB_PRODUCER AND ORB_EVOKE in
        // the catalog but only evoke the front orb (no channel). Without this guard,
        // OrbCardCatalog would set channelCount=1, BuildSynergy would treat them as
        // channelers and apply the wrong full-slots penalty.
        if (channelCount == 0 && evokeCount == 0 && axes.Contains("ORB_PRODUCER"))
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
