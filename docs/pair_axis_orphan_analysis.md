# Pair-axis stem orphan analysis — v0.103.2

Static analysis output for the parent repo (`sts2-card-advisor-dev`) axis-tagger.
Identifies which orphan stems in `BuildSynergy` pair-rule coverage are real
convention bugs (re-taggable in catalog) vs structural non-applicable cases.

This document lives in the mod repo because the analysis runs against this
repo's catalog copy + planner source. The fix work happens in the parent repo.

## Background

`BuildSynergy.cs:68-94` matches pairs by axis stem:

```csharp
if (ax.EndsWith("_PRODUCER")) {
    var stem = ax.Substring(0, ax.Length - "_PRODUCER".Length);
    // look for stem + "_AMPLIFIER" or stem + "_CONSUMER" in hand
}
```

The current audit reports 17/28 stems as orphan (missing one side). Of those,
most are structural (BuildSynergy pair rule does not apply); only a few are
actual convention bugs.

## Categorization of the 17 orphans

### A. Structural — pair rule not applicable, no fix needed (8 stems)

These stems' "producer" is a card *stat* (damage / block / draw), not an axis
tag. EffectSynergy / type-based PlanScorer handles the amplifier side via
other paths (`BLOCK_AMPLIFIER`, `DAMAGE_AMPLIFIER` are scored against
hand stats, not against an axis-tagged producer).

| Stem | A | C | Why structural |
|---|---:|---:|---|
| BLOCK | 7 | 0 | block is a Skill-card stat |
| DAMAGE | 8 | 0 | damage is an Attack-card stat |
| DRAW | 1 | 0 | draw is a card stat (`Cards` var) |
| DEFEND_TYPE | 1 | 0 | FASTEN amplifies Skill block — type-based |
| HP_LOSS | 0 | 3 | producer is self-HP-loss attacks (PainEater etc.) |
| STATUS | 0 | 3 | producer is enemy attacks generating statuses |
| STRIKE | 0 | 2 | producer is Strike-named cards (name-matched) |
| POWER | 3 | 0 | producer is every Power-type card |

**Action**: none. Either re-evaluate whether to drop these as stems (cosmetic
audit improvement) or accept as expected orphans.

### B. Convention — true producer cards exist but lack the suffix (2 stems, 40 cards)

These cards apply Vulnerable / Weak power but are tagged with the bare stem
(`VULN`, `WEAK`) rather than `VULN_PRODUCER` / `WEAK_PRODUCER`. As a result,
`BuildSynergy.Compute()` misses producer-amplifier pair bonuses despite the
synergy being semantically present.

These are also distinct from HandSynergy reach (which scores vars-based power
application against attack benefit). The pair-rule synergy adds the
*amplifier card commitment bonus* — "this turn we play CRUELTY because we
also have BASH; both score higher when paired".

#### `VULN_PRODUCER` candidates (21 cards)

Currently tagged `DEBUFF, VULN` (without the `_PRODUCER` suffix). Re-tag to
add `VULN_PRODUCER`.

| Card ID | Type | Tier | Current relevant axes |
|---|---|---|---|
| `CARD.ASSASSINATE` | Attack | B | DEBUFF, VULN, DAMAGE |
| `CARD.BASH` | Attack | C | DEBUFF, VULN, DAMAGE |
| `CARD.BEAM_CELL` | Attack | A | DEBUFF, VULN, DAMAGE |
| `CARD.BREAK` | Attack | B | DEBUFF, VULN, DAMAGE |
| `CARD.COMET` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.EXPOSE` | Skill | A | BLOCK, DEBUFF, VULN |
| `CARD.FALLING_STAR` | Attack | A | STAR_PRODUCER, DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.FEAR` | Attack | C | DEBUFF, VULN, VOLATILE_PRODUCER, DAMAGE |
| `CARD.GAMMA_BLAST` | Attack | B | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.HIGH_FIVE` | Attack | C | DEBUFF, VULN |
| `CARD.KNOW_THY_PLACE` | Skill | B | DEBUFF, VULN, WEAK |
| `CARD.MAD_SCIENCE` | None | A | BLOCK, DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.METEOR_SHOWER` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.PUTREFY` | Skill | S | DEBUFF, VULN, WEAK |
| `CARD.SHOCKWAVE` | Skill | S | DEBUFF, VULN, WEAK |
| `CARD.SQUASH` | Attack | B | DEBUFF, VULN, DAMAGE |
| `CARD.TAUNT` | Skill | B | BLOCK, DEBUFF, VULN |
| `CARD.THUNDERCLAP` | Attack | B | DEBUFF, VULN, DAMAGE |
| `CARD.TREMBLE` | Skill | A | DEBUFF, VULN |
| `CARD.UPPERCUT` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.VICIOUS` | Power | B | DEBUFF, VULN |

#### `WEAK_PRODUCER` candidates (19 cards)

Currently tagged `DEBUFF, WEAK`. Re-tag to add `WEAK_PRODUCER`.

