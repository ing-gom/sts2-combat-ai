# B-Architecture Session 4 Summary — 2026-05-31

Continuation session 4. Largest single-session parity gain in the
project so far (+9.2pp). Largest single fix was the PileType.Play
snapshotter addition.

---

## TL;DR

| Metric | Session 3 end | **Session 4 end** | Δ |
|---|---|---|---|
| Sim parity (50 ep Ironclad) | 53.4% (350/656) | **62.6% (416/665)** | **+9.2pp** |
| Cumulative since S0 baseline (41.8%) | +11.6pp | **+20.8pp** | — |
| HEADBUTT agree | 0/13 | 7/14 (50%) | +50pp |
| ARMAMENTS agree | 0/11 | 7/12 (58%) | side-benefit of PlayPile fix |
| SWORD_BOOMERANG agree | 0/21 | 8/17 (47%) | round-robin AOE |
| CINDER agree | 0/9 | improved (handler) | partial |
| TRUE_GRIT agree | 0/10 | handler applied | tested |
| MCTS-50sim WIN | 488/720 (67.8%) | (sweep running) | TBD |
| Tests | 132/132 | 132/132 | 0 regression |

---

## What was done

### A. SWORD_BOOMERANG (RandomEnemy round-robin) — +0.8pp

`SWORD_BOOMERANG` has `TargetType.RandomEnemy` + `Repeat=3`. Real game
picks 3 random alive enemies per hit. Mod sim's `isAoe =
TargetType.AllEnemies` was false → all 3 hits land on `targetIdx` →
block-overflow on shielded targets.

**Fix**: detect `card.Target == TargetType.RandomEnemy`, precompute
`hitsByEnemyIdx[]` via round-robin distribution (`hits / alive` per
enemy + 1 extra for first `hits % alive` enemies). In the per-enemy
attack loop, use `hitsByEnemyIdx[i]` instead of `hitsForDmg`.

Approximation is deterministic. Real game's RNG can give different
distributions per run, but the expected total damage matches.

SWORD_BOOMERANG 0/21 → 8/17 agree.

### B. PileType.Play in snapshotter — +5.6pp (biggest single fix in B project history)

The real bug: in sts2 headless, `HEADBUTT`'s `CardSelectCmd.FromSimpleGrid`
(Choose UI) completes but `OnPlayWrapper` post-OnPlay doesn't transition
HEADBUTT from `PileType.Play` to `PileType.Discard`. HEADBUTT sticks in
the Play pile across the step boundary.

`StateSnapshotter` only enumerated 4 piles: Draw / Discard / Hand /
Exhaust. PileType.Play contents were invisible to mod sim. Result:
`pred.DiscardPile.Count = N+1` (mod added HEADBUTT to discard) vs
`real.DiscardPile.Count = N` (HEADBUTT stuck in Play). Diff +1.

**Diagnostic**: added live pile audit to probe → `HBDIAG2: hand=X
draw=Y dis=Z exh=W play=1` confirmed every HEADBUTT play left
PileType.Play with 1 stuck card across snapshot.

**Fix**: in `StateSnapshotter.Capture`:
```csharp
var playPileRaw = PileType.Play.GetPile(player)?.Cards;
int discardPileSize = (discardPileRaw?.Count ?? 0) + (playPileRaw?.Count ?? 0);
// ... build discardPile list ...
if (playPileRaw != null)
    foreach (var card in playPileRaw)
        discardPile.Add(BuildSimCard(card, requirePlayability: false));
