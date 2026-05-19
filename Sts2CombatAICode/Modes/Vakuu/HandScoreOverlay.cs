using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2CombatAI.Planner;
using Sts2CombatAI.Reflection;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Live per-card score overlay for the player's hand during combat. Each card in hand
/// gets a small label attached as a child of its NCard control showing the planner's
/// single-card score for that card in the current state. Top-scoring card is rendered
/// larger and in gold so the user can see at a glance which card the planner would
/// pick if asked to step.
///
/// Design choices (v0.1):
///   • Tick at 0.25s (4Hz). Cards animate continuously but the label is parented to
///     the NCard so it follows automatically — we only need to recompute when state
///     changes, not when cards move.
///   • Cheap dirty-check (round number, energy, hand-pile size) gates the expensive
///     <see cref="StateSnapshotter.Capture"/>. Skips recompute when state is static.
///   • Score = <see cref="PlanScorer.Score"/>. For <see cref="TargetType.AnyEnemy"/>
///     cards we pick the best target. <see cref="PlanScorer.PlayOrderBias"/> is
///     intentionally NOT applied — that bias makes Retain cards look bad to suppress
///     them in selection, which would visually penalise them on the overlay.
///   • Labels are removed from NCards that drop out of hand (played/discarded). Pool
///     reuse is handled: each tick re-walks the previously-labeled list and frees any
///     label whose parent no longer matches a current hand card.
/// </summary>
internal sealed partial class HandScoreOverlay : Node
{
    private const double IntervalSeconds = 0.25;
    private const string LabelName = "VakuuHandScoreLabel";

    /// <summary>Set false to suppress the overlay without removing the polling node.</summary>
    public static bool Enabled { get; set; } = true;

    private double _accumulator = 0;

    /// <summary>Cheap state hash from the previous tick; skip recompute if unchanged.</summary>
    private int _lastStateHash = int.MinValue;

    /// <summary>
    /// Cached score-per-model from the last expensive recompute. Replayed on
    /// every tick (even when the dirty hash hasn't changed) so that NCards
    /// that became reachable via <c>hand.GetCard</c> *between* recomputes —
    /// freshly drawn cards whose NCardHolder.CardNode was still null during
    /// the recompute — can be labeled on a later tick without waiting for
    /// another state change. Cleared on the next dirty recompute.
    /// </summary>
    private readonly Dictionary<CardModel, int> _cachedScores = new();
    private CardModel? _cachedBest;

    /// <summary>
    /// NCards we've previously attached labels to. On each tick we walk this list
    /// to remove labels from cards that have left the hand (or had their NCard
    /// recycled by the pool to show a different model).
    /// </summary>
    private readonly List<NCard> _labeledCards = new();

    public static void Install(SceneTree tree)
    {
        try
        {
            var node = new HandScoreOverlay { Name = "Sts2CombatAIHandScoreOverlay" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, node);
            MainFile.Logger.Info("[CombatAI] hand score overlay installed");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] hand overlay install failed: {ex.Message}");
        }
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        if (_accumulator < IntervalSeconds) return;
        _accumulator = 0;

