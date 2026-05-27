using System;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Onnx;

/// Phase C — single entry point the executor uses. Bundles `OnnxPolicy` and
/// `SimStateAdapter` and exposes a read-only `RecommendAction(SimState)`
/// that returns the model's pick (or null if the model isn't loaded).
///
/// Designed for dual-log mode: ActionPlanner is still authoritative, this
/// advisor just produces a comparison value so we can see whether the
/// RL-trained policy would agree with the heuristic.
internal static class OnnxAdvisor
{
    public sealed record Recommendation(int CardIndex, string CardId, bool IsEndTurn, string Reason, int TargetIndex = -1);

    private static OnnxPolicy? _policy;
    private static SimStateAdapter? _adapter;
    private static bool _initialized;
    private static bool _available;

    public static bool IsAvailable
    {
        get
        {
            Initialize();
            return _available;
        }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _policy = new OnnxPolicy();
            if (!_policy.IsAvailable)
            {
                _available = false;
                return;
            }
            _adapter = new SimStateAdapter();
            if (_adapter.FeatureDim != _policy.ObservationDim)
            {
                // Vocab / observation layout mismatch between mod build and exported model.
                // Disable rather than risk noisy bad output.
                MainFile.Logger.Warn(
                    $"[OnnxAdvisor] feature-dim mismatch: adapter={_adapter.FeatureDim}, "
                    + $"model={_policy.ObservationDim} — Onnx advice disabled");
                _available = false;
                return;
            }
            _available = true;
            MainFile.Logger.Info(
                $"[OnnxAdvisor] loaded — obsDim={_policy.ObservationDim} "
                + $"actionCount={_policy.ActionCount} vocab={_adapter.VocabSize}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[OnnxAdvisor] init failed: {ex.GetType().Name}: {ex.Message}");
            _available = false;
        }
    }

    public static Recommendation? Recommend(SimState state)
    {
        Initialize();
        if (!_available || _policy == null || _adapter == null) return null;

        try
        {
            var features = _adapter.Encode(state);
            var mask = _adapter.BuildMask(state, _policy.ActionCount);
            int action = _policy.PredictAction(features, mask);
            if (action < 0) return null;

            // 2026-05-27 F (action-space expansion): pick decode layout
            // based on the trained ONNX's output dim.
            int expandedEndTurn = _adapter.MaxHandSize * _adapter.MaxEnemies;
            bool isExpanded = _policy.ActionCount == expandedEndTurn + 1;

            if (isExpanded)
            {
                if (action == expandedEndTurn)
                    return new Recommendation(-1, "<EndTurn>", true, "ppo-EndTurn");
                int cardIdx = action / _adapter.MaxEnemies;
                int tgtIdx = action % _adapter.MaxEnemies;
                if (cardIdx >= state.Hand.Count)
                    return new Recommendation(cardIdx, "<OOB>", false, "ppo-out-of-hand", tgtIdx);
                var card = state.Hand[cardIdx];
                return new Recommendation(cardIdx, card.Id, false, "ppo-argmax", tgtIdx);
            }

            // Legacy card-only layout.
            if (action == _adapter.MaxHandSize)
                return new Recommendation(-1, "<EndTurn>", true, "ppo-EndTurn");
            if (action >= state.Hand.Count)
                return new Recommendation(action, "<OOB>", false, "ppo-out-of-hand");
            var legacyCard = state.Hand[action];
            return new Recommendation(action, legacyCard.Id, false, "ppo-argmax");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn(
                $"[OnnxAdvisor] inference threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
