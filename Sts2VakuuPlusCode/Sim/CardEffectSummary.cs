using System.Collections.Generic;

namespace Sts2VakuuPlus.Sim;

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
