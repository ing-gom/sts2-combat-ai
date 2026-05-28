# B-Architecture Session 3 Summary — 2026-05-30

Continuation session from 2026-05-29 (session 2 ended at 48.0% parity).
Same pattern: surgical fixes to remaining 100%-diverging cards based on
parity-probe pattern analysis.

---

## TL;DR

| Metric | Session 2 end | **Session 3 end** | Δ |
|---|---|---|---|
| Sim parity (50 ep Ironclad) | 48.0% (320/666) | **53.4% (350/656)** | **+5.4pp** |
| Cumulative since S0 baseline (41.8%) | +6.2pp | **+11.6pp** | huge |
| POMMEL_STRIKE agree | 0/13 | 8/11 (73%) | +73pp |
| SHRUG_IT_OFF agree | 0/10 | 5/10 (50%) | +50pp |
| SETUP_STRIKE agree | 0/18 | 13/19 (68%) | +68pp |
| VICIOUS agree | 0/7 | 3/4 (75%) | +75pp |
| MCTS-50sim WIN | 486/720 (67.5%) | **488/720 (67.8%)** | +0.3pp / +2 WIN |
| Tests | 132/132 | 132/132 | 0 regression |

Cumulative since S0 baseline: **parity +11.6pp / MCTS WIN +0.6pp (+4 games)**.

---

## What was done

### A. DrawPile list mutation — +3.3pp (biggest of session 3)

Mod sim's `ApplyCardPlay` updated `DrawPileSize` (int) but left
`DrawPile` (list) at pre-play state. Same bug shape as the DiscardPile
issue fixed in commit `eba2a99`, just for the draw pile.

**Symptom**: POMMEL_STRIKE / SHRUG_IT_OFF / ANGER copy etc. consistently
showed `draw_pile_count = +1` (mod over-credits). 13+10+(some ANGER) = 25+
records.

**Fix**: in `AnalyticalSimulator.cs:88`-ish:
```csharp
var newDrawPile = new List<SimCard>(next.DrawPile ?? new List<SimCard>());
```
Then at the draw loop (lines 735-750), `RemoveAt(end)` per drawn card +
reshuffle mirror (`newDrawPile.AddRange(newDiscardPile); newDiscardPile.Clear();`)
when draw pile empties. Wire `DrawPile = newDrawPile` into the return.

POMMEL_STRIKE 0/13 → 8/11 agree. SHRUG_IT_OFF 0/10 → 5/10. ANGER also
recovered (from 7→9 agree).

### B. SETUP_STRIKE-class: self-power-on-attack — +1.3pp

The attack PowerApps loop only applies enemy-debuff entries
(`if (!IsEnemyDebuff(powerName)) continue;`). Self-buff entries
(`StrengthPower:2` on SETUP_STRIKE, similar for INFLAME-style attacks)
were silently dropped.

**Fix**: after the enemy loop, run a second PowerApps pass for the
non-enemy-debuff entries and apply them to self via the existing
`AddPlayerPower(...)` helper + explicit `newPlayerStr / newPlayerDex /
newPlayerVigor` updates. Mirrors sts2.dll's SetupStrike OnPlay: damage
first, then `PowerCmd.Apply<SetupStrikePower>` to self.

SETUP_STRIKE 0/18 → 13/19 agree.

### C. VICIOUS — Power-card CardsVar false-positive draw — +0.8pp

