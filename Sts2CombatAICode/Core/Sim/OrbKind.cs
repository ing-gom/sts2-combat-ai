namespace Sts2CombatAI.Sim;

/// <summary>
/// Defect orb classification. Mirrors STS2's OrbModel subclasses (DarkOrb, FrostOrb,
/// GlassOrb, LightningOrb, PlasmaOrb). Used to track the per-slot composition of the
/// player's orb queue and to value channel / evoke decisions per-orb.
///
/// Passive trigger timing (turn-end vs turn-start) and evoke value differ per kind —
/// see OrbValueCatalog for the numeric model.
/// </summary>
internal enum OrbKind
{
    Unknown,
    Lightning,  // passive 3 dmg random / evoke 8 dmg random — turn-end
    Frost,      // passive 2 block self  / evoke 5 block self  — turn-end
    Dark,       // passive +6 to own evokeVal (accumulating) / evoke = evokeVal to weakest — turn-end
    Plasma,     // passive 1 energy / evoke 2 energy — fires at *next-turn start* (visually "end of my turn")
    Glass,      // passive 4 AOE (decays to 0) / evoke = passive×2 AOE — turn-end
}

internal static class OrbKindExtensions
{
    public static OrbKind FromClassName(string? className) => className switch
    {
        "LightningOrb" => OrbKind.Lightning,
        "FrostOrb"     => OrbKind.Frost,
        "DarkOrb"      => OrbKind.Dark,
        "PlasmaOrb"    => OrbKind.Plasma,
        "GlassOrb"     => OrbKind.Glass,
        _ => OrbKind.Unknown,
    };

    /// <summary>Short tag for log readability (e.g. "Lt", "Fr", "Dk", "Pl", "Gl").</summary>
    public static string ShortTag(this OrbKind k) => k switch
    {
        OrbKind.Lightning => "Lt",
        OrbKind.Frost => "Fr",
        OrbKind.Dark => "Dk",
        OrbKind.Plasma => "Pl",
        OrbKind.Glass => "Gl",
        _ => "?",
    };
}
