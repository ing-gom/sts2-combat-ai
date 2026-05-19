using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Detects multi-link synergy chains in the current hand and surfaces them
/// as a small per-card bonus. The constituent links (Producer → Amplifier,
/// Setup Power → Beneficiary, etc.) are already individually scored by
/// <see cref="BuildSynergy"/> / <see cref="HandSynergy"/> /
/// <see cref="EffectSynergy"/> / <see cref="AmplifierSynergy"/>; this
/// module adds a tie-breaking bonus when 3+ such links connect in one
/// hand AND a debug-friendly log entry so DecisionLog can show
/// "combo chain: Inflame → Bash → Cruelty → Strike×3 = +N".
///
/// Edge model (intentionally coarse):
///   • Producer-Amplifier  — same stem (POISON_PRODUCER → POISON_AMPLIFIER)
///   • Producer-Consumer   — same stem
///   • Setup-Beneficiary   — Strength/Dex Power → Attack/SelfBlock Skill
///   • Vuln/Weak-Carrier → VULN/WEAK_AMPLIFIER on another card
///
/// Bonus is applied to the QUERY card if the hand contains a chain of
/// length ≥ 3 that includes the query card. Magnitude is small (50 per
/// extra link beyond 2) so it cannot dominate score on its own.
/// </summary>
internal static class ComboRecognition
{
    private const int PerLinkBonus     = 50;
    private const int MinChainLinks    = 3;
    private const int MaxChainBonus    = 250;

    /// <summary>
    /// Computes the chain bonus for <paramref name="card"/> given the rest
    /// of the hand. Returns (0, "") if no qualifying chain.
    /// </summary>
    public static (int bonus, string detail) Compute(SimCard card, SimState state)
    {
        if (card.IsCurseOrStatus) return (0, "");

        var hand = state.Hand
            .Where(c => c.IsPlayable && !c.IsCurseOrStatus)
            .ToList();
        if (hand.Count < MinChainLinks) return (0, "");

        // Collect synergy edges that the query card participates in.
        var participants = new HashSet<string> { card.Id };
        foreach (var other in hand)
        {
            if (ReferenceEquals(other, card)) continue;
            if (HasEdge(card, other)) participants.Add(other.Id);
        }

        if (participants.Count < MinChainLinks) return (0, "");

        // Expand once: cards that connect via a chain through participants.
        // (One-hop expansion — full graph BFS is overkill for this nudge.)
        int beforeExpand = participants.Count;
        foreach (var other in hand)
        {
            if (participants.Contains(other.Id)) continue;
            foreach (var p in hand)
            {
                if (!participants.Contains(p.Id)) continue;
                if (HasEdge(p, other)) { participants.Add(other.Id); break; }
            }
        }

        int linkCount = participants.Count;
        if (linkCount < MinChainLinks) return (0, "");

        int extraLinks = linkCount - 2; // baseline pair already credited elsewhere
        int bonus = System.Math.Min(MaxChainBonus, extraLinks * PerLinkBonus);
        if (bonus <= 0) return (0, "");

        var ids = string.Join("→", participants.Take(4));
        return (bonus, $"combo({linkCount}link,{ids})+{bonus}");
    }

    /// <summary>
    /// v0.7.38 — Explicit known-pair recipes. When the played card AND its
    /// partner are both in hand, add a fixed bonus to the played card. Captures
    /// archetype-specific synergies that the generic edge model doesn't price
    /// strongly enough (e.g. DEMON_FORM + a fight-long Strike chain).
    ///
    /// Per-pair bonus is small (100-300) so it nudges priority without
    /// dominating direct effect scoring. Pair is undirected — bonus credits
    /// either side if both are in hand.
    /// </summary>
    public static int ExplicitPairBonus(SimCard self, SimState state)
    {
        if (self.Id == null) return 0;
        // Find recipe where self is one side.
        foreach (var (a, b, bonus) in ExplicitPairs)
        {
            string other;
            if (self.Id == a) other = b;
            else if (self.Id == b) other = a;
            else continue;

            // Other card must be in hand AND playable AND not already played.
            foreach (var c in state.Hand)
            {
                if (ReferenceEquals(c, self)) continue;
                if (c.Id != other) continue;
                if (!c.IsPlayable) continue;
                return bonus;
            }
        }
        return 0;
    }

