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
                Sts2CombatAI.Modes.Vakuu.TestButtonPoller.Install(tree);
            var asmVer = typeof(MainFile).Assembly.GetName().Version?.ToString() ?? "unknown";
            // v0.7.80 — manifest version marker so we can verify the live dll
            // is the freshly-built one (asmVer is always 1.0.0.0).
            const string ManifestVersionMarker = "v0.7.99";
            Logger.Info($"[CombatAI] initialized (asm={asmVer}, manifest={ManifestVersionMarker}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CombatAI] init failed: {ex.Message}");
        }
    }
}
