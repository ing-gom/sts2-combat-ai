# Runtime decision analysis infrastructure — design plan

Roadmap for extending the existing `DecisionLog` (32-entry in-memory ring
buffer at `Sts2CombatAICode/Core/Diagnostics/DecisionLog.cs`) into a
persistent, parseable telemetry pipeline that closes the gap between
static audit metrics (`docs/ai_card_coverage.md`) and actual AI
behavioural quality.

**Why now**: every change since v0.5.1 (PowerCatalog 100% coverage,
SkillSequencingTier, lethal-mode, Vuln synergy bump, Ethereal handling,
fetch pollution, combo recognition, energy monopoly) is behaviorally
unverified. Static audit can confirm a rule fires *in code*; it cannot
confirm the rule *changes the decision in the right direction*.

## 0. Current state

`DecisionLog` exists but is ephemeral:

- In-memory only, 32 entries max
- No persistence (lost on game close)
- No parser / aggregation
- `Dump()` to logger on demand, no automatic flush

Structured per-entry: `Timestamp`, `Step`, `Playstyle`, `CardId`,
`TargetName`, `Score`, `Reason`, `SnapshotSummary`, `BreakdownDetails`.
That's enough to retrofit telemetry without changing the recording call
sites — extension work is on the persistence + parser side.

## 1. Goals

| # | Goal | Metric of success |
|---|---|---|
| 1 | Persist `DecisionLog` to disk per combat | One file per combat, parseable |
| 2 | Aggregate decisions across combats | Run analyzer over N combats; one report |
| 3 | Verify *actual* synergy activation frequencies | "VULN_AMPLIFIER fired X / Y plays where opportunity existed" |
| 4 | Verify lethal-mode trigger correctness | "Lethal-mode active in N turns; M of those actually closed the fight" |
| 5 | Compare planner output vs naïve / fallback baseline | A/B run between mod's planner and stock fallback over identical seeds |
| 6 | Identify decision regressions over releases | Per-release report diff |

## 2. Non-goals (intentional scope cuts)

- **Real-time UI overlay** — analyst-only, post-hoc. Game in-combat UX
  unaffected.
- **Network upload / shared telemetry** — local file only, privacy-respecting.
- **ML-based decision learning** — measurement, not optimisation. Tuning
  remains manual (`PlanScorerWeights`).

## 3. Architecture

```
┌──────────────────────────────────┐
│ In-game: DecisionLog (existing)  │   ring buffer, 32 entries
└──────────────┬───────────────────┘
               │  combat end / mod unload
               ▼
┌──────────────────────────────────┐
│ Phase A: persistence sink         │   NEW — flush to disk
│  DecisionLogPersister.cs          │   one .ndjson per combat
└──────────────┬───────────────────┘
               │  user copies / syncs files
               ▼
┌──────────────────────────────────┐
│ Phase B: parser                   │   NEW — scripts/parse_decision_log.py
│  ndjson → structured DataFrame    │
└──────────────┬───────────────────┘
               ▼
┌──────────────────────────────────┐
│ Phase C: analyzer                 │   NEW — scripts/analyze_decisions.py
│  metrics + comparison + report    │
└──────────────┬───────────────────┘
               ▼
┌──────────────────────────────────┐
│ Output: docs/runtime_metrics.md   │   git-committed; release diffable
│  + per-combat JSON for drill-in   │
└──────────────────────────────────┘
```

## 4. Phases

### Phase A — Persistence (mod-side C#)

**Scope**: extend `DecisionLog` to flush to disk.

