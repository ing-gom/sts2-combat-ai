# Card Play Order & Relationship Framework — v0.103.2

Detailed analysis of within-turn play sequencing for Power / Skill / Attack
cards in this mod's planner, and the inter-card relationships that drive
ordering decisions. Grounded in the actual `Sts2CombatAICode/Core/Planner/`
implementation as of v0.103.2.

## 0. Scope

This document captures:

- **Within-type ordering** — when a hand has multiple cards of one type,
  which plays first
- **Cross-type ordering** — when Power + Skill + Attack compete for the
  same energy budget
- **Hand-context modifiers** — Innate / Ethereal / Retain / Exhaust effects
  on sequencing
- **Threat-context modifiers** — survival urgency, lethal-opportunity,
  no-threat coast
- **Card relationship taxonomy** — Producer / Amplifier / Consumer / Setup /
  Payoff / Resource and how they map to scoring code

It does NOT replace the executable rules in `PlanScorer.cs` /
`PowerSequencingTier.cs` / the 4 Synergy modules — it explains what those
rules collectively express.

## 1. Where ordering actually happens

The planner does NOT have a hard-coded "Power then Skill then Attack" rule.
Instead, **every legal candidate card is scored independently, and the
highest-scoring card plays**. Ordering emerges from each card's score
including all of:

| Layer | Source | What it adjusts |
|---|---|---|
| Base value | `PlanScorer.BreakdownInternal` per-type branch | Attack: dmg × DamagePerPointBonus; Skill: block × BlockPerPointBonus; Power: PowerCatalog.Value |
| Power tier ordering | `PowerSequencingTier.OrderingBonus` (when ≥2 Power in hand) | +200/+150/+100/+50/−300 by Setup/Scaling/Defensive/Tempo/SelfHarm |
| Power tier conditional | `PowerSequencingTier.ConditionalBonus` | Threat-aware Defensive boost; survival urgency penalty for Setup/Scaling/Tempo; Setup beneficiary-absence penalty; Scaling dying-fight discount |
| Build pair synergy | `BuildSynergy.Compute` | `X_PRODUCER` + `X_AMPLIFIER`/`X_CONSUMER` in hand |
| Build commitment | `BuildSynergy.CommitmentBonus` | Primary build tag matches deck dominant build |
| Amplifier reach | `AmplifierSynergy.Compute` (non-Power) | POWER_AMPLIFIER / REPLAY / etc. — scored against best target's PlanScorer.Score × ratio |
| Effect amplifier | `EffectSynergy.Compute` (non-Power) | DAMAGE/BLOCK/VULN/WEAK_AMPLIFIER + BLOCK_PAYOFF + HP_LOSS_CONSUMER |
| Hand synergy | `HandSynergy.Compute` (Power apply) | Strength/Dex × remaining attack/skill beneficiaries; Vuln × remaining hits; Weak × predicted enemy damage saved |
| Play-order bias | `PlanScorer.PlayOrderBias` | Retain: defer penalty per alternative; Ethereal: play-now bonus |
| Override | `CardOverrideCatalog.Lookup` | Hand-tune for ~13 cards algorithm under/over-values |

**Key insight**: type-based ordering ("Power before Attack") is not enforced
— it is **emergent** from the Setup-tier ordering bonus (+200 for
Strength/Dex/Vuln-applying Powers) plus HandSynergy/EffectSynergy nudges
(+70 to amplifiers per remaining beneficiary). If a turn has no Setup
Powers and no amplifiers, attacks and skills compete purely on raw value
adjusted for threat / lethal context.

## 2. Within-type ordering

### 2.1 Powers — fully tier-classified (post-coverage pass)

After the v0.103.2 coverage pass (commits ed07f10 / 42c3788 / 2b28449),
**100% of base Power cards** (112) have an explicit
`PowerSequencingTier` classification. Order priority (when ≥2 Powers in
hand):

```
Setup (+200)  >  Scaling (+150)  >  Defensive (+100)  >  Tempo (+50)
                                                         >  Unknown (0)
                                                              >  SelfHarm (-300)
```

**Setup** (e.g., Strength / Dex / Focus / Vigor / Accuracy / Lethality /
Cruelty / Tracking / Fasten):
- Multiplies same-turn plays. Score collapses if no beneficiary in hand
  (`setupNoBenefit = -300`) — prevents wasted Setup as opener
