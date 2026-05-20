# Sts2CombatAI

**A general-purpose combat-decision AI core for Slay the Spire 2.**
A planner + simulator + scorer bundle that evaluates card/target sequences and
picks the one that minimizes expected HP loss.

The core itself is trigger-agnostic — whichever *mode* invokes it (and whenever),
the same decision logic runs. One mode ships today: **Vakuu**, which replaces
the Whispering Earring relic's vanilla auto-play with the core AI.

## Architecture

```
Sts2CombatAICode/
├── MainFile.cs                — Harmony entrypoint, mode wiring
├── Core/                      — mode-agnostic decision engine
│   ├── Planner/               (ActionPlanner, PlanScorer, Playstyle, ...)
│   ├── Sim/                   (SimState, AnalyticalSimulator, StateSnapshotter, ...)
│   ├── Reflection/            (CardReflection, CombatReflection, ...)
│   ├── Data/                  (CardCatalog + embedded card_triggers.json)
│   ├── Diagnostics/           (DecisionLog ring buffer + NDJSON persister)
│   └── Runtime/               (PlaystylePersistence)
└── Modes/
    └── Vakuu/                 — Vakuu mode: runtime driver that calls Core
        ├── WhisperingEarringPlannerPatch.cs
        ├── VakuuExecutor.cs
        ├── VakuuCardSelectorPatches.cs
        ├── VakuuTestButtonPatch.cs
        └── TestButtonPoller.cs
```

To add a new mode: drop `Modes/<NewMode>/` with a trigger Harmony patch and an
executor, then call `Core`'s `ActionPlanner.PlanNextStep(snapshot)`. Core stays
untouched.

## Core — the decision engine

### Card recognition (100% coverage across all characters)
- **576-card catalog** read automatically (`Core/Data/card_triggers.json`, embedded)
- **14 build archetypes** auto-classified (poison / AOE / scaling / exhaust /
  HP-loss / star / ...)
- **17 enemy intent** categories (Attack / Buff / Heal / Summon / DeathBlow /
  Defend / Debuff / Stun / ...)
- **65+ Power priority catalog** (EchoForm / Barricade / Strength / Vulnerable /
  Poison ...)
- **Zero hardcoded card IDs** — only the catalog needs to be re-extracted after
  a game patch.

### What the scorer considers
- **Exact card values**: Damage / Block / Hits / PowerApps via DynamicVars +
  PreviewValue (multiplier-aware).
- **Status modifiers**: `(base + Strength) × Vulnerable × Weak` for damage,
  `(base + Dex) × Frail` for block.
- **Enemy state awareness**: Vulnerable / Strength / Frail / Artifact / Ritual /
  Poison stacks → differential target priority.
- **Per-hit / per-turn caps**: IntangiblePower (= 1) / HardToKill /
  HardenedShellRemaining damage clamps (single-target and AOE alike).
- **Energy-waste avoidance**: damage ≤ target.Block → penalty.
- **Energy gain cards**: prioritized only when short on energy (Adrenaline combo
  recognized, EnergyNextTurnPower handled separately).
- **Draw cards**: prioritized when the best hand score is low ("transfusion"
  value).
- **Build synergy**: Producer + Amplifier/Consumer pairing + count of same-build
  cards in hand.
- **Defect orb routing**: Producer/Consumer differentiated by slot
  fill/empty state, exact channel-into-full kick counting, and **Focus** applied
  to every evoke/passive.
- **Threat estimation**: player Vulnerable (×1.5) + enemy Weak (×0.75) +
  poison-lethal enemies excluded.
- **Play-order bias**: Retain → defer after other plays. Ethereal → avoid
  turn-end exhaust. 0-cost → bypass MinPlayScore floor.
- **Forward simulator**: simulates card plays (EnergyGain / DrawCount / Damage /
  Block / Power application + Intangible/Shell cap + debuff propagation +
  Artifact absorption + discard pile growth).
- **Depth-2 lookahead**: after the first card, simulates the best second card
  and scores the pair; tiebreak favors the first-card score.

