# Sts2CombatAI

**Slay the Spire 2 의 전투 의사결정을 자동화하는 범용 AI 코어.**
카드/타겟 시퀀스를 평가해 예상 HP 손실을 최소화하는 planner + simulator + scorer 묶음.

코어 자체는 trigger-agnostic — 어떤 *모드* 가 어느 시점에 호출하든 동일한 판단 로직을 돌린다.
현재 제공되는 모드는 한 가지: **Vakuu** (Whispering Earring 의 vanilla auto-play 를 코어 AI 로 교체).

## 아키텍처

```
Sts2CombatAICode/
├── MainFile.cs                — Harmony entrypoint, mode wiring
├── Core/                      — 모드와 무관한 의사결정 엔진
│   ├── Planner/               (ActionPlanner, PlanScorer, Playstyle, ...)
│   ├── Sim/                   (SimState, AnalyticalSimulator, StateSnapshotter, ...)
│   ├── Reflection/            (CardReflection, CombatReflection, ...)
│   ├── Data/                  (CardCatalog + embedded card_triggers.json)
│   ├── Diagnostics/           (DecisionLog ring buffer)
│   └── Runtime/               (PlaystylePersistence)
└── Modes/
    └── Vakuu/                 — Vakuu mode: Core 를 호출하는 runtime driver
        ├── WhisperingEarringPlannerPatch.cs
        ├── VakuuExecutor.cs
        ├── VakuuCardSelectorPatches.cs
        ├── VakuuTestButtonPatch.cs
        └── TestButtonPoller.cs
```

새 모드를 추가하려면: `Modes/<NewMode>/` 아래에 trigger Harmony patch + executor 를 만들고
`Core` 의 `ActionPlanner.PlanNextStep(snapshot)` 를 호출하면 끝. Core 는 건드릴 필요 없음.

## Core — 의사결정 엔진

### 카드 인식 (모든 캐릭터 100% coverage)
- **576 카드** catalog 자동 read (`Core/Data/card_triggers.json` embedded)
- **14 빌드** 자동 분류 (독 / 광역 / 성장 / 소멸 / 골골이 / 별 / ...)
- **17 enemy intent** 분류 (Attack/Buff/Heal/Summon/DeathBlow/Defend/Debuff/Stun/...)
- **65+ Power priority catalog** (EchoForm/Barricade/Strength/Vulnerable/Poison ...)
- 카드 ID 하드코딩 **0** — 게임 패치 시 catalog 만 재추출

### 의사결정 영역
- **카드 효과 정확값**: Damage / Block / Hits / PowerApps via DynamicVars + PreviewValue (multiplier-aware)
- **Status modifier**: (base + Strength) × Vulnerable × Weak / (base + Dex) × Frail
- **적 상태 인식**: Vulnerable/Strength/Frail/Artifact/Ritual/Poison stack → target priority 차등
- **에너지 낭비 회피**: damage ≤ target.Block → penalty
- **Energy gain 카드**: 부족할 때만 우선 (Adrenaline 콤보 인식)
- **Draw 카드**: hand 의 best score 가 낮을 때 (수혈 가치)
- **Build synergy**: Producer + Amplifier/Consumer 페어 + 같은 build 카드 갯수
- **Defect orb**: 슬롯 채워짐 / 비어있음에 따라 Producer/Consumer 차등
- **Forward simulator**: 카드 plays 시뮬레이션 (EnergyGain / DrawCount / Damage / Block / Power 적용)
- **Depth-2 lookahead**: 첫 카드 후 best second card 시뮬레이션해서 평가

### 4가지 Playstyle (영구 저장)
End Turn 버튼 옆 **Style** 버튼으로 cycle:
- **Defensive** — block 1500, 위협 임계 0.2, attack 약화
- **Balanced** — default
- **Aggressive** — attack +350, block 약화, threshold 0.55
- **Killer** — block 0, lethal range 6000, attack 압도

선택한 Style 은 `{user_data}/Sts2CombatAI/playstyle.json` 에 자동 저장 → 게임 재시작 후 유지.

## Mode: Vakuu

Whispering Earring (Ancient 유물) 의 효과 — *"Vakuu plays your first turn for you"*. 그러나 [decompile](../research/baku_decompile/WhisperingEarring.cs) 으로 확인한 vanilla 동작:

```csharp
CardModel card = pile.Cards.FirstOrDefault(c => c.CanPlay());
```

