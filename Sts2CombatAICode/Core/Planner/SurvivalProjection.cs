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
        Grind,      // Losing by raw race, BUT high-HP + block can extend TTD → turtle + chip
        Decided,    // already lethal this turn or already inert
    }

    // STS2_RACE_BLOCK — see _neow/PLANSCORER_RACE_FIX.md. Default OFF = legacy behavior
    // (clean A/B). High-HP grind override for the act3-boss over-attack trap: when the
    // raw race says Losing but blocking can extend survival on a high-HP fight, all-in
    // attack is a trap (block buys turns; each extra turn lands more chip).
    private static readonly bool RaceBlockEnabled =
        System.Environment.GetEnvironmentVariable("STS2_RACE_BLOCK") == "1";
    private const int GrindHpThreshold = 150;   // TUNABLE — boss/elite scale; normal monsters below

    public readonly struct Projection
    {
        public readonly int TurnsToDeath;
        public readonly int TurnsToKill;
        public readonly RaceOutcome Race;
        public readonly int NetHpLossPerTurn;        // diagnostic
        public readonly int NetDamagePerTurn;        // diagnostic
        // v0.9.6 — Smallest SandpitAmount across alive enemies (0 = no
        // SandpitPower active). Used to cap TurnsToDeath: a SandpitPower
        // carrier whose counter transitions to 0 force-kills player +
        // pets + Osty regardless of HP/revive (The Insatiable). If our
        // damage runway can't kill the carrier before its deadline, the
        // race flips to Losing so attack-leaning RaceBonus kicks in.
        public readonly int SandpitDeadline;

        public Projection(int ttd, int ttk, RaceOutcome race, int netHpLoss, int netDmg, int sandpitDeadline)
        {
            TurnsToDeath = ttd;
            TurnsToKill = ttk;
            Race = race;
            NetHpLossPerTurn = netHpLoss;
            NetDamagePerTurn = netDmg;
            SandpitDeadline = sandpitDeadline;
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
            return new Projection(99, 0, RaceOutcome.Decided, netHpLoss, throughput.AvgDamagePerTurn, 0);

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

        // v0.9.6 — SandpitPower hard deadline. The Insatiable's SandpitPower
        // decrements at AfterSideTurnStartLate(Enemy); the transition to 0
        // force-kills player + pets + Osty regardless of HP / revive
        // (decompile sts2.decompiled.cs:318071-318104). If TurnsToKill can't
        // beat the carrier's deadline the race is *actually* lost — cap
        // TurnsToDeath at the deadline so RaceOutcome flips to Losing and
        // the existing attack-leaning RaceBonus pushes carrier-clearing
        // damage. carrier-target preference is already enforced via the
        // killSandpit kill-bonus (v0.9.4) at lethal-detection time, so no
        // additional per-target bias is needed here.
        int sandpitDeadline = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            if (e.SandpitAmount > 0 && (sandpitDeadline == 0 || e.SandpitAmount < sandpitDeadline))
                sandpitDeadline = e.SandpitAmount;
        }
        if (sandpitDeadline > 0 && turnsToKill > sandpitDeadline)
            turnsToDeath = Math.Min(turnsToDeath, sandpitDeadline);

        // === Race outcome ===
        RaceOutcome race;
        if (turnsToDeath == 99) race = RaceOutcome.Winning;
        else if (turnsToKill + 1 < turnsToDeath) race = RaceOutcome.Winning;
        else if (turnsToDeath <= turnsToKill) race = RaceOutcome.Losing;
        else race = RaceOutcome.Tight;

        // High-HP grind override (STS2_RACE_BLOCK). Raw race says Losing, but if
        // (a) the fight is high-HP (boss/elite we can't out-burst) and (b) our deck
        // has real block capacity to offset the bleed, all-in attack is a trap:
        // blocking buys turns and each extra turn lands more chip. Turtle + chip.
        // Loosened gate (2026-06-07): the original deck-AVERAGE block condition
        // (AvgBlockPerTurn*2 >= incoming) almost never fired — measured elite-death decks block
        // only 4% of incoming yet hold 1.9 block CARDS/turn, i.e. block is available IN HAND but the
        // deck-average is low. Fire Grind on any high-HP losing fight; RaceBonus rewards whatever
        // block is actually in hand, and does nothing when there's none.
        if (RaceBlockEnabled && race == RaceOutcome.Losing
            && totalEnemyHp >= GrindHpThreshold
            && incoming > 0)
        {
            race = RaceOutcome.Grind;
        }

        // Scaling-commit override (STS2_SCALE_COMMIT) — boss-tier HP where neither attacks nor
        // block alone can win; the only path is building the scaling engine. Takes precedence
        // over Grind (a pure turtle still loses to a 379-HP boss; turtle + scale can out-pace).
        // Fire on ANY non-comfortable race at boss-tier HP (incl. early turns where the raw race
        // hasn't flipped to Losing yet) — the engine must be built from turn 1 to pay off in time.

        return new Projection(turnsToDeath, turnsToKill, race, netHpLoss, netDpt, sandpitDeadline);
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

            case RaceOutcome.Grind:
                // High-HP fight we can't out-burst in time, but block extends survival and every
                // extra turn lands more chip. Turtle + chip. The block bonus must be LARGE relative
                // to the ~2-10k play-score scale or it's a no-op nudge (the original +70 never
                // changed a choice). At +800 the planner still leads with its big attacks (chip) but
                // banks a block card over marginal attacks / scaling instead of all-in attacking and
                // bleeding out — the measured failure (act2 elite deaths: 4% of incoming blocked
                // while holding 1.9 block cards/turn). TUNABLE via the A/B.
                if (card.Block > 0 && !card.IsAttack) return 800;
                if (card.IsAttack) return 0;
                if (card.IsPower) return -40;
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