- Focus + orb cards → `focusOrbSyn = +80 × remainingOrbCards`
- Accuracy + attacks → `accAtkSyn = +30 × remainingAttacks`
- Vigor with no attacks → `vigorDeadSetup = -250`

**Scaling** (long-fight permanents — DemonForm / EchoForm / Mayhem /
Corruption / Juggernaut / Unmovable / Sleight of Flesh / etc.):
- Value compounds across turns. `PLAY_TRIGGER` axis gets
  `+60 × remainingPlayable` (Afterimage-style)
- Dying-fight discount: 1 enemy + total HP ≤ 25 → `-300`

**Defensive** (Barricade / Intangible / Buffer / Plated Armor / Thorns /
Blur / FlameBarrier / FeelNoPain / Plating / etc.):
- Threat-aware: leak > 0 → `+min(800, leak × 40)`
- AllInert or no-threat (threat < `NoThreatRatio`) → `-200`

**Tempo** (EnergyNextTurn / DrawCardsNextTurn / FreeAttack/Skill/Power /
Pyre / Tools of the Trade / Void Form / Demesne / etc.):
- Defer to end of turn (no within-turn synergy)
- Ending fight (total enemy HP ≤ 15) → `-400`
- Survival urgency: Fatal/-2200, Heavy/-900, Moderate/-250

**Edge cases**:
- Same-turn Setup beneficiary check uses `self.PowerApps.ContainsKey` —
  only counts the power class names; HandSynergy double-credits already
  handled via −1 beneficiary correction
- Multi-power cards (rare): tier = max-priority of all applied powers

### 2.2 Skills — no explicit ordering, implicit from score

Skills have no `SkillSequencingTier`. Within-skill ordering emerges from:

| Skill subtype | Ordering driver | Notes |
|---|---|---|
| Block skills (`Block > 0`, target Self/AnyPlayer) | Threat-aware score (block bonus weight × threat ratio) | Block "leaks past" gets full credit; over-block clamped |
| Vuln/Weak applicators (e.g., Tremble, Defy) | EffectSynergy + HandSynergy + BuildSynergy | Same scoring path as Vuln-applying Attacks |
| Card-draw / Energy-gain (e.g., Acrobatics, Adrenaline) | `EvaluateDrawCard` / `EnergyContext` | Recursion-guarded; energy gain scores remaining playable cost |
| Utility (Discover, fetch, transform — Anointed, Apotheosis) | SelectorMode + `fetch_trigger` axis | Burn-vs-Boost mode picked by trigger axis |
| Exhaust-self skills (Shrug It Off, Power Through) | `EXHAUST_SELF` axis → `PlayOrderBias` | Played first to clear hand for Hellraiser-style etc. |

**Gap — no explicit skill ordering tier**: Skills with the same total
score compete arbitrarily. Edge cases not currently distinguished:

- **Block-now vs block-defer**: a Skill that grants block + cantrip should
  ideally play before pure-block skills if cantrip extends draw
- **Self-target block vs all-ally block** (Regent, multiplayer): no
  preference signal
- **Vuln-Skill vs Vuln-Attack**: both score VULN_PRODUCER but Skill
  (which also blocks) probably should play before the Vuln-Attack to
  maximize the "block-then-amplified-attack" arc. Currently both compete
  on raw score, not order.

**Implementation note**: extending `SkillSequencingTier` mirroring
`PowerSequencingTier` would add structure here, but the impact is likely
smaller than the Power case because Skill scores are already
state-dependent (block leak vs threat, draw vs hand size).

### 2.3 Attacks — implicit, driven by HandSynergy / EffectSynergy

Attack ordering also emerges from score. Effective ordering signals:

