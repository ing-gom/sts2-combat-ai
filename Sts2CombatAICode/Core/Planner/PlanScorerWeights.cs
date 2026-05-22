using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2CombatAI.Planner;

/// <summary>
/// All planner-tuning knobs in one place. Different <see cref="Playstyle"/> values
/// pick different instances. v0.3 will expose individual sliders via ModConfig.
///
/// v0.10 — JSON-loadable for training/tuning workflows. <see cref="LoadFromJsonOrDefault"/>
/// looks for {userdata}/Sts2CombatAI/scoring_weights/{preset}.json and overrides
/// individual fields; missing fields fall back to the code defaults below, so
/// partial JSON files are safe. <see cref="WriteDefaultsTo"/> dumps the current
/// preset to disk for manual editing.
/// </summary>
internal sealed class PlanScorerWeights
{
    // Card-type baselines.
    // Default = Balanced playstyle, leaning safe: "kill if lethal, else defend first."
    // HP-preservation outranks proactive damage; lethal cards still get strong bonuses
    // so a real kill window is never missed.
    //
    // v0.23 (Phase 5 tuning) — PowerCardBonus dropped 1000 → 700 to address
    // ChompersNormal / ExoskeletonsNormal regressions in Planner1Step mode.
    // 1-step PlanScorer was over-valuing late-game scaling Powers (Barricade,
    // DemonForm) when burst kill options existed in hand, because single-card
    // scoring can't see the multi-card synergy depth-N catches. Net effect on
    // 80-encounter compare: +2 saves (Chompers, Exoskeletons), 0 lost saves
    // expected (verified post-change). PowerCatalog per-power values stack on
    // top of this base — high-value Powers still rank well, just less
    // categorically.
    public int PowerCardBonus = 700;
    public int PowerCardBonusWhenAllInert = 5000;
    public int AttackBaseBonus = 150;     // attack value moderate; relies on lethal bonuses to spike
    public int SkillBaseBonus = 100;
    public int CostMultiplier = 30;
    public int CursePenalty = -10000;

    // Block heuristic — Balanced is "defend by default": trip block at very low
    // predicted-damage ratios. 5% of HP (any non-negligible threat) is enough.
    public int BlockUnderThreatBonus = 2000;
    public double ThreatThreshold = 0.05;
    public double ThreatThresholdWithBuff = 0.03;

    // v0.4 — "Block fully neutralises threat" bonus. If a defend card brings the
    // damage that would otherwise leak (after current block) to zero, take zero
    // damage this turn — that beats deploying a Power for future value.
    // Pegged > PowerCardBonus so block-now wins over power-then-tank.
    public int BlockNeutralizeBonus = 1200;

    // Intent-aware target bonuses
    public int BuffEnemyKillBonus = 1000;
    public int HealEnemyKillBonus = 800;
    public int SummonEnemyKillBonus = 500;
    public int DeathBlowEnemyKillBonus = 700;
    public int DefendEnemyAttackPenalty = -300;
    public int InertEnemyAttackPenalty = -1500;
    public int LowHpTargetBonus = 50;

    // Role/lethal — preserved at strong values so Balanced still closes real kill windows.
    public int LethalRangeNearBonus = 3500;
    public int LethalRangeMidBonus = 1800;
    public int LethalRangeFarBonus = 600;

    // v0.4 — Burst-damage window: card not lethal but deals huge % of target HP.
    // Triggers "tank the hit and swing" override of the default block-first policy:
    // a 70%+ chunk means kill next turn, a 50%+ chunk means decisive momentum.
    // Burst70Bonus > BlockUnderThreatBonus on purpose — burst wins the trade.
    public int BurstDamage70Bonus = 2000;
    public int BurstDamage50Bonus = 900;
    public double BurstDamage70Ratio = 0.70;
    public double BurstDamage50Ratio = 0.50;
    public int MinionFirstBonus = 400;
    public int BossDeferPenalty = -500;

