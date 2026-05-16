using System.Collections.Generic;
using System.Linq;
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
            int playerStars = (int)(CombatReflection.PcsStarsField?.GetValue(pcs) ?? 0);

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
            int drawPileSize = drawPileRaw?.Count ?? 0;
            int discardPileSize = discardPileRaw?.Count ?? 0;

            var hand = new List<SimCard>();
            var handPile = PileType.Hand.GetPile(player);
            if (handPile != null)
            {
                foreach (var card in handPile.Cards)
                    hand.Add(BuildSimCard(card, requirePlayability: true));
            }

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
                PlayerStrength = playerStr,
                PlayerDexterity = playerDex,
                PlayerVulnerable = playerVuln,
                PlayerWeak = playerWeak,
                PlayerFrail = playerFrail,
                DrawPileSize = drawPileSize,
                DiscardPileSize = discardPileSize,
                DrawPile = drawPile,
                DiscardPile = discardPile,
                PlayerStars = playerStars,
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
        };
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
