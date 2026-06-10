using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Lightweight projection of an enemy Creature for planner scoring.
/// v0.1.1 expands intent visibility: snapshotter classifies every intent on this enemy
/// and exposes per-category flags + an overall ThreatLevel. PlanScorer uses these to
/// prioritize kills (buff/heal/summon enemies first, defend/inert enemies last).
/// </summary>
internal sealed record SimEnemy
{
    public required int Hp { get; init; }
    public required int Block { get; init; }
    public required int IntentDamage { get; init; }
    public required int IntentRepeats { get; init; }
    // Encoder-parity field (added 2026-05-27): SimStateAdapter previously
    // emitted Hp for both current and max because SimEnemy didn't track
    // MaxHp. See docs/hybrid-boss-debug.md (sts2-combat-core repo).
    public int MaxHp { get; init; }
    // Nullable for testability — live snapshotter always provides a real Creature.
    public Creature? SourceRef { get; init; }

    // Intent-category flags (v0.1.1). Default false unless snapshotter sets it.
    public bool HasAttackIntent { get; init; }
    public bool HasDeathBlowIntent { get; init; }
    public bool HasBuffIntent { get; init; }
    public bool HasDebuffIntent { get; init; }
    public bool HasHealIntent { get; init; }
    public bool HasSummonIntent { get; init; }
    public bool HasDefendIntent { get; init; }
    public bool HasStatusIntent { get; init; }
    public bool IsInert { get; init; }     // stun / sleep / escape
    public bool IsHidden { get; init; }
    public bool IsUnknown { get; init; }

    public ThreatLevel Threat { get; init; } = ThreatLevel.None;

    // v0.2.4 — enemy status powers (affects our attack damage when we hit them).
    public int VulnerableAmount { get; init; }   // turns; >0 = takes +50% damage from us
    public int WeakAmount { get; init; }         // turns; >0 = their attacks -25%
    public int StrengthAmount { get; init; }     // adds to their attack damage

    // v0.2.9 — enemy resistance / defensive powers.
    public int ArtifactAmount { get; init; }     // blocks N next debuffs we apply
    public int FrailAmount { get; init; }        // their block gain ×0.75

    /// <summary>
    /// True when the enemy has a per-turn Strength gain (RitualPower style). These
    /// enemies snowball — every turn alive makes them harder to kill, so the planner
    /// should heavily prioritize killing them ASAP.
    /// </summary>
    public bool HasTurnStartStrengthBuff { get; init; }

    // v0.2.11 — DoT / character-specific debuff stacks.
    public int PoisonAmount { get; init; }    // turn-based damage applied to enemy
    public int ConstrictAmount { get; init; }
    public int BurnAmount { get; init; }

    /// <summary>
    /// v0.7.13 — Doom stacks on this enemy. Necrobinder's REAPER_FORM applies
    /// DoomPower on attack-hit; the stack ticks turn-start damage equal to the
    /// stack count (like Poison) without self-decrement. ApplyCardPlay grows
    /// the stack per hit when ReaperFormPower is active; AdvanceTurn ticks it.
    /// </summary>
    public int DoomAmount { get; init; }

    // v0.4 — damage / counter-attack powers that affect *our* attack decisions.
    /// <summary>
    /// Per-hit damage cap from IntangiblePower (1, or 5 with TheBoot) or HardToKillPower
    /// (Amount). 0 means no cap — uncapped. When >0, every hit we deal is clamped to this
    /// value, so multi-hit cards are *much* more valuable than burst single hits.
    /// </summary>
    public int DamageCapPerHit { get; init; }

    /// <summary>
    /// 2026-06-05 — SlipperyPower stack count, when modeled as a CONSUMABLE buffer
    /// (env STS2_SLIPPERY_CONSUME). Slippery caps the next N times the enemy loses HP
    /// to 1 each, then it's gone — unlike Intangible's permanent cap. When &gt;0, only the
    /// first SlipperyStacks hits are capped (DamageCapPerHit is also 1 so heuristic readers
    /// stay conservative); the state transition decrements this per hit and clears the cap
    /// once stripped, so the planner can value "many small hits → strip → burst". 0 = the
    /// legacy permanent-cap model (flag off) or a non-Slippery enemy.
    /// </summary>
    public int SlipperyStacks { get; init; }

    /// <summary>
    /// ThornsPower amount — every time we attack this enemy, we take this much
    /// HP loss in return. Multi-hit cards trigger once per hit.
    /// </summary>
    public int ThornsAmount { get; init; }

    /// <summary>
    /// Generic dump of every active power on this enemy (class-name → amount).
    /// Used as the fallback lookup when a specific named field isn't carved out above.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, int> Powers { get; init; }
        = new System.Collections.Generic.Dictionary<string, int>();

