# B-Architecture Session 2 Summary — 2026-05-29

Continuation session ("다음 세션 진행해줘"). Started from previous session's
checkpoint (architecture in place at 42.8% parity, 132/132 tests). Focus
shifted from architecture extension to **investigating + fixing concrete
divergence sources** uncovered by the per-card parity audit.

---

## TL;DR

| Metric | Session 1 end | Session 2 end | Δ |
|---|---|---|---|
| Sim parity (50 ep Ironclad) | 42.8% | **48.0%** | **+5.2pp** |
| Cumulative since baseline | — | 41.8% → 48.0% | **+6.2pp** |
| WHIRLWIND records agree | 0/14 (0%) | 9/13 (69%) | huge |
| IRON_WAVE records agree | 0/13 (0%) | 6/13 (46%) | +46pp |
| BLOOD_WALL / HEMOKINESIS / BREAKTHROUGH | 0% all | 36-46% | major |
| MCTS-50sim WIN (bias=-3, 720 games) | 484/720 (67.2%) | 486/720 (67.5%) | +0.3pp (within noise) |
| Sts2CombatAI tests | 132/132 | 132/132 | 0 regression |
| Architecture (V2 path) | intact | intact | unchanged |

**Honest framing**: Real parity lift achieved, but MCTS WIN rate moved
much less than parity. Possible reasons documented in section 7.

---

## What was done

### A. Probe instrumentation (investigation-only, reverted)

Added 11 diagnostic fields to `SimulatorParityCheck.cs` JSONL output
(prev/pred/real per-enemy HPs, prev_player_energy/strength/weak,
card_damage/hits/target/axes). Used to identify root causes; reverted
after the audit completed. Final probe schema unchanged from session 1.

### B. WHIRLWIND root cause + fix (3 layers)

Discovered three stacked bugs:

1. **`CardCmd.AutoPlay` overwrites `CapturedXValue`** (sts2.dll line 396140-143):
   ```csharp
   if (card.EnergyCost.CostsX && !skipXCapture)
       card.EnergyCost.CapturedXValue = playerCombatState.Energy;
   ```
   Our `DirectInvokePump` calls `SpendResources()` first (sets
   `CapturedXValue=2`, drains Energy to 0), then `AutoPlay` (overwrites
   `CapturedXValue` from Energy now 0). Result: X=0 → 0 hits → 0 damage.
   **Fix**: pass `skipXCapture: true` to AutoPlay.

2. **`Whirlwind.OnPlay` VFX block NPEs in headless**:
   ```csharp
   if (num > 0) {
       double num2 = SaveManager.Instance.PrefsSave.FastMode ...;  // NPE
       NCombatRoom.Instance?...AddChildSafely(NHorizontalLinesVfx.Create(...));  // NPE
       SfxCmd.Play(...);  // NPE
       NRun.Instance?...AddChildSafely(NSmokyVignetteVfx.Create(...));  // NPE
   }
   await DamageCmd.Attack(...)...Execute(choiceContext);  // never reached
   ```
   When CapturedXValue=0 (bug 1) the if-block was skipped, hiding the
   issue. With bug 1 fixed, the VFX block fires and throws.
   **Fix**: Harmony prefix `WhirlwindOnPlayPrefix` → replace with
   VFX-free `WhirlwindOnPlayHeadless` that calls only the damage chain.

3. **Mod sim `Math.Max(1, ...)` clamp on X-cost hits**:
   ```csharp
   hitsForDmg = System.Math.Max(1, preSpendEnergy + xBonus);
   ```
   When player plays WHIRLWIND at 0 energy (catalog cost=0 → playable),
   real game does X=0 hits → 0 dmg, mod predicted 1 hit. Also relaxed
   V2 `EffectivePerEnemyTotalV2` to allow hits=0 → return 0.
   **Fix**: change to `Math.Max(0, ...)` + V2 short-circuit.

**Cumulative WHIRLWIND impact**: parity 42.8% → 44.1% (+1.3pp), WHIRLWIND
records 0/14 agree → 9/13 agree.

### C. IRON_WAVE / BLOOD_WALL — Attack-with-block

Mod sim's block-application branch is inside `if (card.IsSkill)`. Attack
cards with block (IRON_WAVE damage+block, BLOOD_WALL HpLoss+block)
fell through → `player_block=-5` consistently.

**Fix**: Add a separate `if (card.IsAttack && card.Block > 0)` branch
just before the attack damage loop. Reuses `StatusMath.EffectiveBlock`
for Dex/Frail scaling.

Parity: 44.1% → 45.0% (+0.9pp). IRON_WAVE 0/13 → 6/13 agree.

### D. HpLossVar catalog extraction — biggest single lift

`CardReflection.GetEffectSummary` was extracting HpLoss only when the
runtime var type was the BASE `DynamicVar` class with `Name=="HpLoss"`.
But sts2.dll uses typed `HpLossVar` (a subclass) — `typeName=="HpLossVar"`,
so the equality check `typeName == "DynamicVar"` excluded all of:

- BLOOD_WALL (HpLossVar 2)
- HEMOKINESIS (HpLossVar 2)
- BREAKTHROUGH (HpLossVar 1)
- BRAND (HpLossVar 1)
- BLOODLETTING, OFFERING, RUPTURE-class, REJECTION (10), etc.

