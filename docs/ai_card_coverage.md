# AI Card Coverage Report

- Master catalog: `..\scripts\cards_catalog.json` (game v0.103.2)
- Embedded triggers: `C:\Users\kl95\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Data\card_triggers.json` (v0.103.2)
- PowerCatalog: `C:\Users\kl95\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Planner\PowerCatalog.cs` (69 powers registered)
- Override: `C:\Users\kl95\sts2-card-advisor-dev\Sts2CombatAI\Sts2CombatAICode\Core\Planner\CardOverrideCatalog.cs` (13 cards)

## Headline metrics  (577 base cards)

| Metric | Count | % |
|---|---:|---:|
| Catalog inclusion (in card_triggers.json) | 576 / 577 | 99.8% |
| Axis coverage (`axes[]` non-empty) | 576 / 577 | 99.8% |
| Build participation (`builds[]` non-empty) | 416 / 577 | 72.1% |
| Override bonus applied | 13 / 577 | 2.3% |
| Dropped (no axes/builds/keywords/trigger) | 1 / 577 | 0.2% |

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
