using System;

namespace Sts2CombatAI.Sim;

/// <summary>
/// STS canonical multipliers. Approximates STS2's resolution math; exact game uses
/// decimals but planner ordering only needs integer ranks, so float ops are fine.
///
/// Damage:
///   final = floor((base + attackerStrength + attackerVigor) × (defenderVulnerable ? 1.5 : 1.0)
///                                                         × (attackerWeak ? 0.75 : 1.0))
///   (Vigor applies to the FIRST attack only this turn — caller is responsible for
///   passing 0 for non-first attacks. EffectivePerEnemyTotal applies Vigor only on
///   the first hit; subsequent hits within the same multi-hit card use Strength only.)
/// Block:
///   final = floor((base + defenderDexterity) × (defenderFrail ? 0.75 : 1.0))
/// </summary>
internal static class StatusMath
{
    public const double VulnerableMult = 1.5;
    public const double WeakMult = 0.75;
    public const double FrailMult = 0.75;

    // v0.7.84 — Damage multiplier powers (Silent Volatile / passive).
    public const double LethalityMult = 1.5;   // first attack/turn (single-shot)
    public const double TrackingMult  = 2.0;   // vs Weak target (passive)
    // Cruelty is NOT a flat multiplier — it ADDS Amount/100 to the Vulnerable
    // multiplier (real CrueltyPower.ModifyVulnerableMultiplier). Kept for ref only;
    // the live math reads the actual Cruelty Amount. Do not multiply by this.
    public const double CrueltyMult   = 1.25;  // DEPRECATED ref (Amount=25 → +0.25)

