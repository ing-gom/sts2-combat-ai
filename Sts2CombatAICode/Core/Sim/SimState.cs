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
    // Encoder-parity fields (added 2026-05-27): SimStateAdapter previously
    // hardcoded 0 / PlayerHp / PlayerEnergy for these because SimState
    // didn't carry them, which caused the ONNX advisor in OnnxAdvisor.Recommend
    // to score a mis-encoded observation vs the Sts2CombatEnv layout the
    // model was trained on. See docs/hybrid-boss-debug.md in the
    // sts2-combat-core repo for the full diff and root-cause writeup.
    public int Turn { get; init; }
    public int PlayerMaxHp { get; init; }
    public int PlayerMaxEnergy { get; init; }
    public int ExhaustPileCount { get; init; }

    // 2026-06-11 — run position for the empirical HP value curve (PlanScorer
    // HpCurveMult): the marginal run-value of an HP point varies by act and
    // floor (mined from 35K fights), so HP-preservation blocking is weighted
    // by where the run currently is. 0 / -1 = unknown (flat-constant fallback).
    public int ActNumber { get; init; }   // 1-based act (0 = unknown)
    public int ActFloor { get; init; } = -1;

    /// <summary>
    /// v0.7.2 — Player character entry id. STS2 has 5 characters:
    /// IRONCLAD / SILENT / DEFECT / NECROBINDER / REGENT (no Watcher in STS2;
    /// some Watcher-named powers from STS1 — Burst / EchoForm / Stratagem —
    /// were reused for STS2 cards/potions). Captured from
    /// <c>player.Character.Id.Entry</c>.
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
    // v0.7.82 — VigorPower stack. Adds +N damage to the next attack played
    // this turn, then consumed (reset to 0). Unlike Strength which persists,
    // Vigor is one-shot.
    public int PlayerVigor { get; init; }
    // 2026-06-04 — multiplicative buff on the NEXT Attack card's damage (GIGANTIFICATION_POTION
    // = ×3). Set by AnalyticalSimulator.ApplyPotionUse, consumed (reset to 1) by ApplyCardPlay
    // when an Attack is played. Default 1 = no multiplier. Lets the potion lookahead value
    // "drink amplifier potion → play big attack".
    public int PlayerNextAttackMult { get; init; } = 1;
    // 2026-06-04 — DUPLICATOR (DuplicationPower): the NEXT card is played twice. Modeled as a ×N
    // multiplier on that one card's damage AND block (any card type), consumed after the next play.
    // Default 1 = no multiplier; guarded by >1 so unused state stays byte-identical.
    public int PlayerNextCardMult { get; init; } = 1;
    // 2026-06-04 — ThrowingAxe relic: the FIRST card played each COMBAT is played twice
    // (ModifyCardPlayCount +1, once/combat). Modeled as a ×2 on that one card's damage AND block,
    // consumed after the first card play. Captured from the live relic's Status (Active = not yet
    // used) so it isn't applied after it has already fired. Default false (no relic / already used).
    public bool PlayerThrowingAxeReady { get; init; }
    // 2026-06-04 — Vambrace relic: the FIRST block GAINED from a card each COMBAT is doubled
    // (ModifyBlockMultiplicative ×2, once/combat, self target). Captured from the live relic's
    // Status (Active = not yet triggered); consumed by the first block-granting card. Default false.
    public bool PlayerVambraceReady { get; init; }
    // 2026-06-04 — BrilliantScarf relic: the 5th card played each TURN costs 0 (TryModifyEnergyCost
    // when CardsPlayedThisTurn == 5-1 == 4). Captured as a one-shot "next card is free" flag when
    // the snapshot's TurnCardsPlayed == 4, consumed by the first continuation card. Default false.
    public bool PlayerBrilliantScarfReady { get; init; }
    // 2026-06-04 — UnsettlingLamp relic: the FIRST power-granting card each TURN has its power
    // amounts doubled (ModifyPowerAmountGiven ×2 for that card's powers). Captured from the live
    // relic's Status (Active = not yet triggered this turn); consumed by the first power card,
    // reset at turn start. Default false.
    public bool PlayerUnsettlingLampReady { get; init; }
    // 2026-06-04 — multiplicative buff on card block THIS turn (ShadowmeldPower = ×2^Amount,
    // removed at turn end). Captured by StateSnapshotter (2^stacks), applied to card block in
    // ApplyCardPlay + valued in PlanScorer, reset to 1 in AdvanceTurn. Default 1 = no multiplier.
    public int PlayerBlockMult { get; init; } = 1;
    // 2026-06-04 — VoidFormPower: the first Amount cards each turn cost 0 (energy AND stars).
    // Captured as the remaining free-card budget (Amount − cards played this turn); a card is
    // free while this is > 0. 0 = none.
    public int PlayerFreeCardBudget { get; init; }
    // 2026-06-04 — VoidFormPower's full Amount (free cards PER turn), so AdvanceTurn can refresh
    // the budget for the next turn instead of carrying the depleted remainder. 0 = no VoidForm.
    public int PlayerVoidFormAmount { get; init; }
    // 2026-06-04 — VeilpiercerPower stacks: each makes the next ETHEREAL card cost 0.
    public int PlayerVeilpiercer { get; init; }
    // 2026-06-04 — FastenPower amount: +N block to CardTag.Defend cards (matched precisely via
    // SimCard.IsDefendTagged, captured from the live CardModel). 0 = none.
    public int PlayerFasten { get; init; }
    // v0.7.83 — BufferPower stack. Each stack negates ONE incoming damage
    // instance (entire hit, regardless of size). Decremented per instance
    // negated. Critical for survival projection: a 30-damage enemy hit with
    // Buffer 1 deals 0 damage. Without modelling this the planner over-defends
    // on Watcher / certain build turns.
    public int PlayerBuffer { get; init; }
    // v0.7.84 — Damage multiplier powers. None modify Strength; all apply to
    // the final per-attack damage as conditional multipliers.
    //   • PlayerLethality > 0 → first attack/turn × 1.5 (Silent Volatile).
    //   • PlayerTracking  > 0 → vs Weak target × 2 (Silent).
    //   • PlayerCruelty   > 0 → vs Vulnerable target × 1.25 (Silent).
    // Lethality is single-shot (first attack only); Tracking/Cruelty are
    // permanent passives that apply every attack.
    public int PlayerLethality { get; init; }
    public int PlayerTracking { get; init; }
    public int PlayerCruelty { get; init; }
    // v0.7.85 — Block-side reactive / multiplier powers.
    //   • PlayerRage      > 0 → +N block per attack played.
    //   • PlayerAfterimage> 0 → +N block per card played.
    //   • PlayerUnmovable > 0 → first block card/turn × 2 (single-shot per turn).
    // Unmovable consumption tracked via UnmovableUsedThisTurn flag; reset by
    // AdvanceTurn.
    public int PlayerRage { get; init; }
    public int PlayerAfterimage { get; init; }
    public int PlayerUnmovable { get; init; }
    public bool UnmovableUsedThisTurn { get; init; }
    // v0.7.86 — AccuracyPower (Silent passive): each Shiv card deals +N extra
    // damage. Applied to cards whose id matches "SHIV" (base) — the only Shiv
    // card kind in STS2's catalog.
    public int PlayerAccuracy { get; init; }
    // v0.7.94 — EnragePower (Ironclad reactive): each Skill played grants
    // Strength +N. Stack persists across turns until removed.
    public int PlayerEnrage { get; init; }
    // 2026-05-29 — MonologuePower (Regent): +stack Strength after EVERY card
    // play (this-turn-only). Carried so depth-N lookahead accumulates the
    // per-play Strength ramp that drives later attacks' damage.
    public int PlayerMonologue { get; init; }
    // v0.7.94 — CorruptionPower: all Skill cards cost 0 (combat-wide). Tracked
    // here in addition to PlayerPowers so depth-N lookahead can see the cost-0
    // effect after a Power card grants Corruption mid-turn.
    public int PlayerCorruption { get; init; }
    // v0.7.95 — BurstPower: next Skill plays its effect twice. Each Skill play
    // consumes 1 stack. Effects affected: block, applied debuffs/buffs, draw,
    // energy gain — anything that comes from the Skill's onPlay resolution.
    public int PlayerBurst { get; init; }
    // v0.7.96 — Player ThornsPower: reflects N damage to attacker per hit
    // received. Modeled in PredictPlayerDmg / PredictRawLeak to early-kill
    // low-HP enemies and cut their remaining hits from the leak total.
    // (Card-side thorns reflection on attacking thorn enemies is handled
    // separately by SimEnemy.ThornsAmount in AnalyticalSimulator.)
    public int PlayerThorns { get; init; }
    // v0.7.97 — FeelNoPainPower: when player exhausts a card (Exhaust keyword
    // OR force-exhaust path), gain N block. Reactive trigger on card resolve.
    public int PlayerFeelNoPain { get; init; }
    // v0.7.98 — EchoFormPower (remaining echoes this turn). Initialized at
    // snapshot as max(EchoFormStack - cardsPlayedThisTurn, 0). Each card play
    // that resolves while > 0 plays its effect TWICE (canonical STS: first N
    // cards/turn copy). Decremented per card play. Type-agnostic (attacks,
    // skills, powers all echo).
    public int PlayerEchoForm { get; init; }
    // v0.7.99 — JuggernautPower (Ironclad reactive): each block-gain event
    // deals N damage to a (random) enemy. Modeled in simulator as damage to
    // weakest alive enemy after every block-gain in card resolution.
    public int PlayerJuggernaut { get; init; }
    // v0.7.99 — HungerPower (cross-character reactive): each card drawn this
    // turn grants Strength +N. Simulator applies on card.DrawCount; PlanScorer
    // credits draw cards.
    public int PlayerHunger { get; init; }
    // v0.8.0 — FlameBarrierPower: 1-turn reflect-on-receive (functionally
    // equivalent to ThornsPower for the duration). Combined with PlayerThorns
    // in PredictPlayerDmg / PredictRawLeak so enemies die mid-attack-sequence
    // to the combined reflect.
    public int PlayerFlameBarrier { get; init; }
    // v0.8.1 — DanseMacabrePower (Necrobinder): when player plays a card with
    // cost ≥ 2, gain N block. STS2 mechanic per cards_catalog.json (NOT the
    // STS1 Shiv-generation interpretation).
    public int PlayerDanseMacabre { get; init; }
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

    /// <summary>
    /// v0.10 — Sum of GalvanicPower.Amount across all creatures sourcing it
    /// (typically a galvanic enemy). Each Galvanized Power card play deals
    /// this much damage to the player (block-absorbed via ValueProp.Unpowered).
    /// PlanScorer Power branch reads this together with <see cref="SimCard.IsGalvanized"/>
    /// to subtract expected HP loss from the Power play's score.
    /// </summary>
    public int GalvanicAmount { get; init; }

    /// <summary>
    /// v0.7.21 — DoomPower stack on the player (Necrobinder self-doom).
    /// Each turn ticks Doom × N damage to the player (mirrors enemy
    /// DoomAmount). DOOM_SELF axis cards add stacks; high cumulative Doom
    /// is an existential risk. Captured by StateSnapshotter from
    /// <c>player.Powers.DoomPower</c>; 0 when not playing Necrobinder or
    /// no Doom applied.
    /// </summary>
    public int PlayerDoom { get; init; }

    /// <summary>
    /// v0.7.35 — Player-side DoT stacks. Visible current-state — when > 0, the
    /// player will lose HP at turn-end / start equal to the stack count. Folded
    /// into PredictPlayerDmg and SurvivalUrgency so the planner accounts for
    /// inevitable HP loss before deciding to attack vs block.
    ///
    ///   Poison  — ticks at start of OUR turn; affects next-turn survival but
    ///             also accounted for in this turn's decision so we don't
    ///             attack into a state where Poison kills us next turn anyway.
    ///   Burn    — ticks at end of OUR turn; affects this-turn survival.
    ///   Constrict — ticks at start of enemy turn; small HP loss before block.
    /// </summary>
    public int PlayerPoison { get; init; }
    public int PlayerBurn { get; init; }
    public int PlayerConstrict { get; init; }

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
    // 2026-06-08 — KaiserCrab BackAttack. SurroundedPower facing: 0 = none (not a crab fight),
    // 1 = facing Left (Crusher faced → Rocket back-attacks ×1.5), 2 = facing Right (Rocket faced →
    // Crusher back-attacks ×1.5). The arm NOT faced (= not last-targeted) deals ×1.5. Updated in
    // AnalyticalSimulator when the player targets a BackAttack arm; PredictPlayerDmg applies the ×1.5.
    public int PlayerFacing { get; init; }
    // 2026-05-29 — ThunderPower (Defect): +Amount damage (Unpowered/flat) each
    // time a Lightning orb is evoked (ThunderPower.AfterOrbEvoked). Boosts every
    // lightning evoke this combat; the THUNDER card applies it.
    public int PlayerThunder { get; init; }

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
    /// 2026-06-02 — BULLET_TIME: all non-X-cost hand cards cost 0 for the REST of
    /// this turn (game calls SetToFreeThisTurn on every hand card). Set true when
    /// BULLET_TIME is played, consumed by ApplyCardPlay's freeApplied path so the
    /// depth-N lookahead sees the freed hand (and the planner therefore values
    /// BULLET_TIME by the whole hand it unlocks). Reset to false at turn boundary.
    /// </summary>
    public bool PlayerFreeHandThisTurn { get; init; }

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
    /// 2026-05-30 — count of star-cost cards across all piles (CanonicalStarCost
    /// >= 0 or HasStarCostX). CRESCENT_SPEAR's CalculatedDamage = 6 + 2 × this.
    /// </summary>
    public int StarCardsInDeck { get; init; }

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

    /// 2026-05-31 — total cards played this turn (any type), for FTL's PlayMax gate
    /// (FTL draws 1 only if cards-played-this-turn &lt; PlayMax(3)).
    public int TurnCardsPlayed { get; init; }

    /// <summary>
    /// Number of Skill cards the player has finished playing this turn. Used
    /// by PINPOINT-equivalent gates and any "play after N skills" payoff.
    /// </summary>
    public int TurnSkillsPlayed { get; init; }
    /// <summary>2026-06-04 — Power cards played this turn. Added for RainbowRing (gain 1 Str + 1 Dex
    /// once per turn after playing an Attack, a Skill, AND a Power).</summary>
    public int TurnPowersPlayed { get; init; }

    /// <summary>
    /// 2026-05-31 — copies of MAKE_IT_SO (Regent) currently sitting in the draw /
    /// discard pile (NOT hand). MAKE_IT_SO.AfterCardPlayedLate returns itself to
    /// hand when the owner plays a Skill and (skills-played-this-turn % 3 == 0),
    /// but only while it is outside the hand. The pile-reactive return is a
    /// count-only mechanic the sim models from these snapshot counts.
    /// </summary>
    public int MakeItSoInDraw { get; init; }
    public int MakeItSoInDiscard { get; init; }

    /// <summary>
    /// 2026-06-01 — did the player apply DoomPower to anything this turn (combat
    /// History PowerReceivedEntry, DoomPower, Applier == player)? DEATHS_DOOR gains
    /// block (1 + Repeat) times instead of once when this is true.
    /// </summary>
    public bool PlayerDoomAppliedThisTurn { get; init; }

    /// <summary>
    /// 2026-06-01 — did the player already gain block from a CARD this turn (combat
    /// History BlockGainedEntry with a CardPlay, Actor == player)? UnmovablePower
    /// doubles only the FIRST block-gain card of the turn; when this is true the sim
    /// must NOT apply the Unmovable ×2 to a later block card.
    /// </summary>
    public bool PlayerBlockGainedFromCardThisTurn { get; init; }

    /// <summary>
    /// 2026-05-31 — JugglingPower's internal attacksPlayedThisTurn counter (the
    /// engine's own field, read via reflection). Juggling adds Amount clones of the
    /// played attack to hand when this counter, incremented on each attack, hits 3.
    /// -1 when Juggling is absent. The sim's turn-start TurnAttacksPlayed does NOT
    /// align (the counter can be seeded at mid-turn application), so the real field
    /// is captured directly.
    /// </summary>
    public int PlayerJugglingCounter { get; init; } = -1;

    /// <summary>
    /// 2026-05-30 — Shivs finished this turn (subset of TurnAttacksPlayed).
    /// PhantomBladesPower adds +Amount only to the FIRST shiv each turn, i.e.
    /// when this count is 0 at the moment the shiv resolves.
    /// </summary>
    public int TurnShivsPlayed { get; init; }

    /// <summary>
    /// 2026-05-30 — PhantomBladesPower amount: +N damage to the first shiv played
    /// each turn (ValueProp powered-attack shivs only). Applied in the shiv damage
    /// path when TurnShivsPlayed == 0.
    /// </summary>
    public int PlayerPhantomBlades { get; init; }

    /// <summary>
    /// 2026-05-30 — ChildOfTheStarsPower (Regent): AfterStarsSpent(N) gains
    /// Amount×N block (flat, Unpowered). Applied when a star-cost card is played
    /// (stars spent == the card's star cost).
    /// </summary>
    public int PlayerChildOfTheStars { get; init; }

    /// <summary>
    /// 2026-05-30 — ParryPower (Regent): AfterSovereignBladePlayed gains Amount
    /// block (flat, Unpowered). Triggers only when SOVEREIGN_BLADE is played.
    /// </summary>
    public int PlayerParry { get; init; }

    /// <summary>
    /// 2026-05-30 — InfernoPower (Ironclad): AfterDamageReceived deals Amount to
    /// ALL hittable enemies whenever the player takes unblocked damage on its own
    /// turn. In the card-play window this fires on self-HP-loss cards (HpLoss),
    /// so an HP_LOSS play with Inferno>0 adds Amount unblockable damage per enemy.
    /// </summary>
    public int PlayerInferno { get; init; }

    /// <summary>
    /// 2026-05-31 — HauntPower (Necrobinder, on the player): AfterCardPlayed, when
    /// the played card is a Soul, deals Amount UNBLOCKABLE damage to one random
    /// hittable enemy. Surfaces cleanly on SOUL (a 0-damage draw token) as a flat
    /// enemy_hp under-deal of +Amount. Applied per SOUL play in AnalyticalSimulator.
    /// </summary>
    public int PlayerHaunt { get; init; }

    /// 2026-05-31 — SmokestackPower (Defect): deals Amount to ALL enemies whenever the
    /// player generates a Status card. Pulsed per status produced in the status-gen path.
    public int PlayerSmokestack { get; init; }

    /// 2026-05-31 — PillarOfCreationPower (Regent): gain Amount block per card the
    /// player generates (AfterCardGeneratedForCombat, any pile, Unpowered/flat).
    public int PlayerPillar { get; init; }

    /// 2026-05-31 — ArsenalPower (Regent): gain Amount Strength per card the player
    /// generates (AfterCardGeneratedForCombat). Strength sibling of PillarOfCreation.
    public int PlayerArsenal { get; init; }

    /// 2026-05-31 — ShroudPower (Necro): gain Amount block whenever the player applies
    /// DoomPower. Doom-applying cards with Shroud add Shroud block per Doom applied.
    public int PlayerShroud { get; init; }

    /// 2026-05-31 — PanachePower (Regent): Amount AOE to all enemies every 5th card
    /// played. PanacheCardsLeft is the live DisplayAmount (5→0 countdown); a play that
    /// takes it to 0 (CardsLeft==1 pre-play) fires the pulse, then it resets to 5.
    public int PlayerPanache { get; init; }
    public int PanacheCardsLeft { get; init; }

    /// <summary>
    /// 2026-05-30 — BlackHolePower (Regent): deals Amount to ALL enemies whenever
    /// the player gains stars (AfterStarsGained) or spends stars (AfterCardPlayed
    /// StarsSpent>0). Applied once per star-gain and once per star-spend play.
    /// </summary>
    public int PlayerBlackHole { get; init; }

    /// <summary>
    /// 2026-05-30 — SleightOfFleshPower (Necrobinder): deals Amount to an enemy each
    /// time the player applies a non-temporary DEBUFF (Vulnerable/Weak/Frail/…) to it.
    /// Applied once per enemy-debuff PowerApp on the affected enemy(ies).
    /// </summary>
    public int PlayerSleightOfFlesh { get; init; }

    /// <summary>
    /// 2026-05-30 — combat-wide count of cards the player has GENERATED
    /// (CardGeneratedEntry). SUPERMASSIVE's CalculatedDamage = CalculationBase +
    /// ExtraDamage × this count.
    /// </summary>
    public int CombatCardsGenerated { get; init; }

    /// <summary>
    /// 2026-05-30 — the player's Osty (skeleton) current HP. UNLEASH's
    /// CalculatedDamage = CalculationBase + ExtraDamage × osty.CurrentHp. 0 when
    /// no Osty is summoned.
    /// </summary>
    public int PlayerOstyHp { get; init; }

    /// <summary>
    /// 2026-05-30 — OrbitPower (Regent) refund amount (energy per /4 boundary) and
    /// the current cumulative-energy-spent mod 4 (from DisplayAmount = 4 − mod).
    /// On a play that spends `cost`, refund = PlayerOrbit × ⌊(PlayerOrbitSpentMod +
    /// cost) / 4⌋. 0 when Orbit absent.
    /// </summary>
    public int PlayerOrbit { get; init; }
    public int PlayerOrbitSpentMod { get; init; }

    /// <summary>
    /// v0.9.2 — Total energy spent by this player this turn, summed from
    /// CombatHistory's EnergySpentEntry entries. HELIX_DRILL's CalculatedHits
    /// multiplier is (energy-spent-this-turn − self.EnergyCost) at OnPlay;
    /// PlanScorer's EstimateVariableHits subtracts the self cost when needed.
    /// </summary>
    public int TurnEnergySpent { get; init; }

    /// <summary>
    /// v0.9.2 — Stars *gained* by this player this turn (positive-delta
    /// StarsModifiedEntry amounts summed). RADIATE's CalculatedHits multiplier
    /// reads positive StarsModifiedEntry amounts only, so "stars used" loses
    /// the underlying signal — what matters is the inflow this turn.
    /// </summary>
    public int TurnStarsGained { get; init; }

    /// <summary>
    /// v0.9.2 — Number of Ethereal card plays finished by this player this
    /// combat (no turn filter — PULL_FROM_BELOW's multiplier walks the whole
    /// CombatHistory.Entries with no HappenedThisTurn gate).
    /// </summary>
    public int CombatEtherealPlayed { get; init; }

    /// <summary>
    /// v0.9.2 — Attacks by this player's Osty (Necrobinder skeleton) this
    /// turn, from CreatureAttackedEntry where Actor == player.Osty. RATTLE's
    /// CalculatedHits = 1 + this count (self play counts as the +1 base).
    /// 0 when no Osty summoned, when Osty is dead, or for non-Necrobinder
    /// runs.
    /// </summary>
    public int TurnOstyAttacks { get; init; }

    /// <summary>
    /// v0.9.2 — Status cards (CardType.Status — Wound / Slime / Burn / Void /
    /// Smog) currently owned by the player across all piles except Exhaust.
    /// FLAK_CANNON's CalculatedHits = GetStatuses(card.Owner).Count() — this
    /// is a snapshot count, not a turn/combat history walk.
    /// </summary>
    public int PlayerStatusCardCount { get; init; }

    /// <summary>
    /// v0.10 — Total self-damage the player will take at THIS turn's end
    /// from cards currently sitting in hand with OnTurnEndInHand effects.
    /// Sum of <see cref="SimCard.TurnEndInHandSelfDamage"/> across the
    /// Hand pile. INFECTION × N → 3N self-dmg this turn.
    ///
    /// The damage goes through player block (canonical CreatureCmd.Damage
    /// — NOT HpLoss), so it absorbs from <see cref="PlayerBlock"/> first.
    /// <see cref="EnemyTurnSimulator.PredictPlayerDmg"/> folds it in
    /// alongside enemy attack instances so survival projection accounts
    /// for the pile-up. Without this the planner thinks an INFECTION-
    /// loaded hand is "safe" because no enemy intent reflects the bleed.
    /// </summary>
    public int PlayerHandTurnEndDamage { get; init; }

    /// <summary>
    /// v0.9.7 — Number of cards drawn by the player this turn (CardDrawnEntry
    /// with !FromHandDraw — i.e. true draws from draw pile, excluding the
    /// hand→pile→hand re-draw cycle). DEATH_MARCH's CalculatedDamage multiplier
    /// reads exactly this count (decompile sts2.decompiled.cs:349298).
    /// </summary>
    public int TurnCardsDrawn { get; init; }

    /// <summary>
    /// 2026-06-04 — MURDER: damage = base 1 + total cards DRAWN this COMBAT by the player
    /// (decompile: CardDrawnEntry count, no turn/hand filter). Distinct from TurnCardsDrawn.
    /// </summary>
    public int CombatCardsDrawn { get; init; }

    /// <summary>
    /// 2026-06-04 — SOUL_STORM: damage = base 9 + 2 × Souls in the Exhaust pile (decompile:
    /// ExhaustPile.Cards.Count(c is Soul)).
    /// </summary>
    public int PlayerSoulsInExhaust { get; init; }

    /// <summary>
    /// v0.9.7 — Snapshot count of star-cost cards owned by the player across
    /// all piles (matches Regent's CRESCENT_SPEAR multiplier: cards where
    /// CanonicalStarCost >= 0 OR HasStarCostX, decompile :348904). Includes
    /// the card itself if it has a star cost. No turn/combat history walk.
    /// </summary>
    public int PlayerStarCostCardCount { get; init; }

    /// <summary>
    /// v0.9.7 — Snapshot count of player's cards tagged CardTag.OstyAttack
    /// (Necrobinder Osty-attack family). SQUEEZE's CalculatedDamage multiplier
    /// counts these excluding the SQUEEZE card itself (decompile :360306).
    /// Includes the SQUEEZE card in this raw count; subtract 1 at scoring
    /// time when card.Id == "SQUEEZE".
    /// </summary>
    public int PlayerOstyAttackCardCount { get; init; }

    /// <summary>
    /// v0.9 — Per-target attack count this turn. Keyed by SimEnemy index
    /// (position in <see cref="Enemies"/> list at snapshot time). Powers
    /// BEAT_INTO_SHAPE's "Forge +5 per OTHER attack on this target this turn"
    /// in the simulator: when BEAT plays on enemy idx N, dynamic forgeAmount
    /// = baseForge + extraForge × TurnAttacksByTargetIdx[N].
    ///
    /// Initial value comes from <see cref="StateSnapshotter"/> walking
    /// CombatHistory; <see cref="AnalyticalSimulator.ApplyCardPlay"/>
    /// increments the entry for the played card's target so depth-2+
    /// lookahead chains see the accumulating context.
    /// </summary>
    public IReadOnlyDictionary<int, int> TurnAttacksByTargetIdx { get; init; }
        = new Dictionary<int, int>();

    /// <summary>
    /// 2026-05-28 S6-3a: tracks whether the player has lost HP this turn.
    /// SPITE checks this via `LostHpThisTurn(base.Owner.Creature)`:
    ///   - true: hits = Repeat (2 base / 3 upgraded)
    ///   - false: hits = 1
    /// Set by StateSnapshotter from CombatManager.History (DamageReceivedEntry
    /// with Receiver=player and HappenedThisTurn). AnalyticalSimulator flips
    /// it when a played card causes player HP loss (HpLossAmount > 0 or
    /// enemy reactive damage exceeds player block).
    /// </summary>
    public bool PlayerLostHpThisTurn { get; init; }

    /// <summary>
    /// 2026-05-28 S6-4: tracks whether any player card was exhausted this turn.
    /// FORGOTTEN_RITUAL gains 3 energy if true (WasCardExhaustedThisTurn). Also
    /// relevant to other 소멸-trigger cards.
    /// </summary>
    public bool PlayerCardExhaustedThisTurn { get; init; }

    /// <summary>
    /// v0.9 — Enemy-applied shutdown debuffs on the player. Each makes a
    /// whole class of player action useless THIS turn:
    ///   • PlayerNoBlock  — DEFEND/block gain is suppressed (DEFEND scoring
    ///                      should drop to ~0)
    ///   • PlayerNoDraw   — draw cards skip (FOREGONE, SKIM, etc. wasted)
    ///   • PlayerNoEnergyGain — next-turn energy reset is reduced/skipped
    ///                      (long-fight tempo loss)
    /// Captured by StateSnapshotter from enemy-applied powers on the player
    /// creature. PlanScorer suppresses DEFEND threatBonus / draw-card bonuses
    /// when these are active.
    /// </summary>
    public int PlayerNoBlock { get; init; }
    public int PlayerNoDraw { get; init; }
    public int PlayerNoEnergyGain { get; init; }

    /// <summary>
    /// v0.9 — ConfusedPower (cards have random energy costs each turn while
    /// active). Player can't reliably plan card sequences — efficiency
    /// ranking still makes sense but specific cost-dependent combos break.
    /// When active, add a flat penalty to high-cost cards (their realised
    /// cost might be different than printed) and increase the value of
    /// 0-cost / cost-1 cards (less affected).
    /// </summary>
    public int PlayerConfused { get; init; }

    /// <summary>
    /// v0.9 — TenderPower (each card played by player → -1 Strength &amp; -1
    /// Dexterity temporarily, restored at turn end). Net effect: each card
    /// played progressively weakens subsequent attacks/blocks this turn.
    /// Captures the per-stack amount; effective per-card degradation is
    /// scaled in scoring.
    /// </summary>
    public int PlayerTender { get; init; }

    /// <summary>
    /// v0.9 — ShrinkPower (Tier B). Outgoing damage × (100-N)/100. Default
    /// applied amount is 30 (= 30% damage reduction). Stored as the percent
    /// reduction directly; EffectiveDmgPerEnergy applies (100-pct)/100.
    /// </summary>
    public int PlayerShrinkPercent { get; init; }

    /// <summary>
    /// v0.9 — DebilitatePower (Tier B). When player has this debuff, the
    /// Vulnerable and Weak multipliers AGAINST the player are amplified:
    /// incoming-Vuln 1.5→2.0 (target=player), outgoing-Weak 0.75→0.50
    /// (dealer=player). Effectively turns Vuln/Weak debuffs into bigger
    /// damage swings. 0 = power absent.
    /// </summary>
    public int PlayerDebilitate { get; init; }

    /// <summary>
    /// v0.9 — ChainsOfBindingPower per-turn flag: true once a Bound card has
    /// been played THIS turn, blocking any further Bound cards (decompile
    /// sts2.decompiled.cs:313044 — ShouldPlay returns false when set).
    /// Reset to false at turn-end (BeforeTurnEnd).
    /// </summary>
    public bool BoundCardPlayedThisTurn { get; init; }

    /// <summary>
    /// SmoggyPower stack on the player (LivingFog's AdvancedGasMove applies
    /// 1 stack). When &gt;0, playing any Skill afflicts every OTHER
    /// unafflicted Skill in deck/hand/discard with Smog → only the first
    /// Skill per turn lands; the rest become unplayable. StackType is
    /// Single — no per-turn decrement; persists until dispelled.
    /// Decompile: sts2.decompiled.cs:318734.
    /// </summary>
    public int PlayerSmoggy { get; init; }

    /// <summary>
    /// Per-turn flag: true once a Skill has been played while
    /// <see cref="PlayerSmoggy"/> &gt; 0, blocking any further Skill plays
    /// this turn (game's SmoggyPower.AfterCardPlayed applies Smog affliction
    /// to all remaining unafflicted Skills). Reset to false at turn-end
    /// (game's SmoggyPower.AfterTurnEnd clears all Smog afflictions).
    /// </summary>
    public bool SmoggySkillPlayedThisTurn { get; init; }

    /// <summary>
    /// Number of times the player has taken unblocked damage during this
    /// combat (one entry per DamageReceived event with UnblockedDamage > 0).
    /// TEAR_ASUNDER scales hits with this — `Hits = 1 + this`.
    /// </summary>
    public int CombatPlayerHpLossEvents { get; init; }

    /// <summary>
    /// v0.10 — Active relics on the player (RelicModel subclass name → counter).
    /// Captured by <see cref="StateSnapshotter"/> via
    /// <c>CombatReflection.GetPlayerRelics</c>. Counter is meaningful for
    /// trigger-counter relics (PenNib: AttacksPlayed%10, IronClub: CardsPlayed%4,
    /// VelvetChoker: cardsPlayedThisTurn); for passive relics with no counter
    /// (Anchor, Vajra, BronzeScales, …) the value is 1.
    ///
    /// Presence is checked via <c>ContainsKey</c>; absence means the relic is
    /// not owned (or is melted). <see cref="Planner.RelicCatalog"/> maps class
    /// names to flat scoring values and special-case handlers.
    ///
    /// Empty when the snapshotter couldn't read the relic list (test fixtures,
    /// mid-screen transitions) — handlers fall back to "no relic" behavior.
    /// </summary>
    public IReadOnlyDictionary<string, int> PlayerRelics { get; init; }
        = new Dictionary<string, int>();

    /// <summary>
    /// 2026-06-04 — Held potions, captured by <see cref="StateSnapshotter"/> from the live
    /// <c>Player.Potions</c>. Potions are first-class lookahead actions:
    /// <see cref="AnalyticalSimulator.ApplyPotionUse"/> applies one and removes it from this
    /// list, so depth-N can evaluate card→potion sequences. Empty when none held / unreadable.
    /// </summary>
    public IReadOnlyList<SimPotion> PlayerPotions { get; init; }
        = new List<SimPotion>();

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
