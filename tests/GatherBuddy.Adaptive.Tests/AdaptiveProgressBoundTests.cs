using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveProgressBoundTests
{
    public static IEnumerable<object[]> Recipes => ReferenceCorpus.Loaded.Recipes.Select(r => new object[] { r.Key });

    [Theory]
    [MemberData(nameof(Recipes))]
    public void OptimisticProgressBoundsEveryIndependentReferenceProjection(string key)
    {
        var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == key);
        var bound = AdaptiveProgressBound.Create(recipe.Craft.ToSimulatorCraft());
        Assert.True(bound.MaximumProgressGain > 0);
        Assert.Equal(3, bound.MinimumProgressDuration);
        // Audited roster maximum: a stronger future progress action must trigger review
        // of the numeric-overflow guard, not silently extend this proof's assumptions.
        Assert.InRange(bound.MaximumProgressGain, 1, Simulator.BaseProgress(recipe.Craft.ToSimulatorCraft()) * 10);
        foreach (var transition in ReferenceCorpus.Loaded.Transitions.Where(t => t.RecipeKey == key && t.Legal))
        {
            var progress = transition.After!.Value.Progress - transition.Before.Progress;
            Assert.True(progress <= bound.MaximumProgressGain, $"{key}/{transition.Action}: {progress}>{bound.MaximumProgressGain}");
            if (progress > 0) Assert.True(transition.Duration >= bound.MinimumProgressDuration);
        }
        var simulation = new AdaptiveSimulation(recipe.Craft, AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.ProgressOnly));
        foreach (var condition in new[] { Condition.Normal, Condition.Good, Condition.Excellent, Condition.Poor })
        foreach (var durability in new[] { 1, 5, recipe.Craft.MaxDurability })
        foreach (var perfection in new[] { false, true })
        foreach (var action in AdaptiveSimulation.Actions)
        {
            var state = ReferenceCorpus.Initial(recipe) with { Condition = condition, Durability = durability,
                Veneration = 4, MuscleMemory = 5, TrainedPerfectionActive = perfection, WasteNot = 8 };
            var outcome = simulation.TryApply(state, action, out var next);
            if (outcome == AdaptiveOutcome.Illegal) continue;
            Assert.True(next.Progress - state.Progress <= bound.MaximumProgressGain);
            if (next.Progress > state.Progress) Assert.True(AdaptiveSimulation.ActionDuration(action) >= bound.MinimumProgressDuration);
        }
    }

    [Fact]
    public void LowerBoundsAreOverflowSafeAndDoNotAddSeparateQualityActions()
    {
        var bound = new AdaptiveProgressBound(int.MaxValue, 1, 3);
        Assert.Equal((long)int.MaxValue, bound.MinimumActions(0));
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 100);
        var initial = AdaptiveCoreTests.Initial();
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, new[] { VulcanSkill.DelicateSynthesis }, out var plan));
        var workspace = simulation.CreateWorkspace();
        Assert.False(AdaptiveSearchPruning.CannotImproveNonterminal(workspace, initial, 0, 0, plan, new(new() { TimeLimit = null }, default)));
        Assert.Equal(1, workspace.CompletionBound.MinimumActions(initial.Progress));
        var fallback = new AdaptiveProgressBound(100, 0, 2);
        Assert.Equal(1, fallback.MinimumActions(0));
        var extreme = AdaptiveProgressBound.Create(SimulatorAuditTests.Craft() with
            { StatCraftsmanship = int.MaxValue, CraftProgressDivider = 1 });
        Assert.Equal(0, extreme.MaximumProgressGain);
        Assert.Equal(2, extreme.MinimumProgressDuration);
    }

    [Theory]
    [InlineData(Condition.Normal, false, false)]
    [InlineData(Condition.Good, false, false)]
    [InlineData(Condition.Excellent, false, false)]
    [InlineData(Condition.Poor, false, false)]
    [InlineData(Condition.Normal, true, false)]
    [InlineData(Condition.Normal, false, true)]
    public void StrongPruningMatchesUnprunedExhaustiveOptimumIncludingResources(Condition condition, bool appraisal, bool strict)
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with
            { CraftProgress = 400, CraftRequiredQuality = strict ? 200 : 0 }, 500, strict);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, CP = 90, Durability = 30, Quality = 500,
            Condition = condition, InnerQuiet = 4, FinalAppraisal = appraisal ? 5 : 0,
            Combo = appraisal ? VulcanSkill.FinalAppraisal : VulcanSkill.None };
        var plain = Enumerate(false);
        var pruned = Enumerate(true);
        Assert.NotNull(plain);
        Assert.NotNull(pruned);
        Assert.False(AdaptivePlanValidator.IsBetter(plain, pruned));
        Assert.False(AdaptivePlanValidator.IsBetter(pruned, plain));
        Assert.Equal(plain.Actions, pruned.Actions);

        AdaptivePlan? Enumerate(bool prune)
        {
            AdaptivePlan? best = null;
            var workspace = simulation.CreateWorkspace();
            var budget = new AdaptiveSearchBudget(new() { TimeLimit = null }, default);
            var path = new List<VulcanSkill>();
            Visit(initial, 0);
            return best;
            void Visit(AdaptiveSearchState state, int duration)
            {
                if (simulation.Classify(state) == AdaptiveOutcome.Success)
                {
                    Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, path, out var candidate));
                    var bound = workspace.CompletionBound;
                    Assert.True(bound.MinimumActions(initial.Progress) <= path.Count);
                    Assert.True(bound.MinimumActions(initial.Progress) * bound.MinimumProgressDuration <= duration);
                    if (AdaptivePlanValidator.IsBetter(candidate!, best)) best = candidate;
                    return;
                }
                if (path.Count >= 3 || (prune && AdaptiveSearchPruning.CannotImproveNonterminal(workspace, state, duration, path.Count, best, budget))) return;
                foreach (var action in AdaptiveSimulation.Actions)
                {
                    var outcome = simulation.TryApply(state, action, out var next);
                    if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) continue;
                    path.Add(action);
                    Visit(next, duration + AdaptiveSimulation.ActionDuration(action));
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
    }
}
