using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveShadowSessionTests
{
    [Fact]
    public void SeedCursorWaitsForCorrelatedOutcomeIncludingNonStepActionAfterRejection()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        var initial = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        var seed = new AdaptivePlanSeed(new("RaphaelCache", "session-test", craft.RecipeId, craft.Level,
            craft.Craftsmanship, craft.Control, craft.MaxCP, craft.ManipulationUnlocked, false, 0),
            new[] { VulcanSkill.FinalAppraisal, VulcanSkill.GreatStrides, VulcanSkill.CarefulSynthesis });
        var scheduler = new Scheduler();
        using var shadow = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero,
            AdaptiveCoreTests.Limits(), scheduler, seed: seed);
        shadow.Observe(1, TimeSpan.FromMilliseconds(1), initial, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(2), initial, false);
        var rejected = shadow.RecordRequest(initial, VulcanSkill.FinalAppraisal, TimeSpan.FromMilliseconds(2));
        Assert.True(shadow.ReportRequest(rejected, false));
        shadow.Observe(3, TimeSpan.FromMilliseconds(3), initial, false);
        shadow.Observe(4, TimeSpan.FromMilliseconds(4), initial, false);
        scheduler.Work[0].CompleteUnknown();
        shadow.Observe(5, TimeSpan.FromMilliseconds(5), initial, false);
        Assert.Equal(0, Assert.Single(scheduler.Work[1].Request.Seeds).Cursor);
        var accepted = shadow.RecordRequest(initial, VulcanSkill.FinalAppraisal, TimeSpan.FromMilliseconds(5));
        Assert.True(shadow.ReportRequest(accepted, true));
        Assert.Equal(0, shadow.CorrelatedActions);
        var after = initial with { CP = 599, FinalAppraisal = 5 }; // No step increment.
        shadow.Observe(6, TimeSpan.FromMilliseconds(6), after, false);
        Assert.Equal(0, shadow.CorrelatedActions);
        shadow.Observe(7, TimeSpan.FromMilliseconds(7), after, false);
        Assert.Equal(1, shadow.CorrelatedActions);
        scheduler.Work[1].CompleteUnknown();
        shadow.Observe(8, TimeSpan.FromMilliseconds(8), after, false);
        Assert.Equal(1, Assert.Single(scheduler.Work[2].Request.Seeds).Cursor);
        Assert.Equal(VulcanSkill.GreatStrides, scheduler.Work[2].Request.Seeds[0].Actions[0]);
    }

    private sealed class Pending(AdaptiveSearchRequest request, CancellationToken token)
    {
        public AdaptiveSearchRequest Request { get; } = request;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<AdaptiveSearchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete()
        {
            var result = new AdaptiveSearchEngine().Search(new(Request.Identity.Craft, Request.Identity.Goal),
                Request.Identity.State, AdaptiveCoreTests.Limits());
            Completion.SetResult(result); // Deliberately ignore cancellation to test stale publication.
        }
        public void CompleteUnknown() => Completion.SetResult(new(AdaptiveSearchStatus.Unknown, null,
            new(5, 3, 1, TimeSpan.FromMilliseconds(2), true), "Test work budget exhausted without a plan."));
    }
    private sealed class Scheduler : IAdaptiveSearchScheduler
    {
        public List<Pending> Work { get; } = [];
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var pending = new Pending(request, cancellation);
            Work.Add(pending);
            return pending.Completion.Task;
        }
    }
    private sealed class Harness : IDisposable
    {
        public Scheduler Scheduler { get; } = new();
        public AdaptiveObservedFrame Initial { get; } = new(1, 0, 0, 80, 600, Condition.Normal);
        public AdaptiveShadowSession Shadow { get; }
        private long _frame;
        private int _time;
        public Harness()
        {
            var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
            Shadow = new(craft, Initial, TimeSpan.Zero, AdaptiveCoreTests.Limits(), Scheduler);
        }
        public void Sample(AdaptiveObservedFrame? frame, bool executing = false, int milliseconds = 10)
            => Shadow.Observe(++_frame, TimeSpan.FromMilliseconds(_time += milliseconds), frame, executing);
        public void Stable(AdaptiveObservedFrame frame) { Sample(frame); Sample(frame); }
        public AdaptiveShadowRequest Request(VulcanSkill action, AdaptiveObservedFrame? origin = null)
            => Shadow.RecordRequest(origin ?? Initial, action, TimeSpan.FromMilliseconds(_time));
        public void Dispose() => Shadow.Dispose();
    }

    [Fact]
    public void StableObservationsRequireDifferentHostFrames()
    {
        using var h = new Harness();
        h.Shadow.Observe(1, TimeSpan.FromMilliseconds(1), h.Initial, false);
        h.Shadow.Observe(1, TimeSpan.FromMilliseconds(2), h.Initial, false);
        Assert.Empty(h.Scheduler.Work);
        h.Shadow.Observe(2, TimeSpan.FromMilliseconds(3), h.Initial, false);
        Assert.Single(h.Scheduler.Work);
        Assert.Equal(h.Initial.Quality, h.Scheduler.Work[0].Request.Identity.State.Quality);
    }

    [Fact]
    public void TornAndExecutingSamplesNeverStartAnalysis()
    {
        using var h = new Harness();
        h.Sample(h.Initial);
        h.Sample(h.Initial with { Quality = 1 });
        h.Sample(h.Initial);
        h.Sample(h.Initial, executing: true);
        Assert.Empty(h.Scheduler.Work);
        h.Stable(h.Initial);
        Assert.Single(h.Scheduler.Work);
    }

    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    public void MidCraftOrExistingBuffHistoryCannotBeGuessed(int index, int progress, bool perfection)
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        var frame = new AdaptiveObservedFrame(index, progress, 0, 80, 600, Condition.Normal, TrainedPerfectionActive: perfection);
        Assert.Throws<ArgumentException>(() => new AdaptiveShadowSession(craft, frame, TimeSpan.Zero));
    }

    [Fact]
    public void FreshCapturePreservesActualIngredientQualityAndCp()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        var scheduler = new Scheduler();
        var frame = new AdaptiveObservedFrame(1, 0, 1234, 80, 587, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(craft, frame, TimeSpan.Zero, AdaptiveCoreTests.Limits(), scheduler);
        shadow.Observe(1, TimeSpan.FromMilliseconds(1), frame, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(2), frame, false);
        var state = Assert.Single(scheduler.Work).Request.Identity.State;
        Assert.Equal(1234, state.Quality);
        Assert.Equal(587, state.CP);
        Assert.Equal(craft.MaxQuality, shadow.Goal.DesiredQuality);
    }

    [Fact]
    public void RequestedActionAndReportedAcceptanceDoNotAdvanceHistory()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        Assert.NotEqual(default, ticket);
        Assert.True(h.Shadow.ReportRequest(ticket, true));
        h.Stable(h.Initial);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        Assert.Empty(h.Scheduler.Work);
        Assert.Empty(h.Shadow.HistoricalResults);
        h.Sample(h.Initial, milliseconds: 8100);
        Assert.True(h.Shadow.Blocked);
    }

    [Fact]
    public void StableNativeOutcomeIsCorrelatedWithoutClaimingActionIdentity()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        var observed = new AdaptiveObservedFrame(2, 0, 135, 70, 582, Condition.Normal, InnerQuiet: 1);
        h.Sample(observed);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        h.Sample(observed);
        Assert.Equal(1, h.Shadow.CorrelatedActions);
        var source = Assert.Single(h.Scheduler.Work).Request.Identity.State;
        Assert.Equal(135, source.Quality);
        Assert.Equal(582, source.CP);
        Assert.Equal(VulcanSkill.BasicTouch, source.Combo);
        Assert.Contains(h.Shadow.Events, e => e.Kind == "Correlated" && e.Detail.Contains("NOT native action-ID confirmation"));
    }

    [Fact]
    public void OutcomeBeforeRequestResponseWaitsForBothSources()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        var observed = new AdaptiveObservedFrame(2, 0, 135, 70, 582, Condition.Normal, InnerQuiet: 1);
        h.Stable(observed);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        h.Shadow.ReportRequest(ticket, true);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        h.Sample(observed);
        Assert.Equal(1, h.Shadow.CorrelatedActions);
    }

    [Fact]
    public void RejectedRequestRetainsOriginalHistory()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        Assert.True(h.Shadow.ReportRequest(ticket, false));
        Assert.False(h.Shadow.ReportRequest(ticket, true));
        h.Stable(h.Initial);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        Assert.Equal(VulcanSkill.None, Assert.Single(h.Scheduler.Work).Request.Identity.State.Combo);
    }

    [Theory]
    [InlineData(3, 582, 135)]
    [InlineData(2, 581, 135)]
    [InlineData(2, 582, 134)]
    public void NativeMismatchBlocksInsteadOfSubstitutingPrediction(int index, int cp, int quality)
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        var observed = new AdaptiveObservedFrame(index, 0, quality, 70, cp, Condition.Normal, InnerQuiet: 1);
        h.Stable(observed);
        Assert.True(h.Shadow.Blocked);
        Assert.Equal(0, h.Shadow.CorrelatedActions);
        Assert.Empty(h.Scheduler.Work);
        Assert.Contains(h.Shadow.Events, e => e.Kind == "Mismatch" && e.After == observed);
    }

    [Fact]
    public void UntrackedTransitionAndDuplicateRequestsFailClosed()
    {
        using var h = new Harness();
        h.Stable(new(2, 122, 0, 70, 600, Condition.Normal));
        Assert.True(h.Shadow.Blocked);
        Assert.Contains("Untracked", h.Shadow.Status);
        using var duplicate = new Harness();
        duplicate.Request(VulcanSkill.BasicTouch);
        Assert.Equal(default, duplicate.Request(VulcanSkill.BasicTouch));
        Assert.True(duplicate.Shadow.Blocked);
    }

    [Fact]
    public void UnsupportedRequestedActionDoesNotReachSearchOrExecutor()
    {
        using var h = new Harness();
        Assert.Equal(default, h.Request(VulcanSkill.HastyTouch));
        Assert.True(h.Shadow.Blocked);
        Assert.Empty(h.Scheduler.Work);
    }

    [Fact]
    public void TrackedStandardTouchChainUsesConfirmedOriginEligibility()
    {
        using var h = new Harness();
        var basic = new AdaptiveObservedFrame(2, 0, 135, 70, 582, Condition.Normal, InnerQuiet: 1);
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(basic);
        ticket = h.Request(VulcanSkill.StandardTouch, basic);
        h.Shadow.ReportRequest(ticket, true);
        var standard = new AdaptiveObservedFrame(3, 0, 320, 60, 564, Condition.Normal, InnerQuiet: 2);
        h.Stable(standard);
        Assert.Equal(2, h.Shadow.CorrelatedActions);
        Assert.False(h.Shadow.Blocked);
        Assert.Contains(h.Shadow.Events, e => e.Kind == "Correlated" && e.After == standard);
    }

    [Fact]
    public void StandaloneStandardTouchDoesNotInventComboDiscount()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.StandardTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(2, 0, 168, 70, 568, Condition.Normal, InnerQuiet: 1));
        Assert.Equal(VulcanSkill.None, Assert.Single(h.Scheduler.Work).Request.Identity.State.Combo);
    }

    [Fact]
    public void NonStepFinalAppraisalAndPerfectionUsageAreTrackedWithoutStepGuessing()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.FinalAppraisal);
        h.Shadow.ReportRequest(ticket, true);
        var appraisal = h.Initial with { CP = 599, FinalAppraisal = 5 };
        h.Stable(appraisal);
        Assert.Equal(1, h.Shadow.CorrelatedActions);
        Assert.True(Assert.Single(h.Scheduler.Work).Request.Identity.State.FirstStep);

        using var perfection = new Harness();
        ticket = perfection.Request(VulcanSkill.TrainedPerfection);
        perfection.Shadow.ReportRequest(ticket, true);
        perfection.Stable(perfection.Initial with { StepIndex = 2, TrainedPerfectionActive = true });
        var state = Assert.Single(perfection.Scheduler.Work).Request.Identity.State;
        Assert.True(state.TrainedPerfectionActive);
        Assert.False(state.TrainedPerfectionAvailable);
    }

    [Fact]
    public void ConditionRandomnessIsAllowedButForcedTransitionMismatchIsNot()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        var excellent = new AdaptiveObservedFrame(2, 0, 135, 70, 582, Condition.Excellent, InnerQuiet: 1);
        h.Stable(excellent);
        Assert.Equal(1, h.Shadow.CorrelatedActions);
        ticket = h.Request(VulcanSkill.BasicSynthesis, excellent);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(3, 122, 135, 60, 582, Condition.Normal, InnerQuiet: 1)); // Must be Poor.
        Assert.True(h.Shadow.Blocked);
    }

    [Fact]
    public void MissingFramesPreserveHistoricalResultsButEventuallyBlock()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        var retained = Assert.Single(h.Shadow.HistoricalResults);
        Assert.False(retained.Executable);
        h.Sample(null);
        Assert.Same(retained, Assert.Single(h.Shadow.HistoricalResults));
        h.Sample(null, milliseconds: 3100);
        Assert.True(h.Shadow.Blocked);
    }

    [Fact]
    public void LateWorkerResultAfterActionRequestCannotBecomeCurrent()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Request(VulcanSkill.BasicTouch);
        Assert.False(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.False(Assert.Single(h.Shadow.HistoricalResults).Executable);
        Assert.Equal(0, h.Shadow.AnalysisSummary.RevokedResultsDiscarded);
    }

    [Fact]
    public void PauseResumeNeedsFreshSamplesAndDoesNotTimeoutDuringPause()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Shadow.SetPaused(true, TimeSpan.Zero);
        h.Sample(h.Initial, milliseconds: 30_000);
        h.Shadow.SetPaused(false, TimeSpan.FromMilliseconds(30_000));
        var observed = new AdaptiveObservedFrame(2, 0, 135, 70, 582, Condition.Normal, InnerQuiet: 1);
        h.Sample(observed);
        Assert.Empty(h.Scheduler.Work);
        h.Sample(observed);
        Assert.False(h.Shadow.Blocked);
        Assert.Equal(1, h.Shadow.CorrelatedActions);
    }

    [Fact]
    public void SessionTicketsCannotLeakAcrossCraftsAndDisposedSessionCannotRestart()
    {
        using var first = new Harness();
        using var second = new Harness();
        var ticket = first.Request(VulcanSkill.BasicTouch);
        second.Request(VulcanSkill.BasicTouch);
        Assert.False(second.Shadow.ReportRequest(ticket, true));
        first.Shadow.Dispose();
        Assert.False(first.Shadow.ReportRequest(ticket, true));
        first.Stable(first.Initial);
        Assert.Empty(first.Scheduler.Work);
    }

    [Fact]
    public void TerminalProgressIsClampedAndFreshTraceStorageIsBounded()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 100 });
        var initial = new AdaptiveObservedFrame(1, 0, 0, 10, 600, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero, scheduler: new Scheduler());
        var ticket = shadow.RecordRequest(initial, VulcanSkill.BasicSynthesis, TimeSpan.Zero);
        shadow.ReportRequest(ticket, true);
        shadow.Observe(1, TimeSpan.FromMilliseconds(1), new(2, 100, 0, 0, 600, Condition.Normal), false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(2), new(2, 100, 0, 0, 600, Condition.Normal), false);
        Assert.Equal(1, shadow.CorrelatedActions);
        Assert.Equal("ObservedSuccess", shadow.Summary.TerminalObservation);
        Assert.Empty(shadow.HistoricalResults);
        using var h = new Harness();
        for (var i = 0; i < 100; ++i) { h.Shadow.SetPaused(true); h.Shadow.SetPaused(false); }
        Assert.InRange(h.Shadow.Events.Count, 1, 64);
        Assert.True(h.Shadow.EventVersion > 64);
    }

    [Fact]
    public void FinishedExportPreservesCorrelationsAndDoesNotInferTerminalSuccess()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(2, 0, 135, 70, 582, Condition.Normal, InnerQuiet: 1));
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        var trace = h.Shadow.CaptureTrace();
        var view = AdaptiveShadowView.FromTrace(h.Shadow.Status, trace);
        Assert.Equal(1, view.CorrelatedActions);
        Assert.DoesNotContain(trace.HistoricalResults, r => r.Executable);
        Assert.True(trace.Summary.ManagedCraftEnded);
        Assert.Equal("Managed craft finished.", trace.Summary.ManagedEndReason);
        Assert.Equal("Unobserved", trace.Summary.TerminalObservation);
        Assert.Equal(2, trace.Summary.LastStableFrame!.Value.StepIndex);
        using var json = System.Text.Json.JsonDocument.Parse(AdaptiveShadowExport.Serialize(view));
        Assert.Equal(6, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("CorrelatedActions").GetInt32());
        Assert.False(json.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
        var summary = json.RootElement.GetProperty("Data").GetProperty("Summary");
        Assert.Equal(1, summary.GetProperty("CorrelatedActions").GetInt32());
        Assert.Equal("Unobserved", summary.GetProperty("TerminalObservation").GetString());
    }

    [Fact]
    public void SingleTerminalSampleThenAddonLossRemainsUnobserved()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        var ticket = h.Request(VulcanSkill.Groundwork);
        h.Shadow.ReportRequest(ticket, true);
        h.Sample(new(2, 200, 0, 60, 582, Condition.Normal));
        h.Sample(null);
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        Assert.Equal("Unobserved", h.Shadow.Summary.TerminalObservation);
        Assert.Equal(0, h.Shadow.Summary.CorrelatedActions);
        Assert.Equal(1, h.Shadow.Summary.LastStableFrame!.Value.StepIndex);
    }

    [Fact]
    public void RepeatedMatchedTerminalSamplesRemainObservedAfterEndAndDispose()
    {
        using var h = new Harness();
        var ticket = h.Request(VulcanSkill.Groundwork);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(2, 200, 0, 60, 582, Condition.Normal));
        Assert.Equal("ObservedSuccess", h.Shadow.Summary.TerminalObservation);
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        h.Shadow.Dispose();
        Assert.Equal("ObservedSuccess", h.Shadow.Summary.TerminalObservation);
        Assert.Equal(1, h.Shadow.Summary.CorrelatedActions);
        Assert.Equal("Managed craft finished.", h.Shadow.Summary.ManagedEndReason);
    }

    [Fact]
    public void EndPreservesMismatchReasonEvenAfterTraceRingWraps()
    {
        using var h = new Harness();
        for (var i = 0; i < 50; ++i) { h.Shadow.SetPaused(true); h.Shadow.SetPaused(false); }
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(2, 0, 134, 70, 582, Condition.Normal, InnerQuiet: 1));
        var blockedReason = h.Shadow.Status;
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        Assert.Equal(blockedReason, h.Shadow.Summary.BlockedReason);
        Assert.Equal("Unobserved", h.Shadow.Summary.TerminalObservation);
        Assert.True(h.Shadow.Summary.DroppedEvents > 0);
        Assert.Equal(h.Shadow.Summary.TotalEvents - 64, h.Shadow.Summary.DroppedEvents);
    }

    [Fact]
    public void RapidNextRequestDoesNotCancelHistoricalAnalysis()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        var generation = h.Scheduler.Work[0].Request.Identity.Generation;
        h.Request(VulcanSkill.BasicTouch);
        Assert.False(h.Scheduler.Work[0].Token.IsCancellationRequested);
        Assert.Equal(1, h.Shadow.AnalysisSummary.ObservationsSubmitted);
        Assert.Equal(1, h.Shadow.AnalysisSummary.SearchesDispatched);
        Assert.Equal(0, h.Shadow.AnalysisSummary.SearchCancellationRequests);
        Assert.Equal(0, h.Shadow.AnalysisSummary.HistoricalResultsRetained);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Equal(1, h.Shadow.AnalysisSummary.SearchResultsObserved);
        Assert.Equal(0, h.Shadow.AnalysisSummary.RevokedResultsDiscarded);
        Assert.Equal(1, h.Shadow.AnalysisSummary.HistoricalResultsRetained);
        var historical = Assert.Single(h.Shadow.HistoricalResults);
        Assert.False(historical.Executable);
        Assert.Equal(VulcanSkill.BasicTouch, historical.Source.ExistingSolverAttempt);
        var result = Assert.Single(h.Shadow.Events, e => e.Kind == "SearchResultObserved");
        Assert.Equal(AdaptiveSearchStatus.Found, result.Search!.SearchStatus);
        Assert.Equal(generation, result.Search.Generation);
        Assert.NotNull(result.Search.Diagnostics);
        Assert.NotNull(result.Search.Limits);
        Assert.DoesNotContain(h.Shadow.Events, e => e.Kind == "SearchCancellationRequested");
        Assert.DoesNotContain(h.Shadow.Events, e => e.Kind == "Recommendation");
    }

    [Fact]
    public void NoPlanResultIncludesStatusBudgetAndNonPublicationReason()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].CompleteUnknown();
        h.Shadow.Poll();
        Assert.False(h.Shadow.Blocked);
        var publication = Assert.Single(h.Shadow.Events, e => e.Kind == "HistoricalResultRetained");
        Assert.Equal(AdaptiveSearchStatus.Unknown, publication.Search!.SearchStatus);
        Assert.True(publication.Search.Diagnostics!.BudgetExhausted);
        Assert.Contains("budget exhausted", publication.Detail);
        Assert.Null(Assert.Single(h.Shadow.HistoricalResults).ProposedFirstAction);
    }

    [Fact]
    public void HistoricalResultHasNamesAndAttemptCanBeLinkedAfterRetention()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Equal(1, h.Shadow.AnalysisSummary.HistoricalResultsRetained);
        var historical = Assert.Single(h.Shadow.HistoricalResults);
        Assert.Equal(historical.ProposedFirstAction.ToString(), historical.ProposedFirstActionName);
        h.Request(VulcanSkill.BasicTouch);
        Assert.Equal(VulcanSkill.BasicTouch, Assert.Single(h.Shadow.HistoricalResults).Source.ExistingSolverAttempt);
        Assert.False(Assert.Single(h.Shadow.HistoricalResults).Executable);
        var request = Assert.Single(h.Shadow.Events, e => e.Kind == "Request");
        Assert.Equal("BasicTouch", request.RequestedActionName);
    }

    [Fact]
    public void TimingsAndSequenceAreMonotonicAndQueueDelayIsExplicit()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Sample(null, milliseconds: 50);
        h.Stable(h.Initial);
        Assert.Single(h.Scheduler.Work); // Historical worker still computing original captured root.
        Assert.Equal(2, h.Shadow.AnalysisSummary.ObservationsSubmitted);
        Assert.Equal(1, h.Shadow.AnalysisSummary.SearchesDispatched);
        Assert.Contains(h.Shadow.Events, e => e.Kind == "SearchQueued" && e.Detail.Contains("Waiting for previous worker"));
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Equal(2, h.Shadow.AnalysisSummary.SearchesDispatched);
        var events = h.Shadow.Events;
        for (var i = 1; i < events.Count; ++i)
        {
            Assert.True(events[i].Sequence > events[i - 1].Sequence);
            Assert.True(events[i].Elapsed >= events[i - 1].Elapsed);
        }
        Assert.All(events.Where(e => e.Search != null), e => Assert.Equal(e.Elapsed, e.Search!.Elapsed));
    }

    [Fact]
    public void ExportIsDetachedReadOnlyBoundedAndSummarySurvivesRingEviction()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        var trace = h.Shadow.CaptureTrace();
        var view = AdaptiveShadowView.FromTrace(h.Shadow.Status, trace);
        var before = AdaptiveShadowExport.Serialize(view);
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveShadowEvent>)trace.Events).Clear());
        for (var i = 0; i < 100; ++i) { h.Shadow.SetPaused(true); h.Shadow.SetPaused(false); }
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        Assert.Equal(before, AdaptiveShadowExport.Serialize(view));
        var final = h.Shadow.CaptureTrace();
        Assert.Equal(64, final.Events.Count);
        Assert.Equal(final.Summary.TotalEvents - 64, final.Summary.DroppedEvents);
        Assert.Equal(1, final.Summary.HistoricalAnalysis.HistoricalResultsRetained);
        Assert.Equal(1, final.Summary.HistoricalAnalysis.SearchesDispatched);
        Assert.Equal(1, final.Summary.HistoricalAnalysis.SearchResultsObserved);
        Assert.DoesNotContain(final.Events, e => e.Kind == "Recommendation");
        Assert.DoesNotContain("ContentId", before);
        Assert.DoesNotContain("CharacterId", before);
    }

    [Fact]
    public void EndCancelsOutstandingWorkAndLateResultCannotMutateFinishedExport()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Shadow.End("Managed craft finished.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        var final = h.Shadow.CaptureTrace();
        Assert.Equal(1, final.Summary.HistoricalAnalysis.SearchCancellationRequests);
        Assert.Equal(0, final.Summary.HistoricalAnalysis.SearchResultsObserved);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Equal(final.Summary, h.Shadow.Summary);
        Assert.Empty(h.Shadow.HistoricalResults);
        Assert.Equal(default, h.Request(VulcanSkill.BasicTouch));
        h.Shadow.End("Later cleanup must not replace end reason.", TimeSpan.FromSeconds(2));
        Assert.Equal("Managed craft finished.", h.Shadow.Summary.ManagedEndReason);
    }

    [Fact]
    public void ObservedDurabilityFailureIsDistinctFromUnobservedAndManagedEnd()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        var initial = new AdaptiveObservedFrame(1, 0, 0, 10, 600, Condition.Normal);
        using var shadow = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero, scheduler: new Scheduler());
        var ticket = shadow.RecordRequest(initial, VulcanSkill.BasicSynthesis, TimeSpan.Zero);
        shadow.ReportRequest(ticket, true);
        var failure = new AdaptiveObservedFrame(2, 122, 0, 0, 600, Condition.Normal);
        shadow.Observe(1, TimeSpan.FromMilliseconds(10), failure, false);
        shadow.Observe(2, TimeSpan.FromMilliseconds(20), failure, false);
        shadow.End("Managed craft ended.", TimeSpan.FromSeconds(1), managedCraftEnded: true);
        Assert.Equal("ObservedFailure", shadow.Summary.TerminalObservation);
        Assert.Equal(1, shadow.Summary.CorrelatedActions);
        Assert.True(shadow.Summary.ManagedCraftEnded);
    }

    [Fact]
    public void WorkerFaultIsRecordedWithoutRecommendation()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].Completion.SetException(new InvalidOperationException("Test worker failure."));
        h.Shadow.Poll();
        Assert.Equal("WorkerFailed", Assert.Single(h.Shadow.HistoricalResults).ReplayValidation);
        var result = h.Shadow.AnalysisSummary.LastObservedSearchResult;
        Assert.NotNull(result);
        Assert.Contains("InvalidOperationException", result.Detail);
        Assert.Null(result.SearchStatus);
        Assert.Contains(h.Shadow.Events, e => e.Kind == "HistoricalResultRetained" && e.Detail.Contains("WorkerFailed"));
        Assert.Null(Assert.Single(h.Shadow.HistoricalResults).ProposedFirstAction);
    }

    [Fact]
    public void CoordinatorTelemetryIsOptInAndItsUndrainedQueueIsBounded()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        var state = new AdaptiveSearchState(true, 0, 0, 80, 600, Condition.Normal);
        using var disabled = new AdaptiveSolveCoordinator(new Scheduler());
        var session = disabled.Begin(craft, goal);
        disabled.Observe(session, 1, state);
        Assert.Empty(disabled.DrainLifecycleEvents());
        using var traced = new AdaptiveSolveCoordinator(new Scheduler(), () => TimeSpan.Zero);
        session = traced.Begin(craft, goal);
        for (var revision = 1; revision <= 100; ++revision) traced.Observe(session, revision, state);
        Assert.True(traced.DroppedLifecycleEvents > 0);
        Assert.Equal(64, traced.DrainLifecycleEvents().Length);
        Assert.Empty(traced.DrainLifecycleEvents());
    }

    [Fact]
    public void DiagnosticRevocationIsNotManagedCompletionAndLaterManagedReportPreservesEvidence()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Shadow.End("Native crafting flag cleared.", TimeSpan.FromSeconds(1));
        var original = h.Shadow.CaptureTrace();
        Assert.True(original.Summary.SessionEnded);
        Assert.False(original.Summary.ManagedCraftEnded);
        Assert.Null(original.Summary.ManagedEndReason);
        var annotated = original.RecordManagedEnd("Managed craft finished.");
        Assert.True(annotated.Summary.ManagedCraftEnded);
        Assert.Equal("Native crafting flag cleared.", annotated.Summary.SessionEndReason);
        Assert.Equal("Managed craft finished.", annotated.Summary.ManagedEndReason);
        Assert.Equal("Unobserved", annotated.Summary.TerminalObservation);
        Assert.Same(original.Events, annotated.Events);
        Assert.False(original.Summary.ManagedCraftEnded);
        Assert.Same(annotated, annotated.RecordManagedEnd("Later cleanup."));
    }

    [Fact]
    public void PauseRevokesHistoricalWorkerAndResumeRequiresRepeatedPostPauseSamples()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Shadow.SetPaused(true);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete();
        h.Shadow.SetPaused(false);
        h.Sample(h.Initial);
        Assert.Single(h.Scheduler.Work);
        Assert.Empty(h.Shadow.HistoricalResults);
        h.Sample(h.Initial);
        Assert.Equal(2, h.Scheduler.Work.Count);
        Assert.Equal(1, h.Shadow.AnalysisSummary.RevokedResultsDiscarded);
        h.Scheduler.Work[1].Complete();
        h.Shadow.Poll();
        Assert.Equal(h.Scheduler.Work[1].Request.Identity, Assert.Single(h.Shadow.HistoricalResults).Source.Identity);
    }

    [Fact]
    public void ModelMismatchRevokesInFlightHistoricalAnalysis()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        var ticket = h.Request(VulcanSkill.BasicTouch);
        h.Shadow.ReportRequest(ticket, true);
        h.Stable(new(2, 0, 134, 70, 582, Condition.Normal, InnerQuiet: 1));
        Assert.True(h.Shadow.Blocked);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Empty(h.Shadow.HistoricalResults);
        Assert.Equal(1, h.Shadow.AnalysisSummary.SearchCancellationRequests);
    }

    [Fact]
    public void SharedLaneAcrossConsecutiveCraftsDoesNotOverlapOrMutateFinishedTrace()
    {
        var scheduler = new Scheduler();
        using var runner = new AdaptiveShadowAnalysisRunner(scheduler);
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftProgress = 200 });
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 600, Condition.Normal);
        using var first = new AdaptiveShadowSession(craft, frame, TimeSpan.Zero, AdaptiveCoreTests.Limits(), runner: runner);
        first.Observe(1, TimeSpan.FromMilliseconds(10), frame, false);
        first.Observe(2, TimeSpan.FromMilliseconds(20), frame, false);
        first.End("First craft ended.", TimeSpan.FromMilliseconds(30), managedCraftEnded: true);
        var exported = AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(first.Status, first.CaptureTrace()));
        using var second = new AdaptiveShadowSession(craft, frame, TimeSpan.FromMilliseconds(40), AdaptiveCoreTests.Limits(), runner: runner);
        second.Observe(3, TimeSpan.FromMilliseconds(50), frame, false);
        second.Observe(4, TimeSpan.FromMilliseconds(60), frame, false);
        Assert.Single(scheduler.Work);
        scheduler.Work[0].Complete();
        second.Poll();
        Assert.Empty(second.HistoricalResults);
        Assert.Equal(2, scheduler.Work.Count);
        scheduler.Work[1].Complete();
        second.Poll();
        Assert.Equal(second.SessionId, Assert.Single(second.HistoricalResults).Source.Identity.SessionId);
        Assert.Equal(exported, AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(first.Status, first.CaptureTrace())));
    }

    [Fact]
    public void ExportDetachesHistoricalHistoryPlansAndLateAttemptLinking()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        var trace = h.Shadow.CaptureTrace();
        var view = AdaptiveShadowView.FromTrace(h.Shadow.Status, trace);
        var exported = AdaptiveShadowExport.Serialize(view);
        var historical = Assert.Single(trace.HistoricalResults);
        Assert.Null(historical.Source.ExistingSolverAttempt);
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveHistoricalResult>)trace.HistoricalResults).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VulcanSkill>)historical.ValidatedPlan!.Actions).Clear());
        h.Request(VulcanSkill.BasicTouch);
        Assert.Equal(VulcanSkill.BasicTouch, Assert.Single(h.Shadow.HistoricalResults).Source.ExistingSolverAttempt);
        Assert.Equal(exported, AdaptiveShadowExport.Serialize(view));
        using var json = System.Text.Json.JsonDocument.Parse(exported);
        Assert.Equal(6, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("Analysis", out _));
        Assert.Contains("not current; never executable", json.RootElement.GetProperty("AnalysisUsage").GetString());
        Assert.False(json.RootElement.GetProperty("Data").GetProperty("HistoricalResults")[0].GetProperty("Executable").GetBoolean());
    }

    [Fact]
    public void UnavailableDeadlineCancelsCapturedWorkWithoutRetainingLateResult()
    {
        using var h = new Harness();
        h.Stable(h.Initial);
        h.Sample(null);
        Assert.False(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Sample(null, milliseconds: 3100);
        Assert.True(h.Shadow.Blocked);
        Assert.True(h.Scheduler.Work[0].Token.IsCancellationRequested);
        h.Scheduler.Work[0].Complete();
        h.Shadow.Poll();
        Assert.Empty(h.Shadow.HistoricalResults);
    }
}