Mod sim never applied their HP loss → consistent `player_hp=+N` diff.

**Fix**: Add `if (typeName.StartsWith("HpLossVar")) { hpLoss += amount; continue; }`
alongside the existing typed `DamageVar` / `BlockVar` branches.

Parity: 45.0% → **48.0% (+3.0pp)** — single largest fix of the session.
BLOOD_WALL 0/12 → 4/11 agree, HEMOKINESIS 0/13 → 6/13 agree, BREAKTHROUGH
0/17 → 3/15 agree.

---

## File changes (UNSTAGED — for user to commit)

### Sts2CombatAI repo

| File | Change | Purpose |
|---|---|---|
| `Sts2CombatAICode/Core/Sim/AnalyticalSimulator.cs` | X-cost hits=0 (Math.Max 1→0) + new attack-block branch | WHIRLWIND/IRON_WAVE fixes |
| `Sts2CombatAICode/Core/Sim/StatusMath.cs` | V2 hits=0 → return 0 | X-cost no-energy correctness |
| `Sts2CombatAICode/Core/Reflection/CardReflection.cs` | New HpLossVar branch | Catalog HP-loss extraction |

### sts2-combat-core repo

| File | Change | Purpose |
|---|---|---|
| `src/Sts2CombatCore/ActionPump/DirectInvokePump.cs` | `skipXCapture: true` arg to AutoPlay | WHIRLWIND X capture timing |
| `src/Sts2CombatCore/Harness/GodotIsolation.cs` | WhirlwindOnPlayPrefix + ...Headless (~50 lines) | Skip WHIRLWIND VFX in headless |

---

## Suggested commit grouping

### Sts2CombatAI

**Commit A**: `fix(sim): X-cost cards — allow X=0 → 0 hits → 0 damage`
- Just the Math.Max(1,...) → Math.Max(0,...) line + V2 hits=0 short-circuit.

**Commit B**: `feat(sim): attack-with-block branch — IRON_WAVE / BLOOD_WALL`
- Just the new `if (card.IsAttack && card.Block > 0)` block.

**Commit C**: `fix(reflection): extract HpLossVar typed subclass`
- Just the new `if (typeName.StartsWith("HpLossVar"))` branch.
- biggest impact (+3pp parity); deserves its own commit message.

### sts2-combat-core

**Commit D**: `fix(harness): X-cost cards — skipXCapture + Whirlwind OnPlay`
- DirectInvokePump skipXCapture + GodotIsolation WhirlwindOnPlayPrefix.
- Two-part bundle since both are needed for WHIRLWIND to apply damage.

---

## Open questions for next session

In rough priority by likely impact (per session 1's 100% diverger list):

1. **SWORD_BOOMERANG (24/24)** — random AOE 3 hits. Mod needs random target distribution (each hit picks random alive enemy).
2. **POMMEL_STRIKE / SHRUG_IT_OFF (13+10)** — `draw_pile_count = +1` consistently. Cards say "Draw 1" on play; mod sim doesn't model the draw (move from draw pile → hand).
3. **TRUE_GRIT (8/8)** — `hand_count=+1, exhaust=-1`. Random hand exhaust on play. Either model with random selection or apply "exhaust an unplayable" heuristic.
4. **SETUP_STRIKE (18/18)** — Attack with self-Strength. Mod sim's PowerApps branch only fires for `selfTarget` skills. Need: when STRENGTH_PRODUCER axis is on an attack, apply Strength to self separately from enemy-debuff PowerApps.
5. **SECOND_WIND (13/13)** — Hand exhaust for block. Similar to TRUE_GRIT but exhausts ALL cards.
6. **HEADBUTT (14/14)** — discard→draw move. Mod sim doesn't simulate the chosen-discard-to-draw move.
7. **ARMAMENTS (11/11)** — Choose timing. ARMAMENTS triggers Choose UI; parity probe handles Choose by auto-Choose(0) but snapshot timing diverges.

Most are 1-3 hour fixes each. The parity-to-MCTS-WIN translation rate
is unclear (this session showed +5.2pp parity → +0.3pp WIN), so future
fixes should measure both.

---

## Honest assessment (parity vs WIN rate)

Session 2 delivered **+5.2pp parity** but only **+0.3pp MCTS-50sim WIN**.
Two likely reasons:

1. MCTS-50 has low leaf-value sensitivity — 50 sims smooths over imprecise
   leaf evaluations via random rollouts.
2. The fixed cards (HP-loss, attack-block) aren't pivotal to WIN/LOSS —
   they refine accounting but don't change critical-turn decisions.

The next batch of fixes (SWORD_BOOMERANG random AOE, POMMEL_STRIKE draw,
TRUE_GRIT exhaust) targets cards with more decision-impact, so MCTS lift
should be larger per parity point.

**Process recommendation for future sessions**: measure both parity AND
MCTS-50/200 WIN rate per fix. Parity alone is misleading.

---

## Memory updates (persisted)

- `[[project_b_damage_modifier_architecture]]` — updated with session 2 outcomes + remaining 100%-diverger list

No new memory files this session — all findings folded into the existing
project memory.
