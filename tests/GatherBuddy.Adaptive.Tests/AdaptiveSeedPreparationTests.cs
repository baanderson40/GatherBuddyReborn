using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveSeedPreparationTests
{
    private static AdaptiveSimulation Simulation => AdaptiveCoreTests.Simulation(
        SimulatorAuditTests.Craft() with { RecipeId = 5618, CraftProgress = 100 }, 500);
    private static AdaptiveSeedPreparationContext Context => new(42, 15, Simulation.Craft, Simulation.Goal, "active policy", "exact-key", 0);
    private static AdaptivePlanSeed Seed(AdaptiveSeedPreparationContext? context = null, VulcanSkill[]? actions = null)
    {
        var c = context ?? Context;
        return new(new("RaphaelCache", c.RequestKey, c.Craft.RecipeId, c.Craft.Level, c.Craft.Craftsmanship,
            c.Craft.Control, c.Craft.MaxCP, c.Craft.ManipulationUnlocked, false, c.ExpectedInitialQuality),
            actions ?? [VulcanSkill.BasicTouch, VulcanSkill.BasicSynthesis]);
    }
    private static AdaptiveSeedPreparation Prepare() => new(TimeSpan.Zero, TimeSpan.FromSeconds(10));

    [Fact]
    public void CacheHitNeverQueuesAndCandidateConsumesOnce()
    {
        var preparation = Prepare();
        var seed = Seed();
        Assert.True(preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Ready, seed), _ => throw new Exception("must not queue")));
        Assert.Equal("Prepared", preparation.Status.Phase);
        Assert.False(preparation.Status.GenerationRequested);
        Assert.Same(seed, preparation.Consume(Context));
        Assert.Null(preparation.Consume(Context));
        Assert.Equal("AwaitingRootReplay", preparation.Status.Phase);
    }

    [Theory]
    [InlineData(AdaptiveSeedLookupState.Missing)]
    [InlineData(AdaptiveSeedLookupState.Pending)]
    public void MissingOrExistingPendingQueuesOrPromotesOnlyOnce(AdaptiveSeedLookupState first)
    {
        var preparation = Prepare();
        var calls = 0;
        var state = first;
        AdaptiveSeedLookup Read(AdaptiveSeedPreparationContext _) => new(state, state == AdaptiveSeedLookupState.Ready ? Seed() : null);
        Assert.False(preparation.Poll(Context, TimeSpan.Zero, Read, _ => ++calls));
        state = AdaptiveSeedLookupState.Pending;
        Assert.False(preparation.Poll(Context, TimeSpan.FromSeconds(1), Read, _ => ++calls));
        Assert.False(preparation.Poll(Context, TimeSpan.FromSeconds(2), Read, _ => ++calls));
        state = AdaptiveSeedLookupState.Ready;
        Assert.True(preparation.Poll(Context, TimeSpan.FromSeconds(3), Read, _ => ++calls));
        Assert.Equal(1, calls);
        Assert.True(preparation.Status.GenerationRequested);
        Assert.NotNull(preparation.Consume(Context));
    }

    [Theory]
    [InlineData(AdaptiveSeedLookupState.Failed)]
    [InlineData(AdaptiveSeedLookupState.Unavailable)]
    public void FailedDisabledMissingCliOrCancelledSharedSolveFallsBackWithoutRetry(AdaptiveSeedLookupState state)
    {
        var preparation = Prepare();
        Assert.True(preparation.Poll(Context, TimeSpan.Zero, _ => new(state, Reason: "failure details"), _ => throw new Exception()));
        Assert.Equal("Unseeded", preparation.Status.Phase);
        Assert.Contains("failure details", preparation.Status.Reason);
        Assert.True(preparation.Poll(Context, TimeSpan.FromSeconds(1), _ => throw new Exception("late lookup"), _ => throw new Exception()));
        Assert.Null(preparation.Consume(Context));
    }

    [Fact]
    public void DeadlineRejectsEvenReadyLateResultAndIncludesQueueWait()
    {
        var preparation = Prepare();
        Assert.False(preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Pending), _ => { }));
        Assert.True(preparation.Poll(Context, TimeSpan.FromSeconds(10), _ => throw new Exception("past deadline"), _ => throw new Exception()));
        Assert.Contains("timeout", preparation.Status.Reason);
        Assert.Null(preparation.Consume(Context));
    }

    [Fact]
    public void PreparedCandidateIsFrozenEvenIfCacheChangesOrDeadlineLaterPasses()
    {
        var preparation = Prepare();
        var seed = Seed();
        preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Ready, seed), _ => { });
        Assert.True(preparation.Poll(Context, TimeSpan.FromSeconds(11), _ => throw new Exception("must not refetch"), _ => throw new Exception()));
        Assert.Same(seed, preparation.Consume(Context));
    }

    [Theory]
    [InlineData("character")]
    [InlineData("job")]
    [InlineData("stats")]
    [InlineData("tool")]
    [InlineData("goal")]
    [InlineData("policy")]
    [InlineData("request")]
    [InlineData("quality")]
    public void ChangedContextCannotReusePreparedSeedOrQueueAnotherAttempt(string field)
    {
        var preparation = Prepare();
        preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Ready, Seed()), _ => { });
        var changed = field switch
        {
            "character" => Context with { Character = 99 },
            "job" => Context with { Job = 8 },
            "stats" => Context with { Craft = Context.Craft with { Control = Context.Craft.Control + 1 } },
            "tool" => Context with { Craft = Context.Craft with { GoodQualityPercent = 175 } },
            "goal" => Context with { Goal = AdaptiveCraftGoal.Resolve(Context.Craft, AdaptiveGoalKind.ProgressOnly) },
            "policy" => Context with { Policy = "changed" },
            "request" => Context with { RequestKey = "other" },
            _ => Context with { ExpectedInitialQuality = 100 },
        };
        Assert.True(preparation.Poll(changed, TimeSpan.FromSeconds(1), _ => throw new Exception(), _ => throw new Exception()));
        Assert.Equal("Unseeded", preparation.Status.Phase);
        Assert.Null(preparation.Consume(changed));
    }

    [Fact]
    public void CancellationAndRearmHaveIndependentCandidates()
    {
        var old = Prepare();
        old.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Pending), _ => { });
        old.Cancel();
        old.Poll(Context, TimeSpan.FromSeconds(1), _ => throw new Exception("late result"), _ => throw new Exception());
        Assert.Null(old.Consume(Context));
        var fresh = Prepare();
        fresh.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Ready, Seed()), _ => { });
        Assert.NotNull(fresh.Consume(Context));
        Assert.Equal("Cancelled", old.Status.Phase);
    }

    [Fact]
    public void MissingAfterQueueOrPrematureStartFallsBackWithoutLateInjection()
    {
        var missing = Prepare();
        missing.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Missing), _ => { });
        Assert.True(missing.Poll(Context, TimeSpan.FromSeconds(1), _ => new(AdaptiveSeedLookupState.Missing), _ => throw new Exception()));
        Assert.Null(missing.Consume(Context));
        var pending = Prepare();
        pending.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Pending), _ => { });
        Assert.Null(pending.Consume(Context));
        pending.Poll(Context, TimeSpan.FromSeconds(1), _ => throw new Exception("no late injection"), _ => throw new Exception());
        Assert.Null(pending.Consume(Context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupOrQueueExceptionFallsBack(bool queueThrows)
    {
        var preparation = Prepare();
        Assert.True(preparation.Poll(Context, TimeSpan.Zero,
            _ => queueThrows ? new(AdaptiveSeedLookupState.Missing) : throw new InvalidOperationException(),
            _ => throw new InvalidOperationException()));
        Assert.Null(preparation.Consume(Context));
        Assert.Contains("exception", preparation.Status.Reason);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("stats")]
    [InlineData("specialist")]
    [InlineData("quality")]
    [InlineData("length")]
    public void UnsupportedOrMismatchedCandidateFallsBack(string error)
    {
        var seed = Seed();
        var p = seed.Provenance;
        p = error switch
        {
            "key" => p with { RequestKey = "wrong" },
            "stats" => p with { MaxCP = p.MaxCP - 1 },
            "specialist" => p with { Specialist = true },
            "quality" => p with { InitialQuality = 100 },
            _ => p,
        };
        seed = new(p, error == "length" ? Enumerable.Repeat(VulcanSkill.FinalAppraisal, 65).ToArray() : seed.Actions);
        var preparation = Prepare();
        Assert.True(preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Ready, seed), _ => throw new Exception()));
        Assert.Null(preparation.Consume(Context));
        Assert.Equal("Unseeded", preparation.Status.Phase);
    }

    [Fact]
    public void DiagnosticReasonIsBoundedAndClockRegressionFallsBack()
    {
        var preparation = Prepare();
        preparation.Poll(Context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Failed, Reason: new string('x', 5000)), _ => { });
        Assert.True(preparation.Status.Reason.Length < 1100);
        preparation = Prepare();
        preparation.Poll(Context, TimeSpan.FromSeconds(2), _ => new(AdaptiveSeedLookupState.Pending), _ => { });
        Assert.True(preparation.Poll(Context, TimeSpan.FromSeconds(1), _ => throw new Exception(), _ => throw new Exception()));
        Assert.Contains("clock regressed", preparation.Status.Reason);
    }

    private sealed class LowerPlanScheduler : IAdaptiveSearchScheduler
    {
        public List<AdaptiveSearchRequest> Requests { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            Requests.Add(request);
            Assert.True(AdaptivePlanValidator.TryValidate(new(request.Identity.Craft, request.Identity.Goal), request.Identity.State,
                new[] { VulcanSkill.BasicSynthesis }, out var plan));
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found, plan, new(0, 0, 0, TimeSpan.Zero, false), "Test low-quality worker result."));
        }
    }

    [Fact]
    public void CoordinatorIndependentlyReplaysAndPreservesSubtargetSeedAgainstFaultyWorker()
    {
        var scheduler = new LowerPlanScheduler();
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        var session = coordinator.Begin(Context.Craft, Context.Goal, startupSeed: Seed());
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial());
        Assert.Single(Assert.Single(scheduler.Requests).Seeds);
        Assert.Equal("Validated", coordinator.StartupSeedStatus.Phase);
        coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Ready, coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.BasicTouch, coordinator.Snapshot.Action); // Worker offered lower-quality synthesis.
        Assert.Equal(0, coordinator.StartupSeedStatus.ObservedInitialQuality);
    }

    [Fact]
    public async Task ProductionSchedulerReceivesSeedAndReportsItsReplay()
    {
        using var coordinator = new AdaptiveSolveCoordinator();
        var session = coordinator.Begin(Context.Craft, Context.Goal,
            new() { TimeLimit = null, MaximumTransitions = 1 }, startupSeed: Seed());
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial());
        await coordinator.WorkerCompletion;
        coordinator.Poll();
        Assert.Equal("Validated", Assert.Single(coordinator.Snapshot.SearchDiagnostics!.Seeding!.Candidates).Status);
        Assert.Equal(VulcanSkill.BasicTouch, coordinator.Snapshot.Action);
    }

    [Theory]
    [InlineData("illegal")]
    [InlineData("mandatory")]
    [InlineData("strict")]
    [InlineData("stats")]
    [InlineData("midcraft")]
    public void InvalidStartupSeedIsDiscardedBeforeSearchWithoutBlockingUnseededLane(string error)
    {
        var craft = Context.Craft;
        var goal = Context.Goal;
        var seed = Seed();
        var state = AdaptiveCoreTests.Initial();
        if (error == "illegal") seed = Seed(actions: [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis]);
        if (error == "stats") seed = new(seed.Provenance with { Control = 999 }, seed.Actions);
        if (error == "mandatory") { craft = craft with { RequiredQuality = 500 }; goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 500); }
        if (error == "strict") goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 500, true);
        if (error == "midcraft") state = state with { FirstStep = false };
        // These cases inspect request dispatch; mandatory/strict worker's intentional low plan
        // is not valid, so use a scheduler that records then returns Unknown instead.
        var recorder = new RecordingScheduler();
        using var checkedCoordinator = new AdaptiveSolveCoordinator(recorder);
        var session = checkedCoordinator.Begin(craft, goal, startupSeed: seed);
        checkedCoordinator.Observe(session, 1, state);
        Assert.Empty(Assert.Single(recorder.Requests).Seeds);
        Assert.Equal("Unseeded", checkedCoordinator.StartupSeedStatus.Phase);
    }

    private sealed class RecordingScheduler : IAdaptiveSearchScheduler
    {
        public List<AdaptiveSearchRequest> Requests { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            Requests.Add(request);
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Unknown, null, new(0, 0, 0, TimeSpan.Zero, false), "Recorded."));
        }
    }

    [Fact]
    public void ActualStartingQualityIsIndependentOfPreparationEstimateAndNewSessionCannotReuseSeed()
    {
        var scheduler = new LowerPlanScheduler();
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        var session = coordinator.Begin(Context.Craft, Context.Goal, startupSeed: Seed());
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial() with { Quality = 100 });
        Assert.Equal("Validated", coordinator.StartupSeedStatus.Phase);
        Assert.Equal(100, coordinator.StartupSeedStatus.ObservedInitialQuality);
        coordinator.Poll();
        session = coordinator.Begin(Context.Craft, Context.Goal);
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial());
        Assert.Empty(scheduler.Requests[^1].Seeds);
        Assert.Equal("Unseeded", coordinator.StartupSeedStatus.Phase);
    }

    [Fact]
    public void LiveTrialSeedNeedsTwoNativeFramesAndCannotAuthorizeEarlyDispatch()
    {
        var scheduler = new LowerPlanScheduler();
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        using var trial = new AdaptiveLiveTrial(coordinator, Context.Craft, Context.Goal, frame,
            new(1, Context.Craft.RecipeId, 0, 2), TimeSpan.Zero, TimeSpan.Zero, Seed());
        trial.Tick(1, TimeSpan.FromMilliseconds(10), frame, false, false);
        Assert.Empty(scheduler.Requests);
        Assert.Equal("AwaitingRootReplay", trial.StartupSeedStatus.Phase);
        Assert.Equal(AdaptiveDispatchResult.NotStarted, trial.TryDispatch(TimeSpan.FromMilliseconds(10), frame,
            (uint)VulcanSkill.BasicTouch, true, () => throw new Exception("seed is not execution authority")));
        trial.Tick(2, TimeSpan.FromMilliseconds(20), frame, false, false);
        Assert.Single(Assert.Single(scheduler.Requests).Seeds);
        Assert.Equal("Validated", trial.StartupSeedStatus.Phase);
        Assert.Equal(AdaptiveDecisionPhase.Ready, trial.Decision.Phase);
        Assert.Equal(VulcanSkill.BasicTouch, trial.Decision.Action);
        Assert.Equal(0, trial.Evidence.AnalysisSummary.SearchesDispatched);
        Assert.False(trial.HasOutstandingTransaction);
    }

    [Fact]
    public async Task InvalidSeedStillRunsRealUnseededSearch()
    {
        using var coordinator = new AdaptiveSolveCoordinator();
        var session = coordinator.Begin(Context.Craft, Context.Goal, new() { TimeLimit = null },
            startupSeed: Seed(actions: [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis]));
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial());
        await coordinator.WorkerCompletion;
        coordinator.Poll();
        Assert.Equal("Unseeded", coordinator.StartupSeedStatus.Phase);
        Assert.Equal(AdaptiveDecisionPhase.Ready, coordinator.Snapshot.Phase);
        Assert.Null(coordinator.Snapshot.SearchDiagnostics!.Seeding);
    }

    private sealed class DeferredScheduler : IAdaptiveSearchScheduler
    {
        public List<AdaptiveSearchRequest> Requests { get; } = [];
        public List<TaskCompletionSource<AdaptiveSearchResult>> Results { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            Requests.Add(request);
            var result = new TaskCompletionSource<AdaptiveSearchResult>();
            Results.Add(result);
            return result.Task; // Deliberately ignores cancellation; coordinator must drain/reject it.
        }
    }

    [Fact]
    public void LateSeededWorkerCannotAttachToNextUnseededSession()
    {
        var scheduler = new DeferredScheduler();
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        var initial = AdaptiveCoreTests.Initial();
        var old = coordinator.Begin(Context.Craft, Context.Goal, startupSeed: Seed());
        coordinator.Observe(old, 1, initial);
        var current = coordinator.Begin(Context.Craft, Context.Goal);
        coordinator.Observe(current, 1, initial);
        Assert.Single(scheduler.Requests); // Old worker must actually finish before replacement.
        Assert.True(AdaptivePlanValidator.TryValidate(Simulation, initial, Seed().Actions, out var seeded));
        scheduler.Results[0].SetResult(new(AdaptiveSearchStatus.Found, seeded, new(0, 0, 0, TimeSpan.Zero, false), "Stale seeded result."));
        coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Searching, coordinator.Snapshot.Phase);
        Assert.Equal(current, coordinator.Snapshot.SessionId);
        Assert.Equal("Unseeded", coordinator.StartupSeedStatus.Phase);
        Assert.Empty(scheduler.Requests[1].Seeds);
        Assert.True(AdaptivePlanValidator.TryValidate(Simulation, initial, new[] { VulcanSkill.BasicSynthesis }, out var plain));
        scheduler.Results[1].SetResult(new(AdaptiveSearchStatus.Found, plain, new(0, 0, 0, TimeSpan.Zero, false), "Current unseeded result."));
        coordinator.Poll();
        Assert.Equal(VulcanSkill.BasicSynthesis, coordinator.Snapshot.Action);
        Assert.Equal(1, coordinator.Snapshot.RejectedResults);
    }
}
