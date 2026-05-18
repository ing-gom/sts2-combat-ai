"""Spot-check v0.7.15 -- EchoForm + MachineLearning next-turn modeling."""

NEXT_TURN_DISCOUNT = 0.30
BASE_HAND_SIZE = 5


def hand_size(machine_learning_stacks: int) -> int:
    return BASE_HAND_SIZE + max(0, machine_learning_stacks)


def next_turn_bonus(base_score: int, echo_form_stacks: int) -> int:
    multiplier = 2.0 if echo_form_stacks > 0 else 1.0
    return int(base_score * multiplier * NEXT_TURN_DISCOUNT)


def main() -> None:
    print("=== Next-turn hand size (MachineLearningPower) ===\n")
    print(f"{'stacks':>6}  {'handSize':>8}")
    for ml in (0, 1, 2, 3):
        print(f"{ml:>6}  {hand_size(ml):>8}")

    print()
    print("=== Next-turn first-card bonus (EchoFormPower) ===\n")
    print(f"{'baseScore':>9}  {'echo=0':>6}  {'echo>=1':>7}  {'shift':>5}")
    for base in (100, 300, 600, 1000, 1500):
        no_echo = next_turn_bonus(base, 0)
        echo = next_turn_bonus(base, 1)
        print(f"{base:>9}  {no_echo:>6}  {echo:>7}  {echo - no_echo:>+5}")

    print()
    print("EchoForm doubles next-turn first-card bonus (depth=1 only).")
    print("MachineLearning expands sampled / synthetic hand by +1 per stack.")


if __name__ == "__main__":
    main()
