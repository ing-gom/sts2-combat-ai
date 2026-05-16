using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Hand-aware scoring for cards that amplify, copy, or replay OTHER cards in the
/// same hand. Examples (from card_triggers.json):
///   • POWER_AMPLIFIER  — Subroutine, Signal Boost, Dual Wield
///   • REPLAY           — Iteration, Loop, Juggling, Hidden Gem, Nostalgia,
///                        Catastrophe, Nightmare, Dual Wield
///   • ATTACK_REPLAY    — Beat Down, One-Two Punch (deterministic)
///   • ATTACK_REPLAY_RANDOM — Stampede (random target — discount applied)
///   • SKILL_REPLAY     — Burst (a Power; tier-handled, but scored here too if hit)
///
/// Echo Form / Mayhem are also REPLAY but resolve as Power cards — they live in
/// <see cref="PowerSequencingTier"/> instead and are not re-counted here.
///
/// Scoring model (target-score-based):
///   bonus = bestTarget.Score × ratio   (ratio ≈ 0.45–0.5 — roughly one extra play)
///   ratio is reduced for ATTACK_REPLAY_RANDOM (random selection ≠ best card).
///
/// Recursion guard: targets that are themselves amplifier / replay / draw cards
/// are excluded from the target pool — prevents Amplifier→Replay→Amplifier loops
/// and Replay→Draw→Replay loops through <c>PlanScorer.EvaluateDrawCard</c>.
///
/// No-target case: penalised (Subroutine + no power in hand is a near-dead card).
/// </summary>
internal static class AmplifierSynergy
{
    private const double PowerAmpRatio       = 0.50;
    private const double AttackReplayRatio   = 0.50;
    private const double AttackReplayRandRatio = 0.35; // Stampede etc.
    private const double SkillReplayRatio    = 0.45;
    private const double GenericReplayRatio  = 0.45;
    private const int    BonusCap            = 3000;
    private const int    NoTargetPenalty     = -500;

    private static bool HasAmplifierAxis(SimCard c)
        => c.Axes.Count > 0 && (
            c.Axes.Contains("POWER_AMPLIFIER")
            || c.Axes.Contains("REPLAY")
            || c.Axes.Contains("ATTACK_REPLAY")
            || c.Axes.Contains("ATTACK_REPLAY_RANDOM")
            || c.Axes.Contains("SKILL_REPLAY"));

    private static bool IsValidTarget(SimCard c, SimCard self)
        => !ReferenceEquals(c, self) && c.IsPlayable
           && !c.IsCurseOrStatus
           && !c.IsDrawCard            // breaks Replay → DrawCard → Replay recursion
           && !HasAmplifierAxis(c);    // breaks Amplifier → Amplifier recursion

    /// <summary>
    /// Computes (bonus, log-detail) for a non-Power card whose axes mark it as an
    /// amplifier or replay. Returns (0, "") if the card has none of those axes.
    /// </summary>
    public static (int bonus, string detail) Compute(SimCard card, SimState state, PlanScorerWeights w)
    {
        if (card.Axes.Count == 0) return (0, "");

        bool isPowerAmp    = card.Axes.Contains("POWER_AMPLIFIER");
        bool isReplay      = card.Axes.Contains("REPLAY");
        bool isAtkReplay   = card.Axes.Contains("ATTACK_REPLAY");
        bool isAtkReplayR  = card.Axes.Contains("ATTACK_REPLAY_RANDOM");
        bool isSkillReplay = card.Axes.Contains("SKILL_REPLAY");

        if (!(isPowerAmp || isReplay || isAtkReplay || isAtkReplayR || isSkillReplay))
            return (0, "");

        // Resolve which target kinds this card can amplify/replay.
        bool acceptsPower  = isPowerAmp || isReplay;
        bool acceptsAttack = isReplay || isAtkReplay || isAtkReplayR;
        bool acceptsSkill  = isReplay || isSkillReplay;

        // Best target across the allowed pools, scored with the SAME PlanScorer as
        // the normal pipeline. Recursion guard via IsValidTarget.
        int bestScore = 0;
        string bestId = "";
        SimCard? best = null;
        foreach (var t in state.Hand)
        {
            if (!IsValidTarget(t, card)) continue;
            if (!((acceptsPower && t.IsPower)
                  || (acceptsAttack && t.IsAttack)
                  || (acceptsSkill && t.IsSkill))) continue;

            int tidx = -1;
            if (t.Target == TargetType.AnyEnemy)
            {
                tidx = state.Enemies.FindIndex(e => e.IsAlive);
                if (tidx < 0) continue;
            }
            int s = PlanScorer.Score(t, tidx, state, w);
            if (best == null || s > bestScore)
            {
                bestScore = s;
                bestId = t.Id;
                best = t;
            }
        }

        if (best == null)
        {
            // Special-case ATTACK_REPLAY with all enemies dead — no penalty (no
            // attack would land anyway). For the rest, no target = mostly wasted.
            int penalty = isPowerAmp ? NoTargetPenalty - 100 : NoTargetPenalty;
            return (penalty, $"ampNoTarget={penalty}");
        }

        // Ratio choice — pick the SMALLEST applicable ratio so a card that overlaps
        // multiple axes (Dual Wield = POWER_AMPLIFIER + REPLAY) stays conservative.
        double ratio;
        string label;
        if (isAtkReplayR && best.IsAttack)        { ratio = AttackReplayRandRatio; label = "atkReplayR"; }
        else if (isPowerAmp && best.IsPower)      { ratio = PowerAmpRatio;         label = "powerAmp"; }
        else if (isAtkReplay && best.IsAttack)    { ratio = AttackReplayRatio;     label = "atkReplay"; }
        else if (isSkillReplay && best.IsSkill)   { ratio = SkillReplayRatio;      label = "skillReplay"; }
        else                                       { ratio = GenericReplayRatio;    label = "replay"; }

        int bonus = System.Math.Min(BonusCap, (int)(bestScore * ratio));
        if (bonus <= 0) return (0, "");

        var parts = new List<string> { $"{label}({ShortId(bestId)})=+{bonus}" };
        return (bonus, string.Join(",", parts));
    }

    private static string ShortId(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return "?";
        var dot = cardId.LastIndexOf('.');
        return dot < 0 ? cardId : cardId.Substring(dot + 1);
    }
}