        try { Tick(); }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] hand overlay tick failed: {ex.Message}");
        }
    }

    private void Tick()
    {
        if (!Enabled) { ClearAllLabels(); _lastStateHash = int.MinValue; return; }

        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsInProgress || cm.IsOverOrEnding)
        {
            ClearAllLabels();
            _lastStateHash = int.MinValue;
            return;
        }
        if (!cm.IsPlayPhase || cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo)
        {
            // Hide during enemy phase / transitions; don't clear the cache so we redraw
            // instantly when player phase returns with the same hand.
            ClearAllLabels();
            return;
        }
        // Suppress hand labels while *any* blocking overlay is on top of the
        // combat scene — pause menu, settings, card-selection prompts, generic
        // modal popups. One scene-tree walk per tick that early-exits on the
        // first visible blocker. See IsAnyBlockingOverlayVisible.
        if (IsAnyBlockingOverlayVisible())
        {
            ClearAllLabels();
            _lastStateHash = int.MinValue;     // force redraw when overlay closes
            return;
        }

        var combatState = cm.DebugOnlyGetState();
        if (combatState == null) { ClearAllLabels(); return; }
        var player = LocalContext.GetMe(combatState);
        if (player?.Creature == null) { ClearAllLabels(); return; }

        // Cheap dirty check before paying for a full StateSnapshotter.Capture.
        // Round number + energy + hand pile size + playstyle catch the dominant
        // "would the scores change?" signals. Misses subtle changes (enemy intent
        // shift, mid-turn power application) — those refresh on the next tick where
        // the user is more likely to be inspecting cards anyway.
        int hash = ComputeCheapHash(combatState, player);
        if (hash != _lastStateHash)
        {
            _lastStateHash = hash;
            var snapshot = StateSnapshotter.Capture(player);
            if (snapshot == null) return;

            // Recompute the score-per-model cache.
            _cachedScores.Clear();
            _cachedBest = null;
            int bestScore = int.MinValue;
            foreach (var sc in snapshot.Hand)
            {
                if (sc.SourceRef == null) continue;
                // Skip cards the planner would never play — overlay would lie.
                if (!sc.IsPlayable || sc.IsCurseOrStatus || sc.Cost < 0) continue;
                int s = BestTargetScore(sc, snapshot);
                _cachedScores[sc.SourceRef] = s;
                if (s > bestScore) { bestScore = s; _cachedBest = sc.SourceRef; }
            }
        }

        // Always refresh labels from the cached score map, even when the hash
        // didn't change. A freshly drawn card may not be reachable via
        // hand.GetCard at the moment we score it (NCardHolder.CardNode is
        // assigned asynchronously during the draw animation). Re-walking the
        // cache every tick lets us attach the label on a later tick when the
        // NCard finally becomes reachable, without needing the dirty hash to
        // change again.
        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand == null) { ClearAllLabels(); return; }

        // 1) Remove labels from previously-labeled NCards that are no longer in
        // the score map (either dropped out of hand or NCard pool-recycled to a
        // different model).
        for (int i = _labeledCards.Count - 1; i >= 0; i--)
        {
            var nc = _labeledCards[i];
            bool keep = false;
            if (nc != null && GodotObject.IsInstanceValid(nc) && nc.Model != null)
                keep = _cachedScores.ContainsKey(nc.Model);
            if (!keep)
            {
                RemoveLabelFrom(nc);
                _labeledCards.RemoveAt(i);
            }
        }

        // 2) Attach or update labels for each current hand card with a score.
        foreach (var kvp in _cachedScores)
        {
            var ncard = hand.GetCard(kvp.Key);
            if (ncard == null || !GodotObject.IsInstanceValid(ncard)) continue;
            bool isBest = ReferenceEquals(kvp.Key, _cachedBest);
            AttachOrUpdateLabel(ncard, kvp.Value, isBest);
        }
    }

    /// <summary>
    /// True if any UI overlay is visibly on top of the combat scene — pause
    /// menu, settings, card-selection screens (transform / fetch / reward),
    /// or generic modal popups. Walks the scene tree once and short-circuits
    /// on the first match. Skips subtrees of invisible Controls to keep the
    /// walk cheap (~one-off cost per Tick).
    ///
    /// Detected node types:
    ///   • <see cref="NSubmenu"/> — base of NPauseMenu, NSettingsScreen, etc.
    ///   • <see cref="NCardGridSelectionScreen"/> — card-pick screens.
    ///   • <see cref="NModalContainer"/> — generic yes/no popups, dialogs.
    ///
    /// Hover tooltips and card-preview popups are intentionally NOT detected:
    /// they don't block gameplay and previewing cards is a frequent action
    /// where the user still wants score context.
    /// </summary>
    private static bool IsAnyBlockingOverlayVisible()
    {
        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        var root = tree?.Root;
        if (root == null) return false;
        return WalkForBlocker(root);
    }

    private static bool WalkForBlocker(Godot.Node node)
    {
        // Skip the entire subtree of an invisible Control — descendants can't
        // be rendered without a visible parent, so they can't be blocking.
        if (node is Control c)
        {
            if (!c.Visible) return false;
            // NSubmenu — pause / settings / main-menu submenus.
            // NCardGridSelectionScreen — transform / fetch / reward prompts.
            //
            // NModalContainer is intentionally NOT checked here: it's a
            // *wrapper* always present in the combat scene (visible=true with
            // no children when idle), so treating it as a blocker would
            // suppress the hand overlay during normal play. Modals attached
            // inside it would still match if they extend one of the types
            // above; otherwise add the specific modal type when needed.
            if (c is NSubmenu) return true;
            if (c is NCardGridSelectionScreen) return true;
        }
        var children = node.GetChildren();
        for (int i = 0; i < children.Count; i++)
        {
            if (WalkForBlocker(children[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Hash that catches the dominant "scores might have changed" signals without
    /// calling the full snapshot pipeline. Misses mid-turn power application etc.,
    /// but those manifest on the next state-changing event (card play, energy spend)
    /// which the user is about to trigger anyway.
    /// </summary>
    private static int ComputeCheapHash(CombatState combatState, Player player)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + combatState.RoundNumber;
            h = h * 31 + (int)PlaystyleState.Current;

            try
            {
                var pcs = player.PlayerCombatState;
                if (pcs != null)
                {
                    int energy = (int)(CombatReflection.PcsEnergyField?.GetValue(pcs) ?? 0);
                    h = h * 31 + energy;
                }
                int hp = (int)(CombatReflection.CreatureHpField?.GetValue(player.Creature!) ?? 0);
                int block = (int)(CombatReflection.CreatureBlockField?.GetValue(player.Creature!) ?? 0);
                h = h * 31 + hp;
                h = h * 31 + block;
            }
            catch { /* defensive — partial hash is acceptable */ }

            var hand = NCombatRoom.Instance?.Ui?.Hand;
            if (hand != null)
            {
                // Visible holder count = effective hand size for our purposes.
                h = h * 31 + hand.ActiveHolders.Count;
                // Mix in card IDs so swapping cards (Skim discard + redraw at same
                // count) still bumps the hash.
                foreach (var holder in hand.ActiveHolders)
                {
                    var model = holder.CardNode?.Model;
                    if (model != null) h = h * 31 + model.Id.GetHashCode();
                }
            }
            return h;
        }
    }

    private static int BestTargetScore(SimCard c, SimState state)
    {
        if (c.Target == TargetType.AnyEnemy)
        {
            int best = int.MinValue;
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                if (!state.Enemies[i].IsAlive) continue;
                int s = PlanScorer.Score(c, i, state);
                if (s > best) best = s;
            }
            return best == int.MinValue ? 0 : best;
        }
        return PlanScorer.Score(c, -1, state);
    }

    private void AttachOrUpdateLabel(NCard ncard, int score, bool isBest)
    {
        var label = ncard.GetNodeOrNull<Label>(LabelName);
        bool fresh = false;
        if (label == null)
        {
            label = new Label
            {
                Name = LabelName,
                ZIndex = 100,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Black outline so the score reads clearly against card art behind it.
            label.AddThemeConstantOverride("outline_size", 8);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
            ncard.AddChild(label);
            fresh = true;
        }

        // Position the label as a full-width band anchored to the TOP of the
        // NCard, offset downward to land just below the title banner.
        //
        // Why anchor-based and not `title.Position + title.Size`:
        //   • %Title isn't a direct child of NCard — it lives several layers
        //     deep (Body / banner container / …). title.Position is the
        //     *immediate-parent-local* coord, not NCard-local, so adding it to
        //     our label (which is NCard's direct child) misplaces the label
        //     by whatever offset those intermediate containers have.
        //   • NCard's own origin may be centred (cards rotate/scale around
        //     centre for animations) — Position(0,0) is not guaranteed to be
        //     the top-left corner.
        //   • Anchors are stretch-based: AnchorLeft=0/AnchorRight=1 with
        //     OffsetLeft=OffsetRight=0 makes the label exactly as wide as the
        //     NCard, regardless of origin. AnchorTop=0 with OffsetTop=N
        //     places the top of the label exactly N pixels below NCard's top
        //     edge — origin-independent.
        //
        // Direct Position/Size (no anchor stretch). NCard's local coordinate
        // origin determines what (0,0) means:
        //   • Top-left origin → label sits at the top edge of the card.
        //   • Centre origin (common for cards that scale/rotate around their
        //     midpoint) → (0,0) is the visual centre, so we need a negative
        //     y to climb above centre. NCard.defaultSize is (300, 422), so
        //     the top edge in centre-origin space is y ≈ -211.
        //
        // v0.1 of the overlay tries top-left first. If the user still sees
        // the label centred, switch CenterOrigin to true and the label drops
        // to the top edge via a negative offset.
        const float labelWidth = 300f;
        const float labelHeight = 36f;
        const bool CenterOrigin = true;  // flip to true if labels appear centred
        float topY = CenterOrigin ? -labelHeight * 0.5f - 130f : 0f;
        // Slight rightward shift (20 px) so the label clears the cost-orb on the
        // left edge of the card.
        float leftX = CenterOrigin ? -labelWidth * 0.5f + 20f : 0f;
        label.AnchorLeft = 0f;  label.AnchorRight = 0f;
        label.AnchorTop = 0f;   label.AnchorBottom = 0f;
        label.OffsetLeft = 0f;  label.OffsetRight = 0f;
        label.OffsetTop = 0f;   label.OffsetBottom = 0f;
        label.Position = new Vector2(leftX, topY);
        label.Size = new Vector2(labelWidth, labelHeight);

        // sign-on-positive format: "+247", "-12", "0".
        label.Text = score.ToString("+0;-0;0");
        label.AddThemeFontSizeOverride("font_size", isBest ? 26 : 20);
        label.AddThemeColorOverride("font_color", ColorFor(score, isBest));

        if (fresh || !_labeledCards.Contains(ncard))
            _labeledCards.Add(ncard);
    }

    private static Color ColorFor(int score, bool isBest)
    {
        if (isBest) return new Color(1f, 0.85f, 0.35f);          // gold — top pick
        if (score >= 200) return new Color(0.55f, 1f, 0.55f);    // bright green
        if (score >= 0)   return new Color(0.95f, 0.95f, 0.7f);  // pale yellow
        return new Color(1f, 0.55f, 0.55f);                      // red — net-negative
    }

    private static void RemoveLabelFrom(NCard? ncard)
    {
        if (ncard == null || !GodotObject.IsInstanceValid(ncard)) return;
        var label = ncard.GetNodeOrNull<Label>(LabelName);
        if (label != null) label.QueueFree();
    }

    private void ClearAllLabels()
    {
        for (int i = _labeledCards.Count - 1; i >= 0; i--)
            RemoveLabelFrom(_labeledCards[i]);
        _labeledCards.Clear();
    }
}
