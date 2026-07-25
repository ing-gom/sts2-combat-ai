# IDamageModifier Listener Architecture — Design + Spike

Status: design doc + spike (2026-05-28). Goal: lift `AnalyticalSimulator`
vs `sts2.dll` parity from 42.8% → 65%+ so `MCTSPlanner` rollout leaf-value
bias becomes tolerable enough that depth produces a positive lift instead
of an overfitting penalty (current MCTS-200sim 66.1% < MCTS-50sim 67.2%
< planner-depth2 baseline 70.1%).

---

## 1. Motivation (evidence)

Pre-flight measurement on fresh `runs/sim_parity_fresh.jsonl` (50 ep
Ironclad PlannerDepthN, 694 dumped steps, 2026-05-28 post ANGER fix):

| Field | % records non-zero | Direction | What it represents |
|---|---|---|---|
| `enemy_hp_sum`        | **28.4%** | mixed (over+under) | **damage calc** — primary target of this architecture |
| `discard_pile_count`  | 12.4% | mod over (70 / 16) | fetch / Choose card routing (out-of-scope; card-specific cleanup) |
| `player_block`        | 8.8%  | mod under (40 / 21) | block math (Dex/Frail) |
| `player_hp`           | 8.5%  | mod over (57 / 2) | enemy reactive (Thorns, on-attack-damage) |
| `hand_count`          | 8.4%  | mod over (48 / 10) | draw mechanics |
| `player_strength`     | 7.3%  | mixed | Strength stacking timing |
| `draw_pile_count`     | 5.6%  | mod over (39 / 0) | reshuffle / card movement |
| `exhaust_pile_count`  | 4.9%  | mod under (32 / 2) | exhaust classification |

Combined coverage by this architecture (enemy_hp_sum + player_block +
player_hp + player_strength) ≈ **53% of all divergences**. The fetch /
discard / draw side is a separate card-specific-mechanic stack (see
section 8 for handoff list).

**Why a listener pattern, not more hard-coded math in StatusMath?**

`StatusMath.cs` currently hard-codes every power in 3 function chains:

```csharp
EffectiveAttackDmg(base, str, vigor, vulnerable, weak)
ApplyDamageMultipliers(damage, state, vuln, weak, lethalityActive)   // Tracking/Cruelty/Lethality
EffectivePerEnemyTotal(...)                                          // hits-loop + HardenedShell cap
```

Adding a new active power (e.g. RagePower per-attack block gain on
attacker side) requires patching multiple chains + every call site. The
16 cards stuck at 100% divergence (`SWORD_BOOMERANG`, `WHIRLWIND`,
`BREAKTHROUGH`, `ANGER`, …) almost all need per-hit modifier refresh —
exactly the case StatusMath's pre-baked formula can't express.

The listener pattern flips this: **each power owns its damage
contribution** and the pipeline iterates whoever is registered for the
current `DamageContext`.

---

## 2. Interface design

