using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Planner;

/// <summary>
/// Picks the next single card to play. v0.1 is a single-step scorer (no DFS lookahead)
/// since we re-snapshot after each play. Enumerates all (card, target) candidates given
/// energy + hand constraints, scores each via PlanScorer, returns the best.
///
/// v0.2 will add DFS lookahead over remaining-turn sequences for tiebreaking.
/// </summary>
internal static class ActionPlanner
{
    /// <summary>
    /// v0.7.73 — Catalog-derived star_cost lookup per card-id. Used by
    /// EnumerateCandidates to detect when a card's snapshot-time IsPlayable
    /// was false purely due to insufficient stars — in which case the
    /// AnalyticalSimulator's depth-N state (with updated PlayerStars) may
    /// have made it playable.
    ///
    /// Generated from scripts/cards_catalog.json (star_cost field). Update
    /// when STS2 patches add new star-cost cards.
    /// </summary>
    // v0.7.81 — Keys are unprefixed Id.Entry values. v0.7.73 used "CARD." prefix and
    // never matched (verified by v0.7.80 stars diagnostic showing sc.Id = "FALLING_STAR").
    // v0.10 — promoted to internal so PlanScorer.IsLethalThisTurn can re-evaluate
    // star-blocked attacks when the hand also contains stars-generating skills.
    internal static readonly System.Collections.Generic.Dictionary<string, int> StarCostByCardId = new()
    {
        ["CLOAK_OF_STARS"] = 1,
        ["CRESCENT_SPEAR"] = 1,
        ["FALLING_STAR"] = 2,
        ["GUIDING_STAR"] = 2,
        ["METEOR_SHOWER"] = 2,
        ["PARTICLE_WALL"] = 2,
        ["QUASAR"] = 2,
        ["ALIGNMENT"] = 3,
        ["ASTRAL_PULSE"] = 3,
        ["DYING_STAR"] = 3,
        ["GAMMA_BLAST"] = 3,
        ["REFLECT"] = 3,
        ["RESONANCE"] = 3,
        ["THE_SEALED_THRONE"] = 3,
        ["DEVASTATE"] = 4,
        ["THE_SMITH"] = 4,
        ["COMET"] = 5,
        ["NEUTRON_AEGIS"] = 5,
        ["ROYAL_GAMBLE"] = 5,
        ["DECISIONS_DECISIONS"] = 6,
        ["SEVEN_STARS"] = 7,
    };

    // 2026-06-04 — a PlanStep is normally a card play (Card != null). When PlanNextStep is
    // called with considerPotions:true (the overlay), it may instead recommend USING a potion —
    // then Potion != null and Card is null. PotionIdx indexes state.PlayerPotions; TargetIdx is
    // the potion's enemy target for damage potions.
    public readonly record struct PlanStep(SimCard? Card, int TargetIdx, int Score, string Reason,
        Sim.SimPotion? Potion = null, int PotionIdx = -1)
    {
        public bool IsPotion => Potion != null;
    }

    /// <summary>
    /// v0.7.10 — discount applied to the next-turn opening score when projecting
    /// multi-turn value. 0.30 = "30% of next turn's best first-card play counts
    /// toward this turn's first-card score". Conservative because the projection
    /// uses AdvanceTurn from after just the first card (no full-turn sim), and
    /// next-turn draw is a synthetic average (RNG noise).
    /// </summary>
    // v0.10 — Converted from const to static field for JSON-load via
    // PlannerConfig. PlannerConfig.LoadFromJson updates NextTurnDiscount,
    // MonteCarloSamples, BeamK, MaxDepth without recompile.
    // 2026-06-03 — TESTED 0.45 (raise multi-turn output valuation, per loss-analysis showing
    // 64% of losses = output insufficiency). Result: overall -0.9pp (Monster -1.4 from over-
    // setup, Elite +0.8). eHp-at-death only -1pp. Reverted: valuing output MORE doesn't convert
    // to wins → the planner already extracts ~max output from its deck; the win-rate lever is
    // DECK QUALITY, not combat scoring. See docs/planscorer_loss_analysis.md.
    // 2026-06-03 — 0.30 confirmed near-optimal even on the meaningful-cell bed (cells where
    // PlanScorer decides, ~15% of all): 0.15 flat (+0.0pp), 0.45 worse (-4.1pp). Horizon lever
    // exhausted. See docs/planscorer_loss_analysis.md.
    public static double NextTurnDiscount = 0.30;

    /// <summary>
    /// v0.7.14 — Monte Carlo sample count for next-turn hand projection.
    /// N=3 is a perf/variance trade-off: enough samples to spread the deck
    /// distribution without exploding PlanScorer call count. With ~50 calls
    /// per depth=1 lookahead, 3 samples cost +150 calls per first-card
    /// candidate (~3x legacy when including the depth=2 main beam).
    /// </summary>
    public static int MonteCarloSamples = EnvInt("STS2_PLANNER_MC", 3);

    /// <summary>v0.10 — Beam width for depth-N continuation search. JSON-tunable.</summary>
    public static int BeamK = EnvInt("STS2_PLANNER_BEAM", 5);

    /// <summary>Env override helper for the perf knobs (MC samples / beam width). Lets a measurement
    /// batch trade lookahead fidelity for speed; per-run noise averages out across the batch mean.</summary>
    private static int EnvInt(string name, int def)
    {
        var s = System.Environment.GetEnvironmentVariable(name);
        return (int.TryParse(s, out var v) && v > 0) ? v : def;
    }

    /// <summary>
    /// Continuation depth passed to BestContinuation after the first card. 2 =
    /// the historical "depth-2" baseline (first card + 2 continuation cards).
    /// Overridable via env var STS2_PLANNER_DEPTH for sweep experiments
    /// (mirrors STS2_MCTS_SIMS / STS2_MCTS_ROLLOUT pattern in ScenarioVerifier).
    /// </summary>
    public static int ContinuationDepth = ResolveContinuationDepth();
    private static int ResolveContinuationDepth()
    {
        var s = System.Environment.GetEnvironmentVariable("STS2_PLANNER_DEPTH");
        if (int.TryParse(s, out var v) && v > 0) return v;
        return 2;
    }

