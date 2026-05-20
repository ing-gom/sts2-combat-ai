namespace Sts2CombatAI.Sim;

internal static class TokenProducerAxes
{
    /// <summary>
    /// Runtime axis self-healing for token producers. The master catalog
    /// hand-tags _PRODUCER axes from description heuristics; side-effect
    /// generators sometimes slip through (LEADING_STRIKE / CLOAK_AND_DAGGER
    /// historically missed SHIV_PRODUCER). The live CardModel exposes the
    /// token-count DynamicVars directly (Shivs / Skeletons / Souls / Forge),
    /// and CardReflection surfaces them on <see cref="CardEffectSummary"/>.
    /// When a count is positive but the matching _PRODUCER axis is absent,
    /// add it (plus CARD_GEN) so the planner's ApplyCardGen / Shiv-stem
    /// projections / Arsenal-Pillar trigger preview all fire correctly.
    ///
    /// Returns the original list unchanged when no augmentation is needed
    /// (avoids allocating).
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> AugmentTokenProducerAxes(
        System.Collections.Generic.IReadOnlyList<string> axes,
        CardEffectSummary effect)
    {
        // Quick-exit when no token vars present (the common case).
        if (effect.ShivGen <= 0 && effect.SkeletonGen <= 0
            && effect.SoulGen <= 0 && effect.ForgeGen <= 0)
            return axes;

        System.Collections.Generic.HashSet<string>? have = null;
        System.Collections.Generic.List<string>? extra = null;

        void EnsureHave()
        {
            if (have != null) return;
            have = new System.Collections.Generic.HashSet<string>(axes,
                System.StringComparer.OrdinalIgnoreCase);
        }
        void Add(string axis)
        {
            EnsureHave();
            if (have!.Contains(axis)) return;
            extra ??= new System.Collections.Generic.List<string>(2);
            extra.Add(axis);
            have.Add(axis);
        }

        if (effect.ShivGen > 0)     { Add("SHIV_PRODUCER"); }
        if (effect.SkeletonGen > 0) { Add("SKELETON_PRODUCER"); }
        if (effect.SoulGen > 0)     { Add("SOUL_PRODUCER"); }
        if (effect.ForgeGen > 0)    { Add("FORGE_PRODUCER"); }
        if (extra != null) Add("CARD_GEN");

        if (extra == null) return axes;
        var merged = new string[axes.Count + extra.Count];
        for (int i = 0; i < axes.Count; i++) merged[i] = axes[i];
        for (int i = 0; i < extra.Count; i++) merged[axes.Count + i] = extra[i];
        return merged;
    }
}
