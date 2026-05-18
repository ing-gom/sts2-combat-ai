"""Drill-down audit -- zero-synergy cards + orphan stems.

The coverage report shows 116 cards with zero synergy participation and
13 orphan stems. This drill-down lists them by tier / character so we
can decide whether each gap is meaningful (real coverage hole) or
expected (standalone utility cards that don't fit synergy patterns).
"""
from __future__ import annotations
import json
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
CATALOG = REPO_ROOT / "scripts" / "cards_catalog.json"
TRIGGERS = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"

PAIR_AXIS_SUFFIXES = ("_PRODUCER", "_AMPLIFIER", "_CONSUMER")
AMP_AXES = {"POWER_AMPLIFIER", "REPLAY", "ATTACK_REPLAY", "SKILL_REPLAY",
            "ATTACK_REPLAY_RANDOM"}
EFFECT_AXES = {"DAMAGE_AMPLIFIER", "BLOCK_AMPLIFIER", "VULN_AMPLIFIER",
               "WEAK_AMPLIFIER", "BLOCK_PAYOFF", "HP_LOSS_CONSUMER"}
HAND_SYN_VARS = {"StrengthPower", "DexterityPower", "VulnerablePower",
                 "WeakPower", "FocusPower"}


def synergy_degree(card: dict) -> int:
    axes = set(card.get("axes", []))
    builds = card.get("builds", [])
    has_pair = any(any(ax.endswith(suf) for suf in PAIR_AXIS_SUFFIXES) for ax in axes)
    has_primary = any(b.get("role") == "primary" for b in builds)
    has_amp = bool(axes & AMP_AXES)
    has_effect = bool(axes & EFFECT_AXES)
    has_hand_syn = any(k in HAND_SYN_VARS for k in card.get("vars", {}).keys())
    return sum([has_pair, has_primary, has_amp, has_effect, has_hand_syn])


def main() -> None:
    cat = json.loads(CATALOG.read_text(encoding="utf-8"))
    cards = [c for c in cat["cards"] if not c.get("is_upgraded")]

    zero_syn = []
    for c in cards:
        if synergy_degree(c) == 0:
            zero_syn.append(c)

    print(f"Zero-synergy cards: {len(zero_syn)} / {len(cards)} ({len(zero_syn)/len(cards):.1%})")
    print()

    # Group by tier
    by_tier = defaultdict(list)
    for c in zero_syn:
        by_tier[c.get("tier", "?")].append(c)

    print(f"{'tier':<5} {'count':<6}")
    for tier in "SABCD?":
        print(f"{tier:<5} {len(by_tier.get(tier, [])):<6}")
    print()

    # S and A tier zero-synergy = the real audit targets
    print("=== S-tier zero-synergy ===")
    for c in sorted(by_tier.get("S", []), key=lambda x: x["id"]):
        print(f"  {c['character']:<11} {c['id']:<28} type={c['type']:<6} axes={c.get('axes', [])}")
    print()
    print("=== A-tier zero-synergy ===")
    for c in sorted(by_tier.get("A", []), key=lambda x: x["id"]):
        print(f"  {c['character']:<11} {c['id']:<28} type={c['type']:<6} axes={c.get('axes', [])}")
    print()
    print(f"=== B-tier zero-synergy ({len(by_tier.get('B', []))}) ===")
    for c in sorted(by_tier.get("B", []), key=lambda x: x["id"])[:15]:
        print(f"  {c['character']:<11} {c['id']:<28} axes={c.get('axes', [])}")
    if len(by_tier.get("B", [])) > 15:
        print(f"  ... and {len(by_tier['B']) - 15} more")

    # Truly dropped
    trig = json.loads(TRIGGERS.read_text(encoding="utf-8"))["cards"]
    dropped = []
    for c in cards:
        t = trig.get(c["id"])
        if t is None:
            dropped.append(c)
            continue
        ax = t.get("axes", [])
        bd = t.get("builds", [])
        kw = c.get("keywords", [])
        upg = t.get("upgrade_trigger", False)
        fch = t.get("fetch_trigger", False)
        if not ax and not bd and not kw and not upg and not fch:
            dropped.append(c)

    print()
    print(f"=== Truly dropped (no axes/builds/keywords/trigger): {len(dropped)} ===")
    for c in dropped:
        print(f"  {c.get('tier', '?'):<3} {c['character']:<11} {c['id']:<28} type={c['type']}")
        print(f"       desc: {c['description'][:80]}")


if __name__ == "__main__":
    main()
