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
    private const int ProducerWithAmplifierBonus = 250;
    private const int ProducerWithConsumerBonus = 200;
    private const int AmplifierWithProducerBonus = 200;  // symmetric — amplifier rises when producer waits in hand
    private const int ConsumerWithProducerBonus = 200;   // symmetric — consumer rises when producer waits
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
        bool isOrbProducer = card.Axes.Contains("ORB_PRODUCER");
        bool isOrbConsumer = card.Axes.Contains("ORB_CONSUMER");
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

        // Pair bonuses — both directions:
        //   Producer + Amplifier-in-hand → Producer rises (existing).
        //   Producer + Consumer-in-hand  → Producer rises (existing).
        //   Amplifier + Producer-in-hand → Amplifier rises (new, symmetric).
        //   Consumer  + Producer-in-hand → Consumer rises (new, symmetric).
        // Producer/Amplifier match by stem (POISON_PRODUCER ↔ POISON_AMPLIFIER).
        foreach (var ax in card.Axes)
        {
            if (ax.EndsWith("_PRODUCER"))
            {
                var stem = ax.Substring(0, ax.Length - "_PRODUCER".Length);
                bool hasAmplifier = state.Hand.Any(c =>
                    !ReferenceEquals(c, self) && !c.Played
                    && c.Axes.Contains(stem + "_AMPLIFIER"));
                if (hasAmplifier) bonus += ProducerWithAmplifierBonus;

                bool hasConsumer = state.Hand.Any(c =>
                    !ReferenceEquals(c, self) && !c.Played
                    && c.Axes.Contains(stem + "_CONSUMER"));
                if (hasConsumer) bonus += ProducerWithConsumerBonus;
            }
            else if (ax.EndsWith("_AMPLIFIER"))
            {
                var stem = ax.Substring(0, ax.Length - "_AMPLIFIER".Length);
                bool hasProducer = state.Hand.Any(c =>
                    !ReferenceEquals(c, self) && !c.Played
                    && c.Axes.Contains(stem + "_PRODUCER"));
                if (hasProducer) bonus += AmplifierWithProducerBonus;
            }
            else if (ax.EndsWith("_CONSUMER"))
            {
                var stem = ax.Substring(0, ax.Length - "_CONSUMER".Length);
                bool hasProducer = state.Hand.Any(c =>
                    !ReferenceEquals(c, self) && !c.Played
                    && c.Axes.Contains(stem + "_PRODUCER"));
                if (hasProducer) bonus += ConsumerWithProducerBonus;
            }
        }

        // Build commitment: count other cards in hand sharing one of this card's primary builds.
        foreach (var buildTag in card.PrimaryBuildTags)
        {
            int sharing = state.Hand.Count(c =>
                !ReferenceEquals(c, self) && !c.Played
                && c.PrimaryBuildTags.Contains(buildTag));
            if (sharing > 0)
                bonus += sharing * PerBuildCommitmentCard;
        }

        return bonus;
    }
}