```csharp
namespace Sts2CombatAI.Planner;  // alongside PowerCatalog

public enum DamageStage
{
    /// Added to base damage before multiplicative stage.
    /// Examples: StrengthPower (+stack), VigorPower (+stack first attack),
    /// AccuracyPower (+stack for SHIV).
    Additive,
    /// Multiplied with running damage value. Order within stage is
    /// stable by registration order so deterministic.
    /// Examples: VulnerablePower (×1.5 on attacker's strikes vs vuln target),
    /// WeakPower (×0.75 from attacker's side), TrackingPower (×2 vs Weak target),
    /// CrueltyPower (×1.25 vs Vuln target), LethalityPower (×1.5 first attack).
    Multiplicative,
    /// Final clamp. Examples: IntangiblePower (cap to 1), HardToKill
    /// (cap to DamageCapPerHit), HardenedShellPower (cap to remaining shell).
    Cap,
}

public readonly struct DamageContext
{
    public readonly SimState State;
    public readonly SimEnemy Target;
    public readonly SimCard Card;
    public readonly int HitIndex;             // 0-based within multi-hit card
    public readonly int TotalHits;
    public readonly bool IsFirstAttackThisTurn;
    public readonly bool IsFirstHitThisCard;  // == HitIndex == 0
    // Side from which the modifier reads stacks. Distinguishes attacker-side
    // (StrengthPower on player, Tracking on player vs Weak target) from
    // defender-side (Vulnerable on enemy when player attacks).
    public DamageContext(SimState state, SimEnemy target, SimCard card,
        int hitIndex, int totalHits, bool firstAttack) { … }
}

public interface IDamageModifier
{
    /// Power identifier — matches stack-source dict key (StrengthPower,
    /// VulnerablePower, …). Registry uses this for ordering + diagnostics.
    string PowerName { get; }

    /// Where in the pipeline this modifier hooks. Determines which
    /// Apply* method is called — others return their input unchanged.
    DamageStage Stage { get; }

    /// Whose stack count does this modifier read from?
    /// AttackerSide=true reads PlayerPowers (or player explicit field);
    /// false reads Target.Powers / explicit enemy field.
    bool AttackerSide { get; }

    /// Returns damage after modifier. Stage mismatch → returns input.
    /// stack = current stack count (already resolved by registry — 0 means
    /// inactive, modifier should no-op).
    int ApplyAdditive(int damage, int stack, in DamageContext ctx);
    double ApplyMultiplicative(double damage, int stack, in DamageContext ctx);
    int ApplyCap(int damage, int stack, in DamageContext ctx);
}

public static class DamageModifierRegistry
{
    private static readonly List<IDamageModifier> _all = new();
    public static void Register(IDamageModifier m) { _all.Add(m); _byStage = null; }
    public static IReadOnlyList<IDamageModifier> All => _all;

    // Cached partition by stage; rebuilt on Register.
    private static List<IDamageModifier>? _additive, _mult, _cap;
    private static (List<IDamageModifier>, List<IDamageModifier>, List<IDamageModifier>) Partition() { … }

    /// Main pipeline entry. Walks Additive → Multiplicative → Cap.
    /// Per-stack count resolved by attackerSide flag against state/target.
    public static int Resolve(int baseDamage, in DamageContext ctx)
    {
        var (additive, mult, cap) = Partition();
        int dmg = baseDamage;
        foreach (var m in additive)
        {
            int stack = ResolveStack(m, ctx);
            if (stack != 0) dmg = m.ApplyAdditive(dmg, stack, ctx);
        }
        double dmgF = dmg;
        foreach (var m in mult)
        {
            int stack = ResolveStack(m, ctx);
            if (stack != 0) dmgF = m.ApplyMultiplicative(dmgF, stack, ctx);
        }
        int final = Math.Max(0, (int)Math.Floor(dmgF));
        foreach (var m in cap)
        {
            int stack = ResolveStack(m, ctx);
            if (stack != 0) final = m.ApplyCap(final, stack, ctx);
        }
        return final;
    }

    /// stack lookup honours AttackerSide. Reads from PlayerPowers /
    /// explicit player fields / Target.Powers. Caller can extend with
    /// custom resolvers if a modifier reads from non-standard source.
    private static int ResolveStack(IDamageModifier m, in DamageContext ctx) { … }
}
```

---

## 3. Pipeline placement

`StatusMath.EffectivePerHitCapped` currently:

```
v0  =  base + str + vigor
v1  =  v0 * VulnerableMult        if defender.VulnerableAmount > 0
v2  =  v1 * WeakMult              if attackerWeak
per =  floor(max(0, v2))
per =  min(per, target.DamageCapPerHit)   if cap > 0
```

V2 replaces this with `DamageModifierRegistry.Resolve(base, ctx)`. Modifier
implementations replicate the exact arithmetic so V1==V2 holds for all
existing 119 unit tests.

The outer `EffectivePerEnemyTotal` (multi-hit loop + HardenedShell post-cap)
also moves into V2 — `Resolve` is called PER HIT with `HitIndex` set, so
per-hit modifiers like Vigor (first-hit-only) or Lethality (first-attack)
can self-gate via `ctx.HitIndex == 0` / `ctx.IsFirstAttackThisTurn`.

