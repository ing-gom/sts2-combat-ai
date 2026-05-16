using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Adds a "Vakuu Play" debug button next to the End Turn button so the planner can be
/// triggered on any turn without needing the Whispering Earring relic. Test-only.
///
/// Hook: Postfix on <see cref="NEndTurnButton._Ready"/> — fires once per button instance,
/// so the test button is recreated for every fresh combat scene without state leakage.
///
/// On click: synthesizes a <see cref="ThrowingPlayerChoiceContext"/> and invokes
/// <see cref="VakuuExecutor.RunPlannedTurn"/>. If a card play triggers an actual choice
/// prompt the context throws, the executor's per-step try-catch logs and breaks the loop —
/// graceful failure mode for v0.1.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "_Ready")]
internal static class VakuuTestButtonPatch
{
    private const string ButtonName = "VakuuTestButton";

    /// <summary>
    /// Combat round number in which the planner was last triggered. Set when the user
    /// clicks the button. Reset to -1 (re-armed) when a new combat starts.
    /// </summary>
    public static int LastUsedRound { get; private set; } = -1;

    /// <summary>Reset by the poller when combat state changes so the button rearms.</summary>
    public static void ResetForNewCombat() => LastUsedRound = -1;

    [HarmonyPostfix]
    public static void Postfix(NEndTurnButton __instance) => AttachIfMissing(__instance);

    /// <summary>
    /// Idempotent attach. Called from the Harmony _Ready postfix and from the polling
    /// fallback (TestButtonPoller). Safe to call multiple times — guarded by
    /// parent.HasNode(ButtonName).
    /// </summary>
    public static void AttachIfMissing(NEndTurnButton instance)
    {
        try
        {
            if (instance.HasNode(ButtonName))
            {
                return; // already attached this scene
            }

            // Attach as a *child of NEndTurnButton*: this way the play button rides on top of
            // End Turn no matter how the parent animates / repositions across resolutions.
            // Position is in End Turn's local coords — (0, -Y) puts us directly above it.
            // The button size mirrors End Turn so it lines up visually.
            const float verticalGap = 8f;
            var endTurnSize = instance.CustomMinimumSize.Y > 0
                ? instance.CustomMinimumSize
                : new Vector2(180, 60);

            // NEndTurnButton's native visual is built by its NButton script-side _Ready and
            // isn't reliably exposed as Godot scene children — Duplicate gave us only a Label
            // and the button still rendered blank. Pragmatic compromise: a plain Godot Button
            // with theme + a big gold-tinted label, sized to match End Turn, with the parent's
            // Theme inherited so it picks up whatever it can.
            var playBtn = new Button
            {
                Name = ButtonName,
                Text = "Vakuu Play",
                CustomMinimumSize = endTurnSize,
                Position = new Vector2(20, -(endTurnSize.Y + verticalGap)),  // small right offset, above End Turn
                ZIndex = 1000,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            if (instance.Theme != null) playBtn.Theme = instance.Theme;
            playBtn.AddThemeFontSizeOverride("font_size", 22);
            playBtn.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));         // STS2-style gold
            playBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.95f, 0.6f));
            playBtn.AddThemeColorOverride("font_pressed_color", new Color(0.9f, 0.7f, 0.3f));
            playBtn.Pressed += () => _ = OnPressedAsync();
            instance.CallDeferred(Node.MethodName.AddChild, playBtn);

            // Diagnostic: enumerate NEndTurnButton's children with types so we can see what's
            // actually there next time we revisit native-styling.
            var childSummary = string.Join(",", instance.GetChildren()
                .Select(c => $"{c.GetType().Name}:{c.Name}"));
            MainFile.Logger.Info(
                $"[CombatAI] test button attached as child of NEndTurnButton " +
                $"at offset (0,{playBtn.Position.Y:F0}) size=({endTurnSize.X:F0},{endTurnSize.Y:F0}) " +
                $"endTurnChildren=[{childSummary}]");
            return;

            MainFile.Logger.Info(
                $"[CombatAI] test button attached as child of NEndTurnButton " +
                $"at offset (0,{playBtn.Position.Y:F0}) size=({endTurnSize.X:F0},{endTurnSize.Y:F0})");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] test button attach failed: {ex.Message}");
        }
    }

    private static async System.Threading.Tasks.Task OnPressedAsync()
    {
        try
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState == null)
            {
                MainFile.Logger.Warn("[CombatAI] test button: no active combat state");
                return;
            }
            var player = LocalContext.GetMe(combatState);
            if (player == null)
            {
                MainFile.Logger.Warn("[CombatAI] test button: no local player");
                return;
            }

            // One-shot per turn: mark used immediately so the poller can disable the button.
            LastUsedRound = combatState.RoundNumber;
            MainFile.Logger.Info($"[CombatAI] test button pressed — running planner (turn {combatState.RoundNumber})");

            var ctx = new ThrowingPlayerChoiceContext();
            await VakuuExecutor.RunPlannedTurn(player, ctx);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] test button execution failed: {ex}");
        }
    }
}
