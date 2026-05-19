using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// v0.10 — Per-card relic scoring adjustments. STS2 has 100+ RelicModel subclasses;
/// most are either out-of-combat (BurningBlood end-fight heal, Whetstone deck
/// upgrade, ArcaneScroll reward modifier) or already reflected in <see cref="SimState"/>
/// at snapshot time (Anchor's +10 block → PlayerBlock; Vajra's +1 Str → PlayerStrength;
/// CrackedCore's Lightning orb → OrbQueue; WhisperingEarring/VelvetChoker +1 max
/// energy → PlayerEnergy; Bellows first-turn upgrade → card.Damage). This catalog
/// only handles relics whose effect depends on THIS card play and isn't already
/// folded into the state — i.e. per-attack modifiers (PenNib ×2 on the 10th,
/// StrikeDummy +3 to Strike cards), trigger counters (IronClub +1 draw every 4th
/// card), on-kill rewards (GremlinHorn +1 energy +1 draw), and play-limit caps
/// (VelvetChoker hard-stops at 6 cards/turn).
///
/// Calibration uses <see cref="PlanScorerWeights"/> (DamagePerPointBonus=50,
/// BlockPerPointBonus=30, RealLethalKillBonus=5000) so the bonus magnitude
/// matches the existing scoring scale.
///
/// Relics absent from this catalog contribute 0 — combat-irrelevant relics
/// (events, shop modifiers, deck-edit) and passive relics whose effects are
/// already in SimState. Adding new relics is opt-in: each handler reads
/// <c>state.PlayerRelics.ContainsKey(name)</c> + (for counters) the dict value.
/// </summary>
internal static class RelicCatalog
{
    /// <summary>
    /// Sum of per-card relic adjustments for this play. Positive = relic
    /// makes this play better; negative = makes it worse. <paramref name="details"/>
    /// receives one entry per relic that contributed a non-zero amount,
    /// surfaced in the scorer's log line (e.g. "PenNib×2(+750)").
    /// </summary>
    public static int ComputeCardBonus(
        SimCard card,
        int targetIdx,
        SimState state,
        PlanScorerWeights w,
        List<string>? details = null)
    {
        if (state.PlayerRelics == null || state.PlayerRelics.Count == 0) return 0;
        int total = 0;
        total += PenNibBonus(card, state, w, details);
        total += StrikeDummyBonus(card, state, w, details);
        total += GremlinHornBonus(card, targetIdx, state, w, details);
        total += IronClubBonus(card, state, w, details);
        total += VelvetChokerPenalty(state, w, details);
        total += KunaiBonus(card, state, w, details);
        total += ShurikenBonus(card, state, w, details);
        total += LetterOpenerBonus(card, state, w, details);
        total += OrnamentalFanBonus(card, state, w, details);
        total += NunchakuBonus(card, state, w, details);
        return total;
    }

