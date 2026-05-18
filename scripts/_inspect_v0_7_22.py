"""Spot-check v0.7.22 -- Power activation condition penalties.

Mirrors ComputePowerActivationPenalty in PlanScorer.
"""


def echo_form_penalty(*, energy: int, cost: int, hand_other_playables: int) -> int:
    energy_after = energy - cost
    # In Python mirror, treat other_playables as the count after gating
    if hand_other_playables == 0:
        return -400
    return 0


def barricade_penalty(*, player_block: int, has_block_source: bool) -> int:
    if player_block == 0 and not has_block_source:
        return -200
    return 0


def machine_learning_penalty(*, hand_size: int) -> int:
    if hand_size >= 10:
        return -250
    return 0


def cruelty_penalty(*, any_vuln_enemy: bool, has_vuln_producer: bool) -> int:
    if not any_vuln_enemy and not has_vuln_producer:
        return -200
    return 0


def main() -> None:
    POWER_VALUES = {
        "EchoForm": 1500,
        "Barricade": 1200,
        "MachineLearning": 900,
        "Cruelty": 600,
    }

    print("=== EchoForm activation penalty ===\n")
    print(f"{'scenario':<55}  {'penalty':>8}  {'net (start 1500)':>15}")
    for label, args in [
        ("normal play (cards left to echo)",  dict(energy=3, cost=3, hand_other_playables=3)),
        ("LAST card, 0 energy left, 0 other plays", dict(energy=3, cost=3, hand_other_playables=0)),
        ("0-cost echoform-class with cards",   dict(energy=2, cost=0, hand_other_playables=2)),
    ]:
        p = echo_form_penalty(**args)
        print(f"{label:<55}  {p:>+8}  {POWER_VALUES['EchoForm']+p:>15}")

    print()
    print("=== Barricade activation penalty ===\n")
    print(f"{'scenario':<55}  {'penalty':>8}  {'net (start 1200)':>15}")
    for label, args in [
        ("normal: 10 block already",           dict(player_block=10, has_block_source=False)),
        ("0 block + Defend in hand",           dict(player_block=0, has_block_source=True)),
        ("0 block + NO block cards",           dict(player_block=0, has_block_source=False)),
    ]:
        p = barricade_penalty(**args)
        print(f"{label:<55}  {p:>+8}  {POWER_VALUES['Barricade']+p:>15}")

    print()
    print("=== MachineLearning hand-cap penalty ===\n")
    print(f"{'scenario':<55}  {'penalty':>8}  {'net (start 900)':>15}")
    for label, args in [
        ("normal: hand 5 cards",               dict(hand_size=5)),
        ("nearly full: 9 cards",                dict(hand_size=9)),
        ("AT cap: 10 cards",                    dict(hand_size=10)),
    ]:
        p = machine_learning_penalty(**args)
        print(f"{label:<55}  {p:>+8}  {POWER_VALUES['MachineLearning']+p:>15}")

    print()
    print("=== Cruelty activation penalty ===\n")
    print(f"{'scenario':<55}  {'penalty':>8}  {'net (start 600)':>15}")
    for label, args in [
        ("normal: Vuln target available",      dict(any_vuln_enemy=True, has_vuln_producer=False)),
        ("Bash in hand (Vuln producer)",       dict(any_vuln_enemy=False, has_vuln_producer=True)),
        ("NO Vuln + NO producer (wasted now)", dict(any_vuln_enemy=False, has_vuln_producer=False)),
    ]:
        p = cruelty_penalty(**args)
        print(f"{label:<55}  {p:>+8}  {POWER_VALUES['Cruelty']+p:>15}")


if __name__ == "__main__":
    main()
