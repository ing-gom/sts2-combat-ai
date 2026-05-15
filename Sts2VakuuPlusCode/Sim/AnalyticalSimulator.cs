using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace Sts2VakuuPlus.Sim;

/// <summary>
/// Minimal forward simulator. Given a SimState and a (card, targetIdx) play, returns
/// a new SimState reflecting the predicted outcome. Used by ActionPlanner's depth-2
/// lookahead to evaluate card sequencing (Inflame → Strike combos, AOE setups, etc.).
///
/// Approximation level (v0.2.5):
///   ✓ Energy spend
///   ✓ Player Strength / Dexterity stacking from Power cards
///   ✓ Attack damage application (block-first, then HP)
///   ✓ AOE damage to all alive enemies
///   ✓ Attached debuff stacking (Vulnerable / Weak)
///   ✓ Skill self-block
///   ✓ Hand removal (Played=true marker via record copy)
///   ✗ Card draws / energy gain triggers (deferred to v0.3)
///   ✗ Multi-hit per-hit Strength refresh
///   ✗ Power card persistent per-turn effects (Strength every turn etc.)
/// </summary>
internal static class AnalyticalSimulator
{
    /// <summary>
    /// Apply a card play to a state, returning a new state. Original is untouched.
    /// </summary>
    public static SimState ApplyCardPlay(SimState state, SimCard card, int targetIdx)
    {
        var next = state.DeepClone();

        // 1. Spend energy, then add any energy gain from the card (Adrenaline, etc.)
        int energy = System.Math.Max(0, next.PlayerEnergy - card.Cost);
        if (card.EnergyGain > 0) energy += card.EnergyGain;

        // 2. Remove the played card from hand. DeepClone produced new references for every
        // SimCard, so ReferenceEquals against the caller's `card` always fails — use record
        // value equality (== on records) and remove only the first match (handles duplicates
        // like 2× Strike correctly).
        var newHand = new List<SimCard>(next.Hand);
        int playedIdx = newHand.FindIndex(c => c == card);
        if (playedIdx >= 0) newHand.RemoveAt(playedIdx);

        // 3. Apply card effects
        int newPlayerStr = next.PlayerStrength;
        int newPlayerDex = next.PlayerDexterity;
        int newPlayerBlock = next.PlayerBlock;
        bool isAoe = card.Target == TargetType.AllEnemies;
        bool playerWeak = next.PlayerWeak > 0;
        bool playerFrail = next.PlayerFrail > 0;

        // 3a. Power card: self-apply powers (Strength, Dex, etc.)
        if (card.IsPower)
        {
            foreach (var (powerName, amount) in card.PowerApps)
            {
                switch (powerName)
                {
                    case "StrengthPower": newPlayerStr += amount; break;
                    case "DexterityPower": newPlayerDex += amount; break;
                    // Other powers (Inflame style) don't directly affect future card scoring
                    // in v0.2.5 — handled by per-power valuation in scorer.
                }
            }
        }

        // 3b. Attack: deal damage to target(s); also stack attached debuffs on enemy.
        if (card.IsAttack && card.Damage > 0)
        {
            var newEnemies = new List<SimEnemy>(next.Enemies.Count);
            for (int i = 0; i < next.Enemies.Count; i++)
            {
                var enemy = next.Enemies[i];
                bool isTarget = isAoe ? enemy.IsAlive
                    : (i == targetIdx && enemy.IsAlive);
                if (!isTarget)
                {
                    newEnemies.Add(enemy);
                    continue;
                }

                bool eVuln = enemy.VulnerableAmount > 0;
                int perHit = StatusMath.EffectiveAttackDmg(card.Damage,
                    newPlayerStr, eVuln, playerWeak);
                int totalDmg = perHit * System.Math.Max(1, card.Hits);

                // Block-first absorption
                int blockAfter = System.Math.Max(0, enemy.Block - totalDmg);
                int dmgPastBlock = System.Math.Max(0, totalDmg - enemy.Block);
                int hpAfter = System.Math.Max(0, enemy.Hp - dmgPastBlock);

                // Attached debuff stacks
                int newVuln = enemy.VulnerableAmount;
                int newWeak = enemy.WeakAmount;
                foreach (var (powerName, amount) in card.PowerApps)
                {
                    if (powerName == "VulnerablePower") newVuln += amount;
                    else if (powerName == "WeakPower") newWeak += amount;
                }

                newEnemies.Add(enemy with
                {
                    Hp = hpAfter,
                    Block = blockAfter,
                    VulnerableAmount = newVuln,
                    WeakAmount = newWeak,
                });
            }
            next = next with { Enemies = newEnemies };
        }

        // 3c. Skill: self block (only when self-targeted) + apply powers to target if any
        if (card.IsSkill)
        {
            bool selfTarget = card.Target == TargetType.Self
                           || card.Target == TargetType.AnyPlayer;

            if (selfTarget && card.Block > 0)
            {
                int eff = StatusMath.EffectiveBlock(card.Block, newPlayerDex, playerFrail);
                newPlayerBlock += eff;
            }

            // Skill that targets an enemy (or AOE) and applies debuffs
            if (!selfTarget && card.PowerApps.Count > 0)
            {
                var newEnemies = new List<SimEnemy>(next.Enemies.Count);
                for (int i = 0; i < next.Enemies.Count; i++)
                {
                    var enemy = next.Enemies[i];
                    bool isTarget = isAoe ? enemy.IsAlive : (i == targetIdx && enemy.IsAlive);
                    if (!isTarget) { newEnemies.Add(enemy); continue; }

                    int newVuln = enemy.VulnerableAmount;
                    int newWeak = enemy.WeakAmount;
                    foreach (var (powerName, amount) in card.PowerApps)
                    {
                        if (powerName == "VulnerablePower") newVuln += amount;
                        else if (powerName == "WeakPower") newWeak += amount;
                    }
                    newEnemies.Add(enemy with
                    {
                        VulnerableAmount = newVuln,
                        WeakAmount = newWeak,
                    });
                }
                next = next with { Enemies = newEnemies };
            }
        }

        // v0.4 — Orb channel / evoke simulation. Mutates orb queue + may damage / block player.
        // Order: evoke first (consumes head N times), then channel (may bump head out if full).
        if (card.EvokeCount > 0 || card.ChannelCount > 0)
        {
            var queue = new List<OrbKind>(next.OrbQueue);
            var evokeVals = new List<int>(next.OrbEvokeValues);
            int aliveCount = next.Enemies.Count(e => e.IsAlive);

            // Evoke: front orb is consumed once per evoke. Dualcast/Quadcast/MultiCast all
            // re-evoke the same front orb until the *last* call dequeues. We model the
            // dequeue at the end of the evoke loop; intermediate values still come from
            // the same head.
            if (card.EvokeCount > 0 && queue.Count > 0)
            {
                var head = queue[0];
                int headEvokeVal = evokeVals.Count > 0 ? evokeVals[0] : 0;
                for (int i = 0; i < card.EvokeCount; i++)
                {
                    ApplyEvokeEffect(head, headEvokeVal, ref next, ref newPlayerBlock, ref energy, aliveCount);
                }
                queue.RemoveAt(0);
                if (evokeVals.Count > 0) evokeVals.RemoveAt(0);
            }

            // Channel: push N orbs of ChannelKind. Each push past capacity kicks the head
            // (auto-evoke) before adding the new orb.
            for (int i = 0; i < card.ChannelCount; i++)
            {
                if (card.ChannelKind == OrbKind.Unknown) break;
                if (queue.Count >= next.PlayerOrbCapacity && queue.Count > 0)
                {
                    // Auto-evoke the head before the channel pushes the new orb.
                    var kicked = queue[0];
                    int kickedVal = evokeVals.Count > 0 ? evokeVals[0] : 0;
                    ApplyEvokeEffect(kicked, kickedVal, ref next, ref newPlayerBlock, ref energy, aliveCount);
                    queue.RemoveAt(0);
                    if (evokeVals.Count > 0) evokeVals.RemoveAt(0);
                }
                queue.Add(card.ChannelKind);
                evokeVals.Add(card.ChannelKind == OrbKind.Dark ? 6 : 0);
            }

            next = next with {
                OrbQueue = queue,
                OrbEvokeValues = evokeVals,
                PlayerOrbCount = queue.Count,
            };
        }

        // 4. DrawCount: simulate fetching N cards from the pile as low-value placeholders.
        // We can't know the exact card; add a generic SimCard with rough average effect so
        // lookahead has something to work with (better than ignoring the draw entirely).
        int drawPileAfter = next.DrawPileSize;
        int discardAfter = next.DiscardPileSize;
        if (card.DrawCount > 0 && drawPileAfter + discardAfter > 0)
        {
            for (int i = 0; i < card.DrawCount; i++)
            {
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    // Reshuffle simulated: discard pile becomes new draw pile
                    drawPileAfter = discardAfter;
                    discardAfter = 0;
                }
                newHand.Add(MakePlaceholderCard());
                drawPileAfter--;
            }
        }

