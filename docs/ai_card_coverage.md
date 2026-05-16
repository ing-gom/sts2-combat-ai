# Sts2CombatAI Rule Coverage Report

Coverage of the STS2 card pool by this mod's evaluation rules. Measures
how much of the card pool each rule (PowerCatalog, PowerSequencingTier,
CardOverrideCatalog, BuildSynergy, AmplifierSynergy, EffectSynergy,
HandSynergy) explicitly handles vs falls back to generic defaults.

- Master catalog: `..\scripts\cards_catalog.json` (game v0.103.2)
- Embedded triggers: `C:\Users\dev\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Data\card_triggers.json` (v0.103.2)
- PowerCatalog: `C:\Users\dev\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Planner\PowerCatalog.cs` (69 powers registered)
- PowerSequencingTier: `C:\Users\dev\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Planner\PowerSequencingTier.cs` (55 powers classified)
- Override: `C:\Users\dev\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Planner\CardOverrideCatalog.cs` (13 cards)

## Headline metrics  (577 base cards)

| Metric | Count | % |
|---|---:|---:|
| Catalog inclusion (in card_triggers.json) | 576 / 577 | 99.8% |
| Axis coverage (`axes[]` non-empty) | 576 / 577 | 99.8% |
| Build participation (`builds[]` non-empty) | 416 / 577 | 72.1% |
| Override bonus applied | 13 / 577 | 2.3% |
| Dropped (no axes/builds/keywords/trigger) | 1 / 577 | 0.2% |
| Any synergy-rule participation (≥1 of 5 rules) | 458 / 577 | 79.4% |
| Conditional-damage vars (`Calculated*` / `Extra*` / `Repeat`) | 66 / 577 | 11.4% |
| Self-modifier axes (`EXHAUST/RETAIN/ETHEREAL/INNATE/UNPLAYABLE`) | 143 / 577 | 24.8% |
| SelectorMode trigger (`upgrade_trigger` / `fetch_trigger`) | 64 / 577 | 11.1% |

## PowerCatalog hit rate  (112 Power-type base cards)

Lower bound: a Power card 'hits' the explicit table if any `*Power`-suffix
key in its `vars`, or its id-derived `PascalCasePower` name, appears in
`PowerCatalog.SelfBuff` / `EnemyDebuff`. Cards without a hit fall back to
`HeuristicFallback()` or `DefaultValue = 200`.

| Metric | Count | % |
|---|---:|---:|
| Total Power cards | 112 | 100% |
| Hit via `vars` *Power suffix | 18 | 16.1% |
| Hit via id-derived PascalCasePower | 10 | 8.9% |
| **Any hit (lower bound)** | **28** | **25.0%** |
| Fallback only (HeuristicFallback / Default 200) | 84 | 75.0% |

## PowerSequencingTier coverage  (Power cards only)

Within-turn ordering bonus for Power cards. `Unknown` tier receives 0
ordering bonus — those cards rely on raw PowerCatalog value only.

| Tier | Cards | % |
|---|---:|---:|
| Setup | 11 | 9.8% |
| Scaling | 11 | 9.8% |
| Defensive | 5 | 4.5% |
| Tempo | 0 | 0.0% |
| SelfHarm | 1 | 0.9% |
| Unknown | 84 | 75.0% |
| **Classified (any non-Unknown)** | **28** | **25.0%** |

## Conditional damage / vars patterns  (66 cards)

Cards whose damage / block / hits depend on runtime calculation. PlanScorer
has special handling for these (`CalculatedDamage`, `ExtraDamage` etc.) —
missing the pattern means the card falls back to static stat scoring.

| Category | Sample vars keys | Cards |
|---|---|---:|
| Calculated* | `CalculatedDamage`, `CalculatedBlock`, `CalculatedHits`, … | 42 |
| Calculation base/extra | `CalculationBase`, `CalculationExtra` | 43 |
| Extra* | `ExtraDamage`, `ExtraBlock`, `ExtraCost` | 22 |
| Repeat | `Repeat` | 23 |

