# Changelog

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
