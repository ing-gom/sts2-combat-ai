using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.55 — Deck throughput estimator. Computes the deck's average
/// per-turn damage capacity (DPT) and per-turn block capacity (BPT),
/// then identifies the "core cards" that contribute most to each.
///
/// Use cases:
///   • Surface "can we survive incoming damage?" indicator
///       (avgBPT vs avg incoming threat)
///   • Surface "can we kill in N turns?"
///       (avgDPT × N vs enemyHpSum)
///   • Boost core-card scoring so the AI prefers high-impact cards over
///     mediocre replacements
///   • DecisionLog visibility: explain what THIS deck depends on
///
/// Methodology:
///   1. Walk Hand+DrawPile+DiscardPile.
///   2. For each card, compute:
///        damage_contribution = TotalDamage (per play, including hits)
///        block_contribution  = Block × max(1, blockMultiplier)
///        per_energy = contribution / max(1, cost) — efficiency metric
///   3. Sum contributions and divide by expected plays-per-turn (≈ 3).
///   4. Rank by per_energy to identify top-K "core" cards.
///
/// Edge cases:
///   • 0-cost cards: treated as cost 1 for per-energy (avoid div/0).
///   • Curses / Status: excluded.
///   • X-cost cards: damage_contribution = damage × (PlayerEnergy hint).
///
/// Output is a small struct cached per scoring round.
/// </summary>
internal static class DeckThroughput
{
    /// <summary>Expected card plays per turn (energy budget proxy).</summary>
    private const int PlaysPerTurn = 3;

    /// <summary>How many top cards to flag as "core" per category.</summary>
    private const int CoreCardCount = 3;

    /// <summary>Bonus applied to core-card scoring when played.</summary>
    private const int CoreCardBonus = 80;

    public readonly struct Profile
    {
        public readonly int AvgDamagePerTurn;        // expected per turn
        public readonly int AvgBlockPerTurn;         // expected per turn
        public readonly int TotalDeckDamage;         // sum of all cards' damage
        public readonly int TotalDeckBlock;          // sum of all cards' block
        public readonly HashSet<string> CoreAttackers;  // card ids
        public readonly HashSet<string> CoreDefenders;  // card ids

        // v0.7.56 — Cycling metrics
        public readonly int DeckSize;                // non-curse cards across all piles
        public readonly int EstimatedCardsPerTurn;   // 5 draw + extra-draws + energy-gain plays
        public readonly int TurnsPerCycle;           // deckSize / cardsPerTurn (1+)
        public readonly HashSet<string> CoreCyclers; // top-3 cards driving cycling

        public Profile(int dpt, int bpt, int totalD, int totalB,
                       HashSet<string> attackers, HashSet<string> defenders,
                       int deckSize, int cardsPerTurn, int turnsPerCycle,
                       HashSet<string> cyclers)
        {
            AvgDamagePerTurn = dpt;
            AvgBlockPerTurn = bpt;
            TotalDeckDamage = totalD;
            TotalDeckBlock = totalB;
            CoreAttackers = attackers;
            CoreDefenders = defenders;
            DeckSize = deckSize;
            EstimatedCardsPerTurn = cardsPerTurn;
            TurnsPerCycle = turnsPerCycle;
            CoreCyclers = cyclers;
        }
    }

    /// <summary>
    /// Compute the throughput profile from SimState. Pure observation; no
    /// state mutation.
    /// </summary>
    public static Profile Compute(SimState state)
    {
        // Per-card (id, damage_total, block_total, per_energy_dmg, per_energy_blk)
        var attackContribs = new List<(string id, int dmg, double perEnergy)>();
        var blockContribs = new List<(string id, int block, double perEnergy)>();
        var cycleContribs = new List<(string id, double cycleScore)>();
        int totalDamage = 0, totalBlock = 0;
        int extraDrawSum = 0, energyGainSum = 0;

        ScanPile(state.Hand, state, attackContribs, blockContribs, cycleContribs,
                 ref totalDamage, ref totalBlock, ref extraDrawSum, ref energyGainSum);
        ScanPile(state.DrawPile, state, attackContribs, blockContribs, cycleContribs,
                 ref totalDamage, ref totalBlock, ref extraDrawSum, ref energyGainSum);
        ScanPile(state.DiscardPile, state, attackContribs, blockContribs, cycleContribs,
                 ref totalDamage, ref totalBlock, ref extraDrawSum, ref energyGainSum);

        int totalCards = state.Hand.Count + state.DrawPile.Count + state.DiscardPile.Count;
        int dpt = totalCards > 0 ? totalDamage * PlaysPerTurn / totalCards : 0;
        int bpt = totalCards > 0 ? totalBlock * PlaysPerTurn / totalCards : 0;

        // v0.7.56 — Cycling metrics. Default hand draw is 5 per turn. Extra
        // draw / energy-gain cards in the deck cycle the deck faster.
        //   cardsPerTurn = baseDraw(5) + drawProbability × extraDrawSum
        //                + energyGainProbability × extraPlays
        // Probability ≈ playsPerTurn / totalCards (chance we draw THIS card
        // each turn). For simplicity, just sum-weighted by /totalCards.
        const int BaseDraw = 5;
        int cardsPerTurn = BaseDraw;
        if (totalCards > 0)
        {
            // Extra draws likely realized per turn: how many of the extra-draw
            // cards we cycle through × their draw amount.
            cardsPerTurn += extraDrawSum * PlaysPerTurn / totalCards;
            // Energy-gain effectively adds plays (each extra energy ≈ 1 more card played).
            cardsPerTurn += energyGainSum * PlaysPerTurn / totalCards;
        }
        int turnsPerCycle = cardsPerTurn > 0
            ? System.Math.Max(1, totalCards / cardsPerTurn)
            : 99;

        // Core cards by category.
        var coreAttackers = new HashSet<string>(
            attackContribs.OrderByDescending(x => x.perEnergy).Take(CoreCardCount).Select(x => x.id));
        var coreDefenders = new HashSet<string>(
            blockContribs.OrderByDescending(x => x.perEnergy).Take(CoreCardCount).Select(x => x.id));
        var coreCyclers = new HashSet<string>(
            cycleContribs.OrderByDescending(x => x.cycleScore).Take(CoreCardCount)
                          .Where(x => x.cycleScore > 0).Select(x => x.id));

        return new Profile(dpt, bpt, totalDamage, totalBlock, coreAttackers, coreDefenders,
                            totalCards, cardsPerTurn, turnsPerCycle, coreCyclers);
    }

