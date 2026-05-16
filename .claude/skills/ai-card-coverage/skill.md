---
name: ai-card-coverage
description: Sts2CombatAI 가 STS2 카드 풀을 명시 규칙으로 얼마나 평가하는지 (단독 평가 path + 카드 간 synergy 룰 reach) 정적 측정. 신규 카드 추가, PowerCatalog/CardOverrideCatalog/BuildSynergy/AmplifierSynergy/EffectSynergy/HandSynergy 변경, 신규 axis/archetype 추가, 릴리즈 전 정기 audit 시 호출. 단독 metric 6개 + 시너지 metric 4개 (synergy reach / pair-stem completeness / participation degree) + 미커버 카드 / orphan stem 상세.
---

# AI Card Coverage Audit

`PlanScorer` 가 카드를 평가할 때 거치는 4 경로 (PowerCatalog / CardOverrideCatalog / BuildSynergy / generic fallback) 가 카드 풀의 몇 %를 명시적으로 다루는지 측정. fallback 의존 카드를 식별해 다음 release 의 hand-tuning 우선순위 산출.

## 호출 트리거

- 신규 카드 추가 후 (게임 패치 후 `version-bump` 후속)
- `PowerCatalog.cs` 에 새 power 등록 / 기존 power 삭제 후
- `CardOverrideCatalog.cs` 수정 후
- 신규 axis / archetype 도입 (BuildSynergy 영향) 후
- 릴리즈 전 정기 audit
- 사용자 보고 "특정 카드가 점수 낮게 나옴 / AI 가 안 씀"

## 측정 Metric

10 개 정적 지표 — 단독 평가 (6) + 카드 간 synergy (4). 모두 카탈로그 + 소스 파일만 보고 계산 (런타임 로그 X).

### 단독 카드 평가 path

| Metric | 정의 | 의미 |
|---|---|---|
| **Catalog inclusion** | `card_triggers.json` 에 entry 존재 / base 카드 수 | mod 가 인지하는 카드 비율 |
| **Axis coverage** | `axes[]` 비어있지 않은 base 카드 비율 | `BuildSynergy.Compute()` 가 작동하는 카드 |
| **Build participation** | `builds[]` 비어있지 않은 비율 | archetype 매핑된 비율 |
| **PowerCatalog hit** (Power 한정) | PowerName 이 `SelfBuff`/`EnemyDebuff` dict 에 명시 등록 (lower bound) | Power 카드 명시 평가율 |
| **Override 활용도** | `CardOverrideCatalog._bonuses` 매칭 (절대 수) | hand-tune 카드 트래킹 |
| **Dropped** | axes/builds/keywords/trigger 모두 부재 | generic fallback 전적 의존 |

### 카드 간 Synergy 룰 reach

| Metric | 정의 | 의미 |
|---|---|---|
| **Synergy participation** | 5 synergy 룰 (pair / build commit / amp / effect / hand) 중 1+ 에 등장하는 카드 비율 | "단독이 아닌" 카드 비율 |
| **Synergy-rule reach** | 각 룰별 잠재 활성 카드 수 (`BuildSynergy pair / commitment / AmplifierSynergy / EffectSynergy / HandSynergy`) | 룰의 카드 풀 적용 범위 |
| **Pair-axis stem completeness** | `*_PRODUCER` × (`*_AMPLIFIER` ∪ `*_CONSUMER`) 양쪽 카드 존재하는 stem 수 / 전체 stem | dead-pair stem 식별 |
| **Synergy participation degree** | 카드별 매칭 룰 수 분포 (0/1/2/3+) | 0 = synergy 무관 단독 카드 |

### 분포 보조

| Metric | 정의 |
|---|---|
| **Per-character 분포** | Ironclad/Silent/Defect/Watcher/Necrobinder/Regent 별 위 metric |
| **Per-build 분포** | 빌드 태그별 카드 수 |

## 초기 Threshold (calibration 전, 첫 실행 후 갱신)

