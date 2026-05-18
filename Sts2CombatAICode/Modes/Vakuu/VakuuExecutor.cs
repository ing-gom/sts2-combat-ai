using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using Sts2CombatAI.Diagnostics;
using Sts2CombatAI.Planner;
using Sts2CombatAI.Reflection;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Modes.Vakuu;

/// <summary>
/// Reusable planner main loop. Called from:
///   • <see cref="WhisperingEarringPlannerPatch"/> — the production hook
///   • test buttons / debug entry points — to iterate without needing the relic
///
/// Mirrors the original <c>WhisperingEarring.BeforePlayPhaseStartLate</c> structure
/// (PushSelector → 13-step loop → SpendResources + AutoPlay → TalkCmd finale). Relic flash
/// + voice line are skipped when invoked without a relic instance.
/// </summary>
internal static class VakuuExecutor
{
    /// <summary>
    /// SimState snapshot captured at the start of each planner step. Read by the
    /// SmartVakuuCardSelector Harmony patches so mid-card prompts can score against
    /// the current combat state. Null when no Vakuu turn is in flight.
    /// </summary>
    public static SimState? CurrentSnapshot { get; private set; }

    /// <summary>
    /// ID of the card currently being auto-played (set just before CardCmd.AutoPlay,
    /// cleared after). Read by VakuuCardSelectorPatches to infer SelectorMode
    /// (Burn vs Boost) for mid-play prompts.
    /// </summary>
    public static string? CurrentPlayingCardId { get; internal set; }