| Trigger | Effect |
|---|---|
| `DAMAGE_AMPLIFIER` axis (Aggression, Conflagration, Knockdown, Lethality, Shadow Step, Sword Sage) | `+70 × remainingAttacks` — wants to play BEFORE remaining attacks |
| Attached debuff (VulnerablePower in PowerApps) | EnemyDebuff score + HandSynergy boost (per remaining hit × 40) |
| `EXHAUST_SELF` axis | `PlayOrderBias` — played first to enable Exhaust payoffs |
| `BLOCK_PAYOFF` (Body Slam = damage equals current block) | `+curBlock × 30` if block already on board; `-600` to `-1500` otherwise |
| `INNATE` (Innate Strike, etc.) | First-turn opener — implicit via energy availability |
| Multi-hit (`Hits ≥ 2`) | Vuln/Strength scale per hit, not per attack — higher value when stacked |
| Free attack (Cost 0, e.g., `FREE_ATTACK` axis) | No energy cost penalty — slots in alongside expensive cards |
| Target damage cap (Intangible / HardenedShell) | Multi-hit beats single-big — implicit via per-hit cap in `EffectiveAttackDmg` |
| `ATTACK_REPLAY` axis (Beat Down, One-Two Punch) | AmplifierSynergy boost — replay best attack in hand |

**Edge cases handled**:
- Vulnerable target & Weak self & Strength self all flow through
  `StatusMath.EffectiveAttackDmg` per hit, so multi-hit attacks benefit
  proportionally more from setup
- AoE attacks score against `Σ per-enemy effective damage` with each
  enemy's own Vulnerable / DamageCap / HardenedShellRemaining applied
- Artifact charge on enemy blocks the attached debuff once per
  application — AOE reach excludes Artifact-shielded enemies

**Gap — explicit attack-ordering nuances**:

- **Big-hit-first vs small-hit-first** when Strength is in hand: currently
  not modelled. Generally play the multi-hit attack last (more total hits
  → more Vuln/Strength leverage), but if enemy has HardenedShellRemaining
  small, big-single-hit first wastes less.
- **AOE-first vs single-target-first** in multi-enemy: depends on cleaving
  near-dead enemies first vs softening the priority threat. Not modelled.
