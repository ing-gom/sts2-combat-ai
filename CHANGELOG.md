# Changelog

## v0.6.4 (2026-05-17)

**Runtime infrastructure Phase B + C — parser + analyzer.**

C# 변경 없음 — Python 스크립트 2개로 Phase A (v0.6.3) 의 NDJSON 출력을
소비해 분석 리포트 생성.

### `scripts/parse_decision_log.py` (신규, ~200 LOC) — Phase B

NDJSON 파일 디렉토리 → 정규화된 record JSON.

- 입력: `--logs <dir>` (default: `~/.local/share/Sts2/Sts2CombatAI/decision_log/`)
- 출력: `--out <path>` (records JSON) 또는 `--summary` (stdout)
- `--since YYYY-MM-DD` 필터
- `breakdown` 문자열에서 KV pair 추출 (`name=int` / `dmg{N}` / `tier=Name+N`)
- `enemy_hp_after` 는 같은 combat 내 다음 decision 의 `enemy_hp_before` 로 채움
- 외부 의존성 없음 (pure stdlib)

Schema 출력 (per record): combat_id, ts, character, playstyle, turn, step,
card_id, card_kind, target, score, enemy_hp_before/after, player_hp_before,
player_block_before, lethal_active, fetch_card, combo_links, reason,
breakdown_raw, breakdown_kv, breakdown_other.

### `scripts/analyze_decisions.py` (신규, ~400 LOC) — Phase C

records JSON → markdown 리포트. 10 metric 계산:

1. **Synergy activation rate** — 16 룰별 fire 빈도 (build_pair, hand_synergy,
   vuln_amp, weak_amp, damage_amp, block_amp, block_payoff, hp_loss,
   amplifier, power_tier, skill_tier, combo, override, lethal_mode,
   fetch_pollution, energy_monopoly)
2. **Lethal precision/recall** — `lethal_active` 가 fight-end 와 얼마나 매칭
3. **PowerCatalog runtime hit rate** — `*Power(N)=M` 패턴 매칭 (정적 lower-bound 보정)
4. **Combo fire rate** — 3+ / 4+ link 체인 빈도
5. **Fetch pollution** — `fetchPoll` key 적용된 fetch 카드 plays
6. **Setup-before-beneficiary** — Setup tier 가 공격 전에 발동하는 비율
7. **Energy curve** — 턴당 평균 plays, p50/p90
8. **Decision diversity** — 캐릭터별 unique cards, Top-10 share
9. **Per-tier play distribution** (`--catalog` 옵션 시) — S/A/B/C/D 사용 분포
10. **Score vs HP outcome** — 평균 score 와 HP loss 의 Pearson r

Phase D (A/B baseline) / Phase E (release diff) 는 향후 PR.

### 사용 예시

```bash
# 직접 NDJSON 디렉토리 분석
python scripts/analyze_decisions.py \
    --logs ~/.local/share/Sts2/Sts2CombatAI/decision_log/ \
    --catalog scripts/cards_catalog.json \
    --out docs/runtime_metrics.md

# 또는 2 단계
python scripts/parse_decision_log.py --logs <dir> --out /tmp/records.json
python scripts/analyze_decisions.py --records /tmp/records.json --out docs/runtime_metrics.md
```

### Phase A/B/C 통합 데이터 흐름

```
in-game → DecisionLog (ring buffer 32 entries) → DecisionLogPersister
  → {user_data}/Sts2CombatAI/decision_log/*.ndjson
  → parse_decision_log.py → normalized records
  → analyze_decisions.py → docs/runtime_metrics.md
```

## v0.6.3 (2026-05-17)

**Runtime analysis infrastructure — Phase A (DecisionLog persistence).**

`docs/runtime_analysis_infra_plan.md` 의 첫 단계. **평가 룰 변경 없음** —
pure observability. 매 전투 종료 시 in-memory `DecisionLog` ring buffer
를 disk 의 NDJSON 파일로 flush 해 후속 Phase B (parser) 와 Phase C
(analyzer) 의 입력 데이터 확보.

### `DecisionLog.cs` — Entry 확장 + Snapshot/Clear helper

Entry 에 8 신규 필드 (runtime context):
- `Turn` — `combatState.RoundNumber`
- `EnemyHpBefore` — 살아있는 적 HP 합
- `PlayerHpBefore` / `PlayerBlockBefore`
- `LethalActive` (bool) — breakdown 에 `lethalMode=` 포함 여부
- `IsFetchCard` (bool) — card.IsFetchTrigger
- `ComboLinks` (int) — `combo(Nlink,...)` 의 N 추출
- `Character` — `player.Creature.GetType().Name`

`DecisionLog.Snapshot()` / `Clear()` 추가 — persister 가 lock 없이 안전하게
read-and-clear 할 수 있도록.

### `DecisionLogPersister.cs` (신규)

- `Install()` — MainFile 가 mod startup 시 호출. `{user_data}/Sts2CombatAI/decision_log/`
  디렉토리 생성
- `FlushIfPending(character, floor, combatId)` — ring buffer 를 NDJSON
  파일로 write, buffer clear, rotation 적용
- 파일명: `{yyyyMMdd_HHmmss}_F{floor:D2}_{character}_{combatId}.ndjson`
- Rotation: 최신 200개만 유지 (~20MB cap)
- 직접 작성한 minimal JSON 직렬화 (Newtonsoft / System.Text.Json 의존 없음)
- `Enabled` 플래그로 런타임 토글 가능 (향후 ModConfig 연동 지점)

### `VakuuExecutor.cs` — 호출부 통합

- 매 결정 record 시 새 필드 모두 채움 (BreakdownDetails substring 검색으로
  LethalActive / ComboLinks 추출 — score path 에 새 dependency 추가 안 함)
- 전투 종료 감지 시 (`IsOverOrEnding || allEnemiesDead`) `FlushIfPending`
  호출. Best-effort — 깔끔한 종료 (보스 처치 / 사망) 는 잡지만 게임 종료
  / mid-combat 종료는 미수집 가능 (Phase D 의 Harmony 패치로 보완 예정)

### `MainFile.cs` — Install hook

`PlaystylePersistence.Install()` 다음 줄에 `DecisionLogPersister.Install()`.

### NDJSON schema (per line)

