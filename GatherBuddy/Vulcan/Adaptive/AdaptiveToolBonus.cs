using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

public sealed record AdaptiveToolEvidence(uint MainHandItemId, uint JobId, int GoodQualityPercent,
    string DetectionSource, string EffectDescription);

/// <summary>
/// Strict English Item effect-text classifier, not tool-name matching. ItemSpecialBonus is zero on
/// audited enhanced tools. Unknown text/other effect channels must not silently become ordinary.
/// Source/templates: docs/adaptive-tool-bonus.md. No runtime network dependency.
/// </summary>
public static class AdaptiveToolBonus
{
    public const string EnhancedEffect = "Increases to quality are 1.75 times higher than normal when material condition is Good.";
    private const string MissionReward = "Completing a stellar mission from start to finish with this tool equipped grants a bonus to earned rewards.";
    private static readonly HashSet<string> EnhancedDescriptions = BuildDescriptions();

    public static bool TryResolve(uint itemId, uint playerJob, uint itemJob, bool mainHandOnly,
        uint specialBonus, int specialBonusParam, uint itemAction, string? englishDescription,
        out AdaptiveToolEvidence? evidence, out string reason)
    {
        evidence = null;
        reason = "Missing, unsupported, or ambiguous equipped crafting-tool metadata.";
        if (itemId == 0 || playerJob is < 8 or > 15 || itemJob != playerJob || !mainHandOnly
            || specialBonus != 0 || specialBonusParam != 0 || itemAction != 0 || englishDescription == null) return false;
        // Only newline convention is normalized. No fuzzy/localized/substring classification.
        var description = englishDescription.Replace("\r\n", "\n", StringComparison.Ordinal);
        var percent = description.Length == 0 ? 150 : EnhancedDescriptions.Contains(description) ? 175 : 0;
        if (percent == 0)
        {
            reason = "Equipped tool has unaudited English Item.Description; refusing to guess Good-quality effect.";
            return false;
        }
        evidence = new(itemId, playerJob, percent,
            percent == 175 ? "English Item.Description: exact audited 1.75x effect template; other effect channels empty"
                : "English Item.Description empty; ItemSpecialBonus/Param and ItemAction empty", description);
        reason = $"Equipped main-hand item {itemId}: Good quality {percent}%.";
        return true;
    }

    private static HashSet<string> BuildDescriptions()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in new[] { "Adaptive", "Brilliant", "Perfected", "Customized", "Inspirational" })
            result.Add($"{stage} splendorous tool.\n\n{EnhancedEffect}");
        var credits = new[]
        {
            "Lunar Credit Bonus: +35%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +10%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +15%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +20%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +25%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +35%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +35%\nOizys Credit Bonus: +10%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +35%\nOizys Credit Bonus: +20%",
            "Lunar Credit Bonus: +35%\nPhaenna Credit Bonus: +35%\nOizys Credit Bonus: +35%",
            "Auxesia Credit Bonus: +10%\nLunar/Phaenna/Oizys Credit Bonus: +35%",
            "Auxesia Credit Bonus: +20%\nLunar/Phaenna/Oizys Credit Bonus: +35%",
        };
        for (var i = 0; i < credits.Length; ++i)
            result.Add($"Phase {i + 9} cosmic tool.\n{EnhancedEffect}\n{MissionReward}\n{credits[i]}");
        result.Add($"Perfected cosmic tool.\n{EnhancedEffect}\n{MissionReward}\nLunar/Phaenna/Oizys/Auxesia Credit Bonus: +35%");
        return result;
    }
}
