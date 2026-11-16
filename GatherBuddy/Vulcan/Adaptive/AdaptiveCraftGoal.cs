using System;

namespace GatherBuddy.Vulcan.Adaptive;

public enum AdaptiveGoalKind { ProgressOnly, HQChance, Collectability, Quality }

/// <summary>Desired quality may be missed; mandatory quality never counts as successful below its floor.</summary>
public sealed record AdaptiveCraftGoal
{
    public AdaptiveGoalKind Kind { get; }
    public int MandatoryQuality { get; }
    public int DesiredQuality { get; }
    public bool StrictTarget { get; }

    private AdaptiveCraftGoal(AdaptiveGoalKind kind, int mandatory, int desired, bool strict)
        => (Kind, MandatoryQuality, DesiredQuality, StrictTarget) = (kind, mandatory, desired, strict);

    public static AdaptiveCraftGoal Resolve(AdaptiveCraftSnapshot craft, AdaptiveGoalKind kind,
        int target = 100, bool strictTarget = false)
    {
        craft.Validate();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var desired = kind switch
        {
            AdaptiveGoalKind.ProgressOnly => 0,
            AdaptiveGoalKind.HQChance when craft.CanHQ && !craft.Collectable => QualityForHQ(craft.MaxQuality, target),
            AdaptiveGoalKind.Collectability when craft.Collectable => target switch
            {
                1 => craft.Collectability1, 2 => craft.Collectability2, 3 => craft.Collectability3,
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            },
            AdaptiveGoalKind.Quality when target >= 0 && target <= craft.MaxQuality => target,
            _ => throw new ArgumentException("Goal does not match recipe capabilities."),
        };
        var mandatory = kind == AdaptiveGoalKind.Collectability ? Math.Max(craft.RequiredQuality, craft.Collectability1) : craft.RequiredQuality;
        return new(kind, mandatory, Math.Max(mandatory, desired), strictTarget);
    }

    private static int QualityForHQ(int maximum, int chance)
    {
        if (chance is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(chance));
        // Binary search the actual HQ curve, not a raw quality percentage.
        var low = 0;
        var high = maximum;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (Calculations.GetHQChance(maximum == 0 ? 100 : mid * 100.0 / maximum) >= chance) high = mid;
            else low = mid + 1;
        }
        return low;
    }
}
