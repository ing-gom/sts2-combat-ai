using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Per-character / per-pool-filter static value distribution of the STS2 card
/// pool. Generated offline by <c>scripts/build_pool_means.py</c>, embedded as
/// <c>Sts2CombatAI.pool_means.json</c>, loaded once at first use.
///
/// Consumed by <see cref="EffectSynergy.ApplyCardGen"/> for Level 4 pool-based
/// random cards (WHITE_NOISE / DISCOVERY / CREATIVE_AI / HELLO_WORLD / SPLASH /
/// JACKPOT / CALL_OF_THE_VOID / LARGESSE / DISTRACTION / CASCADE etc.) so their
/// evaluation reflects the actual mean / top-of-3 / top-of-5 value of the
/// current character's pool rather than a per-card-id flat magnitude.
///
/// Empty character id, missing filter, or load failure all return <see cref="Empty"/>
/// — callers should test <c>summary.N == 0</c> and fall back to flat magnitudes.
/// </summary>
internal static class PoolMeans
{
    internal readonly struct PoolSummary
    {
        public int N { get; init; }
        public int Mean { get; init; }
        public int Top1Of3 { get; init; }
        public int Top1Of5 { get; init; }
    }

    public static readonly PoolSummary Empty = default;

    // character (upper) → filter (lower) → summary.
    private static readonly Dictionary<string, Dictionary<string, PoolSummary>> _byChar
        = new(StringComparer.OrdinalIgnoreCase);
    private static int _schemaVersion;
    private static bool _loaded;

    public static Action<string>? LogWarn { get; set; }

    public static int SchemaVersion
    {
        get { if (!_loaded) Load(); return _schemaVersion; }
    }

    /// <summary>
    /// Look up a pool summary for the given character + filter (e.g. "all",
    /// "power", "skill_free", "common", "colorless"). Returns <see cref="Empty"/>
    /// when the character isn't recognized or the filter isn't computed —
    /// callers must fall back to flat magnitudes when <c>summary.N == 0</c>.
    /// </summary>
    public static PoolSummary Get(string? characterId, string filter)
    {
        if (!_loaded) Load();
        if (string.IsNullOrEmpty(characterId)) return Empty;
        if (!_byChar.TryGetValue(characterId, out var byFilter)) return Empty;
        return byFilter.TryGetValue(filter, out var s) ? s : Empty;
    }

    private static void Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Sts2CombatAI.pool_means.json");
            if (stream == null)
            {
                LogWarn?.Invoke("pool_means.json embedded resource not found");
                _loaded = true;
                return;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("_schema", out var sv) && sv.ValueKind == JsonValueKind.Number)
                _schemaVersion = sv.GetInt32();

            if (!root.TryGetProperty("characters", out var chars)
                || chars.ValueKind != JsonValueKind.Object)
            {
                LogWarn?.Invoke("pool_means.json missing 'characters' object");
                _loaded = true;
                return;
            }

            foreach (var chProp in chars.EnumerateObject())
            {
                var inner = new Dictionary<string, PoolSummary>(StringComparer.OrdinalIgnoreCase);
                if (chProp.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var fProp in chProp.Value.EnumerateObject())
                {
                    var v = fProp.Value;
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    inner[fProp.Name] = new PoolSummary
                    {
                        N        = GetInt(v, "n"),
                        Mean     = GetInt(v, "mean"),
                        Top1Of3  = GetInt(v, "top1of3"),
                        Top1Of5  = GetInt(v, "top1of5"),
                    };
                }
                _byChar[chProp.Name] = inner;
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"PoolMeans load failed: {ex.Message}");
            _loaded = true; // don't retry
        }
    }

    private static int GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
    }
}
