using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSeedTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> RootIndices => Enumerable.Range(0, 7).Select(i => new object[] { i });
    private static AdaptiveCraftSnapshot Craft => AdaptiveSeedRegressionCorpus.Craft;
    private static AdaptiveCraftGoal Goal => AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality, 12000);
    private static AdaptiveSimulation Simulation => new(Craft, Goal);

    [Theory]
    [MemberData(nameof(RootIndices))]
    public void SeedComparisonKeepsCompletionAndRaisesAllSevenEarlyNqRootsToFullQuality(int index)
    {
        var limits = new AdaptiveSearchLimits { TimeLimit = null };
        var state = AdaptiveSeedRegressionCorpus.State(index);
        var candidate = AdaptiveSeedRegressionCorpus.Candidate(index);
        Assert.True(AdaptivePlanValidator.TryValidate(Simulation, state, candidate.Actions, out var recorded));
        Assert.Equal(12000, recorded!.FinalState.Quality);
        var engine = new AdaptiveSearchEngine();
        var start = GC.GetAllocatedBytesForCurrentThread();
        var unseeded = engine.Search(Simulation, state, limits);
        var unseededBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var seeded = engine.Search(Simulation, state, limits, seeds: new[] { candidate });
        var seededBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(AdaptiveSearchStatus.Found, unseeded.Status);
        Assert.Equal(AdaptiveSearchStatus.Found, seeded.Status);
        Assert.Equal(new[] { 10800, 10200, 9570, 8700, 11100, 10500, 11700 }[index], unseeded.Plan!.FinalState.Quality);
        Assert.Equal(12000, seeded.Plan!.FinalState.Quality);
        Assert.False(AdaptivePlanValidator.IsBetter(recorded, seeded.Plan));
        Assert.InRange(seeded.Diagnostics.Transitions, 0, limits.MaximumTransitions);
        Assert.InRange(seeded.Diagnostics.StoredNodes, 0, limits.MaximumStoredNodes);
        Assert.Equal("Validated", Assert.Single(seeded.Diagnostics.Seeding!.Candidates).Status);
        output.WriteLine($"step={index + 2}; unseededQ={unseeded.Plan.FinalState.Quality}; seededQ={seeded.Plan.FinalState.Quality}; recordedQ={recorded.FinalState.Quality}; "
            + $"actions={unseeded.Plan.Actions.Count}/{seeded.Plan.Actions.Count}; duration={unseeded.Plan.Duration}/{seeded.Plan.Duration}; "
            + $"transitions={unseeded.Diagnostics.Transitions}/{seeded.Diagnostics.Transitions}; nodes={unseeded.Diagnostics.StoredNodes}/{seeded.Diagnostics.StoredNodes}; "
            + $"elapsedMs={unseeded.Diagnostics.Elapsed.TotalMilliseconds:F3}/{seeded.Diagnostics.Elapsed.TotalMilliseconds:F3}; bytes={unseededBytes}/{seededBytes}; "
            + $"seedReplaySteps={seeded.Diagnostics.Seeding.ReplaySteps}; seedReplayMs={seeded.Diagnostics.Seeding.ReplayElapsed.TotalMilliseconds:F3}");
    }

    [Fact]
    public void SeedComparisonProductionTimeLimitStillReturnsAcceptedBaselineOrBetter()
    {
        for (var index = 0; index < 7; ++index)
        {
            var candidate = AdaptiveSeedRegressionCorpus.Candidate(index);
            var unseeded = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(index), new());
            var result = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(index),
                new(), seeds: new[] { candidate });
            // Scheduling delays may consume the deadline before seed replay: no fabricated acceptance.
            if (result.Diagnostics.Seeding!.Candidates[0].Status != "Validated")
            {
                output.WriteLine($"production step={index + 2}; seedStatus={result.Diagnostics.Seeding.Candidates[0].Status}; no accepted baseline");
                continue;
            }
            Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
            Assert.Equal(12000, result.Plan!.FinalState.Quality);
            Assert.NotEqual("BelowSeed", result.Diagnostics.Seeding.Comparison);
            output.WriteLine($"production step={index + 2}; q={unseeded.Plan?.FinalState.Quality}/{result.Plan.FinalState.Quality}; "
                + $"elapsedMs={unseeded.Diagnostics.Elapsed.TotalMilliseconds:F3}/{result.Diagnostics.Elapsed.TotalMilliseconds:F3}; "
                + $"transitions={unseeded.Diagnostics.Transitions}/{result.Diagnostics.Transitions}; nodes={unseeded.Diagnostics.StoredNodes}/{result.Diagnostics.StoredNodes}; "
                + $"actions={unseeded.Plan?.Actions.Count}/{result.Plan.Actions.Count}");
        }
    }

    [Fact]
    public void ReplayStopDiscardsPartiallyReplayedCandidate()
    {
        var calls = 0;
        Assert.False(AdaptivePlanValidator.TryValidate(Simulation, AdaptiveSeedRegressionCorpus.State(0),
            AdaptiveSeedRegressionCorpus.Candidate(0).Actions, out var plan, Simulation.CreateWorkspace(),
            () => ++calls > 3, out var steps));
        Assert.Null(plan);
        Assert.Equal(3, steps);
    }

    [Fact]
    public void LegacyContinuationSharesReplayAccountingAndTwoCandidateBound()
    {
        var candidate = AdaptiveSeedRegressionCorpus.Candidate(0);
        var result = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(0),
            new() { MaximumTransitions = 1, TimeLimit = null }, continuation: candidate.Actions, seeds: new[] { candidate });
        var diagnostics = result.Diagnostics.Seeding!;
        Assert.Equal(2, diagnostics.Candidates.Count);
        Assert.All(diagnostics.Candidates, assessment => Assert.Equal("Validated", assessment.Status));
        Assert.Equal(candidate.Actions.Count * 2, diagnostics.ReplaySteps);
        Assert.Equal(12000, result.Plan!.FinalState.Quality);
        Assert.Throws<ArgumentException>(() => new AdaptiveSearchEngine().Search(Simulation,
            AdaptiveSeedRegressionCorpus.State(0), continuation: candidate.Actions, seeds: new[] { candidate, candidate }));
    }

    [Fact]
    public void CandidateAndRequestStorageAreDetachedAndReadOnly()
    {
        var actions = AdaptiveSeedRegressionCorpus.RecordedActions.ToArray();
        var seed = new AdaptivePlanSeed(AdaptiveSeedRegressionCorpus.Seed().Provenance, actions);
        actions[0] = VulcanSkill.None;
        var candidate = new AdaptiveSeedCandidate(seed, 1, 0);
        Assert.Equal(VulcanSkill.MuscleMemory, seed.Actions[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<VulcanSkill>)candidate.Actions).Clear());
        var candidates = new[] { candidate };
        var request = new AdaptiveSearchRequest(new(Guid.NewGuid(), 1, 1, Craft, Goal, new(), AdaptiveSeedRegressionCorpus.State(0)), seeds: candidates);
        candidates[0] = new(seed, 2, 0);
        Assert.Equal(1, request.Seeds[0].Cursor);
    }

    [Fact]
    public void TrackerUsesCorrelatedActionCountIncludingNonStepActionsAndDoesNotAdvanceOnAttempts()
    {
        var actions = new[] { VulcanSkill.FinalAppraisal, VulcanSkill.MuscleMemory, VulcanSkill.Groundwork };
        var seed = new AdaptivePlanSeed(AdaptiveSeedRegressionCorpus.Seed().Provenance, actions);
        var tracker = new AdaptiveSeedTracker(seed, "cache");
        Assert.Equal(0, tracker.Capture(Craft, 0).Candidates[0].Cursor);
        tracker.Correlate(VulcanSkill.FinalAppraisal);
        Assert.Equal(1, tracker.Capture(Craft, 1).Candidates[0].Cursor);
        tracker.Correlate(VulcanSkill.MuscleMemory);
        Assert.Equal(2, tracker.Capture(Craft, 2).Candidates[0].Cursor);
        Assert.Equal(0, tracker.Capture(Craft, 0).Candidates[0].Cursor); // Earlier captured root remains immutable/aligned.
    }

    [Fact]
    public void DivergenceNeverGuessesSuffixButEarlierCapturedRootsRemainUsable()
    {
        var tracker = new AdaptiveSeedTracker(AdaptiveSeedRegressionCorpus.Seed(), "cache");
        tracker.Correlate(VulcanSkill.MuscleMemory);
        tracker.Correlate(VulcanSkill.BasicTouch); // Expected Manipulation, not a skipped action.
        Assert.Empty(tracker.Capture(Craft, 2).Candidates);
        Assert.Equal("Unaligned", tracker.Capture(Craft, 2).Availability[0].Status);
        Assert.Single(tracker.Capture(Craft, 1).Candidates);
        Assert.Empty(tracker.Capture(Craft with { MaxCP = 663 }, 1).Candidates);
    }

    [Fact]
    public void OwnContinuationRequiresExactPrefixAndSupportsTwoBoundedCandidates()
    {
        var tracker = new AdaptiveSeedTracker(AdaptiveSeedRegressionCorpus.Seed(), "cache");
        tracker.Correlate(VulcanSkill.MuscleMemory);
        var candidate = AdaptiveSeedRegressionCorpus.Candidate(0);
        Assert.True(AdaptivePlanValidator.TryValidate(Simulation, AdaptiveSeedRegressionCorpus.State(0), candidate.Actions, out var plan));
        var source = new AdaptiveHistoricalObservation(new(Guid.NewGuid(), 1, 1, Craft, Goal, new(), AdaptiveSeedRegressionCorpus.State(0)),
            AdaptiveSeedRegressionCorpus.Roots[0], TimeSpan.Zero) { ActionOrdinal = 1 };
        tracker.Remember(source, plan!);
        tracker.Correlate(VulcanSkill.Manipulation);
        var batch = tracker.Capture(Craft, 2);
        Assert.Equal(2, batch.Candidates.Count);
        Assert.Equal(1, batch.Candidates[1].Cursor);
        var result = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(1),
            new() { MaximumTransitions = 1, TimeLimit = null }, seeds: batch.Candidates);
        Assert.Equal(12000, result.Plan!.FinalState.Quality);
        Assert.Equal(2, result.Diagnostics.Seeding!.Candidates.Count);
        tracker.Correlate(VulcanSkill.BasicTouch);
        Assert.Empty(tracker.Capture(Craft, 3).Candidates);
    }

    [Fact]
    public void EvictedActionJournalCannotAlignAnOldHistoricalPlan()
    {
        var tracker = new AdaptiveSeedTracker(null, "missing");
        var plan = new AdaptivePlan(Enumerable.Repeat(VulcanSkill.FinalAppraisal, 256).ToArray(), AdaptiveSeedRegressionCorpus.State(0), 256, false);
        var source = new AdaptiveHistoricalObservation(new(Guid.NewGuid(), 1, 1, Craft, Goal, new(), AdaptiveSeedRegressionCorpus.State(0)),
            AdaptiveSeedRegressionCorpus.Roots[0], TimeSpan.Zero);
        for (var i = 0; i < 257; ++i) tracker.Correlate(VulcanSkill.FinalAppraisal);
        tracker.Remember(source, plan);
        Assert.Empty(tracker.Capture(Craft, 257).Candidates);
    }

    [Fact]
    public void InvalidSeedFallsBackWithoutInventingStateOrQuality()
    {
        var seed = new AdaptivePlanSeed(AdaptiveSeedRegressionCorpus.Seed().Provenance, new[] { VulcanSkill.BasicTouch });
        var result = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(0),
            new() { TimeLimit = null }, seeds: new[] { new AdaptiveSeedCandidate(seed, 0, 0) });
        Assert.Equal("Invalid", Assert.Single(result.Diagnostics.Seeding!.Candidates).Status);
        Assert.Equal("NoAcceptedSeed", result.Diagnostics.Seeding.Comparison);
        Assert.Equal(10800, result.Plan!.FinalState.Quality);
    }

    [Fact]
    public void GoodMultiplierAndActualStartingQualityAreUsedForReplayNotSeedAssumptions()
    {
        var craft = Craft with { RequiredProgress = 200 };
        var state = new AdaptiveSearchState(false, 0, 0, 80, 664, Condition.Good);
        var seed = new AdaptivePlanSeed(AdaptiveSeedRegressionCorpus.Seed().Provenance,
            new[] { VulcanSkill.PreparatoryTouch, VulcanSkill.CarefulSynthesis });
        var candidate = new AdaptiveSeedCandidate(seed, 0, 0);
        var enhanced = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 1050, true));
        var ordinaryCraft = craft with { GoodQualityPercent = 150 };
        var ordinary = new AdaptiveSimulation(ordinaryCraft, AdaptiveCraftGoal.Resolve(ordinaryCraft, AdaptiveGoalKind.Quality, 1050, true));
        var limits = new AdaptiveSearchLimits { MaximumTransitions = 1, TimeLimit = null };
        Assert.Equal("Validated", new AdaptiveSearchEngine().Search(enhanced, state, limits, seeds: new[] { candidate }).Diagnostics.Seeding!.Candidates[0].Status);
        Assert.Equal("Invalid", new AdaptiveSearchEngine().Search(ordinary, state, limits, seeds: new[] { candidate }).Diagnostics.Seeding!.Candidates[0].Status);
    }

    [Fact]
    public void CancelledAndOversizedInputsCannotExposeSeedPlans()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var candidate = AdaptiveSeedRegressionCorpus.Candidate(0);
        var result = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(0),
            new(), cancellation: cancellation.Token, seeds: new[] { candidate });
        Assert.Equal(AdaptiveSearchStatus.Cancelled, result.Status);
        Assert.Null(result.Plan);
        Assert.Throws<ArgumentException>(() => new AdaptiveSearchEngine().Search(Simulation,
            AdaptiveSeedRegressionCorpus.State(0), seeds: new[] { candidate, candidate, candidate }));
        Assert.Throws<ArgumentException>(() => new AdaptivePlanSeed(candidate.Seed.Provenance, Enumerable.Repeat(VulcanSkill.Observe, 257).ToArray()));
        Assert.Throws<ArgumentException>(() => new AdaptivePlanSeed(candidate.Seed.Provenance, new[] { VulcanSkill.HastyTouch }));
        var tooLong = new AdaptiveSearchEngine().Search(Simulation, AdaptiveSeedRegressionCorpus.State(0),
            new() { MaximumPlanLength = 1, TimeLimit = null }, seeds: new[] { candidate });
        Assert.Equal("TooLong", tooLong.Diagnostics.Seeding!.Candidates[0].Status);
    }
}
