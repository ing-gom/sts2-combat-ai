"""Walk every Skill card and show which evaluation paths fire for it.

Skill cards (232 playable) go through multiple scoring layers:
  1. PlanScorer Skill branch       -- base block / cost
  2. EffectSynergy axis dispatch   -- 32 axes (BLOCK_AMPLIFIER, DRAW_CONDITIONAL,
                                     HP_LOSS_CONSUMER, STRENGTH_DOWN, HEAL, etc.)
  3. EffectSynergy card-id dispatch -- 30+ specific cards
  4. BuildSynergy                  -- pair-axis stems + primary build
  5. AmplifierSynergy              -- POWER_AMPLIFIER / REPLAY axes
  6. HandSynergy                   -- vars-based power propagation

This audit lists each Skill with the path mix that fires. Used to verify
that no Skill category (block / draw / debuff / buff / energy / heal /
utility / card-gen / cost-enabler) has a systemic gap.
"""
from __future__ import annotations
import json
import re
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
CATALOG = REPO_ROOT / "scripts" / "cards_catalog.json"
EFFECT_SYNERGY_CS = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "EffectSynergy.cs"


def parse_dispatch_paths() -> tuple[set[str], set[str]]:
    text = EFFECT_SYNERGY_CS.read_text(encoding="utf-8")
    axes = set(re.findall(r'axes\.Contains\("([A-Z_]+)"\)', text))
    ids = set(re.findall(r'card\.Id == "(CARD\.[A-Z_]+)"', text))
    return axes, ids


def categorize(card: dict) -> str:
    """Bucket each Skill by its dominant mechanic."""
    axes = set(card.get("axes", []))
    vars_ = card.get("vars") or {}
    cid = card["id"]
    block = card.get("block", -1)

    # 1) Card-id specific (handled by ApplyXXX in EffectSynergy)
    special = {"CARD.WISH", "CARD.ENLIGHTENMENT", "CARD.MAKE_IT_SO",
               "CARD.NIGHTMARE", "CARD.DUAL_WIELD", "CARD.OUTMANEUVER",
               "CARD.HEADBUTT"}
    if cid in special:
        return "special-handler"

    # 2) Cost-enablers
    if axes & {"ATTACK_COST_ENABLER", "SKILL_COST_ENABLER", "POWER_COST_ENABLER"}:
        return "cost-enabler"

    # 3) Card-gen / search
    if axes & {"CARD_GEN", "DRAW_PILE_SEARCH"}:
        return "card-gen/search"
    if axes & {"CARD_RETURN"}:
        return "card-return"

    # 4) Healing / sustain
    if axes & {"HEAL"} or "Heal" in vars_:
        return "heal/sustain"

    # 5) Energy gain
    if "Energy" in vars_ or "ENERGY_PRODUCER" in axes:
        return "energy-gain"

    # 6) Block-amplifier / Block-payoff specific
    if "BLOCK_AMPLIFIER" in axes or "BLOCK_PAYOFF" in axes:
        return "block-amp/payoff"

    # 7) Strength/Dex amplifier
    if axes & {"DAMAGE_AMPLIFIER", "VULN_AMPLIFIER", "WEAK_AMPLIFIER"}:
        return "damage/vuln/weak amp"

    # 8) Self-buff via PowerVar (HandSynergy)
    if any(k in {"StrengthPower", "DexterityPower", "FocusPower"} for k in vars_):
        return "self-buff (Str/Dex/Focus)"

    # 9) Debuff to enemy
    if axes & {"VULN", "WEAK", "FRAIL", "DEBUFF"} or any(k in vars_ for k in (
            "VulnerablePower", "WeakPower", "FrailPower")):
        return "debuff (Vuln/Weak/Frail)"
    if axes & {"STRENGTH_DOWN"} or "StrengthLoss" in vars_:
        return "debuff (Strength Down)"

    # 10) Status to hand (negative)
    if "STATUS_TO_HAND" in axes:
        return "self-pollute"
    if "STATUS_CONSUMER" in axes:
        return "status-consumer"

    # 11) Draw
    if axes & {"DRAW", "DRAW_CONDITIONAL", "DRAW_ON_DRAW", "DRAW_AMPLIFIER"}:
        return "draw"

    # 12) HP-loss
    if axes & {"HP_LOSS", "HP_LOSS_CONSUMER", "HP_LOSS_PREVENT"}:
        return "hp-loss"

    # 13) Build-axis (pair) — generic build participation
    if any(a.endswith(("_PRODUCER", "_AMPLIFIER", "_CONSUMER")) for a in axes):
        return "build-axis"

    # 14) Pure block
    if block > 0:
        return "pure-block"

    # 15) Other
    return "other"


def main() -> None:
    cat = json.loads(CATALOG.read_text(encoding="utf-8"))
    eff_axes, eff_ids = parse_dispatch_paths()
    skills = [c for c in cat["cards"]
              if not c.get("is_upgraded") and c.get("type") == "Skill"]

    by_cat = defaultdict(list)
    for c in skills:
        by_cat[categorize(c)].append(c)

    print(f"Total playable Skill cards: {len(skills)}\n")
    print(f"{'category':<28}  {'count':>5}  {'%':>5}")
    print("-" * 50)
    for cat_name, items in sorted(by_cat.items(), key=lambda x: -len(x[1])):
        n = len(items)
        print(f"{cat_name:<28}  {n:>5}  {n/len(skills)*100:>4.1f}%")

    # For each category, print up to 5 example cards with their axes
    print()
    print("=== Category samples (up to 5 per category) ===\n")
    for cat_name, items in sorted(by_cat.items(), key=lambda x: -len(x[1])):
        items.sort(key=lambda c: ("SABCD?".index(c.get("tier", "?"))
                                  if c.get("tier") in "SABCD?" else 9, c["id"]))
        print(f"## {cat_name} ({len(items)})")
        for c in items[:5]:
            ax = ",".join(a for a in c.get("axes", []) if len(a) < 25)
            print(f"  {c.get('tier','?')} {c['character']:<11} {c['id']:<24} c={c['cost']} blk={c['block']} | axes=[{ax[:90]}]")
        if len(items) > 5:
            print(f"  ... and {len(items) - 5} more")
        print()


if __name__ == "__main__":
    main()