```csharp
public static int EffectivePerEnemyTotalV2(int baseDamage, int hits,
    SimEnemy target, in SimState state, SimCard card, bool firstAttack)
{
    int total = 0;
    int hitsClamped = Math.Max(1, hits);
    for (int h = 0; h < hitsClamped; h++)
    {
        var ctx = new DamageContext(state, target, card,
            hitIndex: h, totalHits: hitsClamped, firstAttack: firstAttack && h == 0);
        total += DamageModifierRegistry.Resolve(baseDamage, ctx);
    }
    // HardenedShell — moved into a modifier (HardenedShellCapModifier reads
    // target.HardenedShellRemaining, clamps total — see modifier list).
    return total;
}
```

---

## 4. Modifier catalog (baseline + extensions)

### 4a. Baseline (V1 parity — 5 modifiers)

These replicate current StatusMath behavior exactly. Registered at startup.

| Modifier | Power | Stage | Side | Behavior |
|---|---|---|---|---|
| StrengthAdditive    | StrengthPower    | Additive | attacker | `dmg + stack` per hit |
| VigorAdditive       | VigorPower       | Additive | attacker | `dmg + stack` first hit only (`ctx.IsFirstHitThisCard`) |
| VulnerableMult      | VulnerablePower  | Mult     | defender | `dmg * 1.5` |
| WeakMult            | WeakPower        | Mult     | attacker | `dmg * 0.75` |
| IntangibleCap       | IntangiblePower  | Cap      | defender | `min(dmg, 1)` |

### 4b. Card-specific & relic modifiers (V1 parity continued)

| Modifier | Source | Stage | Side | Behavior |
|---|---|---|---|---|
| AccuracyShivBonus       | AccuracyPower  | Additive | attacker | `dmg + stack` IF `card.Id == "SHIV"` |
| TrackingVsWeak          | TrackingPower  | Mult     | attacker | `dmg * 2.0` IF `target.WeakAmount > 0` |
| CrueltyVsVulnerable     | CrueltyPower   | Mult     | attacker | `dmg * 1.25` IF `target.VulnerableAmount > 0` |
| LethalityFirstAttack    | LethalityPower | Mult     | attacker | `dmg * 1.5` IF `ctx.IsFirstAttackThisTurn` |
| HardenedShellCap        | HardenedShell  | Cap      | defender | `min(per-enemy total, remaining)` — special: post-loop |

### 4c. Extensions (NEW behaviors not in V1)

These close the 100%-diverging card cases:

| Modifier | Power | Stage | Side | Behavior | Closes |
|---|---|---|---|---|---|
| EnragePowerStackingStrength | EnragePower | per-hit Strength refresh | attacker | each skill play adds N Strength stacks BEFORE next attack hit | RAGE/BERSERK chain |
| RagePowerOnAttackBlock      | RagePower   | side-effect (per-hit block) | attacker | hooks into post-damage block gain — NOT a damage modifier per se; uses a sibling `IOnHitListener` interface | Rage block-gain not modeled |
| ThornsReflectPerHit         | ThornsPower (defender) | side-effect | attacker | each hit reflects N to attacker (mod sim partial — already in code, move to modifier for consistency) | partial; consolidate |
| HardToKillCap               | HardToKillPower (named) | Cap | defender | min(dmg, stack) — same shape as IntangibleCap but reads HardToKillPower | Exoskeleton-class cap |

### 4d. Out-of-scope (handled elsewhere)

- DamageMultiplier from EchoForm — applied per-card (×2 hit count), not per-modifier
- DemonForm/Ritual Strength gain — happens at turn start, modifies player state (not damage pipeline)
- AOE target enumeration (`isAoe = card.Target == TargetType.AllEnemies`) — happens BEFORE registry, in AnalyticalSimulator's enemy loop
- Curse/Status mechanics — pile-side, not damage-side

---

## 5. Migration path