## Self-modifier axes  (143 cards have ≥1)

Axes that drive `PlanScorer.PlayOrderBias` and waste-avoidance branches
(Retain defer, Ethereal-now bonus, Exhaust loss, Innate opener, Unplayable
rejection). A card outside this set takes the default play-order path.

| Axis | Cards |
|---|---:|
| EXHAUST_SELF | 97 |
| RETAIN_SELF | 11 |
| ETHEREAL_SELF | 18 |
| INNATE | 9 |
| INNATE_SELF | 9 |
| UNPLAYABLE | 25 |

## SelectorMode triggers  (64 cards)

Cards whose description keywords drive the Burn vs Boost prompt mode in
`SelectorMode`. Cards without any trigger fall back to the default mode
(usually Burn / discard-worst).

| Trigger | Source | Cards |
|---|---|---:|
| `upgrade_trigger` | description contains "강화" | 12 |
| `fetch_trigger` | description contains 가져옴 / 생성 | 59 |

## Synergy-rule reach

How many cards in the pool can trigger each cross-card synergy rule
`PlanScorer` invokes. Counts cards that *can supply* the rule's axis or
power input — a card with `POWER_AMPLIFIER` is a potential `AmplifierSynergy`
activator regardless of whether a target Power happens to be in hand at
runtime.

| Rule | Source | Cards | % |
|---|---|---:|---:|
| BuildSynergy pair (Producer/Amplifier/Consumer) | `*_PRODUCER/_AMPLIFIER/_CONSUMER` axes | 224 | 38.8% |
| BuildSynergy commitment | primary build tag | 370 | 64.1% |
| AmplifierSynergy | `POWER_AMPLIFIER` / `REPLAY` / `ATTACK_REPLAY*` / `SKILL_REPLAY` | 16 | 2.8% |
| EffectSynergy | `DAMAGE/BLOCK/VULN/WEAK_AMPLIFIER`, `BLOCK_PAYOFF`, `HP_LOSS_CONSUMER` | 28 | 4.9% |
| HandSynergy (lower bound) | `vars` keys ∈ {Strength/Dex/Vuln/Weak Power…} | 40 | 6.9% |

## Pair-axis stem completeness  (28 stems)

A stem `X` triggers `BuildSynergy.Compute()` pair bonuses only when there
is at least one `X_PRODUCER` AND at least one `X_AMPLIFIER` or `X_CONSUMER`
card. Stems missing a side are dead pairs in the catalog.

- **Complete stems** (P ≥ 1 AND (A ≥ 1 OR C ≥ 1)): **11 / 28**
- **Orphan stems** (missing one side): **17**

| Stem | Producer | Amplifier | Consumer | Status |
|---|---:|---:|---:|---|
| BLOCK | 0 | 7 | 0 | no producer |
| CUNNING | 10 | 0 | 10 | complete |
| DAMAGE | 0 | 8 | 0 | no producer |
| DARK_ORB | 0 | 1 | 0 | no producer |
| DEFEND_TYPE | 0 | 1 | 0 | no producer |
| DOOM | 9 | 1 | 3 | complete |
| DOOM_SELF | 1 | 0 | 0 | producer-only |
| DRAW | 0 | 1 | 0 | no producer |
| ENERGY | 5 | 0 | 0 | producer-only |
| EXHAUST | 17 | 0 | 5 | complete |
| FORGE | 11 | 2 | 0 | complete |
| HP_LOSS | 0 | 0 | 3 | no producer |
| LORDS_BLADE | 0 | 1 | 0 | no producer |
| ORB | 35 | 2 | 1 | complete |
| ORB_VARIETY | 0 | 3 | 0 | no producer |
| POISON | 9 | 1 | 2 | complete |
| POWER | 0 | 3 | 0 | no producer |
| SHIV | 4 | 2 | 1 | complete |
| SKELETON | 12 | 0 | 5 | complete |
| SKELETON_ATTACK | 0 | 1 | 0 | no producer |
| SOUL | 11 | 0 | 3 | complete |
| STAR | 23 | 0 | 1 | complete |
| STATUS | 0 | 0 | 3 | no producer |
| STRENGTH | 13 | 0 | 0 | producer-only |
| STRIKE | 0 | 0 | 2 | no producer |
| VOLATILE | 4 | 0 | 5 | complete |
| VULN | 0 | 7 | 0 | no producer |
| WEAK | 0 | 2 | 0 | no producer |

