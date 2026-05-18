"""Spot-check v0.7.14 -- Monte Carlo next-turn hand sampling variance.

Demonstrates how N samples reduce noise vs single synthetic average. Shows
the spread in 'next-turn opening hand quality' across different decks.
"""
from __future__ import annotations

import random


def estimate_card_value(damage: int, block: int, is_attack: bool) -> int:
    if is_attack: return damage * 35
    return block * 25


# Deck samples (mix of card values)
DECKS = {
    "starter (5xStrike-6 + 5xDefend-5)":
        [(6, 0, True)] * 5 + [(0, 5, False)] * 5,
    "balanced (3xStrike + 3xDefend + 2xBash + 2xPommel)":
        [(6, 0, True)] * 3 + [(0, 5, False)] * 3
        + [(8, 0, True)] * 2 + [(9, 0, True)] * 2,
    "Bludgeon deck (5xBludgeon-18 + 3xStrike + 2xDefend)":
        [(18, 0, True)] * 5 + [(6, 0, True)] * 3 + [(0, 5, False)] * 2,
    "Curse-polluted (3xCurse + 4xStrike + 3xDefend)":
        [(0, 0, False)] * 3 + [(6, 0, True)] * 4 + [(0, 5, False)] * 3,
}


def sample_hand(pool: list, hand_size: int, rng: random.Random) -> list:
    if len(pool) <= hand_size:
        return list(pool)
    copy = list(pool)
    rng.shuffle(copy)
    return copy[:hand_size]


def hand_value(hand: list) -> int:
    return sum(estimate_card_value(*c) for c in hand)


def main() -> None:
    rng = random.Random(42)
    print(f"{'deck':<55}  {'synth-avg':>9}  {'N=1':>5}  {'N=3':>5}  {'N=10':>5}  {'true mean':>9}")
    print("-" * 110)
    for label, pool in DECKS.items():
        # Synthetic average
        avg_val = sum(estimate_card_value(*c) for c in pool) / len(pool)
        synth_hand_value = int(avg_val * 5)
        # True mean of 5-card hand
        TRUE_TRIALS = 5000
        true_total = 0
        rng_truth = random.Random(0)
        for _ in range(TRUE_TRIALS):
            h = sample_hand(pool, 5, rng_truth)
            true_total += hand_value(h)
        true_mean = true_total / TRUE_TRIALS

        # MC estimates with N=1, 3, 10 (seed=42 for reproducibility)
        results = {}
        for N in (1, 3, 10):
            rng_mc = random.Random(42)
            total = 0
            for _ in range(N):
                h = sample_hand(pool, 5, rng_mc)
                total += hand_value(h)
            results[N] = total / N

        print(f"{label:<55}  {synth_hand_value:>9}  {int(results[1]):>5}  {int(results[3]):>5}  {int(results[10]):>5}  {int(true_mean):>9}")

    print()
    print("synth-avg = 5x mean card value (current default behavior)")
    print("N samples = Monte Carlo of N drawn hands (Fisher-Yates)")
    print("true mean = 5000-trial Monte Carlo (reference)")


if __name__ == "__main__":
    main()