`CardReflection.GetEffectSummary` routes ANY `CardsVar(N)` to
`DrawCount += N`. But Power cards use CardsVar to express the power's
magnitude (VICIOUS uses `CardsVar(1)` → `PowerCmd.Apply<ViciousPower>(1)`).
Mod sim was erroneously drawing 1 card on every Power-card play with a
CardsVar (only VICIOUS in the Ironclad pool, but the pattern would
affect other characters' Power cards too).

**Fix** at `AnalyticalSimulator.cs:735`-ish: add `!card.IsPower` gate to
the draw mechanic.

VICIOUS 0/7 → 3/4 agree (sample size dropped because VICIOUS was
overused as the planner picked it — fix reduces over-pick).

---

## Files changed (UNSTAGED)

### Sts2CombatAI repo

Same 3 files as Session 2, additional lines:
- `AnalyticalSimulator.cs` — newDrawPile + draw mutation + SETUP_STRIKE PowerApps + VICIOUS gate (~50 new lines)
- (`CardReflection.cs`, `StatusMath.cs` — unchanged from session 2)

### sts2-combat-core repo

No changes in session 3 — all fixes are in Sts2CombatAI side.

---

## Suggested commit grouping (session 3 — appended to S1+S2 list)

**Commit E**: `fix(sim): DrawPile list mutation + reshuffle mirror`
- newDrawPile init, RemoveAt during draw loop, reshuffle list mirror.
- Largest single S3 lift (+3.3pp parity, +8 POMMEL_STRIKE agree).

**Commit F**: `feat(sim): attack self-power application (SETUP_STRIKE)`
- After-enemy-loop pass that applies non-enemy-debuff PowerApps to self.

**Commit G**: `fix(sim): gate draw mechanic against Power cards (VICIOUS)`
- One-line `!card.IsPower` addition on the DrawCount branch.

---

## Field divergence breakdown at S3 end

| Field | % records non-zero | Direction |
|---|---|---|
| `enemy_hp_sum` | 25.8% | mostly mod under (107) — SWORD_BOOMERANG class |
| `discard_pile_count` | 11.3% | mostly mod over — HEADBUTT discard→draw |
| `player_block` | 7.9% | mod under |
| `hand_count` | 7.6% | mod over — CINDER/TRUE_GRIT random hand-exhaust |
| `player_strength` | 5.3% | mixed — BRAND remaining |
| `exhaust_pile_count` | 5.2% | mod under — self-exhaust mechanics |
| `draw_pile_count` | **2.9%** (was 5.8% pre-S3) | most remaining cases edge-case (over/under) |

---

## Open questions for session 4

Remaining 100%-diverging cards in rough impact order:

1. **SWORD_BOOMERANG (21 records, 0%)** — random AOE 3 hits distribution. Mod's AOE iterates all alive enemies × hits, but real game picks 3 random targets (replacement). Needs probabilistic distribution model.
2. **BREAKTHROUGH partial (3/15 only)** — AOE attack + HpLoss. HP loss now applies (S2 fix) but enemy_hp_sum mixed sign suggests AOE damage calc off (per-hit Strength refresh question from S1).
3. **HEADBUTT (13 records, 0%)** — discard→draw move. Need card-specific handler: after damage, simulate `newDiscardPile.RemoveAt(end); newDrawPile.Add(removed);`.
4. **CINDER (9 records, 0%) + TRUE_GRIT (10 records, 0%) + SECOND_WIND** — random hand exhaust pattern. Need: after card resolve, exhaust 1 random card from hand. Could be a `RANDOM_HAND_EXHAUST` axis-based handler.
5. **BRAND (5 records, 0%)** — self-exhaust + Strength + HP loss. Catalog has EXHAUST_PRODUCER axis but mod sim doesn't auto-exhaust based on axis.
6. **ARMAMENTS (11 records, 0%)** — Choose timing UI / Choose-from-hand state divergence.

Realistic next-session goal: pick 3 of the above (SWORD_BOOMERANG + HEADBUTT + one of CINDER/TRUE_GRIT). Estimated +5-8pp parity, +0-3 MCTS WIN.

---

## Honest assessment (continued)

Session 3 mirrors session 2's parity-vs-WIN pattern: **+5.4pp parity → +0.3pp WIN**. Three sessions cumulative: **+11.6pp parity / +0.6pp WIN (+4 games)**.

The 20:1 parity-to-WIN ratio across all three sessions tells us:
- MCTS-50sim at this rollout depth is **not yet leaf-bound** — the fixes refine simulation accuracy in ways that don't change critical decisions.
- The high-WIN-impact fixes are probably the cards we haven't addressed yet — SWORD_BOOMERANG (AOE damage), HEADBUTT (deck cycling), CINDER (random exhaust) — these change WHICH cards survive and WHAT order they're played.
- Worth running MCTS-200sim once after session 4's fixes to see if deeper search starts seeing the gains.

The architecture's pay-off pattern is real but slow. Parity floor at ~80% (per MCTSPlanner.cs comment) is still 27pp away, ~5 more sessions at current pace.

---

## Memory updates (persisted)

- `[[project_b_damage_modifier_architecture]]` — appended S3 section, cumulative tracker updated. Remaining-work list refined with S4 priorities.
