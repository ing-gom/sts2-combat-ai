using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2CombatAI;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2CombatAI";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(MainFile).Assembly);
            Logger.Info("[CombatAI] Harmony patches applied.");
            Sts2CombatAI.Planner.PlaystyleState.LogCallback =
                msg => Logger.Info($"[CombatAI] {msg}");
            Sts2CombatAI.Reflection.CardReflection.LogWarn =
                msg => Logger.Warn($"[CombatAI] {msg}");
            Sts2CombatAI.Data.CardCatalog.LogWarn =
                msg => Logger.Warn($"[CombatAI] {msg}");
            Logger.Info($"[CombatAI] catalog loaded: {Sts2CombatAI.Data.CardCatalog.Count} cards (v{Sts2CombatAI.Data.CardCatalog.Version})");

            // Persist playstyle selection across restarts (user_data file).
            Sts2CombatAI.Runtime.PlaystylePersistence.Install();

            // v0.6.3 — install DecisionLog persistence sink. Writes one NDJSON file
            // per combat to {user_data}/Sts2CombatAI/decision_log/. Used by
            // scripts/parse_decision_log.py for offline analysis (Phase B/C of the
            // runtime infra plan — see docs/runtime_analysis_infra_plan.md).
            Sts2CombatAI.Diagnostics.DecisionLogPersister.Install();
            // v0.10 (Phase 5) — Dense training-data exporter. Off by default;
            // toggle Sts2CombatAI.Diagnostics.TrainingDataExporter.Enabled=true
            // when collecting AI tuning datasets.
            Sts2CombatAI.Diagnostics.TrainingDataExporter.Install();

            // v0.10 — Scoring weights JSON externalization. Writes preset
            // defaults to {user_data}/Sts2CombatAI/scoring_weights/ on first
            // run (skips existing files), then loads any user edits back over
            // the static instances. Lets a tuner edit balanced.json etc. and
            // restart to apply, without recompiling.
            try
            {
                var configDir = System.IO.Path.Combine(
                    Godot.OS.GetUserDataDir(), "Sts2CombatAI", "scoring_weights");
                Sts2CombatAI.Planner.PlanScorerWeights.WriteDefaultsTo(configDir);
                Sts2CombatAI.Planner.PlanScorerWeights.LoadFromDirectory(configDir);

                // Phase 3: catalog + sequencing tier + planner config (single-file each).
                Sts2CombatAI.Planner.PowerCatalog.WriteDefaultsTo(
                    System.IO.Path.Combine(configDir, "power_catalog.json"));
                Sts2CombatAI.Planner.PowerCatalog.LoadFromJson(
                    System.IO.Path.Combine(configDir, "power_catalog.json"));

                Sts2CombatAI.Planner.PowerSequencingTier.WriteDefaultsTo(
                    System.IO.Path.Combine(configDir, "power_sequencing.json"));
                Sts2CombatAI.Planner.PowerSequencingTier.LoadFromJson(
                    System.IO.Path.Combine(configDir, "power_sequencing.json"));

                Sts2CombatAI.Planner.ActionPlanner.WriteDefaultsTo(
                    System.IO.Path.Combine(configDir, "planner_config.json"));
                Sts2CombatAI.Planner.ActionPlanner.LoadFromJson(
                    System.IO.Path.Combine(configDir, "planner_config.json"));
                // Apply mirror to the real exporter toggle.
                Sts2CombatAI.Diagnostics.TrainingDataExporter.Enabled =
                    Sts2CombatAI.Planner.ActionPlanner.TrainingDataEnabledMirror;

                Logger.Info($"[CombatAI] scoring config init from {configDir}");
            }
            catch (Exception wex)
            {
                Logger.Warn($"[CombatAI] scoring config init failed: {wex.Message}");
            }
            // Hook playstyle changes (Cycle/Set) to auto-save.
            var existingLog = Sts2CombatAI.Planner.PlaystyleState.LogCallback;
            Sts2CombatAI.Planner.PlaystyleState.LogCallback = msg =>
            {
                existingLog?.Invoke(msg);
                if (msg.StartsWith("playstyle ")) Sts2CombatAI.Runtime.PlaystylePersistence.Save();
            };

            // Vakuu mode — fallback poller for the Vakuu Play test button if the
            // Harmony _Ready hook on NEndTurnButton misses.
            if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
            {
                Sts2CombatAI.Modes.Vakuu.TestButtonPoller.Install(tree);
                // Live per-card score overlay on the player's hand.
                Sts2CombatAI.Modes.Vakuu.HandScoreOverlay.Install(tree);
            }
            var asmVer = typeof(MainFile).Assembly.GetName().Version?.ToString() ?? "unknown";
            // v0.7.80 — manifest version marker so we can verify the live dll
            // is the freshly-built one (asmVer is always 1.0.0.0).
            const string ManifestVersionMarker = "v0.8.9";
            Logger.Info($"[CombatAI] initialized (asm={asmVer}, manifest={ManifestVersionMarker}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CombatAI] init failed: {ex.Message}");
        }
    }
}
