namespace Sts2CombatAI;

// v0.9.7 — Test-only stub so source files that touch MainFile.Logger
// (PlanScorer.LogCalcDmgProbeOnce) can compile in the Tests project,
// which otherwise excludes the Godot-coupled MainFile.cs. Returns null
// so the production code's `MainFile.Logger?.Info(...)` is a no-op
// under test.
internal static class MainFile
{
    public static MegaCrit.Sts2.Core.Logging.Logger? Logger => null;
}
