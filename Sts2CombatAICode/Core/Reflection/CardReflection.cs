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

    // v0.9 — Affliction reflection. CardModel has an Affliction property
    // (AfflictionModel? — Bound / Devoured / etc.). Used to detect Bound
    // cards (ChainsOfBindingPower) for candidate filtering.
    private static readonly PropertyInfo? _afflictionProp =
        AccessTools.Property(typeof(CardModel), "Affliction");

    /// <summary>
    /// v0.9 — True when the card carries the Bound affliction (from
    /// ChainsOfBindingPower). Only ONE Bound card may be played per turn.
    /// Returns false if reflection fails or no affliction is set.
    /// </summary>
    public static bool HasBoundAffliction(CardModel card)
    {
        try
        {
            var aff = _afflictionProp?.GetValue(card);
            if (aff == null) return false;
            // Match by class name to avoid hard-binding to Bound type.
            return aff.GetType().Name == "Bound";
        }
        catch { return false; }
    }

    // CardModel runtime keywords. Includes both inherent keywords (Strike,
    // Minion, Exhaust on Shiv) and TEMPORARY keywords applied at runtime
    // (HAND_TRICK's "Add Sly to a Skill in hand this turn" lands as a Sly
    // keyword on the targeted card). Property name guess based on STS2
    // convention — falls back gracefully if reflection misses.
    private static readonly PropertyInfo? _keywordsProp =
        AccessTools.Property(typeof(CardModel), "Keywords");

    /// <summary>
    /// True when the card has the Sly keyword at this very moment. Covers:
    ///   • Inherent Sly cards (TACTICIAN / REFLEX / ABRASIVE / ...).
    ///   • Runtime-granted Sly (HAND_TRICK target this turn).
    /// Returns false when the Keywords reflection fails (missing property /
    /// type mismatch). Callers should still fall back to the static CUNNING
    /// axis check for inherent Sly.
    /// </summary>
    public static bool HasSlyKeyword(CardModel card)
    {
        if (_keywordsProp == null) return false;
        try
        {
            var keywords = _keywordsProp.GetValue(card) as IEnumerable;
            if (keywords == null) return false;
            foreach (var kw in keywords)
            {
                // CardKeyword enum value or string — ToString covers both.
                if (kw == null) continue;
                if (kw.ToString() == "Sly") return true;
            }
        }
        catch { /* reflection failure → caller falls back to axis */ }
        return false;
    }

    public static string? GetEnchantmentId(CardModel card)
    {
        var ench = _enchantmentProp?.GetValue(card);
        if (ench == null) return null;
        var idObj = _enchantIdProp?.GetValue(ench);
        return idObj?.ToString();
    }

    /// <summary>
    /// v0.7.78 — Hardcoded fallback for this-turn star gain. STS2's DynamicVar
    /// extraction (`v.Name == "Stars"`) misses some cards (observed: VENERATE
    /// produced 0 in reflection, causing the simulator's PlayerStars
    /// propagation to never unlock FALLING_STAR in depth-N lookahead, so the
    /// planner never values VENERATE→FALLING_STAR chains.
    ///
    /// Use catalog values (EffectSynergy hardcodes confirm authors trusted
    /// these). NEXT-TURN gains (HIDDEN_CACHE, CONVERGENCE) excluded — those
    /// don't unlock current-turn star cards.
    /// </summary>
    // v0.7.81 — Keys are unprefixed Id.Entry values ("VENERATE", not "CARD.VENERATE").
    // v0.7.78 used "CARD." prefix and never matched anything. Verified via v0.7.80
    // diagnostic showing sc.Id = "VENERATE". The EffectSynergy hardcoded handlers
    // (e.g. `card.Id == "CARD.VENERATE"`) suffer the same broken-key bug — separate
    // fix.
    private static readonly System.Collections.Generic.Dictionary<string, int> ThisTurnStarsGain = new()
    {
        ["GLOW"] = 1,
        ["GATHER_LIGHT"] = 1,
        ["RADIATE"] = 1,
        ["VENERATE"] = 2,
        ["SHINING_STRIKE"] = 2,
        ["SOLAR_STRIKE"] = 1,
        ["KNOCKOUT_BLOW"] = 5,
        ["ROYAL_GAMBLE"] = 9,
    };

    /// <summary>
    /// v0.7.81 — Catalog star_cost fallback. SafeStarCost reflection returned
    /// 0 for verified star-cost cards (FALLING_STAR diagnostic). Mirror of
    /// ActionPlanner.StarCostByCardId — keep in sync.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, int> StarCostByCardId = new()
    {
        ["CLOAK_OF_STARS"] = 1,
        ["CRESCENT_SPEAR"] = 1,
        ["FALLING_STAR"] = 2,
        ["GUIDING_STAR"] = 2,
        ["METEOR_SHOWER"] = 2,
        ["PARTICLE_WALL"] = 2,
        ["QUASAR"] = 2,
        ["ALIGNMENT"] = 3,
        ["ASTRAL_PULSE"] = 3,
        ["DYING_STAR"] = 3,
        ["GAMMA_BLAST"] = 3,
        ["REFLECT"] = 3,
        ["RESONANCE"] = 3,
        ["THE_SEALED_THRONE"] = 3,
        ["DEVASTATE"] = 4,
        ["THE_SMITH"] = 4,
        ["COMET"] = 5,
        ["NEUTRON_AEGIS"] = 5,
        ["ROYAL_GAMBLE"] = 5,
        ["DECISIONS_DECISIONS"] = 6,
        ["SEVEN_STARS"] = 7,
    };

    private static int ResolveStarCost(CardModel? card)
    {
        if (card == null) return 0;
        int reflected = SafeStarCost(card);
        if (reflected != 0) return reflected;
        if (card.Id.Entry is { } entry && StarCostByCardId.TryGetValue(entry, out int catalogCost))
            return catalogCost;
        return 0;
    }

    public static CardEffectSummary GetEffectSummary(CardModel card)
    {
        try
        {
            int damage = 0, block = 0, hits = 1, energyGain = 0, drawCount = 0;
            int strengthDown = 0, heal = 0, maxHp = 0, hpLoss = 0;
            int starsGain = 0;  // v0.7.71
            int shivGen = 0, skeletonGen = 0, soulGen = 0, forgeGen = 0;
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
                // v0.9 — CalculatedForge: BEAT_INTO_SHAPE / similar Forge-on-
                // attack cards expose the Forge amount via a CalculatedVar
                // with Name "CalculatedForge". PreviewValue already folds in
                // the "+5 per other same-target attack this turn" multiplier,
                // so reading this is strictly better than the static 5
                // fallback in AnalyticalSimulator. The DynamicVar branch
                // below also catches a same-named plain DynamicVar; the two
                // are mutually exclusive per card (game uses one or the
                // other), so an accidental double-count cannot occur.
                if (typeName == "CalculatedVar" && v.Name == "CalculatedForge")
                {
                    if (amount > 0) forgeGen += amount;
                    continue;
                }
                if (v is EnergyVar)
                {
                    // v0.9 — Power cards' EnergyVar typically represents the
                    // power's PER-TRIGGER amount (AutomationPower: +1 energy
                    // per 10 cards drawn; not on play), NOT immediate energy
                    // gained on play. Crediting it as immediate caused the
                    // simulator to think AUTOMATION (cost 1, Power) leaves
                    // PlayerEnergy unchanged → SB(cost 2) appeared playable
                    // after AUTOMATION when in reality energy is 1 short.
                    // Resulting bug (logs 2026-05-19 21:11): "AUTOMATION → SB"
                    // chain scored 12141 → AUTOMATION beat SB-alone at step 2
                    // → SB then sat at |RX (unplayable) for the rest of the
                    // turn.
                    //
                    // Attacks/Skills with EnergyVar (Storm Of Spears /
                    // Cleaver / Adrenaline-style) still gain immediate energy.
                    if (card.Type == CardType.Power) continue;
                    energyGain += amount;
                    continue;
                }
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
                    else if (v.Name == "Stars") starsGain += amount;  // v0.7.71 — GLOW 1, VENERATE 2, ROYAL_GAMBLE 9
                    // Token-card generation counts. Cards that add specific
                    // tokens to hand expose them by name so the planner can
                    // self-augment SHIV_PRODUCER / SKELETON_PRODUCER /
                    // SOUL_PRODUCER / FORGE_PRODUCER axes at SimCard build
                    // time even when the master catalog forgets to tag them.
                    else if (v.Name == "Shivs")     shivGen += amount;
                    else if (v.Name == "Skeletons") skeletonGen += amount;
                    else if (v.Name == "Souls")     soulGen += amount;
                    else if (v.Name == "Forge")     forgeGen += amount;
                    // v0.9 — CalculatedForge: BEAT_INTO_SHAPE et al expose
                    // the Forge amount as "CalculatedForge" (base + per-attack
                    // bonus folded by PreviewValue). The simpler "Forge" name
                    // covers static-amount cards (REFINE_BLADE / SPOILS) but
                    // misses the dynamic-amount cards entirely. Both names
                    // map to the same ForgeGen field so downstream code is
                    // unchanged.
                    else if (v.Name == "CalculatedForge") forgeGen += amount;
                    // v0.6.8 — RAGE applies RagePower with stack = DynamicVar("Power", N).
                    // Not a PowerVar<T> in the catalog (Rage.cs uses
                    // `PowerCmd.Apply<RagePower>(creature, DynamicVars["Power"].BaseValue, ...)`)
                    // so we promote it to PowerApps here so HandSynergy / PowerCatalog
                    // pipelines see RagePower:N. Card-id gated to avoid colliding
                    // with other generic "Power" vars (Power-type cards' own stacks
                    // are already covered by PowerCatalog id-derived lookup).
                    // v0.7.87 — Id.Entry has no CARD. prefix (verified by v0.7.80 stars diagnostic).
                    else if (v.Name == "Power" && card.Id.Entry == "RAGE")
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

            // v0.7.78 — Star-gain fallback. STS2's DynamicVar "Stars" extraction
            // misses some cards (VENERATE etc.). Without this, the simulator's
            // PlayerStars propagation reads 0 and depth-N lookahead can't unlock
            // FALLING_STAR / star-cost cards via gain chains.
            if (starsGain == 0 && card?.Id.Entry is { } cardIdEntry
                && ThisTurnStarsGain.TryGetValue(cardIdEntry, out int catalogStars))
            {
                starsGain = catalogStars;
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
                // v0.7.71 — Regent star resource
                StarsGain = starsGain,
                // v0.7.81 — SafeStarCost reflection observed returning 0 for
                // star-cost cards (v0.7.80 diagnostic confirmed FALLING_STAR.cost=0).
                // Use ActionPlanner's StarCostByCardId-equivalent table as fallback.
                StarCost = ResolveStarCost(card),
                // Token generation — used by StateSnapshotter to self-augment
                // *_PRODUCER + CARD_GEN axes when the catalog misses them.
                ShivGen = shivGen,
                SkeletonGen = skeletonGen,
                SoulGen = soulGen,
                ForgeGen = forgeGen,
            };
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"effect summary failed for {card?.Id.Entry}: {ex.Message}");
            return CardEffectSummary.Empty;
        }
    }

    /// <summary>
    /// v0.7.71 — Pull star_cost from the live Card via reflection. Wrapped in
    /// try/catch because the field may not exist on every card type.
    /// </summary>
    private static int SafeStarCost(CardModel card)
    {
        try
        {
            // Card.StarCost: per STS2 source, EnergyCost-like field
            var prop = card?.GetType().GetProperty("StarCost",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            if (prop != null)
            {
                var raw = prop.GetValue(card);
                if (raw is int i) return i;
                if (raw != null) return System.Convert.ToInt32(raw);
            }
            // Fallback: field directly on Card
            var field = card?.GetType().GetField("_starCost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var raw = field.GetValue(card);
                if (raw is int i) return i;
            }
        }
        catch (System.Exception ex)
        {
            // v0.8.5 — Surface STS2 field-rename issues. Uses LogWarn sink that
            // MainFile wires up at init (null in test builds → silent).
            LogWarn?.Invoke($"SafeStarCost failed for {card?.Id.Entry}: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// Optional warning sink. MainFile.Initialize wires this to the game logger.
    /// Test builds leave it null → silent (no game-runtime dependency).
    /// </summary>
    public static System.Action<string>? LogWarn { get; set; }
}
