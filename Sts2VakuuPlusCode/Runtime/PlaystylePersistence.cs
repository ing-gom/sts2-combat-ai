using System;
using System.IO;
using System.Text.Json;
using Godot;
using Sts2VakuuPlus.Planner;

namespace Sts2VakuuPlus.Runtime;

/// <summary>
/// Persists the current Playstyle selection across game restarts.
/// File: {user_data_dir}/Sts2VakuuPlus/playstyle.json.
///
/// Wired by MainFile.Initialize — calls Load() once at startup, then attaches a
/// LogCallback hook that also writes whenever Playstyle changes (via Cycle/Set).
/// Failures are non-fatal: persistence is a UX nicety, not a correctness requirement.
/// </summary>
internal static class PlaystylePersistence
{
    private const string FileName = "playstyle.json";

    private static string? _path;
    private static bool _initialized;

    public static void Install()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var dir = Path.Combine(OS.GetUserDataDir(), "Sts2VakuuPlus");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, FileName);
            Load();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VakuuPlus] playstyle persistence init failed: {ex.Message}");
        }
    }

    private static void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var text = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("playstyle", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrEmpty(s) && Enum.TryParse<Playstyle>(s, out var style))
                {
                    PlaystyleState.Set(style);
                    MainFile.Logger.Info($"[VakuuPlus] playstyle restored: {style}");
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VakuuPlus] playstyle load failed: {ex.Message}");
        }
    }

    public static void Save()
    {
        if (_path == null) return;
        try
        {
            var json = $"{{\"playstyle\":\"{PlaystyleState.Current}\"}}";
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VakuuPlus] playstyle save failed: {ex.Message}");
        }
    }
}
