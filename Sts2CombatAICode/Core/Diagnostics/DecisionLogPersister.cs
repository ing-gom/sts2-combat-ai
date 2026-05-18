using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Sts2CombatAI.Diagnostics;

/// <summary>
/// v0.6.3 — flushes the in-memory <see cref="DecisionLog"/> ring buffer to NDJSON
/// files on disk so post-hoc analysis tools (scripts/parse_decision_log.py) can
/// aggregate decisions across combats.
///
/// Output: <c>{user_data}/Sts2CombatAI/decision_log/{timestamp}_{floor}_{seed}.ndjson</c>.
/// Each line is one decision entry as a single JSON object; one file per combat.
///
/// Rotation: keeps the most recent <see cref="MaxFiles"/> files so disk usage stays
/// bounded across long play sessions. Older files are removed at the same flush
/// point as the new one is written.
///
/// Lifecycle: install once at mod startup. Call <see cref="FlushIfPending"/> from
/// the combat-end transition (or before mod unload) to drain the buffer. Failures
/// are non-fatal; diagnostics shouldn't crash the game.
/// </summary>
internal static class DecisionLogPersister
{
    private const string SubDir = "decision_log";
    private const int MaxFiles = 200;

    private static string? _dir;
    private static bool _initialized;
    private static bool _enabled = true;

    /// <summary>Set false from ModConfig to disable persistence at runtime.</summary>
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
            MainFile.Logger.Warn($"[CombatAI] decision-log persistence init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Write the current ring buffer to a new NDJSON file, then clear the buffer.
    /// No-op when persistence is disabled or the buffer is empty. Safe to call
    /// multiple times — only flushes when at least one new entry has been recorded.
    /// </summary>
    /// <param name="character">Player character name (e.g. "Vakuu" / "Necrobinder").</param>
    /// <param name="floorNumber">Current ascension / floor for filename disambiguation.</param>
    /// <param name="seedOrCombatId">Best-effort unique combat id for the filename.</param>
    public static void FlushIfPending(string character, int floorNumber, string seedOrCombatId)
    {
        if (!_enabled || _dir == null) return;
        var entries = DecisionLog.Snapshot();
        if (entries.Length == 0) return;

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeId = Sanitize(seedOrCombatId);
            var safeChar = Sanitize(character);
            var fname = $"{stamp}_F{floorNumber:D2}_{safeChar}_{safeId}.ndjson";
            var path = Path.Combine(_dir, fname);

            var sb = new StringBuilder(entries.Length * 256);
            foreach (var e in entries) sb.AppendLine(SerializeEntry(e));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            DecisionLog.Clear();
            RotateOldFiles();
            MainFile.Logger.Info($"[CombatAI] DecisionLog flushed: {entries.Length} entries -> {fname}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] DecisionLog flush failed: {ex.Message}");
        }
    }

    /// <summary>Sanitize a string for safe use in a file name.</summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) < 0 && c != ' ' ? c : '_');
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    /// <summary>Hand-written JSON serialization — small payload, no Newtonsoft / SJT
    /// dependency. Each entry one line; analyzer is tolerant of new fields.</summary>
    private static string SerializeEntry(DecisionLog.Entry e)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        Append(sb, "ts", e.Timestamp.ToString("O")); sb.Append(',');
        AppendInt(sb, "step", e.Step); sb.Append(',');
        AppendInt(sb, "turn", e.Turn); sb.Append(',');
        Append(sb, "playstyle", e.Playstyle); sb.Append(',');
        Append(sb, "character", e.Character); sb.Append(',');
        Append(sb, "card_id", e.CardId); sb.Append(',');
        Append(sb, "target", e.TargetName); sb.Append(',');
        AppendInt(sb, "score", e.Score); sb.Append(',');
        AppendInt(sb, "enemy_hp_before", e.EnemyHpBefore); sb.Append(',');
        AppendInt(sb, "player_hp_before", e.PlayerHpBefore); sb.Append(',');
        AppendInt(sb, "player_block_before", e.PlayerBlockBefore); sb.Append(',');
        AppendBool(sb, "lethal_active", e.LethalActive); sb.Append(',');
        AppendBool(sb, "fetch_card", e.IsFetchCard); sb.Append(',');
        AppendInt(sb, "combo_links", e.ComboLinks); sb.Append(',');
        Append(sb, "reason", e.Reason); sb.Append(',');
        // v0.7.41 — Outcome fields. Captured after the card actually played
        // so the log shows AI prediction vs reality.
        AppendInt(sb, "player_hp_after", e.PlayerHpAfter); sb.Append(',');
        AppendInt(sb, "player_block_after", e.PlayerBlockAfter); sb.Append(',');
        AppendInt(sb, "enemy_hp_after", e.EnemyHpAfterTotal); sb.Append(',');
        AppendInt(sb, "damage_dealt", e.DamageDealt); sb.Append(',');
        AppendInt(sb, "self_damage", e.SelfDamage); sb.Append(',');
        AppendBool(sb, "killed_enemy", e.KilledEnemy); sb.Append(',');
        AppendBool(sb, "is_turn_end", e.IsTurnEnd); sb.Append(',');
        AppendInt(sb, "turn_hp_start", e.TurnHpStart); sb.Append(',');
        AppendInt(sb, "turn_hp_end", e.TurnHpEnd); sb.Append(',');
        AppendInt(sb, "turn_damage_taken", e.TurnDamageTaken); sb.Append(',');
        AppendInt(sb, "turn_cards_played", e.TurnCardsPlayed); sb.Append(',');
        Append(sb, "snapshot", e.SnapshotSummary); sb.Append(',');
        Append(sb, "breakdown", e.BreakdownDetails);
        sb.Append('}');
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string val)
    {
        sb.Append('"').Append(key).Append("\":\"").Append(Escape(val)).Append('"');
    }

    private static void AppendInt(StringBuilder sb, string key, int val)
    {
        sb.Append('"').Append(key).Append("\":").Append(val);
    }

    private static void AppendBool(StringBuilder sb, string key, bool val)
    {
        sb.Append('"').Append(key).Append("\":").Append(val ? "true" : "false");
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
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
                try { files[i].Delete(); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] decision-log rotation failed: {ex.Message}");
        }
    }
}
