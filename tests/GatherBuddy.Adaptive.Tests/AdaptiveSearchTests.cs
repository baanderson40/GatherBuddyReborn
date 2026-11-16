using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSearchTests
{
    private static AdaptiveSearchResult Search(AdaptiveSimulation simulation, AdaptiveSearchState? initial = null,
        AdaptiveSearchLimits? limits = null, IReadOnlyList<VulcanSkill>? continuation = null)
        => new AdaptiveSearchEngine().Search(simulation, initial ?? AdaptiveCoreTests.Initial(), limits ?? AdaptiveCoreTests.Limits(), continuation);

    private static AdaptivePlan Validated(AdaptiveSimulation simulation, AdaptiveSearchState initial, AdaptiveSearchResult result)
    {
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.NotNull(result.Plan);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, result.Plan.Actions, out var replay));
        Assert.Equal(result.Plan.FinalState, replay!.FinalState);
        Assert.True(result.Plan.FinalState.CP >= 0);
        return result.Plan;
    }

    [Fact]
    public void ImmediateFinishDoesNotSpendExtraQualityActions()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        var plan = Validated(simulation, AdaptiveCoreTests.Initial(), Search(simulation));
        Assert.Single(plan.Actions);
        Assert.True(plan.TargetReached);
    }

    [Fact]
    public void TrainedEyeFindsFullQualityRouteFromNonzeroStartingQuality()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftLevel = 90, CraftProgress = 400 }, 5000);
        var initial = AdaptiveCoreTests.Initial() with { Quality = 700 };
        var plan = Validated(simulation, initial, Search(simulation, initial));
        Assert.Contains(VulcanSkill.TrainedEye, plan.Actions);
        Assert.Equal(5000, plan.FinalState.Quality);
        Assert.True(plan.TargetReached);
    }

    [Fact]
    public void SearchCanBuildQualityWithoutFinishingProgressPrematurely()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 500);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false };
        var plan = Validated(simulation, initial, Search(simulation, initial));
        Assert.True(plan.TargetReached);
        Assert.NotEqual(VulcanSkill.BasicSynthesis, plan.Actions[0]);
    }

    [Fact]
    public void SearchReservesFinishingResourcesAndReturnsShortfallWithoutClaimingImpossible()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 200 }, 5000);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, CP = 18, Durability = 30, TrainedPerfectionAvailable = false };
        var result = Search(simulation, initial);
        var plan = Validated(simulation, initial, result);
        Assert.False(plan.TargetReached);
        Assert.Contains("not found", result.Reason);
        Assert.DoesNotContain("impossible", result.Reason);
    }

    [Fact]
    public void StrictTargetBlocksBelowDesiredCompletion()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 5000, true);
        var result = Search(simulation, AdaptiveCoreTests.Initial() with { CP = 0, Durability = 10, FirstStep = false });
        Assert.Equal(AdaptiveSearchStatus.Unknown, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void CompletionPlannerCanRepairAndDoesNotMisclassifyBudgetFailure()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { StatLevel = 50, CraftProgress = 400 }, 0);
        var initial = AdaptiveCoreTests.Initial() with { Durability = 10, FirstStep = false, TrainedPerfectionAvailable = false };
        var planner = new AdaptiveCompletionPlanner();
        var plan = Validated(simulation, initial, planner.Find(simulation, initial, AdaptiveCoreTests.Limits()));
        Assert.Contains(VulcanSkill.MastersMend, plan.Actions);
        var result = planner.Find(simulation, initial, AdaptiveCoreTests.Limits() with { MaximumTransitions = 1 });
        Assert.Equal(AdaptiveSearchStatus.Unknown, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void MandatoryQualityCannotBecomeAProgressOnlySuffixBelowThreshold()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100, CraftRequiredQuality = 200 }, 200);
        var initial = AdaptiveCoreTests.Initial();
        var plan = Validated(simulation, initial, new AdaptiveCompletionPlanner().Find(simulation, initial, AdaptiveCoreTests.Limits()));
        Assert.True(plan.FinalState.Quality >= 200);
        Assert.True(plan.Actions.Count >= 2);
    }

    [Fact]
    public void CompletedAndBrokenStatesHaveDistinctResults()
    {
        var simulation = AdaptiveCoreTests.Simulation(target: 0);
        var result = Search(simulation, AdaptiveCoreTests.Initial() with { Progress = 2000, Durability = 0 });
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.Empty(result.Plan!.Actions);
        result = Search(simulation, AdaptiveCoreTests.Initial() with { Durability = 0 });
        Assert.Equal(AdaptiveSearchStatus.ProvenImpossible, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void PreviousContinuationIsRevalidatedAndSurvivesTinySearchBudget()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        var limits = AdaptiveCoreTests.Limits() with { MaximumTransitions = 1 };
        var initial = AdaptiveCoreTests.Initial();
        var plan = Validated(simulation, initial, Search(simulation, initial, limits, [VulcanSkill.BasicSynthesis]));
        Assert.Single(plan.Actions);
        var invalid = Search(simulation, initial, limits, [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis]);
        // The bad seed is rejected. The immediate-finisher check can now independently find
        // Basic Synthesis within one transition; accepting that replayed route is safe.
        var independent = Validated(simulation, initial, invalid);
        Assert.Equal(VulcanSkill.BasicSynthesis, Assert.Single(independent.Actions));
        Assert.Equal(1, invalid.Diagnostics.Transitions);
    }

    [Fact]
    public void ChangedConditionInvalidatesConditionDependentContinuation()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 200, true);
        VulcanSkill[] continuation = [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis];
        var limits = AdaptiveCoreTests.Limits() with { MaximumTransitions = 1 };
        var good = AdaptiveCoreTests.Initial() with { Condition = Condition.Good };
        Validated(simulation, good, Search(simulation, good, limits, continuation));
        var normal = Search(simulation, good with { Condition = Condition.Normal }, limits, continuation);
        Assert.Null(normal.Plan);
    }

    [Fact]
    public void BudgetsAreDeterministicAndMemoryIsBounded()
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var limits = AdaptiveCoreTests.Limits() with { MaximumTransitions = 1500, MaximumStoredNodes = 500 };
        var first = Search(simulation, limits: limits);
        var second = Search(simulation, limits: limits);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Plan?.Actions, second.Plan?.Actions);
        Assert.Equal(first.Diagnostics.Transitions, second.Diagnostics.Transitions);
        Assert.True(first.Diagnostics.Transitions <= 1500);
        Assert.True(first.Diagnostics.StoredNodes <= 500);
        Assert.True(first.Diagnostics.BudgetExhausted);
    }

    [Fact]
    public void CancellationNeverPublishesExecutablePlan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = new AdaptiveSearchEngine().Search(AdaptiveCoreTests.Simulation(), AdaptiveCoreTests.Initial(),
            AdaptiveCoreTests.Limits(), [VulcanSkill.BasicSynthesis], cancellation.Token);
        Assert.Equal(AdaptiveSearchStatus.Cancelled, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(0, result.Diagnostics.Transitions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimitsFailBeforeSearching(int width)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Search(AdaptiveCoreTests.Simulation(), limits: AdaptiveCoreTests.Limits() with { BeamWidth = width }));

    [Fact]
    public void InvestmentComboCanReachTargetWhereUnbuffedByregotCannot()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 1500);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, InnerQuiet = 10, CP = 56, Durability = 20, TrainedPerfectionAvailable = false };
        var plan = Validated(simulation, initial, Search(simulation, initial));
        Assert.True(plan.TargetReached);
        Assert.Contains(VulcanSkill.GreatStrides, plan.Actions);
        Assert.Contains(VulcanSkill.ByregotsBlessing, plan.Actions);
    }

    [Fact]
    public void ByregotReachingTargetDoesNotRequireAnExtraStridesAction()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 800);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, InnerQuiet = 10, CP = 24, Durability = 20, TrainedPerfectionAvailable = false };
        var plan = Validated(simulation, initial, Search(simulation, initial));
        Assert.True(plan.TargetReached);
        Assert.Equal(new[] { VulcanSkill.ByregotsBlessing, VulcanSkill.BasicSynthesis }, plan.Actions);
    }

    [Fact]
    public void WallClockCutoffNeverReturnsAnUnvalidatedPartialPlan()
    {
        var result = Search(AdaptiveCoreTests.Simulation(), limits: AdaptiveCoreTests.Limits() with { TimeLimit = TimeSpan.FromTicks(1) });
        Assert.Equal(AdaptiveSearchStatus.Unknown, result.Status);
        Assert.Null(result.Plan);
        Assert.True(result.Diagnostics.BudgetExhausted);
    }
}
