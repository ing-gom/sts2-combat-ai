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

    // 2026-05-30 — cards with orb-evoke axes that do NOT evoke the front orb on
    // play (see Lookup for the per-card rationale). Excluded from axis-inferred evoke.
    private static readonly System.Collections.Generic.HashSet<string> NoAxisEvoke =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "TESLA_COIL",     // triggers each Lightning orb's Passive instead
        "LIGHTNING_ROD",  // GainBlock + LightningRodPower (turn-start channel)
        "COLD_SNAP",      // Attack + channels a Frost orb
    };

    // 2026-05-30 — cards that also don't CHANNEL on play (no on-play orb push).
    // LIGHTNING_ROD channels via its turn-start power; TESLA_COIL only passives.
    // COLD_SNAP is intentionally NOT here — it channels a Frost orb on play.
    private static readonly System.Collections.Generic.HashSet<string> NoAxisChannel =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "TESLA_COIL",
        "LIGHTNING_ROD",
    };

    public static OrbMeta Lookup(string cardId, int costSpent, System.Collections.Generic.IReadOnlyList<string> axes,
        bool isPower = false)
    {
        int evokeCount = 0;
        int channelCount = 0;
        var kind = OrbKind.Unknown;
        // 2026-05-30 — true when the switch below set evokeCount EXPLICITLY (even to
        // 0). MULTI_CAST is X-cost: at 0 energy it evokes 0 times, but the
        // ORB_EVOKE-axis default-1 (below) wrongly bumped it to 1 → phantom evoke
        // (energy=0 yet sim_evoke=1, over-dealt one full evoke). Don't let the
        // default override an explicit X-cost count of 0.
        bool explicitEvoke = false;

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
                explicitEvoke = true;
                break;
        }

        // ---- X-cost channelers ----
        // 2026-05-30 — CAPACITOR does NOT channel/evoke. Decompile: it's a Power
        // card whose OnPlay is `OrbCmd.AddSlots(Repeat)` — it raises orb CAPACITY by
        // Repeat(2), nothing else. The sim modeled it as an X-cost Lightning channeler
        // → phantom channel auto-evoked the head orb (enemy_hp −6/−12, stray block).
        // No on-play orb damage/block; leave channelCount/evokeCount at 0. (The slot
        // increase has no immediate combat-state effect for the single-play probe.)
        // (CAPACITOR intentionally has NO orb-effect entry here.)

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
        else if (cardId == "ICE_LANCE")
        {
            // 2026-05-30 — ICE_LANCE: Attack(19) then `for i in Repeat(3):
            // Channel<FrostOrb>` → 3 Frost orbs. The sim defaulted to channelCount=1
            // (ORB_PRODUCER) and missed 2 channels worth of Frost block/overflow-evoke
            // (player_block −20..−30 over multi-Frost queues).
            channelCount = 3;
            kind = OrbKind.Frost;
        }

        // ---- Single-evoke / single-channel inference from axes ----
        // 2026-05-29 — Power cards never evoke on play. THUNDER (applies
        // ThunderPower) and similar evoke-BOOST powers carry ORB_EVOKE /
        // LIGHTNING_EVOKE axes because their *power* fires on Lightning evoke,
        // but the card itself doesn't consume the front orb. Without this guard
        // the sim evoked the front orb when THUNDER was played (Frost→block 5+focus,
        // Lightning→8+focus dmg) — pure phantom damage/block (THUNDER enemy_hp
        // and player_block divergences on Defect). Evoke is an Attack/Skill action.
        // 2026-05-30 — cards that carry ORB_EVOKE/LIGHTNING_EVOKE axes (from an
        // orb hover-tip or an orb-related power) but do NOT actually evoke the
        // front orb on play. The axis-inferred evoke phantom-evoked the front orb
        // (Frost→phantom block, Lightning/Dark→phantom damage), corrupting both
        // block and enemy_hp. Verified via the orb-queue probe diagnostic:
        //   TESLA_COIL    — triggers each Lightning orb's Passive (handled in sim)
        //   LIGHTNING_ROD — GainBlock + applies LightningRodPower (turn-start channel)
        //   COLD_SNAP     — Attack + channels a Frost orb (not evoke)
        if (evokeCount == 0 && !explicitEvoke && !isPower && !NoAxisEvoke.Contains(cardId)
            && (axes.Contains("ORB_EVOKE") || axes.Contains("LIGHTNING_EVOKE")))
            evokeCount = 1;

        // ORB_PRODUCER axis means at least 1 channel — but for X-cost cases we've already
        // set channelCount above and don't want to overwrite.
        // v0.5 — also skip if the card already has an evoke contribution. Shatter and
        // similar attack-evokers are tagged with both ORB_PRODUCER AND ORB_EVOKE in
        // the catalog but only evoke the front orb (no channel). Without this guard,
        // OrbCardCatalog would set channelCount=1, BuildSynergy would treat them as
        // channelers and apply the wrong full-slots penalty.
        // 2026-05-30 — LIGHTNING_ROD/TESLA_COIL don't channel on PLAY either:
        // LIGHTNING_ROD's channel is its turn-start power (AfterEnergyReset);
        // TESLA_COIL triggers passives. An on-play channelCount made the sim push
        // an orb that, at a full queue, auto-evoked the head (Frost→phantom block
        // +5). Suppress. (COLD_SNAP DOES channel a Frost orb on play — kept.)
        // 2026-05-31 — POWER cards never channel on their OWN play. A Defect orb
        // power (STORM→StormPower, HAILSTORM→HailstormPower, …) GRANTS a triggered
        // channel ability; the channel fires on LATER events, not when the power is
        // played. The ORB_PRODUCER-axis default-1 made the sim push a phantom orb
        // that, at a full queue, auto-evoked the head (Frost→phantom block: STORM
        // +10, q=[Frost×3], real GainBlockInternal absent). Mirror the isPower guard
        // already used for evoke above.
        if (channelCount == 0 && evokeCount == 0 && axes.Contains("ORB_PRODUCER")
            && !isPower && !NoAxisChannel.Contains(cardId))
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
