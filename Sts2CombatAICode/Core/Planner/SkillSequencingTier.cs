using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Within-turn priority bucket for Skill cards. Smaller magnitudes than
/// <see cref="SequencingTier"/> (Power) because Skill scoring already has
/// strong state-dependent signal (block-under-threat, draw quality,
/// energy-gain context, survival urgency). This layer only adds nudges for
/// cases NOT already covered:
///   • Setup    — Skills applying Vuln/Weak before remaining attacks
///   • Cantrip  — Draw / Energy gain that funds further plays
///   • Defensive / Utility tiers carry no bonus (existing scoring handles).
/// </summary>
internal enum SkillTier
{
    Unknown   = 0,
    Utility   = 1,
    Defensive = 2,
    Cantrip   = 3,
    Setup     = 4,
}

/// <summary>
/// Static classifier and ordering bonuses for Skill cards. Mirror of
/// <see cref="PowerSequencingTier"/> for the Skill subset.
/// </summary>
internal static class SkillSequencingTier
{
    /// <summary>
    /// Tier of a Skill card. Returns <see cref="SkillTier.Unknown"/> for
    /// non-Skill input or Skills that don't fit any specific bucket.
    /// </summary>
    public static SkillTier Classify(SimCard card)
    {
        if (!card.IsSkill) return SkillTier.Unknown;

        // Setup — applies Vuln / Weak to enemies. Lets the remaining
        // attacks hit harder. Power-application based so it matches
        // runtime effect, not just axis tags.
        if (card.PowerApps.ContainsKey("VulnerablePower")
            || card.PowerApps.ContainsKey("WeakPower"))
            return SkillTier.Setup;

        // Axis-tagged producers (catalog-driven). The catalog currently
        // mixes `VULN`/`WEAK` (no suffix) and `VULN_PRODUCER`/`WEAK_PRODUCER`
        // — see docs/pair_axis_orphan_analysis.md. Both forms accepted.
        if (card.Axes.Contains("VULN_PRODUCER") || card.Axes.Contains("VULN")
            || card.Axes.Contains("WEAK_PRODUCER") || card.Axes.Contains("WEAK"))
            return SkillTier.Setup;

        // Cantrip — draw or energy generation. Plays before damage so the
        // freshly drawn / energy-funded cards land this turn.
        if (card.IsDrawCard || card.IsEnergyGainCard)
            return SkillTier.Cantrip;

        // Defensive — pure self-target block. No tier bonus; block scoring
        // (BlockUnderThreatBonus / NeutralizeBonus / WastedBlockPenalty)
        // already drives ordering correctly.
        if (card.Block > 0 && IsSelfBlockTarget(card.Target))
            return SkillTier.Defensive;

        return SkillTier.Unknown;
    }

    /// <summary>
    /// Base ordering nudge — only fires when ≥2 Skill cards compete in the
    /// same hand. Magnitudes deliberately smaller than PowerSequencingTier:
    /// Power tier 200 / 150 / 100; Skill tier 100 / 60 / 0.
    /// </summary>
    public static int OrderingBonus(SkillTier tier, int skillsInHand)
    {
        if (skillsInHand < 2) return 0;
        return tier switch
        {
            SkillTier.Setup     => 100,
            SkillTier.Cantrip   => 60,
            SkillTier.Defensive => 0,
            SkillTier.Utility   => 0,
            _ => 0,
        };
    }

    /// <summary>
    /// Tier-aware conditional. Adds cases the static OrderingBonus can't
    /// express:
    ///   • Setup with no remaining attacks → small penalty (no beneficiary)
    ///   • Cantrip with near-full hand → small penalty (over-draw waste)
    /// </summary>
    public static (int bonus, string detail) ConditionalBonus(
        SimCard self, SkillTier tier, SimState state)
    {
        int b = 0;
        var parts = new List<string>();

        switch (tier)
        {
            case SkillTier.Setup:
            {
                int remainingAttacks = state.Hand.Count(c =>
                    !ReferenceEquals(c, self) && c.IsPlayable && c.IsAttack);
                if (remainingAttacks == 0)
                {
                    b -= 200;
                    parts.Add("setupNoAtk=-200");
                }
                break;
            }
            case SkillTier.Cantrip:
            {
                int handSize = state.Hand.Count;
                if (handSize >= 9)
                {
                    b -= 150;
                    parts.Add($"cantripFull(hand{handSize})=-150");
                }
                break;
            }
        }

        return (b, parts.Count == 0 ? "" : string.Join(",", parts));
    }

    private static bool IsSelfBlockTarget(TargetType t)
        => t == TargetType.Self || t == TargetType.AnyPlayer
        || t == TargetType.AnyAlly || t == TargetType.AllAllies;
}
