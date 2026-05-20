using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Effect-axis-driven priority adjustments for non-Power cards. Captures the
/// "play X before Y" order that the effect rules imply but the generic scorer
/// doesn't otherwise see.
///
/// Coverage (from card_triggers.json):
///   • DAMAGE_AMPLIFIER  — Aggression, Conflagration, Flanking, Knockdown,
///                         Lethality, Shadow Step, Sword Sage. Wants to play
///                         BEFORE remaining attacks so they hit harder.
///   • BLOCK_AMPLIFIER   — Entrench / Pillar of Creation / Unmovable. Doubles
///                         existing block; rewarded by current block + remaining
///                         block skills. (Power-card amplifiers — Barricade /
///                         Blur / Danse / Shadowmeld — are tier-handled.)
///   • VULN_AMPLIFIER    — Bully / Colossus / Cruelty / Debilitate / Dismantle /
///                         Dominate / Molten Fist. Wants enemy already Vuln OR
///                         a Vuln-applier in hand to play first.
///   • WEAK_AMPLIFIER    — Debilitate / Tracking. Same pattern, smaller weights
///                         (Weak doesn't compound with our attacks).
///   • BLOCK_PAYOFF      — Body Slam (dmg = block). Wants block already on the
///                         board; heavy penalty if played pre-block-setup.
///   • HP_LOSS_CONSUMER  — Inferno / Tear Asunder. Value scales with HP missing;
///                         bigger payoff at low HP.
///
/// Skips Power cards (they go through PowerSequencingTier instead). Stacks with
/// HandSynergy / BuildSynergy — those handle Strength / Dex / Vuln-as-power /
/// build-tagged producer-side bonuses, this file picks up the amplifier side and
/// the state-dependent payoffs they don't cover.
/// </summary>
internal static class EffectSynergy
{
    public static (int bonus, string detail) Compute(SimCard card, int targetIdx, SimState state)
    {
        if (card.IsPower || card.Axes.Count == 0) return (0, "");

        int b = 0;
        var parts = new List<string>();
        var axes = card.Axes;

        if (axes.Contains("DAMAGE_AMPLIFIER"))
            ApplyDamageAmplifier(card, state, ref b, parts);

        if (axes.Contains("BLOCK_AMPLIFIER"))
            ApplyBlockAmplifier(card, state, ref b, parts);

        if (axes.Contains("VULN_AMPLIFIER"))
            ApplyVulnAmplifier(card, targetIdx, state, ref b, parts);

        if (axes.Contains("WEAK_AMPLIFIER"))
            ApplyWeakAmplifier(card, state, ref b, parts);

        if (axes.Contains("BLOCK_PAYOFF"))
            ApplyBlockPayoff(card, state, ref b, parts);

        if (axes.Contains("HP_LOSS_CONSUMER"))
            ApplyHpLossConsumer(state, ref b, parts);

        // v0.7.37 — Self-harm trigger preview. When this card causes player HP
        // loss (HpLossAmount > 0) AND the player has Inferno/Combust-type
        // passives on, fire the trigger preview as a bonus.
        // Visible deterministic effect, not future-sim.
        if (card.HpLossAmount > 0 || axes.Contains("HP_LOSS_SELF") || axes.Contains("HP_LOSS"))
            ApplySelfHarmTriggerPreview(card, state, ref b, parts);

        // STRENGTH_DOWN — applies StrengthLoss to enemy(ies). Reduces enemy
        // attack damage on subsequent attacks; per-hit savings compound on
        // multi-hit intent. Scoring mirrors WEAK_AMPLIFIER but uses the
        // explicit StrengthLoss amount (e.g. DARK_SHACKLES -9, PIERCING_WAIL
        // AOE -6) for a direct damage-reduction estimate.
        if (axes.Contains("STRENGTH_DOWN"))
            ApplyStrengthDown(card, targetIdx, state, ref b, parts);

        // HEAL — restores HP this turn. Value scales inversely with current HP:
        // healing at full HP is wasted, healing at low HP saves a future death.
        // Mirrors HP_LOSS_CONSUMER's threshold model. Excludes MaxHp-gain cards
        // (BRIGHTEST_FLAME / FEED) — those scale with run length not single
        // combat threat.
        if (axes.Contains("HEAL"))
            ApplyHeal(card, state, ref b, parts);

        // v0.6.9 — STATUS_TO_HAND: card pushes Wreckage/Slime/etc. into player's
        // hand (or deck). Future hands carry the pollution. Static penalty
        // gated on how aggressive: filling hand (CRASH_LANDING) hurts way more
        // than dropping a single card (COLLISION_COURSE).
        if (axes.Contains("STATUS_TO_HAND"))
            ApplyStatusToHandPenalty(card, state, ref b, parts);

        // v0.6.9 — STATUS_CONSUMER: card pays off when status cards are in hand.
        // ROCKET_PUNCH cost 0 if status was generated; FLAK_CANNON deals 8 per
        // exhausted status; COMPACT converts status to Fuel+.
        if (axes.Contains("STATUS_CONSUMER"))
            ApplyStatusConsumer(card, state, ref b, parts);

        // v0.6.9 — MaxHp gain. BRIGHTEST_FLAME (+1 MaxHp), FEED (+3 on kill).
        // Permanent — small flat bonus, run-long value.
        if (card.Effect.MaxHpAmount > 0)
            ApplyMaxHpGain(card, ref b, parts);

        // v0.6.9 — Tier 2 patches: conditional draw, card-return, pile-search,
        // next-card cost enablers.
        if (axes.Contains("DRAW_CONDITIONAL"))
            ApplyDrawConditional(card, state, ref b, parts);

        if (axes.Contains("CARD_RETURN"))
            ApplyCardReturn(card, state, ref b, parts);

        if (axes.Contains("DRAW_PILE_SEARCH"))
            ApplyDrawPileSearch(card, state, ref b, parts);
        // v0.7.93 — Prefix stripped. WISH lacks DRAW_PILE_SEARCH axis but the
        // mechanic is identical (pull 1 card from draw pile, player chooses).
        else if (card.Id == "WISH")
            ApplyDrawPileSearch(card, state, ref b, parts);

        // v0.7.1 — Level 3: pile-based auto-play. v0.7.93 prefix stripped.
        if (card.Id == "CASCADE" || card.Id == "CATASTROPHE"
            || card.Id == "UPROAR" || card.Id == "BEAT_DOWN"
            || card.Id == "HAVOC")
            ApplyAutoPlayFromPile(card, state, ref b, parts);

        // v0.7.1 — Level 3: pile-based random modifier. v0.7.93 prefix stripped.
        if (card.Id == "HIDDEN_GEM" || card.Id == "DRAIN_POWER")
            ApplyDrawPileRandomModifier(card, state, ref b, parts);

        // v0.7.3 / v0.7.5 — Power passives whose tick value depends on the
        // current pile / hand / character pool. All follow the same delta
        // pattern: PowerCatalog["XPower"] stays the baked baseline credited
        // via the PlanScorer Power branch; this layer adds
        //   delta = clamp(state-derived tick − baked, −baked, +Cap)
        // so state actually shifts the Power's score. NOSTALGIA / STRATAGEM
        // remain inside ApplyCardReturn (they have the CARD_RETURN axis); the
        // dispatches below cover the no-axis-routing card ids.
        // v0.7.90 — Prefix stripped (v0.7.81 bug). Archetype Power tick-value handlers.
        if (card.Id == "MAYHEM")
            ApplyMayhemTickValue(card, state, ref b, parts);
        else if (card.Id == "STAMPEDE")
            ApplyStampedeTickValue(card, state, ref b, parts);
        else if (card.Id == "CALAMITY")
            ApplyCalamityTickValue(card, state, ref b, parts);
        else if (card.Id == "HELLRAISER")
            ApplyHellraiserTickValue(card, state, ref b, parts);
        else if (card.Id == "JUGGLING")
            ApplyJugglingTickValue(card, state, ref b, parts);
        // v0.7.26 — per-turn / trigger-based Power passives whose value depends
        // on deck composition or enemy state. Same delta pattern as MAYHEM:
        //   delta = clamp(state_derived − baked, −baked, +Cap)
        else if (card.Id == "DARK_EMBRACE")
            ApplyDarkEmbraceTickValue(card, state, ref b, parts);
        else if (card.Id == "VICIOUS")
            ApplyViciousTickValue(card, state, ref b, parts);
        else if (card.Id == "ACCELERANT")
            ApplyAccelerantTickValue(card, state, ref b, parts);
        else if (card.Id == "ENVENOM")
            ApplyEnvenomTickValue(card, state, ref b, parts);
        else if (card.Id == "SUBROUTINE")
            ApplySubroutineTickValue(card, state, ref b, parts);
        else if (card.Id == "PREP_TIME")
            ApplyPrepTimeTickValue(card, state, ref b, parts);
        else if (card.Id == "STORM")
            ApplyStormTickValue(card, state, ref b, parts);
        else if (card.Id == "TOOLS_OF_THE_TRADE")
            ApplyToolsOfTheTradeTickValue(card, state, ref b, parts);
        // v0.7.27 — Shiv stem Power passives (Silent-focused but party-shared).
        // All five hinge on the Shiv production rate AND the alive enemy count;
        // pure baked value miss-prices them in low-Shiv and AOE contexts.
        else if (card.Id == "ACCURACY")
            ApplyAccuracyTickValue(card, state, ref b, parts);
        else if (card.Id == "PHANTOM_BLADES")
            ApplyPhantomBladesTickValue(card, state, ref b, parts);
        else if (card.Id == "FAN_OF_KNIVES")
            ApplyFanOfKnivesTickValue(card, state, ref b, parts);
        else if (card.Id == "MASTER_PLANNER")
            ApplyMasterPlannerTickValue(card, state, ref b, parts);
        else if (card.Id == "INFINITE_BLADES")
            ApplyInfiniteBladesTickValue(card, state, ref b, parts);
        // v0.7.91 — Prefix stripped. Star stem (Regent) + Forge stem (Lord's Blade) Power passives.
        // v0.7.28 — Star stem Power passives (Regent archetype). Stars =
        // player resource pool. Powers either generate per-trigger or convert
        // Stars to damage/block. All scale with star generation rate × turns.
        else if (card.Id == "GENESIS")
            ApplyGenesisTickValue(card, state, ref b, parts);
        else if (card.Id == "ORBIT")
            ApplyOrbitTickValue(card, state, ref b, parts);
        else if (card.Id == "BLACK_HOLE")
            ApplyBlackHoleTickValue(card, state, ref b, parts);
        else if (card.Id == "CHILD_OF_THE_STARS")
            ApplyChildOfTheStarsTickValue(card, state, ref b, parts);
        else if (card.Id == "THE_SEALED_THRONE")
            ApplyTheSealedThroneTickValue(card, state, ref b, parts);
        // v0.7.29 — Forge stem Power passives (Regent, Lord's Blade archetype).
        // Forge = upgrade the SovereignBlade. Powers either auto-forge per turn
        // or scale with each Lord's Blade play.
        else if (card.Id == "FURNACE")
            ApplyFurnaceTickValue(card, state, ref b, parts);
        else if (card.Id == "HAMMER_TIME")
            ApplyHammerTimeTickValue(card, state, ref b, parts);
        else if (card.Id == "SEEKING_EDGE")
            ApplySeekingEdgeTickValue(card, state, ref b, parts);
        else if (card.Id == "SWORD_SAGE")
            ApplySwordSageTickValue(card, state, ref b, parts);
        else if (card.Id == "PARRY")
            ApplyParryTickValue(card, state, ref b, parts);
        // v0.7.92 — Prefix stripped. Doom/Volatile + Cross-character + Skill mechanics.
        // v0.7.30 — Doom / Volatile stem (Necrobinder). Doom = DoT-style stack
        // on enemies (and on player for self-Doom). Volatile = Ethereal cards
        // that auto-exhaust at turn end. Doom-based Powers scale with
        // RemainingTurns; Volatile-based Powers scale with Ethereal count.
        else if (card.Id == "COUNTDOWN")
            ApplyCountdownTickValue(card, state, ref b, parts);
        else if (card.Id == "RUPTURE")
            ApplyRuptureTickValue(card, state, ref b, parts);
        else if (card.Id == "PAGESTORM")
            ApplyPagestormTickValue(card, state, ref b, parts);
        else if (card.Id == "LETHALITY")
            ApplyLethalityTickValue(card, state, ref b, parts);
        else if (card.Id == "DEMESNE")
            ApplyDemesneTickValue(card, state, ref b, parts);
        // v0.7.31 — Cross-character impact residuals. Five high-priority
        // Powers across multiple characters: each gates on a different state
        // signal (turns / HP_LOSS cards / aliveEnemies / draw rate / hand size).
        else if (card.Id == "PYRE")
            ApplyPyreTickValue(card, state, ref b, parts);
        else if (card.Id == "INFERNO")
            ApplyInfernoTickValue(card, state, ref b, parts);
        else if (card.Id == "AUTOMATION")
            ApplyAutomationTickValue(card, state, ref b, parts);
        else if (card.Id == "OUTBREAK")
            ApplyOutbreakTickValue(card, state, ref b, parts);
        else if (card.Id == "PALE_BLUE_DOT")
            ApplyPaleBlueDotTickValue(card, state, ref b, parts);

        // Draw-event Powers tick — pile-aware adjustment. PowerCatalog gives
        // these a flat baseline (MachineLearning 900, DrawCardsNextTurn 900)
        // that ignores deck composition. Adjust by piles' mean card value
        // and remaining-turns multiplier so a finisher-rich deck sees Machine
        // Learning as the S+ scaler it actually is, while a curse-polluted
        // deck rightly demotes it.
        if (card.PowerApps.TryGetValue("MachineLearningPower", out var mlStack) && mlStack > 0)
            ApplyMachineLearningTickValue(mlStack, state, ref b, parts);
        if (card.PowerApps.TryGetValue("DrawCardsNextTurnPower", out var nextDrawStack) && nextDrawStack > 0)
            ApplyDrawCardsNextTurnTickValue(nextDrawStack, state, ref b, parts);
        // v0.7.43 — DECISIONS_DECISIONS (어려운 결정): choose 1 Skill in hand,
        // play it 3 times. 0-cost / 6-star Rare. REPEAT axis previously
        // unscored — the card looked like a plain draw card to the AI.
        else if (card.Id == "DECISIONS_DECISIONS")
            ApplyDecisionsDecisionsRepeat(card, state, ref b, parts);
        // v0.7.44 — Skill cards with mechanics not captured by generic axes.
        // X-cost skills (MALAISE/DIRGE/MULTI_CAST/TEMPEST) scale with remaining
        // energy. REPEAT/replay skills (QUADCAST/MODDED) multiply per-card effect.
        else if (card.Id == "QUADCAST")
            ApplyQuadcastEvoke(card, state, ref b, parts);
        else if (card.Id == "MULTI_CAST")
            ApplyMultiCastEvoke(card, state, ref b, parts);
        else if (card.Id == "TEMPEST")
            ApplyTempestChannel(card, state, ref b, parts);
        else if (card.Id == "MALAISE")
            ApplyMalaiseXWeak(card, state, ref b, parts);
        else if (card.Id == "DIRGE")
            ApplyDirgeXSouls(card, state, ref b, parts);
        else if (card.Id == "MODDED")
            ApplyModdedReplay(card, state, ref b, parts);
        // v0.7.45 — PROLONG (연장, Shared, A): next turn, gain block equal to
        // current block. EXHAUST_SELF. Pure state-dependent (PlayerBlock); the
        // BLOCK axis alone doesn't see this since card.Block == 0.
        else if (card.Id == "PROLONG")
            ApplyProlongCarryover(card, state, ref b, parts);
        // v0.7.46 — Discard-all skills with extra payoffs the CUNNING handler
        // doesn't capture (Shiv generation, next-turn damage doubling).
        else if (card.Id == "STORM_OF_STEEL")
            ApplyStormOfSteelShivs(card, state, ref b, parts);
        else if (card.Id == "SHADOW_STEP")
            ApplyShadowStepDoubleDmg(card, state, ref b, parts);
        // v0.7.65 — Skill-attack linkage cards with unique mechanics not
        // captured by generic axis scoring.
        else if (card.Id == "EXPOSE")
            ApplyExposeStripArtifact(card, state, ref b, parts);
        else if (card.Id == "CONQUEROR")
            ApplyConquerorBladeDouble(card, state, ref b, parts);
        // v0.7.89 — Forge / Skeleton / Combo handlers. Prefix stripped (v0.7.81 bug).
        // v0.7.66 — SUMMON_FORTH (Regent C 1c): Forge 8 + fetch Sovereign
        // Blade. vars.Forge isn't in CardEffectSummary so PlanScorer's
        // generic flow ignored both effects. Specific handler captures both.
        else if (card.Id == "SUMMON_FORTH")
            ApplySummonForthForge(card, state, ref b, parts);
        // v0.7.67 — Archetype-magnitude cards: vars 에 magnitude 가 있지만
        // CardEffectSummary 가 안 추적 → score 에서 magnitude 무시.
        else if (card.Id == "THE_SMITH")
            ApplyTheSmithForge30(card, state, ref b, parts);
        else if (card.Id == "AFTERLIFE")
            ApplySkeletonSummon6(card, state, ref b, parts, cost: 1);
        else if (card.Id == "LEGION_OF_BONE")
            ApplySkeletonSummon6(card, state, ref b, parts, cost: 2);
        // v0.7.68 — Comprehensive archetype-magnitude handlers for Summon /
        // Forge / Stars / OrbSlots vars across all unhandled cards.
        else if (card.Id == "CLEANSE")           ApplySkeletonSummonN(card, state, ref b, parts, 3);
        else if (card.Id == "INVOKE")            ApplySkeletonSummonN(card, state, ref b, parts, 2);
        else if (card.Id == "BODYGUARD")         ApplySkeletonSummonN(card, state, ref b, parts, 5);
        else if (card.Id == "NECRO_MASTERY")     ApplySkeletonSummonN(card, state, ref b, parts, 5);
        else if (card.Id == "PULL_AGGRO")        ApplySkeletonSummonN(card, state, ref b, parts, 4);
        else if (card.Id == "SPUR")              ApplySkeletonSummonN(card, state, ref b, parts, 3);
        else if (card.Id == "REANIMATE")         ApplySkeletonSummonN(card, state, ref b, parts, 20);
        // Forge generic — Blade required for value
        else if (card.Id == "REFINE_BLADE")      ApplyForgeGeneric(card, state, ref b, parts, 9);
        else if (card.Id == "SPOILS_OF_BATTLE")  ApplyForgeGeneric(card, state, ref b, parts, 5);
        else if (card.Id == "WROUGHT_IN_WAR")    ApplyForgeGeneric(card, state, ref b, parts, 7);
        else if (card.Id == "BIG_BANG")          ApplyBigBangCombo(card, state, ref b, parts);
        else if (card.Id == "BULWARK")           ApplyForgeGeneric(card, state, ref b, parts, 10);
        // v0.7.88 — Star producers (this-turn). Prefix stripped (v0.7.81 bug).
        // HIDDEN_CACHE/CONVERGENCE are next-turn only — handled separately below;
        // their previous immediate-stars entries here were wrong intent AND
        // masked the next-turn handlers via else-if chain ordering.
        else if (card.Id == "GLOW")              ApplyStarsGain(card, state, ref b, parts, 1);
        else if (card.Id == "GATHER_LIGHT")      ApplyStarsGain(card, state, ref b, parts, 1);
        else if (card.Id == "RADIATE")           ApplyStarsGain(card, state, ref b, parts, 1);
        else if (card.Id == "VENERATE")          ApplyStarsGain(card, state, ref b, parts, 2);
        else if (card.Id == "SHINING_STRIKE")    ApplyStarsGain(card, state, ref b, parts, 2);
        else if (card.Id == "SOLAR_STRIKE")      ApplyStarsGain(card, state, ref b, parts, 1);
        else if (card.Id == "KNOCKOUT_BLOW")     ApplyStarsGain(card, state, ref b, parts, 5);
        else if (card.Id == "ROYAL_GAMBLE")      ApplyStarsGain(card, state, ref b, parts, 9);
        // v0.7.74 — Next-turn star producers. Catalog vars["Stars"] captures
        // ONLY this-turn star gain; the "다음 턴에 ★" text is encoded
        // separately in the card class. Add delayed-star value explicitly.
        // v0.7.88 — Prefix stripped; ordering preserved.
        else if (card.Id == "HIDDEN_CACHE")       ApplyHiddenCacheDelayedStars(card, state, ref b, parts);
        else if (card.Id == "CONVERGENCE")        ApplyConvergenceNextTurn(card, state, ref b, parts);
        // OrbSlots — v0.7.89 prefix fix
        else if (card.Id == "BULK_UP")           ApplyBulkUpOrbSlots(card, state, ref b, parts);
        // v0.7.69 — Exhaust-related card handlers. Specific mechanics not
        // captured by generic EXHAUST_CONSUMER (+20/exhausted, cap 320).
        // v0.7.93 — Exhaust handlers prefix stripped.
        else if (card.Id == "FEEL_NO_PAIN")      ApplyFeelNoPainPower(card, state, ref b, parts);
        else if (card.Id == "PACTS_END")         ApplyPactsEndGated(card, state, ref b, parts);
        else if (card.Id == "CHILL")             ApplyChillFrostPerEnemy(card, state, ref b, parts);
        else if (card.Id == "ALCHEMIZE")         ApplyAlchemizePotion(card, state, ref b, parts);
        else if (card.Id == "BURNING_PACT")      ApplyBurningPactExhaustDraw(card, state, ref b, parts);
        else if (card.Id == "EVIL_EYE")          ApplyEvilEyeConditional(card, state, ref b, parts);
        // v0.7.92 — Prefix stripped. Retain skill specific mechanics.
        // SACRIFICE: block = Skeleton max HP × 2 (state-dependent).
        // RESTLESSNESS: conditional empty-hand trigger.
        // PURITY: variable hand-exhaust value.
        else if (card.Id == "SACRIFICE")
            ApplySacrificeBlock(card, state, ref b, parts);
        else if (card.Id == "RESTLESSNESS")
            ApplyRestlessnessConditional(card, state, ref b, parts);
        else if (card.Id == "PURITY")
            ApplyPurityHandClean(card, state, ref b, parts);
        // v0.7.93 — Prefix stripped. Scaling / Conditional / Self-growing.
        // v0.7.49 — Scaling-stem skills.
        else if (card.Id == "APOTHEOSIS")
            ApplyApotheosisUpgradeAll(card, state, ref b, parts);
        else if (card.Id == "DOMINATE")
            ApplyDominateVulnStrike(card, state, ref b, parts);
        else if (card.Id == "BRAND")
            ApplyBrandHpExhaustStr(card, state, ref b, parts);
        else if (card.Id == "STOKE")
            ApplyStokeExhaustGenerate(card, state, ref b, parts);
        // v0.7.50 — Conditional / Heal / multi-turn skill audit.
        else if (card.Id == "BATTLE_TRANCE")
            ApplyBattleTranceTradeoff(card, state, ref b, parts);
        else if (card.Id == "BORROWED_TIME")
            ApplyBorrowedTimeRamp(card, state, ref b, parts);
        else if (card.Id == "NOT_YET")
            ApplyNotYetHeal(card, state, ref b, parts);
        else if (card.Id == "PANIC_BUTTON")
            ApplyPanicButtonEmergency(card, state, ref b, parts);
        else if (card.Id == "THE_BOMB")
            ApplyTheBombDelayed(card, state, ref b, parts);
        else if (card.Id == "TORIC_TOUGHNESS")
            ApplyToricToughnessMultiTurn(card, state, ref b, parts);
        // v0.7.51 — Self-growing attack cards.
        else if (card.Id == "CLAW")
            ApplySelfGrowingAttack(card, state, ref b, parts, increasePerPlay: 2, hitCount: 1);
        else if (card.Id == "MAUL")
            ApplySelfGrowingAttack(card, state, ref b, parts, increasePerPlay: 1, hitCount: 2);
        else if (card.Id == "RAMPAGE")
            ApplyRampageSelfGrow(card, state, ref b, parts);
        // v0.7.32 — Defect orb stem Power passives. v0.7.93 prefix stripped.
        else if (card.Id == "CAPACITOR")
            ApplyCapacitorTickValue(card, state, ref b, parts);
        else if (card.Id == "COOLANT")
            ApplyCoolantTickValue(card, state, ref b, parts);
        else if (card.Id == "SPINNER")
            ApplySpinnerTickValue(card, state, ref b, parts);
        else if (card.Id == "THUNDER")
            ApplyThunderTickValue(card, state, ref b, parts);
        else if (card.Id == "LOOP")
            ApplyLoopTickValue(card, state, ref b, parts);
        else if (card.Id == "CONSUMING_SHADOW")
            ApplyConsumingShadowTickValue(card, state, ref b, parts);
        else if (card.Id == "HAILSTORM")
            ApplyHailstormTickValue(card, state, ref b, parts);

        // v0.7.11 — Self-copy / chain cards. Each play seeds a future play of
        // the same or chosen card. Pure card-id dispatch — none of these have
        // a generic axis we could match on (catalog axes describe the immediate
        // effect, not the chain semantics).
        // v0.7.93 — Self-copy/chain. Prefix stripped.
        if (card.Id == "ANGER")
            ApplyAngerChain(card, state, ref b, parts);
        else if (card.Id == "UNDEATH")
            ApplyUndeathChain(card, state, ref b, parts);
        else if (card.Id == "DUAL_WIELD")
            ApplyDualWieldChain(card, state, ref b, parts);
        else if (card.Id == "HEIRLOOM_HAMMER")
            ApplyHeirloomHammerChain(card, state, ref b, parts);
        else if (card.Id == "NIGHTMARE")
            ApplyNightmareChain(card, state, ref b, parts);
        else if (card.Id == "ADAPTIVE_STRIKE")
            ApplyAdaptiveStrikeChain(card, state, ref b, parts);

        // v0.7.17 — S-tier 1-path coverage: card-id specific mechanics that
        // the axis dispatchers don't capture. Pure direct-stat scoring under-
        // values these because their value comes from a state-dependent
        // post-attack effect.
        // v0.7.93 — S-tier 1-path. Prefix stripped.
        if (card.Id == "ALL_FOR_ONE")
            ApplyAllForOneRecall(card, state, ref b, parts);
        else if (card.Id == "PINPOINT")
            ApplyPinpointEnergyRefund(card, state, ref b, parts);
        else if (card.Id == "FLECHETTES")
            ApplyFlechettesHandSkills(card, state, ref b, parts);
        else if (card.Id == "MAKE_IT_SO")
            ApplyMakeItSoReclaim(card, state, ref b, parts);
        else if (card.Id == "SUNDER")
            ApplySunderKillRefund(card, targetIdx, state, ref b, parts);
        else if (card.Id == "TESLA_COIL")
            ApplyTeslaCoilEvokeAll(card, state, ref b, parts);
        else if (card.Id == "THRUMMING_HATCHET")
            ApplyThrummingHatchetChain(card, state, ref b, parts);

        // v0.7.19 — B-tier 1-path coverage. 9 mechanic-bearing B-tier cards
        // (FINISHER/BOLAS/etc.) whose value depends on hand/turn state
        // unavailable to pure direct-stat scoring.
        // v0.7.93 — B-tier 1-path. Prefix stripped.
        // FINISHER scaling moved into PlanScorer.EstimateVariableHits +
        // AllowsZeroHits (TurnAttacksPlayed → hit count), making the base
        // damage credit correct at 0/1/N attacks. The standalone bonus here
        // would now double-count, so it is dropped.
        else if (card.Id == "BOLAS")
            ApplyBolasChain(card, state, ref b, parts);
        else if (card.Id == "FOLLOW_THROUGH")
            ApplyFollowThroughRepeat(card, state, ref b, parts);
        else if (card.Id == "EXPECT_A_FIGHT")
            ApplyExpectAFightEnergy(card, state, ref b, parts);
        else if (card.Id == "SPITE")
            ApplySpiteHpLossBonus(card, state, ref b, parts);
        else if (card.Id == "HEADBUTT")
            ApplyHeadbuttDeckPick(card, state, ref b, parts);
        else if (card.Id == "REBOUND")
            ApplyReboundSkillReclaim(card, state, ref b, parts);
        else if (card.Id == "OUTMANEUVER")
            ApplyOutmaneuverNextTurnEnergy(card, state, ref b, parts);
        else if (card.Id == "SEEKER_STRIKE")
            ApplySeekerStrikePick(card, state, ref b, parts);

        // v0.9.1 — Replay-next-card amplifiers. These Skills apply a Power that
        // re-plays the next Attack/Skill/Power. The base PowerCatalog value
        // gives a flat tier credit, but the marginal value of playing the
        // amplifier NOW is the best-in-hand target's value — a hand with
        // BLUDGEON makes ONE_TWO_PUNCH worth +1000, a hand with no attack
        // makes it ~0. Sequencing comes naturally: when the target is in
        // hand, the amplifier outscores it and is played first.
        else if (card.Id == "ONE_TWO_PUNCH")
            ApplyReplayBestAttack(card, state, ref b, parts);
        else if (card.Id == "BURST")
            ApplyReplayBestSkill(card, state, ref b, parts);
        else if (card.Id == "SIGNAL_BOOST")
            ApplyReplayBestPower(card, state, ref b, parts);
        else if (card.Id == "STOMP")
            ApplyStompCostDiscountValue(card, state, ref b, parts);
        else if (card.Id == "STRANGLE")
            ApplyStrangleChip(card, state, ref b, parts);
        else if (card.Id == "ECHOING_SLASH")
            ApplyEchoingSlashOverkillBonus(card, targetIdx, state, ref b, parts);

        // Cost-enabler: UNRELENTING (next Attack 0-cost), SYNTHESIS (next Power
        // 0-cost), POUNCE (next Skill 0-cost). Combat-wide enablers (CORRUPTION,
        // ENLIGHTENMENT, BULLET_TIME) are Powers/Skills covered elsewhere.
        if (axes.Contains("ATTACK_COST_ENABLER")
            || axes.Contains("SKILL_COST_ENABLER")
            || axes.Contains("POWER_COST_ENABLER"))
            ApplyNextCardCostEnabler(card, state, ref b, parts);

        // v0.6.9 — Tier 3: CARD_GEN flat-per-generated bonus + EXHAUST_TARGET_RANDOM penalty.
        // CARD_GEN value depends heavily on what's generated (Shivs are concrete
        // attacks, "random Power" is unpredictable). Use specific overrides for
        // known generators and a generic baseline for the rest.
        // v0.7.2 — Level 4 pool-based random cards consult PoolMeans first
        // (character-aware mean / top-1-of-N from the actual card pool) and
        // fall back to per-card-id flat magnitudes when the lookup is empty
        // (no character id, schema mismatch, embedded resource missing).
        if (axes.Contains("CARD_GEN"))
            ApplyCardGen(card, state, ref b, parts);
        // v0.6.9 — Card-id fallback for random-card-to-hand cards missing the
        // CARD_GEN axis in the catalog (WHITE_NOISE / DISCOVERY / DISTRACTION /
        // LARGESSE / SPLASH). Same flat-bonus treatment as CARD_GEN.
        // WISH removed in v0.7.1 — it goes through pile-aware ApplyDrawPileSearch.
        // v0.7.93 — Card-id fallback for random-card-to-hand cards. Prefix stripped.
        else if (card.Id == "WHITE_NOISE" || card.Id == "DISCOVERY"
              || card.Id == "DISTRACTION" || card.Id == "LARGESSE"
              || card.Id == "SPLASH")
            ApplyCardGen(card, state, ref b, parts);

        // Card-create trigger preview. ArsenalPower (+1 Str / card created) and
        // PillarOfCreationPower (+3 block / card created) fire per generated
        // card. STATUS_TO_HAND fillsHand plays generate ~MaxHand−|hand| cards;
        // CARD_GEN plays generate per-recipe counts. Mirrors the HpLossEvent
        // preview pattern (ruptureTrigger/infernoTrigger).
        if (axes.Contains("STATUS_TO_HAND") || axes.Contains("CARD_GEN"))
            ApplyCardCreateTriggerPreview(card, state, ref b, parts);

        // Exhaust-event reactive passives. DarkEmbracePower (+1 draw per
        // exhaust) inverts the "card lost forever" cost of self-exhaust cards.
        // FeelNoPainPower is already credited via PlanScorer attack/skill
        // reactive-block branches, so not re-credited here.
        if (card.IsExhaust || axes.Contains("EXHAUST_SELF"))
            ApplyExhaustEventTriggerPreview(card, state, ref b, parts);

        // Volatile (Ethereal) play reactive passives. SpiritOfAshPower (+4
        // block per Volatile play) rewards the natural "play-or-lose" pattern
        // of ethereal cards.
        if (card.IsEthereal)
            ApplyVolatilePlayTriggerPreview(card, state, ref b, parts);

        // Draw-event reactive passives. HungerPower (+N Strength per card
        // drawn) — applies on cards that draw additional cards. Sim already
        // applies this in AdvanceTurn; preview here so immediate ranking
        // reflects the Hunger active-build value.
        if (card.DrawCount > 0)
            ApplyDrawEventTriggerPreview(card, state, ref b, parts);

        // Skill-played reactive passives. EnragePower (+N Strength per Skill).
        if (card.IsSkill)
            ApplySkillPlayedTriggerPreview(card, state, ref b, parts);

        // Vuln-applied reactive passives. ViciousPower (+1 draw per Vuln
        // applied) — fires once per Vuln-apply event. AOE Vuln (PIERCING_WAIL)
        // fires per alive enemy.
        if (axes.Contains("VULN_PRODUCER"))
            ApplyVulnApplyTriggerPreview(card, state, ref b, parts);

        // Doom-applied reactive passives. ShroudPower (+2 block per Doom
        // applied). AOE_DOOM applies per alive enemy.
        if (axes.Contains("DOOM_PRODUCER"))
            ApplyDoomApplyTriggerPreview(card, state, ref b, parts);

        // ReaperFormPower transforms attack cards into Doom appliers.
        // "Whenever Attacks deal damage, apply equivalent Doom" — each hit
        // applies damage Doom on the target. Future-turn HP damage from
        // Doom ticking is invisible to the base attack score; preview it.
        // Also chains with ShroudPower (per-Doom-apply +block) — one Doom
        // apply event per attack hit.
        if (card.IsAttack && card.Damage > 0)
            ApplyReaperFormAttackPreview(card, state, ref b, parts);

        // Star-cost cards consume N stars on play. ChildOfTheStarsPower and
        // BlackHolePower react to the consume event — invisible to base
        // attack/skill scoring (stars don't appear as damage/block in the
        // played card). Preview the chained block + AOE.
        if (card.Effect.StarCost > 0)
            ApplyStarConsumePreview(card, state, ref b, parts);

        // Star opportunity cost. Stars are STOCKPILE-able (don't reset per turn
        // like energy) — playing a low-efficiency star card now can block a
        // higher-value star card later. Two penalties:
        //   (A) Efficiency gap: this card's damage/block per star < best in deck
        //   (B) Future shortfall: post-play stars < highest-cost card in deck
        // Net-positive converters (ROYAL_GAMBLE: 5→9 stars) skipped — they're
        // banking actions, not payoffs.
        if (card.Effect.StarCost > 0)
            ApplyStarOpportunityCost(card, state, ref b, parts);

        // Star environment penalty. When all alive enemies have heavy
        // damage caps (Intangible / HardToKill), a star_cost burst attack
        // wastes the stockpile — stars carry over to the next turn when the
        // cap usually expires, but the stars themselves are gone.
        if (card.Effect.StarCost > 0 && card.IsAttack)
            ApplyStarEnvironmentPenalty(card, state, ref b, parts);

        // Star-gain cards trigger BlackHolePower per gained Star ("별을
        // 얻을 때마다 ..."). Star gain is invisible to base score outside
        // of resource projection — preview the AOE chain here.
        if (card.Effect.StarsGain > 0)
            ApplyStarGainPreview(card, state, ref b, parts);

        if (axes.Contains("EXHAUST_TARGET_RANDOM"))
            ApplyRandomExhaustPenalty(card, state, ref b, parts);

        // Whole-hand exhaust (FIEND_FIRE) and non-attack filtered exhaust
        // (SECOND_WIND): the damage / block payoff is already credited
        // (PlanScorer.EstimateVariableHits / EstimateBlockMultiplier), but the
        // keystone loss — Powers, Retain, SCALING in hand — was not. Subtract.
        if (card.Id == "FIEND_FIRE")
            ApplyWholeHandExhaustLoss(card, state, ref b, parts);
        else if (card.Id == "SECOND_WIND")
            ApplyNonAttackExhaustLoss(card, state, ref b, parts);

        // v0.6.9 — PRECISE_CUT: damage = 13 − 2 × (other cards in hand).
        // Anti-handsize scaling — small/empty hand multiplies value; full hand
        // gates damage to near-0. Not captured by EstimateVariableHits (which
        // is multiplicative); use a per-card-id damage adjustment here.
        // v0.7.93 — PRECISE_CUT prefix stripped.
        if (card.Id == "PRECISE_CUT")
            ApplyPreciseCutScaling(card, state, ref b, parts);

        // v0.6.9 — OSTY-gated attacks. Cards with OSTY axis but NOT
        // SKELETON_CONSUMER (those go through ApplySkeletonConsumer) have a
        // "if Osty alive then deal damage via Osty" pattern. POKE / FLATTEN /
        // SWEEPING_GAZE / SIC_EM / RATTLE / RIGHT_HAND_HAND / SNAP score
        // near-0 when no skeleton, near-base when alive.
        if (axes.Contains("OSTY") && !axes.Contains("SKELETON_CONSUMER")
            && !axes.Contains("SKELETON_AMPLIFIER")
            && !axes.Contains("SKELETON_PRODUCER"))
            ApplyOstyConditional(state, ref b, parts);

        // v0.7.21 — DOOM_SELF_PRODUCER: card adds Doom to player. High Doom
        // = existential risk (turn-end tick scales with stack). Penalize
        // proportional to how close (PlayerDoom + new) gets to PlayerHp.
        if (axes.Contains("DOOM_SELF_PRODUCER"))
            ApplyDoomSelfRisk(card, state, ref b, parts);

        // v0.6.9 — ENLIGHTENMENT: combat-wide cost reduction (all hand cards
        // cost 1). Value = sum of cost reductions in current + future hands.
        // v0.7.93 — ENLIGHTENMENT prefix stripped.
        if (card.Id == "ENLIGHTENMENT")
            ApplyEnlightenmentBonus(card, state, ref b, parts);

        // STAR_CONSUMER — single attack (STARDUST) scales with player's
        // accumulated Star count. BuildSynergy already credits the pair
        // bonus when producers are in hand, but the actual payoff scales
        // with stack so we layer per-stack value on top.
        if (axes.Contains("STAR_CONSUMER"))
            ApplyStarConsumer(card, state, ref b, parts);

        // DARK_ORB_AMPLIFIER — DARKNESS (Skill) doubles existing dark orb
        // evoke values. Bonus scales with current dark-orb count.
        if (axes.Contains("DARK_ORB_AMPLIFIER"))
            ApplyDarkOrbAmplifier(state, ref b, parts);

        // v0.6.7 — Pile / ally / token-based stack-aware consumer-side bonuses.
        // Each handler reads a freshly-snapshotted SimState counter and scales
        // the consumer/amplifier card's score with the relevant stack. Without
        // these the cards rely only on BuildSynergy's flat 200-point pair bonus
        // (in-hand producer presence) and miss the timing signal that a large
        // accumulated resource produces.
        // SOUL_AMPLIFIER is intentionally absent — catalog audit (v0.6.7) shows 0
        // SOUL_AMPLIFIER cards. Keep CONSUMER only.
        if (axes.Contains("SOUL_CONSUMER"))
            ApplySoulConsumer(card, state, ref b, parts);
        if (axes.Contains("SHIV_CONSUMER") || axes.Contains("SHIV_AMPLIFIER"))
            ApplyShivConsumer(card, state, ref b, parts);
        if (axes.Contains("SKELETON_CONSUMER") || axes.Contains("SKELETON_AMPLIFIER"))
            ApplySkeletonConsumer(card, state, ref b, parts);
        if (axes.Contains("EXHAUST_CONSUMER"))
            ApplyExhaustConsumer(state, ref b, parts);
        if (axes.Contains("FORGE_AMPLIFIER") || axes.Contains("LORDS_BLADE_AMPLIFIER"))
            ApplyBladeAmplifier(card, state, ref b, parts);
        if (axes.Contains("VOLATILE_CONSUMER"))
            ApplyVolatileConsumer(card, state, ref b, parts);
        // CUNNING_CONSUMER — discard-trigger cards (ACROBATICS, CALCULATED_GAMBLE,
        // PREPARED, HIDDEN_DAGGERS, SURVIVOR, etc.). Sly cards auto-play when
        // discarded (CardCmd.cs Sly-discard loop), so the consumer's payoff
        // scales with the number of Sly cards in hand at play time.
        if (axes.Contains("CUNNING_CONSUMER"))
            ApplyCunningConsumer(card, state, ref b, parts);

        // DoT consumer / amplifier ordering. Pair-axis stems (POISON, DOOM,
        // BURN, CONSTRICT) need an explicit consumer-side bonus because the
        // generic damage / block scorer doesn't see the target-stack signal.
        //   • CONSUMER (BUBBLE_BUBBLE: poison-on-tgt → +9 poison;
        //               TIMES_UP: dmg = tgt doom; DEATHS_DOOR: extra block
        //               when doom was applied this turn) — pays off when the
        //               target / any enemy already carries the stack OR a
        //               producer is queued in hand to feed it.
        //   • AMPLIFIER (NO_ESCAPE doom-amp side, AOE amps) — same logic.
        foreach (var stem in DotStems)
        {
            bool isConsumer = axes.Contains(stem + "_CONSUMER");
            bool isAmplifier = axes.Contains(stem + "_AMPLIFIER");
            if (!isConsumer && !isAmplifier) continue;
            ApplyDotConsumerOrAmplifier(card, targetIdx, state, stem, isConsumer, ref b, parts);
        }

        return (b, parts.Count == 0 ? "" : string.Join(",", parts));
    }

