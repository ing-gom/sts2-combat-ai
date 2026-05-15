using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2VakuuPlus;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2VakuuPlus";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(MainFile).Assembly);
            Logger.Info("[VakuuPlus] Harmony patches applied.");
            Sts2VakuuPlus.Planner.PlaystyleState.LogCallback =
                msg => Logger.Info($"[VakuuPlus] {msg}");
            Sts2VakuuPlus.Reflection.CardReflection.LogWarn =
                msg => Logger.Warn($"[VakuuPlus] {msg}");
            Sts2VakuuPlus.Data.CardCatalog.LogWarn =
                msg => Logger.Warn($"[VakuuPlus] {msg}");
            Logger.Info($"[VakuuPlus] catalog loaded: {Sts2VakuuPlus.Data.CardCatalog.Count} cards (v{Sts2VakuuPlus.Data.CardCatalog.Version})");

            // Persist playstyle selection across restarts (user_data file).
            Sts2VakuuPlus.Runtime.PlaystylePersistence.Install();
            // Hook playstyle changes (Cycle/Set) to auto-save.
            var existingLog = Sts2VakuuPlus.Planner.PlaystyleState.LogCallback;
            Sts2VakuuPlus.Planner.PlaystyleState.LogCallback = msg =>
            {
                existingLog?.Invoke(msg);
                if (msg.StartsWith("playstyle ")) Sts2VakuuPlus.Runtime.PlaystylePersistence.Save();
            };

            // Fallback poller — attaches test buttons if the Harmony _Ready hook misses.
            if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
                Sts2VakuuPlus.Runtime.TestButtonPoller.Install(tree);
            Logger.Info("[VakuuPlus] initialized (v0.2.1).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VakuuPlus] init failed: {ex.Message}");
        }
    }
}
