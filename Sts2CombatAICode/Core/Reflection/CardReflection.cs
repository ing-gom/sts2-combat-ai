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

    /// <summary>
    /// True when the card carries the Smog affliction (from SmoggyPower —
    /// LivingFog's AdvancedGasMove). Smogged cards return false from
    /// ShouldPlay and can't be played until SmoggyPower's AfterTurnEnd
    /// clears them. Decompile: sts2.decompiled.cs:318782.
    /// </summary>
    public static bool HasSmogAffliction(CardModel card)
    {
        try
        {
            var aff = _afflictionProp?.GetValue(card);
            if (aff == null) return false;
            return aff.GetType().Name == "Smog";
        }
        catch { return false; }
    }

    /// <summary>
    /// v0.10 — True when the card carries the Galvanized affliction
    /// (applied by GalvanicPower — decompile sts2.decompiled.cs:314903).
    /// Galvanized cards deal Amount damage to the owner on play
    /// (ValueProp.Unpowered, block-absorbed). Used by PlanScorer to
    /// model the HP cost of playing a Power card under a galvanic source.
    /// </summary>
    public static bool HasGalvanizedAffliction(CardModel card)
    {
        try
        {
            var aff = _afflictionProp?.GetValue(card);
            if (aff == null) return false;
            return aff.GetType().Name == "Galvanized";
        }
        catch { return false; }
    }

    // v0.10 — CardModel.HasTurnEndInHandEffect is a virtual bool property
    // overridden by status / hand-decay cards (INFECTION, Burn-style).
    // INFECTION's OnTurnEndInHand deals DynamicVars.Damage to the owner
    // (decompile sts2.decompiled.cs:353749-353765). Used to model the
    // self-damage trail for cards that pile up in hand because they're
    // Unplayable and never get discarded by play.
    private static readonly PropertyInfo? _hasTurnEndInHandEffectProp =
        AccessTools.Property(typeof(CardModel), "HasTurnEndInHandEffect");

    /// <summary>
    /// v0.10 — True when the card runs OnTurnEndInHand each turn while sitting
    /// in hand (INFECTION = 3 self-damage / turn). Combined with the card's
    /// Damage var (already captured by GetEffectSummary), tells the planner
    /// how much HP it'll bleed per turn the card stays unplayed in hand.
    /// Reflection failure returns false (conservative — no penalty modeled).
    /// </summary>
    public static bool HasTurnEndInHandEffect(CardModel card)
    {
        if (_hasTurnEndInHandEffectProp == null) return false;
        try
        {
            var v = _hasTurnEndInHandEffectProp.GetValue(card);
            return v is bool b && b;
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
    /// <summary>
    /// v0.10 — Skill cards whose EnergyVar in CanonicalVars is consumed by
    /// <c>PowerCmd.Apply&lt;EnergyNextTurnPower&gt;</c> in OnPlay, not by an
    /// immediate energy gain. Verified by decompile of v0.103.2
    /// (research/sts2_decomp/sts2.decompiled.cs `grep "Apply&lt;EnergyNextTurnPower&gt;"`).
    /// When in this set, <see cref="GetEffectSummary"/> routes the EnergyVar
    /// amount into <c>PowerApps["EnergyNextTurnPower"]</c> so PowerCatalog
    /// scores it as a next-turn buff (~1500 per stack) instead of
    /// <c>EnergyGain</c> (this-turn-unlock heuristic).
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> _energyToNextTurnIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHARGE_BATTERY", "CONVERGENCE", "DELAY", "HEGEMONY", "INVOKE",
        "OUTMANEUVER", "REFINE_BLADE", "RELAX", "SCAVENGE",
    };

    /// <summary>
    /// v0.10 — Cards that apply a Power via <c>PowerCmd.Apply&lt;XPower&gt;</c>
    /// but drive the amount from a plain <c>DynamicVar("VarName", N)</c>
    /// instead of a typed <c>PowerVar&lt;XPower&gt;</c>. Without this
    /// mapping the var name doesn't match any known case in the
    /// DynamicVar switch — CardEffectSummary's PowerApps stays empty →
    /// PowerCatalog scoring contributes 0 → these cards score as if
    /// their main effect didn't exist (~100 baseBonus only).
    ///
    /// Each entry: card Id.Entry → (varName, powerNames[]) applied by
    /// OnPlay. The var amount becomes the per-stack value in
    /// <c>PowerApps[powerName]</c>. Multi-power cards (Putrefy, Shockwave,
    /// Uppercut) list both powers driven by the same var.
    ///
    /// Verified by decompile (sts2.decompiled.cs grep <c>Apply&lt;XPower&gt;</c>
    /// inside each card's OnPlay block) — 30+ entries. Id.Entry format —
    /// no "CARD." prefix.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, (string varName, string[] powerNames)> _cardPowerVarMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Power-type self-buffs (single power driven by one var)
        ["ACCELERANT"]      = ("Accelerant",    new[] { "AccelerantPower" }),
        ["BLUR"]            = ("Blur",          new[] { "BlurPower" }),
        ["BURST"]           = ("Skills",        new[] { "BurstPower" }),
        ["CHILD_OF_THE_STARS"] = ("BlockForStars", new[] { "ChildOfTheStarsPower" }),
        ["CORROSIVE_WAVE"]  = ("CorrosiveWave", new[] { "CorrosiveWavePower" }),
        ["CORRUPTION"]      = ("Power",         new[] { "CorruptionPower" }),
        ["CREATIVE_AI"]     = ("CreativeAi",    new[] { "CreativeAiPower" }),
        ["ECHO_FORM"]       = ("EchoForm",      new[] { "EchoFormPower" }),
        ["EQUILIBRIUM"]     = ("Equilibrium",   new[] { "RetainHandPower" }),
        ["FASTEN"]          = ("ExtraBlock",    new[] { "FastenPower" }),
        ["FEEL_NO_PAIN"]    = ("Power",         new[] { "FeelNoPainPower" }),
        ["FLAME_BARRIER"]   = ("DamageBack",    new[] { "FlameBarrierPower" }),
        ["GENESIS"]         = ("StarsPerTurn",  new[] { "GenesisPower" }),
        ["LOOP"]            = ("Loop",          new[] { "LoopPower" }),
        ["MONOLOGUE"]       = ("Power",         new[] { "MonologuePower" }),
        ["NOXIOUS_FUMES"]   = ("PoisonPerTurn", new[] { "NoxiousFumesPower" }),
        ["ONE_TWO_PUNCH"]   = ("Attacks",       new[] { "OneTwoPunchPower" }),
        ["PALE_BLUE_DOT"]   = ("CardPlay",      new[] { "PaleBlueDotPower" }),
        ["PANACHE"]         = ("PanacheDamage", new[] { "PanachePower" }),
        ["SHADOWMELD"]      = ("Power",         new[] { "ShadowmeldPower" }),
        ["SPIRIT_OF_ASH"]   = ("BlockOnExhaust", new[] { "SpiritOfAshPower" }),
        ["STAMPEDE"]        = ("Power",         new[] { "StampedePower" }),
        ["TORIC_TOUGHNESS"] = ("Turns",         new[] { "ToricToughnessPower" }),
        ["WELL_LAID_PLANS"] = ("RetainAmount",  new[] { "WellLaidPlansPower" }),
        ["RAGE"]            = ("Power",         new[] { "RagePower" }),

        // Enemy debuffs / mixed effects
        ["COLOSSUS"]        = ("Colossus",      new[] { "ColossusPower" }),
        ["EXPOSE"]          = ("Power",         new[] { "VulnerablePower" }),
        ["MONARCHS_GAZE"]   = ("Power",         new[] { "MonarchsGazePower" }),
        ["NO_ESCAPE"]       = ("DoomThreshold", new[] { "DoomPower" }),
        ["PANIC_BUTTON"]    = ("Turns",         new[] { "NoBlockPower" }),
        ["SHAME"]           = ("Frail",         new[] { "FrailPower" }),

        // Multi-power cards (one var amount drives both Weak + Vulnerable)
        ["PUTREFY"]         = ("Power",         new[] { "WeakPower", "VulnerablePower" }),
        ["SHOCKWAVE"]       = ("Power",         new[] { "WeakPower", "VulnerablePower" }),
        ["UPPERCUT"]        = ("Power",         new[] { "WeakPower", "VulnerablePower" }),
    };

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
    /// 2026-05-28 100%-parity push: cards with hardcoded WithHitCount(N) in
    /// OnPlay. Reflection's RepeatVar / CalculatedHits scan misses these
    /// literals → mod sim defaults to hits=1 and under-credits damage by N-1
    /// hits per play. Source: decompile audit grep of "WithHitCount\([0-9]+\)".
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, int> HardcodedHitCount = new()
    {
        ["TWIN_STRIKE"] = 2,
        ["THRASH"] = 2,
        ["DAGGER_SPRAY"] = 2,
        ["MAUL"] = 2,
        ["REFRACT"] = 2,
        ["RIP_AND_TEAR"] = 2,
        ["UPROAR"] = 2,
    };

    // 2026-05-29 100%-parity push: hardcoded CardPileCmd.Draw(N) override.
    // A few cards issue the draw as a literal inside OnPlay instead of via a
    // CardsVar/DynamicVar, so reflection sees no draw and the mod sim draws 0.
    // Source: decompile audit of "CardPileCmd.Draw(choiceContext, Nm" literal
    // usages across the Cards namespace — only DaggerThrow (Draw 1, then a
    // discard-choice the parity probe's resolver declines) and EscapePlan
    // (Draw 1 unconditionally; the FirstOrDefault only gates the conditional
    // block). DeprecatedCard is a non-playable placeholder and is excluded.
    // Verified against sim_parity_silent DAGGER_THROW 14/24 diverging with
    // {draw_pile +1, hand -1} (real drew, mod didn't). The draw is the
    // unambiguously-correct real behavior in both probe and live play.
    private static readonly System.Collections.Generic.Dictionary<string, int> HardcodedDrawCount = new()
    {
        ["DAGGER_THROW"] = 1,
        ["ESCAPE_PLAN"] = 1,
    };

    // 2026-05-29 — cards whose CardsVar is NOT a draw-into-hand (it's a select /
    // discard / transform count over the draw or hand pile). Routing it to
    // drawCount phantom-draws in the sim. Decompile-verified:
    //   CHARGE         — CardSelectCmd.FromSimpleGrid over the DRAW pile (transform)
    //   HIDDEN_DAGGERS — CardSelectCmd.FromHandForDiscard(Cards) (discard; the Shivs
    //                    var is the real hand addition, routed separately)
    private static readonly System.Collections.Generic.HashSet<string> CardsVarNotDraw =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "CHARGE",
        "HIDDEN_DAGGERS",
        // PHOTON_CUT/GLIMMER draw normally then put N back — handled by
        // AnalyticalSimulator.PutBackToDrawCount (draw, then move N hand→draw),
        // which also models the hand-cap edge (draw caps, put-back still fires).
        // 2026-05-30 — Necrobinder Soul generators: CardsVar = Soul.Create count,
        // added to the DRAW pile (not a hand draw). Handled by CardGenToDrawCount.
        "GRAVE_WARDEN",
        "REAVE",
    };

    /// 2026-05-29 — cards whose RepeatVar drives a NON-damage effect N times
    /// (orb channel etc.) while the attack itself is single-hit. RepeatVar must
    /// NOT be read as the hit count for these (would multiply the one-shot
    /// damage). Decompile-verified:
    ///   ICE_LANCE: Deal 19 once, then Channel&lt;FrostOrb&gt; ×Repeat(3).
    private static readonly System.Collections.Generic.HashSet<string> RepeatDrivesNonDamage =
        new(System.StringComparer.OrdinalIgnoreCase) { "ICE_LANCE" };

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
            int delayedAoeDamage = 0, delayTurns = 0;  // v0.10 — THE_BOMB
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

            // 2026-05-29 — pre-scan for a CalculatedVar that consumes the
            // CalculationBase/CalculationExtra components for a NON-damage
            // effect (Forge amount, hit count). When present, those Calculation*
            // vars feed that calc, NOT the attack damage — so they must be
            // excluded from the damage sum. BEAT_INTO_SHAPE: DamageVar(5) +
            // CalculationBase(5) + CalculationExtra(5) + CalculatedForge → real
            // damage is 5, but the sim summed all three to 15 (enemy_hp -7.7).
            bool calcFeedsNonDamage = false;
            foreach (var obj in vars)
            {
                // 2026-05-29 — a plain CalculatedVar (NOT CalculatedDamageVar/
                // CalculatedBlockVar) with a non-damage name means the card's
                // CalculationBase/CalculationExtra components feed THAT calc
                // (a draw/shiv/energy/channel/focus/forge/hit/etc. count), not
                // the attack damage. Summing them into damage over-deals by the
                // CalculationExtra amount. Full set from a sts2.dll CalculatedVar
                // name scan; all decompile-confirmed non-damage:
                //   SOVEREIGN_BLADE (SeekingEdgeAmount) — enemy_hp -1/hit
                //   COMPILE_DRIVER  (CalculatedCards, orb-count draw) — enemy_hp -1/hit
                if (obj is DynamicVar pv && pv.GetType().Name == "CalculatedVar"
                    && (pv.Name == "CalculatedForge" || pv.Name == "CalculatedHits"
                        || pv.Name == "SeekingEdgeAmount" || pv.Name == "CalculatedCards"
                        || pv.Name == "CalculatedShivs" || pv.Name == "CalculatedEnergy"
                        || pv.Name == "CalculatedChannels" || pv.Name == "CalculatedDoom"
                        || pv.Name == "CalculatedFocus"))
                {
                    calcFeedsNonDamage = true;
                    break;
                }
            }

            foreach (var obj in vars)
            {
                if (obj is not DynamicVar v) continue;
                // PreviewValue is the multiplier-aware "calculated" value (Strength buff,
                // Vulnerable, conditional extras applied). Fall back to BaseValue when 0.
                decimal effective = v.PreviewValue > 0m ? v.PreviewValue : v.BaseValue;
                int amount = (int)effective;
                var typeName = v.GetType().Name;

                // CalculatedDamageVar = base + extra × multiplier (final value).
                // 2026-05-28 S6-3: try BaseValue first — same Strength double-count
                // fix as plain DamageVar. PreviewValue includes Strength buff
                // which then gets re-added by modifier registry.
                if (typeName.StartsWith("CalculatedDamageVar"))
                {
                    // BaseValue for CalculatedDamageVar typically returns the
                    // static base component without runtime modifiers. For
                    // BULLY: CalculationBase=4. Modifier handles Str/Vuln.
                    // For multiplier-driven cards (PERFECTED_STRIKE per-Strike,
                    // BULLY per-Vuln), the multiplier IS lost — those need
                    // per-card special-cases in AnalyticalSimulator. Conservative
                    // fallback to amount if BaseValue is 0 (i.e. var has no
                    // static component, only dynamic).
                    int baseRaw = (int)v.BaseValue;
                    damage = baseRaw > 0 ? baseRaw : amount;
                    hasCalcDamage = true;
                    continue;
                }
                if (typeName.StartsWith("CalculatedBlockVar"))
                {
                    int baseRaw = (int)v.BaseValue;
                    block = baseRaw > 0 ? baseRaw : amount;
                    hasCalcBlock = true;
                    continue;
                }
                // 2026-05-28 100%-parity push: plain DamageVar/BlockVar use
                // BaseValue (raw upgrade-aware value) so modifier registry's
                // Strength/Vuln/Weak application doesn't double-count what
                // PreviewValue already folded in. Parity sample: STRIKE +Str 2
                // = card.Damage 8 (PreviewValue 6+2) → modifier adds +2 again
                // → mod predicts 10, real does 8. enemy_hp_sum -2 pattern.
                // CalculatedDamageVar above keeps PreviewValue since the
                // scaling extras (PerfectedStrike per-Strike etc.) ARE the
                // PreviewValue and modifier registry can't replicate them.
                if (v is DamageVar) { if (!hasCalcDamage) damage += (int)v.BaseValue; continue; }
                // 2026-05-29 — a BlockVar named "BlockNextTurn" is deferred block:
                // OnPlay routes it to BlockNextTurnPower (gained at next turn
                // start), NOT to the current turn's Block. GLITTERSTREAM has
                // BlockVar(11) [now] + BlockVar("BlockNextTurn", 5) [next turn];
                // summing both over-credited current block by 5 (player_block +5
                // on every play). Exclude it from the current-turn block sum.
                if (v is BlockVar && v.Name == "BlockNextTurn") continue;
                if (v is BlockVar) { if (!hasCalcBlock) block += (int)v.BaseValue; continue; }
                // RepeatVar normally = attack hit count (SWORD_BOOMERANG,
                // CELESTIAL_MIGHT, ...). But some cards use RepeatVar to drive a
                // NON-damage effect N times while dealing their damage ONCE:
                //   • ICE_LANCE: Deal 19 once, then Channel<FrostOrb> ×Repeat(3).
                // For these the attack is single-hit; treating Repeat as the hit
                // count over-predicts damage massively (ICE_LANCE sim 19×3=57 vs
                // real 19 → enemy_hp_sum up to -54 in probe). Skip the hit-count
                // assignment for the orb-channel-Repeat family.
                if (v is RepeatVar)
                {
                    if (amount > 0 && !RepeatDrivesNonDamage.Contains(card.Id.Entry))
                        hits = amount;
                    continue;
                }
                // HpLossVar — typed subclass for cards that lose HP on play
                // (BLOOD_WALL, HEMOKINESIS, BRAND, BREAKTHROUGH, BLOODLETTING,
                // OFFERING, ...). The DynamicVar-by-Name branch below catches
                // raw DynamicVars with Name="HpLoss" but typed HpLossVar
                // falls through there (`typeName == "DynamicVar"` excludes
                // subclasses). Without this branch mod sim never applies
                // HP loss for HpLossVar cards → consistent player_hp=+N diff
                // in parity probe (BLOOD_WALL 12/12 with +2).
                if (typeName.StartsWith("HpLossVar")) { hpLoss += amount; continue; }
                // CalculatedVar with Name "CalculatedHits" — runtime hit count
                // derived from the card's scaling closure (BARRAGE, FINISHER,
                // FLAK_CANNON, FLECHETTES, HELIX_DRILL, LUNAR_BLAST,
                // PULL_FROM_BELOW, RADIATE, RATTLE, TEAR_ASUNDER).
                // Accept 0 here — for these cards 0 IS the literal hit count
                // when the scaling trigger (skills/attacks/exhaust/orbs/stars/
                // skeleton-attacks) hasn't fired yet. Defaulting to 1 made
                // premature plays look like base-damage attacks (v0.9.1 fix).
                if (typeName == "CalculatedVar" && v.Name == "CalculatedHits")
                {
                    hits = amount;
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
                    // v0.10 — Some Skill cards (CHARGE_BATTERY, CONVERGENCE,
                    // DELAY, HEGEMONY, INVOKE, OUTMANEUVER, REFINE_BLADE, RELAX,
                    // SCAVENGE) declare EnergyVar but in OnPlay apply
                    // EnergyNextTurnPower instead of immediate energy. Without
                    // this redirect, DELAY's +1 energy was scored as immediate
                    // (EvaluateEnergyGain unlock heuristic), missing the
                    // EnergyNextTurnPower 1500-point self-buff value.
                    if (_energyToNextTurnIds.Contains(card.Id.Entry))
                    {
                        powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        if (powerApps.TryGetValue("EnergyNextTurnPower", out var existing))
                            powerApps["EnergyNextTurnPower"] = existing + amount;
                        else
                            powerApps["EnergyNextTurnPower"] = amount;
                        continue;
                    }
                    // 2026-05-29 — deferred-secondary-var audit (general pattern):
                    // an EnergyVar with a non-default Name is often NOT immediate
                    // energy gain. Route by name (all decompile-verified):
                    //   ExtraCost      — BORROWED_TIME: future cost (BorrowedTimePower)
                    //   EnergyThreshold— IVORY_TILE: a condition/threshold, not gain
                    //   energyPrefix   — TINKER_TIME: a display-only prefix var
                    //   LoseEnergy     — BREAD: OnPlay calls LoseEnergy(amount), so
                    //                    it SUBTRACTS energy (real net = Gain−Lose)
                    // Summing any of these as +amount over-credited PlayerEnergy.
                    // Same deferred-var class as BlockNextTurn.
                    if (v.Name == "ExtraCost"
                        || v.Name == "EnergyThreshold"
                        || v.Name == "energyPrefix") continue;
                    if (v.Name == "LoseEnergy") { energyGain -= amount; continue; }
                    // 2026-05-30 — RIGHT_HAND_HAND's EnergyVar(2) is a THRESHOLD
                    // (AfterCardPlayedLate: if Resources.Energy >= 2, return this
                    // card from discard to hand), NOT an energy gain. The default
                    // var name is "Energy", so gate by card id.
                    if (card.Id.Entry == "RIGHT_HAND_HAND") continue;
                    energyGain += amount;
                    continue;
                }
                // 2026-05-29 — a CardsVar does NOT uniformly mean "draw N into
                // hand". Some cards use it as a SELECT/DISCARD count over the
                // draw or hand pile (decompile-verified), so routing it to
                // drawCount makes the sim phantom-draw (hand +N / draw -N rows
                // that real never produces — confirmed real draws 0 even at
                // hand well under the 10 cap):
                //   CHARGE          — FromSimpleGrid over the DRAW pile (transform-
                //                     select); no hand draw, no pile-count change.
                //   HIDDEN_DAGGERS  — FromHandForDiscard(Cards) (discard, player-
                //                     choice); the Shivs var (handled below) is the
                //                     real hand addition, NOT Cards.
                if (v is CardsVar && CardsVarNotDraw.Contains(card.Id.Entry)) continue;
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
                    // Skip when a non-damage CalculatedVar (Forge/Hits) consumes
                    // these components (BEAT_INTO_SHAPE) — they aren't damage.
                    if (!hasCalcDamage && !hasCalcBlock && !calcFeedsNonDamage)
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
                    // 2026-05-29 — "Shivs" var = number of Shiv cards created in
                    // hand (LEADING_STRIKE, HIDDEN_DAGGERS, ...). Route to
                    // drawCount; AnalyticalSimulator's ShivGenCards set then
                    // creates them in hand instead of drawing from the pile.
                    else if (v.Name == "Shivs") drawCount += amount;
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
                    // v0.10 — THE_BOMB delayed-AOE detonation. Card has plain
                    // DynamicVars "Turns" (countdown) + "BombDamage" (per-enemy
                    // damage when the TheBombPower fires). Without this, the
                    // planner saw Damage=0/Block=0 → scored THE_BOMB as a
                    // ~100-point empty skill instead of a 1000+ AOE finisher.
                    else if (v.Name == "BombDamage") delayedAoeDamage = amount;
                    else if (v.Name == "Turns")      delayTurns = amount;
                    // v0.9 — CalculatedForge: BEAT_INTO_SHAPE et al expose
                    // the Forge amount as "CalculatedForge" (base + per-attack
                    // bonus folded by PreviewValue). The simpler "Forge" name
                    // covers static-amount cards (REFINE_BLADE / SPOILS) but
                    // misses the dynamic-amount cards entirely. Both names
                    // map to the same ForgeGen field so downstream code is
                    // unchanged.
                    else if (v.Name == "CalculatedForge") forgeGen += amount;
                    // v0.10 — Generalized: cards that apply Powers via plain
                    // DynamicVar (not PowerVar<X>) are looked up in
                    // _cardPowerVarMap. Subsumes the prior RAGE-only case;
                    // covers ~30 cards including EchoForm, Corruption, Genesis,
                    // NoxiousFumes, FeelNoPain, Burst, Blur, Accelerant, etc.
                    //
                    // Multi-power cards (Putrefy / Shockwave / Uppercut: Weak +
                    // Vulnerable from same var) add all listed powers with the
                    // same amount. Card id has no "CARD." prefix.
                    else if (_cardPowerVarMap.TryGetValue(card.Id.Entry, out var mapping)
                             && string.Equals(mapping.varName, v.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        powerApps ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pn in mapping.powerNames)
                        {
                            if (powerApps.TryGetValue(pn, out var ex))
                                powerApps[pn] = ex + amount;
                            else
                                powerApps[pn] = amount;
                        }
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

            // 2026-05-28 100%-parity push: hardcoded WithHitCount(N) override.
            // Some cards specify hit count as a literal in OnPlay rather than
            // via RepeatVar/CalculatedHits. Reflection can't see literals, so
            // mod sim defaulted to hits=1 → consistent under-credit damage on
            // parity probe. Found via sim_parity_anger_full TWIN_STRIKE 9/13
            // diverging with diff +3..+10 (real did 2× damage, mod predicted 1×).
            // Source: decompile audit of "WithHitCount(2)" literal usages.
            if (card?.Id.Entry is { } hcEntry
                && HardcodedHitCount.TryGetValue(hcEntry, out int hcHits))
            {
                hits = hcHits;
            }

            // 2026-05-29 — literal CardPileCmd.Draw(N) override (see dict comment).
            // Add to drawCount so AnalyticalSimulator's existing draw-resolution
            // path (pile decrement + reshuffle) handles it uniformly.
            if (card?.Id.Entry is { } drEntry
                && HardcodedDrawCount.TryGetValue(drEntry, out int drDraw))
            {
                drawCount += drDraw;
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
                // v0.10 — Delayed-AOE detonation (THE_BOMB family).
                DelayedAoeDamage = delayedAoeDamage,
                DelayTurns = delayTurns,
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
