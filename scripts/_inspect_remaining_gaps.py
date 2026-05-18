"""v0.6.8 이후 남은 미지원 영역 분류 — 패턴 별 카드 식별 + 패치 가능성 평가.

각 패턴마다:
  - 매칭 카드 수 / tier 분포
  - 추정 방식 (정적 가능 / forward-sim 필요)
  - SimState 기존 노출 충분 여부
"""
import json
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
TRIGGERS = ROOT / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"
CATALOG  = ROOT.parent / "scripts" / "cards_catalog.json"

triggers = json.loads(TRIGGERS.read_text(encoding="utf-8"))["cards"]
cat_by_id = {}
for c in json.loads(CATALOG.read_text(encoding="utf-8"))["cards"]:
    if isinstance(c, dict) and not c.get("is_upgraded") and c.get("id"):
        cat_by_id[c["id"]] = c

def card_info(cid):
    c = cat_by_id.get(cid, {})
    return f"{cid:<32}[{c.get('character','?'):<11}{c.get('type','?'):<7}{c.get('tier','?'):<2}]"

def gap_section(name, axes_or_ids, kind="any", description="", solvability=""):
    if isinstance(axes_or_ids, list):
        cards = []
        for ax in axes_or_ids:
            for cid, t in triggers.items():
                if ax in t.get("axes", []) and cid not in [c[0] for c in cards]:
                    cards.append((cid, t))
        cards = sorted(set([(cid, tuple(sorted(t.get("axes",[])))) for cid, t in cards]))
    else:
        cards = [(cid, tuple(sorted(triggers.get(cid,{}).get("axes",[])))) for cid in axes_or_ids if cid in triggers]
    if not cards: return

    # Tier filter — only show non-curse/status
    filtered = []
    for cid, axes in cards:
        c = cat_by_id.get(cid, {})
        if c.get("type") in ("Curse","Status"): continue
        if kind != "any" and c.get("type") != kind: continue
        tier = c.get("tier","?")
        if tier in "SABCD?":
            filtered.append((cid, c, tier))
    if not filtered: return

    tier_order = "SABCD?"
    filtered.sort(key=lambda x: (tier_order.index(x[2]) if x[2] in tier_order else 9, x[0]))
    sa_count = sum(1 for _,_,t in filtered if t in "SA")
    print(f"\n=== [{name}] — {len(filtered)} cards ({sa_count} S/A) ===")
    if description: print(f"  설명: {description}")
    if solvability: print(f"  해결: {solvability}")
    for cid, c, tier in filtered[:8]:
        desc = c.get("description","").replace("\n"," / ")[:80]
        print(f"  {card_info(cid)} {desc}")
    if len(filtered) > 8: print(f"  ... +{len(filtered)-8}")

print("="*100)
print("v0.6.8 이후 남은 미지원 패턴 분류")
print("="*100)

# === 1. STATUS_TO_HAND — adds curse/status to player's hand/discard ===
gap_section("STATUS_TO_HAND (status 페널티 미반영)",
    ["STATUS_TO_HAND"],
    description="공격 후 잔해/슬라임 등 status 카드를 손/덱에 추가. 미래 hand 오염 페널티 미적용.",
    solvability="EASY — 카드별 -150 ~ -300 정적 페널티 추가.")

# === 2. SKILL_CONDITIONAL / ATTACK_CONDITIONAL — turn-played count dependent ===
print("\n--- 2. CONDITIONAL — turn 카운터 의존 ---")
# Already partially covered: STOMP (cost-discount auto), PINPOINT (cost-discount auto)
# Remaining: ones whose DAMAGE or effect scales (not cost)
for ax in ["SKILL_CONDITIONAL", "ATTACK_CONDITIONAL"]:
    cards = [(cid, t.get("axes",[])) for cid, t in triggers.items() if ax in t.get("axes", [])]
    if not cards: continue
    print(f"\n[{ax}]")
    for cid, axes in cards:
        c = cat_by_id.get(cid, {})
        if c.get("type") in ("Curse","Status"): continue
        desc = c.get("description","").replace("\n"," / ")[:80]
        print(f"  {card_info(cid)} {desc}")

# === 3. CARD_RETURN — return-to-hand mechanics ===
gap_section("CARD_RETURN (회수 메커니즘)",
    ["CARD_RETURN"],
    description="discard/exhaust 에서 카드를 손으로 회수. 다음 턴 가치 회복.",
    solvability="MEDIUM — 평균 카드 가치 추정 후 가산. SimState.DiscardPile 활용 가능.")