    /// <summary>
    /// Nunchaku: every 10 Attacks played (cross-turn — counter doesn't reset
    /// at turn start) → gain +1 energy. Counter == AttacksPlayed%10; when 9
    /// the NEXT attack triggers. Energy on-trigger ≈ valued at the cost-1
    /// card breakpoint × DamagePerPointBonus ≈ +500 (rough — energy gain
    /// scoring elsewhere uses EnergyGainNeutralBonus/EnergyGainUrgentBonus).
    /// </summary>
    private static int NunchakuBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("Nunchaku", out int counter)) return 0;
        if (card.Kind != CardType.Attack) return 0;
        if (counter != 9) return 0;
        const int Bonus = 500;
        details?.Add($"Nunchaku+E({Bonus})");
        return Bonus;
    }

    /// <summary>
    /// PenNib: every 10th Attack played deals double damage. When
    /// <c>AttacksPlayed%10 == 9</c> the NEXT attack is the trigger. We bias
    /// the scoring toward the highest-damage attack at that moment — adding
    /// a bonus equal to the card's total damage in score-points (effectively
    /// scoring the attack as if it dealt 2× damage).
    /// </summary>
    private static int PenNibBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("PenNib", out int counter)) return 0;
        if (card.Kind != CardType.Attack) return 0;
        // Counter equals AttacksPlayed%10 (or 10 when activating display).
        // The trigger fires on the attack that brings the counter from 9→10
        // — i.e. when counter == 9 the next attack is the one that doubles.
        if (counter != 9) return 0;
        int dmg = card.Effect.TotalDamage;
        if (dmg <= 0) return 0;
        int bonus = dmg * w.DamagePerPointBonus;
        details?.Add($"PenNib×2(+{bonus})");
        return bonus;
    }

    /// <summary>
    /// StrikeDummy: cards with the Strike tag deal +3 damage. We approximate
    /// via card-id pattern (Strike / StrikeUpgraded / TwinStrike / PommelStrike
    /// etc. — STS2 ports STS1's Strike-tag family). +3 dmg × DamagePerPointBonus
    /// per hit = +150 per single-hit, scaled by Hits for multi.
    /// </summary>
    private static int StrikeDummyBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.ContainsKey("StrikeDummy")) return 0;
        if (card.Kind != CardType.Attack) return 0;
        if (!IsStrikeFamily(card.Id)) return 0;
        int hits = System.Math.Max(1, card.Effect.Hits);
        int bonus = 3 * hits * w.DamagePerPointBonus;
        details?.Add($"StrikeDummy+{bonus}");
        return bonus;
    }

    /// <summary>
    /// GremlinHorn: when an enemy dies, gain +1 energy and draw 1. Approximate
    /// as a fixed lethal-bonus when this card would kill the target. The
    /// lethal-kill detection mirrors PlanScorer's RealLethalKillBonus logic
    /// at a lighter weight (~ +400) since the bonus is on TOP of the existing
    /// lethal scoring.
    /// </summary>
    private static int GremlinHornBonus(SimCard card, int targetIdx, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.ContainsKey("GremlinHorn")) return 0;
        if (card.Kind != CardType.Attack || card.Effect.TotalDamage <= 0) return 0;
        if (targetIdx < 0 || targetIdx >= state.Enemies.Count) return 0;
        var tgt = state.Enemies[targetIdx];
        if (!tgt.IsAlive) return 0;
        // Use card.EffectiveDmgPerEnergy × cost as an estimate of "this card's
        // damage to this target." If that meets or exceeds tgt.EffectiveHp,
        // it's lethal.
        double dmg = card.EffectiveDmgPerEnergy(state, targetIdx) * System.Math.Max(1, card.Cost);
        int tgtEff = tgt.Hp + tgt.Block;
        if (dmg < tgtEff) return 0;
        const int Bonus = 400;
        details?.Add($"GremlinHorn+{Bonus}");
        return Bonus;
    }

    /// <summary>
    /// IronClub: every 4 cards played → draw 1. Counter equals
    /// <c>CardsPlayed%4</c>. When counter == 3, the NEXT card triggers the
    /// draw — score that play with a flat draw-equivalent bonus (~+200).
    /// Applies to any card type since the trigger doesn't care.
    /// </summary>
    private static int IronClubBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("IronClub", out int counter)) return 0;
        if (counter != 3) return 0;
        const int Bonus = 200;
        details?.Add($"IronClub+draw{Bonus}");
        return Bonus;
    }

    /// <summary>
    /// VelvetChoker: cap of 6 cards per turn. Game's ShouldPlay() already
    /// blocks the 7th+ play, so this is a SAFETY rail — if the planner is
    /// scoring a 6th card play, the 7th plan would be wasted. We mostly
    /// don't need to penalize since the game enforces it, but bias the
    /// planner away from filler plays when at the cap (5+ cards already
    /// played) so it prefers high-value plays for the remaining slots.
    /// Returns a small penalty per card past the warning threshold;
    /// negative magnitude so the planner trims chain length, not so large
    /// it overrides high-value plays.
    /// </summary>
    private static int VelvetChokerPenalty(SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("VelvetChoker", out int played)) return 0;
        const int Cap = 6;
        if (played < Cap - 1) return 0;
        // At played == 5 → slot 6 is the last allowed; at played >= 6 the
        // game already blocks. Add a soft "value the last slot" hint: -50
        // signaling "don't waste this slot on filler."
        int penalty = (played >= Cap) ? -2000 : -50;
        details?.Add($"VelvetChoker@{played}({penalty})");
        return penalty;
    }

    /// <summary>
    /// Kunai (STS2): every 3rd Attack played in a turn → apply Dexterity +1
    /// to the player. Counter == AttacksPlayedThisTurn%3; trigger fires when
    /// counter goes 2→0. So when counter == 2 the NEXT attack triggers.
    /// Per-stack DexterityPower value ~ +400.
    /// </summary>
    private static int KunaiBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("Kunai", out int counter)) return 0;
        if (card.Kind != CardType.Attack) return 0;
        if (counter != 2) return 0;
        const int Bonus = 400;
        details?.Add($"Kunai+Dex({Bonus})");
        return Bonus;
    }

    /// <summary>
    /// Shuriken (STS2): every 3rd Attack played in a turn → Strength +1 to
    /// the player. Same counter pattern as Kunai. StrengthPower per-stack ~ +600.
    /// </summary>
    private static int ShurikenBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("Shuriken", out int counter)) return 0;
        if (card.Kind != CardType.Attack) return 0;
        if (counter != 2) return 0;
        const int Bonus = 600;
        details?.Add($"Shuriken+Str({Bonus})");
        return Bonus;
    }

    /// <summary>
    /// LetterOpener (STS2): every 3rd Skill played in a turn → 5 damage to
    /// ALL enemies. Counter == SkillsPlayedThisTurn%3; when 2 the next Skill
    /// triggers. Value = 5 dmg × DamagePerPointBonus × alive-enemies (capped
    /// at 3 to avoid overweighting in horde fights).
    /// </summary>
    private static int LetterOpenerBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("LetterOpener", out int counter)) return 0;
        if (card.Kind != CardType.Skill) return 0;
        if (counter != 2) return 0;
        int aliveCount = 0;
        foreach (var e in state.Enemies) if (e.IsAlive) aliveCount++;
        int targetsFactor = System.Math.Min(3, System.Math.Max(1, aliveCount));
        int bonus = 5 * w.DamagePerPointBonus * targetsFactor;
        details?.Add($"LetterOpener+{bonus}(t{targetsFactor})");
        return bonus;
    }

    /// <summary>
    /// OrnamentalFan (STS2): every 3rd Attack played in a turn → gain 4 block.
    /// Counter == AttacksPlayedThisTurn%3. 4 block × BlockPerPointBonus.
    /// </summary>
    private static int OrnamentalFanBonus(SimCard card, SimState state, PlanScorerWeights w, List<string>? details)
    {
        if (!state.PlayerRelics.TryGetValue("OrnamentalFan", out int counter)) return 0;
        if (card.Kind != CardType.Attack) return 0;
        if (counter != 2) return 0;
        int bonus = 4 * w.BlockPerPointBonus;
        details?.Add($"OrnamentalFan+blk({bonus})");
        return bonus;
    }

    /// <summary>
    /// Strike-family detection. Matches the canonical STRIKE base ids and
    /// derivatives ported into STS2 ("STRIKE", "PUMMEL_STRIKE", "TWIN_STRIKE",
    /// "PERFECTED_STRIKE", etc.) by suffix and substring. False-positives
    /// (cards that aren't tagged Strike but contain the word) are rare in
    /// STS2's catalog; the cost of one is +150 score on the wrong card, which
    /// doesn't change ranking decisively.
    /// </summary>
    private static bool IsStrikeFamily(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var upper = id.ToUpperInvariant();
        if (upper == "STRIKE") return true;
        if (upper.EndsWith("_STRIKE")) return true;
        if (upper.StartsWith("STRIKE_")) return true;
        if (upper.Contains("PERFECTED_STRIKE")) return true;
        if (upper.Contains("HEAVY_STRIKE")) return true;
        return false;
    }
}
