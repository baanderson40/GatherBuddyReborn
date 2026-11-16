using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveLiveTrialTests
{
    private sealed class Scheduler(VulcanSkill opener = VulcanSkill.None) : IAdaptiveSearchScheduler
    {
        public int Calls;
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var simulation = new AdaptiveSimulation(request.Identity.Craft, request.Identity.Goal);
            var state = request.Identity.State;
            var actions = new List<VulcanSkill>();
            if (++Calls == 1 && opener != VulcanSkill.None)
            {
                actions.Add(opener);
                simulation.TryApply(state, opener, out state);
            }
            while (simulation.Classify(state) == AdaptiveOutcome.Nonterminal && actions.Count < 8)
            {
                actions.Add(VulcanSkill.BasicSynthesis);
                simulation.TryApply(state, VulcanSkill.BasicSynthesis, out state);
            }
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, request.Identity.State, actions, out var plan));
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found, plan,
                new(0, 0, 0, TimeSpan.Zero, false), "Synthetic fake-host plan."));
        }
    }

    private sealed class Host : IDisposable
    {
        public readonly AdaptiveCraftSnapshot Craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredProgress = 600 };
        public readonly Scheduler Scheduler;
        public readonly AdaptiveSolveCoordinator Coordinator;
        public readonly AdaptiveLiveTrial Trial;
        public AdaptiveObservedFrame Frame = new(1, 0, 0, 80, 664, Condition.Normal);
        public TimeSpan Now;
        public long FrameNumber, Sequence = 2;
        public Host(VulcanSkill opener = VulcanSkill.None, int delayMs = 0)
        {
            Scheduler = new(opener);
            Coordinator = new(Scheduler);
            Trial = new(Coordinator, Craft, AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.ProgressOnly),
                Frame, new(1, Craft.RecipeId, 0, Sequence), Now, TimeSpan.FromMilliseconds(delayMs));
        }
        public void Tick(bool executing = false, bool paused = false, AdaptiveObservedFrame? frame = null)
            => Trial.Tick(++FrameNumber, Now += TimeSpan.FromMilliseconds(10), frame ?? Frame, executing, paused);
        public void Ready() { Tick(); Tick(); Assert.Equal(AdaptiveDecisionPhase.Ready, Trial.Decision.Phase); }
        public (AdaptiveNativeCraftEvent Packet, AdaptiveObservedFrame After) Send(bool nativeReturn = true,
            Action<AdaptiveNativeCraftEvent>? onInvocation = null)
        {
            var before = Trial.Evidence.ConfirmedState!.Value;
            var action = Trial.Decision.Action;
            var simulation = new AdaptiveSimulation(Craft, Trial.Evidence.Goal);
            simulation.TryApply(before, action, out var predicted);
            var terminal = simulation.Classify(predicted) != AdaptiveOutcome.Nonterminal;
            var after = Frame with
            {
                StepIndex = Frame.StepIndex + (Simulator.SkipUpdates(action) ? 0 : 1),
                Progress = Math.Min(Craft.RequiredProgress, predicted.Progress), Quality = predicted.Quality,
                Durability = Math.Max(0, predicted.Durability), CP = predicted.CP, Condition = predicted.Condition,
                FinalAppraisal = predicted.FinalAppraisal,
            };
            var packet = new AdaptiveNativeCraftEvent(1, ++Sequence, (uint)action >= 100000 ? 9 : 10, Craft.RecipeId, 0, (uint)action,
                terminal ? Frame.StepIndex : after.StepIndex, after.Progress, after.Quality, after.Durability,
                terminal ? (int)Frame.Condition + 1 : (int)after.Condition + 1, terminal ? 22u : 18u);
            Assert.Equal(AdaptiveDispatchResult.AwaitingOutcome, Trial.TryDispatch(Now, Frame, (uint)action, true,
                () => { onInvocation?.Invoke(packet); return nativeReturn; }));
            return (packet, after);
        }
        public void Dispose() { Trial.Dispose(); Coordinator.Dispose(); }
    }

    [Fact]
    public void FreshRootNeedsSeparateFramesAndDoesNotLaunchHistoricalWork()
    {
        using var host = new Host();
        host.Tick();
        host.Trial.Tick(host.FrameNumber, host.Now, host.Frame, false, false);
        Assert.Equal(0, host.Scheduler.Calls);
        Assert.Null(host.Trial.Evidence.ConfirmedState);
        host.Tick();
        Assert.Equal(1, host.Scheduler.Calls);
        Assert.Equal(0, host.Trial.Evidence.AnalysisSummary.SearchesDispatched);
        Assert.Empty(host.Trial.Evidence.HistoricalResults);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneDispatchNeedsAcknowledgementAndTwoIndependentSamplesBeforeReplan(bool nativeReturn)
    {
        using var host = new Host();
        host.Ready();
        var (packet, after) = host.Send(nativeReturn);
        Assert.Equal(nativeReturn, host.Trial.Transaction!.NativeReturn);
        Assert.True(host.Trial.Transaction.InvocationBegan);
        Assert.True(host.Trial.HasOutstandingTransaction);
        Assert.Equal(AdaptiveDecisionPhase.AwaitingObservation, host.Trial.Decision.Phase);
        var calls = 0;
        Assert.Equal(AdaptiveDispatchResult.NotStarted, host.Trial.TryDispatch(host.Now, host.Frame, packet.ActionId, true, () => { ++calls; return true; }));
        Assert.Equal(0, calls);
        host.Frame = after;
        host.Tick(); host.Tick(); // Frame-only is not confirmation.
        Assert.Equal(1, host.Scheduler.Calls);
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
        host.Trial.ObserveNative(packet, host.Now);
        Assert.Equal(1, host.Scheduler.Calls); // Acknowledgement-only is not a new root.
        host.Tick();
        Assert.Equal(1, host.Trial.Evidence.CorrelatedActions);
        Assert.Equal(2, host.Scheduler.Calls);
        Assert.False(host.Trial.Stopped);
        Assert.False(host.Trial.HasOutstandingTransaction);
        Assert.Equal(AdaptiveTransactionOutcome.Confirmed, host.Trial.Transaction.Outcome);
        Assert.True(host.Trial.Transaction.AcknowledgementMatched);
        Assert.True(host.Trial.Transaction.FullFrameCorrelated);
        Assert.Equal(host.Trial.Transaction.Request, host.Trial.Evidence.LastConfirmedRequest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcknowledgementAloneNeverAdvancesRoot(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var (packet, after) = host.Send(nativeReturn);
        host.Trial.ObserveNative(packet, host.Now);
        Assert.True(host.Trial.Transaction!.AcknowledgementMatched);
        Assert.False(host.Trial.Transaction.FullFrameCorrelated);
        host.Tick(); host.Tick();
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
        host.Frame = after;
        host.Tick();
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
        host.Tick();
        Assert.Equal(1, host.Trial.Evidence.CorrelatedActions);
    }

    [Fact]
    public void ExceptionStopsWithoutRetry()
    {
        using var host = new Host(); host.Ready();
        var calls = 0;
        bool Send() { ++calls; throw new InvalidOperationException(); }
        Assert.Equal(AdaptiveDispatchResult.Stopped, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, Send));
        host.Tick(); host.Tick();
        Assert.Equal(AdaptiveDispatchResult.NotStarted, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, Send));
        Assert.True(host.Trial.Stopped);
        Assert.Equal(1, calls);
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
        Assert.Equal(AdaptiveTransactionOutcome.Exception, host.Trial.Transaction!.Outcome);
        Assert.Null(host.Trial.Transaction.NativeReturn);
    }

    [Theory]
    [InlineData("frame")]
    [InlineData("unavailable")]
    [InlineData("id")]
    public void FinalChecksSendNothingOnDriftOrUnavailableAction(string reason)
    {
        using var host = new Host(); host.Ready();
        var calls = 0;
        Assert.Equal(AdaptiveDispatchResult.Stopped, host.Trial.TryDispatch(host.Now, reason == "frame" ? host.Frame with { CP = 663 } : host.Frame,
            reason == "id" ? 0u : 100001u, reason != "unavailable", () => { ++calls; return true; }));
        Assert.True(host.Trial.Stopped);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ConfiguredDelayPrecedesConsumption()
    {
        using var host = new Host(delayMs: 100); host.Ready();
        Assert.Equal(AdaptiveDispatchResult.NotStarted, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, () => throw new Exception()));
        host.Now += TimeSpan.FromMilliseconds(100);
        Assert.Equal(AdaptiveDispatchResult.AwaitingOutcome, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, () => true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PauseWithOutstandingActionStopsRatherThanClearingUncertainty(bool nativeReturn)
    {
        using var host = new Host(); host.Ready(); host.Send(nativeReturn); host.Tick(paused: true);
        Assert.True(host.Trial.Stopped);
        host.Tick(paused: false);
        Assert.Equal(1, host.Scheduler.Calls);
    }

    [Fact]
    public void ResumeWithoutOutstandingActionRequiresFreshSamples()
    {
        using var host = new Host(); host.Ready(); host.Tick(paused: true);
        Assert.Equal(AdaptiveDispatchResult.NotStarted, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true, () => throw new Exception()));
        host.Tick();
        Assert.Null(host.Trial.Evidence.ConfirmedState);
        host.Tick();
        Assert.Equal(2, host.Scheduler.Calls);
        Assert.False(host.Trial.Stopped);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("loss")]
    [InlineData("wrong-id")]
    [InlineData("abort")]
    public void AmbiguousNativePathsStop(string reason)
    {
        using var host = new Host(); host.Ready();
        var (packet, _) = host.Send();
        if (reason == "timeout")
        {
            host.Now += TimeSpan.FromSeconds(9);
            host.Tick(executing: true);
        }
        else if (reason == "loss") host.Trial.CaptureLost();
        else host.Trial.ObserveNative(reason == "abort" ? packet with { Operation = 6 } : packet with { ActionId = 999 }, host.Now);
        Assert.True(host.Trial.Stopped);
        Assert.Equal(1, host.Scheduler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonStepActionStillRequiresAcknowledgementAndIndependentBuffSamples(bool nativeReturn)
    {
        using var host = new Host(VulcanSkill.FinalAppraisal); host.Ready();
        var (packet, after) = host.Send(nativeReturn);
        Assert.Equal(host.Frame.StepIndex, after.StepIndex);
        host.Trial.ObserveNative(packet, host.Now);
        host.Frame = after;
        host.Tick(); host.Tick();
        Assert.Equal(1, host.Trial.Evidence.CorrelatedActions);
        Assert.Equal(2, host.Scheduler.Calls);
        Assert.False(host.Trial.Stopped);
        var nonstep = host.Trial.Analysis.Roots[0];
        Assert.Equal(VulcanSkill.FinalAppraisal, nonstep.Transaction!.Action);
        Assert.Equal(AdaptiveTransactionOutcome.Confirmed, nonstep.Transaction.Outcome);
        Assert.NotEqual(nonstep.Identity, host.Trial.Analysis.Roots[1].Identity);
        Assert.Equal(nonstep.NativeFrame.StepIndex, host.Trial.Analysis.Roots[1].NativeFrame.StepIndex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalPacketStopsWithoutFabricatedFinalFrame(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var (first, after) = host.Send();
        host.Trial.ObserveNative(first, host.Now); host.Frame = after; host.Tick(); host.Tick();
        var lastActual = host.Frame;
        var (terminal, _) = host.Send(nativeReturn);
        host.Trial.ObserveNative(terminal, host.Now);
        Assert.True(host.Trial.Terminal);
        Assert.Equal(AdaptiveDecisionPhase.Completed, host.Trial.Decision.Phase);
        Assert.Equal(lastActual, host.Trial.Evidence.Summary.LastStableFrame);
        Assert.Equal(AdaptiveDispatchResult.NotStarted, host.Trial.TryDispatch(host.Now, lastActual, terminal.ActionId, true, () => throw new Exception()));
        host.Trial.End(host.Now, true);
        Assert.True(host.Trial.Evidence.Summary.ManagedCraftEnded);
        Assert.Equal(2, host.Scheduler.Calls);
        Assert.Equal(nativeReturn, host.Trial.Transaction!.NativeReturn);
        Assert.Equal(AdaptiveTransactionOutcome.TerminalConfirmed, host.Trial.Transaction.Outcome);
        Assert.True(host.Trial.Transaction.AcknowledgementMatched);
        Assert.False(host.Trial.Transaction.FullFrameCorrelated);
    }

    [Fact]
    public void LaterProtocolFailureCannotBecomeSuccessfulCompletionAtManagedEnd()
    {
        using var host = new Host(); host.Ready();
        var (first, after) = host.Send();
        host.Trial.ObserveNative(first, host.Now); host.Frame = after; host.Tick(); host.Tick();
        var (terminal, _) = host.Send();
        host.Trial.ObserveNative(terminal, host.Now);
        host.Trial.ObserveNative(terminal, host.Now); // Duplicate after admitted terminal remains a protocol error.
        Assert.True(host.Trial.Stopped);
        host.Trial.End(host.Now, true);
        Assert.NotEqual(AdaptiveDecisionPhase.Completed, host.Trial.Decision.Phase);
        Assert.True(host.Trial.ManagedCraftEnded);
        Assert.NotNull(host.Trial.Evidence.Summary.NativeEvidence!.LastMismatch);
    }

    [Theory]
    [InlineData(AdaptiveLivePolicyMode.ProgressOnly, 0)]
    [InlineData(AdaptiveLivePolicyMode.Standard, 0)]
    [InlineData(AdaptiveLivePolicyMode.Standard, 50)]
    [InlineData(AdaptiveLivePolicyMode.Raphael, 100)]
    [InlineData(AdaptiveLivePolicyMode.Adaptive, 100)]
    public void ActivePolicyMapsRealHQCurveNotDiagnosticFullQuality(AdaptiveLivePolicyMode mode, int chance)
    {
        var craft = AdaptiveSeedRegressionCorpus.Craft;
        var goal = new AdaptiveLiveGoalPolicy(mode, chance, 3).Resolve(craft);
        var expected = AdaptiveCraftGoal.Resolve(craft, mode == AdaptiveLivePolicyMode.ProgressOnly ? AdaptiveGoalKind.ProgressOnly
            : mode == AdaptiveLivePolicyMode.Standard ? AdaptiveGoalKind.HQChance : AdaptiveGoalKind.Quality,
            mode is AdaptiveLivePolicyMode.Raphael or AdaptiveLivePolicyMode.Adaptive ? craft.MaxQuality : chance);
        Assert.Equal(expected, goal);
    }

    [Fact]
    public void MissingCollectabilityMetadataBlocksInsteadOfInventingTarget()
        => Assert.Throws<NotSupportedException>(() => new AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode.Standard, 100, 3)
            .Resolve(AdaptiveSeedRegressionCorpus.Craft with { Collectable = true }));

    [Theory]
    [InlineData(AdaptiveLivePolicyMode.ProgressOnly)]
    [InlineData(AdaptiveLivePolicyMode.Standard)]
    [InlineData(AdaptiveLivePolicyMode.Raphael)]
    [InlineData(AdaptiveLivePolicyMode.Adaptive)]
    public void MandatoryRecipeQualitySurvivesEveryActivePolicy(AdaptiveLivePolicyMode mode)
    {
        var craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredQuality = 3000 };
        var goal = new AdaptiveLiveGoalPolicy(mode, 0, 3).Resolve(craft);
        Assert.Equal(3000, goal.MandatoryQuality);
        Assert.Equal(3000, goal.DesiredQuality);
    }

    [Fact]
    public void DuplicateAndLatePacketsCannotAuthorizeAnotherAction()
    {
        using var host = new Host(); host.Ready();
        var (packet, after) = host.Send();
        host.Trial.ObserveNative(packet, host.Now);
        host.Trial.ObserveNative(packet, host.Now);
        Assert.True(host.Trial.Stopped);
        host.Frame = after; host.Tick(); host.Tick();
        Assert.Equal(1, host.Scheduler.Calls);
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
    }

    [Fact]
    public void ChangedStableStateWithoutRequestStopsRatherThanGuessingHistory()
    {
        using var host = new Host(); host.Ready();
        host.Frame = host.Frame with { CP = 663 };
        host.Tick();
        Assert.NotEqual(AdaptiveDecisionPhase.Ready, host.Trial.Decision.Phase);
        host.Tick();
        Assert.True(host.Trial.Stopped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchedPacketCannotSubstituteForWrongIndependentlyReadCP(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var (packet, after) = host.Send(nativeReturn);
        host.Trial.ObserveNative(packet, host.Now);
        host.Frame = after with { CP = after.CP - 1 };
        host.Tick(); host.Tick();
        Assert.True(host.Trial.Stopped);
        Assert.NotNull(host.Trial.Evidence.Summary.LastMismatch);
        Assert.Equal(1, host.Scheduler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherReturnWithoutOutcomeTimesOutUnconfirmedAndCannotRetryOrRevive(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var (packet, after) = host.Send(nativeReturn);
        Assert.False(host.Trial.Stopped);
        Assert.True(host.Trial.Evidence.HasPendingRequest);
        Assert.Equal(AdaptiveTransactionOutcome.AwaitingOutcome, host.Trial.Transaction!.Outcome);
        host.Now += TimeSpan.FromSeconds(9);
        host.Tick(executing: true);
        Assert.True(host.Trial.Stopped);
        Assert.Equal(AdaptiveTransactionOutcome.Unconfirmed, host.Trial.Transaction.Outcome);
        Assert.Contains("unconfirmed", host.Trial.Status);
        Assert.Contains("no rejection inferred", host.Trial.Status);
        Assert.False(host.Trial.HasOutstandingTransaction);
        host.Trial.ObserveNative(packet, host.Now);
        host.Frame = after; host.Tick(); host.Tick();
        Assert.Equal(0, host.Trial.Evidence.CorrelatedActions);
        Assert.Equal(0, host.Trial.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(AdaptiveDispatchResult.NotStarted,
            host.Trial.TryDispatch(host.Now, after, packet.ActionId, true, () => throw new Exception()));
        Assert.Equal(1, host.Scheduler.Calls);
        var root = Assert.Single(host.Trial.Analysis.Roots);
        Assert.Equal(AdaptiveTransactionOutcome.Unconfirmed, root.Transaction!.Outcome);
        Assert.Null(root.AcknowledgementMatchedAt);
        Assert.Null(root.FullFrameConfirmedAt);
        Assert.NotNull(root.CloseReason);
    }

    [Fact]
    public void PacketPastDeadlineCannotSneakInBeforeNextTick()
    {
        using var host = new Host(); host.Ready();
        var (packet, _) = host.Send(false);
        host.Now += TimeSpan.FromSeconds(9);
        host.Trial.ObserveNative(packet, host.Now);
        Assert.True(host.Trial.Stopped);
        Assert.Equal(AdaptiveTransactionOutcome.Unconfirmed, host.Trial.Transaction!.Outcome);
        Assert.Equal(0, host.Trial.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(packet, host.Trial.Evidence.Summary.NativeEvidence.LastMismatch!.Observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredRequestWithoutCompletedInvocationReturnCannotConfirm(bool markInvoked)
    {
        using var host = new Host(); host.Ready();
        var evidence = host.Trial.Evidence;
        var action = VulcanSkill.BasicSynthesis;
        var ticket = evidence.RecordRequest(host.Frame, action, host.Now, (uint)action);
        Assert.NotEqual(default, ticket);
        Assert.False(evidence.ReportRequest(ticket, true)); // Cannot fake an accepted legacy request in live mode.
        Assert.False(evidence.RecordLiveNativeReturn(ticket, false, host.Now)); // Invocation not begun.
        if (markInvoked) Assert.True(evidence.MarkLiveDispatchInvoked(ticket, host.Now));
        var simulation = new AdaptiveSimulation(host.Craft, evidence.Goal);
        simulation.TryApply(host.Frame.ToState(true, VulcanSkill.None), action, out var predicted);
        host.Trial.ObserveNative(new(1, 3, 9, host.Craft.RecipeId, 0, (uint)action, 2,
            predicted.Progress, predicted.Quality, predicted.Durability, 1, 18), host.Now);
        Assert.True(host.Trial.Stopped);
        Assert.Equal(0, evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(0, evidence.CorrelatedActions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SynchronousNativeDeliveryIsQueuedUntilRawReturnIsRecorded(bool nativeReturn)
    {
        using var host = new Host(); host.Ready();
        var queue = new Queue<AdaptiveNativeCraftEvent>();
        var (_, after) = host.Send(nativeReturn, packet =>
        {
            Assert.True(host.Trial.Transaction!.InvocationBegan);
            Assert.Null(host.Trial.Transaction.NativeReturn);
            Assert.Equal(0, host.Trial.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
            queue.Enqueue(packet); // Same isolation as the actual callback tap; no direct host call.
        });
        Assert.Equal(nativeReturn, host.Trial.Transaction!.NativeReturn);
        host.Trial.ObserveNative(queue.Dequeue(), host.Now);
        Assert.Equal(1, host.Trial.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(1, host.Scheduler.Calls);
        host.Frame = after; host.Tick(); host.Tick();
        Assert.Equal(2, host.Scheduler.Calls);
        Assert.Equal(AdaptiveTransactionOutcome.Confirmed, host.Trial.Transaction.Outcome);
    }

    [Fact]
    public void OccupiedNativeQueueStopsBeforeInvocationWithoutClearingIt()
    {
        using var host = new Host(); host.Ready();
        var calls = 0;
        Assert.Equal(AdaptiveDispatchResult.Stopped, host.Trial.TryDispatch(host.Now, host.Frame, 100001, true,
            () => { ++calls; return true; }, nativeQueueOccupied: true));
        Assert.Equal(0, calls);
        Assert.Null(host.Trial.Transaction);
        Assert.False(host.Trial.Evidence.HasPendingRequest);
        Assert.True(host.Trial.Stopped);
    }

    [Fact]
    public void LiveReportingApisCannotWeakenHistoricalShadowRequestRejection()
    {
        using var host = new Host();
        using var shadow = new AdaptiveShadowSession(host.Craft, host.Frame, host.Now, scheduler: new Scheduler(),
            nativeContext: new(1, host.Craft.RecipeId, 0, 2));
        var ticket = shadow.RecordRequest(host.Frame, VulcanSkill.BasicSynthesis, host.Now, 100001);
        Assert.False(shadow.MarkLiveDispatchInvoked(ticket, host.Now));
        Assert.False(shadow.RecordLiveNativeReturn(ticket, false, host.Now));
        Assert.True(shadow.ReportRequest(ticket, false));
        Assert.False(shadow.HasPendingRequest);
        Assert.Equal(0, shadow.CorrelatedActions);
    }

    [Fact]
    public void RawFalseIsDurableDetachedDiagnosticNotFabricatedAcceptedTrue()
    {
        using var host = new Host(); host.Ready(); host.Send(false);
        var before = host.Trial.Transaction!;
        Assert.False(host.Trial.Evidence.RecordLiveNativeReturn(before.Request, true, host.Now));
        Assert.False(host.Trial.Evidence.MarkLiveDispatchInvoked(before.Request, host.Now));
        Assert.False(host.Trial.Evidence.ReportRequest(before.Request, true));
        Assert.True(host.Trial.HasOutstandingTransaction);
        Assert.DoesNotContain(host.Trial.Evidence.Events, entry => entry.Kind == "RequestResult");
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(before));
        Assert.False(json.RootElement.GetProperty("NativeReturn").GetBoolean());
        Assert.True(json.RootElement.GetProperty("InvocationBegan").GetBoolean());
        host.Trial.Stop("Explicit stop.");
        Assert.Equal(AdaptiveTransactionOutcome.AwaitingOutcome, before.Outcome); // Detached immutable snapshot.
        Assert.Equal(AdaptiveTransactionOutcome.Stopped, host.Trial.Transaction!.Outcome);
    }

    private sealed class DeferredScheduler : IAdaptiveSearchScheduler
    {
        public readonly List<(AdaptiveSearchRequest Request, TaskCompletionSource<AdaptiveSearchResult> Completion)> Work = new();
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<AdaptiveSearchResult>(); // Deliberately ignores cancellation.
            Work.Add((request, completion));
            return completion.Task;
        }
        public void Complete(int index)
        {
            var work = Work[index];
            work.Completion.SetResult(new Scheduler().Schedule(work.Request, CancellationToken.None).Result);
        }
    }

    [Fact]
    public void ConsecutiveTrialSessionsWaitForCancellationIgnoringWorkerAndRejectOldResult()
    {
        var scheduler = new DeferredScheduler();
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        var craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredProgress = 600 };
        var frame = new AdaptiveObservedFrame(1, 0, 0, 80, 664, Condition.Normal);
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly);
        var first = new AdaptiveLiveTrial(coordinator, craft, goal, frame, new(1, craft.RecipeId, 0, 2), TimeSpan.Zero, TimeSpan.Zero);
        first.Tick(1, TimeSpan.Zero, frame, false, false);
        first.Tick(2, TimeSpan.Zero, frame, false, false);
        first.Dispose();
        using var next = new AdaptiveLiveTrial(coordinator, craft, goal, frame, new(2, craft.RecipeId, 0, 22), TimeSpan.Zero, TimeSpan.Zero);
        next.Tick(3, TimeSpan.Zero, frame, false, false);
        next.Tick(4, TimeSpan.Zero, frame, false, false);
        Assert.Single(scheduler.Work);
        scheduler.Complete(0);
        next.Tick(5, TimeSpan.Zero, frame, false, false);
        Assert.Equal(2, scheduler.Work.Count);
        Assert.Equal(AdaptiveDecisionPhase.Searching, next.Decision.Phase);
        Assert.True(next.Decision.RejectedResults > 0);
        Assert.Single(next.Analysis.Roots);
        Assert.Null(next.Analysis.Roots[0].Plan);
        Assert.Contains(next.Analysis.UnlinkedLifecycleEvents, entry => entry.Kind == "SearchResultRejected" && entry.SessionId != next.Decision.SessionId);
        scheduler.Complete(1);
        next.Tick(6, TimeSpan.Zero, frame, false, false);
        Assert.Equal(AdaptiveDecisionPhase.Ready, next.Decision.Phase);
        Assert.Equal(0, next.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
    }
}
