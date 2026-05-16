using System.Collections.Generic;
using System.Linq;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Effect-axis-driven priority adjustments for non-Power cards. Captures the
/// "play X before Y" order that the effect rules imply but the generic scorer
/// doesn't otherwise see.
///
/// Coverage (from card_triggers.json):
///   • DAMAGE_AMPLIFIER  — Aggression, Conflagration, Flanking, Knockdown,
///                         Lethality, Shadow Step, Sword Sage. Wants to play
///                         BEFORE remaining attacks so they hit harder.
///   • BLOCK_AMPLIFIER   — Entrench / Pillar of Creation / Unmovable. Doubles
///                         existing block; rewarded by current block + remaining
///                         block skills. (Power-card amplifiers — Barricade /
///                         Blur / Danse / Shadowmeld — are tier-handled.)
///   • VULN_AMPLIFIER    — Bully / Colossus / Cruelty / Debilitate / Dismantle /
///                         Dominate / Molten Fist. Wants enemy already Vuln OR
///                         a Vuln-applier in hand to play first.
///   • WEAK_AMPLIFIER    — Debilitate / Tracking. Same pattern, smaller weights
///                         (Weak doesn't compound with our attacks).
///   • BLOCK_PAYOFF      — Body Slam (dmg = block). Wants block already on the
///                         board; heavy penalty if played pre-block-setup.
///   • HP_LOSS_CONSUMER  — Inferno / Tear Asunder. Value scales with HP missing;
///                         bigger payoff at low HP.
///
/// Skips Power cards (they go through PowerSequencingTier instead). Stacks with
/// HandSynergy / BuildSynergy — those handle Strength / Dex / Vuln-as-power /
/// build-tagged producer-side bonuses, this file picks up the amplifier side and
/// the state-dependent payoffs they don't cover.
/// </summary>
internal static class EffectSynergy
{
    public static (int bonus, string detail) Compute(SimCard card, int targetIdx, SimState state)
    {
        if (card.IsPower || card.Axes.Count == 0) return (0, "");

        int b = 0;
        var parts = new List<string>();
        var axes = card.Axes;

        if (axes.Contains("DAMAGE_AMPLIFIER"))
            ApplyDamageAmplifier(card, state, ref b, parts);

        if (axes.Contains("BLOCK_AMPLIFIER"))
            ApplyBlockAmplifier(card, state, ref b, parts);

        if (axes.Contains("VULN_AMPLIFIER"))
            ApplyVulnAmplifier(card, targetIdx, state, ref b, parts);

        if (axes.Contains("WEAK_AMPLIFIER"))
            ApplyWeakAmplifier(card, state, ref b, parts);

        if (axes.Contains("BLOCK_PAYOFF"))
            ApplyBlockPayoff(card, state, ref b, parts);

        if (axes.Contains("HP_LOSS_CONSUMER"))
            ApplyHpLossConsumer(state, ref b, parts);

        return (b, parts.Count == 0 ? "" : string.Join(",", parts));
    }

    private static void ApplyDamageAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int remainingAttacks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsPlayable && c.IsAttack);
        if (remainingAttacks > 0)
        {
            int v = remainingAttacks * 70;
            b += v;
            parts.Add($"dmgAmp(atk*{remainingAttacks})=+{v}");
        }
        else
        {
            b -= 200;
            parts.Add("dmgAmpNoAtk=-200");
        }
    }

    private static void ApplyBlockAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int curBlock = state.PlayerBlock;
        int remainingBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsPlayable
            && c.IsSkill && c.Block > 0);
        // Existing block doubles immediately; remaining block skills will compound later.
        int v = curBlock * 4 + remainingBlocks * 50;
        if (v > 0)
        {
            b += v;
            parts.Add($"blkAmp(blk{curBlock}+rem{remainingBlocks})=+{v}");
        }
        else
        {
            b -= 250;
            parts.Add("blkAmpNothing=-250");
        }
    }

    private static void ApplyVulnAmplifier(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
    {
        bool targetVuln = targetIdx >= 0 && targetIdx < state.Enemies.Count
                          && state.Enemies[targetIdx].IsAlive
                          && state.Enemies[targetIdx].VulnerableAmount > 0;
        bool anyVuln = state.Enemies.Any(e => e.IsAlive && e.VulnerableAmount > 0);
        bool vulnInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsPlayable
            && (c.Axes.Contains("VULN") || c.PowerApps.ContainsKey("VulnerablePower")));

        if (targetVuln)         { b += 450; parts.Add("vulnAmpTgt=+450"); }
        else if (anyVuln)       { b += 300; parts.Add("vulnAmpAny=+300"); }
        else if (vulnInHand)    { b += 250; parts.Add("vulnAmpInHand=+250"); }
        else                    { b -= 300; parts.Add("vulnAmpNoSource=-300"); }
    }

    private static void ApplyWeakAmplifier(SimCard self, SimState state, ref int b, List<string> parts)
    {
        bool anyWeak = state.Enemies.Any(e => e.IsAlive && e.WeakAmount > 0);
        bool weakInHand = state.Hand.Any(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsPlayable
            && (c.Axes.Contains("WEAK") || c.PowerApps.ContainsKey("WeakPower")));

        // Multi-hit enemies (Repeats ≥ 2) make Weak's per-hit rounding compound.
        // Each such enemy adds extra value to the amplifier.
        int multiHitEnemies = state.Enemies.Count(e =>
            e.IsAlive && e.HasAttackIntent && !e.IsInert && e.IntentRepeats >= 2);
        int multiHitBonus = multiHitEnemies * 120;

        if (anyWeak)         { b += 250 + multiHitBonus; parts.Add($"weakAmpEnemy=+{250 + multiHitBonus}"); }
        else if (weakInHand) { b += 150 + multiHitBonus; parts.Add($"weakAmpInHand=+{150 + multiHitBonus}"); }
        else                 { b -= 150; parts.Add("weakAmpNoSource=-150"); }
    }

    private static void ApplyBlockPayoff(SimCard self, SimState state, ref int b, List<string> parts)
    {
        int curBlock = state.PlayerBlock;
        if (curBlock > 0)
        {
            int v = curBlock * 30;
            b += v;
            parts.Add($"blkPayoff({curBlock})=+{v}");
            return;
        }
        // Block 0 — Body Slam currently does 0 dmg. If block skills still in hand,
        // mild penalty (play them first). If none, this card is dead — heavy.
        int remainingBlocks = state.Hand.Count(c =>
            !ReferenceEquals(c, self) && !c.Played && c.IsPlayable
            && c.IsSkill && c.Block > 0);
        if (remainingBlocks > 0)
        {
            b -= 600;
            parts.Add("blkPayoffEarly=-600");
        }
        else
        {
            b -= 1500;
            parts.Add("blkPayoffNoBlk=-1500");
        }
    }

    private static void ApplyHpLossConsumer(SimState state, ref int b, List<string> parts)
    {
        // MaxHp isn't in SimState — use absolute HP heuristic. Player at low HP
        // means more "damage taken so far", which these cards scale on.
        if (state.PlayerHp <= 30)
        {
            b += 350;
            parts.Add($"hpLossLow(hp{state.PlayerHp})=+350");
        }
        else if (state.PlayerHp <= 50)
        {
            b += 200;
            parts.Add($"hpLossMid(hp{state.PlayerHp})=+200");
        }
    }
}
