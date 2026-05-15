using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Sts2VakuuPlus.Data;

/// <summary>
/// Master card catalog generated from scripts/cards_catalog.json via
/// scripts/extract_card_triggers.py. Embedded as a resource so the mod doesn't
/// depend on user-side file paths.
///
/// Single source of truth for card-id-based classifications — eliminates the
/// hardcoded BoostCards list in SelectorMode.cs. Game patches that rename or
/// add cards only need the master catalog refreshed (headless-sync), not source
/// code changes.
/// </summary>
internal sealed class CardTriggerInfo
{
    public IReadOnlyList<string> Axes { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> BuildTags { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> PrimaryBuildTags { get; init; } = System.Array.Empty<string>();
    public bool UpgradeTrigger { get; init; }
    public bool FetchTrigger { get; init; }
    public bool Exhaust { get; init; }
    public bool Ethereal { get; init; }
    public bool Retain { get; init; }
    public bool Innate { get; init; }
}

internal static class CardCatalog
{
    private static readonly Dictionary<string, CardTriggerInfo> _cards = new(System.StringComparer.OrdinalIgnoreCase);
    private static string _version = "?";
    private static bool _loaded;

    public static string Version => _version;
    public static int Count => _cards.Count;

    static CardCatalog() => Load();

    public static CardTriggerInfo? Lookup(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        if (!_loaded) Load();
        // Catalog keys are like "CARD.APOTHEOSIS". The playing-card ID from
        // CardModel.Id.Entry is also that form, but defensive normalization.
        var key = cardId.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase)
            ? cardId
            : "CARD." + cardId.ToUpperInvariant();
        return _cards.TryGetValue(key, out var info) ? info : null;
    }

    private static void Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Sts2VakuuPlus.card_triggers.json");
            if (stream == null)
            {
                LogWarn?.Invoke("card_triggers.json embedded resource not found");
                _loaded = true;
                return;
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("version", out var v)) _version = v.GetString() ?? "?";

            if (!root.TryGetProperty("cards", out var cardsEl) || cardsEl.ValueKind != JsonValueKind.Object)
            {
                LogWarn?.Invoke("card_triggers.json missing 'cards' object");
                _loaded = true;
                return;
            }

            foreach (var entry in cardsEl.EnumerateObject())
            {
                IReadOnlyList<string> axes = entry.Value.TryGetProperty("axes", out var ax) && ax.ValueKind == JsonValueKind.Array
                    ? (IReadOnlyList<string>)ExtractStringArray(ax)
                    : System.Array.Empty<string>();

                List<string> allBuilds = new();
                List<string> primaryBuilds = new();
                if (entry.Value.TryGetProperty("builds", out var bd) && bd.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bd.EnumerateArray())
                    {
                        if (b.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            var tag = t.GetString();
                            if (string.IsNullOrEmpty(tag)) continue;
                            allBuilds.Add(tag);
                            if (b.TryGetProperty("role", out var r)
                                && r.ValueKind == JsonValueKind.String
                                && r.GetString() == "primary")
                            {
                                primaryBuilds.Add(tag);
                            }
                        }
                    }
                }

                var info = new CardTriggerInfo
                {
                    Axes = axes,
                    BuildTags = allBuilds,
                    PrimaryBuildTags = primaryBuilds,
                    UpgradeTrigger = GetBool(entry.Value, "upgrade_trigger"),
                    FetchTrigger = GetBool(entry.Value, "fetch_trigger"),
                    Exhaust = GetBool(entry.Value, "exhaust"),
                    Ethereal = GetBool(entry.Value, "ethereal"),
                    Retain = GetBool(entry.Value, "retain"),
                    Innate = GetBool(entry.Value, "innate"),
                };
                _cards[entry.Name] = info;
            }
            _loaded = true;
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"CardCatalog load failed: {ex.Message}");
            _loaded = true; // don't retry
        }
    }

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static List<string> ExtractStringArray(JsonElement arr)
    {
        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }

    /// <summary>Optional warning sink; wired by MainFile.Initialize.</summary>
    public static System.Action<string>? LogWarn { get; set; }
}