| Card ID | Type | Tier | Current relevant axes |
|---|---|---|---|
| `CARD.COMET` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.DEATHBRINGER` | Skill | S | DOOM_PRODUCER, DEBUFF, WEAK |
| `CARD.DEFY` | Skill | S | BLOCK, DEBUFF, WEAK, VOLATILE_PRODUCER |
| `CARD.DOUBT` | Curse | ? | DEBUFF, WEAK |
| `CARD.FALLING_STAR` | Attack | A | STAR_PRODUCER, DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.GAMMA_BLAST` | Attack | B | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.GO_FOR_THE_EYES` | Attack | S | DEBUFF, WEAK, DAMAGE |
| `CARD.KNOW_THY_PLACE` | Skill | B | DEBUFF, VULN, WEAK |
| `CARD.LEG_SWEEP` | Skill | S | BLOCK, DEBUFF, WEAK |
| `CARD.MAD_SCIENCE` | None | A | BLOCK, DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.MALAISE` | Skill | S | DEBUFF, WEAK |
| `CARD.METEOR_SHOWER` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |
| `CARD.NEUTRALIZE` | Attack | C | DEBUFF, WEAK, DAMAGE |
| `CARD.NULL` | Attack | A | DEBUFF, WEAK, ORB_PRODUCER, DAMAGE |
| `CARD.PUTREFY` | Skill | S | DEBUFF, VULN, WEAK |
| `CARD.SHOCKWAVE` | Skill | S | DEBUFF, VULN, WEAK |
| `CARD.SUCKER_PUNCH` | Attack | B | DEBUFF, WEAK, DAMAGE |
| `CARD.SUPPRESS` | Attack | A | DEBUFF, WEAK, DAMAGE |
| `CARD.UPPERCUT` | Attack | S | DEBUFF, VULN, WEAK, DAMAGE |

**Action in parent repo**: add `VULN_PRODUCER` / `WEAK_PRODUCER` to the
listed cards' axes in axis-tagger output. The bare `VULN` / `WEAK` tags can
remain (do not break anything) or be dropped (cosmetic).

### C. Open questions — convention vs structural (3 stems, 11 cards)

These need a design decision in the parent repo before re-tagging.

#### `LORDS_BLADE` — 1 amplifier (PARRY)

PARRY rewards Lord's Blade play. Producer candidates are Regent cards that
generate Lord's Blade. None currently tagged `LORDS_BLADE_PRODUCER` —
catalog uses `LORDS_BLADE_PAYOFF` for amplifiers but no producer convention.

Likely action: identify Lord's Blade generators and tag with
`LORDS_BLADE_PRODUCER`. Out of audit scope to enumerate without a clearer
catalog signal.

#### `DARK_ORB` — 1 amplifier (DARKNESS)

Producer candidates already exist:
- `CARD.CONSUMING_SHADOW` — `ORB_PRODUCER, DARK_ORB`
- `CARD.NULL` — `ORB_PRODUCER, DARK_ORB`
- `CARD.RAINBOW` — `ORB_PRODUCER, DARK_ORB`
- `CARD.SHADOW_SHIELD` — `ORB_PRODUCER, DARK_ORB`

Current convention treats the orb-color tag (`DARK_ORB`) as a *modifier* on
`ORB_PRODUCER`, not as its own stem. Two options:

1. **Re-tag** — add `DARK_ORB_PRODUCER` to these 4 cards (creates the pair).
2. **Adjust BuildSynergy** — let DARKNESS amplifier match `ORB_PRODUCER + DARK_ORB`
   composite. Mod-repo change, not parent-repo change.

Recommend option 1 (catalog-side) since it's consistent with how other orb
amplifiers (`LIGHTNING_ORB_PRODUCER` for Storm cards, etc.) would work.

#### `SKELETON_ATTACK` — 1 amplifier (SQUEEZE)

Producer candidates are Skeleton-summoning cards (already tagged
`SKELETON_PRODUCER`). SQUEEZE specifically amplifies *Skeleton attacks*,
not Skeleton summoning. This is a sub-axis distinction.

Likely action: either rename `SKELETON_ATTACK_AMPLIFIER` → `SKELETON_AMPLIFIER`
(unifying the stem with `SKELETON_PRODUCER`), or add `SKELETON_ATTACK_PRODUCER`
to attacker-skeleton summoners. The first is simpler.

## Impact estimate

Re-tagging VULN + WEAK (40 cards across 21+19 with overlaps) would:

- Activate `BuildSynergy` pair bonuses for **7 VULN amplifier × 21 VULN producer**
  potential pairings (e.g., CRUELTY + BASH in same hand)
- Activate **2 WEAK amplifier × 19 WEAK producer** potential pairings
- Move `Pair-axis stem completeness` audit metric from 39.3% → ~46% (11 → 13
  complete stems out of 28)
- Likely small but non-zero impact on actual AI decisions in Vuln-focused or
  Weak-focused builds

Real activation depends on hand draw composition; pair-rule reach is an
upper bound (skill's existing caveat).

## Reproducing this analysis

```bash
# In this mod repo
python scripts/measure_ai_card_coverage.py
# Read the "Pair-axis stem completeness" section. Cross-reference against
# scripts/cards_catalog.json by searching for vars / description Korean
# keywords (취약 for Vuln, 약화 for Weak).
```

The extraction script that generated the candidate tables above is a one-off
inline Python; if this analysis needs to run regularly, promote it to
`scripts/find_orphan_producers.py` (not done yet — pending whether parent
repo automates the re-tag step).
