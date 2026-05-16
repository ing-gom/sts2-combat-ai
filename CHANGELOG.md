# Changelog

## v0.5.0 (2026-05-16)

**카드 사용순서 정확도 향상 — 시뮬레이터/스코어러 정합성 정리.** 게임 로직은 동일하지만 plan
이 실제 in-game 결과와 더 가까워지도록 다수의 sim/scoring 버그 수정 + 누락된 효과 보강.

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
- **EnergizedPower** 즉시 에너지 게인으로 처리 (sim 적용 + EvaluateEnergyGain unlock 로직).
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
