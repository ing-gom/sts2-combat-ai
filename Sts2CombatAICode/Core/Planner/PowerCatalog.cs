using System.Collections.Generic;

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
    public const int DefaultValue = 200;

    public static readonly IReadOnlyDictionary<string, int> SelfBuff = new Dictionary<string, int>
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
    };

    public static readonly IReadOnlyDictionary<string, int> EnemyDebuff = new Dictionary<string, int>
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
        SelfBuff.TryGetValue(powerName, out var v) ? v : HeuristicFallback(powerName, true);

    public static int LookupEnemyDebuff(string powerName) =>
        EnemyDebuff.TryGetValue(powerName, out var v) ? v : HeuristicFallback(powerName, false);

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
}