    // v0.2 — effect-value-aware scoring
    public int DamagePerPointBonus = 50;       // 1 raw damage = +50 score (≈ 1 cost worth)
    public int BlockPerPointBonus = 30;        // 1 raw block = +30 score
    public int RealLethalKillBonus = 5000;     // card.TotalDamage >= enemy.EffectiveHp → confirmed kill

    // v0.2.1 — power valuation delegated to PowerCatalog (self-buff vs enemy-debuff split).
    // Multiplier on attached enemy debuffs riding on attack cards (Vulnerable from Bash etc.).
    // Half-weight because the attack damage itself is the primary value.
    public double AttachedDebuffMultiplier = 0.5;

    // v0.2.6 — energy-waste avoidance penalties (when a play accomplishes nothing).
    public int WastedAttackPenalty = -2000;       // attack dmg ≤ target block → block absorbs everything
    public int WastedBlockPenalty = -800;         // self-block when no incoming threat
    public double NoThreatRatio = 0.02;           // below 2% of HP → block actually wasted (was 0.1)

    // v0.2.6 — Energy gain card scoring.
    public int EnergyGainUrgentBonus = 1500;      // low energy + expensive cards in hand
    public int EnergyGainNeutralBonus = 0;        // (unused after v0.4 greedy-plays rewrite)
    public int EnergyGainWastedPenalty = -500;    // gain doesn't increase plays-count

    // v0.4 — "Stop playing" floor: if the best candidate score is below this, the planner
    // returns null instead of forcing a play. Lets Vakuu hold energy / cards when there's
    // no meaningful payoff left this turn (e.g. enemies already at 0 HP, no threat to block,
    // no upside left on the remaining hand).
    public int MinPlayScore = 80;

    // v0.2.6 — Power card context (fight-length heuristic).
    // Short fight (low total enemy HP) → Powers don't have time to pay off.
    // Long fight (large total HP / multi-enemy) → Powers scale enormously.
    public int PowerShortFightPenalty = -500;     // total enemy HP ≤ 30
    public int PowerLongFightBonus = 500;          // total enemy HP ≥ 100
    public int ShortFightHpThreshold = 30;
    public int LongFightHpThreshold = 100;

    // v0.2.9 — Enemy status-aware target priority.
    public int VulnerableTargetBonus = 500;       // target.VulnerableAmount > 0 → kill before window closes
    public int StrengthTargetBonus = 400;          // target.StrengthAmount > 0 → kill before they hit harder
    public int FrailTargetBonus = 200;             // target.FrailAmount > 0 → AOE / burst payoff
    public int TurnStartBuffTargetBonus = 800;    // RitualPower / EnragePower / FeralPower — snowballing

    // v0.2.11 — DoT-aware overkill avoidance.
    // Enemy already on heavy poison will die soon to DoT; extra direct damage is partly wasted.
    public int HeavyDotPenalty = -300;             // poison/constrict ≥ enemy_hp * 0.5 → overkill
    // v0.5 — poison-lethal: PoisonAmount ≥ Hp → guaranteed dead before next attack. Most
    // attacks on this target are wasted. Stronger penalty than HeavyDot so direct damage
    // redirects to a live, threatening enemy instead.
    public int PoisonLethalPenalty = -1200;

    // v0.2.13 — Defect orb-aware scoring.
    public int OrbProducerEmptySlotsBonus = 150;   // channeling when slots available
    public int OrbProducerFullSlotsPenalty = -300;  // channeling when full → evicts existing orb
    public int OrbConsumerFullBonus = 400;          // evoke when full → use what you have
    public int OrbConsumerEmptyPenalty = -800;      // evoke with no orbs → nothing happens

