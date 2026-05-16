# Sts2CombatAI Rule Mutation Impact Report

Per-entry leave-one-out impact on catalog classification.
`Cards lost` = number of catalog cards that fall out of the
rule's explicit-hit set when this entry is removed.

- Master catalog: `..\scripts\cards_catalog.json`
- PowerCatalog entries: 69
- PowerSequencingTier entries: 55
- CardOverrideCatalog entries: 13

## Baseline (no mutation)

- PowerCatalog explicit hits: 28 / 112
- PowerSequencingTier classified: 28 / 112
- Override applied: 13 / 577

## PowerCatalog mutation (69 entries)

| Power name | Cards lost | Example ids |
|---|---:|---|
| `StrengthPower` | 2 | `CARD.FRIENDSHIP`, `CARD.INFLAME` |
| `AccuracyPower` | 1 | `CARD.ACCURACY` |
| `AfterimagePower` | 1 | `CARD.AFTERIMAGE` |
| `BarricadePower` | 1 | `CARD.BARRICADE` |
| `BeaconOfHopePower` | 1 | `CARD.BEACON_OF_HOPE` |
| `BufferPower` | 1 | `CARD.BUFFER` |
| `CorruptionPower` | 1 | `CARD.CORRUPTION` |
| `DanseMacabrePower` | 1 | `CARD.DANSE_MACABRE` |
| `DexterityPower` | 1 | `CARD.FOOTWORK` |
| `EchoFormPower` | 1 | `CARD.ECHO_FORM` |
| `EntropyPower` | 1 | `CARD.ENTROPY` |
| `FeelNoPainPower` | 1 | `CARD.FEEL_NO_PAIN` |
| `FeralPower` | 1 | `CARD.FERAL` |
| `FocusPower` | 1 | `CARD.DEFRAGMENT` |
| `JuggernautPower` | 1 | `CARD.JUGGERNAUT` |
| `MachineLearningPower` | 1 | `CARD.MACHINE_LEARNING` |
| `MayhemPower` | 1 | `CARD.MAYHEM` |
| `NoxiousFumesPower` | 1 | `CARD.NOXIOUS_FUMES` |
| `ReaperFormPower` | 1 | `CARD.REAPER_FORM` |
| `ThornsPower` | 1 | `CARD.CALTROPS` |

**Zero-impact entries (49)** — registered but no catalog
card maps to them via vars / id-derived matching:

- `ArtifactPower`
- `BiasedCognitionPower`
- `BlurPower`
- `BurstPower`
- `ConfusedPower`
- `ConstrictPower`
- `DampenPower`
- `DarkShacklesPower`
- `DemonFormPower`
- `DrawCardsNextTurnPower`
- `EnergyNextTurnPower`
- `EnfeeblingTouchPower`
- `EnragePower`
- `FlameBarrierPower`
- `FormPower`
- `FrailPower`
- `FreeAttackPower`
- `FreePowerPower`
- `FreeSkillPower`
- `GalvanicPower`
- `HangPower`
- `HexPower`
- `HungerPower`
- `IntangiblePower`
- `MindRotPower`
- `NextTurnPower`
- `NoBlockPower`
- `NoDrawPower`
- `NoEnergyGainPower`
- `PiercingWailPower`
- `PlatedArmorPower`
- `PoisonPower`
- `RagePower`
- `RegenPower`
- `RitualPower`
- `RupturePower`
- `ShacklingPotionPower`
- `ShadowmeldPower`
- `ShrinkPower`
- `SkittishPower`
- `TemporaryDexterityPower`
- `TemporaryFocusPower`
- `TemporaryStrengthPower`
- `VigorPower`
- `VitalSparkPower`
- `VulnerablePower`
- `WasteAwayPower`
- `WeakPower`
- `WraithFormPower`

## PowerSequencingTier mutation (55 entries)

