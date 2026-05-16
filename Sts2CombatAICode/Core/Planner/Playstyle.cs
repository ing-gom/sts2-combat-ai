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
