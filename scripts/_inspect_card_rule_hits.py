"""Per-card rule-hit audit.

For each base card in card_triggers.json, computes which PlanScorer evaluation
rules can fire on it (statically). Reports cards that hit 0 explicit rules
(other than the universal BuildSynergy / base type score) — these are the real
"blind spots" where the planner picks them via raw damage / cost only.

Rules considered:
 1. PowerCatalog                 — Power cards only
 2. PowerSequencingTier          — Power cards only
 3. SkillSequencingTier (Setup/Cantrip/Defensive) — Skill cards only
 4. EffectSynergy (any handler)  — Attack/Skill
 5. AmplifierSynergy             — POWER_AMPLIFIER/REPLAY/ATTACK_REPLAY*/SKILL_REPLAY
 6. HandSynergy                  — Strength/Dex/Vuln/Weak vars
 7. CardOverrideCatalog          — hand-tuned overrides
 8. BuildSynergy pair            — any *_PRODUCER/_AMPLIFIER/_CONSUMER axis
 9. BuildSynergy commitment      — primary build tag

A card is "blind" when it hits NONE of rules 1-7 and ONLY rule 8 generic pair
(no commitment tag, no axis stem match).
"""
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
TRIGGERS = ROOT / "Sts2CombatAICode" / "Core" / "Data" / "card_triggers.json"
CATALOG = ROOT.parent / "scripts" / "cards_catalog.json"
SRC = ROOT / "Sts2CombatAICode" / "Core" / "Planner"

triggers = json.loads(TRIGGERS.read_text(encoding="utf-8"))["cards"]
catalog_data = json.loads(CATALOG.read_text(encoding="utf-8"))
cat_by_id = {}
for c in catalog_data["cards"]:
    if not isinstance(c, dict): continue
    if c.get("is_upgraded"): continue
    if c.get("id"): cat_by_id[c["id"]] = c

def read(name):
    return (SRC / name).read_text(encoding="utf-8")

# Parse PowerCatalog — list of registered power names
pc_src = read("PowerCatalog.cs")
pc_powers = set(re.findall(r'\{\s*"\s*(\w+Power)\s*"', pc_src))

# Parse PowerSequencingTier (already 100% from earlier audit)
pst_src = read("PowerSequencingTier.cs")
pst_powers = set(re.findall(r'"\s*(\w+Power)\s*"', pst_src))

# Parse SkillSequencingTier — Setup axes / PowerApps + PairStemsForSetup
sst_src = read("SkillSequencingTier.cs")
sst_axes_quoted = set(re.findall(r'"\s*(\w+_PRODUCER|\w+_AMPLIFIER|\w+_CONSUMER|VULN|WEAK)\s*"', sst_src))
sst_power_apps = set(re.findall(r'PowerApps\.ContainsKey\("(\w+Power)"\)', sst_src))
pair_match = re.search(r'PairStemsForSetup\s*=\s*new\(\)\s*\{([^}]+)\}', sst_src, re.DOTALL)
generic_setup_stems = set()
if pair_match:
    for m in re.finditer(r'"(\w+)"', pair_match.group(1)):
        generic_setup_stems.add(m.group(1))

# Build full Setup-axis allowlist
setup_axes = set(sst_axes_quoted)
for s in generic_setup_stems:
    setup_axes.add(s + "_PRODUCER")
    setup_axes.add(s + "_AMPLIFIER")

# Parse EffectSynergy — axes recognized
es_src = read("EffectSynergy.cs")
es_axes = set(re.findall(r'axes\.Contains\("(\w+_?\w*)"\)', es_src))
# DotStems registry
ds_match = re.search(r'DotStems\s*=\s*\{([^}]+)\}', es_src)
if ds_match:
    for m in re.finditer(r'"(\w+)"', ds_match.group(1)):
        es_axes.add(m.group(1) + "_CONSUMER")
        es_axes.add(m.group(1) + "_AMPLIFIER")

# Parse CardOverrideCatalog — list of overridden card ids
oc_src = read("CardOverrideCatalog.cs")
oc_ids = set(re.findall(r'"(CARD\.\w+)"', oc_src))

# Build per-card hit map
HAND_SYNERGY_VARS = {"StrengthPower", "TemporaryStrengthPower",
                      "DexterityPower", "TemporaryDexterityPower",
                      "VulnerablePower", "WeakPower",
                      "RagePower"}  # v0.6.8 — RAGE via id-gated CardReflection mapping
