"""Spot-check v0.7.20 -- BuildSynergy role_needs cross-axis integration.

Mirrors AxisSynergyLookup + BuildSynergy.Compute in Python. Validates:
  - Legacy pair (POISON_PRODUCER + POISON_AMPLIFIER) still scores 250
    (role_needs has it at w=2.5, WeightToScore=100)
  - NEW cross-axis (POISON_PRODUCER + DRAW) scores 80 (w=0.8)
  - PerAxisBonusCap (400) caps multi-hook axes (FORGE_PRODUCER + 4 hooks)
"""
from __future__ import annotations
import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
ROLE_NEEDS = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Data" / "role_needs.json"

WEIGHT_TO_SCORE = 100
PER_AXIS_BONUS_CAP = 400


def load_needs() -> dict:
    raw = json.loads(ROLE_NEEDS.read_text(encoding="utf-8"))
    return {k: v for k, v in raw.items() if not k.startswith("_") and isinstance(v, list)}


def compute_bonus_for_axis(axis: str, hand_axes: set[str], needs_table: dict) -> tuple[int, list[str]]:
    """Mirror of BuildSynergy per-axis lookup. Returns (bonus, debug_lines)."""
    needs = needs_table.get(axis, [])
    if not needs: return 0, []

    bonus = 0
    triggered = []
    mutex_best = {}

    for entry in needs:
        role = entry.get("role", "")
        w = entry.get("w", 0)
        req = entry.get("requires_with")
        mg = entry.get("mutex_group")

        if req and req not in hand_axes:
            continue
        if role not in hand_axes:
            continue

        if mg:
            if w > mutex_best.get(mg, 0):
                mutex_best[mg] = w
            triggered.append(f"{role}(mg={mg}, w={w})")
        else:
            bonus += int(w * WEIGHT_TO_SCORE)
            triggered.append(f"{role}(w={w}, +{int(w*WEIGHT_TO_SCORE)})")

    for w in mutex_best.values():
        bonus += int(w * WEIGHT_TO_SCORE)

    capped = min(bonus, PER_AXIS_BONUS_CAP)
    if bonus > PER_AXIS_BONUS_CAP:
        triggered.append(f"[CAPPED {bonus} -> {capped}]")
    return capped, triggered


SCENARIOS = [
    ("Legacy: POISON_PRODUCER + POISON_AMPLIFIER hand",
        "POISON_PRODUCER", {"POISON_AMPLIFIER"}),
    ("Legacy + new: POISON_PRODUCER + DRAW (no amp)",
        "POISON_PRODUCER", {"DRAW"}),
    ("Combo: POISON_PRODUCER + AMP + CONSUMER + DRAW + AOE",
        "POISON_PRODUCER", {"POISON_AMPLIFIER", "POISON_CONSUMER", "DRAW", "AOE_POISON"}),
    ("FORGE_PRODUCER with all hooks (cap test)",
        "FORGE_PRODUCER", {"LORDS_BLADE_AMPLIFIER", "LORDS_BLADE_PAYOFF", "BLOCK", "DAMAGE", "FORGE_AMPLIFIER"}),
    ("Empty hand: no triggers",
        "POISON_PRODUCER", set()),
    ("CUNNING_PRODUCER + DRAW (cross-axis only)",
        "CUNNING_PRODUCER", {"DRAW"}),
    ("SKELETON_PRODUCER + MINION (cross-axis)",
        "SKELETON_PRODUCER", {"MINION", "SKELETON_AMPLIFIER"}),
]


def main() -> None:
    needs_table = load_needs()
    print(f"role_needs axes loaded: {len(needs_table)}")
    print(f"WeightToScore = {WEIGHT_TO_SCORE}, PerAxisBonusCap = {PER_AXIS_BONUS_CAP}")
    print()

    for label, axis, hand_axes in SCENARIOS:
        bonus, triggered = compute_bonus_for_axis(axis, hand_axes, needs_table)
        trig = ", ".join(triggered) if triggered else "(no triggers)"
        print(f"{label}")
        print(f"  axis={axis}, hand={hand_axes}")
        print(f"  bonus={bonus}  triggers: {trig}")
        print()


if __name__ == "__main__":
    main()
