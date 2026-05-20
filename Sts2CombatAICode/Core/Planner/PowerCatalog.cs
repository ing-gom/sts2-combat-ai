using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Per-stack value of each known STS2 power, split by application target.
/// SelfBuff = power applied to the player (Power cards, Self-targeted skills).
/// EnemyDebuff = power applied to enemies (Attack cards, AnyEnemy-targeted skills).
///
/// Same power name can have different value depending on target:
///   StrengthPower on self → +600 (every attack stronger).
///   StrengthPower on enemy → ~ -300 (they hit harder — undesirable; rarely happens).
///
/// Powers absent from the explicit table fall back to <see cref="HeuristicFallback"/>
/// which uses name patterns ("Temporary*", "*Form", "*NextTurn", "No*", "Free*").
///
/// Tier guide (per-stack value):
///   1200+  Game-changing tempo (EnergyNextTurn, EchoForm, Barricade, Intangible)
///    600+  Strong scaling (Strength, Focus, MachineLearning, Poison)
///    300+  Solid (Dexterity, Vulnerable, Weak, Galvanic)
///    150+  Niche / temporary (Vigor, Frail, TemporaryStrength)
///    -     Negative (No*, Confused, WasteAway)
/// </summary>
internal static class PowerCatalog
{
    // v0.10 — DefaultValue + dicts converted to mutable to support JSON-load.
    // Public read-only views preserve existing external code references.
    public static int DefaultValue { get; private set; } = 200;

    public static IReadOnlyDictionary<string, int> SelfBuff => _selfBuff;
    public static IReadOnlyDictionary<string, int> EnemyDebuff => _enemyDebuff;

