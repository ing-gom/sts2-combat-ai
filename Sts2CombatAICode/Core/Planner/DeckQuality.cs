using System.Collections.Generic;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.58 — Deck composition diagnostics. Measures structural quality of
/// the current deck — not just for AI bias but for deck-building feedback.
///
/// Metrics:
///   • PollutionRatio — fraction of curses/status in deck
///   • EnergyCurve     — distribution by cost (0/1/2/3+)
///   • CurveBalance    — how well does the curve match typical 3-energy turns?
///   • PowerRatio      — fraction of Power cards (commitment depth)
///   • CardTypeMix     — Attack/Skill/Power ratios
///   • RareDensity     — high-EstimateCardPower card count (build maturity proxy)
///
/// Used by:
///   • PlanScorer — heavy pollution penalises drawing-into-curses risk; bad
///                  curve makes cycler cards more valuable
///   • VakuuExecutor — surface metrics in turn-start log
///   • Future: deck-building advisor at end-of-combat
///
/// Pure deck-state read; no future-sim.
/// </summary>
internal static class DeckQuality
{
    public readonly struct Profile
    {
        public readonly int TotalCards;
        public readonly int CurseStatusCount;
        public readonly double PollutionRatio;        // 0.0 - 1.0

        // Cost distribution (counts)
        public readonly int Cost0Cards;
        public readonly int Cost1Cards;
        public readonly int Cost2Cards;
        public readonly int Cost3PlusCards;
        public readonly int XCostCards;
        public readonly double AvgCost;

        // Composition
        public readonly int Attacks;
        public readonly int Skills;
        public readonly int Powers;

        // Diagnostics
        public readonly DeckHealth Health;

        public Profile(int total, int curses, int c0, int c1, int c2, int c3, int x,
                       double avgCost, int atks, int skls, int pwrs, DeckHealth h)
        {
            TotalCards = total;
            CurseStatusCount = curses;
            PollutionRatio = total > 0 ? curses / (double)total : 0;
            Cost0Cards = c0;
            Cost1Cards = c1;
            Cost2Cards = c2;
            Cost3PlusCards = c3;
            XCostCards = x;
            AvgCost = avgCost;
            Attacks = atks;
            Skills = skls;
            Powers = pwrs;
            Health = h;
        }
    }

    public enum DeckHealth
    {
        Healthy,        // good curve, low pollution
        Bloated,        // too many cards, slow cycle
        Polluted,       // > 15% curses/status
        Heavy,          // avg cost > 1.7 — energy bottleneck
        Light,          // avg cost < 0.8 — burst loss
        Imbalanced,     // attack/skill/power ratio bad
    }

    public static Profile Compute(SimState state)
    {
        int total = 0, curses = 0;
        int c0 = 0, c1 = 0, c2 = 0, c3 = 0, xc = 0;
        int totalCost = 0;
        int atks = 0, skls = 0, pwrs = 0;

        void Scan(IReadOnlyList<SimCard> pile)
        {
            foreach (var c in pile)
            {
                total++;
                if (c.IsCurseOrStatus) { curses++; continue; }

                if (c.IsAttack) atks++;
                else if (c.IsSkill) skls++;
                else if (c.IsPower) pwrs++;

                if (c.Axes.Contains("X_COST")) xc++;
                int cost = System.Math.Max(0, c.Cost);
                totalCost += cost;
                if (cost == 0) c0++;
                else if (cost == 1) c1++;
                else if (cost == 2) c2++;
                else c3++;
            }
        }
        Scan(state.Hand);
        Scan(state.DrawPile);
        Scan(state.DiscardPile);

        int nonCurseTotal = total - curses;
        double avgCost = nonCurseTotal > 0 ? totalCost / (double)nonCurseTotal : 0;

        // Health classification (first-match wins; order = severity)
        DeckHealth health = DeckHealth.Healthy;
        if (curses > 0 && curses / (double)total > 0.15) health = DeckHealth.Polluted;
        else if (total > 35) health = DeckHealth.Bloated;
        else if (avgCost > 1.7) health = DeckHealth.Heavy;
        else if (avgCost < 0.8 && total >= 15) health = DeckHealth.Light;
        else if (nonCurseTotal > 10)
        {
            double atkR = atks / (double)nonCurseTotal;
            double sklR = skls / (double)nonCurseTotal;
            // Imbalanced: extreme skewing
            if (atkR > 0.75 || sklR > 0.85) health = DeckHealth.Imbalanced;
        }

        return new Profile(total, curses, c0, c1, c2, c3, xc, avgCost,
                            atks, skls, pwrs, health);
    }

    /// <summary>
    /// Per-card score nudge based on deck quality. Mostly small — these are
    /// diagnostic biases, not game-changers.
    /// </summary>
    public static int QualityBonus(SimCard card, Profile profile)
    {
        if (card.IsCurseOrStatus) return 0;
        int bonus = 0;

        // Heavy-pollution deck → cyclers more valuable (skip the curses)
        if (profile.Health == DeckHealth.Polluted)
        {
            if (card.DrawCount > 0) bonus += 60;       // draws past curses
            if (card.Axes.Contains("EXHAUST_TARGET")) bonus += 50; // remove curses
            if (card.Axes.Contains("PILE_TO_HAND")) bonus += 40;   // fetch specific
        }
        // Heavy-cost deck → energy gainers and 0-cost plays welcome
        if (profile.Health == DeckHealth.Heavy)
        {
            if (card.EnergyGain > 0) bonus += 80;
            if (card.Cost == 0) bonus += 20;
        }
        // Bloated deck → cyclers desperately needed
        if (profile.Health == DeckHealth.Bloated)
        {
            if (card.DrawCount > 0) bonus += 40;
            if (card.Axes.Contains("EXHAUST_SELF")) bonus += 30;   // self-shrink
        }
        // Light deck (mostly 0-cost) → big-cost plays underused
        if (profile.Health == DeckHealth.Light)
        {
            if (card.Cost >= 2) bonus += 40;
        }

        return bonus;
    }

    /// <summary>
    /// Brief human-readable diagnostic string. Used in VakuuExecutor log
    /// and at end-of-combat as feedback for deck-building.
    /// </summary>
    public static string Describe(Profile p)
    {
        return $"deck={p.TotalCards} curses={p.CurseStatusCount}({p.PollutionRatio:P0}) " +
               $"cost[0/1/2/3+/X]={p.Cost0Cards}/{p.Cost1Cards}/{p.Cost2Cards}/{p.Cost3PlusCards}/{p.XCostCards} " +
               $"avg={p.AvgCost:F1} mix[A/S/P]={p.Attacks}/{p.Skills}/{p.Powers} health={p.Health}";
    }
}