| Metric | Good | Acceptable | Poor |
|---|---|---|---|
| Catalog inclusion | ≥ 99% | 95~99% | < 95% |
| Axis coverage | ≥ 90% | 80~89% | < 80% |
| PowerCatalog hit | ≥ 70% | 50~69% | < 50% |
| Dropped | ≤ 2% | 2~5% | > 5% |
| Synergy participation | ≥ 80% | 65~79% | < 65% |
| Pair-axis stem completeness | ≥ 70% | 50~69% | < 50% |

threshold 미달 시 우선순위:
1. **Dropped > 5%**: `extract_card_triggers.py` 의 신호 풀 (axes / builds / keywords / description keyword) 확장 검토
2. **PowerCatalog hit < 50%**: 누락 Power 이름 상위 N 개를 `PowerCatalog.SelfBuff` 에 명시 등록
3. **Axis coverage < 80%**: `cards_catalog.json` 의 axis 매핑 (sts2-card-advisor 의 `card_axis_overrides.json`) 확장 — 상위 repo 의 axis-tagger 스킬로 작업
4. **Pair-axis stem orphan 다수**: orphan stem 표를 보고 → `producer-only` 는 amp/cons 추가 또는 producer suffix 제거, `no producer` 는 axis convention 통일 (`VULN` vs `VULN_PRODUCER`) 검토
5. **Synergy participation < 65%**: AmplifierSynergy / EffectSynergy / HandSynergy 룰을 더 많은 카드에 hook 할 axis 확장 검토
6. **per-character 분포 편차 > 15%p**: 약한 캐릭터에 character-specific override 우선

## 핵심 원칙

### 1. Lower bound 인식

PowerCatalog hit 은 **lower bound**. 카탈로그 `vars` 가 카드가 실제 적용하는 모든 power 를 노출하지는 않음 (예: `CARD.BARRICADE` 의 `vars` 가 비어있지만 실제로는 `BarricadePower` 부여). id-derived `PascalCasePower` fallback 으로 일부 보완하지만, 카드 이름과 power 이름이 다른 경우 (예: `CARD.ABRASIVE → DexterityPower + ThornsPower`) 는 catch 못 함. 실제 hit 율은 보고된 수치보다 높음.

HandSynergy reach 도 동일 한계 — vars 기반 lower bound (BARRICADE 처럼 vars 비어있는 카드는 누락).

### Synergy 는 *잠재* 활성 수, 실제 활성 X

Synergy-rule reach 는 "이 카드가 그 룰을 *공급할 자격*이 있는 카드 수" — 런타임에 실제 활성화되려면 partner 카드가 같은 hand 에 들어와야 함. **상한선 측정**. 실제 평균 activation 은 hand draw 통계에 의존하며 정적 분석으론 알 수 없음.

### 2. Override 절대 수 (비율 아님)

`CardOverrideCatalog` 는 **sparse by design** — 알고리즘이 일관되게 under/over-value 하는 카드만 추가 (~20 개 cap). 비율 metric 대신 **절대 수의 추세** 로 트래킹. 50 개를 넘기면 알고리즘 자체 개선이 필요한 신호.

### 3. Dropped 카드 = root cause 분석 대상

dropped 카드 (4 신호 모두 부재) 는 PlanScorer 에서 type-based 평가 (Attack damage / Skill block) 로만 처리됨. axis 시너지 / build 페이오프 / override 보너스 모두 0. **release 전 dropped 0 이 목표**.

## 작업 흐름

### Step 1 — 입력 파일 최신 상태 확인

`card_triggers.json` 이 master `cards_catalog.json` 과 동기화되었는지:

```bash
python scripts/extract_card_triggers.py
```

마지막 `extract_*` 이후 `cards_catalog.json` 갱신이 있었으면 먼저 재추출.

### Step 2 — 측정 실행

리포트 stdout + 파일 동시:

```bash
python scripts/measure_ai_card_coverage.py --out docs/ai_card_coverage.md
```

부모 repo (`sts2-card-advisor-dev`) 의 master catalog 를 외부에서 참조:

```bash
python scripts/measure_ai_card_coverage.py \
    --catalog ../scripts/cards_catalog.json \
    --out docs/ai_card_coverage.md
```

### Step 3 — Threshold 비교

리포트 headline 4 줄을 위 Threshold 표와 대조. Poor 구간 metric 이 있으면 Step 4.

