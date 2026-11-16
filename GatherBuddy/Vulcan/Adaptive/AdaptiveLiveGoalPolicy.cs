using System;

namespace GatherBuddy.Vulcan.Adaptive;

public enum AdaptiveLivePolicyMode { ProgressOnly, Standard, Raphael, Adaptive }

/// <summary>Detached active policy, not the shadow diagnostic full-quality target.</summary>
public sealed record AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode Mode, int HQChance, int CollectabilityTier)
{
    public AdaptiveCraftGoal Resolve(AdaptiveCraftSnapshot craft)
    {
        if (!Enum.IsDefined(Mode) || HQChance is < 0 or > 100 || CollectabilityTier is < 1 or > 3)
            throw new ArgumentException("Unsupported active quality policy.");
        if (Mode == AdaptiveLivePolicyMode.ProgressOnly)
            return AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        if (craft.Collectable)
        {
            if (craft.Collectability1 <= 0 || craft.Collectability3 <= 0)
                throw new NotSupportedException("Collectability reward thresholds are missing; synthesis blocked.");
            return AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability,
                Mode is AdaptiveLivePolicyMode.Standard or AdaptiveLivePolicyMode.Adaptive ? CollectabilityTier : 3);
        }
        if (craft.RequiredQuality > 0)
            return AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, craft.RequiredQuality);
        if (!craft.CanHQ) return AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        return Mode == AdaptiveLivePolicyMode.Standard
            ? AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.HQChance, HQChance)
            : AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, craft.MaxQuality);
    }
}
