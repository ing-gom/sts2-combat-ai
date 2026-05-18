"""Comprehensive evaluation-path audit for v0.7.16.

Goes beyond the narrow 'synergy degree' (pair/amp axes only) to count ALL
evaluation paths the planner uses:
  1. PowerCatalog (Power cards) -- 100% via id-derived fallback
  2. EffectSynergy axis dispatches (15+ axes)
  3. EffectSynergy card-id dispatches (24+ specific cards)
  4. Direct stat scoring (every Attack/Skill via PlanScorer)
  5. PowerSequencingTier (Power cards within-turn ordering)
  6. BuildSynergy (pair / commitment)
  7. AmplifierSynergy / HandSynergy

A card is "truly uncovered" only when NONE of these paths apply.
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
    """Pull axis names and card-ids from EffectSynergy dispatch points."""
    text = EFFECT_SYNERGY_CS.read_text(encoding="utf-8")
    axes = set(re.findall(r'axes\.Contains\("([A-Z_]+)"\)', text))
    ids = set(re.findall(r'card\.Id == "(CARD\.[A-Z_]+)"', text))
    return axes, ids


def evaluation_paths(card: dict, eff_axes: set[str], eff_ids: set[str]) -> list[str]:
    """List of paths that fire for this card. Empty list = truly uncovered."""
    paths = []
    axes = set(card.get("axes", []))
    cid = card.get("id", "")
    ctype = card.get("type", "")

    # 1. Direct stat scoring -- every non-curse/status Attack or Skill scores
    if ctype in ("Attack", "Skill"):
        paths.append("direct-stat")

    # 2. PowerCatalog (Power cards via PowerVar reflection OR id-derived fallback)
    if ctype == "Power":
        paths.append("PowerCatalog")
        paths.append("PowerSequencingTier")

    # 3. EffectSynergy axis dispatches (intersect)
    matched_axes = axes & eff_axes
    for ax in matched_axes:
        paths.append(f"axis:{ax}")

    # 4. EffectSynergy card-id dispatches
    if cid in eff_ids:
        paths.append(f"id:{cid}")

    # 5. BuildSynergy via pair-axis
    if any(a.endswith(("_PRODUCER", "_AMPLIFIER", "_CONSUMER")) for a in axes):
        paths.append("BuildSynergy-pair")

    # 6. BuildSynergy via primary build tag
    if any(b.get("role") == "primary" for b in card.get("builds", [])):
        paths.append("BuildSynergy-primary")

    # 7. HandSynergy (via vars)
    if any(k in {"StrengthPower", "DexterityPower", "VulnerablePower",
                 "WeakPower", "FocusPower"} for k in card.get("vars", {}).keys()):
        paths.append("HandSynergy")

    # 8. AmplifierSynergy
    if axes & {"POWER_AMPLIFIER", "REPLAY", "ATTACK_REPLAY", "SKILL_REPLAY",
               "ATTACK_REPLAY_RANDOM"}:
        paths.append("AmplifierSynergy")

    return paths


def main() -> None:
    eff_axes, eff_ids = parse_dispatch_paths()
    cat = json.loads(CATALOG.read_text(encoding="utf-8"))
    cards = [c for c in cat["cards"] if not c.get("is_upgraded")]

    print(f"EffectSynergy axis dispatches: {len(eff_axes)}")
    print(f"  {sorted(eff_axes)}")
    print(f"EffectSynergy card-id dispatches: {len(eff_ids)}")
    print(f"  {sorted(eff_ids)}")
    print()

    # Bucket by path count
    by_paths = defaultdict(list)
    for c in cards:
        paths = evaluation_paths(c, eff_axes, eff_ids)
        by_paths[len(paths)].append((c, paths))

    print(f"{'paths':<6}  {'cards':<6}  {'pct':<6}")
    total = len(cards)
    for k in sorted(by_paths.keys()):
        n = len(by_paths[k])
        print(f"{k:<6}  {n:<6}  {n/total:.1%}")
    print()

    # 0-path cards = truly uncovered
    zero = by_paths.get(0, [])
    print(f"=== Truly uncovered (0 evaluation paths): {len(zero)} ===")
    for c, _ in zero:
        print(f"  {c.get('tier', '?')} {c['character']:<11} {c['id']:<28} type={c['type']:<6} axes={c.get('axes', [])}")
    print()

    # 1-path cards by tier (likely 'direct-stat' or 'PowerCatalog' only)
    print(f"=== 1-path cards by tier ===")
    by_tier = defaultdict(int)
    for c, _ in by_paths.get(1, []):
        by_tier[c.get("tier", "?")] += 1
    for tier in "SABCD?":
        print(f"  {tier}: {by_tier[tier]}")
    print()

    # S/A 1-path cards specifically (interesting -- pure-stat with no extra hooks)
    print(f"=== S-tier 1-path (pure-stat / pure-PowerCatalog) ===")
    for c, paths in sorted(by_paths.get(1, []), key=lambda x: x[0]["id"]):
        if c.get("tier") == "S":
            print(f"  {c['character']:<11} {c['id']:<28} type={c['type']:<6} -> {paths}")
    print()
    print(f"=== A-tier 1-path ===")
    for c, paths in sorted(by_paths.get(1, []), key=lambda x: x[0]["id"]):
        if c.get("tier") == "A":
            print(f"  {c['character']:<11} {c['id']:<28} type={c['type']:<6} -> {paths}")


if __name__ == "__main__":
    main()
