"""Spot-check AGGRESSION Power passive evaluation across DiscardPile compositions.

Mirrors the CARD.AGGRESSION case in EffectSynergy.ApplyCardReturn (v0.7.4
MAYHEM-aligned). Composes synthetic discard piles with known Attack values
and prints the resulting score delta.

Used as a regression check after PowerCatalog["AggressionPower"] or
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
UPGRADE_FACTOR = 1.3
CAP = 1200


def lookup_aggression_baseline() -> int:
    text = POWER_CATALOG.read_text(encoding="utf-8")
    m = re.search(r'\{\s*"AggressionPower"\s*,\s*(-?\d+)\s*\}', text)
    if not m:
        raise SystemExit("AggressionPower not registered in PowerCatalog.cs")
    return int(m.group(1))


class FakeCard:
    def __init__(self, *, is_attack=False, is_curse_or_status=False,
                 total_damage=0, block=0, cost=1):
        self.IsAttack = is_attack
        self.IsCurseOrStatus = is_curse_or_status
        self.TotalDamage = total_damage
        self.Block = block
        self.DrawCount = 0
        self.EnergyGain = 0
        self.PowerApps = []
        self.Cost = cost


def estimate_card_power(c: FakeCard, free_use: bool) -> int:
    if c.IsCurseOrStatus:
        return W.CURSE_FREE if free_use else W.CURSE_INHAND
    v = 0
    if c.IsAttack:
        v += c.TotalDamage * (W.DAMAGE_FREE if free_use else W.DAMAGE_INHAND)
    if c.Block > 0:
        v += c.Block * (W.BLOCK_FREE if free_use else W.BLOCK_INHAND)
    if not free_use:
        if c.Cost == 0: v += W.COST_0_BONUS
        elif c.Cost == 1: v += W.COST_1_BONUS
        elif c.Cost >= 3: v += W.COST_3_PLUS_PENALTY
    return max(0, v)


def apply_aggression_tick(discard: list[FakeCard], baked: int) -> tuple[int, int, int, int]:
    attacks = [c for c in discard if c.IsAttack and not c.IsCurseOrStatus]
    if not attacks:
        return 0, 0, 0, 80
    total = sum(estimate_card_power(c, free_use=False) for c in attacks)
    mean = total // len(attacks)
    tick = int(mean * UPGRADE_FACTOR * REMAINING_TURNS_PROXY)
    delta = tick - baked
    if delta > CAP: delta = CAP
    if delta < -baked: delta = -baked
    return len(attacks), mean, tick, delta


SCENARIOS: list[tuple[str, list[FakeCard]]] = [
    ("empty discard", []),
    ("no attacks (only blocks)",
        [FakeCard(block=5, cost=1) for _ in range(4)]),
    ("starter Strikes (Strike=6dmg × 5)",
        [FakeCard(is_attack=True, total_damage=6, cost=1) for _ in range(5)]),
    ("mid attacks (mix 6/8/12 dmg)",
        [FakeCard(is_attack=True, total_damage=6, cost=1) for _ in range(2)]
        + [FakeCard(is_attack=True, total_damage=8, cost=2) for _ in range(2)]
        + [FakeCard(is_attack=True, total_damage=12, cost=1) for _ in range(2)]),
    ("strong attacks (Bludgeon-class 18dmg × 4)",
        [FakeCard(is_attack=True, total_damage=18, cost=2) for _ in range(4)]),
    ("Ironclad finisher mix (Reaper/Feed/Bludgeon)",
        [FakeCard(is_attack=True, total_damage=25, cost=2),
         FakeCard(is_attack=True, total_damage=20, cost=2),
         FakeCard(is_attack=True, total_damage=15, cost=1)]),
]


def main() -> None:
    baked = lookup_aggression_baseline()
    print(f"PowerCatalog['AggressionPower'] baseline = {baked}")
    print(f"RemainingTurnsProxy = {REMAINING_TURNS_PROXY}, UpgradeFactor = {UPGRADE_FACTOR}, Cap = {CAP}\n")

    headers = ["scenario", "atks", "mean", f"tick*1.3*{REMAINING_TURNS_PROXY}", "delta", "verdict"]
    rows = []
    for label, discard in SCENARIOS:
        atks, mean, tick, delta = apply_aggression_tick(discard, baked)
        if atks == 0:
            verdict = "noAttacks baseline"
        elif delta >= 500:
            verdict = "strong"
        elif delta >= 0:
            verdict = "ok"
        elif delta >= -baked // 2:
            verdict = "weak"
        else:
            verdict = "skip - hold"
        rows.append([label, str(atks), str(mean), str(tick), f"{delta:+d}", verdict])

    widths = [max(len(h), max(len(r[i]) for r in rows)) for i, h in enumerate(headers)]
    fmt = "  ".join(f"{{:<{w}}}" for w in widths)
    print(fmt.format(*headers))
    print("  ".join("-" * w for w in widths))
    for r in rows:
        print(fmt.format(*r))

    print()
    print("delta = (mean * UpgradeFactor * RemainingTurnsProxy) - baked")
    print("UpgradeFactor 1.3 approximates the temporary upgrade on recalled Attack.")


if __name__ == "__main__":
    main()