## Synergy participation degree

Per-card count of synergy rules the card *can* feed (out of 5):
`BuildSynergy pair`, `BuildSynergy commitment`, `AmplifierSynergy`,
`EffectSynergy`, `HandSynergy` (vars-based lower bound).

| Degree | Cards | % | Interpretation |
|---:|---:|---:|---|
| 0 | 119 | 20.6% | no synergy hooks — evaluated as a standalone card |
| 1 | 272 | 47.1% | single-rule (mostly build or pair) |
| 2 | 154 | 26.7% | two-rule (build + pair, or pair + effect…) |
| 3 | 30 | 5.2% | three-rule (high synergy density) |
| 4 | 2 | 0.3% | four-rule (very dense) |
| 5 | 0 | 0.0% | all five |

## Per-character coverage

| Character | Cards | In triggers | Axes | Builds | Power hit | Dropped |
|---|---:|---:|---:|---:|---:|---:|
| DEFECT | 88 | 88 (100.0%) | 88 (100.0%) | 53 (60.2%) | 7/20 (35.0%) | 0 (0.0%) |
| IRONCLAD | 87 | 87 (100.0%) | 87 (100.0%) | 65 (74.7%) | 7/21 (33.3%) | 0 (0.0%) |
| NECROBINDER | 88 | 88 (100.0%) | 88 (100.0%) | 73 (83.0%) | 3/18 (16.7%) | 0 (0.0%) |
| REGENT | 88 | 88 (100.0%) | 88 (100.0%) | 73 (83.0%) | 0/19 (0.0%) | 0 (0.0%) |
| SHARED | 138 | 137 (99.3%) | 137 (99.3%) | 83 (60.1%) | 5/15 (33.3%) | 1 (0.7%) |
| SILENT | 88 | 88 (100.0%) | 88 (100.0%) | 69 (78.4%) | 6/19 (31.6%) | 0 (0.0%) |

## Per-build participation (from embedded triggers)

| Build tag | Cards |
|---|---:|
| 소멸 빌드 | 123 |
| 압축덱 | 120 |
| 방어 빌드 | 101 |
| 성장 빌드 | 72 |
| 드로우 빌드 | 65 |
| 광역 빌드 | 52 |
| 골골이 빌드 | 27 |
| 별 빌드 | 24 |
| 교활 빌드 | 20 |
| 독 빌드 | 16 |
| 자해 빌드 | 14 |
| 제작 빌드 | 12 |
| 종말 빌드 | 12 |
| 단도 빌드 | 11 |

## Top axes (embedded)

| Axis | Cards |
|---|---:|
| DAMAGE | 200 |
| EXHAUST_TAG | 122 |
| BLOCK | 101 |
| EXHAUST_SELF | 97 |
| SCALING | 70 |
| DEBUFF | 56 |
| DRAW | 55 |
| AOE_OTHER | 52 |
| RANDOM | 48 |
| ENERGY | 39 |
| DURATION | 37 |
| AOE_DAMAGE | 37 |
| AOE | 35 |
| ORB_PRODUCER | 35 |
| FREE_ATTACK | 32 |
| VULN | 28 |
| UNPLAYABLE | 25 |
| STRIKE_TYPE | 23 |
| STAR_PRODUCER | 23 |
| REPEAT | 23 |
| WEAK | 21 |
| UNLIMITED | 18 |
| ETHEREAL_SELF | 18 |
| EXHAUST_PRODUCER | 17 |
| POISON | 16 |
| CARD_GEN | 16 |
| MINION | 14 |
| STAR | 14 |
| STRENGTH_PRODUCER | 13 |
| OSTY | 13 |

