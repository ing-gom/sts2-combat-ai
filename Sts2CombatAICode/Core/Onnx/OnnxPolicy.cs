using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Sts2CombatAI.Onnx;

/// Phase C — loads a PPO policy exported by sts2-combat-core
/// (python/export_onnx.py). Identical contract to that project's OnnxPolicy:
/// flat float32 observation in, action logits out, action mask applied
/// post-hoc by the caller.
///
/// Embedded as `Sts2CombatAI.ppo.onnx` so the mod ships standalone. If the
/// resource is missing (CI build without RL exports), `IsAvailable` returns
/// false and callers should fall back to the heuristic policy.
internal sealed class OnnxPolicy : IDisposable
{
    private readonly InferenceSession? _session;
    private readonly string _inputName = "";
    private readonly string _outputName = "";
    public int ObservationDim { get; }
    public int ActionCount { get; }
    public bool IsAvailable => _session != null;

    public OnnxPolicy()
    {
        var asm = typeof(OnnxPolicy).Assembly;
        using var stream = asm.GetManifestResourceStream("Sts2CombatAI.ppo.onnx");
        if (stream == null)
        {
            ObservationDim = 0;
            ActionCount = 0;
            return;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ms.ToArray(), opts);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        var inShape = _session.InputMetadata[_inputName].Dimensions;
        var outShape = _session.OutputMetadata[_outputName].Dimensions;
        ObservationDim = inShape[1];
        ActionCount = outShape[1];
    }

    public float[] Logits(float[] observation)
    {
        if (_session == null) throw new InvalidOperationException("ONNX model not loaded.");
        if (observation.Length != ObservationDim)
            throw new ArgumentException(
                $"Observation length {observation.Length} != model input dim {ObservationDim}");

        var input = new DenseTensor<float>(observation, new[] { 1, ObservationDim });
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_inputName, input),
        });
        var logitsTensor = results.First().AsTensor<float>();
        var logits = new float[ActionCount];
        for (int i = 0; i < ActionCount; i++) logits[i] = logitsTensor[0, i];
        return logits;
    }

    public int PredictAction(float[] observation, bool[]? mask = null)
    {
        var logits = Logits(observation);
        int best = -1;
        float bestVal = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
        {
            if (mask != null && !mask[i]) continue;
            if (logits[i] > bestVal) { bestVal = logits[i]; best = i; }
        }
        if (best < 0) return -1;
        return best;
    }

    public void Dispose() => _session?.Dispose();
}
