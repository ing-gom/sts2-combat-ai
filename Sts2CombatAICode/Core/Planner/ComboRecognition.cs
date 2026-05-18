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
        // Ironclad — Setup + Burst Attack
        ("CARD.INFLAME",     "CARD.WHIRLWIND",     250),  // Str applies × X hits
        ("CARD.INFLAME",     "CARD.HEAVY_BLADE",   300),  // Str × 3 per Heavy Blade
        ("CARD.LIMIT_BREAK", "CARD.HEAVY_BLADE",   400),  // Str doubled × 3
        ("CARD.LIMIT_BREAK", "CARD.WHIRLWIND",     350),
        ("CARD.BASH",        "CARD.HEAVY_BLADE",   200),  // Vuln × Heavy Blade burst
        ("CARD.PUMMEL",      "CARD.BASH",          150),  // Vuln then 4-hit
        ("CARD.DEMON_FORM",  "CARD.SEVER_SOUL",    250),  // Big damage power play
        ("CARD.DEMON_FORM",  "CARD.IMMOLATE",      250),
        ("CARD.SPOT_WEAKNESS","CARD.HEAVY_BLADE",  250),  // Vuln + multiplier attack
        ("CARD.RAGE",        "CARD.WHIRLWIND",     200),  // Block per hit
        ("CARD.RAGE",        "CARD.TWIN_STRIKE",   180),

        // Silent — Vuln/Weak setup + Shiv burst
        ("CARD.NEUTRALIZE",  "CARD.DAGGER_SPRAY",  180),  // Weak + AOE
        ("CARD.NEUTRALIZE",  "CARD.FINISHER",      200),  // Weak + multi-hit
        ("CARD.BLADE_DANCE", "CARD.ACCURACY",      220),  // Shiv generation × dmg
        ("CARD.BLADE_DANCE", "CARD.INFINITE_BLADES",180),
        ("CARD.PIERCING_WAIL","CARD.FINISHER",     180),
        ("CARD.CLOAK_AND_DAGGER","CARD.FINISHER",  150),

        // Defect — Power + Orb burst
        ("CARD.STORM",       "CARD.DEFRAGMENT",    200),  // +Focus + Lightning
        ("CARD.FOCUS",       "CARD.LIGHTNING_ORB", 180),
        ("CARD.SUBROUTINE",  "CARD.CREATIVE_AI",   220),  // Power-play chain
        ("CARD.BUFFER",      "CARD.ECHO_FORM",     200),  // Sustain + double

        // Necrobinder — Doom + Trigger
        ("CARD.COUNTDOWN",   "CARD.REAPER_FORM",   250),
        ("CARD.PAGESTORM",   "CARD.CALL_OF_THE_VOID",250),

        // Regent — Star economy
        ("CARD.GENESIS",     "CARD.CHILD_OF_THE_STARS",250),
        ("CARD.THE_SEALED_THRONE","CARD.STARDUST", 300),
        ("CARD.ORBIT",       "CARD.CHILD_OF_THE_STARS",200),

        // Cross-character — universal setup
        ("CARD.HEADBUTT",    "CARD.LIMIT_BREAK",   180),  // recall buff power
        ("CARD.HEADBUTT",    "CARD.DEMON_FORM",    220),
        ("CARD.HEADBUTT",    "CARD.DEMESNE",       180),
    };

    /// <summary>
    /// Does an edge exist between two cards? Uses axis-suffix matching and
    /// Setup-Power-to-beneficiary rules. Coarse on purpose.
    /// </summary>
    private static bool HasEdge(SimCard a, SimCard b)
    {
        // Producer ↔ Amplifier / Consumer (stem match).
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
