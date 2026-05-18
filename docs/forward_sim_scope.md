# Forward Simulator — Phase 1 Scope (v0.8.x)

작성: 2026-05-18 (v0.7.8 후속 영역 정의용)

## 1. 현재 상태

| 항목 | 현재 |
|---|---|
| Depth-2 lookahead | `ActionPlanner.PlanNextStep` 에서 1 카드 play → live state 변경 → 다음 plan |
| State cloning | `SimState.DeepClone` 존재. **그러나 사용처 없음** — 실제 분기 0 |
| Hypothetical card effect application | **없음** — 카드 효과를 시뮬레이션에 적용하는 함수 0 |
| 적 intent → player damage | `PlanScorer.PredictPlayerDmg` (block / vuln / frail / intangible 반영) |
| 턴 종료 시뮬 | 없음 — draw N / energy reset / status decrement / EOT block 자동 처리 부분만 |

**= 분기형 forward simulation 인프라 0 상태.**

## 2. 왜 필요한가

현재 평가의 hard cap:

1. **카드 순서 비교 불가** — Bash → Pommel vs Pommel → Bash 둘 다 시뮬해야 어느 게 더 나은지 알 수 있음. 현재는 "지금 최고 점수 카드" 만 그리디 선택.
2. **턴 경계 못 넘음** — "Powers 깔고 다음 턴 Bludgeon" 같은 setup plays 의 진짜 가치 평가 불가
3. **Vakuu 외 모드 확장 한계** — Combat Advisor (자매 모드) 가 1턴 위험도를 보려면 forward sim 필수
4. **Monte Carlo 0** — drowning RNG 모델링 (셔플 분포) 으로 EV 계산하려면 sim 필요

## 3. Phase 1 — 최소 분기 sim

목표: **"이 카드 시퀀스 vs 저 시퀀스" 비교 가능한 최소 인프라**.

### 3.1 API

```csharp
internal static class Simulator
{
    /// <summary>
    /// Apply card play to a cloned state. Returns the new state. Does NOT
    /// modify input. Caller is responsible for cloning if needed.
    /// </summary>
    public static SimState ApplyCardPlay(SimState state, SimCard card, int targetIdx);

    /// <summary>
    /// Resolve enemy intents on the current state. Models block consumption,
    /// HP loss, status decrement. Used for end-of-turn simulation.
    /// </summary>
    public static SimState ResolveEnemyIntents(SimState state);

    /// <summary>
    /// Advance to next turn: enemy intents resolve, player block resets,
    /// new hand drawn (mean-card from deck pool used, not RNG-specific cards),
    /// energy reset, status powers decrement.
    /// </summary>
    public static SimState AdvanceTurn(SimState state);
}
```

### 3.2 ApplyCardPlay scope

Phase 1 cover 범위 (Pareto 80%):

- ✅ Damage: enemy HP / block 차감 (single + AOE)
- ✅ Block gain: player block
- ✅ Energy spend / gain
- ✅ Draw / discard 카드 (mean card from pile, no specific card RNG)
- ✅ Power application (player + enemy)
- ✅ Self-damage (HpLoss — v0.7.8 인프라 활용)
- ✅ Exhaust (카드 제거)

Phase 1 NOT cover:

- ❌ Random target / random pile-search (v0.7.1/0.7.2 휴리스틱 그대로)
- ❌ Card-id 별 special-case effect (DREDGE choice / TEAR_ASUNDER scaling — 휴리스틱)
- ❌ Orb queue dynamics (Defect)
- ❌ Multi-character ally creatures (Necrobinder skeletons)
- ❌ Inter-card chain trigger (Anger → discard 자기 복사본)

### 3.3 신규 파일

```
Sts2CombatAICode/Core/Sim/Simulator.cs              — ApplyCardPlay/AdvanceTurn
Sts2CombatAICode/Core/Sim/SimCardEffectApplier.cs   — 카드별 효과 mutator
Sts2CombatAICode/Core/Sim/IntentResolver.cs         — 적 intent → state mutation
```

### 3.4 통합 지점

```
ActionPlanner.PlanNextStep
  └─ 현재: live state → snapshot → score → play
  └─ 신규: cloned state 에서 depth-3 ~ depth-5 forward sim
         → 최선 시퀀스 선택 → 첫 카드만 실제 play
```

### 3.5 작업량 추정

| 단계 | 작업 |
|---|---|
| 1 | `Simulator.ApplyCardPlay` 기본 (damage/block/energy/draw) | 0.5d |
| 2 | Power application + status modifier | 0.5d |
| 3 | `ResolveEnemyIntents` + EOT processing | 0.5d |
| 4 | `AdvanceTurn` + next-hand 모델링 (deck mean) | 1d |
| 5 | `ActionPlanner` 통합 + depth-N tree search | 1d |
| 6 | spot-check + regression test | 0.5d |
| 총 | | **4일** |

## 4. 의도적으로 안 함 (Phase 1)

- Card RNG mirror — 1117 카드의 special effect 를 모두 mirror 하면 작업이 폭발.
  Phase 1 은 generic damage/block/power 만. Special cards (DREDGE choice 등) 는
  v0.7.x 휴리스틱 유지.
- Monte Carlo — draw RNG 분포 sampling. Phase 2 영역.
- 다중 후보 비교 UI / DecisionLog 확장 — 별개 영역.

## 5. 결정 포인트 (구현 전 합의 필요)

1. **Depth 상수** — 3 vs 5? Branching factor × cost trade-off.
2. **Mean card from deck** — 다음 턴 draw 를 어떻게 모델링? `EstimateCardPower(deck.mean)` 으로 abstract card 1장 만들기?
3. **Tree pruning** — 모든 후보 explore vs alpha-beta vs 상위 K?
4. **Vakuu vs Advisor** — Vakuu (의사결정) 와 Combat Advisor (위험도 표시) 둘 다 같은 simulator 공유?

## 6. 다음 액션

- 사용자 결정 포인트 (5절) 확인 후 Phase 1 단계 1 (ApplyCardPlay 기본) 시작
- 또는 사용자 가 다른 영역 우선시 시 보류
