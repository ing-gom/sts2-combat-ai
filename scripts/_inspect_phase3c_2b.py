"""Spot-check Phase 3c + 2b — skeleton split-fire + player power passives.

3c: enemy damage absorbed by allies before reaching player.
2b: DemonForm / Regen / Barricade per-turn passives applied in AdvanceTurn.
"""

def compute_ally_absorption(raw_leak: int, alive_allies: int, total_ally_hp: int) -> int:
    if raw_leak <= 0 or alive_allies == 0: return 0
    absorption = alive_allies / (1.0 + alive_allies)
    pool = int(raw_leak * absorption)
    return min(pool, total_ally_hp)


def advance_turn(*, player_hp: int, player_block: int, player_eot_block: int,
                 player_str: int, enemy_attacks: list[tuple[int, int]],
                 alive_allies: int, total_ally_hp: int,
                 demon_form: int = 0, regen: int = 0, barricade: bool = False) -> dict:
    # Raw leak (post-block, pre-ally-absorption).
    enemy_total = sum(d * r for d, r in enemy_attacks)
    raw_leak = max(0, enemy_total - player_block - player_eot_block)
    ally_absorbed = compute_ally_absorption(raw_leak, alive_allies, total_ally_hp)
    player_leak = raw_leak - ally_absorbed
    new_hp_pre_passives = max(0, player_hp - player_leak)
    # Passives
    new_hp = new_hp_pre_passives + regen
    new_str = player_str + demon_form
    new_block = player_block if barricade else 0
    return {
        "raw_leak": raw_leak, "ally_absorbed": ally_absorbed,
        "player_leak": player_leak, "new_hp": new_hp,
        "new_str": new_str, "new_block": new_block,
    }


SCENARIOS = [
    # Phase 3c — split-fire
    ("3c: 1 skeleton (HP 30), boss 20 dmg",
        dict(player_hp=60, player_block=0, player_eot_block=0, player_str=0,
             enemy_attacks=[(20, 1)], alive_allies=1, total_ally_hp=30)),
    ("3c: 2 skeletons (HP 60), boss 30 dmg",
        dict(player_hp=60, player_block=0, player_eot_block=0, player_str=0,
             enemy_attacks=[(30, 1)], alive_allies=2, total_ally_hp=60)),
    ("3c: 3 skeletons (HP 90), big hit 50",
        dict(player_hp=60, player_block=0, player_eot_block=0, player_str=0,
             enemy_attacks=[(50, 1)], alive_allies=3, total_ally_hp=90)),
    ("3c: 1 skeleton, ally HP only 5 (overflow back)",
        dict(player_hp=40, player_block=0, player_eot_block=0, player_str=0,
             enemy_attacks=[(40, 1)], alive_allies=1, total_ally_hp=5)),

    # Phase 2b — player powers
    ("2b: DemonForm 2, no enemies",
        dict(player_hp=60, player_block=0, player_eot_block=0, player_str=5,
             enemy_attacks=[], alive_allies=0, total_ally_hp=0,
             demon_form=2)),
    ("2b: Regen 4 after taking 10 damage",
        dict(player_hp=40, player_block=0, player_eot_block=0, player_str=0,
             enemy_attacks=[(10, 1)], alive_allies=0, total_ally_hp=0,
             regen=4)),
    ("2b: Barricade preserves block (15)",
        dict(player_hp=60, player_block=15, player_eot_block=0, player_str=0,
             enemy_attacks=[(8, 1)], alive_allies=0, total_ally_hp=0,
             barricade=True)),
]


def main() -> None:
    print(f"{'scenario':<55}  {'rawLeak':>7}  {'absorbed':>8}  {'plyrLeak':>8}  {'newHp':>5}  {'str':>3}  {'blk':>3}")
    print("-" * 110)
    for label, args in SCENARIOS:
        r = advance_turn(**args)
        print(f"{label:<55}  {r['raw_leak']:>7}  {r['ally_absorbed']:>8}  {r['player_leak']:>8}  {r['new_hp']:>5}  {r['new_str']:>3}  {r['new_block']:>3}")


if __name__ == "__main__":
    main()
