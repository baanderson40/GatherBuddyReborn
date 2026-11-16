using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Owned scalar recipe data. No native services, mutable arrays, or settings references.</summary>
public sealed record AdaptiveCraftSnapshot
{
    public const int ModelVersion = 2;
    public int SimulationModelVersion => ModelVersion;
    public int GoodQualityPercent { get; init; } = 150;
    public AdaptiveToolEvidence? Tool { get; init; }
    public uint RecipeId { get; init; }
    public int Craftsmanship { get; init; }
    public int Control { get; init; }
    public int MaxCP { get; init; }
    public int Level { get; init; }
    public int RecipeLevel { get; init; }
    public int MaxDurability { get; init; }
    public int RequiredProgress { get; init; }
    public int MaxQuality { get; init; }
    public int RequiredQuality { get; init; }
    public int ProgressDivider { get; init; }
    public int ProgressModifier { get; init; }
    public int QualityDivider { get; init; }
    public int QualityModifier { get; init; }
    public int Collectability1 { get; init; }
    public int Collectability2 { get; init; }
    public int Collectability3 { get; init; }
    public AdaptiveCollectabilityEvidence? CollectabilityEvidence { get; init; }
    public bool CanHQ { get; init; }
    public bool Collectable { get; init; }
    public bool ManipulationUnlocked { get; init; }

    public static AdaptiveCraftSnapshot Capture(CraftState craft, AdaptiveToolEvidence? tool = null,
        AdaptiveCollectabilityEvidence? collectabilityEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(craft);
        if (craft.CraftExpert || craft.IshgardExpert || craft.IsCosmic || craft.MissionHasMaterialMiracle
            || (craft.ConditionFlags & ~(ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent | ConditionFlags.Poor)) != 0)
            throw new NotSupportedException("Adaptive prototype supports ordinary recipes only.");
        var snapshot = new AdaptiveCraftSnapshot
        {
            GoodQualityPercent = craft.SplendorCosmic ? 175 : 150, Tool = tool,
            RecipeId = craft.RecipeId, Craftsmanship = craft.StatCraftsmanship, Control = craft.StatControl,
            MaxCP = craft.StatCP, Level = craft.StatLevel, RecipeLevel = craft.CraftLevel,
            MaxDurability = craft.CraftDurability, RequiredProgress = craft.CraftProgress,
            MaxQuality = craft.CraftQualityMax, RequiredQuality = craft.CraftRequiredQuality,
            ProgressDivider = craft.CraftProgressDivider, ProgressModifier = craft.CraftProgressModifier,
            QualityDivider = craft.CraftQualityDivider, QualityModifier = craft.CraftQualityModifier,
            Collectability1 = craft.CraftQualityMin1, Collectability2 = craft.CraftQualityMin2, Collectability3 = craft.CraftQualityMin3,
            CollectabilityEvidence = collectabilityEvidence,
            CanHQ = craft.CraftHQ, Collectable = craft.CraftCollectible, ManipulationUnlocked = craft.UnlockedManipulation,
        };
        snapshot.Validate();
        return snapshot;
    }

    internal void Validate()
    {
        if (CollectabilityEvidence != null)
        {
            var tiers = CollectabilityEvidence.QualityThresholds(MaxQuality);
            if (!Collectable || tiers != (Collectability1, Collectability2, Collectability3))
                throw new ArgumentException("Collectability thresholds differ from captured game-sheet evidence.");
        }
        if (GoodQualityPercent is not (150 or 175)
            || Tool != null && (Tool.MainHandItemId == 0 || Tool.JobId is < 8 or > 15 || Tool.GoodQualityPercent != GoodQualityPercent
                || string.IsNullOrEmpty(Tool.DetectionSource))
            || Level is < 1 or > 100 || RecipeLevel < 1 || Craftsmanship < 0 || Control < 0 || MaxCP < 0
            || MaxDurability <= 0 || RequiredProgress <= 0 || MaxQuality < 0 || RequiredQuality < 0 || RequiredQuality > MaxQuality
            || ProgressDivider <= 0 || QualityDivider <= 0 || ProgressModifier <= 0 || QualityModifier <= 0
            || Collectability1 < 0 || Collectability2 < Collectability1 || Collectability3 < Collectability2 || Collectability3 > MaxQuality)
            throw new ArgumentException("Invalid or unsupported Adaptive recipe/stats.");
    }

    // Each adapter owns this object; it is never shared with callers or workers.
    internal CraftState ToSimulatorCraft() => new()
    {
        RecipeId = RecipeId, StatCraftsmanship = Craftsmanship, StatControl = Control, StatCP = MaxCP,
        StatLevel = Level, CraftLevel = RecipeLevel, CraftDurability = MaxDurability,
        CraftProgress = RequiredProgress, CraftQualityMax = MaxQuality, CraftRequiredQuality = RequiredQuality,
        CraftProgressDivider = ProgressDivider, CraftProgressModifier = ProgressModifier,
        CraftQualityDivider = QualityDivider, CraftQualityModifier = QualityModifier,
        CraftQualityMin1 = Collectability1, CraftQualityMin2 = Collectability2, CraftQualityMin3 = Collectability3,
        CraftHQ = CanHQ, CraftCollectible = Collectable, UnlockedManipulation = ManipulationUnlocked,
        SplendorCosmic = GoodQualityPercent == 175,
        // Unknown future conditions are Normal, while GetNextCondition preserves forced transitions.
        CraftConditionProbabilities = [],
    };
}