**Phase 0 — Spike DONE (2026-05-28):**
1. ✅ Added `Sts2CombatAICode/Core/Sim/DamageModifiers.cs` — interface + DamageContext + Registry + 9 baseline modifiers (4a + 4b minus HardenedShell which stayed in V2 post-total cap).
2. ✅ `StatusMath.EffectivePerHitCappedV2` + `EffectivePerEnemyTotalV2` parallel methods.
3. ✅ 13 V1↔V2 parity unit tests covering base, Strength, Vigor, Vulnerable, Weak, compound, Intangible cap, multi-hit, multi-hit+cap, Lethality first-attack, Tracking vs Weak, Cruelty vs Vuln, HardenedShell total cap. All pass.
4. ✅ AnalyticalSimulator attack branch switched to V2 (the 2 V1 calls — `EffectivePerEnemyTotal` + `ApplyDamageMultipliers` — collapsed to one V2 call carrying a `state with { … }` snapshot of post-card-effect player buffs).
5. ✅ **132/132** unit tests green (119 baseline + 13 V1↔V2 parity).
6. ✅ Parity probe regenerated: **42.8% V2 (697/694 dumped) == 42.8% V1**. Zero regression.
7. ✅ MCTS-50sim 720-game sweep: **484/720 V2 (67.2%) bit-identical to V1 484/720**. Per-bucket (Monster/Elite/Boss) also identical. Zero behavioral drift.
8. Module initializer + defensive `BaselineDamageModifiers.RegisterAll()` in `EffectivePerHitCappedV2` so source-include configurations always register.
9. No feature flag in the end — V2 path replaces V1 calls directly. V1 methods (`EffectivePerEnemyTotal` / `EffectiveAttackDmg` / `ApplyDamageMultipliers`) **remain intact** as the unit-test parity reference; rollback = revert the AnalyticalSimulator call-site edit (~15 lines).

**Phase 1 — extension modifiers (next session, NOT done):**
Now that V2 is the production path, add modifiers for un-modeled active powers:
- Per-hit Strength refresh — investigate whether actual STS2 multi-hit damage uses the same Strength snapshot per hit (V1 behavior) or refreshes between hits.
- EnragePower (Strength on Skill play) — applies BEFORE the next attack, so it's a state mutation in AnalyticalSimulator, not a per-hit modifier. Re-classify when implementing.
- ThornsPower attacker-side reflect — currently inline in AnalyticalSimulator line ~344-355. Move to modifier-registry for consistency once the side-effect listener interface (section 9.3) is designed.
- HardToKillPower — folded into `DamageCapPerHit` at snapshot time, IntangibleCap already covers via the same field.

**Phase 2 — card-specific mechanics (separate from architecture):**
The 16 cards at 100% parity divergence (SWORD_BOOMERANG, BREAKTHROUGH, SECOND_WIND, …) need card-specific handlers, NOT modifier-pattern additions. See section 8 (out-of-scope handoff).

**Phase 3 — measure cumulative lift:**
After Phase 1 + Phase 2: re-run parity probe (target 65%+) + MCTS-50 (target ≥ planner-depth2 70.1%).

**Rollback:** revert the AnalyticalSimulator attack-branch edit (lines that build `dmgState` + call `EffectivePerEnemyTotalV2`) back to the prior `EffectivePerEnemyTotal` + `ApplyDamageMultipliers` chain. V1 code path intact.

---

## 6. Performance considerations

Damage path is hot — depth-2 lookahead simulates ~30-50 card plays per
PlanScorer.Score call, each with N hits × M enemies (AOE).

- Registry size bounded (<30 modifiers). Per-hit iteration O(modifier count).
- `Partition()` cached after `Register()` calls — no per-hit alloc.
- `DamageContext` is `readonly struct` — passed by `in`, no heap alloc.
- Stack lookup via `Dictionary.TryGetValue` — O(1) per modifier.
- **Expected overhead**: ~30 dict lookups + 30 virtual calls per damage instance. Negligible vs damage calc itself.
- If profiler shows hotspot: convert Registry to `IReadOnlyList<IDamageModifier>[3]` indexed by `(int)DamageStage`, hoist out of inner loop.

[[feedback_perf_guard]] applies — re-measure depth-2 latency at Phase 3
gate before flipping `UseV2Damage = true` default.

---

## 7. Testing strategy

