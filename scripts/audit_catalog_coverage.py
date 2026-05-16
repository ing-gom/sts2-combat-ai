"""
Audit which cards from cards_catalog.json (full) made it into our mod's
embedded card_triggers.json, and which were dropped (and why).

Run from repo root:
    python scripts/audit_catalog_coverage.py
"""

import json
from collections import Counter, defaultdict
from pathlib import Path


def main():
    repo_root = Path(__file__).resolve().parent.parent
    full = json.loads((repo_root / "scripts" / "cards_catalog.json").read_text(encoding="utf-8"))
    ours = json.loads((repo_root / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json").read_text(encoding="utf-8"))

    full_cards = [c for c in full["cards"] if not c.get("is_upgraded")]
    print(f"== Coverage ==")
    print(f"Total base (non-upgraded) cards in master catalog: {len(full_cards)}")
    print(f"Cards in our extracted catalog: {len(ours['cards'])}")
    print(f"Dropped (no axes/builds/keywords/triggers): {len(full_cards) - len(ours['cards'])}")
    print()

    # Find dropped cards
    our_ids = set(ours["cards"].keys())
    dropped = [c for c in full_cards if c["id"] not in our_ids]
    print(f"== Dropped cards (sample of first 20) ==")
    for c in dropped[:20]:
        axes = c.get("axes", [])
        builds = [b["tag"] for b in c.get("builds", [])]
        kws = c.get("keywords", [])
        print(f"  {c['id']:<35} char={c.get('character', '?'):<12} axes={axes} kws={kws} builds={builds}")
    if len(dropped) > 20:
        print(f"  ... and {len(dropped) - 20} more")
    print()

    # Per-character distribution
    print(f"== Per-character coverage ==")
    char_total = Counter(c["character"] for c in full_cards)
    char_ours = Counter()
    for c in full_cards:
        if c["id"] in our_ids:
            char_ours[c["character"]] += 1
    for char in sorted(char_total.keys()):
        total = char_total[char]
        covered = char_ours[char]
        pct = 100.0 * covered / total if total else 0
        print(f"  {char:<14} {covered:>3} / {total:>3}  ({pct:.0f}%)")
    print()

    # Per-build coverage (builds present in extracted catalog)
    print(f"== Builds in extracted catalog ==")
    build_counts = Counter()
    for cid, info in ours["cards"].items():
        for b in info.get("builds", []):
            build_counts[b["tag"]] += 1
    for tag, n in sorted(build_counts.items(), key=lambda x: -x[1]):
        print(f"  {tag:<14} {n:>3} cards")
    print()

    # Axis distribution (top 30)
    print(f"== Top axes in extracted catalog ==")
    axis_counts = Counter()
    for info in ours["cards"].values():
        for a in info.get("axes", []):
            axis_counts[a] += 1
    for axis, n in axis_counts.most_common(30):
        print(f"  {axis:<32} {n:>3}")


if __name__ == "__main__":
    main()