```json
{"ts":"2026-05-17T10:23:45.123Z","step":1,"turn":2,
 "playstyle":"Balanced","character":"Vakuu","card_id":"CARD.BASH",
 "target":"JawWorm","score":850,
 "enemy_hp_before":44,"player_hp_before":68,"player_block_before":0,
 "lethal_active":false,"fetch_card":false,"combo_links":3,
 "reason":"Attack","snapshot":"...","breakdown":"..."}
```

Phase B (parser) 가 이 schema 를 그대로 normalize 해 `combat_id`,
`turn`, `synergy_axes`, `breakdown_kv` 등으로 분해.

## v0.6.2 (2026-05-17)

**Status pollution + combo recognition + energy monopoly — Medium/Low
impact gaps (gap 5/6/7 of framework doc).**

세 신규 평가 룰. 모두 작은 magnitude 로 적용해 기존 큰 score 결정 (lethal,
threat, raw damage) 을 뒤집지 않고 tie-breaking + 디버그 가시성 강화.

### `EvaluateFetchPollution` (PlanScorer.cs) — Status / Curse pollution

`fetch_trigger` 카드 (Anointed / Echo of Fallen 등) 가 draw/discard pile 의
status/curse 비율에 비례해 점수 감점. junk 비율 0% 면 영향 X, 30% 면
−210, 50% 면 −350.

```csharp
penalty = -(pollution_prob × FetchPollutionExpectedCost)  // 700
```

영향 카드: 카탈로그의 `fetch_trigger: true` 카드. 오염된 deck (Time Eater /
Necronomicurse 후) 에서 안전성 향상. 깨끗한 deck 에서 영향 X.

필수 인프라:
- `SimCard.IsFetchTrigger` 신규 (`StateSnapshotter` 가 catalog 에서 propagate)
- DrawPile / DiscardPile 의 IsCurseOrStatus 카운트 사용 (v0.5.1 의 pile
  snapshot 활용)

### `ComboRecognition.cs` (신규) — 멀티-링크 시너지 체인 감지

손패에 3+ 연결된 시너지 체인 (Producer↔Amplifier, Setup→Beneficiary,
Vuln/Weak→Amplifier) 발견 시 작은 보너스. 주된 가치: **디버그 가시성**
— `DecisionLog` 에 "combo(4link, Inflame→Bash→Cruelty→Strike)+150" 같은
시그널 노출.

```csharp
bonus = min(MaxChainBonus(250), (link_count - 2) × PerLinkBonus(50))
```

Edge model (axis-suffix + power-application 기반):
- `X_PRODUCER` ↔ `X_AMPLIFIER` / `X_CONSUMER`
- Power 의 StrengthPower/DexterityPower → Attack/Skill beneficiary
- VulnerablePower/WeakPower → 같은 손패의 `VULN_AMPLIFIER`/`WEAK_AMPLIFIER`

### `EvaluateEnergyMonopoly` (PlanScorer.cs) — 에너지 단점 페널티

고비용 카드가 손패의 다른 playable 카드를 스킵하게 만들 때 작은 페널티.

```csharp
if (card.Cost == state.PlayerEnergy && skipped_playables > 0)
    penalty = -min(EnergyMonopolyPenaltyCap(100),
                   skipped × EnergyMonopolyPenaltyPerSkipped(25))
```

Free attack 우대 효과. depth-2 lookahead 가 이미 70~80% 처리하지만
3+ 카드 조합 시 lookahead 한계 보완.

## v0.6.1 (2026-05-17)

**a-2 (multi-hit-attack ordering) + a-3 (휘발성 처리) 보강.**

`docs/card_play_order_framework.md` 의 High-impact gap 2, 3 구현. 모두
weight 조정 / type-aware bonus 로 처리 — 새 룰 모듈 도입 없이 기존
HandSynergy / PlayOrderBias 의 magnitude 만 재보정.

### `HandSynergy.cs` — Vuln 시너지 magnitude 보정

`VulnerableSynergyPerHit` **40 → 100**. 분석상 Vuln 의 *실제* 1-hit 가치
≈ 0.5 × avg_dmg(5) × DamagePerPointBonus(50) = **125**. 기존 40 은 약
1/3 수준의 under-calibration → Bash + 멀티힛 손패 조합에서 Bash 가
Twin Strike 보다 score 낮아 멀티힛이 먼저 (Vuln 미적용) 발동하는
mis-ordering 발생. 100 으로 올려 손패의 멀티힛/공격 카드 수가 클수록
Bash 가 우선 발동되도록.

Strength / Dex / Weak weight 는 그대로 — 분석상 이미 적정 calibration.

### `PlanScorerWeights.cs` + `PlanScorer.PlayOrderBias` — 휘발성 (Ethereal) 보너스 상향

`EtherealPlayNowBonus` **120 → 500**, 신규 `EtherealPowerPlayNowBonus = 800`.

배경: 카탈로그의 ETHEREAL_SELF axis 카드 18장 (휘발성, 해당 턴 미사용 시
손패에서 exhaust) 의 카드 가치는 200~1500 (특히 Power: VoidForm 700,
Demesne 550, EchoForm 1500). 기존 +120 보너스는 "안 쓰면 0" 의 trade-off
대비 너무 작아 Block-under-threat / 다른 Power 등 high-score 대안에
밀려 휘발성 카드가 헛되이 exhaust 되는 케이스 발생.

새 처리:
- Ethereal Power: **+800** (가치 큰 Power 가 무리 없이 다른 대안 이김)
- Ethereal Attack/Skill: **+500** (200~600 가치 대 종합 balanced)
- Curse/Status: 영향 없음 (auto-rejected 영역)

PlayOrderBias 에 type 분기:
```csharp
delta += card.IsPower ? w.EtherealPowerPlayNowBonus : w.EtherealPlayNowBonus;
```

기존 LethalMode 페널티 (-3000) 가 우선 — lethal turn 에서는 휘발성 Power
도 무시하고 공격 선택 (정상).

### 영향 카드 (catalog 의 ethereal:true 18장)

Power: APPARITION, DEFY, DEMESNE, ECHO_FORM, ENFEEBLING_TOUCH, LETHALITY,
PARSE, SEANCE, VOID_FORM  
Attack: DEFILE, DYING_STAR, FEAR, SWEEPING_GAZE  
Skill: APPARITION, DEFY, ENFEEBLING_TOUCH, PARSE, SEANCE (Skill side)  
Curse/Status (영향 없음): ASCENDERS_BANE, CLUMSY, FOLLY, DAZED, VOID

