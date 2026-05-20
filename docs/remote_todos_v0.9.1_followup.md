# Remote Agent TODO templates — v0.9.1 follow-up

v0.9.1 의 5개 follow-up 작업을 claude.ai 리모트 트리거로 실행할 때 사용할 prompt 템플릿.

**선결 조건**: 모든 TODO 는 `master` 가 v0.9.1 이상 (CHANGELOG 에 v0.9.1 entry 존재) 인 상태를 가정. 각 trigger 의 첫 단계에서 `git log --oneline -3` 으로 확인.

**리모트 트리거 생성 방법** (claude.ai Web UI):
1. https://claude.ai/code/triggers 접속
2. "Create trigger" 선택
3. Source repository: `https://github.com/ing-gom/sts2-combat-ai`
4. Model: `claude-sonnet-4-6`
5. Allowed tools: `Bash, Read, Write, Edit, Glob, Grep`
6. Name + Prompt 를 아래 템플릿에서 복사
7. Run once (특정 시각) 또는 manual trigger 로 저장

---

## TODO #1 — 5 conditional-hits cards counter wiring

**Name**: `CombatAI v0.9.2 — counter wiring + AllowsZeroHits promote`

**Prompt**:

```
STS2 CombatAI v0.9.1 follow-up — 5 추가 조건부 hits 카드 counter 배선 + AllowsZeroHits promote.

## 컨텍스트

v0.9.1 (`master` HEAD) 에서 `CardReflection.cs` 의 `CalculatedHits` 처리가 `amount > 0` 가드 제거됨 → 0 도 유효 hit count. `PlanScorer.cs` 의 `_zeroHitsCards` HashSet 은 explicit counter 있는 4개만 등재 (LUNAR_BLAST/FINISHER/BARRAGE/FLECHETTES).

나머지 5개는 같은 `CalculatedHits + CalculationBase=0 + CalculationExtra=1` 패턴인데 counter 미배선이라 status quo (min-1 hit floor) 유지 중:
- FLAK_CANNON (Defect, 2c) — 이번 턴 Ammunition 소멸 수
- HELIX_DRILL (Defect, 0c) — 이번 턴 소비 E
- PULL_FROM_BELOW (Necrobinder, 1c) — 이번 콤뱃 휘발성 사용수
- RADIATE (Regent, 0c) — 이번 턴 사용된 별 수
- RATTLE (Necrobinder, 1c) — 이번 콤뱃 해골(Osty) 공격 수

## 사전 확인

1. `git log --oneline -3` 으로 master HEAD 가 v0.9.1 이상인지 확인. CHANGELOG.md 에 v0.9.1 entry 가 있어야 함.
2. 없으면 stop. 사용자에게 'v0.9.1 push 필요' 보고.

## 작업

### 1. SimState 신규 필드 5개
`Sts2CombatAICode/Core/Sim/SimState.cs` 에 `TurnAttacksPlayed` 패턴 따라:
```csharp
public int TurnEnergySpent { get; init; }
public int TurnStarsSpent { get; init; }
public int TurnAmmunitionExhausted { get; init; }
public int CombatVolatilePlayed { get; init; }
public int CombatSkeletonAttacks { get; init; }
```

### 2. StateSnapshotter 산출
`Sts2CombatAICode/Core/Sim/StateSnapshotter.cs` 의 기존 `CombatHistory.Entries` 단일-walk 루프 (현재 turnAttacksPlayed/turnSkillsPlayed/combatHpLossEvents 계산하는 곳) 에 추가 누적 변수 도입:
- `turnEnergySpent` += `cpe.CardPlay.Card.EnergyCost.GetAmountToSpend()` (당-턴 + 이 플레이어)
- `turnStarsSpent` += 별 cost (Regent 카드의 StarCost var, reflection 으로 읽기 — `CardReflection.SafeStarCost` 활용 가능 여부 확인)
- `turnAmmunitionExhausted` += 1 if 카드가 Ammunition keyword/tag 보유 AND exhaust 됐을 때 (Defect Ammunition 은 `CardKeywords.Ammunition` 또는 비슷한 enum — 게임 source 디컴파일로 확인)
- `combatVolatilePlayed` += 1 if 카드가 Volatile keyword 보유 (round filter 없이 콤뱃 전체)
- `combatSkeletonAttacks` — 별도 entry type. `CreatureAttackFinishedEntry` 혹은 비슷한 ally-attack 이벤트 walk. 또는 `AttackCommandFinishedEntry` 에서 attacker 가 player-side creature (Allies 목록의 SourceRef) 인 경우 count++.

각 reflection 호출은 기존 `try/catch + LogReflectionFailureOnce` 패턴 따를 것. 0 fallback 안전.

### 3. PlanScorer.EstimateVariableHits 추가
```csharp
if (card.Id == "FLAK_CANNON") return state.TurnAmmunitionExhausted;
if (card.Id == "HELIX_DRILL") return state.TurnEnergySpent;
if (card.Id == "PULL_FROM_BELOW") return state.CombatVolatilePlayed;
if (card.Id == "RADIATE") return state.TurnStarsSpent;
if (card.Id == "RATTLE") return state.CombatSkeletonAttacks;
```

### 4. PlanScorer._zeroHitsCards 확장
5개 ID 추가. 해당 doc-comment 의 "deliberately excluded" 단락 갱신.

## 검증

```bash
dotnet build -c Debug --nologo
```
경고 1 (기존 Godot sourcegen 충돌) / 오류 0 만 허용.

## 커밋 & PR

```bash
git checkout -b feat/v0.9.2-counter-wiring
git add Sts2CombatAICode/Core/Sim/SimState.cs Sts2CombatAICode/Core/Sim/StateSnapshotter.cs Sts2CombatAICode/Core/Planner/PlanScorer.cs CHANGELOG.md
git commit -m "feat(v0.9.2): wire 5 conditional-hits card counters

