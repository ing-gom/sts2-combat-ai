using System;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.57 — Multi-turn survival projection. Where SurvivalUrgency tells us
/// "is THIS turn dangerous?", this module projects "how many turns until we
/// die?" and "how many turns until we kill?". The race between the two
/// shapes whether the AI should push damage or stack defense.
///
/// Inputs (all visible state):
///   • Current PlayerHp / PlayerBlock
///   • Predicted incoming damage per turn (PredictPlayerDmg)
///   • Player DoT (Burn / Poison / Doom) — bleeds HP every turn
///   • Block-per-turn capacity (DeckThroughput.BPT)
///   • Damage-per-turn capacity (DeckThroughput.DPT)
///   • Total enemy HP
///   • Enemy auto-block / regen
///
/// Outputs:
///   • TurnsToDeath  — projected turns at current pace
///   • TurnsToKill   — projected turns to clear enemies
///   • Race          — Winning / Tight / Losing
///   • Severity      — penalty/bonus magnitude for plan biasing
///
/// Bias model:
///   • Losing race → push damage hard (block can't save us, must kill faster)
///   • Tight race → balance (every margin matters)
///   • Winning race → safe (can invest in scaling)
///
/// Pure observation — no future-sim of unknown enemy intents.
/// </summary>
internal static class SurvivalProjection
{
    public enum RaceOutcome
    {
        Winning,    // TurnsToKill < TurnsToDeath − 1 (comfortable margin)
        Tight,      // within 1 turn
        Losing,     // TurnsToDeath ≤ TurnsToKill (we die first)
        Decided,    // already lethal this turn or already inert
    }

    public readonly struct Projection
    {
        public readonly int TurnsToDeath;
        public readonly int TurnsToKill;
        public readonly RaceOutcome Race;
        public readonly int NetHpLossPerTurn;        // diagnostic
        public readonly int NetDamagePerTurn;        // diagnostic

        public Projection(int ttd, int ttk, RaceOutcome race, int netHpLoss, int netDmg)
        {
            TurnsToDeath = ttd;
            TurnsToKill = ttk;
            Race = race;
            NetHpLossPerTurn = netHpLoss;
            NetDamagePerTurn = netDmg;
        }
    }

    public static Projection Compute(SimState state, DeckThroughput.Profile throughput)
    {
        // === HP runway ===
        int incoming = EnemyTurnSimulator.PredictPlayerDmg(state);  // post-block leak
        int dotPlayer = state.PlayerBurn + state.PlayerPoison + state.PlayerConstrict;
        // PlayerDoom ticks separately at next turn start, magnitude = stack count
        int doomTick = state.PlayerDoom;

        // Block-per-turn from deck — but actually realized depends on cards drawn.
        // Use 60% of avgBPT (not every turn surfaces our best block cards).
        int realizedBpt = throughput.AvgBlockPerTurn * 6 / 10;
        // Cap to incoming — can't block more than we're hit.
        int effectiveBlock = Math.Min(incoming + realizedBpt, realizedBpt);

        int netHpLoss = Math.Max(0, incoming - effectiveBlock) + dotPlayer + doomTick;
        // Edge: if no incoming and no DoT, can't die from enemy actions.
        if (netHpLoss <= 0) netHpLoss = 1;  // floor to avoid divide-by-zero, treat as eventual loss

        int turnsToDeath = Math.Max(1, state.PlayerHp / netHpLoss);
        if (incoming == 0 && dotPlayer == 0 && doomTick == 0)
            turnsToDeath = 99;  // safe — we don't die

        // === Damage runway ===
        int totalEnemyHp = 0;
        int totalAutoBlock = 0;
        int totalRegen = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            totalEnemyHp += e.Hp + e.Block;
            totalAutoBlock += RemainingTurnsEstimator.EnemyAutoBlock(e);
            totalRegen += RemainingTurnsEstimator.EnemyRegen(e);
        }
        if (totalEnemyHp <= 0)
            return new Projection(99, 0, RaceOutcome.Decided, netHpLoss, throughput.AvgDamagePerTurn);

        int netDpt = Math.Max(0, throughput.AvgDamagePerTurn - totalAutoBlock - totalRegen);
        // Add player DoT on enemies (Poison/Constrict/Doom) as parallel damage stream.
        int enemyDotPerTurn = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            enemyDotPerTurn += e.PoisonAmount + e.ConstrictAmount + e.DoomAmount;
        }
        netDpt += enemyDotPerTurn;
        if (netDpt <= 0) netDpt = 1;  // floor — eventually we'd lose

        int turnsToKill = Math.Max(1, totalEnemyHp / netDpt);

        // === Race outcome ===
        RaceOutcome race;
        if (turnsToDeath == 99) race = RaceOutcome.Winning;
        else if (turnsToKill + 1 < turnsToDeath) race = RaceOutcome.Winning;
        else if (turnsToDeath <= turnsToKill) race = RaceOutcome.Losing;
        else race = RaceOutcome.Tight;

        return new Projection(turnsToDeath, turnsToKill, race, netHpLoss, netDpt);
    }

    /// <summary>
    /// Per-card bias based on the race outcome. Modest magnitudes —
    /// existing survival/lethal penalties dominate the actual emergencies.
    /// This is mid-fight strategic nudging.
    /// </summary>
    public static int RaceBonus(SimCard card, Projection proj)
    {
        if (card.IsCurseOrStatus) return 0;

        switch (proj.Race)
        {
            case RaceOutcome.Losing:
                // We will die before we kill. Push damage HARD; block alone
                // won't save us (the math says we lose anyway).
                if (card.IsAttack) return 80;
                if (card.Block > 0 && !card.IsAttack) return -60;
                if (card.IsPower) return -100;  // scaling won't pay off
                return 0;

            case RaceOutcome.Tight:
                // Every margin matters. Slight pro-damage tilt; defensive
                // cards still useful but not maxed.
                if (card.IsAttack && card.TotalDamage >= 10) return 40;
                if (card.Block >= 8) return 30;
                return 0;

            case RaceOutcome.Winning:
                // Comfortable. Free to invest in setups + scaling.
                if (card.IsPower) return 50;
                if (card.Axes.Contains("SCALING")) return 30;
                return 0;

            case RaceOutcome.Decided:
            default:
                return 0;
        }
    }
}