- **Vuln-Attack vs Vuln-Power-Apply same turn**: Attack inflicts Vuln on
  hit (after damage), Power Apply inflicts at play time. If we have a
  Bash + Inflict Weakness + 3 Strikes, Bash should ideally play first to
  Vuln before the strikes. Score-only competition doesn't always elect
  Bash first (Bash's own damage is small).

## 3. Cross-type ordering

### 3.1 The de-facto play sequence

Putting it all together, the typical "ideal" within-turn sequence with all
mechanisms firing is:

```
1. INNATE openers (forced first via initial-draw)
2. Setup Power (Strength / Vigor / Focus / Cruelty / Lethality / Fasten)
       — tier +200, applies to everything that follows
3. Defensive Power (only if leak > 0; otherwise defer)
       — tier +100 base, +800 from leak
4. Scaling Power (DemonForm / Mayhem / Corruption)
       — tier +150, compounds across turns
5. Vuln/Weak applicator (Skill or Attack-with-debuff)
       — EffectSynergy.VULN_AMPLIFIER chain starts here
6. EXHAUST_SELF utility (clear hand for Hellraiser / Feel No Pain etc.)
7. Block skills (Defend / Iron Wave / etc.)
       — score scales with current threat
8. AmplifierSynergy carriers (POWER_AMPLIFIER, REPLAY, ATTACK_REPLAY)
       — wait for the best target to be present, fire once setup is done
9. Main damage attacks (multi-hit first if Vuln target; AoE first if
   priority threat is near-dead)
10. Tempo Power (EnergyNextTurn / DrawCardsNextTurn / FreeAttack —
    play last so they don't interfere with current-turn budget)
11. ETHEREAL cards if not played get exhausted — emergency squeeze
    (PlayOrderBias +EtherealPlayNowBonus puts them above 0-score baseline)
12. RETAIN cards if no better alternative (PlayOrderBias penalty
    decreases as alternatives are exhausted)
```

**This sequence is emergent, not enforced.** Each step is reproducible
from the scoring formulas in section 1, given typical hand compositions.

### 3.2 Energy budget interactions

Energy is the hard cap that ordering must respect:

- **Setup Power cost matters**: Inflame (1) before 3 Strikes (1 each) needs
  4 energy total — fits the 3-energy default if Inflame is the only Power.
  DemonForm (3) leaves only 0 energy this turn — must value DemonForm's
  scaling above the 3 Strikes' current-turn damage.
- **`EvaluateEnergyGain` (PlanScorer)**: low energy + expensive cards
  waiting → urgent bonus on energy-gaining Powers (Pyre, Subroutine,
  EnergyNextTurnPower)
- **`EvaluatePowerFightContext` (PlanScorer)**: powers shine in long
  fights, waste in short. Bonus scales with predicted fight length.

### 3.3 Threat-context overrides

`EnemyTurnSimulator` computes:
- `AllInert` (no attacking enemy this turn) — Powers get `PowerCardBonusWhenAllInert` instead of normal base
- `ThreatRatio` (incoming damage / current effective HP)
- `NextTurnThreatAmplified` (enemy's next-turn intent is buff-amplified)
- `PredictPlayerDmg` (after-block leak)
- `GetSurvivalUrgency` (Fatal / Heavy / Moderate / Safe)

How these reshape ordering:
- **Fatal**: Setup/Scaling/Tempo Powers get `-2200` — block / kill is the
  only priority. Defensive Powers stay normal (they ARE the survival play).
- **Heavy** (-900) / **Moderate** (-250): same direction, smaller magnitude
- **AllInert**: skip Defensive entirely; Setup/Scaling get bonus base
  (assumes safe to set up)
- **Lethal opportunity**: if total enemy HP ≤ damage we can deal, the
  scoring should skip non-damage cards. Implicit via attack scores
  outweighing alternatives; no explicit "lethal-mode" flag — could be
  an enhancement.

## 4. Hand-context modifiers

Card-state flags that override the generic ordering:

| Flag | Effect | Source |
|---|---|---|
| `INNATE` | First-turn opener (forced into initial draw) | Catalog axis; no planner adjustment beyond standard tier |
| `IsEthereal` | Must play this turn (else exhausted) — `+EtherealPlayNowBonus` | `PlanScorer.PlayOrderBias` |
| `IsRetain` | Defer if other playable cards remain — `-RetainDeferPenaltyPerAlternative × N` | `PlanScorer.PlayOrderBias` |
| `EXHAUST_SELF` axis | Played to enable Exhaust-payoff cards (Feel No Pain / Dark Embrace / etc.) | Direct score / EffectSynergy.HP_LOSS_CONSUMER indirect |
| `UNPLAYABLE` axis | Not played — only handled in evaluation (e.g., Burn / Wound) | `PlanScorer` curse/status branch; auto-rejected |
| `ETHEREAL_SELF` / `RETAIN_SELF` | Same as `IsEthereal` / `IsRetain` (axis tags) | Same |
| `Volatile` (휘발성) | Card removed end of combat | Affects Power valuation (no double-counting in next-fight value) |

**Gap — Volatile/Ethereal Power ordering**: Volatile Powers (VoidForm,
Demesne, Lethality, CallOfTheVoid) should ideally play *late* in early
turns (let Setup/Scaling resolve first) BUT *early* in later turns (the
Volatile fights more efficiently if dropped sooner). Currently no
turn-aware modifier — they're tiered statically (Tempo/Scaling).

## 5. Card relationship taxonomy

Beyond the 1-card-at-a-time scoring, the planner expresses ~9 distinct
inter-card relationship types:

| Relationship | Direction | Implementation | Example |
|---|---|---|---|
| **Producer ↔ Amplifier** | bidirectional | `BuildSynergy.Compute` — same hand, both bonus | POISON_PRODUCER (Bouquet) + POISON_AMPLIFIER (Accelerant) |
| **Producer ↔ Consumer** | bidirectional | `BuildSynergy.Compute` | EXHAUST (any Exhausting card) + EXHAUST_CONSUMER (Feel No Pain) |
| **Setup → Beneficiary** | one-way, time-ordered | `HandSynergy.Compute` (Strength → Attack), `PowerSequencingTier.Setup` ordering | Inflame → Strike × N |
| **Amplifier card → Target card** | one-way, target picked | `AmplifierSynergy.Compute` | Dual Wield → best Power; Beat Down → best Attack |
| **State amplifier ↔ State** | depends on hand+enemy state | `EffectSynergy.Compute` (VULN_AMPLIFIER reads enemy Vuln + hand Vuln-appliers) | Cruelty + (Bash already played OR Bash in hand) |
| **Hand-wide buff → All beneficiaries** | one-to-many | `HandSynergy.Compute` (per-hit Vuln, per-attack Strength) | Inflame benefits 3 Strikes + Bash + Pommel Strike |
| **Resource generator → Consumer** | one-way | `PowerCatalog.value` baseline + `EvaluateEnergyGain` | Pyre (energy/turn) → expensive cards next turns |
| **Card-generator → Generated card** | indirect, probabilistic | Not modelled (catalog `fetch_trigger` only enables SelectorMode) | Anointed → fetched best card |
| **Anti-synergy** | negative | `setupNoBenefit=-300`, `blkPayoffEarly=-600`, `vulnAmpNoSource=-300`, `defNoThreat=-200` | Setup with no beneficiary; Body Slam pre-block |

### 5.1 Multi-hop relationships (2-step amplification)

These exist in the game but aren't directly scored as chains — they emerge
from each link being independently scored:

```
Inflame (Strength) ─┬─► HandSynergy + 100/atk to Bash
                    ├─► HandSynergy + 100/atk to 3× Strike
Bash (Vuln on enemy) ─┬─► EffectSynergy.VULN_AMPLIFIER + 450 to Cruelty
                      ├─► HandSynergy + 40/hit to Cruelty when it plays
Cruelty (per-hit +25% on Vuln targets) ──► applies to ALL future attacks
```

The chain's *total* value is the sum of each link's score, NOT a multi-hop
bonus. This is sufficient because each link's individual contribution is
already proportional to the chain's combined benefit (Inflame's HandSynergy
counts beneficiaries, Bash's EffectSynergy reads hand Vuln-amplifiers,
Cruelty's tier-ordering pushes it before attacks).

**Gap**: the planner does NOT explicitly identify or reward "3-link
synergy chains". A hand with Inflame + Bash + Cruelty + 3 Strikes has
≥3-link value but the planner sees ≥3 separate +N synergies. This is
mostly a non-issue because each link is independent in scoring terms — but
a "combo recognition" tier could surface explicit "this turn has a
top-tier chain" bonus.

### 5.2 Anti-synergy (penalty-only relationships)

Captured penalties:
- Setup with no beneficiary in hand: `−300`
- Vigor with no attacks: `−250`
- Defensive with no threat: `−200`
- VulnAmp with no Vuln source: `−300`
- WeakAmp with no Weak source: `−150`
- BlockPayoff (Body Slam) pre-block: `−600` (block in hand) / `−1500` (none)
- BlockAmp with no block + no remaining block skills: `−250`
- Tempo Powers in dying fight: `−400`
- Scaling in dying fight: `−300`
- Amplifier with no valid target: `−500` to `−600`
- SelfHarm tier ordering: `−300`

**Not captured**:
- Exhaust-self card when Exhaust deck has 0 payoffs (no Feel No Pain /
  Dark Embrace etc.) — just loss
- Energy gain Power when expensive cards in hand will exhaust anyway
- Multi-hit attack when target Intangible+1HP — first hit kills, rest waste
  (partially handled by HardenedShellRemaining cap, not by Intangible)
- Status / Curse generation in hand (NoiseLeft cards) — no penalty currently

## 6. Implementation gaps — punch list

Items where current code under-models actual STS2 mechanics, ordered by
estimated impact:

### High impact

1. **Lethal-mode detection** — when `Σ best-attacks-this-turn ≥ Σ enemy
   HP`, all non-damage cards should be deprioritized below attacks. Would
   prevent "play Setup Power last turn before lethal" misorders.
2. **Multi-hit-attack-last when Vuln target** — currently attacks compete
   on score; explicit "smaller hit first to inflict Vuln, then big multi-hit"
   ordering missing.
3. **Volatile/Ethereal Power turn-awareness** — should play late on
   turn 1 (after Setup/Scaling), early on turn N (where N >> 1 to maximize
   uptime).

### Medium impact

4. **Skill sequencing tier** — analogous to PowerSequencingTier. Buckets:
   `Block` / `Debuff` / `Draw` / `Utility` / `Cantrip`. Same ordering-bonus
   pattern, smaller magnitudes.
5. **Combo recognition** — explicit detection of multi-link chains in
   hand. Probably a small explicit bonus (+50–100) per chain to break ties
   in favor of "this turn is a setup turn".
6. **Status/Curse hand pollution penalty** — generation-style cards
   (Anointed-fetch could pull Wound) should pay a small expected-cost
   penalty if their probability includes pollution.

### Low impact / experimental

7. **Energy curve fit** — when hand contains 4 + 1 + 1 = 6 energy worth
   of cards with 3 budget, prefer 3-cost (full slot) + 0-cost (Free
   attacks fill remainder).
8. **AOE-vs-single intent** — explicit "kill near-dead first" priority
   in multi-enemy turns (currently emerges from damage cap clamping but
   not from explicit ordering).
9. **Orb engine timing** (Defect-specific) — Channel-then-Evoke ordering
   when a Frost orb adds block this turn vs storing for later.
10. **Retain payoff signaling** — when this turn has only Retain cards
    and 1 zero-value card, the zero-value card plays just to defer Retain.
    Currently handled correctly via PlayOrderBias but transparent log
    detail would help debugging.

## 7. How this maps to existing rule modules

Cross-reference: which file owns which relationship type.

| Module | Owns | Notable bounds |
|---|---|---|
| `PowerCatalog.cs` | Per-power per-stack value (now 100% explicit for v0.103.2) | Lower-bound on real game class names (id-derived guesses for 9 cards) |
| `PowerSequencingTier.cs` | Within-Power ordering + threat-aware conditional | Lower-bound for cards with non-id-matching vars |
| `HandSynergy.cs` | Setup → Beneficiary scaling (Str/Dex/Vuln/Weak) | Vars-based; misses powers applied via game-class names not in vars |
| `AmplifierSynergy.cs` | Card-amplifier with chosen target (recursion-guarded) | Target picked by PlanScorer recursion; ratio caps at 0.50 |
| `EffectSynergy.cs` | State-amplifiers (DAMAGE/BLOCK/VULN/WEAK_AMPLIFIER + BLOCK_PAYOFF + HP_LOSS_CONSUMER) | Non-Power only; reads state + remaining hand |
| `BuildSynergy.cs` | Producer ↔ Amp/Cons pair + commitment | Suffix-based (`_PRODUCER` etc.); current orphan analysis in `docs/pair_axis_orphan_analysis.md` |
| `CardOverrideCatalog.cs` | Sparse hand-tune | Cap ~20; current 13 |
| `PlanScorer.PlayOrderBias` | Retain defer + Ethereal play-now | Card-flag based |
| `PlanScorer.EvaluateEnergyGain` | Resource-context bonus | Looks at expensive cards waiting |
| `PlanScorer.EvaluatePowerFightContext` | Fight-length bonus for Powers | Predicted fight length |
| `EnemyTurnSimulator` | Threat / leak / survival / inert detection | All-inert → power-cost skip |

## 8. Next-step recommendations

Ranked by expected impact on actual AI quality (not by audit metric
improvement):

1. **Lethal-mode flag** — `PlanScorer.LethalThisTurn(state)` returns true
   when total reachable damage ≥ remaining enemy HP. Add `-1500` to
   all non-damage card scores when true. Prevents "play a Power on the
   killing-blow turn".
2. **DecisionLog runtime analysis** — parse the `DecisionLog` ring buffer
   (32 entries, currently in-memory only) to measure *actual* synergy
   activation frequency, ordering correctness, and where score-based
   ordering disagrees with expected play sequence.
3. **Skill sequencing tier** — analogous to Power. Block-now vs
   block-defer is the biggest miss.
4. **Volatile-Power turn-1-vs-turn-N awareness** — small but consistent
   loss currently.
5. **Combo-recognition explicit bonus** — debug-friendliness more than
   absolute score change, but makes log inspection easier.

## 9. Limitations

- **Static analysis only** — every claim here is derived from reading the
  rule source + catalog. Runtime behavior (DecisionLog parsing) would
  validate / refine these claims. The mod has no automated regression
  test that compares planner output against expected sequences for
  curated hand compositions.
- **Each section's gap claims are unverified** — they describe what the
  current code does NOT do, but the actual impact (how often the gap
  bites a real decision) is unknown without telemetry.
- **Per-character archetype detail intentionally light** — this is a
  framework doc, not a balance / per-build guide. Character-specific
  ordering (Defect orb timing, Watcher stance flow, Necrobinder Soul/Doom
  payoff) deserves its own doc layered on top.
- **Multi-target / target-picking ordering** — when the same card could
  hit one of 3 enemies, target choice is in `PlanScorer.Score(card,
  targetIdx, state)` loop; per-target ordering is not deeply covered in
  this document.
