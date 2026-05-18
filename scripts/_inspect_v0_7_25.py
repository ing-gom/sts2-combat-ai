"""Spot-check v0.7.25 -- Weak coverage of non-attack-intent + dynamic turn cap."""


WEAK_PER_HP = 30
HARD_CAP = 4
BASELINE_DMG = 8


def weak_savings(*, weak_stacks: int, enemies: list[dict],
                 remaining_turns: int) -> int:
    if weak_stacks <= 0: return 0
    turn_cap = min(weak_stacks, min(remaining_turns, HARD_CAP))
    if turn_cap <= 0: return 0

    hp_saved = 0
    for e in enemies:
        if not e["alive"] or e["inert"]: continue
        current_attacks = e["intent"] in ("attack", "deathblow_with_dmg")
        future_attacks = e["intent"] in ("buff", "heal", "defend",
                                          "summon", "debuff", "status")
        if not current_attacks and not future_attacks: continue

        if current_attacks:
            per_hit = e["dmg"] + max(0, e.get("str", 0))
            hits = max(1, e.get("hits", 1))
        else:
            per_hit = BASELINE_DMG + max(0, e.get("str", 0))
            hits = 1

        savings = per_hit - int(per_hit * 0.75)
        if savings <= 0: continue
        turn_savings = savings * hits
        eff_turns = turn_cap
        if not current_attacks:
            eff_turns = max(0, eff_turns - 1)
        if eff_turns <= 0: continue

        contribution = turn_savings * eff_turns
        if not current_attacks: contribution //= 2
        hp_saved += contribution

    return hp_saved * WEAK_PER_HP


def main() -> None:
    print("=== v0.7.25: Weak scoring coverage ===\n")

    scenarios = [
        # (label, weak_stacks, enemies, remaining_turns)
        ("multi-hit attacker (8x4, 5-turn fight, Weak 2)",
         2, [{"alive": True, "inert": False, "intent": "attack",
              "dmg": 8, "hits": 4}], 5),
        ("multi-hit attacker, high-stack Weak 4",
         4, [{"alive": True, "inert": False, "intent": "attack",
              "dmg": 8, "hits": 4}], 5),
        ("v0.7.24-baseline: same as above (cap was 2)",
         2, [{"alive": True, "inert": False, "intent": "attack",
              "dmg": 8, "hits": 4}], 5),
        ("BIG single-hit attacker (40x1, Weak 2)",
         2, [{"alive": True, "inert": False, "intent": "attack",
              "dmg": 40, "hits": 1}], 5),
        ("buffing now (Strength), will attack next turn, Weak 2",
         2, [{"alive": True, "inert": False, "intent": "buff",
              "dmg": 0, "hits": 0}], 5),
        ("buffing now, Weak 1 (lapses fully)",
         1, [{"alive": True, "inert": False, "intent": "buff",
              "dmg": 0, "hits": 0}], 5),
        ("defending now (still has future attacks), Weak 3",
         3, [{"alive": True, "inert": False, "intent": "defend",
              "dmg": 0, "hits": 0}], 5),
        ("Lethal: 1 turn remaining, Weak 3 (cap kicks in)",
         3, [{"alive": True, "inert": False, "intent": "attack",
              "dmg": 8, "hits": 4}], 1),
        ("inert enemy (stunned): Weak useless",
         2, [{"alive": True, "inert": True, "intent": "attack",
              "dmg": 8, "hits": 4}], 5),
    ]

    for label, stacks, enemies, rem in scenarios:
        score = weak_savings(weak_stacks=stacks, enemies=enemies,
                              remaining_turns=rem)
        print(f"  {label:<58}  -> {score:>4}")

    print("\n=== Key validations ===")
    print("- High-stack Weak now scales beyond 2 turns (cap 4)")
    print("- Buffing enemy: Weak 2 gives PARTIAL value (was 0)")
    print("- Weak 1 on buffing enemy: 0 (correctly lapses)")
    print("- 1-turn-left fight: Weak benefits compressed (cap=1)")


if __name__ == "__main__":
    main()