# === 4. FOCUS-applying cards ===
gap_section("FOCUS — temp Focus this turn",
    ["FOCUS"],
    description="DEFECT 일시 Focus 적용 (orb damage 증가). FocusPower HandSynergy 처리 없음.",
    solvability="EASY — HandSynergy 에 FocusPower 케이스 추가 (orb 카드 수 비례).")

# === 5. SCALING attack — permanent +damage stacks ===
gap_section("SCALING (영구 데미지 증가)",
    ["SCALING"],
    description="이번 전투/run 동안 다른 카드 데미지 영구 증가 (MAUL, RIGHTEOUS_FURY 등).",
    solvability="HARD — multi-combat 가치 모델링 필요. 단일 전투 평가에선 약한 시그널만.")

# === 6. DRAW_CONDITIONAL ===
gap_section("DRAW_CONDITIONAL — 조건부 드로우",
    ["DRAW_CONDITIONAL"],
    description="특정 조건 시에만 카드 드로우 (예: '이번 턴 카드 < 3장 사용').",
    solvability="MEDIUM — TurnAttacks/Skills 카운터 활용해 조건 평가.")

# === 7. CARD_GEN — random card generation ===
gap_section("CARD_GEN (무작위 카드 생성)",
    ["CARD_GEN"],
    description="무작위 카드를 손에 추가. 생성 카드의 가치 예측 불가.",
    solvability="HARD — 무작위 가치 평균 추정만 가능.")

# === 8. DRAW_PILE_SEARCH ===
gap_section("DRAW_PILE_SEARCH",
    ["DRAW_PILE_SEARCH"],
    description="덱에서 특정 카드 검색해 손/discard 로.",
    solvability="MEDIUM — DrawPile.Count 활용해 최적 카드 추정.")

# === 9. STATUS_CONSUMER — consumes status cards in hand ===
gap_section("STATUS_CONSUMER (status 페이오프)",
    ["STATUS_CONSUMER"],
    description="손의 status 카드를 활용 (ROCKET_PUNCH).",
    solvability="EASY — Hand 의 status 카드 수 카운트.")

# === 10. POWER_COST_ENABLER / ATTACK_COST_ENABLER / SKILL_COST_ENABLER ===
print("\n--- 10. COST_ENABLER — 다른 카드 비용 무료화 ---")
for ax in ["POWER_COST_ENABLER","ATTACK_COST_ENABLER","SKILL_COST_ENABLER","COST_ENABLER_ANY"]:
    cards = [(cid, t.get("axes",[])) for cid, t in triggers.items() if ax in t.get("axes", [])]
    if not cards: continue
    print(f"\n[{ax}]")
    for cid, axes in cards:
        c = cat_by_id.get(cid, {})
        if c.get("type") in ("Curse","Status"): continue
        desc = c.get("description","").replace("\n"," / ")[:80]
        print(f"  {card_info(cid)} {desc}")

# === 11. SELF_HP_GAIN — gain MaxHp permanently ===
print("\n--- 11. SELF_HP_GAIN / MaxHp 영구 증가 ---")
matches = [(cid, c) for cid, c in cat_by_id.items() if "MaxHp" in (c.get("vars") or {})]
for cid, c in matches:
    if c.get("type") in ("Curse","Status"): continue
    desc = c.get("description","").replace("\n"," / ")[:80]
    print(f"  {card_info(cid)} MaxHp:{c['vars']['MaxHp']} — {desc}")

# === 12. ABSENT_CONDITIONAL — playable only when condition ===
gap_section("ABSENT_CONDITIONAL — pile 빈 조건",
    ["ABSENT_CONDITIONAL"],
    description="덱/손 비어있을 때만 사용 가능 (GRAND_FINALE 60 AOE).",
    solvability="EASY — CanPlay() 가 게임 본체에서 처리 → 이미 IsPlayable 로 거름.")

# === 13. EXHAUST_TARGET_FORCED / FILTERED / RANDOM — exhaust other cards ===
print("\n--- 13. EXHAUST_TARGET (다른 카드 강제 소멸) ---")
for ax in ["EXHAUST_TARGET_FORCED","EXHAUST_TARGET_FILTERED","EXHAUST_TARGET_RANDOM"]:
    cards = [(cid, t.get("axes",[])) for cid, t in triggers.items() if ax in t.get("axes", [])]
    if not cards: continue
    print(f"\n[{ax}]")
    for cid, axes in cards[:5]:
        c = cat_by_id.get(cid, {})
        if c.get("type") in ("Curse","Status"): continue
        desc = c.get("description","").replace("\n"," / ")[:80]
        print(f"  {card_info(cid)} {desc}")
