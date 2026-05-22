# Planner Verification + PlanScorer Tuning — Handoff Doc

Last updated 2026-05-22. Covers Phase 1–9b of the cross-repo work that uses
Sts2CombatCore (game-engine harness) to verify and improve Sts2CombatAI's
PlanScorer / ActionPlanner.

This doc exists so the work can be resumed on another machine — it captures
where the code lives, how to build and run the harness, and what the open
follow-ups are.

---

## 1. Repos

| Repo | Remote | Local path (primary machine) | Purpose |
|---|---|---|---|
| Sts2CombatAI | `ing-gom/sts2-combat-ai` (public) | `C:\Users\dev\sts2-card-advisor-dev\Sts2CombatAI\` | Combat-decision mod. Contains the PlanScorer / ActionPlanner code that we verify. |
| Sts2CombatCore | `ing-gom/sts2-combat-core` (private) | `C:\Users\dev\sts2-combat-core\` | Headless harness wrapping sts2.dll via Harmony. Includes the ScenarioVerifier and the planner-benchmark script. |

Both repos are on `master`. Sts2CombatCore source-includes selected files from
Sts2CombatAI/Sts2CombatAICode/Core/ via conditional `<Compile Include>` in
`src/Sts2CombatCore/Sts2CombatCore.csproj` — the include path is
`../../../sts2-card-advisor-dev/Sts2CombatAI/Sts2CombatAICode/Core/`, so the
two repo folders must be siblings under the same parent.

The conditional include guards on `Exists($(Sts2DataDir))`, so the build will
fall back gracefully if the game install isn't found.

---

## 2. New-machine setup

### 2.1 Clone sibling repos

```powershell
mkdir C:\Users\<you>\dev; cd C:\Users\<you>\dev
git clone https://github.com/ing-gom/sts2-card-advisor.git sts2-card-advisor-dev
git clone https://github.com/ing-gom/sts2-combat-core.git
```

The `sts2-card-advisor-dev` repo is the outer dev tree; `Sts2CombatAI/` lives
inside it as a separate git repo (the public mod repo). After cloning the
outer repo, also clone Sts2CombatAI into the same subfolder:

```powershell
cd sts2-card-advisor-dev
# Sts2CombatAI is its own git repo — clone it if the directory is empty
git clone https://github.com/ing-gom/sts2-combat-ai.git Sts2CombatAI
```

### 2.2 Game data dependency

Sts2CombatCore needs `sts2.dll` and `0Harmony.dll`. Default lookup path:

```
C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\
```

If the game is installed elsewhere, set `Sts2DataDir` in `local.props` at the
Sts2CombatCore repo root (not committed). Example:

```xml
<Project>
  <PropertyGroup>
    <Sts2DataDir>D:\Games\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64</Sts2DataDir>
  </PropertyGroup>
</Project>
```

### 2.3 Toolchain

- .NET 9 SDK (verified with 9.0.x). `dotnet --info` should show a 9.x SDK.
- PowerShell 5.1 or 7 (the benchmark script uses powershell.exe — Core has
  bash-incompatible `pwsh` aliasing on Windows).
- Optional: `ilspycmd` for decompiling sts2.dll when investigating engine
  internals.
  ```
  dotnet tool install --global ilspycmd
  ```

### 2.4 Build smoke test

```powershell
# Sts2CombatAI unit tests
cd C:\Users\<you>\dev\sts2-card-advisor-dev\Sts2CombatAI
dotnet build Sts2CombatAI.Tests/Sts2CombatAI.Tests.csproj -c Release
& Sts2CombatAI.Tests\bin\Release\net9.0\Sts2CombatAI.Tests.exe
# Expect "=== 119 passed, 0 failed ==="

