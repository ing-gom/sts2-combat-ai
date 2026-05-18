"""POISON/DOOM card audit — list cards by type and check sequencing-tier coverage.

Cross-checks card_triggers.json (axes) against PowerSequencingTier.cs and
SkillSequencingTier.cs to find cards whose ordering hook is missing.
"""
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
TRIGGERS = ROOT / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"
CATALOG = ROOT.parent / "scripts" / "cards_catalog.json"
POWER_TIER = ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "PowerSequencingTier.cs"
SKILL_TIER = ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "SkillSequencingTier.cs"

triggers = json.loads(TRIGGERS.read_text(encoding="utf-8"))["cards"]
catalog_data = json.loads(CATALOG.read_text(encoding="utf-8"))
catalog = catalog_data["cards"]

# Catalog is list of cards (base + upgraded); only keep base entries.
cat_by_id = {}
for c in catalog:
    if not isinstance(c, dict): continue
    if c.get("is_upgraded"): continue
    cid = c.get("id", "")
    if cid: cat_by_id[cid] = c

POISON_STEMS = ("POISON",)
DOOM_STEMS = ("DOOM",)
DOT_STEMS = ("POISON", "DOOM", "BURN", "CONSTRICT")  # all DoT-style setup axes

power_tier_src = POWER_TIER.read_text(encoding="utf-8")
skill_tier_src = SKILL_TIER.read_text(encoding="utf-8")
# Extract registered power names from PowerSequencingTier dict
power_registered = set(re.findall(r'"\s*(\w+Power)\s*"', power_tier_src))

# Categorise relevant cards
buckets = {}  # (stem, kind) -> list of cards
for cid, t in triggers.items():
    axes = set(t.get("axes", []))
    matched_stems = []
    for axis in axes:
        for stem in DOT_STEMS:
            # POISON_PRODUCER, POISON_CONSUMER, POISON_AMPLIFIER, POISON, DOOM, etc.
            if axis == stem or axis.startswith(stem + "_"):
                matched_stems.append(stem)
                break
    if not matched_stems:
        continue
    cat_entry = cat_by_id.get(cid, {})
    ctype = cat_entry.get("type") or t.get("type") or "?"
    char  = cat_entry.get("character", t.get("character", "?"))
    tier  = cat_entry.get("tier", "?")
    vars_dict = cat_entry.get("vars", {}) or {}
    var_powers = [k for k in vars_dict if k.endswith("Power")]
    desc = cat_entry.get("description", "")
    for stem in set(matched_stems):
        # Determine card's relationship to stem (producer/consumer/amplifier)
        role = "?"
        if any(a == f"{stem}_PRODUCER" or a == stem for a in axes):
            role = "P"
        if any(a == f"{stem}_AMPLIFIER" for a in axes):
            role = "A" if role == "?" else role + "/A"
        if any(a == f"{stem}_CONSUMER" for a in axes):
            role = "C" if role == "?" else role + "/C"
        buckets.setdefault((stem, ctype), []).append({
            "id": cid,
            "char": char,
            "tier": tier,
            "role": role,
            "axes": sorted(axes),
            "var_powers": var_powers,
            "desc": desc[:80],
        })

# Print summary
print("="*100)
print("POISON / DOOM / BURN / CONSTRICT — cards by type")
print("="*100)
for stem in DOT_STEMS:
    types = sorted({k[1] for k in buckets if k[0] == stem})
    if not types:
        print(f"\n[{stem}] — no cards")
        continue
    print(f"\n[{stem}]")
    for ctype in types:
        cards = buckets.get((stem, ctype), [])
        print(f"  --- {ctype} ({len(cards)}) ---")
        for c in cards:
            star = ""
            # Power card → check PowerSequencingTier registration
            if ctype == "Power":
                # Try to find matching *Power name from vars or id-derived
                id_short = c["id"].replace("CARD.", "")
                derived = "".join(part.capitalize() for part in id_short.split("_")) + "Power"
                hit_via_var = any(vp in power_registered for vp in c["var_powers"])
                hit_via_derived = derived in power_registered
                star = "" if (hit_via_var or hit_via_derived) else "  *** NO POWER-TIER HIT ***"
            print(f"    {c['id']:<35} [{c['char']:<10}] tier={c['tier']:<3} role={c['role']:<6} {c['desc']}{star}")

print()
print("="*100)
print("SkillSequencingTier coverage check — Skills/Attacks with these stems")
print("="*100)
# SkillSequencingTier currently triggers Setup only on VulnerablePower/WeakPower or VULN/WEAK axes.
# Show which Poison/Doom/Burn producer Skills are NOT classified as Setup.
print()
print("Skills that apply poison/doom/burn/constrict (would benefit from Setup tier):")
for stem in DOT_STEMS:
    cards = buckets.get((stem, "Skill"), [])
    producers = [c for c in cards if c["role"] == "P" or c["role"].startswith("P/")]
    if not producers:
        continue
    print(f"\n  [{stem}_PRODUCER skills]")
    for c in producers:
        print(f"    {c['id']:<35} tier={c['tier']:<3} axes={','.join(a for a in c['axes'] if stem in a)}")

print()
print("="*100)
print("Post-patch verification — Skill cards now classified as Setup tier")
print("="*100)
SETUP_AXES = {
    "POISON_PRODUCER", "POISON_AMPLIFIER",
    "DOOM_PRODUCER",   "DOOM_AMPLIFIER",
    "BURN_PRODUCER",   "BURN_AMPLIFIER",
    "CONSTRICT_PRODUCER",
    # existing
    "VULN_PRODUCER", "VULN", "WEAK_PRODUCER", "WEAK",
}
SETUP_VAR_POWERS = {"VulnerablePower", "WeakPower",
                    "PoisonPower", "DoomPower", "BurnPower", "ConstrictPower"}
flipped = []
for cid, t in triggers.items():
    cat_entry = cat_by_id.get(cid)
    if not cat_entry: continue
    if cat_entry.get("type") != "Skill": continue
    axes = set(t.get("axes", []))
    var_powers = set((cat_entry.get("vars", {}) or {}).keys())
    if axes & SETUP_AXES or var_powers & SETUP_VAR_POWERS:
        was_classified_before = any(a in axes for a in ["VULN_PRODUCER","VULN","WEAK_PRODUCER","WEAK"]) \
            or any(v in var_powers for v in ["VulnerablePower","WeakPower"])
        if not was_classified_before:
            flipped.append(cid)
print(f"\nSkill cards newly classified as Setup tier (DoT path): {len(flipped)}")
for cid in sorted(flipped):
    cat = cat_by_id[cid]
    relevant_axes = [a for a in triggers[cid].get("axes", [])
                     if any(s in a for s in ["POISON","DOOM","BURN","CONSTRICT"])]
    print(f"  {cid:<35} [{cat.get('character','?'):<10}] tier={cat.get('tier','?'):<3} axes={','.join(relevant_axes)}")

print()
print("="*100)
print("Attacks that apply poison/doom (would benefit from setup-aware bonus):")
print("="*100)
for stem in DOT_STEMS:
    cards = buckets.get((stem, "Attack"), [])
    producers = [c for c in cards if c["role"] == "P" or c["role"].startswith("P/")]
    if not producers:
        continue
    print(f"\n  [{stem}_PRODUCER attacks]")
    for c in producers:
        print(f"    {c['id']:<35} tier={c['tier']:<3} axes={','.join(a for a in c['axes'] if stem in a)}")
