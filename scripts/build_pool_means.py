"""
Build pool_means.json — per-character / per-pool-filter static value distribution
of the STS2 card pool. Consumed by EffectSynergy.ApplyCardGen for Level 4
pool-based random cards (WHITE_NOISE, DISCOVERY, CREATIVE_AI, HELLO_WORLD, …).

Steps:
1. Parse Sts2CombatAICode/Core/Planner/PowerCatalog.cs for SelfBuff/EnemyDebuff
   dictionaries (avoids drift — the C# table is the single source).
2. Walk cards_catalog.json, compute static_value per card using the Python
   mirror of EstimateCardPower (free-use = False — pool cards land in hand).
3. Group by (character, filter) and emit mean / top1of3 / top1of5 / top1of3_free
   order statistics. Top1ofN is an unbiased estimate via uniform sampling.
4. Write pool_means.json + a coverage report.

Output is consumed at runtime by Sts2CombatAI.Planner.PoolMeans (loaded once
as an embedded resource).
"""
from __future__ import annotations

import json
import random
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

# Make sibling script importable.
sys.path.insert(0, str(Path(__file__).parent))
import _effect_scoring_weights as W  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = REPO_ROOT / "scripts" / "cards_catalog.json"
POWER_CATALOG_PATH = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerCatalog.cs"
OUT_PATH = REPO_ROOT / "Sts2CombatAICode" / "Core" / "Data" / "pool_means.json"
REPORT_PATH = REPO_ROOT / "scripts" / "_pool_means_report.json"

# Number of Monte Carlo samples for order statistics.
ORDER_STAT_SAMPLES = 100_000

CHARACTERS = ("IRONCLAD", "SILENT", "DEFECT", "WATCHER", "NECROBINDER", "REGENT")
ELIGIBLE_RARITIES = {"Common", "Uncommon", "Rare"}


# --------------------------------------------------------------------------
# PowerCatalog parser — extract SelfBuff / EnemyDebuff dicts from the .cs file.
# --------------------------------------------------------------------------

def parse_power_catalog() -> tuple[dict[str, int], dict[str, int]]:
    text = POWER_CATALOG_PATH.read_text(encoding="utf-8")

    # Split into the two relevant dictionary blocks.
    def extract(name: str) -> dict[str, int]:
        m = re.search(
            rf"public static readonly IReadOnlyDictionary<string, int>\s+{name}\s*=\s*new Dictionary<string, int>\s*\{{(.*?)\n\s*\}};",
            text,
            re.DOTALL,
        )
        if not m:
            raise SystemExit(f"PowerCatalog parse failed: {name} dict not found")
        body = m.group(1)
        out: dict[str, int] = {}
        for line_m in re.finditer(r'\{\s*"([A-Za-z0-9_]+)"\s*,\s*(-?\d+)\s*\}', body):
            out[line_m.group(1)] = int(line_m.group(2))
        return out

    return extract("SelfBuff"), extract("EnemyDebuff")


def apply_stack_curve(base: int, stacks: int) -> int:
    """Mirror of PowerCatalog.ApplyStackCurve."""
    if stacks <= 0:
        return 0
    if stacks == 1:
        return base
    extra = int(base * 0.7 * (stacks - 1))
    cap = base * 4 if base >= 0 else base
    total = base + extra
    return min(total, cap) if base >= 0 else max(total, cap)


def heuristic_power(name: str, is_self: bool) -> int:
    if not name:
        return 200
    if name.startswith("Temporary"):
        return 150 if is_self else 80
    if name.startswith("No") and len(name) > 2 and name[2].isupper():
        return -800 if is_self else 100
    if name.endswith("NextTurnPower"):
        return 300 if is_self else 200
    if name.endswith("FormPower"):
        return 800 if is_self else 400
    if name.startswith("Free"):
        return 600 if is_self else 300
    if "Strength" in name:
        return 400 if is_self else -200
    if "Dexterity" in name:
        return 300 if is_self else -150
    if "Focus" in name:
        return 500 if is_self else -200
    return 200


