using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Within-turn priority bucket for Power cards. When a hand contains multiple Power
/// cards the planner uses this tier to decide which one resolves first.
///
/// Ordering: Setup > Scaling > Defensive > Tempo > Unknown > SelfHarm.
///
/// Conditional modifiers (see <see cref="PowerSequencingTier.ConditionalBonus"/>) can
/// re-order tiers — most importantly, Defensive jumps the queue when incoming damage
/// would actually leak past current block.
/// </summary>
internal enum SequencingTier
{
    SelfHarm  = -1,
    Unknown   = 0,
    Tempo     = 1,
    Defensive = 2,
    Scaling   = 3,
    Setup     = 4,
}

/// <summary>
/// Card-by-card priority classification for Power cards in the same hand.
///
/// PowerCatalog answers "how much is this buff worth across the whole fight".
/// This catalog answers "given multiple Power cards in hand right now, in what order
/// should they hit the table". The two are orthogonal — DemonForm is a top-tier
/// buff (PowerCatalog 1200) but Inflame should still play first this turn so the
/// remaining attacks ride its Strength.
///
/// Tiers:
///   • Setup     — multiplies *other cards in the same turn* (Strength, Dexterity,
///                 Focus, Accuracy, Vigor). Worthless if it lands after the cards
///                 that would consume the buff.
///   • Scaling   — long-fight permanents. Value compounds across turns; play as
///                 early in the fight as possible (DemonForm, EchoForm, ReaperForm,
///                 MachineLearning, Juggernaut, Mayhem, Corruption, NoxiousFumes…).
///   • Defensive — block / mitigation permanents (Barricade, Intangible, Buffer,
///                 Plated Armor, Thorns, FlameBarrier, Blur, FeelNoPain, Artifact).
///                 Threat-aware bonus pushes them ahead when leak > 0.
///   • Tempo     — energy / draw / free-cost (EnergyNextTurn, DrawCardsNextTurn,
///                 FreeAttack/Skill/Power). No within-turn synergy — defer.
///   • SelfHarm  — restrictions (NoDraw, NoBlock, Confused, MindRot…). Already
///                 deeply negative in PowerCatalog; tier just adds another nudge.
///
/// Conditional adjustments live in <see cref="ConditionalBonus"/> rather than the
/// static catalog so per-fight context (threat, hand composition, fight length) can
/// re-shape the priority order without touching this map.
/// </summary>
internal static class PowerSequencingTier
{
    // v0.10 — Mutable for JSON-load (LoadFromJson REPLACES). Public read-only
    // view kept for any future caller that wants to enumerate.
    public static IReadOnlyDictionary<string, SequencingTier> Tiers => _tiers;

