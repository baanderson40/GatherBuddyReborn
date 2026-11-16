using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSeededLiveTraceTests(ITestOutputHelper output)
{
    internal sealed record Fixture(AdaptiveCraftSnapshot Craft, JsonElement Goal, AdaptiveSearchLimits Limits,
        AdaptiveObservedFrame[] Frames, VulcanSkill[] Combos, VulcanSkill[][] Plans, int[] ModeledDurations,
        int[] SearchTransitions, int[] StoredNodes, double[] EngineElapsedMs, bool[] BudgetExhausted,
        string[] SeedSources, string[] SeedStatuses, string[] SeedComparisons, int[] SeedFinalQualities,
        double?[][] TimingSeconds, JsonElement[][] Transactions, AdaptiveLiveSeedStatus SeedStartup,
        AdaptiveNativeCraftEvent Terminal, JsonElement SourceSummary, Guid DecisionSessionId, Guid EvidenceSessionId)
    {
        public AdaptiveSimulation Simulation => new(Craft, AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality,
            Goal.GetProperty("DesiredQuality").GetInt32(), Goal.GetProperty("StrictTarget").GetBoolean()));
        public AdaptiveSearchState State(int i) => Frames[i].ToState(true, Combos[i]); // Explicit true availability in every supplied identity.
        public AdaptiveSeedCandidate Startup() => new(new(new("RaphaelCache", SeedStartup.RequestKey!, Craft.RecipeId,
            Craft.Level, Craft.Craftsmanship, Craft.Control, Craft.MaxCP, Craft.ManipulationUnlocked, false, 0), Plans[0]), 0, 0);
        public AdaptiveSearchResult Search(int i, AdaptiveSearchLimits? limits = null)
            => new AdaptiveSearchEngine().Search(Simulation, State(i), limits ?? Limits with { TimeLimit = null },
                i == 0 ? null : Plans[i - 1].Skip(1).ToArray(), seeds: i == 0 ? new[] { Startup() } : null);
    }
    internal static Fixture Load() => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "live-schema4-seeded-cache-success.json")))!;
    public static IEnumerable<object[]> Roots => Enumerable.Range(0, 17).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Roots))]
    public void AllPublishedRootPlansAndAlignedSeedsReplay(int index)
    {
        var f = Load();
        Assert.True(AdaptivePlanValidator.TryValidate(f.Simulation, f.State(index), f.Plans[index], out var published));
        Assert.Equal(12000, published!.FinalState.Quality);
        Assert.Equal(f.ModeledDurations[index], published.Duration);
        Assert.Equal(f.Plans[0].Skip(index), f.Plans[index]);
        var actions = index == 0 ? f.Startup().Actions : f.Plans[index - 1].Skip(1).ToArray();
        Assert.True(AdaptivePlanValidator.TryValidate(f.Simulation, f.State(index), actions, out var seed));
        Assert.NotNull(seed);
        Assert.False(AdaptivePlanValidator.IsBetter(seed, published));
        Assert.False(AdaptivePlanValidator.IsBetter(published, seed));
        Assert.Equal("Validated", f.SeedStatuses[index]);
        Assert.Equal("Tied", f.SeedComparisons[index]);
        Assert.Equal(index == 0 ? "RaphaelCache" : "Continuation", f.SeedSources[index]);
        Assert.Equal(12000, f.SeedFinalQualities[index]);
        var result = f.Search(index);
        Assert.NotNull(result.Plan);
        Assert.False(AdaptivePlanValidator.IsBetter(seed, result.Plan));
        output.WriteLine(JsonSerializer.Serialize(new { Root = index + 1, result.Plan.FinalState.Quality,
            result.Plan.Duration, Actions = result.Plan.Actions.Count, result.Plan.FinalState.CP, result.Plan.FinalState.Durability,
            result.Diagnostics.Transitions, result.Diagnostics.StoredNodes, result.Diagnostics.CompletionProbes }));
    }

    [Fact]
    public void SourceLinkageOutcomesAndTerminalRemainDistinctFromModeledFinalState()
    {
        var f = Load();
        Assert.NotEqual(f.DecisionSessionId, f.EvidenceSessionId);
        Assert.Equal(17, f.Frames.Length);
        for (var i = 0; i < 17; ++i)
        {
            var tx = f.Transactions[i];
            foreach (var column in new[] { 0, 1, 2, 5, 6 }) Assert.Equal(i + 1, tx[column].GetInt32());
            Assert.Equal(i * 2 + 1, tx[3].GetInt32());
            Assert.Equal(i, tx[4].GetInt32());
            Assert.Equal((int)f.Plans[i][0], tx[7].GetInt32());
            Assert.True(tx[9].GetBoolean());
            Assert.True(tx[10].GetBoolean());
            Assert.Equal(i < 16, tx[11].GetBoolean());
            Assert.Equal(i < 16 ? 1 : 2, tx[12].GetInt32());
            var times = f.TimingSeconds[i];
            for (var t = 1; t <= 5; ++t) Assert.True(times[t] >= times[t - 1]);
            if (i < 16)
            {
                Assert.True(times[6] > times[5]);
                Assert.Null(times[7]);
                Assert.Equal(times[6], f.TimingSeconds[i + 1][0]);
                Assert.Equal(AdaptiveOutcome.Nonterminal, f.Simulation.TryApply(f.State(i), f.Plans[i][0], out var next));
                Assert.Equal(f.State(i + 1), next with { Condition = f.State(i + 1).Condition });
            }
            else { Assert.Null(times[6]); Assert.Equal(times[5], times[7]); }
        }
        Assert.Equal(19003, f.Transactions[3][8].GetInt32()); // Captured CUL Waste Not II binding; not generic ID arithmetic.
        Assert.Equal(Condition.Good, f.Frames[6].Condition);
        Assert.Equal(525, f.Frames[7].Quality);
        Assert.Equal(6209, f.Frames[^1].Progress);
        Assert.Equal(11775, f.Frames[^1].Quality);
        Assert.Equal(86, f.Frames[^1].CP);
        Assert.Equal(6600, f.Terminal.Progress);
        Assert.Equal(12000, f.Terminal.Quality);
        Assert.Equal(5, f.Terminal.Durability);
        Assert.Equal(100330u, f.Terminal.ActionId);
        Assert.True(f.Terminal.Complete);
        Assert.Equal("Validated", f.SeedStartup.Phase);
        Assert.False(f.SeedStartup.GenerationRequested);
        Assert.Equal(0, f.SeedStartup.ExpectedInitialQuality);
        Assert.Equal(0, f.SeedStartup.ObservedInitialQuality);
        foreach (var name in new[] { "DroppedRoots", "TotalUnlinkedEvents", "DroppedLifecycleEvents", "CoordinatorEventsDropped", "RejectedResults", "PredictionMismatches", "HistoricalSearchesDispatched" })
            Assert.Equal(0, f.SourceSummary.GetProperty(name).GetInt32());
        Assert.Equal(17, f.SourceSummary.GetProperty("MatchedAcknowledgements").GetInt32());
        Assert.Equal(16, f.SourceSummary.GetProperty("CorrelatedActions").GetInt32());
        Assert.Equal(109, f.SourceSummary.GetProperty("DroppedEvents").GetInt32());
        Assert.False(f.SourceSummary.GetProperty("NativeLayoutValidated").GetBoolean());
        Assert.False(f.SourceSummary.GetProperty("PlansExecutable").GetBoolean());
    }
}
