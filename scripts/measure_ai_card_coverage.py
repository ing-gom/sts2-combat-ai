"""
Measure how much of the STS2 card pool the combat AI evaluates with explicit
rules versus relying on generic fallbacks.

Reads four static inputs:
  - scripts/cards_catalog.json (master, from Sts2CardAdvisor)
  - Sts2CombatAICode/Core/Data/card_triggers.json (embedded extract)
  - Sts2CombatAICode/Core/Planner/PowerCatalog.cs (explicit SelfBuff/EnemyDebuff)
  - Sts2CombatAICode/Core/Planner/CardOverrideCatalog.cs (sparse hand-tuned bonuses)

Reports 8 coverage metrics in a Markdown table plus per-character / per-build
breakdown plus the first N "uncovered" card IDs.

Run from repo root (Sts2CombatAI/):
    python scripts/measure_ai_card_coverage.py
    python scripts/measure_ai_card_coverage.py --out docs/ai_card_coverage.md
    python scripts/measure_ai_card_coverage.py --catalog ../scripts/cards_catalog.json
"""

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path


POWER_NAME_RE = re.compile(r'"([A-Z][A-Za-z0-9]+Power)"')
OVERRIDE_ID_RE = re.compile(r'"(CARD\.[A-Z0-9_]+)"')
TIER_RE = re.compile(r'\{\s*"([A-Z][A-Za-z0-9]+Power)"\s*,\s*SequencingTier\.(\w+)\s*\}')

PAIR_SUFFIXES = ("_PRODUCER", "_AMPLIFIER", "_CONSUMER")
AMPLIFIER_SYNERGY_AXES = {
    "POWER_AMPLIFIER", "REPLAY",
    "ATTACK_REPLAY", "ATTACK_REPLAY_RANDOM", "SKILL_REPLAY",
}
EFFECT_SYNERGY_AXES = {
    "DAMAGE_AMPLIFIER", "BLOCK_AMPLIFIER",
    "VULN_AMPLIFIER", "WEAK_AMPLIFIER",
    "BLOCK_PAYOFF", "HP_LOSS_CONSUMER",
}
HAND_SYNERGY_POWERS = {
    "StrengthPower", "TemporaryStrengthPower",
    "DexterityPower", "TemporaryDexterityPower",
    "VulnerablePower", "WeakPower",
}

# Self-modifier axes that drive PlanScorer.PlayOrderBias / waste-avoidance branches.
SELF_MODIFIER_AXES = (
    "EXHAUST_SELF", "RETAIN_SELF", "ETHEREAL_SELF",
    "INNATE", "INNATE_SELF", "UNPLAYABLE",
)