    // v0.2.6 — Draw card scoring.
    // The goal of drawing is to find new useful cards when the remaining hand is weak.
    // We measure "hand quality" by the best score in the remainder of the hand.
    public int DrawHandUselessBonus = 1500;       // hand best score < UselessThreshold
    public int DrawHandWeakBonus = 800;            // best score < WeakThreshold
    public int DrawNoCostBottleneckBonus = 500;    // energy left after this card + cheap rest
    public int DrawIdlePenalty = -300;             // hand already loaded with strong plays
    public int HandUselessThreshold = 500;         // below this → drawing is critical
    public int HandWeakThreshold = 1000;           // below this → drawing helps
    public int HandStrongThreshold = 2000;         // above this → drawing wastes
    public int DrawEmptyPilePenalty = -1000;       // v0.2.9 — pile empty → drawing is futile
    // v0.10 — Crisis draw-rescue threshold. When projected HP after incoming
    // leak (minus playable hand block) drops to this level or below, draw cards
    // skip the strong-hand idle penalty AND earn a rescue bonus proportional
    // to expected block drawn from pile. Tuned at 15: lower than the natural
    // "uncomfortable" turn (~25 HP loss-leader threshold), high enough to fire
    // before lethal danger (~5 HP).
    public int DrawRescueHpThreshold = 15;

    // v0.5 — Retain / Ethereal play-order biases.
    // Retain: small per-alternative penalty so a retainable card defers until no
    // non-retain play remains in hand. 60 × N alternatives gives a clear "play
    // those first" signal without overriding lethal / threat-bonus situations.
    public int RetainDeferPenaltyPerAlternative = 60;
    // Ethereal: card exhausts at end of turn if not played. v0.6 raised the
    // base bonus from 120 (a token nudge) to 500 — Ethereal cards have real
    // value (200-1500 score), and the cost of not playing them is the full
    // card value lost. 500 keeps Ethereal Attack/Skill above marginal
    // alternatives without overpowering threat-aware defensive plays.
    public int EtherealPlayNowBonus = 500;
    // v0.6 — Ethereal Power-card extra. Powers tend to be the highest-value
    // Ethereal cards (VoidForm 700, Demesne 550, EchoForm 1500). 800 ensures
    // Ethereal Powers beat moderate alternatives, while still losing to
    // Block-under-threat or Burst-window plays when those genuinely matter.
    public int EtherealPowerPlayNowBonus = 800;

    // v0.6 — turn-finishing lethal mode. When greedy-picked attacks cover
    // total alive enemy HP this turn, non-Attack cards become dead weight.
    // Penalty must be larger than the biggest competing Power score (Power
    // tier-S+ at PowerCardBonus=1000 + DemonFormPower=1200 + tier=Setup+200
    // + Setup beneficiary bonus ≈ 2400+) so attacks reliably win.
    public int LethalModeNonAttackPenalty = -3000;

    // v0.6.2 — Status / Curse pollution expected-cost for fetch cards.
    // Applied as `- pollution_prob × FetchPollutionExpectedCost`. The cost
    // represents the score gap between "best card pulled" (already credited
    // in fetch card's base value) and "junk card pulled" (Wound / Slime /
    // Curse — adds ~0 or negative value to hand). 700 reflects ~1 dead
    // card slot + opportunity cost of the play.
    public int FetchPollutionExpectedCost = 700;

    // v0.6.2 — Energy monopoly penalty. Per-skipped-card cost when the
    // current card consumes all remaining energy and other playable cards
    // would have fit alongside a cheaper alternative. Conservative magnitude
    // — depth-2 lookahead already captures most multi-play scenarios; this
    // is a tie-breaker not a full curve-fit solver.
    public int EnergyMonopolyPenaltyPerSkipped = 25;
    public int EnergyMonopolyPenaltyCap = 100;

    // ─── v0.10 thorns / galvanic / power-ordering knobs ────────────────────
    // All numerics now JSON-tunable via scoring_weights/{preset}.json.

    /// <summary>Thorns penalty per-leak-HP (after block absorption).
    /// thornsPenalty = -leak × this. Was inline -100/leak.</summary>
    public int ThornsPenaltyPerLeakHp = 100;

    /// <summary>Bias added to thornsPenalty when hand has a good block alternative
    /// AND the attack isn't near-kill. Was inline -150.</summary>
    public int ThornsBlockAvailableBias = -150;

    /// <summary>Divisor applied to thornsPenalty when lethalThisTurn=true and
    /// the per-card thorns leak stays under HP-safety. Set to 1 to disable.
    /// Was inline /10 (damp to 10%).</summary>
    public int ThornsLethalDampDivisor = 10;