# Sts2CombatCore benchmark
cd C:\Users\<you>\dev\sts2-combat-core
powershell.exe -NoProfile -File scripts/planner-benchmark.ps1
# Expect "no regressions detected"
```

---

## 3. What's the work, in one paragraph

Sts2CombatAI ships a planner (`PlanScorer.Score` + `ActionPlanner.PlanNextStep`)
that picks cards during combat. Sts2CombatCore loads the real `sts2.dll` and
drives a full encounter through it, letting the planner pick each play. The
ScenarioVerifier runs all 80 in-game encounters (`--scenario-compare`) across
three decision modes (Random / Planner1Step / PlannerDepthN) and emits HTML
traces + an `outcomes.json` summary. `tests/planner-benchmark-baseline.json`
locks the per-encounter WIN/LOSS verdicts; the `planner-benchmark.ps1` script
diffs current vs baseline and exits 1 on any WIN → LOSS regression. This makes
PlanScorer changes safely measurable.

---

## 4. Phase history (highlights)

Full per-phase detail lives in the project memory at
`C:\Users\dev\.claude\projects\C--Users-dev-sts2-card-advisor-dev\memory\project_planner_verification.md`.
Below are the commits and net effect on the strong-deck 80-encounter benchmark.

| Phase | Commits | Net effect |
|---|---|---|
| 1 — source-include infra | Sts2CombatCore `b87bd36` | Planner code source-included into harness. |
| 2 — DecisionMode enum | Sts2CombatCore `473ab81` | `--decision random\|planner\|planner-depth2` CLI flag. |
| 3 — `--scenario-compare` + outcomes.json | Sts2CombatCore | 80-encounter × 3-mode side-by-side. |
| 4 — planner-benchmark.ps1 + baseline | Sts2CombatCore | Regression harness. |
| 5 — `PowerCardBonus 1000→700` + burst-window detector | Sts2CombatAI `170e35c`, `479627c` | P1 WIN ≥ Random baseline. |
| 6 — `StableStringHash` (FNV-1a) | Sts2CombatAI `18c83fa` | Deterministic planner across processes (.NET 5+ randomizes `string.GetHashCode`). |
| 7 — `MinPlayScore 80→0`, `HpPressurePowerPenalty=-1500` | Sts2CombatAI `1277119` | P1 dealt +83, taken unchanged. ExoskeletonsNormal gap closed to 13 points. |
| 8 — `DamageCapWastePenaltyPerLost=50` (cap-waste opportunity-cost) | Sts2CombatAI `5607f6b` | Defensive (no firings on strong-deck, guards BLUDGEON-class decks). |
| 8b — `SlowAttritionPowerExtraPenalty=-150` | Sts2CombatAI `5607f6b` (same) | **ExoskeletonsNormal Planner1Step LOSS→WIN** (+1 WIN). |
| 9 — PlannerCardSelector scaffold | Sts2CombatCore `f43c1b2` | Tried PlanScorer.Score; outcomes unchanged, P1 taken +67. Reverted wiring. |
| 9b — CopyValueScorer + wired PlannerCardSelector | Sts2CombatAI `e0f0eab`, Sts2CombatCore `ba03e3d` | Per-energy × playability × fight-length model. Outcomes unchanged, baseline refreshed to reflect new selector. |

**Cumulative result on strong-deck baseline (Ironclad 25-card deck, 80 encounters):**

| Mode | WIN | LOSS | dealt | taken |
|---|---|---|---|---|
| Random | 46 (57.5%) | 34 | 6821 | 4361 |
| Planner1Step | **52 (65.0%)** | 28 | 6893 | 3873 |
| PlannerDepthN | **53 (66.25%)** | 27 | 7058 | 3723 |

Planner1Step **+6 WIN** vs Random, PlannerDepthN **+7 WIN**. Zero test
regressions (Sts2CombatAI 119/119).

---

## 5. Daily commands

### Run Sts2CombatAI unit tests
```powershell
cd C:\Users\<you>\dev\sts2-card-advisor-dev\Sts2CombatAI
dotnet build Sts2CombatAI.Tests/Sts2CombatAI.Tests.csproj -c Release --nologo
& Sts2CombatAI.Tests\bin\Release\net9.0\Sts2CombatAI.Tests.exe
```

Tests source-include the Core .cs files via `<Compile Include="..\Sts2CombatAICode\Core\...">`,
so editing any file under `Sts2CombatAICode/Core/Planner/` automatically picks
up in the test build.

### Run the planner regression benchmark
```powershell
cd C:\Users\<you>\dev\sts2-combat-core
powershell.exe -NoProfile -File scripts/planner-benchmark.ps1
# -SkipBuild to skip dotnet build (faster iteration if Sts2CombatCore unchanged)
```

The script writes a fresh `scenarios_compare/` tree (HTML traces per encounter,
per mode) and `outcomes.json`. If a PlanScorer change improves outcomes, refresh
the baseline:

```powershell
Copy-Item scenarios_compare/outcomes.json tests/planner-benchmark-baseline.json
```

### Single-mode trace inspection
```powershell
cd C:\Users\<you>\dev\sts2-combat-core
$exe = "src/Sts2CombatCore/.godot/mono/temp/bin/Release/Sts2CombatCore.exe"
& $exe --scenario-all-encounters scenarios_dump --deck strong --decision planner
# Outputs HTML traces under scenarios_dump/ — open Monster_*.html in a browser.
```

### Decompile sts2.dll for engine investigation
```powershell
ilspycmd "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" -t MegaCrit.Sts2.Core.Models.Cards.DualWield
```

---

## 6. Key files

### Sts2CombatAI (PlanScorer side)

- `Sts2CombatAICode/Core/Planner/PlanScorer.cs` — main scoring routine
  (~1900 LOC). Attack branch at line 641, Power branch at line 555, Skill
  branch at line 1479. Phase 8 cap-waste penalty inlined in attack cap-clamp
  block (line 717). Phase 8b slow-attrition penalty in Power branch
  hpPressurePenalty (line 600).
- `Sts2CombatAICode/Core/Planner/PlanScorerWeights.cs` — all tunable knobs.
  `PowerCardBonus = 700`, `MinPlayScore = 0`, `HpPressurePowerPenalty = -1500`,
  `HpPressurePowerThreshold = 32`, `BurstChainAttackBonus = 1500`,
  `DamageCapWastePenaltyPerLost = 50`, `DamageCapWasteMinRatio = 1.5`,
  `SlowAttritionPowerExtraPenalty = -150`.
- `Sts2CombatAICode/Core/Planner/ActionPlanner.cs` — depth-N beam search.
  `StableStringHash` (FNV-1a) at line ~152 replaces process-randomized
  `string.GetHashCode()` for Monte Carlo seeding.
- `Sts2CombatAICode/Core/Planner/CopyValueScorer.cs` — Phase 9b. Per-energy ×
  playability × fight-length scoring for fetch-card future-play decisions.
- `Sts2CombatAI.Tests/Program.cs` — 119 tests covering scoring math, Sim
  helpers, AdvanceTurn, Phase 8/8b breakdown details, Phase 9b CopyValueScorer
  semantics.

### Sts2CombatCore (harness side)

- `src/Sts2CombatCore/Harness/ScenarioVerifier.cs` — 1000+ LOC deck-based
  80-encounter driver. `DecisionMode` enum at top dispatches between Random /
  Planner1Step / PlannerDepthN. `FindCandidatePlanScorer` and
  `FindCandidatePlannerDepthN` snapshot via `StateSnapshotter` and route to
  `PlanScorer.Score` / `ActionPlanner.PlanNextStep`.
- `src/Sts2CombatCore/Harness/PlannerCardSelector.cs` — Phase 9b. Routes
  `CardSelectCmd.From*` choices through `CopyValueScorer.Score` for the
  Planner modes. Falls back to first-N for non-hand options.
- `src/Sts2CombatCore/Harness/GodotIsolation.cs` — Harmony prefix replacements
  for 6 NetId-guarded hook dispatchers + headless-incompatible engine paths
  (NPlayerHand UI lookups, ResourceLoader.Load native crashes).
- `src/Sts2CombatCore/Program.cs` — CLI. `--scenario-compare <dir>`,
  `--deck strong`, `--decision random|planner|planner-depth2`.
- `scripts/planner-benchmark.ps1` — regression harness wrapper.
- `tests/planner-benchmark-baseline.json` — locked WIN/LOSS verdicts +
  per-mode summary metrics.

---

## 7. Open follow-ups

In rough priority order:

1. **Necrobinder / Defect / Silent deck baselines.** The current
   `--deck strong` is Ironclad-only. Extending the benchmark to all four
   classes would surface PlanScorer / CopyValueScorer behavior in fetch-heavy
   decks (Silent SECRET_TECHNIQUE, Defect orb cards). Requires extending
   `BuildIroncladDeckStrong25()` in `Program.cs` and adding a `-Deck` switch
   that maps to per-class deck builders.
2. **Phase 9b calibration on fetch-heavy decks.** Current `PlayabilityFactor`
   table (0→1.0, 1→0.95, 2→0.60, 3→0.30, 4+→0.10) is calibrated against the
   Ironclad 3-energy budget. Silent / Defect run cards with different cost
   distributions; the constants may need per-character tuning when (1) lands.
3. **git pre-push hook auto-install.** `scripts/install-hooks.ps1` would copy
   a pre-push hook that runs `planner-benchmark.ps1` and aborts on regression.
   Currently the benchmark must be run manually.
4. **PlanScorer code change for HardToKill cheap-hit preference.** Phase 7
   diagnosis concluded ExoskeletonsNormal's regression case needs DamageCap-aware
   target-priority logic — Phase 8b's slowAttrition Power penalty fixed it via
   a different lever, but the explicit cheap-hit preference is still on the
   table for future cap-fight scenarios.
5. **Episode/RL integration.** Sts2CombatCore already has Episode.cs +
   PolicyCardSelector for an agent-driven step API (separate from the
   in-process ScenarioVerifier path). When the RL training loop is wired,
   PolicyCardSelector replaces both LazyCardSelector and PlannerCardSelector
   for those runs.

---

## 8. Project memory

The `auto memory` system has the full Phase log + cross-referenced decisions at:

```
C:\Users\<you>\.claude\projects\C--Users-dev-sts2-card-advisor-dev\memory\MEMORY.md
```

The most relevant entries for this work:

- `project_planner_verification.md` — Phase 1–9b log, root index for this work
- `project_sts2_combat_core.md` — Sts2CombatCore architecture + v11–v18 history
- `project_vakuu_plus.md` — Sts2CombatAI (parent project) v0.9.x history
- `feedback_combat_core_vs_combat_ai.md` — disambiguation: "simulation accuracy"
  references Sts2CombatCore, NOT Sts2CombatAI mod
- `reference_netid_guarded_hooks.md` — 6 hooks that silent-skip in headless +
  the G4 prefix-replacement pattern that fixes them
- `feedback_player_choice_context.md` — `BlockingPlayerChoiceContext` vs
  `ThrowingPlayerChoiceContext` semantics
- `reference_combat_reflection.md` — pointer to Sts2UndoMod's prior art
