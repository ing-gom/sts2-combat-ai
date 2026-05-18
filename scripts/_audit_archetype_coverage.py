"""Multi-dimensional coverage audit -- archetype / character / mechanism.

The card-by-card audits (_audit_full_coverage.py + ALL_FOR_ONE handlers etc.)
verified per-card evaluation paths. This audit checks coverage along
*orthogonal* dimensions:

1. Per-build (archetype) -- 14 build tags in the catalog
2. Per-character (5 characters + SHARED)
3. Per-card-type (Attack / Skill / Power / Status / Curse / Quest)
4. Per-mechanism (DAMAGE / BLOCK / DRAW / DEBUFF / SCALING / etc.)

Goal: verify no archetype / character / mechanism has a systemic gap.
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


PAIR_SUFFIXES = ("_PRODUCER", "_AMPLIFIER", "_CONSUMER")
AMP_AXES = {"POWER_AMPLIFIER", "REPLAY", "ATTACK_REPLAY", "SKILL_REPLAY",
            "ATTACK_REPLAY_RANDOM"}


def has_explicit_handling(card: dict, eff_axes: set[str], eff_ids: set[str]) -> bool:
    """Card has explicit mechanic-aware scoring (not just direct-stat)."""
    axes = set(card.get("axes", []))
    if card.get("type") == "Power":
        return True  # PowerCatalog 100% cover
    if any(a.endswith(PAIR_SUFFIXES) for a in axes):
        return True  # BuildSynergy pair
    if any(b.get("role") == "primary" for b in card.get("builds", [])):
        return True  # BuildSynergy commitment
    if axes & eff_axes:
        return True  # EffectSynergy axis dispatch
    if card.get("id") in eff_ids:
        return True  # EffectSynergy card-id dispatch
    if axes & AMP_AXES:
        return True
    if any(k in {"StrengthPower", "DexterityPower", "VulnerablePower",
                 "WeakPower", "FocusPower"} for k in card.get("vars", {}).keys()):
        return True  # HandSynergy
    return False


def main() -> None:
    cat = json.loads(CATALOG.read_text(encoding="utf-8"))
    cards = [c for c in cat["cards"] if not c.get("is_upgraded")]
    eff_axes, eff_ids = parse_dispatch_paths()

    def is_unplayable(c):
        return c.get("type") in ("Curse", "Status", "Quest")

    playable = [c for c in cards if not is_unplayable(c)]

    # 1. Per-build coverage
    print("=== 1. Per-build coverage ===\n")
    by_build_total = defaultdict(int)
    by_build_explicit = defaultdict(int)
    for c in playable:
        for b in c.get("builds", []):
            tag = b.get("tag", "")
            by_build_total[tag] += 1
            if has_explicit_handling(c, eff_axes, eff_ids):
                by_build_explicit[tag] += 1

    print(f"{'build':<14}  {'cards':>5}  {'explicit':>8}  {'%':>5}")
    print("-" * 40)
    for tag, total in sorted(by_build_total.items(), key=lambda x: -x[1]):
        exp = by_build_explicit[tag]
        pct = exp / total * 100 if total else 0
        print(f"{tag:<14}  {total:>5}  {exp:>8}  {pct:>4.0f}%")
    print()

    # 2. Per-character coverage (playable only)
    print("=== 2. Per-character coverage (playable cards) ===\n")
    by_char_total = defaultdict(int)
    by_char_explicit = defaultdict(int)
    by_char_direct_only = defaultdict(list)
    for c in playable:
        ch = c.get("character", "?")
        by_char_total[ch] += 1
        if has_explicit_handling(c, eff_axes, eff_ids):
            by_char_explicit[ch] += 1
        else:
            by_char_direct_only[ch].append(c)

    print(f"{'character':<11}  {'cards':>5}  {'explicit':>8}  {'%':>5}  {'direct-stat only':<10}")
    print("-" * 60)
    for ch, total in sorted(by_char_total.items()):
        exp = by_char_explicit[ch]
        pct = exp / total * 100 if total else 0
        print(f"{ch:<11}  {total:>5}  {exp:>8}  {pct:>4.0f}%  {total - exp} cards")

    # 3. Per-card-type coverage
    print()
    print("=== 3. Per-card-type coverage (playable) ===\n")
    by_type_total = defaultdict(int)
    by_type_explicit = defaultdict(int)
    for c in playable:
        t = c.get("type", "?")
        by_type_total[t] += 1
        if has_explicit_handling(c, eff_axes, eff_ids):
            by_type_explicit[t] += 1

    print(f"{'type':<8}  {'cards':>5}  {'explicit':>8}  {'%':>5}")
    print("-" * 40)
    for t in ("Attack", "Skill", "Power", "None"):
        total = by_type_total.get(t, 0)
        exp = by_type_explicit.get(t, 0)
        pct = exp / total * 100 if total else 0
        print(f"{t:<8}  {total:>5}  {exp:>8}  {pct:>4.0f}%")

    # 4. Per-mechanism (top axes) coverage
    print()
    print("=== 4. Per-mechanism (axis-level) coverage ===\n")
    axis_total = defaultdict(int)
    axis_explicit = defaultdict(int)
    for c in playable:
        for ax in c.get("axes", []):
            axis_total[ax] += 1
            if has_explicit_handling(c, eff_axes, eff_ids):
                axis_explicit[ax] += 1

    print(f"{'axis':<24}  {'cards':>5}  {'explicit':>8}  {'%':>5}")
    print("-" * 50)
    # Top 25 axes by cards
    for ax, total in sorted(axis_total.items(), key=lambda x: -x[1])[:30]:
        exp = axis_explicit[ax]
        pct = exp / total * 100 if total else 0
        print(f"{ax:<24}  {total:>5}  {exp:>8}  {pct:>4.0f}%")

    # 5. Direct-stat-only cards by tier (the "missed mechanic" set)
    print()
    print("=== 5. Direct-stat-only cards by tier (potential gaps) ===\n")
    by_tier = defaultdict(int)
    for c in playable:
        if not has_explicit_handling(c, eff_axes, eff_ids):
            by_tier[c.get("tier", "?")] += 1
    for tier in "SABCD?":
        print(f"  {tier}: {by_tier[tier]}")


if __name__ == "__main__":
    main()