    /// <summary>HP safety margin for the lethal-mode thorns damp. damp fires
    /// only when thornsDamage &lt; PlayerHp − this. Was inline 4.</summary>
    public int ThornsLethalDampHpMargin = 4;

    /// <summary>Suicide-lethal guard in IsLethalThisTurn: chain is demoted to
    /// non-lethal when totalThornsCost ≥ PlayerHp − this. Was inline 4.</summary>
    public int ThornsSuicideLethalHpMargin = 4;

    /// <summary>Penalty when non-lethal attack into thorny enemy when block-only
    /// scenario is meaningfully safer. Was inline -3000.</summary>
    public int ThornsBlockBetterPenalty = -3000;

    /// <summary>HP-loss difference threshold for THORNS_BLOCK_BETTER firing.
    /// Penalty triggers when hpLossB + this &lt; hpLossC. Was inline 3.</summary>
    public int ThornsBlockBetterMargin = 3;

    /// <summary>0-cost Power priority bonus — incentivizes deploying Powers that
    /// don't compete with attacks/defense for energy. Suppressed under lethal.
    /// Was inline 2500.</summary>
    public int FreePlay0CostPowerBonus = 2500;

    /// <summary>Galvanic HP-cost penalty per leak HP (Galvanized power play under
    /// GalvanicPower source). Was inline -100/leak.</summary>
    public int GalvanicPenaltyPerLeakHp = 100;

    /// <summary>Per-hand-draw bonus for AUTOMATION-style draw-counter scaling
    /// powers in PowerSequencingTier ConditionalBonus Scaling branch.
    /// Was inline 100. Set to 0 to disable.</summary>
    public int DrawTrigEarlyPerHandDraw = 100;

    /// <summary>v0.10 (Phase 4) — Thorns HP-fraction multiplier cascade.
    /// Applied to thornsPenalty when thornsDamage (post-block leak) is a
    /// meaningful fraction of PlayerHp. Highest applicable bucket wins —
    /// iteration is min-first then we pick the largest threshold satisfied.
    /// Replaces inline cascade: hpFrac ≥ 0.5 ×3, ≥ 0.25 ×2, ≥ 0.10 ×1.5.
    /// JSON-editable for tuning the HP-pressure curve.</summary>
    public HpFractionBucket[] ThornsHpFractionMultipliers = new HpFractionBucket[]
    {
        new() { MinHpFrac = 0.5,  Multiplier = 3.0 },
        new() { MinHpFrac = 0.25, Multiplier = 2.0 },
        new() { MinHpFrac = 0.10, Multiplier = 1.5 },
    };

    public sealed class HpFractionBucket
    {
        public double MinHpFrac { get; set; }
        public double Multiplier { get; set; }
    }

    public static readonly PlanScorerWeights Defensive = new()
    {
        // 극단 방어 — lethal 도 우선순위 낮음, 무조건 hp 보존
        BlockUnderThreatBonus = 2200,
        ThreatThreshold = 0.08,
        ThreatThresholdWithBuff = 0.04,
        AttackBaseBonus = 60,         // 공격 카드 자체 가치 매우 ↓
        LethalRangeNearBonus = 1500,  // lethal 인지하되 방어 절대 우선
        LethalRangeMidBonus = 600,
        LethalRangeFarBonus = 150,
        BuffEnemyKillBonus = 500,
        HealEnemyKillBonus = 300,
    };

    public static readonly PlanScorerWeights Balanced = new();

    public static readonly PlanScorerWeights Aggressive = new()
    {
        // 공격 가중치 ↑, block 임계값 ↑ (피해 일부 감수)
        BlockUnderThreatBonus = 300,
        ThreatThreshold = 0.55,
        ThreatThresholdWithBuff = 0.40,
        AttackBaseBonus = 350,
        LethalRangeNearBonus = 4000,
        LethalRangeMidBonus = 2200,
        LethalRangeFarBonus = 800,
        BuffEnemyKillBonus = 1300,   // 위협 더 강하게 차단
        DeathBlowEnemyKillBonus = 1000,
    };