    private static readonly Dictionary<string, int> _selfBuff = new Dictionary<string, int>
    {
        // ─── Tier S+ (Tempo / game-changing) ────────────────────────────────
        { "EnergyNextTurnPower",     1500 },  // +N energy next turn
        { "EchoFormPower",           1500 },  // first N cards copy (Watcher)
        { "BarricadePower",          1200 },  // block carryover (Ironclad)
        { "DemonFormPower",          1200 },  // +N Strength every turn
        { "IntangiblePower",         1100 },  // all damage capped to 1
        { "WraithFormPower",         1100 },  // Intangible scaling
        { "DrawCardsNextTurnPower",   900 },
        { "MachineLearningPower",     900 },  // free draw / turn
        { "FreeAttackPower",         1000 },
        { "FreeSkillPower",          1000 },
        { "FreePowerPower",          1000 },

        // ─── Tier S (Strong scaling buffs) ──────────────────────────────────
        { "FocusPower",               800 },  // orb scaling (Defect)
        { "DanseMacabrePower",        800 },
        { "BufferPower",              800 },  // next damage / HP loss negated
        { "ReaperFormPower",          800 },
        { "JuggernautPower",          700 },
        { "AfterimagePower",          700 },
        { "FeelNoPainPower",          600 },
        { "StrengthPower",            600 },
        { "GalvanicPower",            600 },

        // ─── Tier A (Solid defense / utility) ───────────────────────────────
        { "ThornsPower",              500 },
        { "FlameBarrierPower",        500 },
        { "RitualPower",              500 },
        { "PlatedArmorPower",         500 },
        { "ArtifactPower",            500 },
        { "BeaconOfHopePower",        500 },
        { "MayhemPower",              500 },
        { "VitalSparkPower",          500 },

        // ─── Tier A (Solid scaling — moderate) ──────────────────────────────
        { "DexterityPower",           400 },
        { "AccuracyPower",            400 },
        { "BurstPower",               400 },
        { "CorruptionPower",          400 },
        { "EnragePower",              400 },
        { "FeralPower",               400 },
        { "BlurPower",                400 },

        // ─── Tier B (Self-heal / sustain) ───────────────────────────────────
        { "VigorPower",               350 },
        { "RegenPower",               350 },
        { "HungerPower",              350 },

        // ─── Tier B (Conditional / niche) ───────────────────────────────────
        { "BiasedCognitionPower",     300 },
        { "RagePower",                300 },
        { "ShadowmeldPower",          300 },

        // ─── Tier C (Temporary / 1-turn buffs) ──────────────────────────────
        { "TemporaryStrengthPower",   250 },
        { "TemporaryFocusPower",      300 },
        { "TemporaryDexterityPower",  180 },

        // ─── Tier D (Self-harm — should NEVER play if other options exist) ──
        { "NoDrawPower",             -1000 },
        { "NoBlockPower",            -1000 },
        { "NoEnergyGainPower",       -1500 },
        { "ConfusedPower",            -500 },
        { "HangPower",                -500 },
        { "MindRotPower",             -800 },
        { "WasteAwayPower",           -300 },
        { "SkittishPower",            -200 },
        { "ShrinkPower",              -300 },
        { "EntropyPower",             -300 },

        // ─── Coverage pass v0.103.2 — S/A tier cards previously default ─────
        // Values are first-pass: anchored to similar registered powers + the
        // S+/S/A/B/C/D bands above. Adjust during review.
        //
        // Ironclad
        { "CrueltyPower",             600 },  // S — Vuln-target damage +25%
        { "UnmovablePower",           600 },  // S — first block card / turn doubled
        { "CrimsonMantlePower",       500 },  // A — +8 block / 1 HP per turn
        { "DarkEmbracePower",         500 },  // A — draw on exhaust
        { "InfernoPower",             500 },  // A — AoE 6 on HP loss
        { "TankPower",                250 },  // A — multiplayer tank role
        // Defect
        { "CoolantPower",             400 },  // A — block per orb type
        { "SpinnerPower",             400 },  // A — free Frost / turn
        { "ThunderPower",             500 },  // A — +6 on Lightning evoke
        // Necrobinder
        { "CallOfTheVoidPower",       600 },  // S — per-turn draw with Volatile tag
        { "DemesnePower",             550 },  // S — Volatile, turn-start payoff
        { "LethalityPower",           500 },  // S — Volatile, first attack/turn +50%
        { "NeurosurgePower",          550 },  // S
        { "PagestormPower",           500 },  // S — draw on Volatile draw
        { "SleightOfFleshPower",      600 },  // S — 9 dmg per debuff applied
        { "CountdownPower",           500 },  // A — +6 Doom per turn
        { "DevourLifePower",          400 },  // A — summon per Soul use
        { "HauntPower",               500 },  // A — 6 HP loss per Soul use
        { "ShroudPower",              350 },  // A — +2 block per Doom apply
        { "SpiritOfAshPower",         450 },  // A — +4 block per Volatile play
        { "ForbiddenGrimoirePower",   250 },  // A — out-of-combat deck thin
        // Regent
        { "ChildOfTheStarsPower",     500 },  // S — block per Star consumed
        { "TheSealedThronePower",     700 },  // S — Star on every card play
        { "VoidFormPower",            700 },  // S — Volatile, 2 free plays/turn (turn-ending)
        { "HammerTimePower",          400 },  // A — party Forge
        // Shared
        { "FastenPower",              500 },  // S — Skill block +5
        { "AutomationPower",          500 },  // A — energy per 10 cards drawn
        // Silent
        { "SneakyPower",              300 },  // S — multiplayer block on ally attack
        { "ToolsOfTheTradePower",     500 },  // S — draw 1 discard 1 per turn
        { "TrackingPower",            600 },  // S — Weak amplifier (2x dmg on Weak)
        { "WellLaidPlansPower",       400 },  // S — retain 1 per turn
        { "InfiniteBladesPower",      500 },  // A — free Shiv per turn

        // ─── Coverage pass v0.103.2 — B/C tier cards previously default ─────
        // Shared mechanic — also covers STONE_ARMOR (B) and ETERNAL_ARMOR (C)
        // via vars match. PlatedArmorPower (existing) and PlatingPower may be
        // the same class with a name discrepancy; keeping both is harmless.
        { "PlatingPower",             400 },  // Plating block carryover (decays on hit)
        // v0.9 — CalamityPower (player buff): Attack play → N random Attack
        // cards added to hand. Setup tier — value scales with deck quality
        // but generally adds attack volume. Baseline similar to NIGHTMARE.
        { "CalamityPower",            450 },

        // ─── B tier ─────────────────────────────────────────────────────────
        // Defect
        { "CapacitorPower",           350 },  // +2 orb slots (scales with Focus)
        { "CreativeAiPower",          450 },  // random Power /turn to hand
        { "IterationPower",           350 },  // 2 draw on first Status draw /turn
        { "SmokestackPower",          400 },  // AoE 5 on Status generated
        { "StormPower",               450 },  // Lightning channel /Power play
        { "SubroutinePower",          500 },  // +1 energy /Power play
        { "TrashToTreasurePower",     400 },  // random orb /Status generated
        // Ironclad
        { "AggressionPower",          400 },  // discard-pile attack recall +upgrade
        { "PyrePower",                600 },  // permanent +1 energy /turn
        { "StampedePower",            350 },  // turn-end random attack auto-play
        { "ViciousPower",             400 },  // draw on Vuln applied
        // Necrobinder
        { "SentryModePower",          400 },  // per-turn Skim to hand
        // Regent
        { "BlackHolePower",           450 },  // AoE 3 on Star consume/gain
        { "GenesisPower",             500 },  // Star /turn
        { "OrbitPower",               400 },  // Star on every 4 energy spent
        { "PaleBlueDotPower",         350 },  // bonus draw on 5+ card turns
        { "RoyaltiesPower",           150 },  // out-of-combat gold reward
        { "TyrannyPower",             300 },  // turn-start draw +exhaust
        // Shared
        { "HelloWorldPower",          400 },  // Common card /turn to hand
        { "PanachePower",             400 },  // AoE 10 every 5 cards played
        { "PrepTimePower",            450 },  // Vigor 4 /turn
        // Silent
        { "AccelerantPower",          500 },  // +1 Poison tick /apply
        { "PhantomBladesPower",       500 },  // Shivs gain Retain +9 dmg first /turn

        // ─── C tier ─────────────────────────────────────────────────────────
        // Defect
        { "HailstormPower",           350 },  // turn-end AoE 6 if Frost held
        // Ironclad
        { "DrumOfBattlePower",        350 },  // 2 draw +exhaust top of draw /turn
        // Necrobinder
        { "CalcifyPower",             400 },  // Skeleton attack damage +4
        { "NecroMasteryPower",        500 },  // summon 5 +Skeleton HP-loss echo
        // Regent
        { "ArsenalPower",             400 },  // +1 Strength /card generated
        { "FurnacePower",             400 },  // Forge 4 /turn
        { "ParryPower",               350 },  // +10 block /Lord's Blade play
        { "PillarOfCreationPower",    300 },  // +3 block /card generated
        { "SeekingEdgePower",         350 },  // Forge 7 + Lord's Blade AoE toggle
        { "SpectrumShiftPower",       250 },  // Colorless random /turn
        { "SwordSagePower",           400 },  // Lord's Blade +1 hit
        // Shared
        { "RollingBoulderPower",      350 },  // turn-start AoE 5, +5 each turn
        { "StratagemPower",           250 },  // shuffle-trigger hand-grab
        // Silent
        { "EnvenomPower",             500 },  // +1 Poison on unblocked attack
        { "FanOfKnivesPower",         400 },  // Shivs target all (permanent toggle)
        { "MasterPlannerPower",       300 },  // Skill cards gain Sly

        // ─── v0.10 Coverage pass — counter-type player buffs ────────────────
        // Player-side delayed/scaling buffs missing from prior cataloging.
        // Each verified by decompile: Apply<X> sites + the power class's
        // trigger handler (AfterTurnEnd / BeforeHandDraw / AfterEnergyReset
        // / AfterBlockCleared) determine valuation tier.
        //
        // BlockNextTurnPower — DodgeAndRoll, ChargeBattery (block twin),
        // Equilibrium (turn-end retain), ResolveBranch. AfterBlockCleared →
        // gain N block then remove. Conditional defense ≈ half of immediate
        // block (only matters if block actually clears).
        { "BlockNextTurnPower",       250 },
        // StarNextTurnPower — Convergence, GuidingStar. AfterEnergyReset →
        // gain N stars. Regent star resource is scarcer-per-card-cost than
        // energy; 1 star ≈ 1 cost worth of star-cost card unlock.
        { "StarNextTurnPower",        400 },
        // SummonNextTurnPower — Invoke. AfterPlayerTurnStart → summon N Osty.
        // Each summon ≈ 600 (skeleton intent damage + soak). Diminishing
        // when ally cap is reached but planner can't easily tell yet.
        { "SummonNextTurnPower",      500 },
        // LightningRodPower — KeystoneCard / Resonance. AfterEnergyReset →
        // channel Lightning, decrement. Per stack ≈ free Lightning channel
        // (similar to SpinnerPower=400 Frost).
        { "LightningRodPower",        450 },
        // DoubleDamagePower — DoubleDamage card / Mayhem. Next attack ×2.
        // SimCard.EffectiveDmgPerEnergy already applies ×2 when in PlayerPowers
        // dict, but the POWER CARD itself (the one applying it) needs scoring.
        { "DoubleDamagePower",        500 },
        // DuplicationPower — Duplication card. Next card ×2. Already handled
        // in SimCard for damage/block; this scores the applying-card itself.
        { "DuplicationPower",         600 },
        // ReboundPower — RefineBlade, Mayhem. First skill / card goes back
        // to top of draw instead of discard → effectively a free replay.
        { "ReboundPower",             500 },
        // ForegoneConclusionPower — ForegoneConclusion. BeforeHandDraw →
        // choose N cards from draw to hand. Strong setup; comparable to
        // PrepTime=450 / WellLaidPlans=400.
        { "ForegoneConclusionPower",  400 },
        // RetainHandPower — Convergence, Equilibrium, Scavenge. Retain N
        // cards across turn ends. Niche but useful for keystone cards.
        { "RetainHandPower",          350 },
        // ToricToughnessPower — ToricToughness card. AfterBlockCleared →
        // gain CanonicalBlock, decrement. Stack-based reactive block.
        { "ToricToughnessPower",      450 },
        // VeilpiercerPower — Veilpiercer card. Ethereal cards cost 0.
        // Niche but combos hard with ethereal-heavy decks.
        { "VeilpiercerPower",         350 },

        // ─── D tier ─────────────────────────────────────────────────────────
        // Defect
        { "ConsumingShadowPower",     300 },  // Dark x2 channel +leftmost evoke /turn
        { "LoopPower",                350 },  // rightmost orb passive 2x trigger /turn
        // Ironclad
        { "HellraiserPower",          300 },  // Strike-named auto-play on draw
        { "JugglingPower",            300 },  // 3rd attack /turn copied to hand
        // Regent
        { "MonarchsGazePower",        350 },  // per-attack enemy Strength -1
        // Shared
        // CalamityPower moved to coverage-pass section above with value 450.
        { "NostalgiaPower",           250 },  // first attack/skill /turn → top of draw
        // Silent
        { "OutbreakPower",            400 },  // AoE 11 every 3 Poison applied
        { "SerpentFormPower",         350 },  // PLAY_TRIGGER: 4 dmg /card played
        { "SpeedsterPower",           350 },  // AoE 2 /card drawn
    };

