using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Adaptive.Tests;

/// <summary>Minimal schema-4 all-NQ user trace roots and recorded existing-solver requests.
/// This route is reconstructed from recorded execution, NOT a captured Raphael cache artifact.</summary>
public static class AdaptiveSeedRegressionCorpus
{
    public static AdaptiveCraftSnapshot Craft { get; } = new()
    {
        RecipeId = 5618, Craftsmanship = 5953, Control = 5490, MaxCP = 664, Level = 100, RecipeLevel = 100,
        MaxDurability = 80, RequiredProgress = 6600, MaxQuality = 12000,
        ProgressDivider = 170, ProgressModifier = 90, QualityDivider = 150, QualityModifier = 75,
        CanHQ = true, ManipulationUnlocked = true, GoodQualityPercent = 175,
    };
    public static IReadOnlyList<VulcanSkill> RecordedActions { get; } = Array.AsReadOnly(new[]
    {
        VulcanSkill.MuscleMemory, VulcanSkill.Manipulation, VulcanSkill.Veneration, VulcanSkill.WasteNot2,
        VulcanSkill.Groundwork, VulcanSkill.Groundwork, VulcanSkill.DelicateSynthesis, VulcanSkill.Innovation,
        VulcanSkill.PreparatoryTouch, VulcanSkill.PreparatoryTouch, VulcanSkill.PreparatoryTouch, VulcanSkill.PreparatoryTouch,
        VulcanSkill.Innovation, VulcanSkill.PreparatoryTouch, VulcanSkill.GreatStrides, VulcanSkill.ByregotsBlessing,
        VulcanSkill.DelicateSynthesis,
    });
    public static IReadOnlyList<AdaptiveObservedFrame> Roots { get; } = Array.AsReadOnly(new AdaptiveObservedFrame[]
    {
        new(2, 948, 0, 70, 658, Condition.Normal, MuscleMemory: 5),
        new(3, 948, 0, 70, 562, Condition.Normal, Manipulation: 8, MuscleMemory: 4),
        new(4, 948, 0, 75, 544, Condition.Normal, Manipulation: 7, Veneration: 4, MuscleMemory: 3),
        new(5, 948, 0, 80, 446, Condition.Normal, WasteNot: 8, Manipulation: 6, Veneration: 3, MuscleMemory: 2),
        new(6, 3792, 0, 75, 428, Condition.Normal, WasteNot: 7, Manipulation: 5, Veneration: 2),
        new(7, 5498, 0, 70, 410, Condition.Normal, WasteNot: 6, Manipulation: 4, Veneration: 1),
        new(8, 6209, 300, 70, 378, Condition.Normal, InnerQuiet: 1, WasteNot: 5, Manipulation: 3),
    });
    public static AdaptiveSearchState State(int index) => Roots[index].ToState(true, RecordedActions[index]);
    public static AdaptivePlanSeed Seed() => new(new("RecordedExecutionReference", "user-schema4-all-nq-normal-route",
        Craft.RecipeId, Craft.Level, Craft.Craftsmanship, Craft.Control, Craft.MaxCP, true, false, 0), RecordedActions);
    public static AdaptiveSeedCandidate Candidate(int index) => new(Seed(), index + 1, 0);
}
