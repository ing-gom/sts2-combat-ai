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