Adds TurnEnergySpent / TurnStarsSpent / TurnAmmunitionExhausted /
CombatVolatilePlayed / CombatSkeletonAttacks to SimState + StateSnapshotter,
then promotes FLAK_CANNON / HELIX_DRILL / PULL_FROM_BELOW / RADIATE / RATTLE
to _zeroHitsCards with explicit EstimateVariableHits overrides.

Closes v0.9.1 TODO #1.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push -u origin feat/v0.9.2-counter-wiring
gh pr create --title "v0.9.2 — counter wiring for 5 conditional-hits cards" --body "..."
```

CHANGELOG 에 v0.9.2 entry 추가. PR URL 최종 보고.

## 주의
- Ammunition / Volatile 키워드 enum 명이 게임마다 다를 수 있음 → `ilspycmd sts2.dll | grep -i ammunit` 으로 확인하거나 SimState 의 기존 keyword 읽기 패턴 (`SovereignBladeCount` 등) 참조.
- 해골 공격 entry type 도 확인 필요. 없으면 conservative proxy: `Allies.Sum(a => a.IntentRepeats) × turnsElapsed` 으로 대체 (turnsElapsed 도 SimState 에 신규 필드 필요할 수 있음).
- 각 카운터 reflection 실패시 0 fallback 안전. 콤뱃 시작에는 모두 0.
```

---

## TODO #2 — CalculatedDamage runtime preview 검증

**Name**: `CombatAI — CalculatedDamage preview verification`

**Prompt**:

```
STS2 CombatAI — CalculatedDamage runtime preview 가 4개 카드에서 올바르게 fire 하는지 검증.

## 컨텍스트

다음 4개 카드는 `CalculatedDamageVar` 를 사용. CalculationBase + ExtraDamage × N 공식으로 damage 가 런타임에 갱신:
- CONFLAGRATION (Ironclad 1c) — base 8 + 2 × 이번턴 다른 공격수
- SQUEEZE (Necrobinder 3c) — base 25 + 5 × 다른 공격수 (skeleton dmg)
- DEATH_MARCH (Necrobinder 1c) — base 8 + 3 × 이번 콤뱃 죽인 카드 수
- CRESCENT_SPEAR (Regent 1c) — base 6 + 2 × 사용된 별 카드

`CardReflection.cs:372` 의 `if (typeName.StartsWith("CalculatedDamageVar"))` 가 `_updatePreview?.Invoke()` 호출 후 PreviewValue 를 읽음. preview closure 가 제대로 fire 하면 damage 자동 갱신. fire 안 하면 base 값만.

## 작업

### 1. 디버그 로그 추가 (임시)
`Sts2CombatAICode/Core/Reflection/CardReflection.cs` 의 GetEffectSummary 에 4개 카드 ID 매칭시 한 줄 로그:
```csharp
if (card.Id?.Entry is "CONFLAGRATION" or "SQUEEZE" or "DEATH_MARCH" or "CRESCENT_SPEAR")
    MainFile.Logger.Info($"[CalcDmgProbe] {card.Id.Entry}: damage={damage}, hasCalcDamage={hasCalcDamage}");
```

### 2. 게임 콤뱃 로그 캡처
- Ironclad 덱에 CONFLAGRATION 포함하고 2-3턴 플레이
- 콤뱃 로그 (`~/AppData/Roaming/SlayTheSpire2/Sts2CombatAI/decision_log/`) + Godot log (`~/AppData/Roaming/SlayTheSpire2/logs/`) 에서 `[CalcDmgProbe]` 항목 검토
- CONFLAGRATION 점수 breakdown 에 표시된 damage 값과 비교

### 3. 결과 평가
- preview 가 동작 (damage 가 AtkT 에 따라 증가) → 추가 조치 불필요. 디버그 로그 제거 후 커밋.
- preview 실패 (damage 가 항상 CalculationBase) → `PlanScorer.EstimateVariableHits` 에 fallback:
  - CONFLAGRATION: card.Damage += `Math.Max(0, state.TurnAttacksPlayed) × 2`  
    (단, 직접 Damage 수정 불가능 → 별도 boost 핸들러 `ApplyConflagrationScaling` 작성)
  - 다른 3개도 유사 패턴. TODO#1 의 새 counter 필요할 수 있음.

### 4. 결과 보고
- 어느 카드가 preview OK / FAIL 인지 표
- FAIL 인 경우 추가한 fallback 핸들러 + breakdown 예시
- 디버그 로그 정리 여부

## 커밋

- preview OK 인 경우: 디버그 로그 제거만, 커밋 불필요 (조사 결과만 보고)
- FAIL 인 경우: `feat/v0.9.x-calcdmg-fallback` 브랜치에 fallback 핸들러 추가, PR 생성

## 주의
- 이 작업은 게임 클라이언트 직접 플레이가 필요 → 헤드리스 실행 불가. 콤뱃 로그 capture 단계까지는 사용자가 진행해야 함. 리모트 에이전트는 디버그 코드 작성 + 로그 분석 + fallback 핸들러 코드 작성까지.
- 로그 파일 경로는 사용자 윈도우 PC: `C:\Users\dev\AppData\Roaming\SlayTheSpire2\Sts2CombatAI\decision_log\` — 리모트 에이전트는 이 경로 접근 불가. 사용자가 로그 발췌해 PR 코멘트로 첨부할 것을 요청.
```

---

## TODO #3 — HandSynergy 확장 (4 powers)

**Name**: `CombatAI — HandSynergy for AfterimagePower / SerpentFormPower / PanachePower / DanseMacabrePower`

**Prompt**:

