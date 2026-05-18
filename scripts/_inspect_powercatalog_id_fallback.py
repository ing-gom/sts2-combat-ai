"""Spot-check the v0.7.7 id-derived PowerCatalog fallback.

Lists every Power card with catalog vars: {} (= no static PowerVar exposed),
derives the canonical PascalCase power name, and reports whether
PowerCatalog has an explicit (non-heuristic) entry. The C# planner uses the
same logic at runtime when card.PowerApps is empty.

Output highlights which cards stand to GAIN scoring coverage when reflection
fails to populate PowerApps — i.e. the cards whose v0.7.3 'runtime
PowerVar<T> reflection catches it' assumption was load-bearing.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
CATALOG = REPO_ROOT / "scripts" / "cards_catalog.json"
POWER_CATALOG = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs"


def id_to_power_name(card_id: str) -> str:
    body = card_id.split(".", 1)[-1] if "." in card_id else card_id
    parts = body.split("_")
    return "".join(p.capitalize() for p in parts) + "Power"


def parse_power_catalog() -> tuple[dict[str, int], dict[str, int]]:
    text = POWER_CATALOG.read_text(encoding="utf-8")
    def extract(name: str) -> dict[str, int]:
        m = re.search(
            rf"public static readonly IReadOnlyDictionary<string, int>\s+{name}\s*=\s*new Dictionary<string, int>\s*\{{(.*?)\n\s*\}};",
            text, re.DOTALL)
        if not m: return {}
        body = m.group(1)
        out = {}
        for line_m in re.finditer(r'\{\s*"([A-Za-z0-9_]+)"\s*,\s*(-?\d+)\s*\}', body):
            out[line_m.group(1)] = int(line_m.group(2))
        return out
    return extract("SelfBuff"), extract("EnemyDebuff")


def main() -> None:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    self_buff, enemy_debuff = parse_power_catalog()

    rows = []
    for c in catalog["cards"]:
        if c.get("is_upgraded"): continue
        if c.get("type") != "Power": continue
        if c.get("vars"): continue  # has explicit vars - reflection should catch it
        derived = id_to_power_name(c["id"])
        s = self_buff.get(derived)
        e = enemy_debuff.get(derived)
        value = max(s or 0, e or 0)
        explicit = derived in self_buff or derived in enemy_debuff
        rows.append((c["tier"], c["character"], c["id"], derived, value, explicit))

    rows.sort(key=lambda r: ("SABCD?".index(r[0]) if r[0] in "SABCD?" else 9, r[1], r[2]))

    print(f"Power cards with catalog vars empty - {len(rows)} total")
    print(f"PowerCatalog SelfBuff entries: {len(self_buff)}, EnemyDebuff: {len(enemy_debuff)}")
    print()
    print(f"{'tier':<5} {'character':<12} {'card':<30} {'derived':<25} {'value':>5}  hit")
    print("-" * 90)
    no_hit = 0
    for tier, ch, cid, derived, value, explicit in rows:
        mark = "OK" if explicit else "FALLBACK->default(200)"
        if not explicit:
            no_hit += 1
        print(f"{tier:<5} {ch:<12} {cid:<30} {derived:<25} {value:>5}  {mark}")

    print()
    print(f"Explicit PowerCatalog hits: {len(rows) - no_hit}/{len(rows)}")
    print(f"Heuristic fallback only: {no_hit}/{len(rows)} (these score 0 under the v0.7.7 conservative gate)")


if __name__ == "__main__":
    main()
