"""Spot-check v0.7.23 -- survival probability + lethal setup penalty."""


DPE_PER_POINT = 35     # DamagePerPointBonus
ATTACHED_MULT = 1.0    # AttachedDebuffMultiplier (rough)
VULN_BASE = 500        # PowerCatalog.VulnerablePower (enemy debuff)
LETHAL_NONATTACK_PENALTY = -500  # rough estimate; actual from weights


def vuln_score_curve(stacks: int) -> int:
    if stacks <= 0: return 0
    if stacks == 1: return VULN_BASE
    extra = int(VULN_BASE * 0.7 * (stacks - 1))
    cap = VULN_BASE * 4
    return min(VULN_BASE + extra, cap)


def attack_score(*, base_dmg: int, hits: int, target_eff_hp: int,
                 vuln_stacks: int, lethal_this_turn: bool,
                 is_setup_card: bool) -> dict:
    total_dmg = base_dmg * hits
    eff_total = min(total_dmg, target_eff_hp) if target_eff_hp > 0 else total_dmg
    dmg_score = eff_total * DPE_PER_POINT

    # Survival ratio
    if target_eff_hp > 0:
        remaining = max(0, target_eff_hp - total_dmg)
        survival = remaining / target_eff_hp
        if 0 < survival < 0.15: survival = 0.15
    else:
        survival = 1.0

    # Vuln value scaled
    vuln_raw = vuln_score_curve(vuln_stacks) if vuln_stacks > 0 else 0
    vuln_score = int(vuln_raw * survival * ATTACHED_MULT)

    # Lethal setup penalty
    lethal_setup = 0
    if lethal_this_turn and is_setup_card:
        lethal_setup = LETHAL_NONATTACK_PENALTY * 6 // 10

    return {
        "dmg_score": dmg_score,
        "survival_ratio": round(survival, 3),
        "vuln_score": vuln_score,
        "lethal_setup": lethal_setup,
        "total": dmg_score + vuln_score + lethal_setup,
    }


def main() -> None:
    print("=== 사용자 시나리오: Bash(2c 8dmg + Vuln 2) vs Strike(1c 6dmg) ===\n")

    scenarios = [
        ("적 30 HP, 다음 턴 위협 — Vuln 가치 큼",       30, False),
        ("적 18 HP, B(Strike×3=18) 이 kill",            18, False),  # B is lethal
        ("적 18 HP, lethalThisTurn",                    18, True),
        ("적 16 HP, A 도 kill (overkill setup)",        16, True),
        ("적 8 HP, Bash 단독 kill",                     8, True),
    ]

    for label, hp, lethal in scenarios:
        print(f"### {label}")
        print(f"   적 effHp={hp}, lethalThisTurn={lethal}")

        bash = attack_score(base_dmg=8, hits=1, target_eff_hp=hp,
                            vuln_stacks=2, lethal_this_turn=lethal,
                            is_setup_card=True)
        strike = attack_score(base_dmg=6, hits=1, target_eff_hp=hp,
                              vuln_stacks=0, lethal_this_turn=lethal,
                              is_setup_card=False)
        path_b_total = strike["total"] * 3  # 3 strikes approximation
        print(f"   Bash:    dmg_score={bash['dmg_score']:>4}, vuln={bash['vuln_score']:>4} (surv={bash['survival_ratio']}), setup_pen={bash['lethal_setup']:>4} → total={bash['total']:>4}")
        print(f"   Strike:  dmg_score={strike['dmg_score']:>4}                                  → total×3={path_b_total:>4}")
        # Path A is Bash + Strike, but here approximating with score-add
        path_a_total = bash["total"] + strike["total"]
        print(f"   Path A (Bash+Strike) ≈ {path_a_total}, Path B (Strike×3) ≈ {path_b_total}")
        winner = "A" if path_a_total > path_b_total else "B"
        print(f"   → 우선: Path {winner}")
        print()


if __name__ == "__main__":
    main()
