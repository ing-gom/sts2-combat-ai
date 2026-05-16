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

    missing = [p for p in (catalog_path, triggers_path, power_path, override_path) if not p.exists()]
    if missing:
        print("Missing input file(s):", file=sys.stderr)
        for p in missing:
            print(f"  - {p}", file=sys.stderr)
        sys.exit(2)

    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    triggers = json.loads(triggers_path.read_text(encoding="utf-8"))
    power_src = power_path.read_text(encoding="utf-8")
    override_src = override_path.read_text(encoding="utf-8")

    power_names = set(POWER_NAME_RE.findall(power_src))
    override_ids = {cid.upper() for cid in OVERRIDE_ID_RE.findall(override_src)}

    return {
        "catalog": catalog,
        "triggers": triggers,
        "power_names": power_names,
        "override_ids": override_ids,
        "catalog_path": catalog_path,
        "triggers_path": triggers_path,
        "power_path": power_path,
        "override_path": override_path,
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


def classify(card: dict, triggers: dict, power_names: set[str], override_ids: set[str]) -> dict:
    cid = card["id"]
    trig = triggers["cards"].get(cid)
    has_trigger = trig is not None
    axes = card.get("axes") or []
    builds = card.get("builds") or []
    keywords = card.get("keywords") or []

    is_power = card.get("type") == "Power"
    pvars = power_vars(card)
    id_derived = derive_power_name_from_id(cid)
    pc_hit_vars = any(v in power_names for v in pvars)
    pc_hit_id = id_derived in power_names
    pc_hit = pc_hit_vars or pc_hit_id

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
    }


def pct(num: int, denom: int) -> str:
    return f"{(100.0 * num / denom):.1f}%" if denom else "n/a"


def build_report(inputs: dict, top_uncovered: int) -> str:
    catalog = inputs["catalog"]
    triggers = inputs["triggers"]
    power_names = inputs["power_names"]
    override_ids = inputs["override_ids"]

    base_cards = [c for c in catalog["cards"] if not c.get("is_upgraded")]
    rows = [classify(c, triggers, power_names, override_ids) for c in base_cards]

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
    lines.append(f"- Override: `{inputs['override_path']}` ({len(override_ids)} cards)")
    lines.append("")

    lines.append(f"## Headline metrics  ({total} base cards)")
    lines.append("")
    lines.append("| Metric | Count | % |")
    lines.append("|---|---:|---:|")
    lines.append(f"| Catalog inclusion (in card_triggers.json) | {n_trigger} / {total} | {pct(n_trigger, total)} |")
    lines.append(f"| Axis coverage (`axes[]` non-empty) | {n_axes} / {total} | {pct(n_axes, total)} |")
    lines.append(f"| Build participation (`builds[]` non-empty) | {n_builds} / {total} | {pct(n_builds, total)} |")
    lines.append(f"| Override bonus applied | {n_override} / {total} | {pct(n_override, total)} |")
    lines.append(f"| Dropped (no axes/builds/keywords/trigger) | {n_dropped} / {total} | {pct(n_dropped, total)} |")
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
