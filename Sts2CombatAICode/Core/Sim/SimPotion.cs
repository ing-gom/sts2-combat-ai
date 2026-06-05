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
    BlockMult,    // FORTIFIER — gain block = current block × Amount (Fortifier ×2 → total triples).
                  // Value scales with CURRENT block, so it climbs as block cards are played first;
                  // the overlay's live re-eval makes "block up, then Fortifier" emerge naturally.
    // Enemy debuffs — applied to the target (or all enemies for AllEnemies potions). Their value
    // mostly emerges from the continuation (the follow-up attacks hit harder / the enemy hits
    // softer), the same lookahead pattern AttackTriple uses.
    EnemyVuln,    // VULNERABLE_POTION — +Amount Vulnerable (target takes +50% from us)
    EnemyWeak,    // WEAK_POTION / POTION_OF_BINDING — +Amount Weak (enemy attacks −25%)
    EnemyPoison,  // POISON_POTION — +Amount Poison (turn-based damage-over-time on the enemy)
    HealPercent,  // BLOOD_POTION — heal Amount% of max HP
    // Self-defence / utility powers the sim already tracks. Value comes from the prevented
    // incoming damage (Intangible/Buffer/Plating/Thorns) or the heal (Regen).
    SelfIntangible, // GHOST_IN_A_JAR — Amount turns capping every incoming hit to 1
    SelfBuffer,     // LUCKY_TONIC — Amount full-hit cancels (BufferPower)
    SelfPlating,    // HEART_OF_IRON — +Amount block at end of each turn (PlatingPower)
    SelfThorns,     // LIQUID_BRONZE — reflect Amount per hit taken (ThornsPower)
    SelfRegen,      // REGEN_POTION — heal Amount/turn, decaying (RegenPower)
    // Enemy control. EnemyDoom is modeled as a delayed damage tick (sim applies DoomAmount as DoT);
    // EnemyStrengthDown lowers the target's attack output (defensive).
    EnemyDoom,         // POTION_OF_DOOM — +Amount Doom (turn-end damage-over-time on the enemy)
    EnemyStrengthDown, // SHACKLING_POTION / BEETLE_JUICE — −Amount enemy Strength (softer attacks)
    // Card draw — injects Amount deck-average cards into hand so the continuation can play them.
    Draw,           // SWIFT_POTION / BOTTLED_POTENTIAL / SNECKO_OIL / CLARITY / LIQUID_MEMORIES / DROPLET
    DrawExhaustHand,// GLOWWATER_POTION — exhaust current hand, then draw Amount (net card swing)
    // Card generation — injects Amount NEW cards into hand (no pile drain). Generated cards are
    // chosen-from-a-pool (usually decent); modeled conservatively as deck-average / shiv placeholders.
    GenerateCards,  // ATTACK/SKILL/POWER/COLORLESS_POTION (1) / COSMIC_CONCOCTION (3) — avg cards
    GenerateShivs,  // CUNNING_POTION (shivs) / POT_OF_GHOULS (souls ≈ 0-cost small attacks)
    MaxHpGain,      // FRUIT_JUICE — +Amount max HP (and heal Amount)
    // Character resources.
    GainStars,      // STAR_POTION — +Amount stars (Regent; unlocks star-cost cards in the continuation)
    OrbCapacity,    // POTION_OF_CAPACITY — +Amount orb slots (Defect)
    ChannelDark,    // ESSENCE_OF_DARKNESS — channel Dark orbs into every open slot (Defect)
    NextCardDouble, // DUPLICATOR — the next card is played twice (×2 its damage & block)
    UpgradeHand,    // BLESSING_OF_THE_FORGE (all) / SOLDIERS_STEW (Strikes) — upgrade hand cards
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
