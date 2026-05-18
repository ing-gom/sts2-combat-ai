"""Spot-check v0.7.16 -- AGGRESSION turn-start hand addition.

The Power recalls a random Attack from the discard pile (upgraded +30%)
per stack. AdvanceTurnInternal synthesizes the recalled card from the
discard's average attack stats and appends to nextHand.
"""

UPGRADE_FACTOR = 1.3


def recalled_card_stats(discard_attacks: list[tuple[int, int, int]]):
    """discard_attacks = [(damage, hits, cost), ...]. Only Attack cards.
    Returns (avg_damage_upgraded, avg_cost)."""
    if not discard_attacks: return None
    total_dmg = sum(d * max(1, h) for d, h, _ in discard_attacks)
    total_cost = sum(max(0, c) for _, _, c in discard_attacks)
    n = len(discard_attacks)
    return (int(total_dmg / n * UPGRADE_FACTOR), max(0, total_cost // n))


SCENARIOS = [
    # (label, discard_attacks, aggression_stacks)
    ("no discard attacks", [], 1),
    ("1 Strike (6d, 1c)", [(6, 1, 1)], 1),
    ("Strike + Bash (6d/1c + 8d/2c)", [(6, 1, 1), (8, 1, 2)], 1),
    ("3 attacks (mid-game discard)",
        [(6, 1, 1), (8, 1, 2), (12, 1, 1)], 1),
    ("AGGRESSION 2 stacks, 3 attacks",
        [(6, 1, 1), (8, 1, 2), (12, 1, 1)], 2),
    ("strong discard (Bludgeon, Heavy Blade, Iron Wave)",
        [(18, 1, 2), (20, 1, 2), (8, 1, 1)], 1),
    ("multi-hit (Sword Boomerang 3x4d)", [(4, 3, 1)], 1),
]


def main() -> None:
    print(f"{'scenario':<55}  {'avgDmg':>6}  {'avgCost':>7}  {'stacks':>6}")
    print("-" * 90)
    for label, discard, stacks in SCENARIOS:
        result = recalled_card_stats(discard)
        if result is None:
            print(f"{label:<55}  {'-':>6}  {'-':>7}  {stacks:>6}  (no add)")
            continue
        dmg, cost = result
        print(f"{label:<55}  {dmg:>6}  {cost:>7}  {stacks:>6}  -> +{stacks} card(s) of {dmg}dmg")


if __name__ == "__main__":
    main()
