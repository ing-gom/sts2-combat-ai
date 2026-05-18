using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Hand-composition heuristics. Approximates what a 2-step lookahead would discover
/// (Inflame → 3 Strikes combo) without building a real forward simulator. Power
/// applications get a bonus proportional to the number of cards in hand that
/// benefit from the buff/debuff.
///
/// Examples:
///   Inflame (Strength+2) with 3 attacks in hand → +3 × 2 × 50 = +300 score
///   Bash (Vulnerable on enemy) with Twin Strike (2 hits) + Strike (1 hit) in hand
///       → 3 hits × 40 = +120 (per-hit, not per-attack — Vuln amplifies every hit)
///   Weak on a 5dmg×3hit enemy → per-hit savings 2 × 3 hits × 2 turns = 12 HP saved
///       (Weak rounds down per hit, so multi-hit enemies lose proportionally more
///        damage than the flat-percentage shortcut suggests)
/// </summary>
internal static class HandSynergy
{
    private const int StrengthSynergyPerAttack = 50;     // damage point ≈ DamagePerPointBonus
    private const int DexteritySynergyPerSkill = 30;     // block point
    private const int VulnerableSynergyPerHit  = 100;    // v0.6 — up from 40. True per-hit
                                                          // value ≈ 0.5 × avg_dmg(5) × DamagePerPointBonus(50)
                                                          // = 125. Old 40 systematically under-priced
                                                          // Vuln-appliers vs raw-damage multi-hit
                                                          // attacks, causing Twin-Strike-before-Bash
                                                          // mis-orderings.
    private const int WeakSavingsPerHpPoint    = 30;     // score per HP of enemy damage prevented
    private const int WeakSavingsTurnCap       = 4;      // v0.7.25 — was 2; high-stack Weak now scales up to 4 turns

    // v0.7.25 — Baseline per-hit attack estimate used when the enemy is NOT
    // currently attack-intent (buff / heal / defend / summon / debuff / status).
    // Weak applied now still mitigates their FUTURE attack turns; without intent
    // visibility into those turns, fall back to a conservative average.
    private const int WeakNonAttackBaselineDmg = 8;
    private const int WeakNonAttackBaselineHits = 1;
    // v0.6.8 — RagePower (Ironclad RAGE): +N block per attack played this turn,
    // for the rest of the turn. Total expected block = N × (remaining attacks
    // in hand). Same beneficiary-count pattern as Dexterity, but the per-card
    // bonus is "block per attack" not "block per block-skill".
    private const int RageSynergyPerAttack     = 30;     // block point per attack × amount
    // v0.6.9 — FocusPower (Defect): each orb-evoking/channeling card in hand
    // gets +N damage/block when Focus is N. Each orb application (channel
    // start, passive tick, evoke) reads Focus at trigger time.
    private const int FocusSynergyPerOrbCard   = 80;     // approximate per-card payoff × amount

