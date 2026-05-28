# B-Architecture Session Summary — 2026-05-28

Multi-session work block started while user was sleeping (~5h budget,
~2.5h used). Path chosen: **design + memory + spike** (not full Phase
2 mechanics work, which needs runtime instrumentation).

---

## TL;DR

| Metric | Before | After |
|---|---|---|
| Sim parity (overall agree) | 41.8% | **42.8%** (+1.0pp from A2 ANGER fix) |
| Sts2CombatAI tests | 119/119 | **132/132** (+13 V1↔V2 parity) |
| MCTS-50sim (bias=-3, 720 games) | 484/720 (67.2%) | **484/720** bit-identical |
| Architecture | StatusMath hard-coded | **IDamageModifier registry** + 9 baseline modifiers |
| Design doc | n/a | `docs/damage-modifier-architecture.md` |

**Honest framing:** Architecture refactor is done and proven safe. Parity
target (65%+) was NOT reached. The work needed to reach 65% — adding
modifiers for un-modeled active powers + card-specific mechanic handlers
— is iterative, needs per-power runtime instrumentation, and was
intentionally deferred. This session built the safe foundation.

---

## What was done

### 1. Pre-flight diagnostics (re-done on fresh data)

The first analysis ran against stale `runs/sim_parity.jsonl` (v1 dump from
before commit `eba2a99`'s discard fix), producing phantom priorities like
"discard 51% / SPITE 39%." Regenerated probe with current binary →
correct priorities surfaced:

| Field | % of records non-zero |
|---|---|
| `enemy_hp_sum`        | 28.4% |
| `discard_pile_count`  | 12.4% |
| `player_block`        | 8.8%  |
| `player_hp`           | 8.5%  |
| `hand_count`          | 8.4%  |
| `player_strength`     | 7.3%  |
| `draw_pile_count`     | 5.6%  |
| `exhaust_pile_count`  | 4.9%  |

Saved this lesson as `[[feedback_stale_data_lesson]]`.

### 2. A1 — WHIRLWIND deep-dive (closed without fix)

Verified commit `01a79b5`'s X-cost hit-count fix landed; WHIRLWIND still
100% diverges (14/14) on `enemy_hp_sum` (range -5 to -40). Remaining gap
is multi-hit AOE per-enemy + possibly per-hit Strength refresh — both
"larger audit" per the commit author. No single quick fix. Confirmed
architecture work IS the right tool here.

### 3. A2 — ANGER self-duplication fix (LANDED, +1.0pp)

ANGER's description duplicates itself into discard pile (real game adds
+2; mod sim was adding +1). Fixed via 5-line addition in
`AnalyticalSimulator.cs:746-754` (else-if !card.IsExhaust branch).
ANGER agree: 1/15 → 7/15. Overall parity: 41.8% → 42.8%.

### 4. B — IDamageModifier listener architecture spike

**Files added/changed (UNSTAGED — see commit grouping below):**

- **NEW** `Sts2CombatAICode/Core/Sim/DamageModifiers.cs` (~280 lines)
  - `DamageStage` enum (Additive / Multiplicative / Cap)
  - `DamageContext` readonly struct (no-alloc hot path)
  - `IDamageModifier` interface + `DamageModifierBase` helper
  - `DamageModifierRegistry` static (register + Resolve pipeline)
  - 9 baseline modifiers reproducing StatusMath V1 1:1:
    StrengthAdditive, VigorAdditive, AccuracyShivBonus,
    VulnerableMult, WeakMult, TrackingVsWeak, CrueltyVsVulnerable,
    LethalityFirstAttack, IntangibleCap
  - `BaselineDamageModifiers.RegisterAll()` + module initializer
- **CHANGED** `Sts2CombatAICode/Core/Sim/StatusMath.cs` (~50 lines added)
  - `EffectivePerHitCappedV2` (registry-driven, defensive Register)
  - `EffectivePerEnemyTotalV2` (per-hit loop + HardenedShell post-cap)
  - V1 methods UNTOUCHED (rollback intact)
- **CHANGED** `Sts2CombatAICode/Core/Sim/AnalyticalSimulator.cs` (~15 lines)
  - Attack-branch V1 call (`EffectivePerEnemyTotal` + `ApplyDamageMultipliers`)
    collapsed to one `EffectivePerEnemyTotalV2` call
  - Constructs a `state with { … }` carrying post-card-effect player buffs
  - ALSO the A2 ANGER fix (separate concern — see commit grouping)
- **CHANGED** `Sts2CombatAI.Tests/Sts2CombatAI.Tests.csproj` (+1 line)
  - Compile Include DamageModifiers.cs
- **CHANGED** `Sts2CombatAI.Tests/Program.cs` (~180 lines added)
  - 13 V1↔V2 parity tests covering base, Strength, Vigor, Vulnerable,
    Weak, compound, Intangible cap, multi-hit, multi-hit+cap,
    Lethality first attack, Tracking vs Weak, Cruelty vs Vuln,
    HardenedShell total cap
- **NEW** `docs/damage-modifier-architecture.md`
- **CHANGED** `../sts2-combat-core/src/Sts2CombatCore/Sts2CombatCore.csproj` (+1 line)
  - Compile Include DamageModifiers.cs