## v0.6.0 (2026-05-17)

**Lethal-this-turn 감지 + SkillSequencingTier 신규 추가.**

`docs/card_play_order_framework.md` 의 High-impact gap 1 (lethal mode) 와
Medium-impact gap 4 (Skill 순서 tier) 구현. 두 변경은 audit metric 에는
영향 없음 (PowerCatalog 항목 변동 없음) — 실제 *런타임 결정* 의 정확도
향상이 목적.

### `PlanScorer.cs` — `IsLethalThisTurn(SimState)`

턴 시작 시 hand 의 공격 카드를 damage-per-energy 순으로 greedy 선택하고
각 적의 Vuln / 자기 Weak / damage cap / HardenedShellRemaining 을 반영한
유효 데미지 합산. 합 ≥ 살아있는 적 HP 총합이면 lethal turn.

이때 Power / Skill 카드는 `LethalModeNonAttackPenalty = -3000` 적용 →
공격이 안정적으로 score 비교에서 이김. "마지막 턴에 DemonForm 발동하는"
미스플레이 방지.

한계 (의도적 단순화, false-positive 회피 방향):
- 단일 타겟 공격은 가장 Vulnerable 한 적 기준 데미지 추정
- Body Slam / Calculated* / Repeat 스케일은 base damage 만 계산 → false-negative 가능 (안전)
- 같은 턴 Setup Power 로 늘어날 Strength 는 반영 안 함 (현재 상태만 보고 판단)

### `SkillSequencingTier.cs` — 신규

`PowerSequencingTier` 의 Skill 버전. 5 tier:

| Tier | OrderingBonus (≥2 Skills) | 분류 |
|---|---:|---|
| Setup | +100 | Vuln/Weak 부여 (`VulnerablePower`/`WeakPower` PowerApps 또는 `VULN`/`WEAK` axis) |
| Cantrip | +60 | 드로우 / 에너지 생성 |
| Defensive | 0 | self-block (기존 BlockUnderThreatBonus 가 처리) |
| Utility | 0 | 그 외 |
| Unknown | 0 | non-Skill |

ConditionalBonus:
- Setup 인데 hand 에 공격 없음 → −200 (`setupNoAtk`)
- Cantrip 인데 hand 9장 이상 → −150 (`cantripFull`)

Magnitude 는 Power tier (200/150/100) 의 절반. Skill 은 이미
state-dependent scoring (block-under-threat / draw quality / energy
context / survival urgency) 이 강해서 tier 보너스는 tie-breaker 역할.

### `PlanScorerWeights.cs`

- `LethalModeNonAttackPenalty = -3000` 신규 weight. Power tier-S+ 의
  최대 점수보다 크게 잡아 안정적인 공격 선택 보장.

## v0.5.1 (2026-05-16)

**Draw 카드 depth-2 lookahead 정확도 향상 — 덱 내용 기반 평균 카드로 placeholder 교체.**

기존 `AnalyticalSimulator` 의 draw 처리는 뽑힌 카드를 *고정* placeholder (1코스트 5데미지 공격)
로 대체했음. 그 결과 draw 카드의 깊이-2 lookahead 점수가 실제 덱과 무관 — 강한 덱 (high-damage
attacker 다수) 이든 약한 덱 (status 다수) 이든 동일하게 "5뎀 공격 1장이 추가로 들어옴" 으로 평가.

이번 변경: 실제 draw / discard pile 내용을 snapshot 해서 그 pile 의 *평균 효과* 카드를 합성.
"이 덱에서 다음에 뽑힐 카드의 기대값" 에 가까운 representative card 가 simulator hand 에 들어가서
depth-2 second-play 점수가 덱 상태를 정확히 반영.

### `SimState.cs`

- `DrawPile`, `DiscardPile` (둘 다 `IReadOnlyList<SimCard>`) 신규 — 카드 ID / 효과 / cost / kind
  까지 들어있는 실제 카드 정보. `DrawPileSize` / `DiscardPileSize` (기존 raw count) 는 그대로 유지
  — EvaluateDrawCard 등 size 만 보는 callsite 가 list 를 materialize 하지 않도록.

### `StateSnapshotter.cs`

- `PileType.Draw` / `PileType.Discard` 의 `Cards` 를 SimCard 로 변환 — hand snapshot 과 동일한
  `BuildSimCard` 헬퍼 reuse, pile cards 는 `CanPlay()` 체크 skip (hand 밖이라 무관).
- Hand build 로직도 동일 헬퍼로 통합 — `requirePlayability: true` 옵션으로 분기.

### `AnalyticalSimulator.cs` — `MakeAverageDrawCard(state)`

`MakePlaceholderCard()` (고정 5뎀/1코) → `MakeAverageDrawCard(state)` 로 교체:

- **Damage**: pile 의 per-card `Damage × Hits` 합 ÷ 카드 수 = E[per-card TotalDamage]. 그 후
  `avgHits` 로 split back 해서 per-hit damage 산출 (Vulnerable / Weak 의 per-hit 처리 정합).
- **Block**: per-card Block 평균.
- **Cost**: pile 평균 cost (음수는 0 으로 floor).
- **Hits**: per-card hits 평균 (최소 1).
- **Kind**: pile 의 attack 카드가 절반 이상 → Attack, 아니면 Skill. Target 은 그에 맞춰 결정.
- **Combined pool**: draw + discard 둘 다 — 게임 중 reshuffle 로 두 pile 의 카드들이 같은 확률로
  뽑히므로 합쳐서 평균하는 게 EV 모델로 정확.
- **Fallback**: pile 이 비어있으면 기존 5뎀 placeholder (테스트 fixture / capture 실패 케이스).
- **Per-`ApplyCardPlay` 한 번만 계산** — N장 draw 시 pool 평균이 1장 빠진다고 거의 안 움직이므로
  같은 average card 를 N번 hand 에 추가. hot loop 비용 최소화.

### 효과 시나리오

- **강한 덱 (Strike+ / Twin Strike / Pommel Strike 다수)**: avg damage ↑ → draw 카드의 depth-2
  점수 ↑ → 강한 덱에선 draw + follow-up 콤보가 정당하게 prioritize.
- **Status 가득한 덱 (Wound / Slimed 다수)**: avg damage ↓, kind=Skill 로 평가 → draw 카드의
  depth-2 점수 ↓ → 손에 좋은 카드 있으면 draw 안 하고 바로 play.
