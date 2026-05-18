"""Spot-check v0.7.21 -- combat length refinements + CORRUPTION + DOOM self risk."""


# Task 1: RemainingTurnsEstimator refinements
def estimate_turns(*, enemy_hps_blocks_vuln: list[tuple[int, int, bool]],
                    hand_atk_damage: int, strength: int,
                    ally_damage: int, dot_per_turn: int,
                    demon_form: int = 0, player_weak: int = 0):
    eff_hp = sum(hp + block // 2 for hp, block, _ in enemy_hps_blocks_vuln if hp > 0)
    if eff_hp <= 0: return 1
    # playerDpt
    str_proj = max(0, strength) + demon_form
    base = hand_atk_damage // 2 + str_proj * 2 + ally_damage
    alive = sum(1 for hp, _, _ in enemy_hps_blocks_vuln if hp > 0)
    vuln_count = sum(1 for hp, _, v in enemy_hps_blocks_vuln if hp > 0 and v)
    if alive > 0 and vuln_count > 0:
        base = int(base * (1.0 + 0.5 * vuln_count / alive))
    if player_weak > 0:
        base = int(base * 0.75)
    total_dpt = base + dot_per_turn
    if total_dpt <= 0: return 3
    est = eff_hp // total_dpt
    return max(1, min(10, est))


# Task 3: DoomSelf risk
def doom_self_risk(*, current_doom: int, doom_added: int, player_hp: int, turns: int):
    new_doom = current_doom + doom_added
    if new_doom <= 0: return 0
    proj_loss = new_doom * turns
    if proj_loss < player_hp / 2: return 0
    penalty = -proj_loss * 5
    return max(-500, penalty)


def main() -> None:
    print("=== Task 1: RemainingTurnsEstimator refinements ===\n")
    cases = [
        ("boss 250hp, no buffs",
            dict(enemy_hps_blocks_vuln=[(250, 0, False)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=0)),
        ("boss 250 + 20 block (block × 0.5)",
            dict(enemy_hps_blocks_vuln=[(250, 20, False)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=0)),
        ("boss 250 + Vuln (×1.5 dmg)",
            dict(enemy_hps_blocks_vuln=[(250, 0, True)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=0)),
        ("boss 250 + Poison 10/turn (DoT)",
            dict(enemy_hps_blocks_vuln=[(250, 0, False)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=10)),
        ("boss 250 + DemonForm 2 (str grow)",
            dict(enemy_hps_blocks_vuln=[(250, 0, False)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=0, demon_form=2)),
        ("boss 250 + player Weak (×0.75)",
            dict(enemy_hps_blocks_vuln=[(250, 0, False)], hand_atk_damage=20,
                 strength=0, ally_damage=0, dot_per_turn=0, player_weak=1)),
        ("composite: Vuln + Poison + Str 4",
            dict(enemy_hps_blocks_vuln=[(250, 0, True)], hand_atk_damage=20,
                 strength=4, ally_damage=0, dot_per_turn=10)),
    ]
    print(f"{'scenario':<55}  {'turns':>5}")
    for label, args in cases:
        print(f"{label:<55}  {estimate_turns(**args):>5}")

    print()
    print("=== Task 3: DoomSelf risk ===\n")
    cases3 = [
        ("low Doom + 1 added, HP 70", 0, 1, 70, 5),
        ("Doom 3 + 2 added, HP 70 (proj 5×5=25)", 3, 2, 70, 5),
        ("Doom 5 + 2 added, HP 40 (proj 7×5=35)", 5, 2, 40, 5),
        ("Doom 7 + 3 added, HP 30 (proj 10×3=30 = HP!)", 7, 3, 30, 3),
        ("Doom 9 + 2 added, HP 20 (cap penalty)", 9, 2, 20, 4),
    ]
    for label, cd, da, hp, t in cases3:
        print(f"  {label:<48} penalty={doom_self_risk(current_doom=cd, doom_added=da, player_hp=hp, turns=t):>+5}")


if __name__ == "__main__":
    main()