# vars-key patterns the conditional-damage path in PlanScorer handles separately.
CONDITIONAL_VAR_CATEGORIES: dict[str, tuple[str, ...]] = {
    "Calculated*": (
        "CalculatedDamage", "CalculatedBlock", "CalculatedHits",
        "CalculatedCards", "CalculatedChannels", "CalculatedDoom",
        "CalculatedEnergy", "CalculatedFocus", "CalculatedForge",
        "CalculatedShivs",
    ),
    "Calculation base/extra": ("CalculationBase", "CalculationExtra"),
    "Extra*": ("ExtraDamage", "ExtraBlock", "ExtraCost"),
    "Repeat": ("Repeat",),
}


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--catalog", type=Path, default=None,
                   help="Master cards_catalog.json (default: scripts/cards_catalog.json)")
    p.add_argument("--triggers", type=Path, default=None,
                   help="Embedded card_triggers.json (default: Sts2CombatAICode/Core/Data/card_triggers.json)")
    p.add_argument("--power-catalog", type=Path, default=None,
                   help="PowerCatalog.cs source (default: Sts2CombatAICode/Core/Planner/PowerCatalog.cs)")
    p.add_argument("--override-catalog", type=Path, default=None,
                   help="CardOverrideCatalog.cs source (default: Sts2CombatAICode/Core/Planner/CardOverrideCatalog.cs)")
    p.add_argument("--sequencing-tier", type=Path, default=None,
                   help="PowerSequencingTier.cs source (default: Sts2CombatAICode/Core/Planner/PowerSequencingTier.cs)")
    p.add_argument("--out", type=Path, default=None,
                   help="Also write the Markdown report to this path")
    p.add_argument("--top-uncovered", type=int, default=20,
                   help="How many dropped / no-axis cards to list (default 20)")
    return p.parse_args()


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def load_inputs(args: argparse.Namespace):
    root = repo_root()
    catalog_path = args.catalog or (root / "scripts" / "cards_catalog.json")
    triggers_path = args.triggers or (root / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json")
    power_path = args.power_catalog or (root / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs")
    override_path = args.override_catalog or (root / "Sts2CombatAICode" / "Core" / "Planner" / "CardOverrideCatalog.cs")
    tier_path = args.sequencing_tier or (root / "Sts2CombatAICode" / "Core" / "Planner" / "PowerSequencingTier.cs")

    missing = [p for p in (catalog_path, triggers_path, power_path, override_path, tier_path) if not p.exists()]
    if missing:
        print("Missing input file(s):", file=sys.stderr)
        for p in missing:
            print(f"  - {p}", file=sys.stderr)
        sys.exit(2)

    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    triggers = json.loads(triggers_path.read_text(encoding="utf-8"))
    power_src = power_path.read_text(encoding="utf-8")
    override_src = override_path.read_text(encoding="utf-8")
    tier_src = tier_path.read_text(encoding="utf-8")

    power_names = set(POWER_NAME_RE.findall(power_src))
    override_ids = {cid.upper() for cid in OVERRIDE_ID_RE.findall(override_src)}
    tier_map: dict[str, str] = {pw: tier for pw, tier in TIER_RE.findall(tier_src)}

    return {
        "catalog": catalog,
        "triggers": triggers,
        "power_names": power_names,
        "override_ids": override_ids,
        "tier_map": tier_map,
        "catalog_path": catalog_path,
        "triggers_path": triggers_path,
        "power_path": power_path,
        "override_path": override_path,
        "tier_path": tier_path,
    }


def power_vars(card: dict) -> list[str]:
    """Power-suffix keys in `vars`. Lower-bound estimate of which PowerCatalog
    entries this card touches."""
    return [k for k in (card.get("vars") or {}) if k.endswith("Power")]


def derive_power_name_from_id(card_id: str) -> str:
    """CARD.ECHO_FORM -> EchoFormPower (id-based fallback when vars is empty).

    Same convention PowerCatalog.cs uses (PascalCase + Power suffix).
    """
    if not card_id.startswith("CARD."):
        return ""
    tail = card_id[len("CARD."):]
    parts = [p for p in tail.split("_") if p]
    return "".join(p.capitalize() for p in parts) + "Power"


def classify(card: dict, triggers: dict, power_names: set[str], override_ids: set[str],
             tier_map: dict[str, str]) -> dict:
    cid = card["id"]
    trig = triggers["cards"].get(cid)
    has_trigger = trig is not None
    axes = card.get("axes") or []
    builds = card.get("builds") or []
    keywords = card.get("keywords") or []
    axes_set = set(axes)
    vars_keys = set((card.get("vars") or {}).keys())

    is_power = card.get("type") == "Power"
    pvars = power_vars(card)
    id_derived = derive_power_name_from_id(cid)
    pc_hit_vars = any(v in power_names for v in pvars)
    pc_hit_id = id_derived in power_names
    pc_hit = pc_hit_vars or pc_hit_id

    # ---- PowerSequencingTier classification (Power cards) ----
    seq_tier: str | None = None
    if is_power:
        candidates = list(pvars)
        if id_derived not in candidates:
            candidates.append(id_derived)
        # Highest-priority tier wins, matching PowerSequencingTier.Classify() rule
        priority = {"Setup": 4, "Scaling": 3, "Defensive": 2, "Tempo": 1, "Unknown": 0, "SelfHarm": -1}
        for pn in candidates:
            t = tier_map.get(pn)
            if t is None: continue
            if seq_tier is None or priority.get(t, 0) > priority.get(seq_tier, 0):
                seq_tier = t
        if seq_tier is None:
            seq_tier = "Unknown"

    # ---- Conditional / Calculated damage vars ----
    cond_cats: set[str] = set()
    for cat, keys in CONDITIONAL_VAR_CATEGORIES.items():
        if any(k in vars_keys for k in keys):
            cond_cats.add(cat)
    has_conditional = bool(cond_cats)

    # ---- Self-modifier axes (Exhaust/Retain/Ethereal/Innate/Unplayable) ----
    self_mods = axes_set & set(SELF_MODIFIER_AXES)

    # ---- SelectorMode triggers (Burn/Boost) — from card_triggers.json ----
    selector_upgrade = bool(trig and trig.get("upgrade_trigger"))
    selector_fetch = bool(trig and trig.get("fetch_trigger"))
    has_selector_trigger = selector_upgrade or selector_fetch

    # ---- synergy participation (catalog-static) ----
    pair_stems_p = {a[:-len("_PRODUCER")] for a in axes if a.endswith("_PRODUCER")}
    pair_stems_a = {a[:-len("_AMPLIFIER")] for a in axes if a.endswith("_AMPLIFIER")}
    pair_stems_c = {a[:-len("_CONSUMER")] for a in axes if a.endswith("_CONSUMER")}
    has_pair_role = bool(pair_stems_p | pair_stems_a | pair_stems_c)
    has_amp_axis = bool(axes_set & AMPLIFIER_SYNERGY_AXES)
    has_eff_axis = bool(axes_set & EFFECT_SYNERGY_AXES)
    hand_powers = vars_keys & HAND_SYNERGY_POWERS
    primary_builds = [b["tag"] for b in builds if b.get("role") == "primary"]
    has_primary_build = bool(primary_builds)

    synergy_rules_hit = sum([
        has_pair_role,
        has_amp_axis,
        has_eff_axis,
        bool(hand_powers),
        has_primary_build,
    ])

    return {
        "id": cid,
        "char": card.get("character", "?"),
        "tier": card.get("tier", "?"),
        "type": card.get("type", "?"),
        "has_trigger": has_trigger,
        "has_axes": bool(axes),
        "has_builds": bool(builds),
        "has_keywords": bool(keywords),
        "is_power": is_power,
        "pc_hit": pc_hit if is_power else None,
        "pc_hit_via": ("vars" if pc_hit_vars else ("id" if pc_hit_id else "miss")) if is_power else None,
        "has_override": cid.upper() in override_ids,
        "dropped": not (axes or builds or keywords or has_trigger),
        # synergy fields
        "pair_p": pair_stems_p,
        "pair_a": pair_stems_a,
        "pair_c": pair_stems_c,
        "has_pair_role": has_pair_role,
        "has_amp_axis": has_amp_axis,
        "amp_axes": axes_set & AMPLIFIER_SYNERGY_AXES,
        "has_eff_axis": has_eff_axis,
        "eff_axes": axes_set & EFFECT_SYNERGY_AXES,
        "hand_powers": hand_powers,
        "primary_builds": primary_builds,
        "has_primary_build": has_primary_build,
        "synergy_rules_hit": synergy_rules_hit,
        "has_any_synergy": synergy_rules_hit > 0,
        # v3 — extended evaluation paths
        "seq_tier": seq_tier,                          # None for non-Power
        "cond_categories": cond_cats,
        "has_conditional": has_conditional,
        "self_modifiers": self_mods,
        "has_self_modifier": bool(self_mods),
        "selector_upgrade": selector_upgrade,
        "selector_fetch": selector_fetch,
        "has_selector_trigger": has_selector_trigger,
    }


def pct(num: int, denom: int) -> str:
    return f"{(100.0 * num / denom):.1f}%" if denom else "n/a"


def build_report(inputs: dict, top_uncovered: int) -> str:
    catalog = inputs["catalog"]
    triggers = inputs["triggers"]
    power_names = inputs["power_names"]
    override_ids = inputs["override_ids"]
    tier_map = inputs["tier_map"]

    base_cards = [c for c in catalog["cards"] if not c.get("is_upgraded")]
    rows = [classify(c, triggers, power_names, override_ids, tier_map) for c in base_cards]

    total = len(rows)
    n_trigger = sum(1 for r in rows if r["has_trigger"])
    n_axes = sum(1 for r in rows if r["has_axes"])
    n_builds = sum(1 for r in rows if r["has_builds"])
    n_dropped = sum(1 for r in rows if r["dropped"])
    n_override = sum(1 for r in rows if r["has_override"])

    power_rows = [r for r in rows if r["is_power"]]
    n_power = len(power_rows)
    n_pc_hit = sum(1 for r in power_rows if r["pc_hit"])
    n_pc_via_vars = sum(1 for r in power_rows if r["pc_hit_via"] == "vars")
    n_pc_via_id = sum(1 for r in power_rows if r["pc_hit_via"] == "id")

    # --- per-character ---
    char_rows: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        char_rows[r["char"]].append(r)

    # --- per-build (from triggers, since master uses Korean tags) ---
    build_counts: Counter[str] = Counter()
    for cid, info in triggers["cards"].items():
        for b in info.get("builds", []):
            build_counts[b["tag"]] += 1

    # --- top axis ---
    axis_counts: Counter[str] = Counter()
    for info in triggers["cards"].values():
        for a in info.get("axes", []):
            axis_counts[a] += 1

    # ===== Markdown =====
    lines: list[str] = []
    catalog_version = catalog.get("game_version", "?")
    triggers_version = triggers.get("version", "?")
    lines.append(f"# AI Card Coverage Report")
    lines.append("")
    lines.append(f"- Master catalog: `{inputs['catalog_path']}` (game {catalog_version})")
    lines.append(f"- Embedded triggers: `{inputs['triggers_path']}` ({triggers_version})")
    lines.append(f"- PowerCatalog: `{inputs['power_path']}` ({len(power_names)} powers registered)")
    lines.append(f"- PowerSequencingTier: `{inputs['tier_path']}` ({len(tier_map)} powers classified)")
    lines.append(f"- Override: `{inputs['override_path']}` ({len(override_ids)} cards)")
    lines.append("")

    n_any_synergy = sum(1 for r in rows if r["has_any_synergy"])

    lines.append(f"## Headline metrics  ({total} base cards)")
    lines.append("")
    lines.append("| Metric | Count | % |")
    lines.append("|---|---:|---:|")
    lines.append(f"| Catalog inclusion (in card_triggers.json) | {n_trigger} / {total} | {pct(n_trigger, total)} |")
    lines.append(f"| Axis coverage (`axes[]` non-empty) | {n_axes} / {total} | {pct(n_axes, total)} |")
    lines.append(f"| Build participation (`builds[]` non-empty) | {n_builds} / {total} | {pct(n_builds, total)} |")
    lines.append(f"| Override bonus applied | {n_override} / {total} | {pct(n_override, total)} |")
    lines.append(f"| Dropped (no axes/builds/keywords/trigger) | {n_dropped} / {total} | {pct(n_dropped, total)} |")
    lines.append(f"| Any synergy-rule participation (≥1 of 5 rules) | {n_any_synergy} / {total} | {pct(n_any_synergy, total)} |")

    n_conditional = sum(1 for r in rows if r["has_conditional"])
    n_self_mod = sum(1 for r in rows if r["has_self_modifier"])
    n_selector = sum(1 for r in rows if r["has_selector_trigger"])
    lines.append(f"| Conditional-damage vars (`Calculated*` / `Extra*` / `Repeat`) | {n_conditional} / {total} | {pct(n_conditional, total)} |")
    lines.append(f"| Self-modifier axes (`EXHAUST/RETAIN/ETHEREAL/INNATE/UNPLAYABLE`) | {n_self_mod} / {total} | {pct(n_self_mod, total)} |")
    lines.append(f"| SelectorMode trigger (`upgrade_trigger` / `fetch_trigger`) | {n_selector} / {total} | {pct(n_selector, total)} |")
    lines.append("")

    lines.append(f"## PowerCatalog hit rate  ({n_power} Power-type base cards)")
    lines.append("")
    lines.append("Lower bound: a Power card 'hits' the explicit table if any `*Power`-suffix")
    lines.append("key in its `vars`, or its id-derived `PascalCasePower` name, appears in")
    lines.append("`PowerCatalog.SelfBuff` / `EnemyDebuff`. Cards without a hit fall back to")
    lines.append("`HeuristicFallback()` or `DefaultValue = 200`.")
    lines.append("")
    lines.append("| Metric | Count | % |")
    lines.append("|---|---:|---:|")
    lines.append(f"| Total Power cards | {n_power} | 100% |")
    lines.append(f"| Hit via `vars` *Power suffix | {n_pc_via_vars} | {pct(n_pc_via_vars, n_power)} |")
    lines.append(f"| Hit via id-derived PascalCasePower | {n_pc_via_id} | {pct(n_pc_via_id, n_power)} |")
    lines.append(f"| **Any hit (lower bound)** | **{n_pc_hit}** | **{pct(n_pc_hit, n_power)}** |")
    lines.append(f"| Fallback only (HeuristicFallback / Default 200) | {n_power - n_pc_hit} | {pct(n_power - n_pc_hit, n_power)} |")
    lines.append("")

    # ========= PowerSequencingTier =========
    tier_counts: Counter[str] = Counter(r["seq_tier"] for r in power_rows)
    n_tier_classified = sum(v for k, v in tier_counts.items() if k != "Unknown")
    lines.append("## PowerSequencingTier coverage  (Power cards only)")
    lines.append("")
    lines.append("Within-turn ordering bonus for Power cards. `Unknown` tier receives 0")
    lines.append("ordering bonus — those cards rely on raw PowerCatalog value only.")
    lines.append("")
    lines.append("| Tier | Cards | % |")
    lines.append("|---|---:|---:|")
    for tier in ("Setup", "Scaling", "Defensive", "Tempo", "SelfHarm", "Unknown"):
        n = tier_counts.get(tier, 0)
        lines.append(f"| {tier} | {n} | {pct(n, n_power)} |")
    lines.append(f"| **Classified (any non-Unknown)** | **{n_tier_classified}** | **{pct(n_tier_classified, n_power)}** |")
    lines.append("")

    # ========= Conditional damage =========
    cond_cat_counts: Counter[str] = Counter()
    for r in rows:
        for cat in r["cond_categories"]:
            cond_cat_counts[cat] += 1
    lines.append(f"## Conditional damage / vars patterns  ({n_conditional} cards)")
    lines.append("")
    lines.append("Cards whose damage / block / hits depend on runtime calculation. PlanScorer")
    lines.append("has special handling for these (`CalculatedDamage`, `ExtraDamage` etc.) —")
    lines.append("missing the pattern means the card falls back to static stat scoring.")
    lines.append("")
    lines.append("| Category | Sample vars keys | Cards |")
    lines.append("|---|---|---:|")
    for cat, keys in CONDITIONAL_VAR_CATEGORIES.items():
        n = cond_cat_counts.get(cat, 0)
        sample = ", ".join(f"`{k}`" for k in keys[:3])
        if len(keys) > 3:
            sample += ", …"
        lines.append(f"| {cat} | {sample} | {n} |")
    lines.append("")

    # ========= Self-modifier axes =========
    self_mod_counts: Counter[str] = Counter()
    for r in rows:
        for a in r["self_modifiers"]:
            self_mod_counts[a] += 1
    lines.append(f"## Self-modifier axes  ({n_self_mod} cards have ≥1)")
    lines.append("")
    lines.append("Axes that drive `PlanScorer.PlayOrderBias` and waste-avoidance branches")
    lines.append("(Retain defer, Ethereal-now bonus, Exhaust loss, Innate opener, Unplayable")
    lines.append("rejection). A card outside this set takes the default play-order path.")
    lines.append("")
    lines.append("| Axis | Cards |")
    lines.append("|---|---:|")
    for a in SELF_MODIFIER_AXES:
        lines.append(f"| {a} | {self_mod_counts.get(a, 0)} |")
    lines.append("")

    # ========= SelectorMode triggers =========
    n_upg = sum(1 for r in rows if r["selector_upgrade"])
    n_fch = sum(1 for r in rows if r["selector_fetch"])
    lines.append(f"## SelectorMode triggers  ({n_selector} cards)")
    lines.append("")
    lines.append("Cards whose description keywords drive the Burn vs Boost prompt mode in")
    lines.append("`SelectorMode`. Cards without any trigger fall back to the default mode")
    lines.append("(usually Burn / discard-worst).")
    lines.append("")
    lines.append("| Trigger | Source | Cards |")
    lines.append("|---|---|---:|")
    lines.append(f"| `upgrade_trigger` | description contains \"강화\" | {n_upg} |")
    lines.append(f"| `fetch_trigger` | description contains 가져옴 / 생성 | {n_fch} |")
    lines.append("")

    # ========= Synergy / pair-axis coverage =========
    lines.append("## Synergy-rule reach")
    lines.append("")
    lines.append("How many cards in the pool can trigger each cross-card synergy rule")
    lines.append("`PlanScorer` invokes. Counts cards that *can supply* the rule's axis or")
    lines.append("power input — a card with `POWER_AMPLIFIER` is a potential `AmplifierSynergy`")
    lines.append("activator regardless of whether a target Power happens to be in hand at")
    lines.append("runtime.")
    lines.append("")
    lines.append("| Rule | Source | Cards | % |")
    lines.append("|---|---|---:|---:|")

    n_pair = sum(1 for r in rows if r["has_pair_role"])
    n_amp = sum(1 for r in rows if r["has_amp_axis"])
    n_eff = sum(1 for r in rows if r["has_eff_axis"])
    n_hand = sum(1 for r in rows if r["hand_powers"])
    n_pbuild = sum(1 for r in rows if r["has_primary_build"])

    lines.append(f"| BuildSynergy pair (Producer/Amplifier/Consumer) | `*_PRODUCER/_AMPLIFIER/_CONSUMER` axes | {n_pair} | {pct(n_pair, total)} |")
    lines.append(f"| BuildSynergy commitment | primary build tag | {n_pbuild} | {pct(n_pbuild, total)} |")
    lines.append(f"| AmplifierSynergy | `POWER_AMPLIFIER` / `REPLAY` / `ATTACK_REPLAY*` / `SKILL_REPLAY` | {n_amp} | {pct(n_amp, total)} |")
    lines.append(f"| EffectSynergy | `DAMAGE/BLOCK/VULN/WEAK_AMPLIFIER`, `BLOCK_PAYOFF`, `HP_LOSS_CONSUMER` | {n_eff} | {pct(n_eff, total)} |")
    lines.append(f"| HandSynergy (lower bound) | `vars` keys ∈ {{Strength/Dex/Vuln/Weak Power…}} | {n_hand} | {pct(n_hand, total)} |")
    lines.append("")

    # Pair-axis stem completeness
    stems_p: Counter[str] = Counter()
    stems_a: Counter[str] = Counter()
    stems_c: Counter[str] = Counter()
    for r in rows:
        for s in r["pair_p"]: stems_p[s] += 1
        for s in r["pair_a"]: stems_a[s] += 1
        for s in r["pair_c"]: stems_c[s] += 1
    all_stems = sorted(set(stems_p) | set(stems_a) | set(stems_c))

    n_complete = sum(1 for s in all_stems if stems_p[s] and (stems_a[s] or stems_c[s]))
    n_orphan = sum(1 for s in all_stems if not stems_p[s] or not (stems_a[s] or stems_c[s]))

    lines.append(f"## Pair-axis stem completeness  ({len(all_stems)} stems)")
    lines.append("")
    lines.append(f"A stem `X` triggers `BuildSynergy.Compute()` pair bonuses only when there")
    lines.append(f"is at least one `X_PRODUCER` AND at least one `X_AMPLIFIER` or `X_CONSUMER`")
    lines.append(f"card. Stems missing a side are dead pairs in the catalog.")
    lines.append("")
    lines.append(f"- **Complete stems** (P ≥ 1 AND (A ≥ 1 OR C ≥ 1)): **{n_complete} / {len(all_stems)}**")
    lines.append(f"- **Orphan stems** (missing one side): **{n_orphan}**")
    lines.append("")
    lines.append("| Stem | Producer | Amplifier | Consumer | Status |")
    lines.append("|---|---:|---:|---:|---|")
    for s in all_stems:
        p, a, c = stems_p[s], stems_a[s], stems_c[s]
        if p and (a or c):
            status = "complete"
        elif p and not a and not c:
            status = "producer-only"
        elif not p and (a or c):
            status = "no producer"
        else:
            status = "?"
        lines.append(f"| {s} | {p} | {a} | {c} | {status} |")
    lines.append("")

    # Synergy participation distribution
    degree_counts: Counter[int] = Counter(r["synergy_rules_hit"] for r in rows)
    lines.append("## Synergy participation degree")
    lines.append("")
    lines.append("Per-card count of synergy rules the card *can* feed (out of 5):")
    lines.append("`BuildSynergy pair`, `BuildSynergy commitment`, `AmplifierSynergy`,")
    lines.append("`EffectSynergy`, `HandSynergy` (vars-based lower bound).")
    lines.append("")
    lines.append("| Degree | Cards | % | Interpretation |")
    lines.append("|---:|---:|---:|---|")
    interp = {
        0: "no synergy hooks — evaluated as a standalone card",
        1: "single-rule (mostly build or pair)",
        2: "two-rule (build + pair, or pair + effect…)",
        3: "three-rule (high synergy density)",
        4: "four-rule (very dense)",
        5: "all five",
    }
    for d in range(0, 6):
        n = degree_counts[d]
        lines.append(f"| {d} | {n} | {pct(n, total)} | {interp[d]} |")
    lines.append("")

    lines.append("## Per-character coverage")
    lines.append("")
    lines.append("| Character | Cards | In triggers | Axes | Builds | Power hit | Dropped |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|")
    for char in sorted(char_rows.keys()):
        rs = char_rows[char]
        t = len(rs)
        c_trig = sum(1 for r in rs if r["has_trigger"])
        c_ax = sum(1 for r in rs if r["has_axes"])
        c_bld = sum(1 for r in rs if r["has_builds"])
        c_pow = [r for r in rs if r["is_power"]]
        c_pow_hit = sum(1 for r in c_pow if r["pc_hit"])
        c_drop = sum(1 for r in rs if r["dropped"])
        pow_cell = f"{c_pow_hit}/{len(c_pow)} ({pct(c_pow_hit, len(c_pow))})" if c_pow else "—"
        lines.append(f"| {char} | {t} | {c_trig} ({pct(c_trig, t)}) | {c_ax} ({pct(c_ax, t)}) | "
                     f"{c_bld} ({pct(c_bld, t)}) | {pow_cell} | {c_drop} ({pct(c_drop, t)}) |")
    lines.append("")

    lines.append("## Per-build participation (from embedded triggers)")
    lines.append("")
    lines.append("| Build tag | Cards |")
    lines.append("|---|---:|")
    for tag, n in sorted(build_counts.items(), key=lambda x: -x[1]):
        lines.append(f"| {tag} | {n} |")
    lines.append("")

    lines.append("## Top axes (embedded)")
    lines.append("")
    lines.append("| Axis | Cards |")
    lines.append("|---|---:|")
    for axis, n in axis_counts.most_common(30):
        lines.append(f"| {axis} | {n} |")
    lines.append("")

    # --- uncovered detail ---
    dropped = [r for r in rows if r["dropped"]]
    no_axes = [r for r in rows if not r["has_axes"]]
    pc_miss = [r for r in power_rows if not r["pc_hit"]]

    lines.append(f"## Dropped cards  ({len(dropped)} total, top {top_uncovered})")
    lines.append("")
    if dropped:
        lines.append("| Id | Character | Tier | Type |")
        lines.append("|---|---|---|---|")
        for r in dropped[:top_uncovered]:
            lines.append(f"| {r['id']} | {r['char']} | {r['tier']} | {r['type']} |")
    else:
        lines.append("_None._")
    lines.append("")

    lines.append(f"## Power cards without explicit PowerCatalog hit  ({len(pc_miss)} total, top {top_uncovered})")
    lines.append("")
    lines.append("These rely on `HeuristicFallback()` or `DefaultValue = 200`.")
    lines.append("")
    if pc_miss:
        lines.append("| Id | Character | Tier | vars keys |")
        lines.append("|---|---|---|---|")
        for r in pc_miss[:top_uncovered]:
            card = next(c for c in base_cards if c["id"] == r["id"])
            vkeys = ", ".join((card.get("vars") or {}).keys()) or "—"
            lines.append(f"| {r['id']} | {r['char']} | {r['tier']} | {vkeys} |")
    else:
        lines.append("_None._")
    lines.append("")

    if no_axes:
        lines.append(f"## Cards with no axes  ({len(no_axes)} total, top {top_uncovered})")
        lines.append("")
        lines.append("| Id | Character | Tier | Type |")
        lines.append("|---|---|---|---|")
        for r in no_axes[:top_uncovered]:
            lines.append(f"| {r['id']} | {r['char']} | {r['tier']} | {r['type']} |")
        lines.append("")

    lines.append("## Limitations")
    lines.append("")
    lines.append("- **Static only.** Runtime simulator / DecisionLog data not included.")
    lines.append("- **PowerCatalog hit is a lower bound.** A card's `vars` does not always")
    lines.append("  list every power it applies (e.g. `BARRICADE` has empty `vars` but does")
    lines.append("  apply `BarricadePower`). The id-derived fallback catches the common case")
    lines.append("  (`CARD.X_Y → XYPower`) but misses cards whose power name diverges from")
    lines.append("  the card id (e.g. `CARD.ABRASIVE → DexterityPower + ThornsPower`).")
    lines.append("- **Override list is sparse by design.** Low % here is expected, not a bug;")
    lines.append("  the metric is for absolute count tracking across releases.")
    lines.append("- **Synergy reach is a *potential* count, not realized activation.** A card")
    lines.append("  with `POWER_AMPLIFIER` only earns the bonus if a target Power is in hand")
    lines.append("  at the moment of scoring; runtime activation depends on hand composition,")
    lines.append("  draw order, and remaining energy. Static reach is the upper bound.")
    lines.append("- **HandSynergy reach is a lower bound (vars-based).** Cards that apply")
    lines.append("  Strength/Dex/Vuln/Weak through descriptions without exposing the power")
    lines.append("  name in `vars` are missed. Hits via `card.PowerApps` at runtime would be")
    lines.append("  higher.")
    lines.append("- **PowerSequencingTier hit shares the lower-bound caveat.** Same vars +")
    lines.append("  id-derived matching as PowerCatalog — a Power card whose applied power")
    lines.append("  name is not in `vars` or in the id-derived form will be reported as")
    lines.append("  `Unknown` even when the power IS registered in the tier map.")
    lines.append("- **Target distribution and Orb ChannelCount-based reach are not measured**")
    lines.append("  because the catalog exposes neither `target` nor `ChannelCount`. Those")
    lines.append("  paths can only be audited via runtime reflection.")
    lines.append("")

    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    inputs = load_inputs(args)
    report = build_report(inputs, args.top_uncovered)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(report, encoding="utf-8")
        print(f"Wrote report to {args.out}", file=sys.stderr)
    sys.stdout.reconfigure(encoding="utf-8")
    print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
