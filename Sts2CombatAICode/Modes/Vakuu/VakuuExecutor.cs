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
                // Also dump live enemy HP/block so we can detect cases where damage was
                // expected but absorbed by an invisible mechanism (Buffer/Intangible/etc.).
                var hpSummary = string.Join(",",
                    combatState.HittableEnemies.Select(e => {
                        int chp = (int)(CombatReflection.CreatureHpField?.GetValue(e) ?? 0);
                        int cbl = (int)(CombatReflection.CreatureBlockField?.GetValue(e) ?? 0);
                        return $"{e.GetType().Name}({chp}/b{cbl})";
                    }));
                MainFile.Logger.Info(
                    $"[CombatAI] step {step + 1} post-play: " +
                    $"enemiesAlive={aliveAfter} combatEnding={CombatManager.Instance.IsOverOrEnding} " +
                    $"hp=[{hpSummary}] ({stepWatch.ElapsedMilliseconds}ms)");
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