- **Block 카드 위주 덱 (Defend 다수)**: avg block 반영 → draw → block skill 시퀀스가 자연히
  방어 시나리오에서 valued.

### Limitations / 의도된 미구현

- **PowerApps 미반영**: pile 의 power 카드 (Inflame, Demon Form 등) 효과는 평균에 들어가지 않음
  — power stack 은 비선형이라 단순 평균이 의미 없음. 대신 EvaluateDrawCard 의 hand-quality
  heuristic 이 보강 역할.
- **DrawCount / EnergyGain 미반영**: 합성 카드에서 빼서 recursive draw / energy chain 방지. depth-2
  안에서 재귀 lookahead 가 일어나지 않도록.
- **Single representative card (not Monte Carlo sampling)**: 결정적 score 유지 — depth-2 hot loop
  에서 stochastic 결과 노이즈 제거가 우선.

## v0.5.0 (2026-05-16)

**카드 사용순서 정확도 향상 — 시뮬레이터/스코어러 정합성 정리 + 카드 우선순위 분류 신규.**

게임 로직은 동일하지만 plan 이 실제 in-game 결과와 더 가까워지도록 다수의 sim/scoring
버그 수정 + 누락된 효과 보강. 추가로 카드 타입 / 효과 / 상황 별 우선순위 분류 layer 신설.

### `PowerSequencingTier.cs` 신규

각 power 를 5 tier 중 하나로 분류 — Setup / Scaling / Defensive / Tempo / SelfHarm:

- **Setup** (Strength, Dex, Focus, Accuracy, Vigor) — 같은 턴에 *뒤따라 plays 될 카드들의 가치를 곱해주는* 버프. 먼저 깔리지 않으면 multiplier 가 낭비됨.
- **Scaling** (DemonForm, EchoForm, ReaperForm, MachineLearning, Juggernaut, Mayhem, Corruption, Poison/NoxiousFumes, Ritual, Hunger, BeaconOfHope 등) — 장기 fight 에서 turn 마다 누적되는 permanents.
- **Defensive** (Barricade, Intangible, Buffer, Plated Armor, Thorns, FlameBarrier, Blur, FeelNoPain, Artifact, Regen 등) — block / 피해 mitigation. Threat 없으면 의미 없음.
- **Tempo** (EnergyNextTurn, DrawCardsNextTurn, FreeAttack/Skill/Power) — 같은 턴 시너지 없음. defer 가능.
- **SelfHarm** (NoDraw, NoBlock, Confused, MindRot 등) — 회피.

### `OrderingBonus` — 같은 손 ≥2 power 카드일 때만

50–200점 nudge — Setup +200, Scaling +150, Defensive +100, Tempo +50, SelfHarm -300. PowerCatalog 절대값 보존, 동률 깨는 정도.

### `ConditionalBonus` — 상황 인식 보정

- **Setup**: Focus + 손에 남은 orb 카드 수 (×80), Accuracy + 남은 attack 수 (×30), Vigor + 남은 attack 0 → -250. Setup 인데 수혜자 0 → -300. (Strength/Dex 기존 HandSynergy 와 중복 회피.)
- **Defensive**: leak (predicted dmg − current block) > 0 → +leak×40 (max +800). 모든 적 inert / 위협 미만 → -200. 위협 있을 때 Setup 위로 점프.
- **Scaling**: 적 1마리 + 남은 HP ≤ 25 → -300 (broad fightCtx 가 못 잡는 boundary).
- **Tempo**: 총 적 HP ≤ 15 → -400 (이번 턴 끝날 fight 에 next-turn 자원은 무가치).

### Integration

`PlanScorer.BreakdownInternal` 의 power 분기에 통합. score breakdown details 에 `tier=Setup+200,focusOrbSyn=+240` 같은 형태로 노출. 기존 catalog / synergy / fight-context 로직은 그대로.

### `AmplifierSynergy.cs` — power 카드에 영향 주는 skill/attack 우선순위

`POWER_AMPLIFIER` / `REPLAY` / `ATTACK_REPLAY` / `ATTACK_REPLAY_RANDOM` / `SKILL_REPLAY` axis 를 가진 카드 (Subroutine, Signal Boost, Dual Wield, Iteration, Loop, Juggling, Hidden Gem, Nostalgia, Catastrophe, Nightmare, Beat Down, One-Two Punch, Stampede) 의 점수를 *손에 남은 best 타겟의 PlanScorer.Score × 비율*로 계산.

비율: PowerAmp 0.50 / AtkReplay 0.50 / AtkReplay-Random 0.35 / SkillReplay 0.45 / Generic 0.45. Cap 3000.

손에 타겟 없으면 `-500` 페널티 (Subroutine + power 0장은 dead card).

재귀 방지: 타겟 풀에서 amplifier axis / draw card 자체를 제외. Amp→Replay→Amp 루프, Replay→Draw→Replay 루프 차단.

PlanScorer 의 Attack / Skill 분기 양쪽에 hook (Beat Down 등은 attack, Subroutine 등은 skill).

### PLAY_TRIGGER power 인식

Afterimage / Calamity / Serpent Form / Sleight of Flesh / The Sealed Throne — 카드 play 마다 trigger 되는 power. PowerSequencingTier.ConditionalBonus 의 Scaling 분기에서 `remaining playable cards × 60` 보너스. PowerCatalog 의 flat 값이 못 잡는 hand-size scaling 을 보완.

### `EffectSynergy.cs` — attack/skill 효과 기반 순서

Power 카드가 아닌 카드 (attack/skill) 에서 효과 axis 가 implicit 한 ordering 을 갖는 케이스 처리.

- **DAMAGE_AMPLIFIER** (Aggression, Conflagration, Flanking, Knockdown, Lethality, Shadow Step, Sword Sage) — `remainingAttacks × 70`. 후속 공격이 없으면 -200.
- **BLOCK_AMPLIFIER** (Entrench, Pillar of Creation, Unmovable — skill 만; Barricade/Blur/Danse/Shadowmeld 는 power 라 tier 처리) — `curBlock × 4 + remainingBlocks × 50`. 둘 다 0 이면 -250.
- **VULN_AMPLIFIER** (Bully, Colossus, Cruelty, Debilitate, Dismantle, Dominate, Molten Fist) — 타겟 적 Vuln → +450, 다른 적 Vuln → +300, 손에 vuln applier → +250, 아무 source 없음 → -300.
- **WEAK_AMPLIFIER** (Debilitate, Tracking) — 같은 패턴 작은 가중치.
- **BLOCK_PAYOFF** (Body Slam) — `curBlock × 30`. Block 0 + 손에 block skill → -600. Block 0 + 없음 → -1500.
- **HP_LOSS_CONSUMER** (Inferno, Tear Asunder) — PlayerHp ≤ 30 → +350, ≤ 50 → +200.