    public static readonly PlanScorerWeights Killer = new()
    {
        // 한 턴 최대 처치, block 거의 무시
        BlockUnderThreatBonus = 0,    // block 보너스 무효화
        ThreatThreshold = 0.95,       // 거의 죽기 직전이 아니면 block 안 함
        ThreatThresholdWithBuff = 0.85,
        AttackBaseBonus = 500,
        SkillBaseBonus = 50,          // skill 사용 줄이기
        LethalRangeNearBonus = 6000,  // lethal 압도적
        LethalRangeMidBonus = 3500,
        LethalRangeFarBonus = 1500,
        MinionFirstBonus = 800,        // 빠른 minion clear
        BossDeferPenalty = -200,       // boss defer 완화 — 빨리 처치
        DefendEnemyAttackPenalty = -100, // 방어 적도 부수기
        BuffEnemyKillBonus = 1500,
    };

    public static PlanScorerWeights For(Playstyle style) => style switch
    {
        Playstyle.Defensive => Defensive,
        Playstyle.Aggressive => Aggressive,
        Playstyle.Killer => Killer,
        _ => Balanced,
    };

    // ─── JSON loading ───────────────────────────────────────────────────────
    // v0.10 — External overrides at {userdata}/Sts2CombatAI/scoring_weights/{preset}.json.
    // Loaded once at mod init; subsequent For() calls return the loaded copies.
    // Missing fields fall back to the preset's hardcoded value (System.Text.Json
    // PopulateObject-style copy via per-field reflection).

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Load JSON overrides for each preset from <paramref name="dir"/>. Each
    /// preset reads {dir}/{name}.json (balanced/defensive/aggressive/killer).
    /// Missing files leave the preset unchanged. Populates the existing
    /// static instances in-place so callers via <see cref="For"/> see the
    /// updated values.
    /// </summary>
    public static void LoadFromDirectory(string dir)
    {
        TryOverlay(Balanced,   Path.Combine(dir, "balanced.json"));
        TryOverlay(Defensive,  Path.Combine(dir, "defensive.json"));
        TryOverlay(Aggressive, Path.Combine(dir, "aggressive.json"));
        TryOverlay(Killer,     Path.Combine(dir, "killer.json"));
    }

    /// <summary>
    /// Write the current values of each preset to {dir}/{name}.json so a
    /// user (or an AI tuner) can edit them and reload. Idempotent — safe
    /// to call on every mod init. Skips writing if the file already exists
    /// (preserves the user's edits).
    /// </summary>
    public static void WriteDefaultsTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            WriteIfMissing(Balanced,   Path.Combine(dir, "balanced.json"));
            WriteIfMissing(Defensive,  Path.Combine(dir, "defensive.json"));
            WriteIfMissing(Aggressive, Path.Combine(dir, "aggressive.json"));
            WriteIfMissing(Killer,     Path.Combine(dir, "killer.json"));
        }
        catch { /* best-effort — missing write permission shouldn't crash mod init */ }
    }

    private static void TryOverlay(PlanScorerWeights target, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<PlanScorerWeights>(json, _jsonOpts);
            if (loaded == null) return;
            CopyFields(loaded, target);
        }
        catch { /* malformed JSON → keep defaults */ }
    }

    private static void WriteIfMissing(PlanScorerWeights w, string path)
    {
        if (File.Exists(path)) return;
        var json = JsonSerializer.Serialize(w, _jsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Per-field copy from src→dst. Done via reflection so any new field
    /// added to PlanScorerWeights is picked up without a code change here.
    /// Only int / double / bool fields are copied (matches what the JSON
    /// roundtrip preserves).
    /// </summary>
    private static void CopyFields(PlanScorerWeights src, PlanScorerWeights dst)
    {
        var t = typeof(PlanScorerWeights);
        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Instance))
        {
            if (!f.FieldType.IsPrimitive) continue;
            var v = f.GetValue(src);
            if (v != null) f.SetValue(dst, v);
        }
    }
}
