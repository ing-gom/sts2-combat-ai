"""Spot-check v0.7.26 -- per-turn Power passive dynamic delta handlers.

Validates that each new handler produces sensible deltas across:
- attack-heavy deck
- exhaust-heavy deck
- skill-heavy deck
- power-heavy deck (Defect)
- mixed
"""


def clamp_delta(tick: int, baked: int, cap: int) -> int:
    delta = tick - baked
    return max(-baked, min(cap, delta))


def dark_embrace(hand_exh: int, deck_exh: int, turns: int) -> dict:
    baked = 500
    cap = 900
    per_draw = 200
    future = hand_exh + min(deck_exh, int(deck_exh * 0.3 * turns))
    tick = future * per_draw
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def vicious(hand_vp: int, deck_vp: int, turns: int) -> dict:
    baked = 400
    cap = 700
    per = 180
    proj = hand_vp + (deck_vp * turns) // 5
    tick = proj * per
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def envenom(hand_atk: int, deck_atk: int, turns: int) -> dict:
    baked = 500
    cap = 800
    per = 60
    proj = hand_atk + (deck_atk * turns) // 3
    tick = proj * per
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def subroutine(hand_pow: int, deck_pow: int) -> dict:
    baked = 500
    cap = 1500
    energy = 500
    proj = hand_pow + (deck_pow * 3) // 4
    tick = proj * energy
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def prep_time(turns: int) -> dict:
    baked = 450
    cap = 600
    tick = int(turns * 4 * 50 * 0.75)
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def tools(turns: int) -> dict:
    baked = 500
    cap = 1200
    tick = turns * 250
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def storm(hand_pow: int, deck_pow: int) -> dict:
    baked = 450
    cap = 700
    per = 90
    proj = hand_pow + (deck_pow * 3) // 4
    tick = proj * per
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def accelerant(hand_pp: int, deck_pp: int, turns: int) -> dict:
    baked = 500
    cap = 800
    per = 120
    proj = hand_pp + (deck_pp * turns) // 5
    tick = proj * per
    return {"tick": tick, "delta": clamp_delta(tick, baked, cap)}


def main() -> None:
    print("=== v0.7.26: per-turn Power passive dynamic deltas ===\n")

    print("DarkEmbrace (Ironclad, exhaust-on-draw)")
    for label, args in [
        ("exhaust-heavy (5 hand, 8 deck, 5 turns)", (5, 8, 5)),
        ("balanced (1 hand, 3 deck, 5 turns)", (1, 3, 5)),
        ("no-exhaust (0, 0, 5)", (0, 0, 5)),
    ]:
        r = dark_embrace(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nVicious (Vuln-applier draw)")
    for label, args in [
        ("Bash-heavy (2 hand VP, 4 deck VP, 5 turns)", (2, 4, 5)),
        ("no-vuln (0, 0, 5)", (0, 0, 5)),
    ]:
        r = vicious(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nEnvenom (Silent, +1 poison per unblocked attack)")
    for label, args in [
        ("attack-heavy (4 hand, 12 deck, 5 turns)", (4, 12, 5)),
        ("balanced (2 hand, 5 deck, 4 turns)", (2, 5, 4)),
        ("skill-heavy (1 hand, 2 deck, 4 turns)", (1, 2, 4)),
    ]:
        r = envenom(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nSubroutine (Defect, +1 energy per Power play)")
    for label, args in [
        ("Power-heavy (4 hand, 6 deck)", (4, 6)),
        ("Power-light (1 hand, 2 deck)", (1, 2)),
        ("no Powers (0, 0)", (0, 0)),
    ]:
        r = subroutine(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nPrepTime (Shared, Vigor 4/turn)")
    for turns in [1, 3, 5, 8]:
        r = prep_time(turns)
        print(f"  turns={turns:>2}                                        tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nToolsOfTheTrade (Silent, draw1/discard1 turn-start)")
    for turns in [1, 3, 5, 8]:
        r = tools(turns)
        print(f"  turns={turns:>2}                                        tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nStorm (Defect, Lightning per Power play)")
    for label, args in [
        ("Power-heavy (4 hand, 6 deck)", (4, 6)),
        ("Power-light (1, 2)", (1, 2)),
    ]:
        r = storm(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\nAccelerant (Silent, +1 Poison per apply)")
    for label, args in [
        ("Poison-heavy (3 hand, 5 deck, 5 turns)", (3, 5, 5)),
        ("no-poison (0, 0, 5)", (0, 0, 5)),
    ]:
        r = accelerant(*args)
        print(f"  {label:<50}  tick={r['tick']:>4}  delta={r['delta']:>+5}")

    print("\n=== Validations ===")
    print("- exhaust-heavy deck: DarkEmbrace shows big +delta")
    print("- no-exhaust deck: DarkEmbrace strips entire baked baseline (-500)")
    print("- Power-heavy Defect deck: Subroutine + Storm both spike")
    print("- All handlers respect baked-floor (can't go below -baked)")


if __name__ == "__main__":
    main()
