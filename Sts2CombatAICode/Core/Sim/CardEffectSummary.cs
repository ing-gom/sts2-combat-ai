using System.Collections.Generic;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Numeric effects extracted from a card's CanonicalVars. The planner uses this for
/// damage-aware scoring (lethal detection, attack efficiency), block-aware scoring,
/// and per-power valuation.
///
/// Values are <c>BaseValue</c>-only (upgrade reflected, modifiers ignored). v0.3 may
/// upgrade to <c>PreviewValue</c> for Strength/Vulnerable inclusion.
/// </summary>
internal sealed record CardEffectSummary
{
    public int Damage { get; init; }                          // per-hit damage from DamageVar
    public int Hits { get; init; } = 1;                        // multi-hit count from RepeatVar
    public int Block { get; init; }                            // block from BlockVar
    public IReadOnlyDictionary<string, int> PowerApps { get; init; }
        = new Dictionary<string, int>();                       // PowerVar<T>: name → amount

    // v0.2.6 — specialized card metadata.
    public int EnergyGain { get; init; }                       // EnergyVar — direct energy gain on play
    public int DrawCount { get; init; }                        // CardsVar — direct draw count

    // v0.4 — Defect orb metadata.
    /// <summary>Number of times this card evokes the *front* orb (Dualcast=2, Quadcast=4, MultiCast=X+1).</summary>
    public int EvokeCount { get; init; }
    /// <summary>Number of orbs this card channels (Capacitor=X, most channelers=1).</summary>
    public int ChannelCount { get; init; }
    /// <summary>Kind of orb this card channels, when known (Frost/Lightning/Dark/Plasma/Glass). Unknown otherwise.</summary>
    public OrbKind ChannelKind { get; init; } = OrbKind.Unknown;

    // v0.6.7 — Named DynamicVars not surfaced via DamageVar / BlockVar / PowerVar.
    // Cards like DARK_SHACKLES / PIERCING_WAIL / ENFEEBLING_TOUCH use plain
    // `DynamicVar("StrengthLoss", N)` and NOT_YET / SPUR use `DynamicVar("Heal", N)`.
    // These don't fit any DamageVar/PowerVar bucket so we expose the raw amount
    // here for EffectSynergy STRENGTH_DOWN / HEAL handlers.
    /// <summary>Amount of enemy Strength reduction this card applies (DarkShackles 9, PiercingWail 6 AOE).</summary>
    public int StrengthDownAmount { get; init; }
    /// <summary>Amount of HP this card restores this turn (NotYet 10, Spur 5). 0 for permanent MaxHp-gain cards.</summary>
    public int HealAmount { get; init; }
    /// <summary>Amount of permanent MaxHp gain (BrightestFlame +1, Feed +3 on kill).</summary>
    public int MaxHpAmount { get; init; }

    /// <summary>
    /// v0.7.8 — Self-damage on play (DynamicVar named "HpLoss"). Ironclad self-
    /// harm cards expose this: BLOODLETTING 3, OFFERING 6, HEMOKINESIS 2,
    /// BREAKTHROUGH 1, BRAND 1, DEMONIC_SHIELD 1, BLOOD_WALL 2, plus the
    /// HAUNT Power for Necrobinder. EstimateCardPower deducts proportional
    /// to current HP — cheap at full HP, lethal at low HP.
    /// </summary>
    public int HpLossAmount { get; init; }

    /// <summary>
    /// v0.7.71 — Stars gained on play (DynamicVar "Stars": GLOW 1, VENERATE 2,
    /// ROYAL_GAMBLE 9, etc.). Player Regent star resource. The simulator
    /// applies this so depth-2 lookahead correctly unlocks star-cost cards
    /// (FALLING_STAR star_cost 2, DEVASTATE 4, etc.).
    /// </summary>
    public int StarsGain { get; init; }

    /// <summary>
    /// v0.7.71 — Star cost requirement to play (star_cost field). 0 for
    /// most cards. Star-cost cards (FALLING_STAR=2, COMET=5 etc.) are
    /// filtered from candidates when PlayerStars &lt; this value.
    /// </summary>
    public int StarCost { get; init; }

    public int TotalDamage => Damage * Hits;

    /// <summary>True if playing this card eventually grants energy (now or next turn).</summary>
    public bool IsEnergyGainCard =>
        EnergyGain > 0
        || PowerApps.ContainsKey("EnergyNextTurnPower")
        || PowerApps.ContainsKey("EnergizedPower");

    /// <summary>True if playing this card draws additional cards (now or next turn).</summary>
    public bool IsDrawCard =>
        DrawCount > 0
        || PowerApps.ContainsKey("DrawCardsNextTurnPower")
        || PowerApps.ContainsKey("DrawCardPower");

    public static readonly CardEffectSummary Empty = new();
}
