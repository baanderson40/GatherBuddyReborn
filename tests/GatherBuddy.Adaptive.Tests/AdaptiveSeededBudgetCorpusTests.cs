using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSeededBudgetCorpusTests(ITestOutputHelper output)
{
    private sealed record Macros(Macro[] Runs);
    private sealed record Macro(string Key, VulcanSkill[] Actions);
    private sealed record Run(bool Completed, int Quality, int Duration, int Actions, int CP, int Durability,
        bool TrainedPerfectionAvailable, long Transitions, long Bytes, int QualifiedDecisions, double ElapsedMs);

    [Fact]
    [Trait("Category", "Benchmark")]
    public void BroaderFixedAndFormerHoldoutScenariosCompareFullAndReducedWork()
    {
        var fresh = JsonSerializer.Deserialize<Macros>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "raphael-three-way-macros.json")))!.Runs.ToDictionary(m => m.Key);
        foreach (var recipe in ReferenceCorpus.Loaded.Recipes)
        foreach (var conditionSeed in new[] { 0, 2026 }) Compare(recipe, recipe.InitialQuality, 0, conditionSeed, "Fixed");
        foreach (var recipe in ReferenceCorpus.Loaded.Recipes.Where(r => r.Craft.Level == r.Craft.RecipeLevel
                     && r.Craft.RecipeId is 34890 or 35907 or 5618))
        foreach (var quality in new[] { 0, recipe.Craft.MaxQuality / 4 })
        foreach (var cpReduction in new[] { 0, 20 })
        foreach (var conditionSeed in new[] { 101, 7379 }) Compare(recipe, quality, cpReduction, conditionSeed, "FormerHoldout");

        void Compare(ReferenceCorpus.Recipe recipe, int initialQuality, int cpReduction, int conditionSeed, string dataset)
        {
            var actions = fresh.TryGetValue(recipe.Key, out var macro) ? macro.Actions : recipe.RaphaelActions;
            Assert.NotEmpty(actions);
            var craft = recipe.Craft with { MaxCP = recipe.Craft.MaxCP - cpReduction };
            var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
            // Never relabel a frozen macro as having been solved with reduced CP. Match the
            // same startup provenance gate used by the host; otherwise start unseeded.
            var candidate = new AdaptivePlanSeed(new("FrozenRaphael", recipe.Key, recipe.Craft.RecipeId,
                recipe.Craft.Level, recipe.Craft.Craftsmanship, recipe.Craft.Control, recipe.Craft.MaxCP,
                recipe.Craft.ManipulationUnlocked, false, recipe.InitialQuality), actions);
            var startup = AdaptiveSeedPreparation.MatchesCraft(candidate, craft) ? new AdaptiveSeedCandidate(candidate, 0, 0) : null;
            var full = Execute(null);
            var reduced = Execute(16000);
            var reduced24 = Execute(24000);
            Assert.True(full.Completed);
            Assert.True(reduced.Completed);
            Assert.True(reduced24.Completed);
            Assert.True(full.Quality >= craft.RequiredQuality);
            Assert.True(reduced.Quality >= craft.RequiredQuality);
            Assert.True(reduced24.Quality >= craft.RequiredQuality);
            output.WriteLine(JsonSerializer.Serialize(new { Dataset = dataset, recipe.Key, InitialQuality = initialQuality,
                CpReduction = cpReduction, ConditionSeed = conditionSeed, Target = recipe.TargetQuality, StartupCandidate = startup != null,
                Full = full, Reduced16k = reduced, Reduced24k = reduced24 }));

            Run Execute(int? allowance)
            {
                var state = ReferenceCorpus.Initial(recipe, initialQuality) with { CP = craft.MaxCP };
                var random = new Random(conditionSeed);
                IReadOnlyList<VulcanSkill>? continuation = null;
                var count = 0;
                var duration = 0;
                long work = 0, bytes = 0;
                var qualified = 0;
                var elapsedMs = 0.0;
                for (var turn = 0; turn < 128; ++turn)
                {
                    var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                    var result = new AdaptiveSearchEngine().Search(simulation, state,
                        new() { TimeLimit = null, SeededImprovementTransitions = allowance }, continuation,
                        seeds: turn == 0 && startup != null ? new[] { startup } : null);
                    bytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    work += result.Diagnostics.Transitions;
                    elapsedMs += result.Diagnostics.Elapsed.TotalMilliseconds;
                    if (result.Diagnostics.SeededInputEligible) ++qualified;
                    if (turn == 0 && startup == null)
                    {
                        Assert.False(result.Diagnostics.SeededInputEligible);
                        Assert.Equal(40000, result.Diagnostics.EffectiveMaximumTransitions);
                    }
                    Assert.NotNull(result.Plan);
                    Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out _));
                    IReadOnlyList<VulcanSkill>? input = turn == 0 ? startup?.Actions : continuation;
                    if (input != null && AdaptivePlanValidator.TryValidate(simulation, state, input, out var floor))
                        Assert.False(AdaptivePlanValidator.IsBetter(floor!, result.Plan));
                    var action = result.Plan.Actions[0];
                    var previous = state.Condition;
                    var outcome = simulation.TryApply(state, action, out state);
                    Assert.True(outcome is AdaptiveOutcome.Nonterminal or AdaptiveOutcome.Success);
                    ++count;
                    duration += AdaptiveSimulation.ActionDuration(action);
                    if (outcome == AdaptiveOutcome.Success)
                        return new(true, state.Quality, duration, count, state.CP, state.Durability,
                            state.TrainedPerfectionAvailable, work, bytes, qualified, elapsedMs);
                    continuation = result.Plan.Actions.Skip(1).ToArray();
                    if (conditionSeed != 0 && !Simulator.SkipUpdates(action))
                    {
                        var roll = random.NextDouble();
                        state = state with { Condition = previous switch
                        {
                            Condition.Excellent => Condition.Poor,
                            Condition.Good or Condition.Poor => Condition.Normal,
                            _ => roll < 0.04 ? Condition.Excellent : roll < (craft.Level >= 63 ? 0.29 : 0.24) ? Condition.Good : Condition.Normal,
                        } };
                    }
                }
                return new(false, state.Quality, duration, count, state.CP, state.Durability,
                    state.TrainedPerfectionAvailable, work, bytes, qualified, elapsedMs);
            }
        }
    }
}
