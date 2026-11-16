using System;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>Serialized host-thread transaction. Native readers and dispatch are supplied by the host.</summary>
public sealed class AdaptiveLiveTrial : IDisposable
{
    private readonly AdaptiveSolveCoordinator _coordinator;
    private readonly Guid _session;
    private readonly TimeSpan _delay;
    private long _revision, _sourceRevision = -1;
    private bool _hadRoot, _paused, _stopped, _disposed, _ended;
    private TimeSpan? _readyAt;
    private TimeSpan _lastTime;
    private AdaptiveExecutionLease? _outstandingLease;
    private AdaptiveDecisionIdentity? _transactionIdentity;
    private readonly AdaptiveLiveAnalysisHistory _analysis;
    private readonly long _diagnosticDropBaseline;
    public long AnalysisVersion => _analysis.Version;
    public AdaptiveLiveAnalysisSnapshot Analysis => _analysis.Capture();
    public AdaptiveLiveTransaction? Transaction { get; private set; }
    public bool HasOutstandingTransaction => _outstandingLease != null;
    public AdaptiveShadowSession Evidence { get; }
    public bool Stopped => _stopped;
    public bool ManagedCraftEnded { get; private set; }
    public bool Terminal => Evidence.Summary.NativeEvidence?.Terminal != null;
    public string Status { get; private set; } = "Awaiting repeated fresh native samples.";
    public AdaptiveDecisionSnapshot Decision => _coordinator.Snapshot;
    public AdaptiveLiveSeedStatus StartupSeedStatus => _coordinator.StartupSeedStatus;

