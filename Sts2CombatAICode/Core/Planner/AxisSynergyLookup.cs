using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.7.20 — Loads <c>role_needs.json</c> (embedded from CardAdvisor's data
/// directory) and exposes the per-axis weighted role-need lookup used by
/// <see cref="BuildSynergy"/>.
///
/// The single source of truth for cross-mod axis synergy is
/// <c>Sts2CardAdvisorCode/Data/role_needs.json</c>. CombatAI embeds a copy
/// so the runtime doesn't depend on CardAdvisor being installed; the copy
/// must be re-synced when CardAdvisor's source changes (manual cp until a
/// build-time sync is wired).
///
/// Schema (per CardAdvisor's AxisSynergyCatalog convention):
/// <code>
/// {
///   "POISON_PRODUCER": [
///     { "role": "POISON_CONSUMER", "w": 2.5, "label": "독 수요" },
///     { "role": "DRAW",            "w": 0.8, "label": "순환 지원" },
///     ...
///   ],
///   "_comment": "ignored"
/// }
/// </code>
/// Entries may carry <c>requires_with</c> (AND-condition: only fires if
/// another axis is also in hand) or <c>mutex_group</c> (within group, only
/// the top-weight match contributes — used for tiered cost-enabler
/// patterns). Comments (keys prefixed with <c>_</c>) are filtered out.
/// </summary>
internal static class AxisSynergyLookup
{
    internal readonly struct RoleNeed
    {
        public string Role { get; init; }
        public double Weight { get; init; }
        public string? RequiresWith { get; init; }
        public string? MutexGroup { get; init; }
    }

    private static readonly Dictionary<string, List<RoleNeed>> _needs
        = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static Action<string>? LogWarn { get; set; }

    /// <summary>
    /// Returns the role-need list for <paramref name="axis"/>. Empty when
    /// the axis isn't in the catalog or the loader failed.
    /// </summary>
    public static IReadOnlyList<RoleNeed> NeedsFor(string axis)
    {
        if (!_loaded) Load();
        return _needs.TryGetValue(axis, out var v) ? v : System.Array.Empty<RoleNeed>();
    }

    public static int AxisCount
    {
        get { if (!_loaded) Load(); return _needs.Count; }
    }

    private static void Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Sts2CombatAI.role_needs.json");
            if (stream == null)
            {
                LogWarn?.Invoke("role_needs.json embedded resource not found");
                _loaded = true;
                return;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LogWarn?.Invoke("role_needs.json root is not an object");
                _loaded = true;
                return;
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;

                var list = new List<RoleNeed>();
                foreach (var entry in prop.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string role = "";
                    double w = 0;
                    string? requiresWith = null;
                    string? mutexGroup = null;
                    foreach (var f in entry.EnumerateObject())
                    {
                        switch (f.Name)
                        {
                            case "role":          role = f.Value.GetString() ?? ""; break;
                            case "w":             w = f.Value.GetDouble(); break;
                            case "requires_with": requiresWith = f.Value.GetString(); break;
                            case "mutex_group":   mutexGroup = f.Value.GetString(); break;
                            // "label" is informational only — ignored at runtime.
                        }
                    }
                    if (!string.IsNullOrEmpty(role) && w != 0)
                    {
                        list.Add(new RoleNeed
                        {
                            Role = role,
                            Weight = w,
                            RequiresWith = requiresWith,
                            MutexGroup = mutexGroup,
                        });
                    }
                }
                if (list.Count > 0)
                    _needs[prop.Name] = list;
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"AxisSynergyLookup load failed: {ex.Message}");
            _loaded = true; // don't retry
        }
    }
}