    private static readonly Dictionary<string, int> _enemyDebuff = new Dictionary<string, int>
    {
        // ─── Tier S (DoT — long-fight value) ────────────────────────────────
        { "PoisonPower",              700 },
        { "ConstrictPower",           500 },
        { "RupturePower",             400 },
        { "NoxiousFumesPower",        500 },  // ambient poison apply

        // ─── Tier A (Damage amplifiers) ─────────────────────────────────────
        { "VulnerablePower",          500 },  // next attacks +50%

        // ─── Tier A (Mitigation) ────────────────────────────────────────────
        { "WeakPower",                350 },  // their attack -25%
        { "FrailPower",               250 },  // their block -25%
        { "ShacklingPotionPower",     400 },
        { "DampenPower",              350 },
        { "EnfeeblingTouchPower",     300 },

        // ─── Tier B (Niche debuffs) ─────────────────────────────────────────
        { "ConfusedPower",            150 },  // less impactful on enemies
        { "DarkShacklesPower",        300 },
        { "PiercingWailPower",        300 },
        { "HexPower",                 400 },

        // ─── v0.10 Coverage pass — enemy debuffs applied via OnPlay ────────
        // ConquerorPower — applied to enemy by Conqueror card. Sovereign
        // Blade attacks against the marked enemy deal ×2 damage. Massive
        // for SB-build Regent; modest otherwise. TickDownDuration per turn.
        { "ConquerorPower",           400 },
        // DemisePower — applied to enemy by Demise card. AfterTurnEnd →
        // deal Amount unblockable damage to the enemy. Like a one-shot
        // Poison tick. Per-stack value comparable to Poison (700) but
        // single-tick rather than every-turn → ~ 1/3 value.
        { "DemisePower",              250 },
        // MagicBombPower — applied to enemy. AfterTurnEnd → deal Amount
        // damage to enemy, remove. Per-stack ≈ Amount-equivalent damage.
        { "MagicBombPower",           200 },
    };

