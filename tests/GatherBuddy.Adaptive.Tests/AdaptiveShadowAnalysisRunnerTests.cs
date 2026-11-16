using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveShadowAnalysisRunnerTests
{
    [Fact]
    public void LateValidatedPlanSeedsPendingRootAtCapturedOrdinalNotCurrentOrdinal()
    {
        var scheduler = new Scheduler();
        using var runner = new AdaptiveShadowAnalysisRunner(scheduler);
        var craft = AdaptiveSeedRegressionCorpus.Craft;
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 12000);
        var limits = new AdaptiveSearchLimits { MaximumTransitions = 1, TimeLimit = null };
        var session = runner.Begin(craft, goal, limits, TimeSpan.Zero, AdaptiveSeedRegressionCorpus.Seed());
        runner.CorrelateAction(session, VulcanSkill.MuscleMemory, TimeSpan.Zero);
        runner.Submit(session, 1, AdaptiveSeedRegressionCorpus.Roots[0], AdaptiveSeedRegressionCorpus.State(0), TimeSpan.Zero);
        runner.RecordAttempt(session, 1, VulcanSkill.Manipulation, TimeSpan.Zero);
        Assert.Equal(1, Assert.Single(scheduler.Work[0].Request.Seeds).Cursor);
        runner.CorrelateAction(session, VulcanSkill.Manipulation, TimeSpan.Zero);
        runner.Submit(session, 2, AdaptiveSeedRegressionCorpus.Roots[1], AdaptiveSeedRegressionCorpus.State(1), TimeSpan.Zero);
        runner.CorrelateAction(session, VulcanSkill.Veneration, TimeSpan.Zero);
        var request = scheduler.Work[0].Request;
        scheduler.Work[0].Completion.SetResult(new AdaptiveSearchEngine().Search(new(craft, goal), request.Identity.State,
            limits, seeds: request.Seeds));
        runner.Poll(session, TimeSpan.Zero);
        Assert.Equal("Validated", Assert.Single(runner.History).ReplayValidation);
        var pending = scheduler.Work[1].Request;
        Assert.Equal(2, pending.Seeds.Count);
        Assert.Equal(2, pending.Seeds[0].Cursor);
        Assert.Equal(1, pending.Seeds[1].Cursor);
        Assert.Equal(VulcanSkill.Veneration, pending.Seeds[1].Actions[0]);
        Assert.Equal(AdaptiveSeedRegressionCorpus.State(1), pending.Identity.State);

        // Old completion cannot become a continuation in a different craft/session.
        var next = runner.Begin(craft, goal, limits, TimeSpan.Zero);
        runner.Submit(next, 1, AdaptiveSeedRegressionCorpus.Roots[0], AdaptiveSeedRegressionCorpus.State(0), TimeSpan.Zero);
        scheduler.Work[1].Finish();
        runner.Poll(next, TimeSpan.Zero);
        Assert.Empty(runner.History);
        Assert.Empty(scheduler.Work[2].Request.Seeds);
    }

    private sealed record Work(AdaptiveSearchRequest Request, CancellationToken Token,
        TaskCompletionSource<AdaptiveSearchResult> Completion)
    {
        public void Finish() => Completion.SetResult(new AdaptiveSearchEngine().Search(
            new(Request.Identity.Craft, Request.Identity.Goal), Request.Identity.State, AdaptiveCoreTests.Limits()));
    }
    private sealed class Scheduler : IAdaptiveSearchScheduler
    {
        public List<Work> Work { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            Assert.DoesNotContain(Work, w => !w.Completion.Task.IsCompleted);
            var work = new Work(request, cancellation, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Work.Add(work);
            return work.Completion.Task;
        }
    }
    private sealed class BrokenScheduler(bool throwError) : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => throwError ? throw new InvalidOperationException("Test scheduler failure.") : null!;
    }
    private sealed class Harness : IDisposable
    {
        public Scheduler Scheduler { get; } = new();
        public AdaptiveShadowAnalysisRunner Runner { get; }
        public AdaptiveCraftSnapshot Craft { get; } = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        public AdaptiveObservedFrame Frame { get; } = new(1, 0, 0, 80, 600, Condition.Normal);
        public Guid Session { get; private set; }
        public TimeSpan Now { get; private set; }
        public Harness() { Runner = new(Scheduler); Begin(); }
        public void Begin() => Session = Runner.Begin(Craft, AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality, Craft.MaxQuality),
            AdaptiveCoreTests.Limits(), Now);
        public void Submit(long revision, int quality = 0)
        {
            Now += TimeSpan.FromMilliseconds(10);
            var frame = Frame with { Quality = quality };
            Assert.True(Runner.Submit(Session, revision, frame, frame.ToState(true, VulcanSkill.None), Now));
        }
        public void Poll() { Now += TimeSpan.FromMilliseconds(10); Runner.Poll(Session, Now); }
        public void Revoke() { Now += TimeSpan.FromMilliseconds(10); Runner.Revoke(Session, Now, "Test revocation."); }
        public void Resume() { Now += TimeSpan.FromMilliseconds(10); Runner.Resume(Session, Now); }
        public void Dispose() => Runner.Dispose();
    }

    [Fact]
    public void ExistingRequestDoesNotCancelAndHistoricalResultOwnsOriginalRootAndTiming()
    {
        using var h = new Harness();
        h.Submit(1, 123);
        var root = h.Scheduler.Work[0].Request.Identity;
        h.Runner.RecordAttempt(h.Session, 1, VulcanSkill.GreatStrides, h.Now);
        Assert.False(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Submit(3, 456);
        h.Scheduler.Work[0].Finish();
        h.Poll();
        var result = Assert.Single(h.Runner.History);
        Assert.Equal(root, result.Source.Identity);
        Assert.Equal(123, result.Source.NativeFrame.Quality);
        Assert.Equal(VulcanSkill.GreatStrides, result.Source.ExistingSolverAttempt);
        Assert.Equal("Validated", result.ReplayValidation);
        Assert.NotNull(result.ProposedFirstAction);
        Assert.Equal(result.ResultObservedAt - result.Source.ObservedAt, result.AgeAtRetention);
        Assert.True(result.Source.ObservedAt <= result.DispatchedAt && result.DispatchedAt <= result.ResultObservedAt);
        Assert.False(result.Executable);
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(456, h.Scheduler.Work[1].Request.Identity.State.Quality);
    }

    [Fact]
    public void LatestPendingWinsAndEverySupersededRootIsCounted()
    {
        using var h = new Harness();
        h.Submit(1);
        for (var revision = 2; revision <= 20; ++revision) h.Submit(revision, revision);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(20, h.Runner.Summary.ObservationsSubmitted);
        Assert.Equal(18, h.Runner.Summary.ObservationsSkipped);
        h.Scheduler.Work[0].Finish();
        h.Poll();
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(20, h.Scheduler.Work[1].Request.Identity.ObservationRevision);
        Assert.False(h.Scheduler.Work[1].Token.IsCancellationRequested);
    }

    [Fact]
    public void RevokedIgnoringWorkerMustDrainBeforeResumedReplacementCanRun()
    {
        using var h = new Harness();
        h.Submit(1);
        h.Submit(2);
        h.Revoke();
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        Assert.Equal(1, h.Runner.Summary.ObservationsSkipped);
        h.Resume();
        h.Submit(3, 333);
        Assert.Single(h.Scheduler.Work);
        h.Scheduler.Work[0].Finish(); // Deliberately ignore cancellation.
        h.Poll();
        Assert.Empty(h.Runner.History);
        Assert.Equal(1, h.Runner.Summary.RevokedResultsDiscarded);
        Assert.Equal(2, h.Scheduler.Work.Count);
        h.Scheduler.Work[1].Finish();
        h.Poll();
        Assert.Equal(3, Assert.Single(h.Runner.History).Source.Identity.ObservationRevision);
    }

    [Fact]
    public void NewSessionRejectsOldCompletionAndDoesNotLeakItsInputsOrOverlapWork()
    {
        using var h = new Harness();
        h.Submit(1, 777);
        var oldSession = h.Session;
        h.Begin();
        h.Submit(1, 111);
        Assert.Single(h.Scheduler.Work);
        h.Scheduler.Work[0].Finish();
        h.Poll();
        Assert.Empty(h.Runner.History);
        Assert.Equal(2, h.Scheduler.Work.Count);
        h.Runner.RecordAttempt(oldSession, 1, VulcanSkill.BasicTouch, h.Now);
        h.Scheduler.Work[1].Finish();
        h.Poll();
        var result = Assert.Single(h.Runner.History);
        Assert.Equal(h.Session, result.Source.Identity.SessionId);
        Assert.Equal(111, result.Source.NativeFrame.Quality);
        Assert.Null(result.Source.ExistingSolverAttempt);
        Assert.Empty(h.Runner.GetHistory(oldSession));
        Assert.Equal(0, h.Runner.GetSummary(oldSession).HistoricalResultsRetained);
        Assert.DoesNotContain(h.Runner.DrainEvents(h.Session), e => e.Detail.Contains("777"));
    }

    [Theory]
    [InlineData(AdaptiveSearchStatus.Unknown)]
    [InlineData(AdaptiveSearchStatus.Cancelled)]
    [InlineData(AdaptiveSearchStatus.ProvenImpossible)]
    public void NonFoundResultsNeverCarryAProposalEvenWithAPlan(AdaptiveSearchStatus status)
    {
        using var h = new Harness();
        h.Submit(1);
        var work = h.Scheduler.Work[0];
        var valid = new AdaptiveSearchEngine().Search(new(h.Craft, work.Request.Identity.Goal), work.Request.Identity.State, AdaptiveCoreTests.Limits());
        work.Completion.SetResult(valid with { Status = status });
        h.Poll();
        var retained = Assert.Single(h.Runner.History);
        Assert.Equal(status, retained.SearchStatus);
        Assert.Null(retained.ValidatedPlan);
        Assert.Null(retained.ProposedFirstAction);
    }

    [Fact]
    public void FoundPlanMustReplayFromExactCapturedRootNotWorkerClaimedFinalState()
    {
        using var h = new Harness();
        h.Submit(1);
        var work = h.Scheduler.Work[0];
        var result = new AdaptiveSearchResult(AdaptiveSearchStatus.Found,
            new([VulcanSkill.BasicTouch], work.Request.Identity.State with { Progress = 200 }, 1, true),
            new(1, 1, 0, TimeSpan.Zero, false), "Bogus completed plan.");
        work.Completion.SetResult(result);
        h.Poll();
        var retained = Assert.Single(h.Runner.History);
        Assert.Equal("Invalid", retained.ReplayValidation);
        Assert.Null(retained.ProposedFirstAction);
        Assert.Equal(0, h.Runner.Summary.ValidatedPlans);
    }

    [Fact]
    public void PlanLengthIsBoundedBeforeReplay()
    {
        using var h = new Harness();
        h.Submit(1);
        var work = h.Scheduler.Work[0];
        var actions = Enumerable.Repeat(VulcanSkill.Observe, work.Request.Identity.Limits.MaximumPlanLength + 1).ToArray();
        work.Completion.SetResult(new(AdaptiveSearchStatus.Found, new(actions, work.Request.Identity.State, 1, false),
            new(1, 1, 0, TimeSpan.Zero, false), "Oversized plan."));
        h.Poll();
        Assert.Equal("Invalid", Assert.Single(h.Runner.History).ReplayValidation);
        Assert.Null(h.Runner.History[0].ProposedFirstAction);
    }

    [Fact]
    public void WorkerFaultDoesNotPreventLatestPendingRootFromRunning()
    {
        using var h = new Harness();
        h.Submit(1);
        h.Submit(2);
        h.Scheduler.Work[0].Completion.SetException(new InvalidOperationException("Test worker fault."));
        h.Poll();
        var retained = Assert.Single(h.Runner.History);
        Assert.Equal("WorkerFailed", retained.ReplayValidation);
        Assert.Null(retained.ProposedFirstAction);
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Fact]
    public void HistoryAndEventsAreBoundedWhileLifetimeCountersRemainAccurate()
    {
        using var h = new Harness();
        for (var revision = 1; revision <= 25; ++revision)
        {
            h.Submit(revision);
            h.Scheduler.Work[^1].Finish();
            h.Poll();
        }
        Assert.Equal(16, h.Runner.History.Count);
        Assert.Equal(25, h.Runner.Summary.HistoricalResultsRetained);
        Assert.Equal(9, h.Runner.Summary.DroppedResults);
        Assert.Equal(25, h.Runner.Summary.ValidatedPlans);
        Assert.Equal(64, h.Runner.DrainEvents(h.Session).Length);
        Assert.True(h.Runner.Summary.DroppedLifecycleEvents > 0);
        Assert.Equal(25, h.Runner.Summary.ObservationsSubmitted);
    }

    [Fact]
    public void MismatchedNativeScalarsAndStaleSessionCannotBeSubmitted()
    {
        using var h = new Harness();
        var state = h.Frame.ToState(true, VulcanSkill.None);
        Assert.Throws<ArgumentException>(() => h.Runner.Submit(h.Session, 1, h.Frame with { CP = 1 }, state, h.Now));
        Assert.False(h.Runner.Submit(Guid.NewGuid(), 1, h.Frame, state, h.Now));
        Assert.Empty(h.Scheduler.Work);
        h.Submit(1);
        Assert.False(h.Runner.Submit(h.Session, 1, h.Frame, state, h.Now));
    }

    [Fact]
    public void TerminalRootDoesNotScheduleSearch()
    {
        using var h = new Harness();
        var frame = h.Frame with { Progress = h.Craft.RequiredProgress, Durability = 0 };
        Assert.False(h.Runner.Submit(h.Session, 1, frame, frame.ToState(true, VulcanSkill.None), h.Now));
        Assert.Empty(h.Scheduler.Work);
        Assert.Equal(0, h.Runner.Summary.ObservationsSubmitted);
    }

    [Fact]
    public void DisposeCancelsWithoutWaitingAndCannotRetainLateCompletion()
    {
        using var h = new Harness();
        h.Submit(1);
        h.Runner.Dispose();
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Finish();
        h.Poll();
        Assert.Empty(h.Runner.History);
        Assert.Throws<ObjectDisposedException>(() => h.Begin());
    }

    [Fact]
    public async Task DefaultSchedulerCompletesBoundedHistoricalPlanWithoutNativeServices()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        using var runner = new AdaptiveShadowAnalysisRunner();
        var session = runner.Begin(craft, goal, AdaptiveCoreTests.Limits(), TimeSpan.Zero);
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        runner.Submit(session, 1, frame, frame.ToState(true, VulcanSkill.None), TimeSpan.Zero);
        await runner.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Poll(session, TimeSpan.FromSeconds(1));
        Assert.Equal("Validated", Assert.Single(runner.History).ReplayValidation);
        Assert.False(runner.History[0].Executable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchedulerFailureIsRetainedAsNonExecutableDiagnostic(bool throwError)
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        using var runner = new AdaptiveShadowAnalysisRunner(new BrokenScheduler(throwError));
        var session = runner.Begin(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly), AdaptiveCoreTests.Limits(), TimeSpan.Zero);
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        runner.Submit(session, 1, frame, frame.ToState(true, VulcanSkill.None), TimeSpan.Zero);
        var retained = Assert.Single(runner.History);
        Assert.Equal("WorkerFailed", retained.ReplayValidation);
        Assert.Null(retained.ProposedFirstAction);
        Assert.Contains(runner.DrainEvents(session), e => e.Kind == "SearchDispatchFailed");
    }

    [Fact]
    public void ReplayRecomputesFinalQualityInsteadOfTrustingWorkerPlanMetadata()
    {
        using var h = new Harness();
        h.Submit(1);
        var work = h.Scheduler.Work[0];
        var solved = new AdaptiveSearchEngine().Search(new(h.Craft, work.Request.Identity.Goal), work.Request.Identity.State, AdaptiveCoreTests.Limits());
        Assert.NotNull(solved.Plan);
        work.Completion.SetResult(solved with { Plan = new(solved.Plan.Actions.ToArray(),
            solved.Plan.FinalState with { Quality = -1 }, -1, !solved.Plan.TargetReached) });
        h.Poll();
        var retained = Assert.Single(h.Runner.History);
        Assert.Equal("Validated", retained.ReplayValidation);
        Assert.Equal(solved.Plan.FinalState, retained.ValidatedPlan!.FinalState);
        Assert.Equal(solved.Plan.TargetReached, retained.DesiredQualityReached);
    }

    [Fact]
    public void EmptyWorkerResultNeverCarriesAProposal()
    {
        using var h = new Harness();
        h.Submit(1);
        h.Scheduler.Work[0].Completion.SetResult(null!);
        h.Poll();
        var retained = Assert.Single(h.Runner.History);
        Assert.Equal("WorkerFailed", retained.ReplayValidation);
        Assert.Null(retained.SearchStatus);
        Assert.Null(retained.ProposedFirstAction);
    }
}
