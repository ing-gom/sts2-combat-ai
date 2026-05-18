"""Spot-check the v0.7.10 AdvanceTurn projection.

Mirrors the simplified C# AdvanceTurn (player_hp -= leak, status -1,
energy reset, block reset, new synthetic hand). Verifies the multi-turn
signal across canonical states.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))


def predict_player_dmg(*, enemy_attacks: list[tuple[int, int]], player_block: int,
                       player_eot_block: int, player_vuln: int, player_weak: int,
                       player_intangible: int) -> int:
    """Mirror of EnemyTurnSimulator.PredictPlayerDmg simplified."""
    if player_intangible > 0:
        hits = sum(repeats for _dmg, repeats in enemy_attacks)
        return max(0, hits - player_block - player_eot_block)
    total = 0
    for dmg, repeats in enemy_attacks:
        per_hit = dmg
        if player_weak > 0: per_hit = int(per_hit * 0.75)
        per_total = per_hit * repeats
        if player_vuln > 0: per_total = int(per_total * 1.5)
        total += per_total
    return max(0, total - player_block - player_eot_block)


def advance_turn(*, player_hp: int, player_block: int, player_eot_block: int,
                 player_vuln: int, player_weak: int, player_frail: int,
                 player_intangible: int, enemy_attacks: list[tuple[int, int]]):
    leak = predict_player_dmg(
        enemy_attacks=enemy_attacks,
        player_block=player_block,
        player_eot_block=player_eot_block,
        player_vuln=player_vuln,
        player_weak=player_weak,
        player_intangible=player_intangible,
    )
    new_hp = max(0, player_hp - leak)
    return {
        "leak": leak,
        "new_hp": new_hp,
        "new_block": 0,
        "new_energy": 3,
        "new_vuln": max(0, player_vuln - 1),
        "new_weak": max(0, player_weak - 1),
        "new_frail": max(0, player_frail - 1),
        "new_intangible": max(0, player_intangible - 1),
    }


SCENARIOS = [
    ("safe: 70 HP, 15 block, 1 enemy 8x2",
        dict(player_hp=70, player_block=15, player_eot_block=0,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(8, 2)])),
    ("eot block bonus: Metallicize 4 covers small hit",
        dict(player_hp=70, player_block=0, player_eot_block=4,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(5, 1)])),
    ("vuln incoming: dmg x1.5",
        dict(player_hp=60, player_block=0, player_eot_block=0,
             player_vuln=2, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(10, 1)])),
    ("weak enemy, big hit: dmg x0.75",
        dict(player_hp=60, player_block=0, player_eot_block=0,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(20, 1)])),
    ("intangible: cap each hit at 1",
        dict(player_hp=40, player_block=0, player_eot_block=0,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=1, enemy_attacks=[(50, 3), (30, 2)])),
    ("fatal: 12 HP, no block, big boss attack",
        dict(player_hp=12, player_block=0, player_eot_block=0,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(25, 1)])),
    ("multi-enemy: 3 attackers",
        dict(player_hp=80, player_block=0, player_eot_block=0,
             player_vuln=0, player_weak=0, player_frail=0,
             player_intangible=0, enemy_attacks=[(6, 1), (4, 2), (12, 1)])),
]


def main() -> None:
    print(f"{'scenario':<55}  {'leak':>4}  {'hp->':>5}  status decrements")
    print("-" * 100)
    for label, args in SCENARIOS:
        r = advance_turn(**args)
        old = args
        decs = []
        if old['player_vuln'] != r['new_vuln']: decs.append(f"vuln {old['player_vuln']}->{r['new_vuln']}")
        if old['player_weak'] != r['new_weak']: decs.append(f"weak {old['player_weak']}->{r['new_weak']}")
        if old['player_frail'] != r['new_frail']: decs.append(f"frail {old['player_frail']}->{r['new_frail']}")
        if old['player_intangible'] != r['new_intangible']: decs.append(f"intang {old['player_intangible']}->{r['new_intangible']}")
        print(f"{label:<55}  {r['leak']:>4}  {old['player_hp']:>2}->{r['new_hp']:>2}  {', '.join(decs) or '-'}")

    print()
    print("Block, energy always reset to 0/3 (+ EOT bonus folded into leak).")


if __name__ == "__main__":
    main()
