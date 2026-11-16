using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveLiveSearchRegressionTests(ITestOutputHelper output)
{
    internal sealed record Fixture(AdaptiveCraftSnapshot Craft, JsonElement Goal, AdaptiveSearchLimits Limits,
        AdaptiveObservedFrame[] Frames, VulcanSkill[] Combos, bool[] TrainedPerfectionAvailable,
        VulcanSkill[][] Plans, int[] FinalQualities, int[] ModeledDurations, string?[] SeedComparisons, JsonElement[]?[] SeedAssessments,
        int[] SearchTransitions, int[] StoredNodes, double[] EngineElapsedMs, double?[][] TimingSeconds,
        JsonElement[][] Transactions, AdaptiveNativeCraftEvent Terminal, JsonElement SourceSummary,
        Guid DecisionSessionId, Guid EvidenceSessionId)
    {
        public AdaptiveSimulation Simulation => new(Craft, AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality,
            Goal.GetProperty("DesiredQuality").GetInt32(), Goal.GetProperty("StrictTarget").GetBoolean()));
        public AdaptiveSearchState State(int index) => Frames[index].ToState(TrainedPerfectionAvailable[index], Combos[index]);
    }

    internal static Fixture Load() => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "live-schema4-root-history-success.json")))!;

    public static IEnumerable<object[]> Roots => Enumerable.Range(0, 16).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Roots))]
    public void PublishedProposalAndExactRootContinuationReplay(int index)
    {
        var fixture = Load();
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), fixture.Plans[index], out var plan));
        Assert.Equal(fixture.FinalQualities[index], plan!.FinalState.Quality);
        Assert.Equal(fixture.ModeledDurations[index], plan.Duration);
        if (index == 0) return;
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index),
            fixture.Plans[index - 1].Skip(1).ToArray(), out var seed));
        Assert.False(AdaptivePlanValidator.IsBetter(seed!, plan));
        Assert.Equal(fixture.SeedComparisons[index] == "Improved", AdaptivePlanValidator.IsBetter(plan, seed));
        var assessment = fixture.SeedAssessments[index]!;
        Assert.Equal("Continuation", assessment[0].GetString());
        Assert.Equal("Validated", assessment[1].GetString());
        Assert.Equal(seed!.FinalState.Quality, assessment[2].GetInt32());
        Assert.Equal(seed.TargetReached, assessment[3].GetBoolean());
        Assert.Equal(seed.Actions.Count, assessment[4].GetInt32());
        Assert.Equal(seed.Duration, assessment[5].GetInt32());
        Assert.Equal(seed.Actions.Count, assessment[6].GetInt32());
        if (index == 5)
        {
            // Both routes take 13 actions/35 seconds. Establish the actual tie-break, not a guess.
            Assert.Equal(12, seed.FinalState.CP);
            Assert.Equal(20, plan.FinalState.CP);
            Assert.Equal(0, seed.FinalState.Durability);
            Assert.Equal(30, plan.FinalState.Durability);
            output.WriteLine($"root6 seed={seed!.FinalState}; published={plan.FinalState}");
        }
    }

    [Fact]
    public void CapturedBloatedRouteIsLegalAndQualityFirstComparisonIsUnchanged()
    {
        var fixture = Load();
        var state = fixture.State(1);
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, state, fixture.Plans[0].Skip(1).ToArray(), out var continuation));
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, state, fixture.Plans[1], out var published));
        Assert.Equal(13, fixture.Plans[1].Count(a => a == VulcanSkill.FinalAppraisal));
        Assert.Equal(11400, continuation!.FinalState.Quality);
        Assert.Equal(11430, published!.FinalState.Quality);
        Assert.Equal(50, continuation.Duration);
        Assert.Equal(81, published.Duration);
        Assert.True(AdaptivePlanValidator.IsBetter(published, continuation));
        var trimmed = new List<VulcanSkill>();
        foreach (var action in fixture.Plans[1])
        {
            var outcome = fixture.Simulation.TryApply(state, action, out var next);
            Assert.True(outcome is AdaptiveOutcome.Nonterminal or AdaptiveOutcome.Success);
            if (AdaptiveSearchPruning.RedundantRefresh(state, action, next)) continue;
            trimmed.Add(action);
            state = next;
        }
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(1), trimmed, out var shorter));
        Assert.Equal(published.FinalState.Quality, shorter!.FinalState.Quality);
        Assert.True(AdaptivePlanValidator.IsBetter(shorter, published));
        Assert.True(shorter.Duration < published.Duration);
    }

    [Theory]
    [MemberData(nameof(Roots))]
    public void FreshBoundedSearchNeverWorseThanValidatedContinuation(int index)
    {
        var fixture = Load();
        var seed = index == 0 ? null : fixture.Plans[index - 1].Skip(1).ToArray();
        var result = new AdaptiveSearchEngine().Search(fixture.Simulation, fixture.State(index),
            fixture.Limits with { TimeLimit = null }, seed);
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), result.Plan!.Actions, out var replay));
        Assert.Equal(replay!.FinalState, result.Plan.FinalState);
        if (index <= 1) Assert.Equal(12000, result.Plan.FinalState.Quality);
        if (seed != null)
        {
            Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), seed, out var baseline));
            Assert.False(AdaptivePlanValidator.IsBetter(baseline!, result.Plan));
        }
        Assert.InRange(result.Diagnostics.Transitions, 0, fixture.Limits.MaximumTransitions);
        Assert.InRange(result.Diagnostics.StoredNodes, 0, fixture.Limits.MaximumStoredNodes);
        if (index is >= 11 and <= 14) Assert.True(result.Diagnostics.Transitions < fixture.SearchTransitions[index]);
        output.WriteLine($"root={index + 1} quality={result.Plan.FinalState.Quality} duration={result.Plan.Duration} "
            + $"actions={result.Plan.Actions.Count} CP={result.Plan.FinalState.CP} durability={result.Plan.FinalState.Durability} "
            + $"perfection={result.Plan.FinalState.TrainedPerfectionAvailable} transitions={result.Diagnostics.Transitions} "
            + $"nodes={result.Diagnostics.StoredNodes} probes={result.Diagnostics.CompletionProbes} exhausted={result.Diagnostics.BudgetExhausted}");
    }

    [Fact]
    public void CapturedTransactionsAndTimingRetainTerminalBoundary()
    {
        var fixture = Load();
        Assert.NotEqual(fixture.DecisionSessionId, fixture.EvidenceSessionId);
        Assert.Equal(16, fixture.Frames.Length);
        Assert.Equal(16, fixture.Transactions.Length);
        for (var i = 0; i < 16; ++i)
        {
            var tx = fixture.Transactions[i];
            foreach (var column in new[] { 0, 1, 2, 5, 6 }) Assert.Equal(i + 1, tx[column].GetInt32());
            Assert.Equal(i * 2 + 1, tx[3].GetInt32());
            Assert.Equal(i, tx[4].GetInt32());
            Assert.Equal((int)fixture.Plans[i][0], tx[7].GetInt32());
            Assert.Equal(tx[7].GetInt32() + 7, tx[8].GetInt32()); // Captured CUL bindings, not a general mapping.
            Assert.True(tx[9].GetBoolean());
            Assert.True(tx[10].GetBoolean());
            Assert.Equal(i < 15, tx[11].GetBoolean());
            Assert.Equal(i < 15 ? 1 : 2, tx[12].GetInt32());
            var times = fixture.TimingSeconds[i];
            for (var j = 1; j <= 5; ++j) Assert.True(times[j] >= times[j - 1]);
            if (i < 15)
            {
                Assert.True(times[6] > times[5]);
                Assert.Equal(times[6], fixture.TimingSeconds[i + 1][0]);
                Assert.Null(times[7]);
                Assert.Equal(AdaptiveOutcome.Nonterminal, fixture.Simulation.TryApply(fixture.State(i), fixture.Plans[i][0], out var next));
                // The next condition is observed, not predicted; compare every other state field.
                Assert.Equal(fixture.State(i + 1), next with { Condition = fixture.State(i + 1).Condition });
            }
            else
            {
                Assert.Null(times[6]);
                Assert.Equal(times[5], times[7]);
            }
        }
        Assert.Equal(5118, fixture.Frames[^1].Progress);
        Assert.Equal(80, fixture.Frames[^1].CP);
        Assert.Equal(6600, fixture.Terminal.Progress);
        Assert.Equal(12000, fixture.Terminal.Quality);
        Assert.Equal(0, fixture.Terminal.Durability);
        Assert.Equal(100410u, fixture.Terminal.ActionId);
        Assert.True(fixture.Terminal.Complete);
        var summary = fixture.SourceSummary;
        foreach (var name in new[] { "DroppedRoots", "TotalUnlinkedEvents", "DroppedLifecycleEvents", "CoordinatorEventsDropped", "RejectedResults", "PredictionMismatches", "HistoricalSearchesDispatched" })
            Assert.Equal(0, summary.GetProperty(name).GetInt32());
        Assert.Equal(99, summary.GetProperty("DroppedEvents").GetInt32());
        Assert.Equal(16, summary.GetProperty("MatchedAcknowledgements").GetInt32());
        Assert.Equal(15, summary.GetProperty("CorrelatedActions").GetInt32());
        Assert.True(summary.GetProperty("ManagedCraftEnded").GetBoolean());
        Assert.False(summary.GetProperty("NativeLayoutValidated").GetBoolean());
        Assert.False(summary.GetProperty("EvidenceExecutionAuthorized").GetBoolean());
        Assert.False(summary.GetProperty("PlansExecutable").GetBoolean());
    }
}