    private static readonly Dictionary<string, SequencingTier> _tiers =
        new Dictionary<string, SequencingTier>
        {
            // ─── Setup (multiplies same-turn plays) ─────────────────────────────
            { "StrengthPower",            SequencingTier.Setup },
            { "TemporaryStrengthPower",   SequencingTier.Setup },
            { "DexterityPower",           SequencingTier.Setup },
            { "TemporaryDexterityPower",  SequencingTier.Setup },
            { "FocusPower",               SequencingTier.Setup },
            { "TemporaryFocusPower",      SequencingTier.Setup },
            { "AccuracyPower",            SequencingTier.Setup },
            { "VigorPower",               SequencingTier.Setup },

            // ─── Scaling (long-fight permanents) ────────────────────────────────
            { "DemonFormPower",           SequencingTier.Scaling },
            { "EchoFormPower",            SequencingTier.Scaling },
            { "ReaperFormPower",          SequencingTier.Scaling },
            { "JuggernautPower",          SequencingTier.Scaling },
            { "DanseMacabrePower",        SequencingTier.Scaling },
            { "MachineLearningPower",     SequencingTier.Scaling },
            { "BiasedCognitionPower",     SequencingTier.Scaling },
            { "MayhemPower",              SequencingTier.Scaling },
            { "BurstPower",               SequencingTier.Scaling },
            { "CorruptionPower",          SequencingTier.Scaling },
            { "EnragePower",              SequencingTier.Scaling },
            { "FeralPower",               SequencingTier.Scaling },
            { "RagePower",                SequencingTier.Scaling },
            { "HungerPower",              SequencingTier.Scaling },
            { "BeaconOfHopePower",        SequencingTier.Scaling },
            { "VitalSparkPower",          SequencingTier.Scaling },
            { "RitualPower",              SequencingTier.Scaling },
            { "AfterimagePower",          SequencingTier.Scaling },
            { "NoxiousFumesPower",        SequencingTier.Scaling },
            { "GalvanicPower",            SequencingTier.Scaling },

            // ─── Defensive (threat-dependent mitigation) ───────────────────────
            { "BarricadePower",           SequencingTier.Defensive },
            { "BufferPower",              SequencingTier.Defensive },
            { "IntangiblePower",          SequencingTier.Defensive },
            { "WraithFormPower",          SequencingTier.Defensive },
            { "ThornsPower",              SequencingTier.Defensive },
            { "FlameBarrierPower",        SequencingTier.Defensive },
            { "PlatedArmorPower",         SequencingTier.Defensive },
            { "BlurPower",                SequencingTier.Defensive },
            { "FeelNoPainPower",          SequencingTier.Defensive },
            { "ArtifactPower",            SequencingTier.Defensive },
            { "ShadowmeldPower",          SequencingTier.Defensive },
            { "RegenPower",               SequencingTier.Defensive },

            // ─── Tempo (energy/draw/free-cost — no within-turn synergy) ────────
            { "EnergyNextTurnPower",      SequencingTier.Tempo },
            { "DrawCardsNextTurnPower",   SequencingTier.Tempo },
            { "FreeAttackPower",          SequencingTier.Tempo },
            { "FreeSkillPower",           SequencingTier.Tempo },
            { "FreePowerPower",           SequencingTier.Tempo },

            // ─── SelfHarm (avoid — already deep-negative in PowerCatalog) ──────
            { "NoDrawPower",              SequencingTier.SelfHarm },
            { "NoBlockPower",             SequencingTier.SelfHarm },
            { "NoEnergyGainPower",        SequencingTier.SelfHarm },
            { "ConfusedPower",            SequencingTier.SelfHarm },
            { "HangPower",                SequencingTier.SelfHarm },
            { "MindRotPower",             SequencingTier.SelfHarm },
            { "WasteAwayPower",           SequencingTier.SelfHarm },
            { "SkittishPower",            SequencingTier.SelfHarm },
            { "ShrinkPower",              SequencingTier.SelfHarm },
            { "EntropyPower",             SequencingTier.SelfHarm },

            // ─── Coverage pass v0.103.2 — S/A tier cards previously Unknown ─────
            // Setup (multiplies same-turn plays — play before consumers)
            { "CrueltyPower",             SequencingTier.Setup },     // Vuln amp
            { "LethalityPower",           SequencingTier.Setup },     // first attack/turn +50%
            { "TrackingPower",            SequencingTier.Setup },     // Weak amp
            { "FastenPower",              SequencingTier.Setup },     // +5 block to Skills

            // Scaling (long-fight permanents — play early)
            { "UnmovablePower",           SequencingTier.Scaling },
            { "DarkEmbracePower",         SequencingTier.Scaling },
            { "InfernoPower",             SequencingTier.Scaling },
            { "ThunderPower",             SequencingTier.Scaling },
            { "PagestormPower",           SequencingTier.Scaling },
            { "SleightOfFleshPower",      SequencingTier.Scaling },
            { "CountdownPower",           SequencingTier.Scaling },
            { "DevourLifePower",          SequencingTier.Scaling },
            { "HauntPower",               SequencingTier.Scaling },
            { "ChildOfTheStarsPower",     SequencingTier.Scaling },
            { "TheSealedThronePower",     SequencingTier.Scaling },
            { "HammerTimePower",          SequencingTier.Scaling },
            { "AutomationPower",          SequencingTier.Scaling },
            { "NeurosurgePower",          SequencingTier.Scaling },

            // Defensive (block / mitigation permanents — threat-aware priority)
            { "CrimsonMantlePower",       SequencingTier.Defensive },
            { "CoolantPower",             SequencingTier.Defensive },
            { "SpinnerPower",             SequencingTier.Defensive },
            { "ShroudPower",              SequencingTier.Defensive },
            { "SpiritOfAshPower",         SequencingTier.Defensive },
            { "TankPower",                SequencingTier.Defensive },
            { "SneakyPower",              SequencingTier.Defensive },

            // Tempo (energy / draw / free-cost — defer when no current-turn use)
            { "VoidFormPower",            SequencingTier.Tempo },
            { "CallOfTheVoidPower",       SequencingTier.Tempo },
            { "DemesnePower",             SequencingTier.Tempo },
            { "ToolsOfTheTradePower",     SequencingTier.Tempo },
            { "WellLaidPlansPower",       SequencingTier.Tempo },
            { "InfiniteBladesPower",      SequencingTier.Tempo },
            { "ForbiddenGrimoirePower",   SequencingTier.Tempo },     // out-of-combat reward — defer

            // ─── Coverage pass v0.103.2 — B/C tier ──────────────────────────────
            // Shared (Plating block carryover — also classifies STONE_ARMOR / ETERNAL_ARMOR)
            { "PlatingPower",             SequencingTier.Defensive },

            // B tier — Setup (multiplies same-turn plays)
            { "PhantomBladesPower",       SequencingTier.Setup },     // first Shiv +9 dmg

            // B tier — Scaling (long-fight permanents)
            { "CapacitorPower",           SequencingTier.Scaling },
            { "IterationPower",           SequencingTier.Scaling },
            { "SmokestackPower",          SequencingTier.Scaling },
            { "StormPower",               SequencingTier.Scaling },
            { "SubroutinePower",          SequencingTier.Scaling },
            { "TrashToTreasurePower",     SequencingTier.Scaling },
            { "StampedePower",            SequencingTier.Scaling },
            { "ViciousPower",             SequencingTier.Scaling },
            { "BlackHolePower",           SequencingTier.Scaling },
            { "GenesisPower",             SequencingTier.Scaling },
            { "OrbitPower",               SequencingTier.Scaling },
            { "PaleBlueDotPower",         SequencingTier.Scaling },
            { "TyrannyPower",             SequencingTier.Scaling },
            { "PanachePower",             SequencingTier.Scaling },
            { "PrepTimePower",            SequencingTier.Scaling },
            { "AccelerantPower",          SequencingTier.Scaling },

            // B tier — Defensive
            // (none — block-granting B-tier Powers covered via PlatingPower)

            // B tier — Tempo (energy/draw/free-cost)
            { "CreativeAiPower",          SequencingTier.Tempo },
            { "AggressionPower",          SequencingTier.Tempo },
            { "PyrePower",                SequencingTier.Tempo },
            { "SentryModePower",          SequencingTier.Tempo },
            { "RoyaltiesPower",           SequencingTier.Tempo },     // out-of-combat — defer
            { "HelloWorldPower",          SequencingTier.Tempo },

            // C tier — Scaling
            { "HailstormPower",           SequencingTier.Scaling },
            { "CalcifyPower",             SequencingTier.Scaling },
            { "NecroMasteryPower",        SequencingTier.Scaling },
            { "ArsenalPower",             SequencingTier.Scaling },
            { "FurnacePower",             SequencingTier.Scaling },
            { "SeekingEdgePower",         SequencingTier.Scaling },
            { "SwordSagePower",           SequencingTier.Scaling },
            { "RollingBoulderPower",      SequencingTier.Scaling },
            { "EnvenomPower",             SequencingTier.Scaling },
            { "MasterPlannerPower",       SequencingTier.Scaling },

            // C tier — Defensive
            { "ParryPower",               SequencingTier.Defensive },
            { "PillarOfCreationPower",    SequencingTier.Defensive },

            // C tier — Tempo
            { "DrumOfBattlePower",        SequencingTier.Tempo },
            { "SpectrumShiftPower",       SequencingTier.Tempo },
            { "StratagemPower",           SequencingTier.Tempo },
            { "FanOfKnivesPower",         SequencingTier.Tempo },     // 4-Shiv burst

            // ─── Coverage pass v0.103.2 — D tier ────────────────────────────────
            // Scaling (long-fight passives)
            { "ConsumingShadowPower",     SequencingTier.Scaling },
            { "LoopPower",                SequencingTier.Scaling },
            { "HellraiserPower",          SequencingTier.Scaling },
            { "JugglingPower",            SequencingTier.Scaling },
            { "CalamityPower",            SequencingTier.Scaling },
            { "OutbreakPower",            SequencingTier.Scaling },
            { "SerpentFormPower",         SequencingTier.Scaling },
            { "SpeedsterPower",           SequencingTier.Scaling },

            // Defensive (mitigation via debuff)
            { "MonarchsGazePower",        SequencingTier.Defensive },

            // Tempo (deck cycling)
            { "NostalgiaPower",           SequencingTier.Tempo },
        };