## Dropped cards  (1 total, top 20)

| Id | Character | Tier | Type |
|---|---|---|---|
| CARD.FRANTIC_ESCAPE | SHARED |  | Status |

## Power cards without explicit PowerCatalog hit  (84 total, top 20)

These rely on `HeuristicFallback()` or `DefaultValue = 200`.

| Id | Character | Tier | vars keys |
|---|---|---|---|
| CARD.ACCELERANT | SILENT | B | Accelerant |
| CARD.AGGRESSION | IRONCLAD | B | — |
| CARD.ARSENAL | REGENT | C | ArsenalPower |
| CARD.AUTOMATION | SHARED | A | Energy |
| CARD.BLACK_HOLE | REGENT | B | BlackHolePower |
| CARD.CALAMITY | SHARED | D | — |
| CARD.CALCIFY | NECROBINDER | C | CalcifyPower |
| CARD.CALL_OF_THE_VOID | NECROBINDER | S | Cards |
| CARD.CAPACITOR | DEFECT | B | Repeat |
| CARD.CHILD_OF_THE_STARS | REGENT | S | BlockForStars |
| CARD.CONSUMING_SHADOW | DEFECT | D | ConsumingShadowPower, Repeat |
| CARD.COOLANT | DEFECT | A | CoolantPower |
| CARD.COUNTDOWN | NECROBINDER | A | CountdownPower |
| CARD.CREATIVE_AI | DEFECT | B | CreativeAi |
| CARD.CRIMSON_MANTLE | IRONCLAD | A | CrimsonMantlePower |
| CARD.CRUELTY | IRONCLAD | S | CrueltyPower |
| CARD.DARK_EMBRACE | IRONCLAD | A | — |
| CARD.DEMESNE | NECROBINDER | S | Cards, Energy |
| CARD.DEVOUR_LIFE | NECROBINDER | A | DevourLifePower |
| CARD.DRUM_OF_BATTLE | IRONCLAD | C | Cards, DrumOfBattlePower |

## Cards with no axes  (1 total, top 20)

| Id | Character | Tier | Type |
|---|---|---|---|
| CARD.FRANTIC_ESCAPE | SHARED |  | Status |

## Limitations

- **Static only.** Runtime simulator / DecisionLog data not included.
- **PowerCatalog hit is a lower bound.** A card's `vars` does not always
  list every power it applies (e.g. `BARRICADE` has empty `vars` but does
  apply `BarricadePower`). The id-derived fallback catches the common case
  (`CARD.X_Y → XYPower`) but misses cards whose power name diverges from
  the card id (e.g. `CARD.ABRASIVE → DexterityPower + ThornsPower`).
- **Override list is sparse by design.** Low % here is expected, not a bug;
  the metric is for absolute count tracking across releases.
- **Synergy reach is a *potential* count, not realized activation.** A card
  with `POWER_AMPLIFIER` only earns the bonus if a target Power is in hand
  at the moment of scoring; runtime activation depends on hand composition,
  draw order, and remaining energy. Static reach is the upper bound.
- **HandSynergy reach is a lower bound (vars-based).** Cards that apply
  Strength/Dex/Vuln/Weak through descriptions without exposing the power
  name in `vars` are missed. Hits via `card.PowerApps` at runtime would be
  higher.
- **PowerSequencingTier hit shares the lower-bound caveat.** Same vars +
  id-derived matching as PowerCatalog — a Power card whose applied power
  name is not in `vars` or in the id-derived form will be reported as
  `Unknown` even when the power IS registered in the tier map.
- **Target distribution and Orb ChannelCount-based reach are not measured**
  because the catalog exposes neither `target` nor `ChannelCount`. Those
  paths can only be audited via runtime reflection.