    public static async Task RunPlannedTurn(
        Player player,
        PlayerChoiceContext ctx,
        WhisperingEarring? relicForVfx = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var combatState = player.Creature?.CombatState;
        if (combatState == null)
        {
            MainFile.Logger.Warn("[CombatAI] no combat state, aborting");
            return;
        }

        int cardsPlayed = 0;
        bool hitLimit = false;

        // v0.7.41 — Capture turn-start HP so the turn-end summary entry can
        // log this turn's net HP change (damage taken = start − end).
        int turnStartPlayerHp = 0;
        try { turnStartPlayerHp = (int)(CombatReflection.CreatureHpField?.GetValue(player.Creature) ?? 0); }
        catch { }

        relicForVfx?.Flash();
        MainFile.Logger.Info($"[CombatAI] starting plan (style={PlaystyleState.Current})");

        try
        {
        using (CardSelectCmd.PushSelector(new VakuuCardSelector()))
        {
            for (int step = 0; step < 13; step++)
            {
                var stepWatch = System.Diagnostics.Stopwatch.StartNew();
                if (CombatManager.Instance.IsOverOrEnding)
                {
                    MainFile.Logger.Info($"[CombatAI] step {step + 1} break: combat over/ending");
                    break;
                }
                if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
                {
                    MainFile.Logger.Info($"[CombatAI] step {step + 1} break: player ready to end turn");
                    break;
                }

                var snapshot = StateSnapshotter.Capture(player);
                if (snapshot == null)
                {
                    MainFile.Logger.Warn("[CombatAI] snapshot null, aborting plan");
                    break;
                }
                CurrentSnapshot = snapshot;
                MainFile.Logger.Info($"[CombatAI] step {step + 1} snapshot: {StateSnapshotter.FormatForLog(snapshot)}");
                // v0.7.55 — Deck throughput diagnostic (once per step turn-start).
                if (step == 0)
                {
                    // v0.7.59 — Notify CombatPlan of the current round so
                    // Stage.Opening / mature-game classification works.
                    Sts2CombatAI.Planner.CombatPlan.NotifyTurn(combatState.RoundNumber);
                    var tp = Sts2CombatAI.Planner.DeckThroughput.Compute(snapshot);
                    double dmgCov = Sts2CombatAI.Planner.DeckThroughput.DamageCoverage(snapshot, tp);
                    double blkCov = Sts2CombatAI.Planner.DeckThroughput.BlockCoverage(snapshot, tp);
                    string atk = string.Join(",", tp.CoreAttackers);
                    string def = string.Join(",", tp.CoreDefenders);
                    string cyc = string.Join(",", tp.CoreCyclers);
                    MainFile.Logger.Info(
                        $"[CombatAI]   throughput: dpt={tp.AvgDamagePerTurn} bpt={tp.AvgBlockPerTurn} " +
                        $"dmgCov={dmgCov:F2} blkCov={blkCov:F2} " +
                        $"deck={tp.DeckSize} cardsPerTurn={tp.EstimatedCardsPerTurn} cycle={tp.TurnsPerCycle}turns");
                    MainFile.Logger.Info(
                        $"[CombatAI]   core: atk=[{atk}] def=[{def}] cyc=[{cyc}]");
                    // v0.7.57 — Survival race projection
                    var proj = Sts2CombatAI.Planner.SurvivalProjection.Compute(snapshot, tp);
                    MainFile.Logger.Info(
                        $"[CombatAI]   race: {proj.Race} ttd={proj.TurnsToDeath} ttk={proj.TurnsToKill} " +
                        $"hpLoss/turn={proj.NetHpLossPerTurn} dmg/turn={proj.NetDamagePerTurn}");
                    // v0.7.58 — Deck quality diagnostic
                    var quality = Sts2CombatAI.Planner.DeckQuality.Compute(snapshot);
                    MainFile.Logger.Info($"[CombatAI]   quality: {Sts2CombatAI.Planner.DeckQuality.Describe(quality)}");
                    // v0.7.59 — Plan stage
                    var planStage = Sts2CombatAI.Planner.CombatPlan.Classify(snapshot, proj);
                    MainFile.Logger.Info($"[CombatAI]   plan: stage={planStage}");
                    // v0.7.60 — Deck role mix
                    var roleMix = Sts2CombatAI.Planner.CardRole.DeckMix(snapshot);
                    MainFile.Logger.Info($"[CombatAI]   roles: {roleMix}");
                    // v0.7.61 — Finisher identification
                    var fin = Sts2CombatAI.Planner.FinisherIdentifier.Identify(snapshot);
                    if (fin.TopFinishers.Count > 0)
                    {
                        string finStr = string.Join(", ",
                            fin.TopFinishers.Select(f =>
                            {
                                string loc = f.InHand ? "hand" : f.PileLocation == 1 ? "draw" : "disc";
                                return $"{f.CardId}@{loc}={f.EstimatedDamage}";
                            }));
                        MainFile.Logger.Info($"[CombatAI]   finishers: [{finStr}]");
                    }
                }

                var plan = ActionPlanner.PlanNextStep(snapshot);
                if (plan == null)
                {
                    MainFile.Logger.Info($"[CombatAI] step {step + 1} no playable card, stopping");
                    break;
                }

                // v0.4 — dump the top scoring candidates so we can see *why* a particular card
                // won. Shows first-score + second-step-best + lookahead total per candidate.
                // v0.5 — also show which follow-up the depth-2 picked, so combo decisions
                // ("Bash because depth-2 picks vuln-boosted Strike") become inspectable.
                var topCands = ActionPlanner.LastCandidates
                    .OrderByDescending(c => c.total)
                    .Take(5)
                    .Select(c =>
                    {
                        var nextTag = string.IsNullOrEmpty(c.bestNextId) ? "" : $"→{c.bestNextId}";
                        return $"{c.id}@{c.targetIdx}=1st:{c.firstScore}+2nd:{c.secondScore}{nextTag}={c.total}";
                    });
                MainFile.Logger.Info($"[CombatAI]   top: {string.Join(" | ", topCands)}");

                Creature? target = ResolveTarget(plan.Value, snapshot, combatState, player);

                var pickWeights = PlanScorerWeights.For(PlaystyleState.Current);
                var breakdown = PlanScorer.Breakdown(plan.Value.Card, plan.Value.TargetIdx,
                    snapshot, pickWeights);
                int playOrderBias = PlanScorer.PlayOrderBias(plan.Value.Card, snapshot, pickWeights);
                var targetName = target?.GetType().Name ?? "self";
                MainFile.Logger.Info(
                    $"[CombatAI] step {step + 1} → {plan.Value.Card.Id}@{targetName} " +
                    $"(score={plan.Value.Score} reason={plan.Value.Reason})");
                var biasTag = playOrderBias != 0 ? $" playOrder={playOrderBias:+0;-0}" : "";
                MainFile.Logger.Info($"[CombatAI]   breakdown: {breakdown.ToLogLine()}{biasTag}");

                // v0.6.3 — runtime context fields for DecisionLogPersister. Behavioral
                // flags parsed from breakdown.Details with substring search to avoid
                // adding new ScoreBreakdown fields. Persister handles 0/false defaults.
                var detailsLine = breakdown.ToLogLine();
                int enemyHp = 0;
                foreach (var e in snapshot.Enemies) if (e.IsAlive) enemyHp += e.Hp;
                int comboLinks = 0;
                int comboIdx = detailsLine.IndexOf("combo(", System.StringComparison.Ordinal);
                if (comboIdx >= 0 && comboIdx + 6 < detailsLine.Length)
                {
                    int linkEnd = detailsLine.IndexOf("link", comboIdx + 6, System.StringComparison.Ordinal);
                    if (linkEnd > comboIdx + 6)
                        int.TryParse(detailsLine.Substring(comboIdx + 6, linkEnd - comboIdx - 6), out comboLinks);
                }
                bool lethalActive = detailsLine.IndexOf("lethalMode=", System.StringComparison.Ordinal) >= 0;

                DecisionLog.Record(new DecisionLog.Entry
                {
                    Timestamp = System.DateTime.Now,
                    Step = step + 1,
                    Playstyle = PlaystyleState.Current.ToString(),
                    CardId = plan.Value.Card.Id,
                    TargetName = targetName,
                    Score = plan.Value.Score,
                    Reason = plan.Value.Reason,
                    SnapshotSummary = StateSnapshotter.FormatForLog(snapshot),
                    BreakdownDetails = detailsLine,
                    Turn = combatState.RoundNumber,
                    EnemyHpBefore = enemyHp,
                    PlayerHpBefore = snapshot.PlayerHp,
                    PlayerBlockBefore = snapshot.PlayerBlock,
                    LethalActive = lethalActive,
                    IsFetchCard = plan.Value.Card.IsFetchTrigger,
                    ComboLinks = comboLinks,
                    Character = player.Creature?.GetType().Name ?? "",
                });

                var card = plan.Value.Card.SourceRef;
                if (card == null)
                {
                    MainFile.Logger.Warn($"[CombatAI] step {step + 1} card SourceRef null, skipping");
                    break;
                }
                // Final defensive check — CanPlay() can flip between snapshot and execution
                // (e.g., another card just changed the state). Don't try to play unplayable.
                bool canPlayNow = false;
                try { canPlayNow = card.CanPlay(); } catch { }
                if (!canPlayNow)
                {
                    MainFile.Logger.Warn($"[CombatAI] step {step + 1} card not playable at execute time, skipping: {plan.Value.Card.Id}");
                    break;
                }
                CurrentPlayingCardId = plan.Value.Card.Id;
                try
                {
                    await card.SpendResources();
                    await CardCmd.AutoPlay(ctx, card, target, AutoPlayType.Default, skipXCapture: true);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[CombatAI] play failed at step {step + 1} ({plan.Value.Card.Id}): {ex.Message}");
                    break;
                }
                finally
                {
                    CurrentPlayingCardId = null;
                }

                cardsPlayed++;
                stepWatch.Stop();
                // Post-play diagnostics: did the kill resolve, is combat ending?
                int aliveAfter = combatState.HittableEnemies.Count(e => e.IsAlive);
                int aliveBefore = snapshot.Enemies.Count(e => e.IsAlive);
                // Also dump live enemy HP/block so we can detect cases where damage was
                // expected but absorbed by an invisible mechanism (Buffer/Intangible/etc.).
                int enemyHpAfterSum = 0;
                var hpSummary = string.Join(",",
                    combatState.HittableEnemies.Select(e => {
                        int chp = (int)(CombatReflection.CreatureHpField?.GetValue(e) ?? 0);
                        int cbl = (int)(CombatReflection.CreatureBlockField?.GetValue(e) ?? 0);
                        if (e.IsAlive) enemyHpAfterSum += chp;
                        return $"{e.GetType().Name}({chp}/b{cbl})";
                    }));
                MainFile.Logger.Info(
                    $"[CombatAI] step {step + 1} post-play: " +
                    $"enemiesAlive={aliveAfter} combatEnding={CombatManager.Instance.IsOverOrEnding} " +
                    $"hp=[{hpSummary}] ({stepWatch.ElapsedMilliseconds}ms)");

                // v0.7.41 — Capture post-play state so DecisionLog records the
                // outcome alongside the prediction. Live reflection rather than
                // a full snapshot so we don't pay the snapshotter cost every step.
                int playerHpAfter = (int)(CombatReflection.CreatureHpField?.GetValue(player.Creature) ?? 0);
                int playerBlockAfter = (int)(CombatReflection.CreatureBlockField?.GetValue(player.Creature) ?? 0);
                int damageDealt = enemyHp - enemyHpAfterSum;  // pre-step total minus post-step total
                int selfDamage = snapshot.PlayerHp - playerHpAfter;
                bool killedEnemy = aliveAfter < aliveBefore;
                DecisionLog.UpdateLastOutcome(
                    playerHpAfter, playerBlockAfter,
                    enemyHpAfterSum, damageDealt,
                    selfDamage, killedEnemy);
            }
            hitLimit = (cardsPlayed >= 13);

            if (cardsPlayed == 0)
            {
                sw.Stop();
                MainFile.Logger.Info($"[CombatAI] no cards playable ({sw.ElapsedMilliseconds}ms)");
                return;
            }
        }

        sw.Stop();
        bool allEnemiesDead = combatState.HittableEnemies.All(e => !e.IsAlive);

        // v0.7.41 — Turn-end summary entry. Records the result of THIS player
        // turn after all card plays. Enemy-turn incoming damage will land
        // between this entry and the next turn's first entry, so analyzers
        // can compute it from consecutive entries.
        try
        {
            int turnEndPlayerHp = (int)(CombatReflection.CreatureHpField?.GetValue(player.Creature) ?? 0);
            int turnEndPlayerBlock = (int)(CombatReflection.CreatureBlockField?.GetValue(player.Creature) ?? 0);
            int turnEndEnemyHpSum = 0;
            foreach (var e in combatState.HittableEnemies)
            {
                if (!e.IsAlive) continue;
                int chp = (int)(CombatReflection.CreatureHpField?.GetValue(e) ?? 0);
                turnEndEnemyHpSum += chp;
            }
            DecisionLog.Record(new DecisionLog.Entry
            {
                Timestamp = System.DateTime.Now,
                Step = cardsPlayed + 1,
                Playstyle = PlaystyleState.Current.ToString(),
                CardId = "<TURN_END>",
                TargetName = "",
                Score = 0,
                Reason = "turn-end-summary",
                SnapshotSummary = "",
                BreakdownDetails = "",
                Turn = combatState.RoundNumber,
                EnemyHpBefore = turnEndEnemyHpSum,
                PlayerHpBefore = turnEndPlayerHp,
                PlayerBlockBefore = turnEndPlayerBlock,
                LethalActive = false,
                IsFetchCard = false,
                ComboLinks = 0,
                Character = player.Creature?.GetType().Name ?? "",
                IsTurnEnd = true,
                TurnHpStart = turnStartPlayerHp,
                TurnHpEnd = turnEndPlayerHp,
                TurnDamageTaken = System.Math.Max(0, turnStartPlayerHp - turnEndPlayerHp),
                TurnCardsPlayed = cardsPlayed,
            });
        }
        catch { /* defensive */ }

        MainFile.Logger.Info(
            $"[CombatAI] turn complete, {cardsPlayed} cards played, " +
            $"took {sw.ElapsedMilliseconds}ms total, " +
            $"combatEnding={CombatManager.Instance.IsOverOrEnding} allDead={allEnemiesDead}");

        // v0.6.3 — flush DecisionLog ring buffer to disk when this turn ended
        // the combat. Best-effort: a clean exit (boss kill, player ko) lands here;
        // other combat-end paths (manual end-turn that kills via passive damage,
        // game close mid-combat) may miss the hook. Phase A scope — Phase D will
        // add a Harmony patch on CombatManager.End for completeness.
        if (CombatManager.Instance.IsOverOrEnding || allEnemiesDead)
        {
            var character = player.Creature?.GetType().Name ?? "unknown";
            int floor = 0;
            try { floor = combatState.RoundNumber; } catch { /* defensive */ }
            DecisionLogPersister.FlushIfPending(
                character,
                floor,
                sw.Elapsed.Ticks.ToString());
        }

        // Voice line only when invoked via the actual relic — test button stays quiet.
        // Skip the talk when combat is ending (avoids a barge into the combat-end transition).
        if (relicForVfx != null && !CombatManager.Instance.IsOverOrEnding)
        {
            var line = hitLimit
                ? new LocString("relics", "WHISPERING_EARRING.warning")
                : new LocString("relics", "WHISPERING_EARRING.approval");
            TalkCmd.Play(line, player.Creature, VfxColor.Purple);
        }

        // If Vakuu killed every enemy this turn the engine doesn't auto-advance:
        // CombatManager.IsEnding only reports the dead-enemies *state*, it does NOT
        // start the end-combat transition on its own. Vanilla's dumb FirstOrDefault
        // Vakuu never kills everything in turn 1, so the engine never gets a stuck
        // play-phase to fall out of. We explicitly enqueue EndPlayerTurnAction —
        // that drains the action queue, lets discard tweens finish, and triggers
        // the engine's combat-end check.
        if (allEnemiesDead && CombatManager.Instance.IsInProgress)
        {
            try
            {
                int round = combatState.RoundNumber;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(player, round));
                MainFile.Logger.Info("[CombatAI] all enemies dead — requested EndPlayerTurnAction");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[CombatAI] failed to enqueue EndPlayerTurnAction: {ex.Message}");
            }
        }
        }
        finally
        {
            // Always clear static state — exception path safe.
            CurrentSnapshot = null;
            CurrentPlayingCardId = null;
        }
    }

    private static Creature? ResolveTarget(
        ActionPlanner.PlanStep plan, SimState snapshot, CombatState combatState, Player player)
    {
        if (plan.TargetIdx >= 0 && plan.TargetIdx < snapshot.Enemies.Count)
        {
            var picked = snapshot.Enemies[plan.TargetIdx].SourceRef;
            if (picked.IsAlive) return picked;
        }

        var card = plan.Card.SourceRef;
        return card.TargetType switch
        {
            TargetType.AnyEnemy => combatState.HittableEnemies.FirstOrDefault(),
            TargetType.AnyAlly => combatState.Allies
                .Where(c => c != null && c.IsAlive && c.IsPlayer && c != player.Creature)
                .FirstOrDefault(),
            TargetType.AnyPlayer => player.Creature,
            _ => null,
        };
    }
}
