using System;
using System.Collections.Generic;

namespace Sts2CombatAI.Diagnostics;

/// <summary>
/// Ring buffer of recent Vakuu plan steps for post-hoc debugging. Captures the score,
/// chosen card, target, and a compact snapshot summary. Limited to the last N entries
/// so memory stays bounded across long sessions.
///
/// v0.6.3 — entries now also carry runtime context (Turn, total enemy HP, behavioral
/// flag bits) so <see cref="DecisionLogPersister"/> can emit each entry as a
/// self-describing NDJSON line without re-parsing the BreakdownDetails string.
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

        // v0.6.3 — runtime context fields. Required by DecisionLogPersister to avoid
        // re-parsing BreakdownDetails. Set by VakuuExecutor at record time.
        public int Turn { get; init; }
        public int EnemyHpBefore { get; init; }
        public int PlayerHpBefore { get; init; }
        public int PlayerBlockBefore { get; init; }
        public bool LethalActive { get; init; }
        public bool IsFetchCard { get; init; }
        public int ComboLinks { get; init; }
        public string Character { get; init; } = "";

        // v0.7.41 — Outcome tracking. Captured AFTER the card is played, so
        // we can compare AI prediction (Score / Reason) with actual result.
        // Mutable (set, not init) so VakuuExecutor can update post-play.
        public int PlayerHpAfter { get; set; }
        public int PlayerBlockAfter { get; set; }
        public int EnemyHpAfterTotal { get; set; }
        // Damage dealt by THIS card play = EnemyHpBefore (total) - EnemyHpAfter.
        public int DamageDealt { get; set; }
        // HP loss FROM THIS CARD PLAY (HP_LOSS_SELF, Thorns reflect, etc.).
        // Excludes enemy-turn damage; that's recorded at TurnEnd.
        public int SelfDamage { get; set; }
        // True if this play killed an enemy (any enemy went from alive → dead).
        public bool KilledEnemy { get; set; }
        // v0.7.41 — TurnEnd marker. When true, this entry summarizes the turn
        // result (post-enemy-turn) rather than a card play. CardId="<TURN_END>".
        public bool IsTurnEnd { get; set; }
        public int TurnHpStart { get; set; }
        public int TurnHpEnd { get; set; }
        public int TurnDamageTaken { get; set; }
        public int TurnCardsPlayed { get; set; }
    }

    public static void Record(Entry e)
    {
        lock (_lock)
        {
            _entries.AddLast(e);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
    }

    /// <summary>
    /// v0.7.41 — Update the last-recorded entry's post-play outcome fields.
    /// Called by VakuuExecutor after CardCmd.AutoPlay completes, so the
    /// entry records both prediction (pre) and reality (post) for the same
    /// card play.
    /// </summary>
    public static void UpdateLastOutcome(int playerHpAfter, int playerBlockAfter,
                                          int enemyHpAfterTotal, int damageDealt,
                                          int selfDamage, bool killedEnemy)
    {
        lock (_lock)
        {
            var last = _entries.Last;
            if (last == null) return;
            var e = last.Value;
            // Don't overwrite a TurnEnd entry — that has different semantics.
            if (e.IsTurnEnd) return;
            e.PlayerHpAfter = playerHpAfter;
            e.PlayerBlockAfter = playerBlockAfter;
            e.EnemyHpAfterTotal = enemyHpAfterTotal;
            e.DamageDealt = damageDealt;
            e.SelfDamage = selfDamage;
            e.KilledEnemy = killedEnemy;
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

    /// <summary>v0.6.3 — snapshot of current buffer for persistence. Returns a copy
    /// so callers can iterate without holding the internal lock.</summary>
    public static Entry[] Snapshot()
    {
        lock (_lock)
        {
            var arr = new Entry[_entries.Count];
            _entries.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>v0.6.3 — clear the ring buffer. Called by the persister after a
    /// successful flush so the next combat starts with a fresh window.</summary>
    public static void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }

    public static int Count
    {
        get { lock (_lock) return _entries.Count; }
    }
}

