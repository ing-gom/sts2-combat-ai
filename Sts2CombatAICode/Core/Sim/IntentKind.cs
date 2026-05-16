namespace Sts2CombatAI.Sim;

/// <summary>
/// Coarse classification of STS2 enemy intent types (17 concrete subclasses bucketed).
/// Used by the planner to decide attack priority + threat assessment per enemy.
/// </summary>
internal enum IntentKind
{
    Other,        // unclassified / abstract
    Attack,       // AttackIntent / SingleAttackIntent / MultiAttackIntent
    DeathBlow,    // DeathBlowIntent — kill-or-be-killed urgency
    Defend,       // DefendIntent — enemy blocks, attacking is wasted this turn
    Buff,         // BuffIntent — buffs amplify next-turn threat, kill priority
    Debuff,       // DebuffIntent / CardDebuffIntent — we'll be weakened
    Heal,         // HealIntent — kill before they restore HP
    Summon,       // SummonIntent — kill before reinforcements
    Status,       // StatusIntent — adds status cards to our pile
    Inert,        // StunIntent / SleepIntent / EscapeIntent — 0 threat this turn
    Hidden,       // HiddenIntent — we can't see what's coming
    Unknown,      // UnknownIntent — explicit unknown
}

/// <summary>
/// Composite threat assessment per enemy. Higher = more dangerous.
/// Used by PlanScorer for attack-target prioritization.
/// </summary>
internal enum ThreatLevel
{
    None = 0,      // Inert (stun/sleep/escape) — ignore
    Low = 1,       // Defend / Status / Hidden — minor concern
    Medium = 2,    // Light attack / Debuff
    High = 3,      // Heavy attack / Heal / Summon
    Critical = 4,  // Buff (next-turn explosion) / DeathBlow / lethal-threatening
}
