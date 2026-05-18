using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Per-component breakdown of why a (card, target) play got its score. The string
/// <see cref="Details"/> field surfaces additive bonuses by name for log inspection.
/// </summary>
internal readonly record struct ScoreBreakdown(
    int Total,
    string Category,
    int Base,
    int Effect,
    int TargetBonus,
    int ThreatBonus,
    string Details)
{
    public string ToLogLine() =>
        $"{Category} base={Base} effect={Effect} target={TargetBonus} threat={ThreatBonus}"
        + (string.IsNullOrEmpty(Details) ? "" : $" [{Details}]");
}

/// <summary>
/// Scores a candidate (card, target) play. Higher = better.
///
/// v0.2.2 — breakdown surface. <see cref="Breakdown"/> returns per-component details
/// for log inspection; <see cref="Score"/> is now a thin wrapper around it.
/// </summary>
internal static class PlanScorer
{
    public static int Score(SimCard card, int targetIdx, SimState state)
        => Breakdown(card, targetIdx, state, PlanScorerWeights.For(PlaystyleState.Current)).Total;

    public static int Score(SimCard card, int targetIdx, SimState state, PlanScorerWeights w)
        => Breakdown(card, targetIdx, state, w).Total;

    public static ScoreBreakdown Breakdown(SimCard card, int targetIdx, SimState state)
        => Breakdown(card, targetIdx, state, PlanScorerWeights.For(PlaystyleState.Current));

    public static ScoreBreakdown Breakdown(SimCard card, int targetIdx, SimState state, PlanScorerWeights w)
        => AdjustBreakdownForEnchant(BreakdownInternal(card, targetIdx, state, w), card);

    /// <summary>
    /// Play-order biases for Retain / Ethereal. Kept OUT of <see cref="Score"/> /
    /// <see cref="Breakdown"/> so that selector-context callers (discard/exhaust
    /// prompts, reward selection) see the unbiased card value — otherwise the
    /// retain defer-penalty would make retain cards look "worst" and the smart
    /// selector would discard them preferentially, the opposite of what we want.
    ///
    /// Used only by <see cref="ActionPlanner"/> for first-card / depth-2 scoring.
    ///   • Retain — small per-other-playable penalty so a retainable card waits
    ///     until no non-retain alternative remains.
    ///   • Ethereal — flat bonus so a card that would otherwise exhaust unplayed
    ///     wins close-call comparisons against equal-scored non-ethereal cards.
    /// </summary>
    public static int PlayOrderBias(SimCard card, SimState state, PlanScorerWeights w)
    {
        int delta = 0;
        if (card.IsRetain)
        {
            int otherPlayable = 0;
            foreach (var c in state.Hand)
            {
                if (ReferenceEquals(c, card)) continue;
                if (!c.IsPlayable || c.IsCurseOrStatus) continue;
                if (c.IsRetain) continue;          // other retains share the same defer urge
                if (c.Cost < 0 || c.Cost > state.PlayerEnergy) continue;
                otherPlayable++;
            }
            if (otherPlayable > 0)
                delta -= w.RetainDeferPenaltyPerAlternative * otherPlayable;
        }
        if (card.IsEthereal)
        {
            // v0.6 — type-aware Ethereal play-now bonus. Powers have higher
            // intrinsic value (and longer-tail benefit once played), so they
            // get the higher bonus. Curses/Status cards are filtered out
            // earlier; the bonus on them is irrelevant.
            delta += card.IsPower ? w.EtherealPowerPlayNowBonus : w.EtherealPlayNowBonus;
        }
        return delta;
    }

    /// <summary>
    /// Wrap Breakdown's Total + Details with the enchantment adjustment so logs show
    /// e.g. "ench:×2[Glam]" and the planner actually compares enchanted vs plain cards
    /// using the adjusted score.
    /// </summary>
    private static ScoreBreakdown AdjustBreakdownForEnchant(ScoreBreakdown bd, SimCard card)
    {
        if (card.SourceRef == null) return bd;
        var enchId = Reflection.CardReflection.GetEnchantmentId(card.SourceRef);
        if (string.IsNullOrEmpty(enchId)) return bd;
        int multi = Reflection.EnchantmentBonusCatalog.PlayCountMultiplier(enchId, 1);
        int behavior = Reflection.EnchantmentBonusCatalog.BehaviorBonus(enchId);
        if (multi == 1 && behavior == 0) return bd; // numeric-only enchant — already folded by CardReflection

        int adjusted = bd.Total * multi + behavior;
        var dot = enchId.LastIndexOf('.');
        var shortId = dot >= 0 ? enchId.Substring(dot + 1) : enchId;
        var tag = multi > 1
            ? (behavior != 0 ? $"ench:×{multi}+{behavior}[{shortId}]" : $"ench:×{multi}[{shortId}]")
            : $"ench:+{behavior}[{shortId}]";
        var details = string.IsNullOrEmpty(bd.Details) ? tag : $"{bd.Details},{tag}";
        return bd with { Total = adjusted, Details = details };
    }

