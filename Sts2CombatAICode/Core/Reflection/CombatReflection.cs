using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Reflection;

/// <summary>
/// Reflection access points for read-only combat state inspection.
/// Copied verbatim from Sts2CombatAdvisor/Sts2CombatAdvisorCode/Reflection/CombatReflection.cs
/// for project independence (per plan decision). Startup logs every NULL entry so a game-update
/// rename is immediately visible.
/// </summary>
internal static class CombatReflection
{
    public static readonly FieldInfo? CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");

    public static readonly FieldInfo? CreatureHpField =
        AccessTools.Field(typeof(Creature), "_currentHp");
    public static readonly FieldInfo? CreatureMaxHpField =
        AccessTools.Field(typeof(Creature), "_maxHp");
    public static readonly FieldInfo? CreatureBlockField =
        AccessTools.Field(typeof(Creature), "_block");

    public static readonly FieldInfo? PcsEnergyField =
        AccessTools.Field(typeof(PlayerCombatState), "_energy");
    public static readonly FieldInfo? PcsStarsField =
        AccessTools.Field(typeof(PlayerCombatState), "_stars");

    public static readonly FieldInfo? PowerAmountField =
        AccessTools.Field(typeof(PowerModel), "_amount");

    // v0.1.2 — minion detection (monster freshly spawned this turn).
    public static readonly FieldInfo? MonsterSpawnedThisTurnField =
        AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");