    /// <summary>
    /// v0.4 — HardenedShellPower's *remaining* per-turn damage budget (reads the live
    /// DisplayAmount = Amount − damageReceivedThisTurn). 0 means no shell or fully spent.
    /// When &gt; 0, every hit we deal is capped to this value, then the value drops by the
    /// applied damage. Effectively makes attacks worthless once the shell budget is gone.
    /// </summary>
    public int HardenedShellRemaining { get; init; }

    /// <summary>
    /// 2026-05-31 — SkittishPower has already granted its once/turn block this turn
    /// (its internal Data.HasGainedBlockThisTurn, read via reflection). When false and
    /// the enemy carries SkittishPower, the next card attack dealing unblocked damage
    /// makes it gain Amount block — the sim must add that to the post-attack block.
    /// </summary>
    public bool SkittishFiredThisTurn { get; init; }

    /// <summary>
    /// 2026-06-01 — SlowPower's DisplayAmount (= SlowAmount × 10, a percent). A Slow
    /// enemy takes 1 + 0.1×SlowAmount = 1 + DisplayAmount/100 times damage from card
    /// attacks (SlowAmount grows by 1 per card played this turn). Read live via
    /// GetPowerDisplayAmount; 0 when absent. The sim applies (1 + SlowDamagePct/100)
    /// to the per-enemy attack damage.
    /// </summary>
    public int SlowDamagePct { get; init; }

    /// <summary>
    /// v0.9 — SkittishPower (Phantasmal Gardener and similar). When this enemy
    /// takes unblocked damage from a CARD attack for the FIRST time each turn,
    /// it gains Amount block. Multi-hit cards trigger only on the first hit;
    /// subsequent hits deliver full damage. Visible as `Skittish:N` in
    /// snapshot pow string. 0 means absent or already triggered this turn.
    ///
    /// Implication for scoring: first attack on a Skittish enemy effectively
    /// loses up to Amount damage (eaten by reactive block). Single big-burst
    /// finishers beat chip-damage sequences when this is active.
    /// </summary>
    public int SkittishAmount { get; init; }

    /// <summary>
    /// v0.9 — Already-triggered flag for SkittishPower. When true, the enemy
    /// has already gained its Skittish block this turn; further attacks won't
    /// trigger more. Read from runtime Data.hasGainedBlockThisTurn (reflection)
    /// when available; defaults to false (over-conservative — assumes block
    /// will fire on next hit).
    /// </summary>
    public bool SkittishAlreadyTriggered { get; init; }

    /// <summary>
    /// v0.9 — CurlUpPower (Louse-style reactive block). When this enemy takes
    /// the FIRST damage from a player card, gains Amount block AND removes
    /// the power. Same effective shape as Skittish but ONE-SHOT for the
    /// combat (not per-turn).
    /// </summary>
    public int CurlUpAmount { get; init; }

    /// <summary>
    /// 2026-06-03 — SelfFormingClayPower (decompile AfterBlockCleared): when this enemy's
    /// block is fully cleared, it immediately gains Amount block AND removes the power
    /// (one-shot). Same effective shape as CurlUp — our damage that strips its block is
    /// partly re-absorbed once — so it subtracts from effective damage the same way.
    /// </summary>
    public int SelfFormingClayAmount { get; init; }

    /// <summary>
    /// v0.9 — ImbalancedPower (BowlbugRock-style self-stun). When this enemy's
    /// attack is FULLY BLOCKED by the player, the enemy stuns itself the
    /// following turn (skip attack). For player DEFEND scoring this gives an
    /// extra survival bonus when the block fully covers this specific enemy's
    /// intent damage.
    /// </summary>
    public int ImbalancedAmount { get; init; }

    /// <summary>
    /// 2026-06-03 — Actual per-turn-end Strength gain (sum of the decompile-verified
    /// +Amount/turn powers: RitualPower + TerritorialPower + HighVoltagePower). AdvanceTurn
    /// adds THIS each projected turn instead of a flat +1, so Amount&gt;1 escalators (e.g.
    /// Ritual:3) are no longer under-projected. 0 = no turn-end Strength power (the reactive
    /// Enrage/Feral approximations still fall back to +1 via HasTurnStartStrengthBuff).
    /// Replaces the previously dead TerritorialAmount field.
    /// </summary>
    public int TurnStartStrengthGain { get; init; }

    /// <summary>
    /// v0.9 — PaperCutsPower (Tier B). When this enemy deals unblocked damage
    /// to player, player loses Amount MaxHP per damage event. Long-term
    /// survival concern beyond block — should make us value full block even
    /// more (or kill this enemy faster). 0 = power absent.
    /// </summary>
    public int PaperCutsAmount { get; init; }

