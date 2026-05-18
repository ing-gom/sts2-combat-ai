"""Mechanism-by-mechanism ordering coverage audit.

For each pair-axis stem in the catalog, reports:
 - producer / amplifier / consumer counts by card type
 - whether SkillSequencingTier classifies its Skill PRODUCER as Setup
 - whether EffectSynergy provides a CONSUMER context bonus
 - whether BuildSynergy provides a generic pair bonus (always yes, for context)
 - whether SimState/SimEnemy exposes the underlying stack (for stack-aware bonus)
"""
import json
import re
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
TRIGGERS = ROOT / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"
CATALOG  = ROOT.parent / "scripts" / "cards_catalog.json"
SKILL_TIER = ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "SkillSequencingTier.cs"
EFFECT_SYN = ROOT / "Sts2CombatAICode" / "Core" / "Planner" / "EffectSynergy.cs"
SIM_STATE  = ROOT / "Sts2CombatAICode" / "Core" / "Sim" / "SimState.cs"
SIM_ENEMY  = ROOT / "Sts2CombatAICode" / "Core" / "Sim" / "SimEnemy.cs"

triggers = json.loads(TRIGGERS.read_text(encoding="utf-8"))["cards"]
catalog_data = json.loads(CATALOG.read_text(encoding="utf-8"))
catalog = catalog_data["cards"]
cat_by_id = {}
for c in catalog:
    if not isinstance(c, dict): continue
    if c.get("is_upgraded"): continue
    cid = c.get("id", "")
    if cid: cat_by_id[cid] = c

# Discover all stems from PRODUCER axes in the catalog.
stems = set()
for cid, t in triggers.items():
    for a in t.get("axes", []):
        for suffix in ("_PRODUCER", "_AMPLIFIER", "_CONSUMER"):
            if a.endswith(suffix):
                stems.add(a[:-len(suffix)])

# Bucket cards per (stem, role, type).
buckets = defaultdict(lambda: defaultdict(list))  # buckets[stem][(role, type)] = [cards]
for cid, t in triggers.items():
    axes = set(t.get("axes", []))
    cat_entry = cat_by_id.get(cid, {})
    ctype = cat_entry.get("type", "?")
    tier  = cat_entry.get("tier", "?")
    char  = cat_entry.get("character", "?")
    for stem in stems:
        roles = []
        if stem + "_PRODUCER"  in axes: roles.append("P")
        if stem + "_AMPLIFIER" in axes: roles.append("A")
        if stem + "_CONSUMER"  in axes: roles.append("C")
        for r in roles:
            buckets[stem][(r, ctype)].append({
                "id": cid, "tier": tier, "char": char,
            })

# Parse SkillSequencingTier — which stems flip producer to Setup?
skill_tier_src = SKILL_TIER.read_text(encoding="utf-8")
# Find every quoted POWER name and AXIS suffix
setup_axes = set()
for m in re.finditer(r'"\s*(\w+_PRODUCER|\w+_AMPLIFIER|\w+_CONSUMER)\s*"', skill_tier_src):
    setup_axes.add(m.group(1))
setup_power_apps = set()
for m in re.finditer(r'PowerApps\.ContainsKey\("(\w+Power)"\)', skill_tier_src):
    setup_power_apps.add(m.group(1))
# Generic stem allowlist (PairStemsForSetup HashSet)
pair_stems_match = re.search(r'PairStemsForSetup\s*=\s*new\(\)\s*\{([^}]+)\}', skill_tier_src, re.DOTALL)
generic_setup_stems = set()
if pair_stems_match:
    for m in re.finditer(r'"(\w+)"', pair_stems_match.group(1)):
        generic_setup_stems.add(m.group(1))
# Translate generic stems → setup_axes membership (auto _PRODUCER/_AMPLIFIER)
for s in generic_setup_stems:
    setup_axes.add(s + "_PRODUCER")
    setup_axes.add(s + "_AMPLIFIER")

# Parse EffectSynergy — which stems get CONSUMER/AMPLIFIER state-aware bonuses?
effect_src = EFFECT_SYN.read_text(encoding="utf-8")
effect_axes = set()
for m in re.finditer(r'Contains\("(\w+_CONSUMER|\w+_AMPLIFIER|\w+_PAYOFF)"\)', effect_src):
    effect_axes.add(m.group(1))
# DotStems registry pickup
dot_stems_match = re.search(r'DotStems\s*=\s*\{\s*([^}]+)\}', effect_src)
dot_registry = set()
if dot_stems_match:
    for m in re.finditer(r'"(\w+)"', dot_stems_match.group(1)):
        dot_registry.add(m.group(1))
# Stems handled in EffectSynergy via DotStems registry implicitly cover _CONSUMER and _AMPLIFIER
for s in dot_registry:
    effect_axes.add(s + "_CONSUMER")
    effect_axes.add(s + "_AMPLIFIER")