AMPLIFIER_AXES = {"POWER_AMPLIFIER", "REPLAY", "ATTACK_REPLAY",
                   "ATTACK_REPLAY_RANDOM", "SKILL_REPLAY"}

rule_hits = {}  # cid -> set of rule names
for cid, t in triggers.items():
    cat = cat_by_id.get(cid, {})
    ctype = cat.get("type", "?")
    axes = set(t.get("axes", []))
    var_powers = set((cat.get("vars", {}) or {}).keys())
    builds = t.get("builds", [])
    is_curse_status = ctype in ("Status", "Curse")
    hits = set()

    # 1. PowerCatalog
    if ctype == "Power":
        id_short = cid.replace("CARD.", "")
        derived = "".join(p.capitalize() for p in id_short.split("_")) + "Power"
        if any(vp in pc_powers for vp in var_powers) or derived in pc_powers:
            hits.add("PowerCatalog")
        # 2. PowerSequencingTier (same lookup logic)
        if any(vp in pst_powers for vp in var_powers) or derived in pst_powers:
            hits.add("PowerSequencingTier")

    # 3. SkillSequencingTier (Skill cards)
    if ctype == "Skill":
        if var_powers & sst_power_apps:
            hits.add("SkillSetup")
        elif axes & setup_axes:
            hits.add("SkillSetup")
        # Cantrip — Draw/Energy
        elif "DRAW" in axes or "ENERGY" in axes or "CARD_GEN" in axes:
            hits.add("SkillCantrip")
        # Defensive — Self-block (need block + self target — proxy: BLOCK axis + no _PRODUCER/_AMPLIFIER suffix)
        elif "BLOCK" in axes:
            hits.add("SkillDefensive")

    # 4. EffectSynergy — any axis matches
    if ctype in ("Attack", "Skill") and (axes & es_axes):
        hits.add("EffectSynergy")
    # v0.6.9 — new axis-driven handlers
    if ctype in ("Attack", "Skill", "Power") and any(a in axes for a in [
        "STATUS_TO_HAND", "STATUS_CONSUMER",
        "DRAW_CONDITIONAL", "CARD_RETURN", "DRAW_PILE_SEARCH",
        "ATTACK_COST_ENABLER", "SKILL_COST_ENABLER", "POWER_COST_ENABLER",
        "CARD_GEN", "EXHAUST_TARGET_RANDOM"]):
        hits.add("EffectSynergy")
    # v0.6.9 — MaxHp gain via CardEffectSummary
    if "MaxHp" in var_powers:
        hits.add("EffectSynergy")
    # v0.6.9 — FocusPower vars → HandSynergy
    if any(k in var_powers for k in ("FocusPower","TemporaryFocusPower","CalculatedFocus")):
        hits.add("HandSynergy")

    # v0.6.9 — id-fallback random-card-gen
    if cid in ("CARD.WHITE_NOISE","CARD.DISCOVERY","CARD.DISTRACTION","CARD.WISH",
                "CARD.LARGESSE","CARD.SPLASH","CARD.PRECISE_CUT",
                "CARD.ENLIGHTENMENT"):
        hits.add("EffectSynergy")
    # v0.7.1 — Level 3 pile-based handlers
    if cid in ("CARD.CASCADE","CARD.CATASTROPHE","CARD.UPROAR","CARD.BEAT_DOWN",
                "CARD.HIDDEN_GEM","CARD.DRAIN_POWER","CARD.WISH"):
        hits.add("EffectSynergy")
    # v0.6.9 — OSTY-conditional (gated on SkeletonCount)
    if "OSTY" in axes and not any(a in axes for a in ("SKELETON_CONSUMER","SKELETON_AMPLIFIER","SKELETON_PRODUCER")):
        hits.add("EffectSynergy")
    # v0.6.9 — VigorPower → HandSynergy
    if "VigorPower" in var_powers:
        hits.add("HandSynergy")

    # === Audit accuracy fixes — paths the static check originally missed ===
    # ORB mechanic: BuildSynergy.Compute handles ChannelCount/EvokeCount + full/empty
    if any(a in axes for a in ("ORB_PRODUCER","ORB_CONSUMER","ORB_AMPLIFIER","ORB_EVOKE",
                                 "FROST_ORB","LIGHTNING_ORB","DARK_ORB","GLASS_ORB","PLASMA_ORB")):
        hits.add("OrbMechanic")
    # PowerVar<T> applications detected at runtime — static catalog dump shows
    # 'VulnerablePower'/'WeakPower'/etc. as vars key on the card.
    if any(p in var_powers for p in ("StrengthPower","TemporaryStrengthPower",
                                      "DexterityPower","TemporaryDexterityPower",
                                      "VulnerablePower","WeakPower",
                                      "FrailPower")):
        hits.add("HandSynergy")
        hits.add("PowerCatalog")  # also valued via PowerCatalog.ValueEnemyDebuff/SelfBuff
    # Raw-damage Attack: any Attack card with Damage var ≥ 1 gets base damage
    # scoring (StatusMath.EffectiveAttackDmg + per-enemy AOE handling). Not
    # "blind" in any meaningful sense.
    if ctype == "Attack":
        sample_card = cat_by_id.get(cid, {})
        if (sample_card.get("damage") or 0) >= 1:
            hits.add("RawDamage")
    # Raw-block Skill: same for Block
    if ctype == "Skill":
        sample_card = cat_by_id.get(cid, {})
        if (sample_card.get("block") or 0) >= 1:
            hits.add("RawBlock")
    # PINPOINT-style SKILL_CONDITIONAL is cost-discount, handled by EnergyCost.AddThisTurn
    # → CardReflection.GetCost returns discounted value automatically
    if "SKILL_CONDITIONAL" in axes or "ATTACK_CONDITIONAL" in axes:
        hits.add("CostDiscountAuto")

    # 5. AmplifierSynergy
    if axes & AMPLIFIER_AXES:
        hits.add("AmplifierSynergy")

    # 6. HandSynergy — vars contain one of the recognized Power names
    if var_powers & HAND_SYNERGY_VARS:
        hits.add("HandSynergy")
    # 6b. RAGE special — DynamicVar("Power") → RagePower via CardReflection id-gate
    if cid == "CARD.RAGE":
        hits.add("HandSynergy")

    # v0.6.7+ — special evaluation paths
    # STRENGTH_DOWN / HEAL via DynamicVar amount (CardEffectSummary fields)
    if "StrengthLoss" in var_powers or "EnemyStrengthLoss" in var_powers:
        hits.add("EffectSynergy")  # ApplyStrengthDown
    if "Heal" in var_powers:
        hits.add("EffectSynergy")  # ApplyHeal

    # v0.6.7 — Variable damage Attack patterns (EstimateVariableHits in PlanScorer)
    if ctype == "Attack" and ("EXHAUST_BURST" in axes or "X_COST" in axes):
        hits.add("VariableDamage")
    # v0.6.8 — TEAR_ASUNDER id-gated
    if cid == "CARD.TEAR_ASUNDER":
        hits.add("VariableDamage")

    # v0.6.7 — Variable block Skill (EstimateBlockMultiplier)
    if ctype == "Skill" and "EXHAUST_BURST" in axes:
        hits.add("VariableBlock")

    # v0.6.8 — EIDOLON / STOKE / PURITY id-gated special effect
    if cid in ("CARD.EIDOLON", "CARD.STOKE", "CARD.PURITY"):
        hits.add("ExhaustBurstSpecial")

    # 7. CardOverrideCatalog
    if cid in oc_ids:
        hits.add("Override")

    # 8. BuildSynergy pair — any *_PRODUCER/_AMPLIFIER/_CONSUMER axis
    has_pair_axis = any(
        a.endswith("_PRODUCER") or a.endswith("_AMPLIFIER") or a.endswith("_CONSUMER")
        for a in axes
    )
    if has_pair_axis:
        hits.add("BuildSynergyPair")
    # 9. BuildSynergy commitment — primary build tag
    if any(b.get("role") == "primary" for b in builds):
        hits.add("BuildSynergyCommit")

    rule_hits[cid] = (hits, ctype, cat.get("tier", "?"), cat.get("character", "?"),
                       is_curse_status, axes, var_powers)

