using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sts2CombatAI.Sim;

namespace Sts2CombatAI.Onnx;

/// Phase C — converts the mod's `SimState` (game-tick snapshot built by
/// StateSnapshotter) into the flat float32 observation vector the ONNX model
/// was trained on (matches sts2-combat-core's OnnxObservationEncoder layout).
///
/// Field order, sentinel values, and vocab mapping MUST match exactly. The
/// vocab is loaded from the embedded `Sts2CombatAI.vocab.json` (dumped by
/// `Sts2CombatCore.exe --dump-vocab`); if missing, falls back to a small
/// hand-curated list.
internal sealed class SimStateAdapter
{
    public int MaxHandSize { get; } = 10;
    public int MaxEnemies { get; } = 4;

    // 2026-05-27 B (obs expansion) — fixed-slot core player powers the
    // encoder appends after the pile-count block. Must match
    // env.py CORE_PLAYER_POWERS exactly (same names, same order) for
    // train/inference encoder parity. ML iteration sprint reject of
    // capacity/budget/specialist/BC variants pointed at info-bound, so
    // this surfaces the buff stacks PlanScorer's logic reads (Strength
    // for damage, FeelNoPain for Ironclad block, Vuln/Weak/Frail for
    // player-side defensive math).
    private static readonly string[] CorePlayerPowers = new[]
    {
        "StrengthPower",
        "VigorPower",
        "BufferPower",
        "FeelNoPainPower",
        "RagePower",
        "JuggernautPower",
        "VulnerablePower",
        "WeakPower",
        "FrailPower",
    };

    public int FeatureDim => 6 + 6 * MaxEnemies + 2 * MaxHandSize + 3 + CorePlayerPowers.Length;

    private readonly Dictionary<string, int> _cardToIdx;
    public int VocabSize { get; }