    /// <summary>
    /// Heuristic value when a power isn't in the explicit table. Uses name patterns
    /// to bucket unknown / new powers into reasonable defaults.
    /// </summary>
    public static int HeuristicFallback(string powerName, bool isSelf)
    {
        if (string.IsNullOrEmpty(powerName)) return DefaultValue;

        // Temporary buffs: 1 turn only — half value of typical.
        if (powerName.StartsWith("Temporary")) return isSelf ? 150 : 80;

        // No*: usually a restriction (NoDraw / NoBlock / NoEnergyGain).
        if (powerName.StartsWith("No") && powerName.Length > 2 && char.IsUpper(powerName[2]))
            return isSelf ? -800 : 100;

        // *NextTurnPower: delayed effect, discount.
        if (powerName.EndsWith("NextTurnPower")) return isSelf ? 300 : 200;

        // *FormPower: usually big scaling buff (DemonForm, EchoForm, WraithForm, ReaperForm).
        if (powerName.EndsWith("FormPower")) return isSelf ? 800 : 400;

        // Free*: 0-cost mechanic (FreeAttack/Skill/Power).
        if (powerName.StartsWith("Free")) return isSelf ? 600 : 300;

        // *Strength / *Dexterity / *Focus variant — moderate value.
        if (powerName.Contains("Strength")) return isSelf ? 400 : -200;
        if (powerName.Contains("Dexterity")) return isSelf ? 300 : -150;
        if (powerName.Contains("Focus")) return isSelf ? 500 : -200;

        return DefaultValue;
    }

