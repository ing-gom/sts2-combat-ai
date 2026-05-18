using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Sts2CombatAI.Sim;

/// <summary>
/// v0.7.11 — Lightweight projection of an allied creature (Necrobinder
/// skeletons / "Osty") for planner scoring. Mirrors the SimEnemy shape but
/// for player-side combatants: they have intent damage they apply to
/// enemies each turn, and they have HP that enemies may target (split-fire
/// — enemies otherwise focused on the player chew through ally HP instead).
///
/// Phase 3b scope: skeleton attack contribution to outgoing damage +
/// RemainingTurnsEstimator factoring. Enemy split-fire onto allies is
/// recognized but not subtracted from player threat in this pass.
/// </summary>
internal sealed record SimAlly
{
    public required int Hp { get; init; }
    public required int Block { get; init; }
    public required int IntentDamage { get; init; }
    public required int IntentRepeats { get; init; }
    public bool HasAttackIntent { get; init; }

    /// <summary>
    /// Class name (e.g. "Osty"). Useful for downstream consumers that want
    /// to scope behavior to specific summons.
    /// </summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>
    /// Nullable for testability — the live snapshotter always wires a real
    /// Creature reference here.
    /// </summary>
    public Creature? SourceRef { get; init; }

    public bool IsAlive => Hp > 0;
    public int TotalIntentDamage => IntentDamage * IntentRepeats;
}
