using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using Sts2CombatAI.Planner;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Logs AI scores for each card option whenever a card-selection screen
/// opens (transform / fetch / discard / reward prompts). Reads the screen's
/// internal `_cards` list via reflection, captures a fresh SimState
/// snapshot, and prints a sorted "card → score" table to the mod logger.
///
/// Purpose: when VakuuCardSelector's PushSelector window is NOT active
/// (e.g. ForegoneConclusion's BeforeHandDraw prompt fires at turn start,
/// before Vakuu's play phase), the auto-pick hook misses it. This logger
/// at least surfaces the AI's recommendation in the log so the user can
/// make an informed manual choice.
///
/// Read-only — does not modify selection. Safe even when Vakuu is disabled.
///
/// Multiple Harmony patches: each concrete subclass overrides
/// ConnectSignalsAndInitGrid (NSimpleCardSelectScreen / NDeckCardSelectScreen
/// / NDeckEnchantSelectScreen / NDeckTransformSelectScreen /
/// NDeckUpgradeSelectScreen). Patching only the abstract base misses the
/// override calls. The bootstrap class declares no [HarmonyPatch] itself;
/// the nested patch classes apply per-subclass.
/// </summary>
internal static class SelectionScoreLogger
{
    private static readonly FieldInfo? _cardsField =
        AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards");

    /// <summary>
    /// True while a card-selection screen is open. Read by HandScoreOverlay
    /// to suppress its in-hand labels while the selection overlay is shown.
    ///
    /// Computed by walking the scene tree for any *visible*
    /// <see cref="NCardGridSelectionScreen"/>. We don't trust _Ready/_ExitTree
    /// hooks: STS2 sometimes pre-instantiates hidden selection grids as combat
    /// scene children (in which case _Ready fires at combat start, _ExitTree
    /// never fires, and the flag would get stuck on). Visibility-based
    /// detection is timing-independent and self-correcting.
    /// </summary>
    public static bool SelectionOverlayActive
    {
        get
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            var root = tree?.Root;
            if (root == null) return false;
            return AnyVisibleSelectionScreen(root);
        }
    }

    private static bool AnyVisibleSelectionScreen(Godot.Node node)
    {
        if (node is NCardGridSelectionScreen s
            && Godot.GodotObject.IsInstanceValid(s)
            && s.Visible)
            return true;
        var children = node.GetChildren();
        for (int i = 0; i < children.Count; i++)
        {
            if (AnyVisibleSelectionScreen(children[i])) return true;
        }
        return false;
    }

    // Legacy stubs — kept so existing Harmony hooks compile, but they no
    // longer drive the flag. Visibility-based detection makes them inert.
    internal static void NoteOpened() { }
    internal static void NoteClosed() { }

    // Only NSimpleCardSelectScreen overrides ConnectSignalsAndInitGrid. The four
    // Deck* subclasses inherit the base implementation without overriding, so
    // Harmony cannot resolve `[HarmonyPatch(typeof(NDeckCardSelectScreen),
    // "ConnectSignalsAndInitGrid")]` — AccessTools.Method returns null on those
    // types, and PatchAll throws "Patching exception in method null", which
    // aborts ALL of MainFile.Initialize (no overlay install, no logger).
    //
    // For now we only patch the one concrete override. To catch the Deck*
    // screens too we'd patch the base `NCardGridSelectionScreen.
    // ConnectSignalsAndInitGrid` (it's `protected virtual`) — left as a TODO.
    [HarmonyPatch(typeof(NSimpleCardSelectScreen), "ConnectSignalsAndInitGrid")]
    internal static class SimpleSelectScreen
    {
        [HarmonyPostfix]
        public static void Postfix(NSimpleCardSelectScreen __instance)
            => LogScoresFor(__instance);
    }

    /// <summary>
    /// Tracks open/close transitions of any card-selection screen.
    /// HandScoreOverlay uses SelectionOverlayActive to suppress hand labels
    /// while the selection screen is on top.
    /// </summary>
    [HarmonyPatch(typeof(NCardGridSelectionScreen), "_Ready")]
    internal static class OverlayOpenedHook
    {
        [HarmonyPostfix]
        public static void Postfix() => NoteOpened();
    }

    [HarmonyPatch(typeof(NCardGridSelectionScreen), "_ExitTree")]
    internal static class OverlayClosedHook
    {
        [HarmonyPostfix]
        public static void Postfix() => NoteClosed();
    }

    private static void LogScoresFor(NCardGridSelectionScreen __instance)
    {
        try
        {
            if (_cardsField == null) return;
            var cards = _cardsField.GetValue(__instance) as IReadOnlyList<CardModel>;
            if (cards == null || cards.Count == 0) return;

            // Prefer the active Vakuu snapshot when available (mid-play prompt);
            // otherwise capture fresh (turn-start / pre-Vakuu prompts).
            SimState? snapshot = VakuuExecutor.CurrentSnapshot;
            if (snapshot == null)
            {
                var cm = CombatManager.Instance;
                if (cm == null || !cm.IsInProgress || cm.IsOverOrEnding) return;
                var combatState = cm.DebugOnlyGetState();
                if (combatState == null) return;
                var player = LocalContext.GetMe(combatState);
                if (player?.Creature == null) return;
                snapshot = StateSnapshotter.Capture(player);
            }
            if (snapshot == null) return;

            var playingId = VakuuExecutor.CurrentPlayingCardId;
            var ranked = SmartSelectorLogic
                .ScoreAll(cards, snapshot, playingId)
                .ToList();

            // Compact log: one line per option, sorted high → low. Screen name
            // hints which prompt triggered (FOREGONE / Reward / Hand-discard).
            string screen = __instance.GetType().Name;
            string trigger = string.IsNullOrEmpty(playingId) ? "" : $" via {playingId}";
            MainFile.Logger.Info($"[CombatAI] selection scores ({screen}{trigger}):");
            foreach (var (card, score) in ranked)
            {
                MainFile.Logger.Info($"  {score,6}  {card.Id.Entry}");
            }
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] selection score log failed: {ex.Message}");
        }
    }
}
