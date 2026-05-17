"""
Phase C of the runtime analysis infrastructure plan
(docs/runtime_analysis_infra_plan.md). Consumes the normalized records
emitted by scripts/parse_decision_log.py and computes the runtime
metrics that static analysis cannot:

  1. Synergy activation rate — fraction of decisions where one of the
     known synergy axes actually fired in the breakdown.
  2. Lethal-mode precision / recall — how often LethalActive was set
     vs how often the fight actually closed.
  3. PowerCatalog explicit hit rate (runtime) — how often Power plays
     ended up with an explicit *Power(...) entry vs a heuristic fall-
     through. Calibrates the static "True DefaultValue" estimate.
  4. Combo recognition fire rate — plays with combo_links ≥ 3.
  5. Fetch pollution sample — fetch-card plays when pollution penalty
     was applied (proxy: fetchPoll key present in breakdown_kv).
  6. Ordering correctness — Setup-before-beneficiary heuristic:
     within each turn, did Setup-tier Powers / Vuln-Skill plays
     precede the attacks that benefit?
  7. Cost-curve realisation — average plays per turn, score
     distribution.
  8. Decision diversity — distinct card_ids played per combat /
     character; Top-N reliance ratio.
  9. Per-tier card usage — if --catalog supplied, cross-tab plays by
     card tier (S/A/B/C/D/?).
 10. Score vs HP outcome — correlation between average decision score
     and end-of-combat HP delta.

Inputs:
  --records <path>   JSON produced by parse_decision_log.py --out
  --logs <dir>       Alternative: parse on the fly from raw NDJSON
  --catalog <path>   Optional cards_catalog.json for tier metadata
  --out <path>       Write markdown report to this path

Output: markdown report (stdout + --out).

No external deps (pure stdlib).
"""

from __future__ import annotations

import argparse
import json
import statistics
import subprocess
import sys
import tempfile
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


# Breakdown-key markers for synergy classification. Each entry is a list
# of substrings; if any matches a breakdown_kv key (or appears in the
# raw breakdown line) the decision counts as "synergy fired".
SYNERGY_MARKERS = {
    "build_pair":      ["buildSyn"],
    "hand_synergy":    ["+syn=", "vulnAmpInHand", "weakAmpInHand"],
    "vuln_amp":        ["vulnAmpTgt", "vulnAmpAny", "vulnAmpInHand"],
    "weak_amp":        ["weakAmpEnemy", "weakAmpInHand"],
    "damage_amp":      ["dmgAmp"],
    "block_amp":       ["blkAmp"],
    "block_payoff":    ["blkPayoff"],
    "hp_loss":         ["hpLoss"],
    "amplifier":       ["powerAmp", "atkReplay", "skillReplay", "replay"],
    "power_tier":      ["tier=Setup", "tier=Scaling", "tier=Defensive", "tier=Tempo"],
    "skill_tier":      ["sklTier="],
    "combo":           ["combo("],
    "ethereal":        ["EtherealPlayNow"],   # not in current breakdown - placeholder
    "override":        ["override="],
    "lethal_mode":     ["lethalMode"],
    "fetch_pollution": ["fetchPoll"],
    "energy_monopoly": ["energyMono"],
}


def load_records(records_path: Path | None, logs_dir: Path | None) -> list[dict]:
    if records_path:
        return json.loads(records_path.read_text(encoding="utf-8"))
    if logs_dir:
        with tempfile.NamedTemporaryFile(suffix=".json", delete=False) as tmp:
            out = Path(tmp.name)
        script = Path(__file__).parent / "parse_decision_log.py"
        subprocess.run([sys.executable, str(script),
                       "--logs", str(logs_dir), "--out", str(out)],
                       check=True)
        return json.loads(out.read_text(encoding="utf-8"))
    raise SystemExit("Provide --records or --logs")