    /// <summary>
    /// 2026-06-03 — PainfulStabsPower (decompile AfterAttack): when this enemy lands an
    /// unblocked powered attack on the player, it shuffles Amount × (unblocked hits) Wound
    /// cards into the player's discard. Deck-pollution analog of PaperCuts — captured for
    /// parity / future block-priority scoring (PaperCuts is likewise capture-only today).
    /// </summary>
    public int PainfulStabsAmount { get; init; }

    /// <summary>
    /// 2026-06-04 — Per-turn self-heal an enemy with a heal intent restores if it SURVIVES the
    /// player's turn (e.g. WaterfallGiant Siphon = +15). The heal amount is hardcoded in the
    /// monster move and NOT exposed on the intent, so it can't be read generically — captured via
    /// a behavioral carve-out for known healers (default 0 = unknown / no projection). AdvanceTurn
    /// adds it back (capped at MaxHp) so the depth-2 lookahead reflects "chip below the heal rate
    /// makes no net progress → you must out-damage the heal to kill it." Only applies when the
    /// enemy is still alive after the turn (a lethal burst kills before the heal fires).
    /// </summary>
    public int HealAmount { get; init; }

    /// <summary>
    /// SandpitPower stack (The Insatiable). Decrements -1 at each enemy turn
    /// start. When this transitions from &gt;0 to 0 the power's AfterRemoved
    /// hook force-kills the player + pets + Osty, ignoring all revive
    /// mechanics (decompile sts2.decompiled.cs:318071-318104).
    ///
    /// Survival horizon hard-limit: planner MUST finish this carrier before
    /// the counter expires. Player can play FranticEscape (status card) to
    /// regain +1 stack but EnergyCost.AddThisCombat(+1) accumulates per use
    /// — a delaying tactic, not a solution.
    /// </summary>
    public int SandpitAmount { get; init; }

    /// <summary>
    /// v0.10 — On-death spawn count. When this enemy dies, Amount new
    /// monsters spawn in its place. InfestedPower (Phrog Parasite Elite)
    /// is the canonical source: spawns N Wrigglers (decompile
    /// sts2.decompiled.cs:315807 — InfestedPower.AfterDeath). Wrigglers
    /// alternate between Bite (~6 dmg attack) and Wriggle (adds 1 INFECTION
    /// to discard + self-buff +2 Strength). Combat does NOT end while
    /// InfestedPower exists (ShouldStopCombatFromEnding=true), so chip-
    /// killing the carrier just resets enemy HP totals upward AND poisons
    /// the deck with INFECTION cards over time.
    ///
    /// Planner uses this to penalize lethal-this-hit decisions on splitter
    /// carriers unless we have burst-window to clear the spawns too.
    /// 0 = no spawn-on-death, kill freely.
    /// </summary>
    public int OnDeathSpawnsCount { get; init; }

    // v0.1.2 — encounter-role classification (set by Snapshotter)
    public bool IsBoss { get; init; }      // boss-room top creature (highest HP in boss/elite encounter)
    public bool IsElite { get; init; }     // any creature in an elite encounter
    public bool IsMinion { get; init; }    // spawned this turn OR significantly weaker than max HP in fight

    /// <summary>
    /// Monster model type name (e.g. "Queen", "TestSubject") for the few carve-outs that need
    /// identity beyond powers — kill-order rules tied to a specific boss's state machine.
    /// Empty when the snapshotter can't resolve it.
    /// </summary>
    public string MonsterKey { get; init; } = "";

    public bool IsAlive => Hp > 0;
    public int TotalIntentDamage => IntentDamage * IntentRepeats;
    public int EffectiveHp => Hp + Block; // damage required to kill outright

    /// <summary>
    /// Compact log-friendly description of this enemy's intent state.
    /// </summary>
    public string IntentSummary
    {
        get
        {
            var tags = new System.Collections.Generic.List<string>();
            if (HasDeathBlowIntent) tags.Add("DeathBlow");
            if (HasAttackIntent) tags.Add($"Atk{TotalIntentDamage}");
            if (HasBuffIntent) tags.Add("Buff");
            if (HasHealIntent) tags.Add("Heal");
            if (HasSummonIntent) tags.Add("Summon");
            if (HasDebuffIntent) tags.Add("Debuff");
            if (HasDefendIntent) tags.Add("Defend");
            if (HasStatusIntent) tags.Add("Status");
            if (IsInert) tags.Add("Inert");
            if (IsHidden) tags.Add("Hidden");
            if (IsUnknown) tags.Add("Unknown");
            if (IsBoss) tags.Add("BOSS");
            else if (IsElite) tags.Add("ELITE");
            if (IsMinion) tags.Add("minion");
            if (OnDeathSpawnsCount > 0) tags.Add($"spawns{OnDeathSpawnsCount}");
            return tags.Count == 0 ? "?" : string.Join("+", tags);
        }
    }
}