```
STS2 CombatAI — HandSynergy.Compute 에 4개 카드-사용-당-부가효과 파워 추가.

## 컨텍스트

`Sts2CombatAICode/Core/Planner/HandSynergy.cs` 의 `Compute(string powerName, ...)` switch 는 RagePower, StrengthPower 등 ~10개 파워의 hand-aware scaling 만 처리. 다음 4개 파워는 카드 사용시 매번 trigger 되지만 hand-aware scaling 미정의 → `PowerCatalog` 의 평면 tier 값만 받음.

- **AfterimagePower** (Silent AFTERIMAGE 1c): 카드 사용시 블록 +1 (PowerCatalog: ?)
- **SerpentFormPower** (Silent SERPENT_FORM 3c): 카드 사용시 무작위 적에 4 dmg
- **PanachePower** (Shared PANACHE 0c): 카드 10장당 모든 적 5 dmg
- **DanseMacabrePower** (Necrobinder DANSE_MACABRE 1c): 2+E 카드 사용시 블록 +4

## 작업

`HandSynergy.cs` 의 switch 에 4개 case 추가. 패턴은 기존 `RagePower` 참고:
```csharp
"RagePower" => System.Math.Max(0, remainingAttacks - 1) * amount * RageSynergyPerAttack,
```

추가:
```csharp
// AfterimagePower: 카드 사용시 +N block. 남은 playable 카드 수 × amount × per-block.
"AfterimagePower" => System.Math.Max(0, remainingPlayable - 1) * amount * 30,

// SerpentFormPower: 카드 사용시 random 적 +N dmg. 적 약체 우선 잡힘 → half-credit.
"SerpentFormPower" => System.Math.Max(0, remainingPlayable - 1) * amount * 25,

// PanachePower: 10장당 AOE +N. remainingPlayable / 10 으로 cycle 추정.
"PanachePower" => (remainingPlayable / 10) * amount * aliveEnemies * 50,

// DanseMacabrePower: 2+E 카드 사용시 +N block. expensive cards in hand.
"DanseMacabrePower" => System.Math.Max(0, remainingExpensive - 1) * amount * 30,
```

새 변수:
```csharp
int remainingPlayable = state.Hand.Count(c =>
    !ReferenceEquals(c, self) && c.IsPlayable && !c.IsCurseOrStatus);
int remainingExpensive = state.Hand.Count(c =>
    !ReferenceEquals(c, self) && c.IsPlayable && !c.IsCurseOrStatus && c.Cost >= 2);
int aliveEnemies = state.Enemies.Count(e => e.IsAlive);
```

(remainingAttacks / remainingSelfBlocks 변수 옆에 같은 패턴으로 추가)

## 검증

```bash
dotnet build -c Debug --nologo  # 0 errors
cd ../Sts2CombatAI.Tests && dotnet run -c Release --nologo  # 모든 테스트 pass
```

(만약 테스트 빌드 에러 — TODO#4 의 SmartSelectorLogic.cs 이슈 — 가 막으면 stop, 사용자에게 보고)

## 커밋 & PR

```bash
git checkout -b feat/v0.9.x-handsynergy-4powers
git add Sts2CombatAICode/Core/Planner/HandSynergy.cs CHANGELOG.md
git commit -m "feat: extend HandSynergy with 4 per-card-play powers

Adds AfterimagePower / SerpentFormPower / PanachePower / DanseMacabrePower
scaling cases to HandSynergy.Compute. Each scales with remaining playable
cards (or expensive cards for DanseMacabre) so 'play me first to maximize
future triggers' is correctly priced.

Closes v0.9.1 TODO #3.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push -u origin feat/v0.9.x-handsynergy-4powers
gh pr create --title "feat: HandSynergy +4 per-card-play powers" --body "..."
```

CHANGELOG 항목 추가, PR URL 최종 보고.

## 주의
- 매직 넘버 (30, 25, 50) 는 기존 `RageSynergyPerAttack=30`, `StrengthSynergyPerAttack`, `DexteritySynergyPerSkill` 와 정합 유지 — 같은 시스템에서 같은 자릿수가 나오도록.
- depth-2 lookahead double-count 보정 (`-1` clamp) 기존 패턴 그대로 적용.
```

---

## TODO #4 — Sts2CombatAI.Tests 빌드 회복

