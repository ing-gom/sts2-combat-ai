using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using Sts2CombatAI.Reflection;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Builds a SimState from the live combat state. Called at every planner step so the
/// planner always sees actual state (catching draw/energy-gain/exhaust side effects
/// triggered by previously played cards). v0.1.1 adds full intent classification per enemy.
/// </summary>
internal static class StateSnapshotter
{
    // v0.8.5 — One-shot reflection-failure logger. Prevents per-frame spam when
    // STS2 patches break a field name while still surfacing the problem once
    // per session so the user can see the issue in godot.log.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedFailures = new();

    // 2026-06-04 — A/B kill switch for the relic-modeling work (ThrowingAxe/Vambrace/
    // BrilliantScarf/UnsettlingLamp/RainbowRing/Pocketwatch/ArtOfWar). Set STS2_RELICS_OFF=1 to
    // disable all of it so a seed-paired run measures the modeling's net effect. Read once.
    internal static readonly bool RelicModelingOff =
        System.Environment.GetEnvironmentVariable("STS2_RELICS_OFF") == "1";
    // 2026-06-05 — model SlipperyPower as a consumable N-hit buffer instead of a permanent
    // per-hit cap. Default off = byte-identical legacy behavior. See SimEnemy.SlipperyStacks.
    internal static readonly bool _slipperyConsume =
        System.Environment.GetEnvironmentVariable("STS2_SLIPPERY_CONSUME") == "1";
    private static void LogReflectionFailureOnce(string site, System.Exception ex)
    {
        if (_loggedFailures.TryAdd(site, 0))
            MainFile.Logger.Warn($"[CombatAI] snapshot/{site} reflection failed: {ex.Message}");
    }

    // 2026-06-04 — true if the named once-per-combat relic is held AND its live RelicStatus is
    // Active (i.e. has not fired yet this combat). Reflection-read to avoid a hard dependency on
    // the RelicStatus enum type. Fail-open: if held but Status is unreadable, assume Active.
    private static bool RelicActive(Player player, System.Collections.Generic.IReadOnlyDictionary<string, int> relics, string relicName)
    {
        if (!relics.ContainsKey(relicName)) return false;
        try
        {
            foreach (var r in player.Relics)
            {
                if (r == null || r.GetType().Name != relicName) continue;
                var st = r.GetType().GetProperty("Status")?.GetValue(r)?.ToString();
                return st == null || st == "Active";
            }
        }
        catch { }
        return true;
    }