**Verification:**
- `Sts2CombatAI.Tests`: **132/132 pass**
- Sim parity probe (regenerated): **42.8% V2 == 42.8% V1** (zero drift)
- MCTS-50sim 180×4×bias=-3 sweep: **484/720 V2 == 484/720 V1** bit-identical
  (per-bucket: Monster 428/528, Elite 47/96, Boss 9/96 all identical)

---

## Suggested commit grouping (for user to apply in the morning)

The changes are unstaged. Two distinct concerns — recommend two commits
in `Sts2CombatAI` plus one in `sts2-combat-core`.

### Commit 1 — `Sts2CombatAI` — ANGER fix only

```
fix(sim): ANGER self-duplication to discard pile

ANGER's catalog description ("이 카드의 복사본을 1장 버린 카드 더미에 추가합니다")
adds a copy of itself to discard on play. Mod sim was tracking only the
played card (+1) while real game adds both (+2). Affected 14/15 ANGER
plays in the parity probe with consistent discard_pile_count = -1.

Sim parity: 41.8% → 42.8% (+1.0pp).
```

Files: just the `if (card.Id == "ANGER")` block in `AnalyticalSimulator.cs:746-754`.

Use `git add -p` to stage only those 8 lines.

### Commit 2 — `Sts2CombatAI` — B architecture spike

```
feat(sim): IDamageModifier listener architecture spike (V2 path)

Refactor damage resolution from StatusMath's hard-coded chain into a
listener pipeline. Per-power modifiers register against one of three
stages (Additive / Multiplicative / Cap); AnalyticalSimulator's attack
branch iterates them per-hit per-enemy via DamageModifierRegistry.

New: Sts2CombatAICode/Core/Sim/DamageModifiers.cs — interface, context,
registry, 9 baseline modifiers (Strength, Vigor, Accuracy-vs-SHIV,
Vulnerable, Weak, Tracking, Cruelty, Lethality, Intangible cap) that
reproduce StatusMath V1's arithmetic 1:1.

New: StatusMath.EffectivePerHitCappedV2 + EffectivePerEnemyTotalV2 —
registry-driven, parallel to the V1 methods (untouched, rollback intact).

Changed: AnalyticalSimulator attack branch calls V2 once instead of
EffectivePerEnemyTotal + ApplyDamageMultipliers.

Tests: 132/132 pass (119 baseline + 13 V1↔V2 parity unit tests).
Parity probe: 42.8% V2 == 42.8% V1, zero behavioral drift.
MCTS-50sim 720-game sweep: 484/720 V2 bit-identical to V1
(per-bucket Monster/Elite/Boss all identical).

Design doc: docs/damage-modifier-architecture.md — interface, modifier
catalog, migration path, perf notes, out-of-scope card-mechanic handoff.

Foundation only — adding modifiers for un-modeled active powers (per-hit
Strength refresh, EnragePower, etc.) and card-specific mechanic handlers
(16 cards at 100% divergence) are deferred to Phase 1-2 work.
```

Files:
- `Sts2CombatAICode/Core/Sim/DamageModifiers.cs` (new)
- `Sts2CombatAICode/Core/Sim/StatusMath.cs` (V2 method additions, V1 untouched)
- `Sts2CombatAICode/Core/Sim/AnalyticalSimulator.cs` (V2 switch — separate from ANGER block above)
- `Sts2CombatAI.Tests/Sts2CombatAI.Tests.csproj` (+1 Compile Include)
- `Sts2CombatAI.Tests/Program.cs` (+13 parity tests)
- `docs/damage-modifier-architecture.md` (new)
- `docs/b-architecture-session-2026-05-28.md` (this file — optional, can drop)

### Commit 3 — `sts2-combat-core` — build include

```
chore: include DamageModifiers.cs in Sts2CombatCore build

Source-include the new file from sister Sts2CombatAI repo so the
sim-parity probe + MCTS planner pick up the registry-driven V2 path.
```

Files: just `src/Sts2CombatCore/Sts2CombatCore.csproj` (+1 line).

---

## Memory updates (persisted, no user action needed)

- [[project_b_damage_modifier_architecture]] — running notes, V2 outcomes
- [[feedback_stale_data_lesson]] — analyze fresh data before deciding priorities
- [[reference_sim_parity_probe]] — command + JSONL schema + interpretation
- [[MEMORY.md]] — index entries for above three

---

## Open questions for next session

1. **WHIRLWIND -40 root cause** — needs runtime instrumentation. Likely
   one of: AOE target enumeration mismatch, per-hit Strength refresh
   missing, or X-cost calculation edge case. The X-cost fix landed but
   does NOT close the gap.
2. **What 16 cards at 100% divergence actually need** — likely
   card-specific handlers, not modifier-pattern additions. See section 8
   of the design doc for the per-card handoff list.
3. **Phase 1 modifier additions** — which un-modeled active powers
   actually contribute to enemy_hp_sum/player_block divergence? Answer
   requires per-power frequency analysis on parity records, deferred
   pending instrumentation.
4. **MCTSPlanner sims sweep at V2** — only 50sim tested. Re-run
   MCTS-100/200 with V2 to confirm bit-identical (likely yes, since V2
   is bit-identical to V1 in the damage path).

---

## Files NOT touched (kept clean)

- `runs/` directories left as-is (untracked)
- `Sts2CombatAICode/Core/Onnx/ppo.onnx.bak` — pre-existing, not from this session
- `sts2-combat-core/runs/ppo_curriculum_*.onnx` — pre-existing local modifications