# SimState/SimEnemy stack visibility
sim_state_src = SIM_STATE.read_text(encoding="utf-8")
sim_enemy_src = SIM_ENEMY.read_text(encoding="utf-8")
stack_known = {
    "STRENGTH": "PlayerStrength / e.StrengthAmount",
    "DEXTERITY": "PlayerDexterity",
    "FOCUS":     "PlayerFocus",
    "VULN":      "PlayerVulnerable / e.VulnerableAmount",
    "WEAK":      "PlayerWeak / e.WeakAmount",
    "FRAIL":     "PlayerFrail",
    "INTANGIBLE":"PlayerIntangible",
    "POISON":    "e.PoisonAmount",
    "BURN":      "e.BurnAmount",
    "CONSTRICT": "e.ConstrictAmount",
    "STAR":      "PlayerStars",
    "ORB":       "PlayerOrbCount / OrbQueue",
    "DOOM":      "e.Powers['DoomPower']",
    "SOUL":      "SoulInPiles (v0.6.7)",
    "SHIV":      "ShivInPiles (v0.6.7)",
    "SKELETON":  "SkeletonCount (v0.6.7)",
    "EXHAUST":   "ExhaustPileSize (v0.6.7)",
    "FORGE":     "SovereignBladeCount (v0.6.7)",
    "LORDS_BLADE": "SovereignBladeCount (v0.6.7)",
    "VOLATILE":  "Hand.IsEthereal count (v0.6.7)",
    "DARK_ORB":  "OrbQueue Dark count",
    "CUNNING":   "Hand.IsSly count (v0.6.7)",
}

def fmt_card_list(cards, max_show=4):
    cards = sorted(cards, key=lambda c: ("SABCD?".index(c["tier"]) if c["tier"] in "SABCD" else 5, c["id"]))
    shown = ", ".join(c["id"].replace("CARD.","") + f"[{c['tier']}]" for c in cards[:max_show])
    if len(cards) > max_show:
        shown += f", +{len(cards) - max_show}"
    return shown

# ===== Report =====
print("="*100)
print("ORDERING COVERAGE MATRIX — per pair-axis stem")
print("="*100)
print()

# Sort by total card count desc
def stem_sort_key(stem):
    total = sum(len(v) for v in buckets[stem].values())
    return -total

cols = ("stem", "P", "A", "C", "BuildSyn", "SkillSetup", "EffSyn", "StackKnown")

# Per-stem coverage check
def has_setup(stem):
    return (stem + "_PRODUCER" in setup_axes) or (stem + "_AMPLIFIER" in setup_axes)
def has_effect_syn(stem):
    return (stem + "_CONSUMER" in effect_axes) or (stem + "_AMPLIFIER" in effect_axes) or (stem + "_PAYOFF" in effect_axes)

# Only show stems where producer count >= 1 AND (amp or consumer) >= 1 — complete pairs
print(f"{'Stem':<18}{'P':>4} {'A':>4} {'C':>4}  {'BuildSyn':<9} {'SkillSetup':<11} {'EffSyn':<7} {'Stack':<26}")
print("-"*100)
for stem in sorted(stems, key=stem_sort_key):
    p_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "P")
    a_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "A")
    c_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "C")
    if p_total == 0 and a_total == 0 and c_total == 0: continue
    is_complete = p_total >= 1 and (a_total >= 1 or c_total >= 1)
    if not is_complete: continue  # focus on complete pairs

    flag_setup = "YES" if has_setup(stem) else "-"
    flag_eff   = "YES" if has_effect_syn(stem) else "-"
    flag_stack = stack_known.get(stem, "-")
    print(f"{stem:<18}{p_total:>4} {a_total:>4} {c_total:>4}  {'YES':<9} {flag_setup:<11} {flag_eff:<7} {flag_stack:<26}")

print()
print("="*100)
print("Producer breakdown by card TYPE (Attack/Skill/Power) for complete-pair stems")
print("="*100)
print()
for stem in sorted(stems, key=stem_sort_key):
    p_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "P")
    a_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "A")
    c_total = sum(len(b) for (r,_), b in buckets[stem].items() if r == "C")
    if p_total == 0 or (a_total == 0 and c_total == 0): continue
    print(f"[{stem}]  (P={p_total}, A={a_total}, C={c_total})")
    for role in ("P", "A", "C"):
        by_type = defaultdict(list)
        for (r, ctype), cards in buckets[stem].items():
            if r != role: continue
            by_type[ctype].extend(cards)
        if not by_type: continue
        for ctype in sorted(by_type.keys()):
            cards = by_type[ctype]
            print(f"    {role}-{ctype:<6} ({len(cards):>2}): {fmt_card_list(cards, 5)}")
    print()
