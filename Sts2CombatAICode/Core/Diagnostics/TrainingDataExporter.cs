using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Planner;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Diagnostics;

/// <summary>
/// v0.10 (Phase 5) — Dense per-step training data exporter for offline AI
/// learning workflows. Different from <see cref="DecisionLogPersister"/>:
///   • DecisionLogPersister: human-readable summary of the CHOSEN play with
///     top-3 alternatives. One entry per step.
///   • TrainingDataExporter: for each step, every (card, target) candidate
///     with the full PlanScorer breakdown. Enables training a tuner to map
///     "weight change → decision change" without re-running the planner.
///
/// File path: {user_data}/Sts2CombatAI/training_data/{timestamp}_F{floor}_{char}_{id}.ndjson
/// Cost: ~5-10 candidates × ~3-5 alive enemies = 15-50 Breakdown() calls per
/// step. Significant overhead — disabled by default; set <see cref="Enabled"/>
/// = true at runtime when collecting training data.
/// </summary>
internal static class TrainingDataExporter
{
    private const string SubDir = "training_data";
    private const int MaxFiles = 100;

    private static string? _dir;
    private static bool _initialized;
    private static bool _enabled = false;

    private static StreamWriter? _writer;
    private static string? _currentPath;

    /// <summary>Toggle from runtime — when false, all Record calls no-op.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Install()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var dir = Path.Combine(OS.GetUserDataDir(), "Sts2CombatAI", SubDir);
            Directory.CreateDirectory(dir);
            _dir = dir;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] training-data init failed: {ex.Message}");
        }
    }

    public static void OpenForCombat(string character, int floorNumber, string combatId)
    {
        if (!_enabled || _dir == null) return;
        CloseForCombat();
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeId = Sanitize(combatId);
            var safeChar = Sanitize(character);
            var fname = $"{stamp}_F{floorNumber:D2}_{safeChar}_{safeId}.ndjson";
            _currentPath = Path.Combine(_dir, fname);
            _writer = new StreamWriter(_currentPath, append: false, Encoding.UTF8) { AutoFlush = true };
            MainFile.Logger.Info($"[CombatAI] TrainingData opened: {fname}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] TrainingData open failed: {ex.Message}");
            _writer = null;
            _currentPath = null;
        }
    }

    /// <summary>
    /// Record a per-step training entry containing the full breakdown of
    /// every candidate considered at this decision point. The chosen
    /// (cardId, targetIdx) is flagged in the candidate list.
    /// </summary>
    public static void RecordStep(SimState state, int turn, int step, string playstyle,
        string character, string chosenCardId, int chosenTargetIdx,
        PlanScorerWeights weights,
        IEnumerable<(SimCard card, int targetIdx)> candidates)
    {
        if (!_enabled || _writer == null) return;
        try
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            AppendStr(sb, "ts", DateTime.Now.ToString("O")); sb.Append(',');
            AppendInt(sb, "turn", turn); sb.Append(',');
            AppendInt(sb, "step", step); sb.Append(',');
            AppendStr(sb, "playstyle", playstyle); sb.Append(',');
            AppendStr(sb, "character", character); sb.Append(',');
            AppendStr(sb, "chosen_card_id", chosenCardId); sb.Append(',');
            AppendInt(sb, "chosen_target_idx", chosenTargetIdx); sb.Append(',');
            AppendStr(sb, "snapshot", StateSnapshotter.FormatForLog(state)); sb.Append(',');
            sb.Append("\"candidates\":[");
            bool first = true;
            foreach (var (card, targetIdx) in candidates)
            {
                if (!first) sb.Append(','); first = false;
                var bd = PlanScorer.Breakdown(card, targetIdx, state, weights);
                bool isChosen = card.Id == chosenCardId && targetIdx == chosenTargetIdx;
                sb.Append('{');
                AppendStr(sb, "card_id", card.Id); sb.Append(',');
                AppendInt(sb, "target_idx", targetIdx); sb.Append(',');
                AppendBool(sb, "chosen", isChosen); sb.Append(',');
                AppendInt(sb, "total", bd.Total); sb.Append(',');
                AppendInt(sb, "base", bd.Base); sb.Append(',');
                AppendInt(sb, "effect", bd.Effect); sb.Append(',');
                AppendInt(sb, "target_bonus", bd.TargetBonus); sb.Append(',');
                AppendInt(sb, "threat_bonus", bd.ThreatBonus); sb.Append(',');
                AppendStr(sb, "category", bd.Category); sb.Append(',');
                AppendStr(sb, "details", bd.Details);
                sb.Append('}');
            }
            sb.Append("]}");
            _writer.WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] TrainingData write failed: {ex.Message}");
        }
    }

    public static void CloseForCombat()
    {
        if (_writer == null) return;
        try
        {
            _writer.Flush();
            _writer.Dispose();
            var path = _currentPath;
            _writer = null;
            _currentPath = null;
            RotateOldFiles();
            if (path != null)
                MainFile.Logger.Info($"[CombatAI] TrainingData closed: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] TrainingData close failed: {ex.Message}");
            _writer = null;
            _currentPath = null;
        }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) < 0 && c != ' ' ? c : '_');
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    private static void AppendStr(StringBuilder sb, string k, string v)
        => sb.Append('"').Append(k).Append("\":\"").Append(Escape(v)).Append('"');
    private static void AppendInt(StringBuilder sb, string k, int v)
        => sb.Append('"').Append(k).Append("\":").Append(v);
    private static void AppendBool(StringBuilder sb, string k, bool v)
        => sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false");

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void RotateOldFiles()
    {
        if (_dir == null) return;
        try
        {
            var files = Directory.GetFiles(_dir, "*.ndjson")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            for (int i = MaxFiles; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}
