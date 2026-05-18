"""Spot-check the v0.7.7 ApplyHpLossConsumer expansion.

Three signals stack:
  1) HP threshold (low <=30 / mid <=50)
  2) CombatPlayerHpLossEvents (proxy for RUPTURE etc. stacking on past events)
  3) HP_LOSS axis producers in piles (future events likely)

Reports how each scenario scores so RUPTURE / TEAR_ASUNDER / INFERNO
calibration can be checked after HP_LOSS rule changes.
"""
from __future__ import annotations


HP_THRESHOLD_LOW = 30
HP_THRESHOLD_MID = 50
THRESHOLD_LOW_BONUS = 350
THRESHOLD_MID_BONUS = 200
PER_EVENT_BONUS = 60
PER_FUTURE_PRODUCER_BONUS = 35
FUTURE_CAP = 300


def apply_hploss_consumer(*, hp: int, events: int, producers_in_piles: int) -> tuple[int, list[str]]:
    b = 0
    parts: list[str] = []

    if hp <= HP_THRESHOLD_LOW:
        b += THRESHOLD_LOW_BONUS
        parts.append(f"hpLossLow(hp{hp})=+{THRESHOLD_LOW_BONUS}")
    elif hp <= HP_THRESHOLD_MID:
        b += THRESHOLD_MID_BONUS
        parts.append(f"hpLossMid(hp{hp})=+{THRESHOLD_MID_BONUS}")

    if events > 0:
        v = events * PER_EVENT_BONUS
        b += v
        parts.append(f"hpLossEvents({events})=+{v}")

    if producers_in_piles > 0:
        v = min(producers_in_piles * PER_FUTURE_PRODUCER_BONUS, FUTURE_CAP)
        b += v
        parts.append(f"hpLossProducers({producers_in_piles})=+{v}")

    return b, parts


SCENARIOS = [
    # (label, hp, events, producers)
    ("turn 1 healthy, no setup",            80, 0, 0),
    ("turn 1 healthy, 1 BLOODLETTING ready", 80, 0, 1),
    ("turn 1 healthy, OFFERING+HEMOKINESIS+INFERNO ready", 80, 0, 3),
    ("mid-fight, 1 HP loss event",          55, 1, 0),
    ("mid-fight, 1 event + 2 producers",    55, 1, 2),
    ("low HP, 3 events + Bloodletting deck", 25, 3, 5),
    ("critical, 5 events + heavy self-harm", 12, 5, 7),
    ("late, 8 events stacked",              30, 8, 1),
]


def main() -> None:
    print(f"{'scenario':<48}  {'hp':>3}  {'events':>6}  {'prod':>4}  {'bonus':>6}  parts")
    print("-" * 110)
    for label, hp, events, producers in SCENARIOS:
        bonus, parts = apply_hploss_consumer(hp=hp, events=events, producers_in_piles=producers)
        print(f"{label:<48}  {hp:>3}  {events:>6}  {producers:>4}  {bonus:>6}  {' '.join(parts)}")


if __name__ == "__main__":
    main()
