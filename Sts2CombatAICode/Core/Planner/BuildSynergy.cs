using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Detects character-specific build commitments in the hand and rewards plays
/// that synergize with that build. Uses cards_catalog.json's axes + build tags
/// (already extracted by extract_card_triggers.py) — no card-id hardcoding.
///
/// Two kinds of synergy:
///   1) Producer + Consumer/Amplifier pair (e.g., POISON_PRODUCER + POISON_AMPLIFIER)
///      → played first card boosted if the partner is in hand
///   2) Build commitment — N cards from the same build tag (독 빌드 etc.)
///      → all cards from that build get a small bonus
/// </summary>
internal static class BuildSynergy
{
    // v0.7.20 — role_needs weight calibration. weight × WeightToScore = score
    // points. role_needs entry POISON_PRODUCER -> POISON_AMPLIFIER w=2.5
    // maps to 250 (matches the historical ProducerWithAmplifierBonus before
    // the cross-axis lookup was added).
    private const int WeightToScore = 100;

    // Per-stem cap so a single axis with many cross-hooks (e.g., FORGE_PRODUCER
    // wants LORDS_BLADE_AMPLIFIER 3.0 + LORDS_BLADE_PAYOFF 2.0 + BLOCK 1.2 +
    // DAMAGE 1.2 + ...) doesn't dominate scoring beyond the legacy
    // Producer+Consumer 200-pt ceiling.
    private const int PerAxisBonusCap = 400;

    private const int PerBuildCommitmentCard = 80; // each other card from same primary build

    /// <summary>
    /// Bonus added to <paramref name="card"/>'s score based on the rest of the hand's
    /// build composition. <paramref name="self"/> is the card being scored (excluded
    /// from the supporter count).
    /// </summary>
    public static int Compute(SimCard card, SimCard self, SimState state)
    {
        if (card.Axes.Count == 0 && card.PrimaryBuildTags.Count == 0) return 0;

        int bonus = 0;

        // v0.2.13 — Defect orb-state awareness.
        // v0.5 — use Effect.ChannelCount / EvokeCount rather than the raw axes. The
        // catalog tags Dualcast / Quadcast / MultiCast as ORB_PRODUCER even though
        // they actually only re-evoke the front orb (their channel count is 0). With
        // the axis-only check, those cards picked up the channeler "full slots → -300"
        // penalty exactly when they're most useful — clearing the full queue via evoke.
        bool isOrbProducer = card.Effect.ChannelCount > 0;
        bool isOrbConsumer = card.Effect.EvokeCount > 0 || card.Axes.Contains("ORB_CONSUMER");
        if (isOrbProducer || isOrbConsumer)
        {
            int slots = state.PlayerOrbCapacity;
            int filled = state.PlayerOrbCount;
            if (slots > 0)
            {
                bool full = filled >= slots;
                bool empty = filled == 0;
                if (isOrbProducer)
                    bonus += full ? -300 : 150;       // OrbProducerFullSlotsPenalty/EmptySlotsBonus
                if (isOrbConsumer)
                    bonus += empty ? -800 : 400;       // OrbConsumerEmptyPenalty/FullBonus
            }
        }

        // v0.7.20 — role_needs.json driven cross-axis lookup. For every axis
        // on the played card, consult AxisSynergyLookup.NeedsFor(axis) which
        // returns weighted role-need entries imported from CardAdvisor's
        // single source of truth. Each (role, weight) pair fires when the
        // hand contains a card with that role/axis present.
        //
        // The legacy suffix-only pair matching (POISON_PRODUCER ↔ POISON_AMPLIFIER)
        // is a subset of role_needs (those entries are listed at w=2.5 in the
        // table). The lookup also surfaces cross-axis hooks the suffix match
        // can't see (POISON_PRODUCER -> DRAW w=0.8, FORGE_PRODUCER -> BLOCK
        // w=1.2, CUNNING_PRODUCER -> DRAW w=1.0 etc.).
        foreach (var ax in card.Axes)
        {
            var needs = AxisSynergyLookup.NeedsFor(ax);
            if (needs.Count == 0) continue;

            // mutex_group: within group, only the top-weight match contributes.
            // Track best-weight per group, separately accumulate non-grouped.
            Dictionary<string, double>? mutexBest = null;
            int perAxisBonus = 0;

            foreach (var need in needs)
            {
                // requires_with: AND-condition. Only fires when *both* the
                // primary role AND the required-with axis are in hand.
                if (!string.IsNullOrEmpty(need.RequiresWith)
                    && !HandContainsAxis(state.Hand, self, need.RequiresWith))
                    continue;

                if (!HandContainsAxis(state.Hand, self, need.Role)) continue;

                if (!string.IsNullOrEmpty(need.MutexGroup))
                {
                    mutexBest ??= new Dictionary<string, double>(StringComparer.Ordinal);
                    if (!mutexBest.TryGetValue(need.MutexGroup!, out var prev) || need.Weight > prev)
                        mutexBest[need.MutexGroup!] = need.Weight;
                }
                else
                {
                    perAxisBonus += (int)(need.Weight * WeightToScore);
                }
            }

            if (mutexBest != null)
                foreach (var w in mutexBest.Values)
                    perAxisBonus += (int)(w * WeightToScore);

            if (perAxisBonus > PerAxisBonusCap) perAxisBonus = PerAxisBonusCap;
            bonus += perAxisBonus;
        }

        // Build commitment: count other cards in hand sharing one of this card's primary builds.
        foreach (var buildTag in card.PrimaryBuildTags)
        {
            int sharing = state.Hand.Count(c =>
                !ReferenceEquals(c, self)
                && c.PrimaryBuildTags.Contains(buildTag));
            if (sharing > 0)
                bonus += sharing * PerBuildCommitmentCard;
        }

        return bonus;
    }

    /// <summary>
    /// Returns true when any non-<paramref name="self"/> card in the hand
    /// carries the named axis. Used to gate role_needs lookups: a "POISON_PRODUCER
    /// wants DRAW" hook only contributes when at least one DRAW-axis card is
    /// actually in hand.
    /// </summary>
    private static bool HandContainsAxis(IReadOnlyList<SimCard> hand, SimCard self, string axis)
    {
        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains(axis)) return true;
        }
        return false;
    }
}