**Name**: `CombatAI — fix Sts2CombatAI.Tests SmartSelectorLogic build error`

**Prompt**:

```
STS2 CombatAI.Tests — 사전 빌드 에러 fix.

## 컨텍스트

`Sts2CombatAI.Tests/` 에서 `dotnet run -c Release` 실행시:
```
Sts2CombatAICode/Core/Planner/SmartSelectorLogic.cs(70,16): error CS0103: 'StateSnapshotter' 이름이 현재 컨텍스트에 없습니다.
```

이 에러로 테스트 스위트 전체가 빌드 실패 → v0.9.1 (그리고 이후 작업) 회귀 검사 불가능.

main 모드 빌드 (`Sts2CombatAI.csproj`) 는 정상. 테스트 프로젝트만 namespace resolution 실패.

## 작업

1. `Sts2CombatAICode/Core/Planner/SmartSelectorLogic.cs` line 70 컨텍스트 확인
2. 같은 폴더 다른 파일 (PlanScorer.cs, EffectSynergy.cs 등) 의 namespace 선언 / using 비교
3. fix 방법 (가장 확률 높은 순):
   - `using Sts2CombatAI.Sim;` 추가 (가장 가능성 높음)
   - 또는 `Sts2CombatAI.Sim.StateSnapshotter.X` 로 fully qualify
   - Tests 프로젝트의 csproj 에 missing reference 추가 (덜 가능성 있음)
4. 빌드 검증:
   ```bash
   cd Sts2CombatAI.Tests && dotnet build -c Release --nologo
   cd Sts2CombatAI.Tests && dotnet run -c Release --nologo
   ```
   결과: 모든 테스트 pass

## 커밋

```bash
git checkout -b chore/fix-test-build
git add Sts2CombatAICode/Core/Planner/SmartSelectorLogic.cs
# (만약 csproj 수정이라면 그것도 add)
git commit -m "chore: fix Sts2CombatAI.Tests build error

SmartSelectorLogic.cs referenced StateSnapshotter without importing the
Sts2CombatAI.Sim namespace. Main mod builds fine because csproj's global
usings include it; tests project doesn't share the globals.

Closes v0.9.1 TODO #4.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push -u origin chore/fix-test-build
gh pr create --title "chore: fix Sts2CombatAI.Tests build" --body "..."
```

## 주의
- 만약 line 70 컨텍스트가 다른 원인 (예: 신규로 추가된 호출인데 namespace 가 다른 곳) 이면 root cause 파악 후 적절한 fix 선택.
- Tests 빌드 회복 후 추가로 모든 테스트 pass 확인. 기존 fail 케이스가 있다면 별도 issue 로 보고 (이 PR scope 아님).
```

---

## TODO #5 — ECHOING_SLASH chain repeat 확장

**Name**: `CombatAI — ECHOING_SLASH multi-kill chain valuation`

**Prompt**:

