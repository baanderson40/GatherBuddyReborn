using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

/// <summary>Native field evidence, not independent simulator parity or Adaptive execution.</summary>
public class AdaptiveNativeSeedTraceTests
{
    private sealed record Fixture(string Evidence, int SchemaVersion, bool ShadowOnly, bool NativeActionIdentityConfirmed,
        AdaptiveCraftSnapshot Craft, AdaptiveSeedProvenance Provenance, VulcanSkill[] Actions,
        AdaptiveObservedFrame[] Roots, string[] Comparisons, bool[] AdaptiveAligned,
        int ImprovedGoodRootIndex, Dictionary<int, VulcanSkill[]> RetainedPlanOverrides, JsonElement NativeSummary)
    {
        public AdaptiveCraftGoal Goal => AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality, 12000);
        public AdaptiveSimulation Simulation => new(Craft, Goal);
        public AdaptivePlanSeed Seed() => new(Provenance, Actions);
        public AdaptiveSearchState State(int index) => Roots[index].ToState(true, Actions[index]);
        public IReadOnlyList<VulcanSkill> RetainedActions(int index)
            => RetainedPlanOverrides.TryGetValue(index, out var actions) ? actions : new AdaptiveSeedCandidate(Seed(), index + 1, 0).Actions;
    }

    private static Fixture Load() => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "native-schema5-seeded-all-nq.json")),
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;

    public static IEnumerable<object[]> RootIndices => Enumerable.Range(0, 16).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(RootIndices))]
    public void NativeRootAcceptsExactCachedSuffixAndPreservesFullTargetUnderMinimalSearchBudget(int index)
    {
        var fixture = Load();
        var candidate = new AdaptiveSeedCandidate(fixture.Seed(), index + 1, 0);
        Assert.Equal(index + 2, fixture.Roots[index].StepIndex);
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), candidate.Actions, out var baseline));
        Assert.Equal(12000, baseline!.FinalState.Quality);
        var result = new AdaptiveSearchEngine().Search(fixture.Simulation, fixture.State(index),
            new() { MaximumTransitions = 1, TimeLimit = null }, seeds: new[] { candidate });
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.Equal(12000, result.Plan!.FinalState.Quality);
        Assert.False(AdaptivePlanValidator.IsBetter(baseline, result.Plan));
        Assert.Equal("Validated", Assert.Single(result.Diagnostics.Seeding!.Candidates).Status);
        Assert.NotEqual("BelowSeed", result.Diagnostics.Seeding.Comparison);
        Assert.InRange(result.Diagnostics.Transitions, 0, 1);

        // Replay the actual retained native-search route too; no timing/search-order assertion.
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), fixture.RetainedActions(index), out var retained));
        Assert.Equal(12000, retained!.FinalState.Quality);
        Assert.False(AdaptivePlanValidator.IsBetter(baseline, retained));
        // Final native result ties the better Adaptive continuation, not the Raphael suffix.
        var acceptedBaseline = baseline;
        if (index == 15)
        {
            Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index),
                fixture.RetainedActions(index - 1).Skip(1).ToArray(), out acceptedBaseline));
            Assert.True(AdaptivePlanValidator.IsBetter(acceptedBaseline!, baseline));
        }
        Assert.Equal(fixture.Comparisons[index] == "Improved", AdaptivePlanValidator.IsBetter(retained, acceptedBaseline));
    }

    [Theory]
    [InlineData(5, 450, 525)]
    [InlineData(9, 4710, 5047)]
    public void NativeGoodQualityMatchesEnhancedModelAndOrdinaryControlDiffers(int index, int ordinaryQuality, int enhancedQuality)
    {
        var fixture = Load();
        Assert.Equal(Condition.Good, fixture.State(index).Condition);
        Assert.Equal(AdaptiveOutcome.Nonterminal, fixture.Simulation.TryApply(fixture.State(index), fixture.Actions[index + 1], out var enhanced));
        Assert.Equal(fixture.State(index + 1), enhanced);
        Assert.Equal(enhancedQuality, enhanced.Quality);
        Assert.Equal(AdaptiveOutcome.Nonterminal, fixture.Simulation.CreateWorkspace().TryApply(fixture.State(index),
            fixture.Actions[index + 1], out var scratch));
        Assert.Equal(enhanced, scratch);
        var ordinaryCraft = fixture.Craft with { GoodQualityPercent = 150, Tool = null };
        var ordinary = new AdaptiveSimulation(ordinaryCraft, AdaptiveCraftGoal.Resolve(ordinaryCraft, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Nonterminal, ordinary.TryApply(fixture.State(index), fixture.Actions[index + 1], out var control));
        Assert.Equal(ordinaryQuality, control.Quality);
        Assert.Equal(enhanced with { Quality = ordinaryQuality }, control);
    }

    [Fact]
    public void ImprovedGoodPlanCannotAdvanceWhenExecutorUsesRecordedPreparatoryTouch()
    {
        var fixture = Load();
        var tracker = new AdaptiveSeedTracker(fixture.Seed(), "native cache hit");
        var index = fixture.ImprovedGoodRootIndex;
        foreach (var action in fixture.Actions.Take(index + 1)) tracker.Correlate(action);
        Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, fixture.State(index), fixture.RetainedActions(index), out var plan));
        Assert.Equal(VulcanSkill.PreciseTouch, plan!.Actions[0]);
        var source = new AdaptiveHistoricalObservation(new(Guid.NewGuid(), 19, 10, fixture.Craft,
            fixture.Goal, new(), fixture.State(index)), fixture.Roots[index], TimeSpan.Zero) { ActionOrdinal = index + 1 };
        tracker.Remember(source, plan);
        Assert.Equal(2, tracker.Capture(fixture.Craft, index + 1).Candidates.Count);
        tracker.Correlate(fixture.Actions[index + 1]); // Existing executor uses PreparatoryTouch instead.
        var next = tracker.Capture(fixture.Craft, index + 2);
        Assert.Equal("RaphaelCache", Assert.Single(next.Candidates).Seed.Provenance.Source);
        Assert.Equal("Unaligned", next.Availability[1].Status);
        Assert.Equal(index + 2, next.Candidates[0].Cursor);
    }

    // Feed frozen native result ACTIONS through the real host replay boundary, not fake
    // completed-state metadata. This scheduler does not reproduce or benchmark optimizer work.
    private sealed class RecordedScheduler(Fixture fixture) : IAdaptiveSearchScheduler
    {
        public List<AdaptiveSearchRequest> Requests { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            Assert.False(cancellation.IsCancellationRequested);
            var index = Requests.Count;
            Assert.Equal(fixture.State(index), request.Identity.State);
            Assert.Equal(fixture.AdaptiveAligned[index] ? 2 : 1, request.Seeds.Count);
            Assert.Equal(index + 1, request.Seeds[0].Cursor);
            Assert.Equal("RaphaelCache", request.Seeds[0].Seed.Provenance.Source);
            if (fixture.AdaptiveAligned[index]) Assert.Equal("AdaptiveContinuation", request.Seeds[1].Seed.Provenance.Source);
            Requests.Add(request);
            Assert.True(AdaptivePlanValidator.TryValidate(fixture.Simulation, request.Identity.State,
                fixture.RetainedActions(index), out var plan));
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found, plan,
                new(0, 0, 0, TimeSpan.Zero, false), "Recorded native result actions replayed by test scheduler."));
        }
    }

    [Fact]
    public void NativeSequenceCorrelatesAllSixteenOutcomesButEndCannotInventFinalCompletion()
    {
        var fixture = Load();
        var scheduler = new RecordedScheduler(fixture);
        var initial = new AdaptiveObservedFrame(1, 0, 0, 80, 664, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(fixture.Craft, initial, TimeSpan.Zero,
            scheduler: scheduler, seed: fixture.Seed());
        long frame = 0;
        var now = TimeSpan.Zero;
        var before = initial;
        for (var index = 0; index < fixture.Roots.Length; ++index)
        {
            var ticket = shadow.RecordRequest(before, fixture.Actions[index], now);
            Assert.NotEqual(default, ticket);
            Assert.True(shadow.ReportRequest(ticket, true));
            var after = fixture.Roots[index];
            shadow.Observe(++frame, now += TimeSpan.FromMilliseconds(10), after, false);
            Assert.Equal(index, shadow.CorrelatedActions);
            shadow.Observe(++frame, now += TimeSpan.FromMilliseconds(10), after, false);
            Assert.False(shadow.Blocked);
            Assert.Equal(index + 1, shadow.CorrelatedActions);
            before = after;
        }
        Assert.Equal(16, scheduler.Requests.Count);
        Assert.Equal(11, scheduler.Requests.Count(request => request.Seeds.Count == 2));
        var finalRequest = shadow.RecordRequest(before, fixture.Actions[^1], now);
        Assert.True(shadow.ReportRequest(finalRequest, true));
        // Trace has no stable terminal pair: addon disappears before confirmation.
        shadow.Observe(++frame, now += TimeSpan.FromMilliseconds(10), null, false);
        shadow.End("Managed craft finished.", now += TimeSpan.FromMilliseconds(10), managedCraftEnded: true);
        var trace = shadow.CaptureTrace();
        Assert.Equal(16, trace.Summary.CorrelatedActions);
        Assert.Null(trace.Summary.BlockedReason);
        Assert.Null(trace.Summary.LastMismatch);
        Assert.Equal("Unobserved", trace.Summary.TerminalObservation);
        Assert.Equal(fixture.Roots[^1], trace.Summary.LastStableFrame);
        Assert.True(trace.Summary.ManagedCraftEnded);
        Assert.Equal(16, trace.Summary.HistoricalAnalysis.ValidatedPlans);
        Assert.Equal(16, trace.Summary.HistoricalAnalysis.DesiredQualityPlans);
        Assert.Equal(0, trace.Summary.HistoricalAnalysis.ObservationsSkipped);
        Assert.Equal(0, trace.Summary.HistoricalAnalysis.SearchCancellationRequests);
        Assert.Equal(0, trace.Summary.HistoricalAnalysis.RevokedResultsDiscarded);
        Assert.Equal(16, trace.HistoricalResults.Count);
        Assert.All(trace.HistoricalResults, result => { Assert.False(result.Executable); Assert.Equal("Validated", result.ReplayValidation); });
        Assert.Equal(64, trace.Events.Count);
        Assert.True(trace.Summary.DroppedEvents > 0);
        using var exported = JsonDocument.Parse(AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(shadow.Status, trace)));
        Assert.Equal(6, exported.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(exported.RootElement.GetProperty("ShadowOnly").GetBoolean());
        Assert.False(exported.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
        Assert.Equal("Unobserved", exported.RootElement.GetProperty("Data").GetProperty("Summary").GetProperty("TerminalObservation").GetString());
    }

    [Fact]
    public void FixtureRetainsNativeScopeToolEvidenceAndOutcomeLimitations()
    {
        var fixture = Load();
        Assert.Contains("Compact projection", fixture.Evidence);
        Assert.Equal(5, fixture.SchemaVersion);
        Assert.True(fixture.ShadowOnly);
        Assert.False(fixture.NativeActionIdentityConfirmed);
        Assert.Equal(16, fixture.Roots.Length);
        Assert.Equal(17, fixture.Actions.Length);
        Assert.Equal(11, fixture.Comparisons.Count(comparison => comparison == "Tied"));
        Assert.Equal(5, fixture.Comparisons.Count(comparison => comparison == "Improved"));
        Assert.Equal("5618/100/5953/5490/664/1/0/0", fixture.Provenance.RequestKey);
        var tool = fixture.Craft.Tool!;
        Assert.True(AdaptiveToolBonus.TryResolve(tool.MainHandItemId, tool.JobId, tool.JobId, true, 0, 0, 0,
            tool.EffectDescription, out var resolved, out _));
        Assert.Equal(tool, resolved);
        Assert.Equal(175, fixture.Craft.GoodQualityPercent);
        Assert.Equal(0, fixture.Roots[0].Quality);
        Assert.Equal(12000, fixture.Roots[^1].Quality);
        Assert.True(fixture.Roots[^1].Progress < fixture.Craft.RequiredProgress);
        Assert.Equal("Unobserved", fixture.NativeSummary.GetProperty("TerminalObservation").GetString());
        Assert.Equal(16, fixture.NativeSummary.GetProperty("DesiredQualityPlans").GetInt32());
        Assert.Equal(136, fixture.NativeSummary.GetProperty("DroppedEvents").GetInt32());
    }
}
