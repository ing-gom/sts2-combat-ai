# Changelog

## v0.5.0 (2026-05-16)

**Power-card sequencing tier — 같은 손 안 power 카드 간 우선순위.**

PowerCatalog 가 답하는 "이 버프는 fight 전체에서 얼마나 가치 있나" 와는 직교한 새 layer.
같은 손에 여러 power 카드가 있을 때 어느 것을 먼저 plays 할지 결정.

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