        return next with
        {
            PlayerEnergy = energy,
            PlayerStrength = newPlayerStr,
            PlayerDexterity = newPlayerDex,
            PlayerBlock = newPlayerBlock,
            Hand = newHand,
            DrawPileSize = drawPileAfter,
            DiscardPileSize = discardAfter,
        };
    }

    /// <summary>
    /// Apply a single evoke of the given orb kind to the rolling state. Damage hits the
    /// weakest live enemy (Dark) / random one (Lightning) / all (Glass). Frost adds block.
    /// Plasma adds energy. Approximation — Dark accumulator is read from the caller.
    /// </summary>
    private static void ApplyEvokeEffect(
        OrbKind kind, int darkAccumulated,
        ref SimState state, ref int playerBlock, ref int energy, int aliveCount)
    {
        switch (kind)
        {
            case OrbKind.Frost:
                playerBlock += 5;
                break;
            case OrbKind.Plasma:
                energy += 2;
                break;
            case OrbKind.Lightning:
                state = DamageWeakest(state, 8);
                break;
            case OrbKind.Dark:
                state = DamageWeakest(state, System.Math.Max(6, darkAccumulated));
                break;
            case OrbKind.Glass:
                state = DamageAll(state, 8);
                break;
        }
    }

    private static SimState DamageWeakest(SimState state, int dmg)
    {
        var enemies = new List<SimEnemy>(state.Enemies);
        int weakestIdx = -1;
        int weakestHp = int.MaxValue;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (!enemies[i].IsAlive) continue;
            if (enemies[i].Hp < weakestHp) { weakestHp = enemies[i].Hp; weakestIdx = i; }
        }
        if (weakestIdx < 0) return state;
        var e = enemies[weakestIdx];
        int blockAfter = System.Math.Max(0, e.Block - dmg);
        int leak = System.Math.Max(0, dmg - e.Block);
        enemies[weakestIdx] = e with { Block = blockAfter, Hp = System.Math.Max(0, e.Hp - leak) };
        return state with { Enemies = enemies };
    }

    private static SimState DamageAll(SimState state, int dmg)
    {
        var enemies = new List<SimEnemy>(state.Enemies.Count);
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) { enemies.Add(e); continue; }
            int blockAfter = System.Math.Max(0, e.Block - dmg);
            int leak = System.Math.Max(0, dmg - e.Block);
            enemies.Add(e with { Block = blockAfter, Hp = System.Math.Max(0, e.Hp - leak) });
        }
        return state with { Enemies = enemies };
    }

    /// <summary>
    /// Synthetic "average" card used to represent unknown draws in lookahead.
    /// 5 dmg attack — close to average starter card value. Keeps lookahead optimistic
    /// enough to value Draw cards properly without overcommitting to phantom plays.
    /// </summary>
    private static SimCard MakePlaceholderCard() => new()
    {
        Id = "<draw-placeholder>",
        Cost = 1,
        Kind = CardType.Attack,
        Target = TargetType.AnyEnemy,
        SourceRef = null,
        Effect = new CardEffectSummary { Damage = 5, Hits = 1 },
        IsPlayable = true,
    };
}