    /// <summary>
    /// 2026-06-04 (Bug A) — when the first card is a 0-cost, 0-damage setup (RAGE 격노, most
    /// Powers), advance the NEXT-TURN projection base past its best follow-up so a 0-damage opener
    /// isn't penalised by a projection that assumes the turn ended with the enemies untouched.
    /// Default on; STS2_SETUP_PROJECT=0 disables for headless A/B.
    /// </summary>
    public static bool SetupProjectFix = ResolveSetupProjectFix();
    private static bool ResolveSetupProjectFix()
        => System.Environment.GetEnvironmentVariable("STS2_SETUP_PROJECT") != "0";

    /// <summary>
    /// 2026-06-04 (user request) — penalise the player HP a candidate concedes in the next-turn
    /// projection. The projection rewards advancing the kill (enemy HP drops) but was BLIND to the
    /// HP the player gives up by not covering incoming damage — so a chip attack out-raced a defend
    /// even when both ultimately played the same cards (STRIKE/DEFEND order with Frail). AdvanceTurn
    /// already subtracts unblocked incoming from PlayerHp, so the conceded HP is real and measured.
    /// Per-HP weight; 0 disables. When blocking is impossible every candidate eats the same penalty
    /// (no behaviour change); it only bites when a coverable hit is left open. STS2_PROJECT_HPLOSS.
    /// NOTE: also nudges toward blocking in a Losing race (where racing may be correct) — headless
    /// A/B (STS2_PROJECT_HPLOSS=0 vs on) before treating any win-rate delta as settled.
    /// </summary>
    public static int ProjectHpLossPerPoint = ResolveProjectHpLoss();
    private static int ResolveProjectHpLoss()
    {
        var s = System.Environment.GetEnvironmentVariable("STS2_PROJECT_HPLOSS");
        if (int.TryParse(s, out var v) && v >= 0) return v;
        return 20;
    }

    /// <summary>
    /// Per-candidate trace from the most recent PlanNextStep call. <c>bestNextId</c>
    /// reveals which follow-up card the depth-2 lookahead picked as the "best second
    /// play" after this candidate — invaluable for explaining why a setup card won
    /// over a stronger-looking standalone play.
    /// </summary>
    public static System.Collections.Generic.List<(string id, int targetIdx, int firstScore, int secondScore, int total, string? bestNextId)>
        LastCandidates { get; } = new();

    /// <summary>v0.7.75 — When candidates empty but hand non-empty, diagnostic
    /// string describing why each card was filtered. Read by VakuuExecutor.</summary>
    public static string? LastEmptyReason { get; private set; }

    /// <summary>2026-06-04 — potion evaluation trace from the last considerPotions:true call.
    /// Per potion: its kind, chosen target, immediate value (firstScore), and lookahead total.
    /// Compared against <see cref="LastBestCardTotal"/> + <see cref="PotionWinMargin"/> to decide
    /// whether a potion is recommended over the best card play. Drives the F9 overlay dump so the
    /// reason a defensive/utility potion was or wasn't suggested is inspectable in-game.</summary>
    public static System.Collections.Generic.List<(string id, string kind, int targetIdx, int firstScore, int total)>
        LastPotionCandidates { get; } = new();

    /// <summary>Best card-play lookahead total from the last PlanNextStep — the bar a potion must
    /// clear by <see cref="PotionWinMargin"/> to be recommended. int.MinValue = no card eval yet.</summary>
    public static int LastBestCardTotal { get; private set; } = int.MinValue;

    /// <summary>Margin (score units) a potion's lookahead total must beat the best card by before
    /// it is recommended — "potions are precious, don't burn one for a trivial additive gain".</summary>
    public const int PotionWinMargin = 250;

    // ─── v0.10 JSON-loadable planner config ────────────────────────────────
    private sealed class _PlannerCfg
    {
        public double next_turn_discount { get; set; } = 0.30;
        public int monte_carlo_samples { get; set; } = 3;
        public int beam_k { get; set; } = 5;
        /// <summary>v0.10 (Phase 5) — when true, TrainingDataExporter records
        /// dense per-step candidate breakdowns to training_data/{ts}.ndjson.
        /// Significant perf cost; default false.</summary>
        public bool training_data_enabled { get; set; } = false;
    }
    private static readonly System.Text.Json.JsonSerializerOptions _cfgOpts
        = new() { WriteIndented = true };

    /// <summary>Write current planner constants to {path} if missing.</summary>
    /// <summary>Mirror of TrainingDataExporter.Enabled, set by LoadFromJson.
    /// Read by MainFile to apply to TrainingDataExporter. Decoupled from
    /// Diagnostics namespace so the test harness (which excludes Godot-coupled
    /// files) still compiles.</summary>
    public static bool TrainingDataEnabledMirror = false;

