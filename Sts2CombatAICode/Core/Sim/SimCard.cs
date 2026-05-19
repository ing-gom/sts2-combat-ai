using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Lightweight projection of a CardModel used by the planner.
/// v0.1 stores only the heuristic-relevant fields (Type, Cost, TargetType) plus a
/// reference back to the live CardModel for replay binding. Damage/Block values are
/// intentionally absent — v0.1's scorer doesn't need them.
/// </summary>
internal sealed record SimCard
{
    public required string Id { get; init; }
    public required int Cost { get; init; }
    public required CardType Kind { get; init; }
    public required TargetType Target { get; init; }
    // Nullable so unit tests can construct SimCard without a real CardModel.
    // Live mode executors require non-null at execution time.
    public CardModel? SourceRef { get; init; }
    public required CardEffectSummary Effect { get; init; }

    /// <summary>
    /// True when CardModel.CanPlay() is true at snapshot time. Unplayable cards
    /// (curses with "Unplayable" keyword, status cards, conditional plays whose
    /// condition isn't met) are excluded from planner candidates.
    /// Defaults to true so legacy SimCard fixtures without explicit init stay valid.
    /// </summary>
    public bool IsPlayable { get; init; } = true;

    /// <summary>
    /// Build-classification axes from cards_catalog.json (POISON_PRODUCER,
    /// ORB_AMPLIFIER, etc.). Empty for cards not in the catalog.
    /// </summary>
    public IReadOnlyList<string> Axes { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Builds this card primarily contributes to (e.g., "독 빌드"). Used by
    /// HandSynergy to detect hand-wide build commitments and combo plays.
    /// </summary>
    public IReadOnlyList<string> PrimaryBuildTags { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// True when this card survives past end-of-turn discard (Retain keyword in
    /// catalog). Affects play-ORDER: when other useful cards exist, prefer
    /// playing the non-retain cards first so the retained card can act again
    /// next turn. When the retain card is the strongest available play, retain
    /// status is irrelevant and we play it normally.
    /// </summary>
    public bool IsRetain { get; init; }

    /// <summary>
    /// True when this card is exhausted at end-of-turn if not played (Ethereal
    /// keyword in catalog). Affects play-ORDER: even if its score is borderline,
    /// playing now is better than losing it for free — bump priority a notch so
    /// we don't waste it sitting in hand.
    /// </summary>
    public bool IsEthereal { get; init; }

    /// <summary>
    /// True when this card was guaranteed to start in our opening hand (Innate
    /// keyword in catalog). Informational only — opening-turn ordering already
    /// works via normal scoring; flag is here for future "first-turn setup
    /// detection" rules without re-querying the catalog.
    /// </summary>
    public bool IsInnate { get; init; }

    /// <summary>
    /// True when this card is exhausted on play (Exhaust keyword in catalog).
    /// Used by the simulator to decide whether the played card joins the
    /// discard pile (non-exhaust) or leaves the deck entirely (exhaust) —
    /// matters for Draw-card scoring in the depth-2 lookahead.
    /// </summary>
    public bool IsExhaust { get; init; }

    /// <summary>
    /// True when this card carries the `CardKeyword.Sly` keyword — STS2 Silent
    /// "교활". Sly cards auto-play when discarded (see `CardCmd.cs` Sly-discard
    /// loop). The planner uses this to score CUNNING_CONSUMER cards (forced
    /// discards like Acrobatics / Calculated Gamble) higher when the hand is
    /// rich in Sly producers — more Sly cards in hand = more discard-trigger
    /// payoff. Detected via `axes.Contains("CUNNING")` (raw axis without
    /// _PRODUCER/_CONSUMER suffix). Audit confirmed 1:1 mapping with the
    /// catalog's `keywords:["Sly"]` (8 / 8 base cards as of v0.103.2).
    /// </summary>
    public bool IsSly { get; init; }

    /// <summary>
    /// True when this card fetches / discovers a random card from the draw or
    /// discard pile (Anointed, Apotheosis, Echo of Fallen, etc. — catalog
    /// `fetch_trigger`). Drives status / curse pollution penalty scoring:
    /// fetch cards become weaker when the pile contains junk that could be
    /// pulled.
    /// </summary>
    public bool IsFetchTrigger { get; init; }

    /// <summary>
    /// v0.9 — Card carries the "Bound" affliction (ChainsOfBindingPower).
    /// Only ONE Bound card may be played per turn — others are blocked by
    /// ShouldPlay returning false (decompile sts2.decompiled.cs:313044).
    /// Snapshot reads CardModel.Affliction via reflection; runtime
    /// EnumerateCandidates filters extra Bound cards once
    /// <see cref="SimState.BoundCardPlayedThisTurn"/> is set.
    /// </summary>
    public bool IsBound { get; init; }

    /// <summary>
    /// Card carries the "Smog" affliction (from SmoggyPower — LivingFog's
    /// AdvancedGasMove). When SmoggyPower is active and the player plays
    /// any Skill, every other unafflicted Skill in deck/hand/discard gains
    /// Smog and becomes unplayable for the turn (cleared at TurnEnd).
    /// Snapshot-time signal — EnumerateCandidates also uses
    /// <see cref="SimState.SmoggySkillPlayedThisTurn"/> to filter
    /// future-skill candidates inside the forward simulator.
    /// </summary>
    public bool IsSmogged { get; init; }

    public bool IsAttack => Kind == CardType.Attack;
    public bool IsSkill => Kind == CardType.Skill;
    public bool IsPower => Kind == CardType.Power;
    public bool IsCurseOrStatus => Kind == CardType.Curse || Kind == CardType.Status;

    // Convenience accessors
    public int Damage => Effect.Damage;
    public int Hits => Effect.Hits;
    public int TotalDamage => Effect.TotalDamage;
    public int Block => Effect.Block;
    public IReadOnlyDictionary<string, int> PowerApps => Effect.PowerApps;
    public int EnergyGain => Effect.EnergyGain;
    public int DrawCount => Effect.DrawCount;
    public bool IsEnergyGainCard => Effect.IsEnergyGainCard;
    public bool IsDrawCard => Effect.IsDrawCard;

    // v0.4 — orb metadata pass-through.
    public int EvokeCount => Effect.EvokeCount;
    public int ChannelCount => Effect.ChannelCount;
    public OrbKind ChannelKind => Effect.ChannelKind;

    // v0.7.8 — self-damage cost (BLOODLETTING/OFFERING/HEMOKINESIS etc.).
    public int HpLossAmount => Effect.HpLossAmount;

    // v0.7.71 — Regent star resource: gain on play / cost to play.
    public int StarsGain => Effect.StarsGain;
    public int StarCost => Effect.StarCost;

    // v0.9 — Per-energy efficiency. Cost 0 cards are treated as cost 1 for
    // the ratio so free attacks (FALLING_STAR, SHIV, etc.) rank by raw damage
    // — matches the existing convention in IsLethalThisTurn (PlanScorer.cs
    // line 1926) and FinisherIdentifier. Surfaced in PlanScorer details so
    // logs show "eff(d10.5/E,b0/E)" alongside per-target/synergy bonuses;
    // also used by ActionPlanner's win-check as a tiebreaker when two
    // candidates' depth-2 totals are close (efficiency-stable plays beat
    // chip-damage chains that just inflate via per-card bonuses).
    //
    // Static value only. Includes Damage/Block from PreviewValue, which
    // covers PlayerStrength / Vigor / Enchantment numeric upgrades (the
    // game pre-calculates these into the var). Does NOT cover hit-time
    // multipliers (Vuln/Weak/Burst/Echo/Unmovable/Lethality) — use
    // EffectiveDmgPerEnergy / EffectiveBlockPerEnergy below for full accuracy.
    public double DmgPerEnergy => TotalDamage / System.Math.Max(1.0, Cost);
    public double BlockPerEnergy => Block / System.Math.Max(1.0, Cost);

    /// <summary>
    /// v0.9 — Per-energy effective damage with full state context. Includes
    /// PlayerStrength (already in PreviewValue), PlayerWeak (-25%),
    /// any-alive-enemy Vulnerable (+50%), X-cost scaling (hits × energy),
    /// EchoFormPower (×2 plays), and PlayerVigor single-shot bonus.
    /// Returns 0 for non-attack cards or 0-damage cards.
    /// </summary>
    public double EffectiveDmgPerEnergy(SimState state) => EffectiveDmgPerEnergy(state, -1);

    /// <summary>
    /// v0.9 — Per-energy effective damage with optional specific target.
    /// When <paramref name="targetIdx"/> &gt;= 0, the calculation uses that
    /// target's Vuln/Weak/Intangible/HardenedShell/Thorns/DamageCap — exact
    /// for single-target plays. When -1 (or AOE), falls back to "any alive
    /// enemy has X" best-case approximation.
    ///
    /// Includes (covers all hit-time multipliers that PlanScorer.cs applies
    /// at line 505-548 for attacks):
    ///   • Card-id-specific (AccuracyPower on Shiv) via StatusMath
    ///   • Strength / Weak / Vulnerable / Vigor
    ///   • Tracking (vs Weak ×2), Cruelty (vs Vuln ×1.25), Lethality
    ///   • EchoForm (×2 plays)
    ///   • X-cost (hits scale with energy)
    ///   • Intangible (per-hit cap to 1)
    ///   • HardenedShell (per-target damage budget remaining)
    ///   • DamageCapPerHit (target-specific cap)
    ///   • Thorns (subtracted per hit — counts as efficiency loss)
    /// </summary>
    public double EffectiveDmgPerEnergy(SimState state, int targetIdx)
    {
        if (!IsAttack || Damage <= 0) return 0;
        // v0.9 — AccuracyPower (+N dmg per Shiv card).
        int baseDmg = Damage;
        if (state.PlayerAccuracy > 0 && Id == "SHIV") baseDmg += state.PlayerAccuracy;
        // v0.9 — TenderPower: each card played applies -1 Strength (and -1
        // Dex on skills) for the rest of the turn. The Nth card this turn
        // already has -(N-1) Strength stacked. For ranking, use current
        // TurnAttacksPlayed + TurnSkillsPlayed as cards-already-played proxy
        // (Tender ticks per ANY card, not just attacks). Effective Strength
        // for this card = max(-Str, PlayerStrength - cardsPlayedSoFar × Tender).
        int effStrength = state.PlayerStrength;
        if (state.PlayerTender > 0)
        {
            int cardsPlayed = state.TurnAttacksPlayed + state.TurnSkillsPlayed;
            effStrength -= cardsPlayed * state.PlayerTender;
        }
        int perHit = baseDmg + System.Math.Max(0, effStrength);
        if (state.PlayerWeak > 0)
        {
            // v0.9 — DebilitatePower amplifies Weak from 0.75→0.50 (player
            // attacks lose more damage). Decompile sts2.decompiled.cs:
            // ModifyWeakMultiplier doubles the (1 - WeakMult) loss.
            double weakMult = state.PlayerDebilitate > 0 ? 0.50 : 0.75;
            perHit = (int)(perHit * weakMult);
        }

        // Determine target's Vuln/Weak. With specific target, exact; else best-case.
        bool tgtVuln, tgtWeak;
        SimEnemy? tgt = null;
        if (targetIdx >= 0 && targetIdx < state.Enemies.Count && state.Enemies[targetIdx].IsAlive)
        {
            tgt = state.Enemies[targetIdx];
            tgtVuln = tgt.VulnerableAmount > 0;
            tgtWeak = tgt.WeakAmount > 0;
        }
        else
        {
            tgtVuln = false; tgtWeak = false;
            foreach (var e in state.Enemies)
            {
                if (!e.IsAlive) continue;
                if (e.VulnerableAmount > 0) tgtVuln = true;
                if (e.WeakAmount > 0) tgtWeak = true;
                if (tgtVuln && tgtWeak) break;
            }
        }
        if (tgtVuln) perHit = (int)(perHit * 1.5);

        // Silent passive multipliers — Tracking/Cruelty target-conditional;
        // Lethality global first-attack single-shot.
        bool lethalityActive = state.PlayerLethality > 0 && state.TurnAttacksPlayed == 0;
        if (state.PlayerTracking > 0 && tgtWeak) perHit = (int)(perHit * 2.0);
        if (state.PlayerCruelty > 0 && tgtVuln)  perHit = (int)(perHit * 1.25);
        if (lethalityActive)                     perHit = (int)(perHit * 1.5);

        // v0.9 — Target-specific per-hit caps. Intangible clamps to 1
        // regardless of source. DamageCapPerHit (HardToKillPower's cap or
        // similar) clamps per-hit.
        if (tgt != null)
        {
            if (tgt.DamageCapPerHit > 0 && perHit > tgt.DamageCapPerHit)
                perHit = tgt.DamageCapPerHit;
        }

        int hits = System.Math.Max(1, Hits);
        if (Axes != null && Axes.Contains("X_COST"))
        {
            // v0.10 — ChemicalX (`ModifyXValue`) adds +2 to any X-cost card's
            // X value. Canonical "Increase" DynamicVar = 2; applies to both
            // EnergyCost.CostsX and HasStarCostX, so any X_COST axis card
            // benefits. PlayerEnergy is the spent energy upper bound; +2 adds
            // ChemicalX's free bonus.
            int xBonus = (state.PlayerRelics != null
                && state.PlayerRelics.ContainsKey("ChemicalX")) ? 2 : 0;
            hits = System.Math.Max(1, state.PlayerEnergy + xBonus);
        }
        int total = perHit * hits;
        if (state.PlayerVigor > 0) total += state.PlayerVigor;
        if (state.PlayerEchoForm > 0) total *= 2;
        // v0.9 — ShrinkPower (Tier B, -N% damage). Applied after additive
        // multipliers, before single-shot doublers — matches game order
        // where ModifyDamageMultiplicative chain runs once.
        if (state.PlayerShrinkPercent > 0)
            total = total * (100 - state.PlayerShrinkPercent) / 100;

        // v0.9 — DuplicationPower (next card ×2) and DoubleDamagePower
        // (next attack ×2). Both single-shot, consumed by first matching card.
        // From efficiency-ranking perspective, both make THIS card's total ×2.
        // Stacks multiplicatively if both active simultaneously (rare).
        if (state.PlayerPowers != null)
        {
            if (state.PlayerPowers.TryGetValue("DuplicationPower", out var dup) && dup > 0)
                total *= 2;
            if (state.PlayerPowers.TryGetValue("DoubleDamagePower", out var dd) && dd > 0)
                total *= 2;
        }

        // v0.9 — HardenedShell target-specific damage budget (a per-turn
        // total cap, not per-hit). Clamp post-multipliers.
        if (tgt != null && tgt.HardenedShellRemaining > 0
            && total > tgt.HardenedShellRemaining)
        {
            total = tgt.HardenedShellRemaining;
        }

        // v0.9 — Thorns reflect: each hit costs us tgt.ThornsAmount HP
        // (subtract from effective output as "value loss"). Same direction
        // as PlanScorer.cs:964-989. For multi-target, sum reflects.
        int thornsLoss = 0;
        if (tgt != null) thornsLoss = tgt.ThornsAmount * hits;
        else if (Target == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllEnemies)
        {
            foreach (var e in state.Enemies)
                if (e.IsAlive) thornsLoss += e.ThornsAmount * hits;
        }
        // 1 HP loss ≈ 1 dmg-equivalent value (rough conversion). Tunable.
        total = System.Math.Max(0, total - thornsLoss);

        // v0.9 — SkittishPower (first-hit-per-turn → enemy block). When this
        // card is the first attack on a Skittish enemy that hasn't yet
        // triggered Skittish this turn, the enemy gains Amount block which
        // absorbs damage. Approximate as flat value-loss = SkittishAmount.
        // For multi-hit cards the trigger fires on hit-1 only; remaining
        // hits land at full damage — net loss is still ~SkittishAmount.
        if (tgt != null && tgt.SkittishAmount > 0 && !tgt.SkittishAlreadyTriggered)
        {
            total = System.Math.Max(0, total - tgt.SkittishAmount);
        }

        // v0.9 — CurlUpPower (one-shot reactive block on first damage).
        // Power is removed after triggering, so CurlUpAmount > 0 means it
        // hasn't fired yet this combat. Same value-loss model as Skittish.
        if (tgt != null && tgt.CurlUpAmount > 0)
        {
            total = System.Math.Max(0, total - tgt.CurlUpAmount);
        }

        return total / System.Math.Max(1.0, Cost);
    }

    /// <summary>
    /// v0.9 — Per-energy effective block with full state context. Includes
    /// PlayerDexterity (already in PreviewValue), PlayerFrail (×0.75 outgoing
    /// block), PlayerBurst (skills resolve twice), EchoFormPower (×2), and
    /// PlayerUnmovable single-shot ×2 on first block card. Returns 0 for
    /// non-skill cards or 0-block cards.
    /// </summary>
    public double EffectiveBlockPerEnergy(SimState state)
    {
        if (Block <= 0) return 0;
        int perPlay = Block;
        // v0.9 — TenderPower: per-card-played -Dex. Reduces this card's
        // effective block. Mirrors the Strength side in EffectiveDmgPerEnergy.
        if (state.PlayerTender > 0)
        {
            int cardsPlayed = state.TurnAttacksPlayed + state.TurnSkillsPlayed;
            int tenderPenalty = cardsPlayed * state.PlayerTender;
            // Dex normally adds to block via PreviewValue; subtract what
            // Tender would have erased. Floor at 1 to avoid zero-out.
            perPlay = System.Math.Max(1, perPlay - tenderPenalty);
        }
        if (state.PlayerFrail > 0) perPlay = (int)(perPlay * 0.75);
        int plays = 1;
        if (IsSkill && state.PlayerBurst > 0) plays *= 2;
        if (state.PlayerEchoForm > 0) plays *= 2;
        // v0.9 — DuplicationPower (next card ×2). Single-shot — consumed
        // by first card played. Applies to any card type.
        // ReboundPower (first skill goes to Draw instead of Discard) —
        // effectively grants one re-play of the skill next turn. We model
        // as ×2 for skill-block efficiency (this turn + free next-turn play).
        if (state.PlayerPowers != null)
        {
            if (state.PlayerPowers.TryGetValue("DuplicationPower", out var dup) && dup > 0)
                plays *= 2;
            if (IsSkill && state.PlayerPowers.TryGetValue("ReboundPower", out var rb) && rb > 0)
                plays *= 2;
        }
        int total = perPlay * plays;
        // Unmovable: first-block-card-of-turn doubles. Adds one extra perPlay
        // (the first play of a multi-play card is what gets doubled, not
        // the whole multiplier — matches PlanScorer.cs line 1143 model).
        if (state.PlayerUnmovable > 0 && !state.UnmovableUsedThisTurn)
            total += perPlay;
        // v0.9 — DanseMacabre: cost ≥ 2 card grants +N block reactively
        // (Necrobinder STS2 mechanic per cards_catalog.json). Stacks across
        // all card types — Attack/Skill/Power. For SKILL-block efficiency
        // ranking we credit it when the card itself qualifies; the same
        // bonus is also added to Attack-side scoring in PlanScorer's main
        // chain, so this only fires for skills here to avoid double-counting.
        if (IsSkill && Cost >= 2 && state.PlayerDanseMacabre > 0)
            total += state.PlayerDanseMacabre;
        return total / System.Math.Max(1.0, Cost);
    }
}