### Four Playstyles (persisted)
A **Style** button next to End Turn cycles through:
- **Defensive** — block 1500, threat threshold 0.2, attacks weakened
- **Balanced** — default
- **Aggressive** — attack +350, block weakened, threshold 0.55
- **Killer** — block 0, lethal range 6000, attack dominates

The selected style is saved to `{user_data}/Sts2CombatAI/playstyle.json` and
restored across game restarts.

## Mode: Vakuu

The Whispering Earring (Ancient relic) effect — *"Vakuu plays your first turn
for you"*. The vanilla behavior, confirmed via
[decompile](../research/baku_decompile/WhisperingEarring.cs):

```csharp
CardModel card = pile.Cards.FirstOrDefault(c => c.CanPlay());
```

→ it just plays **the first playable card** in hand, up to 13 times. No strategy
at all.

This mode hijacks that hook (`WhisperingEarring.BeforePlayPhaseStartLate`) and
delegates to the Core AI instead. Components:
- `WhisperingEarringPlannerPatch` — Harmony Prefix that intercepts the game's
  Vakuu trigger and forwards to `VakuuExecutor`
- `VakuuExecutor` — 13-step loop: snapshot → Core planner → AutoPlay → repeat
- `VakuuCardSelectorPatches` — answers Vakuu's mid-play card prompts (discard /
  exhaust / upgrade) using the Core scorer
- `VakuuTestButtonPatch` + `TestButtonPoller` — a **Vakuu Play** debug button
  next to End Turn (lets you invoke the AI every turn, no relic required)

## Installation

```
SlayTheSpire2/mods/Sts2CombatAI/
├── Sts2CombatAI.dll
└── Sts2CombatAI.json
```

Enable the mod from the in-game Mods menu.

## Logs

Game log location: `%APPDATA%\Godot\app_userdata\SlayTheSpire2\logs\` — newest
`.log` file.

Lines prefixed with `[CombatAI]` print the decision plus the score breakdown
for every step:

```
[CombatAI] starting plan (style=Balanced)
[CombatAI] step 1 snapshot: player[hp=80 block=0 energy=3]
  hand=[Strike(A1/d6),Inflame(P1/Stre:2),...] enemies=[Acolyte(...)]
[CombatAI] step 1 → CARD.INFLAME@self (score=2207 reason=power(StrengthPower:2))
[CombatAI]   breakdown: Power base=1007 effect=1200 target=0 threat=0
              [powerBase=1000,Stre(2)=1200,buildSyn=160,energyCtx=200]
