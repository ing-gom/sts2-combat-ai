"""Spot-check v0.7.17 -- ALL_FOR_ONE + PINPOINT mechanic handlers.

ALL_FOR_ONE: sum EstimateCardPower over discard 0-cost non-curse cards.
PINPOINT: TurnSkillsPlayed * 60 (EnergyInHand weight).
"""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402

CAP = 1200


def estimate_card_value(*, cost: int, damage: int, block: int, is_attack: bool) -> int:
    v = 0
    if is_attack and damage > 0:
        v += damage * W.DAMAGE_INHAND
    if block > 0:
        v += block * W.BLOCK_INHAND
    if cost == 0: v += W.COST_0_BONUS
    elif cost == 1: v += W.COST_1_BONUS
    elif cost >= 3: v += W.COST_3_PLUS_PENALTY
    return max(W.CURSE_INHAND, v)


def all_for_one_recall(discard_pile: list[dict]) -> tuple[int, int, int]:
    total = 0
    count = 0
    for c in discard_pile:
        if c["cost"] != 0: continue
        if c.get("curse"): continue
        total += estimate_card_value(**c)
        count += 1
    if count == 0:
        return 0, 0, 60  # baseline
    return count, total, min(total, CAP)


def pinpoint_refund(turn_skills_played: int) -> int:
    if turn_skills_played <= 0: return 0
    return turn_skills_played * W.ENERGY_INHAND


# Card stubs: (cost, damage, block, is_attack, curse)
DISCARD_SCENARIOS = [
    ("empty discard", []),
    ("3x Shiv (0c, 4d each)",
        [{"cost": 0, "damage": 4, "block": 0, "is_attack": True}] * 3),
    ("Cantrip mix (Shiv 4d + Inflicting Strike 6d + Toolkit 5b)",
        [{"cost": 0, "damage": 4, "block": 0, "is_attack": True},
         {"cost": 0, "damage": 6, "block": 0, "is_attack": True},
         {"cost": 0, "damage": 0, "block": 5, "is_attack": False}]),
    ("Strong 0-cost (Bloodletting + Offering + 3xShiv)",
        [{"cost": 0, "damage": 0, "block": 0, "is_attack": False}] * 2  # rough draw/energy proxies
        + [{"cost": 0, "damage": 4, "block": 0, "is_attack": True}] * 3),
    ("Pile of strong 0-cost (8 cards)",
        [{"cost": 0, "damage": 8, "block": 0, "is_attack": True}] * 8),
    ("Mixed with 1-cost (only recalls 0-cost)",
        [{"cost": 0, "damage": 4, "block": 0, "is_attack": True}] * 2
        + [{"cost": 1, "damage": 6, "block": 0, "is_attack": True}] * 3),
]


def main() -> None:
    print("=== ALL_FOR_ONE (S, Defect) -- 0-cost recall ===")
    print(f"{'scenario':<55}  {'recalls':>7}  {'sum':>5}  {'bonus':>5}")
    for label, pile in DISCARD_SCENARIOS:
        c, s, bonus = all_for_one_recall(pile)
        print(f"{label:<55}  {c:>7}  {s:>5}  {bonus:>5}")
    print()

    print("=== PINPOINT (S, Silent) -- energy refund per skill ===")
    print(f"{'turnSkillsPlayed':<16}  {'bonus':>5}  {'context':<40}")
    for s in (0, 1, 2, 3, 4, 5):
        print(f"{s:<16}  {pinpoint_refund(s):>+5d}  {'refunds ' + str(s) + ' energy':<40}")


if __name__ == "__main__":
    main()