    /// <summary>
    /// Tier of a single power name. Falls back to <see cref="SequencingTier.Unknown"/>
    /// for powers not in the table — those receive no tier-ordering bonus and rely on
    /// the base PowerCatalog value alone.
    /// </summary>
    public static SequencingTier ClassifyPower(string powerName)
        => _tiers.TryGetValue(powerName, out var t) ? t : SequencingTier.Unknown;

    /// <summary>
    /// Effective tier of a Power card. When a card applies multiple powers (rare —
    /// most apply one), the highest-priority tier wins.
    ///
    /// v0.10 — Falls back to Id-derived power name when PowerApps is empty.
    /// ~24 powers (AUTOMATION, BARRICADE, MAYHEM, REAPER_FORM, UNMOVABLE, TRACKING,
    /// THE_SEALED_THRONE, AGGRESSION, etc.) apply their power via PowerCmd.Apply&lt;X&gt;()
    /// inside OnPlay rather than as a PowerVar in DynamicVars, so PowerApps is
    /// empty at snapshot time. Without the fallback these cards always classified
    /// Unknown — skipping every tier-conditional bonus (drawTrigEarly etc.).
    /// Mirror of PlanScorer.cs:467 idDerived block.
    /// </summary>
    public static SequencingTier Classify(SimCard card)
    {
        if (!card.IsPower) return SequencingTier.Unknown;
        SequencingTier? best = null;
        foreach (var kvp in card.PowerApps)
        {
            var t = ClassifyPower(kvp.Key);
            if (best == null || (int)t > (int)best.Value) best = t;
        }
        if (best == null)
        {
            string derived = IdToPowerName(card.Id);
            if (!string.IsNullOrEmpty(derived))
            {
                var t = ClassifyPower(derived);
                if (t != SequencingTier.Unknown) best = t;
            }
        }
        return best ?? SequencingTier.Unknown;
    }

