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

    /// <summary>
    /// v0.7.2 — Player character entry id (e.g. "IRONCLAD", "SILENT", "DEFECT",
    /// "WATCHER", "NECROBINDER", "REGENT"). Captured from <c>player.Character.Id.Entry</c>.
    /// Used by <see cref="Sts2CombatAI.Planner.PoolMeans"/> to look up the static
    /// value distribution of the active character's card pool — driving Level 4
    /// pool-based random card evaluation (WHITE_NOISE / DISCOVERY / CREATIVE_AI / …).
    /// Empty string if the snapshotter couldn't read it (test fixtures, mid-screen
    /// transitions) — handlers must fall back to the per-card-id flat magnitude.
    /// </summary>
    public string CharacterId { get; init; } = string.Empty;

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
    // Raw counts let EvaluateDrawCard gauge "is drawing fruitful?". Counts are
    // kept distinct from DrawPile / DiscardPile below so existing call sites
    // don't have to materialize the lists when only size matters.
    public int DrawPileSize { get; init; }
    public int DiscardPileSize { get; init; }

    /// <summary>
    /// v0.5.1 — Actual cards in the draw / discard piles. Used by the simulator's
    /// depth-2 lookahead to synthesize a representative "average draw" card based
    /// on real deck contents — replacing the fixed 5-damage placeholder so that
    /// drawing into a strong deck is valued higher than drawing into a weak one.
    /// In-game pile order is randomized; we don't predict which card surfaces next,
    /// only the mean effect of the combined pool (drawn + discarded → after reshuffle
    /// both are eligible to be drawn anyway). Empty when snapshotter couldn't read
    /// the pile (test fixtures, capture failures) — simulator falls back to the
    /// legacy placeholder in that case.
    /// </summary>
    public IReadOnlyList<SimCard> DrawPile { get; init; } = System.Array.Empty<SimCard>();
    public IReadOnlyList<SimCard> DiscardPile { get; init; } = System.Array.Empty<SimCard>();

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

    // v0.6.7 — Player-side mechanic stacks for fine-grained consumer/amplifier scoring.
    // Each field represents the "stack" a consumer/amplifier card scales on. Zero
    // means "mechanism absent or not yet active" — consumer cards then fall back to
    // BuildSynergy's flat in-hand-producer bonus instead.

    /// <summary>
    /// Soul token cards (CARD.SOUL) across hand + draw + discard. SOUL_CONSUMER /
    /// SOUL_AMPLIFIER cards (REAVE, SOUL_STORM, DEVOUR_LIFE, HAUNT) scale with the
    /// total count of soul cards present.
    /// </summary>
    public int SoulInPiles { get; init; }

    /// <summary>
    /// Shiv token cards (CARD.SHIV) across hand + draw + discard. SHIV_CONSUMER
    /// (KNIFE_TRAP) scales with the count; SHIV_AMPLIFIER powers (Accuracy / Fan
    /// of Knives) also benefit from a larger shiv pool.
    /// </summary>
    public int ShivInPiles { get; init; }

    /// <summary>
    /// Alive Osty (skeleton) ally creatures owned by the player. SKELETON_CONSUMER
    /// cards (BONE_SHARDS, PROTECTOR, SQUEEZE) are gated by skeleton presence —
    /// they typically read "if Osty is alive" and scale with skeleton stats.
    /// </summary>
    public int SkeletonCount { get; init; }

    /// <summary>
    /// v0.7.11 — Per-ally projection (Necrobinder skeletons + any other
    /// player-side summons). Replaces the bare SkeletonCount for damage-
    /// contribution accounting: each ally adds its declared intent damage to
    /// the player's outgoing-per-turn estimate, used by RemainingTurnsEstimator
    /// and AdvanceTurn. Empty when no allies / capture failure — handlers
    /// then fall back to SkeletonCount-only gating.
    /// </summary>
    public IReadOnlyList<SimAlly> Allies { get; init; } = System.Array.Empty<SimAlly>();

    /// <summary>
    /// v0.7.12 — All Power instances active on the player (name → stack count).
    /// Captured by StateSnapshotter from <c>creature.Powers</c> so handlers
    /// can detect persistent passives (DemonFormPower, RegenPower, BarricadePower,
    /// EchoFormPower, MachineLearningPower, etc.) without inflating SimState
    /// with one field per power.
    ///
    /// AdvanceTurn consults this for per-turn passive effects (Strength gain
    /// from DemonForm, HP regen from RegenPower, block-carry from Barricade).
    /// Explicit fields like PlayerStrength / PlayerFocus / PlayerIntangible
    /// remain primary — the dict catches the long-tail powers that don't have
    /// a dedicated field.
    /// </summary>
    public IReadOnlyDictionary<string, int> PlayerPowers { get; init; }
        = new Dictionary<string, int>();

    /// <summary>
    /// Cards in the Exhaust pile. EXHAUST_CONSUMER cards (PACTS_END, ASHEN_STRIKE,
    /// SOUL_STORM, EVIL_EYE, DARK_EMBRACE-as-Power-payoff) scale with the count
    /// of cards that have been exhausted this combat. Captured separately from
    /// Draw/Discard since exhausted cards don't return to play.
    /// </summary>
    public int ExhaustPileSize { get; init; }

    /// <summary>
    /// Number of SovereignBlade card instances across all piles (hand + draw +
    /// discard + exhaust). Proxy for FORGE / LORDS_BLADE state — when ≥ 1 the
    /// player has an active blade, and FORGE_AMPLIFIER / LORDS_BLADE_AMPLIFIER
    /// cards (HAMMER_TIME, CONQUEROR) have something to stack on.
    /// </summary>
    public int SovereignBladeCount { get; init; }

    // v0.6.8 — Turn / combat history counters from CombatHistory. Captured so
    // cards whose value scales on "what's already happened this turn / combat"
    // (RAGE: block per past attack; TEAR_ASUNDER: hits = 1 + HP-loss events
    // this combat) can be evaluated correctly without runtime forward-sim.

    /// <summary>
    /// Number of Attack cards the player has finished playing this turn.
    /// Computed from `CombatHistory.CardPlaysFinished` filtered by current
    /// round + side + CardType.Attack. STOMP-style cost-discount cards already
    /// reflect this via `EnergyCost.GetAmountToSpend()` so the counter mainly
    /// serves future passes (RAGE-power scaling, etc.).
    /// </summary>
    public int TurnAttacksPlayed { get; init; }

    /// <summary>
    /// Number of Skill cards the player has finished playing this turn. Used
    /// by PINPOINT-equivalent gates and any "play after N skills" payoff.
    /// </summary>
    public int TurnSkillsPlayed { get; init; }

    /// <summary>
    /// Number of times the player has taken unblocked damage during this
    /// combat (one entry per DamageReceived event with UnblockedDamage > 0).
    /// TEAR_ASUNDER scales hits with this — `Hits = 1 + this`.
    /// </summary>
    public int CombatPlayerHpLossEvents { get; init; }

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