    public static void WriteDefaultsTo(string path)
    {
        if (System.IO.File.Exists(path)) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            var c = new _PlannerCfg
            {
                next_turn_discount = NextTurnDiscount,
                monte_carlo_samples = MonteCarloSamples,
                beam_k = BeamK,
                training_data_enabled = TrainingDataEnabledMirror,
            };
            System.IO.File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(c, _cfgOpts));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Load constants from {path}. No-op if missing.</summary>
    public static void LoadFromJson(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            var c = System.Text.Json.JsonSerializer.Deserialize<_PlannerCfg>(
                System.IO.File.ReadAllText(path), _cfgOpts);
            if (c == null) return;
            NextTurnDiscount = c.next_turn_discount;
            MonteCarloSamples = c.monte_carlo_samples;
            BeamK = c.beam_k;
            TrainingDataEnabledMirror = c.training_data_enabled;
        }
        catch { /* malformed → keep defaults */ }
    }

    /// <summary>
    /// v0.23 — Process-stable string hash for use as Monte Carlo / RNG seed.
    /// .NET 5+ randomizes <c>string.GetHashCode()</c> per process by default
    /// (security hardening against hash-flooding) — call sites that need
    /// reproducibility across runs have to opt out.
    ///
    /// 32-bit FNV-1a roll. Cheap, deterministic, and collision rate is fine
    /// for our card-id namespace (< 1200 distinct keys). Does NOT match any
    /// other hash the game uses; never use this for content lookup.
    /// </summary>
    private static int StableStringHash(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        unchecked
        {
            const uint offset = 2166136261u;
            const uint prime  = 16777619u;
            uint h = offset;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= prime;
            }
            return (int)h;
        }
    }

    private static string WhyFiltered(SimCard c, SimState state)
    {
        if (!c.IsPlayable)
        {
            if (StarCostByCardId.TryGetValue(c.Id ?? "", out int sc))
                return state.PlayerStars < sc ? $"!IsPlayable+stars{state.PlayerStars}<{sc}" : "ok-star-unlock";
            return "!IsPlayable";
        }
        if (c.Cost < 0) return "Cost<0";
        // v0.7.94 — Honor both PlayerCorruption (depth-N propagated) and PlayerPowers
        // (snapshot dict).
        bool corruptionFreeSkill = c.IsSkill
            && ((state.PlayerCorruption > 0)
                || (state.PlayerPowers != null
                    && state.PlayerPowers.TryGetValue("CorruptionPower", out var cs) && cs > 0));
        // 2026-06-02 — BULLET_TIME free-hand: all non-X-cost cards 0-cost this turn.
        bool freeHandCovers = state.PlayerFreeHandThisTurn
            && !(c.Axes != null && c.Axes.Contains("X_COST"));
        // 2026-06-04 — VoidForm (first N cards/turn free) + Veilpiercer (next Ethereal free).
        bool voidFormFree = state.PlayerFreeCardBudget > 0
            && !(c.Axes != null && c.Axes.Contains("X_COST"));
        bool veilpiercerFree = c.IsEthereal && state.PlayerVeilpiercer > 0;
        // 2026-06-04 — BrilliantScarf: the 5th card each turn is free (affordable regardless of cost).
        bool brilliantScarfFree = state.PlayerBrilliantScarfReady
            && !(c.Axes != null && c.Axes.Contains("X_COST"));
        bool freeCovers = (c.IsAttack && state.PlayerFreeAttacks > 0)
                       || (c.IsSkill && state.PlayerFreeSkills > 0)
                       || (c.IsPower && state.PlayerFreePowers > 0)
                       || corruptionFreeSkill
                       || freeHandCovers
                       || voidFormFree
                       || veilpiercerFree
                       || brilliantScarfFree;
        if (!freeCovers && c.Cost > state.PlayerEnergy)
            return $"cost{c.Cost}>energy{state.PlayerEnergy}";
        if (c.IsCurseOrStatus) return "curse/status";
        bool isOrbEvokeOnly =
            (c.Axes.Contains("ORB_AMPLIFIER") || c.Axes.Contains("ORB_EVOKE")
             || c.Axes.Contains("LIGHTNING_EVOKE"))
            && !c.IsAttack && c.Damage == 0;
        if (isOrbEvokeOnly && state.PlayerOrbCount == 0) return "orbEvokeNoOrb";
        if (c.Axes.Contains("STAR_X_COST") && state.PlayerStars == 0) return "starX+0stars";
        if (c.IsEnergyGainCard && c.EnergyGain > 0
            && !c.IsAttack && c.Damage == 0 && c.DrawCount == 0)
        {
            bool anyOther = false;
            foreach (var oc in state.Hand)
            {
                if (ReferenceEquals(oc, c)) continue;
                if (oc.IsCurseOrStatus) continue;
                if (oc.Cost < 0 && !oc.IsPlayable) continue;   // playable X-cost = ideal energy sink
                // Playable now, OR affordable after this card's energy gain (see EnumerateCandidates).
                if (oc.IsPlayable || oc.Cost <= state.PlayerEnergy + c.EnergyGain) { anyOther = true; break; }
            }
            if (!anyOther) return "energyGain-noOtherCard";
        }
        return "OK?";
    }

    public static PlanStep? PlanNextStep(SimState state, bool considerPotions = false)
    {
        var candidates = EnumerateCandidates(state).ToList();
        if (candidates.Count == 0)
        {
            // v0.7.75 — Diagnostic: if state.Hand has cards but candidates is
            // empty, dump per-card filter reasons so we can debug "no playable
            // card, stopping" reports.
            if (state.Hand.Count > 0)
            {
                var sb = new System.Text.StringBuilder("[CombatAI] candidates EMPTY despite hand: ");
                foreach (var c in state.Hand)
                {
                    string reason = WhyFiltered(c, state);
                    sb.Append($"{c.Id}({reason}) ");
                }
                LastEmptyReason = sb.ToString();
            }
            return null;
        }
        LastEmptyReason = null;

        // v0.2.5 — depth-2 lookahead: for each first-card candidate, simulate playing it
        // and score the best possible second card in the resulting state. Combined score
        // surfaces combos (Inflame → Strike, Vulnerable → big attack) the greedy step missed.
        PlanStep? bestPlan = null;
        int bestTotal = int.MinValue;
        int bestFirstScore = int.MinValue;
        string? bestPlanNextId = null;   // the current best plan's depth-2 continuation card id
        LastCandidates.Clear();

        var planWeights = PlanScorerWeights.For(PlaystyleResolver.Resolve(state));
        foreach (var (card, targetIdx) in candidates)
        {
            // v0.5 — play-order biases (Retain defer / Ethereal play-now) live here, not
            // in PlanScorer.Score, so that discard/exhaust selectors see unbiased values.
            int firstScore = PlanScorer.Score(card, targetIdx, state)
                           + PlanScorer.PlayOrderBias(card, state, planWeights);

            // v0.7.9 — depth=3 beam search (K=3, single-turn). Simulate playing
            // this card, then evaluate the best 2-card continuation via top-K
            // beam pruning. depth=3 lets the planner see 2-step combos post-
            // this-card (Inflame → Strike → Bash, Bodyslam-after-block etc.).
            //
            // v0.7.10 — Phase 2a multi-turn signal: after the 1-card simulation,
            // run AdvanceTurn to project the next-turn state (enemy intents
            // resolve, hand redraws, energy refills) and add the best single
            // first-card play from there as a discounted bonus. Lets the
            // planner value Power cards / setups whose payoff is next turn,
            // not just this one.
            int secondScore = 0;
            int nextTurnBonus = 0;
            int hpLossPenalty = 0;   // 2026-06-04 — HP the line concedes in the next-turn projection
            string? bestNextId = null;
            try
            {
                var nextState = Sim.AnalyticalSimulator.ApplyCardPlay(state, card, targetIdx);
                // v0.9 — beamK 3 → 5. K=3 was pruning setup cards (FALLING_STAR
                // with d=8 base, VENERATE with 0 dmg, GLOW with 0 dmg) from the
                // depth-2 beam because their immediate single-step score lost
                // to DEFEND's threatBonus and SB's raw damage. The pruned
                // setup cards are exactly the ones that unlock big-payoff
                // chains (VEN→FS gives 2 stars + Vuln, then SB hits for
                // d31×1.5 = 47 crossing Shriek's 70-HP stun threshold). K=5
                // costs ~67% more sim time per first-card but captures combos
                // the user manually identifies and the previous AI missed
                // (see 2026-05-19 20:05 turn 5/6 logs — VEN→FS→SB stun never
                // surfaced because FS lost the beam cut at 1670 vs DEFEND 2387).
                secondScore = BestContinuation(nextState, depth: ContinuationDepth, planWeights, beamK: BeamK, out bestNextId);
                if (secondScore < 0) secondScore = 0; // never pessimize via bad fallback

                // 2026-06-04 (Bug A) — setup-first next-turn deflation fix. A 0-cost, 0-damage
                // setup card (RAGE 격노, most Powers) played first deals no damage, so projecting
                // the next turn from the state right after it pretends the turn ended with the
                // enemies untouched. That deflates the setup-first next-turn term and lets a big
                // attack wrongly claim the first slot — even when the within-turn score correctly
                // prefers the setup first (so its block/buff rides the later attacks). When the
                // first card is such a setup, advance the projection base past its best follow-up
                // so the projected board reflects the damage we'd actually deal this turn. Only the
                // next-turn projection base changes; the first/second within-turn scores stay put.
                var projBase = nextState;
                if (SetupProjectFix && card.Cost == 0 && card.Damage <= 0)
                {
                    int fbBest = int.MinValue; SimCard? fbCard = null; int fbTarget = -1;
                    foreach (var (fc, ft) in EnumerateCandidates(nextState))
                    {
                        int fs = PlanScorer.Score(fc, ft, nextState)
                               + PlanScorer.PlayOrderBias(fc, nextState, planWeights);
                        if (fs > fbBest) { fbBest = fs; fbCard = fc; fbTarget = ft; }
                    }
                    if (fbCard != null)
                        try { projBase = Sim.AnalyticalSimulator.ApplyCardPlay(nextState, fbCard, fbTarget); }
                        catch { projBase = nextState; }
                }

                // Next-turn projection — discounted because the projection assumes
                // the rest of this turn is forfeit (we advance from projBase: after
                // the first card, or — for a 0-cost setup — after its best follow-up
                // too, so a 0-damage opener isn't penalised; see Bug A note above).
                //
                // v0.7.14 — Monte Carlo over N=3 sampled next-turn hands.
                // Replaces the single synthetic-avg AdvanceTurn call with an
                // average across 3 actually-drawn hands so variance in the
                // deck pool ("might draw Strikes / might draw curses") shows
                // up in the next-turn signal instead of being smoothed away.
                try
                {
                    // Seed deterministically per state so the same scoring
                    // run reproduces. The seed mixes hand size + player HP +
                    // a candidate-specific salt to avoid all first-card
                    // candidates sharing identical samples.
                    //
                    // v0.23 (Phase 6) — Stable string hash. .NET 5+ randomizes
                    // string.GetHashCode() per process, so the same card.Id
                    // produced a different seed every run, drifting the Monte
                    // Carlo samples and producing ±1 win-count noise in the
                    // PlannerDepthN benchmark across reruns. StableStringHash
                    // is an FNV-1a 32-bit roll, deterministic across processes.
                    int seed = nextState.Hand.Count * 31 + nextState.PlayerHp + StableStringHash(card.Id);
                    var rng = new System.Random(seed);
                    int sampleTotal = 0;
                    int sampleCount = 0;
                    int hpLossTotal = 0;
                    for (int s = 0; s < MonteCarloSamples; s++)
                    {
                        try
                        {
                            var nextTurnState = Sim.AnalyticalSimulator.AdvanceTurnSampled(projBase, rng);
                            int singleStep = BestContinuation(nextTurnState, depth: 1, planWeights, beamK: BeamK, out _);
                            if (singleStep < 0) singleStep = 0;
                            sampleTotal += singleStep;
                            // 2026-06-04 (user) — HP the player loses during this projected enemy turn.
                            // AdvanceTurn already subtracts the unblocked incoming from PlayerHp, so a
                            // line that left a coverable hit open surfaces here as real HP given away.
                            hpLossTotal += System.Math.Max(0, projBase.PlayerHp - nextTurnState.PlayerHp);
                            sampleCount++;
                        }
                        catch { /* skip sample on sim failure */ }
                    }
                    int nextTurnAvg = sampleCount > 0 ? sampleTotal / sampleCount : 0;
                    if (ProjectHpLossPerPoint > 0 && sampleCount > 0)
                        hpLossPenalty = (hpLossTotal / sampleCount) * ProjectHpLossPerPoint;

                    // v0.7.15 — EchoFormPower next-turn first-card 2x play.
                    // We only evaluate depth=1 next turn (single first card),
                    // so any positive EchoForm stack saturates: the projected
                    // first card plays twice → score doubles. Higher stacks
                    // (2nd/3rd cards echoed too) would matter at depth>=2
                    // but we don't drill that deep in the next-turn projection.
                    double echoMultiplier = 1.0;
                    if (nextState.PlayerPowers != null
                        && nextState.PlayerPowers.TryGetValue("EchoFormPower", out var ef)
                        && ef > 0)
                    {
                        echoMultiplier = 2.0;
                    }
                    nextTurnBonus = (int)(nextTurnAvg * echoMultiplier * NextTurnDiscount);
                    if (nextTurnBonus < 0) nextTurnBonus = 0;
                }
                catch { /* AdvanceTurn failure → no next-turn signal */ }
            }
            catch
            {
                // Simulator error: fall back to single-step score
                secondScore = 0;
                bestNextId = null;
            }

            int total = firstScore + secondScore + nextTurnBonus - hpLossPenalty;
            LastCandidates.Add((card.Id, targetIdx, firstScore, secondScore, total, bestNextId));
            // v0.5 — tie-break on first-card score so identical totals (e.g., A first=1000
            // second=200 vs B first=600 second=600) prefer the candidate with higher
            // immediate value. Resolves the case where iteration order alone determined
            // the winner of equal-total candidates.
            //
            // v0.7.77 — Hard rule: a candidate with negative firstScore must NEVER
            // beat a candidate with non-negative firstScore, regardless of total.
            // The first card is the only one we actually play this step; a high
            // lookahead total can't justify spending energy on a NOW-hurts move.
            // Observed bug: FOREGONE_CONCLUSION(f=-705 tot=6000) beat
            // DEFEND_REGENT(f=2317 tot=3947) under Critical threat, then floor
            // tripped (bestFirstScore=-705) and the AI stopped with playable
            // defense in hand.
            bool wins;
            if (firstScore >= 0 && bestFirstScore < 0) wins = true;
            else if (firstScore < 0 && bestFirstScore >= 0) wins = false;
            else
            {
                wins = total > bestTotal
                    || (total == bestTotal && firstScore > bestFirstScore);

                // v0.9 — Dominant single-card protection (symmetric). The
                // 2-step lookahead total can crown a "weak now + strong
                // chain" candidate over a "strong now alone" candidate by a
                // razor-thin margin, with the chain total inflated by an
                // extra card play that won't always materialise in the real
                // 13-step loop. When the single-play candidate's firstScore
                // is genuinely dominant (≥ 3000 raw, ≥ 2× the chain's first
                // card), insist on a meaningful total gap (> 10%) before the
                // chain can replace it — and conversely, let the dominant
                // single override a chain whose total is only marginally
                // higher.
                //
                // Observed motivating case (logs 2026-05-19 20:05, turn 6):
                //   SB(5646 alone, total 6369) lost to DEFEND→DEFEND
                //   (2387, total 6598) by 3.6% total margin. Losing-race
                //   combat never converted because the d31 finisher kept
                //   getting deferred.
                bool bestIsDominantSingle =
                    bestFirstScore >= 3000
                    && bestFirstScore >= firstScore * 2;
                bool candIsDominantSingle =
                    firstScore >= 3000
                    && bestFirstScore > 0
                    && firstScore >= bestFirstScore * 2;

                // 2026-06-04 — a 0-cost setup whose best continuation IS the dominant single defers
                // nothing: both cards play THIS turn, the free one first. So it must be exempt from
                // the dominant-single guard, which exists only to stop a real tempo-losing deferral.
                // Motivating case (F9 log): RAGE 격노 (0c, +block per attack) → HOWL_FROM_BEYOND.
                // RAGE total 12887 beat HOWL 12582 by 2.4% (within 10%), so the guard wrongly kept
                // HOWL first, forfeiting RAGE's block on the very attack it boosts.
                // A 0-cost first play defers NOTHING — you play it free, then still play the dominant
                // single (and the rest) this turn. So a free, non-harmful candidate that wins on total
                // must never be suppressed by the dominant-single guard, whatever its specific
                // follow-up is. (Earlier this matched only when bestNext WAS the dominant single, but
                // once potions entered the lookahead RAGE's bestNext became a potion, not HOWL, and
                // the guard wrongly kept HOWL first again — F9: RAGE total 13707 > HOWL 12602.)
                bool candIsFreeSetupIntoBest = card.Cost == 0 && firstScore >= 0;
                bool bestIsFreeSetupIntoCand = bestPlan != null && bestPlan.Value.Card.Cost == 0
                    && bestFirstScore >= 0;

                if (wins && bestIsDominantSingle && !candIsFreeSetupIntoBest
                    && total <= bestTotal + bestTotal / 10)
                {
                    // Incoming chain win is narrow — keep the dominant single.
                    wins = false;
                }
                else if (!wins && candIsDominantSingle && !bestIsFreeSetupIntoCand
                    && bestTotal <= total + total / 10)
                {
                    // Incoming dominant single overrides slightly-higher chain.
                    wins = true;
                }

                // v0.9 — Per-energy efficiency tiebreaker. When two candidates'
                // totals are within 5% AND both are the same card kind
                // (Attack/Skill), prefer the higher effective dmg/E (or
                // block/E for skills). Rationale: a card that spends less
                // energy per unit output leaves room for one more play this
                // turn or next.
                //
                // Uses state-aware EffectiveDmgPerEnergy / EffectiveBlockPerEnergy
                // which include Vuln/Weak/Burst/Echo/Unmovable/Vigor/X-cost on
                // top of the PreviewValue (which already covers Strength/Dex/
                // Enchantment numeric upgrades). So when a Power buff is
                // active (e.g. enemy Vuln from a previously-played FALLING_STAR),
                // a follow-up attack's efficiency correctly reflects the ×1.5
                // multiplier and SB d=21 effective→47 (eff 23.5/E) decisively
                // beats STRIKE d=6 effective→9 (eff 9.0/E).
                //
                // Observed motivating case (logs 2026-05-19 21:11 turn line
                // 1433): STRIKE_REGENT (6 dmg/E raw) and SB (10.5 dmg/E raw)
                // had close totals due to BURST inflation; efficiency would
                // have flipped it. Even with BURST suppression, this remains
                // a useful generic tiebreaker.
                if (bestPlan != null
                    && card.Kind == bestPlan.Value.Card.Kind
                    && (card.IsAttack || (card.IsSkill && card.Block > 0)))
                {
                    bool within5 = System.Math.Abs(total - bestTotal) <= System.Math.Max(total, bestTotal) / 20;
                    if (within5)
                    {
                        // v0.9 — target-aware EffectiveDmgPerEnergy. Single-
                        // target attacks compare with exact target multipliers
                        // (Intangible cap / HardenedShell / Cruelty per Vuln).
                        // AOE / skill paths use the -1 sentinel (any-enemy).
                        double candEff = card.IsAttack
                            ? card.EffectiveDmgPerEnergy(state, targetIdx)
                            : card.EffectiveBlockPerEnergy(state);
                        double bestEff = bestPlan.Value.Card.IsAttack
                            ? bestPlan.Value.Card.EffectiveDmgPerEnergy(state, bestPlan.Value.TargetIdx)
                            : bestPlan.Value.Card.EffectiveBlockPerEnergy(state);
                        // Need a meaningful efficiency edge — at least 20% better.
                        if (candEff >= bestEff * 1.2) wins = true;
                        else if (bestEff >= candEff * 1.2) wins = false;
                    }
                }
            }
            if (wins)
            {
                bestTotal = total;
                bestFirstScore = firstScore;
                bestPlanNextId = bestNextId;
                // PlanStep.Score is the first-card score, not lookahead total (kept for log clarity).
                bestPlan = new PlanStep(card, targetIdx, firstScore, Reason(card));
            }
        }

        // 2026-06-04 — opt-in: also consider USING a potion as the next action (overlay only;
        // Vakuu auto-play keeps considerPotions=false so it never auto-drinks). A potion competes
        // by the same lookahead total as cards: its immediate value + the best continuation from
        // the post-potion state (which is where an amplifier like GIGANTIFICATION cashes out via
        // the ×3 attack). Recommend it only when it beats the best card play by a real margin —
        // potions are precious, so trivial additive gains shouldn't burn one.
        LastPotionCandidates.Clear();
        LastBestCardTotal = bestTotal;
        if (considerPotions && state.PlayerPotions.Count > 0)
        {
            int bestPotionTotal = int.MinValue, bestPotionFirst = 0, bestPotionIdx = -1, bestPotionTarget = -1;
            Sim.SimPotion? bestPotion = null;
            for (int pi = 0; pi < state.PlayerPotions.Count; pi++)
            {
                var potion = state.PlayerPotions[pi];
                if (potion.Kind == Sim.PotionKind.Other) continue;   // unmodeled → never recommend
                int ptarget = PlanScorer.PickPotionTarget(potion, state);
                int pfirst = PlanScorer.PotionValue(potion, ptarget, state);
                int ptotal = pfirst;
                try
                {
                    var ns = Sim.AnalyticalSimulator.ApplyPotionUse(state, pi, ptarget);
                    ptotal += BestContinuation(ns, ContinuationDepth, planWeights, BeamK, out _);
                }
                catch { /* sim failure: immediate value only */ }
                LastPotionCandidates.Add((potion.Id, potion.Kind.ToString(), ptarget, pfirst, ptotal));
                if (ptotal > bestPotionTotal)
                {
                    bestPotionTotal = ptotal; bestPotionFirst = pfirst;
                    bestPotionIdx = pi; bestPotionTarget = ptarget; bestPotion = potion;
                }
            }
            if (bestPotion != null && bestPotionTotal > bestTotal + PotionWinMargin)
                return new PlanStep(null, bestPotionTarget, bestPotionFirst,
                    $"potion:{bestPotion.Id}", bestPotion, bestPotionIdx);
        }

        // "Stop playing" floor: judge on the *first-card score* (the actual card we'd play),
        // not the lookahead total — a high-score follow-up shouldn't keep us spending energy
        // on a worthless first move.
        if (bestPlan != null && bestFirstScore < planWeights.MinPlayScore)
        {
            // v0.5 — 0-cost exemption. The floor exists to stop us spending ENERGY on a
            // weak play. A 0-cost card spends no energy, so any positive-score play is
            // strictly net positive — leaving it in hand wastes it at end-of-turn.
            //   • If best plan is itself a 0-cost positive play, take it.
            //   • If best plan is a paid card below floor, search the candidates for the
            //     best 0-cost positive alternative and play that instead.
            if (bestPlan.Value.Card.Cost == 0 && bestFirstScore > 0)
                return bestPlan;
            SimCard? freeCard = null;
            int freeIdx = -1;
            int freeScore = 0;
            foreach (var (c, t) in candidates)
            {
                if (c.Cost != 0) continue;
                int s = PlanScorer.Score(c, t, state)
                      + PlanScorer.PlayOrderBias(c, state, planWeights);
                if (s > freeScore) { freeScore = s; freeCard = c; freeIdx = t; }
            }
            if (freeCard != null)
                return new PlanStep(freeCard, freeIdx, freeScore, Reason(freeCard));

            // v0.7.40 — Last-resort: if the best paid card has SOME positive
            // value (above 0 but below the 80 floor), prefer playing it over
            // forfeiting the turn. The floor's purpose is to suppress clearly-
            // bad plays; mildly-positive plays should still execute so the AI
            // doesn't surface "stop with cards usable" to the user.
            if (bestFirstScore > 0)
                return bestPlan;

            // v0.7.76 — Candidates were non-empty but every first-card score
            // bottomed out at ≤ 0. Dump per-candidate (firstScore, secondScore,
            // total, bestNextId) so we can diagnose which scoring layer is
            // dragging the score down (most likely culprit: threatBonus not
            // firing for AnyPlayer-targeted defends, or aggregate penalties
            // overrunning legitimate value).
            {
                var sb = new System.Text.StringBuilder("[CombatAI] plan NULL (bestFirstScore<=0): ");
                foreach (var t in LastCandidates)
                    sb.Append($"{t.id}(f={t.firstScore} s={t.secondScore} tot={t.total}) ");
                LastEmptyReason = sb.ToString();
            }
            return null;  // genuinely no positive play
        }

        return bestPlan;
    }

    public static IEnumerable<PlanStep> ScoreAll(SimState state)
    {
        foreach (var (card, targetIdx) in EnumerateCandidates(state))
        {
            yield return new PlanStep(card, targetIdx, PlanScorer.Score(card, targetIdx, state), Reason(card));
        }
    }

    /// <summary>
    /// v0.7.9 — depth-N beam search returning the best continuation score
    /// from <paramref name="state"/>. At each layer scores all candidates, keeps
    /// the top <paramref name="beamK"/> by single-step score, simulates each via
    /// AnalyticalSimulator.ApplyCardPlay, and recurses depth-1. Pure single-turn
    /// search — no AdvanceTurn / EOT processing (Phase 1 scope).
    ///
    /// <paramref name="depth"/> = remaining cards to consider after this one.
    /// depth=0 returns 0; depth=1 = single-step max; depth=2 = best 2-card
    /// continuation; etc.
    ///
    /// <paramref name="firstCardId"/> exits the id of the top-scoring candidate
    /// at this layer (for DecisionLog trace).
    /// </summary>
    private static int BestContinuation(SimState state, int depth, PlanScorerWeights w, int beamK, out string? firstCardId)
    {
        firstCardId = null;
        if (depth <= 0) return 0;

        var candidates = EnumerateCandidates(state).ToList();
        if (candidates.Count == 0) return 0;

        // Score all candidates at this layer once.
        var scored = new List<(int Score, SimCard Card, int TargetIdx)>(candidates.Count);
        foreach (var (card, targetIdx) in candidates)
        {
            int s = PlanScorer.Score(card, targetIdx, state)
                  + PlanScorer.PlayOrderBias(card, state, w);
            scored.Add((s, card, targetIdx));
        }
        // Top-K beam: keep only the best `beamK` by single-step score.
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // v0.9 — Dedupe duplicate card IDs before beam cut. Hands with 3×
        // DEFEND_REGENT consumed all 5 beam slots with copies of the same
        // play, pushing setup cards (FALLING_STAR, VENERATE, GLOW) past the
        // cutoff and erasing the VEN→FS→SB stun combo from depth-2 search
        // (logs 2026-05-19 20:21 turn 5 step 1: 3 DEFENDs at score 2387
        // crowded out FS at 1670). Subsequent step explored chains found FS
        // once the hand thinned. Deduping by Id keeps the beam slot count
        // EFFECTIVE — once you've considered "play DEFEND now", you don't
        // also need "play this OTHER DEFEND now"; the recursion will pick a
        // second DEFEND from the post-play state if needed.
        var unique = new List<(int Score, SimCard Card, int TargetIdx)>(scored.Count);
        var seenIds = new HashSet<string>();
        foreach (var s in scored)
        {
            string id = s.Card.Id ?? "";
            if (id.Length == 0 || seenIds.Add(id))
                unique.Add(s);
        }
        scored = unique;
        int keep = System.Math.Min(beamK, scored.Count);

        int best = int.MinValue;
        for (int i = 0; i < keep; i++)
        {
            var (s, card, targetIdx) = scored[i];
            int total = s;
            if (depth > 1)
            {
                try
                {
                    var nextState = Sim.AnalyticalSimulator.ApplyCardPlay(state, card, targetIdx);
                    total += BestContinuation(nextState, depth - 1, w, beamK, out _);
                }
                catch { /* sim failure: use single-step score only */ }
            }
            if (total > best || firstCardId == null)
            {
                best = total;
                firstCardId = card.Id;
            }
        }

        // 2026-06-04 — potions as continuation actions. Try each held potion: its immediate
        // value plus the best card continuation from the post-potion state. This lets the
        // lookahead sequence "play setup card → use amplifier/finisher potion" or "drink
        // amplifier → big attack" (the amplified attack scores higher via PlayerNextAttackMult).
        // Only entered inside the lookahead (depth>0), so the top-level first-action choice in
        // PlanNextStep is unchanged — the overlay's separate potion advisor still owns that.
        for (int pi = 0; pi < state.PlayerPotions.Count; pi++)
        {
            var potion = state.PlayerPotions[pi];
            int ptarget = PlanScorer.PickPotionTarget(potion, state);
            int total = PlanScorer.PotionValue(potion, ptarget, state);
            if (depth > 1)
            {
                try
                {
                    var nextState = Sim.AnalyticalSimulator.ApplyPotionUse(state, pi, ptarget);
                    total += BestContinuation(nextState, depth - 1, w, beamK, out _);
                }
                catch { /* sim failure: use immediate potion value only */ }
            }
            if (total > best)
            {
                best = total;
                firstCardId = potion.Id;
            }
        }
        return best < 0 ? 0 : best;
    }

    // v0.10 — Promoted to internal so TrainingDataExporter can iterate the
    // same (card, target) candidate list the planner sees, without duplicating
    // the IsPlayable / cost-gate / star-cost / Bound / Smogged filtering logic.
    internal static IEnumerable<(SimCard card, int targetIdx)> EnumerateCandidates(SimState state)
    {
        foreach (var card in state.Hand)
        {
            // v0.7.73 — IsPlayable is snapshot-time. For star-cost cards, the
            // simulator's depth-N state may have updated PlayerStars; re-check
            // against current state. For NON-star-cost cards, IsPlayable=false
            // means genuinely unplayable (curse / status / conditional), so skip.
            if (!card.IsPlayable)
            {
                if (StarCostByCardId.TryGetValue(card.Id ?? "", out int starCost))
                {
                    if (state.PlayerStars < starCost) continue;  // still insufficient
                    // else: stars sufficient now, allow lookahead to evaluate
                }
                else
                {
                    continue;  // not a star-cost card → genuinely unplayable
                }
            }
            if (card.Cost < 0) continue;           // Negative cost = X or unplayable signal

            // v0.9 — ChainsOfBindingPower restriction: only ONE Bound card
            // may be played per turn. Once a Bound card has been played
            // (BoundCardPlayedThisTurn=true), filter all other Bound cards
            // from the candidate set. Matches the game's ShouldPlay hook
            // (decompile sts2.decompiled.cs:313044).
            if (card.IsBound && state.BoundCardPlayedThisTurn) continue;

            // SmoggyPower (LivingFog AdvancedGasMove) restriction: only ONE
            // Skill per turn may be played while SmoggyPower is active.
            // (a) IsSmogged covers snapshot-time afflicted cards directly.
            // (b) PlayerSmoggy>0 + SmoggySkillPlayedThisTurn blocks remaining
            //     Skill candidates inside depth-N forward sim — the game's
            //     AfterCardPlayed hook applies Smog to every other Skill
            //     once the first Skill lands (decompile 318740-318755).
            if (card.IsSmogged) continue;
            if (card.IsSkill && state.PlayerSmoggy > 0
                && state.SmoggySkillPlayedThisTurn) continue;

            // v0.5 — Free*Power lets us play expensive cards over the energy budget.
            // v0.7.21 — CorruptionPower makes Skill cards combat-wide 0-cost.
            // v0.7.94 — Also honor SimState.PlayerCorruption (propagated by simulator
            // after a Power card grants Corruption mid-turn). PlayerPowers is
            // snapshot-time only; depth-N nextState may have Corruption granted but
            // PlayerPowers not yet updated.
            bool corruptionFreeSkill = card.IsSkill
                && ((state.PlayerCorruption > 0)
                    || (state.PlayerPowers != null
                        && state.PlayerPowers.TryGetValue("CorruptionPower", out var corStack)
                        && corStack > 0));
            // 2026-06-02 — BULLET_TIME free-hand: all non-X-cost cards 0-cost this turn.
            bool freeHandCovers = state.PlayerFreeHandThisTurn
                && !(card.Axes != null && card.Axes.Contains("X_COST"));
            // 2026-06-04 — VoidForm (first N cards/turn free) + Veilpiercer (next Ethereal free).
            bool voidFormFree = state.PlayerFreeCardBudget > 0
                && !(card.Axes != null && card.Axes.Contains("X_COST"));
            bool veilpiercerFree = card.IsEthereal && state.PlayerVeilpiercer > 0;
            bool freeCovers =
                (card.IsAttack && state.PlayerFreeAttacks > 0) ||
                (card.IsSkill && state.PlayerFreeSkills > 0) ||
                (card.IsPower && state.PlayerFreePowers > 0) ||
                corruptionFreeSkill ||
                freeHandCovers ||
                voidFormFree ||
                veilpiercerFree;
            if (!freeCovers && card.Cost > state.PlayerEnergy) continue;
            // Note: star-cost cards are filtered by CanPlay() already if no stars; we trust it.

            // Energy-gain card is pointless if there's nothing left to spend the gained energy on.
            // Excluding here (vs penalising in PlanScorer) guarantees we never play it as a
            // "least bad" fallback when the hand has no other useful card.
            // v0.5 — only THIS-turn energy gain (Effect.EnergyGain > 0, Adrenaline-style)
            // qualifies. NEXT-turn variants (EnergyNextTurnPower like Berserk) gain energy
            // on subsequent turns and stay valuable even when this hand is empty.
            // Also exempt cards that DRAW (Skim-style) — drawing produces new candidates,
            // so an isolated Skim is still worth playing for the draw.
            if (card.IsEnergyGainCard && card.EnergyGain > 0
                && !card.IsAttack && card.Damage == 0 && card.DrawCount == 0)
            {
                bool anyOtherUseful = false;
                foreach (var c in state.Hand)
                {
                    if (ReferenceEquals(c, card)) continue;
                    if (c.IsCurseOrStatus) continue;
                    // X-cost cards carry Cost < 0 but ARE playable — they spend ALL energy,
                    // so they're the ideal sink for an energy gain (3E + Bloodletting → X=5).
                    // Only skip genuine unplayable signals (Cost < 0 AND not playable).
                    if (c.Cost < 0 && !c.IsPlayable) continue;
                    // Playable now, OR only blocked by affordability that THIS card's
                    // energy gain would resolve. IsPlayable folds in CanPlay()'s energy
                    // check, so at low energy an expensive payoff card (e.g. THE_BOMB at
                    // 0 energy) reads !IsPlayable — exactly when the energy card is the
                    // right play. Count it via affordable-after-gain so the enabler survives.
                    if (c.IsPlayable || c.Cost <= state.PlayerEnergy + card.EnergyGain)
                    {
                        anyOtherUseful = true;
                        break;
                    }
                }
                if (!anyOtherUseful) continue;
            }

            // Orb-evoke / amplifier prerequisite: cards like DualCast / MultiCast / QuadCast
            // need at least one channeled orb to have any meaning. If orb slots are empty,
            // skip them — and any ORB_PRODUCER in hand becomes the natural top pick instead.
            //
            // Restricted to cards with no standalone damage (Thunder/Shatter etc. still
            // deal damage with 0 orbs, so they remain candidates).
            bool isOrbEvokeOnly =
                (card.Axes.Contains("ORB_AMPLIFIER") || card.Axes.Contains("ORB_EVOKE")
                 || card.Axes.Contains("LIGHTNING_EVOKE"))
                && !card.IsAttack && card.Damage == 0;
            if (isOrbEvokeOnly && state.PlayerOrbCount == 0)
                continue;

            // v0.7.42 — STAR_X_COST: card cost is 0 but the effect scales with
            // X = stars consumed at play time (STARDUST = "deal 5 dmg X times").
            // With PlayerStars == 0 the card resolves to X=0 → no effect → wasted
            // slot. CanPlay() lets it through (cost=0/star_cost=0); we filter
            // here so the AI doesn't pick a card that will evaporate.
            if (card.Axes.Contains("STAR_X_COST") && state.PlayerStars == 0)
                continue;

            // v0.7.72 — Removed v0.7.71's StarCost filter. SafeStarCost
            // reflection returned non-zero for normal cards (likely picking
            // up CardModel's wrapped StarCost type), filtering DEFEND/GLOW/
            // FOREGONE_CONCLUSION etc. and causing "no playable card" stops
            // when energy + cards were available. Star-cost cards are
            // already correctly gated by !card.IsPlayable in the first
            // EnumerateCandidates pass (CanPlay returns false at snapshot
            // when stars insufficient). The chain-unlock improvement for
            // depth-N lookahead is reverted; needs a different mechanism
            // (re-snapshot or state-aware CanPlay) — outside this hotfix.

            switch (card.Target)
            {
                case TargetType.AnyEnemy:
                    for (int i = 0; i < state.Enemies.Count; i++)
                    {
                        if (state.Enemies[i].IsAlive) yield return (card, i);
                    }
                    break;
                default:
                    // Self / AllEnemies / RandomEnemy / AnyAlly / AnyPlayer / TargetedNoCreature / Osty / None
                    yield return (card, -1);
                    break;
            }
        }
    }

    private static string Reason(SimCard c)
    {
        if (c.IsPower) return "power-first";
        if (c.IsAttack) return "attack";
        if (c.IsSkill) return c.Target == TargetType.Self ? "skill-self" : "skill";
        return c.Kind.ToString();
    }
}