    private static void ScanPile(IReadOnlyList<SimCard> pile, SimState state,
                                  List<(string, int, double)> attackContribs,
                                  List<(string, int, double)> blockContribs,
                                  List<(string, double)> cycleContribs,
                                  ref int totalDmg, ref int totalBlk,
                                  ref int extraDrawSum, ref int energyGainSum)
    {
        foreach (var c in pile)
        {
            if (c.IsCurseOrStatus) continue;
            if (c.Id == null) continue;
            int costForEff = System.Math.Max(1, c.Cost);

            if (c.IsAttack && c.TotalDamage > 0)
            {
                int dmg = c.TotalDamage;
                if (c.Axes.Contains("X_COST"))
                    dmg = c.Damage * System.Math.Max(1, state.PlayerEnergy);
                totalDmg += dmg;
                double per = dmg / (double)costForEff;
                attackContribs.Add((c.Id, dmg, per));
            }
            if (c.Block > 0)
            {
                int blk = c.Block;
                totalBlk += blk;
                double per = blk / (double)costForEff;
                blockContribs.Add((c.Id, blk, per));
            }

            // v0.7.56 — Cycling contribution. Card cycles deck via:
            //   • DrawCount: directly pulls more cards
            //   • EnergyGain: enables more plays this turn (effective draw)
            //   • EXHAUST_SELF: shrinks deck size for faster future cycles
            // Cycle score = weighted sum, normalized per-energy.
            double cycleScore = 0;
            if (c.DrawCount > 0)
            {
                extraDrawSum += c.DrawCount;
                cycleScore += c.DrawCount * 1.5;
            }
            if (c.EnergyGain > 0)
            {
                energyGainSum += c.EnergyGain;
                cycleScore += c.EnergyGain * 2.0;  // each energy ≈ 1 extra play
            }
            // Self-exhaust shrinks deck — small one-time cycling benefit.
            if (c.Axes.Contains("EXHAUST_SELF") && c.IsPlayable)
                cycleScore += 0.3;
            // Penalize cost — efficient cyclers preferred.
            cycleScore /= costForEff;
            if (cycleScore > 0)
                cycleContribs.Add((c.Id, cycleScore));
        }
    }

    /// <summary>
    /// Core-card bonus for the played card. Returns CoreCardBonus when the
    /// card id matches a top-K attacker (for Attack plays) or top-K defender
    /// (for Skill plays with Block).
    /// </summary>
    public static int CoreCardBonusFor(SimCard card, Profile profile)
    {
        if (card.Id == null) return 0;
        if (card.IsAttack && profile.CoreAttackers.Contains(card.Id)) return CoreCardBonus;
        if (card.Block > 0 && profile.CoreDefenders.Contains(card.Id)) return CoreCardBonus;
        // v0.7.56 — Cyclers get a smaller bonus. They don't carry the kill
        // directly, but they enable the build (e.g. an attack-heavy deck
        // benefits from playing the draw card first to access more attacks).
        if (profile.CoreCyclers.Contains(card.Id)) return CoreCardBonus * 6 / 10;
        return 0;
    }

    /// <summary>
    /// Coverage diagnostic: can our average DPT kill remaining enemies in
    /// projected turns? Returns ratio &gt; 1.0 if we can outrun the fight.
    /// </summary>
    public static double DamageCoverage(SimState state, Profile profile)
    {
        int turns = RemainingTurnsEstimator.From(state);
        int enemyHp = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) enemyHp += e.Hp + e.Block;
        if (enemyHp <= 0) return 99.0;
        if (profile.AvgDamagePerTurn <= 0) return 0.0;
        return (profile.AvgDamagePerTurn * turns) / (double)enemyHp;
    }

    /// <summary>
    /// Coverage diagnostic: can our average BPT cover expected incoming
    /// damage? Returns ratio &gt; 1.0 if our defense outpaces enemy offense.
    /// </summary>
    public static double BlockCoverage(SimState state, Profile profile)
    {
        int incoming = EnemyTurnSimulator.PredictPlayerDmg(state);
        if (incoming <= 0) return 99.0;
        if (profile.AvgBlockPerTurn <= 0) return 0.0;
        return profile.AvgBlockPerTurn / (double)incoming;
    }
}