[CombatAI] turn complete, 3 cards played, took 24ms total
```

For deeper offline analysis, every combat is also dumped as NDJSON to
`{user_data}/Sts2CombatAI/decision_log/`. Parse it with
`scripts/parse_decision_log.py`.

## Configuration (v0.10+)

All planner-tuning knobs are externalized to JSON under
`{user_data}/Sts2CombatAI/scoring_weights/`. First mod launch writes defaults;
user or AI edits override at next launch. Missing files / missing fields fall
back to code defaults — partial JSON is safe.

```
{user_data}/Sts2CombatAI/scoring_weights/
├── balanced.json            70+ dials for the Balanced playstyle
├── defensive.json           70+ dials for Defensive
├── aggressive.json          70+ dials for Aggressive
├── killer.json              70+ dials for Killer
├── power_catalog.json       ~164 Power value lookups (self-buff + enemy-debuff)
├── power_sequencing.json    Power → tier (Setup/Scaling/Defensive/Tempo/SelfHarm)
└── planner_config.json      depth/beam/MC params + training-data toggle
```

### What each file controls

- **`{preset}.json`** — every scoring magnitude visible in the breakdown log
  (PowerCardBonus, LethalRangeBonuses, BlockUnderThreatBonus, BurstDamage
  ratios, WastedBlockPenalty, MinPlayScore, thorns / galvanic / 0-cost-power
  bonuses, draw-trigger bonuses, the HP-fraction multiplier cascade, etc.).
- **`power_catalog.json`** — per-power score (SelfBuff section for player-side
  application, EnemyDebuff for enemy-side) + `default_value` fallback for
  unknown powers.
- **`power_sequencing.json`** — within-turn play order for multi-Power hands.
  Setup (apply first — Strength, Vulnerable, Vigor) > Scaling (long-fight
  permanents — DemonForm, EchoForm) > Defensive (block / mitigation) > Tempo
  (energy / draw) > SelfHarm (avoid).
- **`planner_config.json`** — algorithm-level: `next_turn_discount`,
  `monte_carlo_samples`, `beam_k` (depth-N search width),
  `training_data_enabled`.

### Training-data mode

Set `"training_data_enabled": true` in `planner_config.json` to enable dense
per-step recording for offline AI tuning workflows. Output:
`{user_data}/Sts2CombatAI/training_data/{timestamp}_F{floor}_{char}_{id}.ndjson`.

Each NDJSON line is one decision step containing:
- snapshot summary
- the chosen `(card_id, target_idx)` flagged
- a `candidates` array — EVERY `(card, target)` the planner considered,
  with full `PlanScorer.Breakdown` (total + base + effect + target_bonus +
  threat_bonus + raw details string)

Lets an offline tuner map "JSON dial change → decision change" without
re-running the planner: the same snapshot scored under a new weight set gives
the candidate score that would have won.

Significant perf cost (~5-10 candidates × ~3-5 alive enemies = 15-50 extra
breakdown calls per step) — keep disabled in normal play.

## Build (developers)

```bash
dotnet build
```

This copies `Sts2CombatAI.dll` + `Sts2CombatAI.json` to
`{STS2 install}/mods/Sts2CombatAI/` automatically.

## Running tests

The test project lives at the repo root and is still named `Sts2VakuuPlus.Tests`
for historical reasons (the mod was renamed; the test runner was not).

```bash
dotnet run --project ../Sts2VakuuPlus.Tests
```

72 unit tests covering the decision rules — a regression net for every scoring
change.

## Refreshing the catalog (after a game patch)

```bash
# 1. Regenerate cards_catalog.json via Sts2CardAdvisor's headless-sync
# 2. Extract this mod's small catalog
python scripts/extract_card_triggers.py
# 3. Rebuild
dotnet build
```

## Execution flow (Vakuu mode)

```
Whispering Earring trigger -OR- the Vakuu Play debug button
       ↓
Modes/Vakuu/VakuuExecutor.RunPlannedTurn  ← loops up to 13 steps
       ↓
Core/Sim/StateSnapshotter.Capture (Live → SimState)
       ├─ Player HP / Block / Energy / Strength / Dex / Stars
       ├─ Hand (cards via CardReflection.GetEffectSummary — DynamicVars + PreviewValue)
       ├─ Enemies (HP / Block / Vulnerable / Strength / Weak / Frail / Artifact / Poison / ...)
       ├─ Pile sizes (Draw / Discard)
       └─ Orb slots (Defect only)
       ↓
Core/Planner/ActionPlanner.PlanNextStep (depth-2 lookahead)
       ├─ EnumerateCandidates (CanPlay + energy budget)
       ├─ for each candidate: PlanScorer.Score
       ├─ AnalyticalSimulator.ApplyCardPlay → next state
       └─ best second card → first + second total
       ↓
Core/Planner/PlanScorer (145+ rules)
       ├─ Card type baseline
       ├─ PowerCatalog (65+ self/enemy split + stack curve)
       ├─ Modifier-aware damage (Strength × Vulnerable × Weak)
       ├─ Target priority (Boss / Minion / Lethal / Intent / Buff state)
       ├─ Build synergy (Producer + Amplifier/Consumer)
       ├─ Hand synergy + Card override catalog
       ├─ Waste avoidance + Energy / Draw / Power context
       └─ Smart selector mode (Burn / Boost)
       ↓
CardCmd.AutoPlay (the game engine actually plays the card)
       ↓
Core/Diagnostics/DecisionLog.Record (ring buffer, 32 entries)
```

## License

MIT