→ **그냥 hand 의 첫 playable 카드** 를 13장까지 plays. 전략 0.

이 모드는 그 hook (`WhisperingEarring.BeforePlayPhaseStartLate`) 을 가로채 Core AI 로 위임한다.
구성:
- `WhisperingEarringPlannerPatch` — 게임의 Vakuu 발동을 Harmony Prefix 로 가로채서 `VakuuExecutor` 로 위임
- `VakuuExecutor` — 13-step loop: snapshot → Core planner → AutoPlay → 반복
- `VakuuCardSelectorPatches` — Vakuu 의 mid-play card prompt (discard/exhaust/upgrade) 를 Core scorer 로 응답
- `VakuuTestButtonPatch` + `TestButtonPoller` — End Turn 옆 **Vakuu Play** 디버그 버튼 (relic 없이 매 턴 호출)

## Installation

```
SlayTheSpire2/mods/Sts2CombatAI/
├── Sts2CombatAI.dll
└── Sts2CombatAI.json
```

게임 내 Mods 메뉴에서 enabled 확인.

## Logs

게임 로그 위치: `%APPDATA%\Godot\app_userdata\SlayTheSpire2\logs\` 최신 `.log`

`[CombatAI]` prefix 라인이 매 step 의 결정 + score breakdown 출력:

```
[CombatAI] starting plan (style=Balanced)
[CombatAI] step 1 snapshot: player[hp=80 block=0 energy=3] 
  hand=[Strike(A1/d6),Inflame(P1/Stre:2),...] enemies=[Acolyte(...)]
[CombatAI] step 1 → CARD.INFLAME@self (score=2207 reason=power(StrengthPower:2))
[CombatAI]   breakdown: Power base=1007 effect=1200 target=0 threat=0
              [powerBase=1000,Stre(2)=1200,buildSyn=160,energyCtx=200]
[CombatAI] turn complete, 3 cards played, took 24ms total
```

## 빌드 (개발자)

```bash
dotnet build
```

자동으로 `{STS2 install}/mods/Sts2CombatAI/` 에 dll + json 복사.

## 테스트 실행

```bash
cd Sts2CombatAI.Tests
dotnet run
```

70 unit tests — 모든 의사결정 룰 회귀 방지 검증.

## Catalog 갱신 (게임 패치 후)

```bash
# 1. Sts2CardAdvisor 의 headless-sync 로 cards_catalog.json 재생성
# 2. 우리 mod 용 작은 catalog 추출
python scripts/extract_card_triggers.py
# 3. mod 재빌드
dotnet build
```

## 실행 흐름 (Vakuu mode 기준)

```
Whispering Earring 또는 Vakuu Play 버튼 trigger
       ↓
Modes/Vakuu/VakuuExecutor.RunPlannedTurn  ← 매 step 13회 loop
       ↓
Core/Sim/StateSnapshotter.Capture (Live → SimState)
       ├─ Player HP/Block/Energy/Strength/Dex/Stars
       ├─ Hand (cards via CardReflection.GetEffectSummary — DynamicVars + PreviewValue)
       ├─ Enemies (HP/Block/Vulnerable/Strength/Weak/Frail/Artifact/Poison/...)
       ├─ Pile sizes (Draw/Discard)
       └─ Orb slots (Defect 만)
       ↓
Core/Planner/ActionPlanner.PlanNextStep (depth-2 lookahead)
       ├─ EnumerateCandidates (CanPlay + 에너지 budget)
       ├─ for each candidate: PlanScorer.Score
       ├─ AnalyticalSimulator.ApplyCardPlay → next state
       └─ best second card → first + second total
       ↓
Core/Planner/PlanScorer (145+ 룰)
       ├─ Card type baseline
       ├─ PowerCatalog (65+ self/enemy split + stack curve)
       ├─ Modifier-aware damage (Strength × Vulnerable × Weak)
       ├─ Target priority (Boss/Minion/Lethal/Intent/Buff state)
       ├─ Build synergy (Producer + Amplifier/Consumer)
       ├─ Hand synergy + Card override catalog
       ├─ Waste avoidance + Energy/Draw/Power context
       └─ Smart selector mode (Burn/Boost)
       ↓
CardCmd.AutoPlay (game-engine 실제 plays)
       ↓
Core/Diagnostics/DecisionLog.Record (ring buffer, 32 entries)
```

## License

MIT
