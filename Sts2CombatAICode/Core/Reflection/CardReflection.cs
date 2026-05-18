using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Reflection;

/// <summary>
/// Card-side accessors used by the planner. v0.1 only uses the public surface that's
/// guaranteed by CardModel base class — Type, TargetType, EnergyCost.GetAmountToSpend.
///
/// Per-card effect values (damage, block, status amounts) are stored in subclass `OnPlay`
/// implementations and aren't read here in v0.1. v0.2 will introduce a DynamicVars-based
/// catalog for known cards.
/// </summary>
internal static class CardReflection
{
    public static int GetCost(CardModel card)
    {
        try
        {
            return card.EnergyCost.GetAmountToSpend();
        }
        catch
        {
            return 0;
        }
    }

    public static CardType GetType(CardModel card) => card.Type;

    public static TargetType GetTargetType(CardModel card) => card.TargetType;

    public static bool IsAttack(CardModel card) => card.Type == CardType.Attack;
    public static bool IsSkill(CardModel card) => card.Type == CardType.Skill;
    public static bool IsPower(CardModel card) => card.Type == CardType.Power;
    public static bool IsCurseOrStatus(CardModel card) =>
        card.Type == CardType.Curse || card.Type == CardType.Status;

    public static bool TargetsSingleEnemy(CardModel card) =>
        card.TargetType == TargetType.AnyEnemy;
    public static bool TargetsAllEnemies(CardModel card) =>
        card.TargetType == TargetType.AllEnemies;
    public static bool TargetsSelf(CardModel card) =>
        card.TargetType == TargetType.Self;

    public static string GetIdEntry(CardModel card) => card.Id.Entry;

    /// <summary>
    /// Extracts numeric effects from a card by iterating CanonicalVars and classifying each
    /// DynamicVar by type. Multi-hit (RepeatVar), damage (DamageVar / CalculatedDamageVar /
    /// ExtraDamageVar / OstyDamageVar), block (BlockVar / CalculatedBlockVar), and per-power
    /// applications (PowerVar&lt;T&gt; → Name = power class name, BaseValue = amount).
    ///
    /// Krafs.Publicizer exposes the protected CanonicalVars enumerable on CardModel.
    /// Falls back to <see cref="CardEffectSummary.Empty"/> on any reflection failure.
    /// </summary>
    // CanonicalVars is protected on CardModel and Krafs.Publicizer skips virtual members
    // (csproj's IncludeVirtualMembers="false"). Cache the PropertyInfo for reflection access.
    private static readonly PropertyInfo? _canonicalVarsProp =
        AccessTools.Property(typeof(CardModel), "CanonicalVars");

    // v0.2.12 — runtime DynamicVars (upgraded values, modifiers applied) is preferred over
    // CanonicalVars (base template). Per Sts2CardAdvisor convention: feedback_canonicalvars_vs_dynamicvars.
    private static readonly PropertyInfo? _dynamicVarsProp =
        AccessTools.Property(typeof(CardModel), "DynamicVars");
    private static readonly FieldInfo? _dynamicVarsField =
        AccessTools.Field(typeof(CardModel), "_dynamicVars");
    // UpdateDynamicVarPreview triggers PreviewValue calculation (multiplier-aware,
    // catches CalculatedDamageVar's base + extra × multiplier value).
    private static readonly MethodInfo? _updatePreview =
        AccessTools.Method(typeof(CardModel), "UpdateDynamicVarPreview");
    private static readonly PropertyInfo? _dynamicVarSetValuesProp =
        System.Type.GetType("MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet, sts2")
            ?.GetProperty("Values");

    // Enchantment exposes its own DynamicVarSet (extra effects layered on top of the card's
    // base vars). Reading it lets us account for "+N damage" / "draw +1" style enchants.
    private static readonly PropertyInfo? _enchantmentProp =
        AccessTools.Property(typeof(CardModel), "Enchantment");
    private static readonly System.Type? _enchantType =
        System.Type.GetType("MegaCrit.Sts2.Core.Models.Cards.EnchantmentModel, sts2")
        ?? System.Type.GetType("MegaCrit.Sts2.Core.Entities.Cards.EnchantmentModel, sts2");
    private static readonly PropertyInfo? _enchantDynamicVarsProp =
        _enchantType != null ? AccessTools.Property(_enchantType, "DynamicVars") : null;
    private static readonly PropertyInfo? _enchantIdProp =
        _enchantType != null ? AccessTools.Property(_enchantType, "Id") : null;
    private static readonly PropertyInfo? _enchantAmountProp =
        _enchantType != null ? AccessTools.Property(_enchantType, "Amount") : null;

    public static bool IsEnchanted(CardModel card) =>
        _enchantmentProp?.GetValue(card) != null;

