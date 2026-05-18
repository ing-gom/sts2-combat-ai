"""Spot-check v0.7.19 -- B-tier 9 mechanic handlers."""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402


def finisher(*, turn_attacks_played: int) -> int:
    if turn_attacks_played <= 0: return 0
    return turn_attacks_played * 6 * W.DAMAGE_INHAND


def bolas(*, turns: int) -> int:
    f = max(0, turns - 1)
    if f == 0: return 0
    per_play = 3 * W.DAMAGE_FREE + W.COST_0_BONUS
    return min(int(f * per_play * 0.5), 500)


def follow_through(*, others_in_hand: int) -> int:
    if others_in_hand < 5: return 0
    return 7 * W.DAMAGE_INHAND


def expect_a_fight(*, powers_in_hand: int) -> int:
    if powers_in_hand <= 0: return 0
    return powers_in_hand * W.ENERGY_INHAND


def spite(*, hp_loss_events: int) -> int:
    if hp_loss_events <= 0: return 0
    return 2 * W.DAMAGE_INHAND


def outmaneuver() -> int:
    return int(2 * W.ENERGY_INHAND * 0.6)


def main() -> None:
    print("=== FINISHER (B, Silent) -- 6dmg per Attack played this turn ===")
    print(f"{'attacks':<7}  {'bonus':>5}")
    for a in (0, 1, 2, 3, 4):
        print(f"{a:<7}  {finisher(turn_attacks_played=a):>+5d}")
    print()

    print("=== BOLAS (B, Shared) -- 3dmg return-to-hand chain ===")
    print(f"{'turns':<5}  {'bonus':>5}")
    for t in (1, 2, 3, 5, 7, 10):
        print(f"{t:<5}  {bolas(turns=t):>+5d}")
    print()

    print("=== FOLLOW_THROUGH (B, Silent) -- repeat if 5+ others ===")
    print(f"{'others':<6}  {'bonus':>5}")
    for o in (3, 4, 5, 6, 8):
        print(f"{o:<6}  {follow_through(others_in_hand=o):>+5d}")
    print()

    print("=== EXPECT_A_FIGHT (B, Ironclad) -- 1 energy per hand Power ===")
    print(f"{'powers':<6}  {'bonus':>5}")
    for p in (0, 1, 2, 3):
        print(f"{p:<6}  {expect_a_fight(powers_in_hand=p):>+5d}")
    print()

    print("=== SPITE (B, Ironclad) -- +2dmg if HP lost ===")
    print(f"{'hpEvents':<8}  {'bonus':>5}")
    for h in (0, 1, 3):
        print(f"{h:<8}  {spite(hp_loss_events=h):>+5d}")
    print()

    print("=== OUTMANEUVER (B, Shared) -- +2 energy next turn ===")
    print(f"  bonus = {outmaneuver():+d} (2 energy × 60 × 0.6 discount)")


if __name__ == "__main__":
    main()
