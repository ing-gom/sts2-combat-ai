"""
Phase B of the runtime analysis infrastructure plan
(docs/runtime_analysis_infra_plan.md). Reads NDJSON files produced by
Sts2CombatAICode/Core/Diagnostics/DecisionLogPersister.cs, normalizes
them into a structured record set, and writes the result for the Phase
C analyzer to consume.

Inputs:
  --logs <dir>     Directory of .ndjson files (default:
                   ~/.local/share/Sts2/Sts2CombatAI/decision_log/)
  --since YYYY-MM-DD  Only files created on/after this date
  --out <path>     Write normalized records as JSON (one array of objects)
  --summary        Print summary stats to stdout (default if --out absent)

Output schema (per normalized record):
  combat_id, character, playstyle
  turn, step
  card_id, card_kind (inferred), target, score
  enemy_hp_before, enemy_hp_after (= next decision's before, or 0 if last)
  player_hp_before, player_block_before
  lethal_active, fetch_card, combo_links
  breakdown_raw (str)
  breakdown_kv  (dict: name -> int)  parsed from breakdown_raw
  reason (str)
  ts (ISO 8601 string)

Robustness:
  - Tolerant of unknown / new breakdown keys (kept in breakdown_kv).
  - Unparseable values stay as strings under breakdown_other (separate dict).
  - Missing fields default sensibly (0 / False / "").

No external dependencies (pure stdlib). Suitable for any Python 3.10+.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path
from typing import Any


# Breakdown detail tokens have a few syntactic flavors:
#   dmg12*50=600
#   eff{15}(base10)
#   vulnAmpTgt=+450
#   tier=Setup+200
#   buildSyn=120
#   combo(4link,Inflame→Bash→Cruelty→Strike)+150
#   fetchPoll=-210
# We extract numeric (name, value) pairs where the LHS is alpha[A-Za-z_]+
# and the RHS is an integer. The remaining unparsed-but-present tokens
# go into `breakdown_other` for diagnostics.
KV_PATTERN = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\+?\d+)")
DMG_BRACE_PATTERN = re.compile(r"dmg\{(-?\d+)\}")
EFF_BRACE_PATTERN = re.compile(r"eff\{(-?\d+)\}")
TIER_PATTERN = re.compile(r"\btier\s*=\s*(\w+?)(?:\+(-?\d+))?\b")


def parse_breakdown(line: str) -> tuple[dict[str, int], dict[str, str]]:
    if not line:
        return {}, {}

    kv: dict[str, int] = {}
    other: dict[str, str] = {}

    for m in KV_PATTERN.finditer(line):
        name = m.group(1)
        val_raw = m.group(2).lstrip("+")
        try:
            kv[name] = int(val_raw)
        except ValueError:
            other[name] = m.group(2)

    m = DMG_BRACE_PATTERN.search(line)
    if m:
        kv.setdefault("dmg", int(m.group(1)))

    m = EFF_BRACE_PATTERN.search(line)
    if m:
        kv.setdefault("eff", int(m.group(1)))

    m = TIER_PATTERN.search(line)
    if m:
        other.setdefault("tier_name", m.group(1))
        if m.group(2) is not None:
            kv.setdefault("tier_ordering", int(m.group(2)))

    return kv, other


def infer_kind_from_id(card_id: str) -> str:
    # Best effort — actual kind is in catalog but we don't load it here.
    # Future refinement: cross-ref with scripts/cards_catalog.json.
    return ""


def parse_one_file(path: Path) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    combat_id = path.stem
    with path.open("r", encoding="utf-8") as fh:
        for raw_line in fh:
            line = raw_line.strip()
            if not line:
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError as e:
                print(f"WARN {path.name}: skipping malformed line: {e}",
                      file=sys.stderr)
                continue

            breakdown_raw = entry.get("breakdown", "") or ""
            breakdown_kv, breakdown_other = parse_breakdown(breakdown_raw)

            records.append({
                "combat_id": combat_id,
                "ts": entry.get("ts", ""),
                "character": entry.get("character", ""),
                "playstyle": entry.get("playstyle", ""),
                "turn": int(entry.get("turn", 0) or 0),
                "step": int(entry.get("step", 0) or 0),
                "card_id": entry.get("card_id", ""),
                "card_kind": infer_kind_from_id(entry.get("card_id", "")),
                "target": entry.get("target", ""),
                "score": int(entry.get("score", 0) or 0),
                "enemy_hp_before": int(entry.get("enemy_hp_before", 0) or 0),
                "enemy_hp_after": 0,  # filled in by next-decision pass
                "player_hp_before": int(entry.get("player_hp_before", 0) or 0),
                "player_block_before": int(entry.get("player_block_before", 0) or 0),
                "lethal_active": bool(entry.get("lethal_active", False)),
                "fetch_card": bool(entry.get("fetch_card", False)),
                "combo_links": int(entry.get("combo_links", 0) or 0),
                "reason": entry.get("reason", ""),
                "snapshot": entry.get("snapshot", ""),
                "breakdown_raw": breakdown_raw,
                "breakdown_kv": breakdown_kv,
                "breakdown_other": breakdown_other,
            })

    fill_enemy_hp_after(records)
    return records


def fill_enemy_hp_after(records: list[dict[str, Any]]) -> None:
    """Assign enemy_hp_after as the NEXT decision's enemy_hp_before within
    the same combat. The final decision uses 0 (combat presumed ended)."""
    for i, rec in enumerate(records):
        if i + 1 < len(records):
            rec["enemy_hp_after"] = records[i + 1]["enemy_hp_before"]
        else:
            rec["enemy_hp_after"] = 0


def default_logs_dir() -> Path:
    # Mirror the C# OS.GetUserDataDir() default. Godot uses
    # ~/.local/share/<game_id>/ on Linux; ~/Library/Application Support/
    # on macOS; %APPDATA%/<game_id>/ on Windows. We cannot resolve the
    # game id from here cleanly, so users should pass --logs explicitly.
    home = Path.home()
    candidates = [
        home / ".local" / "share" / "Sts2" / "Sts2CombatAI" / "decision_log",
        home / "Library" / "Application Support" / "Sts2" / "Sts2CombatAI" / "decision_log",
    ]
    for c in candidates:
        if c.exists():
            return c
    return candidates[0]


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--logs", type=Path, default=default_logs_dir())
    p.add_argument("--since", type=str, default=None,
                   help="Only files created on/after YYYY-MM-DD")
    p.add_argument("--out", type=Path, default=None,
                   help="Write normalized records as JSON to this path")
    p.add_argument("--summary", action="store_true",
                   help="Print summary stats to stdout")
    args = p.parse_args()

    if not args.logs.is_dir():
        print(f"ERROR: logs dir not found: {args.logs}", file=sys.stderr)
        print(f"Pass --logs <dir> to point at the right location.", file=sys.stderr)
        return 2

    since_dt: dt.datetime | None = None
    if args.since:
        try:
            since_dt = dt.datetime.strptime(args.since, "%Y-%m-%d")
        except ValueError:
            print(f"ERROR: --since must be YYYY-MM-DD, got {args.since}",
                  file=sys.stderr)
            return 2

    files = sorted(args.logs.glob("*.ndjson"))
    if since_dt:
        files = [f for f in files
                 if dt.datetime.fromtimestamp(f.stat().st_mtime) >= since_dt]

    if not files:
        print(f"No NDJSON files in {args.logs}"
              + (f" since {args.since}" if since_dt else ""))
        return 0

    all_records: list[dict[str, Any]] = []
    parse_errors = 0
    for f in files:
        try:
            all_records.extend(parse_one_file(f))
        except Exception as e:
            print(f"WARN failed to parse {f.name}: {e}", file=sys.stderr)
            parse_errors += 1

    print(f"Parsed {len(all_records)} decisions from {len(files)} combats "
          f"({parse_errors} parse failures)", file=sys.stderr)

    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(all_records, ensure_ascii=False, indent=2),
                            encoding="utf-8")
        print(f"Wrote {len(all_records)} records to {args.out}", file=sys.stderr)

    if args.summary or not args.out:
        print_summary(all_records)

    return 0


def print_summary(records: list[dict[str, Any]]) -> None:
    if not records:
        print("(no records)")
        return

    combats = len({r["combat_id"] for r in records})
    characters = sorted({r["character"] for r in records if r["character"]})
    cards = sorted({r["card_id"] for r in records})
    avg_score = sum(r["score"] for r in records) / len(records)
    lethal_n = sum(1 for r in records if r["lethal_active"])
    fetch_n = sum(1 for r in records if r["fetch_card"])
    combo_n = sum(1 for r in records if r["combo_links"] >= 3)

    print()
    print("=== DecisionLog summary ===")
    print(f"  Decisions   : {len(records):,}")
    print(f"  Combats     : {combats}")
    print(f"  Characters  : {', '.join(characters) or '(none)'}")
    print(f"  Unique cards: {len(cards)}")
    print(f"  Avg score   : {avg_score:.0f}")
    print(f"  Lethal-active turns : {lethal_n} ({100*lethal_n/len(records):.1f}%)")
    print(f"  Fetch-card plays    : {fetch_n} ({100*fetch_n/len(records):.1f}%)")
    print(f"  Combo (≥3 link)     : {combo_n} ({100*combo_n/len(records):.1f}%)")
    print()
    print("Top 10 cards by play count:")
    counts: dict[str, int] = {}
    for r in records:
        counts[r["card_id"]] = counts.get(r["card_id"], 0) + 1
    for cid, n in sorted(counts.items(), key=lambda x: -x[1])[:10]:
        print(f"  {n:5}  {cid}")


if __name__ == "__main__":
    sys.exit(main())