```
STS2 CombatAI — ECHOING_SLASH 의 chain repeat 모델 확장.

## 컨텍스트

`EffectSynergy.cs` 의 `ApplyEchoingSlashOverkillBonus` (v0.9.1 도입) 는 현재 첫 repeat 만 half-credit 으로 추가. ECHOING_SLASH 는 적 처치할 때마다 효과 반복 → AOE 보드 정리시 여러 chain 가능하지만 현재 모델은 1회만 카운트.

카드:
- ECHOING_SLASH (Silent 1c AOE): "모든 적에게 피해를 10 줍니다. 적을 처치할 때마다, 이 효과를 반복합니다."

## 작업

`Sts2CombatAICode/Core/Planner/EffectSynergy.cs` 의 `ApplyEchoingSlashOverkillBonus` 를 chain-aware 로 확장.

알고리즘:
1. perHit damage 계산 (기존 코드 재사용 — Strength, Weak, Vulnerable, DamageCap 적용)
2. 모든 살아있는 적을 effective HP (Hp + Block) 오름차순 정렬
3. 누적 카운트 chains = 1 (첫 AOE)
4. 각 적 순회: dmg ≥ effHp → kill 가능. chains++ (단 cap=3)
   - chain 이후 적은 누적 dmg 가 같은 perHit 라고 가정 (보수적 — 실제는 chain 마다 새 AOE 라 모든 적 다시 가능하지만, 한 번 죽인 적은 다시 안 죽음)
5. 보너스 = (chains - 1) × self.Damage × EffectScoringWeights.DamageInHand / 2
   - "-1" 은 첫 hit 은 base scorer 가 이미 credit 했으므로 제외

```csharp
private static void ApplyEchoingSlashOverkillBonus(SimCard self, int targetIdx, SimState state, ref int b, List<string> parts)
{
    if (self.Damage <= 0) return;
    int perHit = self.Damage + System.Math.Max(0, state.PlayerStrength);
    if (state.PlayerWeak > 0) perHit = (int)(perHit * 0.75);

    var sortedEnemies = state.Enemies
        .Where(e => e.IsAlive)
        .OrderBy(e => e.Hp + e.Block)
        .ToList();

    int chains = 1;
    const int MaxChains = 3;
    foreach (var e in sortedEnemies)
    {
        int dmg = perHit;
        if (e.VulnerableAmount > 0) dmg = (int)(dmg * StatusMath.VulnerableMult);
        if (e.DamageCapPerHit > 0 && dmg > e.DamageCapPerHit) dmg = e.DamageCapPerHit;
        if (dmg >= e.Hp + e.Block) chains++;
        if (chains >= MaxChains) break;
    }
    if (chains <= 1) return;

    int extraChains = chains - 1;
    int v = extraChains * self.Damage * EffectScoringWeights.DamageInHand / 2;
    b += v;
    parts.Add($"echoingChain(chains={chains},+{extraChains}x{self.Damage}x35/2)=+{v}");
}
```

## 검증

```bash
dotnet build -c Debug --nologo  # 0 errors
cd ../Sts2CombatAI.Tests && dotnet run -c Release --nologo  # 모든 테스트 pass
```

## 커밋 & PR

```bash
git checkout -b feat/v0.9.x-echoing-chain
git add Sts2CombatAICode/Core/Planner/EffectSynergy.cs CHANGELOG.md
git commit -m "feat: ECHOING_SLASH multi-kill chain valuation

Extends ApplyEchoingSlashOverkillBonus from single-repeat half-credit to
chain-aware (up to 3 cumulative kills). For multi-enemy boards where the
first AOE wave triggers cascading kills, the bonus now scales with the
number of likely chains rather than capping at one.

Closes v0.9.1 TODO #5.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git push -u origin feat/v0.9.x-echoing-chain
gh pr create --title "feat: ECHOING_SLASH chain repeat" --body "..."
```

## 주의
- chain cap 3 은 보수적. 4 이상 chain 은 극히 드물고, over-credit 위험 vs under-credit 사이의 tradeoff.
- `StatusMath.VulnerableMult` import 필요할 수 있음 (기존 핸들러에서 이미 사용 중이라 OK).
- 테스트 빌드는 TODO#4 가 먼저 해결돼야 검증 완전.
```

---

## 권장 실행 순서

1. **TODO #4** (테스트 빌드 회복) — 가장 짧음, 다른 작업의 회귀 검증 기반 제공
2. **TODO #1** (counter wiring) — 5개 카드 동시 처리, v0.9.2 minor bump
3. **TODO #3** (HandSynergy 확장) — 독립적
4. **TODO #5** (ECHOING_SLASH chain) — 작음, 독립적
5. **TODO #2** (CalculatedDamage 검증) — 마지막, 사용자의 게임 플레이 필요

TODO #1, #3, #5 는 병렬 실행 가능 (서로 다른 파일 / 다른 영역). TODO #4 결과는 #1/#3/#5 모두에서 테스트 검증에 영향.