Power 카드는 skip (PowerSequencingTier 가 처리).

### `BuildSynergy.cs` — Producer↔Amplifier/Consumer 대칭

기존 `Producer + Amplifier-in-hand → Producer 가 +250` 만 있었고 반대 방향 (Amplifier 가 Producer-in-hand 일 때 +200) 누락. Consumer 도 대칭으로 추가. Poison/Orb/Forge/Shiv/Skeleton/Doom 등 빌드에서 Amplifier/Consumer 쪽 우선순위가 제대로 반영.

### Integration

`PlanScorer.cs` Attack / Skill 분기에 `EffectSynergy.Compute` hook 추가. Score breakdown details 에 `vulnAmpTgt=+450,dmgAmp(atk*3)=+210` 같이 노출.

### Survival urgency — cross-type 상황 인식

기존엔 `BlockUnderThreatBonus +2000` 가 *block 카드*만 잡고 있어서 fatal/heavy threat 상황에서도 DemonForm / EchoForm 같은 강한 power 가 Defend 를 압도 → 사망. `EnemyTurnSimulator.GetSurvivalUrgency(state) → {None, Moderate, Heavy, Fatal}` 도입 + 모든 타입에 상황 페널티.

- **Power 카드 (Setup/Scaling/Tempo tier)** — `PowerSequencingTier.ConditionalBonus`:
  - Fatal (leak ≥ HP) → -2200
  - Heavy (leak ≥ HP × 0.5) → -900
  - Moderate (leak ≥ HP × 0.2) → -250
  - Defensive tier 와 SelfHarm tier 는 면제 (Defensive 는 *survival 응답* 그 자체, SelfHarm 은 이미 음수).
- **Skill 카드 (non-block, non-energy/draw)** — `PlanScorer.cs` Skill 분기:
  - Fatal → -900, Heavy → -350. Inflame / Limit Break / Cleanse 같은 pure setup 만 영향. Block / energy-gain / draw skill 은 면제 (survival 의 일부).
- **Attack 카드 (non-lethal)** — `PlanScorer.cs` Attack 분기:
  - Fatal + non-lethal (single-target OR all-AOE-lethal 둘 다 아님) → -1200
  - Heavy + non-lethal → -400
  - Lethal kill 은 `RealLethalKillBonus +5000` 가 페널티를 압도 → 자연히 면제.

**버그 시나리오 검증** (Player 5 HP, leak 8, hand [DemonForm 3코, Defend 1코, Strike 1코]):
- Before: DemonForm ≈ 3000 vs Defend ≈ 2250 → DemonForm plays → 사망
- After: DemonForm = 3000 − 2200 = 800, Defend = 2250 + neutralize 1200 = 3450, Strike = 900 − 1200 = -300 → Defend plays → 생존 ✓

### Multi-hit 적에 대한 Weak 정확도

기존 `PredictPlayerDmg` 가 Weak 를 *total* damage 에 곱했는데 (`15 × 0.75 = 11`) 실제 STS 는 *per-hit* 으로 floor 적용 (`5dmg×3hits → floor(3.75)×3 = 9`). Multi-hit 적에 대해 sim 이 2 dmg 과소평가 → Weak 의 가치가 깎여있던 상태.

- `EnemyTurnSimulator.PredictPlayerDmg` — `(IntentDamage + Strength) × 0.75 → floor → × IntentRepeats` 로 수정.
- `HandSynergy.WeakPower` — `remainingAttacks × 30` 의미없는 공식을 `ComputeWeakSavings(stacks, state)` 로 교체. 각 적의 `(perHit - floor(perHit × 0.75)) × IntentRepeats × min(stacks, 2) × 30` 합산. Multi-hit 적이 있으면 자연히 더 큰 점수.
- `HandSynergy.VulnerablePower` — `remainingAttacks × 50` → `remainingHits × 40`. Twin Strike(2 hits) 같은 multi-hit 카드가 Vuln 으로 더 큰 이득 보는걸 점수에 반영.
- `EffectSynergy.WEAK_AMPLIFIER` — multi-hit 적 (IntentRepeats ≥ 2) 마다 +120 추가 보너스.

### Draw 카드 동작 분석

1코스트 draw → 0 에너지 시나리오. 기존 `EvaluateDrawCard` 가 hand-best-score / pile size 만 봐서 "drew 후 사용 불가" 케이스 누락.

- **Energy-after-draw 체크**: `energyAfter = energy - cost + energyGain`. 0 이고 손에 0-cost 카드도 energy-gain 카드도 없으면 drawn 카드는 next-turn-only. hand 가 strong (≥2000) 면 -800, weak (≥1000) 면 -400, useless 면 bonus / 3.
- **Hand-cap overflow**: STS hand 10 장 한도. `(handAfterPlay + DrawCount) - 10` 만큼 wasted → wasted/DrawCount 비율로 bonus 차감.

**시나리오 검증** (1 energy, hand = [Pommel Strike 1코 draw, Strike 1코]):
- Before: Pommel Strike draw bonus = DrawNoCostBottleneckBonus(+500) → 그냥 plays
- After: energyAfter = 0, zeroCost = 0, energyGain = 0 → canChain = false → bestOther(Strike) < HandWeakThreshold 라 bonus / 3 = +166. Strike 가 attack 점수로 이김 ✓

**Pommel Strike 자체는 attack 점수가 있어 plays 될 수 있지만, 순수 draw 스킬 (Backflip 같은) 이 같은 상황에 있을 때 우선 deferred.**

### v0.5 추가 패스 (Iter 44-60)
- **Negative Focus** 도 honor — clamp 위치를 input 대신 output (per-tick 0 floor) 로.
- **Orb evoke (Lightning/Dark/Glass)** 가 Intangible / HardenedShell cap 적용. 이전엔 sim 의
  DamageWeakest/DamageAll 이 캡 없이 데미지 → corpse follow-up 계획.