def load_catalog_tiers(catalog_path: Path | None) -> dict[str, str]:
    if not catalog_path or not catalog_path.exists():
        return {}
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    tiers: dict[str, str] = {}
    for c in catalog.get("cards", []):
        if c.get("is_upgraded"):
            continue
        tiers[c["id"]] = c.get("tier", "?")
    return tiers


def breakdown_has_marker(record: dict, markers: list[str]) -> bool:
    raw = record.get("breakdown_raw", "") or ""
    if any(m in raw for m in markers):
        return True
    kv = record.get("breakdown_kv", {}) or {}
    for m in markers:
        key = m.rstrip("=").lstrip("+").rstrip("(")
        if key in kv:
            return True
    return False


def m_synergy_activation(records: list[dict]) -> dict[str, Any]:
    fired: dict[str, int] = {}
    for rec in records:
        for name, markers in SYNERGY_MARKERS.items():
            if breakdown_has_marker(rec, markers):
                fired[name] = fired.get(name, 0) + 1
    return {
        "total_decisions": len(records),
        "per_rule": fired,
    }


def m_lethal_precision_recall(records: list[dict]) -> dict[str, Any]:
    # Group by combat. Last decision per combat is the "final" play.
    by_combat: dict[str, list[dict]] = defaultdict(list)
    for r in records:
        by_combat[r["combat_id"]].append(r)
    for k in by_combat:
        by_combat[k].sort(key=lambda r: (r.get("turn", 0), r.get("step", 0)))

    lethal_active_total = 0
    lethal_active_at_end = 0
    last_decision_lethal = 0
    combats = 0
    for combat, recs in by_combat.items():
        combats += 1
        any_lethal = any(r.get("lethal_active") for r in recs)
        if any_lethal:
            lethal_active_total += 1
        # "fight ended" proxy: combat had at least one decision and the
        # last record's enemy_hp_after is 0 (parser fills this from next
        # decision's before; 0 for the trailing decision = fight closed).
        last = recs[-1]
        if last.get("enemy_hp_after", 0) == 0:
            if any_lethal:
                lethal_active_at_end += 1
        if last.get("lethal_active"):
            last_decision_lethal += 1
    return {
        "combats": combats,
        "combats_with_lethal_active": lethal_active_total,
        "combats_with_lethal_and_ended": lethal_active_at_end,
        "combats_last_decision_lethal": last_decision_lethal,
    }


def m_power_catalog_hit(records: list[dict]) -> dict[str, Any]:
    """Approx: a Power play 'explicit-hit' if breakdown contains a token
    like `<Name>(<n>)=<value>` where Name ends with 'Power' or matches an
    id-derived form. Lower-bound: we only check the raw breakdown string."""
    power_plays = 0
    explicit_hits = 0
    for r in records:
        # Heuristic: card_kind isn't available, but a Power play typically
        # contains 'powerBase=' in its breakdown.
        raw = r.get("breakdown_raw", "")
        if "powerBase=" not in raw:
            continue
        power_plays += 1
        # Explicit hit if any token like Foo(n)=value where Foo ends Power.
        # Conservative: just check for "Power(" anywhere in the line.
        if "Power(" in raw:
            explicit_hits += 1
    return {
        "power_plays": power_plays,
        "explicit_hits": explicit_hits,
        "rate": (explicit_hits / power_plays) if power_plays else 0,
    }


def m_combo_fire_rate(records: list[dict]) -> dict[str, Any]:
    chains3 = sum(1 for r in records if r.get("combo_links", 0) >= 3)
    chains4 = sum(1 for r in records if r.get("combo_links", 0) >= 4)
    return {
        "total": len(records),
        "with_3plus_links": chains3,
        "with_4plus_links": chains4,
    }


def m_fetch_pollution(records: list[dict]) -> dict[str, Any]:
    fetch_plays = sum(1 for r in records if r.get("fetch_card"))
    with_penalty = sum(1 for r in records
                       if r.get("fetch_card")
                       and "fetchPoll" in (r.get("breakdown_kv") or {}))
    return {
        "fetch_plays": fetch_plays,
        "with_pollution_penalty": with_penalty,
    }


