"""Spot-check Level 4 pool-based card evaluation per character.

Mirrors TryApplyPoolBasedRandom (EffectSynergy.cs) to compare the new
pool-aware value against the v0.6.9 / v0.7.1 flat magnitudes for each
Level 4 card. Run as a sanity check after build_pool_means.py changes.
"""
from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
POOL_MEANS = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Data" / "pool_means.json"

# Mirror of the switch in TryApplyPoolBasedRandom.
# (filter, aggregation, multiplier, flat_fallback)
LEVEL4_CARDS = {
    "CREATIVE_AI":      ("power_free", "mean",    3, 150),
    "HELLO_WORLD":      ("common",     "mean",    3, 120),
    "SPECTRUM_SHIFT":   ("colorless",  "mean",    3, 100),
    "WHITE_NOISE":      ("power_free", "mean",    1, 350),
    "DISTRACTION":      ("skill_free", "mean",    1, 240),
    "CALL_OF_THE_VOID": ("all_free",   "mean",    1, 100),
    "LARGESSE":         ("colorless",  "mean",    1, 150),
    "DISCOVERY":        ("all",        "top1of3", 1, 280),
    "SPLASH":           ("attack",     "top1of3", 1, 200),
    "JACKPOT":          ("all_free",   "mean",    3, 180),
}

CAP_PER_CARD = 800


def main() -> None:
    data = json.loads(POOL_MEANS.read_text(encoding="utf-8"))
    chars = data["characters"]

    headers = ["card", "flat"] + sorted(chars.keys())
    rows: list[list[str]] = []
    for card, (flt, agg, mult, flat) in LEVEL4_CARDS.items():
        row = [card, str(flat)]
        for ch in sorted(chars.keys()):
            summary = chars[ch].get(flt, {})
            unit = summary.get(agg, 0)
            v = min(unit * mult, CAP_PER_CARD) if unit > 0 else 0
            marker = "*" if v >= CAP_PER_CARD else " "
            delta = v - flat
            row.append(f"{v:>4}{marker} ({delta:+d})")
        rows.append(row)

    # Width tuning.
    col_widths = [max(len(h), max(len(r[i]) for r in rows)) for i, h in enumerate(headers)]
    fmt = "  ".join(f"{{:<{w}}}" for w in col_widths)
    print(fmt.format(*headers))
    print("  ".join("-" * w for w in col_widths))
    for r in rows:
        print(fmt.format(*r))

    print()
    print("* = capped at 800. Δ shown vs the flat fallback.")
    print("Multiplier 3 reflects RemainingTurnsProxy for per-turn Power passives.")


if __name__ == "__main__":
    main()