    public SimStateAdapter()
    {
        _cardToIdx = new Dictionary<string, int>
        {
            ["<empty>"] = 0,
            ["<unk>"] = 1,
        };
        // Load vocab from the embedded dump.
        var asm = typeof(SimStateAdapter).Assembly;
        using var stream = asm.GetManifestResourceStream("Sts2CombatAI.vocab.json");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            int braceIdx = text.IndexOf('{');
            if (braceIdx >= 0)
            {
                using var doc = JsonDocument.Parse(text.Substring(braceIdx));
                if (doc.RootElement.TryGetProperty("cards", out var cardsEl))
                {
                    int next = _cardToIdx.Count;
                    foreach (var card in cardsEl.EnumerateArray())
                    {
                        if (!card.TryGetProperty("id", out var idEl)) continue;
                        var id = idEl.GetString();
                        if (id != null && !_cardToIdx.ContainsKey(id))
                            _cardToIdx[id] = next++;
                    }
                }
            }
        }
        VocabSize = _cardToIdx.Count;
    }

    public float[] Encode(SimState state)
    {
        var v = new float[FeatureDim];
        int j = 0;
        // 2026-05-27 — all six player fields now sourced from SimState
        // proper, matching the layout `Sts2CombatEnv._encode` produces from
        // `Episode.BuildObservation`. Previously turn/maxHp/maxEnergy were
        // 0 or duplicated from current values, which silently broke ONNX
        // inference against any model trained on the Python encoder. See
        // docs/hybrid-boss-debug.md in sts2-combat-core for the full diff.
        v[j++] = state.Turn;
        v[j++] = 1f; // currentSideIsPlayer (Snapshotter only fires on player turn)
        v[j++] = state.PlayerHp;
        v[j++] = state.PlayerMaxHp;
        v[j++] = state.PlayerEnergy;
        v[j++] = state.PlayerMaxEnergy;

        for (int i = 0; i < MaxEnemies; i++)
        {
            if (i < state.Enemies.Count)
            {
                var e = state.Enemies[i];
                v[j++] = e.Hp;
                v[j++] = e.MaxHp;
                v[j++] = e.Block;
                v[j++] = e.Hp > 0 ? 1f : 0f;
                v[j++] = MapIntent(e);
                v[j++] = e.HasAttackIntent ? e.IntentDamage : 0;
            }
            else
            {
                j += 6;
            }
        }

        for (int i = 0; i < MaxHandSize; i++)
        {
            if (i < state.Hand.Count)
            {
                var c = state.Hand[i];
                v[j++] = c.Cost;
                v[j++] = _cardToIdx.TryGetValue(c.Id, out var idx) ? idx : 1;
            }
            else
            {
                v[j++] = -1f;
                v[j++] = 0f;
            }
        }

        v[j++] = state.DrawPile?.Count ?? 0;
        v[j++] = state.DiscardPile?.Count ?? 0;
        v[j++] = state.ExhaustPileCount;

        // 2026-05-27 B (obs expansion): tail block of core player power
        // stacks. Order must match env.py CORE_PLAYER_POWERS exactly.
        // SimState captures each of these via reflection at snapshot time
        // (Snapshotter sets PlayerStrength / PlayerVigor / PlayerBuffer /
        // PlayerFeelNoPain / PlayerRage / PlayerJuggernaut / PlayerVuln /
        // PlayerWeak / PlayerFrail), so this just reads the cached
        // fields — no extra reflection per encode call.
        v[j++] = state.PlayerStrength;
        v[j++] = state.PlayerVigor;
        v[j++] = state.PlayerBuffer;
        v[j++] = state.PlayerFeelNoPain;
        v[j++] = state.PlayerRage;
        v[j++] = state.PlayerJuggernaut;
        v[j++] = state.PlayerVulnerable;
        v[j++] = state.PlayerWeak;
        v[j++] = state.PlayerFrail;
        return v;
    }

    public bool[] BuildMask(SimState state, int actionCount)
    {
        // 2026-05-27 F (action-space expansion): supports both layouts —
        //   actionCount == MaxHandSize + 1  -> legacy Discrete(11), card-only
        //   actionCount == MaxHandSize * MaxEnemies + 1 -> new Discrete(41),
        //                                                  (card, target) flat
        // Picks the layout from the trained ONNX's output dim so the same
        // adapter works for both checkpoint families during rollout.
        var mask = new bool[actionCount];
        int expandedEndTurn = MaxHandSize * MaxEnemies;
        bool isExpanded = actionCount == expandedEndTurn + 1;

        if (!isExpanded)
        {
            // Legacy card-only mask.
            bool anyAlive = false;
            foreach (var e in state.Enemies) if (e.Hp > 0) { anyAlive = true; break; }
            for (int i = 0; i < MaxHandSize && i < actionCount; i++)
            {
                if (i >= state.Hand.Count) continue;
                var c = state.Hand[i];
                if (c.Cost > state.PlayerEnergy) continue;
                if (c.IsAttack && !anyAlive) continue;
                mask[i] = true;
            }
            if (MaxHandSize < actionCount) mask[MaxHandSize] = true;
            return mask;
        }

        // Expanded (card, target) mask. Matches env.py action_masks:
        //   - targeted card: only alive enemy slots are valid
        //   - non-targeted card: only target slot 0 is valid (mirrors the
        //     "don't waste policy capacity on 4 identical actions" rule)
        for (int i = 0; i < MaxHandSize; i++)
        {
            if (i >= state.Hand.Count) continue;
            var c = state.Hand[i];
            if (c.Cost > state.PlayerEnergy) continue;
            int baseIdx = i * MaxEnemies;
            if (c.IsAttack)
            {
                for (int j = 0; j < MaxEnemies && j < state.Enemies.Count; j++)
                {
                    if (state.Enemies[j].Hp > 0) mask[baseIdx + j] = true;
                }
            }
            else
            {
                mask[baseIdx + 0] = true;
            }
        }
        mask[expandedEndTurn] = true;
        return mask;
    }

    private static int MapIntent(SimEnemy e)
    {
        // 2026-05-27 B-train: mirror Episode.ResolveIntent priority and
        // Python _INTENT_TYPE_TO_INT dict exactly. Attack > Defend > Buff
        // > Debuff > Heal > Hidden; Summon falls through to 0 (Python
        // dict miss on the "Summon" key).
        if (e.HasAttackIntent) return 1;
        if (e.HasDefendIntent) return 2;
        if (e.HasBuffIntent) return 3;
        if (e.HasDebuffIntent) return 4;
        if (e.HasHealIntent) return 5;
        if (e.IsHidden) return 7;
        return 0;
    }
}