    public static int LookupSelfBuff(string powerName) =>
        _selfBuff.TryGetValue(powerName, out var v) ? v : HeuristicFallback(powerName, true);

    public static int LookupEnemyDebuff(string powerName) =>
        _enemyDebuff.TryGetValue(powerName, out var v) ? v : HeuristicFallback(powerName, false);

    /// <summary>
    /// Diminishing-returns scaling for power stacks.
    ///   1 stack → 100% of base
    ///   2 stacks → 170%
    ///   3 stacks → 240%
    ///   5 stacks → 380%
    ///   ≥6 stacks → 400% (capped)
    ///
    /// Linear stacking would over-value high-stack powers like Strength 6.
    /// Most powers have diminishing real-world impact past 3-4 stacks;
    /// this curve approximates that while still rewarding accumulation.
    /// </summary>
    public static int ApplyStackCurve(int baseValue, int stacks)
    {
        if (stacks <= 0) return 0;
        if (stacks == 1) return baseValue;
        // First stack full, each additional stack worth 70% of base, capped at 4x.
        int extra = (int)(baseValue * 0.7 * (stacks - 1));
        int capValue = baseValue >= 0 ? baseValue * 4 : baseValue;
        int total = baseValue + extra;
        return baseValue >= 0 ? System.Math.Min(total, capValue) : System.Math.Max(total, capValue);
    }

    public static int ValueSelfBuff(string powerName, int stacks) =>
        ApplyStackCurve(LookupSelfBuff(powerName), stacks);

    public static int ValueEnemyDebuff(string powerName, int stacks) =>
        ApplyStackCurve(LookupEnemyDebuff(powerName), stacks);

    // ─── JSON load / save ───────────────────────────────────────────────────
    // v0.10 — Single-file power_catalog.json with three sections:
    //   { "default_value": 200, "self_buff": { "X": 1500, ... },
    //     "enemy_debuff": { "Y": 400, ... } }
    // Load behavior: REPLACES entire dict (so JSON is single source of truth
    // once written). Missing file = keep code defaults. Stack curve and
    // heuristic fallback stay in code; they're algorithm shape, not per-power
    // numeric.

    private sealed class _Catalog
    {
        public int default_value { get; set; } = 200;
        public Dictionary<string, int> self_buff { get; set; } = new();
        public Dictionary<string, int> enemy_debuff { get; set; } = new();
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>Write current code defaults to {path} if missing. Idempotent.</summary>
    public static void WriteDefaultsTo(string path)
    {
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var c = new _Catalog
            {
                default_value = DefaultValue,
                self_buff = new Dictionary<string, int>(_selfBuff),
                enemy_debuff = new Dictionary<string, int>(_enemyDebuff),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(c, _jsonOpts));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Load and REPLACE catalog from {path}. No-op if missing/invalid.</summary>
    public static void LoadFromJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var c = JsonSerializer.Deserialize<_Catalog>(File.ReadAllText(path), _jsonOpts);
            if (c == null) return;
            DefaultValue = c.default_value;
            _selfBuff.Clear();
            foreach (var kv in c.self_buff) _selfBuff[kv.Key] = kv.Value;
            _enemyDebuff.Clear();
            foreach (var kv in c.enemy_debuff) _enemyDebuff[kv.Key] = kv.Value;
        }
        catch { /* malformed → keep defaults */ }
    }
}
