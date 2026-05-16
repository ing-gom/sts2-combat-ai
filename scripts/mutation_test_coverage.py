"""
Per-entry impact analysis for the Sts2CombatAI mod's evaluation rules.

Builds on `measure_ai_card_coverage.py` by removing each entry from the
relevant rule (PowerCatalog / PowerSequencingTier / CardOverrideCatalog)
one at a time and counting how many catalog cards lose their explicit
classification as a result.

Used to:
  - Identify high-impact entries (entry covers many cards → critical hand-tune)
  - Identify low-impact entries (entry covers 0–1 card → candidate for cleanup
    or renaming if it was meant to match more)
  - Quantify the marginal value of each registered rule entry

Run from repo root (Sts2CombatAI/):
    python scripts/mutation_test_coverage.py --catalog ../scripts/cards_catalog.json
    python scripts/mutation_test_coverage.py --rule power_catalog
    python scripts/mutation_test_coverage.py --rule sequencing_tier --out docs/mutation_impact.md
"""

import argparse
import json
import re
import sys
from pathlib import Path

# Reuse logic from the measure script
sys.path.insert(0, str(Path(__file__).resolve().parent))
import measure_ai_card_coverage as m  # type: ignore


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--catalog", type=Path, default=None,
                   help="Master cards_catalog.json (default: scripts/cards_catalog.json)")
    p.add_argument("--rule", choices=["power_catalog", "sequencing_tier", "override", "all"],
                   default="all",
                   help="Which rule set to mutate (default: all three).")
    p.add_argument("--out", type=Path, default=None,
                   help="Also write the Markdown report to this path.")
    return p.parse_args()


def load_base_state(catalog_override: Path | None) -> dict:
    """Load the same inputs as `measure` but allow override of catalog path."""
    fake_args = argparse.Namespace(
        catalog=catalog_override,
        triggers=None, power_catalog=None, override_catalog=None,
        sequencing_tier=None,
    )
    return m.load_inputs(fake_args)


def coverage_snapshot(rows: list[dict]) -> dict:
    """Aggregate the gap counts we care about."""
    power_rows = [r for r in rows if r["is_power"]]
    return {
        "pc_hit": sum(1 for r in power_rows if r["pc_hit"]),
        "pc_explicit": sum(1 for r in power_rows if r["pc_hit"]),
        "pc_true_default": sum(1 for r in power_rows
                                if not r["pc_hit"] and not r["heur_pattern"]),
        "seq_classified": sum(1 for r in power_rows if r["seq_tier"] != "Unknown"),
        "override_applied": sum(1 for r in rows if r["has_override"]),
        "n_power": len(power_rows),
        "n_total": len(rows),
    }


def reclassify(state: dict, *,
               power_names: set[str] | None = None,
               tier_map: dict[str, str] | None = None,
               override_ids: set[str] | None = None) -> list[dict]:
    """Run measure_ai_card_coverage.classify() with overridden rule sets."""
    pn = power_names if power_names is not None else state["power_names"]
    tm = tier_map if tier_map is not None else state["tier_map"]
    oi = override_ids if override_ids is not None else state["override_ids"]
    base_cards = [c for c in state["catalog"]["cards"] if not c.get("is_upgraded")]
    return [m.classify(c, state["triggers"], pn, oi, tm) for c in base_cards]


def cards_affected_by_power(state: dict, power: str) -> list[str]:
    """Card ids whose only explicit PowerCatalog match is this power name."""
    base = state["power_names"]
    without = base - {power}
    rows_before = reclassify(state, power_names=base)
    rows_after = reclassify(state, power_names=without)
    by_id_after = {r["id"]: r for r in rows_after}
    affected = []
    for r in rows_before:
        if not r["is_power"] or not r["pc_hit"]:
            continue
        a = by_id_after[r["id"]]
        if not a["pc_hit"]:
            affected.append(r["id"])
    return affected


def cards_affected_by_tier(state: dict, power: str) -> list[str]:
    """Card ids whose seq tier becomes Unknown if this power is dropped."""
    base = state["tier_map"]
    without = {k: v for k, v in base.items() if k != power}
    rows_before = reclassify(state, tier_map=base)
    rows_after = reclassify(state, tier_map=without)
    by_id_after = {r["id"]: r for r in rows_after}
    affected = []
    for r in rows_before:
        if not r["is_power"] or r["seq_tier"] == "Unknown":
            continue
        a = by_id_after[r["id"]]
        if a["seq_tier"] == "Unknown":
            affected.append(r["id"])
    return affected


def cards_affected_by_override(state: dict, card_id: str) -> list[str]:
    """For overrides this is trivial (exactly 1 card) but kept for symmetry."""
    base = state["override_ids"]
    if card_id.upper() not in base:
        return []
    without = base - {card_id.upper()}
    rows_after = reclassify(state, override_ids=without)
    by_id_after = {r["id"]: r for r in rows_after}
    return [card_id] if not by_id_after.get(card_id, {}).get("has_override", False) else []


