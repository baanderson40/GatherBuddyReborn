using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSeededBudgetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(8000)]
    [InlineData(16000)]
    public void NoSeedOrRejectedOrSubtargetSeedCannotActivateAllowance(int allowance)
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 200 }, 500);
        var initial = AdaptiveCoreTests.Initial();
        var full = new AdaptiveSearchLimits { TimeLimit = null, MaximumTransitions = 2000 };
        var reduced = full with { SeededImprovementTransitions = allowance };
        foreach (VulcanSkill[]? seed in new VulcanSkill[]?[] { null,
                     [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis],
                     [VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis] })
        {
            var a = new AdaptiveSearchEngine().Search(simulation, initial, full, seed);
            var b = new AdaptiveSearchEngine().Search(simulation, initial, reduced, seed);
            Assert.False(b.Diagnostics.SeededInputEligible);
            Assert.Equal(full.MaximumTransitions, b.Diagnostics.EffectiveMaximumTransitions);
            Assert.Equal(a.Diagnostics.Transitions, b.Diagnostics.Transitions);
            Assert.Equal(a.Plan?.Actions, b.Plan?.Actions);
        }
    }

    [Fact]
    public void TargetInputSeedEnablesSharedAllowanceWithoutCallingItHardBudgetExhaustion()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 200 }, 0);
        VulcanSkill[] seed = [VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis];
        var result = new AdaptiveSearchEngine().Search(simulation, AdaptiveCoreTests.Initial(),
            new() { TimeLimit = null, SeededImprovementTransitions = 1 }, seed);
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.Equal(seed, result.Plan!.Actions);
        Assert.True(result.Diagnostics.SeededInputEligible);
        Assert.Equal(1, result.Diagnostics.Transitions);
        Assert.Equal(1, result.Diagnostics.EffectiveMaximumTransitions);
        Assert.False(result.Diagnostics.BudgetExhausted);
        Assert.Equal(AdaptiveSearchTermination.SeededImprovementLimit, result.Diagnostics.Termination);
        Assert.Equal("SeededImprovementLimit", result.Diagnostics.TerminationReason);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, AdaptiveCoreTests.Initial(), result.Plan.Actions, out _));
    }

    [Fact]
    public void RequestedAllowanceNeverRaisesHardLimitsAndReplayStillPrecedesPolicy()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 200 }, 0);
        var result = new AdaptiveSearchEngine().Search(simulation, AdaptiveCoreTests.Initial(),
            new() { TimeLimit = null, MaximumTransitions = 1, SeededImprovementTransitions = 8000 },
            [VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis]);
        Assert.Equal(1, result.Diagnostics.EffectiveMaximumTransitions);
        Assert.True(result.Diagnostics.BudgetExhausted);
        Assert.Equal(AdaptiveSearchTermination.TransitionLimit, result.Diagnostics.Termination);
        Assert.Equal("Validated", Assert.Single(result.Diagnostics.Seeding!.Candidates).Status);
    }

    [Fact]
    public void TerminationDistinguishesNodeTimeCancellationAndNaturalCompletion()
    {
        var limits = new AdaptiveSearchLimits { TimeLimit = null, MaximumStoredNodes = 1 };
        var budget = new AdaptiveSearchBudget(limits, default);
        Assert.True(budget.TryStore());
        Assert.Equal(AdaptiveSearchTermination.StoredNodeLimit, budget.Diagnostics.Termination);
        budget = new(limits with { TimeLimit = TimeSpan.FromTicks(1) }, default);
        // A fast Release read can share the stopwatch's initial timestamp. Wait
        // boundedly for the actual deadline instead of assuming time already advanced.
        Assert.True(SpinWait.SpinUntil(() => budget.Stopped, TimeSpan.FromSeconds(1)));
        Assert.Equal(AdaptiveSearchTermination.TimeLimit, budget.Diagnostics.Termination);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        budget = new(limits, cancelled.Token);
        Assert.Equal(AdaptiveSearchTermination.Cancelled, budget.Diagnostics.Termination);
        budget = new(new() { TimeLimit = null }, default);
        Assert.Equal(AdaptiveSearchTermination.Completed, budget.Diagnostics.Termination);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2000001)]
    public void InvalidAllowanceRejectedBeforeSearch(int allowance)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveSearchEngine().Search(
            AdaptiveCoreTests.Simulation(), AdaptiveCoreTests.Initial(), new() { SeededImprovementTransitions = allowance }));

    [Fact]
    public void PolicyParticipatesInImmutableDecisionIdentity()
    {
        var f = AdaptiveSeededLiveTraceTests.Load();
        var identity = new AdaptiveDecisionIdentity(Guid.NewGuid(), 1, 1, f.Craft, f.Simulation.Goal, f.Limits, f.State(0));
        Assert.NotEqual(identity, identity with { Limits = identity.Limits with { SeededImprovementTransitions = 8000 } });
        Assert.NotEqual(identity, identity with { Limits = identity.Limits with { UseProgressCompletionBound = false } });
    }

    [Fact]
    public void LiveDefaultKeepsFullAllowanceAfterReducedPoliciesLoseUsefulImprovements()
    {
        var f = AdaptiveSeededLiveTraceTests.Load();
        using var coordinator = new AdaptiveSolveCoordinator();
        using var trial = new AdaptiveLiveTrial(coordinator, f.Craft, f.Simulation.Goal, f.Frames[0],
            new(1, f.Craft.RecipeId, 0, 3), TimeSpan.Zero, TimeSpan.Zero, f.Startup().Seed);
        trial.Tick(1, TimeSpan.FromMilliseconds(10), f.Frames[0], false, false);
        trial.Tick(2, TimeSpan.FromMilliseconds(20), f.Frames[0], false, false);
        Assert.NotNull(trial.Decision.Decision);
        Assert.Null(trial.Decision.Decision.Limits.SeededImprovementTransitions);
        Assert.Equal(40000, trial.Decision.Decision.Limits.MaximumTransitions);
        Assert.Equal(20000, trial.Decision.Decision.Limits.MaximumStoredNodes);
        Assert.Equal(TimeSpan.FromMilliseconds(100), trial.Decision.Decision.Limits.TimeLimit);
        Assert.Equal("Validated", trial.StartupSeedStatus.Phase);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void CapturedRootsCompareBoundsAndAllowancesWithoutLosingValidatedFloor()
    {
        var f = AdaptiveSeededLiveTraceTests.Load();
        foreach (var policy in Policies())
        for (var i = 0; i < f.Frames.Length; ++i)
        {
            var limits = f.Limits with { TimeLimit = null, UseProgressCompletionBound = policy.Bounds,
                SeededImprovementTransitions = policy.Allowance };
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var result = f.Search(i, limits);
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.NotNull(result.Plan);
            Assert.True(AdaptivePlanValidator.TryValidate(f.Simulation, f.State(i), f.Plans[i], out var baseline));
            Assert.False(AdaptivePlanValidator.IsBetter(baseline!, result.Plan));
            Assert.True(AdaptivePlanValidator.TryValidate(f.Simulation, f.State(i), result.Plan.Actions, out _));
            Assert.True(result.Diagnostics.SeededInputEligible);
            Assert.InRange(result.Diagnostics.Transitions, 0, policy.Allowance ?? 40000);
            if (!policy.Bounds) Assert.Equal(f.SearchTransitions[i], result.Diagnostics.Transitions);
            output.WriteLine(JsonSerializer.Serialize(new { Dataset = "CapturedRoots", Policy = policy.Name, Root = i + 1,
                result.Plan.FinalState.Quality, result.Plan.Duration, Actions = result.Plan.Actions.Count,
                result.Plan.FinalState.CP, result.Plan.FinalState.Durability, result.Plan.FinalState.TrainedPerfectionAvailable,
                result.Diagnostics.Transitions, result.Diagnostics.StoredNodes, result.Diagnostics.CompletionProbes,
                result.Diagnostics.ProvenBoundPrunes, result.Diagnostics.TerminationReason, Bytes = bytes,
                ElapsedMs = result.Diagnostics.Elapsed.TotalMilliseconds }));
        }
    }

    private sealed record Policy(string Name, bool Bounds, int? Allowance);
    private static Policy[] Policies() => [new("FullWeak", false, null), new("FullProgress", true, null),
        new("Seed8k", true, 8000), new("Seed16k", true, 16000), new("Seed24k", true, 24000)];
    private sealed record MacroFixture(Macro[] Runs);
    private sealed record Macro(string Key, VulcanSkill[] Actions);

    [Theory]
    [InlineData("Normal")]
    [InlineData("GoodAt7")]
    [InlineData("ExcellentAt10")]
    [Trait("Category", "Benchmark")]
    public void SeededCraftsCompareIdenticalStepConditionScenarios(string scenario)
    {
        var macros = JsonSerializer.Deserialize<MacroFixture>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "raphael-three-way-macros.json")))!;
        foreach (var macro in macros.Runs)
        foreach (var policy in Policies())
        {
            var recipe = ReferenceCorpus.Loaded.Recipes.Single(r => r.Key == macro.Key);
            var simulation = new AdaptiveSimulation(recipe.Craft, AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
            var state = ReferenceCorpus.Initial(recipe);
            var initialSeed = new AdaptiveSeedCandidate(new(new("FreshRaphaelCli", macro.Key, recipe.Craft.RecipeId,
                recipe.Craft.Level, recipe.Craft.Craftsmanship, recipe.Craft.Control, recipe.Craft.MaxCP,
                recipe.Craft.ManipulationUnlocked, false, recipe.InitialQuality), macro.Actions), 0, 0);
            var limits = new AdaptiveSearchLimits { TimeLimit = null, UseProgressCompletionBound = policy.Bounds,
                SeededImprovementTransitions = policy.Allowance };
            IReadOnlyList<VulcanSkill>? continuation = null;
            var actions = new List<VulcanSkill>();
            long transitions = 0, nodes = 0, probes = 0, prunes = 0, bytes = 0;
            var duration = 0;
            var step = 1;
            var qualified = 0;
            var elapsedMs = 0.0;
            var completed = false;
            for (var turn = 0; turn < 128; ++turn)
            {
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var result = new AdaptiveSearchEngine().Search(simulation, state, limits, continuation,
                    seeds: turn == 0 ? new[] { initialSeed } : null);
                bytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                transitions += result.Diagnostics.Transitions;
                nodes += result.Diagnostics.StoredNodes;
                probes += result.Diagnostics.CompletionProbes;
                prunes += result.Diagnostics.ProvenBoundPrunes;
                elapsedMs += result.Diagnostics.Elapsed.TotalMilliseconds;
                if (result.Diagnostics.SeededInputEligible) ++qualified;
                Assert.NotNull(result.Plan);
                Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out _));
                var input = turn == 0 ? initialSeed.Actions : continuation!;
                if (AdaptivePlanValidator.TryValidate(simulation, state, input, out var floor))
                    Assert.False(AdaptivePlanValidator.IsBetter(floor!, result.Plan));
                Assert.InRange(result.Diagnostics.Transitions, 0, result.Diagnostics.EffectiveMaximumTransitions);
                var action = result.Plan.Actions[0];
                var previous = state.Condition;
                var outcome = simulation.TryApply(state, action, out state);
                Assert.True(outcome is AdaptiveOutcome.Nonterminal or AdaptiveOutcome.Success);
                actions.Add(action);
                duration += AdaptiveSimulation.ActionDuration(action);
                if (outcome == AdaptiveOutcome.Success) { completed = true; break; }
                continuation = result.Plan.Actions.Skip(1).ToArray();
                if (!Simulator.SkipUpdates(action))
                {
                    ++step;
                    state = state with { Condition = previous switch
                    {
                        Condition.Excellent => Condition.Poor,
                        Condition.Good or Condition.Poor => Condition.Normal,
                        _ => scenario == "GoodAt7" && step == 7 ? Condition.Good
                            : scenario == "ExcellentAt10" && step == 10 ? Condition.Excellent : Condition.Normal,
                    } };
                }
            }
            Assert.True(completed);
            output.WriteLine(JsonSerializer.Serialize(new { Dataset = "SeededCrafts", recipe.Key, Scenario = scenario, Policy = policy.Name,
                Completed = completed, state.Quality, Target = recipe.TargetQuality, Duration = duration, Actions = actions.Count,
                state.CP, state.Durability, state.TrainedPerfectionAvailable, Transitions = transitions, StoredNodes = nodes,
                Probes = probes, Prunes = prunes, QualifiedDecisions = qualified, Bytes = bytes, ElapsedMs = elapsedMs,
                Route = actions.Select(a => a.ToString()).ToArray() }));
        }
    }
}