# Summary stats
print("="*100)
print("PER-CARD RULE-HIT SUMMARY")
print("="*100)
total = len(rule_hits)
print(f"\nTotal base cards: {total}")
hit_counter = Counter()
for cid, (hits, *_) in rule_hits.items():
    hit_counter[len(hits)] += 1
print("\nDistribution of rule-hit count per card:")
for n in sorted(hit_counter):
    pct = hit_counter[n] / total * 100
    print(f"  {n} rules: {hit_counter[n]:>4} cards ({pct:>5.1f}%)")

# Per-rule coverage
print("\nPer-rule coverage (cards hit by this rule):")
rule_counts = Counter()
for cid, (hits, *_) in rule_hits.items():
    for r in hits: rule_counts[r] += 1
for r, n in sorted(rule_counts.items(), key=lambda x: -x[1]):
    print(f"  {r:<22} {n:>4} ({n/total*100:>5.1f}%)")

# Blind cards (0 explicit rule, only generic BuildSynergy pair or commitment)
print()
print("="*100)
print("BLIND CARDS — 0 explicit evaluation rule (no PowerCatalog/SequencingTier/EffectSynergy/AmplifierSynergy/HandSynergy/Override/SkillSetup/SkillCantrip/SkillDefensive)")
print("="*100)
EXPLICIT_RULES = {"PowerCatalog", "PowerSequencingTier", "SkillSetup", "SkillCantrip",
                   "SkillDefensive", "EffectSynergy", "AmplifierSynergy",
                   "HandSynergy", "Override",
                   "VariableDamage", "VariableBlock", "ExhaustBurstSpecial",
                   "OrbMechanic", "RawDamage", "RawBlock", "CostDiscountAuto"}