def build_report(state: dict, rules: list[str]) -> str:
    lines: list[str] = []
    lines.append("# Sts2CombatAI Rule Mutation Impact Report")
    lines.append("")
    lines.append("Per-entry leave-one-out impact on catalog classification.")
    lines.append("`Cards lost` = number of catalog cards that fall out of the")
    lines.append("rule's explicit-hit set when this entry is removed.")
    lines.append("")
    lines.append(f"- Master catalog: `{state['catalog_path']}`")
    lines.append(f"- PowerCatalog entries: {len(state['power_names'])}")
    lines.append(f"- PowerSequencingTier entries: {len(state['tier_map'])}")
    lines.append(f"- CardOverrideCatalog entries: {len(state['override_ids'])}")
    lines.append("")

    snap = coverage_snapshot(reclassify(state))
    lines.append("## Baseline (no mutation)")
    lines.append("")
    lines.append(f"- PowerCatalog explicit hits: {snap['pc_explicit']} / {snap['n_power']}")
    lines.append(f"- PowerSequencingTier classified: {snap['seq_classified']} / {snap['n_power']}")
    lines.append(f"- Override applied: {snap['override_applied']} / {snap['n_total']}")
    lines.append("")

    if "power_catalog" in rules:
        lines.append(f"## PowerCatalog mutation ({len(state['power_names'])} entries)")
        lines.append("")
        impacts = []
        for pw in sorted(state["power_names"]):
            affected = cards_affected_by_power(state, pw)
            impacts.append((pw, affected))
        impacts.sort(key=lambda x: -len(x[1]))
        lines.append("| Power name | Cards lost | Example ids |")
        lines.append("|---|---:|---|")
        for pw, affected in impacts:
            if not affected:
                continue
            sample = ", ".join(f"`{i}`" for i in affected[:3])
            if len(affected) > 3:
                sample += f", … (+{len(affected) - 3})"
            lines.append(f"| `{pw}` | {len(affected)} | {sample} |")
        zero = [pw for pw, a in impacts if not a]
        if zero:
            lines.append("")
            lines.append(f"**Zero-impact entries ({len(zero)})** — registered but no catalog")
            lines.append("card maps to them via vars / id-derived matching:")
            lines.append("")
            for pw in zero:
                lines.append(f"- `{pw}`")
        lines.append("")

    if "sequencing_tier" in rules:
        lines.append(f"## PowerSequencingTier mutation ({len(state['tier_map'])} entries)")
        lines.append("")
        impacts = []
        for pw in sorted(state["tier_map"].keys()):
            affected = cards_affected_by_tier(state, pw)
            impacts.append((pw, state["tier_map"][pw], affected))
        impacts.sort(key=lambda x: -len(x[2]))
        lines.append("| Power name | Tier | Cards lost | Example ids |")
        lines.append("|---|---|---:|---|")
        for pw, tier, affected in impacts:
            if not affected:
                continue
            sample = ", ".join(f"`{i}`" for i in affected[:3])
            if len(affected) > 3:
                sample += f", … (+{len(affected) - 3})"
            lines.append(f"| `{pw}` | {tier} | {len(affected)} | {sample} |")
        zero = [(pw, t) for pw, t, a in impacts if not a]
        if zero:
            lines.append("")
            lines.append(f"**Zero-impact entries ({len(zero)})** — registered but no catalog")
            lines.append("card resolves to this tier statically:")
            lines.append("")
            for pw, t in zero:
                lines.append(f"- `{pw}` ({t})")
        lines.append("")

    if "override" in rules:
        lines.append(f"## CardOverrideCatalog mutation ({len(state['override_ids'])} entries)")
        lines.append("")
        lines.append("Each override entry is 1:1 with a card; impact is trivially 1 unless")
        lines.append("the card id is not present in the catalog (orphan override).")
        lines.append("")
        ids_in_catalog = {c["id"].upper() for c in state["catalog"]["cards"]
                          if not c.get("is_upgraded")}
        orphans = [oid for oid in sorted(state["override_ids"])
                   if oid not in ids_in_catalog]
        if orphans:
            lines.append(f"**Orphan overrides ({len(orphans)})** — registered but the card")
            lines.append("id does not appear in the master catalog (likely renamed / removed):")
            lines.append("")
            for oid in orphans:
                lines.append(f"- `{oid}`")
        else:
            lines.append("_All {} override entries match an active catalog card._".format(
                len(state["override_ids"])))
        lines.append("")

    lines.append("## Reading")
    lines.append("")
    lines.append("- **High-impact entries** (≥3 cards lost) are critical hand-tunes; their")
    lines.append("  values should be reviewed carefully — wrong magnitude propagates.")
    lines.append("- **Zero-impact entries** suggest either dead code (target power never")
    lines.append("  appears in catalog) or a naming mismatch between the rule's power name")
    lines.append("  and what the catalog's `vars` / id actually exposes.")
    lines.append("- Static mutation — same lower-bound caveat as the parent script. A card")
    lines.append("  whose true game power class differs from the id-derived name may not")
    lines.append("  be captured in `Cards lost`.")
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    state = load_base_state(args.catalog)
    rules = ["power_catalog", "sequencing_tier", "override"] if args.rule == "all" else [args.rule]
    report = build_report(state, rules)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(report, encoding="utf-8")
        print(f"Wrote report to {args.out}", file=sys.stderr)
    sys.stdout.reconfigure(encoding="utf-8")
    print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
