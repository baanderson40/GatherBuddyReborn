using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

/// <summary>Exercise existing solvers against the actual plugin's corrected simulator without a game.</summary>
public class AdaptiveSimulatorCompatibilityTests
{
    [Fact]
    public void ActualPluginTrainedEyeFillsQualityWithoutSpendingDurabilityOrPerfection()
    {
        var craft = Craft();
        var before = GameStateBuilder.BuildInitialStepState(craft, 700) with { TrainedPerfectionActive = true };
        Assert.Equal(0, Simulator.GetDurabilityCost(before, VulcanSkill.TrainedEye));
        var (result, after) = Simulator.Execute(craft, before, VulcanSkill.TrainedEye, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        Assert.Equal(before.Durability, after.Durability);
        Assert.Equal(before.RemainingCP - 250, after.RemainingCP);
        Assert.Equal(craft.CraftQualityMax, after.Quality);
        Assert.True(after.TrainedPerfectionActive);
    }

    [Fact]
    public void ActualPluginRefinedTouchAllowsStandaloneAndGrantsBonusOnlyInCombo()
    {
        var craft = Craft();
        var initial = GameStateBuilder.BuildInitialStepState(craft, 0);
        var (standaloneResult, standalone) = Simulator.Execute(craft, initial, VulcanSkill.RefinedTouch, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, standaloneResult);
        Assert.Equal(1, standalone.IQStacks);
        var (_, basic) = Simulator.Execute(craft, initial, VulcanSkill.BasicTouch, 0, 1);
        var (_, combo) = Simulator.Execute(craft, basic, VulcanSkill.RefinedTouch, 0, 1);
        Assert.Equal(3, combo.IQStacks);
    }

    [Fact]
    public void ActualPluginQuickInnovationDoesNotAdvanceStepOrCondition()
    {
        var craft = Craft();
        var initial = GameStateBuilder.BuildInitialStepState(craft, 0) with { QuickInnoLeft = 1, Condition = Condition.Excellent };
        var (result, after) = Simulator.Execute(craft, initial, VulcanSkill.QuickInnovation, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        Assert.Equal(initial.Index, after.Index);
        Assert.Equal(initial.Condition, after.Condition);
        Assert.Equal(1, after.InnovationLeft);
    }

    [Theory]
    [InlineData(95, 0)]
    [InlineData(96, 1)]
    public void ActualPluginExpedienceRespectsLevel96Trait(int level, int expected)
    {
        var craft = Craft() with { StatLevel = level };
        var (_, after) = Simulator.Execute(craft, GameStateBuilder.BuildInitialStepState(craft, 0), VulcanSkill.HastyTouch, 0, 1);
        Assert.Equal(expected, after.ExpedienceLeft);
    }

    private static CraftState Craft() => new()
    {
        StatCraftsmanship = 1000, StatControl = 1000, StatCP = 600, StatLevel = 100,
        CraftLevel = 90, CraftDurability = 80, CraftProgress = 400, CraftQualityMax = 5000,
        CraftProgressDivider = 100, CraftProgressModifier = 100,
        CraftQualityDivider = 100, CraftQualityModifier = 100, CraftHQ = true,
        UnlockedManipulation = true, CraftConditionProbabilities = [1, 0.25f, 0.04f],
    };

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ExistingStandardSolverStillCompletesSimpleSupportedCraft(int hqChance)
    {
        var craft = Craft();
        var solver = new StandardSolver(new() { MaxPercentage = hqChance });
        var step = GameStateBuilder.BuildInitialStepState(craft, 700);
        for (var i = 0; i < 64 && step.Progress < craft.CraftProgress && step.Durability > 0; ++i)
        {
            var recommendation = solver.Solve(craft, step);
            Assert.NotEqual(VulcanSkill.None, recommendation.Action);
            var (result, next) = Simulator.Execute(craft, step, recommendation.Action, 0, 1);
            Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
            step = next;
        }
        Assert.True(step.Progress >= craft.CraftProgress);
        if (hqChance == 100) Assert.Equal(craft.CraftQualityMax, step.Quality);
    }

    [Fact]
    public void ExistingProgressOnlyAndEstimatorStillUseWorkingSimulator()
    {
        var craft = Craft();
        var solver = new ProgressOnlySolver();
        Assert.True(SolverUtils.TryEstimateActionCount(solver, craft, 0, out var count));
        Assert.InRange(count, 1, 10);
    }

    [Fact]
    public void AdaptiveModeAppendsWithoutRenumberingExistingModesAndOfflineEngineStillWorks()
    {
        Assert.Equal(0, (int)VulcanSolverMode.PureRaphael);
        Assert.Equal(1, (int)VulcanSolverMode.StandardSolver);
        Assert.Equal(2, (int)VulcanSolverMode.ProgressOnly);
        Assert.Equal(3, (int)VulcanSolverMode.Adaptive);
        Assert.Equal(new[] { VulcanSolverMode.PureRaphael, VulcanSolverMode.StandardSolver, VulcanSolverMode.ProgressOnly, VulcanSolverMode.Adaptive },
            Enum.GetValues<VulcanSolverMode>());
        var craft = AdaptiveCraftSnapshot.Capture(Craft());
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        var simulation = new AdaptiveSimulation(craft, goal);
        var state = AdaptiveSearchState.Capture(GameStateBuilder.BuildInitialStepState(Craft()));
        var result = new AdaptiveSearchEngine().Search(simulation, state, new() { TimeLimit = null, MaximumTransitions = 2000 });
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.NotNull(result.Plan);
    }

    [Fact]
    public void ActualPluginCoordinatorRequiresPublicationAndOneUseLease()
    {
        using var coordinator = new AdaptiveSolveCoordinator(new ImmediateScheduler());
        var craft = AdaptiveCraftSnapshot.Capture(Craft());
        var session = coordinator.Begin(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly),
            new() { TimeLimit = null, MaximumTransitions = 2000 });
        var state = AdaptiveSearchState.Capture(GameStateBuilder.BuildInitialStepState(Craft()));
        coordinator.Observe(session, 0, state);
        Assert.Equal(AdaptiveDecisionPhase.Searching, coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, coordinator.Snapshot.Action);
        coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Ready, coordinator.Snapshot.Phase);
        Assert.True(coordinator.TryCreateLease(session, 0, state, out var lease));
        Assert.True(coordinator.TryConsumeLease(lease!, session, 0, state));
        Assert.False(coordinator.TryConsumeLease(lease!, session, 0, state));
        coordinator.Stop();
        Assert.False(coordinator.ReportExecution(lease!, true));
    }

    private sealed class ImmediateScheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.FromResult(new AdaptiveSearchEngine().Search(new(request.Identity.Craft, request.Identity.Goal),
                request.Identity.State, request.Identity.Limits, request.Continuation, cancellation));
    }
}
