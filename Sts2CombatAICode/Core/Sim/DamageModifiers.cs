using System;
using System.Collections.Generic;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Stage of the damage resolution pipeline at which a modifier applies.
/// Order is fixed: Additive → Multiplicative → Cap. Modifiers within the
/// same stage execute in registration order (deterministic).
/// </summary>
internal enum DamageStage
{
    Additive,
    Multiplicative,
    Cap,
}

/// <summary>
/// Read-only context handed to each damage modifier for one damage instance
/// (one hit × one enemy). Value-type so the per-hit hot path doesn't allocate.
/// </summary>
internal readonly struct DamageContext
{
    public readonly SimState State;
    public readonly SimEnemy Target;
    public readonly SimCard Card;
    public readonly int HitIndex;
    public readonly int TotalHits;
    public readonly bool IsFirstAttackThisTurn;

    public DamageContext(SimState state, SimEnemy target, SimCard card,
        int hitIndex, int totalHits, bool isFirstAttackThisTurn)
    {
        State = state;
        Target = target;
        Card = card;
        HitIndex = hitIndex;
        TotalHits = totalHits;
        IsFirstAttackThisTurn = isFirstAttackThisTurn;
    }

    public bool IsFirstHitThisCard => HitIndex == 0;
}

/// <summary>
/// A damage modifier owns one power's contribution to the damage pipeline.
/// Each modifier hooks exactly one stage; the other two Apply* methods are
/// no-ops. Stack count is resolved by the registry against attacker or
/// defender state (per <see cref="AttackerSide"/>) before invocation —
/// if stack is 0 the modifier is skipped entirely.
/// </summary>
internal interface IDamageModifier
{
    /// Power identifier matching the source stack dictionary key
    /// (StrengthPower, VulnerablePower, IntangiblePower, …). Used by the
    /// registry to look up the current stack count + diagnostics.
    string PowerName { get; }

    /// Pipeline stage this modifier hooks. Calls to other stages return
    /// input unchanged.
    DamageStage Stage { get; }

    /// true → registry reads stack from player (PlayerPowers + explicit
    /// player fields); false → reads from the defending enemy (Target.Powers
    /// + explicit enemy fields).
    bool AttackerSide { get; }

    int ApplyAdditive(int damage, int stack, in DamageContext ctx);
    double ApplyMultiplicative(double damage, int stack, in DamageContext ctx);
    int ApplyCap(int damage, int stack, in DamageContext ctx);
}

/// <summary>
/// Helper base that defaults the two non-target stages to identity. Concrete
/// modifiers override exactly one Apply* method matching their <see cref="Stage"/>.
/// </summary>
internal abstract class DamageModifierBase : IDamageModifier
{
    public abstract string PowerName { get; }
    public abstract DamageStage Stage { get; }
    public virtual bool AttackerSide => true;
    public virtual int ApplyAdditive(int damage, int stack, in DamageContext ctx) => damage;
    public virtual double ApplyMultiplicative(double damage, int stack, in DamageContext ctx) => damage;
    public virtual int ApplyCap(int damage, int stack, in DamageContext ctx) => damage;
}

/// <summary>
/// Static registry of all damage modifiers. The damage pipeline (V2) calls
/// <see cref="Resolve"/> per hit per enemy. Registration is order-stable so
/// scenarios are deterministic across runs.
///
/// Adding a new active power = author one IDamageModifier + one Register call.
/// No changes to StatusMath / AnalyticalSimulator damage paths required.
/// </summary>
internal static class DamageModifierRegistry
{
    private static readonly List<IDamageModifier> _all = new();
    private static List<IDamageModifier>? _additive;
    private static List<IDamageModifier>? _mult;
    private static List<IDamageModifier>? _cap;

    public static IReadOnlyList<IDamageModifier> All => _all;

    /// Register a modifier. Cached stage partition is invalidated so the next
    /// Resolve call rebuilds it. Caller is responsible for one-time registration
    /// (typically a static initializer).
    public static void Register(IDamageModifier m)
    {
        _all.Add(m);
        _additive = _mult = _cap = null;
    }

    /// Clear all registrations. TEST-ONLY — production registration runs once
    /// via static initializer. Exposed for unit tests that need a clean slate.
    public static void ClearForTests()
    {
        _all.Clear();
        _additive = _mult = _cap = null;
    }

