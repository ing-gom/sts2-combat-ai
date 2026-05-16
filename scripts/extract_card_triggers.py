"""
Extract card selector-mode hints from the master cards_catalog.json into a tiny
JSON the mod can embed. Eliminates the hardcoded BoostCards list in
SelectorMode.cs — game patches that rename cards now only need the master
catalog refreshed (via headless-sync), not source code changes.

Run from repo root:
    python scripts/extract_card_triggers.py

Output: Sts2CombatAICode/Core/Data/card_triggers.json

Schema:
{
  "version": "0.103.2",          # from catalog
  "cards": {
    "CARD.APOTHEOSIS": {
      "axes": ["EXHAUST_TAG", "SCALING", "INNATE_SELF", ...],
      "upgrade_trigger": true,     # description contains "강화" + non-self target
      "fetch_trigger": true,       # description contains 가져옴 / fetch axes
      "exhaust": true              # Exhaust keyword
    },
    ...
  }
}
"""

import json
from pathlib import Path

# Boost-mode signals — when a card with one of these axes triggers a card-select
# prompt, the prompt wants the BEST card kept (not the worst burned).
BOOST_AXES = {
    "DRAW_PILE_SEARCH",   # Anointed-style fetch from draw pile
    "CARD_RETURN",        # bring card from discard back to hand
}

# Korean description keywords (catalog descriptions are Korean).
UPGRADE_KW = "강화"      # "upgrade"
FETCH_KW = "가져옵니다"   # "fetch / bring to hand"
DISCOVERY_KW = "생성"    # "create / generate"


def main():
    repo_root = Path(__file__).resolve().parent.parent
    catalog_path = repo_root / "scripts" / "cards_catalog.json"
    out_path = repo_root / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"

    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    out_cards = {}

    for c in catalog["cards"]:
        # Only base (un-upgraded) entries — upgraded versions share the ID
        if c.get("is_upgraded"):
            continue

        card_id = c["id"]
        axes = c.get("axes", [])
        keywords = c.get("keywords", [])
        desc = c.get("description", "") or ""

        entry = {}

        # All axes — used for build synergy (POISON_PRODUCER + POISON_AMPLIFIER, etc.)
        # and mode inference. Filtering at runtime is cheaper than re-extracting.
        if axes:
            entry["axes"] = axes

        # Build memberships (e.g., "독 빌드" / "광역 빌드") — for character-build-aware
        # scoring. Each entry: {"tag": "독 빌드", "role": "primary" | "secondary"}.
        if c.get("builds"):
            entry["builds"] = [{"tag": b["tag"], "role": b.get("role", "secondary")}
                               for b in c["builds"]]

        # description-derived flags
        if UPGRADE_KW in desc:
            entry["upgrade_trigger"] = True
        if FETCH_KW in desc or DISCOVERY_KW in desc:
            entry["fetch_trigger"] = True

        # keywords we care about for the simulator (rest already comes from vars
        # via runtime reflection: Damage/Block/Cards/Energy etc.)
        if "Exhaust" in keywords:
            entry["exhaust"] = True
        if "Ethereal" in keywords:
            entry["ethereal"] = True
        if "Retain" in keywords:
            entry["retain"] = True
        if "Innate" in keywords:
            entry["innate"] = True

        if entry:
            out_cards[card_id] = entry

    payload = {
        "version": catalog.get("game_version", "?"),
        "generated_from": str(catalog_path.relative_to(repo_root)),
        "card_count": len(out_cards),
        "cards": out_cards,
    }

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Wrote {len(out_cards)} entries to {out_path}")
    print(f"Size: {out_path.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