```

Treats Play-stuck cards as discard-equivalent. Mod sim's
post-play `DiscardPile.Count = N+1` now matches real's
`DiscardPile.Count + PlayPile.Count = N+1`.

**Side-benefit**: this fix improved many Choose-from-pile cards
(ARMAMENTS 0→58%, HEADBUTT 0→50%, SHRUG_IT_OFF, SWORD_BOOMERANG and
others — all of which had cards sticking in PlayPile mid-resolution).

`discard_pile_count` field divergence: 11.3% (S3) → **0.6%** (S4) —
massive cleanup.

### C. CINDER + TRUE_GRIT random hand exhaust — +2.8pp

Both cards exhaust a random card from hand on play (in addition to
their main effect). Catalog doesn't have a "random hand exhaust" axis,
so handlers go in card-id form. sts2.dll's `CombatCardSelection.NextItem
(handPile.Cards)` picks an Rng-driven random card; mod sim deterministic
approximation: pop the LAST card from newHand.

**Fix**: in `AnalyticalSimulator.cs` right before the played-card
discard/exhaust branch:
```csharp
if (card.Id == "CINDER" || card.Id == "TRUE_GRIT")
{
    if (newHand.Count > 0)
    {
        newHand.RemoveAt(newHand.Count - 1);
        newExhaustPileCount++;
    }
}
```

CINDER 1/9 → improved. TRUE_GRIT 0/10 → handled (records may not all
agree because the chosen card affects downstream state, but the count
divergence is gone).

---

## Files changed in S4 (UNSTAGED, on top of S1-S3)

### Sts2CombatAI repo

| File | Lines (cumulative S1-S4) | Session 4 changes |
|---|---|---|
| `AnalyticalSimulator.cs` | ~165 | SWORD_BOOMERANG round-robin (~30) + CINDER/TRUE_GRIT handler (~15) |
| `StateSnapshotter.cs` | ~10 | PileType.Play union into DiscardPile (~5 lines) |
| (`StatusMath.cs`, `CardReflection.cs`, `DamageModifiers.cs`) | unchanged from S3 | — |

### sts2-combat-core repo

| File | Session 4 changes |
|---|---|
| (`DirectInvokePump.cs`, `GodotIsolation.cs`, `Sts2CombatCore.csproj`) | unchanged from S2/S3 |

Net: 50 new lines in Sts2CombatAI side. sts2-combat-core untouched in S4.

---

## Suggested commit grouping (S4 additions)

**Commit H**: `fix(sim): SWORD_BOOMERANG — round-robin distribute hits across alive enemies (RandomEnemy AOE)`

**Commit I**: `fix(snapshotter): include PileType.Play in DiscardPile view`
- The biggest single fix in B project history (+5.6pp). PlayPile-stuck cards (HEADBUTT, ARMAMENTS, possibly others) now visible to mod sim. Resolves 11.3% → 0.6% discard divergence systematically.

**Commit J**: `feat(sim): CINDER / TRUE_GRIT random-hand-exhaust handlers`

---

## Field divergence at S4 end (was at S3 end)

| Field | S3 end | **S4 end** | Notes |
|---|---|---|---|
| `enemy_hp_sum` | 25.8% | 23.3% | unchanged — damage calc deeper issues remain |
| `discard_pile_count` | 11.3% | **0.6%** | huge — PlayPile fix |
| `hand_count` | 8.6% | 5.4% | down — CINDER/TRUE_GRIT fixes |
| `player_block` | 7.9% | 7.5% | barely changed |
| `player_strength` | 5.3% | 5.4% | unchanged |
| `exhaust_pile_count` | 5.2% | 2.7% | down — CINDER/TRUE_GRIT fixes |
| `draw_pile_count` | 2.9% | 3.2% | slight uptick (SWORD_BOOMERANG distribution edge cases) |

---

## Open questions for S5

Top remaining 100% (or near-100%) divergers after S4:

1. **SECOND_WIND (13 records, 0%)** — exhausts all non-Attack from hand, +Block per exhaust. Pattern documented in S4 doc but not fixed (block-per-exhaust requires standard self-block branch override). 1-2 hour fix.
2. **BRAND (5 records, 0%)** — `player_strength = +1` mod over-credit. Mod applies BRAND's Strength but real game doesn't (BRAND.OnPlay may throw in headless similar to WHIRLWIND). Investigate Harmony VFX patch.
3. **enemy_hp_sum 23% remains** — broader damage calc issues. Likely candidates:
   - Multi-hit per-hit Strength refresh (WHIRLWIND-class still has residual)
   - DAMAGE_CALC for cards with conditional damage (e.g. BREAKTHROUGH had mixed-sign diffs)
   - Strength STACKING vs per-hit (currently V2 modifier just adds once per hit)
4. **player_block 7.5% remains** — block math for un-modeled powers (RAGE, AFTERIMAGE residuals)

S5 target estimate: +3-5pp parity by fixing SECOND_WIND + BRAND + maybe one more.

---

## Honest assessment (now 4 sessions in)

- Parity: 41.8% → 62.6% (**+20.8pp**) — substantial.
- MCTS WIN: 67.2% → ~67.8% latest, S4 sweep pending — **+0.6pp est**.
- The 20:1 parity-to-WIN ratio persists. Each parity-pp costs an hour or two of careful audit + fix.
- 65% target nearly reached on parity. The Big Lesson: MCTS WIN doesn't move proportionally — leaf bias at 50 sims isn't really binding. Worth running MCTS-200 (or 500) once at S4 to see if deeper search shows the parity gains.

---

## Memory updates

`[[project_b_damage_modifier_architecture]]` — appended S4 section.
