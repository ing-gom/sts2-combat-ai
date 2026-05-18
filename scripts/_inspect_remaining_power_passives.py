"""Spot-check the v0.7.5 batch of DrawPile/Hand/Pool-aware Power passive handlers.

Six handlers covered:
  • STAMPEDE   (B) — DrawPile Attack mean × turns
  • NOSTALGIA  (D) — Hand Attack/Skill mean × turns × RetainDiscount
  • STRATAGEM  (C) — DiscardPile mean × reshuffles
  • CALAMITY   (D) — PoolMeans.attack.mean × expected chains
  • HELLRAISER (D) — Strike-count × per-Strike bonus
  • JUGGLING   (D) — Hand Attack mean × turns × hit-rate

Each section prints a representative scenario and the resulting delta so
calibration drift can be caught after PowerCatalog or weight changes.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]
POWER_CATALOG = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs"
POOL_MEANS = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Data" / "pool_means.json"

# Tunables — match the C# constants 1:1.
REMAINING_TURNS_PROXY = 3
RESHUFFLE_PROXY = 2
EXPECTED_ATTACK_CHAINS = 3
PER_STRIKE_BONUS = 90
RETAIN_DISCOUNT = 0.4
JUGGLING_HIT_RATE = 0.4
UPGRADE_FACTOR = 1.3


def lookup(power_name: str) -> int:
    text = POWER_CATALOG.read_text(encoding="utf-8")
    m = re.search(rf'\{{\s*"{re.escape(power_name)}"\s*,\s*(-?\d+)\s*\}}', text)
    if not m:
        raise SystemExit(f"{power_name} not registered in PowerCatalog.cs")
    return int(m.group(1))


class FakeCard:
    def __init__(self, *, id="", is_attack=False, is_skill=False,
                 is_curse_or_status=False, total_damage=0, block=0, cost=1):
        self.Id = id
        self.IsAttack = is_attack
        self.IsSkill = is_skill
        self.IsCurseOrStatus = is_curse_or_status
        self.TotalDamage = total_damage
        self.Block = block
        self.DrawCount = 0
        self.EnergyGain = 0
        self.PowerApps = []
        self.Cost = cost


def estimate(c: FakeCard, free_use: bool) -> int:
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


def clamp_delta(tick: int, baked: int, cap: int) -> int:
    delta = tick - baked
    if delta > cap: delta = cap
    if delta < -baked: delta = -baked
    return delta


# Shared fixtures.
strike     = lambda: FakeCard(id="CARD.STRIKE",       is_attack=True, total_damage=6, cost=1)
defend     = lambda: FakeCard(id="CARD.DEFEND",       is_skill=True,  block=5,        cost=1)
heavy_atk  = lambda: FakeCard(id="CARD.BLUDGEON",     is_attack=True, total_damage=18, cost=2)
mid_atk    = lambda: FakeCard(id="CARD.WILD_STRIKE",  is_attack=True, total_damage=12, cost=1)
strong_sk  = lambda: FakeCard(id="CARD.IMPERVIOUS",   is_skill=True,  block=30, cost=2)
curse      = lambda: FakeCard(id="CARD.ASCENDERS_BANE", is_curse_or_status=True)


def section(title: str) -> None:
    print(f"\n=== {title} ===")


def section_stampede() -> None:
    baked = lookup("StampedePower")
    cap = 1200
    section(f"STAMPEDE  (baked={baked}, cap={cap})")
    for label, pile in [
        ("empty / no attacks", [defend() for _ in range(3)]),
        ("starter Strikes ×5", [strike() for _ in range(5)]),
        ("mid attack mix",     [strike()]*2 + [mid_atk()]*3 + [heavy_atk()]),
        ("strong attacks ×4",  [heavy_atk() for _ in range(4)]),
    ]:
        attacks = [c for c in pile if c.IsAttack]
        if not attacks:
            print(f"  {label:<22} -> +80 (noAttacks baseline)")
            continue
        mean = sum(estimate(c, free_use=True) for c in attacks) // len(attacks)
        tick = mean * REMAINING_TURNS_PROXY
        delta = clamp_delta(tick, baked, cap)
        print(f"  {label:<22} n={len(attacks)}  mean={mean:>3}  tick={tick:>4}  delta={delta:+d}")


def section_nostalgia() -> None:
    baked = lookup("NostalgiaPower")
    cap = 800
    section(f"NOSTALGIA  (baked={baked}, cap={cap})")
    for label, hand in [
        ("no attack/skill (Powers only)", []),
        ("Strike + Defend × 3 each",      [strike()]*3 + [defend()]*3),
        ("strong attack/skill mix",       [heavy_atk()]*2 + [strong_sk()]*2),
    ]:
        cands = [c for c in hand if not c.IsCurseOrStatus and (c.IsAttack or c.IsSkill)]
        if not cands:
            print(f"  {label:<34} -> +50 (noTargets)")
            continue
        mean = sum(estimate(c, free_use=False) for c in cands) // len(cands)
        tick = int(mean * RETAIN_DISCOUNT * REMAINING_TURNS_PROXY)
        delta = clamp_delta(tick, baked, cap)
        print(f"  {label:<34} n={len(cands)}  mean={mean:>3}  tick={tick:>4}  delta={delta:+d}")


def section_stratagem() -> None:
    baked = lookup("StratagemPower")
    cap = 800
    section(f"STRATAGEM  (baked={baked}, cap={cap})")
    for label, discard in [
        ("empty discard",        []),
        ("mixed (4 cards)",      [strike(), defend(), heavy_atk(), strong_sk()]),
        ("curse-polluted",       [curse()]*2 + [strike()]*2),
        ("strong (6 mixed)",     [heavy_atk()]*3 + [strong_sk()]*3),
    ]:
        if not discard:
            print(f"  {label:<22} -> +80 (emptyDiscard)")
            continue
        mean = sum(estimate(c, free_use=False) for c in discard) // len(discard)
        tick = mean * RESHUFFLE_PROXY
        delta = clamp_delta(tick, baked, cap)
        print(f"  {label:<22} n={len(discard)}  mean={mean:>3}  tick={tick:>4}  delta={delta:+d}")


def section_calamity() -> None:
    baked = lookup("CalamityPower")
    cap = 1500
    section(f"CALAMITY  (baked={baked}, cap={cap})")
    pool_data = json.loads(POOL_MEANS.read_text(encoding="utf-8"))["characters"]
    for ch in sorted(pool_data):
        atk_pool = pool_data[ch].get("attack", {})
        mean = atk_pool.get("mean", 0)
        n = atk_pool.get("n", 0)
        if n == 0:
            print(f"  {ch:<11} -> flat fallback (pool empty)")
            continue
        tick = mean * EXPECTED_ATTACK_CHAINS
        delta = clamp_delta(tick, baked, cap)
        print(f"  {ch:<11} attackPool.mean={mean:>4} (n={n:>3})  tick={tick:>4}  delta={delta:+d}")


def section_hellraiser() -> None:
    baked = lookup("HellraiserPower")
    cap = 1000
    section(f"HELLRAISER  (baked={baked}, cap={cap})")
    for label, strikes in [
        ("Strike-less deck", 0),
        ("3 Strikes",        3),
        ("6 Strikes",        6),
        ("12 Strikes (heavy)", 12),
    ]:
        if strikes == 0:
            print(f"  {label:<24} -> -{baked} (noStrikes - strip baked)")
            continue
        tick = strikes * PER_STRIKE_BONUS
        delta = clamp_delta(tick, baked, cap)
        print(f"  {label:<24} count={strikes:>2}  tick={tick:>4}  delta={delta:+d}")


def section_juggling() -> None:
    baked = lookup("JugglingPower")
    cap = 800
    section(f"JUGGLING  (baked={baked}, cap={cap})")
    for label, attacks in [
        ("no attacks in hand", []),
        ("3 Strikes",          [strike()]*3),
        ("3 mid attacks",      [mid_atk()]*3),
        ("3 heavy attacks",    [heavy_atk()]*3),
    ]:
        if not attacks:
            print(f"  {label:<24} -> +40 (noAttacks)")
            continue
        mean = sum(estimate(c, free_use=False) for c in attacks) // len(attacks)
        tick = int(mean * REMAINING_TURNS_PROXY * JUGGLING_HIT_RATE)
        delta = clamp_delta(tick, baked, cap)
        print(f"  {label:<24} n={len(attacks)}  mean={mean:>3}  tick={tick:>4}  delta={delta:+d}")


def main() -> None:
    print(f"RemainingTurnsProxy={REMAINING_TURNS_PROXY}  ReshuffleProxy={RESHUFFLE_PROXY}  "
          f"EAC={EXPECTED_ATTACK_CHAINS}  RetainDiscount={RETAIN_DISCOUNT}  "
          f"HitRate={JUGGLING_HIT_RATE}  StrikeBonus={PER_STRIKE_BONUS}")
    section_stampede()
    section_nostalgia()
    section_stratagem()
    section_calamity()
    section_hellraiser()
    section_juggling()


if __name__ == "__main__":
    main()
