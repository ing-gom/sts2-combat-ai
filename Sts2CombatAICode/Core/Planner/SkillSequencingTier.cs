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
    /// Complete pair-axis stems (catalog has ≥1 Producer AND ≥1 Amplifier/Consumer)
    /// where producer-first within-turn ordering matters. Each Skill carrying
    /// `<stem>_PRODUCER` or `<stem>_AMPLIFIER` for one of these stems is routed
    /// to Setup tier so it gains the +100 ordering nudge when ≥2 Skills compete.
    ///
    /// ORB is excluded — BuildSynergy already provides full/empty-slot
    /// orb-state awareness for ORB_PRODUCER/CONSUMER and adding Setup tier
    /// on top would double-credit the channel/evoke decision.
    /// </summary>
    private static readonly HashSet<string> PairStemsForSetup = new()
    {
        // DoT (target-resident stack — explicit beneficiary check in ConditionalBonus)
        "POISON", "DOOM", "BURN", "CONSTRICT",
        // Player resources / counters — producer-first ordering helps same-turn
        // consumers but resource accrual stands on its own value (no penalty).
        "STAR", "CUNNING", "SOUL", "FORGE",
        "LORDS_BLADE", "SKELETON",
        // Generated cards / volatile self-managed pool
        "SHIV", "VOLATILE",
        // Exhaust mechanic — EXHAUST_PRODUCER skills feed EXHAUST_CONSUMER payoffs
        "EXHAUST",
        // Specific orb subtype (DefectVisual)
        "DARK_ORB",
    };

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

        // DoT producers — Poison / Doom / Burn / Constrict via PowerApps. The
        // var-key check catches consumer-style cards that ALSO apply the DoT
        // (BUBBLE_BUBBLE applies +9 poison conditionally) and primarily Skill
        // producers (DEADLY_POISON / END_OF_DAYS / etc.).
        if (card.PowerApps.ContainsKey("PoisonPower")
            || card.PowerApps.ContainsKey("DoomPower")
            || card.PowerApps.ContainsKey("BurnPower")
            || card.PowerApps.ContainsKey("ConstrictPower"))
            return SkillTier.Setup;

        // Generic complete-pair stem promotion. Any Skill carrying
        // `<stem>_PRODUCER` or `<stem>_AMPLIFIER` for a registered stem
        // earns Setup tier. Resource-accrual stems (STAR / CUNNING / SOUL /
        // FORGE / LORDS_BLADE / SKELETON / SHIV / VOLATILE / EXHAUST /
        // DARK_ORB) gain ordering nudge without the no-beneficiary penalty —
        // ConditionalBonus distinguishes resource vs DoT vs debuff and only
        // penalises the latter two when their beneficiary is absent.
        foreach (var ax in card.Axes)
        {
            string stem;
            if (ax.EndsWith("_PRODUCER"))
                stem = ax.Substring(0, ax.Length - "_PRODUCER".Length);
            else if (ax.EndsWith("_AMPLIFIER"))
                stem = ax.Substring(0, ax.Length - "_AMPLIFIER".Length);
            else
                continue;
            if (PairStemsForSetup.Contains(stem))
                return SkillTier.Setup;
        }

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
                // Setup-category classification:
                //   • debuff   — VULN / WEAK producer. Beneficiary = remaining attacks.
                //                Penalty when no attack in hand (existing behaviour).
                //   • dot      — POISON / DOOM / BURN / CONSTRICT producer. Beneficiary
                //                = same-stem CONSUMER/AMPLIFIER or remaining attacks
                //                (Envenom / Reaper Form payoff). Penalty when none.
                //   • resource — STAR / CUNNING / SOUL / FORGE / LORDS_BLADE /
                //                SKELETON / SHIV / VOLATILE / EXHAUST / DARK_ORB.
                //                Ordering nudge only — resource accrues for future
                //                turns even without an in-hand consumer, so the
                //                no-beneficiary penalty is omitted.
                bool isDebuff =
                    self.PowerApps.ContainsKey("VulnerablePower")
                    || self.PowerApps.ContainsKey("WeakPower")
                    || self.Axes.Contains("VULN_PRODUCER") || self.Axes.Contains("VULN")
                    || self.Axes.Contains("WEAK_PRODUCER") || self.Axes.Contains("WEAK");
                bool isDot =
                    self.PowerApps.ContainsKey("PoisonPower")
                    || self.PowerApps.ContainsKey("DoomPower")
                    || self.PowerApps.ContainsKey("BurnPower")
                    || self.PowerApps.ContainsKey("ConstrictPower");
                if (!isDot)
                {
                    foreach (var ax in self.Axes)
                    {
                        if (ax == "POISON_PRODUCER" || ax == "POISON_AMPLIFIER"
                            || ax == "DOOM_PRODUCER"   || ax == "DOOM_AMPLIFIER"
                            || ax == "BURN_PRODUCER"   || ax == "BURN_AMPLIFIER"
                            || ax == "CONSTRICT_PRODUCER")
                        { isDot = true; break; }
                    }
                }
                // debuff takes priority — VULN/WEAK producers that *also* carry
                // DoT axes (rare; tagging artefact) should run the attack check.
                string category = isDebuff ? "debuff" : isDot ? "dot" : "resource";

                int remainingAttacks = state.Hand.Count(c =>
                    !ReferenceEquals(c, self) && c.IsPlayable && c.IsAttack);

                if (category == "debuff")
                {
                    if (remainingAttacks == 0)
                    {
                        b -= 200;
                        parts.Add("setupNoAtk=-200");
                    }
                }
                else if (category == "dot")
                {
                    bool hasBeneficiary = remainingAttacks > 0;
                    if (!hasBeneficiary)
                    {
                        foreach (var stem in new[] { "POISON", "DOOM", "BURN", "CONSTRICT" })
                        {
                            bool selfHasStem = false;
                            foreach (var ax in self.Axes)
                            {
                                if (ax == stem || ax == stem + "_PRODUCER" || ax == stem + "_AMPLIFIER")
                                { selfHasStem = true; break; }
                            }
                            if (!selfHasStem) continue;
                            bool found = state.Hand.Any(c =>
                                !ReferenceEquals(c, self) && c.IsPlayable
                                && (c.Axes.Contains(stem + "_CONSUMER")
                                    || c.Axes.Contains(stem + "_AMPLIFIER")));
                            if (found) { hasBeneficiary = true; break; }
                        }
                    }
                    if (!hasBeneficiary)
                    {
                        b -= 200;
                        parts.Add("setupNoBeneficiary=-200");
                    }
                }
                // resource: no penalty — production stands on its own.
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