    /// <summary>
    /// Bonus added to a power-apply's value based on hand composition.
    /// <paramref name="self"/> is the card being scored (excluded from hand counting).
    /// <paramref name="amount"/> is the power stack count.
    ///
    /// v0.5 — depth-2 lookahead double-count correction. ActionPlanner's lookahead
    /// already scores ONE next card with the buff applied, so HandSynergy would
    /// double-credit that single beneficiary if it counted every attack/skill in
    /// hand. Subtract one beneficiary so the lookahead's contribution plus this
    /// hand-wide bonus sum to the right total. When only 0–1 beneficiaries exist,
    /// the lookahead fully covers them and HandSynergy returns 0.
    /// </summary>
    public static int Compute(string powerName, int amount, SimCard self, SimState state)
    {
        if (amount <= 0) return 0;

        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsAttack);
        int remainingSelfBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsSkill && c.Block > 0
            && (c.Target == TargetType.Self || c.Target == TargetType.AnyPlayer));
        // v0.6.9 — orb-card beneficiaries: anything that channels OR evokes
        // benefits from Focus stacks at trigger time.
        int remainingOrbCards = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.ChannelCount > 0 || c.EvokeCount > 0));

        // Subtract the single beneficiary the depth-2 lookahead will independently
        // credit. Negative-clamp so a hand with 0–1 beneficiaries returns 0.
        int incrementalAtkBeneficiaries  = System.Math.Max(0, remainingAttacks    - 1);
        int incrementalBlockBeneficiaries = System.Math.Max(0, remainingSelfBlocks - 1);

        return powerName switch
        {
            // Self-buff synergies — multiplied by both stacks AND beneficiary count.
            // Subtract 1 beneficiary so the depth-2 lookahead's first-card credit
            // doesn't double up with hand-wide synergy.
            "StrengthPower"          => incrementalAtkBeneficiaries  * amount * StrengthSynergyPerAttack,
            "TemporaryStrengthPower" => incrementalAtkBeneficiaries  * amount * StrengthSynergyPerAttack,
            "DexterityPower"         => incrementalBlockBeneficiaries * amount * DexteritySynergyPerSkill,
            "TemporaryDexterityPower"=> incrementalBlockBeneficiaries * amount * DexteritySynergyPerSkill,

            // Vuln amplifies +50% per HIT (not per attack). Twin Strike (2 hits) gets
            // double the Vuln payoff of Strike (1 hit). Subtract avg hits of the
            // lookahead-credited card (approx 1) to stay consistent with the −1
            // beneficiary pattern above.
            "VulnerablePower" => System.Math.Max(0, RemainingHits(self, state) - 1) * VulnerableSynergyPerHit,

            // Weak savings scale with enemy hit-count × per-hit rounding. Multi-hit
            // enemies lose proportionally more damage than the flat estimate.
            // Enemy-state-based — not subject to the hand-card lookahead double-count.
            "WeakPower" => ComputeWeakSavings(amount, state),

            // v0.6.8 — RagePower. Block per attack played for the rest of this turn.
            // Beneficiaries = attacks still in hand (past attacks don't retroactively
            // benefit — RagePower applies forward only). Subtract 1 to stay
            // consistent with the depth-2 lookahead double-count correction.
            "RagePower" => System.Math.Max(0, remainingAttacks - 1) * amount * RageSynergyPerAttack,

            // v0.6.9 — FocusPower (Defect). Beneficiaries = orb cards (channel
            // or evoke) still playable. Permanent Focus persists across turns
            // so even small in-hand orb count is high value.
            "FocusPower"          => System.Math.Max(0, remainingOrbCards - 1) * amount * FocusSynergyPerOrbCard,
            "TemporaryFocusPower" => System.Math.Max(0, remainingOrbCards - 1) * amount * FocusSynergyPerOrbCard,

            // v0.6.9 — VigorPower (TERRAFORMING etc.): next attack +N damage,
            // single-shot. Beneficiary = 1 attack in hand (the next one played).
            // PowerCatalog already values Vigor ~250 for stack; HandSynergy
            // adds a per-shot scaling when an attack is queued.
            "VigorPower" => remainingAttacks > 0 ? amount * 50 : -100,

            _ => 0,
        };
    }

    private static int RemainingHits(SimCard self, SimState state)
    {
        int total = 0;
        foreach (var c in state.Hand)
        {
            if (ReferenceEquals(c, self) || !c.IsAttack) continue;
            total += System.Math.Max(1, c.Hits);
        }
        return total;
    }

    /// <summary>
    /// Estimated HP saved by applying Weak. Per-hit STS-accurate model: each
    /// enemy attack hit deals floor((IntentDamage + Strength) × 0.75), so
    /// savings per hit = perHit − floor(perHit × 0.75) ≈ ceil(perHit × 0.25).
    /// Multi-hit enemies multiply this savings by IntentRepeats; Weak persisting
    /// over multiple turns multiplies again, capped at WeakSavingsTurnCap.
    ///
    /// v0.7.25 — Two refinements:
    ///   1. Non-attack-intent enemies (buffing/healing this turn) still count
    ///      towards Weak value because Weak stacks decay 1/turn — applying now
    ///      mitigates THEIR FUTURE attack turns. Uses a baseline 8 dmg × 1 hit
    ///      estimate, halved for future-intent uncertainty. First turn lapses
    ///      (no current-turn attack to mitigate) so effectiveTurns -= 1.
    ///   2. Cap uses RemainingTurnsEstimator instead of hard-coded 2. High-stack
    ///      Weak on long boss fights now properly scales up to 4 turns.
    /// </summary>
    private static int ComputeWeakSavings(int weakStacks, SimState state)
    {
        if (weakStacks <= 0) return 0;
        int remainingTurns = RemainingTurnsEstimator.From(state);
        int turnCap = System.Math.Min(weakStacks, System.Math.Min(remainingTurns, WeakSavingsTurnCap));
        if (turnCap <= 0) return 0;

        int hpSaved = 0;
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive || e.IsInert) continue;

            // Classify intent. DeathBlow with intent damage acts like attack intent.
            bool currentTurnAttacks = e.HasAttackIntent
                                    || (e.HasDeathBlowIntent && e.IntentDamage > 0);
            bool futureIntentAttack = !currentTurnAttacks
                                    && (e.HasBuffIntent || e.HasDebuffIntent
                                        || e.HasHealIntent || e.HasDefendIntent
                                        || e.HasSummonIntent || e.HasStatusIntent);
            if (!currentTurnAttacks && !futureIntentAttack) continue;

            int perHit, hits;
            if (currentTurnAttacks)
            {
                perHit = e.IntentDamage + System.Math.Max(0, e.StrengthAmount);
                hits = System.Math.Max(1, e.IntentRepeats);
            }
            else
            {
                // Conservative baseline: enemy will likely attack in their next
                // attack turn at roughly this profile.
                perHit = WeakNonAttackBaselineDmg + System.Math.Max(0, e.StrengthAmount);
                hits = WeakNonAttackBaselineHits;
            }

            int perHitSavings = perHit - (int)(perHit * 0.75);
            if (perHitSavings <= 0) continue;
            int turnSavings = perHitSavings * hits;

            int effectiveTurns = turnCap;
            if (!currentTurnAttacks)
            {
                // First Weak turn lapses on a non-attacking intent — by the next
                // turn one stack has already decayed.
                effectiveTurns = System.Math.Max(0, effectiveTurns - 1);
            }
            if (effectiveTurns <= 0) continue;

            int contribution = turnSavings * effectiveTurns;
            // Future-intent enemies: halve for uncertainty (they may keep
            // non-attacking, or die first, etc.)
            if (!currentTurnAttacks) contribution /= 2;
            hpSaved += contribution;
        }
        return hpSaved * WeakSavingsPerHpPoint;
    }
}
