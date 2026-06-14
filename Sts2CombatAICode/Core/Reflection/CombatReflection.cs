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

    // 2026-06-14 perf — StateSnapshotter.Capture calls GetPowerAmount ~75× per
    // snapshot, each a full scan of creature.Powers (GetType().Name compares). On the
    // hot planner/search path (Capture per decision × candidates × rollout depth) that
    // is the dominant cost. Inside a Begin/EndPowerCapture() scope we scan each
    // creature's Powers ONCE into a name→amount map and serve the 75 reads O(1).
    // Outside the scope (other callers) the original direct scan is used unchanged.
    [System.ThreadStatic] private static bool _capActive;
    [System.ThreadStatic] private static System.Collections.Generic.Dictionary<Creature, System.Collections.Generic.Dictionary<string, int>>? _capMaps;

    public static void BeginPowerCapture()
    {
        _capActive = true;
        (_capMaps ??= new System.Collections.Generic.Dictionary<Creature, System.Collections.Generic.Dictionary<string, int>>()).Clear();
    }
    public static void EndPowerCapture()
    {
        _capActive = false;
        _capMaps?.Clear();
    }

    private static int PowVal(PowerModel power)
    {
        var v = PowerAmountField?.GetValue(power);
        if (v is int i) return i;
        if (v is decimal d) return (int)d;
        return System.Convert.ToInt32(v);
    }

    // v0.2.4 — extract status power amount by class-name match.
    // Creature.Powers is a public IReadOnlyList<PowerModel>; walk it and match class names.
    public static int GetPowerAmount(Creature creature, string powerTypeName)
    {
        try
        {
            if (_capActive && _capMaps != null)
            {
                if (!_capMaps.TryGetValue(creature, out var map))
                {
                    map = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
                    foreach (var power in creature.Powers)
                        if (power != null) map[power.GetType().Name] = PowVal(power);
                    _capMaps[creature] = map;
                }
                return map.TryGetValue(powerTypeName, out var amt) ? amt : 0;
            }
            foreach (var power in creature.Powers)
            {
                if (power == null) continue;
                if (string.Equals(power.GetType().Name, powerTypeName, System.StringComparison.Ordinal))
                    return PowVal(power);
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
    /// 2026-06-08 — KaiserCrab BackAttack. The player carries SurroundedPower whose public
    /// Facing (Direction enum: Right=0, Left=1) tracks the arm last targeted. The arm NOT faced
    /// back-attacks for ×1.5 (SurroundedPower.ModifyDamageMultiplicative). Returns:
    /// 0 = no SurroundedPower (not a crab fight), 1 = facing Left (Crusher/BackAttackLeft faced
    /// → Rocket back-attacks), 2 = facing Right (Rocket/BackAttackRight faced → Crusher back-attacks).
    /// </summary>
    public static int GetSurroundedFacing(Creature creature)
    {
        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null || !string.Equals(power.GetType().Name, "SurroundedPower", System.StringComparison.Ordinal))
                    continue;
                var prop = power.GetType().GetProperty("Facing");
                if (prop == null) return 0;
                var v = prop.GetValue(power);
                int d = System.Convert.ToInt32(v);   // Direction: Right=0, Left=1
                return d == 1 ? 1 : 2;                // Left→1, Right→2
            }
        }
        catch (System.Exception ex) { LogReflectionFailureOnce("surrounded-facing", ex); }
        return 0;
    }

    /// <summary>
    /// 2026-05-31 — read a named int field from a power's private _internalData object
    /// (PowerModel._internalData holds the per-power Data instance). Some powers keep
    /// their real trigger counter ONLY there, with no Amount / DisplayAmount accessor —
    /// e.g. JugglingPower.Data.attacksPlayedThisTurn (fires on the 3rd attack of the
    /// turn). The game seeds/increments it itself, so reading it directly is the only
    /// way to align the sim's trigger with the engine. Returns -1 when absent/unreadable.
    /// </summary>
    public static int GetPowerInternalCounter(Creature creature, string powerTypeName, string fieldName)
    {
        try
        {
            foreach (var power in creature.Powers)
            {
                if (power == null) continue;
                if (!string.Equals(power.GetType().Name, powerTypeName, System.StringComparison.Ordinal))
                    continue;
                // _internalData is a private field on the PowerModel base — walk up.
                System.Reflection.FieldInfo? dataField = null;
                for (var t = power.GetType(); t != null && dataField == null; t = t.BaseType)
                    dataField = t.GetField("_internalData",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var data = dataField?.GetValue(power);
                if (data == null) return -1;
                var fld = data.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (fld == null) return -1;
                var v = fld.GetValue(data);
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

    /// <summary>
    /// 2026-06-04 — Snapshot the player's held potions into SimPotion so the planner can
    /// evaluate using them in the lookahead. Reads Id + TargetType + the canonical effect var.
    /// Only the impactful additive kinds are classified; anything else maps to PotionKind.Other
    /// (a no-op in ApplyPotionUse — never invents value).
    /// </summary>
    public static System.Collections.Generic.List<SimPotion> GetPlayerPotions(Player player)
    {
        var list = new System.Collections.Generic.List<SimPotion>();
        try
        {
            if (player?.Potions == null) return list;
            foreach (var p in player.Potions)
            {
                if (p == null) continue;
                try
                {
                    string id = p.Id.Entry;
                    string target = p.TargetType.ToString();
                    var vars = p.DynamicVars;   // IReadOnlyDictionary<string, DynamicVar>

                    PotionKind kind = PotionKind.Other;
                    int amount = 0;
                    int V(string key) => vars.TryGetValue(key, out var dv) ? (int)dv.BaseValue : 0;

                    // Player-buff potions are Self/AnyPlayer-targeted. Gate the stat kinds on that so
                    // an ENEMY-targeted potion that reuses a player-power var (e.g. SHACKLING_POTION =
                    // PowerVar<StrengthPower> applied to AllEnemies as a strength-DOWN debuff) is NOT
                    // misread as "+Strength to me". Such cases fall through to Other (safe no-op).
                    bool selfBuff = target == "Self" || target == "AnyPlayer";

                    if (id == "GIGANTIFICATION_POTION")          { kind = PotionKind.AttackTriple; amount = 3; }
                    // FORTIFIER gains block = current block × 2 (computed in OnUse, no Block var) →
                    // must be matched by Id, not by a flat var. Amount = the multiplier added.
                    else if (id == "FORTIFIER")                  { kind = PotionKind.BlockMult; amount = 2; }
                    else if (selfBuff && vars.ContainsKey("Block"))          { kind = PotionKind.Block;     amount = V("Block"); }
                    else if (selfBuff && vars.ContainsKey("Heal"))           { kind = PotionKind.Heal;      amount = V("Heal"); }
                    else if (vars.ContainsKey("Damage"))                     { kind = PotionKind.Damage;    amount = V("Damage"); }
                    else if (selfBuff && vars.ContainsKey("StrengthPower"))  { kind = PotionKind.Strength;  amount = V("StrengthPower"); }
                    else if (selfBuff && vars.ContainsKey("DexterityPower")) { kind = PotionKind.Dexterity; amount = V("DexterityPower"); }
                    else if (selfBuff && vars.ContainsKey("FocusPower"))     { kind = PotionKind.Focus;     amount = V("FocusPower"); }
                    else if (selfBuff && vars.ContainsKey("Energy"))         { kind = PotionKind.Energy;    amount = V("Energy"); }
                    // Enemy debuffs — PowerVar<T> keys on typeof(T).Name. AllEnemies vs single is
                    // carried by `target` (TargetType) and handled in ApplyPotionUse.
                    else if (vars.ContainsKey("VulnerablePower")) { kind = PotionKind.EnemyVuln;   amount = V("VulnerablePower"); }
                    else if (vars.ContainsKey("WeakPower"))       { kind = PotionKind.EnemyWeak;   amount = V("WeakPower"); }
                    else if (vars.ContainsKey("PoisonPower"))     { kind = PotionKind.EnemyPoison; amount = V("PoisonPower"); }
                    else if (vars.ContainsKey("DoomPower"))       { kind = PotionKind.EnemyDoom;   amount = V("DoomPower"); }
                    // POWDERED_DEMISE: DemisePower deals Amount unblockable dmg to the enemy each turn
                    // end — mechanically the same DoT the sim already applies for Doom, so reuse it.
                    else if (vars.ContainsKey("Demise"))          { kind = PotionKind.EnemyDoom;   amount = V("Demise"); }
                    else if (vars.ContainsKey("HealPercent"))     { kind = PotionKind.HealPercent; amount = V("HealPercent"); }
                    else if (selfBuff && vars.ContainsKey("MaxHp")) { kind = PotionKind.MaxHpGain;  amount = V("MaxHp"); }
                    // Self-defence powers (Self/AnyPlayer-targeted so a same-named enemy debuff
                    // can't be mistaken for a self-buff — same guard as the stat potions).
                    else if (selfBuff && vars.ContainsKey("IntangiblePower")) { kind = PotionKind.SelfIntangible; amount = V("IntangiblePower"); }
                    else if (selfBuff && vars.ContainsKey("BufferPower"))     { kind = PotionKind.SelfBuffer;     amount = V("BufferPower"); }
                    else if (selfBuff && vars.ContainsKey("PlatingPower"))    { kind = PotionKind.SelfPlating;    amount = V("PlatingPower"); }
                    else if (selfBuff && vars.ContainsKey("ThornsPower"))     { kind = PotionKind.SelfThorns;     amount = V("ThornsPower"); }
                    else if (selfBuff && vars.ContainsKey("RegenPower"))      { kind = PotionKind.SelfRegen;      amount = V("RegenPower"); }
                    // SHACKLING_POTION reuses PowerVar<StrengthPower> (key "StrengthPower") but applies
                    // it to ALL enemies as a strength-DOWN debuff; BEETLE_JUICE shrinks via Repeat var.
                    else if (id == "SHACKLING_POTION")           { kind = PotionKind.EnemyStrengthDown; amount = V("StrengthPower"); }
                    else if (id == "BEETLE_JUICE")               { kind = PotionKind.EnemyStrengthDown; amount = V("Repeat"); }
                    // Card draw / generation — Id-gated because the "Cards" var is shared across
                    // draw AND generation potions, and several have hand-altering side effects.
                    // Draw N (cost-randomise on SNECKO and the ClarityPower buff are conservatively
                    // omitted — we model only the draw, never overstating).
                    else if (id == "SWIFT_POTION" || id == "BOTTLED_POTENTIAL"
                          || id == "SNECKO_OIL" || id == "CLARITY")             { kind = PotionKind.Draw; amount = vars.ContainsKey("Cards") ? V("Cards") : 1; }
                    else if (id == "LIQUID_MEMORIES" || id == "DROPLET_OF_PRECOGNITION") { kind = PotionKind.Draw; amount = 1; }
                    else if (id == "GLOWWATER_POTION")           { kind = PotionKind.DrawExhaustHand; amount = vars.ContainsKey("Cards") ? V("Cards") : 10; }
                    // Card generation (inject new cards into hand). Choose-1-of-3 potions → 1 card.
                    else if (id == "ATTACK_POTION" || id == "SKILL_POTION" || id == "POWER_POTION"
                          || id == "COLORLESS_POTION")           { kind = PotionKind.GenerateCards; amount = 1; }
                    else if (id == "COSMIC_CONCOCTION")          { kind = PotionKind.GenerateCards; amount = vars.ContainsKey("Cards") ? V("Cards") : 3; }
                    else if (id == "CUNNING_POTION" || id == "POT_OF_GHOULS") { kind = PotionKind.GenerateShivs; amount = vars.ContainsKey("Cards") ? V("Cards") : 2; }
                    // Character resources (Self-targeted).
                    else if (selfBuff && vars.ContainsKey("Stars")) { kind = PotionKind.GainStars; amount = V("Stars"); }
                    else if (id == "POTION_OF_CAPACITY")         { kind = PotionKind.OrbCapacity; amount = vars.ContainsKey("Repeat") ? V("Repeat") : 2; }
                    else if (id == "ESSENCE_OF_DARKNESS")        { kind = PotionKind.ChannelDark; amount = 0; }
                    else if (id == "DUPLICATOR")                 { kind = PotionKind.NextCardDouble; amount = 2; }
                    // BLESSING_OF_THE_FORGE upgrades every hand card; SOLDIERS_STEW only Strikes.
                    // Amount carries the scope: 1 = all cards, 2 = Strikes-only (read in ApplyPotionUse).
                    else if (id == "BLESSING_OF_THE_FORGE")      { kind = PotionKind.UpgradeHand; amount = 1; }
                    else if (id == "SOLDIERS_STEW")              { kind = PotionKind.UpgradeHand; amount = 2; }

                    list.Add(new SimPotion { Id = id, Kind = kind, Amount = amount, Target = target });
                }
                catch { /* one bad potion shouldn't drop the rest */ }
            }
        }
        catch (System.Exception ex) { LogReflectionFailureOnce("potions-dump", ex); }
        return list;
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
