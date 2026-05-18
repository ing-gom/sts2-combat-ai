"""Analytic complexity check for the v0.7.9 depth=3 beam search.

Recurrence:
  T(n, 0, K) = 0
  T(n, 1, K) = n                       (score all, return max)
  T(n, d, K) = n + K * T(n-1, d-1, K)  (score all, beam top-K, recurse)

PlanNextStep does no beam at the top layer (we want to compare ALL first
cards, not just K). So total scorings:
  plan(B, N, depth, K) = B*N + B*N * T(B*N - 1, depth - 1, K)
"""

def t(n_cards: int, depth: int, beam_k: int) -> int:
    if depth <= 0 or n_cards <= 0:
        return 0
    if depth == 1:
        return n_cards
    return n_cards + beam_k * t(n_cards - 1, depth - 1, beam_k)


def plan_next_step(hand: int, targets: int, depth: int, beam_k: int) -> int:
    top = hand * targets
    if depth <= 1:
        return top
    return top + top * t(top - 1, depth - 1, beam_k)


def main() -> None:
    print(f"{'hand':<5} {'targets':<7} {'depth':<5} {'beamK':<5} {'scorings':>10}  vs depth=2")
    print("-" * 60)
    for hand in (4, 6, 8):
        for tgts in (1, 3):
            base = plan_next_step(hand, tgts, depth=2, beam_k=999)  # depth=2 no beam = legacy v0.7.x
            for depth, beamK in [(2, 999), (2, 3), (3, 3), (3, 5), (4, 3)]:
                n = plan_next_step(hand, tgts, depth, beamK)
                tag = "legacy" if depth == 2 and beamK == 999 else ""
                ratio = f"{n / base:.1f}x"
                print(f"{hand:<5} {tgts:<7} {depth:<5} {beamK:<5} {n:>10}  {ratio:>6}  {tag}")
            print()


if __name__ == "__main__":
    main()
