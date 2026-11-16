using System.Diagnostics;
using GatherBuddy.Adaptive.Tests;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Compatibility.Tests;

/// <summary>Actual plugin algorithms on pinned recipe/stat fixtures. Results, not assertions of superiority.</summary>
public class AdaptiveRecipeComparisonTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases => ReferenceCorpus.Loaded.Recipes.SelectMany(r =>
        new[] { new object[] { r.Key, false }, new object[] { r.Key, true } });

    private sealed record Run(bool Completed, int Quality, int Actions, int Blocked, int Target,
        double P95Milliseconds, long AllocatedBytes, string Reason);

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Category", "Benchmark")]
    public void CompareActualAdaptiveStandardAndFrozenRaphael(string key, bool seededConditions)
    {
        var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == key);
        var simulation = new AdaptiveSimulation(recipe.Craft,
            AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
        var standard = new StandardSolver(new() { MaxPercentage = 100 });
        var craft = LegacyCraft(recipe.Craft);
        var limits = new AdaptiveSearchLimits { TimeLimit = null, BeamWidth = 256, Horizon = 10, MaximumTransitions = 40_000, MaximumStoredNodes = 20_000 };
        IReadOnlyList<VulcanSkill>? continuation = null;
        var trace = new List<string>();
        var validatedPlans = 0;
        var adaptive = Execute(simulation, recipe, seededConditions, state =>
        {
            var result = new AdaptiveSearchEngine().Search(simulation, state, limits, continuation);
            if (result.Plan == null) return (VulcanSkill.None, result.Reason);
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out _));
            trace.Add($"{result.Plan.Actions[0]}: q={state.Quality}; iq={state.InnerQuiet}; cp={state.CP}; dur={state.Durability}; "
                + $"projected={result.Plan.FinalState.Quality}; transitions={result.Diagnostics.Transitions}; nodes={result.Diagnostics.StoredNodes}; "
                + $"depth={result.Diagnostics.DeepestLayer}; deep={result.Diagnostics.DeepTransitions}");
            ++validatedPlans;
            continuation = result.Plan.Actions.Skip(1).ToArray();
            return (result.Plan.Actions[0], result.Reason);
        });
        var standardRun = Execute(simulation, recipe, seededConditions, state =>
            (standard.Solve(craft, LegacyStep(state)).Action, "Standard recommendation"));
        var index = 0;
        var raphael = recipe.RaphaelActions.Length == 0 ? null : Execute(simulation, recipe, seededConditions, _ =>
            (index < recipe.RaphaelActions.Length ? recipe.RaphaelActions[index++] : VulcanSkill.None, "Frozen Raphael macro"));

        output.WriteLine($"{key}; conditions={(seededConditions ? "seed-2026" : "Normal")}; raw-quality target={recipe.TargetQuality}");
        output.WriteLine($"Adaptive: {adaptive}; validatedPlans={validatedPlans}");
        output.WriteLine($"Standard: {standardRun}");
        output.WriteLine($"Raphael: {(raphael == null ? recipe.RaphaelStatus : raphael.ToString())}; sourceSolve={recipe.RaphaelStatus}");
        output.WriteLine("Adaptive trace: " + string.Join(" | ", trace));
        output.WriteLine("Frozen reference: " + string.Join(" → ", recipe.RaphaelActions));
        // Fixed-corpus completion regression: broad probes previously blocked at the first turn.
        // This is not a guarantee for recipes/stats outside this bounded corpus.
        Assert.True(adaptive.Completed, adaptive.Reason);
        Assert.InRange(adaptive.Quality, 0, recipe.Craft.MaxQuality);
        var previousQuality = recipe.Craft.Level == recipe.Craft.RecipeLevel
            ? (recipe.Craft.RecipeId, seededConditions) switch
            {
                (34890, false) => 5600, (34890, true) => 6020,
                (35907, false) => 8018, (35907, true) => 10678,
                (5618, false) => 7447, (5618, true) => 8291,
                _ => recipe.TargetQuality,
            } : recipe.TargetQuality;
        // Frozen PRE-tuning regression floors, never passed to the production search.
        Assert.True(adaptive.Quality >= previousQuality, $"Quality regressed: {adaptive.Quality} < {previousQuality}");
        if (previousQuality < recipe.TargetQuality) Assert.True(adaptive.Quality > previousQuality);
        if (seededConditions && recipe.Craft.Level == recipe.Craft.RecipeLevel && recipe.Craft.RecipeId is 34890 or 35907)
            Assert.Equal(recipe.TargetQuality, adaptive.Quality);
        // Freeze the pre-schema4-pruning quality floors too; neither these numbers nor
        // recipe identities are passed into the optimizer. Preserve all 20 former targets.
        var beforePruningQuality = recipe.Craft.Level == recipe.Craft.RecipeLevel
            ? (recipe.Craft.RecipeId, seededConditions) switch
            {
                (34890, false) => 6838, (34890, true) => 7800,
                (35907, false) => 9495, (35907, true) => 12000,
                (5618, false) => 9282, (5618, true) => 10379,
                _ => recipe.TargetQuality,
            } : recipe.TargetQuality;
        Assert.True(adaptive.Quality >= beforePruningQuality,
            $"Pre-pruning quality regressed: {adaptive.Quality} < {beforePruningQuality}");
        // Allocation traffic, not peak resident memory or a wall-clock assertion. Deliberately
        // loose relative to measured ~3 MB/decision, but catches the old ~12.4 MB hot path.
        Assert.True(adaptive.AllocatedBytes < 6_000_000L * Math.Max(1, adaptive.Actions));
        if (adaptive.Completed) Assert.True(validatedPlans > 0);
        if (!seededConditions && raphael != null)
        {
            Assert.True(raphael.Completed);
            Assert.Equal(recipe.RaphaelQuality, raphael.Quality);
            Assert.Equal(recipe.RaphaelActions.Length, raphael.Actions);
        }
    }

    private static Run Execute(AdaptiveSimulation simulation, ReferenceCorpus.Recipe recipe, bool seeded,
        Func<AdaptiveSearchState, (VulcanSkill Action, string Reason)> recommend)
    {
        var state = ReferenceCorpus.Initial(recipe);
        var random = new Random(2026);
        var latencies = new List<double>();
        var allocations = GC.GetAllocatedBytesForCurrentThread();
        var actions = 0;
        var reason = "";
        var completed = false;
        for (; actions < 128;)
        {
            var timer = Stopwatch.StartNew();
            var recommendation = recommend(state);
            latencies.Add(timer.Elapsed.TotalMilliseconds);
            if (recommendation.Action == VulcanSkill.None) { reason = recommendation.Reason; break; }
            var before = state;
            var outcome = simulation.TryApply(state, recommendation.Action, out var next);
            if (outcome == AdaptiveOutcome.Illegal)
            {
                reason = $"Unavailable or outside reliable-action comparison model: {recommendation.Action}";
                break;
            }
            ++actions;
            state = next;
            if (outcome == AdaptiveOutcome.Failure) { reason = "Simulated unsuccessful craft"; break; }
            if (outcome == AdaptiveOutcome.Success) { completed = true; reason = "Completed"; break; }
            if (seeded && recommendation.Action != VulcanSkill.FinalAppraisal)
            {
                var roll = random.NextDouble();
                var goodChance = recipe.Craft.Level >= 63 ? 0.25 : 0.20;
                state = state with { Condition = before.Condition switch
                {
                    Condition.Excellent => Condition.Poor,
                    Condition.Good or Condition.Poor => Condition.Normal,
                    _ => roll < 0.04 ? Condition.Excellent : roll < 0.04 + goodChance ? Condition.Good : Condition.Normal,
                } };
            }
        }
        allocations = GC.GetAllocatedBytesForCurrentThread() - allocations;
        latencies.Sort();
        return new(completed, state.Quality, actions, completed ? 0 : 1,
            completed && state.Quality >= recipe.TargetQuality ? 1 : 0,
            latencies.Count == 0 ? 0 : Math.Round(latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1], 3),
            allocations, reason);
    }

    // Managed-only bridge for the existing Standard solver; no guessed HQ/collectability metadata.
    private static CraftState LegacyCraft(AdaptiveCraftSnapshot c) => new()
    {
        RecipeId = c.RecipeId, StatCraftsmanship = c.Craftsmanship, StatControl = c.Control, StatCP = c.MaxCP,
        StatLevel = c.Level, CraftLevel = c.RecipeLevel, CraftDurability = c.MaxDurability,
        CraftProgress = c.RequiredProgress, CraftQualityMax = c.MaxQuality, CraftRequiredQuality = c.RequiredQuality,
        CraftProgressDivider = c.ProgressDivider, CraftProgressModifier = c.ProgressModifier,
        CraftQualityDivider = c.QualityDivider, CraftQualityModifier = c.QualityModifier,
        UnlockedManipulation = c.ManipulationUnlocked,
    };

    private static StepState LegacyStep(AdaptiveSearchState s) => new()
    {
        Index = s.FirstStep ? 1 : 2, Progress = s.Progress, Quality = s.Quality, Durability = s.Durability,
        RemainingCP = s.CP, Condition = s.Condition, IQStacks = s.InnerQuiet, WasteNotLeft = s.WasteNot,
        ManipulationLeft = s.Manipulation, GreatStridesLeft = s.GreatStrides, InnovationLeft = s.Innovation,
        VenerationLeft = s.Veneration, MuscleMemoryLeft = s.MuscleMemory, FinalAppraisalLeft = s.FinalAppraisal,
        TrainedPerfectionAvailable = s.TrainedPerfectionAvailable, TrainedPerfectionActive = s.TrainedPerfectionActive,
        PrevComboAction = s.Combo,
    };
}