    /// <summary>
    /// Local copy of PlanScorer.IdToPowerName so PowerSequencingTier doesn't
    /// reach across into PlanScorer internals. Converts "CARD.AUTOMATION" →
    /// "AutomationPower". Returns empty for malformed input.
    /// </summary>
    private static string IdToPowerName(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return "";
        int dot = cardId.IndexOf('.');
        string body = dot >= 0 ? cardId.Substring(dot + 1) : cardId;
        if (string.IsNullOrEmpty(body)) return "";
        var sb = new System.Text.StringBuilder(body.Length + 5);
        bool capitalize = true;
        foreach (char c in body)
        {
            if (c == '_') { capitalize = true; continue; }
            if (capitalize) { sb.Append(char.ToUpperInvariant(c)); capitalize = false; }
            else { sb.Append(char.ToLowerInvariant(c)); }
        }
        sb.Append("Power");
        return sb.ToString();
    }

    /// <summary>
    /// Base tier-ordering nudge — only applies when ≥2 Power cards compete in the
    /// same hand. Magnitude is intentionally small (50–200) so PowerCatalog values
    /// still dominate; this only resolves close ties in the Setup-first direction.
    /// </summary>
    public static int OrderingBonus(SequencingTier tier, int powerCardsInHand)
    {
        if (powerCardsInHand < 2) return 0;
        return tier switch
        {
            SequencingTier.Setup     => 200,
            SequencingTier.Scaling   => 150,
            SequencingTier.Defensive => 100,
            SequencingTier.Tempo     => 50,
            SequencingTier.SelfHarm  => -300,
            _ => 0,
        };
    }

