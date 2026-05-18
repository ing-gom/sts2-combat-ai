"""Spot-check v0.7.13 -- ReaperForm Doom + MAYHEM/STAMPEDE auto-trigger.

ReaperForm: each attack hit adds N stacks of Doom (where N = ReaperForm
stacks). Doom ticks like Poison.
MAYHEM/STAMPEDE: at turn start, auto-play a card. Synthetic average draw
attack damage to weakest enemy. (AGGRESSION skipped - already credited.)
"""


def reaper_form_apply(*, reaper_stacks: int, hits: int) -> int:
    """ApplyCardPlay's Doom accumulation per attack."""
    if reaper_stacks <= 0: return 0
    return reaper_stacks * max(1, hits)


def doom_tick(*, doom_amount: int, enemy_hp: int) -> int:
    """AdvanceTurn's Doom-as-DoT tick (mirrors Poison/Constrict)."""
    if doom_amount <= 0: return enemy_hp
    return max(0, enemy_hp - doom_amount)


def mayhem_stampede_trigger(*, mayhem: int, stampede: int, avg_attack_dmg: int,
                            enemy_hp: int, enemy_block: int) -> tuple[int, int]:
    """At turn start, MAYHEM/STAMPEDE auto-play damage to weakest enemy."""
    total = mayhem + stampede
    if total == 0 or avg_attack_dmg == 0:
        return enemy_hp, enemy_block
    dmg = avg_attack_dmg * total
    blk_after = max(0, enemy_block - dmg)
    hp_after = max(0, enemy_hp - max(0, dmg - enemy_block))
    return hp_after, blk_after


def main() -> None:
    print("=== ReaperForm Doom ===")
    print(f"{'reaper':<6} {'hits':<5} {'doom added':>10}  {'enemy HP 50, after tick':>25}")
    for reap, hits in [(0, 1), (1, 1), (1, 3), (2, 1), (3, 4)]:
        added = reaper_form_apply(reaper_stacks=reap, hits=hits)
        # 3 turns of Doom tick
        hp = 50
        for _ in range(3):
            hp = doom_tick(doom_amount=added, enemy_hp=hp)
        print(f"{reap:<6} {hits:<5} {added:>10}   {hp:>15} (3 ticks)")

    print()
    print("=== MAYHEM/STAMPEDE auto-trigger ===")
    print(f"{'mayhem':<6} {'stamp':<5} {'avgDmg':>6} {'eHp':>4} {'eBlk':>5}  {'newHp':>5} {'newBlk':>6}")
    cases = [
        (0, 0, 12, 40, 0),
        (1, 0, 12, 40, 0),
        (1, 1, 12, 40, 0),
        (2, 0, 12, 40, 0),
        (1, 0, 12, 40, 15),  # with enemy block
        (3, 0, 30, 30, 0),   # over-damage
    ]
    for m, s, dmg, hp, blk in cases:
        new_hp, new_blk = mayhem_stampede_trigger(
            mayhem=m, stampede=s, avg_attack_dmg=dmg,
            enemy_hp=hp, enemy_block=blk)
        print(f"{m:<6} {s:<5} {dmg:>6} {hp:>4} {blk:>5}  {new_hp:>5} {new_blk:>6}")


if __name__ == "__main__":
    main()