def m_ordering(records: list[dict]) -> dict[str, Any]:
    """Within each turn, was a Setup tier card (Power or Skill) played
    before the cards it would benefit? Coarse heuristic."""
    by_turn: dict[tuple[str, int], list[dict]] = defaultdict(list)
    for r in records:
        by_turn[(r["combat_id"], r.get("turn", 0))].append(r)
    for k in by_turn:
        by_turn[k].sort(key=lambda r: r.get("step", 0))

    setup_first = 0
    setup_late = 0
    no_setup = 0
    for k, recs in by_turn.items():
        first_setup_step = None
        first_beneficiary_step = None
        for r in recs:
            raw = r.get("breakdown_raw", "")
            is_setup = ("tier=Setup" in raw or "sklTier=Setup" in raw)
            if is_setup and first_setup_step is None:
                first_setup_step = r.get("step", 0)
            # Beneficiary heuristic: any attack play
            if "attackBase=" in raw and first_beneficiary_step is None:
                first_beneficiary_step = r.get("step", 0)
        if first_setup_step is None:
            no_setup += 1
        elif first_beneficiary_step is None:
            no_setup += 1
        elif first_setup_step < first_beneficiary_step:
            setup_first += 1
        else:
            setup_late += 1
    return {
        "turns_total": len(by_turn),
        "setup_first": setup_first,
        "setup_late": setup_late,
        "no_setup_or_no_attack": no_setup,
    }


