using System;
using System.Collections.Generic;

namespace Sts2CombatAI.Diagnostics;

/// <summary>
/// Ring buffer of recent Vakuu plan steps for post-hoc debugging. Captures the score,
/// chosen card, target, and a compact snapshot summary. Limited to the last N entries
/// so memory stays bounded across long sessions.
/// </summary>
internal static class DecisionLog
{
    private const int Capacity = 32;
    private static readonly LinkedList<Entry> _entries = new();
    private static readonly object _lock = new();

    public sealed class Entry
    {
        public DateTime Timestamp { get; init; }
        public int Step { get; init; }
        public string Playstyle { get; init; } = "";
        public string CardId { get; init; } = "";
        public string TargetName { get; init; } = "";
        public int Score { get; init; }
        public string Reason { get; init; } = "";
        public string SnapshotSummary { get; init; } = "";
        public string BreakdownDetails { get; init; } = "";
    }

    public static void Record(Entry e)
    {
        lock (_lock)
        {
            _entries.AddLast(e);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
    }

    /// <summary>Dump the entire buffer to the game logger.</summary>
    public static void Dump(Action<string> writer)
    {
        Entry[] snapshot;
        lock (_lock) { snapshot = new Entry[_entries.Count]; _entries.CopyTo(snapshot, 0); }
        writer($"[CombatAI] === DecisionLog dump ({snapshot.Length} entries) ===");
        foreach (var e in snapshot)
        {
            writer($"[CombatAI]   {e.Timestamp:HH:mm:ss} step={e.Step} style={e.Playstyle} "
                 + $"→ {e.CardId}@{e.TargetName} (score={e.Score}, {e.Reason})");
        }
    }

    public static int Count
    {
        get { lock (_lock) return _entries.Count; }
    }
}
