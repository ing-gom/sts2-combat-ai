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
            ApplyStatusToHandPenalty(card, ref b, parts);

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
        // v0.7.1 — WISH lacks DRAW_PILE_SEARCH axis but the mechanic is identical
        // (pull 1 card from draw pile, player chooses). Dispatch by id.
        else if (card.Id == "CARD.WISH")
            ApplyDrawPileSearch(card, state, ref b, parts);

        // v0.7.1 — Level 3: pile-based auto-play (CASCADE / CATASTROPHE / UPROAR /
        // BEAT_DOWN). Uses SimState.DrawPile / DiscardPile to compute expected
        // value of randomly auto-played pile cards. Caller's `axes.Contains`
        // matches don't all align (some lack a single axis), so dispatch by card id.
        if (card.Id == "CARD.CASCADE" || card.Id == "CARD.CATASTROPHE"
            || card.Id == "CARD.UPROAR" || card.Id == "CARD.BEAT_DOWN")
            ApplyAutoPlayFromPile(card, state, ref b, parts);

        // v0.7.1 — Level 3: pile-based random modifier (HIDDEN_GEM, DRAIN_POWER).
        if (card.Id == "CARD.HIDDEN_GEM" || card.Id == "CARD.DRAIN_POWER")
            ApplyDrawPileRandomModifier(card, state, ref b, parts);

        // v0.7.3 / v0.7.5 — Power passives whose tick value depends on the
        // current pile / hand / character pool. All follow the same delta
        // pattern: PowerCatalog["XPower"] stays the baked baseline credited
        // via the PlanScorer Power branch; this layer adds
        //   delta = clamp(state-derived tick − baked, −baked, +Cap)
        // so state actually shifts the Power's score. NOSTALGIA / STRATAGEM
        // remain inside ApplyCardReturn (they have the CARD_RETURN axis); the
        // dispatches below cover the no-axis-routing card ids.
        if (card.Id == "CARD.MAYHEM")
            ApplyMayhemTickValue(card, state, ref b, parts);
        else if (card.Id == "CARD.STAMPEDE")
            ApplyStampedeTickValue(card, state, ref b, parts);
        else if (card.Id == "CARD.CALAMITY")
            ApplyCalamityTickValue(card, state, ref b, parts);
        else if (card.Id == "CARD.HELLRAISER")
            ApplyHellraiserTickValue(card, state, ref b, parts);
        else if (card.Id == "CARD.JUGGLING")
            ApplyJugglingTickValue(card, state, ref b, parts);

        // v0.7.11 — Self-copy / chain cards. Each play seeds a future play of
        // the same or chosen card. Pure card-id dispatch — none of these have
        // a generic axis we could match on (catalog axes describe the immediate
        // effect, not the chain semantics).
        if (card.Id == "CARD.ANGER")
            ApplyAngerChain(card, state, ref b, parts);
        else if (card.Id == "CARD.UNDEATH")
            ApplyUndeathChain(card, state, ref b, parts);
        else if (card.Id == "CARD.DUAL_WIELD")
            ApplyDualWieldChain(card, state, ref b, parts);
        else if (card.Id == "CARD.HEIRLOOM_HAMMER")
            ApplyHeirloomHammerChain(card, state, ref b, parts);
        else if (card.Id == "CARD.NIGHTMARE")
            ApplyNightmareChain(card, state, ref b, parts);
        else if (card.Id == "CARD.ADAPTIVE_STRIKE")
            ApplyAdaptiveStrikeChain(card, state, ref b, parts);

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
        else if (card.Id == "CARD.WHITE_NOISE" || card.Id == "CARD.DISCOVERY"
              || card.Id == "CARD.DISTRACTION" || card.Id == "CARD.LARGESSE"
              || card.Id == "CARD.SPLASH")
            ApplyCardGen(card, state, ref b, parts);

        if (axes.Contains("EXHAUST_TARGET_RANDOM"))
            ApplyRandomExhaustPenalty(card, state, ref b, parts);

        // v0.6.9 — PRECISE_CUT: damage = 13 − 2 × (other cards in hand).
        // Anti-handsize scaling — small/empty hand multiplies value; full hand
        // gates damage to near-0. Not captured by EstimateVariableHits (which
        // is multiplicative); use a per-card-id damage adjustment here.
        if (card.Id == "CARD.PRECISE_CUT")
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

        // v0.6.9 — ENLIGHTENMENT: combat-wide cost reduction (all hand cards
        // cost 1). Value = sum of cost reductions in current + future hands.
        if (card.Id == "CARD.ENLIGHTENMENT")
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

    private static void ApplyCunningConsumer(SimCard self, SimState state, ref int b, List<string> parts)
    {
        // Count Sly cards remaining in hand (excluding the consumer itself —
        // most consumers don't carry the CUNNING raw axis themselves, but be
        // defensive in case axis tagging overlaps).
        int slyInHand = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self)) continue;
            if (c.IsSly && c.IsPlayable) slyInHand++;
        }

        bool producerInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.Axes.Contains("CUNNING_PRODUCER") || c.Axes.Contains("CUNNING")));

        if (slyInHand > 0)
        {
            // Each Sly card in hand has a chance to be auto-played on discard.
            // Force-discard cards (Acrobatics: discard 1; Calculated Gamble:
            // discard hand) vary in how many they discard, but the heuristic
            // assumes at least 1 discard — bonus per Sly cap at 3 (most
            // consumers discard 1-3 cards).
            int effective = System.Math.Min(slyInHand, 3);
            int v = effective * 110;
            b += v;
            parts.Add($"slyInHand({slyInHand})=+{v}");
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

    private static void ApplyStatusToHandPenalty(SimCard card, ref int b, List<string> parts)
    {
        // CRASH_LANDING "fills hand with Wreckage" → far worse than dropping 1.
        // Catalog identifies AOE_DAMAGE + STATUS_TO_HAND as the hand-fill case.
        // Single-status cards (COLLISION_COURSE) only add 1.
        bool fillsHand = card.Axes.Contains("AOE_OTHER")
                      || card.Axes.Contains("AOE_DAMAGE");
        int penalty = fillsHand ? -350 : -150;
        b += penalty;
        parts.Add($"statusToHand={penalty}");
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
            case "CARD.FTL":
                // "Draw 1 if < 3 cards used this turn."
                if (cardsThisTurn < 3) v = 200;
                else v = -50;   // condition missed, draw won't fire
                break;
            case "CARD.PALE_BLUE_DOT":
                // "If ≥ 5 cards used, +1 next-turn draw" — Power scaling. Pays
                // off later in fight; modest bonus.
                if (cardsThisTurn >= 4) v = 200;     // about to qualify next play
                else v = 100;
                break;
            case "CARD.FETCH":
                // "Draw 1 if this is the first FETCH used this turn." History
                // would need a per-card-id play counter; assume true since
                // most decks have ≤1 FETCH.
                v = 180;
                break;
            case "CARD.COMPILE_DRIVER":
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
    private static int EstimateCardPower(SimCard c, SimState state, bool freeUse)
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
            case "CARD.DREDGE":
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
            case "CARD.NEOWS_FURY":
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
            case "CARD.AGGRESSION":
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
            case "CARD.NOSTALGIA":
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
            case "CARD.STRATAGEM":
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
            case "CARD.PHOTON_CUT":
            case "CARD.GLIMMER":
                // Hand → top-of-draw (deck order manipulation). Modest bonus —
                // damage / draw already valued by base scoring.
                b += 100;
                parts.Add("topDeck=+100");
                break;
            case "CARD.ANOINTED":
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
            b -= 100;
            parts.Add("pileSearchEmpty=-100");
            return;
        }

        switch (self.Id)
        {
            case "CARD.CHARGE":
            {
                // Player CHOOSES 2 from draw, transforms to upgraded Drop+.
                // Top-2 positives + per-card upgrade bonus.
                int n = System.Math.Min(2, state.DrawPile.Count);
                var ranked = new List<int>(state.DrawPile.Count);
                foreach (var c in state.DrawPile) ranked.Add(EstimateCardPower(c, state, freeUse: false));
                ranked.Sort((x, y) => y.CompareTo(x));
                int sum = 0, taken = 0;
                foreach (int p in ranked)
                {
                    if (p <= 0) break;
                    if (taken >= n) break;
                    sum += p; taken++;
                }
                int v = sum + taken * 40;
                b += v;
                parts.Add($"chargeBest{taken}=+{v}");
                break;
            }
            case "CARD.FOREGONE_CONCLUSION":
            {
                // 2 cards from draw to hand NEXT turn. Mean × 2.
                int sum = 0;
                foreach (var c in state.DrawPile) sum += EstimateCardPower(c, state, freeUse: false);
                int mean = sum / state.DrawPile.Count;
                int v = (int)(mean * 1.5);   // discount for next-turn delay
                b += v;
                parts.Add($"foregone(mean{mean})=+{v}");
                break;
            }
            case "CARD.ANOINTED":
            {
                int v = state.DrawPile.Count > 5 ? 280 : 100;
                b += v;
                parts.Add($"anointed=+{v}");
                break;
            }
            case "CARD.WISH":
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
        switch (self.Id)
        {
            case "CARD.CASCADE":
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
            case "CARD.CATASTROPHE":
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
            case "CARD.UPROAR":
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
            case "CARD.BEAT_DOWN":
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
            case "CARD.HIDDEN_GEM":
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
            case "CARD.DRAIN_POWER":
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
            case "CARD.CREATIVE_AI":
                filter = "power_free"; aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "creativeAI"; break;
            case "CARD.HELLO_WORLD":
                filter = "common";     aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "helloWorld"; break;
            case "CARD.SPECTRUM_SHIFT":
                filter = "colorless";  aggregation = "mean"; n = 1; multiplier = RemainingTurnsProxy; tag = "spectrumShift"; break;

            // One-shot, single random card (no choice).
            case "CARD.WHITE_NOISE":
                filter = "power_free"; aggregation = "mean"; n = 1; multiplier = 1; tag = "whiteNoise"; break;
            case "CARD.DISTRACTION":
                filter = "skill_free"; aggregation = "mean"; n = 1; multiplier = 1; tag = "distraction"; break;
            case "CARD.CALL_OF_THE_VOID":
                filter = "all_free";   aggregation = "mean"; n = 1; multiplier = 1; tag = "callOfVoid"; break;
            case "CARD.LARGESSE":
                filter = "colorless";  aggregation = "mean"; n = 1; multiplier = 1; tag = "largesse"; break;

            // Pick-of-N (player chooses one).
            case "CARD.DISCOVERY":
                filter = "all";        aggregation = "top1of3"; n = 1; multiplier = 1; tag = "discovery"; break;
            case "CARD.SPLASH":
                filter = "attack";     aggregation = "top1of3"; n = 1; multiplier = 1; tag = "splash"; break;

            // Multi-card pulls (each independent, sum value).
            case "CARD.JACKPOT":
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
            case "CARD.BLADE_OF_INK":     v = 600; break;   // S — 2 inked Shivs
            case "CARD.BLADE_DANCE":      v = 450; break;   // A — 3 Shivs
            case "CARD.UP_MY_SLEEVE":     v = 380; break;   // D — 3 Shivs, retains
            case "CARD.PRIMAL_FORCE":     v = 500; break;   // A — converts hand attacks
            case "CARD.GUARDS":           v = 350; break;   // A — convert hand to Sacrifice+
            case "CARD.CHARGE":           v = 350; break;   // A — pick 2 from draw → upgrade
            case "CARD.NIGHTMARE":        v = 400; break;   // B — 3 copies next turn
            case "CARD.JUGGLING":         v = 200; break;   // D — Power, 3rd attack copy
            case "CARD.JACKPOT":          v = 180; break;   // C — 3 zero-cost random
            case "CARD.CALL_OF_THE_VOID": v = 100; break;   // S — random card volatile
            case "CARD.CREATIVE_AI":      v = 150; break;   // B — Power, random Power/turn
            case "CARD.HELLO_WORLD":      v = 120; break;   // B — Power, random common/turn
            case "CARD.INFINITE_BLADES":  v = 200; break;   // A — Power, 1 Shiv/turn
            case "CARD.SENTRY_MODE":      v = 130; break;   // B — Power, scanner card
            case "CARD.SPECTRUM_SHIFT":   v = 100; break;   // C — Power, random colorless
            case "CARD.COMPACT":          v = 150; break;   // B — converts status to Fuel+
            // v0.6.9 — axis-fallback cards (no CARD_GEN axis in catalog)
            case "CARD.WHITE_NOISE":      v = 350; break;   // S — random Power 0-cost
            case "CARD.DISCOVERY":        v = 280; break;   // A — pick 1 of 3
            case "CARD.DISTRACTION":      v = 240; break;   // A — random Skill 0-cost
            case "CARD.WISH":             v = 200; break;   // A — 1 from draw to hand
            case "CARD.LARGESSE":         v = 150; break;   // A — other-player colorless
            case "CARD.SPLASH":           v = 200; break;   // A — pick 1 of 3 attacks
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
        // Random card removed from hand. Penalty proxies "average card value
        // lost". Cards using this axis also have damage/block payoffs (CINDER
        // 18, THRASH 4×2, TRUE_GRIT 7 block) so a moderate penalty is appropriate.
        int handSize = 0;
        for (int i = 0; i < state.Hand.Count; i++)
        {
            var c = state.Hand[i];
            if (ReferenceEquals(c, self)) continue;
            if (c.IsCurseOrStatus) continue;     // exhausting curses is GOOD — no penalty
            handSize++;
        }
        int v;
        switch (self.Id)
        {
            case "CARD.THRASH":
                // THRASH adds exhausted card's damage to its own — partial offset.
                // Net penalty smaller than pure-loss cards.
                v = handSize > 0 ? -60 : 0;
                break;
            case "CARD.TRUE_GRIT":
                // Card_select-able on upgrade — base is random, often a curse.
                v = handSize > 2 ? -90 : 0;
                break;
            case "CARD.CINDER":
                v = handSize > 0 ? -120 : 0;
                break;
            case "CARD.TYRANNY":
                // Power — exhausts each turn. Long-term thinning is value;
                // small bonus rather than penalty.
                v = 40;
                break;
            default:
                v = handSize > 0 ? -80 : 0;
                break;
        }
        if (v != 0)
        {
            b += v;
            parts.Add($"randomExh={v:+#;-#;0}");
        }
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