    private static void EnsurePartition()
    {
        if (_additive != null) return;
        _additive = new List<IDamageModifier>();
        _mult = new List<IDamageModifier>();
        _cap = new List<IDamageModifier>();
        for (int i = 0; i < _all.Count; i++)
        {
            var m = _all[i];
            switch (m.Stage)
            {
                case DamageStage.Additive: _additive.Add(m); break;
                case DamageStage.Multiplicative: _mult.Add(m); break;
                case DamageStage.Cap: _cap.Add(m); break;
            }
        }
    }

    /// Resolve one (hit × enemy) damage instance. Walks Additive → Mult → Cap,
    /// reading per-modifier stack counts from the side indicated by
    /// <see cref="IDamageModifier.AttackerSide"/>. Returns final per-hit damage
    /// (caller multiplies by hit count or sums per-hit results for multi-hit).
    public static int Resolve(int baseDamage, in DamageContext ctx)
    {
        EnsurePartition();

        int dmg = baseDamage;
        for (int i = 0; i < _additive!.Count; i++)
        {
            var m = _additive[i];
            int stack = ResolveStack(m, ctx);
            if (stack != 0) dmg = m.ApplyAdditive(dmg, stack, ctx);
        }

        double dmgF = dmg;
        for (int i = 0; i < _mult!.Count; i++)
        {
            var m = _mult[i];
            int stack = ResolveStack(m, ctx);
            if (stack != 0) dmgF = m.ApplyMultiplicative(dmgF, stack, ctx);
        }
        int final = Math.Max(0, (int)Math.Floor(dmgF));

        for (int i = 0; i < _cap!.Count; i++)
        {
            var m = _cap[i];
            int stack = ResolveStack(m, ctx);
            if (stack != 0) final = m.ApplyCap(final, stack, ctx);
        }
        return final;
    }

    /// Stack lookup. Attacker side reads from explicit player fields first
    /// (PlayerStrength etc.) then falls back to PlayerPowers dictionary.
    /// Defender side reads explicit enemy fields then Target.Powers.
    /// A modifier whose source isn't standard (e.g. relic count) should
    /// override by registering a custom stack accessor — left as a TODO
    /// (no current modifier needs this).
    private static int ResolveStack(IDamageModifier m, in DamageContext ctx)
    {
        if (m.AttackerSide)
        {
            // Explicit player-side fields for hot powers; dict fallback.
            switch (m.PowerName)
            {
                case "StrengthPower":  return ctx.State.PlayerStrength;
                case "VigorPower":     return ctx.State.PlayerVigor;
                case "WeakPower":      return ctx.State.PlayerWeak;
                // 2026-05-29 — ShrinkPower: stack = the percent (30) captured by
                // StateSnapshotter; >0 means the player's powered attacks ×0.7.
                case "ShrinkPower":    return ctx.State.PlayerShrinkPercent;
                case "LethalityPower": return ctx.State.PlayerLethality;
                case "TrackingPower":  return ctx.State.PlayerTracking;
                case "CrueltyPower":   return ctx.State.PlayerCruelty;
                case "AccuracyPower":  return ctx.State.PlayerAccuracy;
            }
            if (ctx.State.PlayerPowers != null
                && ctx.State.PlayerPowers.TryGetValue(m.PowerName, out var v))
                return v;
            return 0;
        }
        // Defender side — enemy state.
        switch (m.PowerName)
        {
            case "VulnerablePower":   return ctx.Target.VulnerableAmount;
            case "WeakPower":         return ctx.Target.WeakAmount;
            case "IntangiblePower":   return ctx.Target.DamageCapPerHit > 0 ? 1 : 0;
            // HardToKill maps to the same DamageCapPerHit field —
            // SimEnemy folds Intangible / HardToKill into DamageCapPerHit
            // at snapshot time. Cap-side modifiers read that field.
            case "HardToKillPower":   return ctx.Target.DamageCapPerHit > 0 ? 1 : 0;
        }
        if (ctx.Target.Powers != null
            && ctx.Target.Powers.TryGetValue(m.PowerName, out var ev))
            return ev;
        return 0;
    }
}

// ─── Baseline modifiers (V1 parity) ──────────────────────────────────────
// These replicate StatusMath.EffectivePerHitCapped's exact arithmetic so
// V1==V2 holds for all existing scenarios. Registered by
// BaselineDamageModifiers.RegisterAll() — called once at static init time.

