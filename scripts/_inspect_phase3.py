"""Spot-check v0.7.11 - self-copy chain handlers + skeleton ally damage.

3a - chain card bonuses across RemainingTurnsEstimator outputs.
3b - RemainingTurnsEstimator gain when allies present.
"""
from __future__ import annotations


# --- Phase 3a: Chain handlers (mirroring C#) ---

CHAIN_DISCOUNT = 0.4
DAMAGE_INHAND = 35
BLOCK_INHAND = 25
COST0_BONUS = 80
DAMAGE_FREE = 50


def anger_chain(turns: int) -> int:
    future = min(3, max(0, turns - 1))
    if future == 0: return 0
    per_play = 6 * DAMAGE_INHAND + COST0_BONUS  # 210 + 80 = 290
    v = int(future * per_play * CHAIN_DISCOUNT)
    return min(v, 400)


def undeath_chain(turns: int) -> int:
    future = min(3, max(0, turns - 1))
    if future == 0: return 0
    per_play = 7 * BLOCK_INHAND + COST0_BONUS  # 175 + 80 = 255
    v = int(future * per_play * CHAIN_DISCOUNT)
    return min(v, 400)


def dual_wield_chain(best_in_hand: int) -> int:
    if best_in_hand == 0: return 60
    return int(best_in_hand * 0.7)


def heirloom_hammer_chain(best_hand_attack: int) -> int:
    if best_hand_attack == 0: return 60
    return int(best_hand_attack * 0.7)


def nightmare_chain(best_in_hand: int) -> int:
    if best_in_hand == 0: return 50
    return min(int(3 * best_in_hand * 0.5), 900)


def adaptive_strike_chain() -> int:
    return int(18 * DAMAGE_FREE * 0.4)  # 18 * 50 * 0.4 = 360


# --- Phase 3b: ally damage in RemainingTurnsEstimator ---

def estimate_turns(*, enemy_hp: int, hand_atk_damage: int, strength: int,
                   ally_damage_per_turn: int) -> int:
    if enemy_hp <= 0: return 1
    dpt = hand_atk_damage // 2 + max(0, strength) * 2 + ally_damage_per_turn
    if dpt <= 0: return 3  # fallback
    est = enemy_hp // dpt
    return max(1, min(10, est))


def main() -> None:
    print("=== Phase 3a - Chain handler outputs ===\n")
    print(f"{'card':<20}  {'turns=2':>7}  {'turns=4':>7}  {'turns=7':>7}")
    for label, fn in [
        ("ANGER", anger_chain),
        ("UNDEATH", undeath_chain),
    ]:
        print(f"{label:<20}  {fn(2):>+7d}  {fn(4):>+7d}  {fn(7):>+7d}")

    print()
    print(f"{'card':<20}  {'hand=0':>6}  {'hand=200':>8}  {'hand=500':>8}  {'hand=900':>8}")
    for label, fn in [
        ("DUAL_WIELD", dual_wield_chain),
        ("HEIRLOOM_HAMMER", heirloom_hammer_chain),
        ("NIGHTMARE", nightmare_chain),
    ]:
        print(f"{label:<20}  {fn(0):>+6d}  {fn(200):>+8d}  {fn(500):>+8d}  {fn(900):>+8d}")

    print(f"{'ADAPTIVE_STRIKE':<20}  free-copy bonus = {adaptive_strike_chain():>+6d} (constant)")

    print()
    print("=== Phase 3b - RemainingTurnsEstimator with allies ===\n")
    scenarios = [
        ("Necrobinder, no skeletons, boss 300 HP",
            dict(enemy_hp=300, hand_atk_damage=20, strength=0, ally_damage_per_turn=0)),
        ("Necrobinder, 1 skeleton 8 dmg, boss 300 HP",
            dict(enemy_hp=300, hand_atk_damage=20, strength=0, ally_damage_per_turn=8)),
        ("Necrobinder, 3 skeletons 8 dmg each, boss 300 HP",
            dict(enemy_hp=300, hand_atk_damage=20, strength=0, ally_damage_per_turn=24)),
        ("Necrobinder, 5 skeletons 8 dmg, elite 100",
            dict(enemy_hp=100, hand_atk_damage=10, strength=0, ally_damage_per_turn=40)),
    ]
    print(f"{'scenario':<55}  {'dpt':>4}  {'turns':>5}")
    for label, args in scenarios:
        t = estimate_turns(**args)
        dpt = args['hand_atk_damage'] // 2 + max(0, args['strength']) * 2 + args['ally_damage_per_turn']
        print(f"{label:<55}  {dpt:>4}  {t:>5}")


if __name__ == "__main__":
    main()
