using System.Collections.Generic;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.52 — Deck archetype detector. Walks the full deck (Hand + DrawPile +
/// DiscardPile) and identifies dominant build commitments by counting cards
/// per major axis stem. The result feeds into PlanScorer as a per-card
/// alignment bonus: a card matching the primary archetype gets a small
/// boost so the AI prefers archetype-aligned plays over generic value.
///
/// Why this exists: BuildSynergy.cs already scores hand-level cross-axis
/// matches (e.g. POISON_PRODUCER + POISON_AMPLIFIER in current hand), but
/// it doesn't see the deck-wide pattern. A poison build with 6 POISON cards
/// across all piles should bias the AI toward poison plays even when no
/// amplifier is in the current hand.
///
/// Design constraints:
///   - Read-only over SimState; no decision-making
///   - Cached per scoring round (a single call per PlanNextStep)
///   - Threshold-based (≥ 3 cards) to avoid mis-detection from random rare cards
/// </summary>
internal static class ArchetypeDetector
{
    public enum Build
    {
        None,
        Strength,       // Ironclad attack-scaling
        Block,          // Defensive
        ExhaustBurst,   // Ironclad / cross-character
        Poison,         // Silent DoT
        ShivStorm,      // Silent burst
        OrbDefect,      // Defect orbs
        StarRegent,     // Regent stars
        ForgeBlade,     // Regent Lord's Blade
        SkeletonSwarm,  // Necrobinder minions
        SoulNecro,      // Necrobinder soul DoT
        Doom,           // Necrobinder enemy DoT
    }

    /// <summary>
    /// Axes that mark a card as belonging to each archetype. A card is
    /// "in" an archetype if it has any of these axes.
    /// </summary>
    private static readonly Dictionary<Build, HashSet<string>> ArchetypeAxes = new()
    {
        [Build.Strength] = new() {
            "STRENGTH_PRODUCER", "STRENGTH_AMPLIFIER", "STR_PRODUCER",
        },
        [Build.Block] = new() {
            "BLOCK", "BLOCK_AMPLIFIER", "BLOCK_PAYOFF",
        },
        [Build.ExhaustBurst] = new() {
            "EXHAUST_SELF", "EXHAUST_PRODUCER", "EXHAUST_CONSUMER",
            "EXHAUST_AMPLIFIER", "EXHAUST_BURST",
        },
        [Build.Poison] = new() {
            "POISON_PRODUCER", "POISON_AMPLIFIER",
        },
        [Build.ShivStorm] = new() {
            "SHIV_PRODUCER", "SHIV_CONSUMER", "SHIV_AMPLIFIER",
        },
        [Build.OrbDefect] = new() {
            "ORB_PRODUCER", "ORB_AMPLIFIER", "ORB_EVOKE",
            "LIGHTNING_ORB", "FROST_ORB", "DARK_ORB", "PLASMA_ORB", "GLASS_ORB",
            "DARK_ORB_AMPLIFIER", "LIGHTNING_EVOKE",
        },
        [Build.StarRegent] = new() {
            "STAR_PRODUCER", "STAR_CONSUMER", "STAR_AMPLIFIER", "STAR_X_COST",
        },
        [Build.ForgeBlade] = new() {
            "FORGE_PRODUCER", "FORGE_AMPLIFIER",
            "LORDS_BLADE_AMPLIFIER", "LORDS_BLADE_PAYOFF",
        },
        [Build.SkeletonSwarm] = new() {
            "SKELETON_PRODUCER", "SKELETON_CONSUMER", "SKELETON_AMPLIFIER",
            "OSTY", "MINION",
        },
        [Build.SoulNecro] = new() {
            "SOUL_PRODUCER", "SOUL_CONSUMER", "SOUL_AMPLIFIER",
        },
        [Build.Doom] = new() {
            "DOOM_PRODUCER", "DOOM_CONSUMER", "DOOM_AMPLIFIER", "DOOM_SELF_PRODUCER",
        },
    };

    /// <summary>
    /// Minimum supporter count (cards across all piles) for an archetype to
    /// register as a commitment. Below this, randomness — don't bias plays.
    /// </summary>
    private const int CommitmentThreshold = 3;

    /// <summary>
    /// Per-card alignment bonus per supporter count above the threshold.
    /// 4 supporters: +30. 6 supporters: +90. 10 supporters: +210 (capped).
    /// </summary>
    private const int PerSupporterBonus = 30;
    private const int MaxAlignmentBonus = 250;

    /// <summary>
    /// Detect the dominant archetype + optional secondary. Returns
    /// (primary, secondary, primaryCount).
    /// </summary>
    public static (Build primary, Build secondary, int primaryCount) Detect(SimState state)
    {
        var counts = new Dictionary<Build, int>();
        foreach (var build in ArchetypeAxes.Keys) counts[build] = 0;

        CountPile(state.Hand, counts);
        CountPile(state.DrawPile, counts);
        CountPile(state.DiscardPile, counts);

        Build primary = Build.None;
        Build secondary = Build.None;
        int primaryCount = 0, secondaryCount = 0;
        foreach (var (b, n) in counts)
        {
            if (n < CommitmentThreshold) continue;
            if (n > primaryCount)
            {
                secondary = primary;
                secondaryCount = primaryCount;
                primary = b;
                primaryCount = n;
            }
            else if (n > secondaryCount)
            {
                secondary = b;
                secondaryCount = n;
            }
        }
        return (primary, secondary, primaryCount);
    }

    private static void CountPile(IReadOnlyList<SimCard> pile, Dictionary<Build, int> counts)
    {
        foreach (var c in pile)
        {
            if (c.IsCurseOrStatus) continue;
            foreach (var (build, axes) in ArchetypeAxes)
            {
                foreach (var ax in axes)
                {
                    if (c.Axes.Contains(ax))
                    {
                        counts[build]++;
                        break;  // one count per card per archetype
                    }
                }
            }
        }
    }

    /// <summary>
    /// Alignment bonus for the played card given a detected archetype. Card
    /// is "aligned" if it has any axis belonging to the archetype.
    /// </summary>
    public static int AlignmentBonus(SimCard card, Build primary, Build secondary, int primaryCount)
    {
        if (primary == Build.None) return 0;
        if (card.IsCurseOrStatus) return 0;

        bool primaryAligned = IsCardAligned(card, primary);
        bool secondaryAligned = IsCardAligned(card, secondary);

        if (!primaryAligned && !secondaryAligned) return 0;

        int supportersOverThreshold = System.Math.Max(0, primaryCount - CommitmentThreshold);
        int bonus = supportersOverThreshold * PerSupporterBonus;
        if (bonus > MaxAlignmentBonus) bonus = MaxAlignmentBonus;
        if (!primaryAligned) bonus /= 2;  // secondary alignment gets half
        return bonus;
    }

    private static bool IsCardAligned(SimCard card, Build build)
    {
        if (build == Build.None) return false;
        if (!ArchetypeAxes.TryGetValue(build, out var axes)) return false;
        foreach (var ax in axes)
            if (card.Axes.Contains(ax)) return true;
        return false;
    }
}