internal sealed class StrengthAdditiveModifier : DamageModifierBase
{
    public override string PowerName => "StrengthPower";
    public override DamageStage Stage => DamageStage.Additive;
    public override bool AttackerSide => true;
    public override int ApplyAdditive(int damage, int stack, in DamageContext ctx)
        => damage + stack;
}

internal sealed class VigorAdditiveModifier : DamageModifierBase
{
    public override string PowerName => "VigorPower";
    public override DamageStage Stage => DamageStage.Additive;
    public override bool AttackerSide => true;
    public override int ApplyAdditive(int damage, int stack, in DamageContext ctx)
    {
        // STS canonical: Vigor adds to base damage on the first attack played,
        // applied to every hit of that card; consumed after card resolves.
        // The caller (AnalyticalSimulator) is responsible for clearing Vigor
        // post-card so this modifier doesn't re-fire on a subsequent attack.
        return damage + stack;
    }
}

internal sealed class AccuracyShivBonusModifier : DamageModifierBase
{
    public override string PowerName => "AccuracyPower";
    public override DamageStage Stage => DamageStage.Additive;
    public override bool AttackerSide => true;
    public override int ApplyAdditive(int damage, int stack, in DamageContext ctx)
        => ctx.Card != null && ctx.Card.Id == "SHIV" ? damage + stack : damage;
}

internal sealed class VulnerableMultModifier : DamageModifierBase
{
    public override string PowerName => "VulnerablePower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => false;  // reads enemy.VulnerableAmount
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => damage * StatusMath.VulnerableMult;
}

internal sealed class WeakMultModifier : DamageModifierBase
{
    public override string PowerName => "WeakPower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => true;  // attacker is Weak → -25%
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => damage * StatusMath.WeakMult;
}

// 2026-05-29 — player ShrinkPower reduces outgoing powered-attack damage by
// DamageDecrease% (sts2.dll: (100 - 30)/100 = ×0.7). Found via the 3rd
// diagnostic layer (HP-application trace): SOVEREIGN_BLADE 15=>10.5,
// HEAVENLY_DRILL 8=>5.6 with player ShrinkPower, sim dealt full. stack =
// the percent captured by StateSnapshotter (30); 0 = no shrink.
internal sealed class ShrinkMultModifier : DamageModifierBase
{
    public override string PowerName => "ShrinkPower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => true;  // attacker (player) is shrunk
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => stack > 0 ? damage * (100 - stack) / 100.0 : damage;
}

internal sealed class TrackingVsWeakModifier : DamageModifierBase
{
    public override string PowerName => "TrackingPower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => true;
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => ctx.Target.WeakAmount > 0 ? damage * StatusMath.TrackingMult : damage;
}

// 2026-05-31 — Cruelty is ADDITIVE to the Vulnerable multiplier, not a separate
// ×1.25 chain. Decompile (CrueltyPower.ModifyVulnerableMultiplier): the vuln
// multiplier becomes 1.5 + Amount/100 (Amount=25 → 1.75), NOT 1.5 × 1.25 (=1.875).
// VulnerableMultModifier already applied ×1.5, so fold cruelty in as
// ×(1 + Amount/(100·VulnerableMult)) → combined = base × (1.5 + Amount/100).
// stack = the live Cruelty Amount (StateSnapshotter PlayerCruelty). Verified vs
// 3 ANGER rows: 6 × 1.75 = 10.5 (was 6 × 1.875 = 11.25, consistent −1 over-deal).
internal sealed class CrueltyVsVulnerableModifier : DamageModifierBase
{
    public override string PowerName => "CrueltyPower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => true;
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => ctx.Target.VulnerableAmount > 0 && stack > 0
            ? damage * (1.0 + stack / (100.0 * StatusMath.VulnerableMult))
            : damage;
}