- **Lethal-range hand projection** 도 Intangible / Shell budget 트래킹 — 못 죽일 적에 lethal
  bonus 가 잘못 fire 되는 문제.
- **FreeAttack/Skill/PowerPower** 카운터 SimState 에 트래킹 + sim 에서 consume + EnumerateCandidates
  가 비싼 카드를 free play 로 통과시킴.
- **Player IntangiblePower** 가 PredictPlayerDmg 에서 incoming hit 을 1/hit 으로 cap. 이전엔
  Apparition 후에도 over-defend.
- **Metallicize / PlatedArmor** end-of-turn block 이 threat 계산에 반영 (불필요한 defend 회피).
- **Thorns 데미지가 PlayerHp 에 반영** — multi-hit attack vs thorny enemy 의 cumulative HP burn
  을 depth-2 가 인식.
- **DoT-lethal threshold** 가 Poison + Constrict 합산 (둘 다 pre-attack tick).
- **Artifact 가 entire debuff application 차단** (canonical STS) — 이전엔 stack-by-stack 으로
  부분 차감하여 partial debuff 가 잘못 land 한 것처럼 계산.
- **OrbCardCatalog** 의 ORB_PRODUCER fallback 이 ORB_EVOKE 카드는 skip (Shatter 가 phantom
  channelCount=1 받아서 BuildSynergy 가 producer 로 오인하던 문제).
- **EvokeValue / PassiveValue 가 PlayerFocus 반영** — Defect 후반 scaling 가시화.
- **EnergizedPower / EnergyNextTurnPower** 는 PowerCatalog 만 사용 (sim 의 immediate
  energy + EvaluateEnergyGain unlock 로직은 STS2 semantics 미확인이라 보류).
- **Skim 류 (energy gain + draw)** 가 hand-empty filter 통과 — 자기 자신의 draw 가 follow-up 생성.
- **Draw card 가 played 후 discard 에 들어가는 순서** 수정 (draw 가 먼저, discard pile bump 가
  그 다음 — 갓 play 한 카드를 같은 turn 에 다시 draw 하는 anomaly 방지).
- **Sim 의 BuildSynergy** producer/consumer 판정이 axes 대신 ChannelCount/EvokeCount 로 (Dualcast/
  Quadcast 가 full slot 에서 -300 penalty 받던 700-point swing fix).

### Play-order biases (신규)
- **Retain** 카드는 다른 plays 가 남아있을 때 우선순위 ↓ (defer). 마지막 선택지일 때는 정상 점수.
- **Ethereal** 카드는 turn-end exhaust 회피를 위한 소폭 boost (play-now).
- **Innate** 플래그 surfaced (현재는 정보 전용).
- Bias 는 `ActionPlanner` 에서만 적용 — `SmartSelectorLogic` (discard/exhaust prompt) 는 unbiased
  score 사용하도록 분리. 이전에는 retain 카드가 discard 우선순위로 잘못 선택될 수 있었음.

### Simulator accuracy
- **`AnalyticalSimulator` 공격 데미지 cap chain**: Intangible per-hit cap + HardenedShellRemaining
  total cap 적용. 이전에는 uncapped damage 로 HP 깎아서 depth-2 가 corpse 에 follow-up 을 계획.
- **HardenedShellRemaining 감소** 동기화 — successive shell 공격 sequence 가 실제 게임처럼 점감.
- **Frail / Poison / Constrict / Burn / Artifact** 디버프 propagation. 이전에는 Vulnerable / Weak
  만 적용. 이제 lookahead 가 full debuff 상태 인식 (Catalyst → 대형 poison 콤보 등).
- **Strength / Dexterity** 가 Power 카드뿐 아니라 self-target Skill 에서도 player stat 에 누적
  (Spot Weakness 류). `TemporaryStrengthPower` / `TemporaryDexterityPower` 도 포함.

### Threat estimation
- **Poison-lethal 적군 → threat 제외**. `PoisonAmount ≥ Hp` 인 적은 자신의 다음 turn 시작에 죽으므로
  intent 가 fire 되지 않음. PredictPlayerDmg 가 이를 인식해서 불필요한 block 결정 회피.
- **Player Vulnerable 멀티플라이어 (×1.5)** 적용. 이전엔 PlayerVulnerable 값이 capture 되었지만
  threat 계산에 미사용 → vulnerable 상태에서 incoming damage 50% 과소평가 문제.

### Per-enemy AOE accuracy
- **AOE 공격 effect 계산**: 적별 Vulnerable + Intangible cap + Shell cap 개별 적용. 이전 bulk
  `effectivePerHit × Hits × aliveCount` 식은 잘못된 target 의 Vulnerable 을 쓰고 cap 을 무시했음.
- **AOE target loop** 도 동일하게 capped per-enemy damage 사용 → 잘못된 lethal flagging 수정.
- **AOE-zeroed** wasted-attack 페널티 신규: 모든 적이 0 데미지로 cap (full shell board, full
  Intangible) 일 때 penalty 적용.

### Skill PowerApps
- **AllEnemies 스킬 디버프** (Footwork-style Weak-to-all) 가 alive enemy count 로 scale.
- **Single-target enemy skill** 의 **Artifact gating**: 대상 Artifact 가 stack 을 fully 흡수하면
  value 0 처리. 이전엔 부여되지 않는 디버프에 점수 부여.

### Target priority
- **Poison-lethal target short-circuit**: PoisonAmount ≥ Hp 인 target 은 모든 intent / state
  bonus 무시하고 즉시 강한 penalty (`PoisonLethalPenalty -1200`). 이전 tier 방식은 buff target
  bonus 와 합쳐서 net positive 가 될 수 있어서 corpse-walking minion 에 공격 낭비.

### Bug fixes
- **`EvaluateEnergyGain` 가 `EnergyNextTurnPower` 카드 (Berserk-style) 잘못 페널티**: 이제 즉시
  `EnergyGain > 0` 인 카드에만 unlock 로직 적용. Berserk 의 PowerCatalog 값이 단독으로 유효.
- **`EnumerateCandidates` 가 next-turn energy power 카드를 hand 가 비었다는 이유로 skip**: 동일
  fix — IsEnergyGainCard 가 아닌 EnergyGain > 0 으로 narrow.
- **CardOverrideCatalog 의 사라진 `CARD.FORETHOUGHT` 항목 제거** (STS2 v0.103.2 catalog 에 없음).

