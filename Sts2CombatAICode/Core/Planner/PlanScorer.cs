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
    /// <summary>
    /// S5 후속: Enrage penalty magnitude per stack. Default 100 (matches
    /// historical value). Higher discourages Skill plays vs Enrage carriers
    /// like TestSubjectBoss. Tune via STS2_ENRAGE_PENALTY.
    /// </summary>
    public static int EnragePenaltyPerStack = ResolveEnragePenalty();
    private static int ResolveEnragePenalty()
    {
        var s = System.Environment.GetEnvironmentVariable("STS2_ENRAGE_PENALTY");
        if (int.TryParse(s, out var v) && v >= 0) return v;
        return 100;
    }

    /// <summary>
    /// 2026-06-03 — HP-preservation block bonus (per useful-block point), applied
    /// ONLY when the fight is already won (race == Winning). HP is a cross-combat
    /// resource: when victory is secure, blocking real incoming is "free" survived
    /// HP — the forgone chip attack wouldn't change the kill turn (the enemy lives
    /// either way). This is the user's distinction: push damage when killing sooner
    /// removes a future enemy turn (handled by kill/burst bonuses); block to
    /// preserve HP when it doesn't. Gated to Winning + usefulBlock>0 so it never
    /// blocks when we must race (Losing) and never over-blocks (leak-capped). The
    /// blind-block Fatal experiment failed precisely because it ignored this gate.
    /// Default 0 (OFF) — A/B via STS2_HP_PRESERVE.
    /// </summary>
    public static int HpPreservePerPoint = ResolveHpPreserve();
    private static int ResolveHpPreserve()
    {
        var s = System.Environment.GetEnvironmentVariable("STS2_HP_PRESERVE");
        if (int.TryParse(s, out var v) && v >= 0) return v;
        return 0;
    }

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
            {
                int perAlt = w.RetainDeferPenaltyPerAlternative;
                // v0.9 — Retain attack cards lose value if deferred while
                // this-turn debuffs are active on the target. Vulnerable /
                // Weak decay by 1 at end of player turn, so saving a Retain
                // attack for "later" forfeits the ×1.5 / -25% bonuses now
                // baked into this card's expected damage. Halve the defer
                // urge when any alive enemy carries Vuln OR Weak.
                //
                // Conditions kept narrow: attack cards only (Retain skills
                // like Apparition still defer normally — their value isn't
                // tied to ephemeral target debuffs); ANY alive enemy with the
                // debuff is enough (single-target picker chooses the right
                // one downstream).
                if (card.IsAttack)
                {
                    // v0.9 — high-damage Retain attacks (d≥12, e.g. SOVEREIGN_BLADE
                    // d=21..36) are the deck's finisher. The Retain keyword is meant
                    // to "save the big play for the right moment", but the defer
                    // penalty does the exact OPPOSITE by pushing the card past every
                    // cheap alternative until it falls out of energy reach this
                    // turn. Observed pattern (2026-05-19 19:37 log): SB stayed in
                    // hand turn-after-turn at |R then |RX (unplayable) because
                    // BEAT/DEFEND always out-scored it. Removing the defer penalty
                    // for big-attack Retain lets it compete on its own merit
                    // (raw damage + Vuln + buff target bonuses).
                    if (card.Damage >= 12)
                    {
                        perAlt = 0;
                    }
                    else
                    {
                        bool anyVulnOrWeak = false;
                        for (int i = 0; i < state.Enemies.Count; i++)
                        {
                            var e = state.Enemies[i];
                            if (!e.IsAlive) continue;
                            if (e.VulnerableAmount > 0 || e.WeakAmount > 0)
                            {
                                anyVulnOrWeak = true;
                                break;
                            }
                        }
                        if (anyVulnOrWeak)
                            perAlt /= 2;       // halve defer penalty when debuffs would expire
                    }
                }
                delta -= perAlt * otherPlayable;
            }
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

    /// <summary>
    /// v0.9.5 — FRANTIC_ESCAPE (The Insatiable) urgency scorer. OnPlay does
    /// <c>SandpitPower.Amount += 1</c> against the carrier targeting the
    /// owner — i.e. buys exactly one extra enemy turn before the instakill
    /// trigger fires (decompile <c>sts2.decompiled.cs:351909</c>).
    /// EnergyCost escalates by 1 per play via <c>AddThisCombat(1)</c>, so
    /// repeated use becomes prohibitively expensive; play it only when
    /// urgency justifies the cost. The card's live SimCard.Cost already
    /// reflects accumulated escalation, so no extra tracking is needed here.
    ///
    /// Score scales inverse to the most-urgent SandpitAmount across alive
    /// enemies. When no SandpitPower is active the card is dead-weight that
    /// also dirties the EnergyCost ledger if played — small negative.
    /// </summary>
    private static ScoreBreakdown BreakdownFranticEscape(SimCard card, SimState state)
    {
        int minStk = int.MaxValue;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) continue;
            if (e.SandpitAmount > 0 && e.SandpitAmount < minStk)
                minStk = e.SandpitAmount;
        }
        if (minStk == int.MaxValue)
        {
            return new ScoreBreakdown(-200, "Status-FranticEscape-Inert",
                -200, 0, 0, 0, "no-sandpit-active");
        }
        int score = minStk switch
        {
            1   => 4500,
            2   => 3000,
            3   => 1500,
            _   => 500,
        };
        return new ScoreBreakdown(score, "Status-FranticEscape", score, 0, 0, 0,
            $"sandpit-deadline(stk={minStk}→{minStk + 1})");
    }

    // v0.9.7 — CalcDmgProbe dedup set. Logged once per (id, previewDamage,
    // counter) triplet so identical scoring states don't flood the log.
    // Reset would require restarting the mod — fine for in-combat diagnosis.
    private static readonly System.Collections.Generic.HashSet<string> _calcDmgProbeLogged
        = new(System.StringComparer.OrdinalIgnoreCase);

    private static void LogCalcDmgProbeOnce(string id, int previewDamage, int counter)
    {
        string key = $"{id}:d={previewDamage}:c={counter}";
        lock (_calcDmgProbeLogged)
        {
            if (!_calcDmgProbeLogged.Add(key)) return;
        }
        try
        {
            MainFile.Logger?.Info(
                $"[CalcDmgProbe] {id} previewDamage={previewDamage} counter={counter}");
        }
        catch { /* MainFile.Logger may not be initialised in test contexts */ }
    }

    private static ScoreBreakdown BreakdownInternal(SimCard card, int targetIdx, SimState state, PlanScorerWeights w)
    {
        if (card.IsCurseOrStatus)
        {
            // v0.9.5 — FRANTIC_ESCAPE (The Insatiable's deadline-extension card,
            // Status cost 1, OnPlay → SandpitPower.Amount +1, escalates own cost
            // by EnergyCost.AddThisCombat(1) on each play). Special handling so
            // the AI plays it precisely when SandpitPower carriers are close to
            // expiring, not as a generic dead-weight status.
            if (card.Id == "FRANTIC_ESCAPE")
                return BreakdownFranticEscape(card, state);

            // v0.9.8 — Status / Curse cards normally carry CardKeyword.Unplayable
            // (Wound / Dazed / Burn / Void / Injury all explicitly set it via
            // CanonicalKeywords — including Burn, which is *not* playable; its
            // self-damage fires from OnTurnEndInHand, not OnPlay). Game CanPlay()
            // returns false for those, so candidate enumeration filters them
            // out before reaching here.
            //
            // FRANTIC_ESCAPE is the only known playable Status — handled above as
            // a special case. The branch below remains as a guard for any future
            // playable Status / Curse card discovered later. Small positive so
            // it ranks above MinPlayScore but never above a real card.
            if (card.IsPlayable)
                return new ScoreBreakdown(200, "Status-Playable", 200, 0, 0, 0, "playable-status-fallback");
            return new ScoreBreakdown(w.CursePenalty, "Curse", w.CursePenalty, 0, 0, 0, "never-play");
        }

        // v0.9.7 — CalcDmgProbe: 4 CalculatedDamageVar 카드의 preview damage 와
        // SimState 의 derived counter 를 단발 로그로 출력. game-play 검증 시
        // mismatch 없으면 PreviewValue 신뢰 가능; 차이가 보이면 다음 release 에서
        // fallback override 결정. SQUEEZE 의 counter 는 자기 자신 제외.
        if (card.Id is "CONFLAGRATION" or "DEATH_MARCH" or "CRESCENT_SPEAR" or "SQUEEZE")
        {
            int counter = card.Id switch
            {
                "CONFLAGRATION"  => state.TurnAttacksPlayed,
                "DEATH_MARCH"    => state.TurnCardsDrawn,
                "CRESCENT_SPEAR" => state.PlayerStarCostCardCount,
                "SQUEEZE"        => System.Math.Max(0, state.PlayerOstyAttackCardCount - 1),
                _                => 0,
            };
            LogCalcDmgProbeOnce(card.Id, card.Damage, counter);
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

        // v0.23 Phase 5b — Per-enemy burst-window. When lethalThisTurn is
        // false but the chain can still finish ≥ 1 enemy this turn, the
        // 1-step picker should still favor that burst sequence over scaling
        // Powers. Skipped when lethalThisTurn is true (LethalModeNonAttackPenalty
        // already enforces "attack only" mode there).
        System.Collections.Generic.HashSet<int> burstKillable =
            lethalThisTurn ? new() : FindBurstKillableEnemies(state);
        bool hasBurstWindow = burstKillable.Count > 0;

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
        // B-2: throughput is needed early so CombatContext's Boss
        // attrition branch can gate on the weak-deck flag. The same
        // throughput value feeds DeckThroughput.CoreCardBonusFor below
        // without a second compute.
        var throughput = DeckThroughput.Compute(state);
        int ctxBonus = CombatContext.ContextBonus(card, combatProfile, throughput);
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
        // (throughput computed above for the B-2 weak-deck gate)
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
        // v0.7.60 — Card role classification + phase coherence. Tag the card
        // as Carry/Setup/Support/Defensive/Cycler/Tech/Filler and add a
        // coherence bonus when the role fits the current plan stage.
        var role = CardRole.Classify(card);
        int roleBonus = CardRole.CoherenceBonus(role, planStage);
        if (roleBonus != 0)
        {
            comboBonus += roleBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"role({role}){roleBonus:+0;-0}"
                : $"{comboDetail},role({role}){roleBonus:+0;-0}";
        }
        // v0.7.61 — Finisher recognition. Identify top-3 cards in deck by
        // estimated effective damage (state-aware). Bonus / penalty depends
        // on whether NOW is the time to deploy the finisher (Cleanup/Burst
        // = yes, Setup/Opening = hold).
        var finishers = FinisherIdentifier.Identify(state);
        int finisherBonus = FinisherIdentifier.FinisherBonus(card, finishers, planStage, raceProj);
        if (finisherBonus != 0)
        {
            comboBonus += finisherBonus;
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"finisher{finisherBonus:+0;-0}"
                : $"{comboDetail},finisher{finisherBonus:+0;-0}";
        }
        // v0.7.63 — Variance penalty in critical situations. High-variance
        // cards (random gen, random target) lose tiebreaks when the race
        // is tight or stage is Cleanup — prefer reliability.
        // v0.7.64 — RANDOM-target attacks collapse to None on single-enemy
        // encounters (all hits land on the same target).
        int variancePenalty = CardVariance.ReliabilityPenalty(card, state, raceProj, planStage);
        if (variancePenalty != 0)
        {
            comboBonus += variancePenalty;
            var level = CardVariance.Classify(card, state);
            comboDetail = string.IsNullOrEmpty(comboDetail)
                ? $"variance({level}){variancePenalty:+0;-0}"
                : $"{comboDetail},variance({level}){variancePenalty:+0;-0}";
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
            // v0.10 — 0-cost Power priority. Upgraded scaling powers
            // (AUTOMATION+ : cost 1→0, AfterCardDrawn counter) don't compete
            // with attacks/defense for energy, AND their cumulative effect
            // strictly benefits from earlier deployment (every card drawn
            // after deploy ticks the counter). Without this bonus, the
            // dmg-based attack scores dominated and 0-cost powers slipped
            // to the last step of the turn (observed 22:29 log Turn 3:
            // AUTOMATION+ played at step 4 of 5, missing 3 trigger draws
            // this turn alone). The bonus is suppressed in lethal mode —
            // when we can kill this turn, the carryover never materializes.
            int freePlayBonus = 0;
            if (cost == 0 && !lethalThisTurn)
            {
                freePlayBonus = w.FreePlay0CostPowerBonus;
                if (freePlayBonus != 0) details.Add($"freePlay0Cost={freePlayBonus}");
            }

            // v0.10 — Galvanic HP-cost. Playing a Galvanized Power card
            // deals state.GalvanicAmount damage to the player (block-
            // absorbed, ValueProp.Unpowered — decompile :314942). Subtract
            // the post-block leak as score penalty at -100/HP. Suppressed
            // in lethal mode (no carryover need: enemies die before another
            // galvanic trigger could matter). Stacks with thorns-style
            // self-damage penalties when both fire.
            int galvanicPenalty = 0;
            if (card.IsGalvanized && state.GalvanicAmount > 0 && !lethalThisTurn)
            {
                int absorbed = System.Math.Min(state.GalvanicAmount, state.PlayerBlock);
                int hpLeak = state.GalvanicAmount - absorbed;
                if (hpLeak > 0)
                {
                    galvanicPenalty = -hpLeak * w.GalvanicPenaltyPerLeakHp;
                    details.Add($"galvanic(amt{state.GalvanicAmount},leak{hpLeak})={galvanicPenalty}");
                }
            }

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

            // v0.23 Phase 5b — Burst-window defer. A burst window means the
            // hand can finish at least one enemy this turn via the attack
            // chain. Cost ≥ 2 Powers claim energy that the chain needs;
            // defer them. 0-cost / 1-cost Powers exempt (Inflame T1 = cost 1
            // is the classic 'open-with-Power' play and doesn't compete with
            // a 3-energy burst). Only fires when not already in lethal mode.
            int burstDefer = 0;
            if (hasBurstWindow && cost >= 2)
            {
                burstDefer = w.BurstChainPowerDeferPenalty;
                details.Add($"burstDeferPower={burstDefer}");
            }

            // v0.23 Phase 7 — HP-pressure penalty. Future-payoff Powers
            // (Barricade, Demon Form, Inflame on cost-2 upgrades) need the
            // player to live multiple more turns to break even. Below
            // HpPressurePowerThreshold, the player is one bad turn from
            // dying; spend energy on damage / block now, not on next-turn
            // carryover. Fires independently of burst-window — caps the
            // common "no kill this turn but HP critical" gap. Suppressed
            // in lethal mode (already attack-only via LethalModeNonAttackPenalty).
            int hpPressurePenalty = 0;
            if (!lethalThisTurn && cost >= 2
                && state.PlayerHp <= w.HpPressurePowerThreshold)
            {
                hpPressurePenalty = w.HpPressurePowerPenalty;
                details.Add($"hpPressurePower(hp{state.PlayerHp})={hpPressurePenalty}");

                // v0.23 Phase 8b — slow-attrition compounding penalty.
                // When any alive enemy caps incoming damage (HardToKill /
                // Intangible), the player's per-turn dealt is also bounded,
                // so a future-payoff Power has even fewer remaining turns to
                // amortise. Stack a small extra penalty on top of the
                // HpPressurePower base. Fires only when both conditions hold,
                // so non-cap fights and full-HP fights are unaffected.
                bool slowAttritionFight = false;
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var e = state.Enemies[i];
                    if (e.IsAlive && e.DamageCapPerHit > 0) { slowAttritionFight = true; break; }
                }
                if (slowAttritionFight)
                {
                    hpPressurePenalty += w.SlowAttritionPowerExtraPenalty;
                    details.Add($"slowAttrition={w.SlowAttritionPowerExtraPenalty}");
                }
            }

            // Sleeping-enemy Power bonus. AsleepPower (Lagavulin) and
            // SlumberPower (Slumbering Beetle) give the player free
            // turns — setup buffs accrue without retaliation. PlanScorer's
            // single-step view already rates Barricade / Demon Form high,
            // but the depth-N beam's 2-step lookahead under-weighs the
            // multi-turn carry (block carry, +Strength every turn) because
            // sleep phases run 5+ turns past the horizon. Explicit bonus
            // surfaces the future value at scoring time. Suppressed in
            // lethal mode (already attack-only via LethalModeNonAttackPenalty).
            int sleepingEnemyBonus = 0;
            if (!lethalThisTurn)
            {
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var e = state.Enemies[i];
                    if (!e.IsAlive) continue;
                    bool asleep = (e.Powers.TryGetValue("AsleepPower", out var ap) && ap > 0)
                               || (e.Powers.TryGetValue("SlumberPower", out var sp) && sp > 0);
                    if (asleep)
                    {
                        sleepingEnemyBonus = w.SleepingEnemyPowerBonus;
                        details.Add($"sleepingEnemy={sleepingEnemyBonus}");
                        break;
                    }
                }
            }

            // v0.7.33 — Self-damage penalty (Power cards rarely carry HP loss,
            // but DOOM_SELF Powers and a few Necrobinder Powers do).
            int selfDmgPowerPenalty = ComputeSelfDamagePenalty(card, state, lethalThisTurn);
            if (selfDmgPowerPenalty != 0)
                details.Add($"selfDmg={selfDmgPowerPenalty}");

            if (fetchPollutionPenalty != 0) details.Add($"fetchPoll={fetchPollutionPenalty}");
            if (comboBonus != 0) details.Add(comboDetail);
            if (monopolyPenalty != 0) details.Add($"energyMono={monopolyPenalty}");

            // v0.10 — Per-card relic bonus (PenNib×2, IronClub+draw, etc.).
            // Most relic effects on Power cards come via IronClub's "+1 draw
            // every 4 cards" — STRIKE-specific / Attack-only relics naturally
            // skip Power-type cards inside RelicCatalog.
            int relicBonusPower = RelicCatalog.ComputeCardBonus(card, targetIdx, state, w, details);

            int total = baseBonus + effect + costTie + energyBonus + fightCtx + freePlayBonus + galvanicPenalty
                        + powerOrbBonus + tierOrdering + tierCond + buildBonus + powerAmpBonus + lethalPenalty + selfDmgPowerPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + relicBonusPower + burstDefer + hpPressurePenalty + sleepingEnemyBonus;
            return new ScoreBreakdown(total, "Power",
                Base: baseBonus + costTie + freePlayBonus,
                Effect: effect + energyBonus + fightCtx + galvanicPenalty + powerOrbBonus + tierOrdering + tierCond + buildBonus + powerAmpBonus + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + relicBonusPower,
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
            // FanOfKnivesPower (Silent S): "Shivs hit all enemies" — converts
            // every Shiv attack from single-target to AOE for the rest of
            // combat. The static TargetType doesn't change, so the planner
            // would otherwise score Shivs as single-target. Promote isAoe so
            // downstream per-enemy aggregation, multi-target finisher logic,
            // and lethal detection all see the correct shape.
            if (!isAoe && card.Id == "SHIV"
                && state.PlayerPowers != null
                && state.PlayerPowers.TryGetValue("FanOfKnivesPower", out var fnkStack)
                && fnkStack > 0)
            {
                isAoe = true;
                details.Add("fanOfKnivesAoe");
            }
            int aliveCount = state.Enemies.Count(e => e.IsAlive);

            // v0.2.4 — effective damage: (base + Strength) × Vulnerable × Weak
            // For single-target attacks we use the picked enemy's Vulnerable.
            // For AOE we average / take the max — picked enemy isn't well-defined, so use player-Weak only.
            bool playerIsWeak = state.PlayerWeak > 0;
            bool targetIsVulnerable = !isAoe
                && targetIdx >= 0 && targetIdx < state.Enemies.Count
                && state.Enemies[targetIdx].VulnerableAmount > 0;
            bool targetIsWeak = !isAoe
                && targetIdx >= 0 && targetIdx < state.Enemies.Count
                && state.Enemies[targetIdx].WeakAmount > 0;
            // v0.7.86 — Card-id-specific damage adder (AccuracyPower on Shiv).
            // Applied to base damage before the Strength/Vigor/multiplier chain.
            int adjustedBaseDamage = StatusMath.ApplyCardSpecificDamageBonus(card.Damage, card.Id, state);
            // v0.7.98 — EchoFormPower remaining echoes: each card resolves twice
            // while charges remain. Double the base damage so per-hit calc
            // reflects the echoed total.
            if (state.PlayerEchoForm > 0)
                adjustedBaseDamage *= 2;
            // v0.7.82 — Include PlayerVigor: when this card is being scored as the
            // CURRENT play, any Vigor on the player applies to it (single-shot).
            int effectivePerHit = StatusMath.EffectiveAttackDmg(adjustedBaseDamage,
                state.PlayerStrength, state.PlayerVigor, targetIsVulnerable, playerIsWeak);
            // v0.7.84 — Damage multipliers: Tracking (vs Weak ×2), Cruelty
            // (vs Vuln ×1.25), Lethality (first attack/turn ×1.5). Lethality
            // assumes this card is the first attack — when scoring a candidate
            // as the IMMEDIATE play, that holds; multi-card chain estimators
            // gate it separately.
            effectivePerHit = StatusMath.ApplyDamageMultipliers(effectivePerHit, state,
                defenderVulnerable: targetIsVulnerable, defenderWeak: targetIsWeak,
                lethalityActive: true);

            // v0.6.7 — Variable-damage hit-count override. Card.Hits comes from
            // RepeatVar / CalculatedHits and defaults to 1, but several attacks
            // scale at play time on hand size or remaining energy:
            //   • EXHAUST_BURST (FIEND_FIRE): per-card damage × hand size
            //   • X_COST (SKEWER, WHIRLWIND, VOLLEY, ERADICATE): damage × X
            //     where X is the energy actually spent (all remaining).
            // Without this adjustment FIEND_FIRE [S] scores as a 7-damage hit
            // instead of 7 × hand.Count, severely underrating the card.
            int variableHits = EstimateVariableHits(card, state);
            // Conditional-payoff cards (LUNAR_BLAST, FINISHER) report 0 hits
            // when their setup hasn't fired this turn — honour that instead of
            // applying the default min-1 floor.
            bool allowZero = AllowsZeroHits(card);
            int effHits = allowZero
                ? System.Math.Max(0, variableHits)
                : System.Math.Max(System.Math.Max(1, card.Hits), variableHits);
            if (allowZero && variableHits != card.Hits)
                details.Add($"condHits({card.Hits}→{variableHits})");
            else if (variableHits > card.Hits)
                details.Add($"varHits({card.Hits}→{variableHits})");

            // v0.4 — per-hit damage cap from IntangiblePower (=1) or HardToKillPower (=Amount).
            // Clamp single-target effective per-hit; multi-hit cards still get value because they
            // chip away cap times Hits times instead of one huge hit being wasted.
            int capWastePenalty = 0;
            if (!isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var capTarget = state.Enemies[targetIdx];
                if (capTarget.DamageCapPerHit > 0 && effectivePerHit > capTarget.DamageCapPerHit)
                {
                    int rawPerHit = effectivePerHit;
                    details.Add($"DMG_CAP({capTarget.DamageCapPerHit}):{effectivePerHit}→{capTarget.DamageCapPerHit}");
                    effectivePerHit = capTarget.DamageCapPerHit;

                    // v0.23 Phase 8 — Cap-waste opportunity-cost penalty.
                    // The clamp above equalizes effective-per-hit so a 32-dmg
                    // BLUDGEON and a 6-dmg STRIKE both score "9 dmg" against a
                    // 9-cap Exoskeleton. But BLUDGEON costs 3 energy while STRIKE
                    // costs 1 — the same 3 energy buys 3× STRIKE for 18-24 dmg.
                    // Without this penalty the planner treats them as equally
                    // valued post-clamp. Fires only on cost ≥ 2 (cheap attacks
                    // can't pivot anyway) and only when raw exceeds cap by
                    // DamageCapWasteMinRatio (otherwise the loss is marginal).
                    if (cost >= 2
                        && rawPerHit >= capTarget.DamageCapPerHit * w.DamageCapWasteMinRatio)
                    {
                        int wasted = (rawPerHit - capTarget.DamageCapPerHit) * effHits;
                        capWastePenalty = -(wasted * w.DamageCapWastePenaltyPerLost);
                        details.Add($"capWaste(raw{rawPerHit}×{effHits})={capWastePenalty}");
                    }
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
                // 2026-06-03 — GuardedPower (decompile: ModifyDamageMultiplicative → 0.5 on the
                // owner for powered attacks): the targeted enemy takes HALF card-attack damage.
                // Halve here so BOTH the primary damage score (dmgForScoring) and the threshold
                // call below see the reduced total. Without it the planner over-credits damage and
                // lethal ~2× vs guarded enemies. (AoE path halves separately in the enemy loop.)
                if (shellTarget.Powers.ContainsKey("GuardedPower") && effectiveTotal > 0)
                {
                    details.Add($"GUARDED:{effectiveTotal}→{effectiveTotal / 2}");
                    effectiveTotal /= 2;
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
                    // v0.7.82 — AOE: Vigor applies to ONE hit total across the whole AOE,
                    // not per enemy. Conservative: include Vigor in the rawPer / perEnemyTotal
                    // for each enemy (slight over-credit), since picking AOE means Vigor
                    // benefits the first enemy resolved. Acceptable noise for AOE scoring.
                    int rawPer = StatusMath.EffectiveAttackDmg(card.Damage,
                        state.PlayerStrength, state.PlayerVigor, e.VulnerableAmount > 0, playerIsWeak);
                    int perEnemyTotal = StatusMath.EffectivePerEnemyTotal(
                        card.Damage, effHits, state.PlayerStrength, state.PlayerVigor, e, playerIsWeak);
                    // v0.7.84 — Apply per-target damage multipliers.
                    rawPer = StatusMath.ApplyDamageMultipliers(rawPer, state,
                        defenderVulnerable: e.VulnerableAmount > 0, defenderWeak: e.WeakAmount > 0,
                        lethalityActive: true);
                    perEnemyTotal = StatusMath.ApplyDamageMultipliers(perEnemyTotal, state,
                        defenderVulnerable: e.VulnerableAmount > 0, defenderWeak: e.WeakAmount > 0,
                        lethalityActive: true);
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
                        // v0.7.82 — Random multi-hit: Vigor applies to one hit total.
                        // Conservative approximation: include Vigor in per-hit calc;
                        // overkill clamp limits over-credit naturally.
                        int perHitForE = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, state.PlayerVigor, e.VulnerableAmount > 0, playerIsWeak);
                        // v0.7.84 — Damage multipliers per target.
                        perHitForE = StatusMath.ApplyDamageMultipliers(perHitForE, state,
                            defenderVulnerable: e.VulnerableAmount > 0,
                            defenderWeak: e.WeakAmount > 0, lethalityActive: true);
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
                        // v0.7.82 — Random lethal-prob: include Vigor in per-hit so
                        // hitsNeeded calc accounts for it (Vigor enables more kills with
                        // fewer hits).
                        int perHitForE = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, state.PlayerVigor, e.VulnerableAmount > 0, playerIsWeak);
                        // v0.7.84 — Damage multipliers.
                        perHitForE = StatusMath.ApplyDamageMultipliers(perHitForE, state,
                            defenderVulnerable: e.VulnerableAmount > 0,
                            defenderWeak: e.WeakAmount > 0, lethalityActive: true);
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
                    // v0.7.82 — AOE per-enemy: Vigor applies once globally, but
                    // approximate by applying to each enemy (slight over-credit at
                    // most +Vigor*aoeCount, bounded by enemy hp).
                    int perEnemyDmg = StatusMath.EffectivePerEnemyTotal(
                        card.Damage, card.Hits, state.PlayerStrength, state.PlayerVigor, ei, playerIsWeak);
                    // v0.7.84 — Damage multipliers per enemy.
                    perEnemyDmg = StatusMath.ApplyDamageMultipliers(perEnemyDmg, state,
                        defenderVulnerable: ei.VulnerableAmount > 0,
                        defenderWeak: ei.WeakAmount > 0, lethalityActive: true);
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
                        else if (effectiveTotal <= t.Block
                                 && !t.Powers.ContainsKey("BurrowedPower"))
                        {
                            // 2026-06-03 — BurrowedPower keeps its block across turns and is
                            // STUNNED the moment its block reaches 0 (AfterBlockBroken, may take
                            // several hits/turns). So chipping a burrowed enemy's block is real
                            // PROGRESS toward a stun, not a wasted attack — exempt it here. The
                            // chip-progress bonus in ScoreThresholdsForEnemy rewards that damage.
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
            //
            // v0.9 — Suppress when this turn can already lethal the whole combat.
            // BURST's purpose is "set up next turn's kill" — but if we ALREADY have a
            // lethal sequence available this turn, chipping at the target with a
            // small attack doesn't help; it just inflates the small-attack's score
            // past the lethal-capable card. Observed bug (logs 2026-05-19 21:11
            // turn line 1433): hand had SB(d=21) lethal on HP=14 enemy, but
            // STRIKE_REGENT got BURST70 +2000 each step (chunking 75% of post-
            // STRIKE 8 HP) and beat SB-alone in the 2-card chain compare every
            // re-evaluation. Plan: STRIKE→SB(LETHAL). Actual: STRIKE→STRIKE→
            // DEFEND (enemy survived at 2 HP), SB stayed |RX.
            int burstBonus = 0;
            if (!isAoe && !lethalThisTurn
                && targetIdx >= 0 && targetIdx < state.Enemies.Count)
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
            int thornsDamage = 0;  // POST-BLOCK HP cost from this attack's thorns reflects
            int hits = System.Math.Max(1, card.Hits);
            // v0.10 — STS2 thorns is absorbed by block per hit (decompile +
            // empirical verified). Block soaks reflect before HP — model
            // per-hit absorption against the current PlayerBlock budget.
            int thornsBlockBudget = state.PlayerBlock;
            if (isAoe)
            {
                int reflectTotal = 0;
                int leakTotal = 0;
                foreach (var e in state.Enemies)
                {
                    if (!e.IsAlive || e.ThornsAmount <= 0) continue;
                    for (int r = 0; r < hits; r++)
                    {
                        int absorbed = System.Math.Min(e.ThornsAmount, thornsBlockBudget);
                        thornsBlockBudget -= absorbed;
                        leakTotal += e.ThornsAmount - absorbed;
                        reflectTotal += e.ThornsAmount;
                    }
                }
                if (reflectTotal > 0)
                {
                    thornsPenalty = -leakTotal * w.ThornsPenaltyPerLeakHp;
                    thornsDamage = leakTotal;
                    details.Add($"THORNS_AOE(raw{reflectTotal},leak{leakTotal})={thornsPenalty}");
                }
            }
            else if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                int thorns = state.Enemies[targetIdx].ThornsAmount;
                if (thorns > 0)
                {
                    int reflectTotal = thorns * hits;
                    int leakTotal = 0;
                    for (int r = 0; r < hits; r++)
                    {
                        int absorbed = System.Math.Min(thorns, thornsBlockBudget);
                        thornsBlockBudget -= absorbed;
                        leakTotal += thorns - absorbed;
                    }
                    thornsPenalty = -leakTotal * w.ThornsPenaltyPerLeakHp;
                    thornsDamage = leakTotal;
                    details.Add($"THORNS(raw{reflectTotal},leak{leakTotal})={thornsPenalty}");
                }
            }
            // 2026-06-03 — ReflectPower (decompile): the BLOCKED portion of our attack is
            // reflected back as Unpowered self-damage (= min(our damage, enemy block); our
            // remaining block soaks it first). Distinct from Thorns' fixed per-hit amount.
            // Fold into the thorns self-damage cascade so it shares the HP-fraction / survival
            // weighting and pushes the planner off suicidal swings into a reflecting wall.
            if (!isAoe && targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                var reflectTarget = state.Enemies[targetIdx];
                if (reflectTarget.Powers != null && reflectTarget.Powers.ContainsKey("ReflectPower")
                    && reflectTarget.Block > 0 && effectiveTotal > 0)
                {
                    int blocked = System.Math.Min(effectiveTotal, reflectTarget.Block);
                    int absorbed = System.Math.Min(blocked, thornsBlockBudget);
                    thornsBlockBudget -= absorbed;
                    int leak = blocked - absorbed;
                    if (leak > 0)
                    {
                        thornsPenalty += -leak * w.ThornsPenaltyPerLeakHp;
                        thornsDamage += leak;
                        details.Add($"REFLECT(blocked{blocked},leak{leak})");
                    }
                }
            }

            // v0.7.40 — HP-fraction multiplier. Compounds with the flat penalty.
            // v0.10 — Cascade externalized to PlanScorerWeights.ThornsHpFractionMultipliers
            // (JSON-tunable). Pick the highest threshold satisfied.
            if (thornsDamage > 0)
            {
                int playerHp = System.Math.Max(1, state.PlayerHp);
                double hpFrac = thornsDamage / (double)playerHp;
                double mul = 1.0;
                double matchedThreshold = 0.0;
                foreach (var b in w.ThornsHpFractionMultipliers)
                {
                    if (hpFrac >= b.MinHpFrac && b.MinHpFrac > matchedThreshold)
                    {
                        mul = b.Multiplier;
                        matchedThreshold = b.MinHpFrac;
                    }
                }
                if (mul != 1.0)
                {
                    int oldPenalty = thornsPenalty;
                    thornsPenalty = (int)(thornsPenalty * mul);
                    details.Add($"THORNS_HP({hpFrac:F2})x{mul:F1}");
                }

                // v0.7.70 — Block-alternative bias. If hand has a meaningful
                // block card AND we're NOT delivering lethal AND the trade
                // (attack value vs self HP loss) is unfavorable, add a
                // stacking penalty to push the AI toward defense.
                if (!lethalThisTurn)
                {
                    bool hasGoodBlockAlternative = false;
                    foreach (var c in state.Hand)
                    {
                        if (ReferenceEquals(c, card)) continue;
                        if (!c.IsPlayable || c.IsCurseOrStatus) continue;
                        // Block ≥ 5 considered meaningful
                        if (c.Block >= 5 && c.Cost <= state.PlayerEnergy)
                        {
                            hasGoodBlockAlternative = true;
                            break;
                        }
                    }
                    // Skip penalty if the attack alone is lethal vs target (single-target case
                    // already handled by lethalThisTurn detection; this catches "would kill
                    // enemy on next play after this attack" scenarios — approximate via
                    // total damage being close to target HP).
                    if (hasGoodBlockAlternative && targetIdx >= 0 && targetIdx < state.Enemies.Count)
                    {
                        var t = state.Enemies[targetIdx];
                        bool nearKill = effectiveTotal >= t.EffectiveHp * 7 / 10;
                        if (!nearKill)
                        {
                            int blockBias = w.ThornsBlockAvailableBias;
                            thornsPenalty += blockBias;
                            details.Add($"THORNS_BLOCK_AVAIL={blockBias}");
                        }
                    }
                }
                // v0.10 — Lethal-mode thorns damp. When the hand can lethal
                // the combat this turn (IsLethalThisTurn already demotes
                // suicide-lethal), every alive enemy will be dead before
                // its attack lands — so thorns reflect is the ENTIRE HP
                // cost of the turn, not extra. Setup attacks in a 2-3 card
                // lethal chain were losing to defense because thornsPenalty
                // ran from -2000 to -6000+ while the finisher's +5000
                // RealLethalKillBonus didn't reach them. Damping divisor
                // (default 10 → 1/10) is JSON-tunable.
                if (lethalThisTurn && thornsDamage < state.PlayerHp - w.ThornsLethalDampHpMargin
                    && w.ThornsLethalDampDivisor > 1)
                {
                    int dampedPenalty = thornsPenalty / w.ThornsLethalDampDivisor;
                    if (dampedPenalty != thornsPenalty)
                    {
                        details.Add($"THORNS_LETHAL_DAMP({thornsPenalty}→{dampedPenalty})");
                        thornsPenalty = dampedPenalty;
                    }
                }

                // v0.10 — Block-vs-thorn-attack scenario penalty. When this
                // attack into a thorny enemy doesn't kill anyone (no lethal,
                // not even the target), compare total turn HP loss between:
                //   B. block-only: spend all energy on block, take enemy hit.
                //   C. block-then-attack-thorn: lose thorns reflect AND take
                //      enemy hit (with whatever block survived this card's
                //      energy spend). Thorns bypasses block in STS2 so the
                //      reflect is pure HP loss either way.
                // Penalize the attack when B is clearly safer. Catches the
                // "defend → attack thorn enemy → enemy turn lands with no
                // block left" trap.
                if (!lethalThisTurn && targetIdx >= 0 && targetIdx < state.Enemies.Count)
                {
                    var tgt = state.Enemies[targetIdx];
                    bool killsTarget = effectiveTotal >= tgt.EffectiveHp;
                    if (!killsTarget && tgt.ThornsAmount > 0)
                    {
                        int enemyLeakNow = Sts2CombatAI.Sim.EnemyTurnSimulator.PredictRawLeak(state);
                        int extraBlockAll = BestBlockInEnergyBudget(state, state.PlayerEnergy, card);
                        int extraBlockMinus = BestBlockInEnergyBudget(state, state.PlayerEnergy - card.Cost, card);
                        int hpLossB = System.Math.Max(0, enemyLeakNow - extraBlockAll);
                        int hpLossC = thornsDamage + System.Math.Max(0, enemyLeakNow - extraBlockMinus);
                        if (hpLossB + w.ThornsBlockBetterMargin < hpLossC)
                        {
                            int blockOnlyBias = w.ThornsBlockBetterPenalty;
                            thornsPenalty += blockOnlyBias;
                            details.Add($"THORNS_BLOCK_BETTER(C={hpLossC},B={hpLossB})={blockOnlyBias}");
                        }
                    }
                }
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

            // v0.5 — attack cards can also draw (cycle-attack hybrids, or attacks
            // that apply MachineLearningPower / DrawCardsNextTurnPower). Same
            // pattern: EvaluateDrawCard returns 0 for non-draw cards, so this is
            // a no-op for plain attacks.
            int atkDrawBonus = EvaluateDrawCard(card, state, w, out int atkDrawRescue);
            if (atkDrawBonus != 0) details.Add($"drawCtx={atkDrawBonus}");
            if (atkDrawRescue > 0) details.Add($"drawRescue+{atkDrawRescue}");

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
                        // v0.7.82 — Lethal-AOE check: include Vigor (applies to first
                        // hit of first enemy; conservative to include for all enemies).
                        int perHit = StatusMath.EffectiveAttackDmg(card.Damage,
                            state.PlayerStrength, state.PlayerVigor, e.VulnerableAmount > 0, playerIsWeak);
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

            // v0.7.85 — Attack-triggered block from RagePower (N block per attack
            // played) and AfterimagePower (N block per card played, includes
            // attacks). Credits the attack's defensive utility so RAGE / AFTERIMAGE
            // builds correctly prefer attacks even on defensive turns.
            // v0.7.97 — FeelNoPainPower: +N block if this attack exhausts on play.
            int attackReactiveBlock = 0;
            if (state.PlayerRage > 0)
                attackReactiveBlock += StatusMath.EffectiveBlock(state.PlayerRage,
                    state.PlayerDexterity, state.PlayerFrail > 0);
            if (state.PlayerAfterimage > 0)
                attackReactiveBlock += StatusMath.EffectiveBlock(state.PlayerAfterimage,
                    state.PlayerDexterity, state.PlayerFrail > 0);
            if (state.PlayerFeelNoPain > 0 && card.IsExhaust)
                attackReactiveBlock += StatusMath.EffectiveBlock(state.PlayerFeelNoPain,
                    state.PlayerDexterity, state.PlayerFrail > 0);
            // v0.8.1 — DanseMacabre: cost≥2 attack → +N block.
            if (state.PlayerDanseMacabre > 0 && card.Cost >= 2)
                attackReactiveBlock += StatusMath.EffectiveBlock(state.PlayerDanseMacabre,
                    state.PlayerDexterity, state.PlayerFrail > 0);
            // v0.8.7 — Cap per-card reactive block at 20. Prevents 4-source
            // stacking (rare cross-character: Ironclad Rage + Watcher Afterimage
            // + Necrobinder DanseMacabre + Ironclad FeelNoPain) from inflating
            // attack score beyond any legitimate single-card block value
            // (canonical STS biggest single-card defense ~20-30).
            const int ReactiveBlockCap = 20;
            int reactiveBlockClamped = System.Math.Min(attackReactiveBlock, ReactiveBlockCap);
            int reactiveBlockBonus = reactiveBlockClamped * w.BlockPerPointBonus;
            if (reactiveBlockBonus != 0)
            {
                string capTag = attackReactiveBlock > ReactiveBlockCap
                    ? $"(capped {attackReactiveBlock}→{ReactiveBlockCap})" : "";
                details.Add($"reactBlk(rage{state.PlayerRage}+afterimg{state.PlayerAfterimage}+fnp{(card.IsExhaust ? state.PlayerFeelNoPain : 0)}+danse{(card.Cost >= 2 ? state.PlayerDanseMacabre : 0)}){capTag}={reactiveBlockBonus}");
            }

            // Threshold-trigger powers on the targeted enemy. ShriekPower
            // (TerrorEel) and PlowPower (CeremonialBeast) STUN the enemy when
            // a hit brings their HP ≤ Amount; DoomPower (player-applied)
            // INSTAKILLS at turn end when HP ≤ Doom amount. The AI normally
            // treats these enemies as ordinary HP pools and misses the
            // high-leverage burst opportunity.
            int thresholdTriggerBonus = ComputeThresholdTriggerBonus(
                card, state, isAoe, targetIdx, effectiveTotal, w, details);

            // v0.10 — Per-card relic bonus. Attack-side relics dominate:
            // PenNib (10th attack ×2), StrikeDummy (STRIKE +3), GremlinHorn
            // (kill → energy+draw), Kunai/Shuriken/OrnamentalFan (Nth-attack
            // triggers), IronClub (every-4-cards +1 draw — also fires here).
            int relicBonusAtk = RelicCatalog.ComputeCardBonus(card, targetIdx, state, w, details);

            // v0.23 Phase 5b — Burst-chain attack bonus. When the hand can
            // finish at least one enemy this turn and THIS attack is aimed
            // at one of those killable enemies, push the score above
            // alternative Powers/skills. Single-target only — AOE already
            // gets credit from hitting every enemy and doesn't need this
            // extra nudge.
            int burstChainBonus = 0;
            if (hasBurstWindow && !isAoe && targetIdx >= 0
                && burstKillable.Contains(targetIdx))
            {
                burstChainBonus = w.BurstChainAttackBonus;
                details.Add($"burstChain[{targetIdx}]=+{burstChainBonus}");
            }

            int total = baseBonus + effect + attached + targetBonus + wastedPenalty + thornsPenalty + burstBonus + atkOrbBonus + buildBonus + atkEnergyBonus + atkDrawBonus + atkAmpBonus + atkEffBonus + survivalAtkPenalty + selfDmgAtkPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + lethalSetupPenalty + reactiveBlockBonus + thresholdTriggerBonus + relicBonusAtk + burstChainBonus + capWastePenalty;
            // v0.9 — Per-energy efficiency diagnostic. Shows BOTH raw dmg/E
            // (Strength/Vigor/Enchant from PreviewValue) AND effective dmg/E
            // (with Vuln/Weak/Echo/X-cost folded in). When they differ
            // significantly, the gap indicates Power buffs / target debuffs
            // changing the card's value beyond its printed damage. Tiebreaker
            // uses Effective; this just informs the user reading the log.
            double rawEff = card.DmgPerEnergy;
            double effEff = card.EffectiveDmgPerEnergy(state);
            details.Add(System.Math.Abs(rawEff - effEff) < 0.05
                ? $"eff(d{rawEff:F1}/E)"
                : $"eff(d{rawEff:F1}/E→{effEff:F1}/E)");
            return new ScoreBreakdown(total, isAoe ? "Attack-AOE" : "Attack",
                Base: baseBonus,
                Effect: effect + attached + burstBonus + atkOrbBonus + thornsPenalty + buildBonus + atkEnergyBonus + atkDrawBonus + atkAmpBonus + atkEffBonus + fetchPollutionPenalty + comboBonus + monopolyPenalty + relicBonusAtk,
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
            int perPlayBlock = StatusMath.EffectiveBlock(rawBlock,
                state.PlayerDexterity, state.PlayerFrail > 0);
            // v0.8.4 — Canonical compound model. Burst + Echo make the card
            // RESOLVE multiple times; Unmovable doubles only the FIRST of those
            // plays. Previously each multiplier was applied as a pure ×2 on
            // the running effectiveBlock, which over-credited the 3-way case
            // (Unmov × Burst × Echo) by ~60%.
            int plays = (state.PlayerBurst > 0 ? 2 : 1) * (state.PlayerEchoForm > 0 ? 2 : 1);
            int effectiveBlock = perPlayBlock * plays;
            // v0.7.85 — Unmovable adds ONE more perPlayBlock (the first play
            // doubled), not a multiplier on the already-multiplied total.
            if (state.PlayerUnmovable > 0 && !state.UnmovableUsedThisTurn && perPlayBlock > 0)
            {
                effectiveBlock += perPlayBlock;
                details.Add($"unmovable(+{perPlayBlock})");
            }
            if (plays > 1)
                details.Add($"plays×{plays}(burst{(state.PlayerBurst > 0 ? 1 : 0)}+echo{(state.PlayerEchoForm > 0 ? 1 : 0)})");
            // v0.8.7 — Reactive block sources (Afterimage / FeelNoPain / Danse)
            // accumulated into a single bucket, then capped at 20 to prevent
            // 3-source stacking from inflating the skill score beyond canonical
            // single-card block value.
            int skillReactiveBlock = 0;
            int afterBlock = 0, fnpBlock = 0, danseBlock = 0;
            if (state.PlayerAfterimage > 0)
            {
                afterBlock = StatusMath.EffectiveBlock(state.PlayerAfterimage,
                    state.PlayerDexterity, state.PlayerFrail > 0);
                skillReactiveBlock += afterBlock;
            }
            if (state.PlayerFeelNoPain > 0 && card.IsExhaust)
            {
                fnpBlock = StatusMath.EffectiveBlock(state.PlayerFeelNoPain,
                    state.PlayerDexterity, state.PlayerFrail > 0);
                skillReactiveBlock += fnpBlock;
            }
            if (state.PlayerDanseMacabre > 0 && card.Cost >= 2)
            {
                danseBlock = StatusMath.EffectiveBlock(state.PlayerDanseMacabre,
                    state.PlayerDexterity, state.PlayerFrail > 0);
                skillReactiveBlock += danseBlock;
            }
            const int SkillReactiveBlockCap = 20;
            int skillReactiveClamped = System.Math.Min(skillReactiveBlock, SkillReactiveBlockCap);
            effectiveBlock += skillReactiveClamped;
            if (afterBlock > 0)  details.Add($"afterimage(+{afterBlock})");
            if (fnpBlock > 0)    details.Add($"feelNoPain(+{fnpBlock})");
            if (danseBlock > 0)  details.Add($"danse(+{danseBlock})");
            if (skillReactiveBlock > SkillReactiveBlockCap)
                details.Add($"reactiveCap({skillReactiveBlock}→{SkillReactiveBlockCap})");
            // v0.12 — Marginal block valuation (over-block fix). Credit only the block that
            // reduces the incoming leak; block beyond it is overkill (penalized at the same
            // rate). leakBefore is the leak AFTER currently-accumulated block, so in a depth-N
            // sequence the 2nd block card sees the reduced leak — this stops "small Defend then
            // big block" from each collecting full value (the DEFEND→BLOOD_WALL over-block).
            int leakBefore = (card.Target == TargetType.Self && card.Block > 0 && !allInert)
                ? EnemyTurnSimulator.PredictPlayerDmg(state) : 0;
            int usefulBlock = effectiveBlock, overkillBlock = 0;
            int effect;
            if (leakBefore > 0)
            {
                usefulBlock = System.Math.Min(effectiveBlock, leakBefore);
                overkillBlock = effectiveBlock - usefulBlock;
                effect = (usefulBlock - overkillBlock) * w.BlockPerPointBonus;
            }
            else
            {
                // No leak after current block: the wasted-block / neutralize rules below handle it.
                effect = effectiveBlock * w.BlockPerPointBonus;
            }
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

            // JuggernautPower (Ironclad reactive): each block-gain event deals
            // N damage to a random alive enemy. Skill plays with block > 0
            // count as block-gain events; Echo/Burst doubled plays fire
            // multiple times. The simulator applies this at depth-N lookahead,
            // but the immediate score skips it — block-heavy Juggernaut decks
            // would otherwise under-rank Skills relative to Attacks of
            // similar score.
            if (state.PlayerJuggernaut > 0 && card.Block > 0)
            {
                int skillAliveCount = state.Enemies.Count(e => e.IsAlive);
                if (skillAliveCount > 0)
                {
                    int triggers = plays;
                    int dmgPerTrigger = state.PlayerJuggernaut;
                    int juggernautBonus = triggers * dmgPerTrigger * w.DamagePerPointBonus;
                    const int JuggCap = 400;
                    if (juggernautBonus > JuggCap) juggernautBonus = JuggCap;
                    effect += juggernautBonus;
                    details.Add($"juggernaut(plays{triggers}×{dmgPerTrigger})=+{juggernautBonus}");
                }
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

            // v0.9 — NoBlockPower shutdown: block gain is suppressed THIS
            // turn (Strangle / certain elite debuffs). DEFEND scoring should
            // drop to ~base — no threatBonus, no neutralize bonus. Zero out
            // effectiveBlock so downstream chained calculations see no block.
            if (state.PlayerNoBlock > 0 && card.Block > 0)
            {
                effectiveBlock = 0;
                details.Add($"NO_BLOCK(active{state.PlayerNoBlock})");
            }

            // v0.9 — ConfusedPower (Skill cost randomized 0..3 each draw).
            // The printed cost is unreliable while Confused is active. For
            // expensive (cost ≥ 2) Skills, factor in a small flat penalty
            // since the realised cost may be unaffordable. 0/1-cost Skills
            // are barely affected. Magnitude proportional to printed cost.
            int confusedPenalty = 0;
            if (state.PlayerConfused > 0 && card.Cost >= 2)
            {
                confusedPenalty = -150 * card.Cost;     // -300 for cost-2, -450 for cost-3+
                details.Add($"CONFUSED(c{card.Cost})={confusedPenalty}");
            }
            int threatBonus = 0;
            int residual = (card.Target == TargetType.Self && effectiveBlock > 0 && !allInert)
                ? leakBefore : 0;   // v0.12 — reuse leakBefore (computed above) instead of a 2nd PredictPlayerDmg call
            bool neutralizes = residual > 0 && effectiveBlock >= residual;

            // v0.9 — ImbalancedPower bonus: when any alive enemy has Imbalanced
            // AND this block fully covers their attack, that enemy stuns
            // itself next turn (skips attack). Worth ~stun bonus magnitude.
            // Approximate via "block ≥ that enemy's intent damage"; we check
            // ALL imbalanced enemies and add bonus for each whose intent we
            // fully cover with this card's block alone (over-conservative —
            // ignores existing player block, but avoids double-counting).
            int imbalancedBonus = 0;
            if (effectiveBlock > 0 && card.Target == TargetType.Self)
            {
                foreach (var e in state.Enemies)
                {
                    if (!e.IsAlive || e.ImbalancedAmount <= 0 || !e.HasAttackIntent) continue;
                    if (effectiveBlock >= e.IntentDamage)
                    {
                        // Save that enemy's next-turn attack. Use similar scale
                        // to stunBonus (DEFEND threatBonus + per-dmg block).
                        int saveDmg = System.Math.Max(15, e.TotalIntentDamage);
                        int bonus = w.BlockUnderThreatBonus + saveDmg * w.BlockPerPointBonus;
                        const int ImbalancedCap = 3000;
                        if (bonus > ImbalancedCap) bonus = ImbalancedCap;
                        imbalancedBonus += bonus;
                        details.Add($"imbalancedStun(e{e.IntentDamage}≤blk{effectiveBlock})=+{bonus}");
                    }
                }
            }

            // BlockUnderThreatBonus only applies when the card *actually* blocks. Otherwise
            // a self-targeted skill with no block (Turbo / Inflame / energy-gain cards) would
            // hoover up a 1500-point bonus just for being Self-target — which has been
            // observed pushing Turbo above Strike in lethal windows.
            if (card.Target == TargetType.Self && card.Block > 0 && threat > threshold && !allInert)
            {
                // v0.12 — pro-rate the urgency bonus by the leak THIS card removes, bounded by
                // rawThreat = currentBlock + leakBefore, so a turn's total block-urgency bonus
                // can't exceed one BlockUnderThreatBonus (no double-collect across block cards —
                // the root of the DEFEND→BLOOD_WALL over-block). A neutralizing single card still
                // gets the full bonus (usefulBlock == leakBefore == rawThreat − currentBlock).
                int rawThreat = state.PlayerBlock + leakBefore;
                threatBonus = rawThreat > 0
                    ? (int)((long)w.BlockUnderThreatBonus * usefulBlock / rawThreat)
                    : w.BlockUnderThreatBonus;
                details.Add($"threatBonus={threatBonus}(useful{usefulBlock}/raw{rawThreat})");
            }

            // v0.4 — "Block fully neutralises threat": if a self-block card brings the
            // residual damage to exactly zero, take 0 hits this turn. That beats Power
            // cards even when the threat ratio is too low to trip BlockUnderThreatBonus.
            if (neutralizes)
            {
                threatBonus += w.BlockNeutralizeBonus;
                details.Add($"neutralize({residual}leak)+{w.BlockNeutralizeBonus}");
            }

            // 2026-06-03 — HP preservation when the fight is already won. usefulBlock
            // is leak-capped (only real, non-wasted block), and the Winning gate means
            // the forgone chip attack wouldn't change the kill turn — so this block is
            // pure cross-combat HP savings. Never fires when Losing/Tight (must race)
            // or when there's no incoming (usefulBlock==0).
            if (HpPreservePerPoint > 0 && usefulBlock > 0
                && raceProj.Race == SurvivalProjection.RaceOutcome.Winning)
            {
                int preserve = usefulBlock * HpPreservePerPoint;
                threatBonus += preserve;
                details.Add($"hpPreserve(useful{usefulBlock})=+{preserve}");
            }

            // Wasted-block penalty: only for blocks that genuinely accomplish nothing.
            // If neutralize fires (block fully absorbs an incoming hit), it's by definition
            // NOT wasted — these two rules used to fight each other.
            //
            // v0.10 — Removed `!allInert` exclusion. Originally the gate
            // skipped the penalty when all enemies were inert (stunned /
            // threat=None), but that's the MOST wasted case: block decays
            // at turn end and the inert enemies aren't dealing damage this
            // turn. Observed 22:54 log Turn 2 step 3-4: after both alive
            // enemies became Inert (stunned post-kill), AI still played
            // PARTICLE_WALL + DEFEND_REGENT because their +block-per-point
            // scores stayed positive without the wasted-block penalty.
            int wastedBlock = (card.Target == TargetType.Self && card.Block > 0
                && (threat < w.NoThreatRatio || allInert) && !neutralizes) ? w.WastedBlockPenalty : 0;
            // v0.2.6 — Energy gain context applies to Skill carriers too (Adrenaline-style).
            int energyBonus = EvaluateEnergyGain(card, state, w);
            if (energyBonus != 0) details.Add($"energyCtx={energyBonus}");

            // v0.2.6 — Draw card: only valuable when the rest of the hand has nothing strong.
            int drawBonus = EvaluateDrawCard(card, state, w, out int skillDrawRescue);
            if (drawBonus != 0) details.Add($"drawCtx={drawBonus}");
            if (skillDrawRescue > 0) details.Add($"drawRescue+{skillDrawRescue}");

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
                // Magnitude tunable via STS2_ENRAGE_PENALTY (default 100). S5 diag
                // showed TestSubjectBoss ending with Strength 6-20 (Enrage:2 → many
                // Skill plays despite -200 penalty) — likely too weak at default.
                enragePenalty = -totalEnrage * EnragePenaltyPerStack;
                if (enragePenalty != 0) details.Add($"enrage{enragePenalty}");
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
            // v0.10 — Card generators (QUASAR / MIRACLE / HELLO_WORLD / JACKPOT etc.)
            // also exempt: they add cards to hand from an external pool, functionally
            // equivalent to draw cards in crisis terms — capable of producing block
            // when the deck alone can't save us. Identified by CARD_GEN axis (catalog)
            // or ID fallback for cards not yet tagged.
            int survivalSkillPenalty = 0;
            if (card.Block == 0 && !card.IsEnergyGainCard && !card.IsDrawCard && !IsCardGenerator(card))
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

            // v0.10 — Generator rescue bonus (mirrors EvaluateDrawCard's
            // ComputeDrawRescueBonus, adapted for cards that pull from an
            // external colorless pool instead of the player's deck). Fires
            // when (a) hand alone can't cover this turn's incoming damage,
            // (b) HP post-leak ≤ crisis threshold. Magnitude uses a fixed
            // pool-average estimate (5 block per generated card) because
            // we don't have the colorless pool catalog loaded — conservative
            // enough that a curse-heavy pile draw doesn't get over-credited.
            int generatorRescueBonus = 0;
            if (IsCardGenerator(card))
            {
                int leak = EnemyTurnSimulator.PredictPlayerDmg(state);
                if (leak > 0)
                {
                    int handBlockCap = SumPlayableBlockExcluding(state, card);
                    int shortfall = leak - state.PlayerBlock - handBlockCap;
                    int hpAfter = state.PlayerHp - shortfall;
                    if (shortfall > 0 && hpAfter <= w.DrawRescueHpThreshold)
                    {
                        // Conservative: assume each generator output card
                        // averages 5 raw block (Defend-class baseline). Caps
                        // at shortfall so over-generation doesn't inflate.
                        const int AvgGeneratorBlock = 5;
                        int generatedCount = System.Math.Max(1, card.DrawCount);
                        int expectedNewBlock = generatedCount * AvgGeneratorBlock;
                        int blockRecovered = System.Math.Min(shortfall, expectedNewBlock);
                        generatorRescueBonus = blockRecovered * w.BlockPerPointBonus;
                        if (generatorRescueBonus > 0)
                            details.Add($"genRescue(short{shortfall},exp{expectedNewBlock})+{generatorRescueBonus}");
                    }
                }
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
            //
            // v0.10 — Exemption for stars-gain setup skills (VENERATE, etc.).
            // When IsLethalThisTurn Phase 0 included a star-cost attack that
            // THIS skill's StarsGain would unlock, the skill is part of the
            // lethal chain — NOT dead weight. Without this carve-out the
            // -3000 penalty drives the skill's firstScore below 0, and the
            // firstScore guard (ActionPlanner line 274-276) forces the
            // planner to pick the highest-positive STRIKE first, skipping
            // the unlock altogether. Observed: 22:06 log Turn 3 step 1
            // hand=[STRIKE,STRIKE,FALLING_STAR(unplayable),VENERATE,...]
            // VENERATE → FALLING_STAR → STRIKE → STRIKE was the optimal kill
            // sequence but planner picked STRIKE → STRIKE then stopped.
            bool isLethalSetupSkill = false;
            if (lethalThisTurn && card.StarsGain > 0)
            {
                int starsAfter = state.PlayerStars + card.StarsGain;
                foreach (var c in state.Hand)
                {
                    if (c.IsPlayable) continue;
                    if (!c.IsAttack) continue;
                    if (ActionPlanner.StarCostByCardId.TryGetValue(c.Id ?? "", out int sc)
                        && state.PlayerStars < sc && starsAfter >= sc)
                    {
                        isLethalSetupSkill = true;
                        break;
                    }
                }
            }
            int lethalPenalty = (lethalThisTurn && !isLethalSetupSkill)
                ? w.LethalModeNonAttackPenalty : 0;
            if (lethalPenalty != 0) details.Add($"lethalMode={lethalPenalty}");
            if (isLethalSetupSkill) details.Add("lethalUnlock(exempt)");

            if (fetchPollutionPenalty != 0) details.Add($"fetchPoll={fetchPollutionPenalty}");
            if (comboBonus != 0) details.Add(comboDetail);
            if (monopolyPenalty != 0) details.Add($"energyMono={monopolyPenalty}");

            // v0.10 — Per-card relic bonus. Skill-relevant relics: LetterOpener
            // (every 3rd Skill → damage), IronClub (every 4th card → +draw),
            // VelvetChoker cap (warn when at the per-turn play limit).
            int relicBonusSkill = RelicCatalog.ComputeCardBonus(card, targetIdx, state, w, details);

            // v0.10 — Delayed-AOE detonation (THE_BOMB). Card itself has no
            // immediate damage/block; the value lies in the AOE damage that
            // detonates at end of turn N+DelayTurns-1. Score:
            //   per_enemy_damage × alive_enemies × delay_discount × DmgPerPoint
            // delay_discount accounts for (a) future enemies may be dead by
            // detonation (overkill risk), (b) we may die before detonation,
            // and (c) opportunity cost of locking the power slot. We use
            // 0.5^max(0, delayTurns-2) — 2-turn delay (THE_BOMB base) → 0.5;
            // any longer delay halves again. AOE multiplier capped at 4
            // mirroring other AOE damage scoring caps in this scorer.
            int delayedBombBonus = 0;
            if (card.Effect.DelayedAoeDamage > 0 && card.Effect.DelayTurns >= 2)
            {
                int aliveCnt = 0;
                foreach (var e in state.Enemies) if (e.IsAlive) aliveCnt++;
                int aoeFactor = System.Math.Min(4, System.Math.Max(1, aliveCnt));
                int delaySteps = System.Math.Max(0, card.Effect.DelayTurns - 2);
                double delayMult = System.Math.Pow(0.5, delaySteps);
                delayedBombBonus = (int)(card.Effect.DelayedAoeDamage
                    * aoeFactor * delayMult * w.DamagePerPointBonus);
                if (delayedBombBonus > 0)
                    details.Add($"delayedAOE({card.Effect.DelayedAoeDamage}×{aoeFactor}×{delayMult:F2})={delayedBombBonus}");
            }

            int total = baseBonus + effect + powerEffect + threatBonus + wastedBlock + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus + skillAmpBonus + skillEffBonus + survivalSkillPenalty + selfDmgSkillPenalty + skillTierOrdering + skillTierCond + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + imbalancedBonus + confusedPenalty + relicBonusSkill + delayedBombBonus + generatorRescueBonus;
            // v0.9 — Per-energy efficiency diagnostic (Skill: block/E).
            // Raw block/E (Dex/Frail-naïve) → Effective block/E with Frail/
            // Burst/Echo/Unmovable applied. Shows the live combat-aware value.
            if (card.Block > 0)
            {
                double rawBlk = card.BlockPerEnergy;
                double effBlk = card.EffectiveBlockPerEnergy(state);
                details.Add(System.Math.Abs(rawBlk - effBlk) < 0.05
                    ? $"eff(b{rawBlk:F1}/E)"
                    : $"eff(b{rawBlk:F1}/E→{effBlk:F1}/E)");
            }
            return new ScoreBreakdown(total, "Skill",
                Base: baseBonus,
                Effect: effect + powerEffect + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus + skillAmpBonus + skillEffBonus + survivalSkillPenalty + skillTierOrdering + skillTierCond + lethalPenalty + fetchPollutionPenalty + comboBonus + monopolyPenalty + relicBonusSkill + delayedBombBonus,
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

    /// <summary>
    /// HP-threshold trigger bonus. Three enemy powers gate special effects on
    /// dropping HP below their Amount stack:
    ///   • ShriekPower (TerrorEel boss)  — STUN + remove self
    ///   • PlowPower (Ceremonial Beast)  — STUN + remove StrengthPower + self
    ///   • DoomPower (player-applied)    — INSTAKILL at turn end
    /// All three previously invisible to scoring — the AI treated the enemy
    /// as an ordinary HP pool and missed the high-leverage burst window
    /// (e.g. preferring multi-hit Shivs over a single big-hit attack when
    /// only the latter crosses the threshold).
    ///
    /// Damage estimate per enemy uses <paramref name="effectiveTotal"/> for
    /// the single-target path (already block-adjusted by the upstream
    /// scoring math) and per-enemy effective damage for the AOE path.
    /// </summary>
    private static int ComputeThresholdTriggerBonus(SimCard card, SimState state,
        bool isAoe, int targetIdx, int effectiveTotal, PlanScorerWeights w,
        List<string> details)
    {
        if (card.Damage <= 0) return 0;
        if (state.Enemies.Count == 0) return 0;

        int totalBonus = 0;

        if (!isAoe)
        {
            if (targetIdx < 0 || targetIdx >= state.Enemies.Count) return 0;
            var target = state.Enemies[targetIdx];
            // Skip only when there's NO threshold-relevant state at all.
            // OnDeathSpawnsCount is a non-Powers signal (carved out into its
            // own SimEnemy field) so we still want to enter ScoreThresholds
            // even when the Powers dict is empty / unpopulated by tests.
            bool hasThresholdState = (target.Powers != null && target.Powers.Count > 0)
                                  || target.OnDeathSpawnsCount > 0;
            if (!target.IsAlive || !hasThresholdState) return 0;
            totalBonus = ScoreThresholdsForEnemy(target, effectiveTotal, state, w, details);
        }
        else
        {
            bool playerIsWeak = state.PlayerWeak > 0;
            int hits = System.Math.Max(1, card.Hits);
            foreach (var e in state.Enemies)
            {
                bool eHasThresholdState = (e.Powers != null && e.Powers.Count > 0)
                                       || e.OnDeathSpawnsCount > 0;
                if (!e.IsAlive || !eHasThresholdState) continue;
                int perHit = StatusMath.EffectivePerHitCapped(card.Damage,
                    state.PlayerStrength, state.PlayerVigor, e, playerIsWeak);
                perHit = StatusMath.ApplyDamageMultipliers(perHit, state,
                    e.VulnerableAmount > 0, e.WeakAmount > 0, lethalityActive: false);
                int effTotalForE = perHit * hits;
                // 2026-06-03 — GuardedPower halves card-attack damage to its owner (see single-
                // target path). Mirror it here for the AoE threshold scoring.
                if (e.Powers != null && e.Powers.ContainsKey("GuardedPower")) effTotalForE /= 2;
                totalBonus += ScoreThresholdsForEnemy(e, effTotalForE, state, w, details);
            }
        }
        return totalBonus;
    }

    private static int ScoreThresholdsForEnemy(SimEnemy target, int effectiveTotal,
        SimState state, PlanScorerWeights w, List<string> details)
    {
        int currentHp = target.Hp;
        int incomingDmg = System.Math.Max(0, effectiveTotal - target.Block);
        int hpAfter = System.Math.Max(0, currentHp - incomingDmg);

        int bonus = 0;

        // TheBombPower: enemy attached a countdown to themselves (Amount =
        // turns until detonation; Damage = 40 AOE to player side). The bomb
        // is INVISIBLE to the AI's intent-based threat estimation. Two
        // scoring nudges:
        //   1. Kill-the-bomber bonus: if this attack would kill the carrier
        //      AND the bomb is about to go off (counter ≤ 2), big bonus —
        //      removing the carrier removes the bomb.
        //   2. Damage-toward-killing-bomber priority: amortized bomb threat
        //      proportional to imminence.
        if (target.Powers.TryGetValue("TheBombPower", out var bombCounter) && bombCounter > 0)
        {
            const int BombDamage = 40;
            if (hpAfter <= 0 && bombCounter <= 3)
            {
                // Bomb defused by killing the carrier within reach.
                int saveValue = BombDamage * w.DamagePerPointBonus / 10;
                const int DefuseCap = 1500;
                if (saveValue > DefuseCap) saveValue = DefuseCap;
                bonus += saveValue;
                details.Add($"bombDefuse(counter{bombCounter},kill)=+{saveValue}");
            }
            else if (hpAfter < currentHp)
            {
                // Otherwise: priority push amortized by counter. counter=1
                // (detonates this turn end) → full BombDamage saved per turn.
                // counter=5 → 1/5 weight.
                int amortizedDmg = BombDamage / System.Math.Max(1, bombCounter);
                int v = amortizedDmg * w.DamagePerPointBonus / 10;
                const int PriorityCap = 500;
                if (v > PriorityCap) v = PriorityCap;
                bonus += v;
                details.Add($"bombPriority(counter{bombCounter})=+{v}");
            }
        }

        // v0.10 — InfestedPower kill-suppression. Killing a carrier triggers
        // AfterDeath → spawn N reinforcements (Wrigglers for the Phrog
        // Parasite Elite). Combat does NOT end while InfestedPower exists
        // (ShouldStopCombatFromEnding=true), so the kill just resets total
        // enemy HP upward and starts INFECTION pile-up. Penalize lethal-
        // this-hit so the planner picks stall / debuff / block over chip-
        // kill until burst-window can clear all spawns.
        //
        // Phase 1 scope (2026-05-20 defeat log): blanket penalty. Future
        // refinement: relax when player damage budget for current turn
        // can also clear N × Wriggler HP (~20 each).
        if (target.OnDeathSpawnsCount > 0 && hpAfter <= 0 && currentHp > 0)
        {
            int spawnPenalty = -1200 * target.OnDeathSpawnsCount;
            const int SpawnPenaltyCap = -5000;
            if (spawnPenalty < SpawnPenaltyCap) spawnPenalty = SpawnPenaltyCap;
            bonus += spawnPenalty;
            details.Add($"infestedKill(spawns{target.OnDeathSpawnsCount})={spawnPenalty}");
        }

        // 2026-06-03 — IllusionPower (decompile AfterDeath): on death the enemy does NOT leave
        // combat — it revives to FULL HP next turn (HealIntent revive move). Chip-killing it is
        // wasted burst: it heals back to max and the kill must be re-paid. Penalize lethal-this-
        // hit (like InfestedPower) so the planner spends damage elsewhere / on the leader —
        // IllusionPower also self-applies MinionPower, so killing the leader is the real out.
        if (target.Powers.TryGetValue("IllusionPower", out var illusion) && illusion > 0
            && hpAfter <= 0 && currentHp > 0)
        {
            const int IllusionKillPenalty = -1500;
            bonus += IllusionKillPenalty;
            details.Add($"illusionKill(revivesFull)={IllusionKillPenalty}");
        }

        // 2026-06-03 — SteamEruptionPower (decompile: ShouldStopCombatFromEnding + owner not
        // removed on death → killing it does NOT end the fight; it triggers an AboutToBlow that
        // deals damage at the end of the next turn). Mild lethal nudge so the planner prefers to
        // have block up when it lands the kill rather than chip it into an unguarded turn.
        if (target.Powers.TryGetValue("SteamEruptionPower", out var steam) && steam > 0
            && hpAfter <= 0 && currentHp > 0)
        {
            const int SteamEruptionKillPenalty = -500;
            bonus += SteamEruptionKillPenalty;
            details.Add($"steamEruptKill(blowsUp)={SteamEruptionKillPenalty}");
        }

        // 2026-06-03 — kill-order enemy powers that need sibling-enemy context.
        bool killsTarget = hpAfter <= 0 && currentHp > 0;
        int aliveEnemies = 0;
        for (int i = 0; i < state.Enemies.Count; i++)
            if (state.Enemies[i].IsAlive) aliveEnemies++;

        // ReattachPower (Decimillipede segment): on death it REVIVES with Amount HP unless ALL
        // other segments are already dead. Chip-killing a segment while siblings live is wasted —
        // it comes back. Only penalize when other enemies remain (the final kill is genuine).
        if (killsTarget && aliveEnemies > 1
            && target.Powers.TryGetValue("ReattachPower", out var reattach) && reattach > 0)
        {
            const int ReattachKillPenalty = -1200;
            bonus += ReattachKillPenalty;
            details.Add($"reattachKill(revives)={ReattachKillPenalty}");
        }

        // MinionPower focus-leader: minions give up the fight when their leader dies. Killing a
        // NON-minion enemy (the likely controller) while minions are present clears them for free.
        // A bonus (never blocks a play), so misidentifying the leader is benign.
        if (killsTarget && !target.Powers.ContainsKey("MinionPower"))
        {
            bool anyMinion = false;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                var e = state.Enemies[i];
                if (e.IsAlive && !ReferenceEquals(e, target)
                    && e.Powers != null && e.Powers.ContainsKey("MinionPower")) { anyMinion = true; break; }
            }
            if (anyMinion)
            {
                const int LeaderKillBonus = 800;
                bonus += LeaderKillBonus;
                details.Add($"leaderKill(clearsMinions)=+{LeaderKillBonus}");
            }
        }

        // CrabRagePower: when an ally dies, the crab gains +6 Strength and +99 Block. Killing
        // OTHER enemies first super-buffs it. Mild penalty for a kill while a different crab-rage
        // enemy is alive — small enough not to block a genuinely needed kill, just a nudge to
        // burst the crab itself or not feed it.
        if (killsTarget && !target.Powers.ContainsKey("CrabRagePower"))
        {
            bool crabAlive = false;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                var e = state.Enemies[i];
                if (e.IsAlive && !ReferenceEquals(e, target)
                    && e.Powers != null && e.Powers.ContainsKey("CrabRagePower")) { crabAlive = true; break; }
            }
            if (crabAlive)
            {
                const int CrabRageFeedPenalty = -600;
                bonus += CrabRageFeedPenalty;
                details.Add($"crabRageFeed(buffsCrab)={CrabRageFeedPenalty}");
            }
        }

        // HatchPower: counter ticks down each enemy turn; on expiry the egg hatches into a
        // (typically worse) creature. A low counter = about to hatch → modest priority to remove
        // it first while it's still a weak egg.
        if (target.Powers.TryGetValue("HatchPower", out var hatch) && hatch > 0 && hatch <= 2
            && incomingDmg > 0 && hpAfter > 0)
        {
            int hatchBonus = w.BlockUnderThreatBonus / 4;
            bonus += hatchBonus;
            details.Add($"hatchSoon(counter{hatch})=+{hatchBonus}");
        }

        // Stun threshold: ShriekPower (TerrorEel), PlowPower (CeremonialBeast).
        int stunThreshold = 0;
        string stunTag = "";
        if (target.Powers.TryGetValue("ShriekPower", out var shriek) && shriek > 0)
        {
            stunThreshold = shriek;
            stunTag = "shriek";
        }
        if (target.Powers.TryGetValue("PlowPower", out var plow) && plow > stunThreshold)
        {
            stunThreshold = plow;
            stunTag = "plow";
        }

        if (stunThreshold > 0 && currentHp > stunThreshold && hpAfter <= stunThreshold && hpAfter > 0)
        {
            // v0.9 — Stun bonus restructured to match the "공격해서 맞지 않는다면
            // 공격" intent. Previously stun was scored as `est × 5` (capped
            // 1500, floor 400) — vs DEFEND scoring +2000 BlockUnderThreatBonus
            // PLUS +1200 BlockNeutralizeBonus when threat is high. Result:
            // DEFEND outscored Stun-via-attack by 4-8× even when stun would
            // negate the SAME enemy attack DEFEND blocks.
            //
            // New formula mirrors the defense side:
            //   • threat-tier base: matches BlockUnderThreatBonus (2000)
            //   • per-dmg block-equivalent: est × BlockPerPointBonus (30)
            //   • plow extra: +150 (Strength removal residual)
            // Cap raised to 3500 — comparable to DEFEND threat+neutralize peak.
            int est = System.Math.Max(15,
                target.IntentDamage * System.Math.Max(1, target.IntentRepeats));
            int stunBonus = w.BlockUnderThreatBonus
                + est * w.BlockPerPointBonus;
            const int StunCap = 3500;
            if (stunBonus > StunCap) stunBonus = StunCap;
            // PlowPower also removes Strength — extra credit for the
            // permanent damage reduction on subsequent attacks.
            if (stunTag == "plow") stunBonus += 150;
            bonus += stunBonus;
            details.Add($"{stunTag}Thresh(th{stunThreshold},hp{currentHp}→{hpAfter},est{est})=+{stunBonus}");
        }
        else if (stunThreshold > 0)
        {
            // Threshold detected but this card doesn't cross it — surface why
            // for diagnostics. Helps verify ShriekPower recognition vs. the
            // separate question of whether any single play actually triggers
            // it. Uses Trace level so it stays out of normal logs but is
            // available when investigating "AI saw Shriek but didn't react."
            string reason;
            if (currentHp <= stunThreshold)
                reason = "alreadyBelow";          // boss already past the cross-line
            else if (incomingDmg <= 0)
                reason = "noDmg";                 // skill / blocked / 0-dmg attack
            else if (hpAfter > stunThreshold)
                reason = $"shortBy{hpAfter - stunThreshold}";   // not enough to cross
            else if (hpAfter <= 0)
                reason = "wouldKill";             // crosses AND kills — stun moot
            else
                reason = "unknown";
            details.Add($"{stunTag}ThreshNoTrig({reason},th{stunThreshold},hp{currentHp}→{hpAfter})");
        }

        // v0.9 — AsleepPower (Lagavulin Matriarch): any unblocked damage wakes
        // the boss AND stuns. While asleep the boss deals 0 damage; waking is
        // when damage opportunity begins. The stun lets the player keep
        // hitting for one more turn before retaliation. Treat the trigger
        // identically to Shriek/Plow — saves one enemy attack-turn equivalent.
        //
        // Note: AsleepPower doesn't have an HP threshold. ANY hit that deals
        // unblocked damage triggers it. We check `incomingDmg > 0` (damage
        // after block) — if that's > 0, this card wakes the boss. Most
        // attacks against a 0-block sleeping boss will land.
        if (target.Powers.TryGetValue("AsleepPower", out var asleep) && asleep > 0
            && incomingDmg > 0)
        {
            int est = System.Math.Max(15,
                target.IntentDamage * System.Math.Max(1, target.IntentRepeats));
            int stunBonus = w.BlockUnderThreatBonus + est * w.BlockPerPointBonus;
            const int AsleepCap = 3500;
            if (stunBonus > AsleepCap) stunBonus = AsleepCap;
            bonus += stunBonus;
            details.Add($"asleepWake(hp{currentHp}→{hpAfter},est{est})=+{stunBonus}");
        }

        // 2026-06-03 — BurrowedPower (잠복). Decompile-verified mechanic:
        //   • ShouldClearBlock(owner)=false  → block PERSISTS across turn starts.
        //   • AfterBlockBroken(owner)         → the moment block reaches 0 the enemy is
        //                                       STUNNED ("BITE_MOVE") and the power is removed.
        // Crucially the break can take SEVERAL hits/turns — block stays chipped because it
        // persists — so any damage into a burrowed enemy's block is PROGRESS toward the stun,
        // not wasted soak. Reward like SlumberPower: full stun bonus for the breaking play,
        // partial credit (proportional to the fraction of block removed) for a chip.
        if (target.Powers.TryGetValue("BurrowedPower", out var burrowed) && burrowed > 0
            && target.Block > 0)
        {
            int est = System.Math.Max(15,
                target.IntentDamage * System.Math.Max(1, target.IntentRepeats));
            int fullStun = w.BlockUnderThreatBonus + est * w.BlockPerPointBonus;
            const int BurrowedCap = 3500;
            if (fullStun > BurrowedCap) fullStun = BurrowedCap;

            if (effectiveTotal >= target.Block && hpAfter > 0)
            {
                // This play strips the whole bar → stun NOW.
                bonus += fullStun;
                details.Add($"burrowedBreak(block{target.Block},raw{effectiveTotal},est{est})=+{fullStun}");
            }
            else if (effectiveTotal > 0)
            {
                // Chip: persistent block means this advances the stun. Credit the fraction
                // of the block bar this play removes.
                int chip = System.Math.Min(effectiveTotal, target.Block);
                int partial = fullStun * chip / target.Block;
                bonus += partial;
                details.Add($"burrowedChip(block{target.Block},chip{chip},est{est})=+{partial}");
            }
        }

        // v0.9 — SlumberPower (Slumbering Beetle): counter starts at Amount,
        // each damage event decrements by 1, stuns when 0. So this attack
        // triggers stun IFF Amount == 1 at start of play. For Amount >= 2,
        // award a partial-progress bonus proportional to "how much we
        // advanced the wake-up state" — same magnitude/Amount but capped
        // so deep counters (Amount=5) don't pay too much for a single hit.
        if (target.Powers.TryGetValue("SlumberPower", out var slumber) && slumber > 0
            && incomingDmg > 0)
        {
            int est = System.Math.Max(15,
                target.IntentDamage * System.Math.Max(1, target.IntentRepeats));
            if (slumber == 1)
            {
                // This hit triggers stun.
                int stunBonus = w.BlockUnderThreatBonus + est * w.BlockPerPointBonus;
                const int SlumberCap = 3500;
                if (stunBonus > SlumberCap) stunBonus = SlumberCap;
                bonus += stunBonus;
                details.Add($"slumberWake(amt{slumber},est{est})=+{stunBonus}");
            }
            else
            {
                // Partial progress: amortize threshold bonus by 1/slumber.
                int partial = (w.BlockUnderThreatBonus + est * w.BlockPerPointBonus) / slumber;
                bonus += partial;
                details.Add($"slumberProgress(amt{slumber}→{slumber - 1},est{est})=+{partial}");
            }
        }

        // Doom kill: HP ≤ Doom at turn end → instakill.
        if (target.Powers.TryGetValue("DoomPower", out var doom) && doom > 0)
        {
            if (hpAfter > 0 && hpAfter <= doom)
            {
                int killBonus = currentHp * w.DamagePerPointBonus / 10;
                const int KillCap = 2000;
                if (killBonus > KillCap) killBonus = KillCap;
                if (killBonus < 800) killBonus = 800;
                bonus += killBonus;
                details.Add($"doomKill(d{doom},hp{currentHp}→{hpAfter})=+{killBonus}");
            }
        }

        return bonus;
    }

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
            // v0.10 — ChemicalX adds +2 X (ModifyXValue, Increase=2). Stacks
            // additively before any threshold-doubler (HEAVENLY_DRILL).
            int xBonus = (state.PlayerRelics != null
                && state.PlayerRelics.ContainsKey("ChemicalX")) ? 2 : 0;
            // 2026-06-02 — X = energy spent. At 0 energy X is 0 (the card does nothing), so do NOT
            // floor at 1: the old Max(1,...) made 0-energy X-cost cards look like they deal damage,
            // and the planner played them with no energy. Real X-cost with 0 energy = wasted.
            int x = System.Math.Max(0, state.PlayerEnergy + xBonus);
            // v0.6.8 — HEAVENLY_DRILL: if X ≥ 4 (threshold stored as Energy:4 var),
            // X doubles. Per game source `if (num >= Energy) num *= 2`. Hardcoded
            // id-check is fine — this is the only card with the threshold-double
            // pattern in v0.103.2.
            // v0.7.87 — Strip CARD. prefix; SimCard.Id is the short entry name.
            if (card.Id == "HEAVENLY_DRILL" && x >= 4)
                x *= 2;
            return x;
        }

        // v0.6.8 — TEAR_ASUNDER: hits = 1 + player HP-loss events this combat.
        // Game source uses CalculatedVar with a multiplier closure that reads
        // CombatHistory at OnPlay time. PreviewValue may or may not invoke it
        // reliably during snapshot, so override here using CombatPlayerHpLossEvents
        // captured in StateSnapshotter (same data source as the game's closure).
        // v0.7.87 — Strip CARD. prefix; SimCard.Id is the short entry name.
        if (card.Id == "TEAR_ASUNDER")
            return 1 + state.CombatPlayerHpLossEvents;

        // Conditional-payoff attacks whose hit count equals cards-played-this-turn.
        // CalculationBase=0, CalculationExtra=1 → Hits = N(skills|attacks) played
        // BEFORE this card in the current turn. Reflection defaults hits=1 when
        // CalculatedHits returns 0, masking premature plays — so override here
        // with the actual turn counters. Returning 0 (allowed by AllowsZeroHits
        // below) makes the base scorer credit 0 damage when no setup happened,
        // pushing the AI to play the setup cards first.
        if (card.Id == "LUNAR_BLAST")
            return state.TurnSkillsPlayed;
        if (card.Id == "FINISHER")
            return state.TurnAttacksPlayed;
        // BARRAGE (Defect, 1c 5dmg): one hit per orb currently slotted.
        // PlayerOrbCount is exposed directly on SimState — use it as the
        // canonical source instead of trusting PreviewValue.
        if (card.Id == "BARRAGE")
            return state.PlayerOrbCount;

        // v0.9.2 — 5 conditional-hits cards backed by SimState counters
        // captured in StateSnapshotter. Each mirrors the card's
        // CalculatedHits multiplier source verified against decompile:
        //   HELIX_DRILL     353179 — EnergySpentEntry sum (this turn)
        //   RADIATE         357507 — StarsModifiedEntry positive-delta sum
        //   PULL_FROM_BELOW 357330 — CardPlayFinishedEntry.WasEthereal (combat)
        //   RATTLE          357678 — CreatureAttackedEntry from player.Osty
        //   FLAK_CANNON     351453 — Status cards in piles excl. Exhaust
        // RATTLE returns 1 + count (its multiplier embeds the +1 base).
        if (card.Id == "HELIX_DRILL")
            return state.TurnEnergySpent;
        if (card.Id == "RADIATE")
            return state.TurnStarsGained;
        if (card.Id == "PULL_FROM_BELOW")
            return state.CombatEtherealPlayed;
        if (card.Id == "RATTLE")
            return 1 + state.TurnOstyAttacks;
        if (card.Id == "FLAK_CANNON")
            return state.PlayerStatusCardCount;

        return 0;
    }

    /// <summary>
    /// Cards whose effective hit count is purely derived from a scaling
    /// trigger (CalculatedHits with CalculationBase=0, CalculationExtra=1).
    /// CardReflection now sets <c>Hits = 0</c> when the trigger hasn't fired
    /// yet (was clamped to 1 prior to v0.9.1); the effHits clamp below honours
    /// that 0 instead of forcing the default min-1 floor so premature plays
    /// score as actual-zero damage.
    ///
    /// All members have explicit EstimateVariableHits overrides backed by
    /// SimState counters captured in StateSnapshotter:
    ///   • LUNAR_BLAST     — skills/turn (TurnSkillsPlayed)
    ///   • FINISHER        — attacks/turn (TurnAttacksPlayed)
    ///   • BARRAGE         — orbs slotted (PlayerOrbCount)
    ///   • FLECHETTES      — skills in hand (ApplyFlechettesHandSkills safety net)
    ///   • HELIX_DRILL     — energy spent this turn (TurnEnergySpent)          [v0.9.2]
    ///   • RADIATE         — stars gained this turn (TurnStarsGained)          [v0.9.2]
    ///   • PULL_FROM_BELOW — ethereal plays this combat (CombatEtherealPlayed) [v0.9.2]
    ///   • RATTLE          — 1 + Osty attacks this turn (TurnOstyAttacks)      [v0.9.2]
    ///   • FLAK_CANNON     — Status cards in piles (PlayerStatusCardCount)     [v0.9.2]
    ///
    /// TEAR_ASUNDER intentionally excluded — its EstimateVariableHits override
    /// returns <c>1 + CombatPlayerHpLossEvents</c>, always ≥ 1, so the base
    /// min-1 clamp is fine.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> _zeroHitsCards =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "LUNAR_BLAST", "FINISHER", "BARRAGE", "FLECHETTES",
        "HELIX_DRILL", "RADIATE", "PULL_FROM_BELOW", "RATTLE", "FLAK_CANNON",
    };

    private static bool AllowsZeroHits(SimCard card)
        => card.Id != null && _zeroHitsCards.Contains(card.Id);

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
        if (!card.Axes.Contains("EXHAUST_BURST") && card.Id != "PURITY") return 0;

        switch (card.Id)
        {
            case "EIDOLON":
            {
                // Need ≥9 hand cards (including self) to fire Intangible.
                int hand = state.Hand.Count;
                const int threshold = 9;
                int keystoneRisk = SumExhaustLossRiskExcludingSelf(card, state);
                if (hand >= threshold)
                {
                    // Approximate IntangiblePower 1 self-buff value. PowerCatalog
                    // values it at ~1500 for permanent stacks; the EIDOLON version
                    // is single-turn (Apparition-like), so scale down. Still
                    // subtract keystone loss — Powers / Retain in hand cost a lot.
                    return 900 - keystoneRisk;
                }
                // Below threshold the card just exhausts the hand — keystone-aware loss.
                return -keystoneRisk;
            }

            case "STOKE":
            {
                // Replaces hand with N random cards. The generation value
                // scales with handExhausted; the keystone loss for cards
                // actually exhausted is subtracted on top.
                int handExhausted = System.Math.Max(0, state.Hand.Count - 1);
                if (handExhausted == 0) return -100;          // no hand → no point
                int keystoneRisk = SumExhaustLossRiskExcludingSelf(card, state);
                return handExhausted * 40 - keystoneRisk;
            }

            case "PURITY":
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
    /// Sum of per-card exhaust loss risk for every hand card except `self`.
    /// Delegates to <see cref="EffectSynergy.EstimateExhaustLossRisk"/>
    /// (Power/Retain/SCALING/Curse weighted) so EIDOLON / STOKE penalty
    /// scales with the actual hand composition instead of a flat per-card
    /// cost.
    /// </summary>
    private static int SumExhaustLossRiskExcludingSelf(SimCard self, SimState state)
    {
        int risk = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            risk += EffectSynergy.EstimateExhaustLossRisk(c);
        }
        return risk;
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

        // v0.9 — Retain big-attack exemption. A Retain attack with high
        // damage (≥12) is the deck's intended finisher / burst tool; the
        // whole point of its Retain keyword is to wait for the right moment
        // to dump all energy into it. Penalising it for "skipping 4 cheap
        // alternatives" works directly against that intent. SOVEREIGN_BLADE
        // (cost 2, d=21~36) was the observed offender — see 2026-05-19 19:37
        // log where d31 SB was beaten by BEAT_INTO_SHAPE d22 across every
        // turn for the entire combat. With this exemption, SB's scoring
        // recovers ~100pt and the planner can finally pick it when it's the
        // highest-damage play available.
        if (card.IsRetain && card.IsAttack && card.Damage >= 12)
            return 0;

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
    /// v0.10 — X-cost card detection. X-cost cards carry the "X_COST" axis
    /// (mirror of <see cref="SimCard.EffectiveDmgPerEnergy"/> line 271).
    /// Their Cost is reported as -1 by the game (StarCost / EnergyCost both
    /// resolve to X at play time), so the lethal-detector treats them by
    /// "consume all remaining energy" semantics.
    /// </summary>
    private static bool IsXCostCard(SimCard c)
        => c.Axes != null && c.Axes.Contains("X_COST");

    /// <summary>
    /// v0.10 — Ordering value for the lethal-detector's greedy attack chain.
    /// X-cost cards rank by full-energy damage potential (Damage × hits at
    /// current energy + ChemicalX bonus); other cards by damage/cost ratio.
    /// Larger value = play first.
    /// </summary>
    private static int OrderingScore(SimCard c, int currentEnergy, int xBonus)
    {
        if (IsXCostCard(c))
        {
            int hits = System.Math.Max(1, currentEnergy + xBonus);
            return c.Damage * hits * 100;
        }
        int costDivisor = c.Cost == 0 ? 1 : System.Math.Max(1, c.Cost);
        return c.TotalDamage * 100 / costDivisor;
    }

    /// <summary>
    /// v0.10 — Greedy "max raw block we can buy with the given energy
    /// budget, excluding one specific card". Used by the block-vs-thorn-
    /// attack scenario penalty so the scoring card itself isn't double-
    /// counted as both attacker and block source.
    ///
    /// Picks by block-per-energy ratio (cost 0 treated as cost 1 for the
    /// ratio so free skills rank by raw block). Returns RAW block — Dex /
    /// Frail / WastedBlock are not applied. Good enough for coarse
    /// scenario comparison (penalty granularity is ±3000).
    /// </summary>
    private static int BestBlockInEnergyBudget(SimState state, int energyBudget, SimCard exclude)
    {
        if (energyBudget <= 0) return 0;
        var blockCards = state.Hand
            .Where(c => !ReferenceEquals(c, exclude)
                        && c.IsPlayable && !c.IsCurseOrStatus
                        && c.Block > 0 && c.Cost >= 0)
            .OrderByDescending(c =>
                c.Block * 100 / System.Math.Max(1, c.Cost == 0 ? 1 : c.Cost))
            .ToList();
        int energy = energyBudget;
        int totalBlock = 0;
        foreach (var c in blockCards)
        {
            if (c.Cost > energy) continue;
            energy -= c.Cost;
            totalBlock += c.Block;
        }
        return totalBlock;
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
    ///
    /// v0.10 — Demotes lethal to false when the thorns reflect cost of the
    /// chosen attack chain would itself kill the player (or push within the
    /// safety margin). Suicide-lethal is not lethal: the planner should
    /// switch to block-only and finish next turn instead.
    /// </summary>
    /// v0.23 Phase 5b — Per-enemy burst-window detector. Returns the set of
    /// enemy indices that the current hand can kill THIS TURN with a greedy
    /// energy-budgeted attack chain routed to that target. Approximation —
    /// no Vigor / Lethality first-attack multiplier, no thorns reflect, no
    /// X-cost / star-cost gating (those are rare in regression cases). The
    /// 1-step PlanScorer uses this to bias attack scores toward burst chains
    /// and defer costly Powers when the chain is viable.
    ///
    /// Why a separate detector (not reuse IsLethalThisTurn): IsLethalThisTurn
    /// checks whether the TOTAL chain kills EVERY enemy. Common regression
    /// case (Chompers, Exoskeletons) has multiple enemies where killing one
    /// at a time over several turns is the right plan — the all-or-nothing
    /// lethal check returns false but burst-windows still exist per-enemy.
    internal static System.Collections.Generic.HashSet<int> FindBurstKillableEnemies(SimState state)
    {
        var killable = new System.Collections.Generic.HashSet<int>();
        if (state.Enemies.Count == 0) return killable;
        bool playerWeak = state.PlayerWeak > 0;

        for (int ti = 0; ti < state.Enemies.Count; ti++)
        {
            var target = state.Enemies[ti];
            if (!target.IsAlive) continue;
            int needed = target.Hp + target.Block;
            if (needed <= 0) continue;

            int energy = state.PlayerEnergy;
            int totalDamage = 0;

            // Greedy damage-per-energy ordering. Single-target attacks
            // routed to `target`; AOE damage counts (also hits other enemies
            // but we only need the per-target sum here).
            var attacks = state.Hand
                .Where(c => c.IsAttack && c.IsPlayable && c.Cost >= 0 && c.Cost <= energy)
                .OrderByDescending(c => OrderingScore(c, energy, 0))
                .ToList();

            foreach (var atk in attacks)
            {
                if (atk.Cost > energy) continue;
                energy -= atk.Cost;
                int hits = System.Math.Max(1, atk.Hits);
                int per = StatusMath.EffectiveAttackDmg(atk.Damage,
                    state.PlayerStrength, 0,
                    target.VulnerableAmount > 0, playerWeak);
                if (target.DamageCapPerHit > 0 && per > target.DamageCapPerHit)
                    per = target.DamageCapPerHit;
                int eachTotal = per * hits;
                eachTotal = StatusMath.ApplyDamageMultipliers(eachTotal, state,
                    defenderVulnerable: target.VulnerableAmount > 0,
                    defenderWeak: target.WeakAmount > 0,
                    lethalityActive: false);
                if (target.HardenedShellRemaining > 0
                    && eachTotal > target.HardenedShellRemaining)
                    eachTotal = target.HardenedShellRemaining;
                totalDamage += eachTotal;
                if (totalDamage >= needed) break;  // early-out
            }

            if (totalDamage >= needed)
                killable.Add(ti);
        }
        return killable;
    }

    /// <summary>
    /// 2026-06-03 — Public wrapper over <see cref="IsLethalThisTurn"/> so external
    /// drivers (sts2-cli full-run potion policy) can ask "can we clear the board
    /// this turn?" before spending a survival potion on a turn we'd win anyway.
    /// </summary>
    public static bool CanKillThisTurn(SimState state) => IsLethalThisTurn(state);

    private static bool IsLethalThisTurn(SimState state)
    {
        int totalEnemyHp = 0;
        foreach (var e in state.Enemies)
            if (e.IsAlive) totalEnemyHp += e.Hp;
        if (totalEnemyHp <= 0) return true;

        int energy = state.PlayerEnergy;
        bool playerWeak = state.PlayerWeak > 0;
        int chemicalXBonus = (state.PlayerRelics != null
            && state.PlayerRelics.ContainsKey("ChemicalX")) ? 2 : 0;

        // v0.10 — Phase 0: stars-budget simulation. Hand may contain skills
        // that generate stars (VENERATE +2, etc.); these unlock star-cost
        // attacks (FALLING_STAR @ 2 stars) for the lethal chain. Without
        // this, IsPlayable=false on star-blocked attacks dropped them
        // wholesale even when their unlocker was right there in hand.
        //
        // Only fires when there's an actual star-blocked attack to unlock,
        // so we don't waste energy on stars-gain skills in non-star hands.
        int simulatedStars = state.PlayerStars;
        bool hasStarBlockedAttack = false;
        foreach (var c in state.Hand)
        {
            if (c.IsPlayable) continue;
            if (!c.IsAttack) continue;
            if (ActionPlanner.StarCostByCardId.ContainsKey(c.Id))
            {
                hasStarBlockedAttack = true;
                break;
            }
        }
        if (hasStarBlockedAttack)
        {
            // Play stars-gain skills cheapest-first to maximize leftover
            // energy for the attack chain. Skip those that exceed remaining
            // energy (no fractional plays).
            var starsGainSkills = state.Hand
                .Where(c => c.IsPlayable && c.StarsGain > 0 && !c.IsAttack)
                .OrderBy(c => c.Cost == 0 ? 1 : System.Math.Max(1, c.Cost))
                .ToList();
            foreach (var sk in starsGainSkills)
            {
                int cost = System.Math.Max(0, sk.Cost);
                if (cost > energy) continue;
                energy -= cost;
                simulatedStars += sk.StarsGain;
            }
        }

        // Greedy damage-per-energy ordering. Cost 0 treated as cost 1 for
        // the ratio so free attacks rank by raw damage. v0.10 — X-cost
        // cards (Axes contains "X_COST", Cost == -1) are folded in: their
        // ordering value uses Damage × currentEnergy (best-case hits if
        // played first), and at play time they consume all remaining
        // energy with hits scaled accordingly. Star-cost attacks unlocked
        // by Phase 0 also qualify.
        var attacks = state.Hand
            .Where(c => c.IsAttack
                        && (IsXCostCard(c) || (c.Cost >= 0 && c.Cost <= energy))
                        && (c.IsPlayable
                            || (ActionPlanner.StarCostByCardId.TryGetValue(c.Id, out int sc)
                                && simulatedStars >= sc)))
            .OrderByDescending(c => OrderingScore(c, energy, chemicalXBonus))
            .ToList();

        int totalReachable = 0;
        // v0.10 — Track thorns reflect cost across the chain. Single-target
        // attacks reflect from the chosen target; AOE attacks reflect from
        // every alive thorny enemy per hit. Multi-hit cards reflect per hit.
        // STS2 thorns is absorbed by player block (decompile-verified). We
        // share a block budget across the chain so block isn't double-counted.
        int totalThornsCost = 0;
        int thornsBlockBudget = state.PlayerBlock;
        // v0.7.82 — Vigor budget. Single-shot: only the FIRST attack in this chain
        // gets the Vigor bonus, subsequent attacks see 0.
        int vigorRemaining = state.PlayerVigor;
        // v0.7.84 — Lethality budget. First attack/turn only.
        bool lethalityRemaining = state.PlayerLethality > 0;
        foreach (var atk in attacks)
        {
            bool xCost = IsXCostCard(atk);
            // X-cost: consume all remaining energy. Other cards: pay listed Cost.
            int effectiveCost = xCost ? energy : atk.Cost;
            if (!xCost && effectiveCost > energy) continue;
            if (xCost && energy <= 0) continue;  // no energy to spend on X-cost
            // v0.10 — Star-cost gate: subtract star cost from simulatedStars
            // budget so multiple star-cost attacks in hand don't all "play"
            // when only N stars are available.
            int starCost = 0;
            if (ActionPlanner.StarCostByCardId.TryGetValue(atk.Id, out int sc2))
                starCost = sc2;
            if (starCost > simulatedStars) continue;
            simulatedStars -= starCost;
            energy -= effectiveCost;
            int useVigor = vigorRemaining;
            vigorRemaining = 0;
            bool useLethality = lethalityRemaining;
            lethalityRemaining = false;

            // v0.10 — X-cost hits = spent energy + ChemicalX bonus (mirror of
            // SimCard.EffectiveDmgPerEnergy line 270-281). Non-X cards use
            // their static Hits.
            int hits = xCost
                ? System.Math.Max(1, effectiveCost + chemicalXBonus)
                : System.Math.Max(1, atk.Hits);
            if (atk.Target == TargetType.AllEnemies)
            {
                foreach (var e in state.Enemies)
                {
                    if (!e.IsAlive) continue;
                    int per = StatusMath.EffectiveAttackDmg(atk.Damage,
                        state.PlayerStrength, useVigor, e.VulnerableAmount > 0, playerWeak);
                    if (e.DamageCapPerHit > 0 && per > e.DamageCapPerHit)
                        per = e.DamageCapPerHit;
                    int eachTotal = per * hits;
                    // v0.7.84 — Damage multipliers per enemy.
                    eachTotal = StatusMath.ApplyDamageMultipliers(eachTotal, state,
                        defenderVulnerable: e.VulnerableAmount > 0,
                        defenderWeak: e.WeakAmount > 0, lethalityActive: useLethality);
                    if (e.HardenedShellRemaining > 0
                        && eachTotal > e.HardenedShellRemaining)
                        eachTotal = e.HardenedShellRemaining;
                    totalReachable += eachTotal;
                    // v0.10 — Thorns reflect: AOE attack reflects per hit
                    // from every alive thorny enemy. STS2 thorns is absorbed
                    // by block (decompile + empirical verified); simulate
                    // per-hit block soak against the shared chain budget.
                    if (e.ThornsAmount > 0)
                    {
                        for (int r = 0; r < hits; r++)
                        {
                            int absorbed = System.Math.Min(e.ThornsAmount, thornsBlockBudget);
                            thornsBlockBudget -= absorbed;
                            totalThornsCost += e.ThornsAmount - absorbed;
                        }
                    }
                }
            }
            else
            {
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
                    state.PlayerStrength, useVigor, bestEnemy.VulnerableAmount > 0, playerWeak);
                if (bestEnemy.DamageCapPerHit > 0 && per > bestEnemy.DamageCapPerHit)
                    per = bestEnemy.DamageCapPerHit;
                int eachTotal = per * hits;
                // v0.7.84 — Damage multipliers.
                eachTotal = StatusMath.ApplyDamageMultipliers(eachTotal, state,
                    defenderVulnerable: bestEnemy.VulnerableAmount > 0,
                    defenderWeak: bestEnemy.WeakAmount > 0, lethalityActive: useLethality);
                totalReachable += eachTotal;
                // v0.10 — Single-target thorns reflect: block-absorbed per hit
                // from the shared chain budget.
                if (bestEnemy.ThornsAmount > 0)
                {
                    for (int r = 0; r < hits; r++)
                    {
                        int absorbed = System.Math.Min(bestEnemy.ThornsAmount, thornsBlockBudget);
                        thornsBlockBudget -= absorbed;
                        totalThornsCost += bestEnemy.ThornsAmount - absorbed;
                    }
                }
            }
        }

        if (totalReachable < totalEnemyHp) return false;

        // v0.10 — Suicide-lethal guard. If the thorns reflect of this lethal
        // chain itself kills the player (or leaves them within SafetyMargin),
        // it isn't lethal — call the chain off so the planner switches to
        // block-only. Safety margin via Balanced weights (JSON-tunable, default 4).
        var balanced = PlanScorerWeights.Balanced;
        if (totalThornsCost >= state.PlayerHp - balanced.ThornsSuicideLethalHpMargin) return false;
        return true;
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

            // v0.9 — Intent-saved bonus: killing an enemy with an attack
            // intent removes their next turn's damage. Scale by the saved
            // intent dmg (×BlockPerPointBonus equivalent) capped at +2000.
            // Especially valuable in multi-enemy fights where killing the
            // big-attacker first matters most. Buff/Heal/Summon intents
            // also worth killing — fold via additional flat bonuses.
            if (target.HasAttackIntent && target.IntentDamage > 0)
            {
                int saved = target.IntentDamage * System.Math.Max(1, target.IntentRepeats);
                int intentBonus = saved * w.BlockPerPointBonus;
                const int IntentSaveCap = 2000;
                if (intentBonus > IntentSaveCap) intentBonus = IntentSaveCap;
                s += intentBonus;
                parts.Add($"killIntentSave({saved}dmg)=+{intentBonus}");
            }
            if (target.HasBuffIntent)    { s += 600; parts.Add("killBuffer+600"); }
            if (target.HasHealIntent)    { s += 800; parts.Add("killHealer+800"); }
            if (target.HasSummonIntent)  { s += 700; parts.Add("killSummoner+700"); }
            if (target.HasDebuffIntent)  { s += 400; parts.Add("killDebuffer+400"); }
            // v0.9.4 — SandpitPower carrier kill (The Insatiable). The power's
            // counter ticks at AfterSideTurnStartLate(Enemy); when it transitions
            // to 0 the AfterRemoved hook force-kills player + pets + Osty
            // regardless of HP/revive. Killing the carrier averts the loss.
            // Magnitude scales inverse to remaining stacks — smaller = more urgent.
            if (target.SandpitAmount > 0)
            {
                int sandpitBonus = target.SandpitAmount switch
                {
                    1   => 4000,
                    2   => 2500,
                    3   => 1500,
                    _   => 800,
                };
                s += sandpitBonus;
                parts.Add($"killSandpit(stk={target.SandpitAmount})=+{sandpitBonus}");
            }
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
    /// hand-quality logic. MachineLearningPower (per-turn perma draw) and
    /// DrawCardsNextTurnPower (one-shot next-turn draw) are scored by their
    /// PowerCatalog baseline + the pile-aware tick handlers in EffectSynergy
    /// (ApplyMachineLearningTickValue / ApplyDrawCardsNextTurnTickValue), so
    /// the hand-quality bonus here would double-credit. Conservative scope
    /// avoids the risk of mis-categorising those as immediate draws.
    ///
    /// v0.5 — energy-after-draw + hand-cap checks:
    ///   • Playing a 1-cost draw with 1 energy leaves 0 energy. Unless a 0-cost or
    ///     energy-gain card is queued in hand to bridge, the drawn cards are
    ///     next-turn-only — score discounted or penalised by remaining-hand quality.
    ///   • Drawing past the 10-card hand cap wastes the overflow — penalty scales
    ///     with the wasted fraction.
    /// </summary>
    private static int EvaluateDrawCard(SimCard card, SimState state, PlanScorerWeights w)
        => EvaluateDrawCard(card, state, w, out _);

    private static int EvaluateDrawCard(SimCard card, SimState state, PlanScorerWeights w, out int rescueComponent)
    {
        rescueComponent = 0;
        if (card.DrawCount <= 0) return 0;

        // v0.9 — NoDrawPower shutdown: player can't draw cards THIS turn.
        // Any draw effect is wasted — return a strong negative so the planner
        // skips draw cards entirely. Magnitude matches DrawEmptyPilePenalty.
        if (state.PlayerNoDraw > 0) return w.DrawEmptyPilePenalty;

        // v0.9 — Hand size cap (STS2 max = 10) early-out. When hand is at
        // or above cap, every drawn card is discarded directly. Reject.
        // (Partial overflow is handled later at line ~2482 via handBonus
        // wasted-frac reduction; this just short-circuits the worst case.)
        if (state.Hand.Count >= 10) return w.DrawEmptyPilePenalty;

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

        // v0.10 — Crisis draw rescue. Two effects:
        //   (a) Override the strong-hand idle penalty when the "strong" card in
        //       hand still can't cover this turn's incoming damage.
        //   (b) Add a block-equivalent rescue bonus so a draw card rises above
        //       straight defense when defense alone leaves us under HP threshold.
        // The bonus only fires when the pile actually contains block cards —
        // drawing into a curse/strike-only pile doesn't save you.
        int rescueBonus = ComputeDrawRescueBonus(card, state, w);
        rescueComponent = rescueBonus;

        int handBonus;
        if (bestOtherScore < w.HandUselessThreshold) handBonus = w.DrawHandUselessBonus;
        else if (bestOtherScore < w.HandWeakThreshold) handBonus = w.DrawHandWeakBonus;
        else if (bestOtherScore >= w.HandStrongThreshold)
            handBonus = rescueBonus > 0 ? w.DrawNoCostBottleneckBonus : w.DrawIdlePenalty;
        else handBonus = w.DrawNoCostBottleneckBonus;

        handBonus += rescueBonus;

        // v0.10 — Hand-pollution boost. Status / curse cards sitting in hand
        // with OnTurnEndInHand effects (INFECTION = 3 self-dmg / turn / card)
        // bleed HP every turn they linger. They also occupy hand slots, so the
        // remaining playable surface is smaller than Hand.Count suggests. A
        // fresh draw has two compounded benefits: (a) likely a non-status
        // card replacing a dead slot, (b) pulls block/utility cards that can
        // absorb the bleed (the rescue path above only fires when the bleed
        // crosses a hard HP threshold — this covers the grey zone below it).
        //
        // Skip when there's no bleed or no pollution (clean hand needs no
        // boost — STATUS_CONSUMER cards handle the "pollution but no bleed"
        // case via their own +180/status logic).
        if (state.PlayerHandTurnEndDamage > 0)
        {
            int statusInHand = 0;
            for (int i = 0; i < state.Hand.Count; i++)
            {
                var c = state.Hand[i];
                if (ReferenceEquals(c, card)) continue;
                if (c.IsCurseOrStatus) statusInHand++;
            }
            if (statusInHand > 0)
            {
                // 150 per status × DrawCount, capped at 600 — order of
                // magnitude below BlockUnderThreatBonus (2000) so this nudges
                // rather than overrides defense in true-crisis turns.
                int pollutionBoost = statusInHand * 150 * System.Math.Max(1, card.DrawCount);
                const int PollutionBoostCap = 600;
                if (pollutionBoost > PollutionBoostCap) pollutionBoost = PollutionBoostCap;
                handBonus += pollutionBoost;
            }
        }

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
        int effectiveDraws = card.DrawCount;
        if (card.DrawCount > 0)
        {
            int handAfterPlay = state.Hand.Count - 1;  // self consumed
            int wasted = (handAfterPlay + card.DrawCount) - HandSizeCap;
            if (wasted > 0)
            {
                int wastedFrac = System.Math.Min(100, (wasted * 100) / card.DrawCount);
                handBonus -= (System.Math.Abs(handBonus) * wastedFrac) / 100;
                effectiveDraws = System.Math.Max(0, card.DrawCount - wasted);
            }
        }

        // Pile-aware EV: drawn cards come from DrawPile first; once exhausted
        // the discard pile reshuffles in. Adds a quality signal to the hand-
        // state heuristic above — finisher-rich pile pushes draw value up,
        // curse/Strike-heavy pile drags it down. Each card's
        // EstimateCardPower is treated as "if played" value; we discount
        // because (a) we don't know which specific cards land, (b) drawn
        // cards typically resolve next turn or later.
        int pileEv = EstimatePileDrawEv(effectiveDraws, state);

        return handBonus + pileEv;
    }

    /// <summary>
    /// v0.10 — Crisis rescue bonus for normal pile-draw cards (RESTLESSNESS,
    /// CHILD_OF_THE_STARS, SEEKER_STRIKE etc. — anything with DrawCount &gt; 0
    /// that pulls from the player's own deck). Returns 0 in non-crisis turns.
    ///
    /// Fires only when ALL of:
    ///   • Incoming enemy damage exceeds (current block + playable hand block)
    ///   • Resulting HP post-leak ≤ DrawRescueHpThreshold (we're about to die)
    ///   • Pile (DrawPile + DiscardPile) actually contains block cards — user
    ///     insight: drawing into a curse/Strike-only pile doesn't save us
    ///
    /// Magnitude = min(shortfall, expected drawn block) × BlockPerPointBonus
    /// — block-equivalent score in the same unit as DEFEND's block5*30 = 150.
    ///
    /// Generator cards (QUASAR / MIRACLE that pull from external colorless
    /// pool, not the deck) are handled separately and bypass this function
    /// via the IsDrawCard / DrawCount &lt;= 0 early-out.
    /// </summary>
    private static int ComputeDrawRescueBonus(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (card.DrawCount <= 0) return 0;

        int leak = EnemyTurnSimulator.PredictPlayerDmg(state);
        if (leak <= 0) return 0;

        int handBlockCap = SumPlayableBlockExcluding(state, card);
        int shortfall = leak - state.PlayerBlock - handBlockCap;
        if (shortfall <= 0) return 0;

        int hpAfter = state.PlayerHp - shortfall;
        if (hpAfter > w.DrawRescueHpThreshold) return 0;

        // Pile must have at least one block card AND non-zero total block —
        // user insight: drawing into a curse-only pile doesn't save you.
        int pileBlockSum = 0;
        int pileCount = 0;
        for (int i = 0; i < state.DrawPile.Count; i++)
        {
            pileBlockSum += state.DrawPile[i].Block;
            pileCount++;
        }
        for (int i = 0; i < state.DiscardPile.Count; i++)
        {
            pileBlockSum += state.DiscardPile[i].Block;
            pileCount++;
        }
        if (pileCount == 0 || pileBlockSum <= 0) return 0;

        // Expected block per drawn card = pile mean block. Cap by shortfall —
        // can't recover more than we actually lose, so a curse-heavy pile with
        // one mega-block card doesn't get over-credited.
        int avgBlockPerCard = pileBlockSum / pileCount;
        int expectedNewBlock = card.DrawCount * avgBlockPerCard;
        int blockRecovered = System.Math.Min(shortfall, expectedNewBlock);
        if (blockRecovered <= 0) return 0;

        return blockRecovered * w.BlockPerPointBonus;
    }

    /// <summary>
    /// v0.10 — True when the card adds cards to the hand from an external
    /// pool (game's colorless / random pool), NOT the player's own deck.
    /// Examples: QUASAR (random Upgraded Colorless), MIRACLE-class boosts,
    /// HELLO_WORLD / JACKPOT / CREATIVE_AI (CARD_GEN axis in catalog).
    ///
    /// Distinct from <c>IsDrawCard</c> (DrawCount &gt; 0) which pulls from
    /// the player's own DrawPile + DiscardPile. Generators have separate
    /// rescue / EV modeling because their output isn't constrained by the
    /// player's current deck composition.
    ///
    /// Identification: CARD_GEN axis from cards_catalog.json (preferred),
    /// or hard-coded ID fallback for cards not yet axis-tagged. To extend
    /// without touching code, use the axis-tagger workflow to add CARD_GEN
    /// to the card's axes in card_axis_overrides.json.
    /// </summary>
    private static bool IsCardGenerator(SimCard card)
    {
        if (card.Axes != null)
        {
            for (int i = 0; i < card.Axes.Count; i++)
                if (card.Axes[i] == "CARD_GEN") return true;
        }
        // ID fallback — cards whose generator nature is unambiguous from
        // their effect text but whose catalog tag hasn't been updated yet.
        // QUASAR ("Choose 1 of 3 random Upgraded Colorless cards to add
        // into your Hand") is the prototypical case from the 2026-05-20
        // boss-fight diagnostic; tagged SCALING / RANDOM in catalog as of
        // game v0.103.2 but functionally a card generator.
        switch (card.Id)
        {
            case "CARD.QUASAR":
            case "CARD.QUASAR+":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Greedy by cost: estimate playable block this turn from the hand,
    /// excluding the card being evaluated. Picks cheaper block cards first
    /// so the result reflects the realistic ceiling under current energy,
    /// not an "infinite energy" upper bound.
    /// </summary>
    private static int SumPlayableBlockExcluding(SimState state, SimCard exclude)
    {
        var candidates = new System.Collections.Generic.List<SimCard>(state.Hand.Count);
        foreach (var c in state.Hand)
        {
            if (System.Object.ReferenceEquals(c, exclude)) continue;
            if (!c.IsPlayable || c.IsCurseOrStatus) continue;
            if (c.Block <= 0) continue;
            candidates.Add(c);
        }
        candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));

        int energy = state.PlayerEnergy;
        int totalBlock = 0;
        foreach (var c in candidates)
        {
            if (c.Cost > energy) break;
            energy -= c.Cost;
            totalBlock += c.Block;
        }
        return totalBlock;
    }

    /// <summary>
    /// Expected total value of <paramref name="drawCount"/> cards drawn from
    /// the player's piles. STS reshuffle rules:
    ///   • First min(drawCount, DrawPile.Count) cards come from DrawPile.
    ///   • Remainder come from DiscardPile after a reshuffle.
    /// Each pile contributes its mean per-card value × the number drawn from
    /// it. Heavy 30% discount because we don't know identity of drawn cards
    /// and they generally resolve later than this turn. Caps to avoid
    /// flooding the score when a 5+ draw lands on a high-EV deck.
    /// </summary>
    internal static int EstimatePileDrawEv(int drawCount, SimState state)
    {
        if (drawCount <= 0) return 0;
        if (state.DrawPile.Count + state.DiscardPile.Count == 0) return 0;

        int fromDraw = System.Math.Min(drawCount, state.DrawPile.Count);
        int fromDiscard = drawCount - fromDraw;

        int drawMean = PileMeanCardPower(state.DrawPile, state);
        int discardMean = fromDiscard > 0
            ? PileMeanCardPower(state.DiscardPile, state)
            : 0;

        int total = fromDraw * drawMean + fromDiscard * discardMean;
        // Discount + cap.
        int v = total * 30 / 100;
        const int Cap = 600;
        if (v > Cap) v = Cap;
        if (v < -Cap) v = -Cap;
        return v;
    }

    private static int PileMeanCardPower(
        System.Collections.Generic.IReadOnlyList<SimCard> pile, SimState state)
    {
        if (pile == null || pile.Count == 0) return 0;
        int sum = 0;
        int cnt = 0;
        for (int i = 0; i < pile.Count; i++)
        {
            // EstimateCardPower returns a curse-floor value (CurseInHand) for
            // curses/status, so they naturally drag the mean down for polluted
            // decks — no separate filtering needed.
            sum += EffectSynergy.EstimateCardPower(pile[i], state, freeUse: false);
            cnt++;
        }
        return cnt > 0 ? sum / cnt : 0;
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

        // Enemy NoEnergyGainPower (ModifyEnergyGain → 0) blocks energy-gain cards: Bloodletting/
        // Adrenaline etc. produce no energy while it's active, so their tempo value evaporates.
        // Captured as PlayerNoEnergyGain but was previously ignored here → energy cards stayed
        // over-valued under the debuff. Treat the gain as wasted.
        if (state.PlayerNoEnergyGain > 0) return w.EnergyGainWastedPenalty;

        int immediateGain = card.EnergyGain;

        int remainingEnergy = System.Math.Max(0, state.PlayerEnergy - card.Cost);
        int afterGain = remainingEnergy + immediateGain;

        // 2026-06-03 — count cards playable NOW or only blocked by affordability that THIS
        // card's energy gain resolves. IsPlayable folds in CanPlay()'s energy check, so the
        // expensive payoff an energy card exists to unlock (e.g. THE_BOMB at 0 energy) reads
        // !IsPlayable — exactly the card the gain enables. The old predicate counted only
        // IsPlayable, so in that scenario otherPlayable was empty → returned -1500 and the
        // planner never played the energy card even though gaining energy would let us use it.
        // (Sibling of the EnumerateCandidates affordable-after-gain fix; the filter kept the
        // energy card as a candidate but this value path still buried it at -1500.)
        var otherPlayable = state.Hand
            .Where(c => !ReferenceEquals(c, card) && !c.IsCurseOrStatus && c.Cost >= 0
                       && (c.IsPlayable || c.Cost <= afterGain))
            .ToList();
        if (otherPlayable.Count == 0) return -1500;

        // 2026-06-02 — value energy gain by TEMPO (it lets you play more cards this turn), not only
        // by "unlocking" one specific expensive card. The old logic penalised any energy card that
        // didn't cross an exact affordability threshold, so the planner under-used energy cards.
        //
        // Energy-constrained = you can't already afford every playable card in hand. Only then does
        // extra energy do anything; if you can already play everything, the gain overflows (wasted).
        int totalOtherCost = otherPlayable.Sum(c => System.Math.Max(0, c.Cost));
        int shortfall = totalOtherCost - remainingEnergy;
        if (shortfall <= 0) return w.EnergyGainWastedPenalty;   // can already play it all → overflow

        int unlocked = otherPlayable.Count(c => c.Cost > remainingEnergy && c.Cost <= afterGain);

        // Energy that actually gets spent (capped by the shortfall) × a per-energy tempo value,
        // plus the urgent-unlock bonus when nearly tapped out and the gain frees a stuck card.
        int usable = System.Math.Min(immediateGain, shortfall);
        int v = usable * 120;
        if (unlocked > 0 && remainingEnergy <= 1) v += w.EnergyGainUrgentBonus;
        return v;
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