def power_value(name: str, stacks: int, self_buff: dict[str, int], enemy_debuff: dict[str, int]) -> int:
    self_v = self_buff.get(name, heuristic_power(name, True))
    enemy_v = enemy_debuff.get(name, heuristic_power(name, False))
    base = max(self_v, enemy_v)
    return apply_stack_curve(base, stacks)


# --------------------------------------------------------------------------
# static_value — Python mirror of EffectSynergy.EstimateCardPower (free=False).
# --------------------------------------------------------------------------

# Catalog var-name → power-name. The C# planner consumes SimCard.PowerApps which
# is populated by reflection over the *Power class. Catalog vars use the same
# class names, so the mapping is identity for *Power keys and skip for anything
# else (damage modifiers like UnderfistPotionStrength, ShieldsUp, etc., aren't
# Power applications — they're card-effect modifiers).
def collect_power_apps(vars_dict: dict) -> list[tuple[str, int]]:
    apps: list[tuple[str, int]] = []
    if not vars_dict:
        return apps
    for k, v in vars_dict.items():
        if not k.endswith("Power"):
            continue
        try:
            iv = int(v)
        except (TypeError, ValueError):
            continue
        if iv == 0:
            continue
        apps.append((k, iv))
    return apps


def estimate_card_power(card: dict, self_buff: dict[str, int], enemy_debuff: dict[str, int], free_use: bool) -> int:
    card_type = card.get("type") or ""
    if card_type in ("Curse", "Status"):
        return W.CURSE_FREE if free_use else W.CURSE_INHAND

    v = 0
    damage = card.get("damage", 0) or 0
    block = card.get("block", 0) or 0

    if card_type == "Attack" and damage > 0:
        # TotalDamage in the C# planner accounts for multi-hit; the catalog
        # stores per-hit damage. Without hits metadata, single-hit is the
        # safe default — this slightly underestimates multi-hit attacks, but
        # those are a minority of the pool and the heuristic is per-card mean.
        v += damage * (W.DAMAGE_FREE if free_use else W.DAMAGE_INHAND)

    if block > 0:
        v += block * (W.BLOCK_FREE if free_use else W.BLOCK_INHAND)

    # Catalog has no explicit DrawCount / EnergyGain fields — these are
    # expressed via vars or descriptions. We approximate by scanning for the
    # well-known signatures. Most pool-based card pools draw from the regular
    # character pool which is overwhelmingly damage/block/power so this gap
    # rarely affects pool means.

    for name, amount in collect_power_apps(card.get("vars") or {}):
        p_val = power_value(name, amount, self_buff, enemy_debuff)
        v += p_val // (W.POWER_DIVISOR_FREE if free_use else W.POWER_DIVISOR_INHAND)

    if not free_use:
        cost = card.get("cost") or 0
        if cost == 0:
            v += W.COST_0_BONUS
        elif cost == 1:
            v += W.COST_1_BONUS
        elif cost >= 3:
            v += W.COST_3_PLUS_PENALTY

    return max(0, v)


# --------------------------------------------------------------------------
# Pool eligibility filters.
# --------------------------------------------------------------------------

def is_base_eligible(card: dict) -> bool:
    """Pool-eligible = base (non-upgraded) card from the regular card pool.

    Excludes upgraded duplicates, tokens (CARD.SHIV, CARD.SOUL etc.),
    starter Basic cards (CARD.STRIKE / CARD.DEFEND), and Status/Curse — these
    aren't part of the random-card pools that CREATIVE_AI / DISCOVERY draw from.
    """
    if card.get("is_upgraded"):
        return False
    if card.get("type") in ("Status", "Curse", "Quest", "None"):
        return False
    rarity = card.get("rarity")
    if rarity not in ELIGIBLE_RARITIES:
        return False
    return True


def pool_for(character: str, cards: list[dict]) -> list[dict]:
    return [c for c in cards if c.get("character") == character and is_base_eligible(c)]


def filter_by_type(pool: list[dict], type_name: str) -> list[dict]:
    return [c for c in pool if c.get("type") == type_name]


def filter_common(pool: list[dict]) -> list[dict]:
    return [c for c in pool if c.get("rarity") == "Common"]


# --------------------------------------------------------------------------
# Distribution summary — mean + top-1-of-N order statistic via sampling.
# --------------------------------------------------------------------------

