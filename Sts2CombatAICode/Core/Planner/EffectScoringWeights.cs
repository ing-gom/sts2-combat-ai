namespace Sts2CombatAI.Planner;

/// <summary>
/// Weights used by <see cref="EffectSynergy.EstimateCardPower"/> to convert a
/// <c>SimCard</c> into a context-free heuristic point value. Pulled out as a
/// single source of truth so the Python mirror (<c>scripts/_effect_scoring_weights.py</c>),
/// used by <c>scripts/build_pool_means.py</c>, references the same numbers.
///
/// "free use" = the card is played without paying its energy cost (auto-play,
/// X-cost, CATASTROPHE/CASCADE-style autoplay). Free-use weights are higher
/// because the card returns full damage/block without consuming a turn-slot.
///
/// When changing a weight, also bump <c>SCHEMA_VERSION</c> below and re-run
/// <c>python scripts/build_pool_means.py</c> so the embedded
/// <c>pool_means.json</c> stays in sync.
/// </summary>
internal static class EffectScoringWeights
{
    public const int SchemaVersion = 1;

    // Damage / block — per unit dealt or gained.
    public const int DamageFree   = 50;
    public const int DamageInHand = 35;
    public const int BlockFree    = 30;
    public const int BlockInHand  = 25;

    // Card-draw / energy-gain — same in both modes for draw, energy is much
    // more valuable when free (no slot cost).
    public const int Draw         = 70;
    public const int EnergyFree   = 130;
    public const int EnergyInHand = 60;

    // PowerApps go through PowerCatalog and are divided by these (heavy
    // context-free discount — we can't tell if target benefits / debuff lands).
    public const int PowerDivisorFree   = 5;
    public const int PowerDivisorInHand = 7;

    // Cost-based adjustments — only applied in-hand (free-use already paid 0).
    public const int Cost0Bonus       = 80;
    public const int Cost1Bonus       = 20;
    public const int Cost3PlusPenalty = -100;

    // Curse / Status — value of receiving the card.
    public const int CurseFree   = -100;
    public const int CurseInHand = -250;
}