    // BURN / CONSTRICT enemy debuffs exist on SimEnemy (PoisonAmount path) but the
    // catalog has 0 BURN_*/CONSTRICT_* axis cards as of v0.103.2 — player can't
    // consume / amplify them, only apply via Powers. Keep registry tight.
    private static readonly string[] DotStems = { "POISON", "DOOM" };

    private static void ApplyDotConsumerOrAmplifier(
        SimCard self, int targetIdx, SimState state,
        string stem, bool isConsumer, ref int b, List<string> parts)
    {
        int targetStack = 0;
        if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
        {
            var e = state.Enemies[targetIdx];
            if (e.IsAlive) targetStack = ReadStack(e, stem);
        }
        int anyStack = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) anyStack = System.Math.Max(anyStack, ReadStack(e, stem));

        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains(stem + "_PRODUCER") || c.Axes.Contains(stem)));

        // Consumer payoff scales with the actual stack on its target.
        // Amplifier (when the card extends existing stacks) values less per-stack
        // but the source-presence bonus is the same.
        if (targetStack > 0)
        {
            int v = isConsumer ? targetStack * 20 : targetStack * 10;
            b += v;
            parts.Add($"{stem.ToLowerInvariant()}OnTgt({targetStack})=+{v}");
        }
        else if (anyStack > 0)
        {
            int v = isConsumer ? 180 : 120;
            b += v;
            parts.Add($"{stem.ToLowerInvariant()}OnAny({anyStack})=+{v}");
        }
        else if (producerInHand)
        {
            int v = isConsumer ? 150 : 100;
            b += v;
            parts.Add($"{stem.ToLowerInvariant()}ProdInHand=+{v}");
        }
        else
        {
            // No source — consumer is dead weight, amplifier still mild loss.
            int v = isConsumer ? -300 : -150;
            b += v;
            parts.Add($"{stem.ToLowerInvariant()}NoSource={v}");
        }
    }

    private static void ApplyStarConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int stars = state.PlayerStars;
        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("STAR_PRODUCER") || c.Axes.Contains("STAR")));
        if (stars > 0)
        {
            int v = System.Math.Min(stars * 15, 450);
            b += v;
            parts.Add($"starOnSelf({stars})=+{v}");
        }
        else if (producerInHand)
        {
            b += 120;
            parts.Add("starProdInHand=+120");
        }
        else
        {
            b -= 250;
            parts.Add("starNoSource=-250");
        }
    }

    private static void ApplyDarkOrbAmplifier(SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerOrbCapacity == 0) return;
        int darkCount = 0;
        foreach (var orb in state.OrbQueue)
            if (orb == OrbKind.Dark) darkCount++;
        bool darkProducerInHand = state.Hand.Any(c =>
            c.IsPlayable && c.Axes.Contains("DARK_ORB_PRODUCER"));
        if (darkCount > 0)
        {
            int v = System.Math.Min(darkCount * 120, 360);
            b += v;
            parts.Add($"darkAmpQueue({darkCount})=+{v}");
        }
        else if (darkProducerInHand)
        {
            b += 100;
            parts.Add("darkAmpProdInHand=+100");
        }
        else
        {
            b -= 150;
            parts.Add("darkAmpNoSource=-150");
        }
    }

    // v0.6.7 — Per-mechanic consumer/amplifier helpers. Each follows the same
    // three-tier signal pattern:
    //   1. Stack > 0  → per-stack bonus (with cap to bound the score impact)
    //   2. Producer in hand → flat fallback (the producer will create stack)
    //   3. Neither      → mild penalty (the card is dead until something supplies)
    // Magnitudes are deliberately smaller than the DoT bonuses because these
    // stems already get BuildSynergy's 200-pt pair bonus; this layer only adds
    // the timing gradient.

    private static void ApplySoulConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int souls = state.SoulInPiles;
        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("SOUL_PRODUCER") || c.Axes.Contains("SOUL")));
        if (souls > 0)
        {
            int v = System.Math.Min(souls * 25, 400);
            b += v;
            parts.Add($"soulPile({souls})=+{v}");
        }
        else if (producerInHand)
        {
            b += 100;
            parts.Add("soulProdInHand=+100");
        }
        else
        {
            b -= 200;
            parts.Add("soulNoSource=-200");
        }
    }

    private static void ApplyShivConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int shivs = state.ShivInPiles;
        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("SHIV_PRODUCER") || c.Axes.Contains("SHIV")));
        if (shivs > 0)
        {
            int v = System.Math.Min(shivs * 30, 360);
            b += v;
            parts.Add($"shivPile({shivs})=+{v}");
        }
        else if (producerInHand)
        {
            b += 100;
            parts.Add("shivProdInHand=+100");
        }
        else
        {
            b -= 180;
            parts.Add("shivNoSource=-180");
        }
    }

    private static void ApplySkeletonConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int alive = state.SkeletonCount;
        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("SKELETON_PRODUCER") || c.Axes.Contains("SKELETON")));
        if (alive > 0)
        {
            // Skeleton consumers (BONE_SHARDS, PROTECTOR) typically gate on
            // *any* skeleton alive — multiple skeletons rarely stack the same
            // payoff, so the bonus saturates after the first.
            int v = alive == 1 ? 300 : 360;
            b += v;
            parts.Add($"skelAlive({alive})=+{v}");
        }
        else if (producerInHand)
        {
            b += 120;
            parts.Add("skelProdInHand=+120");
        }
        else
        {
            // Skeleton consumers are dead weight without a live skeleton AND no
            // way to summon one this turn. Heavy penalty — BONE_SHARDS plays
            // for 0 when Osty is dead.
            b -= 400;
            parts.Add("skelNoSource=-400");
        }
    }

    private static void ApplyExhaustConsumer(SimState state, ref int b, List<string> parts)
    {
        int exhausted = state.ExhaustPileSize;
        // No producer-in-hand fallback for this one — exhaust pile grows
        // monotonically over a combat, so by turn 3-4 there's almost always
        // *some* exhausted card. Pure pile-scaling bonus.
        if (exhausted > 0)
        {
            int v = System.Math.Min(exhausted * 20, 320);
            b += v;
            parts.Add($"exhPile({exhausted})=+{v}");
        }
    }

    private static void ApplyBladeAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int blades = state.SovereignBladeCount;
        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("FORGE_PRODUCER")
                || c.Axes.Contains("LORDS_BLADE_PRODUCER")
                || c.Axes.Contains("FORGE")));
        if (blades > 0)
        {
            // Each existing SovereignBlade benefits from the amplifier (Forge /
            // Conqueror upgrades all live blades). Cap protects against the rare
            // multi-blade build extreme.
            int v = System.Math.Min(blades * 150, 450);
            b += v;
            parts.Add($"bladeCount({blades})=+{v}");
        }
        else if (producerInHand)
        {
            b += 120;
            parts.Add("bladeProdInHand=+120");
        }
        else
        {
            b -= 200;
            parts.Add("bladeNoSource=-200");
        }
    }

    /// <summary>
    /// v0.7.46 — Per-card-id discard count for CUNNING_CONSUMER skills.
    /// Most consumers discard 1; CALCULATED_GAMBLE/SHADOW_STEP/STORM_OF_STEEL
    /// discard ENTIRE hand. Returns the upper bound to size the Sly trigger
    /// pool — clamped by actual hand size at trigger time.
    /// </summary>
    private static int CunningDiscardCount(SimCard self, SimState state)
    {
        switch (self.Id)
        {
            case "CALCULATED_GAMBLE":
            case "SHADOW_STEP":
            case "STORM_OF_STEEL":
                return state.Hand.Count;  // up to entire hand
            case "HIDDEN_DAGGERS":
                return 2;
            // ACROBATICS, PREPARED, SURVIVOR, others: discard 1
            default:
                return 1;
        }
    }

    private static void ApplyCunningConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // v0.7.46 — Use per-card discard count + per-Sly-card EstimateCardPower
        // instead of flat 110. Captures TACTICIAN (gain 1 energy ≈ 500) vs
        // HAND_TRICK (situational utility) correctly.
        int discardCount = CunningDiscardCount(self, state);

        // Collect Sly card values in hand, sorted descending — the discard
        // randomly picks any hand card, so worst-case the Sly cards end up
        // discarded first; we use the top-K by value as upper bound.
        var slyValues = new List<int>();
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsSly || !c.IsPlayable) continue;
            slyValues.Add(EstimateCardPower(c, state, freeUse: true));
        }

        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("CUNNING_PRODUCER") || c.Axes.Contains("CUNNING")));

        if (slyValues.Count > 0)
        {
            slyValues.Sort((x, y) => y.CompareTo(x));
            // Number of Sly cards likely triggered = min(slyInHand, discardCount).
            // Discount per trigger by 0.6 — random discard might not hit a Sly
            // card. CALCULATED_GAMBLE (discard ALL) hits every Sly = no discount.
            int triggered = System.Math.Min(slyValues.Count, discardCount);
            bool guaranteed = discardCount >= state.Hand.Count - 1;  // discard most/all
            double discount = guaranteed ? 0.9 : 0.6;
            int v = 0;
            for (int i = 0; i < triggered; i++) v += (int)(slyValues[i] * discount);
            const int Cap = 1500;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"slyTrigger(n={triggered}/{slyValues.Count},x{discount})=+{v}");
        }
        else if (producerInHand)
        {
            // Producer present but not yet drawn into hand alongside the
            // consumer — modest credit since BuildSynergy already gives 200pt.
            b += 60;
            parts.Add("cunProdInHand=+60");
        }
        else
        {
            // No Sly source — consumer's discard portion is pure tempo. Light
            // penalty (consumers like ACROBATICS still have draw/block value
            // independently, so don't crater the score).
            b -= 150;
            parts.Add("cunNoSly=-150");
        }
    }

    private static void ApplyVolatileConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // "Volatile" in the catalog maps to CardKeyword.Ethereal in the game.
        // PAGESTORM / VEILPIERCER / PULL_FROM_BELOW / BANSHEES_CRY / SPIRIT_OF_ASH
        // pay off when ethereal cards trigger this turn. Count remaining ethereal
        // cards in hand (and queued in draw) — more pending triggers → more value.
        int etherealInHand = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsEthereal) etherealInHand++;
        }
        int etherealInDraw = 0;
        foreach (var c in state.DrawPile)
            if (c.IsEthereal) etherealInDraw++;

        // Hand ethereals weigh more — they will trigger this turn deterministically.
        // Draw-pile ethereals weigh less since they only trigger after a draw card
        // surfaces them (probabilistic).
        int v = etherealInHand * 90 + etherealInDraw * 25;
        if (v > 0)
        {
            v = System.Math.Min(v, 540);
            b += v;
            parts.Add($"volEth(h{etherealInHand}+d{etherealInDraw})=+{v}");
        }
        else
        {
            // No ethereal cards anywhere — consumer is inert. Mild penalty only;
            // some VOLATILE_CONSUMER cards (VEILPIERCER) also do raw damage.
            b -= 150;
            parts.Add("volEthNone=-150");
        }
    }

    private static void ApplyStrengthDown(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
    {
        int amount = self.Effect.StrengthDownAmount;
        if (amount <= 0) return; // safety — no var extracted, skip silently

        bool isAoe = self.Target == TargetType.AllEnemies
                  || self.Axes.Contains("AOE_DEBUFF")
                  || self.Axes.Contains("AOE_OTHER");

        // Sum incoming damage from attacking enemies. Each StrengthLoss point
        // reduces every hit by 1; multi-hit enemies (IntentRepeats >= 2)
        // amplify the savings.
        int savingsHits = 0;
        int targets = 0;
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            var e = state.Enemies[i];
            if (!e.IsAlive || !e.HasAttackIntent || e.IsInert) continue;
            if (!isAoe && i != targetIdx) continue;
            int rep = System.Math.Max(1, e.IntentRepeats);
            savingsHits += rep;
            targets++;
        }

        // Each hit-saved = amount × per-point-value (~30, mirroring WeakAmplifier
        // economy). Multi-hit + AOE compound naturally via savingsHits.
        if (savingsHits > 0)
        {
            int v = System.Math.Min(amount * savingsHits * 30, 1200);
            b += v;
            parts.Add($"strDown({amount}x{savingsHits}h)=+{v}");
        }
        else if (targets == 0)
        {
            // No attacking enemy in scope — debuff is wasted this turn.
            // (Could still pay off next turn if duration persists, but most
            // STRENGTH_DOWN cards are "this turn only" — accept the loss.)
            b -= 200;
            parts.Add("strDownNoAtk=-200");
        }
    }

    private static void ApplyHeal(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int amount = self.Effect.HealAmount;
        if (amount <= 0) return; // MaxHp-gain cards (BRIGHTEST_FLAME / FEED) don't fire here

        // Heal value = HP that would have been lost on next enemy turn, capped
        // by amount. PredictPlayerDmg already accounts for block / Intangible.
        int incoming = EnemyTurnSimulator.PredictPlayerDmg(state);
        int hpAtRisk = System.Math.Min(amount, incoming);

        // Survival-aware: low HP heal is precious, full HP is wasted.
        if (state.PlayerHp <= 20 && hpAtRisk > 0)
        {
            int v = hpAtRisk * 40;
            b += v;
            parts.Add($"healLowHp({hpAtRisk})=+{v}");
        }
        else if (state.PlayerHp <= 40 && hpAtRisk > 0)
        {
            int v = hpAtRisk * 25;
            b += v;
            parts.Add($"healMidHp({hpAtRisk})=+{v}");
        }
        else if (hpAtRisk > 0)
        {
            int v = hpAtRisk * 12;
            b += v;
            parts.Add($"healPrevent({hpAtRisk})=+{v}");
        }
        else
        {
            // No incoming damage and HP not critical — heal is purely cosmetic.
            // Mild penalty so other plays beat NOT_YET on full-HP turns.
            b -= 150;
            parts.Add("healWaste=-150");
        }
    }

    // v0.6.9 — Tier 1 patches: STATUS_TO_HAND / STATUS_CONSUMER / MaxHp.

    private static void ApplyStatusToHandPenalty(SimCard card, SimState state, ref int b, List<string> parts)
    {
        // CRASH_LANDING "fills hand with Wreckage" → far worse than dropping 1.
        // Catalog identifies AOE_DAMAGE + STATUS_TO_HAND as the hand-fill case.
        // Single-status cards (COLLISION_COURSE) only add 1.
        bool fillsHand = card.Axes.Contains("AOE_OTHER")
                      || card.Axes.Contains("AOE_DAMAGE");
        int basePenalty = fillsHand ? -350 : -150;

        // Consumer-aware adjustment. Converters (COMPACT → Fuel+, GUARDS →
        // Minion Sacrifice+) erase the status entirely and turn the polluted
        // hand into 0-cost assets, so flip the penalty to a small upside.
        // Plain STATUS_CONSUMER cards (ROCKET_PUNCH, FLAK_CANNON) only pay off
        // when status is present — they don't erase it, so halve the penalty.
        bool hasConverter = false;
        bool hasConsumer  = false;
        if (state?.Hand != null)
        {
            for (int i = 0; i < state.Hand.Count; i++)
            {
                var c = state.Hand[i];
                if (ReferenceEquals(c, card)) continue;
                if (!c.IsPlayable) continue;
                // SimCard.Id is the base entry name regardless of upgrade
                // (enchantment is tracked separately). Base name covers both.
                if (c.Id == "COMPACT" || c.Id == "GUARDS")
                {
                    hasConverter = true;
                }
                else if (c.Axes != null && c.Axes.Contains("STATUS_CONSUMER"))
                {
                    hasConsumer = true;
                }
            }
        }

        int penalty;
        string tag;
        if (hasConverter)
        {
            penalty = -basePenalty / 2;   // flip + half — timing not guaranteed
            tag     = "statusToHandConverted";
        }
        else if (hasConsumer)
        {
            penalty = basePenalty / 2;
            tag     = "statusToHandConsumed";
        }
        else
        {
            penalty = basePenalty;
            tag     = "statusToHand";
        }
        b += penalty;
        parts.Add($"{tag}={penalty}");
    }

    private static void ApplyStatusConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count current status / curse cards in hand. ROCKET_PUNCH gains cost
        // reduction; FLAK_CANNON deals 8 per exhausted; COMPACT converts.
        int statusInHand = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) statusInHand++;
        }
        if (statusInHand > 0)
        {
            int v = System.Math.Min(statusInHand * 180, 540);
            b += v;
            parts.Add($"statusInHand({statusInHand})=+{v}");
        }
        else
        {
            // No status — consumer's payoff is dormant. Light penalty only;
            // most STATUS_CONSUMER cards have raw damage/block independent value.
            b -= 100;
            parts.Add("statusNoSource=-100");
        }
    }

    private static void ApplyMaxHpGain(SimCard card, ref int b, List<string> parts)
    {
        // Permanent MaxHp gain. Single-combat value is small (no immediate
        // tempo); over a run, +HP compounds. Flat per-point bonus.
        int v = card.Effect.MaxHpAmount * 40;
        b += v;
        parts.Add($"maxHp(+{card.Effect.MaxHpAmount})=+{v}");
    }

    private static void ApplyDrawConditional(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Generic per-card draw bonus when condition likely holds. Without
        // parsing card text, we use card-id gating for the high-impact cases
        // and a generic mid-magnitude bonus otherwise (better than 0).
        int cardsThisTurn = state.TurnAttacksPlayed + state.TurnSkillsPlayed;
        int v = 0;
        switch (self.Id)
        {
            case "FTL":
                // "Draw 1 if < 3 cards used this turn."
                if (cardsThisTurn < 3) v = 200;
                else v = -50;   // condition missed, draw won't fire
                break;
            case "PALE_BLUE_DOT":
                // "If ≥ 5 cards used, +1 next-turn draw" — Power scaling. Pays
                // off later in fight; modest bonus.
                if (cardsThisTurn >= 4) v = 200;     // about to qualify next play
                else v = 100;
                break;
            case "FETCH":
                // "Draw 1 if this is the first FETCH used this turn." History
                // would need a per-card-id play counter; assume true since
                // most decks have ≤1 FETCH.
                v = 180;
                break;
            case "COMPILE_DRIVER":
                // "Draw 1 per distinct orb kind currently channeled."
                int variety = 0;
                bool seenF = false, seenL = false, seenD = false, seenP = false, seenG = false;
                foreach (var k in state.OrbQueue)
                {
                    if (k == OrbKind.Frost && !seenF)     { seenF = true; variety++; }
                    if (k == OrbKind.Lightning && !seenL) { seenL = true; variety++; }
                    if (k == OrbKind.Dark && !seenD)      { seenD = true; variety++; }
                    if (k == OrbKind.Plasma && !seenP)    { seenP = true; variety++; }
                    if (k == OrbKind.Glass && !seenG)     { seenG = true; variety++; }
                }
                v = variety * 130;
                break;
            default:
                // Generic — most DRAW_CONDITIONAL cards yield ~1 card on average.
                v = 100;
                break;
        }
        if (v != 0)
        {
            b += v;
            parts.Add($"drawCond({self.Id.Replace("CARD.","")})={v:+#;-#;0}");
        }
    }

    /// <summary>
    /// v0.7.1 — Context-free card value estimate. Used by Level-3 (pile-based)
    /// random handlers to compute realistic expected value of pulling /
    /// playing cards from <see cref="SimState.DrawPile"/> or
    /// <see cref="SimState.DiscardPile"/>. Returns 0+ score (Curse/Status
    /// negative); roughly half of a typical PlanScorer single-card score
    /// since we lack target / state context.
    ///
    /// <para><paramref name="freeUse"/> distinguishes:
    /// <list type="bullet">
    ///   <item>true — card auto-played from pile (CASCADE / CATASTROPHE /
    ///     BEAT_DOWN / UPROAR). Cost ignored, full damage/block credit.</item>
    ///   <item>false — card added to hand (DREDGE / NEOWS_FURY / WISH).
    ///     Cost reduces value, raw EnergyGain useful for future turn only.</item>
    /// </list></para>
    /// </summary>
    internal static int EstimateCardPower(SimCard c, SimState state, bool freeUse)
    {
        if (c.IsCurseOrStatus)
            return freeUse ? EffectScoringWeights.CurseFree : EffectScoringWeights.CurseInHand;

        int v = 0;
        if (c.IsAttack)
            v += c.TotalDamage * (freeUse ? EffectScoringWeights.DamageFree : EffectScoringWeights.DamageInHand);
        if (c.Block > 0)
            v += c.Block * (freeUse ? EffectScoringWeights.BlockFree : EffectScoringWeights.BlockInHand);
        v += c.DrawCount * EffectScoringWeights.Draw;
        v += c.EnergyGain * (freeUse ? EffectScoringWeights.EnergyFree : EffectScoringWeights.EnergyInHand);

        foreach (var (powerName, amount) in c.PowerApps)
        {
            int pVal = System.Math.Max(
                PowerCatalog.ValueSelfBuff(powerName, amount),
                PowerCatalog.ValueEnemyDebuff(powerName, amount));
            // Heavy discount — context-free; can't tell whether the target
            // would actually benefit / the debuff would actually land.
            v += pVal / (freeUse ? EffectScoringWeights.PowerDivisorFree : EffectScoringWeights.PowerDivisorInHand);
        }

        if (!freeUse)
        {
            if (c.Cost == 0) v += EffectScoringWeights.Cost0Bonus;
            else if (c.Cost == 1) v += EffectScoringWeights.Cost1Bonus;
            else if (c.Cost >= 3) v += EffectScoringWeights.Cost3PlusPenalty;
        }

        // v0.7.8 — Self-damage deduction. Cards expose HpLoss via DynamicVar
        // (BLOODLETTING 3, OFFERING 6, HEMOKINESIS 2, BREAKTHROUGH 1 etc.).
        // Per-HP penalty bands rise as the player's HP buffer shrinks — full
        // HP makes self-damage cheap, sub-25 HP makes it nearly suicidal.
        // Free-use plays (auto-played from pile) still incur HP loss in-game
        // so the deduction applies regardless of freeUse.
        if (c.HpLossAmount > 0)
        {
            int penaltyPerHp;
            if (state.PlayerHp > 60) penaltyPerHp = 12;
            else if (state.PlayerHp > 40) penaltyPerHp = 30;
            else if (state.PlayerHp > 25) penaltyPerHp = 70;
            else penaltyPerHp = 200;
            v -= c.HpLossAmount * penaltyPerHp;
        }

        // Floor at curse-equivalent so extreme HP-loss penalties don't push
        // EV computations into unbounded negatives that would dominate
        // pile-mean handlers (DREDGE / CASCADE / MAYHEM tick etc.).
        int floor = freeUse ? EffectScoringWeights.CurseFree : EffectScoringWeights.CurseInHand;
        return System.Math.Max(floor, v);
    }

    private static void ApplyCardReturn(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // v0.7.1 — Pile-based random / chooser cards. Compute expected value
        // from actual SimState.DiscardPile contents (per-card EstimateCardPower)
        // instead of flat per-card-id magnitudes.
        switch (self.Id)
        {
            case "DREDGE":
            {
                // Player CHOOSES up to 3 from discard. Take top-3 positives —
                // player skips curses / status when given the choice.
                int n = System.Math.Min(3, state.DiscardPile.Count);
                if (n == 0) { b -= 50; parts.Add("dredgeEmpty=-50"); break; }
                var ranked = new List<int>(state.DiscardPile.Count);
                foreach (var c in state.DiscardPile) ranked.Add(EstimateCardPower(c, state, freeUse: false));
                ranked.Sort((x, y) => y.CompareTo(x));
                int v = 0, taken = 0;
                foreach (int p in ranked)
                {
                    if (p <= 0) break;            // descending — rest are negative
                    if (taken >= n) break;
                    v += p; taken++;
                }
                b += v;
                parts.Add($"dredgeBest{taken}=+{v}");
                break;
            }
            case "NEOWS_FURY":
            {
                // 2 random from discard (no choice). Mean × 2.
                int n = System.Math.Min(2, state.DiscardPile.Count);
                if (n == 0) { b -= 50; parts.Add("furyEmpty=-50"); break; }
                int sum = 0;
                foreach (var c in state.DiscardPile) sum += EstimateCardPower(c, state, freeUse: false);
                int mean = sum / state.DiscardPile.Count;
                int v = mean * n;
                b += v;
                parts.Add($"furyMean({mean})×{n}=+{v}");
                break;
            }
            case "AGGRESSION":
            {
                // v0.7.4 — Aligned with v0.7.3 MAYHEM pattern. Power passive:
                // at start of each turn, return a random Attack from discard
                // to hand, upgraded for this turn. PowerCatalog["AggressionPower"]
                // is the baked baseline credited via the PlanScorer Power branch
                // (runtime PowerVar<AggressionPower> populates card.PowerApps);
                // EffectSynergy layers a delta proportional to
                //   (discard.attack_mean × UpgradeFactor × RemainingTurnsProxy) − baked.
                //
                // freeUse=false because the recalled Attack goes to hand and the
                // player still pays its cost. UpgradeFactor approximates the
                // temporary upgrade applied to the recalled card (damage +30%
                // / cost -1 typical net effect).
                int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);
                const double UpgradeFactor = 1.3;
                const int Cap = 1200;
                int baked = PowerCatalog.LookupSelfBuff("AggressionPower");

                int sum = 0, count = 0;
                foreach (var c in state.DiscardPile)
                {
                    if (!c.IsAttack) continue;
                    sum += EstimateCardPower(c, state, freeUse: false);
                    count++;
                }
                if (count == 0)
                {
                    // Empty / no-Attack discard — Power still pays off once
                    // an Attack lands in discard. Small positive nudge.
                    b += 80;
                    parts.Add("aggrNoAttacks=+80");
                    break;
                }
                int mean = sum / count;
                int tickEstimate = (int)(mean * UpgradeFactor * RemainingTurnsProxy);
                int delta = tickEstimate - baked;
                if (delta > Cap) delta = Cap;
                if (delta < -baked) delta = -baked;

                b += delta;
                parts.Add($"aggrTick(mean={mean}x{UpgradeFactor}x{RemainingTurnsProxy}={tickEstimate},baked={baked})={delta:+#;-#;0}");
                break;
            }
            case "NOSTALGIA":
            {
                // v0.7.5 — Power passive: each turn, the first Attack/Skill you
                // play moves to the top of the draw pile (you replay it next
                // turn). Approximate as a Retain-like discount on hand
                // Attack/Skill mean × remaining turns. baked = PowerCatalog.
                int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);
                const double RetainDiscount = 0.4;  // ~40% extra value per kept card
                const int Cap = 800;
                int baked = PowerCatalog.LookupSelfBuff("NostalgiaPower");

                int sum = 0, cnt = 0;
                foreach (var c in state.Hand)
                {
                    if (ReferenceEquals(c, self)) continue;
                    if (c.IsCurseOrStatus) continue;
                    if (!c.IsAttack && !c.IsSkill) continue;
                    sum += EstimateCardPower(c, state, freeUse: false);
                    cnt++;
                }
                if (cnt == 0) { b += 50; parts.Add("nostalgiaNoTargets=+50"); break; }
                int mean = sum / cnt;
                int tick = (int)(mean * RetainDiscount * RemainingTurnsProxy);
                int delta = tick - baked;
                if (delta > Cap) delta = Cap;
                if (delta < -baked) delta = -baked;
                b += delta;
                parts.Add($"nostalgiaTick(mean={mean}x{RetainDiscount}x{RemainingTurnsProxy}={tick},baked={baked})={delta:+#;-#;0}");
                break;
            }
            case "STRATAGEM":
            {
                // v0.7.5 — Power passive: when draw pile empties (reshuffle), a
                // random card from the new draw pile is moved to hand. Value
                // scales with DiscardPile mean (= future draw pile) × expected
                // reshuffles per combat (~2). Curses/Status counted in the mean
                // since they're equally likely to be grabbed.
                const int ReshuffleProxy = 2;
                const int Cap = 800;
                int baked = PowerCatalog.LookupSelfBuff("StratagemPower");

                if (state.DiscardPile.Count == 0)
                {
                    // No reshuffle has happened yet — fall back on a small
                    // positive baseline so STRATAGEM isn't penalized on turn 1.
                    b += 80;
                    parts.Add("stratagemEmptyDiscard=+80");
                    break;
                }

                int sum = 0;
                foreach (var c in state.DiscardPile)
                    sum += EstimateCardPower(c, state, freeUse: false);
                int mean = sum / state.DiscardPile.Count;
                int tick = mean * ReshuffleProxy;
                int delta = tick - baked;
                if (delta > Cap) delta = Cap;
                if (delta < -baked) delta = -baked;
                b += delta;
                parts.Add($"stratagemTick(mean={mean}x{ReshuffleProxy}={tick},baked={baked})={delta:+#;-#;0}");
                break;
            }
            case "PHOTON_CUT":
            case "GLIMMER":
            {
                // v0.7.45 — Hand → top-of-draw scales with hand quality. Player
                // picks the BEST hand card to top-deck, guaranteeing it as next
                // turn's first draw. Base damage / draw already valued.
                int bestHandScore = 0;
                foreach (var c in state.Hand)
                {
                    if (ReferenceEquals(c, self)) continue;
                    if (c.IsCurseOrStatus || !c.IsPlayable) continue;
                    int v = EstimateCardPower(c, state, freeUse: false);
                    if (v > bestHandScore) bestHandScore = v;
                }
                // Effective top-deck guarantee = ~30% of best card's value
                // (covers "definitely drawn next turn" vs "might draw anyway")
                // Cap at 400 so a high-cost Power doesn't make this card overpriced.
                int bonus = System.Math.Min(400, (int)(bestHandScore * 0.30));
                if (bonus < 100) bonus = 100;  // floor — at least the old constant
                b += bonus;
                parts.Add($"topDeck(bestHand={bestHandScore}*.3=+{bonus})");
                break;
            }
            case "ANOINTED":
            {
                // "All Rare cards from draw pile to hand". We don't know rarity
                // per card statically; use draw-pile-size as a proxy.
                int v = state.DrawPile.Count > 5 ? 280 : 100;
                b += v;
                parts.Add($"anointed=+{v}");
                break;
            }
            default:
                b += 100;
                parts.Add("cardReturn=+100");
                break;
        }
    }

    private static void ApplyDrawPileSearch(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // v0.7.1 — Pile-based draw-pile-search cards. Use actual SimState.DrawPile
        // contents for realistic value.
        if (state.DrawPile.Count == 0)
        {
            // v0.9 — bumped from -100 to -400. The old -100 still left FOREGONE
            // / CHARGE / etc. at a small positive total (base 100 + other bonuses
            // - 100 ≈ +50), which the 2-step lookahead happily picked because
            // any positive first-card is allowed to win when the depth-2 chain
            // beats alternatives. Result: AI burned 1 energy on a dead-cycle
            // card and could not afford a Retain SOVEREIGN_BLADE later (see
            // logs 2026-05-19 19:24, turn 4 step 3). At -400 the empty-pile
            // search drops well below MinPlayScore so the rule at
            // ActionPlanner line 262 ("positive-first beats negative-first")
            // pushes any real card ahead of it.
            b -= 400;
            parts.Add("pileSearchEmpty=-400");
            return;
        }

        switch (self.Id)
        {
            case "CHARGE":
            {
                // Player CHOOSES 2 from draw pile and TRANSFORMS them into
                // Minion Dive Bombs+ (0c, 16 dmg, Exhaust). Each transform
                // gain = Minion Dive Bombs+ value − selected card value.
                // Maximized by picking the WORST 2 cards (curses are huge
                // wins; basic Strike is mild upgrade; high-value Power is
                // a loss). Sort ASCENDING and take the bottom N.
                int n = System.Math.Min(2, state.DrawPile.Count);
                if (n == 0) { b -= 50; parts.Add("chargeEmpty=-50"); break; }
                var ranked = new List<int>(state.DrawPile.Count);
                foreach (var c in state.DrawPile) ranked.Add(EstimateCardPower(c, state, freeUse: false));
                ranked.Sort((x, y) => x.CompareTo(y));
                // Minion Dive Bombs+ EV: 16 dmg × DamageFree(50) = ~800.
                // Exhaust + 0c means the card is essentially a free attack
                // when it surfaces — full credit (no cost penalty).
                const int MinionDiveBombsValue = 16 * EffectScoringWeights.DamageFree;
                int totalLoss = 0, transformed = 0;
                for (int i = 0; i < ranked.Count && transformed < n; i++)
                {
                    totalLoss += ranked[i];
                    transformed++;
                }
                int totalGain = transformed * MinionDiveBombsValue;
                int v = totalGain - totalLoss;
                b += v;
                parts.Add($"chargeTransform({transformed}×{MinionDiveBombsValue}-loss{totalLoss}={v})");
                break;
            }
            case "FOREGONE_CONCLUSION":
            {
                // v0.7.47 — Player CHOOSES 2 from draw next turn. Use top-2
                // positives × 0.75 (next-turn delay discount).
                if (state.DrawPile.Count == 0) { b -= 50; parts.Add("foregoneEmpty=-50"); break; }
                var ranked = new List<int>(state.DrawPile.Count);
                foreach (var c in state.DrawPile) ranked.Add(EstimateCardPower(c, state, freeUse: false));
                ranked.Sort((x, y) => y.CompareTo(x));
                int sum = 0, taken = 0;
                foreach (int p in ranked)
                {
                    if (p <= 0) break;
                    if (taken >= 2) break;
                    sum += p; taken++;
                }
                int v = (int)(sum * 0.75);
                b += v;
                parts.Add($"foregoneBest{taken}*0.75=+{v}");
                break;
            }
            case "ANOINTED":
            {
                // v0.7.47 — "All Rare cards from draw to hand. Exhaust."
                // We don't have rarity at runtime, but high-EstimateCardPower
                // values usually correlate with rare cards. Use top-3 average
                // as proxy for "rare cards" pulled.
                if (state.DrawPile.Count == 0) { b -= 50; parts.Add("anointedEmpty=-50"); break; }
                var ranked = new List<int>(state.DrawPile.Count);
                foreach (var c in state.DrawPile) ranked.Add(EstimateCardPower(c, state, freeUse: false));
                ranked.Sort((x, y) => y.CompareTo(x));
                int sum = 0, taken = 0;
                foreach (int p in ranked)
                {
                    if (p <= 250) break;  // threshold: high-value proxy for rare
                    if (taken >= 4) break;
                    sum += p; taken++;
                }
                int v = sum > 0 ? sum : 100;  // floor 100 if no rare-proxy hits
                b += v;
                parts.Add($"anointed(rareProxy{taken}=+{v})");
                break;
            }
            case "WISH":
            {
                // Player CHOOSES 1 from draw. Pure max-of-pile.
                int best = 0;
                foreach (var c in state.DrawPile)
                {
                    int p = EstimateCardPower(c, state, freeUse: false);
                    if (p > best) best = p;
                }
                b += best;
                parts.Add($"wishBest=+{best}");
                break;
            }
            default:
                b += 150;
                parts.Add("pileSearch=+150");
                break;
        }
    }

    /// <summary>
    /// v0.7.1 — Auto-play-from-pile cards. CASCADE / CATASTROPHE (draw pile)
    /// + BEAT_DOWN / UPROAR (filtered: Attack only). Uses pile contents to
    /// compute expected value × N with `freeUse: true` (no cost paid by player).
    /// </summary>
    private static void ApplyAutoPlayFromPile(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // NOTE: SimCard.Id is the unprefixed Id.Entry (e.g. "CASCADE", not
        // "CASCADE") — verified at CardReflection.cs:118. Earlier code in
        // this switch used "CARD." prefix and silently never matched (dead).
        // Keep all cases below unprefixed.
        switch (self.Id)
        {
            case "CASCADE":
            {
                // X+1 cards from draw pile auto-played. X = energy spent.
                if (state.DrawPile.Count == 0) { b -= 200; parts.Add("cascadeEmpty=-200"); return; }
                int n = System.Math.Max(1, state.PlayerEnergy) + 1;
                int sum = 0, cnt = 0;
                foreach (var c in state.DrawPile)
                {
                    if (c.IsCurseOrStatus) continue;
                    sum += EstimateCardPower(c, state, freeUse: true);
                    cnt++;
                }
                if (cnt == 0) { b -= 200; parts.Add("cascadeNoPlayable=-200"); return; }
                int mean = sum / cnt;
                int v = mean * n;
                b += v;
                parts.Add($"cascade(mean{mean})×{n}=+{v}");
                break;
            }
            case "CATASTROPHE":
            {
                // 2 random non-Unplayable from draw, auto-played.
                if (state.DrawPile.Count == 0) { b -= 200; parts.Add("catastropheEmpty=-200"); return; }
                int n = 2;
                int sum = 0, cnt = 0;
                foreach (var c in state.DrawPile)
                {
                    if (c.IsCurseOrStatus) continue;
                    sum += EstimateCardPower(c, state, freeUse: true);
                    cnt++;
                }
                if (cnt == 0) { b -= 200; parts.Add("catastropheNoPlayable=-200"); return; }
                int mean = sum / cnt;
                int v = mean * n;
                b += v;
                parts.Add($"catastrophe(mean{mean})×{n}=+{v}");
                break;
            }
            case "UPROAR":
            {
                // Base 5×2 damage already in Damage score. Plus 1 random Attack
                // from draw auto-played.
                int sum = 0, cnt = 0;
                foreach (var c in state.DrawPile)
                {
                    if (!c.IsAttack || c.IsCurseOrStatus) continue;
                    sum += EstimateCardPower(c, state, freeUse: true);
                    cnt++;
                }
                if (cnt == 0) { b += 50; parts.Add("uproarNoAttack=+50"); return; }
                int mean = sum / cnt;
                b += mean;
                parts.Add($"uproarAtk(mean{mean})=+{mean}");
                break;
            }
            case "BEAT_DOWN":
            {
                // 3 random Attacks from DISCARD pile, auto-played.
                int sum = 0, cnt = 0;
                foreach (var c in state.DiscardPile)
                {
                    if (!c.IsAttack || c.IsCurseOrStatus) continue;
                    sum += EstimateCardPower(c, state, freeUse: true);
                    cnt++;
                }
                if (cnt == 0) { b -= 100; parts.Add("beatDownNoAttack=-100"); return; }
                int mean = sum / cnt;
                int n = 3;
                int v = mean * n;
                b += v;
                parts.Add($"beatDown(mean{mean})×{n}=+{v}");
                break;
            }
            case "HAVOC":
            {
                // 1-energy: play top of draw pile, then exhaust it. The value
                // depends entirely on the draw pile composition:
                //   • Curse/Status on top → wasted energy AND exhausted (so the
                //     curse leaves the deck — small upside).
                //   • Average non-curse card → mean(freeUse value) of pile.
                //   • Plus: the auto-played card is exhausted afterwards, so
                //     subtract per-card keystone risk (averaged across pile).
                //
                // Pile size matters: with a tiny pile the variance is huge
                // (could draw a Power or a Strike), but with a large pile the
                // mean is a tight estimate.
                if (state.DrawPile.Count == 0)
                {
                    b -= 200;
                    parts.Add("havocEmpty=-200");
                    return;
                }

                int sum = 0;
                int cnt = 0;
                int curseCnt = 0;
                int riskSum = 0;
                foreach (var c in state.DrawPile)
                {
                    if (c.IsCurseOrStatus) { curseCnt++; continue; }
                    sum += EstimateCardPower(c, state, freeUse: true);
                    riskSum += EstimateExhaustLossRisk(c);
                    cnt++;
                }

                int pileSize = cnt + curseCnt;
                if (cnt == 0)
                {
                    // Entire pile is curses/status. HAVOC plays one → wasted
                    // energy. But the curse leaves the deck (auto-exhaust),
                    // so small thinning credit instead of a flat penalty.
                    b += 40;
                    parts.Add($"havocCurseThin(pile{pileSize})=+40");
                    return;
                }

                // Expected value of the auto-played card. P(non-curse) × mean
                // + P(curse) × curse-value(small thinning credit).
                int mean = sum / cnt;
                int curseValue = 40;   // small upside from removing curse
                int evPlayed = (mean * cnt + curseValue * curseCnt) / pileSize;

                // Expected keystone loss from exhausting the auto-played card.
                // Curses have negative risk (good to exhaust); already baked
                // into curseValue above, so only the non-curse risk applies
                // proportionally.
                int meanRisk = riskSum / cnt;
                int evRisk = (meanRisk * cnt) / pileSize;

                int v = evPlayed - evRisk;
                b += v;
                parts.Add($"havoc(ev{evPlayed}-risk{evRisk},pile{pileSize},curse{curseCnt})=+{v}");
                break;
            }
        }
    }

    /// <summary>
    /// v0.7.1 — HIDDEN_GEM-style: random card in DrawPile gets a passive
    /// modifier (Retain in HIDDEN_GEM's case). Value scales with pile content.
    /// </summary>
    private static void ApplyDrawPileRandomModifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        switch (self.Id)
        {
            case "HIDDEN_GEM":
            {
                // Random non-Unplayable, non-Power/Status card in draw gets
                // Retain 2 (carries over turns). Value = (avg pile power) × 0.3
                // — Retain is roughly "1 extra turn of usefulness on 1 card".
                if (state.DrawPile.Count == 0) { b -= 200; parts.Add("gemEmpty=-200"); return; }
                int sum = 0, cnt = 0;
                foreach (var c in state.DrawPile)
                {
                    if (c.IsCurseOrStatus || c.IsPower) continue;
                    sum += EstimateCardPower(c, state, freeUse: false);
                    cnt++;
                }
                if (cnt == 0) { b -= 100; parts.Add("gemNoTarget=-100"); return; }
                int mean = sum / cnt;
                int v = (int)(mean * 0.6);  // 2 retain stacks ≈ 60% extra value
                b += v;
                parts.Add($"hiddenGem(mean{mean})=+{v}");
                break;
            }
            case "DRAIN_POWER":
            {
                // 2 random upgradable cards in DISCARD get upgraded. Upgrade
                // is roughly +15-20% card value. Use discard average.
                int sum = 0, cnt = 0;
                foreach (var c in state.DiscardPile)
                {
                    if (c.IsCurseOrStatus) continue;
                    sum += EstimateCardPower(c, state, freeUse: false);
                    cnt++;
                }
                if (cnt == 0) { b += 0; return; }
                int mean = sum / cnt;
                int v = (int)(mean * 0.4);    // 2 cards × ~20% upgrade
                b += v;
                parts.Add($"drainUpgrade(mean{mean})=+{v}");
                break;
            }
        }
    }

    /// <summary>
    /// v0.7.3 — MAYHEM Power passive: at the start of every turn, the top
    /// card of the draw pile is auto-played. <see cref="PowerCatalog"/>'s
    /// flat <c>MayhemPower</c> value is the baseline already credited by the
    /// PlanScorer Power branch via <c>card.PowerApps</c>; this handler layers
    /// a <i>delta</i> proportional to (DrawPile mean × remaining-turns proxy −
    /// baseline) so a deck rich in heavy hitters scores significantly higher
    /// than a Curse-polluted deck.
    ///
    /// freeUse=true is the correct EstimateCardPower mode — auto-plays don't
    /// charge energy. Curses / Status are intentionally included in the mean:
    /// their -100 free-use penalty lets a pile-polluted deck pull MAYHEM's
    /// score down to neutral, matching the real-game risk of the Power.
    /// </summary>
    private static void ApplyMayhemTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);
        const int Cap = 1200;

        // PowerCatalog is the single source for the baseline so a tweak to
        // MayhemPower's flat value automatically retunes this delta — no
        // duplicated magic number to drift.
        int baked = PowerCatalog.LookupSelfBuff("MayhemPower");

        if (state.DrawPile.Count == 0)
        {
            // Empty pile still has long-term value once the discard reshuffles.
            // Small positive nudge (don't fully zero the Power out).
            b += 80;
            parts.Add("mayhemEmptyPile=+80");
            return;
        }

        int sum = 0;
        foreach (var c in state.DrawPile)
            sum += EstimateCardPower(c, state, freeUse: true);
        int mean = sum / state.DrawPile.Count;

        int tickEstimate = mean * RemainingTurnsProxy;
        int delta = tickEstimate - baked;

        if (delta > Cap) delta = Cap;
        // Floor: never subtract more than the baked baseline so MAYHEM's total
        // PowerCatalog-derived value can't go negative from this handler alone.
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"mayhemTick(mean={mean}x{RemainingTurnsProxy}={tickEstimate},baked={baked})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.5 — STAMPEDE Power passive: turn-end auto-play of a random Attack
    /// from the DrawPile. MAYHEM-pattern delta — but filtered to Attacks only
    /// (the Power explicitly skips non-Attack cards) and free-use, since
    /// auto-played cards pay no energy.
    /// </summary>
    private static void ApplyStampedeTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);
        const int Cap = 1200;
        int baked = PowerCatalog.LookupSelfBuff("StampedePower");

        int sum = 0, cnt = 0;
        foreach (var c in state.DrawPile)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            sum += EstimateCardPower(c, state, freeUse: true);
            cnt++;
        }
        if (cnt == 0)
        {
            // No Attacks in draw — Power still pays after the next Attack lands
            // in the pile (most decks shuffle attacks back). Modest baseline.
            b += 80;
            parts.Add("stampedeNoAttacks=+80");
            return;
        }
        int mean = sum / cnt;
        int tick = mean * RemainingTurnsProxy;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"stampedeTick(mean={mean}x{RemainingTurnsProxy}={tick},baked={baked})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.5 — CALAMITY Power passive: each time you finish playing an Attack,
    /// a random Attack from the character pool is added to your hand. Value
    /// scales with the character pool's Attack mean (PoolMeans) × expected
    /// chain procs over the remaining combat. Falls back to a flat baseline
    /// when the character id isn't captured (snapshot timing).
    /// </summary>
    private static void ApplyCalamityTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // EAC = 3 — chained attacks share the energy budget with normal plays;
        // realistic net "extra" attacks resolved per combat is small (~3) once
        // energy/hand-cap dilution is accounted for. EAC=6 (raw chain count)
        // saturated the cap on every character, eliminating discrimination.
        const int ExpectedAttackChains = 3;
        const int Cap = 1500;
        int baked = PowerCatalog.LookupSelfBuff("CalamityPower");

        if (string.IsNullOrEmpty(state.CharacterId)) return;  // flat path stays
        var pool = PoolMeans.Get(state.CharacterId, "attack");
        if (pool.N == 0) return;

        int tick = pool.Mean * ExpectedAttackChains;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"calamityTick(poolAtkMean={pool.Mean}x{ExpectedAttackChains}={tick},baked={baked})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.5 — HELLRAISER Power passive: drawing a Strike-named card auto-plays
    /// it at random target. Value = (Strike count across all piles) × per-Strike
    /// energy-saving bonus, since the Strike is played without spending its
    /// cost. Strike identification uses id Contains "STRIKE" to catch
    /// CARD.STRIKE, CARD.WILD_STRIKE, CARD.SWORD_BOOMERANG, etc.
    /// </summary>
    private static void ApplyHellraiserTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int PerStrikeBonus = 90;   // free-play premium per Strike auto-play (~6dmg + cost1 saved)
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("HellraiserPower");

        int strikes = CountStrikes(state.Hand)
                    + CountStrikes(state.DrawPile)
                    + CountStrikes(state.DiscardPile);
        if (strikes == 0)
        {
            // Strike-less deck — Power is dead weight. Strip the baked bonus.
            b -= baked;
            parts.Add($"hellraiserNoStrikes=-{baked}");
            return;
        }
        int tick = strikes * PerStrikeBonus;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"hellraiserTick(strikes={strikes}x{PerStrikeBonus}={tick},baked={baked})={delta:+#;-#;0}");
    }

    private static int CountStrikes(IReadOnlyList<SimCard> pile)
    {
        int n = 0;
        if (pile == null) return 0;
        foreach (var c in pile)
            if (c.Id != null && c.Id.Contains("STRIKE")) n++;
        return n;
    }

    /// <summary>
    /// v0.7.5 — JUGGLING Power passive: at end of each turn, if you played
    /// 3+ Attacks, a copy of the 3rd Attack is added to hand. Value scales
    /// with hand Attack mean × turns × hit-rate proxy (probability of
    /// reaching the 3-attack threshold — ~40% in mixed decks).
    /// </summary>
    private static void ApplyJugglingTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);
        const double HitRate = 0.4;    // fraction of turns we play 3+ attacks
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("JugglingPower");

        int sum = 0, cnt = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsAttack) continue;
            sum += EstimateCardPower(c, state, freeUse: false);
            cnt++;
        }
        if (cnt == 0)
        {
            // No attacks in hand — fall back to a small positive baseline
            // (next draw might fix it). Strip the baked premium otherwise.
            b += 40;
            parts.Add("jugglingNoAttacks=+40");
            return;
        }
        int mean = sum / cnt;
        int tick = (int)(mean * RemainingTurnsProxy * HitRate);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"jugglingTick(handAtkMean={mean}x{RemainingTurnsProxy}x{HitRate}={tick},baked={baked})={delta:+#;-#;0}");
    }

    // ─── v0.7.26 — Per-turn / trigger-based Power passives ─────────────────────
    //
    // Each handler reads state, projects expected per-turn ticks across the
    // remaining-combat horizon, then commits a clamped delta vs the PowerCatalog
    // baseline. Pattern intentionally mirrors MAYHEM/STAMPEDE/CALAMITY so a
    // tuning change to one is recognizable across the others.

    /// <summary>
    /// v0.7.26 — DarkEmbracePower (Ironclad, A-tier): exhaust 시 카드 1장 draw.
    /// Value scales with exhaust frequency proxy: in-hand EXHAUST_SELF/EXHAUST
    /// axis cards + estimated future exhausts from deck × remaining turns.
    /// </summary>
    private static void ApplyDarkEmbraceTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerExhaustDraw = 200;   // approx value of a free draw
        const int Cap = 900;
        int baked = PowerCatalog.LookupSelfBuff("DarkEmbracePower");

        int handExhausts = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("EXHAUST_SELF") || c.Axes.Contains("EXHAUST")) handExhausts++;
        }
        int deckExhausts = 0;
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("EXHAUST_SELF") || c.Axes.Contains("EXHAUST")))
                deckExhausts++;

        // Estimate exhausts this combat: current hand (likely played) + ~30% of
        // draw-pile exhausts per turn for remaining turns, capped at deck total.
        int futureExhausts = handExhausts + System.Math.Min(deckExhausts, (int)(deckExhausts * 0.3 * turns));
        int tick = futureExhausts * PerExhaustDraw;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"darkEmbraceTick(exh={handExhausts}+~{futureExhausts - handExhausts},perDraw={PerExhaustDraw})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — ViciousPower (Ironclad, B): Vuln 적용 시 1 draw. Hand 내
    /// VULN_PRODUCER × per-vuln-draw × turns.
    /// </summary>
    private static void ApplyViciousTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerVulnDraw = 180;
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("ViciousPower");

        int vulnProducers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("VULN_PRODUCER")) vulnProducers++;
        }
        int deckVulnProducers = 0;
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("VULN_PRODUCER")) deckVulnProducers++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && c.Axes.Contains("VULN_PRODUCER")) deckVulnProducers++;

        // Hand producers are likely-played; deck producers spread over turns.
        int proj = vulnProducers + (deckVulnProducers * turns) / 5;  // ~1/5 cycle per turn
        int tick = proj * PerVulnDraw;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"viciousTick(vulnProd={vulnProducers}+deck~{deckVulnProducers}/5={proj})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — AccelerantPower (Silent, B): Poison apply 시 +1 추가 stack.
    /// Hand 내 POISON_PRODUCER + RemainingTurns. Each producer's poison gets
    /// +1 effective stack, worth ~PoisonPower (700) baseline / 4 stack curve.
    /// </summary>
    private static void ApplyAccelerantTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerPoisonProc = 120;   // +1 extra stack value (1 dmg/turn × ~5 turns × 30 per HP)
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("AccelerantPower");

        int poisonProducers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("POISON_PRODUCER")) poisonProducers++;
        }
        int deckPoison = 0;
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("POISON_PRODUCER")) deckPoison++;

        int proj = poisonProducers + (deckPoison * turns) / 5;
        int tick = proj * PerPoisonProc;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"accelerantTick(poisonProd={poisonProducers}+deck~{deckPoison}/5={proj})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — EnvenomPower (Silent, C): unblocked attack 시 +1 Poison 적용.
    /// Hand 내 attack 수 × per-attack-poison-value × turns. Discount for
    /// "unblocked" condition (~70% of attacks land at least partially).
    /// </summary>
    private static void ApplyEnvenomTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerAttackPoison = 60;  // 1 poison × ~3 turn ticks × 30 per HP × 0.7 land rate
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("EnvenomPower");

        int handAttacks = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsAttack && !c.IsCurseOrStatus) handAttacks++;
        }
        int deckAttacks = 0;
        foreach (var c in state.DrawPile)
            if (c.IsAttack && !c.IsCurseOrStatus) deckAttacks++;

        // Project attacks played over remaining turns: hand-now + ~half of deck attacks per turn
        int projAttacks = handAttacks + (deckAttacks * turns) / 3;
        int tick = projAttacks * PerAttackPoison;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"envenomTick(handAtk={handAttacks}+deck~{deckAttacks}/3={projAttacks})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — SubroutinePower (Defect, B): Power play 시 +1 energy.
    /// Hand + deck 내 Power card 수 × 500 (energy 단위). Self 제외 (Subroutine
    /// 자기 자신은 trigger 안함).
    /// </summary>
    private static void ApplySubroutineTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int EnergyValue = 500;
        const int Cap = 1500;
        int baked = PowerCatalog.LookupSelfBuff("SubroutinePower");

        int handPowers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsPower) handPowers++;
        }
        int deckPowers = 0;
        foreach (var c in state.DrawPile)
            if (c.IsPower) deckPowers++;
        foreach (var c in state.DiscardPile)
            if (c.IsPower) deckPowers++;

        // Assume most deck Powers get played eventually (Powers are sticky).
        int projPlays = handPowers + (deckPowers * 3) / 4;
        int tick = projPlays * EnergyValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"subroutineTick(handPow={handPowers}+deck~{deckPowers}*0.75={projPlays})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — PrepTimePower (Shared, B): turn-start Vigor 4. Vigor amplifies
    /// next attack by 4. Tick = turns × VigorValue, no deck-dependence.
    /// </summary>
    private static void ApplyPrepTimeTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int VigorPerTurn = 4;
        const int DamageBonusValue = 50;   // EffectScoringWeights.DamageInHand
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("PrepTimePower");

        // Each turn Vigor 4 amplifies first attack (or wasted if no attack
        // played that turn). Assume ~75% of turns have a first attack.
        const double AttackTurnRate = 0.75;
        int tick = (int)(turns * VigorPerTurn * DamageBonusValue * AttackTurnRate);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"prepTimeTick(turns={turns}xVigor{VigorPerTurn}x{DamageBonusValue}x{AttackTurnRate})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — StormPower (Defect, B): Power play 시 Lightning orb channel.
    /// Hand + deck Powers × LightningOrbValue (~ 90 per channel). Self 제외.
    /// </summary>
    private static void ApplyStormTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int LightningChannelValue = 90;
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("StormPower");

        int handPowers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsPower) handPowers++;
        }
        int deckPowers = 0;
        foreach (var c in state.DrawPile)
            if (c.IsPower) deckPowers++;
        foreach (var c in state.DiscardPile)
            if (c.IsPower) deckPowers++;

        int projPlays = handPowers + (deckPowers * 3) / 4;
        int tick = projPlays * LightningChannelValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"stormTick(handPow={handPowers}+deck~{deckPowers}*0.75={projPlays}xLtn{LightningChannelValue})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.26 — ToolsOfTheTradePower (Silent, S): turn-start draw 1, discard 1.
    /// Tick = turns × (drawValue − discardCost). Discard cost is small for
    /// well-tuned decks (oldest hand card culled) but real for hand-cap or
    /// retain decks. Conservative net value 250 per turn.
    /// </summary>
    private static void ApplyToolsOfTheTradeTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int NetPerTurn = 250;
        const int Cap = 1200;
        int baked = PowerCatalog.LookupSelfBuff("ToolsOfTheTradePower");

        int tick = turns * NetPerTurn;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"toolsTick(turns={turns}x{NetPerTurn}={tick},baked={baked})={delta:+#;-#;0}");
    }

    // ─── v0.7.27 — Shiv stem Power passives ────────────────────────────────────
    //
    // Shiv archetype share three signals: ShivInPiles (token count), in-hand/
    // deck SHIV_PRODUCER count (future Shiv generation), aliveEnemyCount (for
    // FanOfKnives AOE conversion). Each handler reads the relevant subset and
    // projects ticks over the remaining combat.

    /// <summary>
    /// v0.7.27 — Helper: count SHIV_PRODUCER axis cards across the relevant
    /// piles, excluding the Power card itself. Used by every Shiv-stem handler.
    /// </summary>
    private static (int hand, int deck) CountShivProducers(SimCard self, SimState state)
    {
        int hand = 0, deck = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("SHIV_PRODUCER")) hand++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("SHIV_PRODUCER")) deck++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && c.Axes.Contains("SHIV_PRODUCER")) deck++;
        return (hand, deck);
    }

    /// <summary>
    /// v0.7.27 — AccuracyPower (Silent, A): Shivs deal +N extra damage. Value =
    /// stacks × (projected Shiv plays) × DamagePerPointBonus. Projects Shivs
    /// from current ShivInPiles + future generation by SHIV_PRODUCER cards.
    /// </summary>
    private static void ApplyAccuracyTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("AccuracyPower");

        var (hp, dp) = CountShivProducers(self, state);
        // Future Shivs = current token count + future producer plays × ~3 shivs/play (avg)
        int projShivs = state.ShivInPiles + (hp + (dp * turns) / 4) * 3;
        // Each Shiv hit gains +N (default amount = 4); value = damage × 50 (DamagePerPointBonus)
        int amountPerStack = 4;  // canonical AccuracyPower stack value
        int tick = projShivs * amountPerStack * EffectScoringWeights.DamageInHand / 10;
        // /10 because EstimateCardPower returns absolute-ish values; calibrate down
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"accuracyTick(shivs={state.ShivInPiles}+prod{hp}+deck{dp}~{projShivs})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.27 — PhantomBladesPower (Silent, A): all Shivs gain Retain + first
    /// Shiv each turn +9 dmg. Two value streams:
    ///   • +9 × per-turn first-Shiv: turns × 9 × 50
    ///   • Retain saves Shivs from end-of-turn discard: shiv-in-hand × ~draw-value
    /// </summary>
    private static void ApplyPhantomBladesTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("PhantomBladesPower");

        var (hp, dp) = CountShivProducers(self, state);
        bool hasAnyShivPath = state.ShivInPiles > 0 || hp + dp > 0;
        if (!hasAnyShivPath)
        {
            // Shiv-less deck (party member playing without Silent or no Shiv
            // generators) — Power is dead weight.
            b -= baked;
            parts.Add($"phantomBladesNoShiv=-{baked}");
            return;
        }

        // Per-turn first Shiv +9 dmg payoff; needs at least 1 Shiv per turn
        // (proxy: any producer in hand or pile means likely a Shiv each turn).
        const double FirstShivTurnRate = 0.75;
        int firstShivBonus = (int)(turns * 9 * 50 * FirstShivTurnRate);
        // Retain value: roughly per-turn 1 saved Shiv card = 200 (per-Shiv draw)
        const int RetainPerTurn = 200;
        int retainBonus = (int)(turns * RetainPerTurn * FirstShivTurnRate);
        int tick = firstShivBonus + retainBonus;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"phantomBladesTick(turns={turns},firstShiv={firstShivBonus}+retain={retainBonus})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.27 — FanOfKnivesPower (Silent, C): Shivs target all enemies
    /// permanently. AOE conversion value scales with aliveEnemyCount − 1 (the
    /// extra hits gained). Useless vs single enemy; massive in 3-enemy minion
    /// waves.
    /// </summary>
    private static void ApplyFanOfKnivesTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("FanOfKnivesPower");

        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        if (aliveCount <= 1)
        {
            // Single-target fights — Power equals 0 extra hits.
            b -= baked;
            parts.Add($"fanOfKnivesSingle=-{baked}");
            return;
        }

        var (hp, dp) = CountShivProducers(self, state);
        int projShivs = state.ShivInPiles + (hp + (dp * turns) / 4) * 3;
        // Each future Shiv now hits (aliveCount - 1) additional targets;
        // each hit ~3-4 dmg × DamagePerPointBonus.
        const int ShivDmg = 4;
        int extraHits = projShivs * (aliveCount - 1);
        int tick = extraHits * ShivDmg * 50 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"fanOfKnivesTick(alive={aliveCount},shivs~{projShivs},extraHits={extraHits})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.27 — MasterPlannerPower (Silent, C): all Skill cards gain Sly
    /// (auto-play on discard). Value = (turns × skills-in-hand × discard-rate)
    /// × free-play-value. Sly only helps if cards get discarded — needs a
    /// discard mechanism (hand-overflow / Calculated Gamble / end-of-turn
    /// without Retain). For now estimate discard rate via end-of-turn typical
    /// 1 card discard, multiplied by skill ratio.
    /// </summary>
    private static void ApplyMasterPlannerTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("MasterPlannerPower");

        int totalCards = state.Hand.Count + state.DrawPile.Count + state.DiscardPile.Count;
        int totalSkills = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsSkill && !c.IsCurseOrStatus) totalSkills++;
        }
        foreach (var c in state.DrawPile) if (c.IsSkill && !c.IsCurseOrStatus) totalSkills++;
        foreach (var c in state.DiscardPile) if (c.IsSkill && !c.IsCurseOrStatus) totalSkills++;
        if (totalSkills == 0 || totalCards == 0)
        {
            b -= baked;
            parts.Add($"masterPlannerNoSkills=-{baked}");
            return;
        }

        double skillRatio = totalSkills / (double)totalCards;
        // End-of-turn discard ~= excess hand over (drawn cards − played cards).
        // Conservative: ~0.5 discards/turn on average.
        const double DiscardsPerTurn = 0.5;
        const int FreePlayValue = 200;  // average skill effective value when auto-played
        int tick = (int)(turns * DiscardsPerTurn * skillRatio * FreePlayValue);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"masterPlannerTick(skillRatio={skillRatio:F2},turns={turns})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.27 — InfiniteBladesPower (Silent, A): turn-start +1 Shiv to hand.
    /// Tick = turns × ShivValue. Shiv value depends on consumers in deck
    /// (KNIFE_TRAP / FINISHER / etc. amplify) — use base Shiv value with a
    /// small consumer-presence bonus.
    /// </summary>
    private static void ApplyInfiniteBladesTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int BaseShivValue = 250;   // 4 dmg attack + free-play premium
        const int Cap = 1100;
        int baked = PowerCatalog.LookupSelfBuff("InfiniteBladesPower");

        // Consumer-presence bonus
        int consumers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("SHIV_CONSUMER") || c.Axes.Contains("SHIV_AMPLIFIER")) consumers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("SHIV_CONSUMER") || c.Axes.Contains("SHIV_AMPLIFIER")))
                consumers++;
        int consumerBonus = consumers > 0 ? consumers * 80 : 0;

        int tick = turns * BaseShivValue + consumerBonus * turns / 5;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"infiniteBladesTick(turns={turns}x{BaseShivValue}={turns*BaseShivValue},consumers={consumers})={delta:+#;-#;0}");
    }

    // ─── v0.7.28 — Star stem Power passives (Regent) ───────────────────────────
    //
    // Stars = Regent's resource pool. Star-stem Powers either generate Stars
    // per trigger or amplify the Star → damage/block conversion. Common
    // signals: PlayerStars (current count), STAR_PRODUCER/CONSUMER axis cards,
    // RemainingTurns.

    /// <summary>
    /// v0.7.28 — Helper: count STAR_PRODUCER/CONSUMER cards across piles.
    /// Excludes the Power itself.
    /// </summary>
    private static (int producers, int consumers) CountStarStem(SimCard self, SimState state)
    {
        int prod = 0, cons = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("STAR_PRODUCER")) prod++;
            if (c.Axes.Contains("STAR_CONSUMER")) cons++;
        }
        foreach (var c in state.DrawPile)
        {
            if (c.Axes == null) continue;
            if (c.Axes.Contains("STAR_PRODUCER")) prod++;
            if (c.Axes.Contains("STAR_CONSUMER")) cons++;
        }
        foreach (var c in state.DiscardPile)
        {
            if (c.Axes == null) continue;
            if (c.Axes.Contains("STAR_PRODUCER")) prod++;
            if (c.Axes.Contains("STAR_CONSUMER")) cons++;
        }
        return (prod, cons);
    }

    /// <summary>
    /// v0.7.28 — GenesisPower (Regent, B): +1 Star at turn start. Pure
    /// per-turn tick; value depends on consumers in deck (Stars unused = waste).
    /// </summary>
    private static void ApplyGenesisTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerStarValue = 150;   // 1 Star ≈ small damage / block boost via consumer
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("GenesisPower");

        var (prod, cons) = CountStarStem(self, state);
        if (cons == 0)
        {
            // No way to spend Stars — Power gives currency without sink.
            b -= baked;
            parts.Add($"genesisNoConsumer=-{baked}");
            return;
        }

        int tick = turns * PerStarValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"genesisTick(turns={turns}x{PerStarValue},cons={cons})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.28 — OrbitPower (Regent, B): every 4 energy spent → +1 Star. Energy
    /// expenditure per turn ≈ 3 (player base) × turns / 4 ≈ ~0.75 stars/turn.
    /// </summary>
    private static void ApplyOrbitTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerStarValue = 150;
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("OrbitPower");

        var (prod, cons) = CountStarStem(self, state);
        if (cons == 0)
        {
            b -= baked;
            parts.Add($"orbitNoConsumer=-{baked}");
            return;
        }

        // ~0.75 Stars per turn (3 energy spent on average, 4-threshold)
        int projStars = (turns * 3) / 4;
        int tick = projStars * PerStarValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"orbitTick(stars~{projStars}x{PerStarValue},cons={cons})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.28 — BlackHolePower (Regent, B): Star consume/gain 시 AOE 3 dmg.
    /// Value = (producer + consumer plays) × aliveEnemyCount × 3 dmg × 50.
    /// </summary>
    private static void ApplyBlackHoleTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int DmgPerTrigger = 3;
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("BlackHolePower");

        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;

        var (prod, cons) = CountStarStem(self, state);
        // Each producer/consumer play triggers once. Project hand+deck plays.
        int triggers = prod + cons;
        if (triggers == 0)
        {
            b -= baked;
            parts.Add($"blackHoleNoStarFlow=-{baked}");
            return;
        }

        int tick = triggers * aliveCount * DmgPerTrigger * 50 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"blackHoleTick(triggers={triggers},alive={aliveCount})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.28 — ChildOfTheStarsPower (Regent, S): each Star consumed adds
    /// block. Value = projected STAR_CONSUMER plays × per-Star-block × 30.
    /// </summary>
    private static void ApplyChildOfTheStarsTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int BlockPerStar = 5;     // canonical amount
        const int Cap = 1200;
        int baked = PowerCatalog.LookupSelfBuff("ChildOfTheStarsPower");

        var (prod, cons) = CountStarStem(self, state);
        if (cons == 0)
        {
            b -= baked;
            parts.Add($"childOfTheStarsNoConsumer=-{baked}");
            return;
        }

        // Hand consumers fire immediately; deck consumers per turn at ~1/4 cycle
        int projConsumes = cons + (cons * (turns - 1)) / 4;
        int tick = projConsumes * BlockPerStar * 30;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"childOfTheStarsTick(cons={cons},projConsumes={projConsumes})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.28 — TheSealedThronePower (Regent, S): +1 Star per card play.
    /// Massive Star inflation power. Value depends on card-play rate × turns,
    /// gated by consumer presence. Estimate ~4 cards / turn.
    /// </summary>
    private static void ApplyTheSealedThroneTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerStarValue = 150;
        const int Cap = 1500;
        int baked = PowerCatalog.LookupSelfBuff("TheSealedThronePower");

        var (prod, cons) = CountStarStem(self, state);
        if (cons == 0)
        {
            // Massive Star generation without sink — still has minor value for
            // STAR_CONSUMER potential in future draws, but heavily discount.
            b -= baked * 3 / 4;
            parts.Add($"sealedThroneNoConsumer=-{baked * 3 / 4}");
            return;
        }

        // ~4 cards/turn × turns × per-star
        int projStars = turns * 4;
        // Cap by consumer throughput — can't usefully bank more Stars than
        // consumers in deck can spend.
        int consumerCapPerTurn = System.Math.Max(1, cons / 2);
        int usableStars = System.Math.Min(projStars, consumerCapPerTurn * turns);
        int tick = usableStars * PerStarValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"sealedThroneTick(stars~{usableStars},cons={cons})={delta:+#;-#;0}");
    }

    // ─── v0.7.29 — Forge stem Power passives (Regent, Lord's Blade) ────────────
    //
    // Forge = upgrade SovereignBlade. Two types of Powers:
    //   • Auto-forge per turn: Furnace, SeekingEdge
    //   • Per-LordsBlade-play scaling: HammerTime, Parry, SwordSage
    // Common gate: requires SovereignBladeCount > 0 OR LORDS_BLADE_AMPLIFIER
    // axis cards in deck (otherwise dead weight).

    /// <summary>
    /// v0.7.29 — Helper: detect Lord's Blade axis presence + count.
    /// </summary>
    private static (int producers, int amplifiers) CountForgeStem(SimCard self, SimState state)
    {
        int prod = 0, amp = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("LORDS_BLADE_AMPLIFIER")) amp++;
            if (c.Axes.Contains("FORGE_AMPLIFIER")) prod++;
        }
        foreach (var c in state.DrawPile)
        {
            if (c.Axes == null) continue;
            if (c.Axes.Contains("LORDS_BLADE_AMPLIFIER")) amp++;
            if (c.Axes.Contains("FORGE_AMPLIFIER")) prod++;
        }
        foreach (var c in state.DiscardPile)
        {
            if (c.Axes == null) continue;
            if (c.Axes.Contains("LORDS_BLADE_AMPLIFIER")) amp++;
            if (c.Axes.Contains("FORGE_AMPLIFIER")) prod++;
        }
        return (prod, amp);
    }

    /// <summary>
    /// v0.7.29 — FurnacePower (Regent, C): Forge 4 at turn start. Scales with
    /// SovereignBladeCount > 0 (need an active Blade) × turns.
    /// </summary>
    private static void ApplyFurnaceTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerForgeValue = 100;   // 4 Forge ≈ +N attack on next Blade play
        const int Cap = 500;
        int baked = PowerCatalog.LookupSelfBuff("FurnacePower");

        if (state.SovereignBladeCount == 0)
        {
            b -= baked;
            parts.Add($"furnaceNoBlade=-{baked}");
            return;
        }

        int tick = turns * PerForgeValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"furnaceTick(blade={state.SovereignBladeCount},turns={turns})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.29 — HammerTimePower (Regent, A): Forge propagates to party. Value
    /// depends on FORGE_AMPLIFIER chain + blade presence. Single-player mode
    /// gets minimal value; multiplayer scales.
    /// </summary>
    private static void ApplyHammerTimeTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerPartyForgeValue = 120;
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("HammerTimePower");

        if (state.SovereignBladeCount == 0)
        {
            b -= baked;
            parts.Add($"hammerTimeNoBlade=-{baked}");
            return;
        }

        var (prod, amp) = CountForgeStem(self, state);
        // Forge events per turn ≈ amplifier plays per turn
        double forgeEventsPerTurn = (amp + prod) > 0 ? 1.0 : 0.5;
        int tick = (int)(turns * forgeEventsPerTurn * PerPartyForgeValue);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"hammerTimeTick(turns={turns},forgeRate={forgeEventsPerTurn:F2})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.29 — SeekingEdgePower (Regent, C): Forge 7 + Lord's Blade gains AOE
    /// toggle. Big mid-fight ramp; requires blade present.
    /// </summary>
    private static void ApplySeekingEdgeTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerForge = 150;
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("SeekingEdgePower");

        if (state.SovereignBladeCount == 0)
        {
            b -= baked;
            parts.Add($"seekingEdgeNoBlade=-{baked}");
            return;
        }

        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        // AOE toggle is the value-add: extra hits per Blade play vs more enemies.
        int aoeBonus = aliveCount > 1 ? (aliveCount - 1) * 80 : 0;
        int tick = turns * PerForge + aoeBonus;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"seekingEdgeTick(turns={turns},alive={aliveCount},aoe+={aoeBonus})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.29 — SwordSagePower (Regent, C): Lord's Blade gains +1 hit.
    /// Per-Blade-play bonus. Scales with hand + deck LORDS_BLADE_AMPLIFIER /
    /// blade plays expected.
    /// </summary>
    private static void ApplySwordSageTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerExtraHit = 100;
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("SwordSagePower");

        if (state.SovereignBladeCount == 0)
        {
            b -= baked;
            parts.Add($"swordSageNoBlade=-{baked}");
            return;
        }

        var (prod, amp) = CountForgeStem(self, state);
        // Blade plays per combat ≈ amp + ~1/turn natural Blade use
        int projBladePlays = amp + turns;
        int tick = projBladePlays * PerExtraHit;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"swordSageTick(bladePlays~{projBladePlays})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.29 — ParryPower (Regent, C): +10 block per Lord's Blade play.
    /// Hand + deck LORDS_BLADE plays × 10 block × 30.
    /// </summary>
    private static void ApplyParryTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int BlockPerBlade = 10;
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("ParryPower");

        if (state.SovereignBladeCount == 0)
        {
            b -= baked;
            parts.Add($"parryNoBlade=-{baked}");
            return;
        }

        var (prod, amp) = CountForgeStem(self, state);
        int projBladePlays = amp + turns;
        int tick = projBladePlays * BlockPerBlade * 30;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"parryTick(bladePlays~{projBladePlays}x{BlockPerBlade}block)={delta:+#;-#;0}");
    }

    // ─── v0.7.30 — Doom / Volatile stem (Necrobinder) ──────────────────────────
    //
    // Doom = DoT stack on enemies (tick = DoomAmount × N at turn start). Value
    // scales with RemainingTurns AND enemy survival. Volatile = Ethereal cards
    // that auto-exhaust; their Powers scale with hand+deck Ethereal count.

    /// <summary>
    /// v0.7.30 — Helper: count Volatile (Ethereal) cards across piles excl. self.
    /// </summary>
    private static int CountVolatile(SimCard self, SimState state)
    {
        int n = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsEthereal) n++;
        }
        foreach (var c in state.DrawPile) if (c.IsEthereal) n++;
        foreach (var c in state.DiscardPile) if (c.IsEthereal) n++;
        return n;
    }

    /// <summary>
    /// v0.7.30 — Helper: count attack-intent alive enemies (Doom targets).
    /// </summary>
    private static int CountAliveAttackTargets(SimState state)
    {
        int n = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive && !e.IsInert) n++;
        return n;
    }

    /// <summary>
    /// v0.7.30 — CountdownPower (Necrobinder, A): +6 Doom on a random enemy
    /// per turn. Doom ticks DoomAmount damage per turn. Value =
    ///   turns × (avg projected Doom × tickValue) — saturates at enemy survival.
    /// </summary>
    private static void ApplyCountdownTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int DoomPerTurn = 6;
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("CountdownPower");

        int targets = CountAliveAttackTargets(state);
        if (targets == 0)
        {
            b -= baked;
            parts.Add($"countdownNoTarget=-{baked}");
            return;
        }

        // Doom stacks compound: turn 1 = 6, turn 2 ticks 6, turn 3 ticks 12 etc.
        // Total damage over N turns = 6×(N) + 6×(N-1) + ... = 6×N(N+1)/2
        int totalDoomDamage = DoomPerTurn * turns * (turns + 1) / 2;
        // Scale value: each DoT HP × DamagePerPoint(50) / 10 calibration
        int tick = totalDoomDamage * 50 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"countdownTick(turns={turns},totalDoom={totalDoomDamage})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.30 — RupturePower (Necrobinder, A): HP loss event → +1 Strength
    /// (permanent). Hand + deck HP_LOSS cards × StrengthValue.
    /// </summary>
    private static void ApplyRuptureTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int Cap = 1000;
        int baked = PowerCatalog.LookupSelfBuff("RupturePower");

        int hpLossCards = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("HP_LOSS_SELF") || c.Axes.Contains("HP_LOSS")) hpLossCards++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("HP_LOSS_SELF") || c.Axes.Contains("HP_LOSS"))) hpLossCards++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && (c.Axes.Contains("HP_LOSS_SELF") || c.Axes.Contains("HP_LOSS"))) hpLossCards++;

        if (hpLossCards == 0)
        {
            b -= baked;
            parts.Add($"ruptureNoHpLoss=-{baked}");
            return;
        }

        // Each +1 Strength applies to ALL future attacks. Conservative:
        // ~3 attacks/turn × turns × 50 dmg-bonus = strength's lifetime value.
        const int StrengthLifetimeValue = 400;  // per +1 Str
        // Project HP-loss triggers across combat
        int projTriggers = System.Math.Min(hpLossCards, hpLossCards * (turns + 2) / 5);
        int tick = projTriggers * StrengthLifetimeValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"ruptureTick(hpLossCards={hpLossCards},projTriggers={projTriggers})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.30 — PagestormPower (Necrobinder, S): draw a card when you draw
    /// a Volatile card. Value = Volatile count × per-draw × turn-cycle.
    /// </summary>
    private static void ApplyPagestormTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerVolatileDraw = 200;
        const int Cap = 1200;
        int baked = PowerCatalog.LookupSelfBuff("PagestormPower");

        int volatileCount = CountVolatile(self, state);
        if (volatileCount == 0)
        {
            b -= baked;
            parts.Add($"pagestormNoVolatile=-{baked}");
            return;
        }

        // Volatile cards cycle: hand ones might already be drawn. Future
        // draws over turns ≈ volatileCount × turns / total_pile_size.
        int totalPile = state.Hand.Count + state.DrawPile.Count + state.DiscardPile.Count;
        if (totalPile <= 0) totalPile = 1;
        int projVolatileDraws = (volatileCount * (turns + 1) * 5) / totalPile;
        int tick = projVolatileDraws * PerVolatileDraw;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"pagestormTick(volatile={volatileCount},projDraws={projVolatileDraws})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.30 — LethalityPower (Necrobinder, S): Volatile, first attack per
    /// turn +50%. Value = turns × first-attack-dmg × 0.5. Volatile itself
    /// means the Power exhausts at turn end IF NOT KEPT — but as a Power,
    /// it's stuck onto the player, so the Volatile note really means the
    /// card itself is Volatile (auto-exhaust after play).
    /// </summary>
    private static void ApplyLethalityTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int AvgFirstAttackDmg = 12;
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("LethalityPower");

        // Estimate hand-attack mean to better predict first-attack value
        int sum = 0, cnt = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            sum += System.Math.Max(0, c.TotalDamage);
            cnt++;
        }
        int avgDmg = cnt > 0 ? sum / cnt : AvgFirstAttackDmg;
        // First attack each turn × turns × 0.5 amp × DamagePerPoint(50) / 10
        int tick = turns * avgDmg * 50 / 2 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"lethalityTick(turns={turns},avgFirstAtk={avgDmg})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.30 — DemesnePower (Necrobinder, S): Volatile, turn-start payoff
    /// (variable — typically draw or stat gain). Conservative per-turn value
    /// scaling with turns.
    /// </summary>
    private static void ApplyDemesneTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int NetPerTurn = 200;
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("DemesnePower");

        int tick = turns * NetPerTurn;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"demesneTick(turns={turns}x{NetPerTurn})={delta:+#;-#;0}");
    }

    // ─── v0.7.31 — Cross-character impact Powers ───────────────────────────────
    //
    // Final mechanic-coverage batch. Each Power gates on a distinct signal —
    // grouped here because they don't share a stem.

    /// <summary>
    /// v0.7.31 — PyrePower (Ironclad, B): permanent +1 energy / turn for the
    /// rest of combat. Pure RemainingTurns scaling. Energy value ~500/point.
    /// </summary>
    private static void ApplyPyreTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int EnergyValue = 500;
        const int Cap = 1500;
        int baked = PowerCatalog.LookupSelfBuff("PyrePower");

        // Pyre's value is "rest of combat" — earlier the better. -1 for cur
        // turn cost amortisation.
        int effTurns = System.Math.Max(0, turns - 1);
        int tick = effTurns * EnergyValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"pyreTick(effTurns={effTurns}x{EnergyValue})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.31 — InfernoPower (Ironclad, A): HP loss event → AOE 6 dmg.
    /// HP_LOSS cards in deck × aliveEnemies × 6 × DamagePerPoint.
    /// </summary>
    private static void ApplyInfernoTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int DmgPerTrigger = 6;
        const int Cap = 1200;
        int baked = PowerCatalog.LookupSelfBuff("InfernoPower");

        int hpLossCards = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("HP_LOSS_SELF") || c.Axes.Contains("HP_LOSS")) hpLossCards++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("HP_LOSS_SELF") || c.Axes.Contains("HP_LOSS"))) hpLossCards++;

        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        if (aliveCount == 0 || hpLossCards == 0)
        {
            b -= baked;
            parts.Add($"infernoNoTriggerOrTarget=-{baked}");
            return;
        }

        // Project HP-loss triggers across combat (cycle the deck).
        int projTriggers = System.Math.Min(hpLossCards * (turns + 1) / 4, hpLossCards * 3);
        int tick = projTriggers * aliveCount * DmgPerTrigger * 50 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"infernoTick(hpLoss={hpLossCards},alive={aliveCount},triggers~{projTriggers})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.31 — AutomationPower (Shared, A): +1 energy per 10 cards drawn.
    /// Standard draw rate ≈ 5 cards/turn → 1 trigger per 2 turns.
    /// </summary>
    private static void ApplyAutomationTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int EnergyValue = 500;
        const int Cap = 900;
        int baked = PowerCatalog.LookupSelfBuff("AutomationPower");

        // Expected energy triggers = turns × 5 cards/turn / 10
        int triggers = (turns * 5) / 10;
        int tick = triggers * EnergyValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"automationTick(turns={turns},triggers~{triggers})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.31 — OutbreakPower (Silent, D): every 3 Poison applications → AOE
    /// 11 dmg. Trigger count ≈ POISON_PRODUCER plays / 3.
    /// </summary>
    private static void ApplyOutbreakTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int DmgPerTrigger = 11;
        const int Cap = 900;
        int baked = PowerCatalog.LookupSelfBuff("OutbreakPower");

        int poisonProducers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("POISON_PRODUCER")) poisonProducers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("POISON_PRODUCER")) poisonProducers++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && c.Axes.Contains("POISON_PRODUCER")) poisonProducers++;

        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        if (poisonProducers == 0 || aliveCount == 0)
        {
            b -= baked;
            parts.Add($"outbreakNoTrigger=-{baked}");
            return;
        }

        // Project poison applications over combat: each producer plays
        // ~once per (5 turns / draw cycle).
        int projApplications = poisonProducers + (poisonProducers * turns) / 4;
        int triggers = projApplications / 3;
        int tick = triggers * aliveCount * DmgPerTrigger * 50 / 10;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"outbreakTick(poisonProd={poisonProducers},apps~{projApplications},triggers~{triggers})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.31 — PaleBlueDotPower (Regent, B): bonus draw on 5+ card turns.
    /// Hand size proxy: high-draw decks trigger every turn, low-draw rarely.
    /// </summary>
    private static void ApplyPaleBlueDotTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        const int PerDrawValue = 200;
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("PaleBlueDotPower");

        // Check draw axis cards in deck — proxy for "5+ cards/turn" likelihood
        int drawAxisCards = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes == null) continue;
            if (c.Axes.Contains("DRAW") || c.Axes.Contains("DRAW_CONDITIONAL")
                || c.Axes.Contains("DRAW_ON_DRAW") || c.Axes.Contains("DRAW_AMPLIFIER"))
                drawAxisCards++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("DRAW") || c.Axes.Contains("DRAW_AMPLIFIER")))
                drawAxisCards++;

        // Trigger rate ≈ min(1, drawAxis / 3); base 0.5 (standard 5-card turns).
        double rate = System.Math.Min(1.0, 0.5 + drawAxisCards * 0.15);
        int tick = (int)(turns * rate * PerDrawValue);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"paleBlueDotTick(drawAxis={drawAxisCards},rate={rate:F2})={delta:+#;-#;0}");
    }

    /// <summary>
    /// MachineLearningPower (Defect, +N permanent draw/turn). Existing
    /// PowerCatalog flat (900) ignores deck composition. Adjust by per-turn
    /// pile-EV times remaining turns: a finisher-heavy deck pushes the value
    /// up, a curse-polluted deck pushes it down (delta floored at -baked so
    /// the power can score down to 0 in dead-archetype decks).
    /// </summary>
    private static void ApplyMachineLearningTickValue(int stack, SimState state, ref int b, List<string> parts)
    {
        int baked = PowerCatalog.LookupSelfBuff("MachineLearningPower");
        int turns = RemainingTurnsEstimator.From(state);
        // Per-turn EV: simulate drawing `stack` cards from current piles. Uses
        // the same shared helper as the generic draw evaluator so the
        // valuation stays consistent.
        int perTurnEv = PlanScorer.EstimatePileDrawEv(stack, state);
        int totalEv = perTurnEv * turns;
        int delta = totalEv - baked;
        const int Cap = 1200;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;
        b += delta;
        parts.Add($"machineLearnTick(stack{stack}×turns{turns},perTurnEv{perTurnEv})={delta:+#;-#;0}");
    }

    /// <summary>
    /// DrawCardsNextTurnPower (one-shot, draws N at start of next turn).
    /// Single-turn application — no turns multiplier, but use a slightly
    /// stronger discount than the generic draw (cards arrive next turn so
    /// part of their value depends on the next hand bottleneck which we
    /// can't see yet).
    /// </summary>
    private static void ApplyDrawCardsNextTurnTickValue(int stack, SimState state, ref int b, List<string> parts)
    {
        int baked = PowerCatalog.LookupSelfBuff("DrawCardsNextTurnPower");
        // Use shared pile EV; apply an additional 0.85 next-turn-delay factor.
        int rawEv = PlanScorer.EstimatePileDrawEv(stack, state);
        int ev = rawEv * 85 / 100;
        int delta = ev - baked;
        const int Cap = 700;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;
        b += delta;
        parts.Add($"nextTurnDrawTick(stack{stack},ev{ev})={delta:+#;-#;0}");
    }

    // ─── v0.7.43 — DECISIONS_DECISIONS (Regent, Rare, 0c/6-star) ───────────────

    /// <summary>
    /// v0.7.43 — DECISIONS_DECISIONS payoff: choose 1 Skill in hand AFTER
    /// drawing 3 (5 upgraded) cards, then play that Skill 3 times. The DRAW
    /// part is already credited via the DRAW axis. This handler adds the
    /// "play best Skill 3 times" payoff using the current hand's best Skill
    /// value as a proxy (the freshly-drawn skills aren't visible to the AI;
    /// current hand is the lower bound — actual chosen skill is at least
    /// this strong).
    ///
    /// Conservative 0.7 discount for: (1) the chosen Skill is exhausted /
    /// expended via the repeats (varies by card), (2) the proxy might be
    /// suboptimal if a stronger skill is drawn, (3) some Skills don't scale
    /// linearly with repeats (Block stacks fine, but per-turn buffs cap at 1).
    /// </summary>
    private static void ApplyDecisionsDecisionsRepeat(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int bestSkillValue = 0;
        string? bestSkillId = null;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsSkill || c.IsCurseOrStatus || !c.IsPlayable) continue;
            int v = EstimateCardPower(c, state, freeUse: true);
            if (v > bestSkillValue)
            {
                bestSkillValue = v;
                bestSkillId = c.Id;
            }
        }

        // No skill in current hand. The 3-5 fresh draws may surface one, but
        // can't credit that without knowing the draws. Minimum case credit so
        // the card still slightly scores positive (draw value covers the rest).
        if (bestSkillValue == 0)
        {
            const int NoSkillBaseline = 150;
            b += NoSkillBaseline;
            parts.Add($"decisionsNoSkill=+{NoSkillBaseline}");
            return;
        }

        const int Cap = 1800;
        const double Discount = 0.7;
        const int RepeatCount = 3;
        int v2 = (int)(bestSkillValue * RepeatCount * Discount);
        if (v2 > Cap) v2 = Cap;
        b += v2;
        parts.Add($"decisionsRepeat(best={Short(bestSkillId ?? "?")}({bestSkillValue})x3x{Discount})=+{v2}");
    }

    private static string Short(string id) =>
        id == null ? "?" : (id.StartsWith("CARD.") ? id.Substring(5) : id);

    // ─── v0.7.44 — Skill cards with X-cost or REPEAT mechanics ────────────────

    /// <summary>
    /// v0.7.44 — QUADCAST (Defect, S, 1c): evoke top orb 4 times. Fixed repeat,
    /// scales with current top orb evoke value × 4.
    /// </summary>
    private static void ApplyQuadcastEvoke(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.OrbQueue == null || state.OrbQueue.Count == 0) return;
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        // Evoke top orb (queue[0]) 4 times. Each evoke gives full orb evoke value.
        var topKind = state.OrbQueue[0];
        int evokeVal = OrbValueCatalog.EvokeValue(topKind, aliveCount,
            darkAccumulated: state.OrbEvokeValues.Count > 0 ? state.OrbEvokeValues[0] : 6,
            focus: state.PlayerFocus);
        // 4× evokes. Cap to avoid runaway with high-Focus Dark stacks.
        const int Cap = 1800;
        int v = System.Math.Min(Cap, evokeVal * 4);
        b += v;
        parts.Add($"quadcast({topKind}x4={v})");
    }

    /// <summary>
    /// v0.7.44 — MULTI_CAST (Defect, B, 0c, X-cost): evoke top orb X+1 times.
    /// X = remaining energy. Use PlayerEnergy as proxy.
    /// </summary>
    private static void ApplyMultiCastEvoke(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.OrbQueue == null || state.OrbQueue.Count == 0) return;
        int x = System.Math.Max(0, state.PlayerEnergy);
        int evokes = x + 1;
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        var topKind = state.OrbQueue[0];
        int evokeVal = OrbValueCatalog.EvokeValue(topKind, aliveCount,
            darkAccumulated: state.OrbEvokeValues.Count > 0 ? state.OrbEvokeValues[0] : 6,
            focus: state.PlayerFocus);
        const int Cap = 1500;
        int v = System.Math.Min(Cap, evokeVal * evokes);
        b += v;
        parts.Add($"multiCast({topKind}x{evokes}={v})");
    }

    /// <summary>
    /// v0.7.44 — TEMPEST (Defect, B, 0c, X-cost): channel X+1 Lightning orbs.
    /// Channeled orbs deal passive damage end-of-turn + can be evoked later.
    /// </summary>
    private static void ApplyTempestChannel(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int x = System.Math.Max(0, state.PlayerEnergy);
        int channels = x + 1;
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;
        // Each Lightning passive ~3 dmg + future evoke value
        int lightningPassive = (3 + state.PlayerFocus);
        // Per-orb total value ~ passive + 0.5 evoke (orb may be evoked later)
        int perOrb = lightningPassive * 50 + 100;  // 50 dmg/point + part of future evoke
        const int Cap = 1200;
        int v = System.Math.Min(Cap, perOrb * channels);
        b += v;
        parts.Add($"tempest(Lightx{channels}={v})");
    }

    /// <summary>
    /// v0.7.44 — MALAISE (Silent, S, 0c, X-cost): lose X+1 energy, apply Weak
    /// X+1 to all enemies. Net: energy is sunk but huge Weak coverage.
    /// </summary>
    private static void ApplyMalaiseXWeak(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int x = System.Math.Max(0, state.PlayerEnergy);
        int weakStacks = x + 1;
        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive && !e.IsInert && (e.HasAttackIntent || e.HasDeathBlowIntent)) aliveCount++;
        if (aliveCount == 0)
        {
            // No attackers to weaken — Malaise just burns energy. Big penalty.
            b -= 500;
            parts.Add("malaiseNoAttacker=-500");
            return;
        }
        // Per-enemy Weak value uses HandSynergy-style turn savings × 30 / point.
        // Single hand-level estimate: weak X+1 stacks × ~3 turns × estimated dmg savings.
        // Use baseline: WeakPower stack value ~350, scaled by stacks beyond 1.
        int perEnemy = 350 + (weakStacks - 1) * 200;  // approximate stack curve
        const int Cap = 1500;
        int v = System.Math.Min(Cap, perEnemy * aliveCount);
        // Energy sink penalty — losing X+1 energy this turn means we play
        // fewer follow-ups. Discount ~150 per energy lost.
        int energyCost = weakStacks * 150;
        v = System.Math.Max(0, v - energyCost);
        b += v;
        parts.Add($"malaise(Weak{weakStacks}x{aliveCount}-energy{energyCost}={v})");
    }

    /// <summary>
    /// v0.7.44 — DIRGE (Necrobinder, A, 0c, X-cost): summon 3 + add X+1 Souls
    /// to discard. EXHAUST_SELF.
    /// </summary>
    private static void ApplyDirgeXSouls(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int x = System.Math.Max(0, state.PlayerEnergy);
        int souls = x + 1;
        // Each Soul = SOUL_CONSUMER fuel. Value depends on SOUL_CONSUMER presence.
        int soulConsumers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("SOUL_CONSUMER")) soulConsumers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("SOUL_CONSUMER")) soulConsumers++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && c.Axes.Contains("SOUL_CONSUMER")) soulConsumers++;

        // Per-Soul value: depends on consumer presence. With consumers, ~120 each.
        int perSoul = soulConsumers > 0 ? 120 : 30;
        // 3 summon (Skeleton) base value too
        const int SummonValue = 200;  // 3 skeletons
        const int Cap = 1400;
        int v = System.Math.Min(Cap, SummonValue + perSoul * souls);
        b += v;
        parts.Add($"dirge(Souls{souls}x{perSoul}+summon200,consumers={soulConsumers}={v})");
    }

    /// <summary>
    /// v0.7.51 — Generic self-growing attack handler for cards that boost all
    /// siblings of the same id every time one is played. Current damage is
    /// already correctly resolved via CardReflection's CalculatedDamageVar;
    /// this layer adds the FUTURE-value increment for remaining siblings.
    ///
    /// CLAW (B, Defect): +2 to all CLAWs per play, single-hit
    /// MAUL (A, Ironclad): +1 to all MAULs per play, 2-hit
    /// </summary>
    private static void ApplySelfGrowingAttack(SimCard self, SimState state, ref int b, List<string> parts,
        int increasePerPlay, int hitCount)
    {
        // Count siblings (same id) across all piles excluding self.
        int siblings = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Id == self.Id) siblings++;
        }
        foreach (var c in state.DrawPile) if (c.Id == self.Id) siblings++;
        foreach (var c in state.DiscardPile) if (c.Id == self.Id) siblings++;

        if (siblings == 0)
        {
            // No siblings — this play's increment is wasted (one-shot).
            // No bonus, no penalty (the immediate damage is its own value).
            return;
        }

        int turns = RemainingTurnsEstimator.From(state);
        // Each sibling will play ~once over remaining combat. Cap by turns × 2.
        int futurePlays = System.Math.Min(siblings, turns * 2);
        // Each future play benefits from this card's increment.
        // Per-hit damage × DamagePerPoint(50) calibrated /10.
        int bonus = futurePlays * increasePerPlay * hitCount * 50 / 10;
        const int Cap = 800;
        if (bonus > Cap) bonus = Cap;
        b += bonus;
        parts.Add($"selfGrow(sib{siblings}/play{futurePlays}*inc{increasePerPlay}*hit{hitCount}={bonus})");
    }

    /// <summary>
    /// v0.7.51 — RAMPAGE (Ironclad, C, 1c): "Deal 9 damage. Permanently increase
    /// THIS card's damage by 5." (Only buffs self, not siblings.) Different from
    /// CLAW pattern — value depends on # of times we'll draw THIS instance.
    /// </summary>
    private static void ApplyRampageSelfGrow(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Same-card instance ~ played 1-2 times per combat on average. The +5
        // benefits only this exact instance. Use turns/3 as proxy for draw count.
        int turns = RemainingTurnsEstimator.From(state);
        int futureDraws = System.Math.Max(0, turns / 3);  // expect to draw this instance once per 3 turns
        const int IncreasePerPlay = 5;
        int bonus = futureDraws * IncreasePerPlay * 50 / 10;
        const int Cap = 400;
        if (bonus > Cap) bonus = Cap;
        b += bonus;
        parts.Add($"rampageGrow(futureDraws{futureDraws}*inc5={bonus})");
    }

    /// <summary>
    /// v0.7.50 — BATTLE_TRANCE (Ironclad, S, 0c): Draw 3, can't draw more
    /// this turn. Big upside, but blocks subsequent draw cards.
    /// </summary>
    private static void ApplyBattleTranceTradeoff(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Draw 3 base value ≈ 3 × 200 = 600.
        const int DrawValue = 600;
        // Penalty: any unplayable-yet draw cards in hand become dead.
        int deadDraw = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsDrawCard) deadDraw++;
        }
        int penalty = deadDraw * 200;
        int v = DrawValue - penalty;
        if (v < 100) v = 100;
        b += v;
        parts.Add($"battleTrance(draw{DrawValue}-deadDraw{penalty}={v})");
    }

    /// <summary>
    /// v0.7.50 — BORROWED_TIME (Necrobinder, A, 1c): Gain 4 energy. This turn,
    /// cards cost +1. Net: massive ramp if hand has many low-cost cards.
    /// </summary>
    private static void ApplyBorrowedTimeRamp(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Net energy: +4 - (cards we'll play this turn × +1 cost).
        // We typically play 3-4 cards/turn. Net = +4 - 3.5 = +0.5 effective.
        // But the surge enables a BIG play (e.g. 4-cost card) that wouldn't fit.
        // Score this as +1 energy lifecycle (500) when many low-cost cards in hand.
        int playableLow = 0;  // cost 0-1 cards
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            if (c.Cost <= 1) playableLow++;
        }
        // The +1 cost penalty stings less when many 0-cost cards (those become 1c).
        // Best case: hand has 4+ low-cost. Net ~ +500.
        int v = playableLow >= 3 ? 500 : 200;
        b += v;
        parts.Add($"borrowedTime(lowCost{playableLow}={v})");
    }

    /// <summary>
    /// v0.7.50 — NOT_YET (Necrobinder, S, 2c): Heal 10 HP. Exhaust.
    /// Value depends on HP urgency.
    /// </summary>
    private static void ApplyNotYetHeal(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Each HP healed ≈ 30 score (BlockPerPointBonus equivalent).
        // Critical HP: HP × 60 (double value when low).
        int heal = 10;
        int maxHeal = System.Math.Min(heal, System.Math.Max(0, 100 - state.PlayerHp));  // assume max 100
        double hpFrac = state.PlayerHp / 100.0;
        int perHp = hpFrac < 0.3 ? 60 : 30;
        int v = maxHeal * perHp;
        b += v;
        parts.Add($"notYet(heal{maxHeal}*{perHp}={v})");
    }

    /// <summary>
    /// v0.7.50 — PANIC_BUTTON (Silent, S, 0c): block 30 + 2 turn no-block.
    /// Emergency: huge block now, dead for 2 turns.
    /// </summary>
    private static void ApplyPanicButtonEmergency(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Block 30 = 30 × 30 = 900 baseline. Penalty for 2-turn no-block.
        // Block 30 is good if incoming is big AND we can't block enough otherwise.
        int incoming = EnemyTurnSimulator.PredictPlayerDmg(state);
        // Effective block delta = min(30, incoming) — beyond incoming is wasted.
        int effBlock = System.Math.Min(30, incoming + 5);
        int v = effBlock * 30 - 400;  // -400 for 2-turn block lockout
        if (v < 0) v = 0;
        b += v;
        parts.Add($"panicButton(eff{effBlock}-lockout400={v})");
    }

    /// <summary>
    /// v0.7.50 — THE_BOMB (Shared, B, 2c): 3 turns later, AOE 40 dmg.
    /// Long delayed payoff.
    /// </summary>
    private static void ApplyTheBombDelayed(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        if (turns < 3)
        {
            // Combat ends before payoff. Waste.
            b -= 300;
            parts.Add("theBombShortFight=-300");
            return;
        }
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;
        // 40 dmg × alive × 50 / 10 calibration
        int v = 40 * aliveCount * 50 / 10;
        const int Cap = 1500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"theBomb(40x{aliveCount}={v})");
    }

    /// <summary>
    /// v0.7.50 — TORIC_TOUGHNESS (Regent, B, 2c): block 5 + next 2 turns
    /// turn-start block 5. Multi-turn block.
    /// </summary>
    private static void ApplyToricToughnessMultiTurn(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        // 5 block now + 5 × min(2, turns-1) future block.
        int futureBlock = 5 * System.Math.Min(2, System.Math.Max(0, turns - 1));
        // Base 5 block already scored by BLOCK axis. This handler adds future
        // block delta.
        int v = futureBlock * 30;  // 30 = BlockPerPointBonus
        b += v;
        parts.Add($"toricToughness(future{futureBlock}block={v})");
    }

    /// <summary>
    /// v0.7.49 — APOTHEOSIS (Shared, A, 2c): "Upgrade ALL cards in deck.
    /// Exhaust." Massive long-fight value — every future draw is upgraded.
    /// Per-card upgrade ~50-150 value. Cap at remaining-turns × 3 cards/turn.
    /// </summary>
    private static void ApplyApotheosisUpgradeAll(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count non-upgraded cards in piles (excluding self).
        int upgradable = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            upgradable++;
        }
        foreach (var c in state.DrawPile) if (!c.IsCurseOrStatus) upgradable++;
        foreach (var c in state.DiscardPile) if (!c.IsCurseOrStatus) upgradable++;
        if (upgradable == 0) return;

        int turns = RemainingTurnsEstimator.From(state);
        // Each upgrade ~80 value (avg). Realized only as cards drawn over
        // remaining turns × ~4 cards/turn.
        int expectedRealized = System.Math.Min(upgradable, turns * 4);
        const int PerUpgrade = 80;
        const int Cap = 1500;
        int v = System.Math.Min(Cap, expectedRealized * PerUpgrade);
        b += v;
        parts.Add($"apotheosis(upg{upgradable}/realized{expectedRealized}x{PerUpgrade}={v})");
    }

    /// <summary>
    /// v0.7.49 — DOMINATE (Ironclad, S, 1c): Apply Vulnerable 1 to all enemies +
    /// every Strike in deck gains +1 damage permanently. Exhaust.
    /// </summary>
    private static void ApplyDominateVulnStrike(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count Strikes in deck (id contains STRIKE).
        int strikes = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Id != null && c.Id.Contains("STRIKE")) strikes++;
        }
        foreach (var c in state.DrawPile) if (c.Id != null && c.Id.Contains("STRIKE")) strikes++;
        foreach (var c in state.DiscardPile) if (c.Id != null && c.Id.Contains("STRIKE")) strikes++;

        int turns = RemainingTurnsEstimator.From(state);
        // Vuln 1 on all alive enemies = ~350 value.
        int aliveAttackers = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive && !e.IsInert) aliveAttackers++;
        int vulnPart = aliveAttackers * 350;
        // Strike scaling: each +1 dmg × expected Strike plays this combat (
        // strikes × turns × 0.5 — half cycle per turn).
        int strikePlays = strikes * turns / 2;
        int strikePart = strikePlays * 50;  // +1 dmg × DamagePerPoint
        const int Cap = 2000;
        int v = System.Math.Min(Cap, vulnPart + strikePart);
        b += v;
        parts.Add($"dominate(vuln{vulnPart}+strike{strikes}x{turns}/2={v})");
    }

    /// <summary>
    /// v0.7.49 — BRAND (Ironclad, A, 0c): Lose 1 HP, exhaust 1 card, gain +1
    /// Strength permanent. HP_LOSS_SELF + EXHAUST_TARGET + STR.
    /// </summary>
    private static void ApplyBrandHpExhaustStr(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Strength +1 permanent ≈ 400 lifetime value (3 attacks/turn × turns).
        int turns = RemainingTurnsEstimator.From(state);
        int strValue = turns * 3 * 50;  // 1 str × 3 attacks × DmgPoint, no /10 since lifetime
        // Exhaust target: useful when trashing curses; otherwise neutral.
        int curseInHand = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) curseInHand++;
        }
        int exhaustValue = curseInHand > 0 ? 200 : 50;
        // HP loss penalty handled separately by HP_LOSS axis + survival.
        const int Cap = 1200;
        int v = System.Math.Min(Cap, strValue + exhaustValue);
        b += v;
        parts.Add($"brand(str{strValue}+exh{exhaustValue}={v})");
    }

    /// <summary>
    /// v0.7.49 — STOKE (Ironclad, B, 1c): Exhaust ALL hand cards. Add 1
    /// upgraded random card per exhausted card to hand.
    /// </summary>
    private static void ApplyStokeExhaustGenerate(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int exhausted = 0;
        int handValueSum = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            exhausted++;
            handValueSum += EstimateCardPower(c, state, freeUse: false);
        }
        if (exhausted == 0)
        {
            b -= 100;
            parts.Add("stokeEmpty=-100");
            return;
        }
        // Per-exhausted: gain a random upgraded card. Random upgraded ~ 200.
        const int PerExhaust = 200;
        // Trash cost: half the hand value lost.
        int trashCost = handValueSum / 2;
        int v = exhausted * PerExhaust - trashCost;
        const int Cap = 800;
        if (v > Cap) v = Cap;
        if (v < -200) v = -200;
        b += v;
        parts.Add($"stoke(exh{exhausted}x{PerExhaust}-trash{trashCost}={v})");
    }

    /// <summary>
    /// v0.7.48 — SACRIFICE (Necrobinder, B, 1c, Retain): "If a skeleton is
    /// alive, gain block equal to skeleton's max HP × 2." State-dependent on
    /// Allies collection (skeleton ally HP).
    /// </summary>
    private static void ApplySacrificeBlock(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Find best alive skeleton ally's max HP.
        int bestSkeletonHp = 0;
        foreach (var a in state.Allies)
        {
            if (!a.IsAlive) continue;
            if (a.Hp > bestSkeletonHp) bestSkeletonHp = a.Hp;
        }
        if (bestSkeletonHp == 0)
        {
            // No skeleton — Retain holds it for later. Mild penalty so it
            // doesn't surface as a leading play.
            b -= 200;
            parts.Add("sacrificeNoSkeleton=-200");
            return;
        }

        // Block = skeleton max HP × 2. Sacrifice kills the skeleton — losing
        // its damage contribution counts as opportunity cost (~150 per ally).
        int blockGained = bestSkeletonHp * 2;
        int v = blockGained * 30 - 150;  // 30 = BlockPerPointBonus, -150 ally loss
        const int Cap = 1500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"sacrifice(skHp{bestSkeletonHp}x2={blockGained}block, -150ally)=+{v}");
    }

    /// <summary>
    /// v0.7.48 — RESTLESSNESS (Shared, A, 0c, Retain): "If hand is empty,
    /// draw 2 cards and gain 2 energy." Hand 비었을 때만 trigger. Retain
    /// 덕분에 hand 비울 때까지 다른 카드 먼저 사용 가능.
    /// </summary>
    private static void ApplyRestlessnessConditional(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count other playable cards in hand. If 0 others, this triggers NOW.
        // Otherwise, it'll trigger later (after we play the others).
        int otherPlayable = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            otherPlayable++;
        }

        if (otherPlayable == 0)
        {
            // Trigger now: draw 2 + energy 2. Big payoff.
            const int v = 900;  // draw 2 ~400 + energy 2 ~500
            b += v;
            parts.Add($"restlessnessNow(emptyHand)=+{v}");
        }
        else
        {
            // Will trigger after current hand exhausted. Discount per other
            // card waiting (each delays the trigger).
            int v = System.Math.Max(0, 700 - otherPlayable * 100);
            b += v;
            parts.Add($"restlessnessLater(others{otherPlayable})=+{v}");
        }
    }

    /// <summary>
    /// v0.7.48 — PURITY (Shared, B, 0c, Retain): "Exhaust up to 3 hand cards.
    /// Exhaust self." Value depends on whether hand has trash (curses/status)
    /// or useless cards (low-value cards in a bad turn).
    /// </summary>
    private static void ApplyPurityHandClean(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count curses/status in hand — primary purity targets.
        int curses = 0;
        int veryLowValueCards = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) curses++;
            else
            {
                int v = EstimateCardPower(c, state, freeUse: false);
                if (v <= 50) veryLowValueCards++;  // junk-tier
            }
        }
        int exhausts = System.Math.Min(3, curses + veryLowValueCards);
        if (exhausts == 0)
        {
            // No targets — Retain holds it for later (when we draw curses).
            b += 50;
            parts.Add("purityNoTargets=+50");
            return;
        }
        // Each cursed/junk card exhausted = ~250 (cleared from cycling pool).
        int v2 = exhausts * 250;
        b += v2;
        parts.Add($"purity(exhaust{exhausts}x250)=+{v2}");
    }

    /// <summary>
    /// v0.7.69 — FEEL_NO_PAIN (Ironclad B Power 1c): "When a card is exhausted,
    /// gain 3 block." Per-exhaust block trigger. Value scales with deck's
    /// EXHAUST cards (those that self-exhaust frequently).
    /// </summary>
    private static void ApplyFeelNoPainPower(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int exhaustSources = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && (c.Axes.Contains("EXHAUST_SELF")
                                    || c.Axes.Contains("EXHAUST_PRODUCER"))) exhaustSources++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("EXHAUST_SELF")
                                    || c.Axes.Contains("EXHAUST_PRODUCER"))) exhaustSources++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && (c.Axes.Contains("EXHAUST_SELF")
                                    || c.Axes.Contains("EXHAUST_PRODUCER"))) exhaustSources++;

        if (exhaustSources == 0)
        {
            b -= 200;
            parts.Add("feelNoPainNoExhaust=-200");
            return;
        }
        int turns = RemainingTurnsEstimator.From(state);
        // Expect ~exhaustSources / 2 exhausts per turn, ×3 block per
        int blockPerTurn = (exhaustSources / 2) * 3;
        int v = blockPerTurn * turns * 30 / 2;  // /2 calibration (mid-late game)
        const int Cap = 1000;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"feelNoPain(exhSrc{exhaustSources},turns{turns}={v})");
    }

    /// <summary>
    /// v0.7.69 — PACTS_END (Ironclad S Attack 0c): AOE 17 dmg if exhaust pile
    /// has ≥3 cards. Otherwise unplayable (handled by CanPlay). When playable,
    /// massive value.
    /// </summary>
    private static void ApplyPactsEndGated(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.ExhaustPileSize < 3)
        {
            // CanPlay should have filtered, but defensive
            b -= 200;
            parts.Add($"pactsEndNoExhaust({state.ExhaustPileSize}/3)=-200");
            return;
        }
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;
        // 17 dmg × aliveCount × 50 / 10 calibration on top of base attack score
        int v = 17 * aliveCount * 50 / 10 / 4;  // /4 since base AOE damage already credited
        const int Cap = 600;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"pactsEnd(exh{state.ExhaustPileSize},alive{aliveCount}={v})");
    }

    /// <summary>
    /// v0.7.69 — CHILL (Defect S Skill 0c): channel Frost per alive enemy.
    /// 0-cost AOE Frost generator. Value scales with alive count + orb-active.
    /// </summary>
    private static void ApplyChillFrostPerEnemy(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerOrbCapacity == 0)
        {
            b -= 100;
            parts.Add("chillNoOrb=-100");
            return;
        }
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;
        // Each Frost orb = block ~2 passive + 5 evoke, ~180 value.
        // Capped by orb queue capacity (overflow kicks out).
        int frostsAdded = System.Math.Min(aliveCount,
            System.Math.Max(1, state.PlayerOrbCapacity - state.PlayerOrbCount));
        int v = frostsAdded * 180;
        const int Cap = 900;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"chill(alive{aliveCount},addFrost{frostsAdded}={v})");
    }

    /// <summary>
    /// v0.7.69 — ALCHEMIZE (Silent/Shared S Skill 1c): random potion + exhaust.
    /// Potion value is uncertain but typically 200-400 in-combat.
    /// </summary>
    private static void ApplyAlchemizePotion(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Conservative: 280 average potion value. Variance handled by
        // CardVariance separately (it's High variance).
        const int v = 280;
        b += v;
        parts.Add($"alchemize(randomPotion={v})");
    }

    /// <summary>
    /// v0.7.69 — BURNING_PACT (Ironclad S Skill 1c): "Exhaust 1 hand card.
    /// Draw 2 cards." Cheap deck-cycle + cleanse.
    /// </summary>
    private static void ApplyBurningPactExhaustDraw(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count curses/status in hand — primary exhaust targets.
        int curses = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) curses++;
        }
        int exhaustValue = curses > 0 ? 250 : 60;  // Big value if cleansing curses
        // Draw 2 — already credited by DRAW axis. Add a small synergy bonus.
        int v = exhaustValue + 80;  // exhaust + small draw amplifier
        b += v;
        parts.Add($"burningPact(curse{curses}={v})");
    }

    /// <summary>
    /// v0.7.69 — EVIL_EYE (Ironclad B Skill 1c): "Block 8. If you exhausted
    /// any card this turn, gain 8 more block." Conditional bonus.
    /// </summary>
    private static void ApplyEvilEyeConditional(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // We don't track exhausted-this-turn separately. Use CombatPlayerHpLossEvents
        // as a weak proxy? No — better: check if hand has any EXHAUST_SELF
        // cards expected to play this turn.
        int handExhausters = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("EXHAUST_SELF") && c.IsPlayable)
                handExhausters++;
        }
        if (handExhausters == 0 && state.ExhaustPileSize == 0)
        {
            // No exhaust source this turn — bonus likely won't trigger.
            return;
        }
        // Probable trigger: +8 block × 30
        int bonus = 8 * 30 / 2;  // half (might not actually trigger order-wise)
        b += bonus;
        parts.Add($"evilEye(conditional+8block={bonus})");
    }

    /// <summary>
    /// v0.7.74 — HIDDEN_CACHE (Regent B, 1c): "Gain 1 star. Next turn, gain
    /// 3 stars." Catalog vars["Stars"]=1 captures only the this-turn portion;
    /// the +3 next-turn stars are encoded in the card class internally.
    ///
    /// This handler adds the delayed-star value as a forward credit.
    /// ApplyStarsGain already handles the this-turn 1.
    /// </summary>
    private static void ApplyHiddenCacheDelayedStars(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Next-turn 3 stars. Use the same per-star valuation as immediate stars
        // (consumer-presence multiplier). Discount slightly for next-turn delay.
        int consumers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("STAR_CONSUMER")) consumers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("STAR_CONSUMER")) consumers++;
        foreach (var c in state.DiscardPile)
            if (c.Axes != null && c.Axes.Contains("STAR_CONSUMER")) consumers++;

        int perStar = consumers > 0 ? 120 : 30;  // slightly lower than ApplyStarsGain (delay discount)
        const int NextTurnStars = 3;
        int v = NextTurnStars * perStar;
        const int Cap = 500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"hiddenCacheNext(S{NextTurnStars}x{perStar},cons{consumers}={v})");
    }

    /// <summary>
    /// v0.7.74 — CONVERGENCE (Regent S, 1c): "Next turn, gain 1 star and 1
    /// energy. This turn, retain hand." Massive delayed value.
    /// </summary>
    private static void ApplyConvergenceNextTurn(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Next-turn 1 star + 1 energy. Plus retain (cards survive to next turn).
        int handCount = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsPlayable && !c.IsCurseOrStatus) handCount++;
        }
        // Star value (1 star, next turn, with consumer multiplier)
        int consumers = 0;
        foreach (var c in state.DrawPile)
            if (c.Axes != null && c.Axes.Contains("STAR_CONSUMER")) consumers++;
        int starValue = consumers > 0 ? 120 : 30;
        // Energy 1 next turn = ~400 (one extra play unlock)
        const int EnergyValue = 400;
        // Retain bonus: each saved card = ~80 (avoids draw RNG)
        int retainValue = handCount * 80;

        int v = starValue + EnergyValue + retainValue;
        const int Cap = 1200;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"convergenceNext(★{starValue}+⚡{EnergyValue}+retain{retainValue}={v})");
    }

    /// <summary>
    /// v0.7.68 — Generic Skeleton Summon-N handler. Variants of AFTERLIFE /
    /// LEGION_OF_BONE / CLEANSE / INVOKE / BODYGUARD / NECRO_MASTERY /
    /// PULL_AGGRO / SPUR / REANIMATE. Per-skeleton value scaled by deck
    /// SKELETON_CONSUMER / OSTY presence.
    /// </summary>
    private static void ApplySkeletonSummonN(SimCard self, SimState state, ref int b, List<string> parts, int summonCount)
    {
        int perSkeleton = 130;  // slightly lower per-skeleton than the original 150 to
                                 // avoid over-pricing REANIMATE's 20 summon
        int consumers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && (c.Axes.Contains("SKELETON_CONSUMER")
                                    || c.Axes.Contains("SKELETON_AMPLIFIER")
                                    || c.Axes.Contains("OSTY")))
                consumers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("SKELETON_CONSUMER")
                                    || c.Axes.Contains("SKELETON_AMPLIFIER")
                                    || c.Axes.Contains("OSTY")))
                consumers++;
        // Diminishing returns for huge summon counts (REANIMATE 20) — board
        // can't usefully hold 20 skeletons simultaneously.
        int effectiveSummon = summonCount <= 6 ? summonCount : 6 + (summonCount - 6) / 3;
        int v = effectiveSummon * perSkeleton + consumers * 60;
        const int Cap = 1800;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"skeletonSummonN({summonCount}eff{effectiveSummon}x{perSkeleton}+cons{consumers}={v})");
    }

    /// <summary>
    /// v0.7.68 — Generic Forge-N handler. Gated on Blade presence (no Blade =
    /// penalty proportional to forge amount). Per-Forge value × projected
    /// Blade plays this combat.
    /// </summary>
    private static void ApplyForgeGeneric(SimCard self, SimState state, ref int b, List<string> parts, int forgeAmount)
    {
        int turns = RemainingTurnsEstimator.From(state);
        if (state.SovereignBladeCount == 0)
        {
            // v0.9 — Forge is NEVER wasted. Per decompile (ForgeCmd.Forge,
            // sts2.decompiled.cs:398974): if no SovereignBlade is in piles
            // (excluding exhaust), the game auto-creates one in hand AND
            // immediately applies the Forge amount to it. So the first Forge
            // produces a SovereignBlade(d = 10 + forgeAmount, cost 2, Retain).
            //
            // Value model: the auto-created SB will likely play 1-2 times
            // this combat (it's Retain so it persists). Conservative
            // valuation = (10 + forgeAmount) × DamageFree × 0.4 (combo
            // discount for the +2 energy commitment + 1 hand-slot loss).
            int firstPlayDmg = 10 + forgeAmount;
            int v = firstPlayDmg * EffectScoringWeights.DamageFree * 4 / 10;
            const int Cap = 1200;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"forgeCreateSb(d={firstPlayDmg},f{forgeAmount}={v})");
            return;
        }
        int projectedBladePlays = System.Math.Min(4, System.Math.Max(1, turns / 2));
        int vWithBlade = forgeAmount * projectedBladePlays * 50 / 12;
        const int CapWithBlade = 1500;
        if (vWithBlade > CapWithBlade) vWithBlade = CapWithBlade;
        b += vWithBlade;
        parts.Add($"forgeGeneric(F{forgeAmount}x{projectedBladePlays}={vWithBlade})");
    }

    /// <summary>
    /// v0.7.68 — Generic Star gain handler.
    /// v0.7.70 — Star-cost card enabler bonus. When gaining stars unlocks
    /// previously-unplayable star-cost cards (PlayerStars + gained ≥ card's
    /// required star_cost), award an unlock bonus. Plus considers in-hand
    /// star-cost cards specifically (immediate-use enabler).
    /// </summary>
    private static void ApplyStarsGain(SimCard self, SimState state, ref int b, List<string> parts, int starsGained)
    {
        int consumers = 0;
        // v0.7.70 — Also track star-cost cards currently locked by insufficient stars
        int unlockedInHand = 0;
        int unlockedDeck = 0;
        int currentStars = state.PlayerStars;
        int newStarTotal = currentStars + starsGained;

        void ScanForConsumers(IReadOnlyList<SimCard> pile, bool isHand)
        {
            foreach (var c in pile)
            {
                if (ReferenceEquals(c, self)) continue;
                if (c.Axes != null && c.Axes.Contains("STAR_CONSUMER")) consumers++;
                // v0.7.70 — star_cost is per-card via SourceRef game data;
                // not directly on SimCard. Approximate via STAR axis cards.
                bool hasStarCost = c.Axes != null
                    && (c.Axes.Contains("STAR") || c.Axes.Contains("STAR_CONSUMER")
                        || c.Axes.Contains("STAR_X_COST"));
                if (!hasStarCost) continue;
                // Heuristic star_cost — STARDUST/STAR_X_COST is 0 required (scales);
                // most STAR-axis cards have 2-3 star_cost. Treat as needing 2 stars
                // as conservative threshold. AI doesn't have direct star_cost field.
                const int AssumedStarCost = 2;
                bool wasLocked = currentStars < AssumedStarCost;
                bool nowUnlocked = newStarTotal >= AssumedStarCost;
                if (wasLocked && nowUnlocked)
                {
                    if (isHand) unlockedInHand++;
                    else unlockedDeck++;
                }
            }
        }
        ScanForConsumers(state.Hand, isHand: true);
        ScanForConsumers(state.DrawPile, isHand: false);
        ScanForConsumers(state.DiscardPile, isHand: false);

        int perStar = consumers > 0 ? 150 : 40;
        int v = starsGained * perStar;

        // v0.7.70 — Unlock bonuses
        if (unlockedInHand > 0)
            v += unlockedInHand * 200;  // immediate-use enabler
        if (unlockedDeck > 0)
            v += unlockedDeck * 60;  // future-draw enabler

        const int Cap = 1200;
        if (v > Cap) v = Cap;
        b += v;
        string unlockTag = (unlockedInHand + unlockedDeck) > 0
            ? $",unlk[H{unlockedInHand}/D{unlockedDeck}]" : "";
        parts.Add($"starsGain(S{starsGained}x{perStar},cons{consumers}{unlockTag}={v})");
    }

    /// <summary>
    /// v0.7.68 — BIG_BANG (S 0c): Forge 5 + Stars 1 combo card.
    /// </summary>
    private static void ApplyBigBangCombo(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Treat as both Forge 5 and Stars 1 contribution.
        ApplyForgeGeneric(self, state, ref b, parts, 5);
        ApplyStarsGain(self, state, ref b, parts, 1);
    }

    /// <summary>
    /// v0.7.68 — BULK_UP (Defect A 2c): -1 OrbSlot + Str 2 + Dex 2.
    /// PowerApps catch the buffs; this layer adds the orb-slot penalty
    /// (lose 1 slot is bad in mid/late game, neutral early).
    /// </summary>
    private static void ApplyBulkUpOrbSlots(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Penalty for losing orb slot — only bites if Defect is actively
        // managing orbs. If empty queue, slot loss is neutral.
        if (state.PlayerOrbCapacity == 0)
        {
            // Not Defect / no orb system — penalty is meaningless
            return;
        }
        int filled = state.PlayerOrbCount;
        int newCap = state.PlayerOrbCapacity - 1;
        // If we're already over the new cap, an orb gets kicked out.
        int kickedPenalty = filled > newCap ? -200 : -50;
        b += kickedPenalty;
        parts.Add($"bulkUpOrbSlot({filled}>{newCap}={kickedPenalty})");
    }

    /// <summary>
    /// v0.7.67 — THE_SMITH (Regent A, 1c): "Forge 30". Single largest one-shot
    /// Forge in the game. CardEffectSummary doesn't extract Forge var.
    ///
    /// Value: Forge 30 means next Blade play deals ~30 extra. With multiple
    /// Blade plays in combat, gains accumulate. Gated on Blade presence.
    /// </summary>
    private static void ApplyTheSmithForge30(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.SovereignBladeCount == 0)
        {
            b -= 400;
            parts.Add("theSmithNoBlade=-400");
            return;
        }
        int turns = RemainingTurnsEstimator.From(state);
        int projectedBladePlays = System.Math.Min(4, System.Math.Max(1, turns / 2));
        const int ForgeAmount = 30;
        // Forge 30 distributes across plays: each play uses ~Forge/N. Conservative:
        // first play gets bulk of the Forge effect.
        int forgeValue = ForgeAmount * projectedBladePlays * 50 / 12;  // /12 calibration
        const int Cap = 1800;
        if (forgeValue > Cap) forgeValue = Cap;
        b += forgeValue;
        parts.Add($"theSmith(Forge30x{projectedBladePlays}={forgeValue})");
    }

    /// <summary>
    /// v0.7.67 — Generic Skeleton Summon-N handler. AFTERLIFE / LEGION_OF_BONE
    /// share the "summon 6 skeletons, exhaust" pattern with different costs.
    ///
    /// Value: each skeleton ally provides ~150 (attack + body to soak hits).
    /// Capped at deck space + skeleton archetype synergy.
    /// </summary>
    private static void ApplySkeletonSummon6(SimCard self, SimState state, ref int b, List<string> parts, int cost)
    {
        const int SummonCount = 6;
        const int PerSkeletonValue = 150;
        // Check SKELETON_CONSUMER axis presence: skeletons have extra value
        // when the deck has cards that consume / amplify them.
        int consumers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && (c.Axes.Contains("SKELETON_CONSUMER")
                                    || c.Axes.Contains("SKELETON_AMPLIFIER")
                                    || c.Axes.Contains("OSTY")))
                consumers++;
        }
        foreach (var c in state.DrawPile)
            if (c.Axes != null && (c.Axes.Contains("SKELETON_CONSUMER")
                                    || c.Axes.Contains("SKELETON_AMPLIFIER")
                                    || c.Axes.Contains("OSTY")))
                consumers++;

        int v = SummonCount * PerSkeletonValue + consumers * 60;
        const int Cap = 1500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"skeletonSummon({SummonCount}x{PerSkeletonValue}+cons{consumers}={v})");
    }

    /// <summary>
    /// v0.7.66 — SUMMON_FORTH (Regent, C, 1c): "Forge 8. Fetch the Sovereign
    /// Blade (anywhere) to hand."
    ///
    /// Generic axis flow misses two things:
    ///   1. vars.Forge: 8 — CardEffectSummary doesn't carve out a Forge
    ///      field, so PlanScorer never sees the magnitude.
    ///   2. Blade fetch — pile-to-hand mechanic. If Blade is in draw/discard/
    ///      exhaust, this play surfaces it for immediate burst.
    ///
    /// Both effects gated on SovereignBlade presence anywhere; otherwise the
    /// card is just an axis-matching skill with no payoff.
    /// </summary>
    private static void ApplySummonForthForge(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.SovereignBladeCount == 0)
        {
            // No Blade in deck — Forge stacks but nothing to upgrade, fetch
            // has no target. The axis bonuses still credit the card (~400);
            // strip a chunk so it isn't picked over real plays.
            b -= 300;
            parts.Add("summonForthNoBlade=-300");
            return;
        }

        // Forge 8: significant Blade buff. Each Forge point ≈ +1 dmg on next
        // Blade play. Estimate Blade plays this combat (~2-3) × 8 forge ×
        // DamagePerPoint (50) / calibration.
        int turns = RemainingTurnsEstimator.From(state);
        int projectedBladePlays = System.Math.Min(3, System.Math.Max(1, turns / 2));
        const int ForgeAmount = 8;
        int forgeValue = ForgeAmount * projectedBladePlays * 50 / 10;

        // Blade fetch: if Blade is NOT already in hand, the fetch effect
        // adds significant immediate-access value.
        bool bladeInHand = false;
        foreach (var c in state.Hand)
        {
            if (c.Axes != null
                && (c.Axes.Contains("LORDS_BLADE_PRODUCER")
                    || c.Axes.Contains("LORDS_BLADE_AMPLIFIER"))
                && c.Id != null && c.Id.Contains("SOVEREIGN_BLADE"))
            {
                bladeInHand = true;
                break;
            }
        }
        int fetchValue = bladeInHand ? 0 : 350;  // Blade fetched-into-hand = enabling burst

        const int Cap = 1500;
        int v = System.Math.Min(Cap, forgeValue + fetchValue);
        b += v;
        parts.Add($"summonForth(forge8x{projectedBladePlays}={forgeValue}+fetch{fetchValue}={v})");
    }

    /// <summary>
    /// v0.7.65 — EXPOSE (Regent, A, 0c): "Remove all artifact and block from
    /// target enemy. Apply Vulnerable 2. Exhaust."
    ///
    /// Generic VULN_PRODUCER axis credits the Vuln. The artifact-strip and
    /// block-strip effects — primary reason this card exists — go unscored.
    /// Both are huge value vs specific enemies (Awakened One, Hexaghost
    /// in shielded state, etc.).
    /// </summary>
    private static void ApplyExposeStripArtifact(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Find target enemy's artifact + block. For Vakuu auto-play the
        // target choice happens at execution; we score the most useful
        // alive target as proxy.
        int bestArtifact = 0, bestBlock = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            if (e.ArtifactAmount > bestArtifact) bestArtifact = e.ArtifactAmount;
            if (e.Block > bestBlock) bestBlock = e.Block;
        }
        // Each artifact charge = ~150 (would otherwise block one debuff apply).
        // Each block point = ~30 (saves that much damage delivery).
        int v = bestArtifact * 150 + bestBlock * 30;
        if (v == 0)
        {
            // No artifact / no block on any enemy — Exhaust cost without payoff.
            // Vuln value still credited by axis; this handler just adds 0.
            return;
        }
        const int Cap = 1200;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"expose(artifact{bestArtifact}+block{bestBlock}={v})");
    }

    /// <summary>
    /// v0.7.65 — CONQUEROR (Regent, D, 1c): "Forge 3. This turn, target enemy
    /// takes 2× damage from Lord's Blade." Forge 3 is covered by axis;
    /// the 2× damage doubler — the main payoff — is mostly missed.
    ///
    /// Value scales with whether we have a Lord's Blade play queued THIS
    /// turn (otherwise the 2× window expires unused).
    /// </summary>
    private static void ApplyConquerorBladeDouble(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.SovereignBladeCount == 0)
        {
            // No active Blade — Forge stacks but 2× has no target this turn.
            b -= 100;
            parts.Add("conquerorNoBlade=-100");
            return;
        }
        // Find a Lord's Blade play queued this turn (LORDS_BLADE_AMPLIFIER or
        // LORDS_BLADE_PRODUCER cards typically trigger blade plays).
        // Proxy: estimate avg Blade damage and double half of it.
        int estimatedBladeDmg = 18 + state.PlayerStrength * 3;  // baseline blade damage
        int v = estimatedBladeDmg * 50 / 10;  // half of doubled (we get +N not +2N effective)
        const int Cap = 800;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"conquerorBlade2x(estDmg={estimatedBladeDmg}={v})");
    }

    /// <summary>
    /// v0.7.46 — STORM_OF_STEEL (Silent, D, 1c): discard entire hand, add 1
    /// Shiv+ per discarded card. Net = trade hand → Shiv burst.
    /// </summary>
    private static void ApplyStormOfSteelShivs(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int discardable = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            discardable++;
        }
        if (discardable == 0)
        {
            b -= 100;
            parts.Add("stormOfSteelEmpty=-100");
            return;
        }
        // Shiv+ ≈ 6 dmg × random target. Per-Shiv value ~ 6 × 50 ÷ 4 (4 for
        // calibration — random target / not guaranteed best target).
        const int PerShivValue = 75;
        int v = discardable * PerShivValue;
        // Penalty for trashing hand cards (lose their direct value). Subtract
        // ~half the average hand-card value as cost.
        int handValueSum = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            handValueSum += EstimateCardPower(c, state, freeUse: false);
        }
        int trashCost = handValueSum / (2 * System.Math.Max(1, discardable));  // average / 2
        int net = v - trashCost * discardable;
        const int Cap = 800;
        if (net > Cap) net = Cap;
        if (net < -300) net = -300;
        b += net;
        parts.Add($"stormOfSteel(discard{discardable}x{PerShivValue}-trash{trashCost*discardable}={net})");
    }

    /// <summary>
    /// v0.7.46 — SHADOW_STEP (Silent, D, 1c): discard entire hand. **Next
    /// turn, all card damage is doubled.** Massive setup card.
    /// </summary>
    private static void ApplyShadowStepDoubleDmg(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Next-turn double-damage value = projected next-turn attack damage × 1.0
        // (the extra +100% over the baseline). Use deck attack mean × 3 attacks/turn.
        int totalAtkDmg = 0, atkCount = 0;
        foreach (var c in state.DrawPile)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            totalAtkDmg += c.TotalDamage;
            atkCount++;
        }
        foreach (var c in state.DiscardPile)
        {
            if (!c.IsAttack || c.IsCurseOrStatus) continue;
            totalAtkDmg += c.TotalDamage;
            atkCount++;
        }
        if (atkCount == 0)
        {
            b -= 100;
            parts.Add("shadowStepNoAttacks=-100");
            return;
        }
        int avgDmg = totalAtkDmg / atkCount;
        // ~3 attacks next turn × avg × 50 (DmgPoint). This is the EXTRA damage
        // from doubling — not the total — so it's 1× avg per attack.
        const int AttacksPerTurn = 3;
        int v = avgDmg * AttacksPerTurn * 50 / 10;  // calibration /10
        const int Cap = 1200;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"shadowStep(avgAtk{avgDmg}x{AttacksPerTurn}x2={v})");
    }

    /// <summary>
    /// v0.7.45 — PROLONG (연장, Shared, A, 0c): "Next turn, gain block equal to
    /// your current block. Exhaust." Pure state-dependent — credits as next-
    /// turn block carryover at BlockPerPointBonus per current block point.
    ///
    /// Empty / zero-block plays self-penalize (no carryover, just exhaust loss).
    /// Threat-aware: if next turn has no incoming damage, carryover is wasted.
    /// </summary>
    private static void ApplyProlongCarryover(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int currentBlock = state.PlayerBlock;
        if (currentBlock <= 0)
        {
            // No block to carry. Exhausts self for no value — small penalty so
            // AI doesn't pick this as a "free 0-cost play".
            b -= 200;
            parts.Add("prolongNoBlock=-200");
            return;
        }

        // Approximate value: current block × 30 (BlockPerPointBonus). This
        // matches how DEFEND-equivalent block is scored elsewhere.
        const int BlockPerPoint = 30;
        int carryValue = currentBlock * BlockPerPoint;

        // Discount when next turn has no significant incoming threat. Without
        // intent visibility into the NEXT enemy turn (current intent only),
        // use the THIS-turn threat as a coarse proxy — if enemies are all
        // inert or buffing now, they're often also low-threat next.
        bool anyAttacker = false;
        foreach (var e in state.Enemies)
            if (e.IsAlive && !e.IsInert && (e.HasAttackIntent || e.HasDeathBlowIntent))
            { anyAttacker = true; break; }
        if (!anyAttacker) carryValue = carryValue / 2;  // halve when low-threat

        const int Cap = 900;
        if (carryValue > Cap) carryValue = Cap;
        b += carryValue;
        parts.Add($"prolong(block{currentBlock}x30={carryValue})");
    }

    /// <summary>
    /// v0.7.44 — MODDED (Defect, S, 0c, REPEAT:1): channel 1 orb + draw 1 +
    /// play this card 1 more time. Self-replay = effective 2× channel + 2× draw.
    /// </summary>
    private static void ApplyModdedReplay(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Self-replay credit: equivalent to playing channel + draw twice.
        // Channel value depends on what gets channeled; use baseline orb value.
        const int ChannelValue = 180;  // typical orb passive + part of evoke
        const int DrawValue = 200;
        const int RepeatMultiplier = 2;  // base + 1 repeat
        const int Cap = 1200;
        int v = System.Math.Min(Cap, (ChannelValue + DrawValue) * RepeatMultiplier);
        b += v;
        parts.Add($"modded(channel+draw x{RepeatMultiplier}={v})");
    }

    // ─── v0.7.32 — Defect orb stem Power passives ──────────────────────────────
    //
    // Shared gate: PlayerOrbCapacity == 0 means we're not playing Defect (or
    // orb queue hasn't been initialized). All orb-stem Powers strip the baked
    // baseline in that case.

    /// <summary>
    /// v0.7.32 — Helper: count orb-color presence in OrbQueue.
    /// </summary>
    private static int CountOrbsOfKind(SimState state, Sts2CombatAI.Sim.OrbKind kind)
    {
        int n = 0;
        for (int i = 0; i < state.OrbQueue.Count; i++)
            if (state.OrbQueue[i] == kind) n++;
        return n;
    }

    /// <summary>
    /// v0.7.32 — Helper: count orb-related axis cards in hand+deck (excl. self).
    /// </summary>
    private static (int channels, int evokes) CountOrbCards(SimCard self, SimState state)
    {
        int ch = 0, ev = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.ChannelCount > 0) ch++;
            if (c.EvokeCount > 0) ev++;
        }
        foreach (var c in state.DrawPile)
        {
            if (c.ChannelCount > 0) ch++;
            if (c.EvokeCount > 0) ev++;
        }
        foreach (var c in state.DiscardPile)
        {
            if (c.ChannelCount > 0) ch++;
            if (c.EvokeCount > 0) ev++;
        }
        return (ch, ev);
    }

    private static bool IsOrbActive(SimState state) => state.PlayerOrbCapacity > 0;

    /// <summary>
    /// v0.7.32 — CapacitorPower (Defect, B): +2 orb slots. Value depends on
    /// channel saturation rate × turns. More slots = less orb overflow waste.
    /// </summary>
    private static void ApplyCapacitorTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 600;
        int baked = PowerCatalog.LookupSelfBuff("CapacitorPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"capacitorNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        var (channels, _) = CountOrbCards(self, state);
        // Saturation = orbs / capacity; high saturation means overflow risk
        double saturation = state.PlayerOrbCount / (double)System.Math.Max(1, state.PlayerOrbCapacity);
        // Extra slot value = saved overflow × turns × per-orb-value
        const int PerOrbValue = 120;
        int tick = (saturation > 0.6 ? turns * 2 : turns) * PerOrbValue + channels * 40;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"capacitorTick(sat={saturation:F2},channels={channels})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — CoolantPower (Defect, A): +N block per Frost orb on a trigger.
    /// Value scales with Frost orb count × turns × 30.
    /// </summary>
    private static void ApplyCoolantTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("CoolantPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"coolantNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        int frostNow = CountOrbsOfKind(state, OrbKind.Frost);
        // Steady state: 1-2 Frost orbs maintained, +block per trigger
        int projFrost = System.Math.Max(frostNow, 1);
        const int BlockPerFrost = 4;
        int tick = turns * projFrost * BlockPerFrost * 30 / 4;  // /4 since not every turn
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"coolantTick(frost={frostNow},turns={turns})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — SpinnerPower (Defect, A): free Frost orb / turn. Tick = turns
    /// × FrostOrbValue. Frost orb evoke value ~ 5 block.
    /// </summary>
    private static void ApplySpinnerTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 900;
        int baked = PowerCatalog.LookupSelfBuff("SpinnerPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"spinnerNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        const int FrostOrbValue = 180;  // ~2 passive + 5 evoke block × 30
        int tick = turns * FrostOrbValue;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"spinnerTick(turns={turns}x{FrostOrbValue})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — ThunderPower (Defect, A): +6 dmg on Lightning evoke.
    /// Value = projected Lightning evokes × 6 × DamagePerPoint.
    /// </summary>
    private static void ApplyThunderTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 800;
        int baked = PowerCatalog.LookupSelfBuff("ThunderPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"thunderNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        int lightning = CountOrbsOfKind(state, OrbKind.Lightning);
        var (_, evokes) = CountOrbCards(self, state);
        if (lightning == 0 && evokes == 0)
        {
            b -= baked;
            parts.Add($"thunderNoLightningPath=-{baked}");
            return;
        }

        // Projected lightning evokes over combat = 1 per ~3 turns base + evoke producers
        int projEvokes = (turns / 3) + System.Math.Min(evokes, 3);
        int tick = projEvokes * 6 * 50;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"thunderTick(lightning={lightning},evokes~{projEvokes})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — LoopPower (Defect, D): rightmost orb passive triggers 2x per
    /// turn. Value depends on orb-queue content × turns. With Frost/Lightning
    /// at the rightmost slot, the doubled passive ticks add up.
    /// </summary>
    private static void ApplyLoopTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 500;
        int baked = PowerCatalog.LookupSelfBuff("LoopPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"loopNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        // Average orb passive value ~40 per turn; doubled rightmost = +40/turn
        const int PassiveBonusPerTurn = 40;
        int orbsHeld = state.OrbQueue.Count;
        if (orbsHeld == 0)
        {
            // Useless if no orbs are typically held — strip half baseline.
            b -= baked / 2;
            parts.Add($"loopNoOrbsHeld=-{baked/2}");
            return;
        }
        int tick = turns * PassiveBonusPerTurn;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"loopTick(orbs={orbsHeld},turns={turns})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — ConsumingShadowPower (Defect, D): channels 2 Dark / turn,
    /// evokes leftmost. Net = 2 Dark per turn − 1 evoke (often Dark itself).
    /// Heavy Dark-archetype play.
    /// </summary>
    private static void ApplyConsumingShadowTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("ConsumingShadowPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"consumingShadowNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        int darkOrbs = CountOrbsOfKind(state, OrbKind.Dark);
        // Dark orb scaling value — 2 channels + 1 evoke per turn, conservative 250 net
        const int NetPerTurn = 250;
        int tick = turns * NetPerTurn + darkOrbs * 30;
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"consumingShadowTick(dark={darkOrbs},turns={turns})={delta:+#;-#;0}");
    }

    /// <summary>
    /// v0.7.32 — HailstormPower (Defect, C): turn-end AOE 6 if Frost held.
    /// </summary>
    private static void ApplyHailstormTickValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        const int Cap = 700;
        int baked = PowerCatalog.LookupSelfBuff("HailstormPower");
        if (!IsOrbActive(state)) { b -= baked; parts.Add($"hailstormNoOrb=-{baked}"); return; }

        int turns = RemainingTurnsEstimator.From(state);
        int aliveCount = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) aliveCount++;
        if (aliveCount == 0) return;

        int frostNow = CountOrbsOfKind(state, OrbKind.Frost);
        // Trigger rate: 70% of turns Frost present in steady state
        double frostRate = frostNow > 0 ? 0.85 : 0.5;
        int tick = (int)(turns * frostRate * aliveCount * 6 * 50 / 10);
        int delta = tick - baked;
        if (delta > Cap) delta = Cap;
        if (delta < -baked) delta = -baked;

        b += delta;
        parts.Add($"hailstormTick(frost={frostNow},alive={aliveCount},rate={frostRate:F2})={delta:+#;-#;0}");
    }

    // ─── v0.7.11 — Self-copy chain handlers ────────────────────────────────────
    //
    // Each chain card seeds a future play of itself or a chosen card. Per-play
    // value is multiplied by an "expected future plays" estimate, then heavily
    // discounted for chain uncertainty (deck cycling, draw RNG, exhaust risk).
    //
    // Shared discount: 0.4 — every future play is "you might draw the copy in
    // a useful state; you might not". Half-life one turn.

    private const double ChainDiscount = 0.4;

    /// <summary>
    /// ANGER (B, Ironclad) — 6 dmg + adds copy to discard pile. Each future
    /// shuffle returns it to draw, so ANGER deck cycles multiple Angers per
    /// combat. Bonus = (turns-1) × per-play-value × discount, capped to keep
    /// pre-shuffle ANGER from over-dominating early-game scoring.
    /// </summary>
    private static void ApplyAngerChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        int futurePlays = System.Math.Min(3, System.Math.Max(0, turns - 1));
        if (futurePlays == 0) return;
        // per future ANGER: 6 dmg × DamageInHand + cost-0 bonus
        int perPlay = 6 * EffectScoringWeights.DamageInHand + EffectScoringWeights.Cost0Bonus;
        int v = (int)(futurePlays * perPlay * ChainDiscount);
        const int Cap = 400;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"angerChain(plays={futurePlays}xperPlay={perPlay}x{ChainDiscount})=+{v}");
    }

    /// <summary>
    /// UNDEATH (A, Necrobinder) — 7 block + adds copy to discard pile. Mirror
    /// of ANGER but for block. Value gates on remaining-turns of block need.
    /// </summary>
    private static void ApplyUndeathChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        int futurePlays = System.Math.Min(3, System.Math.Max(0, turns - 1));
        if (futurePlays == 0) return;
        int perPlay = 7 * EffectScoringWeights.BlockInHand + EffectScoringWeights.Cost0Bonus;
        int v = (int)(futurePlays * perPlay * ChainDiscount);
        const int Cap = 400;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"undeathChain(plays={futurePlays}xperPlay={perPlay}x{ChainDiscount})=+{v}");
    }

    /// <summary>
    /// DUAL_WIELD (B, Shared) — duplicates the best Attack OR Power in hand.
    /// Player picks the highest-value target. Bonus = max EstimateCardPower
    /// over hand Attacks + Powers (excluding self).
    /// </summary>
    private static void ApplyDualWieldChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int best = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            if (!c.IsAttack && !c.IsPower) continue;
            int v = EstimateCardPower(c, state, freeUse: false);
            if (v > best) best = v;
        }
        if (best == 0) { b += 60; parts.Add("dualWieldNoTargets=+60"); return; }
        const double Discount = 0.7; // player choice — high confidence
        int bonus = (int)(best * Discount);
        b += bonus;
        parts.Add($"dualWieldCopy(best={best}x{Discount})=+{bonus}");
    }

    /// <summary>
    /// HEIRLOOM_HAMMER (C, Regent) — 20 dmg + duplicates a chosen hand Attack.
    /// Bonus = max EstimateCardPower over hand Attacks (excluding self).
    /// </summary>
    private static void ApplyHeirloomHammerChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int best = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsAttack) continue;
            int v = EstimateCardPower(c, state, freeUse: false);
            if (v > best) best = v;
        }
        if (best == 0) { b += 60; parts.Add("hammerNoTargets=+60"); return; }
        const double Discount = 0.7;
        int bonus = (int)(best * Discount);
        b += bonus;
        parts.Add($"hammerCopy(best={best}x{Discount})=+{bonus}");
    }

    /// <summary>
    /// NIGHTMARE (B, Silent) — picks a card, adds 3 copies next turn (Exhausts
    /// self). Massive but delayed. Bonus = 3 × best hand card × Discount × NextTurn-discount.
    /// </summary>
    private static void ApplyNightmareChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int best = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            int v = EstimateCardPower(c, state, freeUse: false);
            if (v > best) best = v;
        }
        if (best == 0) { b += 50; parts.Add("nightmareNoTargets=+50"); return; }
        // 3 copies next turn — player picks, so high-quality selection. Next-turn
        // discount layered on top of player-choice confidence.
        const double NextTurnDiscount = 0.5;
        int bonus = (int)(3 * best * NextTurnDiscount);
        const int Cap = 900;
        if (bonus > Cap) bonus = Cap;
        b += bonus;
        parts.Add($"nightmareNext(3x{best}x{NextTurnDiscount})=+{bonus}");
    }

    /// <summary>
    /// ADAPTIVE_STRIKE (B, Defect) — 18 dmg + adds 0-cost copy to draw pile.
    /// The copy is a free 18-damage Strike on next-cycle play. Heavy discount
    /// because it lands somewhere in draw pile (may not be drawn this combat).
    /// </summary>
    private static void ApplyAdaptiveStrikeChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Value of the free copy: 18 dmg × DamageFree (50) = 900. No cost bonus
        // (it's free-use). Discount heavily — depends on draw timing.
        int freeCopyValue = 18 * EffectScoringWeights.DamageFree;
        const double Discount = 0.4;
        int bonus = (int)(freeCopyValue * Discount);
        b += bonus;
        parts.Add($"adaptiveCopy(freeVal={freeCopyValue}x{Discount})=+{bonus}");
    }

    /// <summary>
    /// v0.7.17 — ALL_FOR_ONE (S, Defect): "Deal 10 damage. Bring ALL 0-cost
    /// cards from discard pile to hand." Hand refill mechanism — high value
    /// when discard has accumulated free 0-cost plays. Empty discard ≈ +60
    /// baseline (small positive for the recall potential next turn).
    ///
    /// Sum EstimateCardPower over discard 0-cost non-curse cards (in-hand
    /// value, since they'll be played at cost 0 = free). Cap at +1200 so a
    /// massive discard pile doesn't dominate scoring beyond hand-cap reality.
    /// </summary>
    private static void ApplyAllForOneRecall(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int total = 0, count = 0;
        foreach (var c in state.DiscardPile)
        {
            if (c.Cost != 0) continue;
            if (c.IsCurseOrStatus) continue;
            total += EstimateCardPower(c, state, freeUse: false);
            count++;
        }
        if (count == 0)
        {
            b += 60;
            parts.Add("allForOneEmpty=+60");
            return;
        }
        int v = total;
        const int Cap = 1200;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"allForOneRecall(count={count},sum={total})=+{v}");
    }

    /// <summary>
    /// v0.7.17 — PINPOINT (S, Silent): "Deal 15 damage. For each Skill used
    /// this turn, refund 1 energy." Bonus = TurnSkillsPlayed × per-energy
    /// value. Skills already played → already discounted (we played them);
    /// PINPOINT after multiple Skills can fully refund itself + leftover.
    /// </summary>
    private static void ApplyPinpointEnergyRefund(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int skills = state.TurnSkillsPlayed;
        if (skills <= 0) return;
        // Energy refund: use the in-hand energy weight (60). Refund mid-turn
        // is like gaining cost-free plays equal to refunded energy.
        int v = skills * EffectScoringWeights.EnergyInHand;
        b += v;
        parts.Add($"pinpointRefund(skills={skills}x{EffectScoringWeights.EnergyInHand})=+{v}");
    }

    /// <summary>
    /// v0.7.18 — FLECHETTES (A, Silent, 1c 5dmg): "Deal 5 damage per Skill
    /// in hand". Catalog vars have CalculatedHits (PreviewValue at runtime)
    /// but reflection may miss when preview isn't refreshed. Defensive
    /// fallback: if card.Hits stayed at 1 (preview presumed failed), count
    /// hand Skills directly and credit the missing hits.
    /// </summary>
    private static void ApplyFlechettesHandSkills(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int skillsInHand = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            if (c.IsSkill) skillsInHand++;
        }
        // Already-counted hits via CalculatedVar reflection — don't double-count.
        int extraHits = System.Math.Max(0, skillsInHand - self.Hits);
        if (extraHits <= 0) return;
        int v = extraHits * 5 * EffectScoringWeights.DamageInHand;
        b += v;
        parts.Add($"flechettes(extraHits={extraHits}x5x{EffectScoringWeights.DamageInHand})=+{v}");
    }

    /// <summary>
    /// v0.7.18 — MAKE_IT_SO (A, Regent, 0c 6dmg): "Deal 6 damage. If you've
    /// played 3+ Skills this turn, return this card to your hand." Effectively
    /// a free 6-damage replay each turn when the Skill threshold is met.
    /// Scale linearly from 0 to full reclaim value across 0-3 TurnSkillsPlayed.
    /// </summary>
    private static void ApplyMakeItSoReclaim(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int skills = state.TurnSkillsPlayed;
        if (skills <= 0) return;
        double prob = System.Math.Min(1.0, skills / 3.0);
        // Reclaim = next-turn (or this-turn-after-3-skills) free play of 6 dmg
        int perPlay = 6 * EffectScoringWeights.DamageInHand;
        int v = (int)(perPlay * prob);
        b += v;
        parts.Add($"makeItSoReclaim(skills={skills},prob={prob:F2})=+{v}");
    }

    /// <summary>
    /// v0.7.18 — SUNDER (A, Defect, 3c 24dmg): "Deal 24 damage. If this kills
    /// the target, refund 3 energy." 24 dmg + Strength is enough to kill many
    /// mid-game enemies. Bonus = 3 × EnergyInHand (180) when the target's
    /// effective HP (Hp + Block) is ≤ projected damage.
    /// </summary>
    private static void ApplySunderKillRefund(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
    {
        if (targetIdx < 0 || targetIdx >= state.Enemies.Count) return;
        var target = state.Enemies[targetIdx];
        if (!target.IsAlive) return;
        int projected = 24 + System.Math.Max(0, state.PlayerStrength);
        if (state.PlayerVulnerable > 0 && target.VulnerableAmount > 0)
            projected = (int)(projected * 1.5);
        int effective = target.Hp + target.Block;
        if (projected < effective) return;
        // Kill confirmed → 3 energy refund value.
        int v = 3 * EffectScoringWeights.EnergyInHand;
        b += v;
        parts.Add($"sunderKillRefund(projected={projected}vs{effective})=+{v}");
    }

    /// <summary>
    /// v0.7.18 — TESLA_COIL (A, Defect, 0c 3dmg): "Deal 3 damage. Trigger
    /// every orb in your queue (auto-evoke all)." Free attack with massive
    /// orb-evoke payload. Each evoke roughly equates to ~200 score points
    /// (averaging Lightning 8dmg, Frost 5block, Dark accumulated, Plasma 2
    /// energy across the queue).
    /// </summary>
    private static void ApplyTeslaCoilEvokeAll(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int orbCount = state.OrbQueue?.Count ?? 0;
        if (orbCount <= 0) return;
        const int PerOrbEvokeBonus = 200;
        int v = orbCount * PerOrbEvokeBonus;
        b += v;
        parts.Add($"teslaCoilEvokeAll({orbCount} orbs)=+{v}");
    }

    /// <summary>
    /// v0.7.18 — THRUMMING_HATCHET (A, Shared, 1c 11dmg): "Deal 11 damage.
    /// At end of turn, return this card to your hand." Effectively a permanent
    /// 1c/11dmg play every turn the player has spare energy. Chain bonus =
    /// (turns - 1) × per-play value × 0.5 (energy-competition discount).
    /// </summary>
    private static void ApplyThrummingHatchetChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        int futurePlays = System.Math.Max(0, turns - 1);
        if (futurePlays <= 0) return;
        int perPlay = 11 * EffectScoringWeights.DamageInHand + EffectScoringWeights.Cost1Bonus;
        const double Discount = 0.5;
        int v = (int)(futurePlays * perPlay * Discount);
        const int Cap = 1000;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"thrummingChain(plays={futurePlays}xperPlay={perPlay}x{Discount})=+{v}");
    }

    // ─── v0.9.1 — Replay-next-card amplifiers ──────────────────────────────
    //
    // ONE_TWO_PUNCH / BURST / SIGNAL_BOOST apply a Power that re-plays the
    // next Attack / Skill / Power the player uses this turn. Marginal value =
    // value of the BEST eligible target in hand. With no eligible target the
    // amplifier scores ~0 (the wastage penalty in ComputePowerActivationPenalty
    // already covers the negative side).
    //
    // Conservative discount: the amplifier consumes 1 energy / a card slot, and
    // the target was going to be played anyway — only the *extra* copy is the
    // marginal gain. Use DamageInHand (35) / a smaller per-block weight, not
    // the full DamagePerPointBonus (50), to reflect the "second copy" framing.

    /// <summary>ONE_TWO_PUNCH (Ironclad, 1c Skill): next Attack repeats once.</summary>
    private static void ApplyReplayBestAttack(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int bestDmg = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsPlayable) continue;
            if (!c.IsAttack) continue;
            // Use TotalDamage so multi-hit attacks (TWIN_STRIKE, WHIRLWIND) are
            // valued for the full copy, not just per-hit.
            int dmg = c.TotalDamage;
            if (dmg > bestDmg) bestDmg = dmg;
        }
        if (bestDmg <= 0) return;
        int v = bestDmg * EffectScoringWeights.DamageInHand;
        b += v;
        parts.Add($"oneTwoPunch(replayBest={bestDmg}x{EffectScoringWeights.DamageInHand})=+{v}");
    }

    /// <summary>BURST (Silent, 1c Skill): next Skill repeats once.</summary>
    private static void ApplyReplayBestSkill(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Use total block as the primary proxy. For non-block skills (debuffs,
        // draw) the PowerCatalog/HandSynergy already factor in the power-apply
        // value at the original play — doubling it via a flat per-card add
        // would over-credit, so block is the cleanest first-pass approximation.
        int bestBlock = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsPlayable) continue;
            if (!c.IsSkill) continue;
            if (c.Block > bestBlock) bestBlock = c.Block;
        }
        if (bestBlock <= 0) return;
        // BlockPerPointBonus ~ 30 (mirrors HandSynergy.RageSynergyPerAttack).
        const int BlockPerPoint = 30;
        int v = bestBlock * BlockPerPoint;
        b += v;
        parts.Add($"burst(replayBlock={bestBlock}x{BlockPerPoint})=+{v}");
    }

    /// <summary>SIGNAL_BOOST (Defect, 1c Skill, Exhaust): next Power repeats.</summary>
    private static void ApplyReplayBestPower(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Best Power in hand by PowerCatalog tier. The marginal value is one
        // extra full activation; Powers are typically high-value (300-800), so
        // a half-credit (`v / 2`) keeps SIGNAL_BOOST from overshadowing the
        // Power itself.
        //
        // ValueSelfBuff can return NEGATIVE values for self-hostile powers
        // (NoDrawPower=-1000, NoBlockPower=-1000, ConfusedPower=-500, etc.).
        // Without a 0-floor, SIGNAL_BOOST scanning a hand containing one of
        // those Powers would crater. Clamp non-negative per-power so the
        // amplifier never "wants to replay" a negative — at worst the
        // amplifier scores 0 and is deferred.
        int bestPowerVal = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsPlayable) continue;
            if (!c.IsPower) continue;
            foreach (var kv in c.PowerApps)
            {
                int pv = PowerCatalog.ValueSelfBuff(kv.Key, System.Math.Max(1, kv.Value));
                if (pv < 0) continue;  // never "double up" a self-hostile power
                if (pv > bestPowerVal) bestPowerVal = pv;
            }
        }
        if (bestPowerVal <= 0) return;
        int v = bestPowerVal / 2;
        b += v;
        parts.Add($"signalBoost(replayPower=+{v} (best/2))");
    }

    /// <summary>STRANGLE (Silent, 1c Attack, 8 dmg): applies StranglePower —
    /// for the rest of this turn, every card play deals 2 HP loss to all
    /// alive enemies. Value = <c>remainingPlays × 2 × aliveEnemies × DamageInHand</c>.
    /// Remaining plays estimated from current energy / cheapest costs in hand,
    /// capped at 4 to avoid overestimating.</summary>
    private static void ApplyStrangleChip(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int aliveEnemies = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveEnemies++;
        if (aliveEnemies <= 0) return;

        // Rough remaining plays after STRANGLE itself: count playable cards in
        // hand (other than self) up to current energy budget, plus 0-cost
        // cards regardless of energy.
        int energyAfter = state.PlayerEnergy - System.Math.Max(0, self.Cost);
        int remaining = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsPlayable) continue;
            if (c.Cost == 0 || c.Cost <= energyAfter) remaining++;
        }
        remaining = System.Math.Min(4, remaining);
        if (remaining <= 0) return;

        const int HpLossPerCard = 2;
        int v = remaining * HpLossPerCard * aliveEnemies * EffectScoringWeights.DamageInHand;
        b += v;
        parts.Add($"strangleChip(plays={remaining}x{HpLossPerCard}x{aliveEnemies})=+{v}");
    }

    /// <summary>ECHOING_SLASH (Silent, 1c AOE 10dmg): kills an enemy → repeat
    /// the AOE. v0.9.3 — single-repeat half-credit replaced by chain-aware
    /// estimate (up to <c>MaxChains</c>=3). Enemies sorted by effective HP
    /// (HP + Block); each consecutive kill adds one chain. Bonus =
    /// <c>(chains−1) × self.Damage × DamageInHand / 2</c> (the base scorer
    /// already credits the first AOE wave, so subtract 1).</summary>
    private static void ApplyEchoingSlashOverkillBonus(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
    {
        if (self.Damage <= 0) return;
        int perHit = self.Damage + System.Math.Max(0, state.PlayerStrength);
        if (state.PlayerWeak > 0) perHit = (int)(perHit * 0.75);

        var sortedEnemies = state.Enemies
            .Where(e => e.IsAlive)
            .OrderBy(e => e.Hp + e.Block)
            .ToList();

        int chains = 1;
        const int MaxChains = 3;
        foreach (var e in sortedEnemies)
        {
            int dmg = perHit;
            if (e.VulnerableAmount > 0) dmg = (int)(dmg * StatusMath.VulnerableMult);
            if (e.DamageCapPerHit > 0 && dmg > e.DamageCapPerHit) dmg = e.DamageCapPerHit;
            if (dmg >= e.Hp + e.Block) chains++;
            if (chains >= MaxChains) break;
        }
        if (chains <= 1) return;

        int extraChains = chains - 1;
        int v = extraChains * self.Damage * EffectScoringWeights.DamageInHand / 2;
        b += v;
        parts.Add($"echoingChain(chains={chains},+{extraChains}×{self.Damage}÷2)=+{v}");
    }

    /// <summary>STOMP (Ironclad, 3c AOE 12dmg): cost -1 per Attack already
    /// played this turn. The runtime <c>Cost</c> already reflects the
    /// discount, so the planner sees a 1c/0c card directly when discount has
    /// fired. Forward-looking value (play attacks first to discount STOMP) is
    /// what's missing — credit a modest bonus proportional to the savings
    /// realized: <c>min(3, AtkT) × Cost1Bonus / 2</c> so a fully-discounted
    /// STOMP gets ~+150 (half a Cost1Bonus per saved energy).</summary>
    private static void ApplyStompCostDiscountValue(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int saved = System.Math.Min(3, state.TurnAttacksPlayed);
        if (saved <= 0) return;
        int v = saved * EffectScoringWeights.Cost1Bonus / 2;
        b += v;
        parts.Add($"stompDiscount(saved={saved}x{EffectScoringWeights.Cost1Bonus}/2)=+{v}");
    }

    // ─── v0.7.19 — B-tier 1-path coverage (9 cards) ────────────────────────

    // FINISHER scaling now handled directly in PlanScorer.EstimateVariableHits
    // (Hits = TurnAttacksPlayed, with AllowsZeroHits letting effHits drop to 0
    // when no attacks have been played yet). The previous bonus here added on
    // top of the base credit and double-counted at played ≥ 1; removed.

    /// <summary>BOLAS (B, Shared, 0c 3d): return-to-hand at end of turn.</summary>
    private static void ApplyBolasChain(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int turns = RemainingTurnsEstimator.From(state);
        int futurePlays = System.Math.Max(0, turns - 1);
        if (futurePlays <= 0) return;
        int perPlay = 3 * EffectScoringWeights.DamageFree + EffectScoringWeights.Cost0Bonus;
        const double Discount = 0.5;
        int v = (int)(futurePlays * perPlay * Discount);
        const int Cap = 500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"bolasChain(plays={futurePlays}xperPlay={perPlay}x{Discount})=+{v}");
    }

    /// <summary>FOLLOW_THROUGH (B, Silent, 1c 7d): +1 hit if 5+ other cards in hand.</summary>
    private static void ApplyFollowThroughRepeat(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int others = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            others++;
        }
        if (others < 5) return;
        int v = 7 * EffectScoringWeights.DamageInHand;
        b += v;
        parts.Add($"followThroughRepeat(others={others})=+{v}");
    }

    /// <summary>EXPECT_A_FIGHT (B, Ironclad, 2c Skill): gain energy per Power in hand.</summary>
    private static void ApplyExpectAFightEnergy(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int powers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            if (c.IsPower) powers++;
        }
        if (powers <= 0) return;
        // 1 energy per Power. Cost 2 to play, so net energy = powers - 2.
        int v = powers * EffectScoringWeights.EnergyInHand;
        b += v;
        parts.Add($"expectAFight(powersInHand={powers}x{EffectScoringWeights.EnergyInHand})=+{v}");
    }

    /// <summary>SPITE (B, Ironclad, 0c 5d): +2 dmg if HP lost this combat.</summary>
    private static void ApplySpiteHpLossBonus(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.CombatPlayerHpLossEvents <= 0) return;
        // +2 damage when HP loss event happened. The "이번 턴" qualifier is
        // tricky to track turn-scoped; CombatPlayerHpLossEvents > 0 is a
        // conservative proxy (combat-wide).
        int v = 2 * EffectScoringWeights.DamageInHand;
        b += v;
        parts.Add($"spiteHpLoss(+2dmg)=+{v}");
    }

    /// <summary>HEADBUTT (B, Ironclad, 1c 9d): move 1 card from discard to top of draw.</summary>
    private static void ApplyHeadbuttDeckPick(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.DiscardPile.Count == 0) return;
        // Player picks best card from discard → top of draw. Will be drawn
        // next turn (small probability boost over random draw). Value =
        // (best discard card value − pile mean) × small factor.
        int best = 0, sum = 0, count = 0;
        foreach (var c in state.DiscardPile)
        {
            if (c.IsCurseOrStatus) continue;
            int v0 = EstimateCardPower(c, state, freeUse: false);
            if (v0 > best) best = v0;
            sum += v0;
            count++;
        }
        if (count == 0 || best == 0) return;
        int mean = sum / count;
        // Top-of-draw guarantee = +1 turn earlier than random reshuffle ≈ 20% of card value differential.
        int v = (int)((best - mean) * 0.2);
        if (v <= 0) return;
        b += v;
        parts.Add($"headbuttPick(best={best},mean={mean})=+{v}");
    }

    /// <summary>REBOUND (B, Shared, 1c 9d): next Skill played goes to top of draw.</summary>
    private static void ApplyReboundSkillReclaim(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Value = mean Skill in hand × 30% (top-of-draw 1-turn faster).
        int sum = 0, count = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus || !c.IsSkill) continue;
            sum += EstimateCardPower(c, state, freeUse: false);
            count++;
        }
        if (count == 0) return;
        int mean = sum / count;
        int v = (int)(mean * 0.3);
        b += v;
        parts.Add($"reboundReclaim(skillMean={mean})=+{v}");
    }

    /// <summary>OUTMANEUVER (B, Shared, 1c Skill): +2 colorless energy next turn.</summary>
    private static void ApplyOutmaneuverNextTurnEnergy(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // 2 energy next turn = 2 × EnergyInHand × NextTurn discount.
        // Use 0.6 discount since the energy IS guaranteed (not RNG).
        int v = (int)(2 * EffectScoringWeights.EnergyInHand * 0.6);
        b += v;
        parts.Add($"outmaneuverNextE(2x{EffectScoringWeights.EnergyInHand}x0.6)=+{v}");
    }

    /// <summary>SEEKER_STRIKE (B, Shared, 1c 9d): pick 1 of 3 random cards from draw pile.</summary>
    private static void ApplySeekerStrikePick(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (state.DrawPile.Count == 0) return;
        // Top-1-of-3 from draw pile → take best of 3 random samples.
        // For simplicity, use mean ×1.4 as the order-statistic approximation
        // (similar to v0.7.2 PoolMeans top1of3 which is ~1.4× pile mean).
        int sum = 0, count = 0;
        foreach (var c in state.DrawPile)
        {
            if (c.IsCurseOrStatus) continue;
            sum += EstimateCardPower(c, state, freeUse: false);
            count++;
        }
        if (count == 0) return;
        int mean = sum / count;
        int v = (int)(mean * 1.4 * 0.6);  // top-of-3 value × discount (player picks)
        b += v;
        parts.Add($"seekerStrikePick(drawMean={mean})=+{v}");
    }

    private static void ApplyNextCardCostEnabler(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // UNRELENTING / SYNTHESIS / POUNCE — next card of given type is 0-cost.
        // Value = avg cost of remaining same-type cards × 200 (rough cost-to-score
        // conversion). With multiple targets, fires on the best one.
        bool isAtkEnabler = self.Axes.Contains("ATTACK_COST_ENABLER");
        bool isSkillEnabler = self.Axes.Contains("SKILL_COST_ENABLER");
        bool isPowerEnabler = self.Axes.Contains("POWER_COST_ENABLER");

        int bestCost = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (!c.IsPlayable && c.Cost <= state.PlayerEnergy) continue; // affordable already
            if (c.Cost <= 0) continue;
            bool typeMatch =
                (isAtkEnabler && c.IsAttack)
                || (isSkillEnabler && c.IsSkill)
                || (isPowerEnabler && c.IsPower);
            if (!typeMatch) continue;
            if (c.Cost > bestCost) bestCost = c.Cost;
        }
        if (bestCost > 0)
        {
            int v = bestCost * 220;       // unlocking a cost-N card ≈ +220×N points
            b += v;
            parts.Add($"costEnabler(savedX{bestCost})=+{v}");
        }
        else
        {
            b -= 150;
            parts.Add("costEnablerNoTarget=-150");
        }
    }

    /// <summary>
    /// v0.7.2 — Level 4 pool-based random card evaluation. For card ids whose
    /// generated card comes from a random pool (rather than a known token like
    /// Shiv), look up the character's pool distribution in <see cref="PoolMeans"/>
    /// and convert to a flat point value. Each card id maps to a specific
    /// (filter, aggregation, multiplier) tuple matching its in-game effect.
    ///
    /// Aggregation:
    ///   • <c>mean</c>      — single random card (no player choice).
    ///   • <c>top1of3</c>   — player picks 1 of 3 from the pool.
    ///   • <c>top1of5</c>   — wider pick.
    ///
    /// Multiplier:
    ///   • Per-turn Power generators (CREATIVE_AI / HELLO_WORLD / SENTRY_MODE /
    ///     SPECTRUM_SHIFT) use <c>RemainingTurnsProxy</c> to amortize over the
    ///     expected remaining combat length.
    ///   • One-shot generators use multiplier 1.
    ///   • JACKPOT (3 cards) uses multiplier 3.
    ///
    /// Returns true and writes <c>b</c> / <c>parts</c> when a pool-aware value
    /// was produced; false when the card isn't pool-based, the character id
    /// isn't known, or the relevant filter has no data — caller then falls
    /// through to the per-card-id flat magnitude.
    /// </summary>
    private static bool TryApplyPoolBasedRandom(SimCard self, SimState state, ref int b, List<string> parts)
    {
        if (string.IsNullOrEmpty(state.CharacterId)) return false;

        // Per-turn Power-passive generators: amortize value over the expected
        // remaining combat length. 3 turns is a coarse but conservative proxy —
        // boss fights run 6-10 turns, normal fights 3-5; using 3 avoids
        // overvaluing late-game Power plays where 1-2 turns remain.
        int RemainingTurnsProxy = RemainingTurnsEstimator.From(state);

        string filter;
        int n;            // # of cards generated this trigger
        int multiplier;   // turns (Power passives) or stacks (multi-card pulls)
        string aggregation; // "mean", "top1of3", "top1of5"
        string tag;

        switch (self.Id)
        {
            // Per-turn Power-passive generators (drop a card each turn).
            case "CREATIVE_AI":
                filter = "power_free"; aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "creativeAI"; break;
            case "HELLO_WORLD":
                filter = "common";     aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "helloWorld"; break;
            case "SPECTRUM_SHIFT":
                filter = "colorless";  aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "spectrumShift"; break;

            // One-shot, single random card (no choice).
            case "WHITE_NOISE":
                filter = "power_free"; aggregation = "mean"; n = 1; multiplier = 1; tag = "whiteNoise"; break;
            case "DISTRACTION":
                filter = "skill_free"; aggregation = "mean"; n = 1; multiplier = 1; tag = "distraction"; break;
            case "CALL_OF_THE_VOID":
                filter = "all_free";   aggregation = "mean"; n = 1; multiplier = 1; tag = "callOfVoid"; break;
            case "LARGESSE":
                filter = "colorless";  aggregation = "mean"; n = 1; multiplier = 1; tag = "largesse"; break;

            // Pick-of-N (player chooses one).
            case "DISCOVERY":
                filter = "all";        aggregation = "top1of3"; n = 1; multiplier = 1; tag = "discovery"; break;
            case "SPLASH":
                filter = "attack";     aggregation = "top1of3"; n = 1; multiplier = 1; tag = "splash"; break;

            // Multi-card pulls (each independent, sum value).
            case "JACKPOT":
                filter = "all_free";   aggregation = "mean"; n = 1; multiplier = 3; tag = "jackpot"; break;

            default:
                return false;
        }

        var summary = PoolMeans.Get(state.CharacterId, filter);
        if (summary.N == 0) return false; // no pool data — fall back

        int unit = aggregation switch
        {
            "top1of3" => summary.Top1Of3,
            "top1of5" => summary.Top1Of5,
            _         => summary.Mean,
        };

        int v = unit * n * multiplier;
        // Cap per-card pool-based bonus to keep one random card from dominating
        // scoring when the pool happens to skew rich (e.g. REGENT attack_free
        // top1of3 ~1035). Anchored to the flat-fallback ceiling band (S ≈ 600).
        const int CapPerCard = 800;
        if (v > CapPerCard) v = CapPerCard;

        b += v;
        parts.Add($"{tag}({filter}.{aggregation}×{multiplier})=+{v}");
        return true;
    }

    private static void ApplyCardGen(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // v0.7.2 — Try pool-aware evaluation first. When PoolMeans has data for
        // the current character + filter the card draws from, prefer that
        // (character-aware mean of the real pool). The flat switch below stays
        // as a fallback for: unknown character (test fixtures, mid-transition),
        // embedded resource missing, or pool too small to be meaningful.
        if (TryApplyPoolBasedRandom(self, state, ref b, parts))
            return;

        // Generators fall into three value tiers:
        //   • Concrete-attack tokens (Shiv, Slime+, etc.) — known damage.
        //   • Targeted choice (CHARGE/NIGHTMARE/GUARDS) — high-control, high-value.
        //   • Random card (CALL_OF_THE_VOID/CREATIVE_AI/HELLO_WORLD) — low predictability.
        int v = 0;
        switch (self.Id)
        {
            case "BLADE_OF_INK":     v = 600; break;   // S — 2 inked Shivs
            case "BLADE_DANCE":      v = 450; break;   // A — 3 Shivs
            case "UP_MY_SLEEVE":     v = 380; break;   // D — 3 Shivs, retains
            case "PRIMAL_FORCE":     v = 500; break;   // A — converts hand attacks
            case "GUARDS":           v = 350; break;   // A — convert hand to Sacrifice+
            case "CHARGE":           v = 350; break;   // A — pick 2 from draw → upgrade
            case "NIGHTMARE":        v = 400; break;   // B — 3 copies next turn
            case "JUGGLING":         v = 200; break;   // D — Power, 3rd attack copy
            case "JACKPOT":          v = 180; break;   // C — 3 zero-cost random
            case "CALL_OF_THE_VOID": v = 100; break;   // S — random card volatile
            case "CREATIVE_AI":      v = 150; break;   // B — Power, random Power/turn
            case "HELLO_WORLD":      v = 120; break;   // B — Power, random common/turn
            case "INFINITE_BLADES":  v = 200; break;   // A — Power, 1 Shiv/turn
            case "SENTRY_MODE":      v = 130; break;   // B — Power, scanner card
            case "SPECTRUM_SHIFT":   v = 100; break;   // C — Power, random colorless
            case "COMPACT":          v = 150; break;   // B — converts status to Fuel+
            // Shiv side-effect generators (var-derived CARD_GEN tag). Side-
            // effect Shiv count, NOT primary card effect — magnitudes lower
            // than dedicated Shiv producers (BLADE_DANCE 450, BLADE_OF_INK 600).
            case "LEADING_STRIKE":   v = 220; break;   // B — Atk 3 + 2 Shivs
            case "HIDDEN_DAGGERS":   v = 280; break;   // A — 0c, discard 2 + 2 Shiv+
            case "CLOAK_AND_DAGGER": v = 180; break;   // A — Block 6 + 1 Shiv
            case "STORM_OF_STEEL":   v = 200; break;   // D — discard hand → N Shiv+
            case "FAN_OF_KNIVES":    v = 250; break;   // C — Power, +4 Shivs + AOE conv
            // v0.6.9 — axis-fallback cards (no CARD_GEN axis in catalog)
            case "WHITE_NOISE":      v = 350; break;   // S — random Power 0-cost
            case "DISCOVERY":        v = 280; break;   // A — pick 1 of 3
            case "DISTRACTION":      v = 240; break;   // A — random Skill 0-cost
            case "WISH":             v = 200; break;   // A — 1 from draw to hand
            case "LARGESSE":         v = 150; break;   // A — other-player colorless
            case "SPLASH":           v = 200; break;   // A — pick 1 of 3 attacks
            default:                       v = 80;  break;   // generic fallback
        }
        b += v;
        parts.Add($"cardGen=+{v}");
    }

    private static void ApplyPreciseCutScaling(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // PRECISE_CUT: 13 - 2 × (other playable cards). Subtract from base
        // damage already credited by Attack-branch scoring.
        int others = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;
            others++;
        }
        // Each other-card costs 2 damage. Score impact = 2 × others × ~50
        // (DamagePerPointBonus). Negative adjustment.
        int penalty = -others * 100;
        if (penalty != 0)
        {
            b += penalty;
            parts.Add($"preciseCutShrink({others})={penalty}");
        }
    }

    private static void ApplyRandomExhaustPenalty(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Random card removed from hand. Use per-card keystone-aware loss
        // (EstimateExhaustLossRisk) averaged across non-self hand cards to
        // estimate the expected loss when one random card is taken. Curses
        // and status give negative risk (good to exhaust) so a curse-heavy
        // hand actually scores positive for these effects.
        int totalRisk = 0;
        int handSize = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            totalRisk += EstimateExhaustLossRisk(c);
            handSize++;
        }

        int expectedLoss = handSize > 0 ? totalRisk / handSize : 0;

        // Per-card-id offsets — some random-exhaust cards partially compensate
        // for the loss (THRASH inherits damage; TYRANNY is a sustained power).
        int v = -expectedLoss;
        switch (self.Id)
        {
            case "THRASH":
                v = (int)(v * 0.5);   // damage payoff offsets half the expected loss
                break;
            case "TYRANNY":
                // Per-turn exhaust over the fight — deck-thinning value dominates.
                // Treat curses-in-hand as their natural negative risk; for
                // non-curse-heavy hands, still net slightly positive.
                v = expectedLoss < 0 ? -expectedLoss : 40;
                break;
        }

        if (v != 0)
        {
            b += v;
            parts.Add($"randomExh(risk{expectedLoss})={v:+#;-#;0}");
        }
    }

    /// <summary>
    /// Per-card "loss risk" — how painful losing this card to a random or
    /// forced exhaust effect would be. Used by random-exhaust handlers
    /// (CINDER, THRASH, TRUE_GRIT) and whole-hand-exhaust handlers
    /// (FIEND_FIRE, EIDOLON, STOKE, SECOND_WIND).
    ///
    /// Weights:
    ///   • Curse / Status            → −80  (exhausting is a benefit)
    ///   • Power (not yet played)    → +400 (passive lost forever)
    ///   • Retain                    → +250 (setup wasted)
    ///   • SCALING axis              → +200 (mid-fight self-amp interrupted)
    ///   • Default                   → +60
    ///
    /// Sign convention: positive = painful loss; negative = beneficial loss.
    /// Caller subtracts the value (`b -= risk`) to apply as penalty.
    /// </summary>
    internal static int EstimateExhaustLossRisk(SimCard c)
    {
        if (c.IsCurseOrStatus) return -80;
        if (c.IsPower) return 400;
        if (c.IsRetain) return 250;
        if (c.Axes != null && c.Axes.Contains("SCALING")) return 200;
        return 60;
    }

    /// <summary>
    /// FIEND_FIRE exhausts the entire hand and deals per-exhausted damage.
    /// EstimateVariableHits already credits the damage; this subtracts the
    /// keystone loss from the cards exhausted. Curse-heavy hands net out
    /// positive (curses → −80 each → subtraction adds points).
    /// </summary>
    private static void ApplyWholeHandExhaustLoss(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int totalRisk = 0;
        int counted = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            totalRisk += EstimateExhaustLossRisk(c);
            counted++;
        }
        if (counted == 0) return;
        b -= totalRisk;
        parts.Add($"handExhLoss(n{counted})={(-totalRisk):+#;-#;0}");
    }

    /// <summary>
    /// SECOND_WIND exhausts non-attack cards from hand for 5 block each.
    /// EstimateBlockMultiplier already credits the block; this subtracts the
    /// keystone loss for non-attack cards (Powers + Skill-retain especially).
    /// </summary>
    private static void ApplyNonAttackExhaustLoss(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int totalRisk = 0;
        int counted = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.IsAttack) continue;        // SECOND_WIND filter
            totalRisk += EstimateExhaustLossRisk(c);
            counted++;
        }
        if (counted == 0) return;
        b -= totalRisk;
        parts.Add($"secondWindLoss(n{counted})={(-totalRisk):+#;-#;0}");
    }

    private static void ApplyOstyConditional(SimState state, ref int b, List<string> parts)
    {
        if (state.SkeletonCount > 0)
        {
            // Osty alive → damage actually happens. Modest positive nudge
            // (raw damage scoring already credits the attack); this just
            // signals the conditional clears.
            b += 150;
            parts.Add("ostyAlive=+150");
        }
        else
        {
            // No skeleton — attack's primary effect is gated off. Heavy
            // penalty so the planner deprioritises.
            b -= 350;
            parts.Add("ostyDead=-350");
        }
    }

    /// <summary>
    /// v0.7.21 — DOOM_SELF_PRODUCER risk handler. Adding Doom to the player
    /// queues turn-end self-damage that compounds across turns: 5 Doom ticks
    /// 5 HP every turn = 25 over 5 turns. The card's PowerCatalog credit
    /// already values Doom's payoff; this handler penalizes when projected
    /// total Doom damage threatens lethal.
    ///
    /// Heuristic:
    ///   newDoom = state.PlayerDoom + estimatedDoomFromCard
    ///   projectedHpLoss = newDoom × remaining-turns
    ///   penalty = clamp(-(projectedHpLoss × 5), -500, 0) when
    ///             projectedHpLoss > playerHp × 0.5
    /// </summary>
    private static void ApplyDoomSelfRisk(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Estimate the Doom added — fall back to 1 stack when card.vars is
        // unavailable. Most DOOM_SELF_PRODUCER cards add 1-2 stacks.
        int doomDelta = 1;
        if (self.PowerApps.TryGetValue("DoomPower", out var d) && d > 0) doomDelta = d;

        int newDoom = state.PlayerDoom + doomDelta;
        if (newDoom <= 0) return;

        int turns = RemainingTurnsEstimator.From(state);
        int projectedHpLoss = newDoom * turns;

        // Only penalize when this clearly threatens HP — sub-50% of HP is
        // background noise (the existing power-catalog credit covers small
        // doom).
        if (projectedHpLoss < state.PlayerHp / 2) return;

        int penalty = -projectedHpLoss * 5;
        if (penalty < -500) penalty = -500;
        b += penalty;
        parts.Add($"doomSelfRisk(stack={newDoom},turns={turns},lossEst={projectedHpLoss})={penalty}");
    }

    private static void ApplyEnlightenmentBonus(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // "All cards cost 1 this combat" — value = sum of (cost - 1) over
        // higher-cost cards currently visible across hand + draw + discard.
        int saved = 0;
        int CountSavings(System.Collections.Generic.IReadOnlyList<SimCard> pile)
        {
            int s = 0;
            foreach (var c in pile)
            {
                if (ReferenceEquals(c, self)) continue;
                if (c.IsCurseOrStatus) continue;
                if (c.Cost > 1) s += (c.Cost - 1);
            }
            return s;
        }
        saved += CountSavings(state.Hand);
        saved += CountSavings(state.DrawPile);
        saved += CountSavings(state.DiscardPile);
        int v = System.Math.Min(saved * 80, 1600);    // cap to avoid blow-up
        b += v;
        parts.Add($"enlight(save{saved})=+{v}");
    }

    private static int ReadStack(SimEnemy e, string stem)
    {
        switch (stem)
        {
            case "POISON":    return e.PoisonAmount;
            case "CONSTRICT": return e.ConstrictAmount;
            case "BURN":      return e.BurnAmount;
            case "DOOM":      return e.Powers.TryGetValue("DoomPower", out var v) ? v : 0;
            default:          return 0;
        }
    }

    private static void ApplyDamageAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable && c.IsAttack);
        if (remainingAttacks > 0)
        {
            int v = remainingAttacks * 70;
            b += v;
            parts.Add($"dmgAmp(atk*{remainingAttacks})=+{v}");
        }
        else
        {
            b -= 200;
            parts.Add("dmgAmpNoAtk=-200");
        }
    }

    private static void ApplyBlockAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int curBlock = state.PlayerBlock;
        int remainingBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && c.IsSkill && c.Block > 0);
        // Existing block doubles immediately; remaining block skills will compound later.
        int v = curBlock * 4 + remainingBlocks * 50;
        if (v > 0)
        {
            b += v;
            parts.Add($"blkAmp(blk{curBlock}+rem{remainingBlocks})=+{v}");
        }
        else
        {
            b -= 250;
            parts.Add("blkAmpNothing=-250");
        }
    }

    private static void ApplyVulnAmplifier(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
    {
        bool targetVuln = targetIdx >= 0 && targetIdx < state.Enemies.Count
                          && state.Enemies[targetIdx].IsAlive
                          && state.Enemies[targetIdx].VulnerableAmount > 0;
        bool anyVuln = state.Enemies.Any(e => e.IsAlive && e.VulnerableAmount > 0);
        bool vulnInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("VULN") || c.PowerApps.ContainsKey("VulnerablePower")));

        if (targetVuln)         { b += 450; parts.Add("vulnAmpTgt=+450"); }
        else if (anyVuln)       { b += 300; parts.Add("vulnAmpAny=+300"); }
        else if (vulnInHand)    { b += 250; parts.Add("vulnAmpInHand=+250"); }
        else                    { b -= 300; parts.Add("vulnAmpNoSource=-300"); }
    }

    private static void ApplyWeakAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        bool anyWeak = state.Enemies.Any(e => e.IsAlive && e.WeakAmount > 0);
        bool weakInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("WEAK") || c.PowerApps.ContainsKey("WeakPower")));

        // Multi-hit enemies (Repeats ≥ 2) make Weak's per-hit rounding compound.
        // Each such enemy adds extra value to the amplifier.
        int multiHitEnemies = state.Enemies.Count(e =>
            e.IsAlive && e.HasAttackIntent && !e.IsInert && e.IntentRepeats >= 2);
        int multiHitBonus = multiHitEnemies * 120;

        if (anyWeak)         { b += 250 + multiHitBonus; parts.Add($"weakAmpEnemy=+{250 + multiHitBonus}"); }
        else if (weakInHand) { b += 150 + multiHitBonus; parts.Add($"weakAmpInHand=+{150 + multiHitBonus}"); }
        else                 { b -= 150; parts.Add("weakAmpNoSource=-150"); }
    }

    private static void ApplyBlockPayoff(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int curBlock = state.PlayerBlock;
        if (curBlock > 0)
        {
            int v = curBlock * 30;
            b += v;
            parts.Add($"blkPayoff({curBlock})=+{v}");
            return;
        }
        // Block 0 — Body Slam currently does 0 dmg. If block skills still in hand,
        // mild penalty (play them first). If none, this card is dead — heavy.
        int remainingBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && c.IsSkill && c.Block > 0);
        if (remainingBlocks > 0)
        {
            b -= 600;
            parts.Add("blkPayoffEarly=-600");
        }
        else
        {
            b -= 1500;
            parts.Add("blkPayoffNoBlk=-1500");
        }
    }

    /// <summary>
    /// v0.7.37 — Self-harm trigger preview. When the played card causes the
    /// player to take HP loss AND the player has an HP_LOSS_TRIGGER passive,
    /// the passive fires once per HP loss event. Credits the card with the
    /// triggered effect (currently AOE damage from Inferno/Combust, and any
    /// other HP_LOSS-trigger from PlayerPowers).
    ///
    /// Triggers covered:
    ///   InfernoPower    — AOE 6 dmg on HP loss event (1 trigger per card)
    ///   CombustPower    — turn-end AOE per stack (already credited by
    ///                     PowerCatalog flat; not a per-card trigger).
    ///   RupturePower    — +1 Strength on HP loss event (permanent buff)
    ///   ShroudPower     — +2 block per Doom apply (different mechanic; skip)
    ///   FeelNoPainPower — +N block on exhaust (different mechanic; skip)
    ///
    /// Pure current-state read of PlayerPowers — no future-sim.
    /// </summary>
    private static void ApplySelfHarmTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;

        // InfernoPower: AOE 6 dmg on HP loss. Multiplier from stack amount.
        if (state.PlayerPowers.TryGetValue("InfernoPower", out var inferno) && inferno > 0)
        {
            int aliveCount = 0;
            foreach (var e in state.Enemies)
                if (e.IsAlive) aliveCount++;
            if (aliveCount > 0)
            {
                // Per-stack 6 AOE; clamp 1 trigger this card.
                int dmgPerEnemy = 6 * inferno;
                int v = aliveCount * dmgPerEnemy * 50 / 10;  // damage × DamagePerPoint / calibration
                b += v;
                parts.Add($"infernoTrigger(stack={inferno},alive={aliveCount})=+{v}");
            }
        }

        // RupturePower: +1 Strength permanent on HP loss event.
        if (state.PlayerPowers.TryGetValue("RupturePower", out var rupture) && rupture > 0)
        {
            // Each +1 Str applies to all future attacks for the rest of the
            // fight. Use RemainingTurns × ~3 attacks/turn × 50 dmg per Str.
            int turns = RemainingTurnsEstimator.From(state);
            int v = turns * 3 * rupture * 50 / 10;
            b += v;
            parts.Add($"ruptureTrigger(stack={rupture},turns={turns})=+{v}");
        }
    }

    /// <summary>
    /// Card-create trigger preview. When the played card generates additional
    /// cards (status-fills-hand, CARD_GEN recipes) AND the player has
    /// ArsenalPower or PillarOfCreationPower active, fire each trigger N times
    /// where N is the per-card-id card-creation count. Mirrors
    /// <see cref="ApplySelfHarmTriggerPreview"/>.
    ///
    /// Triggers covered:
    ///   ArsenalPower            — +1 Strength / card created (permanent buff)
    ///   PillarOfCreationPower   — +3 Block   / card created (same-turn block)
    /// </summary>
    private static void ApplyCardCreateTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;

        bool hasArsenal = state.PlayerPowers.TryGetValue("ArsenalPower", out var arsenal) && arsenal > 0;
        bool hasPillar  = state.PlayerPowers.TryGetValue("PillarOfCreationPower", out var pillar) && pillar > 0;
        if (!hasArsenal && !hasPillar) return;

        int cardsCreated = EstimateCardsCreated(card, state);
        if (cardsCreated <= 0) return;

        if (hasArsenal)
        {
            // Each +1 Str applies to ~3 future attacks × RemainingTurns × 50/10
            // (DamageFree calibration). Stack amount = Str per trigger.
            int turns = RemainingTurnsEstimator.From(state);
            int v = cardsCreated * arsenal * turns * 3 * 50 / 10;
            const int Cap = 800;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"arsenalTrigger(create{cardsCreated}×stack{arsenal},turns{turns})=+{v}");
        }
        if (hasPillar)
        {
            // +3 block per card created × BlockFree(30)/10 calibration.
            int v = cardsCreated * pillar * 3 * 30 / 10;
            const int Cap = 600;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"pillarTrigger(create{cardsCreated}×stack{pillar})=+{v}");
        }

        // Status-specific generation triggers — fire only when the created
        // cards are Status (STATUS_TO_HAND axis). Defect-side passives that
        // turn the "hand-pollution" penalty into AoE damage or orb tempo.
        bool isStatusCreation = card.Axes.Contains("STATUS_TO_HAND");
        if (isStatusCreation)
        {
            bool hasSmokestack = state.PlayerPowers.TryGetValue("SmokestackPower", out var smoke) && smoke > 0;
            bool hasTrash      = state.PlayerPowers.TryGetValue("TrashToTreasurePower", out var trash) && trash > 0;

            if (hasSmokestack)
            {
                int aliveCount = 0;
                foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
                if (aliveCount > 0)
                {
                    int v = cardsCreated * smoke * 5 * aliveCount * 50 / 10;
                    const int Cap = 700;
                    if (v > Cap) v = Cap;
                    b += v;
                    parts.Add($"smokestackTrigger(status{cardsCreated}×stack{smoke},alive{aliveCount})=+{v}");
                }
            }
            if (hasTrash)
            {
                // Per random orb generated ≈ 200 (light avg across types).
                int v = cardsCreated * trash * 200;
                const int Cap = 600;
                if (v > Cap) v = Cap;
                b += v;
                parts.Add($"trashTreasureTrigger(status{cardsCreated}×stack{trash})=+{v}");
            }
        }
    }

    /// <summary>
    /// Coarse estimate of how many cards a given play adds to hand/deck.
    /// STATUS_TO_HAND fillsHand → fill to STS2 max hand (10) after the played
    /// card leaves; single-status droppers → 1; CARD_GEN → per-recipe count.
    /// In-place transformers (GUARDS / COMPACT / PRIMAL_FORCE) return 0 —
    /// they don't create net new cards.
    /// </summary>
    private static int EstimateCardsCreated(SimCard card, SimState state)
    {
        bool fillsHand = card.Axes.Contains("STATUS_TO_HAND")
                      && (card.Axes.Contains("AOE_OTHER") || card.Axes.Contains("AOE_DAMAGE"));
        if (fillsHand)
        {
            const int MaxHand = 10;
            int free = MaxHand - (state.Hand.Count - 1);
            return free > 0 ? free : 0;
        }
        if (card.Axes.Contains("STATUS_TO_HAND")) return 1;

        return card.Id switch
        {
            "BLADE_OF_INK"  => 2,
            "BLADE_DANCE"   => 3,
            "UP_MY_SLEEVE"  => 3,
            "JACKPOT"       => 3,
            "NIGHTMARE"     => 3,
            "CHARGE"        => 2,
            "JUGGLING"      => 1,
            "PRIMAL_FORCE"  => 0,   // transforms in place
            "GUARDS"        => 0,
            "COMPACT"       => 0,
            // Shiv side-effect generators (auto-tagged via vars/desc)
            "LEADING_STRIKE"   => 2,
            "HIDDEN_DAGGERS"   => 2,
            "CLOAK_AND_DAGGER" => 1,
            "FAN_OF_KNIVES"    => 4,
            // STORM_OF_STEEL count depends on hand size at play time
            "STORM_OF_STEEL"   => System.Math.Max(0, state.Hand.Count - 1),
            _ => card.Axes.Contains("CARD_GEN") && !card.IsPower ? 1 : 0,
        };
    }

    /// <summary>
    /// Exhaust-event trigger preview. When the played card exhausts on play
    /// (IsExhaust or EXHAUST_SELF axis) AND DarkEmbracePower is active, the
    /// "+1 card draw per exhaust" trigger fires once. Inverts the usual
    /// "card lost forever" cost of self-exhaust into a free draw.
    ///
    /// FeelNoPainPower (+block on exhaust) is intentionally NOT credited here
    /// because PlanScorer already applies it via attack/skill reactiveBlock
    /// branches (PlanScorer.cs lines 1069 / 1138).
    /// </summary>
    private static void ApplyExhaustEventTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;

        if (state.PlayerPowers.TryGetValue("DarkEmbracePower", out var de) && de > 0)
        {
            // Mirror ApplyDarkEmbraceTickValue's PerExhaustDraw=200 calibration.
            int v = de * 200;
            const int Cap = 400;     // single-card cap — multi-stack DarkEmbrace is rare
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"darkEmbraceTrigger(stack{de})=+{v}");
        }
    }

    /// <summary>
    /// Volatile-play (ethereal) trigger preview. When an ethereal card is
    /// played AND SpiritOfAshPower is active, +4 block × stack fires. Rewards
    /// the natural play-or-lose behavior of ethereal cards. Mirrors the
    /// Necrobinder Volatile build's signature payoff.
    /// </summary>
    private static void ApplyVolatilePlayTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;

        if (state.PlayerPowers.TryGetValue("SpiritOfAshPower", out var ash) && ash > 0)
        {
            // +4 block per Volatile play × stack × BlockFree(30)/10.
            int v = ash * 4 * 30 / 10;
            const int Cap = 300;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"spiritOfAshTrigger(stack{ash})=+{v}");
        }
    }

    /// <summary>
    /// Card-draw trigger preview. HungerPower grants +N Strength per card
    /// drawn this turn. The simulator advance already credits this in
    /// depth-N lookahead; this method makes the immediate-score (depth-1)
    /// ranking aware as well so draw cards correctly rise when Hunger is
    /// active.
    /// </summary>
    private static void ApplyDrawEventTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerHunger <= 0) return;
        int draws = card.DrawCount;
        if (draws <= 0) return;

        // +1 Str per draw event. Per-Str value mirrors arsenalTrigger:
        // turns × 3 attacks × DamageFree(50)/10.
        int turns = RemainingTurnsEstimator.From(state);
        int v = draws * state.PlayerHunger * turns * 3 * 50 / 10;
        const int Cap = 600;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"hungerTrigger(draw{draws}×stack{state.PlayerHunger},turns{turns})=+{v}");
    }

    /// <summary>
    /// Skill-played trigger preview. EnragePower grants +N Strength on every
    /// Skill played. Sim already applies this in AdvanceTurn; this is the
    /// depth-1 preview so Skill-heavy hands correctly score higher when
    /// Enrage is active.
    /// </summary>
    private static void ApplySkillPlayedTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerEnrage <= 0) return;

        int turns = RemainingTurnsEstimator.From(state);
        int v = state.PlayerEnrage * turns * 3 * 50 / 10;
        const int Cap = 500;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"enrageTrigger(stack{state.PlayerEnrage},turns{turns})=+{v}");
    }

    /// <summary>
    /// Vuln-applied trigger preview. ViciousPower draws +1 card on every Vuln
    /// apply event. Single-target VULN_PRODUCER → 1 event; AOE Vuln (e.g.
    /// PIERCING_WAIL with VULN_PRODUCER + AOE_OTHER) → 1 event per alive enemy.
    /// Mirrors PerVulnDraw=180 calibration from <see cref="ApplyViciousTickValue"/>.
    /// </summary>
    private static void ApplyVulnApplyTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;
        if (!state.PlayerPowers.TryGetValue("ViciousPower", out var vic) || vic <= 0) return;

        int applies = 1;
        if (card.Axes.Contains("AOE_OTHER") || card.Axes.Contains("AOE_DAMAGE"))
        {
            int alive = 0;
            foreach (var e in state.Enemies) if (e.IsAlive) alive++;
            if (alive > 1) applies = alive;
        }

        const int PerDraw = 180;
        int v = applies * vic * PerDraw;
        const int Cap = 540;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"viciousTrigger(apply{applies}×stack{vic})=+{v}");
    }

    /// <summary>
    /// ReaperFormPower turns every attack hit into a Doom apply: per-hit
    /// Doom amount = card.Damage × ReaperFormStack. With Doom ticking 1 HP
    /// per stack per turn over remaining turns, attacks gain hidden future-
    /// turn damage that the immediate attack score ignores. Also chains
    /// with ShroudPower (one Doom apply event per hit). Sim mirror at
    /// AnalyticalSimulator: `newDoom += card.Damage × hits × reaperStacks`.
    /// </summary>
    private static void ApplyReaperFormAttackPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;
        if (!state.PlayerPowers.TryGetValue("ReaperFormPower", out var reaperStacks)
            || reaperStacks <= 0) return;

        int hits = System.Math.Max(1, card.Hits);
        int turns = RemainingTurnsEstimator.From(state);
        // Total Doom applied across all hits per stack.
        int totalDoom = card.Damage * hits * reaperStacks;
        // Doom decays 1/turn (STS1 model). Average tick value over remaining
        // turns ≈ totalDoom × turns × 0.5 HP. × DamageFree(50)/10 to score
        // calibration. 0.5 covers both decay and uncertainty (enemy may die
        // before Doom completes).
        int doomScore = totalDoom * turns * 50 / 10 * 50 / 100;

        // ShroudPower fires once per Doom apply event — one event per attack
        // hit (regardless of Doom amount per hit).
        int shroudBonus = 0;
        if (state.PlayerPowers.TryGetValue("ShroudPower", out var shroudStack) && shroudStack > 0)
            shroudBonus = hits * shroudStack * 2 * 30 / 10;  // 2 block × hits × stack

        int total = doomScore + shroudBonus;
        const int Cap = 800;
        if (total > Cap) total = Cap;
        b += total;
        parts.Add($"reaperFormAttack(doom{totalDoom}×turns{turns}={doomScore},shroud+{shroudBonus},stack{reaperStacks})=+{total}");
    }

    /// <summary>
    /// Star-consume trigger preview. Star-cost cards (FALLING_STAR star_cost 2,
    /// COMET 5, SEVEN_STARS 7, etc.) pay N stars on play, firing one Star-event
    /// trigger per played card. Reactive Regent Powers chain off this:
    ///   • ChildOfTheStarsPower → +block equal to consumed stars × stack
    ///   • BlackHolePower      → AOE 3 damage × stack per Star event
    /// Stars consumed are not surfaced as damage/block in the played card's
    /// effect summary, so the base score misses these chained payoffs.
    /// </summary>
    private static void ApplyStarConsumePreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;
        int starCost = card.Effect.StarCost;
        if (starCost <= 0) return;

        // ChildOfTheStarsPower — block per consumed star × stack.
        if (state.PlayerPowers.TryGetValue("ChildOfTheStarsPower", out var cosStack)
            && cosStack > 0)
        {
            int blockGain = starCost * cosStack;
            int v = blockGain * 30 / 10;   // BlockFree calibration
            const int Cap = 500;
            if (v > Cap) v = Cap;
            b += v;
            parts.Add($"childOfStarsTrigger(stars{starCost}×stack{cosStack}={blockGain}blk)=+{v}");
        }

        // BlackHolePower — AOE 3 damage per Star event × stack. STS2 fires
        // on both gain AND consume; we credit only the consume event here
        // (consistent with star_cost gating). Star gains via STAR_PRODUCER
        // axis would need a parallel preview if also active.
        if (state.PlayerPowers.TryGetValue("BlackHolePower", out var bhStack) && bhStack > 0)
        {
            int alive = 0;
            foreach (var e in state.Enemies) if (e.IsAlive) alive++;
            if (alive > 0)
            {
                int dmg = 3 * bhStack * alive;
                int v = dmg * 50 / 10;     // DamageFree calibration
                const int Cap = 600;
                if (v > Cap) v = Cap;
                b += v;
                parts.Add($"blackHoleConsumeTrigger(alive{alive}×stack{bhStack}×3={dmg})=+{v}");
            }
        }
    }

    /// <summary>
    /// Star resource is bankable (no per-turn reset). Penalize plays that
    /// either (A) waste stars on a low-efficiency play when a higher-
    /// efficiency star card exists in deck, or (B) leave the player short
    /// of stars needed for the biggest star_cost card available later.
    ///
    /// Net-positive converters (StarsGain ≥ StarCost — ROYAL_GAMBLE 5→9)
    /// are exempt: their value is captured by ApplyStarsGain, and the
    /// "consume" is just bookkeeping for the bigger gain.
    /// </summary>
    private static void ApplyStarOpportunityCost(SimCard card, SimState state, ref int b, List<string> parts)
    {
        int starCost = card.Effect.StarCost;
        if (starCost <= 0) return;
        // Exempt net-positive converters (banking action, not payoff).
        if (card.Effect.StarsGain >= starCost) return;

        int currentEff = StarEfficiency(card);
        int bestEff = currentEff;
        int maxFutureStarCost = 0;
        bool seenOther = false;

        void Scan(System.Collections.Generic.IReadOnlyList<SimCard> pile)
        {
            for (int i = 0; i < pile.Count; i++)
            {
                var c = pile[i];
                if (ReferenceEquals(c, card)) continue;
                int sc = c.Effect.StarCost;
                if (sc <= 0) continue;
                if (c.Effect.StarsGain >= sc) continue;   // skip net-positive too
                seenOther = true;
                int e = StarEfficiency(c);
                if (e > bestEff) bestEff = e;
                if (sc > maxFutureStarCost) maxFutureStarCost = sc;
            }
        }
        Scan(state.Hand);
        Scan(state.DrawPile);
        Scan(state.DiscardPile);

        if (!seenOther) return;   // sole star-cost card — no alternative to compare

        int turns = RemainingTurnsEstimator.From(state);
        int starsAfter = state.PlayerStars + card.Effect.StarsGain - starCost;
        int totalPenalty = 0;

        // (A) Efficiency gap. Buffer of 5 absorbs noise between similar cards.
        const int EffBuffer = 5;
        if (bestEff - currentEff > EffBuffer)
        {
            int gap = bestEff - currentEff;
            int penalty = gap * starCost;
            const int EffCap = 250;
            if (penalty > EffCap) penalty = EffCap;
            totalPenalty += penalty;
        }

        // (B) Future shortfall. Only when remaining turns allow drawing the
        // bigger card AND playing this card actually drops stars below the
        // biggest cost threshold.
        if (turns >= 2 && maxFutureStarCost > starCost && starsAfter < maxFutureStarCost)
        {
            int shortage = maxFutureStarCost - starsAfter;
            int shortagePenalty = shortage * 30;
            const int ShortageCap = 200;
            if (shortagePenalty > ShortageCap) shortagePenalty = ShortageCap;
            totalPenalty += shortagePenalty;
        }

        if (totalPenalty > 0)
        {
            b -= totalPenalty;
            parts.Add($"starOpCost(eff{currentEff}<best{bestEff},sAfter{starsAfter}/futMax{maxFutureStarCost})=-{totalPenalty}");
        }
    }

    /// <summary>
    /// Per-star value heuristic for star_cost cards. Sums damage/block
    /// plus light credit for power applications, divides by star_cost.
    /// Used by ApplyStarOpportunityCost to rank cards by their star ROI.
    /// </summary>
    private static int StarEfficiency(SimCard c)
    {
        if (c.Effect.StarCost <= 0) return 0;
        int totalValue = c.TotalDamage + c.Effect.Block;
        foreach (var (_, amt) in c.PowerApps) totalValue += amt * 3;
        return totalValue / c.Effect.StarCost;
    }

    /// <summary>
    /// Star environment penalty. When every alive target has a heavy per-hit
    /// damage cap (Intangible / HardToKill, cap ≤ 5), a star_cost burst
    /// attack is mostly wasted — the face damage doesn't land but the
    /// stockpiled stars are spent. Stars carry over to the next turn while
    /// Intangible (typical) expires, so save them.
    ///
    /// Gates:
    ///   • Skip if turns_remaining &lt; 2 (last turn — just deal what we can).
    ///   • Skip net-positive star converters (already exempted upstream;
    ///     defensive double-check).
    ///   • Single-target uses the first alive enemy as proxy for
    ///     selectable target (matches planner's default target pick).
    /// </summary>
    private static void ApplyStarEnvironmentPenalty(SimCard card, SimState state, ref int b, List<string> parts)
    {
        int starCost = card.Effect.StarCost;
        if (starCost <= 0 || !card.IsAttack || card.TotalDamage <= 0) return;
        if (card.Effect.StarsGain >= starCost) return;

        int turns = RemainingTurnsEstimator.From(state);
        if (turns < 2) return;

        bool isAoe = card.Target == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllEnemies
            || (card.Id == "SHIV" && state.PlayerPowers != null
                && state.PlayerPowers.TryGetValue("FanOfKnivesPower", out var fnk) && fnk > 0);

        const int HeavyCapThreshold = 5;   // Intangible (1) / HardToKill small caps
        int aliveCount = 0;
        int cappedCount = 0;

        if (isAoe)
        {
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                var e = state.Enemies[i];
                if (!e.IsAlive) continue;
                aliveCount++;
                if (e.DamageCapPerHit > 0 && e.DamageCapPerHit <= HeavyCapThreshold)
                    cappedCount++;
            }
        }
        else
        {
            SimEnemy? primary = null;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                if (state.Enemies[i].IsAlive) { primary = state.Enemies[i]; break; }
            }
            if (primary == null) return;
            aliveCount = 1;
            if (primary.DamageCapPerHit > 0 && primary.DamageCapPerHit <= HeavyCapThreshold)
                cappedCount = 1;
        }

        // Only penalize when ALL alive targets are heavily capped (saving
        // stars makes sense). If any target is uncapped, the attack still
        // delivers — use it.
        if (aliveCount == 0 || cappedCount < aliveCount) return;

        // 50 per star_cost — mild nudge toward non-attack alternatives.
        // Cap so a 7-cost SEVEN_STARS doesn't blow up the penalty.
        int penalty = starCost * 50;
        const int PenaltyCap = 250;
        if (penalty > PenaltyCap) penalty = PenaltyCap;
        b -= penalty;
        parts.Add($"starWasteEnv(allCapped{cappedCount}/{aliveCount},star{starCost})=-{penalty}");
    }

    /// <summary>
    /// Star-gain trigger preview. STAR_PRODUCER cards (GLOW +1, VENERATE +2,
    /// ROYAL_GAMBLE +9, etc.) fire BlackHolePower's AOE 3 damage per Star
    /// gained. ChildOfTheStarsPower fires only on consume, so it's not
    /// credited here.
    /// </summary>
    private static void ApplyStarGainPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;
        int starsGain = card.Effect.StarsGain;
        if (starsGain <= 0) return;

        if (state.PlayerPowers.TryGetValue("BlackHolePower", out var bhStack) && bhStack > 0)
        {
            int alive = 0;
            foreach (var e in state.Enemies) if (e.IsAlive) alive++;
            if (alive > 0)
            {
                // N Star events per card play (1 per Star gained).
                int dmg = 3 * bhStack * alive * starsGain;
                int v = dmg * 50 / 10;
                const int Cap = 600;
                if (v > Cap) v = Cap;
                b += v;
                parts.Add($"blackHoleGainTrigger(gain{starsGain}×alive{alive}×stack{bhStack}×3={dmg})=+{v}");
            }
        }
    }

    /// <summary>
    /// Doom-applied trigger preview. ShroudPower grants +2 block per Doom
    /// apply event. Standard DOOM_PRODUCER → 1 event; AOE_DOOM cards apply
    /// to all alive enemies → N events.
    /// </summary>
    private static void ApplyDoomApplyTriggerPreview(SimCard card, SimState state, ref int b, List<string> parts)
    {
        if (state.PlayerPowers == null || state.PlayerPowers.Count == 0) return;
        if (!state.PlayerPowers.TryGetValue("ShroudPower", out var shroud) || shroud <= 0) return;

        int applies = 1;
        if (card.Axes.Contains("AOE_DOOM"))
        {
            int alive = 0;
            foreach (var e in state.Enemies) if (e.IsAlive) alive++;
            if (alive > 1) applies = alive;
        }

        // +2 block per apply × stack × BlockFree(30)/10.
        int v = applies * shroud * 2 * 30 / 10;
        const int Cap = 300;
        if (v > Cap) v = Cap;
        b += v;
        parts.Add($"shroudTrigger(apply{applies}×stack{shroud})=+{v}");
    }

    private static void ApplyHpLossConsumer(SimState state, ref int b, List<string> parts)
    {
        // v0.7.7 — Three additive signals:
        //   1) HP threshold (absolute) — historical low-HP heuristic.
        //   2) CombatPlayerHpLossEvents — counts unblocked-damage and self-
        //      damage events already taken this combat. RUPTURE/INFERNO scale
        //      on these directly (TEAR_ASUNDER hit multiplier is handled in
        //      PlanScorer.EstimateCalculatedHits separately).
        //   3) Future HP-loss producers in piles — BLOODLETTING / OFFERING /
        //      HEMOKINESIS etc. with HP_LOSS axis will add events when played.

        // (1) Threshold
        if (state.PlayerHp <= 30)
        {
            b += 350;
            parts.Add($"hpLossLow(hp{state.PlayerHp})=+350");
        }
        else if (state.PlayerHp <= 50)
        {
            b += 200;
            parts.Add($"hpLossMid(hp{state.PlayerHp})=+200");
        }

        // (2) Historical events — each event = +60 (proxy for RUPTURE per-stack
        // Strength gain × ~3 future attacks, conservatively averaged).
        const int PerEventBonus = 60;
        if (state.CombatPlayerHpLossEvents > 0)
        {
            int v = state.CombatPlayerHpLossEvents * PerEventBonus;
            b += v;
            parts.Add($"hpLossEvents({state.CombatPlayerHpLossEvents})=+{v}");
        }

        // (3) Future producers in piles. HP_LOSS axis marks cards that cost
        // HP when played (BLOODLETTING, OFFERING, HEMOKINESIS, INFERNO,
        // BREAKTHROUGH, BRAND, DEMONIC_SHIELD, BLOOD_WALL, CRIMSON_MANTLE).
        // Each producer in piles = high chance of triggering during remaining
        // combat. Lower per-card bonus than historical events since they're
        // potential, not realized.
        const int PerFutureProducerBonus = 35;
        int producers = CountHpLossProducers(state.Hand)
                      + CountHpLossProducers(state.DrawPile)
                      + CountHpLossProducers(state.DiscardPile);
        if (producers > 0)
        {
            int v = producers * PerFutureProducerBonus;
            // Cap so a Bloodletting-heavy deck doesn't double-score per
            // consumer scored.
            const int FutureCap = 300;
            if (v > FutureCap) v = FutureCap;
            b += v;
            parts.Add($"hpLossProducers({producers})=+{v}");
        }
    }

    private static int CountHpLossProducers(IReadOnlyList<SimCard> pile)
    {
        int n = 0;
        if (pile == null) return 0;
        foreach (var c in pile)
        {
            // HP_LOSS axis = card costs HP on play. Excluding the curse/status
            // siblings (BAD_LUCK / BECKON) — they have HP_LOSS but they're
            // passive damage, not voluntary plays.
            if (c.IsCurseOrStatus) continue;
            if (c.Axes != null && c.Axes.Contains("HP_LOSS")) n++;
        }
        return n;
    }
}
