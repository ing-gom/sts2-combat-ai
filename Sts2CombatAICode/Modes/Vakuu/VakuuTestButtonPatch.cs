using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Adds two debug buttons next to the End Turn button:
///   • "Vakuu Play" — runs the full multi-step planner (up to 13 cards / turn).
///   • "Vakuu Step" — plays a single card via the planner, then stops. Useful for
///     watching the AI's per-card decisions one at a time and inspecting how scores
///     update after each play.
///
/// Hook: Postfix on <see cref="NEndTurnButton._Ready"/> — fires once per button instance,
/// so the buttons are recreated for every fresh combat scene without state leakage.
///
/// On click: synthesizes a <see cref="ThrowingPlayerChoiceContext"/> and invokes
/// <see cref="VakuuExecutor.RunPlannedTurn"/>. If a card play triggers an actual choice
/// prompt the context throws, the executor's per-step try-catch logs and breaks the loop —
/// graceful failure mode for v0.1.
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "_Ready")]
internal static class VakuuTestButtonPatch
{
    private const string PlayButtonName = "VakuuTestButton";
    private const string StepButtonName = "VakuuStepButton";

    /// <summary>
    /// Combat round number in which the planner was last triggered via the Play button.
    /// Set when the user clicks it. Reset to -1 (re-armed) when a new combat starts.
    /// </summary>
    public static int LastUsedRound { get; private set; } = -1;

    /// <summary>
    /// True while a Step click's <see cref="VakuuExecutor.RunPlannedTurn"/> is in flight.
    /// Used by the poller to disable the Step button so rapid clicks don't queue
    /// overlapping plays (which would race over <see cref="VakuuExecutor.CurrentSnapshot"/>
    /// and <see cref="VakuuExecutor.CurrentPlayingCardId"/>).
    /// </summary>
    public static bool StepInFlight { get; private set; } = false;

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
            bool hasPlay = instance.HasNode(PlayButtonName);
            bool hasStep = instance.HasNode(StepButtonName);
            if (hasPlay && hasStep) return; // both already attached this scene

            // Attach as *children of NEndTurnButton*: this way the buttons ride on top of
            // End Turn no matter how the parent animates / repositions across resolutions.
            // Position is in End Turn's local coords — (0, -Y) puts us directly above it.
            // The button size mirrors End Turn so it lines up visually.
            //
            // Vertical stack (bottom → top): End Turn → Vakuu Play → Vakuu Step.
            // Step lives above Play because the typical flow is "inspect via Step
            // repeatedly, then commit the rest of the turn with Play" — keeping Play
            // closest to End Turn preserves muscle memory for the existing button.
            const float verticalGap = 8f;
            var endTurnSize = instance.CustomMinimumSize.Y > 0
                ? instance.CustomMinimumSize
                : new Vector2(180, 60);

            // NEndTurnButton's native visual is built by its NButton script-side _Ready and
            // isn't reliably exposed as Godot scene children — Duplicate gave us only a Label
            // and the button still rendered blank. Pragmatic compromise: a plain Godot Button
            // with theme + a tinted label, sized to match End Turn, with the parent's Theme
            // inherited so it picks up whatever it can.
            if (!hasPlay)
            {
                var playBtn = new Button
                {
                    Name = PlayButtonName,
                    Text = "Vakuu Play",
                    CustomMinimumSize = endTurnSize,
                    Position = new Vector2(20, -(endTurnSize.Y + verticalGap)),
                    ZIndex = 1000,
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                if (instance.Theme != null) playBtn.Theme = instance.Theme;
                playBtn.AddThemeFontSizeOverride("font_size", 22);
                playBtn.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));         // STS2-style gold
                playBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.95f, 0.6f));
                playBtn.AddThemeColorOverride("font_pressed_color", new Color(0.9f, 0.7f, 0.3f));
                playBtn.Pressed += () => _ = OnPlayPressedAsync();
                instance.CallDeferred(Node.MethodName.AddChild, playBtn);
            }

            if (!hasStep)
            {
                // Step button: silver tint to distinguish from gold Play. Same size so the
                // stack is visually uniform.
                var stepBtn = new Button
                {
                    Name = StepButtonName,
                    Text = "Vakuu Step",
                    CustomMinimumSize = endTurnSize,
                    Position = new Vector2(20, -2 * (endTurnSize.Y + verticalGap)),
                    ZIndex = 1000,
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                if (instance.Theme != null) stepBtn.Theme = instance.Theme;
                stepBtn.AddThemeFontSizeOverride("font_size", 20);
                stepBtn.AddThemeColorOverride("font_color", new Color(0.80f, 0.85f, 0.95f));     // silver-blue
                stepBtn.AddThemeColorOverride("font_hover_color", new Color(0.95f, 0.97f, 1f));
                stepBtn.AddThemeColorOverride("font_pressed_color", new Color(0.65f, 0.70f, 0.85f));
                stepBtn.Pressed += () => _ = OnStepPressedAsync();
                instance.CallDeferred(Node.MethodName.AddChild, stepBtn);
            }

            // Diagnostic: enumerate NEndTurnButton's children with types so we can see what's
            // actually there next time we revisit native-styling.
            var childSummary = string.Join(",", instance.GetChildren()
                .Select(c => $"{c.GetType().Name}:{c.Name}"));
            MainFile.Logger.Info(
                $"[CombatAI] test buttons attached as children of NEndTurnButton " +
                $"size=({endTurnSize.X:F0},{endTurnSize.Y:F0}) " +
                $"endTurnChildren=[{childSummary}]");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] test button attach failed: {ex.Message}");
        }
    }

    private static async System.Threading.Tasks.Task OnPlayPressedAsync()
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
            MainFile.Logger.Info($"[CombatAI] play button pressed — running planner (turn {combatState.RoundNumber})");

            var ctx = new ThrowingPlayerChoiceContext();
            await VakuuExecutor.RunPlannedTurn(player, ctx);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] play button execution failed: {ex}");
        }
    }

    /// <summary>
    /// Single-card click handler. Plays exactly one planner-chosen card and returns
    /// without ending the turn. The user can click again to play the next card, or
    /// click Vakuu Play to commit the rest of the turn automatically.
    ///
    /// Re-entrancy: <see cref="StepInFlight"/> blocks overlapping invocations so a
    /// fast double-click doesn't race over <see cref="VakuuExecutor.CurrentSnapshot"/>
    /// (the executor stores a single in-flight snapshot for the SmartVakuuCardSelector
    /// patches to read).
    /// </summary>
    private static async System.Threading.Tasks.Task OnStepPressedAsync()
    {
        if (StepInFlight) return;
        StepInFlight = true;
        try
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState == null)
            {
                MainFile.Logger.Warn("[CombatAI] step button: no active combat state");
                return;
            }
            var player = LocalContext.GetMe(combatState);
            if (player == null)
            {
                MainFile.Logger.Warn("[CombatAI] step button: no local player");
                return;
            }
            MainFile.Logger.Info(
                $"[CombatAI] step button pressed — single-card play (turn {combatState.RoundNumber})");

            var ctx = new ThrowingPlayerChoiceContext();
            await VakuuExecutor.RunPlannedTurn(player, ctx,
                relicForVfx: null, maxSteps: 1, isPartialTurn: true);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] step button execution failed: {ex}");
        }
        finally
        {
            StepInFlight = false;
        }
    }
}
