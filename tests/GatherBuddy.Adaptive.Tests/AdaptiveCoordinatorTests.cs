using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveCoordinatorTests
{
    private sealed class Pending(AdaptiveSearchRequest request, CancellationToken token)
    {
        public AdaptiveSearchRequest Request { get; } = request;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<AdaptiveSearchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(params VulcanSkill[] actions)
        {
            var simulation = new AdaptiveSimulation(Request.Identity.Craft, Request.Identity.Goal);
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, Request.Identity.State, actions, out var plan));
            Completion.SetResult(new(AdaptiveSearchStatus.Found, plan, new(0, 0, 0, TimeSpan.Zero, false), "Test result."));
        }
    }

    private sealed class ManualScheduler : IAdaptiveSearchScheduler
    {
        public List<Pending> Work { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var pending = new Pending(request, cancellation);
            Work.Add(pending);
            return pending.Completion.Task; // Intentionally ignores cancellation to test late results.
        }
    }

    private sealed class Harness : IDisposable
    {
        public ManualScheduler Scheduler { get; } = new();
        public AdaptiveSolveCoordinator Coordinator { get; }
        public AdaptiveCraftSnapshot Craft { get; } = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        public AdaptiveCraftGoal Goal { get; }
        public AdaptiveSearchState Initial { get; } = AdaptiveCoreTests.Initial() with { FirstStep = false };
        public Guid Session { get; }

        public Harness()
        {
            Goal = AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.ProgressOnly);
            Coordinator = new(Scheduler);
            Session = Coordinator.Begin(Craft, Goal, AdaptiveCoreTests.Limits());
        }
        public void Ready(long revision = 1)
        {
            Coordinator.Observe(Session, revision, Initial);
            Scheduler.Work[^1].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
            Coordinator.Poll();
            Assert.Equal(AdaptiveDecisionPhase.Ready, Coordinator.Snapshot.Phase);
        }
        public AdaptiveExecutionLease Consume(long revision = 1)
        {
            Assert.True(Coordinator.TryCreateLease(Session, revision, Initial, out var lease));
            Assert.True(Coordinator.TryConsumeLease(lease!, Session, revision, Initial));
            return lease!;
        }
        public void Dispose() => Coordinator.Dispose();
    }

    [Fact]
    public void CompletedWorkerCannotPublishUntilHostPolls()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Scheduler.Work[0].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        Assert.Equal(AdaptiveDecisionPhase.Searching, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Ready, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.BasicSynthesis, h.Coordinator.Snapshot.Action);
    }

    [Fact]
    public async Task DefaultWorkerSearchRunsWithoutGameServicesAndRequiresExplicitPublication()
    {
        using var coordinator = new AdaptiveSolveCoordinator();
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 100 });
        var session = coordinator.Begin(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly), AdaptiveCoreTests.Limits());
        coordinator.Observe(session, 0, AdaptiveCoreTests.Initial());
        await coordinator.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AdaptiveDecisionPhase.Searching, coordinator.Snapshot.Phase);
        coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Ready, coordinator.Snapshot.Phase);
    }

    [Fact]
    public void DuplicateFramesDoNotRestartSearchingOrReadyDecisions()
    {
        using var h = new Harness();
        for (var i = 0; i < 20; ++i) Assert.True(h.Coordinator.Observe(h.Session, 1, h.Initial));
        Assert.Single(h.Scheduler.Work);
        h.Scheduler.Work[0].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        var identity = h.Coordinator.Snapshot.Decision;
        for (var i = 0; i < 20; ++i) h.Coordinator.Observe(h.Session, 1, h.Initial);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(identity, h.Coordinator.Snapshot.Decision);
    }

    [Fact]
    public void SameRevisionWithDifferentStateFailsClosed()
    {
        using var h = new Harness();
        h.Ready();
        Assert.False(h.Coordinator.Observe(h.Session, 1, h.Initial with { CP = 599 }));
        Assert.Equal(AdaptiveDecisionPhase.Failed, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        Assert.False(h.Coordinator.Observe(h.Session, 2, h.Initial));
    }

    [Fact]
    public void SupersededWorkCannotPublishAndOnlyOneWorkerRunsAtATime()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        var old = h.Scheduler.Work[0];
        var changed = h.Initial with { Condition = Condition.Good };
        h.Coordinator.Observe(h.Session, 2, h.Initial with { CP = 599 });
        h.Coordinator.Observe(h.Session, 3, changed);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Single(h.Scheduler.Work);
        old.Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(changed, h.Scheduler.Work[1].Request.Identity.State);
        Assert.Equal(3, h.Scheduler.Work[1].Request.Identity.ObservationRevision);
        Assert.Equal(1, h.Coordinator.Snapshot.RejectedResults);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        h.Scheduler.Work[1].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Ready, h.Coordinator.Snapshot.Phase);
    }

    [Fact]
    public void ConsecutiveCraftsOfSameRecipeHaveDifferentSessionsAndRejectOldCallbacks()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        var nextSession = h.Coordinator.Begin(h.Craft, h.Goal, AdaptiveCoreTests.Limits());
        Assert.NotEqual(h.Session, nextSession);
        Assert.False(h.Coordinator.ReportExecution(lease, true));
        Assert.False(h.Coordinator.Observe(h.Session, 999, h.Initial));
        Assert.Equal(AdaptiveDecisionPhase.Idle, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(nextSession, 0, h.Initial);
        Assert.Equal(nextSession, h.Scheduler.Work[^1].Request.Identity.SessionId);
    }

    [Fact]
    public void NewSessionWaitsForCancelledOldWorkerBeforeScheduling()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        var old = h.Scheduler.Work[0];
        var nextSession = h.Coordinator.Begin(h.Craft, h.Goal, AdaptiveCoreTests.Limits());
        h.Coordinator.Observe(nextSession, 0, h.Initial);
        Assert.Single(h.Scheduler.Work);
        old.Completion.SetException(new InvalidOperationException("Late old-session error"));
        h.Coordinator.Poll();
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(AdaptiveDecisionPhase.Searching, h.Coordinator.Snapshot.Phase);
        Assert.Equal(1, h.Coordinator.Snapshot.RejectedResults);
    }

    [Fact]
    public void LeaseCanBeReservedAndConsumedOnlyOnce()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        Assert.False(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out _));
        Assert.False(h.Coordinator.TryConsumeLease(lease, h.Session, 1, h.Initial));
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
    }

    [Fact]
    public void DuplicateReservationAndForeignLeaseCannotExecute()
    {
        using var h = new Harness();
        using var foreign = new Harness();
        h.Ready();
        foreign.Ready();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out var own));
        Assert.False(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out _));
        Assert.True(foreign.Coordinator.TryCreateLease(foreign.Session, 1, foreign.Initial, out var other));
        Assert.False(h.Coordinator.TryConsumeLease(other!, h.Session, 1, h.Initial));
        Assert.True(h.Coordinator.TryConsumeLease(own!, h.Session, 1, h.Initial));
    }

    [Fact]
    public void ExecutionTimeStateMismatchRevokesReadyPlanAndLease()
    {
        using var h = new Harness();
        h.Ready();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out var lease));
        Assert.False(h.Coordinator.TryConsumeLease(lease!, h.Session, 1, h.Initial with { CP = 599 }));
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        Assert.False(h.Coordinator.TryConsumeLease(lease!, h.Session, 1, h.Initial));
    }

    [Fact]
    public void DelayedLeaseCannotSurviveNewObservationEvenWhenStateIsIdentical()
    {
        using var h = new Harness();
        h.Ready();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out var lease));
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        Assert.False(h.Coordinator.TryConsumeLease(lease!, h.Session, 1, h.Initial));
        Assert.False(h.Coordinator.TryConsumeLease(lease!, h.Session, 2, h.Initial));
    }

    [Fact]
    public void AcceptedExecutionDoesNotAdvanceUntilActualStateChanges()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        Assert.True(h.Coordinator.ReportExecution(lease, true));
        Assert.False(h.Coordinator.ReportExecution(lease, true));
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 3, lease.PredictedState);
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(new[] { VulcanSkill.BasicSynthesis }, h.Scheduler.Work[1].Request.Continuation);
        Assert.Equal(lease.PredictedState, h.Scheduler.Work[1].Request.Identity.State);
    }

    [Fact]
    public void ObservationBeforeExecutionResponseWaitsThenReconciles()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        h.Coordinator.Observe(h.Session, 2, lease.PredictedState);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, h.Coordinator.Snapshot.Phase);
        Assert.True(h.Coordinator.ReportExecution(lease, true));
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Fact]
    public void RejectedExecutionRetainsWholePlanAndRequiresFreshRead()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        Assert.True(h.Coordinator.ReportExecution(lease, false));
        Assert.False(h.Coordinator.ReportExecution(lease, true));
        h.Coordinator.Poll();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        Assert.Equal(new[] { VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis }, h.Scheduler.Work[1].Request.Continuation);
    }

    [Fact]
    public void UnknownConditionChangeIsAllowedButOtherPredictionMismatchFailsSession()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        h.Coordinator.ReportExecution(lease, true);
        h.Coordinator.Observe(h.Session, 2, lease.PredictedState with { Condition = Condition.Excellent });
        Assert.Equal(AdaptiveDecisionPhase.Searching, h.Coordinator.Snapshot.Phase);
        Assert.Equal(0, h.Coordinator.Snapshot.PredictionMismatches);

        using var mismatch = new Harness();
        mismatch.Ready();
        lease = mismatch.Consume();
        mismatch.Coordinator.ReportExecution(lease, true);
        mismatch.Coordinator.Observe(mismatch.Session, 2, lease.PredictedState with { Progress = 0 });
        Assert.Equal(AdaptiveDecisionPhase.Failed, mismatch.Coordinator.Snapshot.Phase);
        Assert.Equal(1, mismatch.Coordinator.Snapshot.PredictionMismatches);
        Assert.False(mismatch.Coordinator.Observe(mismatch.Session, 3, lease.PredictedState));
    }

    [Theory]
    [InlineData(Condition.Good, VulcanSkill.BasicSynthesis, Condition.Excellent)]
    [InlineData(Condition.Excellent, VulcanSkill.BasicSynthesis, Condition.Normal)]
    [InlineData(Condition.Poor, VulcanSkill.BasicSynthesis, Condition.Good)]
    [InlineData(Condition.Normal, VulcanSkill.BasicSynthesis, Condition.Poor)]
    [InlineData(Condition.Normal, VulcanSkill.FinalAppraisal, Condition.Good)]
    public void ForcedConditionMismatchFailsRatherThanBeingTreatedAsRandom(Condition before, VulcanSkill action, Condition actual)
    {
        using var h = new Harness();
        var initial = h.Initial with { Condition = before };
        h.Coordinator.Observe(h.Session, 1, initial);
        if (action == VulcanSkill.FinalAppraisal)
            h.Scheduler.Work[0].Complete(action, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        else h.Scheduler.Work[0].Complete(action, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, initial, out var lease));
        Assert.True(h.Coordinator.TryConsumeLease(lease!, h.Session, 1, initial));
        h.Coordinator.ReportExecution(lease!, true);
        h.Coordinator.Observe(h.Session, 2, lease!.PredictedState with { Condition = actual });
        Assert.Equal(AdaptiveDecisionPhase.Failed, h.Coordinator.Snapshot.Phase);
        Assert.Equal(1, h.Coordinator.Snapshot.PredictionMismatches);
    }

    [Fact]
    public async Task ConcurrentConsumersStillConsumeExactlyOnce()
    {
        using var h = new Harness();
        h.Ready();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out var lease));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            h.Coordinator.TryConsumeLease(lease!, h.Session, 1, h.Initial))));
        Assert.Equal(1, attempts.Count(success => success));
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, h.Coordinator.Snapshot.Phase);
    }

    [Fact]
    public void WholeCraftLifecycleEndsWithoutSchedulingAnotherAction()
    {
        using var h = new Harness();
        h.Ready();
        var first = h.Consume();
        h.Coordinator.ReportExecution(first, true);
        h.Coordinator.Observe(h.Session, 2, first.PredictedState);
        h.Scheduler.Work[1].Complete(VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 2, first.PredictedState, out var second));
        Assert.True(h.Coordinator.TryConsumeLease(second!, h.Session, 2, first.PredictedState));
        h.Coordinator.ReportExecution(second!, true);
        h.Coordinator.Observe(h.Session, 3, second!.PredictedState);
        Assert.Equal(AdaptiveDecisionPhase.Completed, h.Coordinator.Snapshot.Phase);
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        Assert.False(h.Coordinator.TryCreateLease(h.Session, 3, second.PredictedState, out _));
    }

    [Fact]
    public void NonStepActionIsDistinguishedByRevisionAndBuffState()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Scheduler.Work[0].Complete(VulcanSkill.FinalAppraisal, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        var lease = h.Consume();
        Assert.Equal(VulcanSkill.FinalAppraisal, lease.Action);
        Assert.Equal(h.Initial.FirstStep, lease.PredictedState.FirstStep);
        h.Coordinator.ReportExecution(lease, true);
        h.Coordinator.Observe(h.Session, 2, lease.PredictedState);
        Assert.Equal(AdaptiveDecisionPhase.Searching, h.Coordinator.Snapshot.Phase);
        Assert.Equal(3, h.Scheduler.Work[1].Request.Continuation.Count);
    }

    [Fact]
    public void MissingLiveReadCancelsWorkWithoutSimulatedFallback()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Coordinator.Observe(h.Session, 2, null);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        h.Scheduler.Work[0].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        Assert.Single(h.Scheduler.Work);
        h.Coordinator.Observe(h.Session, 3, h.Initial);
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Fact]
    public void MissingReadDuringExecutionPreservesUncertaintyUntilConfirmed()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        h.Coordinator.Observe(h.Session, 2, null);
        h.Coordinator.ReportExecution(lease, true);
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 3, h.Initial);
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 4, lease.PredictedState);
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Fact]
    public void PauseCancelsSearchAndResumeRequiresNewerObservation()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Coordinator.Pause();
        h.Scheduler.Work[0].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Paused, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        h.Coordinator.Resume();
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        h.Coordinator.Poll();
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 3, h.Initial);
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Fact]
    public void PauseRevokesReservedLease()
    {
        using var h = new Harness();
        h.Ready();
        Assert.True(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out var lease));
        h.Coordinator.Pause();
        Assert.False(h.Coordinator.TryConsumeLease(lease!, h.Session, 1, h.Initial));
    }

    [Fact]
    public void LateAcceptanceAfterResumeCannotReuseObservationFromPause()
    {
        using var h = new Harness();
        h.Ready();
        var lease = h.Consume();
        h.Coordinator.Pause();
        h.Coordinator.Observe(h.Session, 2, lease.PredictedState);
        h.Coordinator.Resume();
        h.Coordinator.ReportExecution(lease, true);
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        Assert.Single(h.Scheduler.Work);
        h.Coordinator.Observe(h.Session, 3, lease.PredictedState);
        Assert.Equal(2, h.Scheduler.Work.Count);
    }

    [Theory]
    [InlineData(false, AdaptiveDecisionPhase.Cancelled)]
    [InlineData(true, AdaptiveDecisionPhase.Completed)]
    public void StopAndFinishDiscardLateResults(bool complete, AdaptiveDecisionPhase expected)
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Coordinator.Stop(complete);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete(VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis);
        h.Coordinator.Poll();
        Assert.Equal(expected, h.Coordinator.Snapshot.Phase);
        Assert.False(h.Coordinator.Observe(h.Session, 2, h.Initial));
        Assert.False(h.Coordinator.TryCreateLease(h.Session, 1, h.Initial, out _));
    }

    [Fact]
    public void DisposeObservesLateFaultAndCannotPublishOrRestart()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Coordinator.Dispose();
        h.Scheduler.Work[0].Completion.SetException(new InvalidOperationException("Worker ignored cancellation"));
        Assert.Equal(AdaptiveDecisionPhase.Cancelled, h.Coordinator.Snapshot.Phase);
        Assert.Throws<ObjectDisposedException>(() => h.Coordinator.Poll());
        Assert.Throws<ObjectDisposedException>(() => h.Coordinator.Observe(h.Session, 2, h.Initial));
        h.Coordinator.Dispose();
    }

    [Fact]
    public void WorkerFailureAndUnknownResultNeverExposeAnAction()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        h.Scheduler.Work[0].Completion.SetException(new InvalidOperationException("Test failure"));
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Failed, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
        h.Coordinator.Observe(h.Session, 2, h.Initial);
        h.Scheduler.Work[1].Completion.SetResult(new(AdaptiveSearchStatus.Unknown, null, new(1, 1, 1, TimeSpan.Zero, true), "No validated route."));
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
    }

    private sealed class BrokenScheduler(int mode) : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => mode switch
            {
                0 => throw new InvalidOperationException("Could not start worker"),
                1 => null!,
                _ => Task.FromResult<AdaptiveSearchResult>(null!),
            };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedSchedulerCannotPublishOrThrowOutOfPoll(int mode)
    {
        using var coordinator = new AdaptiveSolveCoordinator(new BrokenScheduler(mode));
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        var session = coordinator.Begin(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly));
        coordinator.Observe(session, 1, AdaptiveCoreTests.Initial());
        coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Failed, coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, coordinator.Snapshot.Action);
    }

    [Fact]
    public void WorkerPlanIsReplayedRatherThanTrusted()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        var invalid = new AdaptivePlan([VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis], h.Initial, 6, true);
        h.Scheduler.Work[0].Completion.SetResult(new(AdaptiveSearchStatus.Found, invalid, new(0, 0, 0, TimeSpan.Zero, false), "Invalid test plan"));
        h.Coordinator.Poll();
        Assert.Equal(AdaptiveDecisionPhase.Failed, h.Coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.None, h.Coordinator.Snapshot.Action);
    }

    [Fact]
    public void TerminalObservationsDoNotStartWorkers()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial with { Progress = 200, Durability = 0 });
        Assert.Equal(AdaptiveDecisionPhase.Completed, h.Coordinator.Snapshot.Phase);
        Assert.Empty(h.Scheduler.Work);
        using var failure = new Harness();
        failure.Coordinator.Observe(failure.Session, 1, failure.Initial with { Durability = 0 });
        Assert.Equal(AdaptiveDecisionPhase.Failed, failure.Coordinator.Snapshot.Phase);
        Assert.Empty(failure.Scheduler.Work);
    }

    [Fact]
    public void InvalidLiveStateBlocksAndOlderOrForeignObservationsCannotInvalidateReady()
    {
        using var h = new Harness();
        h.Ready();
        Assert.False(h.Coordinator.Observe(Guid.NewGuid(), 999, null));
        Assert.False(h.Coordinator.Observe(h.Session, 0, null));
        Assert.Equal(AdaptiveDecisionPhase.Ready, h.Coordinator.Snapshot.Phase);
        Assert.False(h.Coordinator.Observe(h.Session, 2, h.Initial with { Condition = Condition.Unknown }));
        Assert.Equal(AdaptiveDecisionPhase.Blocked, h.Coordinator.Snapshot.Phase);
        h.Coordinator.Observe(h.Session, 3, h.Initial);
        Assert.Equal(AdaptiveDecisionPhase.Searching, h.Coordinator.Snapshot.Phase);
    }

    [Fact]
    public void SettingsAreCapturedAndContinuationStorageIsOwned()
    {
        using var h = new Harness();
        h.Coordinator.Observe(h.Session, 1, h.Initial);
        var identity = h.Scheduler.Work[0].Request.Identity;
        var editedLimits = identity.Limits with { BeamWidth = 1 };
        Assert.NotEqual(editedLimits, identity.Limits);
        Assert.Equal(128, identity.Limits.BeamWidth);
        var actions = new[] { VulcanSkill.BasicSynthesis };
        var request = new AdaptiveSearchRequest(identity, actions);
        actions[0] = VulcanSkill.None;
        Assert.Equal(VulcanSkill.BasicSynthesis, Assert.Single(request.Continuation));
    }
}
