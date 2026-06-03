namespace Sts2CombatAI.Sim;

/// <summary>
/// 2026-06-04 — Potion modeled as a first-class action in the planner lookahead.
/// Captured by StateSnapshotter from the live <c>Player.Potions</c>; applied by
/// <see cref="AnalyticalSimulator.ApplyPotionUse"/> so depth-N can evaluate
/// card→potion sequences (e.g. play a block card, then an amplifier potion).
/// Only the impactful additive kinds are modeled precisely; everything else is
/// <see cref="PotionKind.Other"/> and treated as a no-op (safe — never invents value).
/// </summary>
internal enum PotionKind
{
    Other = 0,
    Block,        // BLOCK_POTION — gain Amount block
    Heal,         // heal Amount HP
    Damage,       // FIRE_POTION etc. — deal Amount to a target (or all)
    Strength,     // STRENGTH_POTION — +Amount Strength
    Dexterity,    // DEXTERITY_POTION — +Amount Dexterity
    Focus,        // FOCUS_POTION — +Amount Focus
    Energy,       // ENERGY_POTION — +Amount energy this turn
    AttackTriple, // GIGANTIFICATION_POTION — next Attack card deals ×3 damage
}

internal sealed record SimPotion
{
    public required string Id { get; init; }
    public required PotionKind Kind { get; init; }
    /// <summary>Primary numeric magnitude (block, heal, damage, stat amount, ...).</summary>
    public int Amount { get; init; }
    /// <summary>Game TargetType string (AnyEnemy / AllEnemies / Self / None) for targeted potions.</summary>
    public string Target { get; init; } = "";

    public bool TargetsSingleEnemy => Target == "AnyEnemy";
    public bool TargetsAllEnemies => Target == "AllEnemies";
}
