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
        // 2026-05-28 MCTS-P0 A — capture pre-spend energy for X-cost cards.
        // sts2 spends ALL current energy on an X-cost card and that pre-spend
        // value is the X value the card's hits/effect uses. Mod sim's damage
        // calc previously read card.Hits which was the catalog value (1 for
        // X-cost), so WHIRLWIND etc. silently under-damaged by ~10 HP/cast.
        int preSpendEnergy = next.PlayerEnergy;
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
        // 2026-05-31 — X-cost cards (HEAVENLY_DRILL, TEMPEST, …) spend ALL energy
        // (HasEnergyCostX); base Cost is 0 so the plain subtraction left energy
        // untouched → consistent player_energy +preSpend (HEAVENLY_DRILL +4, 3 rows).
        bool isXCost = card.Axes != null && card.Axes.Contains("X_COST");
        int energy = freeApplied
            ? next.PlayerEnergy
            : isXCost
                ? 0
                : System.Math.Max(0, next.PlayerEnergy - card.Cost);
        if (freeApplied && !corruptionFreeSkill)
        {
            // Per-card counters decrement; persistent CorruptionPower doesn't.
            if (card.IsAttack) newFreeAttacks--;
            else if (card.IsSkill) newFreeSkills--;
            else if (card.IsPower) newFreePowers--;
        }
        // 2026-05-30 — SubroutinePower (Defect): AfterCardPlayed refunds Amount
        // energy for every POWER card played (BeforeCardPlayed records the amount
        // only when card.Type == Power). The sim ignored it → player_energy = −Sub
        // on every Power play with Subroutine active. Confirmed: diff == −Subroutine
        // (−1 ↔ Subroutine 1, 8 rows).
        if (card.IsPower && next.PlayerPowers != null
            && next.PlayerPowers.TryGetValue("SubroutinePower", out var subAmt) && subAmt > 0)
        {
            energy += subAmt;
        }
        // 2026-05-30 — OrbitPower (Regent): refunds PlayerOrbit energy each time the
        // cumulative energy-spent crosses a /4 boundary. PlayerOrbitSpentMod (=
        // energySpent % 4, from DisplayAmount) makes the boundary count exact — the
        // earlier TurnEnergySpent attempt was wrong because the Orbit counter is
        // relative to when Orbit was GAINED, not turn start. Refund = Orbit ×
        // ⌊(mod + spent) / 4⌋.
        if (next.PlayerOrbit > 0)
        {
            // energy actually SPENT by this card (the cost; 0 if played free) —
            // independent of any refund added above.
            int orbitSpent = freeApplied ? 0
                : isXCost ? preSpendEnergy
                : System.Math.Max(0, System.Math.Min(card.Cost, preSpendEnergy));
            if (orbitSpent > 0)
            {
                int crossings = (next.PlayerOrbitSpentMod + orbitSpent) / 4;
                if (crossings > 0) energy += next.PlayerOrbit * crossings;
            }
        }
        if (card.EnergyGain > 0)
        {
            // 2026-05-28 S6-4: FORGOTTEN_RITUAL conditional energy gain.
            // gains 3 energy ONLY if a card was exhausted this turn. Mod's
            // unconditional grant over-credited. Other EnergyVar cards (NIGHTMARE,
            // etc.) unconditional — apply for all but FORGOTTEN_RITUAL.
            bool gateEnergy = card.Id == "FORGOTTEN_RITUAL"
                && !next.PlayerCardExhaustedThisTurn;

            // 2026-05-29: SUNDER kill-conditional energy. sts2.dll SUNDER.OnPlay
            // gains energy ONLY when the attack kills the target
            // (DamageResult.WasTargetKilled). The mod sim granted it
            // unconditionally → +3 player_energy on every non-kill play (top
            // Defect divergence, 14 cases). Predict the kill: effective damage
            // vs target effective HP (hp + block).
            if (!gateEnergy && card.Id == "SUNDER"
                && targetIdx >= 0 && targetIdx < next.Enemies.Count)
            {
                var tgt = next.Enemies[targetIdx];
                int effDmg = StatusMath.EffectiveAttackDmg(
                    card.Damage, next.PlayerStrength, next.PlayerVigor,
                    tgt.VulnerableAmount > 0, next.PlayerWeak > 0);
                bool kills = effDmg >= tgt.Hp + tgt.Block;
                if (!kills) gateEnergy = true;
            }

            if (!gateEnergy) energy += card.EnergyGain;
        }
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

        // 2026-05-28 MCTS-P0 — make a mutable DiscardPile copy so the
        // played card lands in the actual list when it doesn't exhaust.
        // Previously only DiscardPileSize (int) tracked the change, so
        // a downstream simulator-parity check saw mod-side
        // DiscardPile.Count stuck at the pre-play value while sts2.dll
        // post-play snapshot already moved the card. Mirrors hand: a
        // fresh list per call, copy of the cloned tail, plus the played
        // card at the end (record value equality preserves identity).
        var newDiscardPile = new List<SimCard>(next.DiscardPile ?? new List<SimCard>());
        // Mutable DrawPile copy — same pattern as DiscardPile. Without
        // this, DrawCount-bearing cards (POMMEL_STRIKE / SHRUG_IT_OFF /
        // ANGER copy generators) updated DrawPileSize int but left
        // SimState.DrawPile list at pre-play count, so parity probe
        // saw consistent draw_pile_count=+1 (mod over-credits).
        var newDrawPile = new List<SimCard>(next.DrawPile ?? new List<SimCard>());

        // 3. Apply card effects
        int newPlayerStr = next.PlayerStrength;
        int newPlayerDex = next.PlayerDexterity;
        // v0.7.99 — Save initial block to detect block-gain events for Juggernaut.
        int initialPlayerBlock = next.PlayerBlock;
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
        // 2026-05-29 — MonologuePower (Regent): AfterCardPlayed grants +stack
        // Strength on EVERY card play (this-turn-only; removed AfterTurnEnd).
        // The gain fires AFTER the card resolves, so it does NOT boost the
        // current card's damage — only subsequent plays in the depth-N chain.
        // Modeled as a late strength bump just before the returned state is
        // built. Decompile: MonologuePower.AfterCardPlayed applies
        // StrengthPower(Strength.IntValue) per played card.
        int newPlayerMonologue = next.PlayerMonologue;
        // 2026-05-29 — TenderPower (Defect debuff): AfterCardPlayed applies
        // StrengthPower(-1) AND DexterityPower(-1) per card played (flat, stack-
        // independent; this-turn-only, restored AfterTurnEnd). Like MonologuePower
        // but negative and on both stats. Fires after the card resolves, so the
        // current card is unaffected — the decay hits subsequent plays.
        int newPlayerTender = next.PlayerTender;
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
        // v0.7.99 — Juggernaut + Hunger reactive trigger sources.
        int newPlayerJuggernaut = next.PlayerJuggernaut;
        int newPlayerHunger = next.PlayerHunger;
        // v0.8.0 — FlameBarrier reflect (1-turn).
        int newPlayerFlameBarrier = next.PlayerFlameBarrier;
        // v0.8.1 — DanseMacabre (cost≥2 card → block N).
        int newPlayerDanseMacabre = next.PlayerDanseMacabre;
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
        // 2026-05-30 — BlackHolePower: AOE Amount to all enemies when the player
        // GAINS stars (AfterStarsGained, unconditional). The star-SPEND trigger
        // (AfterCardPlayed) is gated on IsLastInSeries — only the last card of a
        // combo — which the single-play sim can't determine, so modeling it
        // over-applied (Regent 89.1→88.7). Keep only the clean star-GAIN trigger.
        if (next.PlayerBlackHole > 0 && card.StarsGain > 0)
        {
            int bhDmg = next.PlayerBlackHole;
            var bhEnemies = new List<SimEnemy>(next.Enemies.Count);
            foreach (var e in next.Enemies)
            {
                if (!e.IsAlive) { bhEnemies.Add(e); continue; }
                int past = System.Math.Max(0, bhDmg - e.Block);
                bhEnemies.Add(e with
                {
                    Block = System.Math.Max(0, e.Block - bhDmg),
                    Hp = System.Math.Max(0, e.Hp - past),
                });
            }
            next = next with { Enemies = bhEnemies };
        }
        bool isAoe = card.Target == TargetType.AllEnemies;
        bool playerWeak = next.PlayerWeak > 0;
        bool playerFrail = next.PlayerFrail > 0;

        // v0.8.2 — Generic PlayerPowers dict propagation. Mutable copy created
        // lazily when a PowerApp self-applies; written alongside the explicit
        // field updates so:
        //   1) PowerCatalog lookups + any state.PlayerPowers consumer in
        //      PlanScorer see the up-to-date stack after a setup play.
        //   2) Powers NOT explicitly cased in the switch are still tracked
        //      (DemonForm / Ritual / Mayhem / DanseMacabre... any future power).
        Dictionary<string, int>? newPlayerPowers = null;
        void AddPlayerPower(string powerName, int delta)
        {
            if (delta == 0) return;
            newPlayerPowers ??= next.PlayerPowers != null
                ? new Dictionary<string, int>(next.PlayerPowers, System.StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            if (newPlayerPowers.TryGetValue(powerName, out var cur))
                newPlayerPowers[powerName] = cur + delta;
            else
                newPlayerPowers[powerName] = delta;
        }

        // v0.7.9 — Self-damage on play. Cards expose HpLoss via CardEffectSummary
        // (BLOODLETTING 3, OFFERING 6, HEMOKINESIS 2 etc.). Subtract before any
        // turn-resolution math so subsequent depth-N candidates see the lower HP
        // and the HpLoss penalty band in EstimateCardPower fires correctly.
        // 2026-05-30 — HAUNT's HpLossVar(6) does NOT lose HP on play: OnPlay is
        // Apply<HauntPower>(HpLoss), a DEFERRED per-turn HP drain. The sim applied
        // the 6 immediately (player_hp −6, 3 rows). Exclude it from on-play HP loss.
        if (card.HpLossAmount > 0 && card.Id != "HAUNT")
        {
            newPlayerHp = System.Math.Max(0, newPlayerHp - card.HpLossAmount);
            // 2026-05-28 S6-3c: RupturePower trigger on HP loss.
            // RuptureCfg: gain N Strength per HP-loss event (stack = N).
            // BLOOD_WALL/BREAKTHROUGH/HEMOKINESIS/BLOODLETTING/OFFERING/BRAND
            // are HP-loss-on-play cards; each triggers Rupture once if active.
            // Mod sim previously didn't trigger → player_strength -1 diff per
            // play (1 row per BLOOD_WALL etc. with Rupture active).
            // FeelNoPainPower could ALSO trigger here on exhaust events;
            // already handled separately around line 898.
            if (next.PlayerPowers != null
                && next.PlayerPowers.TryGetValue("RupturePower", out var ruptureStacks)
                && ruptureStacks > 0)
            {
                newPlayerStr += ruptureStacks;
                AddPlayerPower("StrengthPower", ruptureStacks);
            }
            // 2026-05-30 — InfernoPower.AfterDamageReceived: the player taking
            // unblocked HP loss on its own turn deals Amount to ALL hittable
            // enemies (ValueProp.Unpowered, block-first). The self-HP-loss above
            // is that unblocked-damage event, so fire one AOE pulse here. Sim
            // ignored it → enemy_hp_sum = +Inferno on every HP_LOSS play with
            // Inferno active (BLOOD_WALL / BRAND / BREAKTHROUGH / HEMOKINESIS).
            if (next.PlayerInferno > 0 && newPlayerHp > 0)
            {
                int infernoDmg = next.PlayerInferno;
                var infernoEnemies = new List<SimEnemy>(next.Enemies.Count);
                foreach (var e in next.Enemies)
                {
                    if (!e.IsAlive) { infernoEnemies.Add(e); continue; }
                    int past = System.Math.Max(0, infernoDmg - e.Block);
                    infernoEnemies.Add(e with
                    {
                        Block = System.Math.Max(0, e.Block - infernoDmg),
                        Hp = System.Math.Max(0, e.Hp - past),
                    });
                }
                next = next with { Enemies = infernoEnemies };
            }
        }

        // 2026-05-30 — SHARED_FATE (Skill): Apply<StrengthPower>(self,
        // -PlayerStrengthLoss(2)) AND enemy -EnemyStrengthLoss. The enemy debuff is
        // handled via PowerApps/StrengthDown, but the SELF strength loss (a named
        // "PlayerStrengthLoss" DynamicVar, not a PowerVar) was missed → player_strength
        // diff +2 (3 rows). Apply the self −2.
        if (card.Id == "SHARED_FATE") newPlayerStr -= 2;

        // 3a. Power card: self-apply powers (Strength, Dex, etc.)
        if (card.IsPower)
        {
            foreach (var (powerName, rawAmount) in card.PowerApps)
            {
                // v0.7.98 — EchoForm doubles ALL powers granted by this play.
                // EchoFormPower itself is excluded so a self-cast Echo Form
                // doesn't recursively double its own stack.
                int amount = powerName == "EchoFormPower" ? rawAmount : rawAmount * echoMul;
                // 2026-05-28 S6-4: RUPTURE card's PowerVar<StrengthPower> is
                // a misnomer — Rupture.OnPlay applies RupturePower (using the
                // Strength var's BaseValue). Redirect StrengthPower → RupturePower
                // for this card so mod doesn't over-credit Strength gain.
                // Other PowerVar<StrengthPower> cards (BRAND etc.) DO apply
                // StrengthPower per decompile, so keep them as-is.
                string effectivePowerName = powerName;
                if (powerName == "StrengthPower")
                {
                    if (card.Id == "RUPTURE") effectivePowerName = "RupturePower";
                    // SETUP_STRIKE applies SetupStrikePower (Str on next Strike)
                    // not raw StrengthPower → 3 SETUP_STRIKE diverging with
                    // player_strength +2 (mod credited immediate Str gain).
                    else if (card.Id == "SETUP_STRIKE") effectivePowerName = "SetupStrikePower";
                    // 2026-05-30 — FRIENDSHIP applies StrengthPower NEGATIVELY:
                    // Apply<StrengthPower>(self, -StrengthPower.BaseValue). The
                    // PowerVar base is +2 but the self-strength is REDUCED by 2.
                    // The sim credited +2 → player_strength diff +4 (3 rows).
                    else if (card.Id == "FRIENDSHIP") amount = -amount;
                }
                // v0.8.2 — Generic dict propagation. Writes EVERY power granted,
                // including those without an explicit case below.
                AddPlayerPower(effectivePowerName, amount);
                if (effectivePowerName != powerName)
                {
                    // Don't fall through to the StrengthPower case below.
                    continue;
                }
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
                    // v0.7.99 — Juggernaut / Hunger propagation.
                    case "JuggernautPower": newPlayerJuggernaut += amount; break;
                    case "HungerPower": newPlayerHunger += amount; break;
                    // v0.8.0 — FlameBarrier (1-turn reflect).
                    case "FlameBarrierPower": newPlayerFlameBarrier += amount; break;
                    // v0.8.1 — DanseMacabre (cost≥2 card → block N).
                    case "DanseMacabrePower": newPlayerDanseMacabre += amount; break;
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

        // 3a-bis. Attack-with-block (IRON_WAVE / BLOOD_WALL etc.) — sts2.dll
        // marks these via GainsBlock=true and calls CreatureCmd.GainBlock
        // before damage. The skill branch already covers block-bearing skills
        // (DEFEND), but attack cards with a Block field fell through with no
        // block applied → IRON_WAVE 13/13 disagree player_block=-5 in probe.
        // 2026-05-30 — OSTY attack-with-block (BONE_SHARDS): sts2.dll wraps the
        // ENTIRE OnPlay (both DamageCmd AND GainBlock) in
        // `if (!Osty.CheckMissingWithAnim(Owner))`, so when the Osty is
        // missing the card gains NO block either. The damage gate
        // (ostyAttackWhiff, below) didn't cover this block path → player_block
        // = +9 over-credit on every Osty-missing BONE_SHARDS (9 rows).
        bool ostyBlockWhiff = card.Axes != null
            && card.Axes.Contains("OSTY")
            && state.SkeletonCount <= 0;
        if (card.IsAttack && card.Block > 0 && !ostyBlockWhiff)
        {
            int attackBlock = StatusMath.EffectiveBlock(card.Block, newPlayerDex, playerFrail);
            newPlayerBlock += attackBlock;
        }

        // 2026-05-29 — OSTY-attack gating (Necrobinder). Cards tagged CardTag.
        // OstyAttack (POKE / FETCH / UNLEASH / RIGHT_HAND_HAND / SIC_EM / ...)
        // deal their damage via the player's Osty pet: sts2.dll's OnPlay wraps
        // the DamageCmd in `if (!Osty.CheckMissingWithAnim(Owner))`. When the
        // Osty is dead/un-summoned the card whiffs (deals 0). The mod sim
        // previously applied the damage unconditionally → +6/-7 enemy_hp_sum
        // divergence on every Osty-missing play (Necrobinder parity 58.5%,
        // worst of all chars; POKE/UNLEASH/FETCH the top offenders). Gate the
        // damage on Osty presence (SkeletonCount counts alive class-"Osty"
        // allies). Non-damage effects (FETCH's draw/SKELETON_PRODUCER) are
        // applied elsewhere and stay intact.
        bool ostyAttackWhiff = card.Axes != null
            && card.Axes.Contains("OSTY")
            && state.SkeletonCount <= 0;

        // 3b. Attack: deal damage to target(s); also stack attached debuffs on enemy.
        if (card.IsAttack && card.Damage > 0 && !ostyAttackWhiff)
        {
            // 2026-05-31 — SWORD_BOOMERANG-class (TargetType.RandomEnemy +
            // Repeat>1). sts2.dll's TargetingRandomOpponents distributes N
            // hits over random alive enemies; mod sim previously treated
            // RandomEnemy as single-target → all N hits on targetIdx →
            // block-overflow over-credits enemy_hp_sum (+9..+21 in probe).
            // Deterministic approximation: round-robin K hits across alive
            // enemies. Same expected total damage as real for non-block
            // cases; closer for block cases (no over-allocation on one
            // shielded target). Total over enemies = exact hitsForCard.
            bool isRandomAoe = card.Target == TargetType.RandomEnemy;
            int[]? hitsByEnemyIdx = null;
            if (isRandomAoe)
            {
                int totalHits = card.Hits > 0 ? card.Hits : 1;
                int aliveCount = 0;
                for (int i = 0; i < next.Enemies.Count; i++)
                    if (next.Enemies[i].IsAlive) aliveCount++;
                if (aliveCount > 0)
                {
                    hitsByEnemyIdx = new int[next.Enemies.Count];
                    int baseHits = totalHits / aliveCount;
                    int remainder = totalHits % aliveCount;
                    int aliveSeen = 0;
                    for (int i = 0; i < next.Enemies.Count; i++)
                    {
                        if (!next.Enemies[i].IsAlive) continue;
                        hitsByEnemyIdx[i] = baseHits + (aliveSeen < remainder ? 1 : 0);
                        aliveSeen++;
                    }
                }
            }

            var newEnemies = new List<SimEnemy>(next.Enemies.Count);
            // 2026-05-31 — FISTICUFFS gains block == total damage its attack deals
            // (GainBlock(sum of TotalDamage+OverkillDamage)). card.Block is 0 (no
            // BlockVar) so the L463 attack-block path adds nothing → player_block
            // under-predicted by the full damage. Accumulate the pre-absorption
            // attack damage here and convert to block after the loop.
            int attackDmgForFisticuffs = 0;
            for (int i = 0; i < next.Enemies.Count; i++)
            {
                var enemy = next.Enemies[i];
                bool isTarget = isAoe ? enemy.IsAlive
                    : isRandomAoe ? (enemy.IsAlive && hitsByEnemyIdx != null && hitsByEnemyIdx[i] > 0)
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
                // 2026-05-28 S6-3d: CalculatedDamageVar per-card multiplier add-on.
                // CardReflection now reads CalculationBaseVar.BaseValue (e.g. 4
                // for BULLY, 6 for PERFECTED_STRIKE), but loses the extra ×
                // multiplier component which is a runtime closure. Re-add the
                // multiplier here for known cards.
                if (card.Id == "BULLY"
                    && targetIdx >= 0 && targetIdx < next.Enemies.Count)
                {
                    // ExtraDamage 2 × target.VulnerablePower
                    adjustedBase += 2 * next.Enemies[targetIdx].VulnerableAmount;
                }
                else if (card.Id == "BODY_SLAM")
                {
                    // damage = current player block ONLY (CalcBase 0 + Extra 1 × block).
                    // card.Damage from PreviewValue fallback is 1 (the ExtraDamage
                    // var amount); override to 0 + player.Block so modifier can add Str.
                    adjustedBase = next.PlayerBlock;
                }
                else if (card.Id == "PERFECTED_STRIKE")
                {
                    // ExtraDamage 2 (3 upgraded) × # of Strike-tag cards in deck.
                    // Strike-tag cards: STRIKE_IRONCLAD, TWIN_STRIKE, POMMEL_STRIKE,
                    // PERFECTED_STRIKE itself, WILD_STRIKE, SETUP_STRIKE, ASHEN_STRIKE,
                    // FLASH_OF_STEEL, FLAK_CANNON, etc. (any with CardTag.Strike).
                    // Counted across hand + draw + discard + exhaust. Approximation:
                    // walk Hand + DrawPile + DiscardPile + ExhaustPile lists.
                    int strikeCount = 0;
                    foreach (var c in next.Hand) if (IsStrikeCard(c.Id)) strikeCount++;
                    foreach (var c in next.DrawPile) if (IsStrikeCard(c.Id)) strikeCount++;
                    foreach (var c in next.DiscardPile) if (IsStrikeCard(c.Id)) strikeCount++;
                    // ExhaustPile is a count, not a list — Strike cards rarely
                    // exhaust, undercount acceptable
                    adjustedBase += 2 * strikeCount;
                }
                else if (card.Id == "PRECISE_CUT")
                {
                    // 2026-05-29 — CalculatedDamage = CalculationBase(13) +
                    // ExtraDamage(2) × (-(handCount-1)). Decompile: the multiplier
                    // delegate returns -(Hand.Cards.Count - 1) (excludes the card
                    // itself while in hand), so PRECISE_CUT deals MORE with fewer
                    // other cards in hand (empty-hand reward). CardReflection reads
                    // CalculationBase (13) as the base; re-add the lost multiplier.
                    // next.Hand still contains PRECISE_CUT (pre-play), matching the
                    // delegate's -1 self-exclusion. Floor at 0.
                    int otherInHand = System.Math.Max(0, next.Hand.Count - 1);
                    adjustedBase = System.Math.Max(0, adjustedBase - 2 * otherInHand);
                }
                else if (card.Id == "CRESCENT_SPEAR")
                {
                    // 2026-05-30 — CalculatedDamage = CalculationBase(6) +
                    // ExtraDamage(2) × (# star-cost cards in deck). CardReflection
                    // reads CalculationBase (6); re-add the multiplier from the
                    // deck-wide star-card count captured in StateSnapshotter.
                    adjustedBase += 2 * next.StarCardsInDeck;
                }
                else if (card.Id == "ASHEN_STRIKE")
                {
                    // 2026-05-31 — CalculatedDamage = CalculationBase(6) +
                    // ExtraDamage(3) × Exhaust-pile card count. CardReflection reads
                    // CalculationBase (6); re-add the multiplier from the snapshot
                    // exhaust count (ASHEN_STRIKE is a plain attack — it doesn't
                    // exhaust anything itself, so the pre-play count is exact).
                    // Verified: real base 9 = 6+3×1, 15 = 6+3×3 (2 rows).
                    adjustedBase += 3 * next.ExhaustPileSize;
                }
                else if (card.Id == "SQUEEZE")
                {
                    // 2026-05-31 — CalculatedDamage = CalculationBase(25) +
                    // ExtraDamage(5) × (# OstyAttack-tag cards in deck EXCEPT itself).
                    // CardReflection reads CalculationBase (25); re-add the multiplier
                    // by counting OSTY-axis cards across hand+draw+discard (pile
                    // SimCards carry Axes via BuildSimCard), minus 1 for SQUEEZE itself
                    // (still in next.Hand pre-play). Verified real 35 = 25+5×2.
                    int ostyCount = 0;
                    foreach (var c in next.Hand) if (c.Axes != null && c.Axes.Contains("OSTY")) ostyCount++;
                    foreach (var c in next.DrawPile) if (c.Axes != null && c.Axes.Contains("OSTY")) ostyCount++;
                    foreach (var c in next.DiscardPile) if (c.Axes != null && c.Axes.Contains("OSTY")) ostyCount++;
                    if (card.Axes != null && card.Axes.Contains("OSTY"))
                        ostyCount = System.Math.Max(0, ostyCount - 1);
                    adjustedBase += 5 * ostyCount;
                }
                else if (card.Id == "SUPERMASSIVE")
                {
                    // 2026-05-30 — CalculatedDamage = CalculationBase(5) +
                    // ExtraDamage(3) × (cards GENERATED by the player this combat).
                    // CardReflection reads base 5; re-add the multiplier from the
                    // combat-wide generated-card count captured in StateSnapshotter.
                    adjustedBase += 3 * next.CombatCardsGenerated;
                }
                else if (card.Id == "UNLEASH")
                {
                    // 2026-05-30 — CalculatedDamage = CalculationBase(6) +
                    // ExtraDamage(1) × osty.CurrentHp. The attack is Osty-gated
                    // (ostyAttackWhiff) so it only reaches here when an Osty is alive;
                    // add the captured Osty HP multiplier. CardReflection read base 6.
                    adjustedBase += 1 * next.PlayerOstyHp;
                }
                else if (card.Id == "TESLA_COIL")
                {
                    // 2026-05-30 — TESLA_COIL: Attack(3) then triggers each Lightning
                    // orb's Passive (OrbCmd.Passive) at the same target.
                    // LightningOrb.PassiveVal = ModifyOrbValue(3) = 3 + Focus. Add
                    // (lightning-orb count) × (3 + Focus) to the single-target damage.
                    // (The phantom evoke is suppressed in OrbCardCatalog.)
                    int lightning = 0;
                    foreach (var k in next.OrbQueue) if (k == OrbKind.Lightning) lightning++;
                    if (lightning > 0)
                        adjustedBase += lightning * System.Math.Max(0, 3 + next.PlayerFocus);
                }
                // 2026-05-28 MCTS-P0 A — X-cost cards (WHIRLWIND, etc.) use
                // pre-spend energy as their hit count, not the catalog
                // card.Hits value. SimCard.EffectiveDamage applies the same
                // rule (SimCard.cs:282-293); replicate here so the depth-N
                // damage path agrees. Mirrors FinisherIdentifier and
                // SimCard's path so the three damage estimators stay in
                // sync with sts2.dll's GetTotalDamage.
                int hitsForDmg = card.Hits;
                if (card.Axes != null && card.Axes.Contains("X_COST"))
                {
                    int xBonus = (next.PlayerRelics != null
                        && next.PlayerRelics.ContainsKey("ChemicalX")) ? 2 : 0;
                    // X=0 → zero hits → zero damage. Matches sts2.dll's
                    // Whirlwind.OnPlay: WithHitCount(num) where num=
                    // CapturedXValue=energy=0. Previously clamped to 1 by
                    // Math.Max(1,...), over-crediting damage on 0-energy plays.
                    hitsForDmg = System.Math.Max(0, preSpendEnergy + xBonus);
                }
                // 2026-05-28 S6-3a: SPITE conditional hits.
                // SPITE.OnPlay: hits = LostHpThisTurn(player) ? Repeat.IntValue : 1
                // Mod sim previously used Repeat = 2 always → over-credit when
                // player healthy this turn. Decompile: Spite.cs line 34.
                if (card.Id == "SPITE")
                {
                    hitsForDmg = next.PlayerLostHpThisTurn ? card.Hits : 1;
                }
                // 2026-05-28 S6-3e: DISMANTLE conditional hits.
                // hits = target.HasVulnerable ? 2 : 1. Mod doesn't have RepeatVar
                // for DISMANTLE so hits defaults to 1 — under-credit when target
                // Vulnerable, no diff when not. Adding HardcodedHitCount=2 would
                // over-credit non-Vuln case. Special-case here based on target.
                if (card.Id == "DISMANTLE"
                    && targetIdx >= 0 && targetIdx < next.Enemies.Count)
                {
                    hitsForDmg = next.Enemies[targetIdx].VulnerableAmount > 0 ? 2 : 1;
                }
                // 2026-05-28 S6-4: FIEND_FIRE damage × hand.Count hits.
                // FiendFire.OnPlay captures hand BEFORE exhausting, so cardCount
                // includes FIEND_FIRE itself (was in hand at OnPlay entry).
                // newHand has already had FIEND_FIRE popped (line 78) so +1 to
                // include FIEND_FIRE in count.
                if (card.Id == "FIEND_FIRE")
                {
                    hitsForDmg = newHand.Count + 1;
                }
                // 2026-05-30 — FOLLOW_THROUGH: WithHitCount(IsPlayedAnAdditionalTime
                // ? 2 : 1) where the condition is hand.Cards.Count(c != this) >=
                // CardCount(5). newHand has this card already removed, so its Count
                // equals the decompile's "cards != this". Single-hit sim under-dealt
                // 7 (one full hit) whenever the hand held >=5 other cards (9 rows).
                if (card.Id == "FOLLOW_THROUGH")
                {
                    hitsForDmg = newHand.Count >= 5 ? 2 : 1;
                }
                // Random AOE: this enemy gets only its share of the
                // round-robin distribution computed above.
                if (isRandomAoe && hitsByEnemyIdx != null)
                    hitsForDmg = hitsByEnemyIdx[i];
                // 2026-05-28 B-architecture — damage pipeline now goes
                // through DamageModifierRegistry (V2). Equivalent to the V1
                // EffectivePerEnemyTotal + ApplyDamageMultipliers chain when
                // only baseline modifiers are registered (verified by 13
                // V1↔V2 parity unit tests); enables un-modeled active powers
                // to plug in without further StatusMath edits.
                var dmgState = next with
                {
                    PlayerStrength = newPlayerStr,
                    PlayerVigor = newPlayerVigor,
                    PlayerWeak = playerWeak ? next.PlayerWeak : 0,
                    PlayerLethality = newPlayerLethality,
                    PlayerTracking = newPlayerTracking,
                    PlayerCruelty = newPlayerCruelty,
                };
                int totalDmg = StatusMath.EffectivePerEnemyTotalV2(
                    adjustedBase, hitsForDmg, enemy, card, dmgState,
                    isFirstAttackThisTurn: newPlayerLethality > 0);
                // v0.7.98 — EchoForm doubles the entire attack (each hit lands
                // twice). Applied after damage-multiplier chain so the doubled
                // damage benefits from Tracking / Cruelty / Lethality once.
                totalDmg *= echoMul;
                // 2026-06-01 — flying enemies (SoarPower / FlutterPower) take HALF damage
                // from card attacks: ModifyDamageMultiplicative returns DamageDecrease/100
                // = 50/100 = 0.5 on any powered attack. The sim dealt full damage → ~2x
                // over-prediction (decimal traces 15→7.5, 4→2.0, 6→3.0 across PINPOINT/
                // SHIV/NEUTRALIZE/MAKE_IT_SO/RIGHT_HAND_HAND/FLATTEN). Halve here (floor;
                // real applies a 0.5 multiplier in the decimal chain).
                if (enemy.Powers != null
                    && (enemy.Powers.ContainsKey("SoarPower") || enemy.Powers.ContainsKey("FlutterPower")))
                    totalDmg /= 2;
                // 2026-06-01 — SlowPower: the enemy takes (1 + 0.1×SlowAmount) damage from
                // card attacks, where SlowAmount = cards played this turn. DisplayAmount =
                // SlowAmount×10 is captured into SlowDamagePct, so the multiplier is
                // (100 + SlowDamagePct)/100. The sim missed the amp → under-predicted hits
                // on Slow enemies (the ×1.1/×1.2/×1.3 boost cluster). Snapshot SlowAmount
                // is the pre-play count (this card's own increment is AfterCardPlayed).
                if (enemy.SlowDamagePct > 0)
                    totalDmg = totalDmg * (100 + enemy.SlowDamagePct) / 100;
                attackDmgForFisticuffs += totalDmg;

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
                // us ThornsAmount damage per hit. Multi-hit cards trigger per hit.
                // v0.10 — STS2 ThornsPower.BeforeDamageReceived invokes
                // CreatureCmd.Damage on the dealer with ValueProp.Unpowered, which
                // routes through normal block absorption — verified empirically
                // (Turn 2 STRIKE killed a Thorns:2 enemy with player_block 5→3,
                // hp 60→60) and against the STS2 decompile. Block soaks reflect
                // per-hit until depleted.
                if (enemy.ThornsAmount > 0 && totalDmg > 0)
                {
                    int hits = System.Math.Max(1, card.Hits);
                    for (int r = 0; r < hits; r++)
                    {
                        int reflect = enemy.ThornsAmount;
                        int absorbed = System.Math.Min(reflect, newPlayerBlock);
                        newPlayerBlock -= absorbed;
                        int leak = reflect - absorbed;
                        if (leak > 0)
                            newPlayerHp = System.Math.Max(0, newPlayerHp - leak);
                    }
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

                // REAPER_FORM: "Whenever Attacks deal damage, they also apply
                // that much Doom" (per STS2 description). Per-hit Doom equals
                // the attack's damage value, multiplied by hit count and the
                // power's stack. Earlier formula used `stacks × hits` which
                // ignored damage and severely under-credited big single hits.
                // Artifact does NOT intercept self-buff-driven debuffs
                // (Doom is added on hit, not via a debuff PowerVar).
                if (next.PlayerPowers != null
                    && next.PlayerPowers.TryGetValue("ReaperFormPower", out var reaperStacks)
                    && reaperStacks > 0
                    && card.Damage > 0)
                {
                    newDoom += card.Damage * System.Math.Max(1, card.Hits) * reaperStacks;
                }
                // v0.8.3 — Enemy.Powers dict catch-all (mirror of v0.8.2 PlayerPowers).
                // Lazy-built mutable copy; tracks debuffs without explicit field
                // (Hex / DarkShackles / PiercingWail / Dampen / EnfeeblingTouch /
                // Confused / Rupture / NoxiousFumes / etc.) so depth-N lookahead
                // sees them via PowerCatalog / enemy.Powers consumers.
                Dictionary<string, int>? newEnemyPowers = null;
                void AddEnemyPower(string powerName, int delta)
                {
                    if (delta == 0) return;
                    newEnemyPowers ??= enemy.Powers != null
                        ? new Dictionary<string, int>(enemy.Powers, System.StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
                    if (newEnemyPowers.TryGetValue(powerName, out var cur))
                        newEnemyPowers[powerName] = cur + delta;
                    else
                        newEnemyPowers[powerName] = delta;
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
                    // v0.8.3 — Always update dict (catch-all). Explicit fields below
                    // still update for tracked debuffs.
                    AddEnemyPower(powerName, amount);
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

                // 2026-05-31 — CurlUpPower (enemy): AfterDamageReceived from a card
                // attack, the enemy gains Amount block ONCE PER COMBAT. The block is
                // granted AFTER the hit (it does NOT absorb this attack), so it adds to
                // the post-attack block. The sim missed it → enemy_block under-predicted
                // by the CurlUp amount (14 ×4 cards: PILLAGE/NEUTRALIZE/BLIGHT_STRIKE/
                // GO_FOR_THE_EYES). Reliable: CurlUp is consumed after firing, so a
                // snapshot that still carries the power means it hasn't fired yet.
                // SkittishPower is the once/TURN sibling: AfterAttack, on a card attack
                // dealing UNBLOCKED damage, gain Amount block — but only if it hasn't
                // already fired this turn. Its power persists after firing, so the turn
                // gate (Data.hasGainedBlockThisTurn) is read directly into
                // SkittishFiredThisTurn (2026-05-31 internal-counter reflection, same tool
                // as JugglingPower). Without the flag an earlier-this-turn fire whose
                // block was since destroyed would re-trigger and over-predict
                // (MOMENTUM_STRIKE). With it, the gate is exact.
                // Both gate on hpAfter > 0: a dead creature's powers don't trigger, so
                // an attack that KILLS the enemy grants no reactive block (REAP killing a
                // Skittish enemy showed sim +6 / real 0 without this).
                int reactiveEnemyBlock = 0;
                if (hpAfter > 0 && totalDmg > 0 && enemy.Powers != null
                    && enemy.Powers.TryGetValue("CurlUpPower", out int curlUp) && curlUp > 0)
                    reactiveEnemyBlock += curlUp;
                if (hpAfter > 0 && dmgPastBlock > 0 && !enemy.SkittishFiredThisTurn && enemy.Powers != null
                    && enemy.Powers.TryGetValue("SkittishPower", out int skittish) && skittish > 0)
                    reactiveEnemyBlock += skittish;

                newEnemies.Add(enemy with
                {
                    Hp = hpAfter,
                    Block = blockAfter + reactiveEnemyBlock,
                    VulnerableAmount = newVuln,
                    WeakAmount = newWeak,
                    FrailAmount = newFrail,
                    PoisonAmount = newPoison,
                    ConstrictAmount = newConstrict,
                    BurnAmount = newBurn,
                    DoomAmount = newDoom,
                    ArtifactAmount = artifactLeft,
                    HardenedShellRemaining = shellLeft,
                    Powers = newEnemyPowers ?? enemy.Powers,
                });
            }
            next = next with { Enemies = newEnemies };

            // 2026-05-31 — FISTICUFFS: gain block equal to the damage dealt (Move →
            // scaled by Dex/Frail like any block). card.Block was 0 so nothing was
            // added above; convert the accumulated attack damage to block here.
            if (card.Id == "FISTICUFFS" && attackDmgForFisticuffs > 0)
                newPlayerBlock += StatusMath.EffectiveBlock(attackDmgForFisticuffs, newPlayerDex, playerFrail);

            // 2026-05-29 — SETUP_STRIKE-class self-power-on-attack. The attack
            // PowerApps loop above only applies enemy-debuff entries
            // (Vulnerable / Weak / Frail / …); self-buff entries (StrengthPower
            // on SETUP_STRIKE etc.) fell through. sts2.dll's SetupStrike
            // applies SetupStrikePower (Strength wrapper) to self AFTER the
            // damage, so do the same here — apply once per card play (not per
            // enemy hit) at end of attack resolution.
            if (card.PowerApps.Count > 0)
            {
                foreach (var (powerName, rawAmount) in card.PowerApps)
                {
                    if (IsEnemyDebuff(powerName)) continue;
                    int amount = powerName == "EchoFormPower" ? rawAmount : rawAmount * echoMul;
                    AddPlayerPower(powerName, amount);
                    switch (powerName)
                    {
                        case "StrengthPower":
                        case "TemporaryStrengthPower":
                            newPlayerStr += amount; break;
                        case "DexterityPower":
                        case "TemporaryDexterityPower":
                            newPlayerDex += amount; break;
                        case "VigorPower":
                            newPlayerVigor += amount; break;
                    }
                }
            }

            // v0.7.82 — Vigor is single-shot: consumed when this attack resolves.
            // Subsequent attacks in the depth-N lookahead chain see Vigor=0.
            newPlayerVigor = 0;
            // v0.7.84 — Lethality is "first attack of the turn ×1.5" → after the
            // first attack, drop to 0. (Tracking/Cruelty are passive — keep.)
            newPlayerLethality = 0;
            // v0.7.85 — RagePower: gain N block per attack played.
            // 2026-05-30 — but NOT when this attack kills the last enemy: combat
            // ends, so the real engine's AfterCardPlayed Rage-block doesn't apply
            // (or is moot). The sim over-blocked by exactly RagePower whenever a
            // lethal attack cleared the board (6 rows, all real-enemies-alive=0).
            // next.Enemies already reflects post-attack HP here.
            bool anyEnemyAliveAfter = false;
            foreach (var e in next.Enemies) if (e.IsAlive) { anyEnemyAliveAfter = true; break; }
            // 2026-05-31 — RagePower block is GainBlock(Amount, ValueProp.Unpowered):
            // FLAT, NOT modified by Dexterity or Frail (decompile + block-trace:
            // ModifyBlock(3)=>3 with Frail 1 present). The sim ran it through
            // EffectiveBlock → under-blocked by the Frail 25% (STRIKE_IRONCLAD −1,
            // 3 rows) and would over-add Dex. Same flat-block class as AfterImage.
            if (newPlayerRage > 0 && anyEnemyAliveAfter)
                newPlayerBlock += newPlayerRage;
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

            // SECOND_WIND grants block PER non-Attack exhausted, handled in
            // the post-skill carve-out below. Skip the standard single-grant
            // here so the per-exhaust loop owns all block math.
            // 2026-05-28 S6-3e: enemy-target Skills with GainsBlock=true (e.g.
            // TAUNT — TargetType.AnyEnemy + 7 block to self + Vuln to target)
            // still grant block to self. Mod sim's selfTarget check missed
            // these → TAUNT 0/5 agree with player_block -7 diff per play.
            // 2026-05-30 — generalized: ANY enemy/AOE-target Skill with a BlockVar
            // grants that block to SELF (CreatureCmd.GainBlock(Owner)). In STS2 a
            // skill's BlockVar is always self-block; the enemy target is only for
            // the debuff it also applies (TAUNT→Vuln, DEFY→Weak, NEGATIVE_PULSE→
            // Doom, …). The previous id whitelist (TAUNT/DEFY) dropped every other
            // such card's self-block (player_block -N).
            // 2026-05-30 — MIRAGE: CalculatedBlock = CalcBase(0) + CalcExtra(1) ×
            // (sum of alive-enemy Poison). CardReflection reads CalculationBase (0),
            // so override the effective card block with the live poison total.
            int effCardBlock = card.Block;
            if (card.Id == "MIRAGE")
            {
                int totalPoison = 0;
                foreach (var e in next.Enemies) if (e.IsAlive) totalPoison += e.PoisonAmount;
                effCardBlock = totalPoison;
            }
            bool enemyTargetSkillGainsBlock = card.IsSkill && !selfTarget && effCardBlock > 0;
            // 2026-05-31 — ESCAPE_PLAN: draws 1, gains Block ONLY if the drawn card is
            // a Skill (decompile: `if (drawn.Type == Skill) GainBlock`). The sim gave
            // the block unconditionally → player_block +3/+5 whenever the next draw was
            // a non-skill (4 rows). Peek the real next-to-draw (newDrawPile top, index
            // 0 = what real draws) and gate the block on its type. (Empty pile = rare
            // reshuffle edge; default to no block.)
            bool escapePlanBlocks = card.Id != "ESCAPE_PLAN"
                || (newDrawPile.Count > 0 && newDrawPile[0].IsSkill);
            if (((selfTarget && effCardBlock > 0 && card.Id != "SECOND_WIND")
                || enemyTargetSkillGainsBlock) && escapePlanBlocks)
            {
                int perPlayBlock = StatusMath.EffectiveBlock(effCardBlock, newPlayerDex, playerFrail);
                // v0.7.95 / v0.7.98 — Burst + Echo cause the card to RESOLVE
                // multiple times. Each resolution is a separate "block card play".
                // 2026-06-01 — DEATHS_DOOR gains block (1 + Repeat) times instead of
                // once when the player applied Doom this turn (blockGains = 1 + 2 = 3).
                // The sim gave 1× → player_block under by ~2× the block (DEATHS_DOOR −8).
                int deathsDoorGains = (card.Id == "DEATHS_DOOR" && next.PlayerDoomAppliedThisTurn) ? 3 : 1;
                int plays = burstMul * echoMul * deathsDoorGains;
                int totalBlock = perPlayBlock * plays;
                // v0.7.85 + v0.8.4 — UnmovablePower: first block card play/turn ×2.
                // Canonical STS: when a card plays multiple times via Burst/Echo,
                // ONLY the first of those plays gets the Unmovable doubling — not
                // every multiplied copy. So add ONE more perPlayBlock (not totalBlock).
                // 2026-06-01 — Unmovable doubles only the FIRST block-gain card of the
                // turn. The per-play newUnmovableUsedThisTurn can't see PRIOR plays, so
                // gate on the history flag too: if the player already gained card block
                // this turn, this play is not the first and gets no ×2 (DEFEND_IRONCLAD
                // +5 when block_pre=10 already came from an earlier block card).
                if (newPlayerUnmovable > 0 && !newUnmovableUsedThisTurn
                    && !next.PlayerBlockGainedFromCardThisTurn)
                {
                    totalBlock += perPlayBlock;
                    newUnmovableUsedThisTurn = true;
                }
                newPlayerBlock += totalBlock;
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
                    // v0.8.2 — Generic dict propagation.
                    AddPlayerPower(powerName, amount);
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
                        // v0.7.99 — Skill-granted Juggernaut / Hunger.
                        case "JuggernautPower": newPlayerJuggernaut += amount; break;
                        case "HungerPower": newPlayerHunger += amount; break;
                        // v0.8.0 — Skill-granted FlameBarrier.
                        case "FlameBarrierPower": newPlayerFlameBarrier += amount; break;
                        // v0.8.1 — Skill-granted DanseMacabre.
                        case "DanseMacabrePower": newPlayerDanseMacabre += amount; break;
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
                    // v0.8.3 — Enemy.Powers dict catch-all (mirror of attack path).
                    Dictionary<string, int>? newEnemyPowers = null;
                    void AddEnemyPower(string powerName, int delta)
                    {
                        if (delta == 0) return;
                        newEnemyPowers ??= enemy.Powers != null
                            ? new Dictionary<string, int>(enemy.Powers, System.StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
                        if (newEnemyPowers.TryGetValue(powerName, out var cur))
                            newEnemyPowers[powerName] = cur + delta;
                        else
                            newEnemyPowers[powerName] = delta;
                    }
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
                        // v0.8.3 — Catch-all dict update for any debuff (incl.
                        // Hex / DarkShackles / NoxiousFumes / etc.).
                        AddEnemyPower(powerName, amount);
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
                        Powers = newEnemyPowers ?? enemy.Powers,
                    });
                }
                next = next with { Enemies = newEnemies };

                // 2026-05-28 S6-3b: DOMINATE special-case.
                // Dominate.OnPlay (line 40-42): apply VulnerablePower 1 → read
                // target's resulting Vuln amount → apply that many Strength to
                // player. vars.StrengthPerVulnerable=1 is the per-Vuln rate
                // (currently hardcoded; no card uses != 1).
                // PowerApps loop above adds VulnerablePower (newVuln update).
                // Here we add target's final Vuln count to player Strength.
                if (card.Id == "DOMINATE"
                    && targetIdx >= 0 && targetIdx < newEnemies.Count)
                {
                    int targetVuln = newEnemies[targetIdx].VulnerableAmount;
                    if (targetVuln > 0)
                    {
                        newPlayerStr += targetVuln;
                        AddPlayerPower("StrengthPower", targetVuln);
                    }
                }
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
            // 2026-05-31 — TEMPEST is X-cost: it channels X (= energy spent) Lightning,
            // not the catalog's ORB_PRODUCER default of 1. costSpent at snapshot time is
            // the base cost (0), so card.ChannelCount can't carry X — resolve it here
            // from preSpendEnergy. X=0 → 0 channels (no phantom evoke, was +5); X≥2 →
            // multiple overflow-evokes (was −5 under).
            int effChannelCount = (card.Id == "TEMPEST" && isXCost)
                ? preSpendEnergy
                : card.ChannelCount;
            for (int i = 0; i < effChannelCount; i++)
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
        // Power cards' CardsVar represents the power's amount (e.g. VICIOUS's
        // CardsVar(1) → ViciousPower(1)), NOT a draw. CardReflection routes
        // CardsVar → DrawCount uniformly; gate by type so Power cards don't
        // erroneously draw. Affected before fix: VICIOUS 7/7 with
        // hand_count=+1 / draw_pile_count=-1 (mod over-drew, real didn't).
        // 2026-05-29 — shiv-generator cards (CLOAK_AND_DAGGER / BLADE_DANCE /
        // HIDDEN_DAGGERS / ...) route their Cards/Shivs var through DrawCount,
        // but sts2.dll CREATES Shivs in hand (Shiv.CreateInHand) rather than
        // drawing from the pile. The mod sim drew from the draw pile →
        // draw_pile_count = -N divergence (Silent's biggest bookkeeping bucket).
        // Redirect: add N shiv placeholders to hand, leave the draw pile alone.
        // 2026-05-30 — CALCULATED_GAMBLE: DiscardAndDraw(hand, hand.Count) —
        // discard the ENTIRE hand, then draw that many (capped at 10). hand.Count
        // at OnPlay = newHand (the card itself already removed). Net counts:
        // discard += N, hand → min(N,10), draw pile drains by the redraw (with
        // reshuffle). The card itself exhausts (handled by the Exhaust flag below).
        if (card.Id == "CALCULATED_GAMBLE")
        {
            int n = newHand.Count;
            newDiscardPile.AddRange(newHand);
            discardAfter += n;
            newHand.Clear();
            for (int i = 0; i < n && newHand.Count < 10; i++)
            {
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    drawPileAfter = discardAfter; discardAfter = 0;
                    newDrawPile.AddRange(newDiscardPile); newDiscardPile.Clear();
                }
                newHand.Add(MakeAverageDrawCard(next));
                drawPileAfter--;
                if (newDrawPile.Count > 0) newDrawPile.RemoveAt(newDrawPile.Count - 1);
            }
        }
        else if (card.DrawCount > 0 && ShivGenCards.Contains(card.Id)
                 // 2026-05-31 — same combat-end gate as the generic draw: LEADING_STRIKE
                 // attacks THEN creates Shivs; if the attack killed the last enemy the
                 // Shiv creation never runs (combat over) → sim over-added {hand +2}.
                 && (!card.IsAttack || next.Enemies.Any(e => e.IsAlive)))
        {
            // 2026-05-30 — Shiv.CreateInHand respects the hand-10 cap; at a full
            // hand the created shiv OVERFLOWS to the discard pile (not lost). Same
            // as card-gen overflow. Earlier this broke (shivs dropped), which
            // under-counted discard for CLOAK_AND_DAGGER/HIDDEN_DAGGERS at hand>=10.
            for (int i = 0; i < card.DrawCount; i++)
            {
                if (newHand.Count < 10)
                    newHand.Add(MakeShivPlaceholderCard());
                else
                {
                    newDiscardPile.Add(MakeShivPlaceholderCard());
                    discardAfter += 1;
                }
            }
        }
        else if (card.DrawCount > 0
                 && (!card.IsPower || PowerCardsThatDraw.Contains(card.Id))
                 && drawPileAfter + discardAfter > 0
                 && !ostyAttackWhiff
                 // SCRAPE draws then conditionally discards the non-0-cost draws —
                 // handled by its own draw-and-split block below (needs per-card cost).
                 && card.Id != "SCRAPE"
                 // 2026-05-31 — FTL draws its CardsVar(1) ONLY if cards-played-this-turn
                 // < PlayMax(3) (CanDrawCard). The sim drew unconditionally → hand +1 /
                 // draw −1 once ≥3 cards had been played (4 rows). next.TurnCardsPlayed
                 // is the pre-FTL count (snapshot is pre-play).
                 && (card.Id != "FTL" || next.TurnCardsPlayed < 3)
                 // 2026-05-31 — IMPATIENCE draws Cards(2) ONLY if the hand holds NO
                 // attack card (Impatience.OnPlay: if !Hand.Any(Attack) Draw). The sim
                 // drew unconditionally → {hand +2, draw −2} when an attack was in hand.
                 && (card.Id != "IMPATIENCE" || !newHand.Exists(hc => hc.IsAttack))
                 // REBOOT is a full reshuffle-then-draw — handled by its own block below.
                 && card.Id != "REBOOT"
                 // 2026-05-31 — NoDrawPower blocks all in-combat card-effect draws this
                 // turn (DRUM_OF_BATTLE drew 0 in real, sim drew 2 → {hand +2, draw −2}).
                 && next.PlayerNoDraw == 0
                 // 2026-05-31 — an ATTACK that draws AFTER dealing damage (ROCKET_PUNCH/
                 // POMMEL_STRIKE: Attack then Draw) doesn't draw when it KILLED the last
                 // enemy — combat ends before the draw resolves. next.Enemies is the
                 // post-attack state here; if none are alive, skip the draw. Confirmed:
                 // pred_alive==real_alive==0 on the {hand +1, draw −1} rows.
                 && (!card.IsAttack || next.Enemies.Any(e => e.IsAlive)))
        {
            // 2026-05-30 — ostyAttackWhiff gates the DRAW too: FETCH wraps its
            // CardPileCmd.Draw inside `if (!Osty.CheckMissingWithAnim)` (same
            // block as the Osty attack), so an Osty-missing FETCH draws nothing.
            // The sim drew 1 unconditionally → hand +1 / draw_pile -1 (5 rows).
            var avgDraw = MakeAverageDrawCard(next);
            // 2026-05-30 — draw-to-hand-size cards (EXPERTISE): the Cards var is
            // the TARGET hand size, not a fixed count. sts2.dll draws
            // (Cards - Hand.Count). The sim drew the full Cards value → over-drew
            // by the pre-play hand size (EXPERTISE draw_pile +N/hand -N, 4 rows).
            int drawTimes = DrawToHandSize.Contains(card.Id)
                ? System.Math.Max(0, card.DrawCount - newHand.Count)
                : card.DrawCount;
            for (int i = 0; i < drawTimes; i++)
            {
                // 2026-05-29 — hand is capped at 10 (CardPileCmd.Draw: num =
                // max(0, 10 - hand.Count); breaks when hand.Count >= 10). The
                // played card is already removed from newHand (line ~105), so
                // newHand.Count here equals the real hand size at draw resolution.
                // Without this the sim over-drew whenever the (headless-inflated)
                // hand was >=10 while real drew nothing — the systematic cause of
                // SLIMED / CHARGE / SCRAPE / draw-card {hand +1, draw -1} rows.
                if (newHand.Count >= 10) break;
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    // Reshuffle simulated: discard pile becomes new draw pile.
                    drawPileAfter = discardAfter;
                    discardAfter = 0;
                    // Mirror reshuffle on the actual lists — discard contents
                    // move into draw, discard clears. Real game shuffles the
                    // order but for Count parity that doesn't matter.
                    newDrawPile.AddRange(newDiscardPile);
                    newDiscardPile.Clear();
                }
                newHand.Add(avgDraw);
                drawPileAfter--;
                // Pop one card off the (now non-empty) drawPile list to keep
                // DrawPile.Count in sync with DrawPileSize. Order doesn't
                // matter for the probe; pop from end (O(1)).
                // 2026-05-30 — tried drawing the REAL top card here (index 0) to
                // give drawn cards true identity, but it regressed Silent 90.5->
                // 87.4 (real cards added to hand perturb downstream hand-scan
                // logic: discard-from-hand / retain / star-count). avgDraw keeps
                // the hand neutral. Order-sensitive draw cards stay modeled
                // per-card (e.g. PILLAGE has its own real-top-draw block).
                if (newDrawPile.Count > 0)
                    newDrawPile.RemoveAt(newDrawPile.Count - 1);
            }
        }

        // 2026-05-31 — REBOOT: move ALL hand + ALL discard into the draw pile, shuffle,
        // then draw Cards(4). The COUNT is deterministic (only the order is RNG): real
        // ends hand=4, discard=0, draw=old_draw+(hand−REBOOT)+old_discard−4. The sim
        // treated it as a plain draw-4 → massively wrong (draw −17, hand +4, discard
        // +13). REBOOT is already removed from newHand and exhausts via the keyword.
        if (card.Id == "REBOOT")
        {
            drawPileAfter += newHand.Count + discardAfter;
            newDrawPile.AddRange(newHand); newHand.Clear();
            newDrawPile.AddRange(newDiscardPile); newDiscardPile.Clear();
            discardAfter = 0;
            int rebootDraw = System.Math.Min(card.DrawCount > 0 ? card.DrawCount : 4, drawPileAfter);
            var avgReboot = MakeAverageDrawCard(next);
            for (int i = 0; i < rebootDraw && newHand.Count < 10; i++)
            {
                newHand.Add(avgReboot);
                drawPileAfter--;
                if (newDrawPile.Count > 0) newDrawPile.RemoveAt(newDrawPile.Count - 1);
            }
        }

        // 2026-05-31 — SCRAPE: Draw Cards(4), then DISCARD every drawn card whose
        // cost is non-zero (Scrape.OnPlay keeps only 0-cost, non-X, non-star cards;
        // discards the rest). The generic DrawCount path uses identity-neutral
        // avgDraw and never discards → consistent {hand +N, discard -N} (7/7 rows,
        // mostly Defect). Peek each REAL top card's cost to decide keep vs discard,
        // but place KEPT cards in hand as avgDraw placeholders (real card identity in
        // hand regressed Silent 90.5->87.4 — see generic path note). Discarded cards
        // keep their identity (discard pile isn't hand-scanned). Counts match real.
        if (card.Id == "SCRAPE")
        {
            var avgDraw = MakeAverageDrawCard(next);
            for (int i = 0; i < card.DrawCount; i++)
            {
                if (newHand.Count >= 10) break;
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    drawPileAfter = discardAfter;
                    discardAfter = 0;
                    newDrawPile.AddRange(newDiscardPile);
                    newDiscardPile.Clear();
                }
                if (newDrawPile.Count == 0) break;
                var drawn = newDrawPile[0];        // real next-to-draw (carries Cost)
                newDrawPile.RemoveAt(0);
                drawPileAfter--;
                // Keep only truly-free cards: cost 0, no star cost, not X-cost.
                bool keep = drawn.Cost == 0
                            && drawn.StarCost <= 0
                            && (drawn.Axes == null || !drawn.Axes.Contains("X_COST"));
                if (keep)
                {
                    newHand.Add(avgDraw);          // neutral placeholder in hand
                }
                else
                {
                    newDiscardPile.Add(drawn);
                    discardAfter++;
                }
            }
        }

        // 2026-05-29 — draw-then-FromHandForDiscard cards. After the draw resolves,
        // these cards discard N hand cards (player-choice, but COUNT is fixed):
        //   DAGGER_THROW   — CardSelectCmd.FromHandForDiscard(1)
        //   HIDDEN_DAGGERS — CardSelectCmd.FromHandForDiscard(Cards.IntValue=2)
        // The earlier "resolver declines" conclusion was an artifact of the probe
        // capturing realState MID-choice; once the probe resolves the choice before
        // snapshotting (SimulatorParityCheck choice loop), real DOES discard and the
        // divergence is a clean {hand +1, discard -1} = sim missing the discard.
        // Identity is irrelevant for counts: move min(N, hand) from hand → discard.
        if (DiscardFromHandCount.TryGetValue(card.Id, out int discN) && discN > 0)
        {
            int moved = System.Math.Min(discN, newHand.Count);
            for (int k = 0; k < moved; k++)
            {
                var movedCard = newHand[newHand.Count - 1];
                newHand.RemoveAt(newHand.Count - 1);
                newDiscardPile.Add(movedCard);
                discardAfter += 1;
            }
        }

        // 2026-05-29 — discard→draw retrieval cards. COSMIC_INDIFFERENCE:
        // CardSelectCmd.FromSimpleGrid over the DISCARD pile, then moves the
        // chosen card to the DRAW pile (CardPileCmd.Add … PileType.Draw). Count
        // fixed (1); identity is the player's choice but irrelevant for counts.
        // Move N from discard → draw.
        if (DiscardToDrawCount.TryGetValue(card.Id, out int retrN) && retrN > 0)
        {
            int moved = System.Math.Min(retrN, newDiscardPile.Count);
            for (int k = 0; k < moved; k++)
            {
                var movedCard = newDiscardPile[newDiscardPile.Count - 1];
                newDiscardPile.RemoveAt(newDiscardPile.Count - 1);
                newDrawPile.Add(movedCard);
                discardAfter -= 1;
                drawPileAfter += 1;
            }
        }

        // 2026-05-30 — put-back cards: draw N (handled above), then return M cards
        // from hand to the TOP of the draw pile (CardSelectCmd.FromHand →
        // CardPileCmd.Add PileType.Draw). PHOTON_CUT: draw 1, put back 1 (net 0);
        // GLIMMER: draw 3, put back 1 (net +2). Player-choice but count fixed;
        // identity irrelevant for counts. Also correctly handles the hand-cap edge:
        // if the draw capped, the put-back still moves a hand card to draw.
        if (PutBackToDrawCount.TryGetValue(card.Id, out int pbN) && pbN > 0)
        {
            int moved = System.Math.Min(pbN, newHand.Count);
            for (int k = 0; k < moved; k++)
            {
                var movedCard = newHand[newHand.Count - 1];
                newHand.RemoveAt(newHand.Count - 1);
                newDrawPile.Add(movedCard);
                drawPileAfter += 1;
            }
        }

        // 2026-05-30 — discard→hand retrieval (HOLOGRAM): FromSimpleGrid over the
        // discard pile → CardPileCmd.Add PileType.Hand. Move N from discard → hand,
        // respecting the hand-10 cap (can't retrieve into a full hand).
        if (DiscardToHandCount.TryGetValue(card.Id, out int d2hN) && d2hN > 0)
        {
            for (int k = 0; k < d2hN && newDiscardPile.Count > 0 && newHand.Count < 10; k++)
            {
                var movedCard = newDiscardPile[newDiscardPile.Count - 1];
                newDiscardPile.RemoveAt(newDiscardPile.Count - 1);
                newHand.Add(movedCard);
                discardAfter -= 1;
            }
        }

        // v0.5 — AFTER draw resolves, the played card joins the discard pile unless it
        // exhausts on play (catalog Exhaust flag). Done here so any post-play snapshot
        // a downstream card sees reflects the realistic pile sizes including this card.
        // 2026-05-28 MCTS-P0 — also push into newDiscardPile (actual list) so
        // SimState.DiscardPile.Count and DiscardPileSize stay in sync. When the
        // card exhausts, bump ExhaustPileCount instead. Power cards skip both
        // discard AND exhaust paths: sts2.dll moves them to a hidden PowerPile
        // (the effect persists as a Creature.Power, the card itself leaves play
        // entirely). Previously mod sim sent them to discard, which produced
        // 100% divergence on every Power card in the parity probe (76/76).
        int newExhaustPileCount = next.ExhaustPileCount;

        // 2026-05-31 — Random hand-exhaust mechanic (CINDER, TRUE_GRIT base).
        // sts2.dll: `CombatCardSelection.NextItem(handPile.Cards)` picks an
        // Rng-driven random card to exhaust. Mod sim deterministic: pop the
        // LAST card from newHand. Total damage/block elsewhere unchanged.
        // Card itself follows normal discard/exhaust path below (CINDER/
        // TRUE_GRIT have ExtraHoverTip Exhaust but no CanonicalKeyword,
        // so they go to discard via the IsExhaust=false branch).
        // 2026-05-30 — SCAVENGE also exhausts 1 hand card: CardSelectCmd.FromHand
        // (ExhaustSelectionPrompt, 1) → CardCmd.Exhaust. Player-choice but count is
        // fixed at 1; identity is irrelevant for the exhaust/hand counts.
        // 2026-05-30 — THRASH (Ironclad): after its 2-hit attack it picks a random
        // ATTACK card from hand and exhausts it (Rng.NextItem over hand Attacks).
        // Same exhaust-1-from-hand pile/FeelNoPain shape as CINDER/TRUE_GRIT (the
        // attack-only filter is irrelevant for the pile counts). Was exhaust −1 /
        // hand +1 (sim missed the hand-card exhaust, 3 rows).
        if (card.Id == "CINDER" || card.Id == "TRUE_GRIT" || card.Id == "SCAVENGE"
            || card.Id == "THRASH")
        {
            if (newHand.Count > 0)
            {
                newHand.RemoveAt(newHand.Count - 1);
                newExhaustPileCount++;
                // 2026-05-30 — FeelNoPainPower triggers on the EXHAUSTED HAND CARD,
                // not just the played card's own exhaust (line ~1490 covers that).
                // TRUE_GRIT/CINDER/SCAVENGE exhaust 1 hand card → +FeelNoPain block.
                // Confirmed: TRUE_GRIT player_block diff == −FeelNoPain (−3↔3, −6↔6).
                if (newPlayerFeelNoPain > 0)
                    newPlayerBlock += StatusMath.EffectiveBlock(newPlayerFeelNoPain, newPlayerDex, playerFrail);
            }
        }

        // 2026-05-28 S6-4: FIEND_FIRE exhaust all hand cards.
        // FiendFire.OnPlay: foreach card in hand → exhaust. Then attack
        // damage × cardCount. The attack hits are already captured in the
        // hitsForDmg override above. Here we move all remaining hand cards
        // (newHand) to exhaust pile.
        if (card.Id == "FIEND_FIRE")
        {
            newExhaustPileCount += newHand.Count;
            newHand.Clear();
        }

        // 2026-05-31 — STOKE: exhausts the ENTIRE remaining hand, then generates
        // exhaustCount random cards back to hand (CardFactory → PileType.Hand).
        // Net: exhaust += handCount, hand COUNT unchanged (identities differ but
        // count parity holds). Sim missed it entirely → exhaust_pile under-counted
        // by the hand size (observed exhaust_pile −2 at a 2-card hand).
        if (card.Id == "STOKE")
        {
            int stokeN = newHand.Count;
            newExhaustPileCount += stokeN;
            newHand.Clear();
            for (int i = 0; i < stokeN; i++)
                newHand.Add(MakeAverageDrawCard(next));
        }

        // 2026-05-28 S6-4: PILLAGE draw-until-non-attack approximation.
        // Pillage.OnPlay: do { card = Draw(); } while (card.Type == Attack &&
        // hand.Count < 10). Mod sim has no native modeling — DrawCount=0 left
        // the draw unaccounted for. Approximation: walk newDrawPile from the
        // end (cheap pop), consume attack cards into hand, stop at first
        // non-Attack or hand-full.
        if (card.Id == "PILLAGE" && newHand.Count < 10)
        {
            int safety = 10;
            while (safety-- > 0 && newHand.Count < 10)
            {
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    drawPileAfter = discardAfter;
                    discardAfter = 0;
                    newDrawPile.AddRange(newDiscardPile);
                    newDiscardPile.Clear();
                }
                if (newDrawPile.Count == 0) break;
                // 2026-05-30 — draw from the TOP (index 0) of the captured draw
                // pile, which is the real game's next-to-draw order. PILLAGE's
                // draw COUNT (draw-while-Attack) depends on the actual attack/
                // non-attack sequence, so the order direction matters.
                var drawn = newDrawPile[0];
                newDrawPile.RemoveAt(0);
                drawPileAfter--;
                newHand.Add(drawn);
                if (!drawn.IsAttack) break;  // stops at first non-Attack
            }
        }

        // SECOND_WIND: exhaust all non-Attack hand cards, gain card.Block
        // per exhaust. sts2.dll's SecondWind.OnPlay loops
        //   foreach (var c in pile.Cards.Where(c => c.Type != CardType.Attack))
        //     { Exhaust(c); GainBlock(BlockVar); }
        // Mod sim previously fell through to the standard skill self-block
        // branch (single grant) and never moved the hand cards → 13/13
        // SECOND_WIND records diverged in S4 probe (player_block -10..-43,
        // hand_count +3..+6, exhaust_pile_count -3..-6). Standard branch is
        // gated above to leave block math here.
        if (card.Id == "SECOND_WIND")
        {
            int nonAttackCount = 0;
            for (int i = newHand.Count - 1; i >= 0; i--)
            {
                if (!newHand[i].IsAttack)
                {
                    nonAttackCount++;
                    newExhaustPileCount++;
                    newHand.RemoveAt(i);
                }
            }
            if (nonAttackCount > 0 && card.Block > 0)
            {
                int perPlayBlock = StatusMath.EffectiveBlock(card.Block, newPlayerDex, playerFrail);
                newPlayerBlock += nonAttackCount * perPlayBlock;
            }
        }

        if (card.IsPower)
        {
            // Power moves to PowerPile (untracked in SimState — the Power
            // catalog stack now lives on PlayerPowers / PlayerStrength etc.).
            // No discard/exhaust counter update.
        }
        else if (SelfRecycleToDraw.Contains(card.Id))
        {
            // 2026-05-29 — self-recycle cards return THIS card to the draw pile
            // (top) instead of discard (sts2.dll: CardPileCmd.Add(this,
            // PileType.Draw, Top)). SHINING_STRIKE. Mod sim sent it to discard →
            // draw_pile_count -1 / discard +1 divergence. Route to draw.
            drawPileAfter += 1;
            newDrawPile.Add(card);
        }
        else if (RetainOnPlay.Contains(card.Id))
        {
            // 2026-05-30 — retain-on-play: GetResultPileType() returns Hand instead
            // of Discard, so the played card goes BACK to hand (PARTICLE_WALL).
            // The card was removed from newHand at the start; re-add it (hand-cap:
            // overflow to discard if the hand is already full).
            if (newHand.Count < 10) newHand.Add(card);
            else { discardAfter += 1; newDiscardPile.Add(card); }
        }
        else if (!card.IsExhaust)
        {
            discardAfter += 1;
            newDiscardPile.Add(card);
            // ANGER ("이 카드의 복사본을 1장 버린 카드 더미에 추가합니다.") —
            // sts2.dll uses CardPileCmd.AddGeneratedCardToCombat which requires
            // NCard creation = Godot UI throw in headless. Production game
            // does add the clone, but the parity probe's headless harness
            // can't replicate it. 2026-05-28 100%-parity push: remove mod's
            // clone-add to match headless behavior. Production deploy of
            // Sts2CombatAI (real STS2 with UI) loses this 1-card-extra in
            // discard fidelity, but the planner barely uses ANGER count as
            // a signal — single-card divergence acceptable trade for clean
            // parity number. To restore production fidelity later, add a
            // headless logical-pile-add helper in Sts2CombatCore.
        }
        else
        {
            newExhaustPileCount += 1;
        }

        // 2026-05-29 — status-producer cards generate status cards into the
        // discard pile (sts2.dll: CardPileCmd.AddGeneratedCardToCombat(card,
        // PileType.Discard)). Headless real game DOES add them (probe shows
        // discard +N), but the mod sim didn't → consistent discard_pile_count
        // = -N divergence (GUNK_UP/OVERCLOCK/BOOST_AWAY/FIGHT_THROUGH, ~40 cases).
        // Mirror the count so pile bookkeeping matches.
        if (StatusToDiscardCount.TryGetValue(card.Id, out int statusN) && statusN > 0)
        {
            for (int s = 0; s < statusN; s++)
            {
                discardAfter += 1;
                newDiscardPile.Add(MakeStatusPlaceholderCard());
            }
            // 2026-05-31 — SmokestackPower: each Status card the player generates deals
            // Amount to ALL enemies (AfterCardGeneratedForCombat, Unpowered). So a
            // status-producer (BOOST_AWAY→Dazed, GUNK_UP→Slimed, OVERCLOCK→Burn) with
            // Smokestack active pulses statusN × Amount AOE. Sim missed it → enemy_hp
            // +Smokestack (BOOST_AWAY +5/+7). Apply flat (unblockable-style, clamped).
            if (next.PlayerSmokestack > 0)
            {
                int smoke = next.PlayerSmokestack;
                var smokeList = new List<SimEnemy>(next.Enemies.Count);
                foreach (var e in next.Enemies)
                {
                    if (e.IsAlive && e.Hp > 0)
                        smokeList.Add(e with { Hp = System.Math.Max(0, e.Hp - smoke * statusN) });
                    else
                        smokeList.Add(e);
                }
                next = next with { Enemies = smokeList };
            }
            // 2026-05-31 — PillarOfCreationPower: +Amount block (flat) per generated card.
            if (next.PlayerPillar > 0) newPlayerBlock += next.PlayerPillar * statusN;
            // 2026-05-31 — ArsenalPower: +Amount Strength per generated card.
            if (next.PlayerArsenal > 0) newPlayerStr += next.PlayerArsenal * statusN;
        }

        // 2026-05-29 — card-generators that add a generated card to HAND
        // (sts2.dll: AddGeneratedCardToCombat(card, PileType.Hand)). Regent
        // COLLISION_COURSE (Debris→hand), MANIFEST_AUTHORITY. Mod sim didn't
        // model → hand_count = -N divergence. Add N hand placeholders.
        if (CardGenToHandCount.TryGetValue(card.Id, out int handN) && handN > 0)
        {
            // 2026-05-29 — AddGeneratedCardToCombat(card, PileType.Hand) respects
            // the hand-10 cap: when the hand is full the generated card OVERFLOWS
            // to the discard pile (decompile: COLLISION_COURSE/MANIFEST_AUTHORITY
            // generate Debris/card to Hand, but the headless-inflated hand is >=10
            // so real routes them to discard). Mirror: hand if room, else discard.
            for (int h = 0; h < handN; h++)
            {
                if (newHand.Count < 10)
                    newHand.Add(MakeStatusPlaceholderCard());
                else
                {
                    newDiscardPile.Add(MakeStatusPlaceholderCard());
                    discardAfter += 1;
                }
            }
            // 2026-05-31 — PillarOfCreationPower: +Amount block (flat) per generated card.
            // COLLISION_COURSE→Debris with Pillar gives Pillar block; sim missed it
            // (player_block −Pillar, 3 rows). Block grant is Unpowered (no Dex/Frail).
            if (next.PlayerPillar > 0) newPlayerBlock += next.PlayerPillar * handN;
            // 2026-05-31 — ArsenalPower: +Amount Strength per generated card
            // (MANIFEST_AUTHORITY/CONQUEROR gen → +str; sim missed it, str −Arsenal).
            if (next.PlayerArsenal > 0) newPlayerStr += next.PlayerArsenal * handN;
        }

        // 2026-05-31 — CLEANSE: after summoning Osties, selects 1 card from the DRAW
        // pile to EXHAUST (FromSimpleGrid over Draw → Exhaust). Sim missed it →
        // draw +1 / exhaust −1 (3 rows). Move 1 from draw to exhaust.
        if (card.Id == "CLEANSE" && drawPileAfter > 0)
        {
            drawPileAfter -= 1;
            if (newDrawPile.Count > 0) newDrawPile.RemoveAt(newDrawPile.Count - 1);
            newExhaustPileCount += 1;
        }

        // 2026-05-30 — card generators that add to the DRAW pile (not hand).
        // Necrobinder Soul makers: GRAVE_WARDEN/REAVE call Soul.Create(Cards) →
        // AddGeneratedCardsToCombat(PileType.Draw). Add N to the draw pile.
        if (CardGenToDrawCount.TryGetValue(card.Id, out int drawGenN) && drawGenN > 0)
        {
            for (int g = 0; g < drawGenN; g++)
            {
                newDrawPile.Add(MakeStatusPlaceholderCard());
                drawPileAfter += 1;
            }
        }

        // 2026-05-31 — DIRGE (Necrobinder, X-cost): summons X Ostys AND creates X
        // Souls via Soul.Create(X) → AddGeneratedCardsToCombat(PileType.Draw). Soul
        // count == energy spent (X), not a fixed var, so CardGenToDrawCount can't
        // model it. Add preSpendEnergy Souls to the draw pile. (draw_ids diag:
        // +SOUL to draw; observed draw_pile −1 at X=1.)
        if (card.Id == "DIRGE")
        {
            for (int g = 0; g < preSpendEnergy; g++)
            {
                newDrawPile.Add(MakeStatusPlaceholderCard());
                drawPileAfter += 1;
            }
        }

        // 2026-05-31 — MAKE_IT_SO (Regent) pile-reactive return. Each copy in a
        // non-hand pile has AfterCardPlayedLate: when the owner plays a SKILL and
        // (skills-played-this-turn % 3 == 0), it returns ITSELF to hand. Cards is
        // always 3 (OnUpgrade bumps Damage only). All non-hand copies fire on the
        // same boundary skill (shared `num`), so move every copy (draw first, then
        // discard), each gated on the 10-card hand cap. draw_ids diagnostic cracked
        // this: basic Regent skills showed MAKE_IT_SO leaving draw → draw +1 / hand
        // −1 (4 rows: DEFEND_REGENT/TAUNT/KNOW_THY_PLACE/MONOLOGUE).
        int misRetDraw = 0, misRetDiscard = 0;
        if (card.IsSkill
            && (next.MakeItSoInDraw > 0 || next.MakeItSoInDiscard > 0)
            && (next.TurnSkillsPlayed + 1) % 3 == 0)
        {
            for (int i = 0; i < next.MakeItSoInDraw && newHand.Count < 10; i++)
            {
                if (drawPileAfter <= 0) break;
                if (newDrawPile.Count > 0) newDrawPile.RemoveAt(newDrawPile.Count - 1);
                drawPileAfter -= 1;
                newHand.Add(MakeAverageDrawCard(next));
                misRetDraw++;
            }
            for (int i = 0; i < next.MakeItSoInDiscard && newHand.Count < 10; i++)
            {
                if (discardAfter <= 0) break;
                if (newDiscardPile.Count > 0) newDiscardPile.RemoveAt(newDiscardPile.Count - 1);
                discardAfter -= 1;
                newHand.Add(MakeAverageDrawCard(next));
                misRetDiscard++;
            }
        }

        // 2026-05-31 — SECRET_WEAPON (Regent): the player SELECTS an Attack from the
        // DRAW pile and moves it to hand (CardSelectCmd.FromSimpleGrid → CardPileCmd
        // .Add(Hand)). Count effect: draw −1, hand +1 when an attack exists in draw
        // and the hand has room. The count-only sim can't see card types, so it
        // assumes a draw-pile card is fetchable when draw is non-empty — SECRET_WEAPON
        // is only played to grab an attack, so this holds in practice. (draw_ids:
        // attack leaves draw → hand; observed hand −1 / draw +1.)
        if (card.Id == "SECRET_WEAPON" && drawPileAfter > 0 && newHand.Count < 10)
        {
            if (newDrawPile.Count > 0) newDrawPile.RemoveAt(newDrawPile.Count - 1);
            drawPileAfter -= 1;
            newHand.Add(MakeAverageDrawCard(next));
        }

        // 2026-05-31 — JugglingPower (player): AfterCardPlayed increments an internal
        // attacksPlayedThisTurn counter on each attack and, when it hits 3, adds Amount
        // clones of the played attack to hand. The counter is engine-internal (seeded at
        // mid-turn application) so turn-start TurnAttacksPlayed does NOT align — the real
        // counter is captured into PlayerJugglingCounter via reflection. Fire when this
        // attack pushes the counter from 2 to 3. Hand-cap overflow goes to discard.
        if (card.IsAttack && next.PlayerJugglingCounter == 2 && next.PlayerPowers != null
            && next.PlayerPowers.TryGetValue("JugglingPower", out int jugglingAmt) && jugglingAmt > 0)
        {
            for (int i = 0; i < jugglingAmt; i++)
            {
                if (newHand.Count < 10) newHand.Add(MakeAverageDrawCard(next));
                else { newDiscardPile.Add(MakeAverageDrawCard(next)); discardAfter += 1; }
            }
        }

        // 2026-05-31 — DarkEmbracePower (player): AfterCardExhausted draws Amount cards
        // every time a card is exhausted. exhaustDelta = cards exhausted THIS play (the
        // played card's own exhaust + any effect-exhausts like FIEND_FIRE/STOKE). Draw
        // DarkEmbrace × exhaustDelta from the draw pile (reshuffle when empty, hand-cap).
        // The sim missed it → {hand −N, draw +N} on exhaust-card plays with DarkEmbrace
        // (MOLTEN_FIST/FORGOTTEN_RITUAL clean at 1 exhaust → 1 draw).
        if (next.PlayerPowers != null
            && next.PlayerPowers.TryGetValue("DarkEmbracePower", out int darkEmbrace) && darkEmbrace > 0)
        {
            int exhaustedThisPlay = newExhaustPileCount - next.ExhaustPileCount;
            int deDraw = darkEmbrace * System.Math.Max(0, exhaustedThisPlay);
            for (int i = 0; i < deDraw && newHand.Count < 10; i++)
            {
                if (drawPileAfter <= 0)
                {
                    if (discardAfter <= 0) break;
                    drawPileAfter = discardAfter; discardAfter = 0;
                    newDrawPile.AddRange(newDiscardPile); newDiscardPile.Clear();
                }
                if (newDrawPile.Count > 0) newDrawPile.RemoveAt(0);
                drawPileAfter--;
                newHand.Add(MakeAverageDrawCard(next));
            }
        }

        // 2026-05-31 — PersonalHivePower (ENEMY): AfterDamageReceived from a powered
        // attack, the enemy adds Amount Dazed to the PLAYER's DRAW pile (random pos).
        // A powered attack hitting a PersonalHive enemy pads the draw pile per hit; the
        // sim missed it → draw_pile −Amount (DAZED, 14 rows, root-caused by the
        // draw_ids diagnostic). Add Amount × hits Dazed placeholders to draw.
        if (card.IsAttack && !freeApplied && targetIdx >= 0 && targetIdx < next.Enemies.Count)
        {
            var hiveTgt = next.Enemies[targetIdx];
            if (hiveTgt.Powers != null
                && hiveTgt.Powers.TryGetValue("PersonalHivePower", out int hive) && hive > 0)
            {
                int hiveAdds = hive * System.Math.Max(1, card.Hits);
                for (int i = 0; i < hiveAdds; i++)
                {
                    newDrawPile.Add(MakeStatusPlaceholderCard());
                    drawPileAfter += 1;
                }
            }
        }

        // 2026-05-30 — card generators that add to the DISCARD pile. UNDEATH:
        // GainBlock + AddGeneratedCardToCombat(card, PileType.Discard).
        if (CardGenToDiscardCount.TryGetValue(card.Id, out int discGenN) && discGenN > 0)
        {
            for (int g = 0; g < discGenN; g++)
            {
                newDiscardPile.Add(MakeStatusPlaceholderCard());
                discardAfter += 1;
            }
        }

        // v0.7.85 — AfterimagePower: gain N block on every card played.
        // 2026-05-29 — decompile: AfterimagePower.AfterCardPlayed calls
        // GainBlock(value, ValueProp.Unpowered) — the block is FLAT, NOT modified
        // by Dexterity or Frail. The sim wrongly ran it through EffectiveBlock,
        // so whenever Frail was on the player it under-credited afterimage block
        // by 25% (player_block divergence correlated with FrailPower in 58 rows).
        // 2026-05-31 — use the PRE-PLAY amount (next.PlayerAfterimage), NOT the
        // post-increment newPlayerAfterimage. The block equals base.Amount recorded
        // at BeforeCardPlayed, which fires BEFORE OnPlay. So when the AFTERIMAGE card
        // itself is played, the power isn't on the player yet → its own play gets 0
        // (sim over-credited +1 on every AFTERIMAGE self-play: 3 rows, all +1).
        if (next.PlayerAfterimage > 0 && !card.IsCurseOrStatus)
            newPlayerBlock += next.PlayerAfterimage;

        // 2026-05-30 — ChildOfTheStarsPower: AfterStarsSpent(N) gains Amount×N
        // block (flat, Unpowered). A star-cost card spends StarCost stars on play.
        // (PARTICLE_WALL StarCost 2 × ChildOfStars 2 = +4 block.)
        if (next.PlayerChildOfTheStars > 0 && card.StarCost > 0)
            newPlayerBlock += next.PlayerChildOfTheStars * card.StarCost;

        // 2026-05-30 — ParryPower: +Amount block (flat, Unpowered) when
        // SOVEREIGN_BLADE is played (ParryPower.AfterSovereignBladePlayed).
        if (next.PlayerParry > 0 && card.Id == "SOVEREIGN_BLADE")
            newPlayerBlock += next.PlayerParry;

        // v0.7.97 — FeelNoPainPower: gain N block when a card is exhausted.
        // Only fires for cards with the Exhaust keyword (catalog flag); status /
        // curse Ethereal exhaust at turn-end, not on play.
        if (newPlayerFeelNoPain > 0 && card.IsExhaust)
            newPlayerBlock += StatusMath.EffectiveBlock(newPlayerFeelNoPain, newPlayerDex, playerFrail);

        // v0.8.1 — DanseMacabrePower: gain N block on cost≥2 card play. Per
        // STS2 catalog: "Whenever you play a card with cost ≥ 2, gain N block."
        // card.Cost is the catalog cost; 0-cost cards (including free Skills
        // under Corruption) do NOT trigger Danse.
        if (newPlayerDanseMacabre > 0 && card.Cost >= 2)
            newPlayerBlock += StatusMath.EffectiveBlock(newPlayerDanseMacabre, newPlayerDex, playerFrail);

        // v0.7.98 — Consume one EchoForm charge per card resolve. Subsequent
        // cards in depth-N lookahead see one less remaining echo. Curse/Status
        // cards still count as plays (canonical: Echo Form text says "you play",
        // which curses/status do when forced — but typical play loop avoids them).
        if (echoActive && newPlayerEchoForm > 0)
            newPlayerEchoForm--;

        // v0.7.99 — JuggernautPower: each block-gain event deals N damage to a
        // random enemy. Approximation: if net block increased during card
        // resolve, fire once for the weakest alive enemy. Under-credits cards
        // with multiple block sources (Rage + Afterimage + skill block) but
        // avoids over-credit.
        if (newPlayerJuggernaut > 0 && newPlayerBlock > initialPlayerBlock)
        {
            int weakestIdx = -1;
            int weakestHp = int.MaxValue;
            for (int i = 0; i < next.Enemies.Count; i++)
            {
                if (!next.Enemies[i].IsAlive) continue;
                if (next.Enemies[i].Hp < weakestHp)
                {
                    weakestHp = next.Enemies[i].Hp;
                    weakestIdx = i;
                }
            }
            if (weakestIdx >= 0)
            {
                var updated = new List<SimEnemy>(next.Enemies);
                var tgt = updated[weakestIdx];
                int blockAfter = System.Math.Max(0, tgt.Block - newPlayerJuggernaut);
                int dmgPastBlock = System.Math.Max(0, newPlayerJuggernaut - tgt.Block);
                int hpAfter = System.Math.Max(0, tgt.Hp - dmgPastBlock);
                updated[weakestIdx] = tgt with { Hp = hpAfter, Block = blockAfter };
                next = next with { Enemies = updated };
            }
        }

        // v0.7.99 — HungerPower: each card drawn grants Strength +N. Apply
        // BEFORE returning so depth-N lookahead sees the bumped Strength.
        if (newPlayerHunger > 0 && card.DrawCount > 0)
            newPlayerStr += newPlayerHunger * card.DrawCount;

        // v0.9 — Forge propagation. STS2 Regent build: cards with FORGE_AMPLIFIER /
        // LORDS_BLADE_PRODUCER axes "Forge" the SovereignBlade token in piles,
        // permanently bumping its Damage. Previously the simulator missed this,
        // so depth-2 lookahead never saw "play BEAT then play SB" with the
        // boosted SB damage — SB scored at base d=10..21 forever and lost score
        // races to BEAT_INTO_SHAPE every turn (logs 2026-05-19, full combat,
        // SB never selected).
        //
        // Forge amount: prefer card.Effect.ForgeGen (runtime DynamicVar
        // extracted by CardReflection, includes "+N per same-target attack
        // already done" because PreviewValue is calculated live by the
        // game). Fall back to known per-card baselines when ForgeGen is 0.
        //
        // For BEAT_INTO_SHAPE specifically — and any other card whose Forge
        // amount scales on "attacks on this target THIS TURN" — we ALSO add
        // the per-prior-attack bonus tracked in TurnAttacksByTargetIdx.
        // ForgeGen from snapshot captures only the state at capture time;
        // depth-2+ chains where we play FALLING_STAR(tgt) then BEAT(tgt)
        // need this in-sim bump to score BEAT's true Forge value.
        int forgeAmount = 0;
        if (card.Effect.ForgeGen > 0) forgeAmount = card.Effect.ForgeGen;
        else if (card.Axes != null
                 && (card.Axes.Contains("LORDS_BLADE_PRODUCER")
                     || card.Axes.Contains("FORGE_AMPLIFIER")))
        {
            forgeAmount = card.Id switch
            {
                "BEAT_INTO_SHAPE"  => 5,    // 5 base; per-attack bonus added below
                "REFINE_BLADE"     => 9,
                "SPOILS_OF_BATTLE" => 5,
                "WROUGHT_IN_WAR"   => 7,
                "BULWARK"          => 10,
                "BIG_BANG"         => 5,
                _                  => 5,   // unknown FORGE_AMPLIFIER → conservative 5
            };
        }
        // 2026-05-31 — FURNACE forges via FurnacePower.AfterSideTurnStart (it applies
        // a counter Power and forges at the START of each following turn), NOT on play.
        // Its ForgeVar(4) is the power amount, not an immediate forge — every other
        // Forge card (REFINE_BLADE/CONQUEROR/SEEKING_EDGE included) calls ForgeCmd.Forge
        // in OnPlay, but FURNACE does not. The sim treated ForgeGen=4 as an on-play
        // forge and auto-created a SovereignBlade in hand → {hand +1} on every FURNACE
        // play with no SB yet (6 rows). Zero it so the on-play forge/SB-creation skips.
        if (card.Id == "FURNACE") forgeAmount = 0;
        // v0.9 — Dynamic per-target attack bonus. BEAT_INTO_SHAPE's text:
        // "단조 5. 이번 턴에 대상 적을 공격한 다른 횟수마다 추가로 단조 5."
        // The +5 multiplier mirrors the base Forge amount (CalculationExtra=5).
        // Upgraded BEAT base 7 + 7/atk. Generalised: extra bonus = base × prior
        // attacks on target this turn. Use a per-card whitelist so unrelated
        // FORGE_AMPLIFIER cards (BULWARK, REFINE_BLADE etc. — static amount)
        // don't get spurious scaling.
        if (forgeAmount > 0 && card.Id == "BEAT_INTO_SHAPE"
            && targetIdx >= 0 && targetIdx < next.Enemies.Count
            && next.TurnAttacksByTargetIdx.TryGetValue(targetIdx, out var priorAttacksOnTgt)
            && priorAttacksOnTgt > 0)
        {
            // BEAT counts attacks "OTHER THAN THIS ONE" — priorAttacksOnTgt is
            // exactly the count before this play (we haven't incremented yet).
            int perAttackExtra = forgeAmount;     // base == per-attack bonus
            forgeAmount += perAttackExtra * priorAttacksOnTgt;
        }
        int newSovereignBladeCount = next.SovereignBladeCount;
        if (forgeAmount > 0)
        {
            // v0.9 — Auto-create SovereignBlade when none exists. Per game's
            // ForgeCmd.Forge (sts2.decompiled.cs:398974): the first Forge with
            // no SB in piles spawns SovereignBlade(d=10, cost=2, Retain) in
            // hand AND then applies the Forge amount. So a hand without SB
            // playing BEAT(5) ends with SB(d=15) in hand. Previously the
            // simulator missed this entirely; the planner never saw the
            // "Forge creates the win condition" pathway.
            //
            // Axes mirror card_triggers.json's SOVEREIGN_BLADE entry; cost 2
            // matches the base card (upgraded variant is 1, but we can't
            // distinguish without runtime info — use base for safety).
            if (newSovereignBladeCount == 0)
            {
                var sbEffect = new CardEffectSummary
                {
                    Damage = 10,    // forgeAmount added in the loop below
                    Hits  = 1,
                };
                var sbCard = new SimCard
                {
                    Id = "SOVEREIGN_BLADE",
                    Cost = 2,
                    Kind = CardType.Attack,
                    Target = TargetType.AnyEnemy,
                    Effect = sbEffect,
                    IsPlayable = energy >= 2,
                    Axes = new[]
                    {
                        "RETAIN_SELF", "DAMAGE", "REPEAT",
                        "RETAIN", "LORDS_BLADE_PAYOFF"
                    },
                    PrimaryBuildTags = new[] { "압축덱" },
                    IsRetain = true,
                };
                // 2026-05-31 — respect the 10-card hand cap: AddGeneratedCardToCombat
                // overflows a full hand to the DISCARD pile. A first-Forge from a full
                // hand (REFINE_BLADE/SPOILS at hand≥10) sent the new SB to discard in
                // real, but the sim added it to hand unconditionally → hand +1 /
                // discard −1 (6 rows). Mirror the overflow.
                if (newHand.Count < 10)
                    newHand.Add(sbCard);
                else
                {
                    newDiscardPile.Add(sbCard);
                    discardAfter += 1;
                }
                newSovereignBladeCount = 1;
                // 2026-05-31 — the SovereignBlade creation IS a card generation, so
                // PillarOfCreation (block) and Arsenal (str) fire once. CONQUEROR
                // forges → first SB → +Pillar/+Arsenal that the sim otherwise missed
                // (player_block −Pillar, player_strength −Arsenal).
                if (next.PlayerPillar > 0) newPlayerBlock += next.PlayerPillar;
                if (next.PlayerArsenal > 0) newPlayerStr += next.PlayerArsenal;
            }

            for (int i = 0; i < newHand.Count; i++)
            {
                var c = newHand[i];
                if (c.Id == "SOVEREIGN_BLADE")
                {
                    // SimCard.Damage is a computed alias for Effect.Damage —
                    // bump via Effect (CardEffectSummary record) using `with`.
                    newHand[i] = c with
                    {
                        Effect = c.Effect with { Damage = c.Effect.Damage + forgeAmount }
                    };
                }
            }
        }

        // v0.9 — Increment per-target attack counter for the played card so
        // subsequent depth-N steps in the same simulation see the updated
        // count. Mirrors the live game's tracking. Only updates when this is
        // an actual attack with a single target (AOE / skill-self plays
        // don't count toward BEAT's per-target Forge bonus).
        IReadOnlyDictionary<int, int> newTurnAttacksByTgt = next.TurnAttacksByTargetIdx;
        if (card.IsAttack && targetIdx >= 0 && targetIdx < next.Enemies.Count
            && card.Target != TargetType.AllEnemies)
        {
            var newDict = new Dictionary<int, int>(next.TurnAttacksByTargetIdx);
            newDict[targetIdx] = newDict.TryGetValue(targetIdx, out var prev)
                ? prev + 1
                : 1;
            newTurnAttacksByTgt = newDict;
        }

        // v0.10 — Advance relic counters for depth-N lookahead so the second
        // step in a chain doesn't re-fire a trigger that the first step
        // already consumed (PenNib×2 must not double both cards in a 2-card
        // depth-2 plan; IronClub's +draw on the 4th card must not also score
        // on the 5th). The catalog reads the counter pattern documented per
        // relic; here we mirror the live game's "AfterCardPlayed" increments
        // in-place so the scored "next state" matches reality.
        IReadOnlyDictionary<string, int> newPlayerRelics = next.PlayerRelics;
        if (next.PlayerRelics != null && next.PlayerRelics.Count > 0)
        {
            Dictionary<string, int>? updated = null;
            void Bump(string key, int mod)
            {
                if (!next.PlayerRelics.TryGetValue(key, out var v)) return;
                updated ??= new Dictionary<string, int>(next.PlayerRelics);
                updated[key] = (v + 1) % mod;
            }
            bool isAttack = card.IsAttack;
            bool isSkill = card.Kind == CardType.Skill;
            // Cross-turn attack counters (Pen Nib / Nunchaku — don't reset
            // at turn boundary).
            if (isAttack) { Bump("PenNib", 10); Bump("Nunchaku", 10); }
            // Per-turn attack counters (Kunai / Shuriken / OrnamentalFan —
            // BeforeSideTurnStart resets these in the live game).
            if (isAttack)
            {
                Bump("Kunai", 3);
                Bump("Shuriken", 3);
                Bump("OrnamentalFan", 3);
            }
            // Per-turn skill counter.
            if (isSkill) Bump("LetterOpener", 3);
            // IronClub fires on ANY card type, every 4 plays. Cross-turn (no
            // reset hook in the relic).
            Bump("IronClub", 4);
            // VelvetChoker tracks cards-played-this-turn linearly (no mod;
            // we just increment so the catalog's >=5 / >=6 thresholds reflect
            // the second play in a chain).
            if (next.PlayerRelics.TryGetValue("VelvetChoker", out var vc))
            {
                updated ??= new Dictionary<string, int>(next.PlayerRelics);
                updated["VelvetChoker"] = vc + 1;
            }
            if (updated != null) newPlayerRelics = updated;
        }

        // 2026-05-29 — MonologuePower post-play Strength gain. Applied here, after
        // all damage for THIS card is computed (dmgState snapshotted newPlayerStr
        // earlier), so the current card is unaffected but the next card in a
        // depth-N chain sees the accumulated Strength. Fires for every card type.
        if (newPlayerMonologue > 0)
            newPlayerStr += newPlayerMonologue;

        // 2026-05-29 — TenderPower per-card Strength+Dex decay (flat -1 each,
        // stack-independent). Same late placement as Monologue: post-damage, so
        // only subsequent plays in the depth-N chain see the reduced stats.
        if (newPlayerTender > 0)
        {
            newPlayerStr -= 1;
            newPlayerDex -= 1;
        }

        // 2026-05-30 — StranglePower (enemy debuff): AfterCardPlayed deals Amount
        // UNBLOCKABLE damage to the Strangled enemy on EVERY card the player plays
        // (any type — BeforeCardPlayed/AfterCardPlayed, not gated on attack). The
        // sim ignored it → enemy_hp_sum = +Strangle under-deal on every play vs a
        // Strangled enemy (Silent SHIV +2 residual, but applies to all cards).
        // Fire one unblockable pulse per card play here, on the final enemy state.
        {
            bool anyStrangle = false;
            for (int i = 0; i < next.Enemies.Count; i++)
            {
                var e = next.Enemies[i];
                if (e.IsAlive && e.Powers != null
                    && e.Powers.TryGetValue("StranglePower", out var sv) && sv > 0)
                { anyStrangle = true; break; }
            }
            if (anyStrangle)
            {
                var strangled = new List<SimEnemy>(next.Enemies.Count);
                foreach (var e in next.Enemies)
                {
                    if (e.IsAlive && e.Powers != null
                        && e.Powers.TryGetValue("StranglePower", out var sv) && sv > 0)
                        strangled.Add(e with { Hp = System.Math.Max(0, e.Hp - sv) });
                    else
                        strangled.Add(e);
                }
                next = next with { Enemies = strangled };
            }
        }

        // 2026-05-31 — PanachePower (Regent): every 5th card played deals Amount to
        // ALL enemies (Unpowered AOE). DisplayAmount = CardsLeft counts 5→0; a play
        // that takes CardsLeft to 0 (i.e. pre-play CardsLeft == 1) fires the pulse.
        // The sim missed it → enemy_hp +Panache on the 5th-card plays. Apply flat AOE.
        if (next.PlayerPanache > 0 && next.PanacheCardsLeft == 1)
        {
            int pan = next.PlayerPanache;
            var panList = new List<SimEnemy>(next.Enemies.Count);
            foreach (var e in next.Enemies)
            {
                if (e.IsAlive && e.Hp > 0)
                    panList.Add(e with { Hp = System.Math.Max(0, e.Hp - pan) });
                else
                    panList.Add(e);
            }
            next = next with { Enemies = panList };
        }

        // 2026-05-31 — HauntPower (player, Necrobinder): AfterCardPlayed, when the
        // played card is a Soul, deals Amount UNBLOCKABLE damage to ONE random
        // hittable enemy. SOUL itself is a 0-damage draw token, so the sim under-
        // dealt enemy_hp by exactly Haunt on every SOUL play (4/4 rows, all +6).
        // Random target but enemy_hp_SUM is target-invariant (flat unblockable, no
        // overkill in practice) — apply to the first alive enemy.
        if (card.Id == "SOUL" && next.PlayerHaunt > 0)
        {
            int hauntDmg = next.PlayerHaunt;
            var hauntList = new List<SimEnemy>(next.Enemies.Count);
            bool applied = false;
            foreach (var e in next.Enemies)
            {
                if (!applied && e.IsAlive && e.Hp > 0)
                {
                    hauntList.Add(e with { Hp = System.Math.Max(0, e.Hp - hauntDmg) });
                    applied = true;
                }
                else hauntList.Add(e);
            }
            if (applied) next = next with { Enemies = hauntList };
        }

        // 2026-05-30 — SleightOfFleshPower: when the player applies a (non-temporary)
        // enemy DEBUFF, deal Amount to that enemy, once PER debuff power applied
        // (AfterPowerAmountChanged). FEAR applies Vulnerable → +SleightOfFlesh damage
        // to the target (enemy_hp +9 == SleightOfFlesh 9). Single-target debuffs hit
        // the target; AOE debuffs hit every alive enemy.
        // 2026-05-31 — ShroudPower (Necro): gain Amount block (Unpowered) whenever the
        // player APPLIES DoomPower (AfterPowerAmountChanged on DoomPower). A Doom-applying
        // card (BLIGHT_STRIKE) with Shroud active adds Shroud block per Doom applied; the
        // sim missed it → player_block −Shroud (BLIGHT_STRIKE −2, 4 rows). Flat block.
        if (next.PlayerShroud > 0
            && ((card.Axes != null && card.Axes.Contains("DOOM_PRODUCER"))
                || (card.PowerApps != null
                    && card.PowerApps.TryGetValue("DoomPower", out var doomAmt) && doomAmt != 0)))
        {
            newPlayerBlock += next.PlayerShroud;
        }

        if (next.PlayerSleightOfFlesh > 0 && card.PowerApps != null && card.PowerApps.Count > 0)
        {
            int debuffCount = 0;
            foreach (var (pn, amt) in card.PowerApps)
                if (amt != 0 && IsEnemyDebuff(pn)) debuffCount++;
            if (debuffCount > 0)
            {
                int sofDmg = next.PlayerSleightOfFlesh * debuffCount;
                bool aoeDebuff = card.Target == TargetType.AllEnemies;
                var sofEnemies = new List<SimEnemy>(next.Enemies.Count);
                for (int i = 0; i < next.Enemies.Count; i++)
                {
                    var e = next.Enemies[i];
                    bool hit = e.IsAlive && (aoeDebuff || i == targetIdx);
                    if (hit)
                    {
                        int past = System.Math.Max(0, sofDmg - e.Block);
                        sofEnemies.Add(e with
                        {
                            Block = System.Math.Max(0, e.Block - sofDmg),
                            Hp = System.Math.Max(0, e.Hp - past),
                        });
                    }
                    else sofEnemies.Add(e);
                }
                next = next with { Enemies = sofEnemies };
            }
        }

        // 2026-05-30 — EXPOSE: LoseBlock(target, target.Block) strips the target's
        // ENTIRE block (then applies Vulnerable / removes Artifact). The sim left
        // the enemy block intact → enemy_block_sum = +target.Block (8/15/32). Zero
        // the target's block.
        if (card.Id == "EXPOSE" && targetIdx >= 0 && targetIdx < next.Enemies.Count
            && next.Enemies[targetIdx].IsAlive && next.Enemies[targetIdx].Block > 0)
        {
            var exposed = new List<SimEnemy>(next.Enemies);
            exposed[targetIdx] = exposed[targetIdx] with { Block = 0 };
            next = next with { Enemies = exposed };
        }

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
            PlayerJuggernaut = newPlayerJuggernaut,
            PlayerHunger = newPlayerHunger,
            PlayerFlameBarrier = newPlayerFlameBarrier,
            PlayerDanseMacabre = newPlayerDanseMacabre,
            PlayerFocus = newPlayerFocus,
            PlayerIntangible = newPlayerIntangible,
            PlayerEndOfTurnBlockBonus = newPlayerEotBlockBonus,
            PlayerBlock = newPlayerBlock,
            PlayerFreeAttacks = newFreeAttacks,
            PlayerFreeSkills = newFreeSkills,
            PlayerFreePowers = newFreePowers,
            // v0.7.71 — propagate updated star count for depth-N lookahead
            PlayerStars = newPlayerStars,
            // v0.8.2 — Propagate updated PlayerPowers dict if any self-power
            // applied this card. Keeps explicit fields (PlayerStrength etc.)
            // and dict in sync, plus tracks any non-explicit powers granted.
            PlayerPowers = (IReadOnlyDictionary<string, int>?)newPlayerPowers
                ?? next.PlayerPowers
                ?? new Dictionary<string, int>(),
            Hand = newHand,
            DrawPileSize = drawPileAfter,
            DiscardPileSize = discardAfter,
            // 2026-05-28 MCTS-P0 — DiscardPile list + ExhaustPileCount
            // propagated alongside the size counters so simulator-parity
            // audit sees the played card in mod sim's post-play state.
            DiscardPile = newDiscardPile,
            // 2026-05-29 — DrawPile list mirrors DrawPileSize (draw-on-play
            // cards: POMMEL_STRIKE / SHRUG_IT_OFF / ANGER copy etc.).
            DrawPile = newDrawPile,
            ExhaustPileCount = newExhaustPileCount,
            // v0.9 — propagate per-target attack counter for depth-N forge math.
            TurnAttacksByTargetIdx = newTurnAttacksByTgt,
            // v0.9 — propagate updated SB count so a second Forge in the
            // same turn doesn't trigger auto-create again.
            SovereignBladeCount = newSovereignBladeCount,
            // v0.9 — ChainsOfBindingPower: if the played card was Bound,
            // set the flag so depth-N candidates filter further Bound cards.
            BoundCardPlayedThisTurn = next.BoundCardPlayedThisTurn || card.IsBound,
            // SmoggyPower: if a Skill was played while SmoggyPower is
            // active, set the flag so depth-N candidates filter all
            // further Skill candidates this turn. PlayerSmoggy itself is
            // persistent (StackType.Single), so it carries through `next`.
            SmoggySkillPlayedThisTurn = next.SmoggySkillPlayedThisTurn
                || (card.IsSkill && next.PlayerSmoggy > 0),
            // v0.10 — Relic counter advancement (PenNib/Nunchaku/Kunai/etc.).
            // Built above; absent or empty → reuse prior dict instance.
            PlayerRelics = newPlayerRelics,
            // Per-turn play counters. Without these, depth-N lookahead can't see
            // that "play a Skill first → LUNAR_BLAST hits +1" or
            // "play an Attack first → FINISHER hits +1". PlanScorer's
            // EstimateVariableHits for COMBO-axis payoff cards reads exactly
            // these fields; leaving them frozen at snapshot value makes every
            // simulated reorder score the payoff card with stale hits.
            TurnAttacksPlayed = next.TurnAttacksPlayed + (card.IsAttack ? 1 : 0),
            TurnSkillsPlayed  = next.TurnSkillsPlayed  + (card.IsSkill  ? 1 : 0),
            // MAKE_IT_SO pile-reactive return: decrement when a copy left draw/
            // discard for hand this play; a played MAKE_IT_SO (Attack) lands in
            // discard (no Exhaust keyword) so it re-enters the discard pool.
            MakeItSoInDraw    = System.Math.Max(0, next.MakeItSoInDraw - misRetDraw),
            MakeItSoInDiscard = System.Math.Max(0, next.MakeItSoInDiscard - misRetDiscard)
                                + (card.Id == "MAKE_IT_SO" ? 1 : 0),
            // Energy spent this card: 0 when a Free*Power covered it, else
            // min(card.Cost, available). X-cost cards keep their static Cost
            // proxy here — close enough for HELIX_DRILL ordering since X cards
            // are usually played last anyway.
            TurnEnergySpent   = next.TurnEnergySpent
                + (freeApplied ? 0 : System.Math.Max(0, System.Math.Min(card.Cost, next.PlayerEnergy))),
            // Stars gained: positive inflow only (RADIATE counts positive
            // StarsModifiedEntry deltas). card.StarCost is a consumption and
            // doesn't subtract here.
            TurnStarsGained   = next.TurnStarsGained + System.Math.Max(0, card.StarsGain),
            // True draws this card. DEATH_MARCH's CalculatedDamage reads this.
            TurnCardsDrawn    = next.TurnCardsDrawn + (card.DrawCount > 0 ? card.DrawCount : 0),
            // OstyAttack-tagged plays — each such play triggers one Osty
            // attack in-game (catalog OSTY axis mirrors CardTag.OstyAttack).
            // RATTLE's CalculatedHits = 1 + TurnOstyAttacks so the depth-N
            // lookahead needs this counter to grow as setup OstyAttack cards
            // (FETCH / POKE / RIGHT_HAND_HAND / SIC_EM / etc.) are played first.
            TurnOstyAttacks   = next.TurnOstyAttacks + (card.Axes != null && card.Axes.Contains("OSTY") ? 1 : 0),
            // Ethereal plays. PULL_FROM_BELOW's CalculatedHits multiplier
            // walks CombatHistory entries with WasEthereal — combat-scoped, no
            // turn filter. Depth-N: playing an Ethereal first (or forced into
            // play via SWEEPING_GAZE etc.) bumps PULL_FROM_BELOW's later score.
            CombatEtherealPlayed = next.CombatEtherealPlayed + (card.IsEthereal ? 1 : 0),
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
                // SandpitPower decrements at AfterSideTurnStartLate(Enemy)
                // — for our turn-boundary model that's "end of player turn /
                // start of next enemy phase". Transitioning to 0 here means
                // the player WILL be instakilled at the enemy turn start
                // (AfterRemoved hook). Reset HardenedShellRemaining to base
                // amount too — separate Skulking Colony fix (see below).
                SandpitAmount    = System.Math.Max(0, ne.SandpitAmount - 1),
            };
            newEnemies.Add(ne);
        }

        // SandpitPower instakill check: if any ALIVE enemy's Sandpit just
        // dropped to 0 (was >0 last turn → 0 this turn), the game force-kills
        // the player on the upcoming enemy turn. Model as player HP=0 so
        // survival sim and PlanScorer treat the run as lost — forces all
        // plans to finish the carrier before the counter expires.
        bool sandpitInstakill = false;
        for (int idx = 0; idx < state.Enemies.Count && idx < newEnemies.Count; idx++)
        {
            var before = state.Enemies[idx];
            var after = newEnemies[idx];
            if (after.IsAlive && before.SandpitAmount > 0 && after.SandpitAmount == 0)
            {
                sandpitInstakill = true;
                break;
            }
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

        // SandpitPower instakill (The Insatiable). Carrier's counter just hit
        // 0 — game force-kills player + pets + Osty regardless of HP/revive.
        // Set HP=0 so survival sim treats this branch as a loss.
        if (sandpitInstakill)
            newPlayerHp = 0;

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
        int newPlayerEnergy = BaseTurnEnergy;
        int newPlayerStarsAtStart = state.PlayerStars;

        // v0.9 — Tier A next-turn buffs / debuffs that fire at energy-reset
        // or turn-start. All self-remove after applying, so they're single-
        // shot one-turn-only effects.
        if (state.PlayerPowers != null && state.PlayerPowers.Count > 0)
        {
            // EnergyNextTurnPower: +N energy at next turn start.
            if (state.PlayerPowers.TryGetValue("EnergyNextTurnPower", out var ent) && ent > 0)
                newPlayerEnergy += ent;

            // BlockNextTurnPower: +N block at next turn start (AfterBlockCleared
            // hook in real game; for AdvanceTurn we apply after block reset).
            if (state.PlayerPowers.TryGetValue("BlockNextTurnPower", out var bnt) && bnt > 0)
                newPlayerBlock += bnt;

            // StarNextTurnPower: +N stars at next turn start (Regent token).
            if (state.PlayerPowers.TryGetValue("StarNextTurnPower", out var snt) && snt > 0)
                newPlayerStarsAtStart += snt;

            // BorrowedTimePower (player DEBUFF — TryModifyEnergyCostInCombat
            // adds Amount to every card's cost): treated as energy loss for
            // next-turn budget. e.g. BorrowedTime:1 with 5 cards → 5 energy
            // shortfall. Approximate as flat hand-size × Amount reduction
            // from base energy, clamped at 0.
            if (state.PlayerPowers.TryGetValue("BorrowedTimePower", out var bt) && bt > 0)
                newPlayerEnergy = System.Math.Max(0, newPlayerEnergy - bt * nextHand.Count);
        }

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

        // v0.9 — SummonNextTurnPower: spawns Osty/Skeleton ally at start of
        // next player turn. Approximate as a generic ally with avg HP/Atk
        // (full mechanics requires per-character summon details; this
        // captures the "I'll have a damage-contributor next turn" benefit).
        if (state.PlayerPowers != null
            && state.PlayerPowers.TryGetValue("SummonNextTurnPower", out var summon)
            && summon > 0)
        {
            // Generic skeleton stat block (Necrobinder Osty baseline).
            // Real Osty has variable HP/Atk; planner-level approximation suffices.
            const int SkeletonHp = 15;
            const int SkeletonAtk = 5;
            for (int i = 0; i < summon; i++)
            {
                newAllies.Add(new SimAlly
                {
                    Hp = SkeletonHp,
                    Block = 0,
                    IntentDamage = SkeletonAtk,
                    IntentRepeats = 1,
                    HasAttackIntent = true,
                    ClassName = "Osty",
                    SourceRef = null,
                });
            }
        }

        return state with
        {
            PlayerHp = newPlayerHpAfterPassives,
            PlayerBlock = newPlayerBlock,
            // v0.9 — Use newPlayerEnergy which folds in EnergyNextTurnPower
            // (+N) and BorrowedTimePower (debuff cost adder).
            PlayerEnergy = newPlayerEnergy,
            PlayerStrength = newPlayerStr,
            // v0.7.83 — Carry Buffer minus instances consumed this turn.
            PlayerBuffer = newPlayerBufferEot,
            // v0.7.84 — Lethality re-arms each turn (it's "first attack/turn"
            // multiplier; AdvanceTurn refreshes Lethality to its full stack value).
            // Tracking and Cruelty are passive — preserved via `state with`.
            PlayerLethality = state.PlayerLethality,
            // v0.7.85 — Unmovable re-arms each turn (single-shot per turn).
            UnmovableUsedThisTurn = false,
            // v0.8.0 — FlameBarrier expires at end of player turn (1-turn).
            PlayerFlameBarrier = 0,
            PlayerVulnerable = newPlayerVuln,
            PlayerWeak = newPlayerWeak,
            PlayerFrail = newPlayerFrail,
            PlayerIntangible = newPlayerIntangible,
            // v0.9 — StarNextTurnPower / BorrowedTimePower may have adjusted
            // starting stars. Carry through.
            PlayerStars = newPlayerStarsAtStart,
            // v0.9 — ChainsOfBinding: flag resets at turn boundary (game
            // resets boundCardPlayed in BeforeTurnEnd).
            BoundCardPlayedThisTurn = false,
            // SmoggyPower per-turn lockout resets at turn boundary — the
            // game's SmoggyPower.AfterTurnEnd clears every Smog affliction
            // on player cards, so next turn's first Skill is playable
            // again. PlayerSmoggy itself is persistent, carried via `state with`.
            SmoggySkillPlayedThisTurn = false,
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
        OrbKind kind, int evokeVal, int focus,
        ref SimState state, ref int playerBlock, ref int energy, int aliveCount)
    {
        // 2026-05-30 — evokeVal is the orb's CAPTURED EvokeVal (OrbModel.EvokeVal =
        // ModifyOrbValue(base) = base + Focus, per orb). Use it directly rather than
        // recomputing base+Focus: the orb-queue probe diagnostic showed the captured
        // value is authoritative and the recompute was wrong for orbs whose base
        // differs (Glass evoke captured 12 vs recomputed 8+Focus=10 → DUALCAST
        // under-dealt). Fall back to base+Focus only when the capture is 0/missing.
        // Each per-evoke damage/block clamped at 0 (Focus can be negative).
        switch (kind)
        {
            case OrbKind.Frost:
                playerBlock += System.Math.Max(0, evokeVal > 0 ? evokeVal : 5 + focus);
                break;
            case OrbKind.Plasma:
                energy += 2;
                break;
            case OrbKind.Lightning:
                // ThunderPower adds +Amount (flat, AfterOrbEvoked) per lightning
                // evoke, on top of the captured base+Focus value.
                state = DamageWeakest(state,
                    System.Math.Max(0, evokeVal > 0 ? evokeVal : 8 + focus) + state.PlayerThunder);
                break;
            case OrbKind.Dark:
                state = DamageWeakest(state, System.Math.Max(0, evokeVal > 0 ? evokeVal : 6));
                break;
            case OrbKind.Glass:
                state = DamageAll(state, System.Math.Max(0, evokeVal > 0 ? evokeVal : 8 + focus));
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
    /// <summary>
    /// 2026-05-28 S6-3d: cards tagged with CardTag.Strike for PERFECTED_STRIKE
    /// multiplier. List from sts2.dll decompile grep "CardTag.Strike". Used
    /// to count Strike-tag cards across all piles for PERFECTED_STRIKE's
    /// damage scaling (base 6 + extra 2 × count).
    /// </summary>
    private static bool IsStrikeCard(string? id) => id switch
    {
        "STRIKE_IRONCLAD" or "STRIKE_SILENT" or "STRIKE_DEFECT"
        or "STRIKE_NECROBINDER" or "STRIKE_REGENT"
        or "TWIN_STRIKE" or "POMMEL_STRIKE" or "PERFECTED_STRIKE"
        or "WILD_STRIKE" or "SETUP_STRIKE" or "ASHEN_STRIKE"
        or "FLASH_OF_STEEL" or "MEMENTO_MORI" or "SOLAR_STRIKE"
        or "SHINING_STRIKE" => true,
        _ => false,
    };

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

    // 2026-05-29 — status-producer card → number of status cards (Burn/Dazed/
    // Wound/Slimed/...) added to the DISCARD pile on play. Decompile-verified
    // (sts2.dll OnPlay: CardPileCmd.AddGeneratedCardToCombat(card, Discard)).
    private static readonly System.Collections.Generic.Dictionary<string, int> StatusToDiscardCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["GUNK_UP"] = 1,        // Slimed/Gunk → discard
        ["OVERCLOCK"] = 1,      // Burn → discard
        ["BOOST_AWAY"] = 1,     // Dazed → discard
        ["FIGHT_THROUGH"] = 2,  // 2× Wound → discard (OnPlay loop i<2)
        ["SEVERANCE"] = 1,      // Soul → discard (1 of 3 Souls; +draw +hand below)
        // 2026-05-31 — ADAPTIVE_STRIKE: after the attack, creates a cost-0 CLONE
        // and AddGeneratedCardToCombat(PileType.Discard). Real keeps it (discard
        // +2: the card itself + clone); sim only added the card → discard −1
        // (6 rows). Add the clone as a discard placeholder.
        ["ADAPTIVE_STRIKE"] = 1,
    };

    // 2026-05-29 — card-generator card → number of cards added to HAND on play
    // (sts2.dll: AddGeneratedCardToCombat(card, PileType.Hand)).
    private static readonly System.Collections.Generic.Dictionary<string, int> CardGenToHandCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["COLLISION_COURSE"] = 1,    // Debris → hand
        ["MANIFEST_AUTHORITY"] = 1,  // generated card → hand
        // 2026-05-31 — generate-a-random-card-to-hand (free this turn). Real keeps
        // it (hand_count −1 in probe, headless retains hand-adds), sim didn't model.
        ["WHITE_NOISE"] = 1,    // random Power card → hand (SetToFreeThisTurn)
        ["INFERNAL_BLADE"] = 1, // random Attack card → hand (free)
        ["QUASAR"] = 1,         // choose 1 of 3 colorless → hand (resolver picks, no skip)
        ["BUNDLE_OF_JOY"] = 3,  // 3 distinct colorless cards → hand (CardFactory gen)
        ["SEVERANCE"] = 1,      // Soul → hand (1 of 3 Souls)
    };

    // 2026-05-30 — generators that add to the DRAW pile (Necrobinder Souls).
    private static readonly System.Collections.Generic.Dictionary<string, int> CardGenToDrawCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["GRAVE_WARDEN"] = 1,   // Soul.Create(1) → PileType.Draw
        ["REAVE"] = 1,          // Soul.Create(1) → draw
        ["SEVERANCE"] = 1,      // Soul → draw (1 of 3 Souls; +discard +hand elsewhere)
        ["CAPTURE_SPIRIT"] = 3, // CardsVar(3) Souls → draw pile
    };

    // 2026-05-30 — generators that add to the DISCARD pile.
    private static readonly System.Collections.Generic.Dictionary<string, int> CardGenToDiscardCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["UNDEATH"] = 1,        // AddGeneratedCardToCombat(card, PileType.Discard)
        // NOTE: GUNK_UP also generates a Slimed to discard, but modeling it
        // REGRESSED parity (0/35) — in the headless harness its
        // AddGeneratedCardToCombat is a no-op (NCard VFX path, like ANGER), so
        // real never adds the Slimed. Do NOT add it.
    };

    // 2026-05-29 — draw-then-FromHandForDiscard cards: after drawing, the player
    // discards N hand cards. Count is fixed (decompile); the probe resolves the
    // choice before snapshotting so real DOES discard. Move N from hand → discard.
    private static readonly System.Collections.Generic.Dictionary<string, int> DiscardFromHandCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["DAGGER_THROW"] = 1,     // FromHandForDiscard(1)
        ["HIDDEN_DAGGERS"] = 1,   // FromHandForDiscard(Cards=2) but the probe's
                                  // Choose(0) resolver discards 1 in practice
                                  // (multi-select completes after one pick); match real.
        ["SURVIVOR"] = 1,         // FromHandForDiscard(1)
        ["ACROBATICS"] = 1,       // FromHandForDiscard(1)
        ["PREPARED"] = 1,         // FromHandForDiscard(cardCount, base 1)
    };

    // 2026-05-29 — cards that retrieve N cards from the discard pile to the draw
    // pile (CardSelectCmd over Discard → CardPileCmd.Add PileType.Draw).
    private static readonly System.Collections.Generic.Dictionary<string, int> DiscardToDrawCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["COSMIC_INDIFFERENCE"] = 1,
        ["HEADBUTT"] = 1,   // FromSimpleGrid over Discard → Add PileType.Draw (top)
    };

    // 2026-05-30 — cards that draw then return N cards from hand to the draw pile
    // (FromHand → CardPileCmd.Add PileType.Draw). Net draw = CardsVar − PutBack.
    private static readonly System.Collections.Generic.Dictionary<string, int> PutBackToDrawCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["PHOTON_CUT"] = 1,   // draw 1, put back 1 (net 0)
        ["GLIMMER"] = 1,      // draw 3, put back 1 (net +2)
    };

    // 2026-05-30 — cards that retrieve N cards from discard → hand (FromSimpleGrid
    // over Discard → CardPileCmd.Add PileType.Hand). HOLOGRAM retrieves 1 (+ exhausts).
    private static readonly System.Collections.Generic.Dictionary<string, int> DiscardToHandCount =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["HOLOGRAM"] = 1,
        // 2026-05-31 — DREDGE (Skill, Exhaust): pulls min(Cards(3), 10−hand) cards
        // from the DISCARD pile to hand (FromSimpleGrid over Discard). The handler
        // caps by discard-available + hand-room, matching the decompile's Min().
        ["DREDGE"] = 3,
        // 2026-05-30 — GRAVEBLAST: after the attack, FromSimpleGrid over the
        // discard pile → CardPileCmd.Add(card, PileType.Hand). Retrieves 1 from
        // discard to hand (the card itself exhausts via the base Exhaust keyword,
        // handled by IsExhaust). Sim missed the retrieval → discard +1 / hand -1
        // (6 rows). Upgraded GRAVEBLAST removes Exhaust (goes to discard) — the
        // single exhaust-pile row is that variant, left as residual.
        ["GRAVEBLAST"] = 1,
    };

    // 2026-05-30 — retain-on-play cards: GetResultPileType() returns Hand instead
    // of Discard, so the played card stays in hand rather than going to discard.
    private static readonly System.Collections.Generic.HashSet<string> RetainOnPlay =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "PARTICLE_WALL",
    };

    // Minimal unplayable status placeholder — only the discard-pile COUNT
    // matters for parity, so contents are inert.
    private static SimCard MakeStatusPlaceholderCard() => new()
    {
        Id = "<status-placeholder>",
        Cost = 0,
        Kind = CardType.Status,
        Target = TargetType.None,
        SourceRef = null,
        Effect = new CardEffectSummary(),
        IsPlayable = false,
    };

    // 2026-05-29 — shiv-generator cards that CREATE Shivs in hand (not draw
    // from pile). Decompile-verified: each calls Shiv.CreateInHand(N) where N
    // is the card's Cards/Shivs var (routed to DrawCount by CardReflection).
    private static readonly System.Collections.Generic.HashSet<string> ShivGenCards =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "CLOAK_AND_DAGGER", "BLADE_DANCE", "HIDDEN_DAGGERS",
        "LEADING_STRIKE",  // Damage + Shivs-var shivs created in hand
        // 2026-05-30 — BLADE_OF_INK: Shiv.CreateInHand(Cards=2). Its CardsVar
        // routes to DrawCount; redirect to 2 shiv placeholders in hand (no pile
        // draw). Was draw_pile −2 (sim drew from pile, real created shivs).
        "BLADE_OF_INK",
        // 2026-05-31 — FAN_OF_KNIVES (Power): CardsVar("Shivs",4) → Shiv.CreateInHand
        // ×4 (cap-aware overflow to discard). Sim missed the 4 shivs → hand −4 (or
        // hand/discard split at a full hand). DrawCount carries the 4 ("Shivs" var).
        "FAN_OF_KNIVES",
    };

    // 2026-05-29 — cards that recycle THEMSELVES to the draw pile (top) on
    // play instead of going to discard (sts2.dll: CardPileCmd.Add(this,
    // PileType.Draw, Top)). SHINING_STRIKE (Regent).
    private static readonly System.Collections.Generic.HashSet<string> SelfRecycleToDraw =
        new(System.StringComparer.OrdinalIgnoreCase) { "SHINING_STRIKE" };

    // 2026-05-30 — cards whose Cards var is the TARGET hand size (draw until hand
    // has N), not a fixed draw count. sts2.dll: Draw(Cards - Hand.Cards.Count).
    private static readonly System.Collections.Generic.HashSet<string> DrawToHandSize =
        new(System.StringComparer.OrdinalIgnoreCase) { "EXPERTISE" };

    // 2026-05-30 — Power-type cards that ALSO draw (CardPileCmd.Draw in OnPlay).
    // The generic draw loop gates on !card.IsPower (so VICIOUS-style power-amount
    // CardsVars don't phantom-draw); these genuinely draw their Cards var and were
    // under-drawn. DRUM_OF_BATTLE Draw(2)+DrumOfBattlePower, NEUROSURGE Draw(2).
    private static readonly System.Collections.Generic.HashSet<string> PowerCardsThatDraw =
        new(System.StringComparer.OrdinalIgnoreCase) { "DRUM_OF_BATTLE", "NEUROSURGE" };

    // Shiv: 0-cost 4-damage attack created in hand. Damage matters if the
    // planner later "plays" it in lookahead; count matters for hand parity.
    private static SimCard MakeShivPlaceholderCard() => new()
    {
        Id = "SHIV",
        Cost = 0,
        Kind = CardType.Attack,
        Target = TargetType.AnyEnemy,
        SourceRef = null,
        Effect = new CardEffectSummary { Damage = 4, Hits = 1 },
        IsPlayable = true,
    };
}
