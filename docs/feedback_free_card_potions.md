[풀런 피드백 #4 — 무료-카드 포션(Power/Attack/Skill) 과소평가 + Power 복리/조기사용]

## 한 줄 요약
Power/Attack/Skill 포션은 "랜덤 [타입] 카드 3장 중 1장을 **그 턴 무료(0코)로** 손에 추가"인데,
플래너 sim이 (A) 생성 카드를 **free 로 모델링하지 않고** (B) Power 의 **복리/타입을 무시**해서
과소평가→hoarding 합니다. Defect 보스 near-win 패배의 직접 원인 중 하나.

## 게임 실제 메커니즘 (디컴파일 검증, sts2.dll)
`PowerPotion`/`AttackPotion`/`SkillPotion`.OnUse (모두 동일 패턴, TargetType.Self, CombatOnly):
```
cards = 3 random [type] cards from character pool
chosen = FromChooseACardScreen(cards, canSkip:true)   // 3장 중 1장 선택 (mid-combat)
chosen.SetToFreeThisTurn()                              // ★ 그 턴 0코
AddGeneratedCardToCombat(chosen, Hand)
```
→ **순수 무료 카드.** 기회비용 ≈ 0 (에너지 안 듦). Power 의 경우 고른 Power 카드는 **전투 내내 누적**.

## 현재 플래너 처리 (이미 GenerateCards 로 모델링됨, 하지만 두 갭)
**갭 A — 생성 카드를 free 로 안 만듦.** `AnalyticalSimulator.cs:~2877`:
```
case PotionKind.GenerateCards:
    for (...) newHand.Add(MakeAverageDrawCard(next));   // 평균 카드 주입, free 표시 없음
    next = next with { Hand = newHand };
```
→ continuation 은 이 카드들을 **정상 코스트로 가정** → "spare energy 있어야 cash-in" (PlanScorer.cs:313 주석
그대로) → 에너지 부족하면 가치 0 처리 → 포션을 hoarding. 실제론 0코라 항상 즉시 가치.

**갭 B — Power 복리/타입 무시.** GenerateCards 가 타입 무관 `MakeAverageDrawCard` 주입 →
PowerPotion 이 줄 **Power 카드(영구 누적)** 를 일반 카드로 취급. 게다가 depth-2 lookahead 는 2턴만
보므로 "turn 1 에 Power 깔면 전투 끝까지 복리" 를 구조적으로 못 봄 → PowerPotion 과소평가 + 조기사용
우선순위 없음.

`PlanScorer.cs:313`:
```
case GenerateCards: case GenerateShivs:
    return potion.Amount * 20;   // flat base, 나머지는 continuation 의존
```

## 수정 방향 (제안)
**A) 생성 카드 = free 주입.** Sim 의 GenerateCards 에서 주입 카드를 그 턴 무료로:
   - `PlayerFreeAttacks`/`PlayerFreeSkills`/`PlayerFreePowers` 를 타입별 +Amount (sim 이 이미 가진 free 카운터),
     또는 `PlayerFreeHandThisTurn` 류로 처리 → continuation 이 0코로 굴림.
   - 효과: "턴에 쓸 카드 애매하면 무조건 사용" 이 자연히 도출 (기회비용 0).

**B) 타입 인지 + Power 복리 + 조기사용.**
   - PowerPotion → 평균 카드 대신 **Power 카드** 주입(또는 그 효과). Power 가치는 다른 power 들처럼
     **× 남은 턴수**로 환산(복리). depth-2 너머의 복리를 base 가치에 직접 반영.
   - AttackPotion → 평균 **공격** 카드, SkillPotion → 평균 **스킬**. (현재 전부 generic 평균)
   - Power 가치를 남은-턴 비례로 주면, **전투 초반일수록 PowerPotion 가치↑** = 조기사용이 자동 우선.

## 진단 증거 (Defect 보스전, n=100, 전 레버 ON)
- 보스 19/100 도달, **처치 0.** baseline act2 도달 0%.
- near-win 5건(보스를 ≤60HP, 심지어 1HP 까지 깎음)이 **전부 미사용 포션 보유**:
  - 보스 252→12 / 내HP 40 — **Power Potion** 보유(미사용)
  - 보스 252→46 / 내HP 10 — **Focus Potion** 보유
  - 보스 252→1  / 내HP 6  — Block/Droplet 보유
- 즉 덱데미지는 거의 충분(보스 ~10-40HP). 무료-카드/버프 포션 한 장이면 마무리되는데 플래너가 hoarding.

## 검증 방법 (수정 후)
풀런(상점·포션 전 레버 ON), 동일 N(시드셋 고정), Defect:
```
STS2_ROUTE=1 STS2_SHOP=1 STS2_SMITH=1 STS2_USE_POTIONS=1 STS2_PLANNER_POTIONS=1
  STS2_SETUP_SUPPLEMENT=1 STS2_HP_PRESERVE=120 python python/play_full_run.py 100 Defect
```
- 1차 지표: **near-win→win 전환** (보스전 진입 중 보스 처치 수 0 → ↑). near-win 카운트(보스 ≤60HP)는
  n=100 에 ~5건이라 전환이 보이기 쉬움.
- 2차: act-1 클리어(act2 도달) 0 → ↑.
- **주의(메서드론): play_full_run 은 run-index=시드. 다른 N 배치는 다른 시드셋이라 비교 불가 — 반드시
  동일 N(동일 시드) 으로 before/after.** boss-reach 자체는 시드-결정이라 안 움직여도 정상; 보는 건 보스-처치.

## 비고
- 이 포션들은 사용 시 mid-combat "카드 선택"(3중1) 화면을 띄움. 플래너가 considerPotions 로 lookahead 에
  넣을 때 그 선택지(3장)를 어떻게 고르는지도 함께 점검 요망(best-of-3). 헤드리스 실사용 경로에서 그 선택
  해소(ThrowingPlayerChoiceContext freeze 회피)도 확인.
- "버프 포션 시퀀싱"(Power 를 전투 초반 우선 사용)은 위 B 의 남은-턴 비례 가치로 자연 해결됨.
