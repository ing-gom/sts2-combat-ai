# Changelog

## v0.7.37 (2026-05-18)

**Self-harm trigger preview — InfernoPower / RupturePower.**

HP_LOSS_SELF 카드 play 시 InfernoPower 활성 → AOE 6 자동 trigger.
RupturePower 활성 → 영구 +1 Str trigger. 이 정보 정적 baseline 에 묻혀
있어서 같은 HP_LOSS 카드도 Inferno/Rupture 보유 시 가치 급상승 못 봤음.

### 변경

`ApplySelfHarmTriggerPreview(card, state)` 신규 — HpLossAmount > 0 또는
HP_LOSS axis 카드에서 호출. PlayerPowers 읽어서:
- InfernoPower N → AOE 6×N × aliveCount × DamagePerPoint
- RupturePower N → turns × 3 attacks × N × DamagePerPoint

순수 현재 visible state, 미래시 X.

---

## v0.7.36 (2026-05-18)

**적 passive (PlatedArmor / Metallicize / Regen) RemainingTurns 통합.**

### 배경

`RemainingTurnsEstimator` 가 적의 effective HP 만 보고, **매 턴 자동 회복**
효과 무시. 결과:
- PlatedArmor 4 적: 매 턴 우리 damage 4 흡수 → 실제 fight 더 길어짐
- Regen 적: 매 턴 HP 회복 → 사실상 우리 DPT 감소

이 정보는 게임이 보여주는 결정론적 효과 (visible state). 미래시 아님.

### 변경

`RemainingTurnsEstimator.From()` 에 두 항목 추가:

```csharp
int enemyAutoBlock = Σ(PlatedArmor + Metallicize) per alive enemy
int enemyRegen    = Σ(RegenPower) per alive enemy
netDpt = max(0, playerDpt - enemyAutoBlock - enemyRegen)
totalDpt = netDpt + dot
estimate = effectiveEnemyHp / totalDpt
```

Helpers 노출: `EnemyAutoBlock(e)`, `EnemyRegen(e)` — 다른 consumer 도 사용 가능.

### 영향

| 시나리오 | 이전 turns | v0.7.36 turns |
|---|---:|---:|
| Boss HP 200, 우리 DPT 30 | 6 | 6 (변화 없음) |
| Boss HP 200, DPT 30, PlatedArmor 5 | 6 | **8** (DPT 25 적용) |
| Boss HP 200, DPT 30, Regen 5 + PlatedArmor 5 | 6 | **10** (DPT 20) |
| Boss HP 200, DPT 30, PlatedArmor 25+ (Barricade boss) | 6 | **10 (cap)** |

RemainingTurns 증가는 propagation 통해 모든 turns-based handler 의 가치 증폭
(MAYHEM/STAMPEDE/PYRE/POWER-TICK 등).

---

## v0.7.35 (2026-05-18)

**Player-side DoT 통합 → survival check.**

기존 EnemyTurnSimulator 의 leak 계산은 enemy intent damage 만. PlayerBurn /
PlayerPoison / PlayerConstrict stack 이 있어도 무시 → 이번 턴 자해 lethal
인식 못함.

### 변경
1. SimState 에 PlayerPoison / PlayerBurn / PlayerConstrict 필드 추가
2. StateSnapshotter 가 player.Powers 에서 reflection 으로 추출
3. EnemyTurnSimulator.PredictPlayerDmg / PredictRawLeak 에 DoT tick 가산
   - DoT 는 block bypass — 항상 추가
   - Intangible 도 bypass (hit-cap 만 적용, DoT 는 별개)

### 예시
| 상황 | 이전 | v0.7.35 |
|---|---:|---:|
| HP 8, block 0, incoming 4, Burn 3 | leak 4 (Moderate) | leak 7 (Heavy) |
| HP 5, block 8, incoming 4, Constrict 2 | leak 0 (None) | leak 2 (Moderate) |
| HP 4, block 0, incoming 0, Burn 5 | leak 0 (None) | leak 5 (Fatal) |

---

## v0.7.34 (2026-05-18)

**Thorns 자해를 survival check 에 통합.**

### 배경

`thornsPenalty` 는 attack score 에 직접 차감되어 있었지만 **survival urgency
재산정** 에는 안 들어감. 결과:
- HP 8 + incoming 4 (Moderate) → 5-thorns 적 상대 2-hit 공격 → 자해 10
- effHp = -2 → 사실상 self-kill 인데, 기존 penalty 는 -1000 만

### 변경

`ComputeSelfDamagePenaltyWithThorns(card, state, lethalThisTurn, thornsDamage)`
헬퍼 추가. Attack branch 의 thornsDamage 를 hpLoss 와 합산해서 urgency
재평가 (v0.7.33 의 self-damage 모델 확장).

```csharp
int hpLoss = card.HpLossAmount + thornsDamage;
if (hpLoss >= state.PlayerHp) return -2000;  // self-kill
var effUrg = GetEffectiveUrgency(state, hpLoss);
// jump-비례 penalty (v0.7.33 로직 동일)
```

Skill / Power branch 는 thornsDamage=0 passthrough.

### 효과 예시

| 상황 | 이전 | v0.7.34 |
|---|---:|---:|
| HP 8 + incoming 4, 5-thorns 적 2-hit | thorns-1000 | thorns-1000 + selfDmg-1000 (Heavy→Fatal) |
| HP 4 + incoming 3, 2-thorns 적 3-hit (자해 6) | thorns-600 | thorns-600 + selfDmg-2000 (self-kill) |
| Lethal-this-turn vs thorns 적 | thorns-N | 0 (combat 종료 bypass) |

---

## v0.7.33 (2026-05-18)

**HP-loss aware survival check — 현재 턴 정밀화 Phase 1.**

### 배경

기존 `EnemyTurnSimulator.GetSurvivalUrgency` 는 enemy intent → 현재 block
계산만. 카드 자체의 self-damage (Spite/Inferno trigger/Doom self) 는
별도 평가가 없어 self-induced death 위험 무시.

예: HP 8 + incoming 4 = leak 4 (Moderate). 그 상태에서 HP_LOSS 4 카드 play
하면 effHp 4 → effLeak 4 = Fatal. 기존 모델은 이 추가 위험 못 봄.

### 변경

1. `GetEffectiveUrgency(state, extraHpLoss)` 헬퍼 추가
   - card.HpLossAmount 만큼 effHp 줄여서 urgency 재산정
2. `ComputeSelfDamagePenalty(card, state, lethalThisTurn)` 헬퍼 추가
   - 카드가 urgency tier 를 올리면 jump 크기 비례 페널티
   - Fatal 로 promote → -1000 × jump
   - Heavy 로 promote → -300 × jump
   - lethalThisTurn 면 무시 (combat 종료)
3. Attack / Skill / Power 세 branch 의 survival check 에 wiring

### 효과 예시

| 상황 | 이전 | v0.7.33 |
|---|---:|---:|
| HP 12 + incoming 4 (None) → HP_LOSS 4 (effHp 8, leak 4 → Moderate) | 0 | -300 |
| HP 8 + incoming 4 (Moderate) → HP_LOSS 4 (effHp 4, leak 4 → Fatal) | 0 | -2000 (2 jumps) |
| HP 8 + incoming 6 (Heavy) → HP_LOSS 0 (변화 없음) | 0 | 0 |
| HP 4 + lethal-this-turn → HP_LOSS 3 | 0 | 0 (lethal bypass) |

---

## v0.7.32 (2026-05-18)

**Defect orb stem — 7 handlers.**

| Power | Tier | Scaling factor |
|---|---|---|
| CapacitorPower | B | orb-saturation × turns × PerOrbValue |
| CoolantPower | A | turns × FrostOrbs × BlockPerFrost |
| SpinnerPower | A | turns × FrostOrbValue |
| ThunderPower | A | projected Lightning evokes × 6 dmg × 50 |
| LoopPower | D | turns × PassiveBonusPerTurn |
| ConsumingShadowPower | D | turns × NetPerTurn + DarkOrb bonus |
| HailstormPower | C | turns × frost-rate × alive × 6 dmg |

핵심 gate: **PlayerOrbCapacity == 0** (Defect 비-활성) 시 모두 baseline 차감.

### 누적 coverage (v0.7.27 ~ v0.7.32)

| 버전 | 묶음 | 누계 dynamic |
|---|---|---:|
| v0.7.26 | per-turn baseline | 13 |
| v0.7.27 | Shiv (Silent) | 18 |
| v0.7.28 | Star (Regent) | 23 |
| v0.7.29 | Forge (Regent) | 28 |
| v0.7.30 | Doom/Volatile (Necrobinder) | 33 |
| v0.7.31 | cross-character | 38 |
| **v0.7.32** | **Orb (Defect)** | **45 (40%)** |

---

## v0.7.31 (2026-05-18)

**Cross-character impact Powers — 5 handlers.**

| Power | Char | Tier | Scaling factor |
|---|---|---|---|
| PyrePower | Ironclad | B | (turns − 1) × EnergyValue |
| InfernoPower | Ironclad | A | HP_LOSS × alive × 6 × 50 |
| AutomationPower | Shared | A | (turns × 5 / 10) × EnergyValue |
| OutbreakPower | Silent | D | (POISON × turns / 4 / 3) × alive × 11 |
| PaleBlueDotPower | Regent | B | turns × draw-rate × PerDrawValue |

Gates:
- Inferno: HP_LOSS 카드 또는 적 없으면 baseline 차감
- Outbreak: POISON_PRODUCER 또는 적 없으면 baseline 차감

### v0.7.27~v0.7.31 묶음 결과

| 버전 | 묶음 | dynamic coverage |
|---|---|---:|
| v0.7.26 | per-turn baseline | 13 |
| v0.7.27 | Shiv stem (Silent) | 18 |
| v0.7.28 | Star stem (Regent) | 23 |
| v0.7.29 | Forge stem (Regent) | 28 |
| v0.7.30 | Doom/Volatile (Necrobinder) | 33 |
| **v0.7.31** | **cross-character 잔여** | **38** |

**최종**: 112 unique STS2 Powers 중 **38 (34%) dynamic delta + 8 추가 layer
(activation penalty + forward sim) = 46 (41%) non-flat coverage**.

남은 ~66 powers 는 두 부류:
1. State-independent 영구 버프 (Inflame/BulkUp/Footwork 등) — flat 으로 충분
2. 하위 tier 또는 niche 메커니즘 — 추가 작업 가능 (Coolant/Capacitor/Loop/
   Hailstorm/Iteration/SmokeStack/Friendship/Caltrops/Juggernaut 등)

---

## v0.7.30 (2026-05-18)

**Doom / Volatile stem — 5 handlers (Necrobinder).**

| Power | Tier | Scaling factor |
|---|---|---|
| CountdownPower | A | turns^2 (Doom compound) × 50 |
| RupturePower | A | HP_LOSS card count × Str-lifetime-value |
| PagestormPower | S | Volatile count × turns × per-draw / pile-size |
| LethalityPower | S | turns × avg first attack × 0.5 amp |
| DemesnePower | S | turns × NetPerTurn |

핵심 gate:
- Countdown: alive attack-target 없으면 baseline 차감
- Rupture: HP_LOSS 카드 없으면 baseline 차감
- Pagestorm: Volatile 카드 없으면 baseline 차감

---

## v0.7.29 (2026-05-18)