### Step 4 — 미커버 카드 분류 + 액션

리포트 하단 3 섹션 (Dropped / PowerCatalog miss / no-axes) 을 보고:

- **Dropped 가 신규 카드**: `extract_card_triggers.py` 가 인식할 신호 (axis/keyword) 가 카드에 부족. 부모 repo 의 `axis-tagger` 스킬로 axis 부여 → master 갱신 → 재추출
- **PowerCatalog miss 카드 중 같은 power 이름이 여러 카드에 등장**: `PowerCatalog.SelfBuff`/`EnemyDebuff` 에 해당 power 추가 (tier 표 참고해 적정 값)
- **no-axes 카드**: 마찬가지로 axis-tagger 작업 대상

### Step 5 — 회귀 비교

이전 릴리즈 리포트 (`docs/ai_card_coverage.md` git 이력) 와 비교. 어떤 metric 도 5%p 이상 하락 시 alert.

## 일관성 체크 (PR/commit 전)

- [ ] `card_triggers.json` 이 최신 `cards_catalog.json` 에서 추출됐는지 (version 필드 일치)
- [ ] 리포트의 catalog inclusion 이 직전 릴리즈 대비 하락 안 했는지
- [ ] PowerCatalog 에 등록 추가했으면 hit % 가 실제 상승했는지
- [ ] CardOverrideCatalog 절대 수 ≤ 20
- [ ] dropped 카드 목록에 신규 카드만 있는지 (기존 카드 회귀 X)

## 출력 산출물

1. **`docs/ai_card_coverage.md`** — 리포트 본문 (git 에 commit 해 추세 트래킹)
2. **stdout** — 동일 내용 (sanity check)

리포트 구성:
- Headline metrics 표 (6 행: 단독 5 + synergy participation 1)
- PowerCatalog hit rate 세부 표
- **Synergy-rule reach** (5 룰별 카드 수 / %)
- **Pair-axis stem completeness** (P / A / C 카운트 표 + complete vs orphan)
- **Synergy participation degree** (0/1/2/3/4/5 분포)
- Per-character 분포
- Per-build 분포 (14 빌드)
- Top 30 axes
- Dropped / PowerCatalog miss / no-axes 카드 ID 상위 N 개 (default 20)
- Limitations 섹션

## 자주 헷갈리는 케이스

- **`vars` 가 비어있는 Power 카드**: id-derived 이름이 PowerCatalog 에 등록되면 hit 으로 인정. 둘 다 miss 면 `HeuristicFallback` 의 `*FormPower` / `Free*` 패턴 매칭에 의존 (리포트는 이 패턴 매칭을 hit 으로 카운트하지 않음 — 명시 등록만 hit).
- **upgrade 카드 중복 집계 방지**: 모든 metric 은 `is_upgraded == false` 만 카운트. base 카드 = id 기준 유일.
- **Korean build tag**: per-build 표의 tag 가 한글 (예: "독 빌드"). 터미널 encoding 깨지면 `--out` 으로 파일에 저장 후 확인 권장.
- **CardOverrideCatalog 매칭은 case-insensitive**: `_bonuses` dict 는 `OrdinalIgnoreCase` — 측정 스크립트도 upper-case 정규화 후 비교.

## 한계 — 정적 분석의 본질

- 런타임 `PlanScorer` 가 실제로 어떤 path 를 탔는지는 측정 X (DecisionLog 분석은 후속)
- Synergy reach 는 *잠재* 카드 수 — 같은 hand 에 partner 가 안 들어오면 실제론 활성 안 됨
- `Modifier-aware` 평가 (Str/Vuln/Weak 등) 는 모든 Attack/Skill 카드에 자동 적용 — 별도 metric 없음
- Axis convention 불일치: `VULN_AMPLIFIER` 는 있는데 `VULN_PRODUCER` 대신 `VULN` 만 쓰는 경우, pair completeness 표가 false-orphan 으로 보고. 표를 보고 axis convention 통일을 검토하는 게 룰 자체의 검증이기도 함.

향후 작업 후보: DecisionLog ring buffer (32 entry) 파싱으로 *실제 활성된* synergy 룰 카운트 (현 metric 은 상한선).
