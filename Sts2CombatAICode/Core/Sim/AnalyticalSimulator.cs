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
        // v0.7.21 — CorruptionPower: Skill cards cost 0 combat-wide. Persistent
        // (no decrement); checked as a global flag on the state.
        int newFreeAttacks = next.PlayerFreeAttacks;
        int newFreeSkills = next.PlayerFreeSkills;
        int newFreePowers = next.PlayerFreePowers;
        bool corruptionFreeSkill = card.IsSkill
            && next.PlayerPowers != null
            && next.PlayerPowers.TryGetValue("CorruptionPower", out var corStack)
            && corStack > 0;
        bool freeApplied =
            (card.IsAttack && newFreeAttacks > 0) ||
            (card.IsSkill && newFreeSkills > 0) ||
            (card.IsPower && newFreePowers > 0) ||
            corruptionFreeSkill;
        int energy = freeApplied
            ? next.PlayerEnergy
            : System.Math.Max(0, next.PlayerEnergy - card.Cost);
        if (freeApplied && !corruptionFreeSkill)
        {
            // Per-card counters decrement; persistent CorruptionPower doesn't.
            if (card.IsAttack) newFreeAttacks--;
            else if (card.IsSkill) newFreeSkills--;
            else if (card.IsPower) newFreePowers--;
        }
        if (card.EnergyGain > 0) energy += card.EnergyGain;
        // EnergizedPower / EnergyNextTurnPower: deliberately NOT added to immediate
        // energy here. The exact semantics (immediate vs next-turn) varies between
        // STS variants and we don't have a test harness to verify either way.
        // PowerCatalog values these via the power-stack mechanism instead.

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
        // v0.7.82 — VigorPower buffer. Carried across the simulated step so a
        // skill that grants Vigor lifts the next attack's damage, then is
        // consumed when an attack plays.
        int newPlayerVigor = next.PlayerVigor;
        // v0.7.83 — BufferPower stacks. Each stack negates one incoming damage
        // instance. Propagated so depth-N lookahead sees the cushion.
        int newPlayerBuffer = next.PlayerBuffer;
        // v0.7.84 — Damage multiplier powers. Lethality is single-shot
        // (first-attack-this-turn) so the attack path consumes it; Tracking
        // and Cruelty are passive and just carried.
        int newPlayerLethality = next.PlayerLethality;
        int newPlayerTracking = next.PlayerTracking;
        int newPlayerCruelty = next.PlayerCruelty;
        // v0.7.85 — Block-side reactive / multiplier powers.
        int newPlayerRage = next.PlayerRage;
        int newPlayerAfterimage = next.PlayerAfterimage;
        int newPlayerUnmovable = next.PlayerUnmovable;
        bool newUnmovableUsedThisTurn = next.UnmovableUsedThisTurn;
        // v0.7.86 — Shiv damage bonus (Silent passive).
        int newPlayerAccuracy = next.PlayerAccuracy;
        // v0.7.94 — Reactive Strength on Skill play + Skill cost-0 enabler.
        int newPlayerEnrage = next.PlayerEnrage;
        int newPlayerCorruption = next.PlayerCorruption;
        // v0.7.95 — Next Skill ×2 (single-shot per stack).
        int newPlayerBurst = next.PlayerBurst;
        // v0.7.96 — Player Thorns (reflect damage on hit).
        int newPlayerThorns = next.PlayerThorns;
        // v0.7.97 — FeelNoPainPower (block on Exhaust trigger).
        int newPlayerFeelNoPain = next.PlayerFeelNoPain;
        // v0.7.98 — EchoForm remaining; if >0, this card's effects double then
        // remaining decrements by 1. Type-agnostic; combines multiplicatively
        // with burstMul for Skills.
        int newPlayerEchoForm = next.PlayerEchoForm;
        bool echoActive = newPlayerEchoForm > 0;
        int echoMul = echoActive ? 2 : 1;
        int newPlayerFocus = next.PlayerFocus;
        int newPlayerIntangible = next.PlayerIntangible;
        int newPlayerEotBlockBonus = next.PlayerEndOfTurnBlockBonus;
        int newPlayerBlock = next.PlayerBlock;
        int newPlayerHp = next.PlayerHp;
        // v0.7.71 — Regent star resource. Subsequent depth-N candidates need
        // to see updated star count for star-cost cards (FALLING_STAR etc.)
        // to unlock properly in the simulator's filter pass.
        int newPlayerStars = next.PlayerStars + card.StarsGain - card.StarCost;
        if (newPlayerStars < 0) newPlayerStars = 0;
        bool isAoe = card.Target == TargetType.AllEnemies;
        bool playerWeak = next.PlayerWeak > 0;
        bool playerFrail = next.PlayerFrail > 0;

        // v0.7.9 — Self-damage on play. Cards expose HpLoss via CardEffectSummary
        // (BLOODLETTING 3, OFFERING 6, HEMOKINESIS 2 etc.). Subtract before any
        // turn-resolution math so subsequent depth-N candidates see the lower HP
        // and the HpLoss penalty band in EstimateCardPower fires correctly.
        if (card.HpLossAmount > 0)
            newPlayerHp = System.Math.Max(0, newPlayerHp - card.HpLossAmount);

        // 3a. Power card: self-apply powers (Strength, Dex, etc.)
        if (card.IsPower)
        {
            foreach (var (powerName, rawAmount) in card.PowerApps)
            {
                // v0.7.98 — EchoForm doubles ALL powers granted by this play.
                // EchoFormPower itself is excluded so a self-cast Echo Form
                // doesn't recursively double its own stack.
                int amount = powerName == "EchoFormPower" ? rawAmount : rawAmount * echoMul;
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
                    // v0.7.82 — VigorPower propagation. Some Power cards (TERRAFORMING,
                    // PREP_TIME-derived buffs etc.) grant Vigor; the next attack
                    // lookahead must see the boost.
                    case "VigorPower": newPlayerVigor += amount; break;
                    // v0.7.83 — BufferPower propagation (Buffer, etc.). Each stack
                    // negates one incoming damage instance — depth-N threat estimate
                    // sees the cushion.
                    case "BufferPower": newPlayerBuffer += amount; break;
                    // v0.7.84 — Damage multiplier powers. PlanScorer uses these
                    // via ApplyDamageMultipliers; propagation here lets the depth-N
                    // lookahead see the same buff after a setup Power is played.
                    case "LethalityPower": newPlayerLethality += amount; break;
                    case "TrackingPower": newPlayerTracking += amount; break;
                    case "CrueltyPower": newPlayerCruelty += amount; break;
                    // v0.7.85 — Block-side reactive / multiplier powers.
                    case "RagePower": newPlayerRage += amount; break;
                    case "AfterimagePower": newPlayerAfterimage += amount; break;
                    case "UnmovablePower": newPlayerUnmovable += amount; break;
                    // v0.7.86 — Shiv damage bonus.
                    case "AccuracyPower": newPlayerAccuracy += amount; break;
                    // v0.7.94 — Reactive Skill→Strength trigger + Skill cost-0 enabler.
                    case "EnragePower": newPlayerEnrage += amount; break;
                    case "CorruptionPower": newPlayerCorruption += amount; break;
                    // v0.7.95 — Next Skill ×2 multiplier.
                    case "BurstPower": newPlayerBurst += amount; break;
                    // v0.7.96 — Player Thorns reflect.
                    case "ThornsPower": newPlayerThorns += amount; break;
                    // v0.7.97 — FeelNoPain (block on Exhaust).
                    case "FeelNoPainPower": newPlayerFeelNoPain += amount; break;
                    // v0.7.98 — EchoForm Power. Use rawAmount (the carve-out
                    // above prevented self-doubling). Adds to remaining echoes
                    // available this turn for SUBSEQUENT cards.
                    case "EchoFormPower": newPlayerEchoForm += rawAmount; break;
                    // v0.5 — Free*Power propagation. A Power card that grants
                    // FreeAttackPower (or similar) needs to update the counter so the
                    // very next attack lookahead sees the free play available.
                    case "FreeAttackPower": newFreeAttacks += amount; break;
                    case "FreeSkillPower":  newFreeSkills  += amount; break;
                    case "FreePowerPower":  newFreePowers  += amount; break;
                    // v0.5 — IntangiblePower propagation. Apparition / WraithForm
                    // apply Intangible to the player; the next-card threat estimate
                    // should drop accordingly. The "ticks at start of player turn"
                    // detail is irrelevant to within-turn lookahead — we use the
                    // stack to gate PredictPlayerDmg's per-hit cap.
                    case "IntangiblePower": newPlayerIntangible += amount; break;
                    // v0.5 — Metallicize / PlatedArmor add to end-of-turn block.
                    // Once applied, subsequent block-decision scoring sees the
                    // cushion and stops over-recommending defends.
                    case "MetallicizePower":
                    case "PlatedArmorPower":
                        newPlayerEotBlockBonus += amount; break;
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
                // v0.7.82 — Apply Vigor to damage. Consumption happens once after
                // the attack resolves (after the enemy loop) so each enemy in AOE
                // sees the same Vigor amount, matching STS canonical behavior.
                // v0.7.86 — AccuracyPower → +N damage for Shiv. Folded into base.
                int adjustedBase = StatusMath.ApplyCardSpecificDamageBonus(card.Damage, card.Id, next);
                int totalDmg = StatusMath.EffectivePerEnemyTotal(
                    adjustedBase, card.Hits, newPlayerStr, newPlayerVigor, enemy, playerWeak);
                // v0.7.84 — Apply damage multipliers. Lethality fires on first
                // attack only; the next attack in depth-N lookahead sees
                // newPlayerLethality=0 below.
                totalDmg = StatusMath.ApplyDamageMultipliers(totalDmg,
                    next with { PlayerLethality = newPlayerLethality, PlayerTracking = newPlayerTracking, PlayerCruelty = newPlayerCruelty },
                    defenderVulnerable: enemy.VulnerableAmount > 0,
                    defenderWeak: enemy.WeakAmount > 0,
                    lethalityActive: newPlayerLethality > 0);
                // v0.7.98 — EchoForm doubles the entire attack (each hit lands
                // twice). Applied after damage-multiplier chain so the doubled
                // damage benefits from Tracking / Cruelty / Lethality once.
                totalDmg *= echoMul;

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

                // v0.5 — thorns reflect: each hit we deal to a thorny enemy costs
                // us ThornsAmount HP. Multi-hit cards trigger per hit. Bypass our
                // block (thorns is "lose HP" in STS). PlayerIntangible doesn't
                // affect reflected damage in canonical STS, so don't cap here.
                if (enemy.ThornsAmount > 0 && totalDmg > 0)
                {
                    int hits = System.Math.Max(1, card.Hits);
                    newPlayerHp = System.Math.Max(0, newPlayerHp - enemy.ThornsAmount * hits);
                }

                // Attached debuff stacks. v0.5 — extend beyond Vulnerable/Weak so
                // depth-2 sees the full debuff picture: Frail (enemy block gain ×0.75
                // — informational), Poison / Constrict / Burn (DoT that triggers the
                // HeavyDotPenalty so we don't overkill an enemy already dying to DoT).
                // Artifact intercepts each debuff APPLICATION (entire stack count
                // blocked per Artifact charge — canonical STS behavior). Buffs aren't
                // intercepted; here we're only propagating debuffs so the per-app
                // consumption is safe.
                int newVuln = enemy.VulnerableAmount;
                int newWeak = enemy.WeakAmount;
                int newFrail = enemy.FrailAmount;
                int newPoison = enemy.PoisonAmount;
                int newConstrict = enemy.ConstrictAmount;
                int newBurn = enemy.BurnAmount;
                int newDoom = enemy.DoomAmount;
                int artifactLeft = enemy.ArtifactAmount;

                // v0.7.13 — REAPER_FORM applies DoomPower stack on every attack
                // hit. Add 1 stack per hit (multi-hit attacks apply multiple
                // stacks). Artifact does NOT intercept self-buff-driven debuffs
                // (Doom is added on hit, not via a debuff PowerVar).
                if (next.PlayerPowers != null
                    && next.PlayerPowers.TryGetValue("ReaperFormPower", out var reaperStacks)
                    && reaperStacks > 0
                    && card.Damage > 0)
                {
                    newDoom += reaperStacks * System.Math.Max(1, card.Hits);
                }
                foreach (var (powerName, rawAttachAmount) in card.PowerApps)
                {
                    if (!IsEnemyDebuff(powerName)) continue;
                    if (artifactLeft > 0)
                    {
                        // One Artifact charge intercepts the entire application.
                        artifactLeft--;
                        continue;
                    }
                    // v0.7.98 — EchoForm doubles attached-debuff stacks on attacks.
                    int amount = rawAttachAmount * echoMul;
                    switch (powerName)
                    {
                        case "VulnerablePower": newVuln += amount; break;
                        case "WeakPower":       newWeak += amount; break;
                        case "FrailPower":      newFrail += amount; break;
                        case "PoisonPower":     newPoison += amount; break;
                        case "ConstrictPower":  newConstrict += amount; break;
                        case "BurnPower":       newBurn += amount; break;
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
                    DoomAmount = newDoom,
                    ArtifactAmount = artifactLeft,
                    HardenedShellRemaining = shellLeft,
                });
            }
            next = next with { Enemies = newEnemies };

            // v0.7.82 — Vigor is single-shot: consumed when this attack resolves.
            // Subsequent attacks in the depth-N lookahead chain see Vigor=0.
            newPlayerVigor = 0;
            // v0.7.84 — Lethality is "first attack of the turn ×1.5" → after the
            // first attack, drop to 0. (Tracking/Cruelty are passive — keep.)
            newPlayerLethality = 0;
            // v0.7.85 — RagePower: gain N block per attack played.
            if (newPlayerRage > 0)
                newPlayerBlock += StatusMath.EffectiveBlock(newPlayerRage, newPlayerDex, playerFrail);
        }

        // 3c. Skill: self block (only when self-targeted) + apply powers to target if any
        if (card.IsSkill)
        {
            // v0.7.94 — EnragePower: gain Strength +N on Skill play. Applies to
            // ALL skills (self or enemy-targeted), so do it before the selfTarget
            // block branch. Multiple skills compound across the depth-N chain.
            if (newPlayerEnrage > 0)
                newPlayerStr += newPlayerEnrage;

            // v0.7.95 — BurstPower: next Skill effect ×2. Single-shot per stack.
            // Captured once at start of skill resolve; consumed at end.
            bool burstActive = newPlayerBurst > 0;
            int burstMul = burstActive ? 2 : 1;

            bool selfTarget = card.Target == TargetType.Self
                           || card.Target == TargetType.AnyPlayer;

            if (selfTarget && card.Block > 0)
            {
                int eff = StatusMath.EffectiveBlock(card.Block, newPlayerDex, playerFrail);
                // v0.7.85 — UnmovablePower: first block card/turn ×2. Single-shot
                // per turn (canonical STS). Consume the flag on use.
                if (newPlayerUnmovable > 0 && !newUnmovableUsedThisTurn)
                {
                    eff *= 2;
                    newUnmovableUsedThisTurn = true;
                }
                // v0.7.95 — Burst stacks with Unmovable multiplicatively
                // (canonical STS: independent multipliers compose).
                // v0.7.98 — EchoForm also compounds multiplicatively.
                eff *= burstMul * echoMul;
                newPlayerBlock += eff;
            }

            // v0.5 — Self-targeted skills that apply self-buffs (Strength/Dex from
            // Spot Weakness style cards) need to propagate too, otherwise the second
            // card lookahead won't see the Strength bump and won't reward sequencing
            // "Spot Weakness → big attack" combos. Previously only Power cards
            // applied their PowerApps; self skills were silently dropped.
            if (selfTarget && card.PowerApps.Count > 0)
            {
                foreach (var (powerName, rawAmount) in card.PowerApps)
                {
                    // v0.7.95 — Burst doubles all Skill self-buff amounts.
                    // v0.7.98 — Echo also multiplies (compounds multiplicatively).
                    // The granting power itself is excluded from its own multiplier
                    // (no recursive double-stack).
                    bool grantsBurst = powerName == "BurstPower";
                    bool grantsEcho = powerName == "EchoFormPower";
                    int skillMul = grantsBurst ? 1 : burstMul;
                    int multiplier = grantsEcho ? skillMul : skillMul * echoMul;
                    int amount = rawAmount * multiplier;
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
                        // v0.7.82 — Skill-granted Vigor (rare but exists in STS2).
                        case "VigorPower": newPlayerVigor += amount; break;
                        // v0.7.83 — Skill-granted Buffer (e.g., Buffer card itself).
                        case "BufferPower": newPlayerBuffer += amount; break;
                        // v0.7.84 — Skill-granted damage multipliers.
                        case "LethalityPower": newPlayerLethality += amount; break;
                        case "TrackingPower": newPlayerTracking += amount; break;
                        case "CrueltyPower": newPlayerCruelty += amount; break;
                        // v0.7.85 — Skill-granted block-side powers.
                        case "RagePower": newPlayerRage += amount; break;
                        case "AfterimagePower": newPlayerAfterimage += amount; break;
                        case "UnmovablePower": newPlayerUnmovable += amount; break;
                        // v0.7.86 — Skill-granted Shiv damage bonus.
                        case "AccuracyPower": newPlayerAccuracy += amount; break;
                        // v0.7.94 — Skill-granted Enrage / Corruption.
                        case "EnragePower": newPlayerEnrage += amount; break;
                        case "CorruptionPower": newPlayerCorruption += amount; break;
                        // v0.7.95 — Skill-granted Burst.
                        case "BurstPower": newPlayerBurst += amount; break;
                        // v0.7.96 — Skill-granted Thorns.
                        case "ThornsPower": newPlayerThorns += amount; break;
                        // v0.7.97 — Skill-granted FeelNoPain.
                        case "FeelNoPainPower": newPlayerFeelNoPain += amount; break;
                        // v0.7.98 — Skill-granted EchoForm (rare but possible).
                        case "EchoFormPower": newPlayerEchoForm += amount; break;
                        case "FreeAttackPower": newFreeAttacks += amount; break;
                        case "FreeSkillPower":  newFreeSkills  += amount; break;
                        case "FreePowerPower":  newFreePowers  += amount; break;
                        case "IntangiblePower": newPlayerIntangible += amount; break;
                        case "MetallicizePower":
                        case "PlatedArmorPower":
                            newPlayerEotBlockBonus += amount; break;
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

                    // v0.5 — same full debuff propagation as the attack path. Each
                    // tracked debuff application consumes one Artifact charge (entire
                    // amount blocked when intercepted — canonical STS behavior).
                    int newVuln = enemy.VulnerableAmount;
                    int newWeak = enemy.WeakAmount;
                    int newFrail = enemy.FrailAmount;
                    int newPoison = enemy.PoisonAmount;
                    int newConstrict = enemy.ConstrictAmount;
                    int newBurn = enemy.BurnAmount;
                    int artifactLeft = enemy.ArtifactAmount;
                    foreach (var (powerName, rawAmount) in card.PowerApps)
                    {
                        if (!IsEnemyDebuff(powerName)) continue;
                        if (artifactLeft > 0)
                        {
                            artifactLeft--;
                            continue;
                        }
                        // v0.7.95 — Burst doubles applied debuff amounts.
                        // v0.7.98 — Echo also multiplies (compounds with Burst).
                        int amount = rawAmount * burstMul * echoMul;
                        switch (powerName)
                        {
                            case "VulnerablePower": newVuln += amount; break;
                            case "WeakPower":       newWeak += amount; break;
                            case "FrailPower":      newFrail += amount; break;
                            case "PoisonPower":     newPoison += amount; break;
                            case "ConstrictPower":  newConstrict += amount; break;
                            case "BurnPower":       newBurn += amount; break;
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

            // v0.7.95 — Consume one Burst stack after the skill resolves.
            // Block / self-buffs / debuffs above already used `burstMul`; now
            // subtract one from the carried stack so the NEXT skill in depth-N
            // sees one less. Floor at 0.
            if (burstActive && newPlayerBurst > 0)
                newPlayerBurst--;
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

        // 4. DrawCount: simulate fetching N cards from the pile.
        // v0.5 — draws fire BEFORE the played card moves to the discard pile (the card
        // is "in play" while its effects resolve), so use pre-discard-bump pile sizes.
        // v0.5.1 — Use a deck-aware "average card" derived from the actual pile contents
        // instead of a fixed 5-damage placeholder. A draw into a strong deck (lots of
        // high-damage attacks) now scores higher in the depth-2 lookahead than a draw
        // into a weak / status-heavy deck. Computed once per ApplyCardPlay since the
        // mean of a pool barely shifts when one card is plucked out.
        int drawPileAfter = next.DrawPileSize;
        int discardAfter = next.DiscardPileSize;
        if (card.DrawCount > 0 && drawPileAfter + discardAfter > 0)
        {
            var avgDraw = MakeAverageDrawCard(next);
            for (int i = 0; i < card.DrawCount; i++)
            {
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    // Reshuffle simulated: discard pile becomes new draw pile
                    drawPileAfter = discardAfter;
                    discardAfter = 0;
                }
                newHand.Add(avgDraw);
                drawPileAfter--;
            }
        }

        // v0.5 — AFTER draw resolves, the played card joins the discard pile unless it
        // exhausts on play (catalog Exhaust flag). Done here so any post-play snapshot
        // a downstream card sees reflects the realistic pile sizes including this card.
        if (!card.IsExhaust)
            discardAfter += 1;

        // v0.7.85 — AfterimagePower: gain N block on every card played (including
        // this one). Applies after Rage/Unmovable, so total block stacks cleanly.
        if (newPlayerAfterimage > 0 && !card.IsCurseOrStatus)
            newPlayerBlock += StatusMath.EffectiveBlock(newPlayerAfterimage, newPlayerDex, playerFrail);

        // v0.7.97 — FeelNoPainPower: gain N block when a card is exhausted.
        // Only fires for cards with the Exhaust keyword (catalog flag); status /
        // curse Ethereal exhaust at turn-end, not on play.
        if (newPlayerFeelNoPain > 0 && card.IsExhaust)
            newPlayerBlock += StatusMath.EffectiveBlock(newPlayerFeelNoPain, newPlayerDex, playerFrail);

        // v0.7.98 — Consume one EchoForm charge per card resolve. Subsequent
        // cards in depth-N lookahead see one less remaining echo. Curse/Status
        // cards still count as plays (canonical: Echo Form text says "you play",
        // which curses/status do when forced — but typical play loop avoids them).
        if (echoActive && newPlayerEchoForm > 0)
            newPlayerEchoForm--;

        return next with
        {
            PlayerHp = newPlayerHp,
            PlayerEnergy = energy,
            PlayerStrength = newPlayerStr,
            PlayerDexterity = newPlayerDex,
            PlayerVigor = newPlayerVigor,
            PlayerBuffer = newPlayerBuffer,
            PlayerLethality = newPlayerLethality,
            PlayerTracking = newPlayerTracking,
            PlayerCruelty = newPlayerCruelty,
            PlayerRage = newPlayerRage,
            PlayerAfterimage = newPlayerAfterimage,
            PlayerUnmovable = newPlayerUnmovable,
            UnmovableUsedThisTurn = newUnmovableUsedThisTurn,
            PlayerAccuracy = newPlayerAccuracy,
            PlayerEnrage = newPlayerEnrage,
            PlayerCorruption = newPlayerCorruption,
            PlayerBurst = newPlayerBurst,
            PlayerThorns = newPlayerThorns,
            PlayerFeelNoPain = newPlayerFeelNoPain,
            PlayerEchoForm = newPlayerEchoForm,
            PlayerFocus = newPlayerFocus,
            PlayerIntangible = newPlayerIntangible,
            PlayerEndOfTurnBlockBonus = newPlayerEotBlockBonus,
            PlayerBlock = newPlayerBlock,
            PlayerFreeAttacks = newFreeAttacks,
            PlayerFreeSkills = newFreeSkills,
            PlayerFreePowers = newFreePowers,
            // v0.7.71 — propagate updated star count for depth-N lookahead
            PlayerStars = newPlayerStars,
            Hand = newHand,
            DrawPileSize = drawPileAfter,
            DiscardPileSize = discardAfter,
        };
    }

    /// <summary>
    /// v0.7.10 (Forward Sim Phase 2a) — Advance state to the next player turn.
    /// Combines (a) end-of-turn block bonuses, (b) enemy intent resolution
    /// (damage applied through block, leak to HP), (c) enemy turn-start buffs +
    /// DoT ticks, (d) per-turn status decrement (Vuln/Weak/Frail/Intangible -1
    /// for both sides + Poison -1), (e) player block reset, (f) energy reset to
    /// base 3, (g) synthetic next-turn hand from current deck pool.
    ///
    /// Approximations (Phase 2a):
    ///   • PredictPlayerDmg used directly for the lump leak (block consumed
    ///     atomically). Per-hit accounting deferred.
    ///   • Energy resets to a flat 3. Per-character base + EnergyNextTurnPower
    ///     / Pyre / Berserk bonuses not folded in (no SimState field).
    ///   • Barricade / Calipers not modeled — block always resets fully.
    ///   • Hand draw uses one synthetic average card duplicated × 5
    ///     (matches AnalyticalSimulator's intra-turn draw model).
    /// </summary>
    public static SimState AdvanceTurn(SimState state)
        => AdvanceTurnInternal(state, BuildSyntheticHand(state, ComputeNextTurnHandSize(state)));

    /// <summary>
    /// v0.7.14 (Phase 2c) — AdvanceTurn variant whose next-turn hand is drawn
    /// via Monte Carlo sampling from the actual deck pool (DrawPile +
    /// DiscardPile) rather than 5× synthetic average cards. Lets the planner
    /// evaluate "what if I draw a Strike heavy hand next turn" vs "what if
    /// I draw a defensive hand" — variance the synthetic average smooths out.
    ///
    /// Caller is responsible for averaging scores across N samples for noise
    /// reduction (ActionPlanner uses N=3).
    /// </summary>
    public static SimState AdvanceTurnSampled(SimState state, System.Random rng)
        => AdvanceTurnInternal(state, BuildSampledHand(state, ComputeNextTurnHandSize(state), rng));

    /// <summary>
    /// v0.7.15 — Next-turn hand size. STS2 default = 5, plus
    /// <c>MachineLearningPower</c> stacks ("at start of turn, draw +1 card per
    /// stack"). Used by both synthetic and sampled hand builders so the
    /// multi-turn projection correctly inflates Machine Learning's value.
    /// </summary>
    private const int BaseNextTurnHandSize = 5;
    private static int ComputeNextTurnHandSize(SimState state)
    {
        int size = BaseNextTurnHandSize;
        if (state.PlayerPowers != null
            && state.PlayerPowers.TryGetValue("MachineLearningPower", out var ml)
            && ml > 0)
        {
            size += ml;
        }
        return size;
    }

    /// <summary>
    /// Synthetic next-turn hand: handSize copies of the pile's average card.
    /// Kept as the default <see cref="AdvanceTurn"/> behavior — deterministic,
    /// noise-free, suitable for non-Monte-Carlo callers.
    /// </summary>
    private static System.Collections.Generic.List<SimCard> BuildSyntheticHand(SimState state, int handSize)
    {
        var hand = new System.Collections.Generic.List<SimCard>();
        if (state.DrawPile.Count + state.DiscardPile.Count > 0)
        {
            var avg = MakeAverageDrawCard(state);
            for (int i = 0; i < handSize; i++) hand.Add(avg);
        }
        return hand;
    }

    /// <summary>
    /// Monte Carlo next-turn hand: <paramref name="handSize"/> distinct cards
    /// drawn uniformly without replacement from <c>DrawPile + DiscardPile</c>.
    /// Fisher–Yates partial shuffle keeps the sample O(handSize) rather than
    /// O(pile-size). Returns the full pool when it's smaller than handSize.
    /// </summary>
    private static System.Collections.Generic.List<SimCard> BuildSampledHand(
        SimState state, int handSize, System.Random rng)
    {
        var hand = new System.Collections.Generic.List<SimCard>(handSize);
        int poolSize = state.DrawPile.Count + state.DiscardPile.Count;
        if (poolSize == 0) return hand;

        if (poolSize <= handSize)
        {
            foreach (var c in state.DrawPile) hand.Add(c);
            foreach (var c in state.DiscardPile) hand.Add(c);
            return hand;
        }

        // Copy combined pool into a mutable array, then Fisher–Yates the
        // first `handSize` indices. Cheaper than a full shuffle.
        var copy = new SimCard[poolSize];
        int w = 0;
        foreach (var c in state.DrawPile) copy[w++] = c;
        foreach (var c in state.DiscardPile) copy[w++] = c;
        for (int i = 0; i < handSize; i++)
        {
            int j = i + rng.Next(poolSize - i);
            (copy[i], copy[j]) = (copy[j], copy[i]);
            hand.Add(copy[i]);
        }
        return hand;
    }

    private static SimState AdvanceTurnInternal(SimState state, System.Collections.Generic.List<SimCard> nextHand)
    {
        // (a)+(b) Resolve enemy intents — PredictPlayerDmg already factors
        // block (incl. EOT bonus), Vulnerable on player, Weak on enemies, and
        // Intangible cap.
        //
        // v0.7.12 — split the raw post-block leak between player and allies
        // (skeleton split-fire defense). Allies take the absorption pool
        // proportional to their Hp share; overflow returns to player.
        int rawLeak = EnemyTurnSimulator.PredictRawLeak(state);
        int allyAbsorbed = EnemyTurnSimulator.ComputeAllyAbsorption(state, rawLeak);
        int playerLeak = rawLeak - allyAbsorbed;
        int newPlayerHp = System.Math.Max(0, state.PlayerHp - playerLeak);

        // v0.7.83 — Buffer consumption after turn resolves. Each stack absorbed
        // one damage instance; count attack instances this turn (capped at stack
        // count). Approximation: ignores per-enemy dead/alive subtleties already
        // checked in PredictRawLeak.
        int bufferConsumed = 0;
        if (state.PlayerBuffer > 0)
        {
            int attackInstances = 0;
            foreach (var e in state.Enemies)
            {
                if (!e.IsAlive) continue;
                if (e.HasAttackIntent || e.HasDeathBlowIntent)
                    attackInstances += System.Math.Max(1, e.IntentRepeats);
            }
            bufferConsumed = System.Math.Min(state.PlayerBuffer, attackInstances);
        }
        int newPlayerBufferEot = System.Math.Max(0, state.PlayerBuffer - bufferConsumed);

        // v0.7.11 — Ally attacks fire after player turn ends but before
        // enemy intents resolve (in practice STS2 allies act on the player's
        // EOT). Their damage applies to the weakest live enemy each turn.
        // Block-absorbed-first matches enemy attack handling.
        int totalAllyDmg = 0;
        foreach (var ally in state.Allies)
        {
            if (!ally.IsAlive || !ally.HasAttackIntent) continue;
            totalAllyDmg += ally.TotalIntentDamage;
        }
        // Apply ally damage to weakest live enemy (single-target heuristic).
        int weakestIdx = -1;
        int weakestHp = int.MaxValue;
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            var e = state.Enemies[i];
            if (!e.IsAlive) continue;
            if (e.Hp < weakestHp) { weakestHp = e.Hp; weakestIdx = i; }
        }

        // (c) Enemies: turn-start Strength gain (Ritual etc.), DoT ticks, and
        // per-turn debuff decrement on the enemy itself.
        var newEnemies = new System.Collections.Generic.List<SimEnemy>(state.Enemies.Count);
        for (int idx = 0; idx < state.Enemies.Count; idx++)
        {
            var e = state.Enemies[idx];
            if (!e.IsAlive) { newEnemies.Add(e); continue; }
            var ne = e;

            // Apply ally damage to weakest enemy (block-first).
            if (idx == weakestIdx && totalAllyDmg > 0)
            {
                int newBlock = System.Math.Max(0, ne.Block - totalAllyDmg);
                int leakToEnemy = System.Math.Max(0, totalAllyDmg - ne.Block);
                ne = ne with { Block = newBlock, Hp = System.Math.Max(0, ne.Hp - leakToEnemy) };
            }
            if (e.HasTurnStartStrengthBuff)
                ne = ne with { StrengthAmount = ne.StrengthAmount + 1 };

            // DoT ticks (Poison + Constrict + Doom). Burn timing varies — left out.
            // v0.7.13 — DoomPower from REAPER_FORM ticks alongside other DoT.
            int dotTick = ne.PoisonAmount + ne.ConstrictAmount + ne.DoomAmount;
            if (dotTick > 0)
                ne = ne with { Hp = System.Math.Max(0, ne.Hp - dotTick) };

            ne = ne with
            {
                VulnerableAmount = System.Math.Max(0, ne.VulnerableAmount - 1),
                WeakAmount       = System.Math.Max(0, ne.WeakAmount - 1),
                FrailAmount      = System.Math.Max(0, ne.FrailAmount - 1),
                PoisonAmount     = System.Math.Max(0, ne.PoisonAmount - 1),
                Block            = 0, // enemies' block also resets each turn
            };
            newEnemies.Add(ne);
        }

        // (d) Player status decrement.
        int newPlayerVuln = System.Math.Max(0, state.PlayerVulnerable - 1);
        int newPlayerWeak = System.Math.Max(0, state.PlayerWeak - 1);
        int newPlayerFrail = System.Math.Max(0, state.PlayerFrail - 1);
        int newPlayerIntangible = System.Math.Max(0, state.PlayerIntangible - 1);

        // v0.7.21 — DoomPower tick on player (Necrobinder self-doom).
        // Adds N damage to player at turn-end where N = stack. Stack persists
        // (no decrement) — Doom only goes up.
        if (state.PlayerDoom > 0)
            newPlayerHp = System.Math.Max(0, newPlayerHp - state.PlayerDoom);

        // v0.7.12 — Player Power per-turn passives (Phase 2b). The full
        // PlayerPowers dict is consulted for persistent powers that don't have
        // a dedicated SimState field. Each adds its turn-start effect on top
        // of what's already credited via PowerCatalog scoring.
        int newPlayerStr = state.PlayerStrength;
        int newPlayerHpAfterPassives = newPlayerHp;
        bool barricadeActive = false;
        if (state.PlayerPowers != null && state.PlayerPowers.Count > 0)
        {
            // DemonFormPower N → +N Strength every turn-start.
            if (state.PlayerPowers.TryGetValue("DemonFormPower", out var df) && df > 0)
                newPlayerStr += df;
            // RegenPower N → restore N HP at turn-start (capped by absolute heal).
            if (state.PlayerPowers.TryGetValue("RegenPower", out var rg) && rg > 0)
                newPlayerHpAfterPassives += rg;
            // BarricadePower → block carries over instead of resetting.
            if (state.PlayerPowers.TryGetValue("BarricadePower", out var brc) && brc > 0)
                barricadeActive = true;
            // ReaperFormPower 는 ApplyCardPlay 의 attack 분기에서 적 DoomAmount 누적
            // (v0.7.13). 여기서는 별도 처리 없음 — AdvanceTurn 의 enemy DoT loop 가
            // PoisonAmount + ConstrictAmount + DoomAmount 합산해 tick.
        }

        // (e)+(f) Block reset (unless Barricade) + energy reset (flat 3 base).
        int newPlayerBlock = barricadeActive ? state.PlayerBlock : 0;
        const int BaseTurnEnergy = 3;

        // (g) New hand from deck pool — provided by caller. Caller picks
        // synthetic-avg (BuildSyntheticHand, default AdvanceTurn) or Monte
        // Carlo sampling (BuildSampledHand, AdvanceTurnSampled).
        // Existing hand is conceptually discarded — we don't track which
        // cards survive via Ethereal exhaust / Retain (Phase 2a simplification).
        var newHand = nextHand;

        // v0.7.16 — AGGRESSION turn-start hand addition. The Power recalls a
        // random Attack from the discard pile (upgraded for one turn) per
        // stack. Synthesize the recalled card as an "average attack" from the
        // discard with a +30% damage boost approximating the temporary upgrade.
        if (state.PlayerPowers != null
            && state.PlayerPowers.TryGetValue("AggressionPower", out var aggStacks)
            && aggStacks > 0
            && state.DiscardPile.Count > 0)
        {
            int totalDmg = 0, count = 0;
            int totalCost = 0;
            foreach (var c in state.DiscardPile)
            {
                if (!c.IsAttack || c.IsCurseOrStatus) continue;
                totalDmg += c.Damage * System.Math.Max(1, c.Hits);
                totalCost += System.Math.Max(0, c.Cost);
                count++;
            }
            if (count > 0)
            {
                int avgDmg = (int)(totalDmg / (double)count * 1.3); // +30% upgrade
                int avgCost = System.Math.Max(0, totalCost / count);
                var recalled = new SimCard
                {
                    Id = "<aggression-recall>",
                    Cost = avgCost,
                    Kind = CardType.Attack,
                    Target = TargetType.AnyEnemy,
                    SourceRef = null,
                    Effect = new CardEffectSummary
                    {
                        Damage = avgDmg,
                        Hits = 1,
                    },
                    IsPlayable = true,
                };
                for (int i = 0; i < aggStacks; i++) newHand.Add(recalled);
            }
        }

        int newDrawPileSize = System.Math.Max(0, state.DrawPileSize + state.DiscardPileSize - newHand.Count);
        int newDiscardPileSize = 0;

        // v0.7.13 — MAYHEM / STAMPEDE turn-start auto-play. Each stack auto-
        // plays a card (MAYHEM: top of draw pile, STAMPEDE: random Attack from
        // draw). Both modeled as free-use damage from the synthetic average
        // draw card landing on the weakest alive enemy.
        //
        // v0.7.16 — AGGRESSION 의 hand-addition 효과는 위쪽 newHand 빌드 직후
        // 처리됨 (discard pile 의 평균 attack +30% upgrade 를 합성해 nextHand
        // 에 추가). MAYHEM/STAMPEDE 와 달리 enemy 데미지가 아닌 next-turn
        // hand 옵션 증가 — 별도 코드 경로.
        int mayhemStacks = 0, stampedeStacks = 0;
        if (state.PlayerPowers != null)
        {
            state.PlayerPowers.TryGetValue("MayhemPower", out mayhemStacks);
            state.PlayerPowers.TryGetValue("StampedePower", out stampedeStacks);
        }
        int autoTriggers = mayhemStacks + stampedeStacks;
        if (autoTriggers > 0)
        {
            var avgAuto = MakeAverageDrawCard(state);
            int perTriggerDmg = avgAuto.IsAttack ? avgAuto.TotalDamage : 0;
            int totalAutoDmg = perTriggerDmg * autoTriggers;
            if (totalAutoDmg > 0)
            {
                int wIdx = -1; int wHp = int.MaxValue;
                for (int i = 0; i < newEnemies.Count; i++)
                {
                    if (!newEnemies[i].IsAlive) continue;
                    if (newEnemies[i].Hp < wHp) { wHp = newEnemies[i].Hp; wIdx = i; }
                }
                if (wIdx >= 0)
                {
                    var t = newEnemies[wIdx];
                    int blkAfter = System.Math.Max(0, t.Block - totalAutoDmg);
                    int leakToE = System.Math.Max(0, totalAutoDmg - t.Block);
                    newEnemies[wIdx] = t with
                    {
                        Block = blkAfter,
                        Hp = System.Math.Max(0, t.Hp - leakToE),
                    };
                }
            }
        }

        // v0.7.12 — distribute allyAbsorbed across alive allies proportional
        // to their HP share. Allies whose Hp drops to 0 become inert (dead).
        var newAllies = new System.Collections.Generic.List<SimAlly>(state.Allies.Count);
        if (allyAbsorbed > 0)
        {
            int totalAllyHp = 0;
            foreach (var a in state.Allies) if (a.IsAlive) totalAllyHp += a.Hp;
            foreach (var a in state.Allies)
            {
                if (!a.IsAlive) { newAllies.Add(a); continue; }
                // Each ally absorbs in proportion to its HP share.
                int share = totalAllyHp > 0
                    ? (int)((long)allyAbsorbed * a.Hp / totalAllyHp)
                    : 0;
                int newHp = System.Math.Max(0, a.Hp - share);
                newAllies.Add(a with { Hp = newHp });
            }
        }
        else
        {
            newAllies.AddRange(state.Allies);
        }

        return state with
        {
            PlayerHp = newPlayerHpAfterPassives,
            PlayerBlock = newPlayerBlock,
            PlayerEnergy = BaseTurnEnergy,
            PlayerStrength = newPlayerStr,
            // v0.7.83 — Carry Buffer minus instances consumed this turn.
            PlayerBuffer = newPlayerBufferEot,
            // v0.7.84 — Lethality re-arms each turn (it's "first attack/turn"
            // multiplier; AdvanceTurn refreshes Lethality to its full stack value).
            // Tracking and Cruelty are passive — preserved via `state with`.
            PlayerLethality = state.PlayerLethality,
            // v0.7.85 — Unmovable re-arms each turn (single-shot per turn).
            UnmovableUsedThisTurn = false,
            PlayerVulnerable = newPlayerVuln,
            PlayerWeak = newPlayerWeak,
            PlayerFrail = newPlayerFrail,
            PlayerIntangible = newPlayerIntangible,
            Enemies = newEnemies,
            Allies = newAllies,
            Hand = newHand,
            DrawPileSize = newDrawPileSize,
            DiscardPileSize = newDiscardPileSize,
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
    /// Set of enemy-debuff PowerApps the sim recognizes for Artifact consumption.
    /// Artifact intercepts every debuff application regardless of whether we have
    /// a dedicated SimEnemy field for it (the propagation switch below only updates
    /// fields when we track them; powers like Hex / DarkShackles still consume an
    /// Artifact charge but aren't carried forward into nextState).
    /// </summary>
    private static bool IsEnemyDebuff(string powerName) => powerName switch
    {
        "VulnerablePower" or "WeakPower" or "FrailPower"
        or "PoisonPower" or "ConstrictPower" or "BurnPower"
        or "HexPower" or "DarkShacklesPower" or "PiercingWailPower"
        or "DampenPower" or "EnfeeblingTouchPower" or "ShackedPotionPower"
        or "ShacklingPotionPower" or "ConfusedPower" or "RupturePower"
        or "NoxiousFumesPower" => true,
        _ => false,
    };

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
    /// v0.5.1 — Synthesize a deck-aware "average draw" card from the pile contents.
    /// Damage / Block / Cost / Hits are means across the combined draw + discard
    /// pool (both eligible to be drawn once reshuffle hits). Kind is decided by
    /// majority — attack-heavy decks model draws as attacks (so depth-2 sees a
    /// damage option), skill-heavy decks model draws as skills (self-block option).
    ///
    /// Why average over the *combined* pool: in-game pile order is randomized and
    /// the discard pile feeds back via reshuffle, so over the rest of the fight
    /// every card in the pool is equally likely to surface. Averaging matches that.
    ///
    /// Why a single synthetic card (not sampling): the simulator runs inside a
    /// depth-2 hot loop and stochastic draws would make scoring noisy across runs.
    /// One representative card keeps the lookahead deterministic.
    ///
    /// Falls back to the legacy 5-damage placeholder when the pile snapshot is
    /// empty (tests, capture failure) so behaviour stays defined.
    /// </summary>
    private static SimCard MakeAverageDrawCard(SimState state)
    {
        int total = state.DrawPile.Count + state.DiscardPile.Count;
        if (total == 0) return MakeLegacyPlaceholderCard();

        // sumTotalDmg accumulates per-card TotalDamage (Damage × Hits) so the mean
        // is E[per-card total damage] regardless of how multi-hit it was. We split
        // back into Damage / Hits at the end so the scorer's per-hit logic
        // (Vulnerable / Weak per-hit floors) still has a sensible value.
        long sumTotalDmg = 0, sumBlock = 0, sumCost = 0, sumHits = 0;
        int attackCount = 0;
        for (int i = 0; i < state.DrawPile.Count; i++)
            Accumulate(state.DrawPile[i], ref sumTotalDmg, ref sumBlock, ref sumCost, ref sumHits, ref attackCount);
        for (int i = 0; i < state.DiscardPile.Count; i++)
            Accumulate(state.DiscardPile[i], ref sumTotalDmg, ref sumBlock, ref sumCost, ref sumHits, ref attackCount);

        // Majority kind wins. Tie → attack (the simulator's attack path is the
        // damage-bearing one; ties usually mean the deck has roughly equal output
        // options and modeling as attack is the less-pessimistic choice).
        bool dominantlyAttack = attackCount * 2 >= total;
        // Hits floor of 1 — a fractional avg under 1 would zero out TotalDamage.
        int avgHits = (int)System.Math.Max(1, sumHits / total);
        int avgTotalDmg = (int)(sumTotalDmg / total);
        // Split TotalDamage back across Hits so Score()'s per-hit logic sees the
        // right per-hit value (Damage × Hits == avgTotalDmg by construction).
        int perHitDmg = avgTotalDmg / avgHits;
        return new SimCard
        {
            Id = "<draw-avg>",
            Cost = (int)System.Math.Max(0, sumCost / total),
            Kind = dominantlyAttack ? CardType.Attack : CardType.Skill,
            Target = dominantlyAttack ? TargetType.AnyEnemy : TargetType.None,
            SourceRef = null,
            Effect = new CardEffectSummary
            {
                Damage = perHitDmg,
                Hits = avgHits,
                Block = (int)(sumBlock / total),
            },
            IsPlayable = true,
        };
    }

    private static void Accumulate(SimCard c, ref long sumTotalDmg, ref long sumBlock,
        ref long sumCost, ref long sumHits, ref int attackCount)
    {
        sumTotalDmg += c.Effect.Damage * System.Math.Max(1, c.Effect.Hits);
        sumBlock += c.Effect.Block;
        sumCost += System.Math.Max(0, c.Cost);
        sumHits += System.Math.Max(1, c.Effect.Hits);
        if (c.IsAttack) attackCount++;
    }

    /// <summary>
    /// Fallback when the pile snapshot is unavailable (capture failed, or in unit-
    /// test fixtures that don't populate piles). 5 dmg / 1 cost — close to starter
    /// average value, keeps lookahead optimistic enough that draw cards aren't
    /// completely ignored.
    /// </summary>
    private static SimCard MakeLegacyPlaceholderCard() => new()
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