    /// <summary>
    /// v0.7.38 — Static recipe table. Undirected pairs with empirical bonus
    /// magnitude. Each tuple: (card-id A, card-id B, bonus).
    /// </summary>
    private static readonly (string a, string b, int bonus)[] ExplicitPairs =
    {
        // Ironclad — Strength scaling + high-damage attacks. STS2 redesigned
        // strength scaling away from the STS1 "+Str → Limit Break → Heavy Blade"
        // chain; the equivalent compounding now flows through DEMON_FORM /
        // RUPTURE + the heavy single-hit attacks (BLUDGEON 32, UPPERCUT 13 +
        // Vuln+Weak, CINDER 18 + random exhaust).
        ("INFLAME",     "WHIRLWIND",     250),  // +Str × per-hit AOE
        ("INFLAME",     "BLUDGEON",      350),  // +Str × biggest single hit (32 dmg)
        ("INFLAME",     "UPPERCUT",      250),  // +Str × S-tier Vuln+Weak attack
        ("DEMON_FORM",  "BLUDGEON",      350),  // compounding +Str × big hit
        ("DEMON_FORM",  "UPPERCUT",      250),  // compounding +Str × debuff attack
        ("DEMON_FORM",  "CINDER",        180),  // +Str × 18-dmg random-exhaust burst
        ("DEMON_FORM",  "WHIRLWIND",     280),  // +Str/turn × AOE multi-hit
        ("BASH",        "BLUDGEON",      280),  // Vuln × 32 dmg ≈ 48 dmg
        ("BASH",        "UPPERCUT",      200),  // Vuln+ + Weak/Vuln stacking
        ("UPPERCUT",    "BLUDGEON",      250),  // Weak/Vuln pre-buff then big hit
        // HP-loss-based Strength scaling (RupturePower fires on HP loss event)
        ("RUPTURE",     "BLOODLETTING",  220),
        ("RUPTURE",     "OFFERING",      220),
        ("RUPTURE",     "HEMOKINESIS",   180),
        // Reactive block via RagePower / attack hits
        ("RAGE",        "WHIRLWIND",     200),  // Block per attack played
        ("RAGE",        "TWIN_STRIKE",   180),

        // Silent — Vuln/Weak setup + multi-hit burst
        ("NEUTRALIZE",  "DAGGER_SPRAY",  180),  // Weak + AOE
        ("NEUTRALIZE",  "FINISHER",      200),  // Weak + multi-hit-per-card-played
        ("PIERCING_WAIL","FINISHER",     180),
        ("CLOAK_AND_DAGGER","FINISHER",  150),
        // Silent — Shiv generation + AccuracyPower amplification
        ("BLADE_DANCE", "ACCURACY",      220),  // 3 Shivs × Accuracy stack
        ("BLADE_DANCE", "INFINITE_BLADES",180),
        ("LEADING_STRIKE","ACCURACY",    200),  // 2-Shiv side-gen + amp
        ("HIDDEN_DAGGERS","ACCURACY",    220),  // 2 Shiv+ side-gen + amp
        ("DAGGER_SPRAY","ACCURACY",      250),  // Shiv-style AOE + amp
        ("FAN_OF_KNIVES","INFINITE_BLADES",200), // per-turn Shiv + AOE conversion
        // Silent Poison — top archetype payoff beyond role_needs generic
        // (POISON_PRODUCER → POISON_AMPLIFIER w=2.5/250). NOXIOUS_FUMES is
        // S-tier per-turn AOE poison; ACCELERANT's +1 stack per apply
        // compounds significantly over 4-5 turns.
        ("NOXIOUS_FUMES","ACCELERANT",   200),
        ("HAZE",         "ACCELERANT",   180),  // AOE poison burst + amp

        // Defect — Power + orb scaling
        ("STORM",       "DEFRAGMENT",    200),  // Power play → Lightning + Focus
        ("DEFRAGMENT",  "BALL_LIGHTNING",220),  // Focus stack × Lightning channel
        ("SUBROUTINE",  "CREATIVE_AI",   220),  // Power-play chain
        ("SUBROUTINE",  "DEFRAGMENT",    200),  // Power play → +energy + Focus
        ("SUBROUTINE",  "STORM",         180),  // Power play → +energy + Lightning channel
        ("BUFFER",      "ECHO_FORM",     200),  // Sustain + double
        // Defect Frost — HAILSTORM scales with Frost orbs held at turn end;
        // CHILL/GLACIER produce bulk frost; role_needs FROST_PAYOFF → FROST_ORB
        // w=2.5/250 covers axis-axis. Explicit pair adds top-combo magnitude.
        ("HAILSTORM",   "CHILL",         180),
        ("HAILSTORM",   "GLACIER",       180),

        // Ironclad Exhaust core — Corruption enables 0-cost Skill exhaust,
        // which DarkEmbrace/FNP capitalize on. Highest-value Ironclad combo
        // outside Strength scaling.
        ("CORRUPTION",  "DARK_EMBRACE",  280),  // every Skill exhaust → free draw
        ("CORRUPTION",  "FEEL_NO_PAIN",  220),  // every Skill exhaust → free block
        ("DARK_EMBRACE","FEEL_NO_PAIN",  150),  // dual exhaust trigger

        // Necrobinder — Doom + Trigger
        ("COUNTDOWN",   "REAPER_FORM",   250),
        ("PAGESTORM",   "CALL_OF_THE_VOID",250),
        // Necrobinder Skeleton — beyond role_needs generic
        // (SKELETON_PRODUCER → SKELETON_CONSUMER w=2.5/250). REANIMATE is
        // S-tier mass summon; BONE_SHARDS scales linearly with skeleton
        // count; SQUEEZE amplifies skeleton attack damage.
        ("REANIMATE",   "BONE_SHARDS",   250),
        ("DIRGE",       "BONE_SHARDS",   150),  // cheapest producer + S consumer
        ("NECRO_MASTERY","SQUEEZE",      180),  // passive summon + attack amp
        ("REANIMATE",   "SQUEEZE",       180),

        // Regent — Star economy
        ("GENESIS",     "CHILD_OF_THE_STARS",250),
        ("THE_SEALED_THRONE","STARDUST", 300),
        ("ORBIT",       "CHILD_OF_THE_STARS",200),

        // Cross-character — HEADBUTT recall (discard → top of draw pile)
        // pairs only with non-Power cards in STS2 (Powers exhaust on play, so
        // a played Power is not in the discard pile to be recalled). The
        // recall-Strength-setup combo therefore targets INFLAME / RUPTURE in
        // hand-but-discarded contexts (e.g., after CALCULATED_GAMBLE cycle).
        ("HEADBUTT",    "DEMON_FORM",    220),
        ("HEADBUTT",    "DEMESNE",       180),
        ("HEADBUTT",    "INFLAME",       180),
    };