    /// <summary>
    /// Tier-aware conditional bonus. Big-magnitude effects (threat-aware Defensive,
    /// beneficiary-aware Setup, dying-fight Tempo demotion) live here.
    ///
    /// Anti-double-count notes:
    /// • Strength / Dexterity beneficiary scaling stays in <see cref="HandSynergy"/>.
    ///   Setup tier here only adds Focus / Accuracy / Vigor synergy + a "no
    ///   beneficiary at all" penalty, neither of which HandSynergy covers.
    /// • Generic fight-length bonus stays in EvaluatePowerFightContext. The Scaling
    ///   tier here adds an extra penalty only for the boundary case of one nearly-dead
    ///   enemy left, which the broad threshold doesn't catch.
    /// </summary>
    public static (int bonus, string detail) ConditionalBonus(
        SimCard self, SequencingTier tier, SimState state, PlanScorerWeights w)
    {
        int b = 0;
        var parts = new List<string>();

        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable && c.IsAttack);
        int remainingSelfBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable && c.IsSkill && c.Block > 0);
        int remainingOrbCards = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && c.IsPlayable
            && (c.ChannelCount > 0 || c.EvokeCount > 0));

        // Survival urgency — Setup / Scaling / Tempo powers should defer when the
        // player is about to die. Defensive tier is exempt (it IS the survival play).
        // SelfHarm is already deep-negative — no need to pile on.
        if (tier == SequencingTier.Setup
            || tier == SequencingTier.Scaling
            || tier == SequencingTier.Tempo)
        {
            var urgency = EnemyTurnSimulator.GetSurvivalUrgency(state);
            int p = urgency switch
            {
                SurvivalUrgency.Fatal    => -2200,
                SurvivalUrgency.Heavy    => -900,
                SurvivalUrgency.Moderate => -250,
                _ => 0,
            };
            if (p != 0)
            {
                b += p;
                parts.Add($"survival{urgency}={p}");
            }
        }

        switch (tier)
        {
            case SequencingTier.Setup:
            {
                bool hasStrength = self.PowerApps.ContainsKey("StrengthPower")
                                || self.PowerApps.ContainsKey("TemporaryStrengthPower");
                bool hasDex = self.PowerApps.ContainsKey("DexterityPower")
                           || self.PowerApps.ContainsKey("TemporaryDexterityPower");
                bool hasFocus = self.PowerApps.ContainsKey("FocusPower")
                             || self.PowerApps.ContainsKey("TemporaryFocusPower");
                bool hasAccuracy = self.PowerApps.ContainsKey("AccuracyPower");
                bool hasVigor = self.PowerApps.ContainsKey("VigorPower");

                // Focus / Accuracy synergy isn't in HandSynergy yet — add per-card scaling.
                if (hasFocus && remainingOrbCards > 0)
                {
                    int v = remainingOrbCards * 80;
                    b += v;
                    parts.Add($"focusOrbSyn=+{v}");
                }
                if (hasAccuracy && remainingAttacks > 0)
                {
                    int v = remainingAttacks * 30;
                    b += v;
                    parts.Add($"accAtkSyn=+{v}");
                }
                if (hasVigor && remainingAttacks == 0)
                {
                    b -= 250;
                    parts.Add("vigorDeadSetup=-250");
                }

                bool anyBenefit =
                    (hasStrength && remainingAttacks > 0)
                    || (hasDex && remainingSelfBlocks > 0)
                    || (hasFocus && remainingOrbCards > 0)
                    || (hasAccuracy && remainingAttacks > 0)
                    || (hasVigor && remainingAttacks > 0);
                if (!anyBenefit)
                {
                    b -= 300;
                    parts.Add("setupNoBenefit=-300");
                }
                break;
            }
            case SequencingTier.Defensive:
            {
                if (EnemyTurnSimulator.AllInert(state))
                {
                    b -= 200;
                    parts.Add("defNoThreat=-200");
                    break;
                }
                // PredictPlayerDmg already subtracts PlayerBlock and clamps at 0,
                // so its return value IS the leak.
                int leak = EnemyTurnSimulator.PredictPlayerDmg(state);
                double threat = EnemyTurnSimulator.ThreatRatio(state);
                if (leak > 0)
                {
                    int v = System.Math.Min(800, leak * 40);
                    b += v;
                    parts.Add($"defLeak({leak})=+{v}");
                }
                else if (threat < w.NoThreatRatio)
                {
                    b -= 200;
                    parts.Add("defNoThreat=-200");
                }
                break;
            }
            case SequencingTier.Scaling:
            {
                // PLAY_TRIGGER powers (Afterimage, Calamity, Serpent Form, Sleight of
                // Flesh, The Sealed Throne) gain value per card we'll still play this
                // turn. The flat PowerCatalog value doesn't capture this — boost by
                // remaining playable count × 60.
                if (self.Axes.Contains("PLAY_TRIGGER"))
                {
                    int remainingPlayable = state.Hand.Count(c =>
                        !ReferenceEquals(c, self) && c.IsPlayable
                        && !c.IsCurseOrStatus);
                    int v = remainingPlayable * 60;
                    if (v > 0) { b += v; parts.Add($"playTrigSyn=+{v}"); }
                }

                // v0.10 — Cumulative-counter Scaling powers (AUTOMATION's
                // AfterCardDrawn counter etc.). The carryover value across
                // remaining turns is ALREADY captured by automationTick in
                // EffectSynergy. Deploy-order within THIS turn only matters
                // when mid-turn draws are still pending (Skim, Backflip,
                // draw-axis attacks). Without those, step-1 vs step-N
                // placement produces identical trigger counts — both wait
                // for next turn's hand draw to start ticking.
                //
                // Magnitude: handDrawCount × 100. Small nudge proportional
                // to remaining mid-turn draws. Earlier prototype used
                // remainingTurns × 400 which double-counted automationTick's
                // carryover value — caught when the user pointed out that
                // AUTOMATION is fundamentally a next-turn-onwards power.
                if (self.Axes.Contains("DRAW_ON_DRAW"))
                {
                    int handDrawCount = 0;
                    foreach (var c in state.Hand)
                    {
                        if (ReferenceEquals(c, self)) continue;
                        if (!c.IsPlayable || c.IsCurseOrStatus) continue;
                        if (c.DrawCount > 0) handDrawCount += c.DrawCount;
                    }
                    int v = handDrawCount * w.DrawTrigEarlyPerHandDraw;
                    if (v > 0) { b += v; parts.Add($"drawTrigInHand({handDrawCount})=+{v}"); }
                }

                int aliveEnemies = state.Enemies.Count(e => e.IsAlive);
                int totalHp = EnemyTurnSimulator.TotalAliveEnemyHp(state);
                if (aliveEnemies <= 1 && totalHp <= 25)
                {
                    b -= 300;
                    parts.Add("scaleDyingFight=-300");
                }
                break;
            }
            case SequencingTier.Tempo:
            {
                int totalHp = EnemyTurnSimulator.TotalAliveEnemyHp(state);
                if (totalHp <= 15)
                {
                    b -= 400;
                    parts.Add("tempoEndingFight=-400");
                }
                break;
            }
        }

        return (b, parts.Count == 0 ? "" : string.Join(",", parts));
    }

    // ─── JSON load / save ───────────────────────────────────────────────────
    // v0.10 — power_sequencing.json overlays the in-memory tier map. Format:
    //   { "tiers": { "PowerName": "Setup|Scaling|Defensive|Tempo|SelfHarm" } }
    // Load REPLACES the map (single source of truth once written). Conditional
    // magnitudes inside the switch (urgency penalties, defLeak etc.) stay in
    // code for now — those are tied to algorithm shape, not pure lookup data.

    private sealed class _SequencingFile
    {
        public Dictionary<string, string> tiers { get; set; } = new();
    }

    private static readonly JsonSerializerOptions _seqJsonOpts = new() { WriteIndented = true };

    public static void WriteDefaultsTo(string path)
    {
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var f = new _SequencingFile();
            foreach (var kv in _tiers) f.tiers[kv.Key] = kv.Value.ToString();
            File.WriteAllText(path, JsonSerializer.Serialize(f, _seqJsonOpts));
        }
        catch { /* best-effort */ }
    }

    public static void LoadFromJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var f = JsonSerializer.Deserialize<_SequencingFile>(File.ReadAllText(path), _seqJsonOpts);
            if (f == null) return;
            _tiers.Clear();
            foreach (var kv in f.tiers)
            {
                if (System.Enum.TryParse<SequencingTier>(kv.Value, ignoreCase: true, out var t))
                    _tiers[kv.Key] = t;
            }
        }
        catch { /* malformed → keep defaults */ }
    }
}
