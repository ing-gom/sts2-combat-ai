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

            int playerStr = CombatReflection.GetPowerAmount(creature, "StrengthPower");
            int playerDex = CombatReflection.GetPowerAmount(creature, "DexterityPower");
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
            // v0.7.94 — Reactive Strength + Skill-cost reduction.
            int playerEnrage = CombatReflection.GetPowerAmount(creature, "EnragePower");
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
            int playerVuln = CombatReflection.GetPowerAmount(creature, "VulnerablePower");
            int playerWeak = CombatReflection.GetPowerAmount(creature, "WeakPower");
            int playerFrail = CombatReflection.GetPowerAmount(creature, "FrailPower");
            int playerFocus = CombatReflection.GetPowerAmount(creature, "FocusPower");
            int playerIntangible = CombatReflection.GetPowerAmount(creature, "IntangiblePower");
            int playerMetallicize = CombatReflection.GetPowerAmount(creature, "MetallicizePower");
            int playerPlatedArmor = CombatReflection.GetPowerAmount(creature, "PlatedArmorPower");
            int playerEotBlockBonus = playerMetallicize + playerPlatedArmor;
            int playerFreeAttacks = CombatReflection.GetPowerAmount(creature, "FreeAttackPower");
            int playerFreeSkills = CombatReflection.GetPowerAmount(creature, "FreeSkillPower");
            int playerFreePowers = CombatReflection.GetPowerAmount(creature, "FreePowerPower");
            // v0.7.12 — capture all player powers as a single dict so
            // AdvanceTurn can apply persistent passives (DemonForm, Regen,
            // Barricade, EchoForm etc.) without one field per power.
            var playerPowerDict = CombatReflection.GetAllPowers(creature);
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
                            catch { }
                        }
                        orbEvokeValues.Add(evokeVal);
                    }
                }
            }
            catch { }

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
            int drawPileSize = drawPileRaw?.Count ?? 0;
            int discardPileSize = discardPileRaw?.Count ?? 0;
            int exhaustPileSize = exhaustPileRaw?.Count ?? 0;

            var hand = new List<SimCard>();
            var handPile = PileType.Hand.GetPile(player);
            if (handPile != null)
            {
                foreach (var card in handPile.Cards)
                    hand.Add(BuildSimCard(card, requirePlayability: true));
            }

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

            // Skeleton (Osty) ally count — alive monsters of class Osty owned by player.
            int skeletonCount = 0;
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

                    allies.Add(new SimAlly
                    {
                        Hp = allyHp,
                        Block = allyBlock,
                        IntentDamage = allyIntentDmg,
                        IntentRepeats = System.Math.Max(1, allyIntentRepeats),
                        HasAttackIntent = allyHasAttack,
                        ClassName = cls,
                        SourceRef = ally,
                    });
                }
            }
            catch { }

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

            int turnAttacksPlayed = 0, turnSkillsPlayed = 0, combatHpLossEvents = 0;
            try
            {
                var history = CombatManager.Instance.History;
                if (history != null)
                {
                    foreach (var entry in history.Entries)
                    {
                        if (entry is CardPlayFinishedEntry cpe)
                        {
                            if (cpe.RoundNumber != cs.RoundNumber) continue;
                            if (cpe.CurrentSide != cs.CurrentSide) continue;
                            // Owner check — only count this player's plays in multiplayer.
                            if (cpe.CardPlay?.Card?.Owner != player) continue;
                            var type = cpe.CardPlay.Card.Type;
                            if (type == CardType.Attack) turnAttacksPlayed++;
                            else if (type == CardType.Skill) turnSkillsPlayed++;
                        }
                        else if (entry is DamageReceivedEntry dre)
                        {
                            if (dre.Receiver != creature) continue;
                            if (dre.Result.UnblockedDamage > 0) combatHpLossEvents++;
                        }
                    }
                }
            }
            catch { /* counters stay 0 — defensive */ }

            // v0.5.1 — Pile cards skip the CanPlay() check (irrelevant outside hand)
            // but reuse the same builder so Effect / Cost / Kind are consistent with
            // hand cards. The simulator averages over these for draw EV modeling.
            var drawPile = new List<SimCard>();
            if (drawPileRaw != null)
                foreach (var card in drawPileRaw) drawPile.Add(BuildSimCard(card, requirePlayability: false));
            var discardPile = new List<SimCard>();
            if (discardPileRaw != null)
                foreach (var card in discardPileRaw) discardPile.Add(BuildSimCard(card, requirePlayability: false));

            return new SimState
            {
                PlayerHp = hp,
                PlayerBlock = block,
                PlayerEnergy = energy,
                Enemies = enemies,
                Hand = hand,
                CharacterId = characterId,
                PlayerStrength = playerStr,
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
                PlayerEnrage = playerEnrage,
                PlayerCorruption = playerCorruption,
                PlayerBurst = playerBurst,
                PlayerThorns = playerThorns,
                PlayerFeelNoPain = playerFeelNoPain,
                // v0.7.98 — remaining echoes this turn.
                PlayerEchoForm = System.Math.Max(0, echoStack - (turnAttacksPlayed + turnSkillsPlayed)),
                PlayerJuggernaut = playerJuggernaut,
                PlayerHunger = playerHunger,
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
                PlayerDoom = playerDoom,
                PlayerOrbCount = orbCount,
                PlayerOrbCapacity = orbCapacity,
                OrbQueue = orbQueue,
                OrbEvokeValues = orbEvokeValues,
                PlayerFocus = playerFocus,
                PlayerIntangible = playerIntangible,
                PlayerEndOfTurnBlockBonus = playerEotBlockBonus,
                PlayerFreeAttacks = playerFreeAttacks,
                PlayerFreeSkills = playerFreeSkills,
                PlayerFreePowers = playerFreePowers,
                PlayerPowers = playerPowerDict,
                SoulInPiles = soulInPiles,
                ShivInPiles = shivInPiles,
                SkeletonCount = skeletonCount,
                Allies = allies,
                ExhaustPileSize = exhaustPileSize,
                SovereignBladeCount = sovereignBladeCount,
                TurnAttacksPlayed = turnAttacksPlayed,
                TurnSkillsPlayed = turnSkillsPlayed,
                CombatPlayerHpLossEvents = combatHpLossEvents,
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
        var orbMeta = Reflection.OrbCardCatalog.Lookup(id, costSpent, axes);
        var effect = baseEffect with {
            EvokeCount = orbMeta.EvokeCount,
            ChannelCount = orbMeta.ChannelCount,
            ChannelKind = orbMeta.ChannelKind,
        };
        // v0.6.7 — Sly detection. Raw `CUNNING` axis (no _PRODUCER/_CONSUMER
        // suffix) on a Silent card aligns 1:1 with CardKeyword.Sly in the
        // current catalog. The runtime keyword check would be more authoritative
        // (covers GiveSingleTurnSly temp-Sly cards) but card.Keywords isn't a
        // public stable property — stick with the axis proxy until we need
        // temp-Sly precision.
        bool isSly = false;
        for (int i = 0; i < axes.Count; i++)
        {
            if (axes[i] == "CUNNING") { isSly = true; break; }
        }

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
            IsExhaust = catalogInfo?.Exhaust ?? false,
            IsFetchTrigger = catalogInfo?.FetchTrigger ?? false,
            IsSly = isSly,
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
        if (powerDict.TryGetValue("HardToKillPower", out var hard) && hard > 0)
            damageCap = damageCap == 0 ? hard : System.Math.Min(damageCap, hard);
        int thorns = powerDict.TryGetValue("ThornsPower", out var t) ? t : 0;

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
        bool hasRitual = CombatReflection.GetPowerAmount(enemy, "RitualPower") > 0
                      || CombatReflection.GetPowerAmount(enemy, "EnragePower") > 0
                      || CombatReflection.GetPowerAmount(enemy, "FeralPower") > 0;

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

        return new SimEnemy
        {
            Hp = hp,
            Block = block,
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
            ThornsAmount = thorns,
            Powers = powerDict,
            HardenedShellRemaining = hardenedShellRemaining,
        };
    }

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
        return $"player[hp={s.PlayerHp} block={s.PlayerBlock} energy={s.PlayerEnergy}]{orbTag}{statusTag} hand=[{hand}] enemies=[{enemies}]";
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