    public static string? GetEnchantmentId(CardModel card)
    {
        var ench = _enchantmentProp?.GetValue(card);
        if (ench == null) return null;
        var idObj = _enchantIdProp?.GetValue(ench);
        return idObj?.ToString();
    }

    public static CardEffectSummary GetEffectSummary(CardModel card)
    {
        try
        {
            int damage = 0, block = 0, hits = 1, energyGain = 0, drawCount = 0;
            int strengthDown = 0, heal = 0, maxHp = 0, hpLoss = 0;
            bool hasCalcDamage = false, hasCalcBlock = false;
            Dictionary<string, int>? powerApps = null;

            // Prefer runtime DynamicVars (upgraded + modifier-aware via PreviewValue).
            // Fallback to CanonicalVars if DynamicVars unavailable.
            object? dynVarsObj = _dynamicVarsProp?.GetValue(card) ?? _dynamicVarsField?.GetValue(card);

            IEnumerable? vars = null;
            if (dynVarsObj != null)
            {
                // Refresh PreviewValue so CalculatedDamageVar reflects (base + extra × multiplier).
                try
                {
                    _updatePreview?.Invoke(card, new object?[] {
                        MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal,
                        /*target=*/ null,
                        dynVarsObj
                    });
                }
                catch { /* preview optional — fall back to BaseValue */ }

                vars = _dynamicVarSetValuesProp?.GetValue(dynVarsObj) as IEnumerable;
            }
            vars ??= _canonicalVarsProp?.GetValue(card) as IEnumerable;
            if (vars == null) return CardEffectSummary.Empty;

            foreach (var obj in vars)
            {
                if (obj is not DynamicVar v) continue;
                // PreviewValue is the multiplier-aware "calculated" value (Strength buff,
                // Vulnerable, conditional extras applied). Fall back to BaseValue when 0.
                decimal effective = v.PreviewValue > 0m ? v.PreviewValue : v.BaseValue;
                int amount = (int)effective;
                var typeName = v.GetType().Name;

                // CalculatedDamageVar = base + extra × multiplier (final value).
                // Once we have it, ignore the component vars (Damage / ExtraDamage / CalculationBase).
                if (typeName.StartsWith("CalculatedDamageVar"))
                {
                    damage = amount;
                    hasCalcDamage = true;
                    continue;
                }
                if (typeName.StartsWith("CalculatedBlockVar"))
                {
                    block = amount;
                    hasCalcBlock = true;
                    continue;
                }
                if (v is DamageVar) { if (!hasCalcDamage) damage += amount; continue; }
                if (v is BlockVar) { if (!hasCalcBlock) block += amount; continue; }
                if (v is RepeatVar) { if (amount > 0) hits = amount; continue; }
                // CalculatedVar with Name "CalculatedHits" (Barrage etc.) — runtime hit count.
                if (typeName == "CalculatedVar" && v.Name == "CalculatedHits")
                {
                    if (amount > 0) hits = amount;
                    continue;
                }
                // v0.6.9 — CalculatedFocus (SYNCHRONIZE: orb-variety × 2 → TempFocus).
                // The card applies TemporaryFocusPower with the calculated amount,
                // not as a PowerVar<T>. Surface it as TemporaryFocusPower so the
                // FocusPower HandSynergy/PowerCatalog paths fire.
                if (typeName == "CalculatedVar" && v.Name == "CalculatedFocus")
                {
                    if (amount > 0)
                    {
                        powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        if (powerApps.TryGetValue("TemporaryFocusPower", out var ex))
                            powerApps["TemporaryFocusPower"] = ex + amount;
                        else
                            powerApps["TemporaryFocusPower"] = amount;
                    }
                    continue;
                }
                if (v is EnergyVar) { energyGain += amount; continue; }
                if (v is CardsVar) { drawCount += amount; continue; }

                // Standalone OstyDamage (Necrobinder summon attack) treated like Damage.
                if (typeName.StartsWith("OstyDamageVar"))
                {
                    if (!hasCalcDamage) damage += amount;
                    continue;
                }
                // Components of CalculatedDamage / CalculatedBlock — skip when the
                // final var already covered them.
                if (typeName.StartsWith("ExtraDamageVar")
                    || typeName.StartsWith("CalculationBaseVar")
                    || typeName.StartsWith("CalculationExtraVar"))
                {
                    if (!hasCalcDamage && !hasCalcBlock)
                        damage += amount; // conservative — standalone case
                    continue;
                }

                // PowerVar<T> — generic class, Name carries the power's class name.
                if (typeName.StartsWith("PowerVar"))
                {
                    powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (powerApps.TryGetValue(v.Name, out var existing))
                        powerApps[v.Name] = existing + amount;
                    else
                        powerApps[v.Name] = amount;
                    continue;
                }

                // v0.6.7 — plain DynamicVar by name: StrengthLoss (DARK_SHACKLES /
                // PIERCING_WAIL / ENFEEBLING_TOUCH / etc.) and Heal (NOT_YET / SPUR).
                // Neither uses PowerVar so they'd otherwise be invisible to scoring.
                if (typeName == "DynamicVar")
                {
                    if (v.Name == "StrengthLoss" || v.Name == "EnemyStrengthLoss") strengthDown += amount;
                    else if (v.Name == "Heal") heal += amount;
                    else if (v.Name == "MaxHp") maxHp += amount;     // v0.6.9 — BRIGHTEST_FLAME, FEED
                    else if (v.Name == "HpLoss") hpLoss += amount;   // v0.7.8 — BLOODLETTING, OFFERING, HEMOKINESIS
                    // v0.6.8 — RAGE applies RagePower with stack = DynamicVar("Power", N).
                    // Not a PowerVar<T> in the catalog (Rage.cs uses
                    // `PowerCmd.Apply<RagePower>(creature, DynamicVars["Power"].BaseValue, ...)`)
                    // so we promote it to PowerApps here so HandSynergy / PowerCatalog
                    // pipelines see RagePower:N. Card-id gated to avoid colliding
                    // with other generic "Power" vars (Power-type cards' own stacks
                    // are already covered by PowerCatalog id-derived lookup).
                    else if (v.Name == "Power" && card.Id.Entry == "CARD.RAGE")
                    {
                        powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        if (powerApps.TryGetValue("RagePower", out var ex))
                            powerApps["RagePower"] = ex + amount;
                        else
                            powerApps["RagePower"] = amount;
                    }
                    continue;
                }
            }

            // Layer enchantment effects on top. The card's DynamicVars already include the
            // numeric upgrades the enchant applies to base vars (PreviewValue is enchant-aware),
            // so we only pull *additive* effects from the enchant — PowerVar applications
            // (e.g. "on play: gain 2 Dexterity"), bonus EnergyVar/CardsVar, bonus damage hits.
            var enchObj = _enchantmentProp?.GetValue(card);
            if (enchObj != null && _enchantDynamicVarsProp != null)
            {
                var enchDvs = _enchantDynamicVarsProp.GetValue(enchObj);
                var enchVars = enchDvs != null
                    ? _dynamicVarSetValuesProp?.GetValue(enchDvs) as IEnumerable
                    : null;
                if (enchVars != null)
                {
                    foreach (var obj in enchVars)
                    {
                        if (obj is not DynamicVar v) continue;
                        decimal eff = v.PreviewValue > 0m ? v.PreviewValue : v.BaseValue;
                        int amount = (int)eff;
                        var tn = v.GetType().Name;
                        if (amount == 0) continue;

                        if (tn.StartsWith("PowerVar"))
                        {
                            powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            if (powerApps.TryGetValue(v.Name, out var ex))
                                powerApps[v.Name] = ex + amount;
                            else
                                powerApps[v.Name] = amount;
                        }
                        else if (v is EnergyVar) energyGain += amount;
                        else if (v is CardsVar) drawCount += amount;
                        // Skip DamageVar/BlockVar here — already folded into card PreviewValue.
                    }
                }

                // Hardcoded-Amount enchants (Sown/Swift/Adroit): these don't surface their
                // effect as a DynamicVar — they read EnchantmentModel.Amount directly inside
                // their OnPlay. We pull Id + Amount and add deltas explicitly.
                var idObj = _enchantIdProp?.GetValue(enchObj);
                var enchId = idObj?.ToString();
                if (!string.IsNullOrEmpty(enchId))
                {
                    decimal rawAmount = _enchantAmountProp != null
                        ? System.Convert.ToDecimal(_enchantAmountProp.GetValue(enchObj) ?? 0)
                        : 0m;
                    int amt = (int)rawAmount;
                    EnchantmentBonusCatalog.ApplyEffect(enchId, amt,
                        ref damage, ref block, ref energyGain, ref drawCount);
                }
            }

            return new CardEffectSummary
            {
                Damage = damage,
                Hits = hits,
                Block = block,
                EnergyGain = energyGain,
                DrawCount = drawCount,
                PowerApps = (IReadOnlyDictionary<string, int>?)powerApps
                            ?? CardEffectSummary.Empty.PowerApps,
                StrengthDownAmount = strengthDown,
                HealAmount = heal,
                MaxHpAmount = maxHp,
                HpLossAmount = hpLoss,
            };
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"effect summary failed for {card?.Id.Entry}: {ex.Message}");
            return CardEffectSummary.Empty;
        }
    }

    /// <summary>
    /// Optional warning sink. MainFile.Initialize wires this to the game logger.
    /// Test builds leave it null → silent (no game-runtime dependency).
    /// </summary>
    public static System.Action<string>? LogWarn { get; set; }
}