    public static SimState? Capture(Player player)
    {
        try
        {
            var creature = player.Creature;
            if (creature == null) return null;
            var cs = creature.CombatState;
            if (cs == null) return null;
            var pcs = player.PlayerCombatState;
            if (pcs == null) return null;

            int hp = (int)(CombatReflection.CreatureHpField?.GetValue(creature) ?? 0);
            int block = (int)(CombatReflection.CreatureBlockField?.GetValue(creature) ?? 0);
            int energy = (int)(CombatReflection.PcsEnergyField?.GetValue(pcs) ?? 0);
            // Encoder-parity additions (2026-05-27): SimStateAdapter needs
            // these so the obs vector matches what Sts2CombatEnv._encode
            // emits. Snapshot once here; SimState carries them through to
            // the Encode call. See docs/hybrid-boss-debug.md.
            int maxHp = (int)(CombatReflection.CreatureMaxHpField?.GetValue(creature) ?? hp);
            // MaxEnergy: PlayerCombatState exposes it via the publicizer
            // (mirror of pcs.MaxEnergy in BuildObservation). Try direct
            // property access; fall back to current energy when the field
            // isn't reachable (mod-side reflection variant).
            int maxEnergy = energy;
            try { maxEnergy = (int)pcs.GetType().GetProperty("MaxEnergy")?.GetValue(pcs)!; }
            catch { /* fall back to current energy */ }

            int playerStr = CombatReflection.GetPowerAmount(creature, "StrengthPower");
            int playerDex = CombatReflection.GetPowerAmount(creature, "DexterityPower");
            // 2026-06-08 — KaiserCrab BackAttack facing (0=none, 1=Left, 2=Right).
            int playerFacing = CombatReflection.GetSurroundedFacing(creature);
            // v0.7.82 — VigorPower: next attack +N damage, then consumed.
            int playerVigor = CombatReflection.GetPowerAmount(creature, "VigorPower");
            // v0.7.83 — BufferPower: negates next damage instance.
            int playerBuffer = CombatReflection.GetPowerAmount(creature, "BufferPower");
            // v0.7.84 — Damage multiplier powers (Silent).
            int playerLethality = CombatReflection.GetPowerAmount(creature, "LethalityPower");
            int playerTracking = CombatReflection.GetPowerAmount(creature, "TrackingPower");
            int playerCruelty = CombatReflection.GetPowerAmount(creature, "CrueltyPower");
            // v0.7.85 — Block-side reactive / multiplier powers.
            int playerRage = CombatReflection.GetPowerAmount(creature, "RagePower");
            int playerAfterimage = CombatReflection.GetPowerAmount(creature, "AfterimagePower");
            int playerUnmovable = CombatReflection.GetPowerAmount(creature, "UnmovablePower");
            // v0.7.86 — Shiv damage bonus (Silent).
            int playerAccuracy = CombatReflection.GetPowerAmount(creature, "AccuracyPower");
            // 2026-05-30 — PhantomBlades: +N to the first shiv each turn.
            int playerPhantomBlades = CombatReflection.GetPowerAmount(creature, "PhantomBladesPower");
            // 2026-05-30 — ChildOfTheStars: +Amount block per star spent.
            int playerChildOfTheStars = CombatReflection.GetPowerAmount(creature, "ChildOfTheStarsPower");
            // 2026-05-30 — Parry: +Amount block when SOVEREIGN_BLADE is played.
            int playerParry = CombatReflection.GetPowerAmount(creature, "ParryPower");
            // 2026-05-30 — Inferno: AfterDamageReceived (player takes unblocked
            // damage on its own turn) deals Amount to ALL hittable enemies. Fires
            // on self-HP-loss cards (HpLoss). Applied in the HP_LOSS path.
            int playerInferno = CombatReflection.GetPowerAmount(creature, "InfernoPower");
            // 2026-05-31 — HauntPower (Necrobinder): SOUL play deals Amount unblockable
            // to a random enemy (reactive). Applied in the SOUL path.
            int playerHaunt = CombatReflection.GetPowerAmount(creature, "HauntPower");
            // 2026-05-31 — SmokestackPower (Defect): deals Amount to ALL enemies each
            // time the player GENERATES a Status card (AfterCardGeneratedForCombat).
            // Status-producer cards (BOOST_AWAY→Dazed, GUNK_UP→Slimed, OVERCLOCK→Burn)
            // thus pulse Smokestack AOE. Applied in the StatusToDiscardCount block.
            int playerSmokestack = CombatReflection.GetPowerAmount(creature, "SmokestackPower");
            // 2026-05-31 — PillarOfCreationPower (Regent): gain Amount block (Unpowered)
            // each time the player GENERATES a card (AfterCardGeneratedForCombat, any
            // pile). Card-gen cards (COLLISION_COURSE→Debris, BUNDLE_OF_JOY, status gen)
            // thus add Pillar block. Applied per generated card in the gen blocks.
            int playerPillar = CombatReflection.GetPowerAmount(creature, "PillarOfCreationPower");
            // 2026-05-31 — ArsenalPower (Regent): gain Amount Strength per generated
            // card (AfterCardGeneratedForCombat). Strength sibling of PillarOfCreation.
            int playerArsenal = CombatReflection.GetPowerAmount(creature, "ArsenalPower");
            // 2026-05-31 — ShroudPower (Necro): gain Amount block when the player applies
            // DoomPower (AfterPowerAmountChanged on DoomPower). Doom-applying cards
            // (BLIGHT_STRIKE) with Shroud add Shroud block per Doom applied.
            int playerShroud = CombatReflection.GetPowerAmount(creature, "ShroudPower");
            // 2026-05-31 — PanachePower (Regent): deals Amount to ALL enemies every 5th
            // card played. DisplayAmount = CardsLeft (remaining toward the next pulse,
            // 5→0), so a play with CardsLeft==1 triggers the AOE. Exact via DisplayAmount.
            int playerPanache = CombatReflection.GetPowerAmount(creature, "PanachePower");
            int panacheCardsLeft = CombatReflection.GetPowerDisplayAmount(creature, "PanachePower");
            // 2026-05-30 — BlackHolePower (Regent): deals Amount to ALL enemies when
            // the player GAINS or SPENDS stars (AfterStarsGained / AfterCardPlayed
            // StarsSpent>0). Applied on star-gain/star-cost plays.
            int playerBlackHole = CombatReflection.GetPowerAmount(creature, "BlackHolePower");
            // 2026-05-30 — SleightOfFleshPower (Necrobinder): when the player applies a
            // (non-temporary) DEBUFF to an enemy, deal Amount to that enemy
            // (AfterPowerAmountChanged). Fires once per debuff power applied.
            int playerSleightOfFlesh = CombatReflection.GetPowerAmount(creature, "SleightOfFleshPower");
            // 2026-05-30 — OrbitPower (Regent): refunds Amount energy each time the
            // cumulative energy-spent crosses a multiple of 4. The /4 counter is
            // internal, but DisplayAmount = 4 − (energySpent % 4) exposes it, so we
            // can compute the exact spent-mod-4 and model the refund precisely.
            int playerOrbit = CombatReflection.GetPowerAmount(creature, "OrbitPower");
            int orbitDisplay = CombatReflection.GetPowerDisplayAmount(creature, "OrbitPower");
            int playerOrbitSpentMod = orbitDisplay >= 0 ? ((4 - orbitDisplay) % 4 + 4) % 4 : 0;
            // v0.7.94 — Reactive Strength + Skill-cost reduction.
            int playerEnrage = CombatReflection.GetPowerAmount(creature, "EnragePower");
            // 2026-05-29 — MonologuePower per-play Strength (Regent). _amount =
            // stack = Strength granted after each card play this turn.
            int playerMonologue = CombatReflection.GetPowerAmount(creature, "MonologuePower");
            int playerCorruption = CombatReflection.GetPowerAmount(creature, "CorruptionPower");
            // v0.7.95 — Next Skill ×2.
            int playerBurst = CombatReflection.GetPowerAmount(creature, "BurstPower");
            // v0.7.96 — Player Thorns (reflect damage on receiving a hit).
            int playerThorns = CombatReflection.GetPowerAmount(creature, "ThornsPower");
            // v0.7.97 — FeelNoPainPower (Ironclad reactive: exhaust → block).
            int playerFeelNoPain = CombatReflection.GetPowerAmount(creature, "FeelNoPainPower");
            // v0.7.98 — EchoFormPower raw stack (remaining echoes computed below
            // after turnAttacksPlayed/turnSkillsPlayed counters are populated).
            int echoStack = CombatReflection.GetPowerAmount(creature, "EchoFormPower");
            // v0.7.99 — JuggernautPower (block→damage) + HungerPower (draw→Strength).
            int playerJuggernaut = CombatReflection.GetPowerAmount(creature, "JuggernautPower");
            int playerHunger = CombatReflection.GetPowerAmount(creature, "HungerPower");
            // v0.8.0 — FlameBarrierPower (1-turn reflect; folds into Thorns).
            int playerFlameBarrier = CombatReflection.GetPowerAmount(creature, "FlameBarrierPower");
            // v0.8.1 — DanseMacabrePower (cost≥2 card → block).
            int playerDanseMacabre = CombatReflection.GetPowerAmount(creature, "DanseMacabrePower");
            int playerVuln = CombatReflection.GetPowerAmount(creature, "VulnerablePower");
            int playerWeak = CombatReflection.GetPowerAmount(creature, "WeakPower");
            int playerFrail = CombatReflection.GetPowerAmount(creature, "FrailPower");
            int playerFocus = CombatReflection.GetPowerAmount(creature, "FocusPower");
            // 2026-05-29 — ThunderPower: +Amount damage per Lightning evoke.
            int playerThunder = CombatReflection.GetPowerAmount(creature, "ThunderPower");
            int playerIntangible = CombatReflection.GetPowerAmount(creature, "IntangiblePower");
            int playerMetallicize = CombatReflection.GetPowerAmount(creature, "MetallicizePower");
            int playerPlatedArmor = CombatReflection.GetPowerAmount(creature, "PlatedArmorPower");
            // v0.9 — PlatingPower (Regent CHILD_OF_THE_STARS payoff, STS2-specific):
            // BeforeTurnEndEarly the player gains block = Amount, then decrements.
            // Behaves identically to Metallicize / PlatedArmor for our threat
            // estimation purposes (free block before enemy attacks). Previously
            // omitted → AI saw 0 EOT block on Plating-buffed turns → over-
            // defended into already-covered threat (decomp sts2.decompiled.cs
            // :317136 — PlatingPower.BeforeTurnEndEarly → GainBlock).
            int playerPlating = CombatReflection.GetPowerAmount(creature, "PlatingPower");
            int playerEotBlockBonus = playerMetallicize + playerPlatedArmor + playerPlating;
            // v0.9 — Enemy-applied shutdown debuffs (one-turn class-of-action
            // disables). Captured here so PlanScorer can zero out the
            // affected card category instead of scoring them at full value.
            int playerNoBlock      = CombatReflection.GetPowerAmount(creature, "NoBlockPower");
            int playerNoDraw       = CombatReflection.GetPowerAmount(creature, "NoDrawPower");
            int playerNoEnergyGain = CombatReflection.GetPowerAmount(creature, "NoEnergyGainPower");
            // v0.9 — Confused (random cost), Tender (per-card -Str/-Dex temp)
            int playerConfused     = CombatReflection.GetPowerAmount(creature, "ConfusedPower");
            int playerTender       = CombatReflection.GetPowerAmount(creature, "TenderPower");
            // v0.9 — ShrinkPower (Tier B): outgoing damage × (100-N)/100.
            // 2026-05-29 — ShrinkPower.Amount is -1 when INFINITE (IsInfinite =>
            // Amount < 0), so the old `> 0` check missed the common infinite
            // case → shrink never applied (SOVEREIGN_BLADE 15=>10.5 diverged).
            // The reduction is a fixed 30% (DamageDecrease=30) whenever the
            // power is present; use != 0 to catch both infinite(-1) and stacked.
            int playerShrinkPct = CombatReflection.GetPowerAmount(creature, "ShrinkPower") != 0 ? 30 : 0;
            int playerDebilitate = CombatReflection.GetPowerAmount(creature, "DebilitatePower");
            // SmoggyPower (LivingFog) — when active, first Skill played
            // each turn afflicts all OTHER skills with Smog, locking them
            // out until turn-end. Captured here so candidate enumeration
            // can prune subsequent-skill candidates.
            int playerSmoggy = CombatReflection.GetPowerAmount(creature, "SmoggyPower");
            // v0.9 — CalcifyPower (player Necrobinder buff): Osty (skeleton)
            // attacks gain +N damage. Read here so SimAlly intent damage
            // capture below can fold it in.
            int playerCalcify    = CombatReflection.GetPowerAmount(creature, "CalcifyPower");
            int playerFreeAttacks = CombatReflection.GetPowerAmount(creature, "FreeAttackPower");
            int playerFreeSkills = CombatReflection.GetPowerAmount(creature, "FreeSkillPower");
            int playerFreePowers = CombatReflection.GetPowerAmount(creature, "FreePowerPower");
            // v0.7.12 — capture all player powers as a single dict so
            // AdvanceTurn can apply persistent passives (DemonForm, Regen,
            // Barricade, EchoForm etc.) without one field per power.
            var playerPowerDict = CombatReflection.GetAllPowers(creature);
            // 2026-06-04 — ShadowmeldPower multiplies this-turn card block by 2^stacks (decompile
            // ModifyBlockMultiplicative). Capture as a flat multiplier (cap 2^4=16 to bound). 1 = none.
            int shadowmeldStacks = playerPowerDict != null
                && playerPowerDict.TryGetValue("ShadowmeldPower", out var smAmt) ? smAmt : 0;
            int playerBlockMult = shadowmeldStacks > 0 ? (1 << System.Math.Min(shadowmeldStacks, 4)) : 1;
            // 2026-06-04 — VoidForm (first Amount cards/turn free), Veilpiercer (next Ethereal
            // card free), Fasten (+N block on Defend cards). turnCardsPlayed is computed below;
            // VoidForm budget is finalized after it. Capture the raw power amounts here.
            int voidFormAmt = CombatReflection.GetPowerAmount(creature, "VoidFormPower");
            int playerVeilpiercer = CombatReflection.GetPowerAmount(creature, "VeilpiercerPower");
            int playerFasten = CombatReflection.GetPowerAmount(creature, "FastenPower");
            int playerStars = (int)(CombatReflection.PcsStarsField?.GetValue(pcs) ?? 0);
            int playerDoom = CombatReflection.GetPowerAmount(creature, "DoomPower");
            // v0.7.35 — Player-side DoT stacks. Tick at turn end / start;
            // factor into current-turn survival check.
            int playerPoison = CombatReflection.GetPowerAmount(creature, "PoisonPower");
            int playerBurn = CombatReflection.GetPowerAmount(creature, "BurnPower");
            int playerConstrict = CombatReflection.GetPowerAmount(creature, "ConstrictPower");

            int orbCount = 0, orbCapacity = 0;
            var orbQueue = new List<OrbKind>();
            var orbEvokeValues = new List<int>();
            try
            {
                var oq = pcs.OrbQueue;
                if (oq != null)
                {
                    orbCount = oq.Orbs.Count;
                    orbCapacity = oq.Capacity;
                    foreach (var orb in oq.Orbs)
                    {
                        var kind = OrbKindExtensions.FromClassName(orb?.GetType().Name);
                        orbQueue.Add(kind);
                        // DarkOrb's evoke value accumulates via its passive — reflect the
                        // live EvokeVal property so the planner values evoking it realistically.
                        int evokeVal = 0;
                        if (orb != null)
                        {
                            try
                            {
                                var evProp = orb.GetType().GetProperty("EvokeVal");
                                var raw = evProp?.GetValue(orb);
                                if (raw is decimal d) evokeVal = (int)d;
                                else if (raw is int i) evokeVal = i;
                                else if (raw != null) evokeVal = System.Convert.ToInt32(raw);
                            }
                            catch (System.Exception ex) { LogReflectionFailureOnce("orb-evoke", ex); }
                        }
                        orbEvokeValues.Add(evokeVal);
                    }
                }
            }
            catch (System.Exception ex) { LogReflectionFailureOnce("orb-snapshot", ex); }

            var enemies = new List<SimEnemy>();
            var rawEnemies = cs.HittableEnemies.ToList();
            int maxEnemyMaxHp = rawEnemies
                .Select(e => (int)(CombatReflection.CreatureMaxHpField?.GetValue(e) ?? 0))
                .DefaultIfEmpty(0)
                .Max();
            var roomType = cs.Encounter?.RoomType ?? RoomType.Unassigned;
            bool isBossRoom = roomType == RoomType.Boss;
            bool isEliteRoom = roomType == RoomType.Elite;

            foreach (var e in rawEnemies)
            {
                int eMaxHp = (int)(CombatReflection.CreatureMaxHpField?.GetValue(e) ?? 0);
                bool spawned = WasSpawnedThisTurn(e.Monster);
                // Minion heuristic: spawned this turn OR significantly weaker than the strongest enemy in this fight
                bool isMinion = spawned ||
                    (maxEnemyMaxHp > 0 && eMaxHp > 0 && eMaxHp < maxEnemyMaxHp * 0.5);
                // Boss = the dominant creature in a boss room (not a minion-class spawn)
                bool isBoss = isBossRoom && eMaxHp == maxEnemyMaxHp && !isMinion;
                enemies.Add(BuildSimEnemy(e, hp, isBoss, isEliteRoom, isMinion));
            }

            // v0.2.9 — pile counts (Draw card scoring uses these to gauge "is drawing fruitful?")
            // v0.5.1 — also capture pile contents so the simulator can model expected draws.
            var drawPileRaw = PileType.Draw.GetPile(player)?.Cards;
            var discardPileRaw = PileType.Discard.GetPile(player)?.Cards;
            var exhaustPileRaw = PileType.Exhaust.GetPile(player)?.Cards;
            // 2026-05-31 — PileType.Play contains cards stuck mid-resolution
            // (sts2 headless: HEADBUTT's CardSelectCmd.FromSimpleGrid completes
            // but OnPlayWrapper post-OnPlay doesn't transition the card to
            // its result pile). Treat them as "discard-equivalent" so mod
            // sim's post-play DiscardPile.Count matches real game's effective
            // state. Verified via HBDIAG2 probe: every HEADBUTT play leaves
            // play=1 across the step boundary.
            var playPileRaw = PileType.Play.GetPile(player)?.Cards;
            int drawPileSize = drawPileRaw?.Count ?? 0;
            int discardPileSize = (discardPileRaw?.Count ?? 0) + (playPileRaw?.Count ?? 0);
            int exhaustPileSize = exhaustPileRaw?.Count ?? 0;

            // 2026-05-31 — count MAKE_IT_SO copies in draw / discard for the
            // pile-reactive return (AfterCardPlayedLate moves it to hand on the
            // 3rd/6th/... skill of the turn). PileType.Play cards count as discard.
            int makeItSoInDraw = 0, makeItSoInDiscard = 0;
            if (drawPileRaw != null)
                foreach (var c in drawPileRaw)
                    if (CardReflection.GetIdEntry(c) == "MAKE_IT_SO") makeItSoInDraw++;
            if (discardPileRaw != null)
                foreach (var c in discardPileRaw)
                    if (CardReflection.GetIdEntry(c) == "MAKE_IT_SO") makeItSoInDiscard++;
            if (playPileRaw != null)
                foreach (var c in playPileRaw)
                    if (CardReflection.GetIdEntry(c) == "MAKE_IT_SO") makeItSoInDiscard++;

            var hand = new List<SimCard>();
            var handPile = PileType.Hand.GetPile(player);
            if (handPile != null)
            {
                foreach (var card in handPile.Cards)
                    hand.Add(BuildSimCard(card, requirePlayability: true));
            }

            // NOTE: MasterPlannerPower grants Sly to Skills ON PLAY (per its
            // description "When you play a Skill, it gains Sly"). That means
            // the buff lands AFTER the card leaves hand — discarding a Skill
            // before playing it doesn't trigger the Sly auto-play. We do NOT
            // blanket-set IsSly for Skills here. Static Sly (CUNNING axis)
            // and runtime HAND_TRICK temp-Sly (via CardReflection.HasSlyKeyword
            // inside BuildSimCard) cover the cases that actually fire on
            // discard.

            // v0.6.7 — Token / pile-based mechanic counters. Walk hand + draw +
            // discard + exhaust counting Soul, Shiv, SovereignBlade instances.
            // Type-name matching avoids hardcoding card ID format and survives
            // localization. Exhaust pile is included for SovereignBlade since
            // exhausted blades still count toward Lord's Blade scaling.
            int soulInPiles = 0;
            int shivInPiles = 0;
            int sovereignBladeCount = 0;
            CountTokenCards(handPile?.Cards, ref soulInPiles, ref shivInPiles, ref sovereignBladeCount);
            CountTokenCards(drawPileRaw,     ref soulInPiles, ref shivInPiles, ref sovereignBladeCount);
            CountTokenCards(discardPileRaw,  ref soulInPiles, ref shivInPiles, ref sovereignBladeCount);
            CountTokenCards(exhaustPileRaw,  ref soulInPiles, ref shivInPiles, ref sovereignBladeCount);

            // 2026-05-30 — deck-wide star-card count (CRESCENT_SPEAR scaling).
            int starCardsInDeck = CountStarCards(handPile?.Cards) + CountStarCards(drawPileRaw)
                + CountStarCards(discardPileRaw) + CountStarCards(exhaustPileRaw);

            // Skeleton (Osty) ally count — alive monsters of class Osty owned by player.
            int skeletonCount = 0;
            // 2026-05-30 — the Osty's current HP (UNLEASH's CalculatedDamage =
            // CalculationBase + ExtraDamage × osty.CurrentHp). 0 when no Osty.
            int playerOstyHp = 0;
            var allies = new List<SimAlly>();
            try
            {
                foreach (var ally in cs.Allies)
                {
                    if (ally == null || !ally.IsAlive) continue;
                    var monster = ally.Monster;
                    if (monster == null) continue;
                    string cls = monster.GetType().Name;
                    if (cls == "Osty") skeletonCount++;

                    // v0.7.11 — capture ally combat stats for damage contribution
                    int allyHp = (int)(CombatReflection.CreatureHpField?.GetValue(ally) ?? 0);
                    int allyBlock = (int)(CombatReflection.CreatureBlockField?.GetValue(ally) ?? 0);
                    if (cls == "Osty") playerOstyHp = allyHp;  // 2026-05-30 UNLEASH scaling
                    int allyIntentDmg = 0, allyIntentRepeats = 1;
                    bool allyHasAttack = false;
                    try
                    {
                        var nextMove = monster.NextMove;
                        if (nextMove?.Intents != null)
                        {
                            foreach (var intent in nextMove.Intents)
                            {
                                if (intent == null) continue;
                                var kind = CombatReflection.Classify(intent);
                                if (kind == IntentKind.Attack || kind == IntentKind.DeathBlow)
                                {
                                    int d = CombatReflection.GetAttackIntentDamage(intent);
                                    int r = CombatReflection.GetAttackIntentRepeats(intent);
                                    if (r <= 0) r = 1;
                                    allyIntentDmg += d;
                                    allyIntentRepeats = System.Math.Max(allyIntentRepeats, r);
                                    allyHasAttack = true;
                                }
                            }
                        }
                    }
                    catch { /* intent extraction is best-effort */ }

                    // v0.9 — CalcifyPower (Necrobinder player buff): Osty
                    // ally attacks gain +N damage per stack. Fold into the
                    // ally's intent damage so survival/race calculations
                    // see the boosted output.
                    int allyEffectiveDmg = allyIntentDmg;
                    if (playerCalcify > 0 && cls == "Osty" && allyIntentDmg > 0)
                        allyEffectiveDmg += playerCalcify;
                    allies.Add(new SimAlly
                    {
                        Hp = allyHp,
                        Block = allyBlock,
                        IntentDamage = allyEffectiveDmg,
                        IntentRepeats = System.Math.Max(1, allyIntentRepeats),
                        HasAttackIntent = allyHasAttack,
                        ClassName = cls,
                        SourceRef = ally,
                    });
                }
            }
            catch (System.Exception ex) { LogReflectionFailureOnce("allies-snapshot", ex); }

            // v0.6.8 — Turn / combat history counters. Walk CombatHistory.Entries
            // once, accumulating:
            //   • TurnAttacksPlayed / TurnSkillsPlayed — same-turn finished card
            //     plays of the corresponding type, owned by THIS player.
            //   • CombatPlayerHpLossEvents — DamageReceived events on player
            //     creature with UnblockedDamage > 0. Mirrors TEAR_ASUNDER's
            //     in-game multiplier logic.
            // Reflection-free — uses the public CombatManager.Instance.History API.
            // v0.7.2 — Player character entry id (e.g. "IRONCLAD"). Read once
            // from player.Character.Id.Entry; surfaced on SimState so PoolMeans
            // can look up the right character's static pool distribution.
            // Wrapped in try / catch because mid-transition states can null the
            // Character ref — empty string falls through to flat-magnitude path.
            string characterId = string.Empty;
            try { characterId = player.Character?.Id.Entry ?? string.Empty; }
            catch { }

            // v0.10 — Active relic snapshot. Captured once per planner step so
            // counter-based relics (PenNib AttacksPlayed%10, IronClub
            // CardsPlayed%4, VelvetChoker cards-played-this-turn) reflect live
            // state. Falls back to empty dict on reflection failure.
            var playerRelics = CombatReflection.GetPlayerRelics(player);
            // 2026-06-04 — ThrowingAxe ready = held AND not yet used this combat. The live relic's
            // Status is Active until its one play-count modification fires (then Normal), so the sim
            // only doubles the first card when the relic genuinely hasn't fired. Reflection-read to
            // avoid a hard dependency on the RelicStatus enum type; fail-open (assume ready).
            // Both ThrowingAxe (first card ×2) and Vambrace (first block ×2) are once-per-combat
            // relics whose live Status is Active until they fire; read it so the sim only applies
            // the bonus while genuinely unfired. Fail-open (assume ready if Status unreadable).
            bool throwingAxeReady = !RelicModelingOff && RelicActive(player, playerRelics, "ThrowingAxe");
            bool vambraceReady = !RelicModelingOff && RelicActive(player, playerRelics, "Vambrace");
            bool unsettlingLampReady = !RelicModelingOff && RelicActive(player, playerRelics, "UnsettlingLamp");
            // 2026-06-04 — held potions as first-class lookahead actions.
            var playerPotions = CombatReflection.GetPlayerPotions(player);

            int turnAttacksPlayed = 0, turnSkillsPlayed = 0, turnPowersPlayed = 0, combatHpLossEvents = 0;
            int turnCardsPlayed = 0;  // total this-turn plays (any type) — FTL PlayMax gate
            // 2026-05-30 — shivs played this turn (PhantomBlades adds its bonus only
            // to the FIRST shiv each turn, i.e. when this count is 0). Shivs are
            // Attack-type, so counted within the attack branch by CardTag.Shiv.
            int turnShivsPlayed = 0;
            // 2026-05-30 — combat-wide count of cards GENERATED by the player
            // (CardGeneratedEntry, GeneratedByPlayer). SUPERMASSIVE's CalculatedDamage
            // = base + ExtraDamage × this count.
            int combatCardsGenerated = 0;
            // 2026-05-28 S6-3a: SPITE's hit count depends on whether player lost
            // HP this turn (true → hits = Repeat=2, false → hits = 1).
            bool playerLostHpThisTurn = false;
            // 2026-05-28 S6-4: FORGOTTEN_RITUAL gains energy if any card was
            // exhausted this turn. Walks CardExhaustedEntry filtered by player
            // Owner + HappenedThisTurn.
            bool playerCardExhaustedThisTurn = false;
            // v0.9.2 — Counters for the 5 CalculatedHits cards that scale on
            // history-derived triggers: HELIX_DRILL (energy spent this turn),
            // RADIATE (stars gained this turn), PULL_FROM_BELOW (combat-wide
            // ethereal plays), RATTLE (Osty attacks this turn). Accumulated in
            // the same single-pass walk as turnAttacksPlayed/turnSkillsPlayed.
            int turnEnergySpent = 0, turnStarsGained = 0,
                combatEtherealPlayed = 0, turnOstyAttacks = 0;
            // v0.9.7 — DEATH_MARCH's CalculatedDamage multiplier counts non-hand-
            // draw CardDrawnEntry this turn (real draws from draw pile, excluding
            // hand→pile→hand cycles).
            int turnCardsDrawn = 0;
            int combatCardsDrawn = 0;   // MURDER: total draws this combat (all entries)
            // v0.9.2 — Osty reference captured before the history walk so
            // CreatureAttackedEntry can compare its Actor against this
            // player's skeleton (RATTLE's exact source pattern). Null when no
            // Osty is summoned or for non-Necrobinder characters.
            object? playerOsty = null;
            try { playerOsty = (object?)player.Osty; }
            catch (System.Exception ex) { LogReflectionFailureOnce("player-osty", ex); }
            // v0.9 — Track whether any Bound card was already played this turn
            // (ChainsOfBindingPower mechanic). When true, no further Bound
            // cards may be played until next turn.
            bool boundCardPlayedThisTurn = false;
            bool playerDoomAppliedThisTurn = false;
            bool playerBlockGainedFromCardThisTurn = false;
            // SmoggyPower per-turn flag — true once any Skill was played
            // this turn while SmoggyPower was active. Walks history same as
            // boundCardPlayedThisTurn; gated on playerSmoggy>0 so the check
            // is free when SmoggyPower is absent.
            bool smoggySkillPlayedThisTurn = false;
            // v0.9 — Per-target attack count this turn. Built alongside the
            // total counters; keyed by enemy index in the `enemies` list so
            // the simulator can compute BEAT_INTO_SHAPE's dynamic Forge
            // bonus ("+5 per OTHER same-target attack this turn"). Reads
            // CardPlay.Target (Creature?) and matches via SimEnemy.SourceRef.
            // Skill-self / AOE / null-target plays don't contribute (they
            // wouldn't count toward BEAT's same-target requirement anyway).
            Dictionary<int, int>? turnAttacksByTargetIdx = null;
            try
            {
                var history = CombatManager.Instance.History;
                if (history != null)
                {
                    foreach (var entry in history.Entries)
                    {
                        // 2026-06-01 — DEATHS_DOOR's WasDoomAppliedThisTurn: a
                        // PowerReceivedEntry this turn whose Power is DoomPower and
                        // Applier is this player (matches the card's own history query).
                        if (!playerDoomAppliedThisTurn && entry is PowerReceivedEntry pre
                            && pre.RoundNumber == cs.RoundNumber && pre.CurrentSide == cs.CurrentSide
                            && pre.Applier == creature
                            && pre.Power?.GetType().Name == "DoomPower")
                        {
                            playerDoomAppliedThisTurn = true;
                        }
                        // 2026-06-01 — UnmovablePower doubles only the first block-gain
                        // CARD of the turn; track whether the player already gained card
                        // block this turn (BlockGainedEntry with a CardPlay, this side).
                        if (!playerBlockGainedFromCardThisTurn && entry is BlockGainedEntry bge
                            && bge.RoundNumber == cs.RoundNumber && bge.CurrentSide == cs.CurrentSide
                            && bge.Actor == creature && bge.CardPlay != null)
                        {
                            playerBlockGainedFromCardThisTurn = true;
                        }
                        if (entry is CardPlayFinishedEntry cpe)
                        {
                            // v0.9.2 — Combat-wide ethereal play count for
                            // PULL_FROM_BELOW. Counted before the turn/side
                            // gate because the card's multiplier walks the
                            // entire CombatHistory.Entries with no
                            // HappenedThisTurn filter.
                            if (cpe.WasEthereal && cpe.CardPlay?.Card?.Owner == player)
                                combatEtherealPlayed++;
                            if (cpe.RoundNumber != cs.RoundNumber) continue;
                            if (cpe.CurrentSide != cs.CurrentSide) continue;
                            // Owner check — only count this player's plays in multiplayer.
                            if (cpe.CardPlay?.Card?.Owner != player) continue;
                            turnCardsPlayed++;  // any-type this-turn play (FTL PlayMax)
                            var type = cpe.CardPlay.Card.Type;
                            // v0.9 — Detect if a Bound card was played this
                            // turn (ChainsOfBindingPower mechanic). Affliction
                            // is per-card runtime state; check via reflection.
                            if (!boundCardPlayedThisTurn
                                && cpe.CardPlay.Card != null
                                && CardReflection.HasBoundAffliction(cpe.CardPlay.Card))
                            {
                                boundCardPlayedThisTurn = true;
                            }
                            if (type == CardType.Attack)
                            {
                                turnAttacksPlayed++;
                                // 2026-05-30 — shiv subset for PhantomBlades.
                                try
                                {
                                    var tags = cpe.CardPlay.Card.Tags;
                                    if (tags != null && System.Linq.Enumerable.Contains(tags,
                                            MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv))
                                        turnShivsPlayed++;
                                }
                                catch { }
                                // Map target Creature → SimEnemy idx via SourceRef.
                                var target = cpe.CardPlay.Target;
                                if (target != null)
                                {
                                    for (int i = 0; i < enemies.Count; i++)
                                    {
                                        if (ReferenceEquals(enemies[i].SourceRef, target))
                                        {
                                            turnAttacksByTargetIdx ??= new Dictionary<int, int>();
                                            turnAttacksByTargetIdx[i] = turnAttacksByTargetIdx.TryGetValue(i, out var prev)
                                                ? prev + 1
                                                : 1;
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (type == CardType.Skill)
                            {
                                turnSkillsPlayed++;
                                // SmoggyPower per-turn flag: any prior Skill
                                // played this turn while SmoggyPower is
                                // active triggers the lockout. Gated on
                                // playerSmoggy>0 to skip the assignment
                                // when SmoggyPower isn't on the player.
                                if (!smoggySkillPlayedThisTurn && playerSmoggy > 0)
                                    smoggySkillPlayedThisTurn = true;
                            }
                            else if (type == CardType.Power)
                            {
                                turnPowersPlayed++;   // RainbowRing: attack+skill+power this turn
                            }
                        }
                        else if (entry is DamageReceivedEntry dre)
                        {
                            if (dre.Receiver != creature) continue;
                            if (dre.Result.UnblockedDamage > 0)
                            {
                                combatHpLossEvents++;
                                // 2026-05-28 S6-3a: SPITE's LostHpThisTurn check.
                                // Spite.cs:48 — `e.HappenedThisTurn(creature.CombatState)
                                // && e.Receiver == creature && e.Result.UnblockedDamage > 0`.
                                // Same predicate; gate by current round/side to scope
                                // to "this turn" (turn-end resets round/side counters).
                                if (dre.RoundNumber == cs.RoundNumber
                                    && dre.CurrentSide == cs.CurrentSide)
                                {
                                    playerLostHpThisTurn = true;
                                }
                            }
                        }
                        else if (entry is CardExhaustedEntry cee)
                        {
                            // 2026-05-28 S6-4: FORGOTTEN_RITUAL etc.
                            // CombatManager.History.Entries.OfType<CardExhaustedEntry>()
                            //   .Any(e => e.HappenedThisTurn(combatState)
                            //          && e.Card.Owner == player)
                            if (cee.RoundNumber != cs.RoundNumber) continue;
                            if (cee.CurrentSide != cs.CurrentSide) continue;
                            if (cee.Card?.Owner != player) continue;
                            playerCardExhaustedThisTurn = true;
                        }
                        // v0.9.2 — Turn-gated counters for HELIX_DRILL /
                        // RADIATE / RATTLE. Each mirrors the exact filter the
                        // card's CalculatedHits multiplier uses (decompile
                        // sts2.decompiled.cs lines 353179 / 357507 / 357678).
                        else if (entry is EnergySpentEntry ese)
                        {
                            if (ese.RoundNumber != cs.RoundNumber) continue;
                            if (ese.CurrentSide != cs.CurrentSide) continue;
                            if (ese.Actor != creature) continue;
                            turnEnergySpent += ese.Amount;
                        }
                        // 2026-05-30 — combat-wide player card-generation count for
                        // SUPERMASSIVE (no turn/side gate — counts the whole combat).
                        else if (entry is CardGeneratedEntry cge)
                        {
                            try
                            {
                                if (cge.GeneratedByPlayer && cge.Actor?.Player == player)
                                    combatCardsGenerated++;
                            }
                            catch { }
                        }
                        else if (entry is StarsModifiedEntry sme)
                        {
                            if (sme.RoundNumber != cs.RoundNumber) continue;
                            if (sme.CurrentSide != cs.CurrentSide) continue;
                            if (sme.Actor != creature) continue;
                            if (sme.Amount > 0) turnStarsGained += sme.Amount;
                        }
                        else if (entry is CreatureAttackedEntry cae)
                        {
                            if (playerOsty == null) continue;
                            if (cae.RoundNumber != cs.RoundNumber) continue;
                            if (cae.CurrentSide != cs.CurrentSide) continue;
                            if (System.Object.ReferenceEquals(cae.Actor, playerOsty))
                                turnOstyAttacks++;
                        }
                        // v0.9.7 — DEATH_MARCH counter: this turn's non-hand-draw
                        // CardDrawnEntry by this player (decompile sts2.decompiled.cs:349298).
                        else if (entry is CardDrawnEntry cde)
                        {
                            if (cde.Actor != creature) continue;
                            combatCardsDrawn++;   // MURDER: all draws this combat (no turn/hand filter)
                            if (cde.RoundNumber != cs.RoundNumber) continue;
                            if (cde.CurrentSide != cs.CurrentSide) continue;
                            if (cde.FromHandDraw) continue;
                            turnCardsDrawn++;     // DEATH_MARCH: this-turn non-hand draws
                        }
                    }
                }
            }
            catch (System.Exception ex) { LogReflectionFailureOnce("history-walk", ex); }

            // v0.9.2 — Status cards currently owned by the player across all
            // piles except Exhaust (FLAK_CANNON's CalculatedHits multiplier).
            // v0.9.7 — Extended in the same walk: star-cost cards (CRESCENT_SPEAR)
            // and OstyAttack-tag cards (SQUEEZE) deck-wide snapshots.
            int playerStatusCardCount = 0;
            int playerStarCostCardCount = 0;
            int playerOstyAttackCardCount = 0;
            int playerSoulsInExhaust = 0;   // SOUL_STORM
            try
            {
                var allCards = player.PlayerCombatState?.AllCards;
                if (allCards != null)
                {
                    foreach (var c in allCards)
                    {
                        if (c == null) continue;
                        if (c.Type == CardType.Status && c.Pile?.Type != PileType.Exhaust)
                            playerStatusCardCount++;
                        if (c.Pile?.Type == PileType.Exhaust && c.Id.Entry == "SOUL")
                            playerSoulsInExhaust++;
                        try
                        {
                            if (c.CanonicalStarCost >= 0 || c.HasStarCostX)
                                playerStarCostCardCount++;
                        }
                        catch { }
                        try
                        {
                            if (c.Tags != null && c.Tags.Contains(CardTag.OstyAttack))
                                playerOstyAttackCardCount++;
                        }
                        catch { }
                    }
                }
            }
            catch (System.Exception ex) { LogReflectionFailureOnce("deck-card-count", ex); }

            // v0.5.1 — Pile cards skip the CanPlay() check (irrelevant outside hand)
            // but reuse the same builder so Effect / Cost / Kind are consistent with
            // hand cards. The simulator averages over these for draw EV modeling.
            var drawPile = new List<SimCard>();
            if (drawPileRaw != null)
                foreach (var card in drawPileRaw) drawPile.Add(BuildSimCard(card, requirePlayability: false));
            var discardPile = new List<SimCard>();
            if (discardPileRaw != null)
                foreach (var card in discardPileRaw) discardPile.Add(BuildSimCard(card, requirePlayability: false));
            // Append PileType.Play stuck cards into the discard view — they
            // can't be re-played from Play and act as discard for next-turn
            // mechanics. Matches DiscardPileSize calculation above.
            if (playPileRaw != null)
                foreach (var card in playPileRaw) discardPile.Add(BuildSimCard(card, requirePlayability: false));

            // v0.10 — Sum GalvanicPower amount across all sources in combat.
            // Typically a single galvanic enemy, but additive on the rare
            // multi-source case. Player-side GalvanicPower (curses, mod-applied)
            // is also summed defensively.
            int galvanicAmount = 0;
            if (playerPowerDict != null
                && playerPowerDict.TryGetValue("GalvanicPower", out var pgAmt))
                galvanicAmount += pgAmt;
            foreach (var enemy in enemies)
            {
                if (!enemy.IsAlive) continue;
                if (enemy.Powers != null
                    && enemy.Powers.TryGetValue("GalvanicPower", out var egAmt))
                    galvanicAmount += egAmt;
            }

            // 2026-06-11 — run position for the empirical HP value curve. Reflection-safe:
            // Player.RunState may be inaccessible in some hosts; 0/-1 = flat-constant fallback.
            int actNumber = 0, actFloor = -1;
            try
            {
                var runState = player.GetType().GetProperty("RunState")?.GetValue(player);
                if (runState != null)
                {
                    var ai = runState.GetType().GetProperty("CurrentActIndex")?.GetValue(runState);
                    if (ai is int aiv) actNumber = aiv + 1;
                    var af = runState.GetType().GetProperty("ActFloor")?.GetValue(runState);
                    if (af is int afv) actFloor = afv;
                }
            }
            catch { /* curve falls back to flat constant */ }

            return new SimState
            {
                PlayerHp = hp,
                PlayerBlock = block,
                PlayerEnergy = energy,
                Enemies = enemies,
                Hand = hand,
                ActNumber = actNumber,
                ActFloor = actFloor,
                // Encoder-parity fields (snapshot here so SimStateAdapter
                // can emit the same numbers Sts2CombatEnv._encode does).
                // 2026-05-27 — Turn pulled from cs.RoundNumber so the obs
                // vector's index 0 (turn) matches Episode.BuildObservation
                // instead of always emitting 0. RoundNumber is 1-based
                // (combat starts at 1); Episode._turn is 0-based (Reset
                // sets it to 0, ++ on each EndTurn). Subtract 1 to align.
                Turn = System.Math.Max(0, cs.RoundNumber - 1),
                PlayerMaxHp = maxHp,
                PlayerMaxEnergy = maxEnergy,
                ExhaustPileCount = exhaustPileSize,
                CharacterId = characterId,
                PlayerStrength = playerStr,
                PlayerFacing = playerFacing,
                PlayerDexterity = playerDex,
                PlayerVigor = playerVigor,
                PlayerBuffer = playerBuffer,
                PlayerLethality = playerLethality,
                PlayerTracking = playerTracking,
                PlayerCruelty = playerCruelty,
                PlayerRage = playerRage,
                PlayerAfterimage = playerAfterimage,
                PlayerUnmovable = playerUnmovable,
                PlayerAccuracy = playerAccuracy,
                PlayerPhantomBlades = playerPhantomBlades,
                PlayerChildOfTheStars = playerChildOfTheStars,
                PlayerParry = playerParry,
                PlayerInferno = playerInferno,
                PlayerHaunt = playerHaunt,
                PlayerSmokestack = playerSmokestack,
                PlayerPillar = playerPillar,
                PlayerArsenal = playerArsenal,
                PlayerShroud = playerShroud,
                PlayerPanache = playerPanache,
                PanacheCardsLeft = panacheCardsLeft,
                PlayerBlackHole = playerBlackHole,
                PlayerSleightOfFlesh = playerSleightOfFlesh,
                PlayerOrbit = playerOrbit,
                PlayerOrbitSpentMod = playerOrbitSpentMod,
                PlayerEnrage = playerEnrage,
                PlayerMonologue = playerMonologue,
                PlayerCorruption = playerCorruption,
                PlayerBurst = playerBurst,
                PlayerThorns = playerThorns,
                PlayerFeelNoPain = playerFeelNoPain,
                // v0.7.98 — remaining echoes this turn.
                // 2026-06-01 — EchoFormPower.ModifyCardPlayCount counts CardPlaysStarted
                // (IsFirstInSeries, this turn) of ANY card type — it doubles the first
                // `Amount` cards regardless of type, POWERS included. Subtracting only
                // attacks+skills missed a power played first this turn, so the sim kept echo
                // active and double-dealt a card the engine no longer echoed (BEAM_CELL 3→6
                // vs real 3, §106). Use the any-type turnCardsPlayed (which already counts
                // every this-turn play). For the common Amount=1 the first play deactivates
                // echo whether or not it was a power.
                PlayerEchoForm = System.Math.Max(0, echoStack - turnCardsPlayed),
                PlayerJuggernaut = playerJuggernaut,
                PlayerHunger = playerHunger,
                PlayerFlameBarrier = playerFlameBarrier,
                PlayerDanseMacabre = playerDanseMacabre,
                // Snapshot: Unmovable starts un-used each turn; conservative — if
                // mid-turn we re-snapshot, the live game state's first-block-played
                // bit isn't easily readable so we assume not-yet-used. The next
                // block card will then double; if game actually used it, the planner
                // over-estimates block until consumed in the simulator.
                UnmovableUsedThisTurn = false,
                PlayerVulnerable = playerVuln,
                PlayerWeak = playerWeak,
                PlayerFrail = playerFrail,
                PlayerPoison = playerPoison,
                PlayerBurn = playerBurn,
                PlayerConstrict = playerConstrict,
                DrawPileSize = drawPileSize,
                DiscardPileSize = discardPileSize,
                DrawPile = drawPile,
                DiscardPile = discardPile,
                PlayerStars = playerStars,
                GalvanicAmount = galvanicAmount,
                PlayerDoom = playerDoom,
                PlayerOrbCount = orbCount,
                PlayerOrbCapacity = orbCapacity,
                OrbQueue = orbQueue,
                OrbEvokeValues = orbEvokeValues,
                PlayerFocus = playerFocus,
                PlayerThunder = playerThunder,
                PlayerIntangible = playerIntangible,
                PlayerEndOfTurnBlockBonus = playerEotBlockBonus,
                PlayerFreeAttacks = playerFreeAttacks,
                PlayerFreeSkills = playerFreeSkills,
                PlayerFreePowers = playerFreePowers,
                PlayerBlockMult = playerBlockMult,
                PlayerFreeCardBudget = System.Math.Max(0, voidFormAmt - turnCardsPlayed),
                PlayerVoidFormAmount = voidFormAmt,
                PlayerVeilpiercer = playerVeilpiercer,
                PlayerFasten = playerFasten,
                PlayerPowers = playerPowerDict,
                SoulInPiles = soulInPiles,
                ShivInPiles = shivInPiles,
                StarCardsInDeck = starCardsInDeck,
                SkeletonCount = skeletonCount,
                Allies = allies,
                ExhaustPileSize = exhaustPileSize,
                SovereignBladeCount = sovereignBladeCount,
                TurnAttacksPlayed = turnAttacksPlayed,
                TurnPowersPlayed = turnPowersPlayed,
                TurnCardsPlayed = turnCardsPlayed,
                TurnSkillsPlayed = turnSkillsPlayed,
                MakeItSoInDraw = makeItSoInDraw,
                MakeItSoInDiscard = makeItSoInDiscard,
                PlayerDoomAppliedThisTurn = playerDoomAppliedThisTurn,
                PlayerBlockGainedFromCardThisTurn = playerBlockGainedFromCardThisTurn,
                PlayerJugglingCounter = (playerPowerDict != null && playerPowerDict.ContainsKey("JugglingPower"))
                    ? CombatReflection.GetPowerInternalCounter(creature, "JugglingPower", "attacksPlayedThisTurn")
                    : -1,
                TurnShivsPlayed = turnShivsPlayed,
                CombatCardsGenerated = combatCardsGenerated,
                PlayerOstyHp = playerOstyHp,
                TurnEnergySpent = turnEnergySpent,
                TurnStarsGained = turnStarsGained,
                CombatEtherealPlayed = combatEtherealPlayed,
                TurnOstyAttacks = turnOstyAttacks,
                PlayerStatusCardCount = playerStatusCardCount,
                PlayerHandTurnEndDamage = ComputeHandTurnEndDamage(hand),
                TurnCardsDrawn = turnCardsDrawn,
                CombatCardsDrawn = combatCardsDrawn,
                PlayerSoulsInExhaust = playerSoulsInExhaust,
                PlayerStarCostCardCount = playerStarCostCardCount,
                PlayerOstyAttackCardCount = playerOstyAttackCardCount,
                TurnAttacksByTargetIdx = (IReadOnlyDictionary<int, int>?)turnAttacksByTargetIdx
                    ?? new Dictionary<int, int>(),
                CombatPlayerHpLossEvents = combatHpLossEvents,
                PlayerLostHpThisTurn = playerLostHpThisTurn,
                PlayerCardExhaustedThisTurn = playerCardExhaustedThisTurn,
                PlayerNoBlock = playerNoBlock,
                PlayerNoDraw = playerNoDraw,
                PlayerNoEnergyGain = playerNoEnergyGain,
                PlayerConfused = playerConfused,
                PlayerTender = playerTender,
                PlayerShrinkPercent = playerShrinkPct,
                PlayerDebilitate = playerDebilitate,
                BoundCardPlayedThisTurn = boundCardPlayedThisTurn,
                PlayerSmoggy = playerSmoggy,
                SmoggySkillPlayedThisTurn = smoggySkillPlayedThisTurn,
                PlayerRelics = playerRelics,
                PlayerPotions = playerPotions,
                PlayerThrowingAxeReady = throwingAxeReady,
                PlayerVambraceReady = vambraceReady,
                // BrilliantScarf makes the 5th card each turn free → ready when exactly 4 played.
                PlayerBrilliantScarfReady = !RelicModelingOff && playerRelics.ContainsKey("BrilliantScarf") && turnCardsPlayed == 4,
                PlayerUnsettlingLampReady = unsettlingLampReady,
            };
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Warn($"[CombatAI] snapshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// v0.5.1 — Shared SimCard builder used by both hand and pile snapshots. Hand
    /// cards need the live CanPlay() result (filters curses / conditional plays);
    /// pile cards are abstractions for EV averaging and skip the check. Orb meta
    /// is layered for both so a drawn Cold Snap is modeled with its channel kind.
    /// </summary>
    private static SimCard BuildSimCard(CardModel card, bool requirePlayability)
    {
        bool playable = true;
        if (requirePlayability)
        {
            playable = false;
            try { playable = card.CanPlay(); } catch { }
        }
        var id = CardReflection.GetIdEntry(card);
        var catalogInfo = Data.CardCatalog.Lookup(id);
        var axes = catalogInfo?.Axes ?? System.Array.Empty<string>();
        var baseEffect = CardReflection.GetEffectSummary(card);
        int costSpent = CardReflection.GetCost(card);
        var orbMeta = Reflection.OrbCardCatalog.Lookup(id, costSpent, axes, CardReflection.IsPower(card));
        var effect = baseEffect with {
            EvokeCount = orbMeta.EvokeCount,
            ChannelCount = orbMeta.ChannelCount,
            ChannelKind = orbMeta.ChannelKind,
        };

        // Self-heal token-generator axes from runtime DynamicVars. The master
        // catalog occasionally misses side-effect producers (LEADING_STRIKE,
        // CLOAK_AND_DAGGER, STORM_OF_STEEL). Token counts on the live
        // CardModel are authoritative; if the corresponding _PRODUCER /
        // CARD_GEN axis is absent, add it. Build the augmented list once and
        // reuse for downstream consumers.
        axes = TokenProducerAxes.AugmentTokenProducerAxes(axes, effect);
        // Sly detection — combined static (catalog) + runtime (CardKeyword
        // reflection). The static CUNNING axis covers inherent Sly cards
        // (TACTICIAN / REFLEX / ABRASIVE / ...); the runtime keyword check
        // covers HAND_TRICK's "Add Sly to a Skill in hand this turn" which
        // applies the keyword to a SPECIFIC card chosen at play time.
        // Either signal is sufficient; reflection failure falls back to
        // axis-only behavior.
        bool isSly = false;
        for (int i = 0; i < axes.Count; i++)
        {
            if (axes[i] == "CUNNING") { isSly = true; break; }
        }
        if (!isSly && Reflection.CardReflection.HasSlyKeyword(card))
            isSly = true;

        // v0.10 — Status / hand-decay cards (INFECTION = 3 self-dmg/turn).
        // GetEffectSummary already populated effect.Damage from the card's
        // DamageVar; we only need to know whether that damage applies as
        // OnTurnEndInHand rather than OnPlay. Reflection-only flag — no
        // catalog dependency, so new game-version status cards are picked
        // up automatically as long as they override HasTurnEndInHandEffect.
        // INFECTION-class self-damage rides a Damage var; BECKON-class
        // (SoulFysh-injected status, sts2.decompiled.cs:395745 — HpLossVar 6
        // Unblockable / turn while the card sits in hand) rides an HpLoss var,
        // so effect.Damage stays 0 for it. Fall back to HpLossAmount. The
        // HasTurnEndInHandEffect guard keeps voluntary on-play HP-loss cards
        // (BLOODLETTING / OFFERING / HEMOKINESIS) out — their loss fires OnPlay,
        // not while held — so reusing HpLossAmount here is safe.
        int turnEndInHandDmg = 0;
        if (CardReflection.HasTurnEndInHandEffect(card))
            turnEndInHandDmg = effect.Damage > 0 ? effect.Damage : effect.HpLossAmount;

        return new SimCard
        {
            Id = id,
            Cost = costSpent,
            Kind = card.Type,
            Target = card.TargetType,
            SourceRef = card,
            Effect = effect,
            IsPlayable = playable,
            Axes = axes,
            PrimaryBuildTags = catalogInfo?.PrimaryBuildTags ?? System.Array.Empty<string>(),
            IsRetain = catalogInfo?.Retain ?? false,
            IsEthereal = catalogInfo?.Ethereal ?? false,
            IsInnate = catalogInfo?.Innate ?? false,
            // 2026-05-30 — prefer the LIVE card's runtime Exhaust keyword
            // (upgrade-aware: GRAVEBLAST/etc. RemoveKeyword(Exhaust) on upgrade);
            // fall back to the static catalog when reflection is unavailable.
            IsExhaust = CardReflection.HasExhaustKeyword(card) ?? catalogInfo?.Exhaust ?? false,
            IsFetchTrigger = catalogInfo?.FetchTrigger ?? false,
            // 2026-06-04 — live CardTag.Defend for precise FastenPower block bonus.
            IsDefendTagged = card.Tags != null && card.Tags.Contains(CardTag.Defend),
            IsSly = isSly,
            // v0.9 — Bound affliction (ChainsOfBindingPower restriction).
            IsBound = CardReflection.HasBoundAffliction(card),
            // Smog affliction (SmoggyPower / LivingFog) — card can't be
            // played until SmoggyPower's AfterTurnEnd clears it.
            IsSmogged = CardReflection.HasSmogAffliction(card),
            // v0.10 — Galvanized affliction (GalvanicPower). Deals damage
            // on play through the normal block-absorbing path.
            IsGalvanized = CardReflection.HasGalvanizedAffliction(card),
            TurnEndInHandSelfDamage = turnEndInHandDmg,
        };
    }

    /// <summary>
    /// v0.6.7 — Walk a card pile counting Soul / Shiv / SovereignBlade instances
    /// for SimState.{SoulInPiles, ShivInPiles, SovereignBladeCount}. Uses runtime
    /// class-name matching against the game's CardModel subclasses (Soul, Shiv,
    /// SovereignBlade in MegaCrit.Sts2.Core.Models.Cards). Catalog ID matching
    /// would also work but is more brittle to localization / id format changes.
    /// </summary>
    private static void CountTokenCards(
        System.Collections.Generic.IReadOnlyList<CardModel>? pile,
        ref int soul, ref int shiv, ref int sovereign)
    {
        if (pile == null) return;
        foreach (var card in pile)
        {
            if (card == null) continue;
            switch (card.GetType().Name)
            {
                case "Soul":           soul++; break;
                case "Shiv":           shiv++; break;
                case "SovereignBlade": sovereign++; break;
            }
        }
    }

    // 2026-05-30 — count cards with a star cost (CanonicalStarCost >= 0, i.e. a
    // real star card; -1 means no star cost) OR an X star cost. CRESCENT_SPEAR's
    // CalculatedDamage scales by this deck-wide count. Cheap: just reads two
    // public ints per card, no SimCard build.
    private static int CountStarCards(System.Collections.Generic.IReadOnlyList<CardModel>? pile)
    {
        if (pile == null) return 0;
        int n = 0;
        foreach (var card in pile)
        {
            if (card == null) continue;
            try { if (card.CanonicalStarCost >= 0 || card.HasStarCostX) n++; }
            catch { }
        }
        return n;
    }

    /// <summary>
    /// v0.10 — Sum of self-damage from in-hand status cards that fire
    /// OnTurnEndInHand (INFECTION = 3 dmg per card per turn). The
    /// TurnEndInHandSelfDamage field is set in BuildSimCard via reflection
    /// on CardModel.HasTurnEndInHandEffect. Survival projection uses this
    /// to model deck-pollution bleed so the planner doesn't think a hand
    /// stacked with INFECTION is "safe" because no enemy intent moves.
    /// </summary>
    private static int ComputeHandTurnEndDamage(System.Collections.Generic.IList<SimCard> hand)
    {
        if (hand == null || hand.Count == 0) return 0;
        int total = 0;
        for (int i = 0; i < hand.Count; i++)
            total += hand[i].TurnEndInHandSelfDamage;
        return total;
    }

    private static bool WasSpawnedThisTurn(object? monster)
    {
        if (monster == null) return false;
        var f = CombatReflection.MonsterSpawnedThisTurnField;
        if (f == null) return false;
        try { return f.GetValue(monster) is bool b && b; }
        catch { return false; }
    }

    private static SimEnemy BuildSimEnemy(Creature enemy, int playerHp, bool isBoss, bool isElite, bool isMinion)
    {
        int hp = (int)(CombatReflection.CreatureHpField?.GetValue(enemy) ?? 0);
        int block = (int)(CombatReflection.CreatureBlockField?.GetValue(enemy) ?? 0);
        int vuln = CombatReflection.GetPowerAmount(enemy, "VulnerablePower");
        int weak = CombatReflection.GetPowerAmount(enemy, "WeakPower");
        int eStr = CombatReflection.GetPowerAmount(enemy, "StrengthPower");
        int eArtifact = CombatReflection.GetPowerAmount(enemy, "ArtifactPower");
        int eFrail = CombatReflection.GetPowerAmount(enemy, "FrailPower");
        int ePoison = CombatReflection.GetPowerAmount(enemy, "PoisonPower");
        int eConstrict = CombatReflection.GetPowerAmount(enemy, "ConstrictPower");
        int eBurn = CombatReflection.GetPowerAmount(enemy, "BurnPower");

        // v0.4 — per-hit damage cap (Intangible = 1, HardToKill = Amount) and Thorns reflect.
        var powerDict = CombatReflection.GetAllPowers(enemy);
        int damageCap = 0;
        if (powerDict.TryGetValue("IntangiblePower", out _)) damageCap = 1;
        // 2026-05-29 — SlipperyPower caps each incoming hit to 1
        // (sts2.dll SlipperyPower.ModifyDamageCap returns 1m). Found via the
        // damage-pipeline trace: Hook.ModifyDamage(17)=>1 on Slippery enemies,
        // sim dealt full → big enemy_hp over-prediction (SOVEREIGN_BLADE class).
        // 2026-06-01 — FlutterPower was ALSO mapped here to a 1-cap, but that
        // misread its ModifyDamageMultiplicative: the `return 1m` is the no-op
        // multiplier (1.0 = no change), NOT a cap-to-1. Flutter's real reduction
        // is DamageDecrease/100 = 0.5 (HALVE), modeled in AnalyticalSimulator's
        // flying-halving (totalDmg/=2). Keeping the 1-cap here double-applied
        // with the /2 → 1/2 = 0 (integer), so attacks vs flying ThievingHopper
        // (FlutterPower+EscapeArtist) whiffed to 0 in the sim while real dealt
        // card×0.5 (PINPOINT/SHIV/GIANT_ROCK/COLLISION_COURSE/MAKE_IT_SO/NEUTRALIZE).
        // Removed — the halving is the sole correct Flutter model.
        int slipperyStacks = 0;
        if (powerDict.TryGetValue("SlipperyPower", out var slip) && slip > 0)
        {
            damageCap = 1;
            // 2026-06-05 — consumable Slippery (env STS2_SLIPPERY_CONSUME). Vantom's Slippery 9
            // is a ONE-TIME 9-hit buffer (decompile: applied once at combat start, never re-applied),
            // but the sim folds it into the PERMANENT DamageCapPerHit=1 → the planner sees Vantom as
            // forever-1/hit (unkillable) and never finds the "strip stacks → burst" line. Capturing
            // the stack count lets the precise cap + state transition model it as consumable. Flag
            // off → stacks stay 0 = legacy permanent-cap behavior (byte-identical).
            if (_slipperyConsume) slipperyStacks = slip;
        }
        if (powerDict.TryGetValue("HardToKillPower", out var hard) && hard > 0)
            damageCap = damageCap == 0 ? hard : System.Math.Min(damageCap, hard);
        int thorns = powerDict.TryGetValue("ThornsPower", out var t) ? t : 0;

        // v0.9 — SkittishPower (Phantasmal Gardener etc.): when this enemy
        // takes unblocked damage from a player CARD attack for the FIRST time
        // each turn, gains Amount block (reactive). Read the live
        // hasGainedBlockThisTurn flag via reflection on Data — when true,
        // the trigger is already spent for this turn and Amount=0 effectively.
        int skittish = powerDict.TryGetValue("SkittishPower", out var sk) ? sk : 0;
        bool skittishUsed = false;
        if (skittish > 0)
        {
            try
            {
                foreach (var p in enemy.Powers)
                {
                    if (p?.GetType().Name != "SkittishPower") continue;
                    var dataField = p.GetType().GetField("_data",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var dataObj = dataField?.GetValue(p);
                    var flagProp = p.GetType().GetProperty("HasGainedBlockThisTurn");
                    if (flagProp?.GetValue(p) is bool flag) { skittishUsed = flag; break; }
                    // Fallback: try internal Data class field directly.
                    var hasGainedField = dataObj?.GetType().GetField("hasGainedBlockThisTurn");
                    if (hasGainedField?.GetValue(dataObj) is bool flag2) { skittishUsed = flag2; break; }
                }
            }
            catch { /* reflection best-effort — fallback to "not used yet" */ }
        }

        // v0.9 — CurlUpPower (Louse-style one-shot reactive block). The power
        // self-removes after triggering, so if present in the dict it hasn't
        // fired yet this combat. Amount = block gained on first hit.
        int curlUp = powerDict.TryGetValue("CurlUpPower", out var cu) ? cu : 0;

        // 2026-06-03 — SelfFormingClayPower: one-shot block regain when the enemy's block is
        // fully cleared. Same reactive-block shape as CurlUp (see SimEnemy).
        int selfFormingClay = powerDict.TryGetValue("SelfFormingClayPower", out var sfc) ? sfc : 0;

        // v0.9 — ImbalancedPower (BowlbugRock self-stun-on-fully-blocked).
        // Stack 1 by default (Single stack type). Captures presence.
        int imbalanced = powerDict.TryGetValue("ImbalancedPower", out var ib) ? ib : 0;

        // v0.9 — TerritorialPower (per-turn-end +N Strength). Captured for
        // future multi-turn threat projection. Currently also folded into
        // HasTurnStartStrengthBuff flag below for back-compat.
        int territorial = powerDict.TryGetValue("TerritorialPower", out var tp) ? tp : 0;

        // v0.9 — PaperCutsPower (Tier B): enemy gives player -N MaxHP per
        // unblocked damage event landed. Long-term value loss; informs
        // "kill this enemy first" prioritization.
        int paperCuts = powerDict.TryGetValue("PaperCutsPower", out var pc) ? pc : 0;
        int painfulStabs = powerDict.TryGetValue("PainfulStabsPower", out var psb) ? psb : 0;

        // SandpitPower (The Insatiable): hard instakill counter. Decrements
        // each enemy turn; when it transitions to 0 the AfterRemoved hook
        // force-kills the player + pets + Osty regardless of revive.
        // Capture the raw stack — AdvanceTurn handles the decrement and
        // models the instakill in survival sim.
        int sandpit = powerDict.TryGetValue("SandpitPower", out var sp) ? sp : 0;

        // v0.10 — InfestedPower (Phrog Parasite Elite, decompile :315807).
        // Amount = spawn count on death (always 4 in the base monster).
        // Killing the carrier triggers AfterDeath → spawn Wrigglers AND
        // ShouldStopCombatFromEnding=true → combat continues. Both make
        // chip-killing counterproductive — see SimEnemy.OnDeathSpawnsCount.
        int onDeathSpawns = powerDict.TryGetValue("InfestedPower", out var inf) ? inf : 0;

        // HardenedShell — read live DisplayAmount (Amount − damageReceivedThisTurn).
        // The dict above only has the static Amount, not the live remaining cap.
        int hardenedShellRemaining = 0;
        if (powerDict.TryGetValue("HardenedShellPower", out _))
        {
            try
            {
                foreach (var p in enemy.Powers)
                {
                    if (p == null) continue;
                    if (p.GetType().Name == "HardenedShellPower")
                    {
                        var dispProp = p.GetType().GetProperty("DisplayAmount");
                        var raw = dispProp?.GetValue(p);
                        if (raw is int ri) hardenedShellRemaining = ri;
                        else if (raw is decimal rd) hardenedShellRemaining = (int)rd;
                        else if (raw != null) hardenedShellRemaining = System.Convert.ToInt32(raw);
                        break;
                    }
                }
            }
            catch { }
        }

        // v0.2.9 — turn-start strength buffs make the enemy snowball (Ritual / Enrage / similar).
        // v0.9 — also TerritorialPower (per-turn-end +Strength).
        // v0.10 — also HighVoltagePower (per-turn-end +Strength, Fabricator
        // family). Doesn't decrement → permanent scaling, prioritize killing.
        int ritualAmt = CombatReflection.GetPowerAmount(enemy, "RitualPower");
        int highVoltageAmt = CombatReflection.GetPowerAmount(enemy, "HighVoltagePower");
        bool hasRitual = ritualAmt > 0
                      || CombatReflection.GetPowerAmount(enemy, "EnragePower") > 0
                      || CombatReflection.GetPowerAmount(enemy, "FeralPower") > 0
                      || highVoltageAmt > 0
                      || territorial > 0;
        // 2026-06-03 — actual per-turn-end Strength gain (decompile: Ritual/Territorial/
        // HighVoltage each Apply<StrengthPower>(owner, Amount) at turn end). AdvanceTurn adds
        // this instead of a flat +1, fixing Amount>1 under-projection. Enrage (reactive on
        // player skills) and Feral (zero-cost-attack mechanic) are NOT per-turn → excluded;
        // they keep the +1 approximation via HasTurnStartStrengthBuff.
        int turnStartStrGain = ritualAmt + territorial + highVoltageAmt;

        int totalDmg = 0;
        bool hasAtk = false, hasDeathBlow = false, hasBuff = false, hasDebuff = false;
        bool hasHeal = false, hasSummon = false, hasDefend = false, hasStatus = false;
        bool isInert = false, isHidden = false, isUnknown = false;

        var nextMove = enemy.Monster?.NextMove;
        if (nextMove?.Intents != null)
        {
            foreach (var intent in nextMove.Intents)
            {
                if (intent == null) continue;
                var kind = CombatReflection.Classify(intent);
                switch (kind)
                {
                    case IntentKind.Attack:
                        hasAtk = true;
                        AccumulateAttackDmg(intent, ref totalDmg);
                        break;
                    case IntentKind.DeathBlow:
                        hasDeathBlow = true;
                        // DeathBlow inherits damage semantics — still extract.
                        AccumulateAttackDmg(intent, ref totalDmg);
                        break;
                    case IntentKind.Buff: hasBuff = true; break;
                    case IntentKind.Debuff: hasDebuff = true; break;
                    case IntentKind.Heal: hasHeal = true; break;
                    case IntentKind.Summon: hasSummon = true; break;
                    case IntentKind.Defend: hasDefend = true; break;
                    case IntentKind.Status: hasStatus = true; break;
                    case IntentKind.Inert: isInert = true; break;
                    case IntentKind.Hidden: isHidden = true; break;
                    case IntentKind.Unknown: isUnknown = true; break;
                    case IntentKind.Other: break;
                }
            }
        }

        var threat = ComputeThreat(hasAtk, totalDmg, hasBuff, hasDeathBlow,
            hasHeal, hasSummon, hasDebuff, hasDefend, hasStatus,
            isInert, isHidden, isUnknown, playerHp);

        // 2026-06-04 — Self-heal amount for known healers. The heal value is hardcoded in the
        // monster move (e.g. WaterfallGiant.SiphonHeal => 15, scaled ×players) and NOT exposed on
        // the HealIntent, so it cannot be read generically — behavioral carve-out keyed on the
        // monster class name, gated on actually having a heal intent this turn. Default 0 = no
        // projection (graceful for unknown healers). See SimEnemy.HealAmount.
        int healAmount = hasHeal ? KnownHealAmount(enemy.Monster?.GetType().Name) : 0;

        return new SimEnemy
        {
            Hp = hp,
            Block = block,
            // 2026-05-31 — SkittishPower's once/turn gate (Data.hasGainedBlockThisTurn).
            // Read directly so the sim knows whether the next attack still triggers the
            // block gain — the bool persists across the snapshot so it can't be inferred.
            SkittishFiredThisTurn = powerDict != null && powerDict.ContainsKey("SkittishPower")
                && CombatReflection.GetPowerInternalCounter(enemy, "SkittishPower", "hasGainedBlockThisTurn") == 1,
            SlowDamagePct = (powerDict != null && powerDict.ContainsKey("SlowPower"))
                ? System.Math.Max(0, CombatReflection.GetPowerDisplayAmount(enemy, "SlowPower"))
                : 0,
            // Encoder-parity (2026-05-27): SimEnemy now carries MaxHp so
            // SimStateAdapter can emit it instead of duplicating Hp.
            MaxHp = (int)(CombatReflection.CreatureMaxHpField?.GetValue(enemy) ?? hp),
            IntentDamage = totalDmg,
            IntentRepeats = 1, // already aggregated
            SourceRef = enemy,
            HasAttackIntent = hasAtk,
            HasDeathBlowIntent = hasDeathBlow,
            HasBuffIntent = hasBuff,
            HasDebuffIntent = hasDebuff,
            HasHealIntent = hasHeal,
            HasSummonIntent = hasSummon,
            HasDefendIntent = hasDefend,
            HasStatusIntent = hasStatus,
            IsInert = isInert,
            IsHidden = isHidden,
            IsUnknown = isUnknown,
            Threat = threat,
            IsBoss = isBoss,
            IsElite = isElite,
            IsMinion = isMinion,
            MonsterKey = enemy.Monster?.GetType().Name ?? "",
            VulnerableAmount = vuln,
            WeakAmount = weak,
            StrengthAmount = eStr,
            ArtifactAmount = eArtifact,
            FrailAmount = eFrail,
            HasTurnStartStrengthBuff = hasRitual,
            PoisonAmount = ePoison,
            ConstrictAmount = eConstrict,
            BurnAmount = eBurn,
            DamageCapPerHit = damageCap,
            SlipperyStacks = slipperyStacks,
            ThornsAmount = thorns,
            Powers = powerDict,
            HardenedShellRemaining = hardenedShellRemaining,
            SkittishAmount = skittish,
            SkittishAlreadyTriggered = skittishUsed,
            CurlUpAmount = curlUp,
            SelfFormingClayAmount = selfFormingClay,
            ImbalancedAmount = imbalanced,
            TurnStartStrengthGain = turnStartStrGain,
            PaperCutsAmount = paperCuts,
            PainfulStabsAmount = painfulStabs,
            HealAmount = healAmount,
            SandpitAmount = sandpit,
            OnDeathSpawnsCount = onDeathSpawns,
        };
    }

    /// <summary>
    /// 2026-06-04 — Decompiled per-turn self-heal for monsters whose heal amount is hardcoded in
    /// their move (not on the HealIntent). Single-player values; STS2 scales some by player count
    /// but the headless planner is single-player. Returns 0 for unknown monsters so the heal
    /// projection is a no-op unless we've verified the value. Add new healers here as found.
    ///   • WaterfallGiant.SiphonMove → CreatureCmd.Heal(SiphonHeal=15 × players)  [Act 1 boss]
    /// </summary>
    private static int KnownHealAmount(string? monsterClassName) => monsterClassName switch
    {
        "WaterfallGiant" => 15,
        _ => 0,
    };

    private static void AccumulateAttackDmg(object intent, ref int totalDmg)
    {
        int d = CombatReflection.GetAttackIntentDamage(intent);
        int r = CombatReflection.GetAttackIntentRepeats(intent);
        if (r <= 0) r = 1;
        totalDmg += d * r;
    }

    private static ThreatLevel ComputeThreat(
        bool hasAtk, int dmg, bool hasBuff, bool hasDeathBlow,
        bool hasHeal, bool hasSummon, bool hasDebuff, bool hasDefend, bool hasStatus,
        bool isInert, bool isHidden, bool isUnknown, int playerHp)
    {
        if (isInert) return ThreatLevel.None;
        if (hasBuff || hasDeathBlow) return ThreatLevel.Critical;
        if (hasHeal || hasSummon) return ThreatLevel.High;
        if (hasAtk && playerHp > 0 && dmg > playerHp * 0.3) return ThreatLevel.High;
        if (hasAtk || hasDebuff) return ThreatLevel.Medium;
        if (hasDefend || hasStatus || isHidden || isUnknown) return ThreatLevel.Low;
        return ThreatLevel.None;
    }

    public static string FormatForLog(SimState s)
    {
        var hand = string.Join(",", s.Hand.Select(FormatCard));
        var enemies = string.Join(",",
            s.Enemies.Select(e => {
                var powerTag = "";
                if (e.Powers != null && e.Powers.Count > 0)
                {
                    var ps = e.Powers
                        .Where(kv => kv.Value > 0)
                        .Select(kv => $"{kv.Key.Replace("Power", "")}:{kv.Value}")
                        .ToList();
                    if (ps.Count > 0) powerTag = $" pow=[{string.Join(",", ps)}]";
                }
                return $"{e.SourceRef?.GetType().Name ?? "Enemy"}(hp={e.Hp}/b{e.Block} {e.IntentSummary} threat={e.Threat}){powerTag}";
            }));
        string orbTag = "";
        if (s.OrbQueue.Count > 0)
        {
            var slots = new List<string>();
            for (int i = 0; i < s.OrbQueue.Count; i++)
            {
                var k = s.OrbQueue[i];
                var ev = i < s.OrbEvokeValues.Count ? s.OrbEvokeValues[i] : 0;
                slots.Add(k == OrbKind.Dark && ev > 0 ? $"{k.ShortTag()}{ev}" : k.ShortTag());
            }
            orbTag = $" orbs=[{string.Join(",", slots)}/{s.PlayerOrbCapacity}]";
        }
        // v0.5 — surface player status powers when they're relevant. Common case
        // (no debuffs / no Intangible / no free counters) prints nothing.
        var statusBits = new List<string>();
        if (s.PlayerStrength != 0)   statusBits.Add($"Str:{s.PlayerStrength}");
        if (s.PlayerDexterity != 0)  statusBits.Add($"Dex:{s.PlayerDexterity}");
        if (s.PlayerFocus != 0)      statusBits.Add($"Fcs:{s.PlayerFocus}");
        if (s.PlayerVulnerable > 0)  statusBits.Add($"Vuln:{s.PlayerVulnerable}");
        if (s.PlayerWeak > 0)        statusBits.Add($"Weak:{s.PlayerWeak}");
        if (s.PlayerFrail > 0)       statusBits.Add($"Frail:{s.PlayerFrail}");
        if (s.PlayerIntangible > 0)  statusBits.Add($"Intang:{s.PlayerIntangible}");
        if (s.PlayerFreeAttacks > 0) statusBits.Add($"FreeA:{s.PlayerFreeAttacks}");
        if (s.PlayerFreeSkills > 0)  statusBits.Add($"FreeS:{s.PlayerFreeSkills}");
        if (s.PlayerFreePowers > 0)  statusBits.Add($"FreeP:{s.PlayerFreePowers}");
        // v0.6.7 — mechanic stacks (silent when empty)
        if (s.SoulInPiles > 0)       statusBits.Add($"Soul:{s.SoulInPiles}");
        if (s.ShivInPiles > 0)       statusBits.Add($"Shiv:{s.ShivInPiles}");
        if (s.SkeletonCount > 0)     statusBits.Add($"Osty:{s.SkeletonCount}");
        if (s.ExhaustPileSize > 0)   statusBits.Add($"Exh:{s.ExhaustPileSize}");
        if (s.SovereignBladeCount > 0) statusBits.Add($"Blade:{s.SovereignBladeCount}");
        // v0.6.8 — turn / combat counters (silent when 0)
        if (s.TurnAttacksPlayed > 0)   statusBits.Add($"AtkT:{s.TurnAttacksPlayed}");
        if (s.TurnSkillsPlayed > 0)    statusBits.Add($"SklT:{s.TurnSkillsPlayed}");
        if (s.CombatPlayerHpLossEvents > 0) statusBits.Add($"HpLost:{s.CombatPlayerHpLossEvents}");
        var statusTag = statusBits.Count > 0 ? $" status=[{string.Join(",", statusBits)}]" : "";
        // v0.10 — Relics dump (silent when empty). PenNib:9 = 9/10 attacks toward
        // the next ×2 trigger. Most passive relics show value 1.
        var relicTag = "";
        if (s.PlayerRelics.Count > 0)
        {
            var rs = s.PlayerRelics
                .Select(kv => kv.Value > 1 ? $"{kv.Key}:{kv.Value}" : kv.Key)
                .ToList();
            relicTag = $" relics=[{string.Join(",", rs)}]";
        }
        return $"player[hp={s.PlayerHp} block={s.PlayerBlock} energy={s.PlayerEnergy}]{orbTag}{statusTag}{relicTag} hand=[{hand}] enemies=[{enemies}]";
    }

    private static string FormatCard(SimCard c)
    {
        var detail = c.IsAttack && c.TotalDamage > 0
            ? $"d{c.TotalDamage}" + (c.Hits > 1 ? $"x{c.Hits}" : "")
            : c.Block > 0 ? $"b{c.Block}"
            : c.PowerApps.Count > 0
                ? string.Join("+", c.PowerApps.Select(kv => $"{ShortPowerName(kv.Key)}:{kv.Value}"))
                : "";
        var prefix = c.Kind.ToString()[0];
        // ★[encId] marker — shows which enchant is on the card so we can verify
        // PlayCountMultiplier / numeric-bonus catalog wiring at a glance.
        string ench = "";
        if (c.SourceRef != null)
        {
            var enchId = Reflection.CardReflection.GetEnchantmentId(c.SourceRef);
            if (!string.IsNullOrEmpty(enchId))
            {
                // Id is typically "MegaCrit.Sts2.Core.Models.Enchantments.Glam" — keep tail only.
                var dot = enchId.LastIndexOf('.');
                var shortId = dot >= 0 ? enchId.Substring(dot + 1) : enchId;
                ench = $"★[{shortId}]";
            }
        }
        // v0.5 — keyword flags relevant to play-order decisions. R=retain (defers
        // until other plays exhausted), E=ethereal (must play this turn or exhaust),
        // I=innate (opener marker), Xh=exhaust-on-play. X (no h) = currently unplayable.
        // Suffix only when at least one is set.
        var flags = (c.IsRetain ? "R" : "")
                  + (c.IsEthereal ? "E" : "")
                  + (c.IsInnate ? "I" : "")
                  + (c.IsExhaust ? "Xh" : "")
                  + (c.IsPlayable ? "" : "X");
        var flagsTag = flags.Length > 0 ? $"|{flags}" : "";
        return string.IsNullOrEmpty(detail)
            ? $"{c.Id}{ench}({prefix}{c.Cost}{flagsTag})"
            : $"{c.Id}{ench}({prefix}{c.Cost}/{detail}{flagsTag})";
    }

    private static string ShortPowerName(string fullName)
    {
        // "StrengthPower" → "Str", "VulnerablePower" → "Vuln" — keep log compact
        var idx = fullName.LastIndexOf("Power", System.StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return fullName;
        var stem = fullName.Substring(0, idx);
        return stem.Length <= 4 ? stem : stem.Substring(0, 4);
    }
}
