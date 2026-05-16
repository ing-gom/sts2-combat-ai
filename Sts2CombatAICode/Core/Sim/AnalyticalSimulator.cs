using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace Sts2CombatAI.Sim;

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

        // 1. Spend energy unless a Free*Power counter covers this card's type.
        // v0.5 — Free counters decrement here so subsequent depth-2 cards see the
        // updated count and don't double-consume the same free play.
        int newFreeAttacks = next.PlayerFreeAttacks;
        int newFreeSkills = next.PlayerFreeSkills;
        int newFreePowers = next.PlayerFreePowers;
        bool freeApplied =
            (card.IsAttack && newFreeAttacks > 0) ||
            (card.IsSkill && newFreeSkills > 0) ||
            (card.IsPower && newFreePowers > 0);
        int energy = freeApplied
            ? next.PlayerEnergy
            : System.Math.Max(0, next.PlayerEnergy - card.Cost);
        if (freeApplied)
        {
            if (card.IsAttack) newFreeAttacks--;
            else if (card.IsSkill) newFreeSkills--;
            else if (card.IsPower) newFreePowers--;
        }
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
        int newPlayerFocus = next.PlayerFocus;
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
                    // Temporary*Power lasts 1 turn but is fully active for this turn's
                    // remaining card plays, so the second-card lookahead must see it.
                    case "StrengthPower":
                    case "TemporaryStrengthPower":
                        newPlayerStr += amount; break;
                    case "DexterityPower":
                    case "TemporaryDexterityPower":
                        newPlayerDex += amount; break;
                    // v0.5 — Focus scaling on orb output. Defect's BiasedCognition,
                    // CreativeAI, etc. apply FocusPower; subsequent orb plays should
                    // see the higher passive / evoke values in the second-card scorer.
                    case "FocusPower":
                    case "TemporaryFocusPower":
                        newPlayerFocus += amount; break;
                    // v0.5 — Free*Power propagation. A Power card that grants
                    // FreeAttackPower (or similar) needs to update the counter so the
                    // very next attack lookahead sees the free play available.
                    case "FreeAttackPower": newFreeAttacks += amount; break;
                    case "FreeSkillPower":  newFreeSkills  += amount; break;
                    case "FreePowerPower":  newFreePowers  += amount; break;
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

                // v0.5 — full cap chain matches the scorer: Vulnerable → Intangible
                // per-hit cap → HardenedShellRemaining total cap. Without this the
                // sim was dealing uncapped damage to Intangible / shell enemies, so
                // the second-card lookahead saw a corpse where the game would still
                // have a full-HP target and planned overkill chains accordingly.
                int totalDmg = StatusMath.EffectivePerEnemyTotal(
                    card.Damage, card.Hits, newPlayerStr, enemy, playerWeak);

                // Block-first absorption
                int blockAfter = System.Math.Max(0, enemy.Block - totalDmg);
                int dmgPastBlock = System.Math.Max(0, totalDmg - enemy.Block);
                int hpAfter = System.Math.Max(0, enemy.Hp - dmgPastBlock);

                // Decrement HardenedShell budget by the actual capped damage dealt.
                // Successive attacks against the same shell enemy in depth-2 then see
                // the reduced remaining instead of re-paying from the full budget.
                int shellLeft = enemy.HardenedShellRemaining;
                if (shellLeft > 0)
                    shellLeft = System.Math.Max(0, shellLeft - totalDmg);

                // Attached debuff stacks. v0.5 — extend beyond Vulnerable/Weak so
                // depth-2 sees the full debuff picture: Frail (enemy block gain ×0.75
                // — informational), Poison / Constrict / Burn (DoT that triggers the
                // HeavyDotPenalty so we don't overkill an enemy already dying to DoT).
                // Artifact intercepts incoming debuffs stack-by-stack and is consumed
                // in the order debuffs are applied — we approximate by deducting from
                // a remaining-Artifact counter as each stack lands.
                int newVuln = enemy.VulnerableAmount;
                int newWeak = enemy.WeakAmount;
                int newFrail = enemy.FrailAmount;
                int newPoison = enemy.PoisonAmount;
                int newConstrict = enemy.ConstrictAmount;
                int newBurn = enemy.BurnAmount;
                int artifactLeft = enemy.ArtifactAmount;
                foreach (var (powerName, amount) in card.PowerApps)
                {
                    int delta = amount;
                    if (artifactLeft > 0)
                    {
                        int absorb = System.Math.Min(artifactLeft, delta);
                        delta -= absorb;
                        artifactLeft -= absorb;
                        if (delta == 0) continue;
                    }
                    switch (powerName)
                    {
                        case "VulnerablePower": newVuln += delta; break;
                        case "WeakPower":       newWeak += delta; break;
                        case "FrailPower":      newFrail += delta; break;
                        case "PoisonPower":     newPoison += delta; break;
                        case "ConstrictPower":  newConstrict += delta; break;
                        case "BurnPower":       newBurn += delta; break;
                    }
                }

                newEnemies.Add(enemy with
                {
                    Hp = hpAfter,
                    Block = blockAfter,
                    VulnerableAmount = newVuln,
                    WeakAmount = newWeak,
                    FrailAmount = newFrail,
                    PoisonAmount = newPoison,
                    ConstrictAmount = newConstrict,
                    BurnAmount = newBurn,
                    ArtifactAmount = artifactLeft,
                    HardenedShellRemaining = shellLeft,
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

            // v0.5 — Self-targeted skills that apply self-buffs (Strength/Dex from
            // Spot Weakness style cards) need to propagate too, otherwise the second
            // card lookahead won't see the Strength bump and won't reward sequencing
            // "Spot Weakness → big attack" combos. Previously only Power cards
            // applied their PowerApps; self skills were silently dropped.
            if (selfTarget && card.PowerApps.Count > 0)
            {
                foreach (var (powerName, amount) in card.PowerApps)
                {
                    switch (powerName)
                    {
                        case "StrengthPower":
                        case "TemporaryStrengthPower":
                            newPlayerStr += amount; break;
                        case "DexterityPower":
                        case "TemporaryDexterityPower":
                            newPlayerDex += amount; break;
                        case "FocusPower":
                        case "TemporaryFocusPower":
                            newPlayerFocus += amount; break;
                        case "FreeAttackPower": newFreeAttacks += amount; break;
                        case "FreeSkillPower":  newFreeSkills  += amount; break;
                        case "FreePowerPower":  newFreePowers  += amount; break;
                    }
                }
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

                    // v0.5 — same full debuff propagation as the attack path. Artifact
                    // absorbs stacks until depleted (per-stack, in PowerApps order).
                    int newVuln = enemy.VulnerableAmount;
                    int newWeak = enemy.WeakAmount;
                    int newFrail = enemy.FrailAmount;
                    int newPoison = enemy.PoisonAmount;
                    int newConstrict = enemy.ConstrictAmount;
                    int newBurn = enemy.BurnAmount;
                    int artifactLeft = enemy.ArtifactAmount;
                    foreach (var (powerName, amount) in card.PowerApps)
                    {
                        int delta = amount;
                        if (artifactLeft > 0)
                        {
                            int absorb = System.Math.Min(artifactLeft, delta);
                            delta -= absorb;
                            artifactLeft -= absorb;
                            if (delta == 0) continue;
                        }
                        switch (powerName)
                        {
                            case "VulnerablePower": newVuln += delta; break;
                            case "WeakPower":       newWeak += delta; break;
                            case "FrailPower":      newFrail += delta; break;
                            case "PoisonPower":     newPoison += delta; break;
                            case "ConstrictPower":  newConstrict += delta; break;
                            case "BurnPower":       newBurn += delta; break;
                        }
                    }
                    newEnemies.Add(enemy with
                    {
                        VulnerableAmount = newVuln,
                        WeakAmount = newWeak,
                        FrailAmount = newFrail,
                        PoisonAmount = newPoison,
                        ConstrictAmount = newConstrict,
                        BurnAmount = newBurn,
                        ArtifactAmount = artifactLeft,
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
                    ApplyEvokeEffect(head, headEvokeVal, newPlayerFocus,
                        ref next, ref newPlayerBlock, ref energy, aliveCount);
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
                    ApplyEvokeEffect(kicked, kickedVal, newPlayerFocus,
                        ref next, ref newPlayerBlock, ref energy, aliveCount);
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

        // v0.5 — the card we just played joins the discard pile unless it exhausts
        // on play (catalog Exhaust flag). The catalog marks Slimed, Apparition, etc.
        // as Exhaust so the gate doesn't need a separate curse/status exclusion. Track
        // this so subsequent Draw-card scoring in depth-2 sees the post-play pile size.
        int drawPileAfter = next.DrawPileSize;
        int discardAfter = next.DiscardPileSize;
        if (!card.IsExhaust)
            discardAfter += 1;

        // 4. DrawCount: simulate fetching N cards from the pile as low-value placeholders.
        // We can't know the exact card; add a generic SimCard with rough average effect so
        // lookahead has something to work with (better than ignoring the draw entirely).
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
            PlayerFocus = newPlayerFocus,
            PlayerBlock = newPlayerBlock,
            PlayerFreeAttacks = newFreeAttacks,
            PlayerFreeSkills = newFreeSkills,
            PlayerFreePowers = newFreePowers,
            Hand = newHand,
            DrawPileSize = drawPileAfter,
            DiscardPileSize = discardAfter,
        };
    }

    /// <summary>
    /// Apply a single evoke of the given orb kind to the rolling state. Damage hits the
    /// weakest live enemy (Dark) / random one (Lightning) / all (Glass). Frost adds block.
    /// Plasma adds energy. Approximation — Dark accumulator is read from the caller.
    /// v0.5 — Focus adds to every damage / block evoke (Plasma untouched).
    /// </summary>
    private static void ApplyEvokeEffect(
        OrbKind kind, int darkAccumulated, int focus,
        ref SimState state, ref int playerBlock, ref int energy, int aliveCount)
    {
        // Each per-evoke damage/block clamped at 0 — Focus can be negative
        // (rare debuff scenarios) and the game floors damage at 0.
        switch (kind)
        {
            case OrbKind.Frost:
                playerBlock += System.Math.Max(0, 5 + focus);
                break;
            case OrbKind.Plasma:
                energy += 2;
                break;
            case OrbKind.Lightning:
                state = DamageWeakest(state, System.Math.Max(0, 8 + focus));
                break;
            case OrbKind.Dark:
                // Dark accumulator already absorbs Focus per tick from the game; the stored
                // value is the actual per-evoke damage. Don't double-apply Focus here.
                state = DamageWeakest(state, System.Math.Max(6, darkAccumulated));
                break;
            case OrbKind.Glass:
                state = DamageAll(state, System.Math.Max(0, 8 + focus));
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
        enemies[weakestIdx] = ApplyCappedHit(enemies[weakestIdx], dmg);
        return state with { Enemies = enemies };
    }

    private static SimState DamageAll(SimState state, int dmg)
    {
        var enemies = new List<SimEnemy>(state.Enemies.Count);
        foreach (var e in state.Enemies)
        {
            if (!e.IsAlive) { enemies.Add(e); continue; }
            enemies.Add(ApplyCappedHit(e, dmg));
        }
        return state with { Enemies = enemies };
    }

    /// <summary>
    /// Apply a single orb-hit's damage to an enemy with per-hit Intangible cap and
    /// HardenedShellRemaining budget. v0.5 — DamageWeakest / DamageAll used to skip
    /// both caps, so orb damage in the depth-2 sim overstated kills against shielded
    /// boards (Cathedral Intangible phase, Donu/Deca shells).
    /// </summary>
    private static SimEnemy ApplyCappedHit(SimEnemy e, int dmg)
    {
        int effective = dmg;
        if (e.DamageCapPerHit > 0 && effective > e.DamageCapPerHit)
            effective = e.DamageCapPerHit;
        int shellLeft = e.HardenedShellRemaining;
        if (shellLeft > 0 && effective > shellLeft)
            effective = shellLeft;
        else if (effective > 0 && shellLeft == 0 && e.Powers.ContainsKey("HardenedShellPower"))
            effective = 0;
        int blockAfter = System.Math.Max(0, e.Block - effective);
        int leak = System.Math.Max(0, effective - e.Block);
        int newShell = shellLeft > 0 ? System.Math.Max(0, shellLeft - effective) : shellLeft;
        return e with
        {
            Block = blockAfter,
            Hp = System.Math.Max(0, e.Hp - leak),
            HardenedShellRemaining = newShell,
        };
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
