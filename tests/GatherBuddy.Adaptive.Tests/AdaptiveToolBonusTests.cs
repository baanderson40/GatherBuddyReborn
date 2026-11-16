using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveToolBonusTests
{
    private const string CosmicDescription = "Phase 9 cosmic tool.\n" + AdaptiveToolBonus.EnhancedEffect
        + "\nCompleting a stellar mission from start to finish with this tool equipped grants a bonus to earned rewards.\nLunar Credit Bonus: +35%";

    private static AdaptiveToolEvidence Evidence(bool enhanced)
    {
        Assert.True(AdaptiveToolBonus.TryResolve(enhanced ? 45686u : 39761u, 15, 15, true, 0, 0, 0,
            enhanced ? CosmicDescription : "", out var evidence, out _));
        return evidence!;
    }
    private static CraftState Craft(bool enhanced) => new()
    {
        RecipeId = 5618, StatCraftsmanship = 5953, StatControl = 5490, StatCP = 664, StatLevel = 100,
        CraftLevel = 100, CraftDurability = 80, CraftProgress = 6600, CraftQualityMax = 12000,
        CraftProgressDivider = 170, CraftProgressModifier = 90, CraftQualityDivider = 150, CraftQualityModifier = 75,
        CraftHQ = true, UnlockedManipulation = true, SplendorCosmic = enhanced,
    };
    private static AdaptiveSearchState Before => new(false, 6209, 7290, 70, 320, Condition.Good,
        InnerQuiet: 3, WasteNot: 3, Manipulation: 1, Innovation: 3, TrainedPerfectionAvailable: true,
        Combo: VulcanSkill.PreparatoryTouch);
    private sealed class Scheduler : IAdaptiveSearchScheduler
    {
        public List<(AdaptiveSearchRequest Request, CancellationToken Token, TaskCompletionSource<AdaptiveSearchResult> Completion)> Work { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<AdaptiveSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Work.Add((request, cancellation, completion));
            return completion.Task;
        }
    }

    [Theory]
    [InlineData(false, 150, 9045)]
    [InlineData(true, 175, 9337)]
    public void SuppliedGoodTransitionHasExactQualityAndAllObservedEffects(bool enhanced, int percent, int quality)
    {
        var snapshot = AdaptiveCraftSnapshot.Capture(Craft(enhanced), Evidence(enhanced));
        Assert.Equal(percent, snapshot.GoodQualityPercent);
        Assert.Equal(300, Simulator.BaseQuality(snapshot.ToSimulatorCraft()));
        var simulation = new AdaptiveSimulation(snapshot, AdaptiveCraftGoal.Resolve(snapshot, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(Before, VulcanSkill.PreparatoryTouch, out var after));
        Assert.Equal(new(false, 6209, quality, 65, 280, Condition.Normal, InnerQuiet: 5, WasteNot: 2,
            Innovation: 2, TrainedPerfectionAvailable: true, Combo: VulcanSkill.PreparatoryTouch), after);
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.CreateWorkspace().TryApply(Before, VulcanSkill.PreparatoryTouch, out var scratch));
        Assert.Equal(after, scratch);
    }

    [Theory]
    [InlineData(Condition.Normal, 1170)]
    [InlineData(Condition.Excellent, 4680)]
    [InlineData(Condition.Poor, 585)]
    public void EnhancedToolDoesNotChangeNonGoodConditions(Condition condition, int expectedGain)
    {
        var normal = Craft(false);
        var enhanced = Craft(true);
        var step = Before.ToSimulatorStep();
        step.Condition = condition;
        Assert.Equal(expectedGain, Simulator.CalculateQuality(normal, step, VulcanSkill.PreparatoryTouch));
        Assert.Equal(expectedGain, Simulator.CalculateQuality(enhanced, step, VulcanSkill.PreparatoryTouch));
    }

    [Fact]
    public void BuffIqRoundingAndQualityCapMatchManualIntegerFixtures()
    {
        foreach (var enhanced in new[] { false, true })
        {
            var craft = AdaptiveCraftSnapshot.Capture(Craft(enhanced));
            var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 12000));
            var workspace = simulation.CreateWorkspace();
            foreach (var condition in new[] { Condition.Normal, Condition.Good, Condition.Excellent, Condition.Poor })
            for (var iq = 0; iq <= 10; ++iq)
            for (var buffs = 0; buffs < 4; ++buffs)
            {
                var state = Before with { Quality = 11000, InnerQuiet = iq, Condition = condition,
                    Innovation = (buffs & 1) != 0 ? 3 : 0, GreatStrides = (buffs & 2) != 0 ? 3 : 0 };
                var conditionPercent = condition switch { Condition.Good => enhanced ? 175 : 150, Condition.Excellent => 400, Condition.Poor => 50, _ => 100 };
                var buffPercent = 100 + ((buffs & 1) != 0 ? 50 : 0) + ((buffs & 2) != 0 ? 100 : 0);
                var gain = (int)(300L * 200 * buffPercent * (100 + 10 * iq) * conditionPercent / 100_000_000);
                simulation.TryApply(state, VulcanSkill.PreparatoryTouch, out var after);
                workspace.TryApply(state, VulcanSkill.PreparatoryTouch, out var scratch);
                Assert.Equal(Math.Min(12000, 11000 + gain), after.Quality);
                Assert.Equal(after, scratch);
            }
        }
    }

    [Fact]
    public void SnapshotRoundTripAndSearchIdentityIncludeToolMultiplier()
    {
        var normal = AdaptiveCraftSnapshot.Capture(Craft(false));
        var enhanced = AdaptiveCraftSnapshot.Capture(Craft(true), Evidence(true));
        Assert.True(enhanced.ToSimulatorCraft().SplendorCosmic);
        Assert.Equal(enhanced, AdaptiveCraftSnapshot.Capture(enhanced.ToSimulatorCraft(), enhanced.Tool));
        Assert.Equal(enhanced, JsonSerializer.Deserialize<AdaptiveCraftSnapshot>(JsonSerializer.Serialize(enhanced)));
        Assert.Equal(2, enhanced.SimulationModelVersion);
        var identity = new AdaptiveDecisionIdentity(Guid.NewGuid(), 1, 1, normal,
            AdaptiveCraftGoal.Resolve(normal, AdaptiveGoalKind.Quality, 12000), new(), Before);
        Assert.NotEqual(identity, identity with { Craft = normal with { GoodQualityPercent = 175 } });
        Assert.Equal(150, JsonSerializer.Deserialize<AdaptiveCraftSnapshot>("{}")!.GoodQualityPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(151)]
    [InlineData(200)]
    public void UnsupportedPercentageIsRejected(int percent)
    {
        var craft = AdaptiveCraftSnapshot.Capture(Craft(false)) with { GoodQualityPercent = percent };
        Assert.Throws<ArgumentException>(() => craft.Validate());
    }

    [Fact]
    public void ConflictingToolEvidenceAndCosmicMissionMechanicsAreStillRejected()
    {
        Assert.Throws<ArgumentException>(() => AdaptiveCraftSnapshot.Capture(Craft(false), Evidence(true)));
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(Craft(true) with { IsCosmic = true }));
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(Craft(true) with { MissionHasMaterialMiracle = true }));
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(Craft(true) with { CraftExpert = true }));
    }

    [Fact]
    public void ExactRootReplayCannotSubstituteOrdinaryAndEnhancedGoodModels()
    {
        var normal = AdaptiveCraftSnapshot.Capture(Craft(false));
        var enhanced = AdaptiveCraftSnapshot.Capture(Craft(true));
        var plan = new[] { VulcanSkill.PreparatoryTouch, VulcanSkill.CarefulSynthesis };
        var ordinarySimulation = new AdaptiveSimulation(normal, AdaptiveCraftGoal.Resolve(normal, AdaptiveGoalKind.Quality, 9337, true));
        var enhancedSimulation = new AdaptiveSimulation(enhanced, AdaptiveCraftGoal.Resolve(enhanced, AdaptiveGoalKind.Quality, 9337, true));
        Assert.False(AdaptivePlanValidator.TryValidate(ordinarySimulation, Before, plan, out _));
        Assert.True(AdaptivePlanValidator.TryValidate(enhancedSimulation, Before, plan, out var validated));
        Assert.Equal(9337, validated!.FinalState.Quality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedNativeSequenceCorrelatesOnlyWithCorrectToolBonus(bool enhanced)
    {
        var snapshot = AdaptiveCraftSnapshot.Capture(Craft(enhanced), Evidence(enhanced));
        var initial = new AdaptiveObservedFrame(1, 0, 6000, 80, 664, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(snapshot, initial, TimeSpan.Zero, scheduler: new Scheduler());
        var transitions = new (VulcanSkill Action, AdaptiveObservedFrame Frame)[]
        {
            (VulcanSkill.MuscleMemory, new(2, 948, 6000, 70, 658, Condition.Normal, MuscleMemory: 5)),
            (VulcanSkill.Manipulation, new(3, 948, 6000, 70, 562, Condition.Normal, Manipulation: 8, MuscleMemory: 4)),
            (VulcanSkill.Veneration, new(4, 948, 6000, 75, 544, Condition.Normal, Manipulation: 7, Veneration: 4, MuscleMemory: 3)),
            (VulcanSkill.WasteNot2, new(5, 948, 6000, 80, 446, Condition.Normal, WasteNot: 8, Manipulation: 6, Veneration: 3, MuscleMemory: 2)),
            (VulcanSkill.Groundwork, new(6, 3792, 6000, 75, 428, Condition.Normal, WasteNot: 7, Manipulation: 5, Veneration: 2)),
            (VulcanSkill.Groundwork, new(7, 5498, 6000, 70, 410, Condition.Normal, WasteNot: 6, Manipulation: 4, Veneration: 1)),
            (VulcanSkill.DelicateSynthesis, new(8, 6209, 6300, 70, 378, Condition.Normal, InnerQuiet: 1, WasteNot: 5, Manipulation: 3)),
            (VulcanSkill.Innovation, new(9, 6209, 6300, 75, 360, Condition.Normal, InnerQuiet: 1, WasteNot: 4, Manipulation: 2, Innovation: 4)),
            (VulcanSkill.PreparatoryTouch, new(10, 6209, 7290, 70, 320, Condition.Good, InnerQuiet: 3, WasteNot: 3, Manipulation: 1, Innovation: 3)),
            (VulcanSkill.PreparatoryTouch, new(11, 6209, 9337, 65, 280, Condition.Normal, InnerQuiet: 5, WasteNot: 2, Innovation: 2)),
        };
        var before = initial;
        var frame = 0;
        foreach (var transition in transitions)
        {
            var ticket = shadow.RecordRequest(before, transition.Action, TimeSpan.FromMilliseconds(frame));
            Assert.True(shadow.ReportRequest(ticket, true));
            shadow.Observe(++frame, TimeSpan.FromMilliseconds(frame), transition.Frame, false);
            shadow.Observe(++frame, TimeSpan.FromMilliseconds(frame), transition.Frame, false);
            before = transition.Frame;
        }
        Assert.Equal(!enhanced, shadow.Blocked);
        Assert.Equal(enhanced ? 10 : 9, shadow.CorrelatedActions);
        if (enhanced) Assert.Null(shadow.Summary.LastMismatch);
        else
        {
            shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), true);
            var mismatch = shadow.Summary.LastMismatch!;
            var difference = Assert.Single(mismatch.Differences);
            Assert.Equal(new("Quality", "9045", "9337"), difference);
            Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveFieldDifference>)mismatch.Differences).Clear());
            Assert.Equal(9337, mismatch.ObservedProjection.Quality);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(39761u)]
    public void ToolChangeOrUnavailableIdentityRevokesHistoricalWork(uint? changedItem)
    {
        var craft = AdaptiveCraftSnapshot.Capture(Craft(true), Evidence(true));
        var scheduler = new Scheduler();
        var initial = new AdaptiveObservedFrame(1, 0, 6000, 80, 664, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero, scheduler: scheduler);
        shadow.Observe(1, TimeSpan.FromMilliseconds(1), initial, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(2), initial, false);
        Assert.True(shadow.CheckToolContext(45686, TimeSpan.FromMilliseconds(3)));
        Assert.False(shadow.CheckToolContext(changedItem, TimeSpan.FromMilliseconds(4)));
        Assert.True(shadow.Blocked);
        var work = Assert.Single(scheduler.Work);
        Assert.True(work.Token.IsCancellationRequested);
        work.Completion.SetResult(new AdaptiveSearchEngine().Search(new(work.Request.Identity.Craft, work.Request.Identity.Goal),
            work.Request.Identity.State, AdaptiveCoreTests.Limits()));
        shadow.Poll();
        Assert.Empty(shadow.HistoricalResults);
    }

    [Theory]
    [InlineData("", 150)]
    [InlineData("Adaptive splendorous tool.\n\n" + AdaptiveToolBonus.EnhancedEffect, 175)]
    [InlineData(CosmicDescription, 175)]
    public void ExactItemEffectMetadataResolvesWithoutToolNames(string description, int percent)
    {
        Assert.True(AdaptiveToolBonus.TryResolve(123, 15, 15, true, 0, 0, 0, description, out var evidence, out _));
        Assert.Equal(percent, evidence!.GoodQualityPercent);
        Assert.Equal(123u, evidence.MainHandItemId);
        Assert.Equal(description, evidence.EffectDescription);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Cosmic Frypan")]
    [InlineData("Increases to quality are 2.0 times higher than normal when material condition is Good.")]
    [InlineData(CosmicDescription + "\nUnknown crafting effect.")]
    [InlineData("Phase 8 cosmic tool.")]
    public void UnrecognizedOrMissingEffectDataIsNeverAssumedOrdinary(string? description)
    {
        Assert.False(AdaptiveToolBonus.TryResolve(123, 15, 15, true, 0, 0, 0, description, out var evidence, out _));
        Assert.Null(evidence);
    }

    [Theory]
    [InlineData(0u, 15u, 15u, true, 0u, 0, 0u)]
    [InlineData(123u, 15u, 14u, true, 0u, 0, 0u)]
    [InlineData(123u, 16u, 16u, true, 0u, 0, 0u)]
    [InlineData(123u, 15u, 15u, false, 0u, 0, 0u)]
    [InlineData(123u, 15u, 15u, true, 1u, 0, 0u)]
    [InlineData(123u, 15u, 15u, true, 0u, 1, 0u)]
    [InlineData(123u, 15u, 15u, true, 0u, 0, 1u)]
    public void InvalidToolMetadataAndOtherEffectChannelsFailClosed(uint id, uint job, uint itemJob, bool slot, uint bonus, int parameter, uint action)
        => Assert.False(AdaptiveToolBonus.TryResolve(id, job, itemJob, slot, bonus, parameter, action, "", out _, out _));

    [Fact]
    public void FrozenEnglishItemMetadataTemplatesAndReplicaExamplesResolveExactly()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tool-effects.json")));
        var descriptions = fixture.RootElement.GetProperty("Descriptions").EnumerateArray().Select(d => d.GetString()!).ToArray();
        Assert.Equal(17, descriptions.Length);
        foreach (var description in descriptions)
        {
            Assert.True(AdaptiveToolBonus.TryResolve(1, 8, 8, true, 0, 0, 0, description, out var evidence, out _));
            Assert.Equal(175, evidence!.GoodQualityPercent);
            Assert.True(AdaptiveToolBonus.TryResolve(1, 8, 8, true, 0, 0, 0, description.Replace("\n", "\r\n"), out _, out _));
        }
        foreach (var row in fixture.RootElement.GetProperty("ItemExamples").EnumerateArray())
        {
            var index = row.GetProperty("DescriptionIndex");
            var description = index.ValueKind == JsonValueKind.Null ? "" : descriptions[index.GetInt32()];
            var job = row.GetProperty("JobId").GetUInt32();
            Assert.True(AdaptiveToolBonus.TryResolve(row.GetProperty("ItemId").GetUInt32(), job, job, true, 0, 0, 0, description, out var evidence, out _));
            Assert.Equal(row.GetProperty("GoodQualityPercent").GetInt32(), evidence!.GoodQualityPercent);
        }
    }

    [Fact]
    public void EnhancedToolNonGoodConditionsStillMatchIndependentFrozenRustProjection()
    {
        // Pinned Rust has no 175% Good setting. These cases independently cover only unaffected conditions.
        foreach (var recipe in ReferenceCorpus.Loaded.Recipes)
        {
            var craft = recipe.Craft with { GoodQualityPercent = 175 };
            var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
            foreach (var trial in ReferenceCorpus.Loaded.Transitions.Where(t => t.RecipeKey == recipe.Key && t.Before.Condition != Condition.Good))
            {
                var outcome = simulation.TryApply(trial.Before, trial.Action, out var after);
                Assert.Equal(trial.Legal || ReferenceCorpus.IsStandaloneRefinedTouchErratum(recipe, trial), outcome != AdaptiveOutcome.Illegal);
                if (!trial.Legal) continue;
                var expected = trial.After!.Value;
                // Same explicit native-backed Trained Eye erratum as AdaptiveReferenceTests;
                // raw pinned Rust rows remain unchanged. Tool/quality comparisons stay exact.
                if (trial.Action == VulcanSkill.TrainedEye)
                    expected = expected with { Durability = trial.Before.Durability };
                var actual = ReferenceCorpus.Project(after);
                if (!trial.Terminal) Assert.Equal(expected, actual);
                else
                {
                    Assert.Equal(expected.Progress, actual.Progress);
                    Assert.Equal(expected.Quality, actual.Quality);
                    Assert.Equal(expected.CP, actual.CP);
                    Assert.Equal(expected.Durability, actual.Durability);
                }
            }
        }
    }
}