blind_cards = []
for cid, (hits, ctype, tier, char, is_cs, axes, var_powers) in rule_hits.items():
    if not (hits & EXPLICIT_RULES):
        blind_cards.append((cid, ctype, tier, char, is_cs, axes, hits))
print(f"\nTotal blind cards: {len(blind_cards)}")
# Group by type & tier
print("\nBy type:")
by_type = Counter(b[1] for b in blind_cards)
for t, n in by_type.most_common():
    print(f"  {t:<10} {n}")
print("\nBy tier (excluding Status/Curse):")
by_tier = Counter(b[2] for b in blind_cards if not b[4])
for t in "SABCD?":
    if by_tier.get(t,0): print(f"  {t}: {by_tier[t]}")

# Detail listing of high-impact blind cards (S/A tier, non-curse)
print("\n--- High-impact blind cards (S/A tier, non-curse/status) ---")
for cid, ctype, tier, char, is_cs, axes, hits in sorted(blind_cards, key=lambda b: ("SABCD?".index(b[2]) if b[2] in "SABCD" else 5, b[0])):
    if is_cs or tier not in ("S", "A"): continue
    cat = cat_by_id.get(cid, {})
    desc = cat.get("description", "").replace("\n", " / ")[:90]
    other_hits = hits - EXPLICIT_RULES
    print(f"  {cid:<35} [{char:<10}] {ctype:<7} {tier} axes={sorted(axes)}")
    print(f"      hits={sorted(hits)} desc={desc}")

# All non-status blind cards
print("\n--- All non-status/curse blind cards ---")
for cid, ctype, tier, char, is_cs, axes, hits in sorted(blind_cards, key=lambda b: ("SABCD?".index(b[2]) if b[2] in "SABCD" else 5, b[0])):
    if is_cs: continue
    cat = cat_by_id.get(cid, {})
    desc = cat.get("description", "").replace("\n", " / ")[:60]
    print(f"  {cid:<35} [{char:<10}] {ctype:<7} {tier} axes={sorted(axes)[:5]}")

# Validate new EffectSynergy handlers — does catalog actually have cards for each axis?
print()
print("="*100)
print("EffectSynergy handler axis coverage — do real cards trigger each new handler?")
print("="*100)
NEW_AXES = ["SOUL_CONSUMER", "SOUL_AMPLIFIER",
             "SHIV_CONSUMER", "SHIV_AMPLIFIER",
             "SKELETON_CONSUMER", "SKELETON_AMPLIFIER",
             "EXHAUST_CONSUMER",
             "FORGE_AMPLIFIER", "LORDS_BLADE_AMPLIFIER",
             "VOLATILE_CONSUMER",
             "CUNNING_CONSUMER",
             "STAR_CONSUMER", "DARK_ORB_AMPLIFIER",
             "POISON_CONSUMER", "POISON_AMPLIFIER",
             "DOOM_CONSUMER", "DOOM_AMPLIFIER",
             "BURN_CONSUMER", "BURN_AMPLIFIER",
             "CONSTRICT_CONSUMER", "CONSTRICT_AMPLIFIER"]
for ax in NEW_AXES:
    matching = [cid for cid, t in triggers.items() if ax in t.get("axes", [])]
    if not matching:
        print(f"  *** DEAD HANDLER ***  {ax} — 0 cards in catalog")
    else:
        sample = ", ".join(c.replace("CARD.","") for c in matching[:3])
        more = f", +{len(matching)-3}" if len(matching) > 3 else ""
        print(f"  {ax:<30} {len(matching):>3} cards: {sample}{more}")