def m_curve(records: list[dict]) -> dict[str, Any]:
    by_turn: dict[tuple[str, int], int] = defaultdict(int)
    for r in records:
        by_turn[(r["combat_id"], r.get("turn", 0))] += 1
    counts = list(by_turn.values())
    if not counts:
        return {"avg_plays_per_turn": 0, "p50": 0, "p90": 0}
    return {
        "avg_plays_per_turn": sum(counts) / len(counts),
        "p50": sorted(counts)[len(counts) // 2],
        "p90": sorted(counts)[int(len(counts) * 0.9)] if len(counts) > 1 else counts[0],
        "turns_total": len(counts),
    }


def m_diversity(records: list[dict]) -> dict[str, Any]:
    by_char: dict[str, Counter] = defaultdict(Counter)
    for r in records:
        by_char[r.get("character", "")][r["card_id"]] += 1
    result = {}
    for ch, ctr in by_char.items():
        plays = sum(ctr.values())
        top10_share = sum(n for _, n in ctr.most_common(10)) / plays if plays else 0
        result[ch or "(unknown)"] = {
            "unique_cards": len(ctr),
            "total_plays": plays,
            "top10_share": top10_share,
        }
    return result


def m_per_tier(records: list[dict], tiers: dict[str, str]) -> dict[str, Any]:
    if not tiers:
        return {}
    counts: Counter = Counter()
    for r in records:
        t = tiers.get(r["card_id"], "?")
        counts[t] += 1
    return dict(counts)


def m_score_vs_outcome(records: list[dict]) -> dict[str, Any]:
    by_combat: dict[str, list[dict]] = defaultdict(list)
    for r in records:
        by_combat[r["combat_id"]].append(r)
    pairs: list[tuple[float, int]] = []
    for combat, recs in by_combat.items():
        if not recs:
            continue
        recs.sort(key=lambda r: (r.get("turn", 0), r.get("step", 0)))
        avg_score = sum(r["score"] for r in recs) / len(recs)
        # HP delta = first decision's player_hp_before - last decision's player_hp_before
        # (decisions don't currently record player_hp_after — proxy).
        first_hp = recs[0].get("player_hp_before", 0)
        last_hp = recs[-1].get("player_hp_before", 0)
        hp_delta = first_hp - last_hp
        pairs.append((avg_score, hp_delta))
    if len(pairs) < 2:
        return {"combats": len(pairs), "correlation": None}
    # Pearson correlation
    n = len(pairs)
    xs = [p[0] for p in pairs]
    ys = [p[1] for p in pairs]
    mx = sum(xs) / n
    my = sum(ys) / n
    num = sum((xs[i] - mx) * (ys[i] - my) for i in range(n))
    sx = (sum((x - mx) ** 2 for x in xs)) ** 0.5
    sy = (sum((y - my) ** 2 for y in ys)) ** 0.5
    corr = num / (sx * sy) if sx > 0 and sy > 0 else 0
    return {
        "combats": len(pairs),
        "correlation": corr,
        "avg_score_mean": mx,
        "hp_delta_mean": my,
    }


def render_markdown(metrics: dict[str, Any]) -> str:
    lines = []
    lines.append("# Sts2CombatAI Runtime Metrics")
    lines.append("")
    lines.append("Decision-log analysis output. Each metric measures actual planner")
    lines.append("behavior — validates the static audit and the documented gaps.")
    lines.append("")

    s = metrics["synergy"]
    lines.append("## 1. Synergy activation")
    lines.append("")
    lines.append(f"Total decisions: **{s['total_decisions']}**")
    lines.append("")
    lines.append("| Rule | Fired | % |")
    lines.append("|---|---:|---:|")
    for k, n in sorted(s["per_rule"].items(), key=lambda x: -x[1]):
        pct = 100 * n / s["total_decisions"] if s["total_decisions"] else 0
        lines.append(f"| {k} | {n} | {pct:.1f}% |")
    lines.append("")

    l = metrics["lethal"]
    lines.append("## 2. Lethal-mode precision / recall")
    lines.append("")
    lines.append(f"- Combats analyzed: **{l['combats']}**")
    lines.append(f"- Combats with any lethal-active decision: **{l['combats_with_lethal_active']}**")
    lines.append(f"- Of those, fight actually ended: **{l['combats_with_lethal_and_ended']}**")
    lines.append(f"- Combats where last decision was lethal-active: **{l['combats_last_decision_lethal']}**")
    if l['combats_with_lethal_active']:
        prec = l['combats_with_lethal_and_ended'] / l['combats_with_lethal_active']
        lines.append(f"- Approximate precision: **{prec:.1%}**")
    lines.append("")

    p = metrics["power_catalog"]
    lines.append("## 3. PowerCatalog explicit-hit rate (runtime)")
    lines.append("")
    lines.append(f"- Power plays: **{p['power_plays']}**")
    lines.append(f"- Explicit hits (token like `*Power(...)=N`): **{p['explicit_hits']}**")
    rate_str = f"{p['rate']:.1%}" if p['power_plays'] else "n/a"
    lines.append(f"- Runtime hit rate: **{rate_str}**")
    lines.append("")

    c = metrics["combo"]
    lines.append("## 4. Combo recognition fire rate")
    lines.append("")
    lines.append(f"- Decisions: **{c['total']}**")
    lines.append(f"- Plays with 3+ link chain: **{c['with_3plus_links']}**")
    lines.append(f"- Plays with 4+ link chain: **{c['with_4plus_links']}**")
    lines.append("")

    f = metrics["fetch"]
    lines.append("## 5. Fetch pollution penalty applied")
    lines.append("")
    lines.append(f"- Fetch-card plays: **{f['fetch_plays']}**")
    lines.append(f"- ... with pollution penalty (junk in piles): **{f['with_pollution_penalty']}**")
    lines.append("")

    o = metrics["ordering"]
    lines.append("## 6. Setup-before-beneficiary ordering")
    lines.append("")
    lines.append(f"- Turns analyzed: **{o['turns_total']}**")
    lines.append(f"- Setup played before attacks: **{o['setup_first']}**")
    lines.append(f"- Setup played after attacks (mis-order): **{o['setup_late']}**")
    lines.append(f"- No Setup OR no attack this turn: **{o['no_setup_or_no_attack']}**")
    if o['setup_first'] + o['setup_late']:
        good = o['setup_first'] / (o['setup_first'] + o['setup_late'])
        lines.append(f"- Ordering correctness: **{good:.1%}**")
    lines.append("")

    cv = metrics["curve"]
    lines.append("## 7. Energy-curve realisation")
    lines.append("")
    lines.append(f"- Turns: **{cv.get('turns_total', 0)}**")
    lines.append(f"- Avg plays per turn: **{cv['avg_plays_per_turn']:.2f}**")
    lines.append(f"- Median (p50): {cv['p50']}, p90: {cv['p90']}")
    lines.append("")

    d = metrics["diversity"]
    lines.append("## 8. Decision diversity (per character)")
    lines.append("")
    if d:
        lines.append("| Character | Unique cards | Plays | Top-10 share |")
        lines.append("|---|---:|---:|---:|")
        for ch, info in sorted(d.items()):
            lines.append(f"| {ch} | {info['unique_cards']} | {info['total_plays']} | {info['top10_share']:.1%} |")
    else:
        lines.append("_(no data)_")
    lines.append("")

    t = metrics.get("per_tier", {})
    lines.append("## 9. Per-tier play distribution")
    lines.append("")
    if t:
        lines.append("| Tier | Plays |")
        lines.append("|---|---:|")
        for tier in ["S", "A", "B", "C", "D", "?"]:
            lines.append(f"| {tier} | {t.get(tier, 0)} |")
    else:
        lines.append("_(catalog not supplied; tier breakdown skipped)_")
    lines.append("")

    sv = metrics["score_outcome"]
    lines.append("## 10. Score vs HP-outcome correlation")
    lines.append("")
    lines.append(f"- Combats: **{sv['combats']}**")
    if sv.get("correlation") is not None:
        lines.append(f"- Pearson r (avg score vs HP lost): **{sv['correlation']:.3f}**")
        lines.append(f"- Avg score mean: {sv['avg_score_mean']:.0f}")
        lines.append(f"- HP delta mean: {sv['hp_delta_mean']:.1f}")
        lines.append("")
        lines.append("_Interpretation_: negative correlation = higher decision scores")
        lines.append("associate with LESS HP loss (planner is helping). Positive")
        lines.append("correlation would suggest score is not predictive of outcome.")
    else:
        lines.append("_(needs ≥2 combats for correlation)_")
    lines.append("")

    lines.append("## Limitations")
    lines.append("")
    lines.append("- Heuristics for Power-card detection use breakdown substring")
    lines.append("  patterns. Future refinement: cross-ref catalog kinds.")
    lines.append("- Ordering metric treats any attack as a Setup beneficiary;")
    lines.append("  doesn't filter to attacks that *would have benefited*.")
    lines.append("- HP-outcome correlation uses player_hp_before only;")
    lines.append("  enemy_hp_after derives from next decision's before, so")
    lines.append("  final-combat damage to the killing blow is undercounted.")
    return "\n".join(lines)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--records", type=Path, default=None,
                   help="JSON file from parse_decision_log.py --out")
    p.add_argument("--logs", type=Path, default=None,
                   help="Directory of NDJSON files (will invoke parser internally)")
    p.add_argument("--catalog", type=Path, default=None,
                   help="cards_catalog.json for tier lookup")
    p.add_argument("--out", type=Path, default=None,
                   help="Write markdown report to this path")
    args = p.parse_args()

    records = load_records(args.records, args.logs)
    if not records:
        print("No records to analyze", file=sys.stderr)
        return 0

    tiers = load_catalog_tiers(args.catalog)

    metrics = {
        "synergy": m_synergy_activation(records),
        "lethal": m_lethal_precision_recall(records),
        "power_catalog": m_power_catalog_hit(records),
        "combo": m_combo_fire_rate(records),
        "fetch": m_fetch_pollution(records),
        "ordering": m_ordering(records),
        "curve": m_curve(records),
        "diversity": m_diversity(records),
        "per_tier": m_per_tier(records, tiers),
        "score_outcome": m_score_vs_outcome(records),
    }

    report = render_markdown(metrics)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(report, encoding="utf-8")
        print(f"Wrote report to {args.out}", file=sys.stderr)
    print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
