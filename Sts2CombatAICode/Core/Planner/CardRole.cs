using System.Collections.Generic;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.60 — Per-card role classification. Tags each card with what it
/// contributes to the combat plan: Carry / Support / Setup / Filler /
/// Defensive / Cycler / Tech / Curse.
///
/// Used by:
///   • PlanScorer — modest role-coherence bias (Carry in Burst phase wins
///     ties vs Filler; Setup in Cleanup phase loses ties)
///   • DecisionLog — surfaces card role in breakdown for explainability
///   • Future deck-building advisor — "your deck has 8 Filler cards"
///
/// Classification rules (priority order):
///   1. Curse / Status → Curse
///   2. Power card → Setup
///   3. EnergyGain / DrawCount > 0 → Cycler
///   4. EXHAUST_TARGET / removes curses → Tech
///   5. Block >= 8 → Defensive
///   6. Block >= 4 → Defensive (light)
///   7. Damage >= 16 or X_COST attack → Carry
///   8. Vuln/Weak/Frail producer (debuff setup) → Setup
///   9. Damage 6-15 → Support
///   10. Else → Filler
///
/// Role tags are STATIC per card — not deck-state dependent. They describe
/// the card's *intended* role, not its current effectiveness.
/// </summary>
internal static class CardRole
{
    public enum Role
    {
        Curse,        // unplayable burden
        Filler,       // low-impact attack / weak skill
        Support,      // mid-damage attack
        Carry,        // primary damage / win-condition card
        Setup,        // applies buffs / debuffs for later payoff
        Defensive,    // block-heavy
        Cycler,       // draw / energy / hand throughput
        Tech,         // exhaust curses / pile-search / utility
    }

    public static Role Classify(SimCard card)
    {
        if (card.IsCurseOrStatus) return Role.Curse;
        if (card.IsPower) return Role.Setup;

        bool hasDraw = card.DrawCount > 0;
        bool hasEnergy = card.EnergyGain > 0;
        if (hasDraw || hasEnergy) return Role.Cycler;

        var axes = card.Axes;
        if (axes.Contains("EXHAUST_TARGET") || axes.Contains("EXHAUST_TARGET_RANDOM")
            || axes.Contains("PILE_TO_HAND") || axes.Contains("DRAW_PILE_SEARCH"))
            return Role.Tech;

        if (card.Block >= 8) return Role.Defensive;

        if (card.IsAttack)
        {
            // X-cost or high-damage = Carry
            if (axes.Contains("X_COST") || card.TotalDamage >= 16) return Role.Carry;
            if (card.TotalDamage >= 6) return Role.Support;
            return Role.Filler;
        }

        // Skills that aren't block-heavy / cyclers / tech
        if (axes.Contains("VULN_PRODUCER") || axes.Contains("WEAK_PRODUCER")
            || axes.Contains("FRAIL_PRODUCER") || axes.Contains("DEBUFF"))
            return Role.Setup;

        if (card.Block >= 4) return Role.Defensive;

        // Strength / Dex / Vigor producers
        if (axes.Contains("STRENGTH_PRODUCER") || axes.Contains("DEXTERITY_PRODUCER")
            || axes.Contains("VIGOR") || axes.Contains("SCALING"))
            return Role.Setup;

        return Role.Filler;
    }

    /// <summary>
    /// Phase-aware role coherence bonus. Carry cards in Burst phase get
    /// nudged up; Setup in Cleanup phase nudged down. Modest magnitudes
    /// because existing phase/race biases already partially capture this.
    /// </summary>
    public static int CoherenceBonus(Role role, CombatPlan.Stage stage)
    {
        switch (stage)
        {
            case CombatPlan.Stage.Opening:
                if (role == Role.Setup) return 30;
                if (role == Role.Cycler) return 20;
                return 0;
            case CombatPlan.Stage.Setup:
                if (role == Role.Setup) return 40;
                if (role == Role.Cycler) return 20;
                if (role == Role.Carry) return -20;  // hold for burst
                return 0;
            case CombatPlan.Stage.Burst:
                if (role == Role.Carry) return 50;
                if (role == Role.Support) return 30;
                if (role == Role.Setup) return -30;
                return 0;
            case CombatPlan.Stage.Lockdown:
                if (role == Role.Defensive) return 50;
                if (role == Role.Setup) return 20;
                return 0;
            case CombatPlan.Stage.Cleanup:
                if (role == Role.Carry) return 60;
                if (role == Role.Support) return 40;
                if (role == Role.Filler) return 20;
                if (role == Role.Setup) return -60;
                if (role == Role.Tech) return -30;
                return 0;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Aggregate the deck's role mix. Used by VakuuExecutor turn-start log
    /// and by future deck-building advice.
    /// </summary>
    public readonly struct RoleMix
    {
        public readonly int Curse, Filler, Support, Carry, Setup, Defensive, Cycler, Tech;
        public RoleMix(int cu, int fi, int su, int ca, int se, int de, int cy, int te)
        {
            Curse = cu; Filler = fi; Support = su; Carry = ca;
            Setup = se; Defensive = de; Cycler = cy; Tech = te;
        }
        public override string ToString()
            => $"carry={Carry} support={Support} setup={Setup} def={Defensive} " +
               $"cyc={Cycler} tech={Tech} filler={Filler} curse={Curse}";
    }

    public static RoleMix DeckMix(SimState state)
    {
        int cu = 0, fi = 0, su = 0, ca = 0, se = 0, de = 0, cy = 0, te = 0;
        void Scan(IReadOnlyList<SimCard> pile)
        {
            foreach (var c in pile)
            {
                switch (Classify(c))
                {
                    case Role.Curse: cu++; break;
                    case Role.Filler: fi++; break;
                    case Role.Support: su++; break;
                    case Role.Carry: ca++; break;
                    case Role.Setup: se++; break;
                    case Role.Defensive: de++; break;
                    case Role.Cycler: cy++; break;
                    case Role.Tech: te++; break;
                }
            }
        }
        Scan(state.Hand);
        Scan(state.DrawPile);
        Scan(state.DiscardPile);
        return new RoleMix(cu, fi, su, ca, se, de, cy, te);
    }
}