### Score breakdown / diagnostics
- **HandSynergy lookahead double-count 보정**: depth-2 가 이미 한 beneficiary 의 buff 적용을
  catch 하므로 HandSynergy 는 그 외 (N-1) 명만 카운트. 이전엔 Inflame / Bash 류 setup 카드가
  ~50-100 점 과대평가.
- **0-cost 카드 MinPlayScore 우회**: 0-cost positive 카드는 floor 무시하고 항상 play (free 카드를
  turn-end 에 버리지 않도록).
- **`bestNextId`** 가 candidate trace 에 추가됨 — depth-2 가 어떤 follow-up 을 골랐는지 로그에 표시.
- **Card log 에 |R / |E / |I / |X (Retain/Ethereal/Innate/Unplayable) 플래그 표시**.
- **ScoreBreakdown component 합산**이 Total 과 일치하도록 수정 (로그 명확성).
- **`SimCard.Played` dead 필드 제거** — 시뮬레이터는 hand 에서 제거하지 mark 하지 않음.
- **다중 channel 카드 kick value** (Glacier 2 Frost / ConsumingShadow 2 Dark / Refract 2 Glass)
  가 full slot 상황에서 모든 kicked orb 의 evoke value 합산.

### Tests
- Test 프로젝트가 이 repo 에 없어서 직접 실행 검증 못함 (build 환경 부재). 모든 변경은 정합성
  분석 + 코드 리뷰 기반. 다음 dotnet 빌드 시 unit test 회귀 확인 권장.

## v0.4.0 (2026-05-16)

**Project rename + architecture split — Vakuu 종속 컨셉을 범용 Combat AI 로 재정렬.**

기능 변경 없음 — 순수 리팩터. 같은 의사결정 로직, 같은 Vakuu hook, 다른 폴더 구조.

### Project rename
- `Sts2VakuuPlus` → `Sts2CombatAI` (csproj, RootNamespace, AssemblyName, ModId, mod manifest, 로그 prefix `[VakuuPlus]` → `[CombatAI]`)
- 사용자 데이터 디렉토리 `{user_data}/Sts2VakuuPlus/` → `{user_data}/Sts2CombatAI/` (기존 playstyle.json 은 마이그레이션되지 않음 — 한 번 다시 선택)
- 모드 폴더 경로 `mods/Sts2VakuuPlus/` → `mods/Sts2CombatAI/`

### Folder split — Core vs Modes
- `Sts2VakuuPlusCode/` → `Sts2CombatAICode/`
- 의사결정 엔진은 `Sts2CombatAICode/Core/` 아래로 (Planner / Sim / Reflection / Data / Diagnostics / Runtime). 모드와 무관 — namespace `Sts2CombatAI.Planner`, `Sts2CombatAI.Sim`, etc.
- Vakuu 전용 trigger / runtime 은 `Sts2CombatAICode/Modes/Vakuu/` 로 분리 (namespace `Sts2CombatAI.Modes.Vakuu`):
  - `WhisperingEarringPlannerPatch.cs` (게임 relic hook)
  - `VakuuExecutor.cs` (13-step auto-play loop — was Planner/VakuuExecutor.cs)
  - `VakuuCardSelectorPatches.cs` (mid-play prompt 응답 — was Patches/)
  - `VakuuTestButtonPatch.cs` (Vakuu Play 디버그 버튼 — was Patches/)
  - `TestButtonPoller.cs` (위 버튼의 fallback poller — was Runtime/)
- 향후 새 모드는 `Modes/<NewMode>/` 에 trigger + executor 만 추가하면 Core 재사용. Smart Vakuu (예정) 도 같은 방식.

## v0.3.0 (2026-05-15)

**Major release — simulator accuracy + character mechanics + persistence.**

### Phase 1 — Simulator forward-state accuracy
- **EnergyGain trigger** in AnalyticalSimulator. Adrenaline / Skim 같은 카드의 +energy 가 시뮬레이션에 반영 → Adrenaline → BigStrike 콤보 인식.
- **DrawCount trigger** — DrawCount 카드 plays 후 hand 에 평균-가치 placeholder 카드 추가. DrawPileSize / DiscardPileSize 동적 갱신 (reshuffle 포함).

### Phase 2 — Character mechanics
- **Defect orb tracking** — SimState.PlayerOrbCount / PlayerOrbCapacity 가 PlayerCombatState.OrbQueue 에서 read. BuildSynergy 가 orb 상태별 보너스:
  - ORB_PRODUCER + 빈 슬롯 → +150
  - ORB_PRODUCER + 가득 → -300 (eviction 회피)
  - ORB_CONSUMER + 가득 → +400 (evoke 적기)
  - ORB_CONSUMER + 빈 → -800 (낭비 회피)

### Phase 3 — Sparse card overrides
- `CardOverrideCatalog` — algorithm 이 under/over-value 하는 15 카드 manual 보정:
  - EchoForm +800, DemonForm +700, Barricade +600, Wraith +600
  - Anointed/Forethought/Havoc/AllForOne (fetch)
  - BansheesCry / AshenStrike / PerfectedStrike (conditional)
  - Apparition / Alchemize / Apotheosis

### Phase 4 — UX + safety
- **Playstyle persistence** — `{user_data}/Sts2VakuuPlus/playstyle.json` 자동 save/load. 게임 재시작 후 마지막 선택한 Style 유지.
- **DecisionLog ring buffer** — 마지막 32 plan steps 저장 (timestamp, card, target, score, breakdown). 향후 debug hotkey 통해 dump 가능.

### Tests
- 66 tests, 모두 PASS (v0.2.12 의 59 → 66, 7 신규: Adrenaline / Draw / Override / Orb / Combo)

## v0.2.12 (2026-05-15)

- **Conditional damage 정확도** — CardReflection 이 CanonicalVars 대신 *runtime DynamicVars* 사용 (upgraded values + modifier-aware).
- **PreviewValue 활용** — UpdateDynamicVarPreview 호출 후 PreviewValue read. Strength buff + Vulnerable + conditional extras 적용된 정확값.
- **CalculatedDamageVar 우선 처리** — Base + Extra × multiplier 의 final 값을 단일 source 로 (double-count 방지).
- AshenStrike (CalculatedBase + ExtraDamage × exhaust pile count) 같은 conditional 카드 정확 평가.