Changes:
- New file `Sts2CombatAICode/Core/Diagnostics/DecisionLogPersister.cs`:
  - Subscribes to combat-end event (existing in mod's combat lifecycle).
  - Writes the current ring buffer contents as NDJSON to
    `<game_user_data>/Sts2CombatAI/decision_log/<timestamp>_<seed>_<floor>.ndjson`.
  - File rotation: keep last 200 combats (~20 MB cap).
- Extend `DecisionLog.Entry`:
  - Add `Turn` (current combat turn number)
  - Add `EnemyHpBefore` / `EnemyHpAfter` for damage attribution
  - Add `LethalActive` / `IsFetchCard` / `ComboLinks` flags surfaced from
    score breakdown (avoids parsing detail strings later)
- ModConfig toggle: `EnableDecisionLogPersist` (default ON in debug, OFF
  in release).

Acceptance: launching a combat, finishing it, observing one
`.ndjson` file appear in the user data dir with `≤Capacity` entries.

Estimated effort: 1 PR, ~150 LOC.

### Phase B — Parser (Python side)

**Scope**: ingest NDJSON files, produce a normalized DataFrame.

New file: `scripts/parse_decision_log.py`.

Inputs:
- `--logs <dir>` — directory of NDJSON files (default
  `~/.local/share/Sts2/Sts2CombatAI/decision_log/`)
- `--since <date>` — filter

Output:
- Stdout: summary (combat count, total decisions, average decisions per turn)
- `--out <path>` — write Parquet / JSON of normalized records for analyzer

Schema (per decision):
```
combat_id      : str    (file stem)
turn           : int
step           : int
playstyle      : str
card_id        : str
target         : str
score          : int
lethal_active  : bool
fetch_card     : bool
combo_links    : int
breakdown_keys : list[str]   (parsed from Details, e.g. ["dmg{12}", "vulnAmpTgt=+450"])
breakdown_kv   : dict        (subset parsed to numeric, e.g. {"dmg": 12, "vulnAmpTgt": 450})
enemy_hp_before: int
enemy_hp_after : int
character      : str         (derived from snapshot summary or filename)
```

Robustness: tolerant of detail-string format changes; warn-not-fail on
unparseable rows.

Acceptance: parse 100 sample combats without error, report row count
and schema.

Estimated effort: 1 PR, ~300 LOC Python.

### Phase C — Analyzer (Python side)

**Scope**: compute the actual runtime metrics that static analysis can't.

New file: `scripts/analyze_decisions.py` (consumes Phase B output).

Metrics (initial set — extensible):

| Metric | Definition | Validates |
|---|---|---|
| **Synergy activation rate** | Fraction of plays where the active synergy axis (`vulnAmpTgt`, `dmgAmp`, `buildSyn` etc.) actually fired | Whether ours pair-rule / amplifier rules trigger in practice |
| **Lethal detection precision** | (Turns where `lethal_active=true` AND fight ended) / (all `lethal_active=true` turns) | False-positive rate of lethal-mode |
| **Lethal detection recall** | (Combats where last turn had `lethal_active=true`) / (all combats) | False-negative rate |
| **PowerCatalog hit miss-as-lower-bound** | For each Power play, did the breakdown include any `*Power(...)=N` entry or did it fall through to default? | Calibrates the static "True DefaultValue" estimate |
| **Combo recognition fire rate** | Plays with `combo_links ≥ 3` | Whether the chain detector catches real combos |
| **Fetch pollution decisions** | Plays of fetch cards when piles had ≥20% junk | Whether the penalty deters fetches in dirty decks |
| **Ordering correctness** | Setup-Power-before-beneficiary fraction; Vuln-applier-before-multi-hit fraction | A-2 / d gap validations |
| **Cost-curve realisation** | Average plays-per-turn; energy-leftover distribution | Energy monopoly impact |
| **Decision diversity** | Per-character: distinct card IDs played / unique plays. Highlights over-reliance | Sanity check on hand-tuning |
| **Per-tier card usage** | Audit cross-ref: did S-tier cards actually play more often than D-tier? | Validates impact-weighted gap analysis |
| **Score vs HP outcome** | Correlation between average decision score and combat outcome (HP loss) | Top-level "is the planner doing better?" indicator |

Outputs:
- `docs/runtime_metrics.md` (markdown report, git-committable)
- `docs/runtime_metrics_<version>.json` (machine-readable, for release diffs)

Acceptance: run analyzer over a sample log set, all metrics populate
without nulls; report is human-readable.

Estimated effort: 1 PR, ~500 LOC Python.

### Phase D — A/B baseline comparison (optional)

**Scope**: run the planner with `Playstyle.Disabled` (fallback AI) vs
normal modes over identical combats, compare outcomes.

Mechanism:
- Mod-side flag forces fallback AI for half of combats (configurable
  ratio).
- Logs include `planner_mode = ours | fallback`.
- Analyzer pivots on this column → per-character HP-saved / win-rate diff.

Skip if turn-around speed isn't critical — Phase C alone proves most
hypotheses.

Estimated effort: 1 PR, ~80 LOC C# + 100 LOC Python analyzer extension.

### Phase E — CI / release diff

**Scope**: per-release report diff in CI.

- New script `scripts/diff_runtime_metrics.py`: compare two
  `runtime_metrics_<version>.json` files, flag any metric that moved
  >5% (configurable).
- Optional GitHub Actions hook: comment on PR with diff if both
  baseline and PR have runtime data.

Estimated effort: 1 PR, ~150 LOC, depends on Phase C output being stable.

## 5. Initial development order

1. **Phase A** first — without persistence nothing downstream works.
2. **Phase B** second — confirm the format is parseable before building
   metrics on top.
3. **Phase C** third — value-extraction phase. Most analyst time here.
4. Phase D / E are nice-to-haves; ship when Phase A-C steady-state.

Stop criteria for Phase A:
- 10 real combats logged
- One sample NDJSON manually inspected for completeness

Stop criteria for Phase B:
- Parser handles all 10 sample files without warnings
- Schema validated against representative rows

Stop criteria for Phase C:
- All metrics in the initial table produce non-null output
- One actionable insight identified ("X synergy never fires in practice
  → re-tune") OR null result documented ("nothing to fix here")

## 6. Risks & open questions

### Risks

| Risk | Mitigation |
|---|---|
| Log file size growth (long sessions) | Rotation cap (200 combats / ~20 MB), per-combat file |
| Detail string format drift breaks parser | Tolerant parser; warn-not-fail; pin format changes in CHANGELOG |
| Performance impact of persistence | Async flush at combat end, not per-decision; expected <5ms / combat |
| User-data path varies by OS / Steam | Use `Path.Combine(Application.userDataPath, ...)`; fall back to mod directory |
| Privacy / unwanted data capture | Opt-in via ModConfig; no telemetry leaves user machine |
| Combat-end event hook fragility | Defensive: also flush on mod unload + game exit |

### Open questions (decide during Phase A)

- **File format**: NDJSON (chosen — easy to append, line-delimited).
  Alternative SQLite (more structured) considered; deferred unless
  cross-combat queries get heavy.
- **Granularity**: per-decision (current `DecisionLog.Entry` shape) vs
  per-turn (one record per turn with all candidate scores). Per-decision
  is simpler; per-turn enables "why didn't card X play?" analysis. Phase
  A defaults to per-decision; Phase C can request per-turn extension if
  needed.
- **Combat outcome attribution**: who records "fight won" vs "fight lost
  HP X"? Likely the persister at flush time, reading from combat state.
- **Seed capture**: needed for A/B comparison (Phase D). Capture at
  combat start in the NDJSON header line.

## 7. Cross-references

- `docs/ai_card_coverage.md` — static audit (the lower-bound this work
  validates and refines)
- `docs/card_play_order_framework.md` — ordering rules whose runtime
  behavior this work measures
- `docs/pair_axis_orphan_analysis.md` — convention work whose actual
  decision impact this work can quantify

## 8. Effort summary

| Phase | C# | Python | Total LOC | Time est. (focused) |
|---|---:|---:|---:|---|
| A — persistence | 150 | 0 | 150 | 1 dev-day |
| B — parser | 0 | 300 | 300 | 1 dev-day |
| C — analyzer | 0 | 500 | 500 | 2 dev-days |
| D — A/B | 80 | 100 | 180 | 1 dev-day (optional) |
| E — release diff | 0 | 150 | 150 | 0.5 dev-day (optional) |
| **Core (A+B+C)** | **150** | **800** | **950** | **~4 dev-days** |

## 9. First concrete next step

PR #1: Phase A — `DecisionLogPersister.cs` + extended `DecisionLog.Entry`
fields. Does NOT change planner behavior; pure observability. Safe to
land independently.

Recommended starting commit message:
`feat(diag): persist DecisionLog ring buffer to NDJSON on combat end`.