    public AdaptiveLiveTrial(AdaptiveSolveCoordinator coordinator, AdaptiveCraftSnapshot craft, AdaptiveCraftGoal goal,
        AdaptiveObservedFrame initial, AdaptiveNativeCaptureContext native, TimeSpan now, TimeSpan delay,
        AdaptivePlanSeed? startupSeed = null, AdaptiveLiveSeedStatus? seedStatus = null)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _coordinator = coordinator;
        _delay = delay;
        _lastTime = now;
        Evidence = new(craft, initial, now, nativeContext: native, confirmationGoal: goal);
        coordinator.EnableDecisionDiagnostics();
        _diagnosticDropBaseline = coordinator.DroppedDecisionDiagnostics;
        _session = coordinator.Begin(craft, goal, startupSeed: startupSeed, seedStatus: seedStatus);
        _analysis = new(_session, now);
        SyncAnalysis(now);
    }

    public void Tick(long frameNumber, TimeSpan now, AdaptiveObservedFrame? frame, bool executing, bool paused)
    {
        if (_disposed || _stopped || !CheckTime(now)) return;
        if (Terminal) { FinishTerminal(); return; }
        if (_paused != paused)
        {
            // Conservative first trial: never resume an action whose outcome crossed pause.
            if (paused && Evidence.HasPendingRequest) { Stop("Paused with outstanding action; no automatic recovery."); return; }
            _paused = paused;
            Evidence.SetPaused(paused, now);
            if (paused) _analysis.RevokeUndispatched(now, "Paused; undispatched recommendations revoked.");
            if (paused) _coordinator.Pause(); else _coordinator.Resume();
            _readyAt = null;
        }
        Evidence.Observe(frameNumber, now, frame, executing);
        if (Evidence.Blocked) { StopForEvidence(); return; }
        if (Terminal) { FinishTerminal(); return; }
        if (_paused) { Status = "Paused; fresh samples required on resume."; SyncAnalysis(now); return; }
        var root = Evidence.ConfirmedState;
        if (root.HasValue && (_sourceRevision != Evidence.ObservationRevision || !_hadRoot))
        {
            if (_outstandingLease != null)
            {
                if (Transaction == null || Evidence.LastConfirmedRequest != Transaction.Request
                    || Evidence.CorrelatedActions != Transaction.Ordinal || !_coordinator.ReportExecution(_outstandingLease, true))
                { Stop("Confirmed outcome does not match outstanding transaction.", AdaptiveTransactionOutcome.ProtocolFailure); return; }
                Transaction = Transaction with { Outcome = AdaptiveTransactionOutcome.Confirmed, FullFrameCorrelated = true };
                _outstandingLease = null;
            }
            _sourceRevision = Evidence.ObservationRevision;
            _hadRoot = true;
            _coordinator.Observe(_session, ++_revision, root);
            if (_coordinator.Snapshot.Decision is { } identity && identity.SessionId == _session && identity.ObservationRevision == _revision
                && Evidence.Summary.LastStableFrame is { } native)
                _analysis.Submit(identity, Evidence.ObservationRevision, Evidence.CorrelatedActions, native, now);
            _readyAt = null;
        }
        else if (!root.HasValue && _hadRoot && !Evidence.HasPendingRequest)
        {
            _hadRoot = false;
            _analysis.RevokeUndispatched(now, "Live root unavailable; recommendation revoked.");
            _coordinator.Observe(_session, ++_revision, null);
            _readyAt = null;
        }
        _coordinator.Poll();
        SyncAnalysis(now);
        if (Decision.Phase == AdaptiveDecisionPhase.Ready && root.HasValue) _readyAt ??= now + _delay;
        else _readyAt = null;
        Status = _outstandingLease != null ? $"UseAction returned {Transaction?.NativeReturn}; awaiting native outcome. No retry permitted." : Decision.Reason;
        if (Decision.Phase is AdaptiveDecisionPhase.Failed or AdaptiveDecisionPhase.Blocked or AdaptiveDecisionPhase.Cancelled
            && root.HasValue) Stop(Decision.Reason);
    }

    public void ObserveNative(AdaptiveNativeCraftEvent packet, TimeSpan now)
    {
        if (_disposed || _stopped || !CheckTime(now)) return;
        Evidence.ObserveNative(packet, now);
        if (Transaction != null && Evidence.LastAcknowledgedRequest == Transaction.Request)
            Transaction = Transaction with { AcknowledgementMatched = true };
        if (Evidence.Blocked) StopForEvidence();
        else if (Terminal) FinishTerminal();
        else SyncAnalysis(now);
    }

    public void CaptureLost() => Stop("Native capture lost; no retry or fallback.", AdaptiveTransactionOutcome.ProtocolFailure);

    private void StopForEvidence() => Stop(Evidence.Status,
        Evidence.PendingTimedOut ? AdaptiveTransactionOutcome.Unconfirmed : AdaptiveTransactionOutcome.ProtocolFailure);

    /// <summary>No await/delay allowed between the host's final reads/checks and this call.</summary>
    public AdaptiveDispatchResult TryDispatch(TimeSpan now, AdaptiveObservedFrame fresh, uint exactActionId, bool available, Func<bool> send,
        bool nativeQueueOccupied = false)
    {
        if (_disposed || _stopped || _paused || Terminal || !CheckTime(now) || !_readyAt.HasValue || now < _readyAt.Value)
            return AdaptiveDispatchResult.NotStarted;
        if (_outstandingLease != null) return AdaptiveDispatchResult.NotStarted;
        var root = Evidence.ConfirmedState;
        if (!root.HasValue || Evidence.Summary.LastStableFrame != fresh)
        { Stop("Final native frame differs from confirmed root."); return AdaptiveDispatchResult.Stopped; }
        if (nativeQueueOccupied) { Stop("Native action queue occupied before dispatch; queue ownership unknown; no action sent."); return AdaptiveDispatchResult.Stopped; }
        if (!available || exactActionId == 0) { Stop("Native action unavailable; no retry."); return AdaptiveDispatchResult.Stopped; }
        if (!_coordinator.TryCreateLease(_session, _revision, root.Value, out var lease) || lease == null)
        { Stop("Execution lease reservation failed."); return AdaptiveDispatchResult.Stopped; }
        var request = Evidence.RecordRequest(fresh, lease.Action, now, exactActionId);
        if (request == default || !_coordinator.TryConsumeLease(lease, _session, _revision, root.Value))
        { Stop("Request registration or lease consumption failed; no action sent."); return AdaptiveDispatchResult.Stopped; }
        _outstandingLease = lease;
        _transactionIdentity = lease.Decision;
        Transaction = new(request, lease.Action, exactActionId, Evidence.CorrelatedActions + 1, now, false, null);
        _readyAt = null;
        if (!Evidence.MarkLiveDispatchInvoked(request, now))
        { Stop("Live invocation registration failed; no action sent.", AdaptiveTransactionOutcome.ProtocolFailure); return AdaptiveDispatchResult.Stopped; }
        Transaction = Transaction with { InvocationBegan = true };
        try
        {
            // Native callback tap queues scalar evidence; it is drained only after disposition is reported.
            var nativeReturn = send();
            Transaction = Transaction with { NativeReturn = nativeReturn };
            if (!Evidence.RecordLiveNativeReturn(request, nativeReturn, now))
            { Stop("Native return recording failed; outcome uncertain.", AdaptiveTransactionOutcome.ProtocolFailure); return AdaptiveDispatchResult.Stopped; }
            // Raw true and false are equally non-authoritative. ReportExecution occurs only after confirmed outcome.
            Status = $"UseAction returned {nativeReturn}; awaiting native outcome. No retry permitted.";
            SyncAnalysis(now);
            return AdaptiveDispatchResult.AwaitingOutcome;
        }
        catch (Exception error)
        {
            Stop($"Dispatch exception ({error.GetType().Name}); outcome uncertain; no retry.", AdaptiveTransactionOutcome.Exception);
            return AdaptiveDispatchResult.Stopped;
        }
    }

    private bool CheckTime(TimeSpan now)
    {
        if (now < _lastTime) { Stop("Monotonic trial clock regressed."); return false; }
        _lastTime = now;
        return true;
    }

    private void FinishTerminal()
    {
        if (Transaction?.Outcome != AdaptiveTransactionOutcome.TerminalConfirmed)
        {
            if (_outstandingLease == null || Transaction == null || Evidence.LastConfirmedRequest != Transaction.Request
                || Evidence.CorrelatedActions + 1 != Transaction.Ordinal)
            { Stop("Terminal evidence does not match outstanding transaction.", AdaptiveTransactionOutcome.ProtocolFailure); return; }
            Transaction = Transaction with { Outcome = AdaptiveTransactionOutcome.TerminalConfirmed };
            _outstandingLease = null;
        }
        _coordinator.Stop(completed: Evidence.Summary.TerminalObservation == "ExperimentalPacketSuccess");
        _readyAt = null;
        Status = $"{Evidence.Summary.TerminalObservation}; awaiting managed end. No final full frame invented.";
        SyncAnalysis(_lastTime);
    }

    public void Stop(string reason, AdaptiveTransactionOutcome outcome = AdaptiveTransactionOutcome.Stopped)
    {
        if (_disposed || _stopped) return;
        _stopped = true;
        _readyAt = null;
        if (Transaction != null && (_outstandingLease != null || Evidence.Blocked))
            Transaction = Transaction with { Outcome = outcome, Error = reason };
        _outstandingLease = null;
        _coordinator.Stop();
        SyncAnalysis(_lastTime);
        _analysis.RevokeUndispatched(_lastTime, reason);
        Evidence.End(reason, _lastTime);
        Status = reason;
    }

    public void End(TimeSpan now, bool managedEnded)
    {
        if (_disposed) return;
        ManagedCraftEnded |= managedEnded; // Confirmation may already have ended on an earlier failure.
        if (_ended) return;
        _ended = true;
        if (!CheckTime(now)) return;
        if (_outstandingLease != null)
            Stop("Managed end with unconfirmed action; no terminal outcome inferred.", AdaptiveTransactionOutcome.Unconfirmed);
        var completed = !_stopped && !Evidence.Blocked && Terminal && Evidence.Summary.TerminalObservation == "ExperimentalPacketSuccess";
        if (!_stopped)
            Status = Terminal
                ? $"{Evidence.Summary.TerminalObservation}; {(ManagedCraftEnded ? "managed craft ended" : "session ended; managed end not observed")}. No final full frame invented."
                : "Session ended without confirmed terminal evidence.";
        Evidence.End(Status, now, managedEnded);
        _coordinator.Stop(completed: completed);
        SyncAnalysis(now);
        _analysis.RevokeUndispatched(now, Status);
        _stopped = true;
    }

    private void SyncAnalysis(TimeSpan now)
    {
        if (_transactionIdentity != null && Transaction != null) _analysis.Transaction(_transactionIdentity, Transaction, now);
        foreach (var entry in _coordinator.DrainDecisionDiagnostics()) _analysis.Observe(entry, now);
        _analysis.CoordinatorDrops(_coordinator.DroppedDecisionDiagnostics - _diagnosticDropBaseline);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop("Trial disposed.");
        Evidence.Dispose();
        _disposed = true;
    }
}
