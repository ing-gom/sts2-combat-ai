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
# "Choose a card and use/copy" — DECISIONS_DECISIONS pattern. Selector should
# pick the BEST card from hand because the chosen card will be played/copied.
# Examples (현재 STS2 v0.103.x 카탈로그):
#   • DECISIONS_DECISIONS: "선택해 3번 사용합니다"
SELECT_USE_KW = "선택해"
SELECT_USE_FOLLOWUPS = ("사용", "복사")
# "Choose a card and transform" — CHARGE / BEGONE pattern. Selector should
# pick the WORST card because the chosen one is REPLACED by a different card
# (Minion Dive Bombs+ / Minion Strike+). Replacing trash → upgrade; replacing
# value → loss. Without this flag, DRAW_PILE_SEARCH-axis transformers like
# CHARGE incorrectly route to Boost.
TRANSFORM_KW = "변화"
# "Choose a hand card and place on top of draw pile" — GLIMMER / PHOTON_CUT
# pattern. Implicit selection (no "선택해" word). The chosen card is
# guaranteed to be drawn first next turn — pick the BEST card. Detection
# requires "손" + "1장" + "맨 위" so NOSTALGIA-style passive top-deck
# ("매 턴마다 처음 ... 맨 위") doesn't false-match.
TOPDECK_HAND_KW = "손"
TOPDECK_ONE_KW = "1장"
TOPDECK_TOP_KW = "맨 위"


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

        # Var-based axis derivation. cards_catalog.json's `vars` field exposes the
        # token counts a card grants (e.g. {"Shivs": 2} on LEADING_STRIKE). The
        # catalog `axes` field is hand-curated and sometimes misses the producer
        # tag when generation is a side effect. Auto-derive so:
        #   • LEADING_STRIKE / HIDDEN_DAGGERS / CLOAK_AND_DAGGER → SHIV_PRODUCER + CARD_GEN
        #   • Skeleton-summon cards → SKELETON_PRODUCER + CARD_GEN
        #   • Star/Soul/Forge generators → matching _PRODUCER + CARD_GEN
        # Keeps the master catalog as source of truth while filling the gap.
        vars_dict = c.get("vars", {}) or {}
        VAR_TO_PRODUCER = {
            "Shivs":     "SHIV_PRODUCER",
            "Skeletons": "SKELETON_PRODUCER",
            "Stars":     "STAR_PRODUCER",
            "Souls":     "SOUL_PRODUCER",
        }
        # Korean token keywords in descriptions (catalog is Korean). Used when
        # vars omits the token key (e.g. CLOAK_AND_DAGGER stores "Cards: 1"
        # instead of "Shivs: 1"; STORM_OF_STEEL stores nothing because count
        # is dynamic). Detection: keyword present AND card has "Cards" var OR
        # description mentions adding-to-hand ("손으로 가져옵니다" / "받습니다").
        DESC_TOKEN_TO_PRODUCER = {
            "단도":     "SHIV_PRODUCER",
            "해골":     "SKELETON_PRODUCER",  # Necrobinder skeleton token
            "골골이":   "SKELETON_PRODUCER",  # alt spelling
        }
        # Strip BBCode-style color tags before keyword search so wording split
        # by tag boundaries (e.g. "손[/gold]으로 가져옵니다") still matches.
        import re as _re
        desc_plain = _re.sub(r'\[/?[a-z]+\]', '', desc)
        adds_card = vars_dict.get("Cards", 0) > 0 or "손으로 가져옵니다" in desc_plain

        derived = []
        for var_key, producer_axis in VAR_TO_PRODUCER.items():
            if vars_dict.get(var_key, 0) and producer_axis not in axes:
                derived.append(producer_axis)
        # Description-based fallback for cards whose vars don't expose the
        # token count by name. Only fires when card visibly adds cards
        # (Cards var > 0 or explicit "to hand" wording), so we don't tag
        # cards that merely reference the token in flavor text.
        if adds_card:
            for token, producer_axis in DESC_TOKEN_TO_PRODUCER.items():
                if token in desc_plain and producer_axis not in axes and producer_axis not in derived:
                    derived.append(producer_axis)

        # If any token generation derived, also ensure CARD_GEN tag is present
        # (drives ApplyCardGen flat bonus + ApplyCardCreateTriggerPreview for
        # Arsenal/Pillar/Smokestack/Trash power triggers).
        if derived and "CARD_GEN" not in axes:
            derived.append("CARD_GEN")
        if derived:
            axes = list(axes) + derived

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
        # Select-and-use pattern: card prompts "select 1 card in hand and
        # use/copy it" (DECISIONS_DECISIONS, etc.). VakuuCardSelector must use
        # BOOST mode here (pick the best skill), not the default BURN.
        if SELECT_USE_KW in desc and any(fw in desc for fw in SELECT_USE_FOLLOWUPS):
            entry["select_play_trigger"] = True
        # Select-and-transform pattern: card prompts "select N cards and turn
        # them into X" (CHARGE → Minion Dive Bombs+, BEGONE → Minion Strike+).
        # The chosen card is REPLACED; selector must pick WORST so the
        # transform gains the most. Overrides Boost-leaning axes like
        # DRAW_PILE_SEARCH downstream in SelectorModeCatalog.
        if SELECT_USE_KW in desc and TRANSFORM_KW in desc:
            entry["transform_trigger"] = True
        # Hand → top-of-draw-pile pattern (GLIMMER / PHOTON_CUT). The chosen
        # card is drawn first next turn — pick BEST. Conjunction of three
        # tokens excludes NOSTALGIA-style passive top-deck triggers that
        # mention "맨 위" without manual hand selection.
        if TOPDECK_HAND_KW in desc and TOPDECK_ONE_KW in desc and TOPDECK_TOP_KW in desc:
            entry["select_topdeck_trigger"] = True

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
