using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Sts2CombatAI.Diagnostics;

/// <summary>
/// v0.10 — Streaming writer for NDJSON decision logs. One file per combat,
/// opened at Vakuu turn entry and appended on every plan step so a crash or
/// abnormal exit after a boss kill still leaves the entire combat on disk.
///
/// Pattern: each entry sits in <see cref="_pending"/> until the NEXT
/// <see cref="AppendEntry"/> call (or <see cref="CloseForCombat"/>). This
/// "commit on next append" defers serialization just long enough for
/// <c>DecisionLog.UpdateLastOutcome</c> to mutate the entry's post-play
/// fields — both ring buffer and persister hold the SAME object reference,
/// so the outcome update lands in the eventual disk write automatically.
///
/// Crash semantics: pending entry is lost on crash; all earlier entries are
/// safely on disk (AutoFlush=true). Previous design (FlushIfPending) lost the
/// entire combat if the process died before the combat-end hook — which is
/// exactly what happened to the Soul Fysh boss log on 2026-05-20.
///
/// Output: <c>{user_data}/Sts2CombatAI/decision_log/{timestamp}_{floor}_{character}_{id}.ndjson</c>.
/// Rotation: keeps most recent <see cref="MaxFiles"/> on <see cref="CloseForCombat"/>.
/// </summary>
internal static class DecisionLogPersister
{
    private const string SubDir = "decision_log";
    private const int MaxFiles = 200;

    private static string? _dir;
    private static bool _initialized;
    private static bool _enabled = true;

    // Streaming state — set between OpenForCombat and CloseForCombat.
    private static StreamWriter? _writer;
    private static string? _currentPath;
    private static DecisionLog.Entry? _pending;
    private static int _entriesCommitted;

    /// <summary>Set false from ModConfig to disable persistence at runtime.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>True while a combat NDJSON file is open for writing.
    /// UI badges read this to confirm the persister is armed.</summary>
    public static bool IsOpen => _writer != null;

    /// <summary>Number of entries that have actually been flushed to disk
    /// in the currently-open file. Excludes the pending (uncommitted) entry.
    /// Resets to 0 on each <see cref="OpenForCombat"/>.</summary>
    public static int EntriesCommitted => _entriesCommitted;

    /// <summary>File name (no directory) of the currently-open NDJSON, or null.</summary>
    public static string? CurrentFileName => _currentPath is null ? null : Path.GetFileName(_currentPath);

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
    /// Open a new NDJSON file for this combat. Closes any previous file
    /// safely (committing its pending entry). Call on Vakuu Play entry —
    /// e.g. when <c>LastPlannedTurnRound == -1</c> in VakuuExecutor.
    /// </summary>
    public static void OpenForCombat(string character, int floorNumber, string combatId)
    {
        if (!_enabled || _dir == null) return;

        // Defensive: a previous combat that never reached CloseForCombat
        // (game crash before, mod reload mid-combat) — flush whatever's
        // pending and close it before opening the new file.
        CloseForCombat();

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeId = Sanitize(combatId);
            var safeChar = Sanitize(character);
            var fname = $"{stamp}_F{floorNumber:D2}_{safeChar}_{safeId}.ndjson";
            _currentPath = Path.Combine(_dir, fname);
            // AutoFlush=true so each WriteLine hits the OS write buffer
            // immediately — no manual Flush needed per step.
            _writer = new StreamWriter(_currentPath, append: false, Encoding.UTF8) { AutoFlush = true };
            _pending = null;
            _entriesCommitted = 0;
            MainFile.Logger.Info($"[CombatAI] DecisionLog opened: {fname}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] DecisionLog open failed: {ex.Message}");
            _writer = null;
            _currentPath = null;
        }
    }

    /// <summary>
    /// Append a decision entry to the open file. The entry is held in
    /// <see cref="_pending"/> until the NEXT call to AppendEntry or
    /// CloseForCombat — this lets <c>DecisionLog.UpdateLastOutcome</c>
    /// mutate the entry's outcome fields (same object reference) before
    /// serialization.
    ///
    /// No-op when persistence is disabled or no file is open.
    /// </summary>
    public static void AppendEntry(DecisionLog.Entry entry)
    {
        if (!_enabled || _writer == null) return;
        CommitPending();
        _pending = entry;
    }

    /// <summary>
    /// Close the current combat file. Commits the pending entry, flushes
    /// the writer, rotates old files. Safe to call multiple times.
    /// </summary>
    public static void CloseForCombat()
    {
        if (_writer == null) { _pending = null; return; }

        try
        {
            CommitPending();
            _writer.Flush();
            _writer.Dispose();
            var path = _currentPath;
            _writer = null;
            _currentPath = null;
            RotateOldFiles();
            if (path != null)
                MainFile.Logger.Info($"[CombatAI] DecisionLog closed: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] DecisionLog close failed: {ex.Message}");
            _writer = null;
            _currentPath = null;
            _pending = null;
        }
    }

    private static void CommitPending()
    {
        if (_pending == null || _writer == null) return;
        try
        {
            _writer.WriteLine(SerializeEntry(_pending));
            _entriesCommitted++;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] DecisionLog write failed: {ex.Message}");
        }
        _pending = null;
    }

    /// <summary>
    /// v0.6.3 backward-compat shim — older call sites (combat-end hooks not
    /// yet migrated to OpenForCombat / CloseForCombat) still call this.
    /// Treats the call as "drain whatever's open and close." No-op when no
    /// file is open (the streaming path already handles that case).
    /// </summary>
    public static void FlushIfPending(string character, int floorNumber, string seedOrCombatId)
    {
        CloseForCombat();
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
        // v0.7.62 — Opportunity cost
        Append(sb, "alternatives", e.AlternativeCards); sb.Append(',');
        AppendInt(sb, "runner_up_delta", e.RunnerUpDelta); sb.Append(',');
        Append(sb, "snapshot", e.SnapshotSummary); sb.Append(',');
        Append(sb, "breakdown", e.BreakdownDetails); sb.Append(',');
        // v0.10 — Per-target breakdowns for AnyEnemy attacks. Empty for
        // self/AOE targeting. See DecisionLog.Entry comment for format.
        Append(sb, "target_breakdowns", e.TargetBreakdowns);
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
