"""Spot-check EstimateCardPower HP-loss deduction across HP bands.

Self-damage cards (BLOODLETTING / OFFERING / HEMOKINESIS / BREAKTHROUGH /
BRAND / DEMONIC_SHIELD / BLOOD_WALL / HAUNT) now expose HpLossAmount via
CardEffectSummary. EstimateCardPower deducts proportional to current player
HP so the planner avoids suicidal plays at low HP.

Mirrors the C# EstimateCardPower + HpLoss deduction. Prints expected
in-hand values across HP bands for each self-damage card.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402


CURSE_FREE = W.CURSE_FREE
CURSE_INHAND = W.CURSE_INHAND


def hp_loss_penalty(player_hp: int) -> int:
    if player_hp > 60: return 12
    if player_hp > 40: return 30
    if player_hp > 25: return 70
    return 200


def estimate(*, total_damage=0, block=0, draw=0, energy=0, cost=1,
             hp_loss=0, free_use=False, player_hp=80) -> int:
    v = 0
    if total_damage > 0:
        v += total_damage * (W.DAMAGE_FREE if free_use else W.DAMAGE_INHAND)
    if block > 0:
        v += block * (W.BLOCK_FREE if free_use else W.BLOCK_INHAND)
    v += draw * W.DRAW
    v += energy * (W.ENERGY_FREE if free_use else W.ENERGY_INHAND)
    if not free_use:
        if cost == 0: v += W.COST_0_BONUS
        elif cost == 1: v += W.COST_1_BONUS
        elif cost >= 3: v += W.COST_3_PLUS_PENALTY
    if hp_loss > 0:
        v -= hp_loss * hp_loss_penalty(player_hp)
    floor = CURSE_FREE if free_use else CURSE_INHAND
    return max(floor, v)


# (card name, cost, damage, block, draw, energy_gain, hp_loss)
SELF_DAMAGE_CARDS = [
    ("BLOODLETTING (S, 0c)", 0, 0, 0, 3, 2, 3),
    ("OFFERING    (S, 0c)", 0, 0, 0, 3, 2, 6),    # OFFERING actually has different vars but use these as proxy
    ("HEMOKINESIS (A, 1c)", 1, 15, 0, 0, 0, 2),
    ("BREAKTHROUGH (B, 1c)", 1, 12, 0, 0, 0, 1),
    ("BRAND       (A, 0c)", 0, 0, 0, 0, 0, 1),  # actually applies brand effect; base proxy
    ("BLOOD_WALL  (A, 2c)", 2, 0, 14, 0, 0, 2),
    ("HAUNT-card-stub", 1, 0, 0, 0, 0, 6),       # Power card, but for stub
]

HP_BANDS = [80, 50, 35, 20]


def main() -> None:
    print("EstimateCardPower with HP-loss deduction\n")
    print(f"{'card':<25}  " + "  ".join(f"hp={h:>2}" for h in HP_BANDS))
    print("-" * 70)
    for label, cost, dmg, blk, draw, energy, hp_loss in SELF_DAMAGE_CARDS:
        cells = []
        for hp in HP_BANDS:
            v = estimate(total_damage=dmg, block=blk, draw=draw,
                         energy=energy, cost=cost, hp_loss=hp_loss,
                         free_use=False, player_hp=hp)
            cells.append(f"{v:>+5d}")
        print(f"{label:<25}  " + "  ".join(cells))

    print()
    print("Penalty bands:")
    for hp in HP_BANDS:
        print(f"  HP > {25 if hp <= 25 else (40 if hp <= 40 else (60 if hp <= 60 else 999))}: penalty = {hp_loss_penalty(hp)}/HP loss")


if __name__ == "__main__":
    main()
