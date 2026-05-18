"""Spot-check v0.7.24 -- future attack potential scaling for Vuln."""


VULN_BASE = 500
DPE_PER_POINT = 35
ATTACHED_MULT = 1.0


def vuln_score_curve(stacks: int) -> int:
    if stacks <= 0: return 0
    if stacks == 1: return VULN_BASE
    extra = int(VULN_BASE * 0.7 * (stacks - 1))
    return min(VULN_BASE + extra, VULN_BASE * 4)


def future_atk_mult(*, hand_atk: int, hand_other: int,
                    draw_atk: int, draw_other: int,
                    disc_atk: int, disc_other: int) -> float:
    total = hand_atk + hand_other + draw_atk + draw_other + disc_atk + disc_other
    atk = hand_atk + draw_atk + disc_atk
    if total <= 0: return 1.0
    return min(1.0, (atk / total) / 0.3)


def bash_score(*, target_hp: int, hand_atk: int, hand_other: int,
               draw_atk: int, draw_other: int,
               disc_atk: int, disc_other: int) -> dict:
    dmg = 8
    eff_dmg = min(dmg, target_hp)
    dmg_score = eff_dmg * DPE_PER_POINT

    # Survival ratio (v0.7.23)
    remaining = max(0, target_hp - dmg)
    survival = remaining / target_hp if target_hp > 0 else 1.0
    if 0 < survival < 0.15: survival = 0.15

    # Future atk mult (v0.7.24)
    mult = future_atk_mult(hand_atk=hand_atk, hand_other=hand_other,
                           draw_atk=draw_atk, draw_other=draw_other,
                           disc_atk=disc_atk, disc_other=disc_other)

    vuln_raw = vuln_score_curve(2)
    vuln_after_surv = int(vuln_raw * survival * ATTACHED_MULT)
    vuln_after_mult = int(vuln_after_surv * mult)

    return {
        "survival": round(survival, 2),
        "future_mult": round(mult, 2),
        "vuln_raw": vuln_raw,
        "vuln_surv": vuln_after_surv,
        "vuln_final": vuln_after_mult,
        "total": dmg_score + vuln_after_mult,
    }


def main() -> None:
    print("=== v0.7.24: Vuln 가치가 미래 공격카드 잔량에 따라 스케일링 ===\n")

    scenarios = [
        ("Attack-heavy 덱 (50% atk, 풍부)",
         dict(target_hp=30, hand_atk=3, hand_other=1, draw_atk=8, draw_other=5,
              disc_atk=2, disc_other=2)),
        ("Balanced 덱 (30% atk, 임계)",
         dict(target_hp=30, hand_atk=2, hand_other=3, draw_atk=4, draw_other=8,
              disc_atk=1, disc_other=2)),
        ("Skill-heavy 덱 (15% atk)",
         dict(target_hp=30, hand_atk=1, hand_other=4, draw_atk=2, draw_other=10,
              disc_atk=0, disc_other=3)),
        ("Pure-skill 덱 (0% atk, Vuln 무용)",
         dict(target_hp=30, hand_atk=0, hand_other=4, draw_atk=0, draw_other=12,
              disc_atk=0, disc_other=4)),
        ("Late-game 덱 거의 비어있음 (1/3 atk)",
         dict(target_hp=30, hand_atk=0, hand_other=2, draw_atk=1, draw_other=0,
              disc_atk=0, disc_other=0)),
    ]

    print(f"{'scenario':<48} {'mult':>5} {'vuln_raw':>9} {'vuln_final':>11} {'total':>7}")
    print("-" * 84)
    for label, args in scenarios:
        r = bash_score(**args)
        print(f"{label:<48} {r['future_mult']:>5} {r['vuln_raw']:>9} "
              f"{r['vuln_final']:>11} {r['total']:>7}")

    print("\n=== Vuln scaling check ===")
    print("- Attack-heavy -> 1.0x  (full Vuln value)")
    print("- Skill-heavy  -> 0.5x  (half)")
    print("- Pure-skill   -> 0.0x  (fully blocked = correct)")


if __name__ == "__main__":
    main()