    /// <summary>
    /// Does an edge exist between two cards? Uses axis-suffix matching and
    /// Setup-Power-to-beneficiary rules. Symmetric — the relationship is
    /// undirected, so calling with (a,b) or (b,a) yields the same answer.
    /// </summary>
    private static bool HasEdge(SimCard a, SimCard b)
        => HasDirectionalEdge(a, b) || HasDirectionalEdge(b, a);

    private static bool HasDirectionalEdge(SimCard a, SimCard b)
    {
        // Producer ↔ Amplifier / Consumer (stem match) — already symmetric
        // when checked from either side, but keeping it here so the single
        // directional path returns true on the first matching pass.
        foreach (var ax in a.Axes)
        {
            if (ax.EndsWith("_PRODUCER"))
            {
                var stem = ax.Substring(0, ax.Length - "_PRODUCER".Length);
                if (b.Axes.Contains(stem + "_AMPLIFIER")
                    || b.Axes.Contains(stem + "_CONSUMER"))
                    return true;
            }
            else if (ax.EndsWith("_AMPLIFIER"))
            {
                var stem = ax.Substring(0, ax.Length - "_AMPLIFIER".Length);
                if (b.Axes.Contains(stem + "_PRODUCER")) return true;
            }
            else if (ax.EndsWith("_CONSUMER"))
            {
                var stem = ax.Substring(0, ax.Length - "_CONSUMER".Length);
                if (b.Axes.Contains(stem + "_PRODUCER")) return true;
            }
        }

        // Setup Power → Beneficiary (a applies Str/Dex, b is Attack/Skill).
        // The wrapper HasEdge() will also call HasDirectionalEdge(b, a), so
        // an attack/skill scored against a power in hand still detects the
        // edge — previously this was direction-only and the edge was missed.
        if (a.IsPower)
        {
            bool aHasStr = a.PowerApps.ContainsKey("StrengthPower")
                        || a.PowerApps.ContainsKey("TemporaryStrengthPower");
            bool aHasDex = a.PowerApps.ContainsKey("DexterityPower")
                        || a.PowerApps.ContainsKey("TemporaryDexterityPower");
            if (aHasStr && b.IsAttack) return true;
            if (aHasDex && b.IsSkill && b.Block > 0) return true;
        }

        // Vuln/Weak carrier (a applies the debuff) → VULN/WEAK_AMPLIFIER on b.
        if (a.PowerApps.ContainsKey("VulnerablePower")
            && b.Axes.Contains("VULN_AMPLIFIER")) return true;
        if (a.PowerApps.ContainsKey("WeakPower")
            && b.Axes.Contains("WEAK_AMPLIFIER")) return true;

        return false;
    }
}