## v0.2.11 (2026-05-15)

- **Bug fix**: Vakuu 가 사용불가 카드 (Curse/Unplayable) plays 시도하는 문제. SimCard.IsPlayable + Snapshotter 가 card.CanPlay() read + VakuuExecutor final check.
- **Test button visibility fix**: viewport-relative 절대 좌표 + ZIndex 1000 + Modulate 색상.
- **TestButtonPoller**: Harmony _Ready hook 실패 시 0.5초 마다 retry 안전망.
- **Build synergy** (캐릭터 매커니즘 인식):
  - extract_card_triggers.py 가 모든 axes + build 멤버십 추출 (263→576 entries)
  - BuildSynergy.cs — Producer + Amplifier/Consumer combo 인식 (POISON, ORB, SKELETON 등)
  - 같은 build 카드 N장 commitment bonus
- **Enemy DoT awareness**: PoisonAmount / ConstrictAmount / BurnAmount 인식. 이미 죽어가는 적 (DoT ≥ HP/2) 에 attack overkill penalty.
- **Player Stars**: SimState.PlayerStars (Regent/Watcher 자원).
- 59 unit tests (6 신규).

## v0.2.10 (2026-05-14)

- **카드 ID 하드코딩 제거** — `SelectorMode.cs` 의 13줄 BoostCards HashSet 삭제.
- **cards_catalog.json 활용** — 263 카드 trigger 정보 (axes, keywords, description-derived flags) 추출해 mod 에 embed.
- `scripts/extract_card_triggers.py` — 게임 패치 후 재실행하면 catalog 자동 갱신.
- `Data/CardCatalog.cs` — embedded JSON 로드 + lookup. ID 기반 mode/keyword 분류.
- `SelectorModeCatalog` 가 catalog 의 `upgrade_trigger` / `fetch_trigger` / `DRAW_PILE_SEARCH` 등 axes 로 자동 mode 결정.
- 53 unit tests (catalog-loaded sanity + Anointed boost detection 추가).

## v0.2.9 (2026-05-14)

- **Enemy state-aware target priority**:
  - Vulnerable target → +500 (kill before window closes)
  - Strength-buffed target → +400 (kill before they hit harder)
  - Frail target → +200 (AOE/burst payoff)
- **Enemy Artifact recognition** — debuff PowerApps blocked stack-by-stack.
- **Ritual / EnragePower / FeralPower detection** — snowballing enemies get +800 kill priority.
- **Pile-aware Draw scoring** — empty pile → -1000 penalty, tiny pile (≤2) → bonus halved.
- 52 unit tests (6 new: enemy state + ritual + pile).

## v0.2.8 (2026-05-14)

- **Smart card selector** — Vakuu 의 mid-play prompt (discard X / exhaust X / upgrade) 자동 응답.
  - Default Burn mode: 가치 낮은 카드 우선 (discard/exhaust 가정)
  - Boost mode (Apotheosis 같은 upgrade-trigger 카드): 가치 높은 카드 우선
  - VakuuExecutor.CurrentSnapshot + CurrentPlayingCardId 로 context inference
  - Mode catalog 확장 가능 (게임 테스트 시 정확도 ↑)
- 46 unit tests (Selector + Mode 추가)

## v0.2.7 (2026-05-14)

- **SmartCardSelector logic** — PlanScorer 기반 worst/best 정렬.
- VakuuCardSelector Harmony patches (GetSelectedCards + GetSelectedCardReward).
- 42 unit tests.

## v0.2.6 (2026-05-14)

- **Energy waste avoidance** — damage ≤ target.Block → -2000; block under no threat → -800.
- **Energy gain card recognition** — 부족할 때 +1500, 낭비될 때 -500.
- **Power fight-length context** — short fight -500, long fight +500.
- **Draw card hand-best-score rule** — hand 의 best card 약하면 draw 우선 (수혈).
- 38 unit tests.

## v0.2.5 (2026-05-14)

- **Forward simulator + depth-2 lookahead** — AnalyticalSimulator.ApplyCardPlay 구현 (Strength/Block 누적, attached debuff, AOE).
- SimEnemy/SimCard → record (with-expression 가능). SimState.DeepClone().
- ActionPlanner 의 PlanNextStep 가 first + best-second 합으로 카드 선택.
- 30 unit tests.

## v0.2.4 (2026-05-14)

- **Modifier-aware damage** — Strength/Vulnerable/Weak/Frail/Dex multiplier 적용 effective damage / block.
- CombatReflection.GetPowerAmount(creature, "PowerName").
- StatusMath 헬퍼.
- 23 unit tests.

## v0.2.3 (2026-05-14)

- **Diminishing returns** on power stacks (1× → 4× cap, 70% per stack).
- **HandSynergy** — Inflame + N attacks → Inflame 점수 부스트.
- 18 unit tests.

## v0.2.2 (2026-05-14)

- Score breakdown logging (per-component detail in log).
- **AOE damage scoring** — alive enemy 수 비례.
- 14 unit tests.

## v0.2.1 (2026-05-14)

- **PowerCatalog** — 65+ explicit + heuristic fallback.
- Self-buff vs Enemy-debuff lookup split.
- Diminishing returns 도입 (4x cap).

## v0.2.0 (2026-05-14)

- **CardEffectSummary** 추출 — Damage / Hits / Block / PowerApps via DynamicVars.
- SimCard 확장.
- PlanScorer damage / block / power 효율 기반 평가.

## v0.1.3 (2026-05-14)

- **Playstyle system** — Defensive / Balanced / Aggressive / Killer 4 preset.
- PlanScorerWeights 추출, in-combat cycle 버튼.

## v0.1.2 (2026-05-14)

- **IsBoss / IsElite / IsMinion** flag 자동 분류 (RoomType + spawnedThisTurn + HP heuristic).
- Lethal range tier bonus (effective HP ≤ 6/12/20).

## v0.1.1 (2026-05-14)

- **17 intent types** 분류 (AttackIntent + 16종).
- SimEnemy 의 ThreatLevel + intent flags.
- 적 intent 차등 우선순위 (Buff > Heal > Summon > 일반).

## v0.1.0 (2026-05-14)

- Initial scaffold + Harmony Prefix on WhisperingEarring.BeforePlayPhaseStartLate.
- Step-greedy planner with simple heuristic.
- VakuuExecutor + Test 버튼.