    // v0.2.4 — extract status power amount by class-name match.
    // Creature.Powers is a public IReadOnlyList<PowerModel>; walk it and match class names.
    public static int GetPowerAmount(Creature creature, string powerTypeName)
    {
        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null) continue;
                if (string.Equals(power.GetType().Name, powerTypeName, System.StringComparison.Ordinal))
                {
                    var v = PowerAmountField?.GetValue(power);
                    if (v is int i) return i;
                    if (v is decimal d) return (int)d;
                    return System.Convert.ToInt32(v);
                }
            }
        }
        catch (System.Exception ex) { LogReflectionFailureOnce($"power/{powerTypeName}", ex); }
        return 0;
    }

    /// <summary>
    /// 2026-05-30 — read a power's public DisplayAmount (the in-game counter shown
    /// on the power icon), which for some powers exposes INTERNAL state the Amount
    /// field hides. OrbitPower.DisplayAmount = 4 − (energySpent % 4) reveals its
    /// internal /4 energy counter; MonologuePower.DisplayAmount = StrengthApplied.
    /// Returns -1 when the power is absent or DisplayAmount throws.
    /// </summary>
    public static int GetPowerDisplayAmount(Creature creature, string powerTypeName)
    {
        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null) continue;
                if (!string.Equals(power.GetType().Name, powerTypeName, System.StringComparison.Ordinal))
                    continue;
                var prop = power.GetType().GetProperty("DisplayAmount");
                if (prop == null) return -1;
                var v = prop.GetValue(power);
                return v switch { int i => i, decimal d => (int)d, null => -1, _ => System.Convert.ToInt32(v) };
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// Dump every active power on the creature as a (class-name → amount) map.
    /// Lets SimEnemy carry a generic snapshot of all enemy powers (Thorns / Intangible /
    /// Weak / Buffer / Regen / ...) without us having to enumerate each one up-front.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyDictionary<string, int> GetAllPowers(Creature creature)
    {
        var dict = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
        try
        {
            foreach (var p in creature.Powers)
            {
                if (p == null) continue;
                var name = p.GetType().Name;
                var v = PowerAmountField?.GetValue(p);
                int amt = v switch
                {
                    int i => i,
                    decimal d => (int)d,
                    null => 1,
                    _ => System.Convert.ToInt32(v),
                };
                dict[name] = amt;
            }
        }
        catch (System.Exception ex) { LogReflectionFailureOnce("powers-dump", ex); }
        return dict;
    }

    // v0.10 — Relic snapshot. Player.Relics returns IReadOnlyList<RelicModel>; we
    // capture (class-name → counter). The counter is RelicModel.DisplayAmount when
    // the relic exposes a counter (ShowCounter==true) — e.g. PenNib's
    // AttacksPlayed % 10, IronClub's CardsPlayed % 4, VelvetChoker's
    // _cardsPlayedThisTurn. For passive relics with no counter (Anchor, Vajra,
    // BronzeScales, …) the entry is present with value 1, so callers can
    // distinguish "has relic" via ContainsKey from "absent".
    //
    // Presence is the primary signal for combat-relevant relics; the counter
    // matters only for trigger-counter relics where the scorer needs to know
    // "how close are we to the bonus." RelicCatalog (Phase 2) maps the
    // class-name to a flat value and special-case handlers for counters.
    public static System.Collections.Generic.IReadOnlyDictionary<string, int> GetPlayerRelics(Player player)
    {
        var dict = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
        try
        {
            var relics = player.Relics;
            if (relics == null) return dict;
            foreach (var r in relics)
            {
                if (r == null) continue;
                if (r.IsMelted) continue;
                var name = r.GetType().Name;
                int counter = 0;
                try
                {
                    if (r.ShowCounter) counter = r.DisplayAmount;
                }
                catch { /* DisplayAmount throws on some relics outside combat */ }
                // Presence-with-counter-0 should still register, so collapse to 1
                // when the relic doesn't expose a meaningful number.
                dict[name] = counter > 0 ? counter : 1;
            }
        }
        catch (System.Exception ex) { LogReflectionFailureOnce("relics-dump", ex); }
        return dict;
    }

    // v0.8.5 — One-shot logger to surface STS2 field-rename issues without
    // flooding godot.log every frame.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedFailures = new();
    private static void LogReflectionFailureOnce(string site, System.Exception ex)
    {
        if (_loggedFailures.TryAdd(site, 0))
            MainFile.Logger.Warn($"[CombatAI] reflection/{site} failed: {ex.Message}");
    }

    // AttackIntent — STS2 stores damage as `DamageCalc: Func<int>` (computed
    // dynamically per relic/power state) and multi-hit count as `Repeats: int`.
    // Per-frame access means the value reflects current Strength/Vulnerable etc.
    public static readonly Type? AttackIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent");

    public static readonly PropertyInfo? AttackIntentDamageCalcProp =
        AttackIntentType != null ? AccessTools.Property(AttackIntentType, "DamageCalc") : null;
    public static readonly PropertyInfo? AttackIntentRepeatsProp =
        AttackIntentType != null ? AccessTools.Property(AttackIntentType, "Repeats") : null;

    // Other intent subclass types — used for Classify(intent).
    // Concrete attacks: SingleAttackIntent + MultiAttackIntent both inherit from AttackIntent,
    // so AttackIntentType.IsInstanceOfType covers them too.
    public static readonly Type? DeathBlowIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.DeathBlowIntent");
    public static readonly Type? DefendIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.DefendIntent");
    public static readonly Type? BuffIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.BuffIntent");
    public static readonly Type? DebuffIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.DebuffIntent");
    public static readonly Type? CardDebuffIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.CardDebuffIntent");
    public static readonly Type? HealIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.HealIntent");
    public static readonly Type? SummonIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.SummonIntent");
    public static readonly Type? StatusIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.StatusIntent");
    public static readonly Type? StunIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.StunIntent");
    public static readonly Type? SleepIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.SleepIntent");
    public static readonly Type? EscapeIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.EscapeIntent");
    public static readonly Type? HiddenIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.HiddenIntent");
    public static readonly Type? UnknownIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.UnknownIntent");

    /// <summary>
    /// Classifies a runtime intent instance into one of the IntentKind buckets.
    /// Order matters: more-specific subclasses checked before the AttackIntent base.
    /// </summary>
    public static IntentKind Classify(object intent)
    {
        if (intent == null) return IntentKind.Other;
        // DeathBlow checked before Attack (DeathBlowIntent inherits from AttackIntent in many games — defensive).
        if (DeathBlowIntentType?.IsInstanceOfType(intent) == true) return IntentKind.DeathBlow;
        if (AttackIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Attack;
        if (DefendIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Defend;
        if (BuffIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Buff;
        if (CardDebuffIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Debuff;
        if (DebuffIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Debuff;
        if (HealIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Heal;
        if (SummonIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Summon;
        if (StatusIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Status;
        if (StunIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Inert;
        if (SleepIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Inert;
        if (EscapeIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Inert;
        if (HiddenIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Hidden;
        if (UnknownIntentType?.IsInstanceOfType(intent) == true) return IntentKind.Unknown;
        return IntentKind.Other;
    }

    public static int GetAttackIntentDamage(object intent)
    {
        if (AttackIntentDamageCalcProp?.GetValue(intent) is not Delegate calc) return 0;
        try
        {
            var result = calc.DynamicInvoke();
            return result == null ? 0 : Convert.ToInt32(result);
        }
        catch { return 0; }
    }

    public static int GetAttackIntentRepeats(object intent)
    {
        var v = AttackIntentRepeatsProp?.GetValue(intent);
        return v is int n && n > 0 ? n : 1;
    }

    static CombatReflection()
    {
        var nulls = new List<string>();
        void Check(string name, object? m) { if (m == null) nulls.Add(name); }
        Check(nameof(CombatManagerStateField), CombatManagerStateField);
        Check(nameof(CreatureHpField), CreatureHpField);
        Check(nameof(CreatureMaxHpField), CreatureMaxHpField);
        Check(nameof(CreatureBlockField), CreatureBlockField);
        Check(nameof(PcsEnergyField), PcsEnergyField);
        Check(nameof(PcsStarsField), PcsStarsField);
        Check(nameof(PowerAmountField), PowerAmountField);
        Check(nameof(MonsterSpawnedThisTurnField), MonsterSpawnedThisTurnField);
        Check(nameof(AttackIntentType), AttackIntentType);
        Check(nameof(AttackIntentDamageCalcProp), AttackIntentDamageCalcProp);
        Check(nameof(AttackIntentRepeatsProp), AttackIntentRepeatsProp);
        // Non-fatal — intent classify falls back to Other if a subclass type is missing.
        var intentNulls = new List<string>();
        void CheckIntent(string name, Type? t) { if (t == null) intentNulls.Add(name); }
        CheckIntent(nameof(DeathBlowIntentType), DeathBlowIntentType);
        CheckIntent(nameof(DefendIntentType), DefendIntentType);
        CheckIntent(nameof(BuffIntentType), BuffIntentType);
        CheckIntent(nameof(DebuffIntentType), DebuffIntentType);
        CheckIntent(nameof(CardDebuffIntentType), CardDebuffIntentType);
        CheckIntent(nameof(HealIntentType), HealIntentType);
        CheckIntent(nameof(SummonIntentType), SummonIntentType);
        CheckIntent(nameof(StatusIntentType), StatusIntentType);
        CheckIntent(nameof(StunIntentType), StunIntentType);
        CheckIntent(nameof(SleepIntentType), SleepIntentType);
        CheckIntent(nameof(EscapeIntentType), EscapeIntentType);
        CheckIntent(nameof(HiddenIntentType), HiddenIntentType);
        CheckIntent(nameof(UnknownIntentType), UnknownIntentType);

        if (nulls.Count > 0)
            MainFile.Logger.Warn($"[Reflection] {nulls.Count} member(s) NULL — game update may have changed: {string.Join(", ", nulls)}");
        else
            MainFile.Logger.Info("[Reflection] all combat targets resolved.");

        if (intentNulls.Count > 0)
            MainFile.Logger.Warn($"[Reflection] {intentNulls.Count} intent subtype(s) NULL — Classify will return Other for those: {string.Join(", ", intentNulls)}");
    }
}
