"""Spot-check v0.7.18 -- A-tier 5 mechanic handlers."""

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402


def flechettes(*, skills_in_hand: int, current_hits: int = 1) -> int:
    extra = max(0, skills_in_hand - current_hits)
    if extra == 0: return 0
    return extra * 5 * W.DAMAGE_INHAND


def make_it_so(*, turn_skills_played: int) -> int:
    if turn_skills_played <= 0: return 0
    prob = min(1.0, turn_skills_played / 3.0)
    per_play = 6 * W.DAMAGE_INHAND
    return int(per_play * prob)


def sunder_kill(*, projected_dmg: int, target_eff_hp: int) -> int:
    if projected_dmg < target_eff_hp: return 0
    return 3 * W.ENERGY_INHAND


def tesla_coil(*, orb_count: int) -> int:
    if orb_count <= 0: return 0
    return orb_count * 200


def thrumming_hatchet(*, turns: int) -> int:
    future = max(0, turns - 1)
    if future == 0: return 0
    per_play = 11 * W.DAMAGE_INHAND + W.COST_1_BONUS
    return min(int(future * per_play * 0.5), 1000)


def main() -> None:
    print("=== FLECHETTES (A, Silent) -- 5dmg per Skill in hand ===")
    print(f"{'skills':<6}  {'current hits':<13}  {'bonus':>5}")
    for s, h in [(0, 1), (1, 1), (2, 1), (3, 1), (5, 1), (5, 3)]:
        print(f"{s:<6}  {h:<13}  {flechettes(skills_in_hand=s, current_hits=h):>+5d}")
    print()

    print("=== MAKE_IT_SO (A, Regent) -- reclaim at 3+ Skills ===")
    print(f"{'turnSkills':<10}  {'prob':>5}  {'bonus':>5}")
    for s in (0, 1, 2, 3, 4, 5):
        print(f"{s:<10}  {min(1.0, s/3.0):>5.2f}  {make_it_so(turn_skills_played=s):>+5d}")
    print()

    print("=== SUNDER (A, Defect) -- 24dmg + 3-energy on kill ===")
    print(f"{'projected':<9}  {'targetEffHp':<11}  {'killed?':<8}  {'bonus':>5}")
    for p, hp in [(24, 50), (24, 30), (28, 30), (24, 24), (30, 25)]:
        print(f"{p:<9}  {hp:<11}  {'YES' if p >= hp else 'no':<8}  {sunder_kill(projected_dmg=p, target_eff_hp=hp):>+5d}")
    print()

    print("=== TESLA_COIL (A, Defect) -- evoke all orbs ===")
    print(f"{'orbCount':<8}  {'bonus':>5}")
    for n in (0, 1, 2, 3, 5):
        print(f"{n:<8}  {tesla_coil(orb_count=n):>+5d}")
    print()

    print("=== THRUMMING_HATCHET (A, Shared) -- return to hand each turn ===")
    print(f"{'turns':<5}  {'futurePlays':<11}  {'bonus':>5}")
    for t in (1, 2, 3, 5, 7, 10):
        print(f"{t:<5}  {max(0, t-1):<11}  {thrumming_hatchet(turns=t):>+5d}")


if __name__ == "__main__":
    main()
