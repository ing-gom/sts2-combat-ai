"""Spot-check RemainingTurnsEstimator (v0.7.6) across canonical combat states.

Mirrors RemainingTurnsEstimator.From in Python so the estimator and the
v0.7.x handlers that consume it can be validated without launching the game.
Prints turn-count estimates for representative scenarios and shows how MAYHEM
/ AGGRESSION / STAMPEDE deltas shift compared to the old static proxy of 3.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]
POWER_CATALOG = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs"

MIN_TURNS = 1
MAX_TURNS = 10
FALLBACK = 3


def lookup(power_name: str) -> int:
    text = POWER_CATALOG.read_text(encoding="utf-8")
    m = re.search(rf'\{{\s*"{re.escape(power_name)}"\s*,\s*(-?\d+)\s*\}}', text)
    return int(m.group(1)) if m else 0


class Card:
    def __init__(self, dmg=0, curse=False, attack=True):
        self.TotalDamage = dmg
        self.IsAttack = attack
        self.IsCurseOrStatus = curse


def estimate_remaining_turns(enemy_hps, hand_attack_dmgs, player_strength=0):
    enemy_hp = sum(h for h in enemy_hps if h > 0)
    if enemy_hp <= 0: return MIN_TURNS
    dpt = sum(hand_attack_dmgs) // 2 + max(0, player_strength) * 2
    if dpt <= 0: return FALLBACK
    est = enemy_hp // dpt
    if est < MIN_TURNS: return MIN_TURNS
    if est > MAX_TURNS: return MAX_TURNS
    return est


def estimate_attack_power_free(dmg):
    return dmg * W.DAMAGE_FREE


def main() -> None:
    print(f"RemainingTurnsEstimator - clamp [{MIN_TURNS}, {MAX_TURNS}], fallback {FALLBACK}")
    print()

    scenarios = [
        # (label, enemy_hps, hand_attack_dmgs, player_strength)
        ("turn 1: boss 250 HP, opener hand (Strike x3)",     [250],      [6,6,6], 0),
        ("turn 1: boss 250 HP, mid hand (Strike+Bash+Bod)",  [250],      [6,8,10], 0),
        ("turn 4: boss 60 HP left, scaled hand",             [60],       [6,12,12], 4),
        ("elite 120 HP, weak hand (Strike x2)",              [120],      [6,6], 0),
        ("3 minions 30 HP each, AoE hand",                   [30,30,30], [10,10], 0),
        ("near-lethal: boss 15 HP, big attack",              [15],       [20], 2),
        ("no attacks in hand (Power-only opener)",           [200],      [], 0),
        ("turn 9 grind: enemy 400 HP, weak hand",            [400],      [4,4], 0),
    ]

    print(f"{'scenario':<55}  {'enemyHp':>7}  {'dpt':>4}  {'turns':>5}")
    print("-" * 80)
    for label, hps, dmgs, str_ in scenarios:
        turns = estimate_remaining_turns(hps, dmgs, str_)
        ehp = sum(h for h in hps if h > 0)
        dpt = sum(dmgs) // 2 + max(0, str_) * 2
        print(f"{label:<55}  {ehp:>7}  {dpt:>4}  {turns:>5}")

    # MAYHEM delta comparison: static vs dynamic.
    print()
    print("=== MAYHEM delta - static (3) vs dynamic ===")
    baked = lookup("MayhemPower")
    cap = 1200
    drawpile_means = [
        ("starter (Strike+Defend)", 225),
        ("mid (mean ~437)",         437),
        ("strong (mean ~637)",      637),
    ]
    test_combats = [
        ("near-lethal (1 turn)",    1),
        ("normal (3 turns)",        3),
        ("long boss (7 turns)",     7),
    ]
    print(f"{'pile':<28}  {'combat':<20}  {'static':>7}  {'dynamic':>8}  {'shift':>6}")
    print("-" * 80)
    for plabel, mean in drawpile_means:
        for clabel, turns in test_combats:
            static_tick = mean * 3
            static_delta = max(min(static_tick - baked, cap), -baked)
            dynamic_tick = mean * turns
            dynamic_delta = max(min(dynamic_tick - baked, cap), -baked)
            shift = dynamic_delta - static_delta
            print(f"{plabel:<28}  {clabel:<20}  {static_delta:>+7d}  {dynamic_delta:>+8d}  {shift:>+6d}")


if __name__ == "__main__":
    main()
