using System.Collections.Generic;
using System.Linq;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Snapshot of the combat state used by the planner. POCO — no game refs except
/// SourceRef pointers on SimCard/SimEnemy used to bind plans back to live objects.
///
/// v0.1 is read-only by design: the planner picks one (card, target) per step, the real
/// game engine resolves it, then we re-snapshot. Mutating sim state isn't required for
/// the step-greedy approach, but the type is left mutable for v0.2 lookahead expansion.
/// </summary>
internal sealed record SimState
{
    public required int PlayerHp { get; init; }
    public required int PlayerBlock { get; init; }
    public required int PlayerEnergy { get; init; }
    public required List<SimEnemy> Enemies { get; init; }
    public required List<SimCard> Hand { get; init; }

    // v0.2.4 — player status powers (Strength/Dexterity/Vulnerable/Weak/Frail).
    public int PlayerStrength { get; init; }
    public int PlayerDexterity { get; init; }
    public int PlayerVulnerable { get; init; }  // turns of Vulnerable on player
    public int PlayerWeak { get; init; }
    public int PlayerFrail { get; init; }
    /// <summary>
    /// v0.5 — IntangiblePower stacks on the player. While > 0, each incoming hit
    /// is capped at 1 damage (regardless of source). PredictPlayerDmg honors this
    /// so threat estimation correctly drops on Intangible turns (Apparition,
    /// WraithForm) — without it, the planner would over-defend during a turn
    /// where the player is effectively invulnerable.
    /// </summary>
    public int PlayerIntangible { get; init; }

    /// <summary>
    /// v0.5 — End-of-turn block bonus from MetallicizePower + PlatedArmorPower.
    /// Added to PlayerBlock in PredictPlayerDmg so threat estimation knows the
    /// player will gain these blocks just before enemies attack. Avoids
    /// double-blocking turns where Metallicize already covers a small incoming
    /// hit (block-defends would otherwise score as needed when they're not).
    /// </summary>
    public int PlayerEndOfTurnBlockBonus { get; init; }

    // v0.2.9 — pile sizes for Draw card valuation.
    // We don't need card identities (privacy + complexity); raw counts let the
    // planner know whether "drawing more" is even possible / fruitful.
    public int DrawPileSize { get; init; }
    public int DiscardPileSize { get; init; }

    // v0.2.11 — player resource pool (Regent's Stars, Watcher's Mantra-equivalent).
    // Star-cost cards (star_cost > 0 in catalog) require this resource separate
    // from energy. Tracked here so planner can avoid star-cost plays when empty.
    public int PlayerStars { get; init; }

    // v0.2.13 — Defect orb queue state. Count == 0 + Capacity == 0 → not Defect; otherwise
    // ratio tells planner whether Channel (need more orbs) or Evoke (clear room) is better.
    public int PlayerOrbCount { get; init; }
    public int PlayerOrbCapacity { get; init; }

    /// <summary>
    /// v0.5 — FocusPower stacks on the player. Adds to every Defect orb's
    /// passive tick and evoke value (Lightning, Frost, Dark, Glass — Plasma
    /// is unaffected since it grants energy not damage/block). Captured by
    /// StateSnapshotter and propagated through the simulator's Power-card
    /// branch so depth-2 sees the boosted orb output.
    /// </summary>
    public int PlayerFocus { get; init; }

    /// <summary>
    /// v0.5 — Per-type "next N plays cost 0" counters from FreeAttackPower /
    /// FreeSkillPower / FreePowerPower. When > 0, EnumerateCandidates lets
    /// otherwise-unaffordable cards through and the simulator skips the energy
    /// spend on play (decrementing the counter). Without this, depth-2 still
    /// charges the card's cost in the lookahead even when a Free*Power has
    /// already been applied this turn.
    /// </summary>
    public int PlayerFreeAttacks { get; init; }
    public int PlayerFreeSkills { get; init; }
    public int PlayerFreePowers { get; init; }

    /// <summary>
    /// v0.4 — Ordered orb queue. OrbQueue[0] is the head (oldest, evokes first / kicked first
    /// on overflow). Empty when not playing Defect. Count matches PlayerOrbCount.
    /// </summary>
    public IReadOnlyList<OrbKind> OrbQueue { get; init; } = System.Array.Empty<OrbKind>();

    /// <summary>
    /// v0.4 — Per-Dark-orb accumulated evoke value (DarkOrb's passive raises its own
    /// evokeVal each turn-end). Index aligns with OrbQueue when the slot is Dark; other
    /// kinds use 0. Used to value evoking dark orbs realistically.
    /// </summary>
    public IReadOnlyList<int> OrbEvokeValues { get; init; } = System.Array.Empty<int>();

    /// <summary>
    /// Deep clone for forward simulation. Records have an auto-generated Clone() that
    /// shallow-copies, so this name (DeepClone) avoids the conflict and also copies the
    /// List<> contents (each SimEnemy/SimCard cloned via record with-expression).
    /// </summary>
    public SimState DeepClone() => this with
    {
        Enemies = Enemies.Select(e => e with { }).ToList(),
        Hand = Hand.Select(c => c with { }).ToList(),
    };
}
