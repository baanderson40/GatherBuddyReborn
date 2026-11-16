using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveLiveAnalysisTests
{
    private static readonly AdaptiveCraftSnapshot Craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredProgress = 600 };
    private static readonly AdaptiveObservedFrame Initial = new(1, 0, 0, 80, 664, Condition.Normal);
    private static readonly AdaptiveCraftGoal Goal = AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.ProgressOnly);
    private sealed class Scheduler(Func<AdaptiveSearchRequest, Task<AdaptiveSearchResult>>? solve = null) : IAdaptiveSearchScheduler
    {
        public int Calls;
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        { ++Calls; return solve?.Invoke(request) ?? Task.FromResult(Result(request)); }
    }
    private static AdaptiveSearchResult Result(AdaptiveSearchRequest request, VulcanSkill[]? actions = null)
    {
        var simulation = new AdaptiveSimulation(request.Identity.Craft, request.Identity.Goal);
        actions ??= request.Identity.State.Progress == 0 ? new[] { VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis } : new[] { VulcanSkill.BasicSynthesis };
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, request.Identity.State, actions, out var plan));
        return new(AdaptiveSearchStatus.Found, plan, new(12, 4, 0, TimeSpan.FromMilliseconds(1), false), "Synthetic diagnostic test plan.");
    }
    private sealed class Host : IDisposable
    {
        public readonly Scheduler Scheduler = new();
        public readonly AdaptiveSolveCoordinator Coordinator;
        public readonly AdaptiveLiveTrial Trial;
        public AdaptiveObservedFrame Frame = Initial;
        public TimeSpan Now;
        private long _frame, _sequence = 2;
        public Host(int delayMs = 0)
        {
            Coordinator = new(Scheduler);
            Trial = new(Coordinator, Craft, Goal, Initial, new(1, Craft.RecipeId, 0, 2), Now, TimeSpan.FromMilliseconds(delayMs));
        }
        public void Tick(bool paused = false) => Trial.Tick(++_frame, Now += TimeSpan.FromMilliseconds(10), Frame, false, paused);
        public void Ready() { Tick(); Tick(); }
        public void SendAndConfirm(bool nativeReturn)
        {
            var action = Trial.Decision.Action;
            var simulation = new AdaptiveSimulation(Craft, Goal);
            var outcome = simulation.TryApply(Trial.Evidence.ConfirmedState!.Value, action, out var predicted);
            var terminal = outcome != AdaptiveOutcome.Nonterminal;
            var after = Frame with { StepIndex = Frame.StepIndex + 1, Progress = Math.Min(predicted.Progress, Craft.RequiredProgress),
                Durability = predicted.Durability, CP = predicted.CP, Quality = predicted.Quality, Condition = predicted.Condition };
            Assert.Equal(AdaptiveDispatchResult.AwaitingOutcome, Trial.TryDispatch(Now, Frame, (uint)action, true, () => nativeReturn));
            Now += TimeSpan.FromSeconds(2);
            Trial.ObserveNative(new(1, ++_sequence, 9, Craft.RecipeId, 0, (uint)action, terminal ? Frame.StepIndex : after.StepIndex,
                after.Progress, after.Quality, after.Durability, 1, terminal ? 22u : 18u), Now);
            if (!terminal) { Frame = after; Tick(); Tick(); }
        }
        public void Dispose() { Trial.Dispose(); Coordinator.Dispose(); }
    }

    [Fact]
    public void StableFramesProduceOneRootAndDiagnosticsNeverScheduleAdditionalWork()
    {
        using var host = new Host(); host.Ready();
        var snapshot = host.Trial.Analysis;
        var root = Assert.Single(snapshot.Roots);
        Assert.Equal(Initial, root.NativeFrame);
        Assert.Equal(host.Trial.Decision.Decision, root.Identity);
        Assert.Equal(0, root.ConfirmedActionOrdinal);
        Assert.Equal("Ready", root.SearchOutcome);
        Assert.Equal(VulcanSkill.BasicSynthesis, root.ProposedAction);
        Assert.Null(root.Transaction);
        Assert.NotNull(root.Plan);
        Assert.Equal(2, root.Plan.ActionCount);
        Assert.Equal(6, root.Plan.ModeledDuration);
        Assert.True(root.Plan.TargetReached);
        Assert.Equal(0, root.Plan.FinalQuality);
        Assert.False(root.Plan.Executable);
        Assert.Equal(12, root.Diagnostics!.Transitions);
        Assert.Equal(40000, root.Identity.Limits.MaximumTransitions);
        Assert.Equal(20000, root.Identity.Limits.MaximumStoredNodes);
        var version = host.Trial.AnalysisVersion;
        for (var index = 0; index < 5; ++index) host.Tick();
        Assert.Equal(1, host.Scheduler.Calls);
        Assert.Equal(version, host.Trial.AnalysisVersion);
        Assert.Equal(1, host.Trial.Analysis.TotalRoots);
        Assert.Null(root.Transaction); // Detached old snapshot never changes.
        Assert.Throws<NotSupportedException>(() => ((IList<VulcanSkill>)root.Plan.Actions).Clear());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransactionTicketAndObservedTimingAttachOnlyToExactRoot(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var before = host.Trial.Analysis;
        host.SendAndConfirm(nativeReturn);
        Assert.Equal(2, host.Trial.Analysis.TotalRoots);
        var root = host.Trial.Analysis.Roots[0];
        Assert.Equal(nativeReturn, root.Transaction!.NativeReturn);
        Assert.Equal(1, root.Transaction.Ordinal);
        Assert.Equal(AdaptiveTransactionOutcome.Confirmed, root.Transaction.Outcome);
        Assert.True(root.Transaction.InvocationBegan);
        Assert.Equal(TimeSpan.Zero, root.SubmissionToResultObservation);
        Assert.Equal(TimeSpan.Zero, root.PublicationToDispatch);
        Assert.Equal(TimeSpan.FromSeconds(2), root.DispatchToAcknowledgement);
        Assert.Equal(TimeSpan.FromMilliseconds(2020), root.DispatchToFullFrame);
        Assert.Null(root.DispatchToTerminal);
        Assert.Null(root.RevokedAt);
        Assert.Null(before.Roots[0].Transaction);
        var version = host.Trial.AnalysisVersion;
        host.Tick(); host.Tick();
        Assert.Equal(version, host.Trial.AnalysisVersion); // Resolution timestamps cannot drift on later frames.
        host.SendAndConfirm(nativeReturn);
        Assert.Equal(2, host.Trial.Analysis.TotalRoots); // Terminal scalars never create another root.
        var final = host.Trial.Analysis.Roots[1];
        Assert.Equal(2, final.Transaction!.Ordinal);
        Assert.NotEqual(root.Transaction.Request, final.Transaction.Request);
        Assert.Equal(root.ProposedAction, final.ProposedAction); // Repeated action names do not drive linkage.
        Assert.Equal(AdaptiveTransactionOutcome.TerminalConfirmed, final.Transaction.Outcome);
        Assert.NotNull(final.TerminalAdmittedAt);
        Assert.Null(final.FullFrameConfirmedAt);
        Assert.False(final.Transaction.FullFrameCorrelated);
        Assert.Equal(host.Frame, final.NativeFrame);
        var view = new AdaptiveLiveView(host.Trial.Status, "Synthetic", host.Trial.Decision, false, host.Trial.Transaction,
            false, false, AdaptiveLiveEvidenceTrace.FromConfirmation(host.Trial.Evidence.CaptureTrace()), host.Trial.Analysis);
        using var json = JsonDocument.Parse(AdaptiveLiveExport.Serialize(view));
        Assert.Equal(4, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("LiveAnalysis").GetProperty("RetainedRoots").GetInt32());
        Assert.False(json.RootElement.GetProperty("NativeLayoutValidated").GetBoolean());
    }

    [Fact]
    public void PauseRevokesUndispatchedProposalAndFreshResumeGetsNewIdentity()
    {
        using var host = new Host(); host.Ready(); host.Tick(paused: true);
        var old = Assert.Single(host.Trial.Analysis.Roots);
        Assert.NotNull(old.RevokedAt);
        Assert.Null(old.Transaction);
        host.Tick(); host.Tick();
        var next = host.Trial.Analysis.Roots[^1];
        Assert.Equal(2, host.Trial.Analysis.TotalRoots);
        Assert.NotEqual(old.Identity, next.Identity);
        Assert.Equal(old.NativeFrame, next.NativeFrame);
        Assert.Null(next.RevokedAt);
    }

    [Fact]
    public void ExceptionPreservesInvocationAndErrorWithoutPretendingNoDispatchOccurred()
    {
        using var host = new Host(); host.Ready();
        host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, () => throw new InvalidOperationException());
        var root = Assert.Single(host.Trial.Analysis.Roots);
        Assert.Equal(AdaptiveTransactionOutcome.Exception, root.Transaction!.Outcome);
        Assert.True(root.Transaction.InvocationBegan);
        Assert.NotNull(root.DispatchInvokedAt);
        Assert.Null(root.AcknowledgementMatchedAt);
        Assert.Null(root.RevokedAt);
        Assert.NotNull(root.CloseReason);
    }

    [Fact]
    public void ConfiguredDelayIsPublicationToDispatchNotSearchElapsed()
    {
        using var host = new Host(100); host.Ready();
        host.Now += TimeSpan.FromMilliseconds(100);
        host.SendAndConfirm(true);
        var root = host.Trial.Analysis.Roots[0];
        Assert.Equal(TimeSpan.FromMilliseconds(100), root.PublicationToDispatch);
        Assert.Equal(TimeSpan.FromMilliseconds(1), root.Diagnostics!.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(2), root.DispatchToAcknowledgement);
    }

    [Fact]
    public void CoordinatorCaptureTimeIsNotReplacedByLaterDiagnosticDrainTime()
    {
        var session = Guid.NewGuid(); var identity = Identity(session, 1);
        var history = new AdaptiveLiveAnalysisHistory(session, TimeSpan.Zero);
        history.Submit(identity, 1, 0, Initial, TimeSpan.FromSeconds(1));
        history.Observe(new(identity, "SearchResultObserved", "Observed", AdaptiveSearchStatus.Found, null, null, null,
            CapturedAt: TimeSpan.FromMilliseconds(1500)), TimeSpan.FromSeconds(5));
        history.Observe(Publication(identity) with { CapturedAt = TimeSpan.FromSeconds(2) }, TimeSpan.FromSeconds(5));
        var root = Assert.Single(history.Capture().Roots);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), root.ResultObservedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), root.PublishedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(500), root.SubmissionToResultObservation);
    }

    private static AdaptiveDecisionIdentity Identity(Guid session, int generation) => new(session, generation, generation, Craft,
        Goal, new(), Initial.ToState(true, VulcanSkill.None));
    private static AdaptiveCoordinatorDiagnostic Publication(AdaptiveDecisionIdentity identity) => new(identity, "SearchPublication", "Validated synthetic proposal",
        AdaptiveSearchStatus.Found, new(2, 1, 0, TimeSpan.Zero, false), AdaptiveDecisionPhase.Ready,
        new(Array.AsReadOnly(new[] { VulcanSkill.BasicSynthesis }), 0, true, 1, 3), ProposedAction: VulcanSkill.BasicSynthesis);

    [Fact]
    public void RootAndUnlinkedRingsStayBoundedAndCannotGuessEvictedOrOtherSessionLinkage()
    {
        var session = Guid.NewGuid();
        var history = new AdaptiveLiveAnalysisHistory(session, TimeSpan.Zero);
        for (var index = 1; index <= 80; ++index) history.Submit(Identity(session, index), index, index, Initial, TimeSpan.FromSeconds(index));
        history.Submit(Identity(session, 1), 1, 1, Initial, TimeSpan.FromSeconds(81)); // Evicted duplicate cannot become a new root.
        var snapshot = history.Capture();
        Assert.Equal(80, snapshot.TotalRoots);
        Assert.Equal(64, snapshot.RetainedRoots);
        Assert.Equal(16, snapshot.DroppedRoots);
        Assert.Equal(17, snapshot.Roots[0].RootOrdinal);
        history.Observe(Publication(Identity(session, 1)), TimeSpan.FromSeconds(82));
        for (var index = 0; index < 80; ++index) history.Observe(Publication(Identity(Guid.NewGuid(), 80)), TimeSpan.FromSeconds(83));
        var final = history.Capture();
        Assert.Equal(81, final.TotalUnlinkedEvents);
        Assert.Equal(17, final.DroppedLifecycleEvents);
        Assert.Equal(64, final.UnlinkedLifecycleEvents.Count);
        Assert.All(final.Roots, root => Assert.Null(root.Plan));
        Assert.All(final.UnlinkedLifecycleEvents, entry => Assert.Contains("Other-session", entry.Reason));
        Assert.Empty(snapshot.UnlinkedLifecycleEvents);
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveLiveRootRecord>)snapshot.Roots).Clear());
    }

    [Fact]
    public void ConflictingTicketsAndMalformedDiagnosticsAreExplicitWithoutGrowingRecords()
    {
        var session = Guid.NewGuid(); var identity = Identity(session, 1);
        var history = new AdaptiveLiveAnalysisHistory(session, TimeSpan.Zero);
        history.Submit(identity, 1, 0, Initial, TimeSpan.Zero);
        var actions = new List<VulcanSkill> { VulcanSkill.BasicSynthesis };
        history.Observe(Publication(identity) with { Plan = new(actions, 0, true, 1, 3) }, TimeSpan.Zero);
        actions.Clear();
        Assert.Single(history.Capture().Roots[0].Plan!.Actions);
        var transaction = new AdaptiveLiveTransaction(new(Guid.NewGuid(), 1), VulcanSkill.BasicSynthesis, 100001, 1, TimeSpan.Zero, true, false);
        history.Transaction(identity, transaction, TimeSpan.Zero);
        history.Transaction(identity, transaction with { Request = new(Guid.NewGuid(), 1) }, TimeSpan.Zero);
        Assert.Equal(transaction.Request, history.Capture().Roots[0].Transaction!.Request);
        Assert.Equal(1, history.Capture().TotalUnlinkedEvents);
        history.Observe(Publication(identity) with { Plan = new(Enumerable.Repeat(VulcanSkill.Observe, 65).ToArray(), 0, true, 65, 130),
            Detail = new string('x', 5000) }, TimeSpan.Zero);
        var root = history.Capture().Roots[0];
        Assert.Null(root.Plan);
        Assert.Contains("omitted", root.CaptureWarning);
        Assert.True(root.Reason!.Length < 1040);
        Assert.Equal(VulcanSkill.BasicSynthesis, root.ProposedAction);
    }

    [Theory]
    [InlineData(AdaptiveSearchStatus.Unknown, AdaptiveDecisionPhase.Blocked)]
    [InlineData(AdaptiveSearchStatus.ProvenImpossible, AdaptiveDecisionPhase.Blocked)]
    [InlineData(AdaptiveSearchStatus.Cancelled, AdaptiveDecisionPhase.Cancelled)]
    public void FailedSearchStatusesNeverManufactureValidatedPlan(AdaptiveSearchStatus status, AdaptiveDecisionPhase phase)
    {
        var scheduler = new Scheduler(_ => Task.FromResult(new AdaptiveSearchResult(status, null, new(0, 0, 0, TimeSpan.Zero, false), "Synthetic non-plan result")));
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        coordinator.EnableDecisionDiagnostics();
        var session = coordinator.Begin(Craft, Goal);
        coordinator.Observe(session, 1, Initial.ToState(true, VulcanSkill.None)); coordinator.Poll();
        var entry = Assert.Single(coordinator.DrainDecisionDiagnostics(), item => item.Kind == "SearchPublication");
        Assert.Equal(phase, entry.PublicationPhase);
        Assert.Equal(status, entry.SearchStatus);
        Assert.Null(entry.Plan);
        Assert.Equal(1, scheduler.Calls);
    }

    [Fact]
    public void ReplayRejectedAndFaultedResultsStayUnpublished()
    {
        foreach (var fault in new[] { false, true })
        {
            var scheduler = new Scheduler(request => fault ? Task.FromException<AdaptiveSearchResult>(new InvalidOperationException())
                : Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found,
                    new(new[] { VulcanSkill.ByregotsBlessing }, request.Identity.State, 3, true), new(1, 1, 0, TimeSpan.Zero, false), "Invalid fake plan")));
            using var coordinator = new AdaptiveSolveCoordinator(scheduler);
            coordinator.EnableDecisionDiagnostics(); var session = coordinator.Begin(Craft, Goal);
            coordinator.Observe(session, 1, Initial.ToState(true, VulcanSkill.None)); coordinator.Poll();
            var entry = Assert.Single(coordinator.DrainDecisionDiagnostics(), item => item.Kind == "SearchPublication");
            Assert.Equal(AdaptiveDecisionPhase.Failed, entry.PublicationPhase);
            Assert.Null(entry.Plan);
            Assert.Equal(VulcanSkill.None, entry.ProposedAction);
            Assert.Equal(1, scheduler.Calls);
        }
    }

    [Fact]
    public void DiagnosticActionCapDoesNotChangeValidatedExecutionPlanOrLimits()
    {
        var actions = Enumerable.Repeat(VulcanSkill.Observe, 63).Concat(new[] { VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis }).ToArray();
        var scheduler = new Scheduler(request => Task.FromResult(Result(request, actions)));
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        coordinator.EnableDecisionDiagnostics(); var session = coordinator.Begin(Craft, Goal, new() { MaximumPlanLength = 80 });
        coordinator.Observe(session, 1, Initial.ToState(true, VulcanSkill.None)); coordinator.Poll();
        var entry = Assert.Single(coordinator.DrainDecisionDiagnostics(), item => item.Kind == "SearchPublication");
        Assert.Equal(AdaptiveDecisionPhase.Ready, coordinator.Snapshot.Phase);
        Assert.Equal(VulcanSkill.Observe, coordinator.Snapshot.Action);
        Assert.Equal(VulcanSkill.Observe, entry.ProposedAction);
        Assert.Null(entry.Plan);
        Assert.Contains("omitted", entry.CaptureWarning);
        Assert.Equal(80, entry.Identity.Limits.MaximumPlanLength);
        Assert.Equal(1, scheduler.Calls);
    }

    [Fact]
    public void CoordinatorNotificationsAreBoundedEvenWithoutConsumerDrain()
    {
        using var coordinator = new AdaptiveSolveCoordinator(new Scheduler());
        coordinator.EnableDecisionDiagnostics(); var session = coordinator.Begin(Craft, Goal);
        for (var revision = 1; revision <= 100; ++revision) coordinator.Observe(session, revision, Initial.ToState(true, VulcanSkill.None));
        Assert.Equal(64, coordinator.DrainDecisionDiagnostics().Length);
        Assert.True(coordinator.DroppedDecisionDiagnostics > 0);
        Assert.Empty(coordinator.DrainDecisionDiagnostics());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SeedDiagnosticsAreDetachedAndOversizedBatchesDoNotAffectAction(int count)
    {
        var candidates = Enumerable.Range(0, count).Select(index => new AdaptiveSeedAssessment("Synthetic", index,
            "Validated", "Fake seed diagnostic", 0, true, 2, 6)).ToList();
        var scheduler = new Scheduler(request =>
        {
            var result = Result(request);
            return Task.FromResult(result with { Diagnostics = result.Diagnostics with
            { Seeding = new(candidates, "Synthetic", 0, true, 2, 6, 4, TimeSpan.FromMilliseconds(0.1), "Tied") } });
        });
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        coordinator.EnableDecisionDiagnostics(); var session = coordinator.Begin(Craft, Goal);
        coordinator.Observe(session, 1, Initial.ToState(true, VulcanSkill.None)); coordinator.Poll();
        var entry = Assert.Single(coordinator.DrainDecisionDiagnostics(), item => item.Kind == "SearchPublication");
        candidates.Clear();
        Assert.Equal(VulcanSkill.BasicSynthesis, coordinator.Snapshot.Action);
        Assert.NotNull(entry.Plan);
        if (count == 2)
        {
            Assert.Equal(2, entry.Diagnostics!.Seeding!.Candidates.Count);
            Assert.Equal("Tied", entry.Diagnostics.Seeding.Comparison);
            Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveSeedAssessment>)entry.Diagnostics.Seeding.Candidates).Clear());
        }
        else { Assert.Null(entry.Diagnostics); Assert.Contains("omitted", entry.CaptureWarning); }
        Assert.Equal(1, scheduler.Calls);
    }
}