**Forge stem Power passives — 5 handlers (Regent Lord's Blade).**

| Power | Tier | Scaling factor |
|---|---|---|
| FurnacePower | C | turns × PerForgeValue (blade 부재: 차감) |
| HammerTimePower | A | turns × forgeRate × PartyForgeValue |
| SeekingEdgePower | C | turns × Forge + (alive enemies − 1) × AOE bonus |
| SwordSagePower | C | blade plays × extra-hit bonus |
| ParryPower | C | blade plays × 10 block × 30 |

핵심 gate: **SovereignBladeCount == 0 시 baseline 차감**. Lord's Blade 없는
deck 에서 Forge stem 무의미.

---

## v0.7.28 (2026-05-18)

**Star stem Power passives — 5 handlers (Regent archetype).**

| Power | Tier | Scaling factor |
|---|---|---|
| GenesisPower | B | turns × PerStarValue (no consumer: 차감) |
| OrbitPower | B | (turns × 3 / 4) × PerStarValue (energy spend rate) |
| BlackHolePower | B | (producer + consumer plays) × aliveEnemies × 3 dmg |
| ChildOfTheStarsPower | S | consumer plays × BlockPerStar × 30 |
| TheSealedThronePower | S | card-play rate (4/turn) capped by consumer throughput |

핵심 gate: **STAR_CONSUMER 부재 시 baseline 차감**. Star 생성만 있고 소비 없으면
자원 inflation = 가치 없음.

---

## v0.7.27 (2026-05-18)

**Shiv stem Power passives — 5 handlers (Silent archetype).**

| Power | Tier | Scaling factor |
|---|---|---|
| AccuracyPower | A | projected Shivs × +N stack × DamagePerPoint |
| PhantomBladesPower | A | turns × (first-Shiv +9 + Retain savings) |
| FanOfKnivesPower | C | (alive enemies − 1) × projected Shivs × ShivDmg |
| MasterPlannerPower | C | turns × skill-ratio × discards × free-play-value |
| InfiniteBladesPower | A | turns × ShivValue + consumer-presence bonus |

**예외 처리**:
- PhantomBlades: Shiv 없는 deck → entire baseline 차감 (party 멤버 비-Silent)
- FanOfKnives: 단일 적 → entire baseline 차감 (extra hit = 0)
- MasterPlanner: 스킬 없는 deck → entire baseline 차감

---

## v0.7.26 (2026-05-18)

**Per-turn Power passive dynamic delta — 8 handlers added.**

### 배경

`EffectSynergy` 의 power-passive 동적 delta 가 MAYHEM/STAMPEDE/CALAMITY/
HELLRAISER/JUGGLING 5종 외 미커버. **매 턴 / 트리거 기반 Power** 들은
PowerCatalog flat 값만 받아 deck-state 와 무관 동일 점수.

특히 다음 8종이 deck 구성에 강하게 의존하면서 dynamic 처리 없음:
DarkEmbrace, Vicious, Accelerant, Envenom, Subroutine, PrepTime, Storm,
ToolsOfTheTrade.

### 변경

기존 MAYHEM delta pattern (`delta = clamp(state_derived - baked, -baked, Cap)`)
8종 추가. CardId dispatch 확장 + 각 handler 작성.

```csharp
else if (card.Id == "CARD.DARK_EMBRACE")  ApplyDarkEmbraceTickValue(...);
else if (card.Id == "CARD.VICIOUS")        ApplyViciousTickValue(...);
else if (card.Id == "CARD.ACCELERANT")     ApplyAccelerantTickValue(...);
else if (card.Id == "CARD.ENVENOM")        ApplyEnvenomTickValue(...);
else if (card.Id == "CARD.SUBROUTINE")     ApplySubroutineTickValue(...);
else if (card.Id == "CARD.PREP_TIME")      ApplyPrepTimeTickValue(...);
else if (card.Id == "CARD.STORM")          ApplyStormTickValue(...);
else if (card.Id == "CARD.TOOLS_OF_THE_TRADE") ApplyToolsOfTheTradeTickValue(...);
```

### 결과 — `scripts/_inspect_v0_7_26.py`

| Handler | deck 구성 | delta |
|---|---|---:|
| DarkEmbrace | exhaust 5+8 / 5턴 | **+900** (cap) |
| DarkEmbrace | exhaust 없음 | **-500** (baseline 차감) |
| Vicious | VULN_PRODUCER 2+4 | +680 |
| Vicious | Vuln 없음 | -400 |
| Envenom | attack 4+12 | +800 |
| Subroutine | Power 4+6 (Defect 가속) | **+1500** (cap) |
| Subroutine | Power 0 | -500 |
| PrepTime | 8턴 fight | +600 |
| PrepTime | 1턴 lethal | -300 (baked > tick) |
| Tools | 8턴 fight | +1200 |
| Storm | Power 4+6 | +270 |
| Accelerant | Poison-producer 3+5 | +460 |

### 커버리지 향상

| | v0.7.25 | v0.7.26 |
|---|---:|---:|
| Power passive dynamic delta | 5 | **13** |
| Power passive 정적 (PowerCatalog only) | ~40+ | ~32+ |

다음 후보: ChildOfTheStars, Storm, OrbitPower, BlackHole, Pyre, HelloWorld,
RollingBoulder, PaleBlueDot, Outbreak, PhantomBlades, Capacitor, MonarchsGaze.

---

## v0.7.25 (2026-05-18)

**Weak scoring — non-attack-intent coverage + dynamic turn cap.**

### 배경

v0.7.24 까지 `ComputeWeakSavings` 의 2가지 한계:

1. **Non-attack-intent 적 무시**: enemy.HasAttackIntent 만 통과. Buff/heal/defend
   중인 적은 Weak 가치 0 계산. 하지만 Weak stack 은 1턴/소멸이라 다음 턴 공격
   intent 일 때 여전히 유효.
2. **턴 cap 2 고정**: 4-stack Weak 같은 high-stack power 의 long-fight 가치
   불완전 평가. 1턴 lethal 상황에서도 cap=2 면 over-estimate.

### 변경

```csharp
// Cap by min(stacks, RemainingTurnsEstimator, hardCap=4)
int turnCap = min(weakStacks, min(remainingTurns, WeakSavingsTurnCap));

foreach (var e in state.Enemies) {
    bool currentTurnAttacks = e.HasAttackIntent
                            || (e.HasDeathBlowIntent && e.IntentDamage > 0);
    bool futureIntentAttack = !currentTurnAttacks
                            && (e.HasBuffIntent || e.HasDebuffIntent
                                || e.HasHealIntent || e.HasDefendIntent
                                || e.HasSummonIntent || e.HasStatusIntent);
    if (!currentTurnAttacks && !futureIntentAttack) continue;

    // perHit/hits: 현재 attack intent 면 실측, 아니면 baseline 8×1
    // effectiveTurns: 첫 턴이 lapse 인 future-intent 의 경우 -1
    // contribution: future-intent 의 경우 1/2 (불확실성)
}
```

### 결과

`scripts/_inspect_v0_7_25.py` (9 시나리오):

| 시나리오 | 점수 | 변화 |
|---|---:|---|
| multi-hit (8×4) attacker, Weak 2 | 480 | 변화 없음 |
| multi-hit attacker, **Weak 4** | **960** | cap=4 적용, 2× |
| BIG single-hit (40×1), Weak 2 | 600 | 변화 없음 |
| **Buff 중 (Weak 2)** | **30** | 0 → 30 (다음 턴 공격 반영) |
| Buff 중 (Weak 1, 완전 lapse) | 0 | 0 (정답) |
| Defend 중 (Weak 3) | 60 | 0 → 60 |
| **1턴 lethal, Weak 3** | **240** | cap=1 적용으로 자연 down-scale |
| Inert (stun) | 0 | 0 (정답) |

---

## v0.7.24 (2026-05-18)

**Future attack potential scaling for Vulnerable.**

### 배경

v0.7.23 의 survival probability 는 "이 공격이 적을 죽이면 Vuln 가치 0" 만
처리. 하지만 **"Vuln 걸었는데 다음 턴 공격카드가 없어 못 쓰는 케이스"** 미처리.

Pure-skill 덱 (Hexaghost shutout 등) 이나 attack-light 덱에서 Vuln 가치
overestimate.

### 변경

PowerApps 루프에서 `IsAttackDependentDebuff(powerName)` 체크 후 future
attack ratio 로 추가 스케일링.

```csharp
double futureAttackMult = ComputeFutureAttackMultiplier(state, card);
//   ratio = attacks / (hand∪draw∪discard size, self 제외)
//   mult = min(1.0, ratio / 0.3)

if (IsAttackDependentDebuff(powerName) && futureAttackMult < 1.0)
    perEnemy = (int)(perEnemy * futureAttackMult);
```

대상 powers: `VulnerablePower`, `DarkShacklesPower`.

명시 제외:
- `WeakPower/FrailPower/ShacklingPotion/Dampen/EnfeeblingTouch` — 적 행동
  의존 (우리 공격과 무관)
- `Poison/Constrict/Rupture/NoxiousFumes` — DoT 자동 트리거
- `Hex/Confused/PiercingWail` — 다른 경로

### 결과

`scripts/_inspect_v0_7_24.py`:

| 덱 구성 | atk ratio | mult | Bash Vuln 가치 |
|---|:---:|:---:|---:|
| Attack-heavy (50%) | 0.50 | 1.0× | 623 |
| Balanced (30%) | 0.30 | 1.0× | 623 |
| Skill-heavy (15%) | 0.15 | 0.5× | 311 |
| Pure-skill (0%) | 0.00 | 0.0× | **0** |

Pure-skill 덱에서 Vuln 가치 정확히 0 처리.

---

## v0.7.23 (2026-05-18)

**Survival probability scaling + lethal-mode setup attack penalty.**

### 배경

기존 PlanScorer Attack branch 는 Bash 같은 setup attack (low dpe + debuff)
의 PowerCatalog 값 (VulnerablePower 850) 을 무조건 flat 가산. 결과:
- 적 18 HP: Bash(8 dmg + Vuln 2) + Strike(9 dmg) = 17 ≠ kill
- vs Strike × 3 = 18 dmg = kill
- 점수상 Bash + Strike 가 우위라도 실제론 Strike × 3 정답

문제: Vuln 의 future-turn 가치가 적이 죽으면 0인데 그 보정 없음.

### 변경 — 2 layer

#### Layer 1: Survival probability scaling

PlanScorer Attack branch 의 PowerApps 루프에서 enemy debuff 점수에 survival
확률 곱셈. 적이 이 공격에 죽으면 future-turn 가치 0.

```csharp
double survivalRatio = (effHp - effectiveTotal) / effHp;
// floor 0.15: 죽지 않으면 minimum 가치 보존 (chain attack 가치)
if (0 < survivalRatio < 0.15) survivalRatio = 0.15;
// kill case (effectiveTotal >= effHp): survivalRatio = 0 (full waste)

perEnemy = (int)(PowerCatalog.ValueEnemyDebuff(name, amt) * w.AttachedDebuffMultiplier
                  * survivalRatio);
```

AOE 의 경우 alive enemies 의 HP 잔여 비율 평균.

#### Layer 2: Lethal-mode setup attack penalty

`IsSetupAttackCard(card)`: Attack + (VULN/WEAK/FRAIL_PRODUCER axis 보유) + dpe < 5.5.

```csharp
if (lethalThisTurn && IsSetupAttackCard(card))
    lethalSetupPenalty = w.LethalModeNonAttackPenalty * 6 / 10;  // 60%
```

### 검증 (`scripts/_inspect_v0_7_23.py`)

```
적 30 HP, non-lethal:           A=1113   B= 630   → A (Vuln 가치 큼) ✅
적 18 HP, non-lethal:           A= 962   B= 630   → A (multi-turn Vuln) ✅
적 18 HP, lethalThisTurn:       A= 662   B= 630   → A (margin 좁아짐)
적 16 HP, lethal A도 kill:      A= 615   B= 630   → B (overkill setup) ✅
적  8 HP, Bash 단독 kill:       A= 190   B= 630   → B (Vuln 완전 waste) ✅
```

이전 모든 시나리오 A 압도적 우위 → 이제 적 HP 작아질수록 B 자연스럽게 우위
로 전환. cost-효율 (Strike × 3 = 18 dmg, Bash + Strike = 17 dmg) 정확 인지.

### 영향

- **자해 / overkill setup 회피**: 적 죽일 수 있는데 Bash 깐다 → 자동 deprioritise
- **multi-turn Vuln 보존**: 적 HP 큼 → Vuln 잔존 가치 살아있음 → 콤보 우대
- **dynamic balance**: lethal-this-turn flag + survival ratio + setup penalty 가
  자동 조합

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_v0_7_23.py`: 5 시나리오 모두 예상치.

## v0.7.22 (2026-05-18)

**Power activation condition penalties — S+ Power cards 의 조건부 가치 인지.**

### 배경

PowerCatalog 가 BarricadePower 1200, EchoFormPower 1500 등 절대값 큰 점수
부여. 하지만 다음 조건들 미충족 시 실제 활성 가치는 훨씬 낮음:
- EchoForm 마지막 카드로 깔면 → 이번 턴 echo 0
- Barricade 0 block → 운반할 게 없음 (다음 턴부터 활성)
- MachineLearning 손 cap (10) → 추가 draw 못 받음
- Cruelty Vuln 적 없고 hand 에 Vuln producer 도 없음 → +25% 부스트 안 됨

기존엔 fightCtx 가 부분적으로 short-fight 만 보정. 이번 핸들러는 **board state
specific 조건** 점수화.

### 변경 (`PlanScorer.ComputePowerActivationPenalty`)

PlanScorer Power branch 의 PowerCatalog credit 직후 호출. 4가지 검사:

#### 1. EchoForm / Burst
```
energyAfter = state.PlayerEnergy - card.Cost
playablesAfter = count(other playable cards with cost <= energyAfter or cost == 0)
if playablesAfter == 0: penalty -= 400
```
첫 echo 가 다음 턴으로 deferred → 30% 가치 감산 (1500 → 1100).

#### 2. Barricade
```
if state.PlayerBlock == 0 && !HasBlockSourceInHand(hand): penalty -= 200
```
운반할 block 도, 만들 카드도 없으면 1200 → 1000. 다음 턴 block 빌드 시
활성하지만 지연.

#### 3. MachineLearning
```
if state.Hand.Count >= 10: penalty -= 250
```
손 가득이면 draw 못 받아 wasted → 900 → 650.

#### 4. Cruelty
```
if !anyVulnEnemy && !HasVulnProducerInHand(hand): penalty -= 200
```
Vuln 적용 못 받으면 25% 부스트 0 → 600 → 400.

### 검증 (`scripts/_inspect_v0_7_22.py`)

```
=== EchoForm ===
normal play (cards left to echo)            penalty=  +0  net=1500
LAST card, 0 energy, 0 other plays          penalty= -400 net=1100
0-cost echoform-class with cards            penalty=  +0  net=1500

=== Barricade ===
normal: 10 block already                    penalty=  +0  net=1200
0 block + Defend in hand                    penalty=  +0  net=1200
0 block + NO block cards                    penalty= -200 net=1000

=== MachineLearning ===
normal: hand 5 cards                        penalty=  +0  net=900
nearly full: 9 cards                        penalty=  +0  net=900
AT cap: 10 cards                            penalty= -250 net=650

=== Cruelty ===
normal: Vuln target available               penalty=  +0  net=600
Bash in hand (Vuln producer)                penalty=  +0  net=600
NO Vuln + NO producer (wasted now)          penalty= -200 net=400
```

### 의도

- **조건부 가치 인지** — Power score 가 실제 활용도와 일치
- **타이밍 가이드** — Barricade 는 block 카드 함께 들렸을 때 / EchoForm 은
  energy 있을 때 우선 선택
- **보수적 페널티** — 활성 가능성이 있으면 0 페널티 (HasBlockSource /
  HasVulnProducer / playablesAfter > 0 등 분기로 conditions met 인지)

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_v0_7_22.py`: 4 카드 × 3 시나리오 모두 예상치.

## v0.7.21 (2026-05-18)

**Combat length estimator 정교화 + CORRUPTION cost-reduction + DOOM
self-risk handler.**

### 1. RemainingTurnsEstimator 정교화

기존 `enemy_hp / playerDpt` clamp [1,10] 단순 모델을 다음 세 차원으로 확장:

#### Effective enemy HP
```
eff_hp = sum(alive enemy hp + block / 2)
```
적 block 의 50% 만 HP-equivalent 로 카운트 (block 은 매 턴 reset/regen 되므로
single-hit absorption 의 평균 가치).

#### DoT parallel damage stream
```
total_dot = sum(PoisonAmount + ConstrictAmount + DoomAmount)
total_dpt = playerDpt + total_dot
```
Poison/Constrict/Doom 이 매 턴 enemy HP 차감 → playerDpt 와 합산. 자해 안 하는
독 빌드도 컴뱃 길이 단축 인지.

#### EstimatePlayerDpt 확장
- **Strength projection** — `DemonFormPower / RitualPower / ArsenalPower` 보유 시
  미래 Strength 성장 가산. DemonForm N → +N str/turn 누적
- **Vulnerable multiplier** — alive enemies 의 Vuln 비율에 따라 ×1.0~1.5
  (1 of 1 → 1.5×, 1 of 2 → 1.25×)
- **Player Weak** — outgoing damage ×0.75 (Weak 적용)

#### 효과
시나리오별 turn estimate 변화 (`scripts/_inspect_v0_7_21.py`):
```
boss 250hp, no buffs                                       10
boss 250 + DemonForm 2 (str grow)                          10 (capped)
boss 250 + Vuln (×1.5 dmg)                                 10 (capped)
boss 250 + Poison 10/turn (DoT)                            10 (capped)
composite: Vuln + Poison + Str 4                            6
```

Single-buff 들은 starter-vs-boss 시나리오에선 cap (10) 에 묶여 큰 차이 안 나지만
**stack 조합 시 6 으로 단축** — Power 패시브 가치 비례 조정 ↓.

### 2. CORRUPTION / global cost-reduction

`CorruptionPower` (Ironclad S+): 모든 Skill 카드 combat-wide 0-cost (+ Exhaust).
기존: 게임에서 적용되지만 시뮬레이터는 cost 그대로 차감 → 손해 시뮬.

#### 변경
- **`ActionPlanner.EnumerateCandidates`** — `state.PlayerPowers["CorruptionPower"] > 0`
  + `card.IsSkill` 시 energy check 우회 (`corruptionFreeSkill` flag)
- **`AnalyticalSimulator.ApplyCardPlay`** — 같은 조건에서 energy 차감 안 함
- per-card `FreeSkillPower` counter 와 달리 persistent (decrement 안 함)

#### 효과
CORRUPTION 활성 후:
- 3-cost Skill (e.g., GHOSTLY_ARMOR) 도 후보로 enumerated
- depth=3 beam 이 Skill 다수-play 시나리오 평가
- ENLIGHTENMENT (이미 처리) 와 같은 패턴

### 3. DOOM self-risk handler

Necrobinder `DOOM_SELF_PRODUCER` 카드 (NEUROSURGE 등 2장) — 플레이어에게 Doom
스택 추가. 매 턴 stack 만큼 HP 차감 (enemy Doom 와 동일 메커니즘).

#### 변경
- **`SimState.PlayerDoom`** — DoomPower stack on player
- **`StateSnapshotter`** — `creature.Powers.DoomPower` 캡쳐
- **`AnalyticalSimulator.AdvanceTurn`** — `newPlayerHp -= state.PlayerDoom` tick
- **`EffectSynergy.ApplyDoomSelfRisk`** 핸들러 — DOOM_SELF_PRODUCER 축 카드
  점수에 위험 페널티

#### 페널티 공식
```
newDoom = currentDoom + cardDoomDelta
projectedHpLoss = newDoom × RemainingTurnsEstimator
if projectedHpLoss > playerHp / 2:
    penalty = clamp(-projectedHpLoss × 5, -500, 0)
```

#### 검증
```
low Doom + 1 added, HP 70                      penalty=  +0
Doom 3 + 2 added (proj 5×5=25, < 35 threshold) penalty=  +0
Doom 5 + 2 added, HP 40 (proj 7×5=35 > 20)    penalty=-175
Doom 9 + 2 added, HP 20 (proj 11×4=44)        penalty=-220
```

낮은 Doom 은 무시, HP 의 50% 이상 위협 시 점수 페널티. Necrobinder 자해
빌드의 한도 인지.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_v0_7_21.py`: 7 turn-estimate 시나리오 + 5 doom-risk
  시나리오 모두 예상치.

## v0.7.20 (2026-05-18)

**BuildSynergy: role_needs.json 기반 cross-axis lookup.**

### 배경

기존 `BuildSynergy.Compute` 는 suffix-only 매칭으로 pair-stem 만 인식:
- `POISON_PRODUCER ↔ POISON_AMPLIFIER` → +250
- `POISON_PRODUCER ↔ POISON_CONSUMER` → +200

cross-mod audit 에서 CardAdvisor 의 `role_needs.json` 은 **283 cross-axis
hooks** 를 추가로 가지고 있음을 발견 (POISON_PRODUCER → DRAW w=0.8,
FORGE_PRODUCER → BLOCK w=1.2 등). 이 패스에서 CombatAI 가 같은 데이터
참조하도록 통합.

### 변경

#### EmbeddedResource 추가
- `Sts2CombatAICode/Core/Data/role_needs.json` (CardAdvisor 의 142 axes
  복사본) → csproj `EmbeddedResource` 로 패킹
- 단일 출처: `Sts2CardAdvisorCode/Data/role_needs.json`. 갱신 시 CombatAI
  복사본도 sync 필요 (현재 manual `cp`, 향후 build-time auto-sync 후속).

#### 신규 로더 `AxisSynergyLookup.cs`
- `NeedsFor(axis)` → `IReadOnlyList<RoleNeed>` (PoolMeans 패턴 미러)
- `RoleNeed` struct: `Role / Weight / RequiresWith / MutexGroup` (CardAdvisor
  의 AxisSynergyCatalog 와 동일 schema)
- `_*` prefixed keys 필터링 (주석)

#### `BuildSynergy.Compute` 재작성

기존 suffix-only 분기 → role_needs lookup. 보너스 계산:

```
for ax in card.Axes:
    needs = AxisSynergyLookup.NeedsFor(ax)
    for need in needs:
        if need.RequiresWith and not hand_contains(need.RequiresWith): continue
        if not hand_contains(need.Role): continue
        if need.MutexGroup: mutex_best[group] = max(mutex_best, weight)
        else: per_axis_bonus += int(weight * WeightToScore)
    per_axis_bonus += sum(mutex_best.Values) * WeightToScore
    bonus += min(per_axis_bonus, PerAxisBonusCap)
```

상수:
- **WeightToScore = 100** (role_needs w=2.5 ↔ 기존 ProducerWithAmplifierBonus 250)
- **PerAxisBonusCap = 400** (multi-hook 축 (FORGE_PRODUCER 5개 hooks 등) 의
  점수 폭주 방지)
- **`requires_with`** AND-condition, **`mutex_group`** within-group top-weight
  매칭 — CardAdvisor 와 동일 의미

### 검증 (`scripts/_inspect_build_synergy_cross_axis.py`)

```
Legacy: POISON_PRODUCER + POISON_AMPLIFIER → 250 (unchanged)
NEW: POISON_PRODUCER + DRAW (no amp)       → 80   (cross-axis)
Combo 5 hooks                              → 400 (CAPPED 780)
FORGE_PRODUCER all hooks                   → 400 (CAPPED 990)
CUNNING_PRODUCER + DRAW                    → 100 (cross-axis)
SKELETON_PRODUCER + MINION + SKELETON_AMPLIFIER → 300
```

기존 점수 보존 + 새 cross-axis hooks 활성화. cap 이 multi-hook 폭주 방지.

### 영향 시나리오

| 손 구성 | 이전 | 이후 |
|---|---|---|
| Bash + Strike | 0 | 0 (no role_needs match) |
| Bash + Poison + Catalyst (POISON_PRODUCER + AMP) | 250 | 250 |
| Bash + Poison + Acrobatics (POISON_PRODUCER + DRAW) | **0** | **+80** |
| Forge + Glacial Strike + Bash (FORGE_PRODUCER + BLOCK + DAMAGE) | 0 | **+240** |
| Skeleton + Captain (SKELETON_PRODUCER + MINION) | 0 | **+150** |

CombatAI 의 hand-aware 평가가 CardAdvisor 의 deck-building 추천과 같은
synergy 데이터 사용 → 양쪽 mod 평가 정합화 완성.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_build_synergy_cross_axis.py`: 7 시나리오 모두
  예상치.
- 양방향 score 정합화 (Legacy pair-stem score 보존 + 새 hooks 가산).

## v0.7.19 (2026-05-18)

**B-tier 1-path coverage — 9 mechanic 핸들러.**

### 배경

v0.7.18 audit 후 B-tier 1-path 20장 검토. 11장은 direct-stat 충분
(BLUDGEON 32dmg 단순 / BYRD_SWOOP 14dmg / multiplayer 카드 / 등),
**9장은 특수 메커니즘 미반영**.

### 추가 핸들러 (9)

| 카드 | char | cost/dmg | 메커니즘 | 공식 |
|---|---|---|---|---|
| **FINISHER** | Silent | 1c 6d | 이번턴 Attack 당 +6dmg | TurnAttacksPlayed × 6 × 35 |
| **BOLAS** | Shared | 0c 3d | 턴말 hand 복귀 | (turns−1) × perPlay × 0.5, cap 500 |
| **FOLLOW_THROUGH** | Silent | 1c 7d | 5+ 손에 → +1 hit | hand≥5 → 7×35 = 245 |
| **EXPECT_A_FIGHT** | Ironclad | 2c Skill | hand Power 당 +1 energy | powers × 60 |
| **SPITE** | Ironclad | 0c 5d | HP 손실 시 +2dmg | events>0 → 2×35 = 70 |
| **HEADBUTT** | Ironclad | 1c 9d | discard 1장 → top of draw | (best − mean) × 0.2 |
| **REBOUND** | Shared | 1c 9d | 다음 Skill → top of draw | skill mean × 0.3 |
| **OUTMANEUVER** | Shared | 1c Skill | 다음 턴 +2 energy | 2 × 60 × 0.6 = 72 |
| **SEEKER_STRIKE** | Shared | 1c 9d | 3장 중 1장 손에 | draw mean × 1.4 × 0.6 |

### 검증 (`scripts/_inspect_btier_9_handlers.py`)

```
FINISHER:        attacks=4 → +840 (확실한 finisher)
BOLAS:           turns=7 → +500 (cap)
FOLLOW_THROUGH:  others=5+ → +245
EXPECT_A_FIGHT:  powers=3 → +180
SPITE:           events>0 → +70
OUTMANEUVER:     +72 (constant)
```

### 의도된 의사결정 영향

- **FINISHER**: turn 후반 (4+ attacks 후) 점수 +840 — combo deck 핵심 카드
- **BOLAS**: 보스전 (10+ turns) 누적 +500. 단기 컴뱃엔 약함
- **FOLLOW_THROUGH**: hand 큰 상황 (5+) 우대 — 카드 사이클 빌드
- **EXPECT_A_FIGHT**: hand Power 다수 시 net energy gain — Power 빌드
- **SPITE**: 자해 빌드와 자연 시너지

### 종합 Coverage — v0.7.19 후

| Path 수 | 카드 | % |
|---:|---:|---:|
| 0 (UNPLAYABLE auto-skip) | 28 | 4.9% |
| 1 (direct-stat 충분) | 45 | 7.8% |
| 2+ | 504 | 87.3% |

- **S-tier 1-path**: 0 ✓
- **A-tier 1-path**: 2 (DEVASTATE/VOLLEY — 진짜 direct-stat 충분)
- **B-tier 1-path**: 11 (11장 — 진짜 direct-stat 충분 카드만 남음)

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_btier_9_handlers.py`: 9 카드 × 시나리오 모두 예상.

## v0.7.18 (2026-05-18)

**A-tier 1-path coverage — 5 mechanic 핸들러.**

### 배경

v0.7.17 audit 후 A-tier 1-path 12장 검토. 7장은 direct-stat 평가 충분
(DEVASTATE 30dmg / SKEWER X-cost / VOLLEY X-cost 등) 또는 PlanScorer
EstimateCalculatedHits 가 이미 cover (HEAVENLY_DRILL x≥4 doubling).
**5장이 실질 gap**: 특수 메커니즘 미반영.

### 추가 핸들러 (5)

| 카드 | char | 메커니즘 | 공식 |
|---|---|---|---|
| **FLECHETTES** | Silent | 5dmg × hand.Skills count | extraHits = max(0, skills - currentHits); v = extraHits×5×35 |
| **MAKE_IT_SO** | Regent | 0c 6dmg + 3 Skills 시 reclaim | v = (6×35) × min(1, TurnSkillsPlayed/3) |
| **SUNDER** | Defect | 3c 24dmg + kill 시 +3 energy | projected ≥ target.Hp+Block → v = 3×60 |
| **TESLA_COIL** | Defect | 0c 3dmg + 모든 orb evoke | v = orbCount × 200 |
| **THRUMMING_HATCHET** | Shared | 1c 11dmg + 턴말 hand 복귀 | (turns-1) × (11×35+20) × 0.5, cap 1000 |

### 핵심 결과 (`scripts/_inspect_atier_5_handlers.py`)

```
FLECHETTES: 2 skills → +175, 5 skills → +700 (hits=1 fallback)
MAKE_IT_SO: 0 skills → 0, 3+ skills → +210 (full reclaim)
SUNDER: kill confirmed (projected ≥ effHp) → +180
TESLA_COIL: 3 orbs → +600, 5 orbs → +1000
THRUMMING_HATCHET: 3 turns → +405, 7+ turns → +1000 (cap)
```

### 의도된 의사결정 영향

- **FLECHETTES**: Silent Skill 빌드에서 hand 가 Skills 가득이면 점수 폭증
  (단순 5dmg/1c 카드 → 진짜 가치 인지)
- **MAKE_IT_SO**: Silent Sly 빌드 turn 3+ Skills 후 free 6dmg perpetual
- **SUNDER**: weakened 적 처치 시 즉시 cost-refund (3c 부담 해소)
- **TESLA_COIL**: orb 풀 후 사용 시 매우 강력 — 빈 orb queue 시 그냥 3dmg
- **THRUMMING_HATCHET**: 단기간 컴뱃에 약함, 보스전에 강함 (자연 reflection)

### 종합 Coverage — v0.7.18 후

| Path 수 | 카드 | % |
|---:|---:|---:|
| 0 (UNPLAYABLE 자동 skip) | 28 | 4.9% |
| 1 (direct-stat) | 54 | 9.4% |
| 2+ | 495 | 85.8% |

S-tier 1-path: 0 (v0.7.17). A-tier 1-path: **7 → 2** (5장 핸들러 추가).
잔존 A-tier 1-path 2장 (DEVASTATE 30dmg 단순 / VOLLEY X-cost 단순) 은
direct-stat 으로 충분히 평가됨.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_atier_5_handlers.py`: 5 카드 × 5-6 시나리오
  모두 예상.

## v0.7.17 (2026-05-18)

**S-tier 1-path coverage — ALL_FOR_ONE + PINPOINT mechanic handlers.**

### 배경

v0.7.16 coverage audit (`scripts/_audit_full_coverage.py`) 결과 28 truly-
uncovered 카드는 전부 Curse / Status / Quest (UNPLAYABLE 자동 skip) →
실제 평가 누락 0. 단 1-path 카드 61장 중 S-tier 2장 (ALL_FOR_ONE / PINPOINT)
은 direct-stat 만 평가 → 특수 메커니즘 누락.

### ALL_FOR_ONE (S, Defect, Attack 2c 10d)

효과: "10 데미지 + Discard pile 의 모든 0-cost 카드를 손에 가져옴".

핸들러: discard 의 0-cost non-curse 카드들 EstimateCardPower 합산, cap 1200.

```
empty discard                                  -> +60 (baseline)
3x Shiv (4d each)                              -> +660
Strong 0-cost (Bloodletting + Offering + 3xShiv) -> +820
Massive pile (8x 8d 0-cost)                    -> +1200 [cap]
Mixed with 1-cost (recalls only 0-cost)        -> +440
```

### PINPOINT (S, Silent, Attack 3c 15d)

효과: "15 데미지 + 이번 턴 사용한 Skill 당 +1 에너지 환급".

핸들러: `TurnSkillsPlayed × EnergyInHand (60)`. SimState 의
TurnSkillsPlayed 가 v0.6.8 부터 정확히 tracking.

```
0 skills played -> +0
1 skill         -> +60
3 skills        -> +180
5 skills        -> +300
```

### 영향

- ALL_FOR_ONE: 0-cost cycle 빌드 (특히 Silent Shiv / Defect Coolant 코어
  메커니즘) 인지. discard 쌓인 후 폭발적 hand refill 가치 visible.
- PINPOINT: skill-heavy 빌드 (Silent Sly) 와 시너지. Skill 여러 장 깐 후
  PINPOINT 가 거의 비용 0 으로 떨어지는 가치 인지.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_all_for_one_pinpoint.py`: 6 ALL_FOR_ONE 시나리오
  + 6 PINPOINT 시나리오 모두 예상.

### 종합 Coverage — v0.7.17 후

| Path 수 | 카드 | % |
|---:|---:|---:|
| 0 (UNPLAYABLE 만) | 28 | 4.9% |
| 1 (direct-stat) | 59 | 10.2% |
| 2-3 | 351 | 60.8% |
| 4+ | 139 | 24.1% |

S-tier 1-path: **0 장 남음** (ALL_FOR_ONE / PINPOINT 모두 핸들러 추가).
A-tier 1-path 12장은 D-tier impact 작아 후순위 (개별 mechanic 매핑은 후속
필요 시).

## v0.7.16 (2026-05-18)

**AGGRESSION turn-start hand addition — Phase 4 마무리.**

### 배경

v0.7.13 changelog 에서 명시적으로 "AGGRESSION 은 합성-평균 hand 모델에서
distinguishable 안 함" 으로 미반영. 이제 (v0.7.14 Monte Carlo + v0.7.15
MachineLearning) 으로 hand 모델이 정교해져, 1 장의 합성 카드 추가가 의미
있는 단계.

### 변경 (`AnalyticalSimulator.AdvanceTurnInternal`)

`newHand = nextHand` 직후, `state.PlayerPowers["AggressionPower"]` 검사:

```csharp
if (aggStacks > 0 && state.DiscardPile.Count > 0)
{
    // discard 의 평균 attack 통계
    avgDmg = mean(c.Damage * max(1, c.Hits)) for non-curse attacks
    avgCost = mean(c.Cost) clamped >= 0
    upgradedDmg = (int)(avgDmg * 1.3)  // +30% upgrade approx

    recalled = new SimCard {
        Id = "<aggression-recall>",
        Kind = CardType.Attack,
        Cost = avgCost,
        Effect = { Damage = upgradedDmg, Hits = 1 },
    }
    for i in 0..aggStacks: newHand.Add(recalled)
}
```

### 검증 (`scripts/_inspect_aggression_handrecall.py`)

```
scenario                                     avgDmg  cost  stacks
no discard attacks                              -     -      1   (no add)
1 Strike (6d/1c)                                7     1      1   +1×7dmg
Strike + Bash (6d/1c + 8d/2c)                   9     1      1   +1×9dmg
3 attacks mid-game                             11     1      1   +1×11dmg
AGGRESSION 2 stacks                            11     1      2   +2×11dmg
strong discard (Bludgeon/HeavyBlade/IronWave)  19     1      1   +1×19dmg
multi-hit (Sword Boomerang 3×4d)               15     1      1   +1×15dmg
```

### 효과

- AGGRESSION 활성화된 next-turn projection 시 hand 가 6 → 7 cards (MachineLearning
  과 별개 추가)
- 추가 카드의 damage 가 discard 의 평균 attack × 1.3 → 강한 attack 덱에서
  더 큰 가치
- Monte Carlo N=3 의 각 sample 마다 동일 카드 추가 (deterministic — 합성 변형)

### Forward sim coverage — v0.7.16 최종

| 영역 | 상태 |
|---|---|
| 단일턴 depth=3 beam search | ✅ |
| 멀티턴 AdvanceTurn projection + Monte Carlo N=3 | ✅ |
| Power 패시브 PowerCatalog 도달 (24장 fallback) | ✅ |
| HP_LOSS producer/consumer | ✅ |
| Self-copy chain 6장 | ✅ |
| Skeleton ally damage + split-fire | ✅ |
| DemonForm/Regen/Barricade per-turn passive | ✅ |
| ReaperForm Doom on enemies | ✅ |
| MAYHEM/STAMPEDE 실제 auto-trigger | ✅ |
| Monte Carlo next-turn hand sampling | ✅ |
| EchoForm next-turn 2x first-card | ✅ |
| MachineLearning +1 hand size | ✅ |
| **AGGRESSION turn-start hand 추가** | ✅ **v0.7.16** |

### 남은 영역

- CombatAdvisor 자매 모드 포트 (sim 인프라 활용 1턴 위험도 표시 MVP)
- 게임 실플레이 검증 (사용자)

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.

## v0.7.15 (2026-05-18)

**EchoForm / MachineLearning multi-turn 시그널 통합.**

### 배경

v0.7.10 의 multi-turn projection 이 다음 턴 hand 와 첫 카드 점수를 모델링하지만,
S+ tier persistent passive 두 개를 무시:

- **EchoFormPower** — 매 턴 첫 N 카드 두 번 발동. depth=1 next-turn lookahead 의
  첫 카드 점수가 실제론 2배. 가치 누락
- **MachineLearningPower** — 매 턴 +1 카드 draw. next-turn hand size 가 5 가 아닌
  5+N. 더 큰 hand 의 옵션 다양성 무시

### 변경

**`AnalyticalSimulator.cs`**:

- `ComputeNextTurnHandSize(state)` 신규 — `5 + MachineLearningPower stack`
- `BuildSyntheticHand(state)` → `BuildSyntheticHand(state, handSize)` 시그니처
  확장. handSize 만큼 평균 카드 복제
- `AdvanceTurn` / `AdvanceTurnSampled` 가 호출 시 `ComputeNextTurnHandSize`
  결과 전달

**`ActionPlanner.cs`** MC 루프:

```csharp
echoMultiplier = (PlayerPowers["EchoFormPower"] > 0) ? 2.0 : 1.0
nextTurnBonus = (int)(nextTurnAvg * echoMultiplier * NextTurnDiscount)
```

EchoForm 의 진짜 효과는 stack 별로 "첫 N 카드" 가 echo 되지만, 우리는 depth=1
만 평가 → 첫 카드 echo (×2) 로 saturate. 더 깊은 lookahead 가 필요해야 stack 2,
3 의 가산이 의미있음.

### 검증 (`scripts/_inspect_echoform_machinelearning.py`)

```
=== Next-turn hand size (MachineLearningPower) ===
stacks  handSize
     0         5
     1         6
     2         7
     3         8

=== Next-turn first-card bonus (EchoFormPower) ===
baseScore  echo=0  echo>=1  shift
      100      30       60    +30
      300      90      180    +90
      600     180      360   +180
     1000     300      600   +300
     1500     450      900   +450
```

EchoForm 의 multi-turn 가산이 첫 카드 점수에 비례. 강한 카드 (1500) 가 손에 들어
오면 +450 bonus. MachineLearning 으로 hand 6 → MC sample 의 옵션 +1.

### 의도된 시나리오

| 카드 | EchoForm 없음 | EchoForm 1 |
|---|---|---|
| EchoForm 자체 (S+, +1500 mc-credit) | 0.7.10 처럼 단일 projection | 2× projection — 자체 가치 visible |
| Bludgeon (300 base) | 90 next-turn bonus | 180 next-turn bonus |
| Power 카드 (200 base) | 60 next-turn bonus | 120 next-turn bonus |

MachineLearning:
- 손이 6장이라면 BestContinuation 에 더 많은 후보 (1 candidate 추가) → 더 정확
  한 best-first-card 평가

### 비용

추가 비용 ~0% — 동일 코드 경로, hand size 변수화. ML stacks=3 일 때만 +3 cards
샘플링 (perf 영향 미미).

### Forward sim coverage — v0.7.15 후

| 영역 | 상태 |
|---|---|
| 단일턴 depth=3 beam search | ✅ |
| 멀티턴 AdvanceTurn projection + Monte Carlo (N=3) | ✅ |
| Power 패시브 PowerCatalog 도달 | ✅ |
| HP_LOSS producer/consumer | ✅ |
| Self-copy chain 6장 | ✅ |
| Skeleton ally damage + split-fire | ✅ |
| DemonForm/Regen/Barricade per-turn passive | ✅ |
| ReaperForm Doom on enemies | ✅ |
| MAYHEM/STAMPEDE 실제 auto-trigger | ✅ |
| Monte Carlo next-turn hand sampling | ✅ |
| **EchoForm next-turn first-card 2x score** | ✅ **v0.7.15** |
| **MachineLearning +1 hand size** | ✅ **v0.7.15** |
| AGGRESSION 손에 카드 추가 | ⊘ EffectSynergy credit |
| CombatAdvisor 자매 모드 포트 | ❌ |

## v0.7.14 (2026-05-18)

**Forward Simulator Phase 2c — Monte Carlo next-turn hand sampling.**

### 배경

v0.7.10 의 multi-turn projection 은 `MakeAverageDrawCard × 5` synthetic
hand 만 모델링. 다음 턴 hand 다양성 (강한 카드만 vs 저주 폴루션 vs 콤보)
이 한 표본으로 평탄화됨 → planner 의 second-turn 시그널이 노이즈 줄어든
대신 **고가치 콤보 / 저가치 위험 둘 다 못 봄**.

Phase 2c 는 actual deck pool sampling 으로 대체.

### 변경

**`AnalyticalSimulator.cs`**:

- `AdvanceTurn(state)` 본체 → `AdvanceTurnInternal(state, nextHand)` 로 추출
  (불변)
- `BuildSyntheticHand(state)` 신규 — 기존 `MakeAverageDrawCard ×5` 동작
- `BuildSampledHand(state, handSize, rng)` 신규 — Fisher-Yates partial
  shuffle 로 `DrawPile + DiscardPile` 에서 `handSize` 장 sampling without
  replacement. pool < handSize 시 전체 반환.
- **`AdvanceTurnSampled(state, rng)`** public API — `AdvanceTurnInternal(state,
  BuildSampledHand(state, 5, rng))` 위임

**`ActionPlanner.cs`**:

- `MonteCarloSamples = 3` 상수
- multi-turn lookahead: 기존 단일 `AdvanceTurn(nextState)` → N=3 sample
  의 `AdvanceTurnSampled(nextState, rng)` 평균:
  ```
  seed = nextState.Hand.Count * 31 + nextState.PlayerHp + card.Id.GetHashCode()
  rng = Random(seed)
  for s in 0..N:
      nextTurnState = AdvanceTurnSampled(nextState, rng)
      sampleScore[s] = BestContinuation(nextTurnState, depth=1, K=3)
  nextTurnBonus = mean(sampleScore) * NextTurnDiscount
  ```

Deterministic seed: 같은 state + 같은 first card → 같은 sample 시퀀스
재현. card.Id 가 salt 로 들어가 후보별 sample 다양성 보장.

### MC 의 진짜 가치

`scripts/_inspect_monte_carlo.py` 의 linear sum 표는 synth-avg ≈ true mean
보여줌 — **선형 합산만 한다면 MC 가 별 의미 없음**. 실제 가치는 PlanScorer
의 non-linear 평가에서 나옴:

- **Lethal detection** — synth-avg "평균 카드" 는 절대 lethal 못 침. MC
  sample 은 3장의 강한 Attack 이 우연히 들어와 lethal kill 가능 ↔ 약한 hand 는 못 함
- **BuildSynergy / AmplifierSynergy** — POISON_PRODUCER + POISON_CONSUMER
  특정 조합이 동시에 hand 에 있어야 트리거. 평균 카드는 어떤 build axis 도
  안 가짐
- **HP threshold effects** — HP_LOSS_CONSUMER 가 events 카운트에 따라
  bonus. 다양한 hand 가 events 다르게 만들 수 있음 (sampling 미세 차이지만)
- **Curse / Status pollution** — synth-avg 는 저주를 0.X 비율로 섞은 "회색
  카드" 가 됨. MC sample 은 "이번 hand 에 저주 1장 들어왔다" vs "안 들어왔다"
  로 갈림

### 비용

- per first-card candidate: depth=2 beam (~50 calls) + N=3 × depth=1
  (~50 calls) = ~200 calls
- legacy single sample = +50 calls → MC 추가 +100 calls
- 24 first candidates (hand=8, targets=3): legacy ~2160, MC ~4800 (~2.2x)
- 100ms/PlanNextStep → ~220ms (여전히 허용)

### 의도된 효과

| 시나리오 | synth-avg signal | MC signal |
|---|---|---|
| Power 깔기 vs 공격 | 다음 턴 "평균 카드" hand 본인 효과 못 봄 | 강한 hand sample → Power 가치 visible |
| 자해 카드 (BLOODLETTING) | 다음 턴 평균 draw 평탄 | 저주 sample → +HP loss producer 인지 |
| Combo 빌드 | 평균 카드 0 시너지 | sample 별 시너지 발생 → 시너지 카드 점수 상향 |
| 저주 폴루션 | 평균에 묻힘 | 일부 sample 에 저주, 일부 cleaner — variance 보임 |

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_monte_carlo.py`: linear sum 시 synth-avg/MC
  거의 동등 (선형성 검증) — 실제 비선형 PlanScorer 에서 차이가 발생.

### Forward sim coverage — v0.7.14 후

| 영역 | 상태 |
|---|---|
| Pile / random / pool-aware | ✅ |
| Power 패시브 PowerCatalog 도달 | ✅ |
| HP_LOSS producer/consumer | ✅ |
| 단일턴 depth=3 beam search | ✅ |
| 멀티턴 AdvanceTurn projection | ✅ |
| Self-copy chain 6장 | ✅ |
| Skeleton ally damage + split-fire | ✅ |
| DemonForm/Regen/Barricade per-turn passive | ✅ |
| ReaperForm Doom on enemies | ✅ |
| MAYHEM/STAMPEDE 실제 auto-trigger | ✅ |
| **Monte Carlo next-turn hand sampling (N=3)** | ✅ **v0.7.14** |
| AGGRESSION 손에 카드 추가 시뮬 | ⊘ EffectSynergy credit |
| EchoForm/MachineLearning hand mutation | ❌ Phase 4 |
| CombatAdvisor 자매 모드 포트 | ❌ |

## v0.7.13 (2026-05-18)

**ReaperForm Doom on enemies + MAYHEM/STAMPEDE 실제 AdvanceTurn 발화.**

### ReaperForm Doom — Necrobinder doom 빌드 완성

이전: REAPER_FORM 의 PowerCatalog 800 baseline 만 점수화. 실제 enemy 누적
Doom 데미지는 simulator 에서 안 보임. doom 빌드 (REAPER_FORM + DOOM_CONSUMER)
의 컴뱃 길이 단축 미반영.

**변경**:

- **`SimEnemy.DoomAmount`** 신규 필드. 누적 Doom 스택.
- **`AnalyticalSimulator.ApplyCardPlay`** attack 분기:
  - `state.PlayerPowers["ReaperFormPower"]` active + attack damage > 0 일 때
  - 명중한 enemy 의 `DoomAmount += reaperStacks × max(1, card.Hits)`
  - 멀티-hit 카드는 hit 별 스택 (3-hit 공격 = +3 Doom)
- **`AnalyticalSimulator.AdvanceTurn`** enemy DoT 루프:
  - 기존 `Poison + Constrict` → `Poison + Constrict + Doom`
  - Doom 은 self-decrement 없음 (Poison 처럼 stack 유지)

### MAYHEM / STAMPEDE 실제 발화

이전: PowerCatalog + EffectSynergy 가 score 보너스만 줌. AdvanceTurn 에서
실제 auto-play 효과 미반영. 다음 턴 enemy HP 가 잘못 계산됨.

**변경** (`AnalyticalSimulator.AdvanceTurn` 새 블록):

```
mayhemStacks + stampedeStacks = autoTriggers
if autoTriggers > 0:
    avgAuto = MakeAverageDrawCard(state)   # 합성 평균 draw 카드
    perDmg = avgAuto.IsAttack ? avgAuto.TotalDamage : 0
    totalDmg = perDmg × autoTriggers
    apply to weakest alive enemy (block-first)
```

- MAYHEM 1 stack + STAMPEDE 1 stack → 2 auto-trigger 발화
- 합성 평균 attack 카드의 데미지를 약한 적에 적용
- 비-attack 평균 카드의 경우 데미지 0 (block/draw 가치는 별도)

### AGGRESSION 미반영 이유

AGGRESSION 의 turn-start 효과 = "random Attack from discard → hand".
- 우리의 next-turn hand 는 5 장의 synthetic average card 로 모델링됨
- 여기 1 장 더해도 평균에 묻혀 distinguishable 안 함
- 이미 v0.7.4 의 EffectSynergy bonus 로 적절히 credited
- 본 패스에서 추가 시뮬은 의도적으로 미반영

### 검증 (`scripts/_inspect_v0_7_13.py`)

```
ReaperForm Doom (HP 50, 3 turn ticks):
  reaper=0 hits=1 → doom=0,  HP after = 50
  reaper=1 hits=1 → doom=1,  HP after = 47
  reaper=1 hits=3 → doom=3,  HP after = 41
  reaper=2 hits=1 → doom=2,  HP after = 44
  reaper=3 hits=4 → doom=12, HP after = 14

MAYHEM/STAMPEDE auto-trigger:
  m=0 s=0 → HP unchanged
  m=1 s=0 avgDmg=12 → 40 HP → 28 HP
  m=1 s=1 → 40 HP → 16 HP (24 damage)
  m=2 s=0 → 40 HP → 16 HP
  m=1 s=0 with 15 block → block absorbs (40 HP → 40, block 15 → 3)
  m=3 s=0 avgDmg=30 over-dmg → 30 HP → 0 (kill)
```

### Forward sim coverage — v0.7.13 후

| 영역 | 상태 |
|---|---|
| Power 패시브 (S/A/B/C/D) PowerCatalog 도달 | ✅ v0.7.7 |
| HP_LOSS producer/consumer | ✅ v0.7.7/v0.7.8 |
| 단일턴 depth=3 beam search | ✅ v0.7.9 |
| 멀티턴 AdvanceTurn projection | ✅ v0.7.10 |
| Self-copy chain 6장 | ✅ v0.7.11 |
| Skeleton ally damage contribution | ✅ v0.7.11 |
| Skeleton split-fire defense | ✅ v0.7.12 |
| DemonForm/Regen/Barricade per-turn passive | ✅ v0.7.12 |
| **ReaperForm Doom on enemies** | ✅ **v0.7.13** |
| **MAYHEM/STAMPEDE 실제 auto-trigger** | ✅ **v0.7.13** |
| AGGRESSION turn-start hand addition | ⊘ EffectSynergy credit only |
| Monte Carlo draw RNG | ❌ Phase 2c |
| EchoForm hand mutation | ❌ Phase 4 |

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_v0_7_13.py`: 11 시나리오 모두 예상.

## v0.7.12 (2026-05-18)

**Phase 3c — Skeleton split-fire defense. Phase 2b — Player Powers
+ AdvanceTurn auto-trigger.**

### Phase 3c — Skeleton split-fire defense

이전: AdvanceTurn 의 적 데미지는 모두 player HP 로 흡수. ally HP 가 따로 있어도
무시. Necrobinder 스켈레톤이 적 공격을 흡수하는 split-fire 미반영.

**변경**:

- `EnemyTurnSimulator.PredictRawLeak` 신규 — post-block, pre-ally-absorption
  leak 반환. AdvanceTurn 이 split-fire 분배 전 raw leak 알아야 해서 필요.
- `EnemyTurnSimulator.ComputeAllyAbsorption(s, rawLeak)` 신규 헬퍼:
  ```
  absorption = aliveAllies / (1 + aliveAllies)
  pool = rawLeak × absorption
  return min(pool, totalAllyHp)
  ```
  - 1 ally → 50% aggro, 2 → 67%, 3 → 75% (대칭적 share)
  - Ally 총 HP 로 cap → overflow 는 player 에게 복귀
- `EnemyTurnSimulator.PredictPlayerDmg` 는 이제 absorption 후 leak 반환 (planner /
  survival-urgency 가 ally 흡수 반영해 정확한 threat 인식).
- `AnalyticalSimulator.AdvanceTurn` 가:
  - raw leak 으로 absorbed 계산
  - ally 별 HP 비율로 흡수 데미지 분배 (총 HP 비례)
  - 0 HP 가 된 ally 는 dead (다음 턴부터 attack 기여 X)

### Phase 2b — Player Powers + AdvanceTurn auto-trigger

이전: SimState 에 PlayerStrength / PlayerDexterity 등 explicit 필드만. 그 외
DemonFormPower / RegenPower / BarricadePower 같은 persistent passive 가 active
인지 알 수 없음. AdvanceTurn 이 per-turn 효과를 시뮬 못 함.

**변경**:

- **`SimState.PlayerPowers`** — `IReadOnlyDictionary<string, int>`. 모든 player
  Power 의 (name → stack) 매핑. SimEnemy.Powers 와 대칭.
- **`StateSnapshotter`** — `CombatReflection.GetAllPowers(playerCreature)` 호출
  로 populate.
- **`AdvanceTurn`** per-turn passive 처리:
  - **DemonFormPower N** → `PlayerStrength += N` (스케일링 빌드)
  - **RegenPower N** → `PlayerHp += N` (sustain)
  - **BarricadePower** → block 유지 (보통 reset 0 → 그대로 보존)

ReaperFormPower (Doom on enemies) 은 SimEnemy 의 DoomPower 필드가 없어 본
패스에서 미반영 — follow-up 영역.

### 검증 (`scripts/_inspect_phase3c_2b.py`)

```
3c: 1 skeleton (HP 30), boss 20 dmg            rawLeak=20  absorb=10  plyrLeak=10  newHp=50
3c: 2 skeletons (HP 60), boss 30 dmg           rawLeak=30  absorb=20  plyrLeak=10  newHp=50
3c: 3 skeletons (HP 90), big hit 50            rawLeak=50  absorb=37  plyrLeak=13  newHp=47
3c: 1 skeleton, ally HP only 5 (overflow)      rawLeak=40  absorb= 5  plyrLeak=35  newHp= 5
2b: DemonForm 2                                rawLeak= 0  absorb= 0  plyrLeak= 0  newHp=60  str=7
2b: Regen 4 after 10 dmg                       rawLeak=10  absorb= 0  plyrLeak=10  newHp=34
2b: Barricade preserves block                  rawLeak= 0  absorb= 0  plyrLeak= 0  newHp=60  blk=15
```

- absorption 비율 정확 (1 ally 50% / 2 allies 67% / 3 allies 75%)
- ally HP cap overflow 도 정확 (5 HP ally → 5 흡수, 35 player 로 leak)
- DemonForm/Regen/Barricade 모두 의도대로 동작

### 의도적으로 안 한 부분

- **ReaperFormPower** — DoomPower 가 SimEnemy 에 없음. enemy.Powers dict 에
  추가하는 follow-up 작업.
- **EchoFormPower / MachineLearningPower** — per-turn effect 는 PowerCatalog 가
  이미 잡고 있음 (900 baseline). AdvanceTurn 의 hand-mutation 까지 가는 건
  큰 작업 영역 (Phase 4).
- **Aggro priority** — STS2 실제 게임이 ally 우선 공격 / player 우선 공격을
  결정하는 정확한 룰 unknown. 50/50 비율 휴리스틱은 conservative.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_phase3c_2b.py`: 7 시나리오 모두 예상.

### Forward sim coverage — v0.7.12 후

| 영역 | 상태 |
|---|---|
| Power 패시브 (S/A/B/C/D) PowerCatalog 도달 | ✅ v0.7.7 |
| HP_LOSS producer/consumer | ✅ v0.7.7/v0.7.8 |
| 단일턴 depth=3 beam search | ✅ v0.7.9 |
| 멀티턴 AdvanceTurn projection | ✅ v0.7.10 |
| Self-copy chain 6장 | ✅ v0.7.11 |
| Skeleton ally damage contribution | ✅ v0.7.11 |
| **Skeleton split-fire defense** | ✅ **v0.7.12** |
| **DemonForm/Regen/Barricade per-turn passive** | ✅ **v0.7.12** |
| ReaperForm Doom on enemies | ❌ follow-up |
| Monte Carlo draw RNG | ❌ Phase 2c |

## v0.7.11 (2026-05-18)

**Phase 3 — Self-copy chain 6장 + Skeleton ally 모델링.**

### Phase 3a — Self-copy chain 카드 평가

기존 6장이 단일 효과만 점수화 → 자기 복사본 / 손 복제 의 future-play 가치
누락. 카드별 핸들러 추가.

| 카드 | tier | 핸들러 | 공식 | Cap |
|---|---|---|---|---:|
| **ANGER** | B | `ApplyAngerChain` | (turns−1) × (6dmg×35 + 80cost0) × 0.4 | 400 |
| **UNDEATH** | A | `ApplyUndeathChain` | (turns−1) × (7blk×25 + 80cost0) × 0.4 | 400 |
| **DUAL_WIELD** | B | `ApplyDualWieldChain` | max(hand atk/power EV) × 0.7 | — |
| **HEIRLOOM_HAMMER** | C | `ApplyHeirloomHammerChain` | max(hand atk EV) × 0.7 | — |
| **NIGHTMARE** | B | `ApplyNightmareChain` | 3 × max(hand EV) × 0.5 (next-turn discount) | 900 |
| **ADAPTIVE_STRIKE** | B | `ApplyAdaptiveStrikeChain` | 18dmg × 50 × 0.4 = +360 (constant) | — |

`ChainDiscount = 0.4` 공통 — deck cycling / draw RNG / exhaust 불확실성 반영.

**예상치 (`scripts/_inspect_phase3.py`)**:

```
ANGER         turns=2 +116  turns=4 +348  turns=7 +348
UNDEATH       turns=2 +102  turns=4 +306  turns=7 +306
DUAL_WIELD    hand=200 +140  hand=500 +350  hand=900 +630
NIGHTMARE     hand=200 +300  hand=500 +750  hand=900 +900 [cap]
ADAPTIVE_STR  constant +360
```

ANGER 의 long-fight 가치 ×3 (단일 6dmg 점수의 ~3 배). NIGHTMARE 는 강덱에서
cap 까지 saturate.

### Phase 3b — Skeleton ally 모델링

`SimEnemy` 와 대칭 구조의 신규 `SimAlly` 도입. Necrobinder 스켈레톤 / 기타
player-side 소환물 모델링.

**신규 파일**:
- `Sts2CombatAICode/Core/Sim/SimAlly.cs` — Hp, Block, IntentDamage,
  IntentRepeats, HasAttackIntent, ClassName, SourceRef

**`SimState.Allies`** — `IReadOnlyList<SimAlly>` 필드. 빈 리스트 = capture
실패 fallback.

**StateSnapshotter 변경**:
- 기존 SkeletonCount 카운팅 루프 → SimAlly 빌드도 함께. 각 ally:
  - Hp/Block: `CombatReflection` 동일 필드 활용
  - Intent: `monster.NextMove.Intents` 순회 (`CombatReflection.Classify`,
    `GetAttackIntentDamage`, `GetAttackIntentRepeats`)
- SkeletonCount 는 호환성 위해 유지.

**`RemainingTurnsEstimator.EstimatePlayerDpt` 가산**:
```csharp
allyDamage = sum(ally.TotalIntentDamage for alive attacking ally)
playerDpt = handAttackDamage / 2 + strengthBonus + allyDamage
```

스켈레톤 데미지가 컴뱃 길이 추정에 반영 → Necrobinder 패시브 가치 visible.

**`AnalyticalSimulator.AdvanceTurn` 가산**:
- 적 intent 해소 전 ally 가 가장 HP 낮은 적에 데미지 (block-first)
- 단일 타겟 휴리스틱 (스켈레톤별 타겟팅 분기 미반영)

**예상치 (3b)**:

```
scenario                                       dpt  turns
no skeletons, boss 300 HP                       10     10 [cap]
1 skeleton 8 dmg, boss 300 HP                   18     10 [cap]
3 skeletons 8 dmg, boss 300 HP                  34      8
5 skeletons 8 dmg, elite 100                    45      2
```

스켈레톤 다수 시 컴뱃 길이 추정 정확히 단축 → 패시브 카드 가치 비례 조정.

### Random target 카드

catalog 정찰 결과 **0 카드** (STS2 에는 random-target attack 카드 없음 —
Sword Boomerang 류는 STS1 only). 작업 영역 X.

### 의도적으로 안 한 부분

- **Skeleton 별도 타겟팅 + 적 split-fire**: 적이 ally HP 를 우선 공격하는
  로직 미반영. AdvanceTurn 의 enemy 데미지가 ally HP 차감 후 player HP
  로 가는 분기 없음. Phase 3c 영역.
- **ANGER 의 chain-of-chain**: 추가된 ANGER 가 다시 ANGER 를 추가 → 무한
  복리. 현재 (turns−1) 선형 추정.
- **ADAPTIVE_STRIKE 의 cost-set-zero 카드가 그 턴에 draw 안 될 수도**:
  확률 미반영, 단순 0.4 discount.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_phase3.py`: 3a 카드 6종 × 시나리오, 3b 스켈레톤
  4 시나리오 모두 예상 범위.

### Power 효과 + 자해 + 자기복사 + 스켈레톤 — v0.7.11 후

| 영역 | 처리 상태 |
|---|---|
| Power 패시브 (S/A/B/C/D) | ✅ v0.7.7 |
| HP_LOSS producer/consumer | ✅ v0.7.7/v0.7.8 |
| 단일턴 depth=3 beam search | ✅ v0.7.9 |
| 멀티턴 AdvanceTurn projection | ✅ v0.7.10 |
| **Self-copy chain 6장** | ✅ v0.7.11 |
| **Skeleton ally 데미지 기여** | ✅ v0.7.11 |
| Skeleton split-fire defense | ❌ Phase 3c |
| Monte Carlo draw RNG | ❌ Phase 2c |

## v0.7.10 (2026-05-18)

**Forward Simulator Phase 2a — AdvanceTurn + multi-turn 평가 통합.**

### Phase 2a scope (사전 합의)

| 구성 | 포함 |
|---|---|
| A. ResolveEnemyIntents | ✅ `EnemyTurnSimulator.PredictPlayerDmg` 활용 |
| B. AdvanceTurn | ✅ 신규 (`AnalyticalSimulator.AdvanceTurn`) |
| C. ActionPlanner multi-turn 통합 | ✅ next-turn discount 0.30 |
| D. Power passive 자동 트리거 | ❌ Phase 2b |
| E. Monte Carlo draw RNG | ❌ Phase 2c |

### A+B — `AnalyticalSimulator.AdvanceTurn`

신규 메서드. 단일 호출로 턴 종료 → 다음 플레이어 턴 시작 state 생성.

처리 항목 (Phase 2a 단순화):
1. **적 intent 해소** — `PredictPlayerDmg` (Vuln/Weak/Intangible/Frail + block
   + EOT block bonus 다 반영) → player HP 차감
2. **적 turn-start Strength** — `HasTurnStartStrengthBuff` (Ritual 등) → +1 str
3. **DoT tick** — enemy.Hp -= (Poison + Constrict). enemy.Block reset.
4. **Player 상태 decrement** — Vuln/Weak/Frail/Intangible -1 each
5. **Enemy 상태 decrement** — Vuln/Weak/Frail/Poison -1 each
6. **Player block reset** + **energy reset to 3** (base, character 추가
   에너지 미반영 — Pyre/Berserk 등 Phase 2b)
7. **New hand** — 5장 synthetic average card (`MakeAverageDrawCard` 재사용)
8. **Pile 시프트** — discard → reshuffle 로 모두 draw pool 흡수

### C — ActionPlanner multi-turn 통합

기존 depth=3 beam 후, 첫 카드 적용 state 에서 `AdvanceTurn` 실행 →
**next-turn single-step max × 0.30 discount** 가산:

```
total = firstScore
      + BestContinuation(state1, depth=2, K=3)    // 이번 턴 2~3 카드
      + BestContinuation(AdvanceTurn(state1), depth=1) * 0.30   // 다음 턴 첫 카드
```

`NextTurnDiscount = 0.30` — 보수적. 이유:
- 다음 턴 hand 가 synthetic (RNG 노이즈)
- AdvanceTurn 이 *첫 카드 적용 후* state 에서 출발 (이번 턴 잔여 카드 미반영)
- 적 intent 가 정확하지 않을 수 있음 (다음 턴 intent unknown)

### 의도된 효과

| 결정 시나리오 | depth=3 만 | + multi-turn |
|---|---|---|
| Inflame (P) vs Strike | Inflame 점수 약함 (이번 턴 0 데미지) | Inflame +next-turn boost — Power 가치 visible |
| Defend (S) vs 공격 | 적 데미지 적으면 둘 비슷 | 다음 턴 HP 보존 → Defend score ↑ |
| BLOODLETTING — 자해 + draw | depth=3 시퀀스 정확 | 다음 턴 hand 가 새로 그려지니 draw 가치 ↓ (정확) |
| Power 깔기 (DemonForm) | 점수 보수적 | next-turn Strength stack 보너스 → Power score ↑ |

### 검증 (`scripts/_inspect_advance_turn.py`)

```
scenario                                  leak  hp->   status
safe: 70 HP, 15 block, 1 enemy 8x2          1   70->69  -
vuln incoming: dmg x1.5                    15   60->45  vuln 2->1
intangible: cap each hit at 1               5   40->35  intang 1->0
fatal: 12 HP, no block, big boss            25   12-> 0  -
multi-enemy: 3 attackers                   26   80->54  -
```

`AdvanceTurn` 가 `PredictPlayerDmg` 의 vuln/weak/intangible 분기 모두
정확히 mirror.

### Phase 2a 단순화 (의도적)

- **Player block 항상 0 으로 reset** — Barricade / Calipers 미반영
- **Energy = 3 flat** — Pyre / Berserk / EnergyNextTurnPower 추가 보너스 미반영
- **Ethereal exhaust / Retain 미반영** — 다음 턴 hand 가 deck pool 의 average ×5
- **AdvanceTurn 의 시작 state** — depth=3 시퀀스 끝 state 가 아닌 첫 카드만
  적용된 state. multi-turn 보너스가 약간 보수적 (의도)
- **다음 턴은 single-step max** — depth=1, full beam search 안 함

### Phase 2b/2c 영역

- Power passive 자동 트리거 (MAYHEM/AGGRESSION/STAMPEDE/REAPER_FORM)
- Monte Carlo draw sampling
- AdvanceTurn 시작 state 정확화 (depth=3 끝 state 사용)
- 다음 턴 depth=3 full search
- 캐릭터별 base energy / EnergyNextTurnPower / Barricade 반영

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_advance_turn.py`: 7 시나리오 모두 leak / HP /
  status decrement 정확.

## v0.7.9 (2026-05-18)

**Forward Simulator Phase 1 — depth=3 beam search + HpLoss simulator
통합.**

### 배경

`docs/forward_sim_scope.md` 의 Phase 1 작업 (원래 4일 추정). 정찰 결과
**`AnalyticalSimulator.ApplyCardPlay` 가 이미 v0.2.5 부터 존재** (damage /
block / power / orb / draw / Vuln-Weak-Frail-Poison-Constrict-Burn-Artifact
propagation, 약 600줄). Phase 1 의 80% 가 기존 인프라로 cover 됨.

진짜 남은 작업:
1. v0.7.8 의 `HpLossAmount` 를 simulator 가 PlayerHp 에 차감 (누락)
2. 결정된 (depth=3, beamK=3) 단일 턴 beam search 로 확장

### 결정 사항 (사전 합의)

| 결정 | 값 |
|---|---|
| Depth | 3 (1 first card + 2 lookahead) |
| Multi-turn | NO (Phase 1 = 단일 턴) |
| Tree pruning | Top-K beam search (K=3) |
| Vakuu vs Advisor 공유 | NO (CombatAI 만, Advisor 포트는 별개) |

### 변경

#### Phase 1.1 — HpLoss simulator 통합

`AnalyticalSimulator.ApplyCardPlay` (line 73):
```csharp
if (card.HpLossAmount > 0)
    newPlayerHp = Math.Max(0, newPlayerHp - card.HpLossAmount);
```

자해 카드 (BLOODLETTING / OFFERING / HEMOKINESIS) 시퀀스 시 다음 카드 평가가
낮아진 HP 를 정확히 봄. v0.7.8 의 `EstimateCardPower` HP-페널티 밴드와 연결:
"BLOODLETTING 두 번 연달아" 같은 위험 시퀀스가 자동 페널티.

#### Phase 1.2 — Depth=3 Beam Search

`ActionPlanner.cs`:
- 기존 `secondScore` 계산 (single-step max from second card) →
  `BestContinuation(nextState, depth=2, beamK=3, ...)`
- 신규 헬퍼 `BestContinuation(state, depth, w, beamK, out firstCardId)`:
  - 모든 candidate 점수 → 상위 K (=3) beam pruning
  - depth > 1 이면 ApplyCardPlay 후 재귀
  - depth = 0 시 return 0

탐색 트리:
```
PlanNextStep (전체 enumeration, no beam)
  ├── ApplyCardPlay → BestContinuation(state, depth=2, K=3)
  │     ├── score all, take top 3
  │     │   ├── ApplyCardPlay → BestContinuation(state, depth=1, K=3)  [single-step max]
  │     │   ...
```

= 1 first card + 2 lookahead cards = depth 3 total 카드 시퀀스 평가.

### 복잡도 (`scripts/_inspect_beam_search_complexity.py`)

| hand × targets | legacy d2 | **v0.7.9 d=3 K=3** | 배수 |
|---|---:|---:|---:|
| 4 × 1 | 16 | 40 | 2.5x |
| 6 × 1 | 36 | 108 | 3.0x |
| 6 × 3 | 324 | **1188** | 3.7x |
| 8 × 3 | 576 | 2160 | 3.8x |

per-step ~1k~2k PlanScorer.Score calls, ~50us each → ~100ms / PlanNextStep.
턴당 ~5 steps × 100ms = ~500ms. **허용 범위**.

(d=4 K=3 은 ~11x, d=3 K=5 는 ~5x. 추후 튜닝 가능.)

### 예상 효과

**Combo 발견** — depth=2 까지 못 봤던 3-step 시퀀스 인식:
- Inflame → Strike → Bash (2 Strength stack 후 강타격)
- Dexterity → Defend → Body Slam (block 빌드 후 block→dmg 변환)
- Spot Weakness → 추가 setup → finisher
- Bloodletting → Bash → Strike (자해 후 에너지 활용)

**자해 시퀀스 페널티 자동화** — HpLoss 가 state 에 반영되므로 두 번째/세 번째
자해 카드의 EstimateCardPower 가 낮아진 HP 의 페널티 밴드 (≤25 HP → -200/HP)
자동 적용.

### Phase 1 NOT cover (Phase 2 영역)

- 턴 경계 — `AdvanceTurn` / `ResolveEnemyIntents` 없음. 다음 턴 시나리오 0.
- Monte Carlo draw RNG — synthetic average draw 카드만 (v0.5.1 기존 인프라).
- Card-specific special effect (DREDGE player choice, TEAR_ASUNDER scaling)
  — 기존 EffectSynergy 휴리스틱.
- CombatAdvisor 자매 모드 포트 — 별개 작업.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_beam_search_complexity.py`: 복잡도 표 — depth=3
  K=3 가 legacy 의 3-3.8x.

## v0.7.8 (2026-05-18)

**HpLossAmount → CardEffectSummary 통합. 자해 비용 EstimateCardPower 차감.
Forward simulator Phase 1 scope 정의.**

### 자해 카드 평가 — Phase 2 완성

v0.7.7 의 `ApplyHpLossConsumer` 는 *소비자 측* (RUPTURE / TEAR_ASUNDER /
INFERNO) 만 정교화 — *생산자 측* (BLOODLETTING / OFFERING 등) 의 HP 비용은
미반영. 이번 작업은 producer cost 도 점수화.

#### 변경 파일

| 파일 | 변경 |
|---|---|
| `CardEffectSummary.cs` | `HpLossAmount` 필드 추가 |
| `CardReflection.cs` | DynamicVar `HpLoss` 추출 (`hpLoss += amount` ~line 226) |
| `SimCard.cs` | `HpLossAmount` 노출 |
| `EffectSynergy.cs` `EstimateCardPower` | HP 비례 패널티 + floor at curse-equivalent |

#### 패널티 밴드

| PlayerHp | per-HP-loss 패널티 |
|---:|---:|
| > 60 | 12 |
| 41-60 | 30 |
| 26-40 | 70 |
| ≤ 25 | 200 |

floor: `CurseInHand=-250` (in-hand) / `CurseFree=-100` (free-use).

#### 평가 표 (`scripts/_inspect_hploss_card_eval.py`)

| 카드 | HP 80 | HP 50 | HP 35 | HP 20 |
|---|---:|---:|---:|---:|
| **BLOODLETTING** (0c, 3d+2e, -3HP) | +374 | +320 | +200 | **-190** |
| **OFFERING** (0c, 3d+2e, -6HP) | +338 | +230 | -10 | **-250** [floor] |
| **HEMOKINESIS** (1c, 15dmg, -2HP) | +521 | +485 | +405 | +145 |
| **BREAKTHROUGH** (1c, 12dmg, -1HP) | +428 | +410 | +370 | +240 |
| **BLOOD_WALL** (2c, 14blk, -2HP) | +326 | +290 | +210 | -50 |

- OFFERING 은 HP ≤ 25 에서 floor (-250) — "절대 쓰지 마" 신호
- HEMOKINESIS 는 큰 damage 가 HP loss 압도 → low HP 에서도 양수 유지 (실제
  플레이 직관: 15 데미지로 적 죽이면 turn 종료 데미지 회피 가능)
- BLOODLETTING 은 HP 20 에서 -190 — 강한 경고 but not floor

#### 영향

- 자해 카드 10장 (BLOODLETTING / OFFERING / HEMOKINESIS / BREAKTHROUGH /
  BRAND / DEMONIC_SHIELD / BLOOD_WALL / HAUNT / + curse BAD_LUCK)
- pile-mean 핸들러 (DREDGE / CASCADE / MAYHEM tick 등) 도 자동으로 HP-loss
  카드 평가가 정확해짐 — pile 에 OFFERING 이 있으면 mean 이 음수 쪽으로 정확
  반영
- Floor 처리로 pile-mean 폭발 방지

### Mantra 메모리 정정

`project_vakuu_plus.md` 의 "Watcher Mantra 시스템 미인식" 항목은 **잘못된 기록**.
STS2 catalog 0 Mantra 카드 / 0 코드 reference. Regent 의 `PlayerStars` 가
STS1 Watcher Mantra 와 동일 카테고리 (`SimState.cs:78` 의 주석이 이미 명시).
메모리 strikethrough 로 정정.

### Forward simulator Phase 1 scope 문서

`docs/forward_sim_scope.md` 신규. 본격 구현 전 alignment 용 — 현재 한계,
Phase 1 API (Simulator.ApplyCardPlay / AdvanceTurn / ResolveEnemyIntents),
신규 파일 구조, 작업량 추정 (~4일), Pareto 80% scope, Phase 1 cover 안 함
영역, 결정 포인트 5개 (depth / deck mean / tree pruning / Vakuu vs Advisor
공유 / branching factor).

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_hploss_card_eval.py`: 7 카드 × 4 HP 밴드 표
  모두 예상 범위.

### Power 효과 + 자해 빌드 — v0.7.8 후 상태

- **Power 패시브** (S/A/B/C/D): 모두 PowerCatalog + amplifier axis + id-derived
  fallback 안전망 cover (v0.7.7).
- **HP_LOSS_CONSUMER**: state-aware (events × 60, producers × 35) — v0.7.7.
- **HP_LOSS_PRODUCER cost**: HP 비례 패널티 — **v0.7.8 신규**.

→ **Ironclad 자해 빌드 평가 완전 정합**. producer 비용 + consumer 가치 + 통합
state 트래킹.

## v0.7.7 (2026-05-18)

**[B] HP_LOSS 평가 정교화 + [C] PowerCatalog id-derived fallback (안전망).**

---

### Part C — PowerCatalog 24장 도달성 안전망

**배경**: v0.7.3 의 가정 — "catalog `vars: {}` Power 카드 24장은 runtime
reflection 이 `PowerVar<T>` 를 잡아 PowerCatalog 값에 도달". 이 가정 실패
시 BARRICADE / REAPER_FORM / UNMOVABLE / TRACKING 등 **S/A-tier 11장 + B
이하 13장 = 총 24장이 모두 Power effect 0점** 평가됨.

**조치**: PlanScorer Power branch 에 **id-derived fallback** 추가
(`PlanScorer.cs:174` 다음). `card.PowerApps.Count == 0` 인 Power 카드에 대해:

```
derivedName = IdToPowerName(card.Id)   // CARD.MAYHEM -> "MayhemPower"
v = max(PowerCatalog.LookupSelfBuff(derived), LookupEnemyDebuff(derived))
if (v != DefaultValue) effect += v
```

`DefaultValue=200` 가드 — heuristic-fallback 200 은 추측이라 신뢰 안 함.
explicit 등록된 카드만 가산.

**검증** (`scripts/_inspect_powercatalog_id_fallback.py`):

```
Power cards with catalog vars empty - 24 total

S  UNMOVABLE         UnmovablePower         600  OK
S  REAPER_FORM       ReaperFormPower        800  OK
S  THE_SEALED_THRONE TheSealedThronePower   700  OK
S  TOOLS_OF_THE_TRADE ToolsOfTheTradePower  500  OK
S  TRACKING          TrackingPower          600  OK
A  DARK_EMBRACE      DarkEmbracePower       500  OK
...
D  BARRICADE         BarricadePower        1200  OK
D  CALAMITY          CalamityPower          350  OK

Explicit PowerCatalog hits: 24/24
Heuristic fallback only: 0/24
```

**24/24 카드 모두 PowerCatalog 등록 확인**. id-derived fallback 가
reflection 실패 시 안전망으로 작동. 만약 reflection 이 정상이면 fallback
는 no-op (Count == 0 가드).

---

### Part B — HP_LOSS_CONSUMER state 통합

**배경**: `ApplyHpLossConsumer` 가 절대 HP 임계값 (≤30 / ≤50) 만 사용 →
RUPTURE (event 당 Strength +1), TEAR_ASUNDER (hits = 1 + events), INFERNO
(event 누적) 의 진짜 가치 미반영. 자해 빌드 (BLOODLETTING + RUPTURE) 시너지
못 봄.

(참고: TEAR_ASUNDER hits 스케일링은 v0.6.8 의 `EstimateCalculatedHits` 가
이미 처리 — 본 작업은 점수 가산 측면.)

**조치**: `ApplyHpLossConsumer` 3-signal 가산:

| 시그널 | 공식 | 비고 |
|---|---|---|
| **(1) HP 임계값** | HP≤30 → +350, HP≤50 → +200 | 기존 |
| **(2) CombatPlayerHpLossEvents** | events × 60 | 이미 발생한 자해/피격 |
| **(3) HP_LOSS 축 producer 카운트** | min(producers × 35, 300) | 미래 자해 producer 잠재량 |

HP_LOSS axis 카드: BLOODLETTING / OFFERING / HEMOKINESIS / INFERNO /
BREAKTHROUGH / BRAND / DEMONIC_SHIELD / BLOOD_WALL / CRIMSON_MANTLE 등.
저주/Status (BAD_LUCK / BECKON) 는 카운트 제외 (passive damage 라 자발 X).

**검증** (`scripts/_inspect_hploss_consumer.py`):

```
scenario                                          hp  events  prod  bonus
turn 1 healthy, no setup                          80      0      0      0
turn 1 healthy, OFFERING+HEMOKINESIS+INFERNO      80      0      3   +105
mid-fight, 1 event + 2 producers                  55      1      2   +130
low HP, 3 events + Bloodletting deck              25      3      5   +705
critical, 5 events + heavy self-harm              12      5      7   +895
late, 8 events stacked                            30      8      1   +865
```

이전: 동일 시나리오들 모두 0 또는 +350 (임계값만). 이제 producer/event
스택에 비례. RUPTURE 같은 카드 평가 시 자해 빌드 시너지 정확 반영.

---

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_powercatalog_id_fallback.py`: 24/24 OK.
- `python scripts/_inspect_hploss_consumer.py`: 8 시나리오 모두 예상 범위.

### Power 효과 커버리지 — v0.7.7 후

- **S/A/B/C/D Power 카드 100% PowerCatalog 도달 보장** (reflection +
  id-derived 이중 보완).
- 자해 빌드 (Ironclad) consumer 카드 (RUPTURE / TEAR_ASUNDER / INFERNO)
  state-aware 평가.

## v0.7.6 (2026-05-18)

**RemainingTurnsProxy 정적 3 → 동적 추정.**

### 배경

v0.7.2~0.7.5 의 모든 Power 패시브 핸들러가 `RemainingTurnsProxy = 3` 상수
사용. 실제 컴뱃 길이는 1턴 lethal 부터 10턴 보스까지 변동 큰데 모두 같은
값으로 평가됨 — 1턴 남은 컴뱃에서 MAYHEM 을 과대평가, 7턴 보스전에서 과소
평가.

### 핵심 — `RemainingTurnsEstimator` (Sts2CombatAICode/Core/Planner/)

```csharp
turns = clamp(enemy_hp_sum / playerDpt, [1, 10])
playerDpt = sum(hand_attack_damage) / 2 + player_strength × 2
```

- **O(1) 산술** — forward sim 0, Monte Carlo 0. 호출 당 ~10 micro-ops.
- 핸들러 매 호출 시 직접 calling (SimState 캐싱 안 함 — overhead 무시할
  수준이고 cache invalidation 이슈 0).
- **edge cases**:
  - 적 모두 사망 → 1
  - 핸드에 attack 0장 (Power-only opener / 방어 턴) → **FallbackTurns=3**
    (MaxTurns 10 으로 가면 패시브 과대평가)
  - 극단 강덱 → 1 (lethal next turn)
  - 극단 약덱 → 10 (clamp 상한)

### 교체된 핸들러 (6곳)

| 핸들러 | 카드 |
|---|---|
| `ApplyMayhemTickValue` | MAYHEM |
| `ApplyCardReturn` case AGGRESSION | AGGRESSION |
| `ApplyCardReturn` case NOSTALGIA | NOSTALGIA |
| `ApplyStampedeTickValue` | STAMPEDE |
| `ApplyJugglingTickValue` | JUGGLING |
| `TryApplyPoolBasedRandom` | CREATIVE_AI / HELLO_WORLD / SPECTRUM_SHIFT |

STRATAGEM (ReshuffleProxy=2), CALAMITY (ExpectedAttackChains=3),
HELLRAISER (per-Strike) 는 turns 비의존 — 그대로.

### 검증 (`scripts/_inspect_remaining_turns_estimator.py`)

```
scenario                                            enemyHp   dpt  turns
turn 1: boss 250 HP, opener hand (Strike x3)            250     9     10
turn 4: boss 60 HP left, scaled hand                     60    23      2
3 minions 30 HP each, AoE hand                           90    10      9
near-lethal: boss 15 HP, big attack                      15    14      1
no attacks in hand (Power-only opener)                  200     0      3

=== MAYHEM delta - static (3) vs dynamic ===
starter pile, near-lethal (1 turn)     static +175  dynamic -275  shift -450
starter pile, normal (3 turns)         static +175  dynamic +175  shift   +0
starter pile, long boss (7 turns)      static +175  dynamic +1075 shift +900
mid pile, near-lethal                  static +811  dynamic  -63  shift -874
strong pile, near-lethal               static +1200 dynamic +137  shift -1063
```

- 컴뱃 길이 = 3 일 때 정확히 v0.7.5 와 동일 (회귀 없음)
- near-lethal 일 때 MAYHEM 점수 −450 ~ −1063 감소 — "이제 패시브 깔 시간 없어"
- long boss 일 때 starter 덱 MAYHEM +900 상승 — "긴 컴뱃, 패시브 가치 ↑"
- 강덱은 이미 cap saturated 라 long boss 에서 추가 변화 없음

### 의도적으로 안 한 부분

- **SimState 캐싱**: per-snapshot 1회 계산해 필드로 저장하는 방안 검토했으나
  추정 자체가 너무 가볍고 (per-scoring ~10 micro-ops × ~200 calls/turn =
  2k ops) cache invalidation 위험 없는 직접 호출 선택.
- **블록 / Poison / Vuln 가산 dpt**: 1차 버전은 hand attack damage + strength
  만. 따라서 starter-vs-boss 시나리오에서 약간 long-side 로 추정. 실제 cap
  (MAYHEM 1200 등) 이 영향 제한.
- **Forward simulation 기반 length 추정**: 큰 인프라 작업 영역. 별개 추진.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_remaining_turns_estimator.py`: 8 시나리오 + 9
  MAYHEM 비교 모두 예상 범위.

## v0.7.5 (2026-05-18)

**잔존 Power 패시브 6장 — DrawPile / Hand / Pool / Strike-count aware 평가.**

### 배경

v0.7.4 까지 Power 효과 평가에서 flat magnitude 로 남아 있던 6장 처리. v0.7.3
MAYHEM 패턴 (PowerCatalog baked baseline + state-derived delta + cap/floor)
을 각 메커니즘에 맞춰 적용.

### 카드별 처리

| 카드 | tier | 메커니즘 | state 입력 | tick 공식 | baked |
|---|---|---|---|---|---:|
| **STAMPEDE** | B | 턴 종료 시 DrawPile random Attack auto-play | DrawPile.Attacks | mean(free=T) × 3 | 350 |
| **NOSTALGIA** | D | 첫 attack/skill → top of draw (retain-like) | Hand.Atk/Skl | mean(free=F) × 0.4 × 3 | 250 |
| **STRATAGEM** | C | shuffle 시 random card → hand | DiscardPile | mean(free=F) × 2 | 250 |
| **CALAMITY** | D | Attack 사용 후 random Attack → hand | PoolMeans.attack | mean × 3 | 350 |
| **HELLRAISER** | D | Strike-named 카드 draw 시 auto-play | Strike count in piles | count × 90 | 300 |
| **JUGGLING** | D | 3번째 Attack 복사 → hand | Hand.Attacks | mean × 3 × 0.4 | 300 |

### 검증 (`scripts/_inspect_remaining_power_passives.py`)

```
STAMPEDE  (baked=350, cap=1200)
  starter Strikes×5    n=5  mean=300  tick= 900  delta= +550
  strong attacks×4     n=4  mean=900  tick=2700  delta=+1200 [cap]

NOSTALGIA  (baked=250, cap=800)
  starter mix          n=6  mean=187  tick= 224  delta=  -26
  strong mix           n=4  mean=690  tick= 828  delta= +578

STRATAGEM  (baked=250, cap=800)
  mixed (4)            n=4  mean=438  tick= 876  delta= +626
  curse-polluted       n=4  mean= -10  tick=-20  delta= -250 [floor]
  strong (6)           n=6  mean=690  tick=1380  delta= +800 [cap]

CALAMITY  (baked=350, cap=1500)
  IRONCLAD pool.mean=317  tick= 951  delta= +601
  REGENT   pool.mean=458  tick=1374  delta=+1024

HELLRAISER  (baked=300, cap=1000)
  Strike-less          -> -300 (strip baked)
  6 Strikes            count=6  delta= +240
  12 Strikes           count=12 delta= +780

JUGGLING  (baked=300, cap=800)
  3 Strikes            mean=230  delta=-24
  3 heavy atks         mean=630  delta=+456
```

### 튜닝 노트

- **EAC (Expected Attack Chains)** for CALAMITY: 초기 6 → 3 으로 하향. 6 은
  cap saturation 으로 character 변별력 0. 에너지/핸드캡 dilution 고려한
  realistic net chain 수 = 3 (3 attacks/turn × 1 turn dilution).
- **HELLRAISER PerStrikeBonus = 90**: free-Strike 가치 (~300) − paid-Strike
  (~230) = ~70/play, ×~1.3 plays/strike 평균 = ~90.
- **NOSTALGIA RetainDiscount = 0.4**: Retain 1턴 ≈ 40% 추가 효용
  (v0.7.1 HIDDEN_GEM 0.6 retain-2 의 절반 — retain 1).
- **JUGGLING HitRate = 0.4**: 3+ attacks/turn 확률, mixed deck 휴리스틱.

### Power 효과 커버리지 — v0.7.5 후

| Tier | 처리됨 (pile/hand/pool-aware) |
|---|---|
| **S/A** | 모두 PowerCatalog + amplifier axis 로 cover (Tracking/Unmovable/Reaper Form/Sealed Throne 등) |
| **B** | MAYHEM(C)·AGGRESSION·STAMPEDE·HELLO_WORLD·CREATIVE_AI·SUBROUTINE·TYRANNY·TRASH_TO_TREASURE — pile/pool-aware 또는 axis 시너지 |
| **C** | STRATAGEM·SPECTRUM_SHIFT pile-aware, MASTER_PLANNER (단순) flat |
| **D** | NOSTALGIA·HELLRAISER·JUGGLING·CALAMITY pile/hand/pool-aware, BARRICADE flat (block-amp axis 로 별도 cover) |

**모든 D-tier 이상 Power 패시브 100% state-aware.** flat 잔존은 단순
효과 (BARRICADE block carryover, MASTER_PLANNER Skill→Sly buff) 뿐.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_remaining_power_passives.py`: 6 카드 모두 예상치,
  cap/floor 정상 발화.

## v0.7.4 (2026-05-18)

**AGGRESSION Power passive — v0.7.3 MAYHEM 패턴으로 정렬.**

### 배경

v0.7.1 에서 `CARD.AGGRESSION` 는 이미 DiscardPile-aware:
```
v = discard_attack_mean × 1.5
```
하지만 `PowerCatalog["AggressionPower"]=400` 이 PlanScorer Power branch 에서
이미 가산되는데 EffectSynergy 가 위 보너스를 **on top** 으로 추가 →
**over-count**. v0.7.3 의 MAYHEM 처리에서 정한 단일 패턴 (delta-from-baseline)
과도 불일치.

### 변경

`EffectSynergy.cs:830` 의 `CARD.AGGRESSION` case 를 MAYHEM 패턴으로 재작성:

```
baked = PowerCatalog.LookupSelfBuff("AggressionPower")  // 400 동적
tick  = discard_attack_mean × UpgradeFactor × RemainingTurnsProxy
delta = clamp(tick − baked, −baked, +Cap)
b    += delta
```

- **RemainingTurnsProxy = 3** — v0.7.2/0.7.3 와 동일
- **UpgradeFactor = 1.3** — 회수된 Attack 이 "임시 강화" 되는 효과 근사
  (damage +30% 정도의 net 보너스)
- **Cap = 1200**, **Floor = −baked** (MAYHEM 과 동일)
- **freeUse = false** — 회수된 카드는 손에 들어가 normal-cost 로 플레이

### 시나리오 (`scripts/_inspect_aggression_eval.py`)

| 시나리오 | atks | mean | tick | delta | 판정 |
|---|---:|---:|---:|---:|---|
| empty discard / no attacks |  0 |   0 |     0 |    +80 | baseline |
| starter Strikes (Strike=6 ×5)        |  5 | 230 |  897 |   +497 | ok |
| mid (mix 6/8/12 dmg)                 |  6 | 316 | 1232 |   +832 | strong |
| **Bludgeon-class (18 ×4)**           |  4 | 630 | 2457 |  +1200 | **cap hit** |
| **Ironclad finisher mix (25/20/15)** |  3 | 706 | 2753 |  +1200 | **cap hit** |

(저주/Status floor 케이스는 Attack-only 필터링 때문에 존재하지 않음 —
mean 은 항상 ≥ 0.)

### Power 효과 커버리지 — 진행 상황 (v0.7.4 기준)

| Tier | Power 패시브 카드 | DrawPile/Discard-aware 처리 |
|---|---|---|
| **B+** | AGGRESSION, MAYHEM (C), HELLO_WORLD (B), CREATIVE_AI (B), WHITE_NOISE | ✅ 5장 모두 |
| C | STRATAGEM, MASTER_PLANNER | flat |
| D | NOSTALGIA, HELLRAISER, CALAMITY, JUGGLING | flat |

**B-tier 이상은 모두 cover 완료.** D/C tier 4장만 follow-up 영역.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings.
- `python scripts/_inspect_aggression_eval.py`: 6 시나리오 모두 예상치,
  cap 정상 발화.

## v0.7.3 (2026-05-18)

**MAYHEM Power passive — DrawPile-aware turn-tick value.**

### 배경

MAYHEM 은 매 턴 시작 시 DrawPile 맨 위 카드를 auto-play 하는 Power 패시브.
v0.7.2 까지 PlanScorer Power 브랜치가 `PowerCatalog["MayhemPower"]=500` 만
flat 가산 — DrawPile 구성을 무시함. Bludgeon-급 강덱이든 저주 다수 덱이든
동일 점수.

### 핵심 — `ApplyMayhemTickValue` (EffectSynergy.cs)

```
delta = (DrawPile.mean[freeUse=true] * RemainingTurnsProxy) - baked
b += clamp(delta, -baked, +Cap)
```

- **baked**: `PowerCatalog.LookupSelfBuff("MayhemPower")` 동적 lookup — 매직
  넘버 0 (PowerCatalog 값을 600 으로 올려도 자동 추종).
- **RemainingTurnsProxy = 3**: v0.7.2 의 Level 4 휴리스틱과 통일된 컴뱃-길이
  proxy.
- **Cap = 1200**: 강덱 (mean 600+) 에서 runaway 방지.
- **Floor = −baked**: PowerCatalog 가산분을 완전히 상쇄할 수 있되 음수로는
  안 가게 (total MAYHEM 점수가 0 미만 못 가는 정상화).
- **저주/Status 포함**: `EstimateCardPower(freeUse=true)` 가 -100 반환 →
  pile-polluted 덱이 mean 을 끌어내려 자연스러운 패널티.
- **빈 DrawPile**: +80 baseline (discard 리셔플 후 다시 작동 — 완전히 0 아님).

### 검증 (`scripts/_inspect_mayhem_eval.py`)

| 시나리오 | n | mean | tick×3 | delta | 판정 |
|---|---:|---:|---:|---:|---|
| empty            |  0 |    0 |     0 |    +80 | baseline |
| starter (S6/D5)  | 10 |  225 |   675 |   +175 | ok |
| mid (mean ~437)  |  8 |  437 |  1311 |   +811 | strong |
| **strong** (mean ~637) |  8 |  637 |  1911 |  +1200 | **cap hit** |
| curse-polluted (3C+4S) |  7 |  128 |   384 |   -116 | weak |
| **all curses**   |  5 | -100 |  -300 |   -500 | **floor (= -baked)** |

### 의도적으로 안 한 부분

- **STAMPEDE / CALAMITY / NOSTALGIA / STRATAGEM / HELLRAISER / JUGGLING** —
  같은 catalog `vars: {}` 그룹이지만 각각 다른 메커니즘 (random Attack only /
  top-modification / hand-copy / attack-chain). 별도 핸들러로 follow-up.
- **AGGRESSION Power 패시브** — 카드 단위 (`CARD.AGGRESSION` in `ApplyCardReturn`,
  v0.7.1) 로는 이미 DiscardPile-aware. Power 패시브 (`AggressionPower`) 시점에서
  추가로 DiscardPile 가산 필요 시 별도 작업.
- **동적 RemainingTurns 추정** — SimEnemy HP/intent 로 컴뱃 길이 추정은 모드
  전체 공유 인프라가 될 만한 크기. v0.7.x 범위 밖.

### 검증

- `dotnet build`: 0 compilation errors. (mods 폴더 copy 는 게임 실행 중이라
  락 — DLL 자체는 빌드 성공.)
- `python scripts/_inspect_mayhem_eval.py`: 6 시나리오 모두 예상치 출력,
  cap / floor 정상 발화.

## v0.7.2 (2026-05-18)

**Level 4 — Pool-based random 카드 (CREATIVE_AI / HELLO_WORLD / WHITE_NOISE /
DISCOVERY / SPLASH / JACKPOT / DISTRACTION / CALL_OF_THE_VOID / LARGESSE /
SPECTRUM_SHIFT) 의 character pool 평균치 기반 평가.**

### 배경

v0.7.1 의 `EstimateCardPower` 는 *카드 객체* 를 평가할 뿐, "캐릭터 풀에서
한 장이 random 으로 뽑힐 때 평균 가치" 는 다루지 못함. 그래서 Level 4 카드
12장이 카드-id 별 **flat magnitude** 로만 평가됨 (CREATIVE_AI=150, HELLO_WORLD=120
등). 현재 캐릭터 풀이 power 가 약한지 강한지 무관하게 같은 점수.

### 핵심 인프라

1. **`EffectScoringWeights.cs`** — `EstimateCardPower` 가중치를 상수화. Python
   mirror (`scripts/_effect_scoring_weights.py`) 가 같은 숫자 참조해
   pool-means 생성 시 drift 방지.

2. **`scripts/build_pool_means.py`** — `cards_catalog.json` 의 1117 카드를
   character × pool-filter 로 그룹핑해 mean / top1of3 / top1of5 분포를 100k
   Monte Carlo 샘플링으로 산출. `PowerCatalog.cs` 의 SelfBuff/EnemyDebuff
   dict 를 정규식으로 파싱해 power-value 도 그대로 mirror.

3. **`Sts2CombatAICode/Core/Data/pool_means.json`** — 5 character × 11 filter
   = 55 분포 통계. csproj `EmbeddedResource` 로 DLL 에 패킹.

4. **`SimState.CharacterId`** — `player.Character.Id.Entry` 캡쳐. 미상시 빈
   문자열 → flat-magnitude fallback.

5. **`PoolMeans` 로더** — 1회 정적 로드, `Get(characterId, filter)` 가
   PoolSummary { N, Mean, Top1Of3, Top1Of5 } 반환.

6. **`TryApplyPoolBasedRandom`** — 12 card-id 별 (filter, aggregation,
   multiplier) 매핑. Pool-aware 값 사용 가능하면 그걸로 평가, 아니면 기존
   flat switch 로 폴백.

### 카드별 평가 표 (IRONCLAD 기준, 단위: 평가 점수)

| 카드 | filter | agg | mult | flat | pool-aware | 변화 |
|---|---|---|---:|---:|---:|---:|
| **CREATIVE_AI**     | power_free | mean    | 3 | 150 | 420 | +270 |
| **HELLO_WORLD**     | common     | mean    | 3 | 120 | 726 | +606 |
| **SPECTRUM_SHIFT**  | colorless  | mean    | 3 | 100 | 642 | +542 |
| **WHITE_NOISE**     | power_free | mean    | 1 | 350 | 140 | −210 |
| **DISTRACTION**     | skill_free | mean    | 1 | 240 | 134 | −106 |
| **CALL_OF_THE_VOID**| all_free   | mean    | 1 | 100 | 261 | +161 |
| **LARGESSE**        | colorless  | mean    | 1 | 150 | 214 |  +64 |
| **DISCOVERY**       | all        | top1of3 | 1 | 280 | 373 |  +93 |
| **SPLASH**          | attack     | top1of3 | 1 | 200 | 485 | +285 |
| **JACKPOT**         | all_free   | mean    | 3 | 180 | 783 | +603 |

- WHITE_NOISE / DISTRACTION 의 하락은 의도된 보수성. `EstimateCardPower` 의
  Power-divisor (free=5) 가 context-free 디스카운트라 PowerCatalog
  base 600 → 120 으로 떨어짐. v0.7.1 의 pile-based 핸들러와 동일 룰.
- Per-card cap 800 — REGENT JACKPOT 1 장만 캡 적중.
- RemainingTurnsProxy = 3 — 컴뱃 평균 길이 conservative proxy.

### 의도적으로 안 한 부분

- **MAYHEM** (Power 패시브 — DrawPile-aware): PowerCatalog 확장 영역. Level 4
  대상 아님.
- **MAD_SCIENCE** (type=None 무작위 effect): Level 5. catalog 에 effect-type
  메타데이터 embed 필요.
- **Cost-set-0 이후 후속 카드의 BuildSynergy 시너지**: PoolMeans 는 단일 카드
  평균. 시너지 매칭은 단독 axis 라우팅에서 처리.

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings (Godot source-gen,
  VakuuExecutor null).
- `python scripts/build_pool_means.py`: 5 character × 11 filter 모두 산출,
  최소 pool 크기 n=17 (NECROBINDER power).
- `python scripts/_inspect_pool_based_eval.py` — character-by-character
  spot-check 표 정상 출력.

## v0.7.1 (2026-05-17)

**Level 3 — Pile-based random 카드의 SimState.DrawPile / DiscardPile 활용
정확 평가.**

### 배경

v0.6.9 까지 pile-based random 카드들 (DREDGE / CATASTROPHE / WISH 등 11장)
이 카드-id 별 **flat magnitude** (DREDGE=450 등) 로만 평가됨. SimState 가
실제 pile 카드 리스트를 보유하고 있는데도 활용 안 함:

- DiscardPile 에 강한 카드 가득 → DREDGE 진짜 가치 ~800
- DiscardPile 에 약한 카드만 → DREDGE 가치 ~200
- 현재는 둘 다 +450 으로 동일 평가

### 핵심 헬퍼

**`EstimateCardPower(SimCard, SimState, bool freeUse)`** — context-free 카드 가치 추정:
- Attack: TotalDamage × 50 (free) or 35 (in-hand)
- Skill: Block × 30 (free) or 25
- DrawCount × 70, EnergyGain × 130 (free) or 60
- PowerApps: PowerCatalog 가치 / 5 (free) or 7 (in-hand, 큰 discount)
- Cost penalty/bonus (free 일 땐 적용 안 함)
- Curse/Status: -100 (free) 또는 -250 (in-hand)

### 패치 — 7개 핸들러 재작성/신설

**`ApplyCardReturn` 재작성 (in-hand value):**
- **DREDGE**: top-N positives × player choice (negative 카드 skip)
- **NEOWS_FURY**: random 2 → DiscardPile mean × 2
- **AGGRESSION**: discard Attack mean × 1.5 (Power 다중 턴)
- 기타 (NOSTALGIA / PHOTON_CUT / GLIMMER / ANOINTED): pile-aware 또는 flat fallback

**`ApplyDrawPileSearch` 재작성:**
- **WISH** (id-dispatch, axis 없음): DrawPile 의 **max** (player choice)
- **CHARGE**: top-2 positives + upgrade bonus
- **FOREGONE_CONCLUSION**: mean × 1.5 (next-turn discount)

**`ApplyAutoPlayFromPile` 신설 (freeUse=true):**
- **CASCADE**: DrawPile non-curse mean × (X+1)
- **CATASTROPHE**: DrawPile non-curse mean × 2
- **UPROAR**: DrawPile Attack mean × 1
- **BEAT_DOWN**: DiscardPile Attack mean × 3

**`ApplyDrawPileRandomModifier` 신설:**
- **HIDDEN_GEM**: DrawPile non-curse/non-Power mean × 0.6 (Retain 2 = ~60% 추가 가치)
- **DRAIN_POWER**: DiscardPile mean × 0.4 (2 카드 × ~20% upgrade)

### 시나리오 (DiscardPile = [Strike, Bash, Curse])

| 카드 | v0.6.9 (flat) | v0.7.1 (pile-aware) |
|---|---:|---:|
| **DREDGE** (top-3 from discard) | 450 | top-2 positives (Strike+Bash) = **510**, curse 제외 |
| **NEOWS_FURY** (2 random) | flat | (Strike+Bash+Curse mean=87) × 2 = **174** |
| **WISH** (best of draw) | 200 (id flat) | DrawPile max (예: Anger) = **290** |
| **CATASTROPHE** (2 autoplay) | 0 (no handler) | freeUse mean × 2 = **434** |
| **CASCADE** (X+1 autoplay) | 0 (no handler) | freeUse mean × (X+1) = **변동** |
| **HIDDEN_GEM** [S] (Retain on random) | 0 | mean × 0.6 = **~120** |

### 검증

- `dotnet build`: 0 errors, 0 warnings
- 0-rule blind: 32 → **31** (1장 감소)
- EffectSynergy reach: 109 → **132** (23장 추가)
- A-tier blind: 1장만 남음 (MAD_SCIENCE — type=None 무작위 effect, Level 5 영역)

### 의도적으로 안 한 부분

- **Level 4 (pool-based random)**: CREATIVE_AI, HELLO_WORLD, DISCOVERY, WHITE_NOISE 등 ~12장. character pool 의 평균 가치를 build 시점에 통계로 embed 해야 정확. 별도 데이터 작업 영역.
- **MAYHEM (Power 패시브)**: PowerCatalog 가 처리. DrawPile-aware 가산은 PowerCatalog 확장 필요.
- **재귀적 PlanScorer.Score 호출**: `EstimateCardPower` 는 의도적으로 light heuristic (target / 상태 무관). 재귀 호출 시 stack overflow / 비용 폭증 위험.

## v0.7.0 (2026-05-17)

**Random multi-hit 카드 binomial 확률 모델 + 단일타겟 overkill discount.**

### 배경

사용자 보고: "RICOCHET 3×3 (총 9) 가 HP4 적 + HP20 적 상황에서 단일 6-dmg
카드보다 우선되는 게 맞나? 실제로는 50% 확률 처치라 6-dmg 단일타겟 (100%
처치) 이 더 안전한데."

게임 본체 `AttackCommand.TargetingRandomOpponents` 디컴파일 결과: 매 hit
마다 `Rng.CombatTargets.NextItem(validTargets)` 로 **독립 재선택** 확인.
PlanScorer 는 `TargetType.RandomEnemy` 카드를 단일타겟으로 취급해 모든 hit
이 `targetIdx` 에 적중한다고 가정 → LETHAL bonus over-credit.

### 두 가지 별개의 누락

**1. Random multi-hit 분포 미모델링**
- RICOCHET 3×3 vs 2적: 단일타겟 처리 → 모든 9 데미지가 E1 적중 가정 → LETHAL +5000
- 실제: 적 1마리 처치 확률 = P(X≥2 | Bin(3, 0.5)) = 50%
- 결과: 무작위 카드가 deterministic 단일타겟보다 항상 우위 (잘못)

**2. Overkill discount 부재**
- 6-dmg 카드가 HP4 적 칠 때 2 데미지 낭비 → score 에 반영 안 됨
- 6×50 + LETHAL(5000) = 5300 (실제 가치는 4×50 + 5000 = 5200)

### 패치

**`PlanScorer.cs:Attack 브랜치`** — `isRandom` 분기 신설:
```csharp
bool isRandom = card.Target == TargetType.RandomEnemy;
```

**Damage scoring 분기:**
```csharp
if (isAoe)       // 기존 AOE 합산
else if (isRandom)
{
    // 살아있는 적 N마리 → pHit = 1/N
    // 각 적의 expected damage = effHits × pHit × perHitForE
    // 각 적의 EffectiveHp 로 overkill clamp
    // sum → aggregatedDmg
}
else             // 단일타겟: dmgForScoring = min(effectiveTotal, t.EffectiveHp) 추가 clamp
```

**Target-bonus 분기 (LETHAL + intent 보너스):**
```csharp
if (isRandom)
{
    // 각 적에 대해:
    //   hitsNeeded = ⌈EffHp / perHit⌉
    //   pLethal = BinomialAtLeast(effHits, 1/N, hitsNeeded)
    //   weightedBonus = ScoreAttackTarget(idx, EffHp) × pLethal
}
```

**Binomial 헬퍼 (`BinomialAtLeast` + `BinomialCoefficient`)**:
- `P(X ≥ k | X ~ Bin(n, p))` 직접 CDF 합산
- STS2 hit count 최대 ~10 — overflow 무관

### Spread vs Kill 자동 구별

기존 ScoreAttackTarget 의 intent-aware 보너스가 그대로 작동:
- BuffEnemyKillBonus 1000 / HealEnemyKillBonus 800 / SummonEnemyKillBonus 500 / DeathBlowEnemyKillBonus 700
- RealLethalKillBonus 5000

랜덤 카드는 `pLethal` 로 weighted → must-kill 시 deterministic 카드가 자연스럽게 이김. 분산 가치 시 expected damage 가 단일타겟 데미지 초과 → random 이김.

### 시나리오 검증 (수동 trace)

| 시나리오 | 6-dmg 단일 | 3×3 무작위 | 결과 |
|---|---:|---:|---|
| **A**: HP4 + HP20 normal | 5200 (clamp4 + LETHAL) | 2900 (0.5×LETHAL + 분산 dmg) | **단일 +2300** ✓ |
| **B**: HP20 + HP20 (처치 불가) | 300 (dmg only) | 400 (분산 dmg) | **무작위 +100** ✓ |
| **C**: HP4 BuffIntent + HP20 | 6200 (LETHAL + BuffKill) | 3400 (0.5×weighted) | **단일 +2800** ✓ |

### 검증

- `dotnet build`: 0 compile errors (DLL copy 만 game-lock — 게임 종료 후 재빌드 필요)
- 모든 RANDOM 축 카드에 적용 (RICOCHET / RIP_AND_TEAR / STARDUST / VOLLEY / SWORD_BOOMERANG / BOUNCING_FLASK / FLAK_CANNON 등 14장)
- 단일타겟 overkill clamp 는 모든 single-target attack 에 적용

### 의도적으로 안 한 부분

- **AOE overkill clamp**: 이미 `StatusMath.EffectivePerEnemyTotal` 이 per-enemy block / shell 처리. AOE 전반 clamp 는 over-discount 위험.
- **HardenedShellPower 의 random 분배**: 각 hit 의 shell budget 추적은 forward-sim 영역. 현재 random 분기는 per-enemy 적용 안 함 (예외적 케이스).
- **StackededPower / 다중 buff 적의 가중치 차등**: ScoreAttackTarget 가 이미 처리.

## v0.6.9 (2026-05-17)

**Tier 1+2+3 카드 커버리지 일괄 패치 — 12개 패턴, 30+ 카드.**

### 배경

v0.6.8 audit 후 식별된 12개 미지원 패턴:
- Tier 1 (EASY, 4개): STATUS_TO_HAND / FOCUS / STATUS_CONSUMER / MaxHp
- Tier 2 (MEDIUM, 4개): DRAW_CONDITIONAL / CARD_RETURN / DRAW_PILE_SEARCH / COST_ENABLER
- Tier 3 (HARD, 2개): CARD_GEN / EXHAUST_TARGET_RANDOM
- 부가: OSTY-conditional / VigorPower / ENLIGHTENMENT / PRECISE_CUT / WHITE_NOISE&DISCOVERY 류

### 전체 카드 룰-hit 통계 (576 base cards)

| 메트릭 | v0.6.6 | v0.6.8 | **v0.6.9** | 개선 |
|---|---:|---:|---:|---:|
| 0-rule blind | 92 (16.0%) | 87 (15.1%) | **32 (5.6%)** | **-60장** |
| S-tier blind | 14 | 9 | **0** | -14 |
| A-tier blind | 53 | 48 | **1** | -52 |
| EffectSynergy 적용 | 46 | 58 | **109** | +63 |
| HandSynergy 적용 | 40 | 41 | **47** | +7 |

### Tier 1 패치

**STATUS_TO_HAND 페널티** (`ApplyStatusToHandPenalty`):
- CRASH_LANDING [A] AOE 잔해 가득 → -350
- COLLISION_COURSE [A] 잔해 1장 → -150

**FOCUS HandSynergy** (`HandSynergy.FocusPower` case + `remainingOrbCards`):
- 일반 + Temporary FocusPower 모두 처리. 카드별 PowerVar<FocusPower> 자동 추출됨
- SYNCHRONIZE 의 `CalculatedFocus` → `CardReflection` 가 TemporaryFocusPower 로 승격
- 적용: DEFRAGMENT [S], BIASED_COGNITION [A], FOCUSED_STRIKE [A], HOTFIX [B], SYNCHRONIZE [B]

**STATUS_CONSUMER** (`ApplyStatusConsumer`):
- 손패 status/curse 수 × 180 (cap 540), 없으면 -100
- 적용: ROCKET_PUNCH, FLAK_CANNON, COMPACT (+ Powers: ITERATION, SMOKESTACK, TRASH_TO_TREASURE)

**MaxHp gain** (`CardEffectSummary.MaxHpAmount` + `ApplyMaxHpGain`):
- BRIGHTEST_FLAME [A] (+1), FEED [C] (+3 on kill)
- 영구 +40 per point — small but recognized

### Tier 2 패치

**DRAW_CONDITIONAL** (`ApplyDrawConditional`): id-gated per-card
- FTL: `cardsThisTurn < 3` 시 +200
- PALE_BLUE_DOT: `≥ 4` 시 +200
- FETCH / COMPILE_DRIVER / 기타 fallback

**CARD_RETURN** (`ApplyCardReturn`): DiscardPile 카운트 기반
- DREDGE: min(discardSize, 3) × 150
- AGGRESSION / NOSTALGIA / STRATAGEM / PHOTON_CUT / GLIMMER / ANOINTED

**DRAW_PILE_SEARCH** (`ApplyDrawPileSearch`): DrawPile 사이즈 gated
- CHARGE [A] +280, FOREGONE_CONCLUSION / ANOINTED, 빈 pile -100

**COST_ENABLER** (`ApplyNextCardCostEnabler`):
- UNRELENTING (Attack), SYNTHESIS (Power), POUNCE (Skill) — 손패 최고 cost × 220
- 매치 없으면 -150

### Tier 3 패치

**CARD_GEN** (`ApplyCardGen`): 카드 id 별 가치 추정
- 구체 생성 (Shiv/Slime+): BLADE_OF_INK +600, BLADE_DANCE +450
- 선택형 (CHARGE/NIGHTMARE/GUARDS): +350~400
- 무작위 (CALL_OF_THE_VOID/CREATIVE_AI): +100~150
- axis-missing 카드 fallback (id-gate): WHITE_NOISE [S]=+350, DISCOVERY/DISTRACTION/WISH/LARGESSE/SPLASH

**EXHAUST_TARGET_RANDOM** (`ApplyRandomExhaustPenalty`):
- CINDER/TRUE_GRIT: -90~120
- THRASH: -60 (자체 데미지 가산 offset)
- TYRANNY (Power): +40 (turn-thin)

### 부가 패치

**OSTY-conditional 공격** (`ApplyOstyConditional`):
- SkeletonCount > 0 시 +150, 죽었으면 **-350**
- 적용: POKE [B], SWEEPING_GAZE [B], FLATTEN/RATTLE/RIGHT_HAND_HAND/SNAP [C]

**ENLIGHTENMENT** (`ApplyEnlightenmentBonus`):
- 손/덱/discard 의 cost > 1 카드 cost 절감 합산 × 80, cap 1600
- B-tier 스킬, 전투 영향 큰 카드

**PRECISE_CUT anti-handsize** (`ApplyPreciseCutScaling`):
- 13 - 2 × others → other-card 수 × -100 페널티

**VigorPower HandSynergy**:
- next attack +N — 남은 공격 1+ 시 N × 50, 없으면 -100

### 남은 진짜 blind (32장)

| Tier | 카드 수 | 분류 |
|---:|---:|---|
| S | **0** | ✓ 전부 cover |
| A | **1** | MAD_SCIENCE (type=None 무작위 effect — 본질적 forward-sim 영역) |
| B | 6 | CASCADE/METAMORPHOSIS/SECRET_TECHNIQUE 등 random pulls |
| C | 8 | random/scaling 카드 |
| D | 6 | low-tier scaling/random |
| Status/Curse/Quest | 33 | 의도된 미스코어 (UNPLAYABLE / 페널티) |

남은 32장은 **무작위 카드 가치 예측** 또는 **multi-combat scaling** 영역으로
정적 평가의 본질적 한계. forward-sim / depth-2 lookahead 확장 영역.

### 검증

- `dotnet build`: 0 errors, 0 warnings (4 pre-existing)
- `measure_ai_card_coverage`: 회귀 없음
- `_inspect_card_rule_hits`: blind 92→32 (-65%)

### 새 audit script

- `scripts/_inspect_remaining_gaps.py` — 패턴 별 카드 + solvability 분류

## v0.6.8 (2026-05-17)

**Turn/combat-history 카운터를 SimState 에 노출 + 가변-데미지 정적 한계 영역
6장 (RAGE, TEAR_ASUNDER, HEAVENLY_DRILL, EIDOLON, STOKE, PURITY) 정확 평가.**

### 배경

v0.6.7 까지 다음 6장은 "SimState 가 추적 안 하는 turn-level 카운터 필요"
로 분류되어 정적 평가 한계 영역이었음. 게임 본체 `CombatManager.Instance.History`
가 모든 카드 플레이 / 데미지 받음 이벤트를 추적하므로 이를 read-only 로
스냅샷하면 모두 정확히 평가 가능.

### SimState 확장 (3개 신규 필드)

```csharp
public int TurnAttacksPlayed { get; init; }       // 이번 턴 사용한 공격 수
public int TurnSkillsPlayed { get; init; }        // 이번 턴 사용한 스킬 수
public int CombatPlayerHpLossEvents { get; init; } // 이번 전투 HP 손실 횟수
```

### StateSnapshotter 확장

`CombatManager.Instance.History.Entries` 를 single-pass 로 walk:
- `CardPlayFinishedEntry` 중 `RoundNumber == cs.RoundNumber && CurrentSide == cs.CurrentSide && Card.Owner == player` 인 entry 의 type 별 카운트
- `DamageReceivedEntry` 중 `Receiver == player.Creature && Result.UnblockedDamage > 0` 카운트
- try/catch — 실패 시 0으로 fallback (defensive)
- `FormatForLog` 에 AtkT/SklT/HpLost statusBits 추가

### 카드별 패치

| 카드 | 메커니즘 | 구현 위치 | 효과 (예시) |
|---|---|---|---|
| **RAGE** | Skill applies RagePower N → 공격당 N block | CardReflection 가 `DynamicVar("Power", N)` + `id == "CARD.RAGE"` → PowerApps["RagePower"]=N 매핑. HandSynergy 에 `RagePower` case 추가 (남은 공격 × N × 30) | 공격 4장 손패 → +270 (기존 PowerCatalog 300 단독) |
| **TEAR_ASUNDER** | "5 dmg, 이번 전투 HP 손실 횟수만큼 반복" | `EstimateVariableHits` 에 id-gate: `1 + state.CombatPlayerHpLossEvents` | HP loss 4회 → Hits=5, 데미지 25 (기존 5) |
| **HEAVENLY_DRILL** | "8 × X, X≥4 면 X×=2" | `EstimateVariableHits` X_COST 분기에 `if (id == HEAVENLY_DRILL && x >= 4) x *= 2` | Energy 5 → 데미지 80 (기존 40) |
| **EIDOLON** | "hand 모두 소멸, 9장 이상이면 Intangible" | `EvaluateExhaustBurstSpecial` 헬퍼: hand ≥ 9 면 +900, 미만이면 -(handExhausted × 60) | hand 9 → +900, hand 5 → -240 |
| **STOKE** | "hand 소멸, N개 무작위 카드 생성" | 동일 헬퍼: handExhausted × 40 | hand 5 → +160 |
| **PURITY** | "최대 3장 선택 소멸 (player choose)" | 동일 헬퍼: curse/status 카드 수 × 220, 없으면 +40 | curse 2장 → +440 |

### 의도적으로 안 한 부분

- **STOMP / PINPOINT**: 데미지 고정. cost-discount 메커니즘인데 게임 본체
  `EnergyCost.AddThisTurn(-N)` 가 이미 `GetAmountToSpend()` 에 반영되므로
  `CardReflection.GetCost` 가 자동으로 할인된 cost 반환 — **이미 정확** 임.
- **CORRUPTION / FEEL_NO_PAIN / MONOLOGUE / SHADOWMELD / STAMPEDE**:
  Power-type 카드로 PowerCatalog 가 자기 stack 처리. `DynamicVar("Power", N)`
  는 PowerCatalog id-derived lookup 으로 cover.
- **UPPERCUT / PUTREFY / SHOCKWAVE / EXPOSE**: 별도 PowerVar<T> (Vuln/Weak)
  가 vars 에 있어 기존 PowerApps 추출 path 가 처리.

### 검증

- `dotnet build`: 0 errors / 0 warnings
- `measure_ai_card_coverage`: 기존 metric 회귀 없음
- 카드 룰-hit audit: RAGE / TEAR_ASUNDER 등 추가 룰 매칭 확인

## v0.6.7 (2026-05-17)

**남은 8개 stem 의 fine-grained stack-aware EffectSynergy 보너스 추가 —
SimState 에 pile/ally/token 카운터 5종 신설.**

### 배경

v0.6.6 까지 14개 complete-pair stem 모두에 SkillSequencingTier Setup 가 들어
갔으나, **stack 누적량에 비례한 burst-타이밍 자동 인지**는 8개 stem 에서 결여:
CUNNING / SOUL / FORGE / LORDS_BLADE / SKELETON / EXHAUST / VOLATILE / SHIV.
`BuildSynergy` 의 200pt 플랫 보너스만 받아서 "영혼 30 쌓였을 때 SOUL_STORM"
vs "영혼 5 쌓였을 때 SOUL_STORM" 을 구분 못함.

### 게임-측 데이터 소스 매핑 (decompile 검증)

`ilspycmd` 로 `sts2.dll` 까서 각 stem 의 실제 메커니즘 확인:

| Stem | 게임 본체 구현 | 가시화 경로 |
|---|---|---|
| SOUL | `MegaCrit.Sts2.Core.Models.Cards.Soul` (token card) | piles 의 `Soul` 인스턴스 카운트 |
| SHIV | `MegaCrit.Sts2.Core.Models.Cards.Shiv` (token card) | piles 의 `Shiv` 인스턴스 카운트 |
| SKELETON | `Osty` monster, `cs.Allies` 에 등재 | `Allies.Where(Monster is Osty && IsAlive)` |
| EXHAUST | `PileType.Exhaust` (enum 값 존재) | `PileType.Exhaust.GetPile(player).Cards.Count` |
| FORGE / LORDS_BLADE | `SovereignBlade` (token card) | piles 의 `SovereignBlade` 카운트 |
| VOLATILE | `CardKeyword.Ethereal` (휘발성 = Ethereal 의 한국어) | hand 의 `IsEthereal` 카운트 (기존 필드) |
| CUNNING | `CardKeyword.Sly` (카드 키워드, 스택 없음) | **mechanism 없음 → skip** |

### SimState 확장

```csharp
public int SoulInPiles { get; init; }         // hand+draw+discard+exhaust 의 Soul 카드
public int ShivInPiles { get; init; }         // hand+draw+discard+exhaust 의 Shiv 카드
public int SkeletonCount { get; init; }       // 살아있는 Osty 동맹
public int ExhaustPileSize { get; init; }     // Exhaust pile 카드 수
public int SovereignBladeCount { get; init; } // piles 의 SovereignBlade (Forge/LordsBlade proxy)
```

VolatileInHand 은 `SimCard.IsEthereal` 으로 inline 계산 — 신규 필드 불필요.

### StateSnapshotter 변경

`CountTokenCards()` 헬퍼 신설 — 한 pile 을 한 번 walk 하면서 Soul/Shiv/
SovereignBlade 세 종류를 동시에 카운트. hand + draw + discard + exhaust
네 pile 모두 처리.

Skeleton 카운트는 `cs.Allies` 를 walk 하며 `monster.GetType().Name == "Osty"`
+ `IsAlive` 필터. 실패해도 0 으로 fallback (try/catch).

`FormatForLog` 에 statusBits 5개 추가 (Soul/Shiv/Osty/Exh/Blade) — DecisionLog
에 stack 가시화. 0 일 때는 출력 안 함 (조용한 디폴트).

### EffectSynergy 핸들러 (6개 신규)

| 핸들러 | Axis | Stack signal | Per-stack | Cap | No-source 페널티 |
|---|---|---|---|---:|---:|
| `ApplySoulConsumer` | SOUL_CONSUMER/AMPLIFIER | SoulInPiles | ×25 | 400 | -200 |
| `ApplyShivConsumer` | SHIV_CONSUMER/AMPLIFIER | ShivInPiles | ×30 | 360 | -180 |
| `ApplySkeletonConsumer` | SKELETON_CONSUMER/AMPLIFIER | SkeletonCount | 300/360 (포화) | - | **-400** (BONE_SHARDS 사례) |
| `ApplyExhaustConsumer` | EXHAUST_CONSUMER | ExhaustPileSize | ×20 | 320 | 없음 (pile 단조 증가) |
| `ApplyBladeAmplifier` | FORGE/LORDS_BLADE_AMPLIFIER | SovereignBladeCount | ×150 | 450 | -200 |
| `ApplyVolatileConsumer` | VOLATILE_CONSUMER | Hand.IsEthereal + 0.25×Draw.IsEthereal | hand×90 + draw×25 | 540 | -150 |

각 핸들러는 3-tier signal pattern (stack > 0 / producer-in-hand / nothing) 동일.
BuildSynergy 가 이미 producer↔consumer 페어 200pt 를 주므로 매그니튜드를
DoT 핸들러 (×20) 보다 약간 작게 책정 — over-credit 방지.

### 영향 카드 (대표)

- **SOUL_STORM** (NECROBINDER, Attack [C]): 영혼 30 쌓이면 +400, 5장이면 +125
- **REAVE** (NECROBINDER, Attack [C]): 동일하게 영혼 누적량 따라 가산
- **BONE_SHARDS** (NECROBINDER, Attack [S]): Osty 살아있으면 +300, 죽었으면 **-400** (현재 dead-weight 정확 표현)
- **PROTECTOR** (NECROBINDER, Attack [A]): 동일
- **HAMMER_TIME** (REGENT, Power [A]): SovereignBlade 1개=+150, 2개=+300, 3+개=+450
- **CONQUEROR** (REGENT, Skill [D]): 동일
- **PAGESTORM** (NECROBINDER, Power [S]): hand 에 Ethereal 3장이면 +270
- **VEILPIERCER** (NECROBINDER, Attack [B]): 동일
- **PACTS_END** (IRONCLAD, Attack [S]): Exhaust pile 10장이면 +200
- **KNIFE_TRAP** (SILENT, Skill [B]): Shiv 5장이면 +150

### 검증

- `dotnet build`: 0 errors, 4 pre-existing warnings
- `_inspect_mechanic_coverage.py`: **14/15 complete-pair stem 이 SkillSetup+EffSyn YES** (ORB / CUNNING 의도적 제외 명시)
- `measure_ai_card_coverage.py`: 기존 metric 회귀 없음

### 의도적으로 안 한 것

- **ORB**: `BuildSynergy.Compute` 가 이미 orb full/empty 처리 (`_combat`/`v0.5` 시점 코드). EffSyn 추가는 double-credit.
- **SovereignBlade `_currentDamage` 읽기**: private field reflection 가능하지만, 단순 카운트로 충분. 정밀도 vs 복잡도 trade-off 에서 카운트 선택.

### 추가 패치 — CUNNING discard-trigger signal (1차 누락 부분)

초기에 "CUNNING 은 키워드만, 스택 메커니즘 없음" 으로 제외했으나 재검토 결과
**Sly = discard 시 자동 발동** 메커니즘으로 확인 (`CardCmd.cs`):

```csharp
foreach (CardModel card in discardCards) {
    if (card.IsSlyThisTurn) slyCards.Add(card);
    // ... add to discard pile ...
}
foreach (CardModel item in slyCards) {
    await AutoPlay(choiceContext, item, null, AutoPlayType.SlyDiscard);
}
```

따라서 **CUNNING_CONSUMER (forced-discard 카드: ACROBATICS, CALCULATED_GAMBLE,
PREPARED, HIDDEN_DAGGERS, SURVIVOR) 의 가치는 손패 Sly 카드 수에 비례**.

#### 구현

- `SimCard.IsSly` 신규 필드. `Axes.Contains("CUNNING")` (raw axis) 으로 판정.
  catalog audit 결과 CUNNING raw axis ↔ `keywords:["Sly"]` 가 **8/8 1:1 매칭**.
- `EffectSynergy.ApplyCunningConsumer` 신규 핸들러. 손패 Sly 카드 수 × 110 (최대 3장),
  producer-in-hand 폴백 +60, 둘 다 없으면 -150 (consumer 의 draw/block 자체 가치는 보존).

#### 영향 카드

- **ACROBATICS** (SILENT, Skill [S]): "Draw 3, discard 1" → 손패 Sly 1장이면 +110, 2-3장이면 +220~330
- **CALCULATED_GAMBLE** (SILENT, Skill [S]): "Discard hand, draw same" → 동일 식
- **HIDDEN_DAGGERS** (SILENT, Skill [A]), **PREPARED**, **SURVIVOR** 등 동일

이로써 16개 stem 중 **15개 (ORB 제외) 가 full stack-aware coverage** 달성.

### 추가 패치 — 카드별 룰-hit audit 후 누락 카드 처리

`_inspect_card_rule_hits.py` 로 각 base card 가 어떤 PlanScorer 룰에 hit 하는지
정적 매핑. 결과: 0-rule 카드 92장 (16%), S-tier 14장 / A-tier 53장이 "blind"
상태. 그 중 진짜 누락 메커니즘 식별:

#### Gap 1: STRENGTH_DOWN 축 (8 카드, S-tier 3장)

**미커버**: DARK_SHACKLES [S], ENFEEBLING_TOUCH [S], PIERCING_WAIL [S], SHARED_FATE [A],
DYING_STAR [B], MANGLE [C], CRUSH_UNDER [C], MONARCHS_GAZE [D]

`StrengthLoss` var (DynamicVar 형식, PowerVar 아님) 으로 적 힘 감소. WEAK 와
같은 위협-감소 setup 인데 EffectSynergy 에 핸들러 없었음.

**구현**:
- `CardEffectSummary.StrengthDownAmount` 신규 필드
- `CardReflection.GetEffectSummary` 가 `DynamicVar.Name == "StrengthLoss" / "EnemyStrengthLoss"` 추출
- `EffectSynergy.ApplyStrengthDown`: amount × savingsHits × 30 (cap 1200).
  AOE 검출 (AOE_DEBUFF / AOE_OTHER / TargetType.AllEnemies) 으로 multi-enemy
  합산. 공격 적이 없으면 -200.

#### Gap 2: HEAL 축 (5 카드, S-tier 1장)

**미커버**: NOT_YET [S, Heal 10], SPUR [C, Heal 5], FEED [C — MaxHp gain, 별개],
BRIGHTEST_FLAME [A — MaxHp gain, 별개], DEVOUR_LIFE [Power, 기존 cover]

**구현**:
- `CardEffectSummary.HealAmount` 신규 필드
- `CardReflection` 가 `DynamicVar.Name == "Heal"` 추출
- `EffectSynergy.ApplyHeal`: HP threshold 기반 (≤20: ×40, ≤40: ×25, 그 외 ×12).
  no-incoming-damage AND high HP 면 -150 (full HP 회복 페널티).
- MaxHp-gain 카드는 `HealAmount=0` 이라 자동 skip — long-run scaling 은 별도 영역.

#### Gap 3: Dead handler 정리

audit 결과 0 카드 매칭 핸들러 식별:
- `SOUL_AMPLIFIER` — 0 cards → 핸들러 호출 조건에서 제거 (CONSUMER 만 유지)
- `BURN_CONSUMER` / `BURN_AMPLIFIER` — 0 cards
- `CONSTRICT_CONSUMER` / `CONSTRICT_AMPLIFIER` — 0 cards

`DotStems = {"POISON", "DOOM", "BURN", "CONSTRICT"}` → `{"POISON", "DOOM"}` 으로
축소. enemy-side SimEnemy.BurnAmount/ConstrictAmount 필드는 유지 (적이 player
에게 적용한 debuff 가능성).

#### 영향 카드 (대표)

- **DARK_SHACKLES** (SHARED, Skill [S]): 적 1마리 공격 의도 시 9×1×30=+270, 다중공격이면 +540+
- **PIERCING_WAIL** (SILENT, Skill [S]): AOE — 적 3마리 single-hit 시 6×3×30=+540
- **NOT_YET** (IRONCLAD, Skill [S]): HP 20 이하 + 다음 턴 데미지 10 예상 시 10×40=+400
- **MANGLE** (IRONCLAD, Attack [C]): 데미지 15 + StrengthLoss 10 → +300 추가

#### 정적 평가 한계 영역 (별도 작업)

여전히 raw 데미지만 받는 카드들 (블라인드 92→90):
- **X_COST 가변** (WHIRLWIND, SKEWER, VOLLEY, HEAVENLY_DRILL): X 값 runtime 알아야 정확
- **EXHAUST_BURST** (FIEND_FIRE): hand 사이즈 비례
- **SKILL_CONDITIONAL / ATTACK_CONDITIONAL** (PINPOINT, MAKE_IT_SO, STOMP): 누적 조건
- **STATUS_TO_HAND** (CRASH_LANDING, COLLISION_COURSE): 잔해 추가 페널티 미반영
- **ABSENT_CONDITIONAL** (GRAND_FINALE): pile 상태 의존
- **SCALING-on-Attack** (MAUL): 영구 증가 — 런 후반 가치 반영 어려움

이들은 정적 evaluation 의 본질적 한계 — depth-2 lookahead / runtime simulator
확장 영역.

### Audit script

- `scripts/_inspect_card_rule_hits.py` — 카드별 룰 hit 매핑 + dead handler 감지 + per-rule coverage

### 추가 패치 — 가변-데미지 / 가변-블록 카드 정확 평가 (EXHAUST_BURST + X_COST)

사용자 지적: "FIEND_FIRE (지옥불) 같은 카드 평가는?" / "SECOND_WIND (기사회생) 도?"

`vars={Damage:7}` 만 보면 FIEND_FIRE 는 7 데미지 1히트로 평가되어 실제 게임의
`7 × hand_size` 와 큰 괴리. SECOND_WIND 도 base Block:5 만 봐서 실제
`5 × non-attack hand` 와 괴리. SimState 가 hand / energy 카운트 노출하므로 정확
추정 가능 — runtime simulator 없이 정적 평가 단계에서 처리.

#### 패턴별 추정 룰

| Axis | 카드 | 추정 |
|---|---|---|
| **EXHAUST_BURST (Attack)** | FIEND_FIRE [S, 7] | `Hits = hand non-curse 카드 수 + 1 (self)` |
| **EXHAUST_BURST (Skill, Block>0)** | SECOND_WIND [A, 5 block] | `Block ×= non-attack non-curse hand 수` |
| **X_COST (Attack)** | SKEWER [A, 8] / WHIRLWIND [A, 5 AOE] / VOLLEY [A, 10] / ERADICATE [B, 11] | `Hits = state.PlayerEnergy` |

#### 구현

**`PlanScorer.EstimateVariableHits(card, state)`** — Attack 전용 헬퍼:
- EXHAUST_BURST: `state.Hand` 의 non-curse non-self 카운트 + 1
- X_COST: `state.PlayerEnergy` (X-cost 카드는 남은 에너지 전부 소비)
- 둘 다 아니면 0 반환 (기본 Hits 사용)

**`PlanScorer.EstimateBlockMultiplier(card, state)`** — Skill 전용:
- EXHAUST_BURST + Block > 0 시 non-attack non-curse hand 수 반환
- 그 외 1 (변경 없음)

Attack 브랜치 `effHits = max(card.Hits, variableHits)` 로 override. AOE 분기도
동일하게 effHits 사용 (HardenedShellRemaining 체크도 effHits 반영).

Skill 브랜치 `rawBlock = card.Block × blockMultiplier` → effectiveBlock 계산.

#### 영향 — 대표 시나리오

| 카드 | 상황 | Before | After |
|---|---|---|---|
| **FIEND_FIRE** [S] | hand 5장 | dmg 7 → 7점 등급 | dmg 35 → A-S 등급 (uplift 약 +1400) |
| **SECOND_WIND** [A] | non-attack 3장 hand | block 5 | block 15 (uplift 약 +400) |
| **WHIRLWIND** [A] | energy 3, AOE 2 적 | 10 AOE | 30 AOE (uplift 약 +1000) |
| **SKEWER** [A] | energy 3, 단일 타겟 | 8 | 24 (uplift 약 +800) |
| **VOLLEY** [A] | energy 2, random | 10 | 20 (uplift 약 +500) |
| **ERADICATE** [B] | energy 4, retain | 11 | 44 (uplift 약 +1600) |

#### 의도적으로 안 한 부분

- **HEAVENLY_DRILL** 의 "X ≥ 4 시 ×2 doubling" — base X 만 적용 (loss of accuracy on 1 card)
- **STOMP / RAGE** (ATTACK_CONDITIONAL — 사용한 공격 수 비례): 턴 내 played-count 추적 필요. SimState 미노출.
- **TEAR_ASUNDER** (MULTI_HIT_SCALING via HP loss count): combat-level history 필요.
- **PINPOINT** (SKILL_CONDITIONAL cost reduction): turn-level skill count 필요.
- **EIDOLON / STOKE / PURITY** (EXHAUST_BURST 의 비-데미지 효과): 특수 보너스 처리는 카드별 override 영역.

이들은 SimState 확장 (TurnPlayedAttacks / TurnPlayedSkills / CombatHpLost / TurnExhaustCount 등)
이 필요한 별도 패치 — depth-2 lookahead 와 함께 다룰 영역.

## v0.6.6 (2026-05-17)

**전체 pair-axis stem 에 대한 Skill within-turn ordering 커버리지 + 가시
stack 기반 EffectSynergy 확장.**

### 배경 (1차 — POISON/DOOM)

POWER 타입 POISON/DOOM 카드 (NOXIOUS_FUMES, COUNTDOWN, REAPER_FORM 등 9장)
는 `PowerSequencingTier` 에 모두 등록되어 Setup/Scaling 우선순위가 잡혀
있었으나, **Skill / Attack 타입 producer/consumer 는 ordering hook 누락**:

- POISON Skill (8): BOUNCING_FLASK / BUBBLE_BUBBLE / CORROSIVE_WAVE /
  DEADLY_POISON / HAZE / MIRAGE / MONOLOGUE / SNAKEBITE — 전부 `SkillTier.Unknown`
- DOOM Skill (7): DEATHBRINGER / DEATHS_DOOR / END_OF_DAYS / NEGATIVE_PULSE /
  NO_ESCAPE / OBLIVION / SCOURGE — 6장이 `Unknown` (DEATHBRINGER 만 부수
  Weak 적용으로 Setup 이었음)
- POISON Attack (2) + DOOM Attack (2 — BLIGHT_STRIKE, TIMES_UP) — `EffectSynergy`
  가 target stack 신호를 읽지 않아 consumer (TIMES_UP) 가 dead-weight

### 변경

**`SkillSequencingTier.Classify`** — POISON/DOOM/BURN/CONSTRICT producer/
amplifier 를 `SkillTier.Setup` 으로 분류. PowerApps 키 (PoisonPower/DoomPower/
BurnPower/ConstrictPower) 또는 axis suffix (`*_PRODUCER`, `*_AMPLIFIER`) 매칭.

**`SkillSequencingTier.ConditionalBonus`** — Setup tier no-beneficiary
판정을 일반화. VULN/WEAK producer 는 남은 attack 수, DoT producer 는
같은 stem 의 CONSUMER/AMPLIFIER 가 손패에 있는지 (또는 attack 수 fallback)
를 검사. penalty label `setupNoAtk` → `setupNoBeneficiary`.

**`EffectSynergy.Compute`** — POISON/DOOM/BURN/CONSTRICT CONSUMER/AMPLIFIER
axis 처리 추가. target 의 실제 stack (PoisonAmount/ConstrictAmount/BurnAmount/
DoomPower) 을 읽어 per-stack ×20 (consumer) / ×10 (amplifier) bonus. stack
없으면 any-enemy / in-hand producer fallback, 둘 다 없으면 -300/-150.

### 영향 카드

| 카테고리 | 카드 수 | 카드 (대표) |
|---|---:|---|
| Skill — Setup tier 신규 분류 | 12 | BOUNCING_FLASK, BUBBLE_BUBBLE, CORROSIVE_WAVE, DEADLY_POISON, END_OF_DAYS, HAZE, MIRAGE, NEGATIVE_PULSE, NO_ESCAPE, OBLIVION, SCOURGE, SNAKEBITE |
| Attack — EffectSynergy consumer 보너스 신규 | 1 | TIMES_UP (DOOM_CONSUMER) |
| Skill — EffectSynergy consumer 보너스 신규 | 2 | BUBBLE_BUBBLE (POISON_CONSUMER), DEATHS_DOOR (DOOM_CONSUMER) |

### 비고 (1차)

- POWER 타입 POISON/DOOM 9장은 기존에 이미 PowerSequencingTier 에서 cover됨 — 본 패치 변경 없음.
- MONOLOGUE / TYRANNY 처럼 axis tag (`POISON`) 가 실제 효과와 무관해 보이는
  경우는 PRODUCER suffix 없으면 무시 — mis-tag 방어.
- `ComboRecognition` 의 producer↔consumer/amplifier edge 매칭은 기존에 이미
  POISON_PRODUCER ↔ POISON_CONSUMER 를 포함 (≥3 link 체인에서만 발화).

### 2차 확장 — 모든 complete-pair stem 으로 일반화

`scripts/_inspect_mechanic_coverage.py` 로 audit 한 결과 POISON/DOOM 외에도
같은 producer/consumer 패턴을 가진 stem 이 다수 있었음. **complete pair
(P≥1 AND (A≥1 OR C≥1))** 인 14개 stem 모두에 Skill Setup-tier ordering 적용.

| Stem | Skill 프로듀서 | Setup tier (post) | Notes |
|---|---:|---|---|
| VULN  | 6 | ✓ (pre) | |
| WEAK  | 7 | ✓ (pre) | |
| POISON | 6 | ✓ (1차) | |
| DOOM  | 6 | ✓ (1차) | |
| ORB   | 18 | **제외** | `BuildSynergy` 가 orb full/empty 처리 — double-credit 방지 |
| STAR  | 9 | ✓ (2차) | |
| EXHAUST | 10 | ✓ (2차) | |
| CUNNING | 5 | ✓ (2차) | |
| SKELETON | 9 | ✓ (2차) | |
| SOUL  | 6 | ✓ (2차) | |
| FORGE | 7 | ✓ (2차) | |
| LORDS_BLADE | 7 | ✓ (2차) | |
| VOLATILE | 1 | ✓ (2차) | |
| SHIV  | 3 | ✓ (2차) | |
| DARK_ORB | 2 | ✓ (2차) | |

### 구현 — SkillSequencingTier

`PairStemsForSetup` HashSet 도입. `Classify` 가 임의 `<stem>_PRODUCER` /
`<stem>_AMPLIFIER` 축을 발견하면 stem 이 allowlist 에 있을 때 Setup 반환.

`ConditionalBonus` 의 no-beneficiary penalty 를 3 카테고리로 분리:
- **debuff** (VULN/WEAK) → 남은 attack 0 시 -200
- **dot** (POISON/DOOM/BURN/CONSTRICT) → 같은 stem CONSUMER/AMPLIFIER 손패 OR 남은 attack 둘 다 없으면 -200
- **resource** (STAR/CUNNING/SOUL/FORGE/LORDS_BLADE/SKELETON/SHIV/VOLATILE/EXHAUST/DARK_ORB) → 페널티 없음 (resource 는 다음 턴에도 가치 보존)

### 구현 — EffectSynergy 추가 stack-aware 핸들러

| 카드 | Axis | 새 핸들러 | Stack signal |
|---|---|---|---|
| `CARD.STARDUST` | STAR_CONSUMER | `ApplyStarConsumer` | `state.PlayerStars × 15` (cap 450) |
| `CARD.DARKNESS` | DARK_ORB_AMPLIFIER | `ApplyDarkOrbAmplifier` | `OrbQueue` 의 Dark 개수 × 120 (cap 360) |

기타 stem (CUNNING/SOUL/FORGE/LORDS_BLADE/SKELETON/EXHAUST/VOLATILE/SHIV)
은 `SimState` / `SimEnemy` 가 player-side stack 을 노출하지 않아 stack-aware
EffectSynergy 추가 불가. **BuildSynergy 의 generic producer↔consumer pair
bonus (200점) 가 baseline 역할** — pair 가 hand 에 있을 때만 가산.

### Audit 도구

- `scripts/_inspect_poison_doom.py` — 1차용 (POISON/DOOM 카드 목록 + Setup 분류 검증)
- `scripts/_inspect_mechanic_coverage.py` — 2차용 (전체 pair-stem coverage 매트릭스)



## v0.6.5 (2026-05-17)

**Power-typed amplifier 카드 hand-aware 보너스 — AmplifierSynergy 를
Power 브랜치에서도 호출.**

### 배경

`AmplifierSynergy.cs` 는 POWER_AMPLIFIER / REPLAY / ATTACK_REPLAY /
SKILL_REPLAY 축의 카드가 같은 손패의 best target 점수의 50% 를 추가
점수로 받는 룰. 그런데 호출이 Attack/Skill 브랜치에만 있어서, 카드 자체가
**Power 타입인 amplifier** (8장) 가 hand-aware 보너스 못 받는 gap 발생.

### 영향 카드 (8장)

| 카드 | Tier | Axis | 기존 PowerCatalog |
|---|---|---|---:|
| `CARD.SUBROUTINE` | B | POWER_AMPLIFIER | 500 |
| `CARD.ECHO_FORM` | S | REPLAY | 1500 |
| `CARD.MAYHEM` | C | REPLAY | 500 |
| `CARD.ITERATION` | B | REPLAY | 350 |
| `CARD.JUGGLING` | D | REPLAY | 300 |
| `CARD.LOOP` | D | REPLAY | 350 |
| `CARD.NOSTALGIA` | D | REPLAY | 250 |
| `CARD.STAMPEDE` | B | ATTACK_REPLAY_RANDOM | 350 |

### 변경

`PlanScorer.cs` Power 브랜치에 `AmplifierSynergy.Compute(card, state, w)`
호출 추가. tierOrdering / tierCond / buildBonus 다음, lethalPenalty 직전
위치. Total / Effect 합산에 `powerAmpBonus` 포함.

```csharp
var (powerAmpBonus, powerAmpDetail) = AmplifierSynergy.Compute(card, state, w);
if (powerAmpBonus != 0) details.Add(powerAmpDetail);
```

### 시뮬레이션 예시

손패: SUBROUTINE + DEMON_FORM + 2 attacks

- **이전**: Subroutine 1650, DemonForm 2350 → DemonForm 먼저 (Subroutine 의
  에너지 효과 다음 Power 에 못 적용)
- **이후**: Subroutine 1650 + (DemonForm × 0.5 = ~1175) = **2825**, DemonForm
  2350 → **Subroutine 먼저** ✓

### Recursion 안전성

`AmplifierSynergy.IsValidTarget` 의 `!HasAmplifierAxis(c)` 체크가 Power→
Power-amplifier 무한 루프 방지. 두 amplifier (예: DemonForm + Mayhem 둘 다
REPLAY) 가 서로 target 하지 않음 — 의도된 보수적 설계.

### 미해결 gap

**`CARD.STORM`** description 은 "파워 카드 사용 시 전기 영창" 인데
catalog axes 가 `ORB_PRODUCER, LIGHTNING_ORB` 뿐 — **POWER_AMPLIFIER axis
누락**. 부모 repo 의 axis-tagger 작업 영역 (이 repo 에서 직접 못 고침).

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
