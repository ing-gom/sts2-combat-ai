using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Lightweight projection of a CardModel used by the planner.
/// v0.1 stores only the heuristic-relevant fields (Type, Cost, TargetType) plus a
/// reference back to the live CardModel for replay binding. Damage/Block values are
/// intentionally absent — v0.1's scorer doesn't need them.
/// </summary>
internal sealed record SimCard
{
    public required string Id { get; init; }
    public required int Cost { get; init; }
    public required CardType Kind { get; init; }
    public required TargetType Target { get; init; }
    // Nullable so unit tests can construct SimCard without a real CardModel.
    // Live mode executors require non-null at execution time.
    public CardModel? SourceRef { get; init; }
    public required CardEffectSummary Effect { get; init; }

    /// <summary>
    /// True when CardModel.CanPlay() is true at snapshot time. Unplayable cards
    /// (curses with "Unplayable" keyword, status cards, conditional plays whose
    /// condition isn't met) are excluded from planner candidates.
    /// Defaults to true so legacy SimCard fixtures without explicit init stay valid.
    /// </summary>
    public bool IsPlayable { get; init; } = true;

    /// <summary>
    /// Build-classification axes from cards_catalog.json (POISON_PRODUCER,
    /// ORB_AMPLIFIER, etc.). Empty for cards not in the catalog.
    /// </summary>
    public IReadOnlyList<string> Axes { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Builds this card primarily contributes to (e.g., "독 빌드"). Used by
    /// HandSynergy to detect hand-wide build commitments and combo plays.
    /// </summary>
    public IReadOnlyList<string> PrimaryBuildTags { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// True when this card survives past end-of-turn discard (Retain keyword in
    /// catalog). Affects play-ORDER: when other useful cards exist, prefer
    /// playing the non-retain cards first so the retained card can act again
    /// next turn. When the retain card is the strongest available play, retain
    /// status is irrelevant and we play it normally.
    /// </summary>
    public bool IsRetain { get; init; }

    /// <summary>
    /// True when this card is exhausted at end-of-turn if not played (Ethereal
    /// keyword in catalog). Affects play-ORDER: even if its score is borderline,
    /// playing now is better than losing it for free — bump priority a notch so
    /// we don't waste it sitting in hand.
    /// </summary>
    public bool IsEthereal { get; init; }

    /// <summary>
    /// True when this card was guaranteed to start in our opening hand (Innate
    /// keyword in catalog). Informational only — opening-turn ordering already
    /// works via normal scoring; flag is here for future "first-turn setup
    /// detection" rules without re-querying the catalog.
    /// </summary>
    public bool IsInnate { get; init; }

    /// <summary>
    /// True when this card is exhausted on play (Exhaust keyword in catalog).
    /// Used by the simulator to decide whether the played card joins the
    /// discard pile (non-exhaust) or leaves the deck entirely (exhaust) —
    /// matters for Draw-card scoring in the depth-2 lookahead.
    /// </summary>
    public bool IsExhaust { get; init; }

    /// <summary>
    /// True when this card fetches / discovers a random card from the draw or
    /// discard pile (Anointed, Apotheosis, Echo of Fallen, etc. — catalog
    /// `fetch_trigger`). Drives status / curse pollution penalty scoring:
    /// fetch cards become weaker when the pile contains junk that could be
    /// pulled.
    /// </summary>
    public bool IsFetchTrigger { get; init; }

    public bool IsAttack => Kind == CardType.Attack;
    public bool IsSkill => Kind == CardType.Skill;
    public bool IsPower => Kind == CardType.Power;
    public bool IsCurseOrStatus => Kind == CardType.Curse || Kind == CardType.Status;

    // Convenience accessors
    public int Damage => Effect.Damage;
    public int Hits => Effect.Hits;
    public int TotalDamage => Effect.TotalDamage;
    public int Block => Effect.Block;
    public IReadOnlyDictionary<string, int> PowerApps => Effect.PowerApps;
    public int EnergyGain => Effect.EnergyGain;
    public int DrawCount => Effect.DrawCount;
    public bool IsEnergyGainCard => Effect.IsEnergyGainCard;
    public bool IsDrawCard => Effect.IsDrawCard;

    // v0.4 — orb metadata pass-through.
    public int EvokeCount => Effect.EvokeCount;
    public int ChannelCount => Effect.ChannelCount;
    public OrbKind ChannelKind => Effect.ChannelKind;
}