| Power name | Tier | Cards lost | Example ids |
|---|---|---:|---|
| `StrengthPower` | Setup | 3 | `CARD.FRIENDSHIP`, `CARD.INFLAME`, `CARD.RUPTURE` |
| `AccuracyPower` | Setup | 1 | `CARD.ACCURACY` |
| `AfterimagePower` | Scaling | 1 | `CARD.AFTERIMAGE` |
| `BarricadePower` | Defensive | 1 | `CARD.BARRICADE` |
| `BeaconOfHopePower` | Scaling | 1 | `CARD.BEACON_OF_HOPE` |
| `BufferPower` | Defensive | 1 | `CARD.BUFFER` |
| `CorruptionPower` | Scaling | 1 | `CARD.CORRUPTION` |
| `DanseMacabrePower` | Scaling | 1 | `CARD.DANSE_MACABRE` |
| `DexterityPower` | Setup | 1 | `CARD.FOOTWORK` |
| `EchoFormPower` | Scaling | 1 | `CARD.ECHO_FORM` |
| `EntropyPower` | SelfHarm | 1 | `CARD.ENTROPY` |
| `FeelNoPainPower` | Defensive | 1 | `CARD.FEEL_NO_PAIN` |
| `FeralPower` | Scaling | 1 | `CARD.FERAL` |
| `FocusPower` | Setup | 1 | `CARD.DEFRAGMENT` |
| `JuggernautPower` | Scaling | 1 | `CARD.JUGGERNAUT` |
| `MachineLearningPower` | Scaling | 1 | `CARD.MACHINE_LEARNING` |
| `MayhemPower` | Scaling | 1 | `CARD.MAYHEM` |
| `NoxiousFumesPower` | Scaling | 1 | `CARD.NOXIOUS_FUMES` |
| `ReaperFormPower` | Scaling | 1 | `CARD.REAPER_FORM` |
| `ThornsPower` | Defensive | 1 | `CARD.CALTROPS` |

**Zero-impact entries (35)** — registered but no catalog
card resolves to this tier statically:

- `ArtifactPower` (Defensive)
- `BiasedCognitionPower` (Scaling)
- `BlurPower` (Defensive)
- `BurstPower` (Scaling)
- `ConfusedPower` (SelfHarm)
- `DemonFormPower` (Scaling)
- `DrawCardsNextTurnPower` (Tempo)
- `EnergyNextTurnPower` (Tempo)
- `EnragePower` (Scaling)
- `FlameBarrierPower` (Defensive)
- `FreeAttackPower` (Tempo)
- `FreePowerPower` (Tempo)
- `FreeSkillPower` (Tempo)
- `GalvanicPower` (Scaling)
- `HangPower` (SelfHarm)
- `HungerPower` (Scaling)
- `IntangiblePower` (Defensive)
- `MindRotPower` (SelfHarm)
- `NoBlockPower` (SelfHarm)
- `NoDrawPower` (SelfHarm)
- `NoEnergyGainPower` (SelfHarm)
- `PlatedArmorPower` (Defensive)
- `RagePower` (Scaling)
- `RegenPower` (Defensive)
- `RitualPower` (Scaling)
- `ShadowmeldPower` (Defensive)
- `ShrinkPower` (SelfHarm)
- `SkittishPower` (SelfHarm)
- `TemporaryDexterityPower` (Setup)
- `TemporaryFocusPower` (Setup)
- `TemporaryStrengthPower` (Setup)
- `VigorPower` (Setup)
- `VitalSparkPower` (Scaling)
- `WasteAwayPower` (SelfHarm)
- `WraithFormPower` (Defensive)

## CardOverrideCatalog mutation (13 entries)

Each override entry is 1:1 with a card; impact is trivially 1 unless
the card id is not present in the catalog (orphan override).

_All 13 override entries match an active catalog card._

## Reading

- **High-impact entries** (≥3 cards lost) are critical hand-tunes; their
  values should be reviewed carefully — wrong magnitude propagates.
- **Zero-impact entries** suggest either dead code (target power never
  appears in catalog) or a naming mismatch between the rule's power name
  and what the catalog's `vars` / id actually exposes.
- Static mutation — same lower-bound caveat as the parent script. A card
  whose true game power class differs from the id-derived name may not
  be captured in `Cards lost`.