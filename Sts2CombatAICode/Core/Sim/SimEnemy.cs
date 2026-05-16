using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Sts2CombatAI.Sim;

/// <summary>
/// Lightweight projection of an enemy Creature for planner scoring.
/// v0.1.1 expands intent visibility: snapshotter classifies every intent on this enemy
/// and exposes per-category flags + an overall ThreatLevel. PlanScorer uses these to
/// prioritize kills (buff/heal/summon enemies first, defend/inert enemies last).
/// </summary>
internal sealed record SimEnemy
{
    public required int Hp { get; init; }
    public required int Block { get; init; }
    public required int IntentDamage { get; init; }
    public required int IntentRepeats { get; init; }
    // Nullable for testability — live snapshotter always provides a real Creature.
    public Creature? SourceRef { get; init; }

    // Intent-category flags (v0.1.1). Default false unless snapshotter sets it.
    public bool HasAttackIntent { get; init; }
    public bool HasDeathBlowIntent { get; init; }
    public bool HasBuffIntent { get; init; }
    public bool HasDebuffIntent { get; init; }
    public bool HasHealIntent { get; init; }
    public bool HasSummonIntent { get; init; }
    public bool HasDefendIntent { get; init; }
    public bool HasStatusIntent { get; init; }
    public bool IsInert { get; init; }     // stun / sleep / escape
    public bool IsHidden { get; init; }
    public bool IsUnknown { get; init; }

    public ThreatLevel Threat { get; init; } = ThreatLevel.None;

    // v0.2.4 — enemy status powers (affects our attack damage when we hit them).
    public int VulnerableAmount { get; init; }   // turns; >0 = takes +50% damage from us
    public int WeakAmount { get; init; }         // turns; >0 = their attacks -25%
    public int StrengthAmount { get; init; }     // adds to their attack damage

    // v0.2.9 — enemy resistance / defensive powers.
    public int ArtifactAmount { get; init; }     // blocks N next debuffs we apply
    public int FrailAmount { get; init; }        // their block gain ×0.75

    /// <summary>
    /// True when the enemy has a per-turn Strength gain (RitualPower style). These
    /// enemies snowball — every turn alive makes them harder to kill, so the planner
    /// should heavily prioritize killing them ASAP.
    /// </summary>
    public bool HasTurnStartStrengthBuff { get; init; }

    // v0.2.11 — DoT / character-specific debuff stacks.
    public int PoisonAmount { get; init; }    // turn-based damage applied to enemy
    public int ConstrictAmount { get; init; }
    public int BurnAmount { get; init; }

    // v0.4 — damage / counter-attack powers that affect *our* attack decisions.
    /// <summary>
    /// Per-hit damage cap from IntangiblePower (1, or 5 with TheBoot) or HardToKillPower
    /// (Amount). 0 means no cap — uncapped. When >0, every hit we deal is clamped to this
    /// value, so multi-hit cards are *much* more valuable than burst single hits.
    /// </summary>
    public int DamageCapPerHit { get; init; }

    /// <summary>
    /// ThornsPower amount — every time we attack this enemy, we take this much
    /// HP loss in return. Multi-hit cards trigger once per hit.
    /// </summary>
    public int ThornsAmount { get; init; }

    /// <summary>
    /// Generic dump of every active power on this enemy (class-name → amount).
    /// Used as the fallback lookup when a specific named field isn't carved out above.
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, int> Powers { get; init; }
        = new System.Collections.Generic.Dictionary<string, int>();

    /// <summary>
    /// v0.4 — HardenedShellPower's *remaining* per-turn damage budget (reads the live
    /// DisplayAmount = Amount − damageReceivedThisTurn). 0 means no shell or fully spent.
    /// When &gt; 0, every hit we deal is capped to this value, then the value drops by the
    /// applied damage. Effectively makes attacks worthless once the shell budget is gone.
    /// </summary>
    public int HardenedShellRemaining { get; init; }

    // v0.1.2 — encounter-role classification (set by Snapshotter)
    public bool IsBoss { get; init; }      // boss-room top creature (highest HP in boss/elite encounter)
    public bool IsElite { get; init; }     // any creature in an elite encounter
    public bool IsMinion { get; init; }    // spawned this turn OR significantly weaker than max HP in fight

    public bool IsAlive => Hp > 0;
    public int TotalIntentDamage => IntentDamage * IntentRepeats;
    public int EffectiveHp => Hp + Block; // damage required to kill outright

    /// <summary>
    /// Compact log-friendly description of this enemy's intent state.
    /// </summary>
    public string IntentSummary
    {
        get
        {
            var tags = new System.Collections.Generic.List<string>();
            if (HasDeathBlowIntent) tags.Add("DeathBlow");
            if (HasAttackIntent) tags.Add($"Atk{TotalIntentDamage}");
            if (HasBuffIntent) tags.Add("Buff");
            if (HasHealIntent) tags.Add("Heal");
            if (HasSummonIntent) tags.Add("Summon");
            if (HasDebuffIntent) tags.Add("Debuff");
            if (HasDefendIntent) tags.Add("Defend");
            if (HasStatusIntent) tags.Add("Status");
            if (IsInert) tags.Add("Inert");
            if (IsHidden) tags.Add("Hidden");
            if (IsUnknown) tags.Add("Unknown");
            if (IsBoss) tags.Add("BOSS");
            else if (IsElite) tags.Add("ELITE");
            if (IsMinion) tags.Add("minion");
            return tags.Count == 0 ? "?" : string.Join("+", tags);
        }
    }
}
