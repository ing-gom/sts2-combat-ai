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
            delta += w.EtherealPlayNowBonus;
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
            // v0.2.6 — Energy gain context: low energy + expensive cards waiting → urgent.
            int energyBonus = EvaluateEnergyGain(card, state, w);
            if (energyBonus != 0) details.Add($"energyCtx={energyBonus}");

            // v0.2.6 — Fight-length context: powers shine in long fights, waste in short.
            int fightCtx = EvaluatePowerFightContext(state, w);
            if (fightCtx != 0) details.Add($"fightCtx={fightCtx}");

            var (powerOrbBonus, powerOrbDetail) = EvaluateOrbEffects(card, state);
            if (powerOrbBonus != 0) details.Add(powerOrbDetail);

            if (buildBonus != 0) details.Add($"buildSyn={buildBonus}");
            if (overrideBonus != 0) details.Add($"override={overrideBonus}");
            buildBonus += overrideBonus;
            int total = baseBonus + effect + costTie + energyBonus + fightCtx + powerOrbBonus + buildBonus;
            return new ScoreBreakdown(total, "Power",
                Base: baseBonus + costTie,
                Effect: effect + energyBonus + fightCtx + powerOrbBonus + buildBonus,
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
            int effectiveTotal = effectivePerHit * System.Math.Max(1, card.Hits);

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
                        card.Damage, card.Hits, state.PlayerStrength, e, playerIsWeak);
                    if (rawPer > 0 && e.DamageCapPerHit > 0 && rawPer > e.DamageCapPerHit) capsHit++;
                    if ((e.HardenedShellRemaining > 0 && rawPer * System.Math.Max(1, card.Hits) > e.HardenedShellRemaining)
                        || (rawPer > 0 && e.HardenedShellRemaining == 0
                            && e.Powers.ContainsKey("HardenedShellPower"))) shellHit++;
                    aggregatedDmg += perEnemyTotal;
                }
                effect = aggregatedDmg * w.DamagePerPointBonus;
                dmgLabel = aggregatedDmg != card.TotalDamage * System.Math.Max(1, aliveCount)
                    ? $"eff{aggregatedDmg}(base{card.TotalDamage}×{aliveCount})"
                    : $"dmg{aggregatedDmg}";
                var clampTags = (capsHit > 0 ? $",cap×{capsHit}" : "")
                              + (shellHit > 0 ? $",shell×{shellHit}" : "");
                details.Add(aliveCount > 1
                    ? $"{dmgLabel}*{w.DamagePerPointBonus}*aoe{aliveCount}={effect}{clampTags}"
                    : $"{dmgLabel}*{w.DamagePerPointBonus}={effect}{clampTags}");
            }
            else
            {
                effect = effectiveTotal * w.DamagePerPointBonus;
                dmgLabel = effectiveTotal != card.TotalDamage
                    ? $"eff{effectiveTotal}(base{card.TotalDamage})"
                    : $"dmg{card.TotalDamage}";
                details.Add($"{dmgLabel}*{w.DamagePerPointBonus}={effect}");
            }

            int attached = 0;
            foreach (var (powerName, amount) in card.PowerApps)
            {
                // AOE attaches debuff to every enemy too. Apply stack curve once,
                // then multiplier for AOE breadth.
                int perEnemy = (int)(PowerCatalog.ValueEnemyDebuff(powerName, amount) * w.AttachedDebuffMultiplier);

                // v0.2.9 — Artifact blocks our enemy debuffs. Per-enemy gating:
                // count alive enemies whose ArtifactAmount blocks at least one stack.
                if (isAoe)
                {
                    int reach = state.Enemies.Count(e => e.IsAlive && e.ArtifactAmount < amount);
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
                        && state.Enemies[targetIdx].ArtifactAmount >= amount;
                    if (blockedSingle)
                    {
                        details.Add($"+{Short(powerName)}({amount})=BLOCKED");
                        // perEnemy = 0 — Artifact fully absorbs this debuff stack
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
            if (isAoe)
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
            int thornsPenalty = 0;
            int hits = System.Math.Max(1, card.Hits);
            if (isAoe)
            {
                int aliveThorns = state.Enemies.Where(e => e.IsAlive).Sum(e => e.ThornsAmount);
                if (aliveThorns > 0)
                {
                    thornsPenalty = -aliveThorns * hits * 100;
                    details.Add($"THORNS_AOE{thornsPenalty}");
                }
            }
            else if (targetIdx >= 0 && targetIdx < state.Enemies.Count)
            {
                int thorns = state.Enemies[targetIdx].ThornsAmount;
                if (thorns > 0)
                {
                    thornsPenalty = -thorns * hits * 100;
                    details.Add($"THORNS{thornsPenalty}");
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

            // v0.5 — attack cards can also have draw (DrawCardPower in PowerApps,
            // Mind Blast / cycle-attack hybrids). Same pattern: EvaluateDrawCard
            // returns 0 for non-draw cards, so this is a no-op for plain attacks.
            int atkDrawBonus = EvaluateDrawCard(card, state, w);
            if (atkDrawBonus != 0) details.Add($"drawCtx={atkDrawBonus}");

            int total = baseBonus + effect + attached + targetBonus + wastedPenalty + thornsPenalty + burstBonus + atkOrbBonus + buildBonus + atkEnergyBonus + atkDrawBonus;
            return new ScoreBreakdown(total, isAoe ? "Attack-AOE" : "Attack",
                Base: baseBonus,
                Effect: effect + attached + burstBonus + atkOrbBonus + thornsPenalty + buildBonus + atkEnergyBonus + atkDrawBonus,
                TargetBonus: targetBonus + wastedPenalty, ThreatBonus: 0,
                Details: string.Join(",", details));
        }

        // Skill
        {
            int baseBonus = w.SkillBaseBonus + cost * (w.CostMultiplier / 4);
            // v0.2.4 — effective block: (base + Dexterity) × Frail
            int effectiveBlock = StatusMath.EffectiveBlock(card.Block,
                state.PlayerDexterity, state.PlayerFrail > 0);
            int effect = effectiveBlock * w.BlockPerPointBonus;
            details.Add($"skillBase={w.SkillBaseBonus}");
            if (card.Block > 0)
            {
                string blockLabel = effectiveBlock != card.Block
                    ? $"eff{effectiveBlock}(base{card.Block})"
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
                    //   • Artifact gating: per-target Artifact absorbs the stack and
                    //     should zero out that target's contribution. AOE reduces by
                    //     count of enemies whose Artifact blocks the stack.
                    int per = PowerCatalog.ValueEnemyDebuff(powerName, amount);
                    if (skillIsAoe)
                    {
                        int reach = state.Enemies.Count(e => e.IsAlive && e.ArtifactAmount < amount);
                        int blocked = state.Enemies.Count(e => e.IsAlive) - reach;
                        v = per * reach;
                        powerEffect += v;
                        details.Add(blocked > 0
                            ? $"{Short(powerName)}({amount})→aoe×{reach}={v} (artif-blk={blocked})"
                            : $"{Short(powerName)}({amount})→aoe×{reach}={v}");
                    }
                    else if (targetIdx >= 0 && targetIdx < state.Enemies.Count
                             && state.Enemies[targetIdx].ArtifactAmount >= amount)
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
            int total = baseBonus + effect + powerEffect + threatBonus + wastedBlock + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus;
            return new ScoreBreakdown(total, "Skill",
                Base: baseBonus,
                Effect: effect + powerEffect + energyBonus + drawBonus + skillOrbBonus + enragePenalty + buildBonus,
                TargetBonus: wastedBlock, ThreatBonus: threatBonus,
                Details: string.Join(",", details));
        }
    }

    private static bool IsSelfTargetedTarget(TargetType t)
        => t == TargetType.Self || t == TargetType.AnyAlly
        || t == TargetType.AnyPlayer || t == TargetType.AllAllies;

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

        // v0.5 — poison-lethal short-circuit. Target dies to its own poison at start
        // of its next turn, before any intent fires. Skip ALL intent / state bonuses
        // (buff-stop / heal-deny / etc.) — none of those triggers can land if the
        // enemy is dead by the time their turn starts. Heavy flat penalty so live
        // enemies always win target priority when one exists.
        if (target.PoisonAmount > 0 && target.PoisonAmount >= target.Hp)
            return (w.PoisonLethalPenalty, $"tgt:poisonLethal{w.PoisonLethalPenalty}");

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

    /// <summary>
    /// v0.2.6 — Draw-card value. Drawing is valuable when the rest of the hand can't do
    /// much. We measure the BEST score among the other cards in the hand and the size of
    /// the draw pile (no point drawing from an empty pile).
    ///
    /// v0.2.9 — pile-aware: if DrawPileSize+DiscardPileSize == 0 → drawing is futile.
    /// v0.5 — only THIS-turn draws (DrawCount > 0 or DrawCardPower in PowerApps) use the
    /// hand-quality logic. Cards with DrawCardsNextTurnPower (Machine Learning) draw
    /// NEXT turn — current hand quality is irrelevant; PowerCatalog already values them
    /// at 900/stack. Returning 0 here avoids double-credit of "weak hand → +1500 bonus".
    /// </summary>
    private static int EvaluateDrawCard(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (!card.IsDrawCard) return 0;
        bool thisTurnDraw = card.DrawCount > 0 || card.PowerApps.ContainsKey("DrawCardPower");
        if (!thisTurnDraw) return 0;

        // v0.2.9 — pile guard: nothing to draw means no value.
        int totalPile = state.DrawPileSize + state.DiscardPileSize;
        if (totalPile == 0) return w.DrawEmptyPilePenalty;

        // Find the max score among other cards in hand.
        int bestOtherScore = int.MinValue;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, card)) continue;
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

        // v0.5 — only this-turn energy gain (EnergyVar / IsEnergyGainCard via EnergyGain > 0)
        // is evaluated for "unlock waiting big cards" logic. Next-turn energy gain
        // (EnergyNextTurnPower like Berserk's recurring +1, EnergizedPower) doesn't help
        // this turn's playability, so the unlock / urgent / waste checks don't apply —
        // the card's value is already in PowerCatalog (1500 per stack for EnergyNextTurnPower).
        // Returning 0 here avoids double-penalising Berserk-style cards as "wasted gain".
        if (card.EnergyGain <= 0) return 0;

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
        int afterGain = remainingEnergy + card.EnergyGain;
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
}
