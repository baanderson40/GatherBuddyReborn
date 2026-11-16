using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveTuningTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("recipe-34890-level-90-durability-35")]
    [InlineData("recipe-35907-level-100-durability-35")]
    [InlineData("recipe-5618-level-100-durability-80")]
    public void DeepQualityWitnessesShareOriginalWorkBudgets(string key)
    {
        var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == key);
        var simulation = new AdaptiveSimulation(recipe.Craft, AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
        var limits = Limits();
        var result = new AdaptiveSearchEngine().Search(simulation, ReferenceCorpus.Initial(recipe), limits);
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, ReferenceCorpus.Initial(recipe), result.Plan!.Actions, out _));
        Assert.True(result.Diagnostics.DeepTransitions > 0);
        Assert.True(result.Diagnostics.DeepestLayer > limits.Horizon);
        Assert.InRange(result.Diagnostics.Transitions, 1, limits.MaximumTransitions);
        Assert.InRange(result.Diagnostics.StoredNodes, 1, limits.MaximumStoredNodes);
    }

    [Fact]
    public void CashOutProbeIncludesActualProgressSuffixAndRespectsExcellent()
    {
        var craft = SimulatorAuditTests.Craft() with { CraftProgress = 100 };
        var simulation = AdaptiveCoreTests.Simulation(craft, 1500);
        var state = AdaptiveCoreTests.Initial() with { FirstStep = false, InnerQuiet = 10, CP = 56, Durability = 20 };
        var limits = Limits();
        var result = AdaptiveFinisherProbe.Find(simulation, state, limits, new(), new(limits, default), 512, 10, simulation.CreateWorkspace());
        Assert.NotNull(result);
        Assert.True(result.TargetReached);
        Assert.Equal(new[] { VulcanSkill.GreatStrides, VulcanSkill.ByregotsBlessing, VulcanSkill.BasicSynthesis }, result.Actions);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Actions, out _));

        simulation = AdaptiveCoreTests.Simulation(craft, 3000);
        state = state with { CP = 74, Condition = Condition.Excellent };
        result = AdaptiveFinisherProbe.Find(simulation, state, limits, new(), new(limits, default), 512, 10, simulation.CreateWorkspace());
        Assert.NotNull(result);
        Assert.True(result.TargetReached);
        Assert.Equal(VulcanSkill.ByregotsBlessing, result.Actions[0]); // Do not waste Excellent on setup.
    }

    [Fact]
    public void CashOutCannotConsumeLastDurabilityWithoutCompletingProgress()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 1500);
        var state = AdaptiveCoreTests.Initial() with { FirstStep = false, InnerQuiet = 10, CP = 56, Durability = 10 };
        var limits = Limits();
        var plan = AdaptiveFinisherProbe.Find(simulation, state, limits, new(), new(limits, default), 512, 10, simulation.CreateWorkspace());
        Assert.NotNull(plan);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, plan.Actions, out _));
        Assert.DoesNotContain(VulcanSkill.ByregotsBlessing, plan.Actions);
    }

    [Fact]
    public void SupportedSnapshotNamesAndIdsDoNotAffectOptimization()
    {
        var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == "recipe-34890-level-90-durability-35");
        var goal = AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.Quality, recipe.TargetQuality);
        var original = new AdaptiveSearchEngine().Search(new(recipe.Craft, goal), ReferenceCorpus.Initial(recipe), Limits());
        var renamed = recipe.Craft with { RecipeId = 999999 };
        var changed = new AdaptiveSearchEngine().Search(new(renamed, goal), ReferenceCorpus.Initial(recipe), Limits());
        Assert.Equal(original.Plan!.Actions, changed.Plan!.Actions);
        Assert.Equal(original.Plan.FinalState, changed.Plan.FinalState);
        Assert.Equal(original.Diagnostics.Transitions, changed.Diagnostics.Transitions);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void HoldoutSeedsStartingQualityAndLowerCpRemainCompletable()
    {
        var recipes = ReferenceCorpus.Loaded.Recipes.Where(r => r.Craft.Level == r.Craft.RecipeLevel
            && r.Craft.RecipeId is 34890 or 35907 or 5618).ToArray();
        var completed = 0;
        var targetReached = 0;
        foreach (var recipe in recipes)
        foreach (var quality in new[] { 0, recipe.Craft.MaxQuality / 4 })
        foreach (var seed in new[] { 101, 7379 })
        foreach (var cpReduction in new[] { 0, 20 })
        {
            var craft = recipe.Craft with { MaxCP = recipe.Craft.MaxCP - cpReduction };
            var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
            var state = ReferenceCorpus.Initial(recipe, quality) with { CP = craft.MaxCP };
            IReadOnlyList<VulcanSkill>? continuation = null;
            var random = new Random(seed);
            for (var turn = 0; turn < 128; ++turn)
            {
                var result = new AdaptiveSearchEngine().Search(simulation, state, Limits(), continuation);
                Assert.NotNull(result.Plan);
                Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out _));
                Assert.True(result.Diagnostics.Transitions <= 40_000);
                Assert.True(result.Diagnostics.StoredNodes <= 20_000);
                var before = state;
                var action = result.Plan.Actions[0];
                var outcome = simulation.TryApply(state, action, out state);
                Assert.NotEqual(AdaptiveOutcome.Illegal, outcome);
                Assert.NotEqual(AdaptiveOutcome.Failure, outcome);
                if (outcome == AdaptiveOutcome.Success)
                {
                    ++completed;
                    if (state.Quality >= recipe.TargetQuality) ++targetReached;
                    break;
                }
                continuation = result.Plan.Actions.Skip(1).ToArray();
                if (action != VulcanSkill.FinalAppraisal)
                {
                    var roll = random.NextDouble();
                    state = state with { Condition = before.Condition switch
                    {
                        Condition.Excellent => Condition.Poor,
                        Condition.Good or Condition.Poor => Condition.Normal,
                        _ => roll < 0.04 ? Condition.Excellent : roll < 0.29 ? Condition.Good : Condition.Normal,
                    } };
                }
            }
        }
        output.WriteLine($"Holdouts: {completed}/24 completed; {targetReached}/24 reached full raw quality. No claim of statistical success rate.");
        Assert.Equal(24, completed);
    }

    private static AdaptiveSearchLimits Limits() => new()
    {
        TimeLimit = null, BeamWidth = 256, Horizon = 10, MaximumTransitions = 40_000, MaximumStoredNodes = 20_000,
    };
}