    /// <summary>
    /// v0.7.84 — Apply per-target conditional damage multipliers (Tracking,
    /// Cruelty, Lethality). Returns the multiplied damage. Lethality is
    /// single-shot: the caller MUST gate it (first attack of the turn) by
    /// passing <paramref name="lethalityActive"/>=true only for the first call.
    /// </summary>
    public static int ApplyDamageMultipliers(int damage, SimState state, bool defenderVulnerable,
        bool defenderWeak, bool lethalityActive)
    {
        if (damage <= 0) return 0;
        double v = damage;
        if (state.PlayerTracking > 0 && defenderWeak) v *= TrackingMult;
        // Cruelty ADDS Amount/100 to the Vulnerable multiplier (1.5 → 1.5+Amt/100),
        // it is NOT a separate ×1.25 chain. v already carries ×VulnerableMult, so
        // apply ×(1 + Amt/(100·VulnerableMult)). See CrueltyVsVulnerableModifier.
        if (state.PlayerCruelty > 0 && defenderVulnerable)
            v *= 1.0 + state.PlayerCruelty / (100.0 * VulnerableMult);
        if (state.PlayerLethality > 0 && lethalityActive) v *= LethalityMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    /// <summary>
    /// v0.7.86 — Apply card-id-specific damage adders before the multiplier
    /// chain. Currently: AccuracyPower → +N damage per Shiv card. Returns
    /// adjusted base damage to feed into EffectiveAttackDmg / EffectivePerEnemyTotal.
    /// </summary>
    public static int ApplyCardSpecificDamageBonus(int baseDamage, string? cardId, SimState state)
    {
        if (string.IsNullOrEmpty(cardId)) return baseDamage;
        if (cardId == "SHIV")
        {
            int d = baseDamage;
            if (state.PlayerAccuracy > 0) d += state.PlayerAccuracy;
            // 2026-05-30 — PhantomBlades: +Amount to the FIRST shiv each turn
            // (ModifyDamageAdditive returns Amount only when shivs-played-this-turn
            // == 0). Subsequent shivs get nothing.
            if (state.PlayerPhantomBlades > 0 && state.TurnShivsPlayed == 0)
                d += state.PlayerPhantomBlades;
            return d;
        }
        return baseDamage;
    }

    // v0.7.82 — Vigor-aware overload. Adds attackerVigor to the additive part
    // (same step as Strength). 0-arg legacy overload preserved below for callers
    // that don't track Vigor.
    public static int EffectiveAttackDmg(int baseDamage, int attackerStrength,
        int attackerVigor, bool defenderVulnerable, bool attackerWeak)
    {
        if (baseDamage <= 0) return 0;
        double v = baseDamage + attackerStrength + attackerVigor;
        if (defenderVulnerable) v *= VulnerableMult;
        if (attackerWeak) v *= WeakMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    public static int EffectiveAttackDmg(int baseDamage, int attackerStrength,
        bool defenderVulnerable, bool attackerWeak)
        => EffectiveAttackDmg(baseDamage, attackerStrength, 0, defenderVulnerable, attackerWeak);

    public static int EffectiveBlock(int baseBlock, int defenderDexterity, bool defenderFrail)
    {
        if (baseBlock <= 0) return 0;
        double v = baseBlock + defenderDexterity;
        // 2026-05-28 reverted Frail-skip: sim_parity probe shows two populations:
        //  A) real doesn't apply Frail (mod overshoots by Frail × 25%)
        //  B) real applies Frail canonically (mod undershoots when Frail off)
        // Net: Frail-canonical is the more common case post BaseValue fix.
        // Keep Frail mult; investigate Population A separately (likely
        // Frail-timing edge case where Frail decrements pre-play).
        if (defenderFrail) v *= FrailMult;
        return Math.Max(0, (int)Math.Floor(v));
    }

    /// <summary>
    /// Per-hit attack damage clamped by the target's IntangiblePower / HardToKill
    /// cap (<see cref="SimEnemy.DamageCapPerHit"/>). 0 cap means uncapped.
    /// v0.7.82 — Vigor parameter. Caller passes 0 for non-first-attack contexts.
    /// </summary>
    public static int EffectivePerHitCapped(int baseDamage, int attackerStrength, int attackerVigor,
        SimEnemy target, bool attackerWeak)
    {
        int per = EffectiveAttackDmg(baseDamage, attackerStrength, attackerVigor,
            target.VulnerableAmount > 0, attackerWeak);
        if (target.DamageCapPerHit > 0 && per > target.DamageCapPerHit)
            per = target.DamageCapPerHit;
        return per;
    }

    public static int EffectivePerHitCapped(int baseDamage, int attackerStrength,
        SimEnemy target, bool attackerWeak)
        => EffectivePerHitCapped(baseDamage, attackerStrength, 0, target, attackerWeak);

    /// <summary>
    /// Per-target total attack damage: hits × per-hit (after Intangible cap), then
    /// clamped by HardenedShellRemaining. If the target has HardenedShellPower but
    /// the budget is fully spent (Remaining == 0), returns 0.
    /// v0.7.82 — Vigor is added to base damage on play (STS canonical: "your next
    /// attack deals additional damage equal to Vigor"). It applies to every hit of
    /// the card as a per-hit bonus, then consumed when the card resolves. Multi-card
    /// estimators (IsLethalThisTurn) must pass Vigor=0 for subsequent attacks.
    /// </summary>
    public static int EffectivePerEnemyTotal(int baseDamage, int hits, int attackerStrength,
        int attackerVigor, SimEnemy target, bool attackerWeak)
    {
        int perHit = EffectivePerHitCapped(baseDamage, attackerStrength, attackerVigor,
            target, attackerWeak);
        int hitsClamped = Math.Max(1, hits);
        int total = perHit * hitsClamped;
        if (target.HardenedShellRemaining > 0)
        {
            if (total > target.HardenedShellRemaining)
                total = target.HardenedShellRemaining;
        }
        else if (perHit > 0 && target.Powers.ContainsKey("HardenedShellPower"))
        {
            total = 0;
        }
        return total;
    }

    public static int EffectivePerEnemyTotal(int baseDamage, int hits, int attackerStrength,
        SimEnemy target, bool attackerWeak)
        => EffectivePerEnemyTotal(baseDamage, hits, attackerStrength, 0, target, attackerWeak);

    // ─── V2 (DamageModifierRegistry-driven) ──────────────────────────────
    // Parallel pipeline that delegates per-hit damage to the modifier
    // registry. Currently produces V1-parity results when the baseline
    // modifier set is registered. AnalyticalSimulator flips between V1
    // and V2 via the feature flag — see IDamageModifier doc for the
    // migration plan.

    public static int EffectivePerHitCappedV2(int baseDamage,
        SimEnemy target, SimCard card, SimState state,
        int hitIndex, int totalHits, bool isFirstAttackThisTurn)
    {
        // Defensive — module initializer may not fire when this file is
        // source-included into another csproj. Idempotent; no-op once
        // the baseline set is registered.
        BaselineDamageModifiers.RegisterAll();

        var ctx = new DamageContext(state, target, card,
            hitIndex, totalHits, isFirstAttackThisTurn);
        int per = DamageModifierRegistry.Resolve(baseDamage, in ctx);
        return per;
    }

    public static int EffectivePerEnemyTotalV2(int baseDamage, int hits,
        SimEnemy target, SimCard card, SimState state, bool isFirstAttackThisTurn)
    {
        if (baseDamage <= 0) return 0;
        // hits=0 is valid for X-cost cards at 0 energy → no damage.
        if (hits <= 0) return 0;
        int hitsClamped = hits;
        int total = 0;
        for (int h = 0; h < hitsClamped; h++)
        {
            // isFirstAttackThisTurn is a CARD-level flag (this whole card is
            // the first attack of the turn). All hits of that card pick up the
            // Lethality ×1.5 multiplier — V1's ApplyDamageMultipliers folds
            // Lethality into the total once, so the per-hit V2 sum agrees only
            // when all hits get the bonus.
            total += EffectivePerHitCappedV2(baseDamage, target, card, state,
                hitIndex: h, totalHits: hitsClamped,
                isFirstAttackThisTurn: isFirstAttackThisTurn);
        }
        // HardenedShell — post-total cap. Stays outside the registry since
        // it acts on the sum, not per-hit. (Could be promoted to a
        // "PostTotalCap" stage if other powers want the same shape.)
        if (target.HardenedShellRemaining > 0)
        {
            if (total > target.HardenedShellRemaining)
                total = target.HardenedShellRemaining;
        }
        else if (total > 0 && target.Powers != null
            && target.Powers.ContainsKey("HardenedShellPower"))
        {
            // Shell power present but Remaining counter not snapshotted —
            // matches V1 conservative "treat as fully spent" branch.
            total = 0;
        }
        return total;
    }
}
