"""Spot-check MAYHEM Power passive evaluation across canonical DrawPile compositions.

Mirrors ApplyMayhemTickValue (EffectSynergy.cs) to validate the v0.7.3 delta
calculation. Composes synthetic piles with known static values and prints
the resulting MAYHEM score adjustment.

Used as a regression check after PowerCatalog["MayhemPower"] or
EstimateCardPower weight changes.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]
POWER_CATALOG = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs"

REMAINING_TURNS_PROXY = 3
CAP = 1200


def lookup_mayhem_baseline() -> int:
    """Pull PowerCatalog['MayhemPower'] dynamically — matches the runtime path."""
    text = POWER_CATALOG.read_text(encoding="utf-8")
    m = re.search(r'\{\s*"MayhemPower"\s*,\s*(-?\d+)\s*\}', text)
    if not m:
        raise SystemExit("MayhemPower not registered in PowerCatalog.cs")
    return int(m.group(1))


# Synthetic SimCard — only the fields EstimateCardPower reads.
class FakeCard:
    def __init__(self, *, is_curse_or_status=False, is_attack=False,
                 total_damage=0, block=0, draw_count=0, energy_gain=0,
                 power_apps=None, cost=1):
        self.IsCurseOrStatus = is_curse_or_status
        self.IsAttack = is_attack
        self.TotalDamage = total_damage
        self.Block = block
        self.DrawCount = draw_count
        self.EnergyGain = energy_gain
        self.PowerApps = power_apps or []
        self.Cost = cost


def estimate_card_power(c: FakeCard, free_use: bool) -> int:
    """Python mirror of EffectSynergy.EstimateCardPower."""
    if c.IsCurseOrStatus:
        return W.CURSE_FREE if free_use else W.CURSE_INHAND
    v = 0
    if c.IsAttack:
        v += c.TotalDamage * (W.DAMAGE_FREE if free_use else W.DAMAGE_INHAND)
    if c.Block > 0:
        v += c.Block * (W.BLOCK_FREE if free_use else W.BLOCK_INHAND)
    v += c.DrawCount * W.DRAW
    v += c.EnergyGain * (W.ENERGY_FREE if free_use else W.ENERGY_INHAND)
    # PowerApps mirroring uses pre-resolved values for predictability — real
    # tests should reach into PowerCatalog, but the scenarios here use plain
    # attack/block cards for clarity.
    for _name, contrib in c.PowerApps:
        v += contrib // (W.POWER_DIVISOR_FREE if free_use else W.POWER_DIVISOR_INHAND)
    if not free_use:
        if c.Cost == 0: v += W.COST_0_BONUS
        elif c.Cost == 1: v += W.COST_1_BONUS
        elif c.Cost >= 3: v += W.COST_3_PLUS_PENALTY
    return max(0, v)


def apply_mayhem_tick(pile: list[FakeCard], baked: int) -> tuple[int, int, int]:
    """Returns (mean, tick_estimate, delta) — matches ApplyMayhemTickValue."""
    if not pile:
        return 0, 0, 80  # empty pile baseline matches handler
    total = sum(estimate_card_power(c, free_use=True) for c in pile)
    mean = total // len(pile)
    tick = mean * REMAINING_TURNS_PROXY
    delta = tick - baked
    if delta > CAP: delta = CAP
    if delta < -baked: delta = -baked
    return mean, tick, delta


# Canonical pile compositions covering the realistic value range.
SCENARIOS: list[tuple[str, list[FakeCard]]] = [
    ("empty", []),
    ("starter strikes/defends (Strike=6dmg, Defend=5blk × 5 each)",
        [FakeCard(is_attack=True, total_damage=6, cost=1) for _ in range(5)]
        + [FakeCard(block=5, cost=1) for _ in range(5)]),
    ("mid (mix Strike+Wild Strike+Bash, mean ~250)",
        [FakeCard(is_attack=True, total_damage=6, cost=1) for _ in range(3)]
        + [FakeCard(is_attack=True, total_damage=12, cost=1) for _ in range(3)]
        + [FakeCard(is_attack=True, total_damage=8, cost=2) for _ in range(2)]),
    ("strong (Bludgeon-class hitters, mean ~400)",
        [FakeCard(is_attack=True, total_damage=18, cost=2) for _ in range(3)]
        + [FakeCard(is_attack=True, total_damage=12, cost=1) for _ in range(3)]
        + [FakeCard(block=10, cost=1) for _ in range(2)]),
    ("curse-polluted (3 curses + 4 strikes)",
        [FakeCard(is_curse_or_status=True) for _ in range(3)]
        + [FakeCard(is_attack=True, total_damage=6, cost=1) for _ in range(4)]),
    ("all curses (worst case)",
        [FakeCard(is_curse_or_status=True) for _ in range(5)]),
]


def main() -> None:
    baked = lookup_mayhem_baseline()
    print(f"PowerCatalog['MayhemPower'] baseline = {baked}")
    print(f"RemainingTurnsProxy = {REMAINING_TURNS_PROXY}, Cap = {CAP}\n")

    headers = ["scenario", "n", "mean", f"tick*{REMAINING_TURNS_PROXY}", "delta", "verdict"]
    rows = []
    for label, pile in SCENARIOS:
        mean, tick, delta = apply_mayhem_tick(pile, baked)
        if not pile:
            verdict = "empty fallback"
        elif delta >= 500:
            verdict = "strong"
        elif delta >= 0:
            verdict = "ok"
        elif delta >= -baked // 2:
            verdict = "weak"
        else:
            verdict = "skip - keep in hand"
        rows.append([label, str(len(pile)), str(mean), str(tick), f"{delta:+d}", verdict])

    widths = [max(len(h), max(len(r[i]) for r in rows)) for i, h in enumerate(headers)]
    fmt = "  ".join(f"{{:<{w}}}" for w in widths)
    print(fmt.format(*headers))
    print("  ".join("-" * w for w in widths))
    for r in rows:
        print(fmt.format(*r))

    print()
    print("delta = (DrawPile mean * RemainingTurnsProxy) - baked")
    print("Sign tells the planner whether MAYHEM is worth playing now.")


if __name__ == "__main__":
    main()