def summarize(values: list[int], rng: random.Random) -> dict:
    if not values:
        return {"n": 0, "mean": 0, "top1of3": 0, "top1of5": 0}
    mean = int(round(statistics.fmean(values)))
    # Order statistic via Monte Carlo — analytic top1ofN over arbitrary discrete
    # distributions is the empirical CDF integral; sampling is simpler and the
    # sample count drives the noise well below ±1%.
    top1of3 = top_of_n(values, 3, rng)
    top1of5 = top_of_n(values, 5, rng)
    return {
        "n": len(values),
        "mean": mean,
        "top1of3": top1of3,
        "top1of5": top1of5,
    }


def top_of_n(values: list[int], n: int, rng: random.Random) -> int:
    samples = ORDER_STAT_SAMPLES
    total = 0
    for _ in range(samples):
        best = max(rng.choice(values) for _ in range(n))
        total += best
    return int(round(total / samples))


# --------------------------------------------------------------------------
# Driver.
# --------------------------------------------------------------------------

def main() -> None:
    rng = random.Random(0xC0FFEE)
    self_buff, enemy_debuff = parse_power_catalog()

    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    cards: list[dict] = catalog["cards"]

    detected_chars = sorted({c.get("character") for c in cards if c.get("character")})
    print(f"[pool_means] catalog characters: {detected_chars}", file=sys.stderr)

    shared_pool = [c for c in cards if c.get("character") == "SHARED" and is_base_eligible(c)]

    out: dict[str, dict] = {"_schema": W.SCHEMA_VERSION, "characters": {}}
    report_lines: list[str] = []

    for character in detected_chars:
        if character == "SHARED":
            continue  # SHARED handled via the colorless_other_chars filter.

        pool = pool_for(character, cards)
        if not pool:
            print(f"[pool_means] skip {character} — empty pool", file=sys.stderr)
            continue

        # Cache static values to avoid recomputing per filter.
        sv = {c["id"]: estimate_card_power(c, self_buff, enemy_debuff, free_use=False) for c in pool}
        sv_free = {c["id"]: estimate_card_power(c, self_buff, enemy_debuff, free_use=True) for c in pool}

        def values(filtered: list[dict], *, free: bool = False) -> list[int]:
            table = sv_free if free else sv
            return [table[c["id"]] for c in filtered]

        # Colorless = SHARED character — used by LARGESSE.
        colorless_vals = [estimate_card_power(c, self_buff, enemy_debuff, free_use=True) for c in shared_pool]

        filters: dict[str, list[int]] = {
            "all":            values(pool),
            "attack":         values(filter_by_type(pool, "Attack")),
            "skill":          values(filter_by_type(pool, "Skill")),
            "power":          values(filter_by_type(pool, "Power")),
            "common":         values(filter_common(pool)),
            "common_any":     values(filter_common(pool)),
            # free-use variants — for cards that play the drawn card immediately
            # at 0 cost (WHITE_NOISE / DISTRACTION / JACKPOT / CASCADE).
            "all_free":       values(pool, free=True),
            "skill_free":     values(filter_by_type(pool, "Skill"), free=True),
            "power_free":     values(filter_by_type(pool, "Power"), free=True),
            "attack_free":    values(filter_by_type(pool, "Attack"), free=True),
            # LARGESSE — "other player colorless" simplifies to SHARED pool.
            "colorless":      colorless_vals,
        }

        summaries = {name: summarize(vs, rng) for name, vs in filters.items()}
        out["characters"][character] = summaries

        # Coverage report.
        for name, vs in filters.items():
            report_lines.append(
                f"{character:<11} {name:<13} n={len(vs):>3}  mean={summaries[name]['mean']:>4}  "
                f"top1of3={summaries[name]['top1of3']:>4}  top1of5={summaries[name]['top1of5']:>4}"
            )

    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
    REPORT_PATH.write_text("\n".join(report_lines) + "\n", encoding="utf-8")
    print(f"[pool_means] wrote {OUT_PATH.relative_to(REPO_ROOT)}", file=sys.stderr)
    print(f"[pool_means] wrote {REPORT_PATH.relative_to(REPO_ROOT)}", file=sys.stderr)


if __name__ == "__main__":
    main()
