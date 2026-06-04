using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Behavioral profiles for the planner. Each profile maps to a distinct
/// <see cref="PlanScorerWeights"/> preset; the user can cycle live via the
/// test button.
/// </summary>
internal enum Playstyle
{
    Defensive,   // minimize damage taken, block-heavy, kills deprioritized
    Balanced,    // v0.1.2 default
    Aggressive,  // attack-focused, accepts some damage to push kills
    Killer,      // maximum lethality, block largely ignored
    // 2026-06-04 — Auto is a META mode (kept LAST so Defensive..Killer int values stay stable for
    // persisted configs and the Cycle() mod-4 over the concrete styles). When selected, the
    // effective style is DERIVED from the current deck composition each combat — see
    // PlaystyleResolver. Opt-in via {userdata}/Sts2CombatAI/playstyle.json → {"playstyle":"Auto"}.
    Auto,
}

/// <summary>
/// 2026-06-04 — Resolves the EFFECTIVE playstyle for a given combat state. When the user's
/// selection is a concrete style it's returned as-is. When it's <see cref="Playstyle.Auto"/>
/// the style is DERIVED from the deck's offense/defense profile + archetype, so the planner
/// adapts to the CARDS the player actually holds rather than a fixed toggle or character id.
///
/// Why deck-derived (not character-derived): the same character plays very differently by deck
/// — a poison Silent wants patience, a shiv Silent wants aggression; a Barricade Ironclad turtles,
/// a Strength Ironclad races. The signal is the deck, not the hero.
///
/// Perf: DeckThroughput.Compute / ArchetypeDetector.Detect each scan all piles, and Resolve is
/// called on the hot scoring path, so the derived result is cached on a combat-stable signature
/// (deck size + character). It re-derives only when the deck size changes (card exhausted/added)
/// — within a turn and across normal plays it's a single field compare.
/// </summary>
internal static class PlaystyleResolver
{
    private static int _cacheSig = int.MinValue;
    private static Playstyle _cacheStyle = Playstyle.Balanced;

    public static Playstyle Resolve(SimState state)
    {
        var cur = PlaystyleState.Current;
        if (cur != Playstyle.Auto || state == null) return cur;

        int sig = DeckSignature(state);
        if (sig == _cacheSig) return _cacheStyle;
        _cacheStyle = Derive(state);
        _cacheSig = sig;
        return _cacheStyle;
    }

    private static int DeckSignature(SimState s)
    {
        int total = s.Hand.Count + s.DrawPile.Count + s.DiscardPile.Count;
        return unchecked(total * 397 ^ (s.CharacterId?.GetHashCode() ?? 0));
    }

    private static Playstyle Derive(SimState state)
    {
        var p = DeckThroughput.Compute(state);
        var (arch, _, _) = ArchetypeDetector.Detect(state);
        int dpt = p.AvgDamagePerTurn, bpt = p.AvgBlockPerTurn;

        Playstyle style;
        // Block-leaning deck (block archetype, or block per turn ≥ 1.5× damage) → defend by default.
        if (arch == ArchetypeDetector.Build.Block || (dpt > 0 && bpt >= dpt + dpt / 2))
            style = Playstyle.Defensive;
        // Burst / offense-leaning (a burst archetype, or damage ≥ 2× block) → push kills.
        // Killer is intentionally NOT auto-selected — it ignores block entirely and is too risky
        // to pick without the player explicitly opting into it.
        else if (arch is ArchetypeDetector.Build.Strength or ArchetypeDetector.Build.ShivStorm
                       or ArchetypeDetector.Build.ForgeBlade or ArchetypeDetector.Build.ExhaustBurst
                 || (dpt > 0 && dpt >= bpt * 2))
            style = Playstyle.Aggressive;
        // Scaling / poison / orb / soul decks and everything else → Balanced (patient default).
        else
            style = Playstyle.Balanced;

        PlaystyleState.LogCallback?.Invoke(
            $"[CombatAI] Auto playstyle → {style} (arch={arch}, dpt={dpt}, bpt={bpt})");
        return style;
    }
}

/// <summary>
/// Process-wide setting for the planner's playstyle. Mutated externally by mode
/// runtimes (e.g. the Vakuu mode's cycle button) and read by <see cref="PlanScorer"/>.
/// </summary>
internal static class PlaystyleState
{
    public static Playstyle Current { get; private set; } = Playstyle.Balanced;

    /// <summary>
    /// Optional logger hook. Wired by MainFile.Initialize so production builds get
    /// real Godot logs. Test builds leave it null → silent.
    /// </summary>
    public static System.Action<string>? LogCallback { get; set; }

    public static Playstyle Cycle()
    {
        Current = (Playstyle)(((int)Current + 1) % 4);
        LogCallback?.Invoke($"playstyle changed: {Current}");
        return Current;
    }

    public static void Set(Playstyle p)
    {
        if (Current != p)
        {
            Current = p;
            LogCallback?.Invoke($"playstyle set: {Current}");
        }
    }
}
