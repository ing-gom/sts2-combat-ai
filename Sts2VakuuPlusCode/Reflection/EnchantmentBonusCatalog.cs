using System.Collections.Generic;
using Sts2VakuuPlus.Sim;

namespace Sts2VakuuPlus.Reflection;

/// <summary>
/// Hand-coded bonus catalog for enchantments whose effect is hardcoded against
/// <c>EnchantmentModel.Amount</c> rather than exposed through DynamicVars.
///
/// The card's PreviewValue already folds in damage/block-modifier enchants
/// (Sharp/Sown's damage isn't this kind — Vigorous/Momentum/Inky/Corrupted/Instinct
/// all hook EnchantDamageAdditive/Multiplicative which the engine applies during
/// PreviewValue calculation). What's NOT exposed:
///
///   • Sown   → OnPlay: GainEnergy(Amount), once per combat
///   • Swift  → OnPlay: Draw(Amount), once per combat
///   • Adroit → OnPlay: GainBlock(Amount)   (BlockVar canonical = 0, Amount is the real value)
///
/// Other enchantments mutate keywords / shuffle order / play count, which can't
/// be folded into a single CardEffectSummary delta and are handled at score time
/// (see <see cref="EnchantmentScoreBonus"/>).
/// </summary>
internal static class EnchantmentBonusCatalog
{
    /// <summary>
    /// Apply numeric effect deltas from the enchantment Id + amount to the
    /// in-flight effect summary fields.
    /// </summary>
    public static void ApplyEffect(
        string enchantId, int amount,
        ref int damage, ref int block, ref int energyGain, ref int drawCount)
    {
        if (amount <= 0) return;

        // EnchantmentModel ids are dotted, last segment is the class name.
        // Match permissively: contains-check on lowercased id.
        var key = enchantId.ToLowerInvariant();

        if (key.Contains("sown"))   energyGain += amount;
        else if (key.Contains("swift"))  drawCount  += amount;
        else if (key.Contains("adroit")) block      += amount;
    }

    /// <summary>
    /// Multiplicative play-count factor — used at score time to weight enchanted
    /// cards that effectively play multiple times (Glam, Spiral).
    /// Returns 1 when no multiplier applies.
    /// </summary>
    public static int PlayCountMultiplier(string? enchantId, int amount)
    {
        if (string.IsNullOrEmpty(enchantId)) return 1;
        var key = enchantId.ToLowerInvariant();
        if (key.Contains("glam"))   return 1 + System.Math.Max(1, amount);
        if (key.Contains("spiral")) return 1 + System.Math.Max(1, amount);
        return 1;
    }

    /// <summary>
    /// Returns 0 — non-numeric enchants (Innate/Retain/Eternal/keyword-only) don't
    /// force play priority. Innate just means "starts in opening hand"; Retain just
    /// means "doesn't discard at end of turn." Both leave the play-now-vs-later
    /// decision to the normal scorer based on the card's actual effect.
    ///
    /// Kept as a method (not a constant) so the per-enchant decision is
    /// documented in one place if we ever want to revisit a specific case.
    /// </summary>
    public static int BehaviorBonus(string? enchantId) => 0;
}