// 2026-06-01 §130 — DebilitatePower (enemy debuff, card Debilitate: "Vulnerable and Weak
// are twice as effective against this enemy for N turns"). The stack is a turn COUNTER
// (3/2/1 — decrements each turn), magnitude-INDEPENDENT: any stack > 0 doubles the
// Vulnerable damage BONUS, so vuln goes 1.5× → 2.0× (the 0.5 bonus becomes 1.0). The sim's
// hardcoded VulnerableMult=1.5 ignored it → STRIKE_NECROBINDER 6×1.5=9 vs real 6×2.0=12,
// FEAR 7×1.5=10 vs 14 (enemy_hp +3/+4 on every Debilitate-marked target). Derive the live
// vuln factor f = 1.5 + Cruelty/100 from STATE (not the running damage, so this composes
// order-independently with VulnerableMult + Cruelty) and rescale by (2f−1)/f so the bonus
// doubles. Trace evidence: "x VulnerablePower(in=6) => x2.0". (Weak-doubling lives in the
// enemy-turn sim, a separate path; this modifier only governs player card damage.)
internal sealed class DebilitateVsVulnerableModifier : DamageModifierBase
{
    public override string PowerName => "DebilitatePower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => false;  // reads enemy.Powers["DebilitatePower"]
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
    {
        if (stack <= 0 || ctx.Target.VulnerableAmount <= 0) return damage;
        double f = StatusMath.VulnerableMult
                 + (ctx.State.PlayerCruelty > 0 ? ctx.State.PlayerCruelty / 100.0 : 0.0);
        return damage * (2.0 * f - 1.0) / f;
    }
}

internal sealed class LethalityFirstAttackModifier : DamageModifierBase
{
    public override string PowerName => "LethalityPower";
    public override DamageStage Stage => DamageStage.Multiplicative;
    public override bool AttackerSide => true;
    public override double ApplyMultiplicative(double damage, int stack, in DamageContext ctx)
        => ctx.IsFirstAttackThisTurn ? damage * StatusMath.LethalityMult : damage;
}

internal sealed class IntangibleCapModifier : DamageModifierBase
{
    public override string PowerName => "IntangiblePower";
    public override DamageStage Stage => DamageStage.Cap;
    public override bool AttackerSide => false;
    public override int ApplyCap(int damage, int stack, in DamageContext ctx)
    {
        // SimEnemy.DamageCapPerHit folds Intangible + HardToKill. Both cap
        // to the field value. ResolveStack returns 1 if the cap is active so
        // we read the actual cap directly from Target here.
        int cap = ctx.Target.DamageCapPerHit;
        if (cap > 0 && damage > cap) return cap;
        return damage;
    }
}

/// <summary>
/// One-time static registration of the V1-parity modifier set. Called by
/// the static initializer at the top of <see cref="DamageModifierBootstrap"/>
/// — guarded so test code can reset + re-register via ClearForTests().
/// </summary>
internal static class BaselineDamageModifiers
{
    public static void RegisterAll()
    {
        var r = DamageModifierRegistry.All;
        if (r.Count > 0) return;  // already registered

        // Stage order documented; within-stage order matches StatusMath's
        // V1 sequence (Strength + Vigor + Accuracy → Vulnerable × Weak ×
        // multipliers → Intangible cap) for V1==V2 parity.

        // --- Additive (attacker side, base damage adjustment) ---
        DamageModifierRegistry.Register(new StrengthAdditiveModifier());
        DamageModifierRegistry.Register(new VigorAdditiveModifier());
        // 2026-05-29 — AccuracyShivBonusModifier intentionally NOT registered.
        // AccuracyPower's +stack shiv bonus is already folded into the base via
        // StatusMath.ApplyCardSpecificDamageBonus (used by BOTH AnalyticalSimulator
        // line 431 and PlanScorer line 741). Registering the modifier too applied
        // Accuracy a SECOND time on the AnalyticalSimulator path — SHIV over-dealt
        // by exactly PlayerAccuracy (sim_parity SHIV enemy_hp -4 with Accuracy 4).
        // Keep the single fold (shared by sim+planner); drop the duplicate modifier.

        // --- Multiplicative (defender-vuln then attacker-weak, then
        //     conditional Tracking / Cruelty / Lethality) ---
        DamageModifierRegistry.Register(new VulnerableMultModifier());
        DamageModifierRegistry.Register(new WeakMultModifier());
        DamageModifierRegistry.Register(new ShrinkMultModifier());
        DamageModifierRegistry.Register(new TrackingVsWeakModifier());
        DamageModifierRegistry.Register(new CrueltyVsVulnerableModifier());
        DamageModifierRegistry.Register(new DebilitateVsVulnerableModifier());
        DamageModifierRegistry.Register(new LethalityFirstAttackModifier());

        // --- Cap (Intangible / HardToKill via DamageCapPerHit) ---
        DamageModifierRegistry.Register(new IntangibleCapModifier());
    }
}

/// <summary>
/// Module initializer hook — registers baseline modifiers once when this
/// assembly first loads. Avoids "did anyone call RegisterAll?" coupling.
/// </summary>
internal static class DamageModifierBootstrap
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => BaselineDamageModifiers.RegisterAll();
}