    private static ScoreBreakdown BreakdownInternal(SimCard card, int targetIdx, SimState state, PlanScorerWeights w)
    {
        if (card.IsCurseOrStatus)
        {
            // Most curse/status cards are Unplayable (Wound / Dazed / Void / Injury) — already
            // filtered out of candidate enumeration by IsPlayable. The few that ARE playable
            // (Burn for one — sits in hand and deals self-damage at turn end if not played)
            // are worth using when there's spare energy: spending 1 energy on Burn avoids the
            // 2 HP tick. Score is small but above MinPlayScore so it gets picked only when no
            // attack / defend / power play is available, never above a real card.
            if (card.IsPlayable)
                return new ScoreBreakdown(200, "Status-Playable", 200, 0, 0, 0, "status-spend-to-discard");
            return new ScoreBreakdown(w.CursePenalty, "Curse", w.CursePenalty, 0, 0, 0, "never-play");
        }

        int cost = card.Cost;
        bool allInert = EnemyTurnSimulator.AllInert(state);
        double threat = EnemyTurnSimulator.ThreatRatio(state);
        double threshold = EnemyTurnSimulator.NextTurnThreatAmplified(state)
            ? w.ThreatThresholdWithBuff : w.ThreatThreshold;

        // v0.6 — turn-finishing lethal detection. When the hand's attacks
        // (with current Strength + Vuln + Weak applied, energy-budget greedy
        // pick) can kill every alive enemy this turn, non-Attack cards are
        // heavily penalised so attacks win the score comparison.
        bool lethalThisTurn = IsLethalThisTurn(state);

        // v0.6.2 — Status / Curse pile pollution penalty for fetch cards.
        // Anointed / Echo of Fallen / Apotheosis etc. pull a card from the
        // draw or discard pile; if that pile is loaded with Wound / Slime /
        // Curse, expected value of the fetch drops proportionally. 0 if not
        // a fetch card, or if the piles are clean.
        int fetchPollutionPenalty = EvaluateFetchPollution(card, state, w);

        // v0.6.2 — Combo chain recognition. Small per-link bonus when the
        // hand contains a 3+ link synergy chain that includes this card.
        // Bonus is intentionally small (≤250) — individual links are already
        // scored by BuildSynergy / HandSynergy / EffectSynergy; this is
        // tie-breaking + DecisionLog visibility for "combo turn" detection.
        var (comboBonus, comboDetail) = ComboRecognition.Compute(card, state);
        // v0.7.38 — Explicit known-pair recipe bonus on top of the generic chain.
        int comboPairBonus = ComboRecognition.ExplicitPairBonus(card, state);
        if (comboPairBonus != 0)
        {
            comboBonus += comboPairBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"pairBonus+{comboPairBonus}"
                : $"{comboDetail},pairBonus+{comboPairBonus}";
        }
        // v0.7.52 — Deck-wide archetype alignment. Detect the dominant build
        // commitment (≥3 supporters in deck) and bias the score toward
        // cards belonging to that build. Folded into comboBonus so the
        // existing log column captures it.
        var (archPrimary, archSecondary, archCount) = ArchetypeDetector.Detect(state);
        int archAlignment = ArchetypeDetector.AlignmentBonus(card, archPrimary, archSecondary, archCount);
        if (archAlignment != 0)
        {
            comboBonus += archAlignment;
            string archTag = $"arch({archPrimary}/{archCount})+{archAlignment}";
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? archTag
                : $"{comboDetail},{archTag}";
        }
        // v0.7.53 — Combat-specific situational bonus. Profile the current
        // encounter (multi-hit / big-burst / AOE / long / short / artifact /
        // thorns / buffing) and award per-card bonuses for cards that match
        // the encounter shape. Modest magnitudes (50-150).
        var combatProfile = CombatContext.Profile(state);
        int ctxBonus = CombatContext.ContextBonus(card, combatProfile);
        if (ctxBonus != 0)
        {
            comboBonus += ctxBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"ctx{ctxBonus:+0;-0}"
                : $"{comboDetail},ctx{ctxBonus:+0;-0}";
        }
        // v0.7.54 — Win-condition phase inference. Classify the combat
        // into Standard / LethalThisTurn / LethalSoon / Sustain / Survival
        // and add a strategic per-card bias. Modest magnitudes (50-180);
        // existing lethal/survival penalties dominate when those trigger.
        var phase = WinConditionInference.Classify(state);
        int phaseBonus = WinConditionInference.PhaseBonus(card, phase);
        if (phaseBonus != 0)
        {
            comboBonus += phaseBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"phase({phase}){phaseBonus:+0;-0}"
                : $"{comboDetail},phase({phase}){phaseBonus:+0;-0}";
        }
        // v0.7.55 — Deck throughput core-card bonus. Highly-efficient cards
        // (top-3 attackers / defenders by per-energy ratio) get an extra
        // nudge so the AI prefers them over mediocre filler.
        var throughput = DeckThroughput.Compute(state);
        int coreBonus = DeckThroughput.CoreCardBonusFor(card, throughput);
        if (coreBonus != 0)
        {
            comboBonus += coreBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"core+{coreBonus}"
                : $"{comboDetail},core+{coreBonus}";
        }
        // v0.7.57 — Survival race projection. Compare turns-to-death vs
        // turns-to-kill and bias toward damage (losing race), balance
        // (tight), or scaling (winning).
        var raceProj = SurvivalProjection.Compute(state, throughput);
        int raceBonus = SurvivalProjection.RaceBonus(card, raceProj);
        if (raceBonus != 0)
        {
            comboBonus += raceBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"race({raceProj.Race}){raceBonus:+0;-0}"
                : $"{comboDetail},race({raceProj.Race}){raceBonus:+0;-0}";
        }
        // v0.7.58 — Deck quality nudge. Adjusts for polluted / bloated /
        // heavy-cost / light deck states. Modest magnitudes.
        var quality = DeckQuality.Compute(state);
        int qualityBonus = DeckQuality.QualityBonus(card, quality);
        if (qualityBonus != 0)
        {
            comboBonus += qualityBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"quality({quality.Health})+{qualityBonus}"
                : $"{comboDetail},quality({quality.Health})+{qualityBonus}";
        }
        // v0.7.59 — Multi-turn plan stage. Smooths flip-flopping across turns
        // by tracking the macro stage and biasing toward stage-aligned plays.
        var planStage = CombatPlan.Classify(state, raceProj);
        int stageBonus = CombatPlan.StageBonus(card, planStage);
        if (stageBonus != 0)
        {
            comboBonus += stageBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"stage({planStage}){stageBonus:+0;-0}"
                : $"{comboDetail},stage({planStage}){stageBonus:+0;-0}";
        }

        // v0.6.2 — Energy monopoly penalty. When the current card consumes
        // ALL remaining energy AND there are other meaningful playable
        // cards in hand that would have fit, a small penalty captures the
        // "this turn could have done 3 plays instead of 1" opportunity
        // cost. Conservative magnitude (≤100) so big damage cards still
        // win when they're genuinely the best play.
        int monopolyPenalty = EvaluateEnergyMonopoly(card, state, w);

        var details = new List<string>();

        // Build synergy applies to every non-curse card exactly once.
        int buildBonus = BuildSynergy.Compute(card, card, state);

        // Sparse manual override on top of the base algorithm.
        int overrideBonus = CardOverrideCatalog.Lookup(card.Id);

        if (card.IsPower)
        {
            int baseBonus = allInert ? w.PowerCardBonusWhenAllInert : w.PowerCardBonus;
            int costTie = cost * (w.CostMultiplier / 4);
            details.Add(allInert ? $"allInertBonus={baseBonus}" : $"powerBase={baseBonus}");
            int effect = 0;
            foreach (var (powerName, amount) in card.PowerApps)
            {
                int v = PowerCatalog.ValueSelfBuff(powerName, amount);
                effect += v;
                details.Add($"{Short(powerName)}({amount})={v}");
                int syn = HandSynergy.Compute(powerName, amount, card, state);
                if (syn != 0)
                {
                    effect += syn;
                    details.Add($"  +syn={syn}");
                }
            }

            // v0.7.7 — Id-derived PowerCatalog fallback. Power cards apply
            // their named Power class via OnPlay, but the application is
            // expressed as a PowerCmd.Apply<XPower>() call rather than a
            // PowerVar<T> in DynamicVars for ~24 cards (BARRICADE, REAPER_FORM,
            // UNMOVABLE, TRACKING, THE_SEALED_THRONE, MAYHEM, AGGRESSION etc.).
            // Those cards have empty PowerApps at runtime even though
            // PowerCatalog knows their canonical value. Derive the power name
            // from card.Id (CARD.MAYHEM -> MayhemPower) and credit it once.
            if (card.PowerApps.Count == 0)
            {
                string derived = IdToPowerName(card.Id);
                if (!string.IsNullOrEmpty(derived))
                {
                    int self = PowerCatalog.LookupSelfBuff(derived);
                    int enemy = PowerCatalog.LookupEnemyDebuff(derived);
                    int pick = System.Math.Max(self, enemy);
                    // Skip the heuristic-default 200 — that fires when neither
                    // dict has the name, providing no real coverage; we'd be
                    // crediting a guess. Anything explicit (positive or negative)
                    // gets credited.
                    if (pick != PowerCatalog.DefaultValue)
                    {
                        effect += pick;
                        details.Add($"idDerived({Short(derived)})={pick}");
                    }
                }
            }

            // v0.7.22 — Power activation condition penalty. Some S+ Powers
            // (EchoForm / Barricade / MachineLearning / Cruelty) have specific
            // board-state conditions to generate value. When those aren't met,
            // PowerCatalog's flat credit overrates the play. Penalty applied
            // here (after PowerCatalog credit) so the net score reflects the
            // delayed / wasted activation.
            int activationPenalty = ComputePowerActivationPenalty(card, state);
            if (activationPenalty != 0)
            {
                effect += activationPenalty;
                details.Add($"actCond={activationPenalty}");
            }

            // v0.2.6 — Energy gain context: low energy + expensive cards waiting → urgent.
            int energyBonus = EvaluateEnergyGain(card, state, w);
            if (energyBonus != 0) details.Add($"energyCtx={energyBonus}");

            // v0.2.6 — Fight-length context: powers shine in long fights, waste in short.
            int fightCtx = EvaluatePowerFightContext(state, w);
            if (fightCtx != 0) details.Add($"fightCtx={fightCtx}");

            var (powerOrbBonus, powerOrbDetail) = EvaluateOrbEffects(card, state);
            if (powerOrbBonus != 0) details.Add(powerOrbDetail);

            // v0.5 — Tier-based ordering for multi-Power hands. Setup > Scaling >
            // Defensive > Tempo > SelfHarm; Defensive jumps the queue under threat.
            int powerCardsInHand = state.Hand.Count(c =>
                c.IsPower && c.IsPlayable);
            var tier = PowerSequencingTier.Classify(card);
            int tierOrdering = PowerSequencingTier.OrderingBonus(tier, powerCardsInHand);
            var (tierCond, tierDetail) = PowerSequencingTier.ConditionalBonus(card, tier, state, w);
            if (tier != SequencingTier.Unknown)
                details.Add(tierOrdering != 0 ? $"tier={tier}+{tierOrdering}" : $"tier={tier}");
            if (!string.IsNullOrEmpty(tierDetail)) details.Add(tierDetail);

            if (buildBonus != 0) details.Add($"buildSyn={buildBonus}");
            if (overrideBonus != 0) details.Add($"override={overrideBonus}");
            buildBonus += overrideBonus;

            // v0.6.5 — POWER_AMPLIFIER / REPLAY axes on the Power-typed cards
            // themselves (Subroutine, Echo Form, Mayhem, Iteration, Juggling,
            // Loop, Nostalgia, Stampede). AmplifierSynergy was previously only
            // called from the Attack and Skill branches, so a Power amplifier
            // with another Power queued behind it would miss the hand-aware
            // boost. Recursion guard (AmplifierSynergy.IsValidTarget excludes
            // other amplifier-axis cards) prevents Power→Power amp loops.
            var (powerAmpBonus, powerAmpDetail) = AmplifierSynergy.Compute(card, state, w);
            if (powerAmpBonus != 0) details.Add(powerAmpDetail);

            // v0.6 — lethal this turn: every non-Attack is dead weight, the
            // remaining damage closes the fight. Heavy penalty so a Power
            // doesn't beat a winning attack on the killing-blow turn.
            int lethalPenalty = lethalThisTurn ? w.LethalModeNonAttackPenalty : 0;
            if (lethalPenalty != 0) details.Add($"lethalMode={lethalPenalty}");

            // v0.7.33 — Self-damage penalty (Power cards rarely carry HP loss,
            // but DOOM_SELF Powers and a few Necrobinder Powers do).
            int selfDmgPowerPenalty = ComputeSelfDamagePenalty(card, state, lethalThisTurn);
            if (selfDmgPowerPenalty != 0)
                details.Add($"selfDmg={selfDmgPowerPenalty}");

            if (fetchPollutionPenalty != 0) details.Add($"fetchPoll={fetchPollutionPenalty}");
            if (comboBonus != 0) details.Add(comboDetail);
            if (monopolyPenalty != 0) details.Add($"energyMono={monopolyPenalty}");

            int total = baseBonus + effect + costTie + energyBonus + fightCtx
                        + powerOrbBonus + tierOrdering + tierCond + buildBonus + powerAmpBonus + lethalPenalty + selfDmgPowerPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty;
            return new ScoreBreakdown(total, "Power",
                Base: baseBonus + costTie,
                Effect: effect + energyBonus + fightCtx + powerOrbBonus + tierOrdering + tierCond + buildBonus + powerAmpBonus + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty,
                TargetBonus: 0, ThreatBonus: 0,
                Details: string.Join(",", details));
        }

        if (card.IsAttack)
        {
            int baseBonus = w.AttackBaseBonus + cost * (w.CostMultiplier / 4);
            details.Add($"attackBase={w.AttackBaseBonus}");

            // AOE: damage applies to every alive enemy; sum target bonuses across all of them.
            // Single-target: damage * 1; target bonus from the chosen enemy.
            bool isAoe = card.Target == TargetType.AllEnemies;
            int aliveCount = state.Enemies.Count(e => e.IsAlive);

            // v0.2.4 — effective damage: (base + Strength) × Vulnerable × Weak
            // For single-target attacks we use the picked enemy's Vulnerable.
            // For AOE we average / take the max — picked enemy isn't well-defined, so use player-Weak only.
            bool playerIsWeak = state.PlayerWeak > 0;
            bool targetIsVulnerable = !isAoe
                && targetIdx >= 0 && targetIdx < state.Enemies.Count
                && state.Enemies[targetIdx].VulnerableAmount > 0;
            int effectivePerHit = StatusMath.EffectiveAttackDmg(card.Damage,
                state.PlayerStrength, targetIsVulnerable, playerIsWeak);

            // v0.6.7 — Variable-damage hit-count override. Card.Hits comes from
            // RepeatVar / CalculatedHits and defaults to 1, but several attacks
            // scale at play time on hand size or remaining energy:
            //   • EXHAUST_BURST (FIEND_FIRE): per-card damage × hand size
            //   • X_COST (SKEWER, WHIRLWIND, VOLLEY, ERADICATE): damage × X
            //     where X is the energy actually spent (all remaining).
            // Without this adjustment FIEND_FIRE [S] scores as a 7-damage hit
            // instead of 7 × hand.Count, severely underrating the card.
            int variableHits = EstimateVariableHits(card, state);
            int effHits = System.Math.Max(System.Math.Max(1, card.Hits), variableHits);
            if (variableHits > card.Hits)
                details.Add($"varHits({card.Hits}→{variableHits})");

            // v0.4 — per-hit damage cap from IntangiblePower (=1) or HardToKillPower (=Amount).
            // Clamp single-target effective per-hit; multi-hit cards still get value because they
            // chip away cap times Hits times instead of one huge hit being wasted.
            if (!isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var capTarget = state.Enemies[targetIdx];
                if (capTarget.DamageCapPerHit > 0 && effectivePerHit > capTarget.DamageCapPerHit)
                {
                    details.Add($"DMG_CAP({capTarget.DamageCapPerHit}):{effectivePerHit}→{capTarget.DamageCapPerHit}");
                    effectivePerHit = capTarget.DamageCapPerHit;
                }
            }
            int effectiveTotal = effectivePerHit * effHits;

            // v0.4 — HardenedShellPower turn-cap: enemy ignores damage past Remaining for this
            // turn. Clamp the card's effective total to the remaining budget; if remaining is 0,
            // the attack is fully wasted (handled by WastedAttackPenalty further down).
            if (!isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var shellTarget = state.Enemies[targetIdx];
                if (shellTarget.HardenedShellRemaining > 0)
                {
                    if (effectiveTotal > shellTarget.HardenedShellRemaining)
                    {
                        details.Add($"SHELL({shellTarget.HardenedShellRemaining}):{effectiveTotal}→{shellTarget.HardenedShellRemaining}");
                        effectiveTotal = shellTarget.HardenedShellRemaining;
                    }
                }
                else if (shellTarget.Powers.ContainsKey("HardenedShellPower"))
                {
                    // Shell exists but budget fully spent this turn — every further attack = 0 dmg.
                    details.Add($"SHELL(spent)→0");
                    effectiveTotal = 0;
                }
            }

            // v0.7.0 — Random multi-hit detection. Cards with TargetType.RandomEnemy
            // pick a NEW random alive opponent for *each* hit (game source:
            // `AttackCommand.TargetingRandomOpponents` re-rolls per hit via
            // `Rng.CombatTargets.NextItem`). Existing single-target scoring
            // assumes all hits land on the planner-picked enemy, which both
            // over-credits LETHAL bonuses (3×3 vs HP4 enemy is 50% lethal, not
            // 100%) and skips chip damage on other enemies. Probability model
            // below handles both.
            bool isRandom = card.Target == TargetType.RandomEnemy;

            int effect;
            string dmgLabel;
            if (isAoe)
            {
                // v0.5 — Per-enemy AOE damage with each target's own Vulnerable, Intangible
                // (DamageCapPerHit) and HardenedShellRemaining cap applied via StatusMath
                // helpers. Previous bulk `effectivePerHit × Hits × aliveCount` used the
                // wrong-target Vulnerable (always false for AOE) and ignored caps entirely.
                int aggregatedDmg = 0;
                int capsHit = 0, shellHit = 0;
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var e = state.Enemies[i];
                    if (!e.IsAlive) continue;
                    int rawPer = StatusMath.EffectiveAttackDmg(card.Damage,
                        state.PlayerStrength, e.VulnerableAmount > 0, playerIsWeak);
                    int perEnemyTotal = StatusMath.EffectivePerEnemyTotal(
                        card.Damage, effHits, state.PlayerStrength, e, playerIsWeak);
                    if (rawPer > 0 && e.DamageCapPerHit > 0 && rawPer > e.DamageCapPerHit) capsHit++;
                    if ((e.HardenedShellRemaining > 0 && rawPer * effHits > e.HardenedShellRemaining)
                        || (rawPer > 0 && e.HardenedShellRemaining == 0
                            && e.Powers.ContainsKey("HardenedShellPower"))) shellHit++;
                    aggregatedDmg += perEnemyTotal;
                }
                effect = aggregatedDmg * w.DamagePerPointBonus;
                int baseTotalForLabel = card.Damage * effHits * System.Math.Max(1, aliveCount);
                dmgLabel = aggregatedDmg != baseTotalForLabel
                    ? $"eff{aggregatedDmg}(base{card.Damage}×{effHits}×{aliveCount})"
                    : $"dmg{aggregatedDmg}";
                var clampTags = (capsHit > 0 ? $",cap×{capsHit}" : "")
                              + (shellHit > 0 ? $",shell×{shellHit}" : "");
                details.Add(aliveCount > 1
                    ? $"{dmgLabel}*{w.DamagePerPointBonus}*aoe{aliveCount}={effect}{clampTags}"
                    : $"{dmgLabel}*{w.DamagePerPointBonus}={effect}{clampTags}");
            }
            else if (isRandom)
            {
                // v0.7.0 — Random multi-hit: each hit independently re-rolls
                // a target. Damage scoring iterates alive enemies, distributing
                // expected hits and clamping per-enemy damage to that enemy's
                // EffectiveHp (overkill discount). Per-enemy clamp avoids
                // counting damage that would have been "wasted" overkill if
                // one enemy soaked all the hits.
                int aggregatedDmg = 0;
                int reachableEnemies = 0;
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var e = state.Enemies[i];
                    if (!e.IsAlive) continue;
                    reachableEnemies++;
                }
                if (reachableEnemies > 0)
                {
                    double pHitOnEnemy = 1.0 / reachableEnemies;
                    for (int i = 0; i < state.Enemies.Count; i++)
                    {
                        var e = state.Enemies[i];
                        if (!e.IsAlive) continue;
                        int perHitForE = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, e.VulnerableAmount > 0, playerIsWeak);
                        if (e.DamageCapPerHit > 0 && perHitForE > e.DamageCapPerHit)
                            perHitForE = e.DamageCapPerHit;
                        if (perHitForE <= 0) continue;
                        double expectedHits = effHits * pHitOnEnemy;
                        int expectedDmg = (int)(expectedHits * perHitForE);
                        // Overkill clamp per enemy.
                        int clamped = System.Math.Min(expectedDmg, e.Hp + e.Block);
                        aggregatedDmg += clamped;
                    }
                }
                effect = aggregatedDmg * w.DamagePerPointBonus;
                dmgLabel = $"randDmg{aggregatedDmg}(p{reachableEnemies}t,base{card.Damage}×{effHits})";
                details.Add($"{dmgLabel}*{w.DamagePerPointBonus}={effect}");
            }
            else
            {
                // v0.7.0 — Overkill discount: damage beyond what kills the target
                // is wasted. Clamp the *damage-score* portion to target.EffectiveHp;
                // the LETHAL bonus in ScoreAttackTarget still uses the unclamped
                // effectiveTotal so kills still register.
                int dmgForScoring = effectiveTotal;
                if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
                {
                    var t = state.Enemies[targetIdx];
                    if (t.IsAlive)
                    {
                        int effHp = t.Hp + t.Block;
                        if (effHp > 0 && dmgForScoring > effHp)
                            dmgForScoring = effHp;
                    }
                }
                effect = dmgForScoring * w.DamagePerPointBonus;
                dmgLabel = dmgForScoring != card.TotalDamage
                    ? (dmgForScoring < effectiveTotal
                        ? $"eff{dmgForScoring}(cap{effectiveTotal}→hp)"
                        : $"eff{effectiveTotal}(base{card.TotalDamage})")
                    : $"dmg{card.TotalDamage}";
                details.Add($"{dmgLabel}*{w.DamagePerPointBonus}={effect}");
            }

            // v0.7.23 — Survival probability for enemy-debuff value scaling.
            // VulnerablePower / WeakPower / FrailPower / Constrict etc. give
            // multi-turn future-attack benefit. If THIS attack kills the
            // target, that future value is wasted (Vuln on a corpse = 0).
            // Single-target: ratio = (effHp − attackDmg) / effHp, floor 0.
            // AOE: avg fraction-of-HP-remaining across alive enemies.
            // Floor 0.15 preserves a small portion (chain-attack benefit even
            // when the next strike kills).
            double survivalRatio;
            if (isAoe)
            {
                int sumRemain = 0, sumEffHp = 0;
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var e = state.Enemies[i];
                    if (!e.IsAlive) continue;
                    int eEff = e.Hp + e.Block;
                    if (eEff <= 0) continue;
                    sumRemain += System.Math.Max(0, eEff - effectiveTotal);
                    sumEffHp += eEff;
                }
                survivalRatio = sumEffHp > 0 ? sumRemain / (double)sumEffHp : 1.0;
            }
            else if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var t = state.Enemies[targetIdx];
                if (!t.IsAlive) survivalRatio = 1.0;
                else
                {
                    int effHp = t.Hp + t.Block;
                    survivalRatio = effHp > 0
                        ? System.Math.Max(0, effHp - effectiveTotal) / (double)effHp
                        : 0.0;
                }
            }
            else survivalRatio = 1.0;
            if (survivalRatio < 0.15 && survivalRatio > 0) survivalRatio = 0.15;
            // Pure-kill (survivalRatio = 0) keeps 0 — debuff fully wasted.

            // v0.7.24 — Future-attack-potential ratio. Vulnerable only gives
            // value when WE attack the debuffed enemy in subsequent turns. If
            // the remaining deck (hand-without-self + draw + discard) has no
            // attacks, applied Vuln decays unused. ratio = attacks / total,
            // saturated at 0.3 → 1.0 multiplier (typical attack-heavy deck).
            double futureAttackMult = ComputeFutureAttackMultiplier(state, card);

            int attached = 0;
            foreach (var (powerName, amount) in card.PowerApps)
            {
                // AOE attaches debuff to every enemy too. Apply stack curve once,
                // then multiplier for AOE breadth.
                int perEnemy = (int)(PowerCatalog.ValueEnemyDebuff(powerName, amount) * w.AttachedDebuffMultiplier);
                // v0.7.23 — Survival probability scaling. Future-turn debuff
                // value is lost when target dies on this attack.
                perEnemy = (int)(perEnemy * survivalRatio);
                // v0.7.24 — Attack-dependent debuff scaling. Powers that only
                // pay off when we attack (Vulnerable, Rupture) are discounted
                // when the deck is attack-poor.
                if (IsAttackDependentDebuff(powerName) && futureAttackMult < 1.0)
                {
                    int before = perEnemy;
                    perEnemy = (int)(perEnemy * futureAttackMult);
                    if (before != perEnemy)
                        details.Add($"  futureAtk×{futureAttackMult:F2}");
                }

                // v0.2.9 — Artifact blocks our enemy debuffs. v0.5 — canonical STS
                // semantics: each debuff APPLICATION consumes 1 Artifact charge and is
                // entirely blocked (the amount is irrelevant). So an enemy is "reached"
                // by this debuff iff its Artifact stack is 0.
                if (isAoe)
                {
                    int reach = state.Enemies.Count(e => e.IsAlive && e.ArtifactAmount == 0);
                    int blocked = aliveCount - reach;
                    if (blocked > 0)
                        details.Add($"  artifact-blocked={blocked}");
                    int v = perEnemy * reach;
                    attached += v;
                    details.Add($"+{Short(powerName)}({amount})x{reach}={v}");
                }
                else
                {
                    bool blockedSingle = targetIdx >= 0 && targetIdx < state.Enemies.Count
                        && state.Enemies[targetIdx].ArtifactAmount > 0;
                    if (blockedSingle)
                    {
                        details.Add($"+{Short(powerName)}({amount})=BLOCKED");
                        // perEnemy = 0 — Artifact fully absorbs this debuff application
                    }
                    else
                    {
                        attached += perEnemy;
                        details.Add($"+{Short(powerName)}({amount})={perEnemy}");
                    }
                }

                int syn = HandSynergy.Compute(powerName, amount, card, state);
                if (syn != 0)
                {
                    attached += syn;
                    details.Add($"  +syn={syn}");
                }
            }

            int targetBonus = 0;
            string targetDetails = "";
            int wastedPenalty = 0;
            if (isRandom)
            {
                // v0.7.0 — For each alive enemy, compute P(this card kills it)
                // via binomial distribution over hits, then weight ScoreAttackTarget's
                // full kill-bonus (LETHAL + intent-aware: Buff/Heal/Summon/DeathBlow
                // disruption + state bonuses) by that probability. Single-target
                // attacks score the planner's *picked* enemy's kill bonus
                // unconditionally; random attacks earn only their fair share of
                // each potential kill they could land.
                var rndParts = new List<string>();
                int reach = 0;
                for (int i = 0; i < state.Enemies.Count; i++)
                    if (state.Enemies[i].IsAlive) reach++;
                if (reach > 0)
                {
                    double pHit = 1.0 / reach;
                    for (int i = 0; i < state.Enemies.Count; i++)
                    {
                        var e = state.Enemies[i];
                        if (!e.IsAlive) continue;
                        int perHitForE = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, e.VulnerableAmount > 0, playerIsWeak);
                        if (e.DamageCapPerHit > 0 && perHitForE > e.DamageCapPerHit)
                            perHitForE = e.DamageCapPerHit;
                        if (perHitForE <= 0) continue;
                        int effHp = e.Hp + e.Block;
                        int hitsNeeded = (effHp + perHitForE - 1) / perHitForE;
                        double pLethal = BinomialAtLeast(effHits, pHit, hitsNeeded);
                        // Always credit DefendIntent penalty (no kill needed —
                        // even chip wastes block scaling).
                        var (fullBonus, _) = ScoreAttackTarget(card, i, state, w, effHp);
                        int weightedBonus = (int)(fullBonus * pLethal);
                        if (weightedBonus != 0)
                        {
                            targetBonus += weightedBonus;
                            if (pLethal >= 0.2)
                                rndParts.Add($"e{i}:P={pLethal:F2}→{weightedBonus:+#;-#;0}");
                        }
                    }
                }
                if (rndParts.Count > 0) targetDetails = string.Join("|", rndParts);
            }
            else if (isAoe)
            {
                var aoeParts = new List<string>();
                int totalAliveBlock = 0, totalAliveDmg = 0, aliveTargets = 0;
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    if (!state.Enemies[i].IsAlive) continue;
                    var ei = state.Enemies[i];
                    int perEnemyDmg = StatusMath.EffectivePerEnemyTotal(
                        card.Damage, card.Hits, state.PlayerStrength, ei, playerIsWeak);
                    var (b, d) = ScoreAttackTarget(card, i, state, w, perEnemyDmg);
                    targetBonus += b;
                    totalAliveBlock += ei.Block;
                    totalAliveDmg += perEnemyDmg;
                    aliveTargets++;
                    if (!string.IsNullOrEmpty(d)) aoeParts.Add($"e{i}:{d}");
                }
                if (aoeParts.Count > 0) targetDetails = string.Join("|", aoeParts);
                // v0.2.6 — AOE wasted check: every alive enemy's block absorbs all damage → wasted.
                if (aliveTargets > 0 && totalAliveDmg <= totalAliveBlock && totalAliveDmg > 0)
                {
                    wastedPenalty = w.WastedAttackPenalty / 2;
                    details.Add($"WASTED_AOE{wastedPenalty}");
                }
                // v0.5 — AOE-zeroed check: every enemy capped to 0 damage (full shell
                // or Intangible-with-no-piercing). Without this, an AOE swing that
                // accomplishes literally nothing would only get the half-penalty above
                // (and only if totalAliveBlock > 0); shell-spent boards have 0 block
                // remaining so the check above would skip them.
                else if (aliveTargets > 0 && totalAliveDmg == 0 && card.Damage > 0)
                {
                    wastedPenalty = w.WastedAttackPenalty;
                    details.Add($"WASTED_AOE_ZERO{wastedPenalty}");
                }
            }
            else
            {
                var (b, d) = ScoreAttackTarget(card, targetIdx, state, w, effectiveTotal);
                targetBonus = b;
                targetDetails = d;
                // v0.2.6 — single-target wasted-attack: block / shell absorbs all damage.
                if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
                {
                    var t = state.Enemies[targetIdx];
                    if (t.IsAlive && !t.IsInert)
                    {
                        // v0.4 — shell-zeroed: HardenedShell budget spent or cap reduced to 0.
                        if (effectiveTotal <= 0)
                        {
                            wastedPenalty = w.WastedAttackPenalty;
                            details.Add($"WASTED_ATK_ZERO{wastedPenalty}");
                        }
                        else if (effectiveTotal <= t.Block)
                        {
                            wastedPenalty = w.WastedAttackPenalty;
                            details.Add($"WASTED_ATK{wastedPenalty}");
                        }
                    }
                }
            }
            if (targetDetails.Length > 0) details.Add(targetDetails);

            // v0.4 — Burst-damage window: not lethal, but chunks enough HP to flip the
            // attack-vs-block calculus. Pays out only for direct single-target burst
            // (AOE total-HP rules are noisier and the per-enemy bonus already adds up).
            int burstBonus = 0;
            if (!isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var bt = state.Enemies[targetIdx];
                if (bt.IsAlive && effectiveTotal > 0 && effectiveTotal < bt.EffectiveHp)
                {
                    int chunkedHp = System.Math.Max(0, effectiveTotal - bt.Block);
                    double ratio = (double)chunkedHp / System.Math.Max(1, bt.Hp);
                    if (ratio >= w.BurstDamage70Ratio)
                    {
                        burstBonus = w.BurstDamage70Bonus;
                        details.Add($"BURST70({ratio:F2})+{burstBonus}");
                    }
                    else if (ratio >= w.BurstDamage50Ratio)
                    {
                        burstBonus = w.BurstDamage50Bonus;
                        details.Add($"BURST50({ratio:F2})+{burstBonus}");
                    }
                }
            }

            // v0.4 — Thorns reflect: every hit we deal costs us ThornsAmount HP.
            // Balanced playstyle prioritises HP preservation, so weight self-damage at
            // ×100 score per point (≈ 2× WastedBlock penalty per HP, big enough to push
            // the planner toward non-attack alternatives when reflect is heavy).
            // v0.7.34 — Also accumulate thornsDamage (raw HP) for the survival
            // check below: a multi-hit attack into thorns can self-promote
            // urgency from Heavy to Fatal.
            // v0.7.40 — Scale penalty by HP fraction. 1 HP loss matters more
            // at HP 10 than at HP 80 — flat -100/point under-penalised low-HP
            // thorns plays, letting AI attack instead of defending.
            int thornsPenalty = 0;
            int thornsDamage = 0;  // raw HP cost from this attack's thorns reflects
            int hits = System.Math.Max(1, card.Hits);
            if (isAoe)
            {
                int aliveThorns = state.Enemies.Where(e => e.IsAlive).Sum(e => e.ThornsAmount);
                if (aliveThorns > 0)
                {
                    thornsPenalty = -aliveThorns * hits * 100;
                    thornsDamage = aliveThorns * hits;
                    details.Add($"THORNS_AOE{thornsPenalty}");
                }
            }
            else if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                int thorns = state.Enemies[targetIdx].ThornsAmount;
                if (thorns > 0)
                {
                    thornsPenalty = -thorns * hits * 100;
                    thornsDamage = thorns * hits;
                    details.Add($"THORNS{thornsPenalty}");
                }
            }
            // v0.7.40 — HP-fraction multiplier. Compounds with the flat penalty.
            if (thornsDamage > 0)
            {
                int playerHp = System.Math.Max(1, state.PlayerHp);
                double hpFrac = thornsDamage / (double)playerHp;
                int oldPenalty = thornsPenalty;
                if (hpFrac >= 0.5)       thornsPenalty = thornsPenalty * 3;
                else if (hpFrac >= 0.25) thornsPenalty = thornsPenalty * 2;
                else if (hpFrac >= 0.10) thornsPenalty = thornsPenalty * 15 / 10;
                if (thornsPenalty != oldPenalty)
                    details.Add($"THORNS_HP({hpFrac:F2})x{thornsPenalty/(double)oldPenalty:F1}");
            }

            if (buildBonus != 0) details.Add($"buildSyn={buildBonus}");
            if (overrideBonus != 0) details.Add($"override={overrideBonus}");
            buildBonus += overrideBonus;
            var (atkOrbBonus, atkOrbDetail) = EvaluateOrbEffects(card, state);
            if (atkOrbBonus != 0) details.Add(atkOrbDetail);

            // v0.5 — attack cards can also carry EnergyGain (rare but exists, e.g.,
            // Defect's Sweeping Beam variants). Previously only Power/Skill paths
            // consulted EvaluateEnergyGain so attack+energy-gain combos missed the
            // urgent / waste signals. EvaluateEnergyGain returns 0 for non-gain cards
            // so this is a no-op for plain attacks.
            int atkEnergyBonus = EvaluateEnergyGain(card, state, w);
            if (atkEnergyBonus != 0) details.Add($"energyCtx={atkEnergyBonus}");

            // v0.5 — attack cards can also have draw (DrawCardPower in PowerApps,
            // Mind Blast / cycle-attack hybrids). Same pattern: EvaluateDrawCard
            // returns 0 for non-draw cards, so this is a no-op for plain attacks.
            int atkDrawBonus = EvaluateDrawCard(card, state, w);
            if (atkDrawBonus != 0) details.Add($"drawCtx={atkDrawBonus}");

            // v0.5 — ATTACK_REPLAY axis (Beat Down, One-Two Punch, Stampede). Scores
            // off the best other attack in hand, so a replay attack rises in priority
            // when another high-value attack is queued behind it.
            var (atkAmpBonus, atkAmpDetail) = AmplifierSynergy.Compute(card, state, w);
            if (atkAmpBonus != 0) details.Add(atkAmpDetail);

            // v0.5 — Effect-axis synergies (DAMAGE_AMPLIFIER, VULN_AMPLIFIER,
            // WEAK_AMPLIFIER, BLOCK_PAYOFF, HP_LOSS_CONSUMER, …).
            var (atkEffBonus, atkEffDetail) = EffectSynergy.Compute(card, targetIdx, state);
            if (atkEffBonus != 0) details.Add(atkEffDetail);

            // v0.5 — Survival urgency: non-lethal attacks should defer to defense
            // when the player is about to die. Lethal kills bypass naturally via
            // RealLethalKillBonus (+5000) which dwarfs the penalty.
            // v0.7.33 — Effective urgency accounts for card.HpLossAmount: a
            // Spite/Inferno-trigger attack on a low-HP turn can self-promote
            // the situation from Heavy to Fatal.
            // v0.7.34 — Thorns reflect HP is also folded into the survival
            // check. A multi-hit attack vs heavy thorns can self-kill before
            // enemy intent fires.
            int survivalAtkPenalty = 0;
            {
                var urg = GetEffectiveUrgency(state, card.HpLossAmount + thornsDamage);
                if (urg == SurvivalUrgency.Fatal || urg == SurvivalUrgency.Heavy)
                {
                    bool isLethalSingle = !isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count
                                         && state.Enemies[targetIdx].IsAlive
                                         && effectiveTotal >= state.Enemies[targetIdx].EffectiveHp;
                    bool isLethalAoe = isAoe && state.Enemies.Where(e => e.IsAlive).All(e =>
                    {
                        int perHit = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, e.VulnerableAmount > 0, playerIsWeak);
                        return perHit * System.Math.Max(1, card.Hits) >= e.EffectiveHp;
                    });
                    if (!isLethalSingle && !isLethalAoe)
                    {
                        survivalAtkPenalty = urg == SurvivalUrgency.Fatal ? -1200 : -400;
                        details.Add($"survival{urg}_nonLethal={survivalAtkPenalty}");
                    }
                }
            }

            // v0.7.33 — Additional penalty for self-damage that promotes urgency.
            // v0.7.34 — Includes thorns reflect HP for accurate urgency.
            int selfDmgAtkPenalty = ComputeSelfDamagePenaltyWithThorns(card, state, lethalThisTurn, thornsDamage);
            if (selfDmgAtkPenalty != 0)
                details.Add($"selfDmg={selfDmgAtkPenalty}");

            if (fetchPollutionPenalty != 0) details.Add($"fetchPoll={fetchPollutionPenalty}");
            if (comboBonus != 0) details.Add(comboDetail);
            if (monopolyPenalty != 0) details.Add($"energyMono={monopolyPenalty}");

            // v0.7.23 — Lethal-mode setup attack penalty. Setup attacks
            // (Bash-like: low dmg-per-energy + applies debuff) sacrifice
            // immediate damage for future-turn payoff. When we can kill
            // THIS turn with simpler attacks, the setup card is overkill
            // — its debuff value lapses on a corpse.
            int lethalSetupPenalty = 0;
            if (lethalThisTurn && IsSetupAttackCard(card))
            {
                lethalSetupPenalty = w.LethalModeNonAttackPenalty * 6 / 10;
                details.Add($"lethalSetup={lethalSetupPenalty}");
            }

            int total = baseBonus + effect + attached + targetBonus + wastedPenalty + thornsPenalty + burstBonus + atkOrbBonus + buildBonus + atkEnergyBonus + atkDrawBonus + atkAmpBonus + atkEffBonus + survivalAtkPenalty + selfDmgAtkPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + lethalSetupPenalty;
            return new ScoreBreakdown(total, isAoe ? "Attack-AOE" : "Attack",
                Base: baseBonus,
                Effect: effect + attached + burstBonus + atkOrbBonus + thornsPenalty + buildBonus + atkEnergyBonus + atkDrawBonus + atkAmpBonus + atkEffBonus + fetchPollutionPenalty + comboBonus + monopolyPenalty,
                TargetBonus: targetBonus + wastedPenalty + survivalAtkPenalty, ThreatBonus: 0,
                Details: string.Join(",", details));
        }

        // Skill
        {
            int baseBonus = w.SkillBaseBonus + cost * (w.CostMultiplier / 4);
            // v0.2.4 — effective block: (base + Dexterity) × Frail
            // v0.6.7 — Variable-block multiplier for EXHAUST_BURST skills
            // (SECOND_WIND: 5 block × non-attack hand cards exhausted). The card's
            // raw Block is per-card; the realised block scales with eligible hand.
            int blockMultiplier = EstimateBlockMultiplier(card, state);
            int rawBlock = card.Block * System.Math.Max(1, blockMultiplier);
            int effectiveBlock = StatusMath.EffectiveBlock(rawBlock,
                state.PlayerDexterity, state.PlayerFrail > 0);
            int effect = effectiveBlock * w.BlockPerPointBonus;
            details.Add($"skillBase={w.SkillBaseBonus}");
            if (blockMultiplier > 1)
                details.Add($"varBlock(×{blockMultiplier})");
            if (card.Block > 0)
            {
                string blockLabel = effectiveBlock != card.Block
                    ? $"eff{effectiveBlock}(base{card.Block}×{blockMultiplier})"
                    : $"block{card.Block}";
                details.Add($"{blockLabel}*{w.BlockPerPointBonus}={effect}");
            }

            bool isSelfApply = IsSelfTargetedTarget(card.Target);
            bool skillIsAoe = card.Target == TargetType.AllEnemies;
            int powerEffect = 0;
            foreach (var (powerName, amount) in card.PowerApps)
            {
                int v;
                if (isSelfApply)
                {
                    v = PowerCatalog.ValueSelfBuff(powerName, amount);
                    powerEffect += v;
                    details.Add($"{Short(powerName)}({amount})→self={v}");
                }
                else
                {
                    // v0.5 — skill enemy-debuff scoring now respects:
                    //   • AOE scaling: AllEnemies skills (Footwork-style Weak-to-all)
                    //     used to score a single-target value regardless of board.
                    //   • Artifact gating (canonical STS): each debuff APPLICATION
                    //     consumes one Artifact charge and is entirely blocked. So an
                    //     enemy is reachable iff their ArtifactAmount is 0.
                    int per = PowerCatalog.ValueEnemyDebuff(powerName, amount);
                    if (skillIsAoe)
                    {
                        int reach = state.Enemies.Count(e => e.IsAlive && e.ArtifactAmount == 0);
                        int blocked = state.Enemies.Count(e => e.IsAlive) - reach;
                        v = per * reach;
                        powerEffect += v;
                        details.Add(blocked > 0
                            ? $"{Short(powerName)}({amount})→aoe×{reach}={v} (artif-blk={blocked})"
                            : $"{Short(powerName)}({amount})→aoe×{reach}={v}");
                    }
                    else if (targetIdx >= 0 && targetIdx < state.Enemies.Count
                             && state.Enemies[targetIdx].ArtifactAmount > 0)
                    {
                        v = 0;
                        details.Add($"{Short(powerName)}({amount})→enemy=BLOCKED");
                    }
                    else
                    {
                        v = per;
                        powerEffect += v;
                        details.Add($"{Short(powerName)}({amount})→enemy={v}");
                    }
                }
                int syn = HandSynergy.Compute(powerName, amount, card, state);
                if (syn != 0)
                {
                    powerEffect += syn;
                    details.Add($"  +syn={syn}");
                }
            }

            int threatBonus = 0;
            int residual = (card.Target == TargetType.Self && effectiveBlock > 0 && !allInert)
                ? EnemyTurnSimulator.PredictPlayerDmg(state) : 0;
            bool neutralizes = residual > 0 && effectiveBlock >= residual;

            // BlockUnderThreatBonus only applies when the card *actually* blocks. Otherwise
            // a self-targeted skill with no block (Turbo / Inflame / energy-gain cards) would
            // hoover up a 1500-point bonus just for being Self-target — which has been
            // observed pushing Turbo above Strike in lethal windows.
            if (card.Target == TargetType.Self && card.Block > 0 && threat > threshold && !allInert)
            {
                threatBonus = w.BlockUnderThreatBonus;
                details.Add($"threatBonus={threatBonus}");
            }

            // v0.4 — "Block fully neutralises threat": if a self-block card brings the
            // residual damage to exactly zero, take 0 hits this turn. That beats Power
            // cards even when the threat ratio is too low to trip BlockUnderThreatBonus.
            if (neutralizes)
            {
                threatBonus += w.BlockNeutralizeBonus;
                details.Add($"neutralize({residual}leak)+{w.BlockNeutralizeBonus}");
            }

            // Wasted-block penalty: only for blocks that genuinely accomplish nothing.
            // If neutralize fires (block fully absorbs an incoming hit), it's by definition
            // NOT wasted — these two rules used to fight each other.
            int wastedBlock = (card.Target == TargetType.Self && card.Block > 0
                && threat < w.NoThreatRatio && !allInert && !neutralizes) ? w.WastedBlockPenalty : 0;
            // v0.2.6 — Energy gain context applies to Skill carriers too (Adrenaline-style).
            int energyBonus = EvaluateEnergyGain(card, state, w);
            if (energyBonus != 0) details.Add($"energyCtx={energyBonus}");

            // v0.2.6 — Draw card: only valuable when the rest of the hand has nothing strong.
            int drawBonus = EvaluateDrawCard(card, state, w);
            if (drawBonus != 0) details.Add($"drawCtx={drawBonus}");

            var (skillOrbBonus, skillOrbDetail) = EvaluateOrbEffects(card, state);
            if (skillOrbBonus != 0) details.Add(skillOrbDetail);

            // v0.4 — EnragePower on any alive enemy: playing a Skill raises their Strength
            // permanently for the fight, so every future hit they land does Amount extra
            // damage. Penalise skill plays proportional to total enrage stacks × expected
            // future hit count (avg 2 turns × 1 hit/enemy).
            int totalEnrage = 0;
            foreach (var e in state.Enemies)
            {
                if (!e.IsAlive) continue;
                if (e.Powers.TryGetValue("EnragePower", out var enrage) && enrage > 0)
                    totalEnrage += enrage;
            }
            int enragePenalty = 0;
            if (totalEnrage > 0)
            {
                // Each enrage stack = +1 enemy damage per hit; assume ~2 future hits.
                enragePenalty = -totalEnrage * 100;
                details.Add($"enrage{enragePenalty}");
            }

            if (buildBonus != 0) details.Add($"buildSyn={buildBonus}");
            if (overrideBonus != 0) details.Add($"override={overrideBonus}");
            buildBonus += overrideBonus;

            // v0.5 — POWER_AMPLIFIER / REPLAY / SKILL_REPLAY axes (Subroutine, Signal
            // Boost, Dual Wield, Iteration, Loop, Juggling, Hidden Gem, Nostalgia,
            // Catastrophe, Nightmare). Scores off the best replay target in hand.
            var (skillAmpBonus, skillAmpDetail) = AmplifierSynergy.Compute(card, state, w);
            if (skillAmpBonus != 0) details.Add(skillAmpDetail);

            // v0.5 — Effect-axis synergies (BLOCK_AMPLIFIER, VULN_AMPLIFIER, etc.).
            // Skill side: Entrench / Pillar / Unmovable rise when block accumulated;
            // Bully / Cruelty / Dismantle rise when enemy is already Vuln.
            var (skillEffBonus, skillEffDetail) = EffectSynergy.Compute(card, -1, state);
            if (skillEffBonus != 0) details.Add(skillEffDetail);

            // v0.6.8 — EXHAUST_BURST skills with non-Block/non-damage payoffs
            // (EIDOLON: hand≥9 → Intangible; STOKE: gen N random cards;
            //  PURITY: target-select up to 3 exhaust — typically curses).
            // Hand-state-aware special-effect bonus.
            int exhaustBurstBonus = EvaluateExhaustBurstSpecial(card, state);
            if (exhaustBurstBonus != 0) details.Add($"exhBurstSpecial={exhaustBurstBonus}");
            skillEffBonus += exhaustBurstBonus;

            // v0.5 — Survival urgency for pure-setup skills (no block, no energy gain,
            // no draw). Inflame / Limit Break style cards should defer when the player
            // is about to die. Block / energy / draw skills are exempt — they're the
            // survival response or feed it.
            // v0.7.33 — Effective urgency includes the skill's own HP loss.
            int survivalSkillPenalty = 0;
            if (card.Block == 0 && !card.IsEnergyGainCard && !card.IsDrawCard)
            {
                var urgency = GetEffectiveUrgency(state, card.HpLossAmount);
                survivalSkillPenalty = urgency switch
                {
                    SurvivalUrgency.Fatal    => -900,
                    SurvivalUrgency.Heavy    => -350,
                    _ => 0,
                };
                if (survivalSkillPenalty != 0)
                    details.Add($"survival{urgency}={survivalSkillPenalty}");
            }

            // v0.7.33 — Self-damage penalty for skills that worsen survivability
            // (e.g. HP_LOSS axis skills used for archetype payoffs).
            int selfDmgSkillPenalty = ComputeSelfDamagePenalty(card, state, lethalThisTurn: false);
            if (selfDmgSkillPenalty != 0)
                details.Add($"selfDmg={selfDmgSkillPenalty}");

            // v0.6 — Skill sequencing tier. Smaller than Power's tier
            // ordering (Setup +100 / Cantrip +60 / others 0). Only kicks in
            // when ≥2 Skills compete in hand.
            int skillsInHand = state.Hand.Count(c => c.IsSkill && c.IsPlayable);
            var skillTier = SkillSequencingTier.Classify(card);
            int skillTierOrdering = SkillSequencingTier.OrderingBonus(skillTier, skillsInHand);
            var (skillTierCond, skillTierDetail) = SkillSequencingTier.ConditionalBonus(card, skillTier, state);
            if (skillTier != SkillTier.Unknown)
                details.Add(skillTierOrdering != 0 ? $"sklTier={skillTier}+{skillTierOrdering}" : $"sklTier={skillTier}");
            if (!string.IsNullOrEmpty(skillTierDetail)) details.Add(skillTierDetail);

            // v0.6 — lethal this turn: non-Attack cards are dead weight.
            // Energy / draw / setup-debuff skills also penalised — by
            // definition we already have lethal damage in hand, so nothing
            // else this turn matters.
            int lethalPenalty = lethalThisTurn ? w.LethalModeNonAttackPenalty : 0;
            if (lethalPenalty != 0) details.Add($"lethalMode={lethalPenalty}");

            if (fetchPollutionPenalty != 0) details.Add($"fetchPoll={fetchPollutionPenalty}");
            if (comboBonus != 0) details.Add(comboDetail);
            if (monopolyPenalty != 0) details.Add($"energyMono={monopolyPenalty}");

            int total = baseBonus + effect + powerEffect + threatBonus + wastedBlock + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus + skillAmpBonus + skillEffBonus + survivalSkillPenalty + selfDmgSkillPenalty + skillTierOrdering + skillTierCond + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty;
            return new ScoreBreakdown(total, "Skill",
                Base: baseBonus,
                Effect: effect + powerEffect + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus + skillAmpBonus + skillEffBonus + survivalSkillPenalty + skillTierOrdering + skillTierCond + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty,
                TargetBonus: wastedBlock, ThreatBonus: threatBonus,
                Details: string.Join(",", details));
        }
    }

    /// <summary>
    /// v0.6.7 — Estimates the effective Hits count for variable-damage attacks
    /// at play time. Returns the larger of <see cref="SimCard.Hits"/> and the
    /// estimate; callers default to Hits when the estimate is smaller (no
    /// inadvertent downgrade for cards whose RepeatVar already reflects reality).
    ///
    /// Patterns handled:
    ///   • EXHAUST_BURST (FIEND_FIRE): card.Damage applies once per card exhausted
    ///     from hand. Returns <c>handPlayableCount</c> — playable cards excluding
    ///     self that will be exhausted on play. Excludes curses/status (still
    ///     exhausted in some cases but the per-card damage convention is for
    ///     non-curse cards; conservative direction toward false-negative).
    ///   • X_COST (SKEWER, WHIRLWIND, VOLLEY, ERADICATE): card consumes all
    ///     remaining energy. Returns <see cref="SimState.PlayerEnergy"/>.
    ///
    /// HEAVENLY_DRILL's "×2 if X ≥ 4" doubling is intentionally omitted — the
    /// card already scores significantly at base X.
    /// </summary>
    private static int EstimateVariableHits(SimCard card, SimState state)
    {
        if (!card.IsAttack || card.Damage <= 0) return 0;

        if (card.Axes.Contains("EXHAUST_BURST"))
        {
            int n = 0;
            for (int i = 0; i < state.Hand.Count; i++)
            {
                var c = state.Hand[i];
                if (ReferenceEquals(c, card)) continue;
                if (c.IsCurseOrStatus) continue;
                n++;
            }
            // +1 for the card itself being exhausted counts as damage too? FIEND_FIRE
            // exhausts THE card itself plus all other hand cards. Per the description
            // ("deal 7 per exhausted card"), each exhausted card including self contributes.
            // n excludes self above, so add 1.
            return n + 1;
        }

        if (card.Axes.Contains("X_COST"))
        {
            // X = energy spent (X-cost cards consume all remaining energy on play).
            // Use PlayerEnergy as the upper bound — actual X may be less if the
            // card has a min-cost rule, but PlayerEnergy is a tight upper-bound
            // estimate.
            int x = System.Math.Max(1, state.PlayerEnergy);
            // v0.6.8 — HEAVENLY_DRILL: if X ≥ 4 (threshold stored as Energy:4 var),
            // X doubles. Per game source `if (num >= Energy) num *= 2`. Hardcoded
            // id-check is fine — this is the only card with the threshold-double
            // pattern in v0.103.2.
            if (card.Id == "CARD.HEAVENLY_DRILL" && x >= 4)
                x *= 2;
            return x;
        }

        // v0.6.8 — TEAR_ASUNDER: hits = 1 + player HP-loss events this combat.
        // Game source uses CalculatedVar with a multiplier closure that reads
        // CombatHistory at OnPlay time. PreviewValue may or may not invoke it
        // reliably during snapshot, so override here using CombatPlayerHpLossEvents
        // captured in StateSnapshotter (same data source as the game's closure).
        if (card.Id == "CARD.TEAR_ASUNDER")
            return 1 + state.CombatPlayerHpLossEvents;

        return 0;
    }

    /// <summary>
    /// v0.6.7 — Estimates the block-multiplier for EXHAUST_BURST skills. SECOND_WIND
    /// gains its declared Block value PER non-attack card exhausted from hand;
    /// PURITY gains per card exhausted (up to 3). Returns 1 (no multiplier) when
    /// the card isn't EXHAUST_BURST.
    /// </summary>
    private static int EstimateBlockMultiplier(SimCard card, SimState state)
    {
        if (!card.IsSkill || card.Block <= 0) return 1;
        if (!card.Axes.Contains("EXHAUST_BURST")) return 1;

        // SECOND_WIND: non-attack cards in hand → 5 block each.
        // The exact filter is hard to detect from axes alone; default to
        // counting non-attack non-curse hand cards (SECOND_WIND pattern).
        int n = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, card)) continue;
            if (c.IsCurseOrStatus) continue;
            if (c.IsAttack) continue;   // SECOND_WIND filter
            n++;
        }
        return System.Math.Max(1, n);
    }

    /// <summary>
    /// v0.6.8 — Hand-state-aware bonus for EXHAUST_BURST Skills whose payoff
    /// isn't directly captured by Damage or Block scoring:
    ///
    ///   • EIDOLON  — exhaust all hand; if exhausted ≥ 9 → IntangiblePower 1
    ///                (1-turn invulnerability). Conditional on a near-max hand.
    ///   • STOKE    — exhaust hand, generate N random cards (N = exhausted).
    ///                Replacement value is roughly half average card score;
    ///                use a flat per-card estimate.
    ///   • PURITY   — exhaust up to 3 PLAYER-CHOSEN cards. Curses/status come
    ///                out first; otherwise modest deck thinning.
    ///
    /// Card-id-gated since each effect is bespoke. Other EXHAUST_BURST skills
    /// (SECOND_WIND block, etc.) are handled by EstimateBlockMultiplier.
    /// </summary>
    private static int EvaluateExhaustBurstSpecial(SimCard card, SimState state)
    {
        if (!card.IsSkill) return 0;
        if (!card.Axes.Contains("EXHAUST_BURST") && card.Id != "CARD.PURITY") return 0;

        switch (card.Id)
        {
            case "CARD.EIDOLON":
            {
                // Need ≥9 hand cards (including self) to fire Intangible.
                int hand = state.Hand.Count;
                const int threshold = 9;
                if (hand >= threshold)
                {
                    // Approximate IntangiblePower 1 self-buff value. PowerCatalog
                    // values it at ~1500 for permanent stacks; the EIDOLON version
                    // is single-turn (Apparition-like), so scale down.
                    return 900;
                }
                // Below threshold the card just exhausts the hand — heavy loss.
                int handExhausted = System.Math.Max(0, hand - 1);
                return -handExhausted * 60;
            }

            case "CARD.STOKE":
            {
                // Replaces hand with N random cards. Each generated card is worth
                // some fraction of an average draw — modest baseline. Net value
                // depends on what's exhausted: cheap throwaways → positive; high-
                // value retained cards → negative. Without per-hand-card scoring
                // here, use a small flat per-card bonus.
                int handExhausted = System.Math.Max(0, state.Hand.Count - 1);
                if (handExhausted == 0) return -100;          // no hand → no point
                return handExhausted * 40;                     // ~40pt per generated card
            }

            case "CARD.PURITY":
            {
                // Target-selectable exhaust up to 3. Player picks curses / status
                // first, then dead-weight cards. Score per curse/status in hand.
                int curseCount = 0;
                for (int i = 0; i < state.Hand.Count; i++)
                {
                    var c = state.Hand[i];
                    if (ReferenceEquals(c, card)) continue;
                    if (c.IsCurseOrStatus) curseCount++;
                }
                int effective = System.Math.Min(curseCount, 3);
                if (effective > 0) return effective * 220;     // remove curse → big payoff
                // No curse — pure deck thin. Minor positive (retains card, costs 0).
                return 40;
            }

            // EIDOLON / STOKE / PURITY only — other EXHAUST_BURST skills (SECOND_WIND)
            // are covered by EstimateBlockMultiplier above.
            default:
                return 0;
        }
    }

    /// <summary>
    /// v0.7.0 — `P(X ≥ k)` for `X ~ Binomial(n, p)`. Used by the random
    /// multi-hit scorer to estimate "this card kills enemy E" probability:
    /// `BinomialAtLeast(totalHits, 1/aliveEnemies, hitsNeededToKill)`.
    ///
    /// Returns 1.0 when `k ≤ 0` (no hits needed, certain) and 0.0 when `k > n`
    /// (impossible). Hit counts in STS2 attacks are small (most cards ≤ 6
    /// hits), so the direct CDF evaluation is fast and exact.
    /// </summary>
    private static double BinomialAtLeast(int n, double p, int k)
    {
        if (k <= 0) return 1.0;
        if (k > n) return 0.0;
        if (p <= 0.0) return 0.0;
        if (p >= 1.0) return k <= n ? 1.0 : 0.0;
        double q = 1.0 - p;
        double cumulative = 0.0;
        // Use C(n,i) * p^i * q^(n-i) directly. Hit counts are tiny (≤ ~10),
        // overflow not a concern.
        for (int i = k; i <= n; i++)
        {
            cumulative += BinomialCoefficient(n, i) * System.Math.Pow(p, i) * System.Math.Pow(q, n - i);
        }
        return cumulative;
    }

    private static long BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n - k) k = n - k;
        long result = 1;
        for (int i = 0; i < k; i++)
        {
            result = result * (n - i) / (i + 1);
        }
        return result;
    }

    private static bool IsSelfTargetedTarget(TargetType t)
        => t == TargetType.Self || t == TargetType.AnyAlly
        || t == TargetType.AnyPlayer || t == TargetType.AllAllies;

    /// <summary>
    /// v0.6.2 — Expected-cost penalty for fetch / discover cards when the
    /// draw and discard piles contain Curse / Status pollution. The pulled
    /// card is unknown until SelectorMode resolves it at runtime, so the
    /// anticipatory score should discount by the probability the pull
    /// returns junk. 0 if not a fetch card, if piles are empty, or if there
    /// is no pollution.
    ///
    /// Penalty model: pollution_prob × FetchPollutionExpectedCost. The
    /// expected cost roughly represents the gap between "best card pulled"
    /// (which the planner already credited) and "junk card pulled" (near
    /// zero or negative value).
    /// </summary>
    private static int EvaluateFetchPollution(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (!card.IsFetchTrigger) return 0;
        int total = state.DrawPile.Count + state.DiscardPile.Count;
        if (total == 0) return 0;

        int junk = 0;
        for (int i = 0; i < state.DrawPile.Count; i++)
            if (state.DrawPile[i].IsCurseOrStatus) junk++;
        for (int i = 0; i < state.DiscardPile.Count; i++)
            if (state.DiscardPile[i].IsCurseOrStatus) junk++;

        if (junk == 0) return 0;
        double p = (double)junk / total;
        return -(int)(p * w.FetchPollutionExpectedCost);
    }

    /// <summary>
    /// v0.6.2 — Energy monopoly opportunity-cost penalty. Fires only when
    /// the current card's cost consumes the *entire* remaining energy AND
    /// the hand contains other meaningful playable cards that would have
    /// fit alongside a cheaper alternative. Conservative magnitude — meant
    /// to break ties against multi-card alternatives, not override raw
    /// damage / threat-bonus decisions.
    /// </summary>
    private static int EvaluateEnergyMonopoly(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (card.Cost <= 0) return 0;
        int afterPlay = state.PlayerEnergy - card.Cost;
        if (afterPlay > 0) return 0;

        int skipped = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, card)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            if (c.Cost < 0 || c.Cost > state.PlayerEnergy) continue;
            skipped++;
        }
        if (skipped == 0) return 0;

        int penalty = -System.Math.Min(w.EnergyMonopolyPenaltyCap,
            skipped * w.EnergyMonopolyPenaltyPerSkipped);
        return penalty;
    }

    /// <summary>
    /// v0.6 — Lethal-this-turn detection. Greedy-pick playable attacks in
    /// damage-per-energy order, apply per-enemy Vulnerable / Weak self /
    /// damage caps, sum the projected damage, and return true if it covers
    /// every alive enemy's HP. Used to deprioritise non-Attack cards on the
    /// closing turn of a fight.
    ///
    /// Limitations (intentional simplifications, biased toward false-NEGATIVE):
    ///   • Single-target attacks use the most-Vulnerable alive enemy for
    ///     damage estimation (over-counts when actual best target is lower
    ///     HP but not Vuln). Conservative direction for the *boolean*
    ///     output — over-estimating damage gives false positives, which are
    ///     more dangerous than false negatives. We accept the rare false
    ///     positive in exchange for catching the common lethal case.
    ///   • Body Slam / Calculated* / Repeat-scaling attacks use stored
    ///     base damage, not the runtime-computed value. Lethal may go
    ///     undetected for these — false negative, safe.
    ///   • Strength-from-Setup-this-turn not modelled (we'd need to score
    ///     play order). Lethal detected at current Strength only.
    /// </summary>
    private static bool IsLethalThisTurn(SimState state)
    {
        int totalEnemyHp = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) totalEnemyHp += e.Hp;
        if (totalEnemyHp <= 0) return true;

        int energy = state.PlayerEnergy;
        bool playerWeak = state.PlayerWeak > 0;

        // Greedy damage-per-energy ordering. Cost 0 treated as cost 1 for
        // the ratio so free attacks rank by raw damage.
        var attacks = state.Hand
            .Where(c => c.IsAttack && c.IsPlayable
                        && c.Cost >= 0 && c.Cost <= energy)
            .OrderByDescending(c =>
                c.TotalDamage * 100 / System.Math.Max(1, c.Cost == 0 ? 1 : c.Cost))
            .ToList();

        int totalReachable = 0;
        foreach (var atk in attacks)
        {
            if (atk.Cost > energy) continue;
            energy -= atk.Cost;

            if (atk.Target == TargetType.AllEnemies)
            {
                foreach (var e in state.Enemies)
                {
                    if (!e.IsAlive) continue;
                    int per = StatusMath.EffectiveAttackDmg(atk.Damage,
                        state.PlayerStrength, e.VulnerableAmount > 0, playerWeak);
                    if (e.DamageCapPerHit > 0 && per > e.DamageCapPerHit)
                        per = e.DamageCapPerHit;
                    int eachTotal = per * System.Math.Max(1, atk.Hits);
                    if (e.HardenedShellRemaining > 0
                        && eachTotal > e.HardenedShellRemaining)
                        eachTotal = e.HardenedShellRemaining;
                    totalReachable += eachTotal;
                }
            }
            else
            {
                // Pick the most-Vulnerable alive enemy for damage estimation.
                SimEnemy? bestEnemy = null;
                foreach (var e in state.Enemies)
                {
                    if (!e.IsAlive) continue;
                    if (bestEnemy == null
                        || (e.VulnerableAmount > 0 && bestEnemy.VulnerableAmount == 0))
                        bestEnemy = e;
                }
                if (bestEnemy == null) continue;
                int per = StatusMath.EffectiveAttackDmg(atk.Damage,
                    state.PlayerStrength, bestEnemy.VulnerableAmount > 0, playerWeak);
                if (bestEnemy.DamageCapPerHit > 0 && per > bestEnemy.DamageCapPerHit)
                    per = bestEnemy.DamageCapPerHit;
                int eachTotal = per * System.Math.Max(1, atk.Hits);
                totalReachable += eachTotal;
            }
        }

        return totalReachable >= totalEnemyHp;
    }

    private static (int bonus, string details) ScoreAttackTarget(
        SimCard card, int targetIdx, SimState state, PlanScorerWeights w, int effectiveDamage)
    {
        if (targetIdx < 0 || targetIdx >= state.Enemies.Count) return (0, "");
        var target = state.Enemies[targetIdx];
        if (!target.IsAlive) return (-100000, "dead");

        // v0.4 — inert enemies (asleep/stunned) are still valid targets. Hitting them now is
        // strictly better than waiting for them to wake — every chip is free damage that
        // bypasses the turn they'd otherwise spend hitting us. Earlier −1500 penalty was
        // over-applied and pushed Vakuu into defend-only loops vs sleeping bosses.

        // v0.5 — DoT-lethal short-circuit. Target dies to its own Poison + Constrict
        // tick at start of next turn, before any intent fires. Skip ALL intent /
        // state bonuses (buff-stop / heal-deny / etc.) — none of those triggers can
        // land if the enemy is dead by the time their turn starts. Heavy flat penalty
        // so live enemies always win target priority when one exists. Burn is
        // intentionally excluded since its tick timing isn't universal.
        int preTurnDot = target.PoisonAmount + target.ConstrictAmount;
        if (preTurnDot > 0 && preTurnDot >= target.Hp)
            return (w.PoisonLethalPenalty, $"tgt:dotLethal{w.PoisonLethalPenalty}");

        int s = 0;
        var parts = new List<string>();
        if (target.HasBuffIntent) { s += w.BuffEnemyKillBonus; parts.Add($"buff+{w.BuffEnemyKillBonus}"); }
        if (target.HasHealIntent) { s += w.HealEnemyKillBonus; parts.Add($"heal+{w.HealEnemyKillBonus}"); }
        if (target.HasSummonIntent) { s += w.SummonEnemyKillBonus; parts.Add($"summon+{w.SummonEnemyKillBonus}"); }
        if (target.HasDeathBlowIntent) { s += w.DeathBlowEnemyKillBonus; parts.Add($"deathblow+{w.DeathBlowEnemyKillBonus}"); }
        if (target.HasDefendIntent) { s += w.DefendEnemyAttackPenalty; parts.Add($"defend{w.DefendEnemyAttackPenalty}"); }

        // v0.2.9 — enemy current state bonuses.
        if (target.VulnerableAmount > 0) { s += w.VulnerableTargetBonus; parts.Add($"vuln+{w.VulnerableTargetBonus}"); }
        if (target.StrengthAmount > 0) { s += w.StrengthTargetBonus; parts.Add($"str+{w.StrengthTargetBonus}"); }
        if (target.FrailAmount > 0) { s += w.FrailTargetBonus; parts.Add($"frail+{w.FrailTargetBonus}"); }
        if (target.HasTurnStartStrengthBuff)
        {
            s += w.TurnStartBuffTargetBonus;
            parts.Add($"ritual+{w.TurnStartBuffTargetBonus}");
        }

        // v0.2.11 — heavy DoT overkill: target already dying to poison this/next turn.
        // Poison-lethal case (PoisonAmount ≥ Hp) is handled by the early-return at the
        // top of this method, so here we only apply the milder warning for partial DoT.
        int dotDamage = target.PoisonAmount + target.ConstrictAmount + target.BurnAmount;
        if (dotDamage > 0 && dotDamage >= target.Hp / 2)
        {
            s += w.HeavyDotPenalty;
            parts.Add($"dotOverkill{w.HeavyDotPenalty}");
        }

        // v0.2.4 — lethal check uses effective damage (Strength + Vulnerable applied).
        if (effectiveDamage >= target.EffectiveHp)
        {
            s += w.RealLethalKillBonus;
            parts.Add($"LETHAL+{w.RealLethalKillBonus}");
        }
        else
        {
            // v0.4 — "Range" lethal bonuses only fire when the hand can ACTUALLY finish the
            // enemy this turn. A small enemy that we can't kill (no Strength, weak attacks)
            // should not award lethal-range bonuses just because its HP number is low — that
            // was inflating attack scores past Defend in survival-mode situations.
            int handAttackDmg = 0;
            bool playerWeakForCalc = state.PlayerWeak > 0;
            int energyForCalc = state.PlayerEnergy;
            // v0.5 — track HardenedShell budget across the projected attack chain so
            // multi-card lethal-range estimates don't double-spend the shell. The
            // budget is a per-turn total: each card chips into a shared pool, and a
            // depleted pool zeros out subsequent attacks.
            int shellBudget = target.HardenedShellRemaining;
            bool hasShell = shellBudget > 0 || target.Powers.ContainsKey("HardenedShellPower");
            // Greedy: sum effective damage of cheap-enough attack cards in hand,
            // each capped by per-hit Intangible and the running shell budget.
            foreach (var c in state.Hand.OrderBy(x => x.Cost))
            {
                if (!c.IsPlayable || !c.IsAttack || c.Cost > energyForCalc) continue;
                int perHit = StatusMath.EffectivePerHitCapped(
                    c.Damage, state.PlayerStrength, target, playerWeakForCalc);
                int cardTotal = perHit * System.Math.Max(1, c.Hits);
                if (hasShell)
                {
                    if (cardTotal > shellBudget) cardTotal = shellBudget;
                    shellBudget = System.Math.Max(0, shellBudget - cardTotal);
                }
                handAttackDmg += cardTotal;
                energyForCalc -= c.Cost;
                if (energyForCalc <= 0) break;
            }
            bool canKillThisTurn = handAttackDmg >= target.EffectiveHp;

            int eh = target.EffectiveHp;
            int rangeBonus = 0;
            string tag = "";
            if (eh <= 6) { rangeBonus = w.LethalRangeNearBonus; tag = "lethalNear"; }
            else if (eh <= 12) { rangeBonus = w.LethalRangeMidBonus; tag = "lethalMid"; }
            else if (eh <= 20) { rangeBonus = w.LethalRangeFarBonus; tag = "lethalFar"; }

            if (rangeBonus != 0)
            {
                if (!canKillThisTurn)
                {
                    rangeBonus /= 4; // 75% suppression — chipping at a corpse we can't finish
                    tag += "(noKill)";
                }
                s += rangeBonus;
                parts.Add($"{tag}+{rangeBonus}");
            }
        }

        if (target.IsMinion) { s += w.MinionFirstBonus; parts.Add($"minion+{w.MinionFirstBonus}"); }
        if (target.IsBoss && EnemyTurnSimulator.AnyMinionAlive(state))
        {
            s += w.BossDeferPenalty;
            parts.Add($"bossDefer{w.BossDeferPenalty}");
        }

        int hpProtection = target.Hp + target.Block;
        int low = w.LowHpTargetBonus / System.Math.Max(1, hpProtection / 5);
        if (low > 0) { s += low; parts.Add($"lowHp+{low}"); }

        return (s, parts.Count > 0 ? "tgt:" + string.Join("+", parts) : "");
    }

    /// <summary>
    /// v0.2.6 — Power-card fight-length context. Short fight → Powers don't pay back; long
    /// fight → scaling is huge. Uses total alive enemy HP+Block as the proxy.
    /// </summary>
    /// <summary>
    /// v0.4 — Orb-aware score component. Adds:
    ///   • Evoke value of the front orb × EvokeCount (Dualcast/Quadcast/MultiCast all
    ///     re-evoke the same front orb)
    ///   • Passive value of the channelled orb × ChannelCount (over estimated turns)
    ///   • Auto-evoke of the head when channelling into a full slot
    /// </summary>
    private static (int bonus, string detail) EvaluateOrbEffects(SimCard card, SimState state)
    {
        if (card.EvokeCount == 0 && card.ChannelCount == 0) return (0, "");
        int aliveEnemies = state.Enemies.Count(e => e.IsAlive);
        int total = 0;
        var parts = new List<string>();

        if (card.EvokeCount > 0 && state.OrbQueue.Count > 0)
        {
            var head = state.OrbQueue[0];
            int darkAcc = state.OrbEvokeValues.Count > 0 ? state.OrbEvokeValues[0] : 6;
            int perEvoke = OrbValueCatalog.EvokeValue(head, aliveEnemies, darkAcc, state.PlayerFocus);
            int evokeTotal = perEvoke * card.EvokeCount;
            total += evokeTotal;
            parts.Add($"evoke({head.ShortTag()}×{card.EvokeCount})+{evokeTotal}");
        }

        if (card.ChannelCount > 0 && card.ChannelKind != OrbKind.Unknown
            && state.PlayerOrbCapacity > 0)
        {
            int perChannel = OrbValueCatalog.PassiveValue(card.ChannelKind, state, aliveEnemies);
            int channelTotal = perChannel * card.ChannelCount;
            total += channelTotal;
            parts.Add($"channel({card.ChannelKind.ShortTag()}×{card.ChannelCount})+{channelTotal}");

            // v0.5 — auto-evoke (kick) accounting. A channel triggers a kick iff the queue
            // is already at capacity at the moment of that channel. Multi-channel cards
            // (Glacier 2 Frost, ConsumingShadow 2 Dark, Refract 2 Glass) can FILL a partial
            // queue and then start kicking. Correct kick count = overflow:
            //   kicks = max(0, initialQueueSize + ChannelCount − Capacity)
            // The previous formulation gated kicks behind "queue already at cap", missing
            // the partial-queue case (e.g., Defect at 2/3 channels 2 → 1 kick on channel #2).
            int kicks = System.Math.Max(0,
                state.OrbQueue.Count + card.ChannelCount - state.PlayerOrbCapacity);
            if (kicks > 0)
            {
                int kickedTotal = 0;
                for (int kickIdx = 0; kickIdx < kicks && kickIdx < state.OrbQueue.Count; kickIdx++)
                {
                    var kicked = state.OrbQueue[kickIdx];
                    int kickedVal = kickIdx < state.OrbEvokeValues.Count
                        ? state.OrbEvokeValues[kickIdx] : 6;
                    kickedTotal += OrbValueCatalog.EvokeValue(kicked, aliveEnemies, kickedVal, state.PlayerFocus);
                }
                if (kickedTotal != 0)
                {
                    total += kickedTotal;
                    parts.Add($"kicks×{kicks}+{kickedTotal}");
                }
            }
        }

        return (total, string.Join(",", parts));
    }

    private static int EvaluatePowerFightContext(SimState state, PlanScorerWeights w)
    {
        int totalHp = EnemyTurnSimulator.TotalAliveEnemyHp(state);
        if (totalHp <= w.ShortFightHpThreshold) return w.PowerShortFightPenalty;
        if (totalHp >= w.LongFightHpThreshold) return w.PowerLongFightBonus;
        return 0;
    }

    private const int HandSizeCap = 10;

    /// <summary>
    /// v0.2.6 — Draw-card value. Drawing is valuable when the rest of the hand can't do
    /// much. We measure the BEST score among the other cards in the hand and the size of
    /// the draw pile (no point drawing from an empty pile).
    ///
    /// v0.2.9 — pile-aware: if DrawPileSize+DiscardPileSize == 0 → drawing is futile.
    ///
    /// v0.5 — only THIS-turn immediate draws (DrawCount > 0 via CardsVar) use the
    /// hand-quality logic. DrawCardPower and DrawCardsNextTurnPower are per-turn /
    /// next-turn buffs whose value is already in PowerCatalog (900/stack), so the
    /// hand-quality bonus would double-credit. Conservative scope avoids the risk
    /// of mis-categorising DrawCardPower as immediate when it's actually a per-turn
    /// buff.
    ///
    /// v0.5 — energy-after-draw + hand-cap checks:
    ///   • Playing a 1-cost draw with 1 energy leaves 0 energy. Unless a 0-cost or
    ///     energy-gain card is queued in hand to bridge, the drawn cards are
    ///     next-turn-only — score discounted or penalised by remaining-hand quality.
    ///   • Drawing past the 10-card hand cap wastes the overflow — penalty scales
    ///     with the wasted fraction.
    /// </summary>
    private static int EvaluateDrawCard(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (card.DrawCount <= 0) return 0;

        // v0.2.9 — pile guard: nothing to draw means no value.
        int totalPile = state.DrawPileSize + state.DiscardPileSize;
        if (totalPile == 0) return w.DrawEmptyPilePenalty;

        // Walk the rest of the hand once — capture best non-draw score AND any
        // chain-enabler counts (0-cost cards / energy-gain cards). One pass.
        int bestOtherScore = int.MinValue;
        int zeroCostOthers = 0;
        int energyGainOthers = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, card)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            if (c.Cost == 0) zeroCostOthers++;
            if (c.IsEnergyGainCard) energyGainOthers++;
            if (c.IsDrawCard) continue;
            int targetIdx = -1;
            if (c.Target == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy)
            {
                targetIdx = state.Enemies.FindIndex(e => e.IsAlive);
                if (targetIdx < 0) continue;
            }
            int s = Score(c, targetIdx, state, w);
            if (s > bestOtherScore) bestOtherScore = s;
        }

        if (bestOtherScore == int.MinValue) return w.DrawHandUselessBonus;

        int handBonus;
        if (bestOtherScore < w.HandUselessThreshold) handBonus = w.DrawHandUselessBonus;
        else if (bestOtherScore < w.HandWeakThreshold) handBonus = w.DrawHandWeakBonus;
        else if (bestOtherScore >= w.HandStrongThreshold) handBonus = w.DrawIdlePenalty;
        else handBonus = w.DrawNoCostBottleneckBonus;

        // v0.2.9 — small pile thinning: very small pile (<=2) reduces draw value
        // because there's little new info to fetch.
        if (totalPile <= 2) handBonus = handBonus / 2;

        // v0.5 — Energy-after-draw check. If the draw leaves 0 energy and nothing
        // in hand bridges (0-cost or energy-gain), the drawn cards are next-turn
        // only — heavily discount when a strong play is being skipped, fractional
        // value when the hand is weak (next-turn setup still has some worth).
        int energyAfter = state.PlayerEnergy - card.Cost + card.EnergyGain;
        bool canChainThisTurn = energyAfter > 0 || zeroCostOthers > 0 || energyGainOthers > 0;
        if (!canChainThisTurn)
        {
            if (bestOtherScore >= w.HandStrongThreshold)
                handBonus -= 800;
            else if (bestOtherScore >= w.HandWeakThreshold)
                handBonus -= 400;
            else
                handBonus = handBonus / 3;
        }

        // v0.5 — Hand-cap overflow: drawn cards over 10 are silently discarded.
        // Penalty proportional to the wasted fraction of the draw.
        if (card.DrawCount > 0)
        {
            int handAfterPlay = state.Hand.Count - 1;  // self consumed
            int wasted = (handAfterPlay + card.DrawCount) - HandSizeCap;
            if (wasted > 0)
            {
                int wastedFrac = System.Math.Min(100, (wasted * 100) / card.DrawCount);
                handBonus -= (System.Math.Abs(handBonus) * wastedFrac) / 100;
            }
        }

        return handBonus;
    }

    /// <summary>
    /// v0.2.6 — Conditional value for energy-gain cards. Urgent when current energy is
    /// low AND the rest of the hand has plays that can use the gained energy. Wasted when
    /// energy is already full and no cost-bottlenecked cards remain.
    /// </summary>
    private static int EvaluateEnergyGain(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (!card.IsEnergyGainCard) return 0;

        // v0.5 — only IMMEDIATE energy gain (EnergyVar via EnergyGain > 0)
        // is evaluated for "unlock waiting big cards" logic. EnergizedPower /
        // EnergyNextTurnPower variants are next-turn / per-turn powers whose
        // value PowerCatalog captures; folding them in here would double-credit
        // a card whose actual game effect is on subsequent turns.
        if (card.EnergyGain <= 0) return 0;
        int immediateGain = card.EnergyGain;

        int remainingEnergy = System.Math.Max(0, state.PlayerEnergy - card.Cost);

        var otherPlayable = state.Hand
            .Where(c => !ReferenceEquals(c, card) && c.IsPlayable
                       && c.Cost >= 0 && !c.IsCurseOrStatus)
            .ToList();
        if (otherPlayable.Count == 0) return -1500;

        // The gain is only meaningful if it *unlocks* a card that couldn't have been played
        // without it: cost > remainingEnergy (couldn't afford) AND cost ≤ remaining + gain
        // (now affordable). Cards cheap enough to already play, or still too expensive after
        // the gain, don't count toward valuation.
        int afterGain = remainingEnergy + immediateGain;
        int unlocked = otherPlayable.Count(c => c.Cost > remainingEnergy && c.Cost <= afterGain);
        if (unlocked == 0) return w.EnergyGainWastedPenalty;

        if (remainingEnergy <= 1) return w.EnergyGainUrgentBonus;
        return 0;
    }

    /// <summary>Compact power-name suffix for log readability (StrengthPower → Str).</summary>
    private static string Short(string powerName)
    {
        var idx = powerName.LastIndexOf("Power", System.StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return powerName;
        var stem = powerName.Substring(0, idx);
        return stem.Length <= 4 ? stem : stem.Substring(0, 4);
    }

    /// <summary>
    /// v0.7.23 — A "setup attack" is an Attack card whose primary value is
    /// applying a debuff for future-turn payoff (Bash/Sucker Punch/Clothesline
    /// etc.) rather than dealing damage now. Heuristic: card is Attack with
    /// VULN/WEAK/FRAIL_PRODUCER axis AND its dmg-per-energy is materially
    /// lower than a generic Strike-tier attack (dpe < 5.5 vs Strike's 6.0).
    /// Used by lethal-mode penalty so setup cards aren't preferred over
    /// simpler attacks when greedy damage already kills.
    /// </summary>
    private static bool IsSetupAttackCard(SimCard card)
    {
        if (!card.IsAttack || card.Cost <= 0) return false;
        if (card.Axes == null) return false;
        bool hasSetupAxis = card.Axes.Contains("VULN_PRODUCER")
                         || card.Axes.Contains("WEAK_PRODUCER")
                         || card.Axes.Contains("FRAIL_PRODUCER");
        if (!hasSetupAxis) return false;
        double dpe = card.TotalDamage / (double)card.Cost;
        return dpe < 5.5;
    }

    /// <summary>
    /// v0.7.24 — Powers whose value depends on the player landing additional
    /// attacks on the debuffed enemy in subsequent turns. Used by the future-
    /// attack-potential multiplier so an attack-poor deck doesn't over-value
    /// these debuffs.
    ///
    /// Excluded: Weak/Frail/ShacklingPotion/Dampen/EnfeeblingTouch (enemy-
    /// action dependent, not our attacks), Poison/Constrict/Rupture (DoT —
    /// triggers without our attacks), Hex/Confused/PiercingWail (other paths).
    /// </summary>
    private static bool IsAttackDependentDebuff(string powerName)
    {
        return powerName == "VulnerablePower"
            || powerName == "DarkShacklesPower";  // -Str only this turn — still
                                                  // requires enemy hits us OR our
                                                  // attack vs them, scale loosely
    }

    /// <summary>
    /// v0.7.33 — Survival urgency accounting for the card's self-damage. When a
    /// card with HpLossAmount > 0 is being scored, the effective post-play HP
    /// is reduced, which can promote the situation from Moderate to Heavy or
    /// from Heavy to Fatal. Pure observation of current state — no future-sim.
    /// </summary>
    private static SurvivalUrgency GetEffectiveUrgency(SimState state, int extraHpLoss)
    {
        if (extraHpLoss <= 0)
            return EnemyTurnSimulator.GetSurvivalUrgency(state);

        if (state.PlayerHp <= 0) return SurvivalUrgency.None;
        if (EnemyTurnSimulator.AllInert(state)) return SurvivalUrgency.None;

        int effHp = System.Math.Max(0, state.PlayerHp - extraHpLoss);
        if (effHp <= 0) return SurvivalUrgency.Fatal;  // card itself kills us

        int leak = EnemyTurnSimulator.PredictPlayerDmg(state);
        if (leak <= 0) return SurvivalUrgency.None;
        if (leak >= effHp) return SurvivalUrgency.Fatal;
        double ratio = leak / (double)effHp;
        if (ratio >= 0.5) return SurvivalUrgency.Heavy;
        if (ratio >= 0.2) return SurvivalUrgency.Moderate;
        return SurvivalUrgency.None;
    }

    /// <summary>
    /// v0.7.33 — Heavy penalty for cards whose self-damage would self-kill or
    /// turn a survivable turn fatal. Returns 0 when the card is HpLoss-free.
    /// Doesn't apply when the card delivers a real-lethal kill this turn (the
    /// kill bonus dwarfs and the combat ends before HP can matter).
    /// </summary>
    private static int ComputeSelfDamagePenalty(SimCard card, SimState state, bool lethalThisTurn)
        => ComputeSelfDamagePenaltyWithThorns(card, state, lethalThisTurn, 0);

    /// <summary>
    /// v0.7.34 — Same as ComputeSelfDamagePenalty but also factors in raw
    /// thorns reflect HP (only relevant for Attack branch where target's
    /// ThornsAmount applies per hit). Skill / Power branches pass 0.
    /// </summary>
    private static int ComputeSelfDamagePenaltyWithThorns(SimCard card, SimState state, bool lethalThisTurn, int thornsDamage)
    {
        int hpLoss = card.HpLossAmount + System.Math.Max(0, thornsDamage);
        if (hpLoss <= 0) return 0;
        if (lethalThisTurn) return 0;  // we kill them first; HP loss doesn't matter

        // Self-kill: card alone reduces HP to ≤ 0.
        if (hpLoss >= state.PlayerHp) return -2000;

        var baseUrg = EnemyTurnSimulator.GetSurvivalUrgency(state);
        var effUrg = GetEffectiveUrgency(state, hpLoss);
        if (effUrg <= baseUrg) return 0;  // didn't worsen the situation

        // Urgency rose. Penalty scales with the jump.
        int jump = (int)effUrg - (int)baseUrg;
        return effUrg == SurvivalUrgency.Fatal ? -1000 * jump : -300 * jump;
    }

    /// <summary>
    /// v0.7.24 — Compute the future-attack-potential multiplier for attack-
    /// dependent debuffs. Counts attacks in (hand-without-self + draw +
    /// discard) and saturates at 0.3 ratio → 1.0 multiplier.
    ///
    /// 0% attacks → 0.0 mult (Vuln fully wasted — pure skill deck like
    ///   Hexaghost shutout).
    /// 15% attacks → 0.5 mult.
    /// 30%+ attacks → 1.0 mult (full debuff value).
    /// </summary>
    private static double ComputeFutureAttackMultiplier(SimState state, SimCard self)
    {
        int total = 0, attacks = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            total++;
            if (c.IsAttack) attacks++;
        }
        for (int i = 0; i < state.DrawPile.Count; i++)
        {
            total++;
            if (state.DrawPile[i].IsAttack) attacks++;
        }
        for (int i = 0; i < state.DiscardPile.Count; i++)
        {
            total++;
            if (state.DiscardPile[i].IsAttack) attacks++;
        }
        if (total <= 0) return 1.0;  // Snapshot incomplete — fail open.

        double ratio = attacks / (double)total;
        return System.Math.Min(1.0, ratio / 0.3);
    }

    /// <summary>
    /// v0.7.7 — Convert a card id (CARD.MAYHEM, CARD.DARK_EMBRACE,
    /// CARD.THE_SEALED_THRONE) into its canonical Power class name
    /// (MayhemPower, DarkEmbracePower, TheSealedThronePower). Used by the
    /// Power-branch fallback so cards whose Power application isn't visible
    /// via PowerVar at runtime still get a PowerCatalog credit.
    ///
    /// Returns empty string when the id doesn't follow the CARD.NAME format.
    /// </summary>
    private static string IdToPowerName(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return "";
        int dot = cardId.IndexOf('.');
        string body = dot >= 0 ? cardId.Substring(dot + 1) : cardId;
        if (string.IsNullOrEmpty(body)) return "";
        var sb = new System.Text.StringBuilder(body.Length + 5);
        bool capitalize = true;
        foreach (char c in body)
        {
            if (c == '_') { capitalize = true; continue; }
            if (capitalize) { sb.Append(char.ToUpperInvariant(c)); capitalize = false; }
            else { sb.Append(char.ToLowerInvariant(c)); }
        }
        sb.Append("Power");
        return sb.ToString();
    }

    /// <summary>
    /// v0.7.22 — Penalize S+/S Power cards whose activation conditions aren't
    /// met. PowerCatalog credits these at high absolute values (BarricadePower
    /// 1200, EchoFormPower 1500 etc.) regardless of board state. When the
    /// condition that makes the Power useful THIS turn / immediately is
    /// missing, the credit is misleading.
    ///
    /// Each branch returns a negative score adjustment per the listed
    /// condition. The penalties are conservative (small relative to PowerCatalog
    /// value) so the Power is still preferred when conditions are partially
    /// met; only the no-condition case takes a meaningful hit.
    /// </summary>
    private static int ComputePowerActivationPenalty(SimCard card, SimState state)
    {
        if (!card.IsPower) return 0;

        int penalty = 0;
        string idDerived = IdToPowerName(card.Id);
        var apps = card.PowerApps;

        // EchoFormPower / BurstPower: first N cards each turn play twice.
        // Wasted if no cards left to play after this Power is dropped.
        if (apps.ContainsKey("EchoFormPower") || idDerived == "EchoFormPower"
            || apps.ContainsKey("BurstPower")  || idDerived == "BurstPower")
        {
            int energyAfter = state.PlayerEnergy - System.Math.Max(0, card.Cost);
            int playablesAfter = 0;
            for (int i = 0; i < state.Hand.Count; i++)
            {
                var c = state.Hand[i];
                if (ReferenceEquals(c, card)) continue;
                if (c.IsCurseOrStatus || !c.IsPlayable) continue;
                if (c.Cost == 0 || c.Cost <= energyAfter) playablesAfter++;
            }
            if (playablesAfter == 0)
            {
                // No echoes this turn. First trigger deferred to next turn.
                penalty -= 400;
            }
        }

        // BarricadePower: block carries over. Wasted if no block this turn AND
        // no block-generating cards in hand to feed it.
        if (apps.ContainsKey("BarricadePower") || idDerived == "BarricadePower")
        {
            if (state.PlayerBlock == 0 && !HasBlockSourceInHand(state.Hand, card))
                penalty -= 200;
        }

        // MachineLearningPower: +1 card draw per turn. Hand-cap (10) waste.
        if (apps.ContainsKey("MachineLearningPower") || idDerived == "MachineLearningPower")
        {
            if (state.Hand.Count >= 10)
                penalty -= 250;
        }

        // CrueltyPower: +25% dmg vs Vuln targets. Wasted with no Vuln AND no
        // Vuln-producer in hand to set up.
        if (apps.ContainsKey("CrueltyPower") || idDerived == "CrueltyPower")
        {
            bool anyVuln = false;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                var e = state.Enemies[i];
                if (e.IsAlive && e.VulnerableAmount > 0) { anyVuln = true; break; }
            }
            if (!anyVuln && !HasVulnProducerInHand(state.Hand, card))
                penalty -= 200;
        }

        // TheSealedThronePower: Star per card play. Wasted if combat is over
        // (no more plays). Less of a per-card check — fightCtx mostly covers.

        return penalty;
    }

    private static bool HasBlockSourceInHand(System.Collections.Generic.IReadOnlyList<SimCard> hand, SimCard self)
    {
        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.Block > 0) return true;
            if (c.Axes != null && c.Axes.Contains("BLOCK")) return true;
        }
        return false;
    }

    private static bool HasVulnProducerInHand(System.Collections.Generic.IReadOnlyList<SimCard> hand, SimCard self)
    {
        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.Axes != null && c.Axes.Contains("VULN_PRODUCER")) return true;
            if (c.PowerApps != null && c.PowerApps.ContainsKey("VulnerablePower")) return true;
        }
        return false;
    }
}