```
Sts2CombatAI.Tests/Sim/
  AnalyticalSimulatorTests.cs   (existing 119, must stay green)
  StatusMathTests.cs            (existing, must stay green)
  DamageModifierRegistryTests.cs   (NEW)
    - Register order respected
    - Stage partition cached correctly
    - V1 vs V2 parity on canonical scenarios:
      • base 6 dmg + str 3 → 9
      • base 6 + vuln → 9 (×1.5)
      • base 6 + str 3 + vuln + weak → 10 (floor(9 * 1.5 * 0.75))
      • Intangible cap → 1
      • Vigor on first hit, not second
      • Lethality on first attack of turn only
  DamageModifierExtensionTests.cs  (NEW, Phase 2)
    - EnragePower stacking on multi-skill turn
    - HardToKill cap on multi-hit attack
```

Property-based test approach: generate random `(base, str, vigor, vuln,
weak, lethality, tracking, cruelty, intangible)` tuples, assert V1 ==
V2 over 1000 cases. This catches arithmetic drift.

---

## 8. Out-of-scope handoff (cleanup AFTER architecture)

These are card-specific bugs not addressed by the modifier listener
pattern. Track separately, address post-Phase-3.

| Card | Issue | Fix shape | Effort |
|---|---|---|---|
| HEADBUTT | discard→draw move not simulated | move chosen card from `newDiscardPile` to top of `newDrawPile` | ~30 min |
| ARMAMENTS | Choose timing — basic version upgrades 1 chosen, mod sim adds to discard before resolution | special-case Choose handler | ~1 hr |
| BRAND | suspected self-exhaust not in catalog | verify by decompile; if self-exhaust, set `IsExhaust=true` in catalog | ~30 min |
| SWORD_BOOMERANG | random AOE distribution (3 random hits, not all enemies) | enum sample; for parity, distribute hits proportionally | ~1 hr |
| FIEND_FIRE | hand-exhaust × N hit | special: pre-compute exhaust count from current hand | ~1 hr |
| RAMPAGE | growing damage per play this combat | combat-history counter on SimState | ~1 hr |
| POMMEL_STRIKE | draw 1 on play | catalog `card.DrawCount` already exists — verify mod sim reads | ~15 min |

Total estimated cleanup: ~1 day post-architecture. Improves parity an
additional 5-10pp on top of architecture lift.

---

## 9. Open design questions

1. **Order within a stage** — registration order (deterministic) vs explicit
   priority field on `IDamageModifier`? Current decision: registration
   order. Revisit if a modifier ever depends on another's output.

2. **Multi-source stacks** — what if both player and enemy have
   `VulnerablePower` for different reasons? Current: AttackerSide flag
   resolves source unambiguously per modifier. If a power applies to
   both sides (rare), register two modifier instances.

3. **Side-effect listeners** (Rage block-gain, Thorns reflect, Juggernaut
   on block-gain) — these aren't damage modifiers, they're per-event
   reactions. Proposed: sibling `IOnHitListener` / `IOnBlockListener`
   interfaces with their own registries. Out of scope for this doc;
   design when first needed.

4. **Serialization** — modifiers currently code-only. Could be JSON-loaded
   like `PowerCatalog`'s value table (see v0.10 mutable conversion).
   Defer until 10+ modifiers exist and patterns crystallize.

---

## 10. Related artifacts

- `Sts2CombatAICode/Core/Planner/PowerCatalog.cs` — current static value table; modifiers register alongside
- `Sts2CombatAICode/Core/Sim/StatusMath.cs` — current damage chain (V1, untouched)
- `Sts2CombatAICode/Core/Sim/AnalyticalSimulator.cs` — call site that switches V1/V2 (feature flag)
- `Sts2CombatAI.Tests/Program.cs` — 119 baseline tests
- `src/Sts2CombatCore/Harness/SimulatorParityCheck.cs` (in `ing-gom/sts2-combat-core`) — probe
- `python/sim_parity_analyzer.py` (in `ing-gom/sts2-combat-core`) — analysis
- `runs/sim_parity_fresh.jsonl` (Sts2CombatCore side) — current baseline 42.8%
- Memory: [[project_b_damage_modifier_architecture]] — running notes
- Memory: [[reference_sim_parity_probe]] — probe command reference
- Memory: [[feedback_stale_data_lesson]] — analysis hygiene
