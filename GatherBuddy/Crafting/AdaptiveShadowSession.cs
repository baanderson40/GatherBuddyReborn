using System;
using System.Collections.Generic;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>
/// Shared read-only confirmation host; historical search is optional. Experimental packet matches
/// do not establish blanket native validation. All entry points belong to the host thread.
/// No execution lease is created or consumed here, including in confirmation-only mode.
/// </summary>
public sealed class AdaptiveShadowSession : IDisposable
{
    private sealed record Pending(AdaptiveShadowRequest Ticket, VulcanSkill Action, AdaptiveObservedFrame Before,
        AdaptiveSearchState Predicted, int ExpectedIndex, TimeSpan Started, bool? ReportedSuccess = null,
        uint NativeActionId = 0, AdaptiveNativeCraftEvent? Acknowledgement = null,
        bool LiveInvocationBegan = false, bool? NativeReturn = null,
        TimeSpan? AcknowledgedAt = null, AdaptiveShadowMismatch? SettlingMismatch = null);
    private readonly AdaptiveShadowAnalysisRunner? _runner;
    private readonly bool _ownsRunner;
    private readonly AdaptiveSimulation _simulation;
    private readonly List<AdaptiveShadowEvent> _events = new();
    private readonly IReadOnlyList<AdaptiveShadowEvent> _readOnlyEvents;
    private AdaptiveObservedFrame _knownFrame;
    private AdaptiveSearchState _knownState;
    private AdaptiveObservedFrame? _candidate;
    private Pending? _pending;
    private long _lastFrame = -1;
    private long _requestId;
    private long _revision;
    private int _samples;
    private bool _analysisMissing = true;
    private bool _paused;
    private bool _blocked;
    private bool _disposed;
    private TimeSpan _lastTime;
    private TimeSpan? _unavailableSince;
    private TimeSpan? _pausedAt;
    private readonly TimeSpan _startedAt;
    private bool _ended;
    private bool _managedCraftEnded;
    private string? _endReason;
    private string? _blockedReason;
    private AdaptiveShadowMismatch? _lastMismatch;
    private AdaptiveObservedFrame? _lastStableFrame;
    private string _terminalObservation = "Unobserved";
    private AdaptiveHistoricalAnalysisSummary? _finalAnalysis;
    private IReadOnlyList<AdaptiveHistoricalResult>? _finalResults;
    private readonly AdaptiveNativeCaptureContext? _nativeContext;
    private long _nativeSequence;
    private int _nativeAcknowledgements;
    private AdaptiveNativeCraftEvent? _lastNativeAcknowledgement, _nativeTerminal;
    private AdaptiveNativeMismatch? _lastNativeMismatch;

    public Guid SessionId { get; }
    public string Status { get; private set; } = "Waiting for repeated stable native samples.";
    public int CorrelatedActions { get; private set; }
    public bool Blocked => _blocked;
    public long EventVersion { get; private set; }
    public IReadOnlyList<AdaptiveShadowEvent> Events => _readOnlyEvents;
    public IReadOnlyList<AdaptiveHistoricalResult> HistoricalResults => _finalResults ?? _runner?.GetHistory(SessionId) ?? Array.Empty<AdaptiveHistoricalResult>();
    public AdaptiveHistoricalAnalysisSummary AnalysisSummary => _finalAnalysis ?? _runner?.GetSummary(SessionId) ?? new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
    // Confirmation-only consumers share the audited protocol without creating a historical worker.
    public AdaptiveSearchState? ConfirmedState => !_disposed && !_ended && !_blocked && !_paused
        && !_analysisMissing && _pending == null && _nativeTerminal == null && _lastStableFrame.HasValue ? _knownState : null;
    public long ObservationRevision => _revision;
    public bool HasPendingRequest => _pending != null;
    public AdaptiveShadowRequest LastConfirmedRequest { get; private set; }
    public AdaptiveShadowRequest LastAcknowledgedRequest { get; private set; }
    public bool PendingTimedOut { get; private set; }
    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(8);
    // Independent status-list replication can trail the acknowledged addon/CP update.
    // This is a confirmation deadline, never an action delay, retry, or modeled observation.
    private static readonly TimeSpan StatusSettlementTimeout = TimeSpan.FromMilliseconds(500);
    public AdaptiveCraftSnapshot Craft => _simulation.Craft;
    public AdaptiveCraftGoal Goal => _simulation.Goal;
    public AdaptiveShadowSummary Summary => new(_ended, _endReason, _managedCraftEnded,
        _managedCraftEnded ? _endReason : null, _terminalObservation, _lastStableFrame,
        CorrelatedActions, _blockedReason, EventVersion, Math.Max(0, EventVersion - _events.Count), AnalysisSummary, _lastMismatch)
        { NativeEvidence = new(_nativeContext != null, _nativeAcknowledgements, _lastNativeAcknowledgement, _nativeTerminal)
            { LastMismatch = _lastNativeMismatch } };

    public AdaptiveShadowTrace CaptureTrace() => new(Craft, Goal, Summary, Array.AsReadOnly(_events.ToArray()),
        Array.AsReadOnly(new List<AdaptiveHistoricalResult>(HistoricalResults).ToArray()));

    public AdaptiveShadowSession(AdaptiveCraftSnapshot craft, AdaptiveObservedFrame initial, TimeSpan now,
        AdaptiveSearchLimits? limits = null, IAdaptiveSearchScheduler? scheduler = null, AdaptiveShadowAnalysisRunner? runner = null,
        AdaptivePlanSeed? seed = null, string seedCaptureReason = "No cached seed supplied.",
        AdaptiveNativeCaptureContext? nativeContext = null, AdaptiveCraftGoal? confirmationGoal = null)
    {
        initial.Validate(craft);
        if (!initial.IsFreshStart) throw new ArgumentException("Shadow must begin at a fresh step-one craft; mid-craft history is unknown.");
        if (nativeContext != null && (nativeContext.Stream <= 0 || nativeContext.Sequence <= 0
            || nativeContext.RecipeId != craft.RecipeId || nativeContext.StartingQuality != initial.Quality))
            throw new ArgumentException("Native stream/start context differs from fresh observed root.");
        _nativeContext = nativeContext;
        _nativeSequence = nativeContext?.Sequence ?? 0;
        if (confirmationGoal != null && (nativeContext == null || runner != null || scheduler != null || seed != null))
            throw new ArgumentException("Confirmation-only mode requires native context and no historical lane or seed.");
        _simulation = new(craft, confirmationGoal ?? AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, craft.MaxQuality));
        _knownFrame = initial;
        _knownState = initial.ToState(craft.Level >= 100, VulcanSkill.None);
        _lastTime = now;
        _startedAt = now;
        _readOnlyEvents = _events.AsReadOnly();
        if (runner != null && scheduler != null) throw new ArgumentException("Pass shared runner or scheduler, not both.");
        _ownsRunner = confirmationGoal == null && runner == null;
        _runner = confirmationGoal == null ? runner ?? new(scheduler) : null;
        SessionId = _runner?.Begin(craft, _simulation.Goal, limits, now, seed, seedCaptureReason) ?? Guid.NewGuid();
        Add(new(0, "Begin", confirmationGoal == null
            ? "Shadow-only full raw-quality target; initial seed is not yet a stable sample. Native action identity is unconfirmed."
            : "Confirmation-only active-goal evidence; no historical search. Initial frame requires independent repeated samples. Native layout remains experimental.", After: initial));
    }

    public AdaptiveShadowRequest RecordRequest(AdaptiveObservedFrame before, VulcanSkill action, TimeSpan now, uint nativeActionId = 0)
    {
        if (_disposed || _ended || _blocked || _paused || _nativeTerminal != null) return default;
        if (now < _lastTime) { Fail("Monotonic clock regressed."); return default; }
        _lastTime = now;
        if (_nativeContext != null && nativeActionId == 0) { Fail("Native capture requires exact job-resolved action ID."); return default; }
        if (_pending != null) { Fail("Multiple requests before a correlated outcome; action history is ambiguous."); return default; }
        if (before != _knownFrame) { Fail("Request origin differs from known native frame; untracked action or torn read."); return default; }
        var outcome = _simulation.TryApply(_knownState, action, out var predicted);
        if (outcome == AdaptiveOutcome.Illegal) { Fail($"Requested action outside supported shadow model: {action}."); return default; }
        var ticket = new AdaptiveShadowRequest(SessionId, ++_requestId);
        _pending = new(ticket, action, before, predicted,
            before.StepIndex + (Simulator.SkipUpdates(action) ? 0 : 1), now, NativeActionId: nativeActionId);
        _runner?.RecordAttempt(SessionId, _revision, action, now);
        DrainSearchEvents();
        ClearSamples();
        Invalidate("Requested action is unconfirmed; waiting for stable native outcome.");
        Add(new(_revision, "Request", "Request only, not proof of native execution.", Before: before, RequestedAction: action));
        return ticket;
    }

    public bool ReportRequest(AdaptiveShadowRequest ticket, bool reportedSuccess)
    {
        if (_runner == null) return false; // Live invocation/return has a distinct, non-authoritative path.
        if (_disposed || _ended || _blocked || _pending is not { } pending || ticket != pending.Ticket || pending.ReportedSuccess.HasValue) return false;
        Add(new(_revision, "RequestResult", $"Executor reported {reportedSuccess}; native outcome still required.", RequestedAction: pending.Action));
        if (!reportedSuccess)
        {
            _pending = null;
            ClearSamples();
            Invalidate("Request rejected; waiting for fresh native samples without advancing history.");
        }
        else _pending = pending with { ReportedSuccess = true };
        return true;
    }

    public bool MarkLiveDispatchInvoked(AdaptiveShadowRequest ticket, TimeSpan now)
    {
        if (_runner != null || _disposed || _ended || _blocked || _paused || _nativeContext == null
            || _pending is not { } pending || pending.Ticket != ticket || pending.LiveInvocationBegan) return false;
        if (now < _lastTime) { Fail("Monotonic clock regressed at live invocation."); return false; }
        _lastTime = now;
        _pending = pending with { LiveInvocationBegan = true };
        Add(new(_revision, "LiveDispatchInvoked", "Native invocation begins once; neither invocation nor return confirms execution.", RequestedAction: pending.Action));
        return true;
    }

    public bool RecordLiveNativeReturn(AdaptiveShadowRequest ticket, bool value, TimeSpan now)
    {
        if (_runner != null || _disposed || _ended || _blocked || _paused || _pending is not { } pending
            || pending.Ticket != ticket || !pending.LiveInvocationBegan || pending.NativeReturn.HasValue) return false;
        if (now < _lastTime) { Fail("Monotonic clock regressed at live return."); return false; }
        _lastTime = now;
        _pending = pending with { NativeReturn = value };
        Add(new(_revision, "LiveNativeReturn", $"UseAction returned {value}; awaiting native outcome. No retry permitted.", RequestedAction: pending.Action));
        return true;
    }

    private bool EligibleForConfirmation(Pending pending) => _runner == null
        ? pending.LiveInvocationBegan && pending.NativeReturn.HasValue : pending.ReportedSuccess == true;

    public void NativeCaptureLost(TimeSpan now)
    {
        if (_disposed || _ended || _blocked || _nativeContext == null) return;
        if (now >= _lastTime) _lastTime = now;
        Fail("Native crafting capture unavailable, malformed, or overflowed; acknowledgement history revoked.");
    }

    /// <summary>Experimental callback acknowledgement. Does not replace CP/status samples or authorize execution.</summary>
    public void ObserveNative(AdaptiveNativeCraftEvent evidence, TimeSpan now)
    {
        if (_disposed || _ended || _blocked || _nativeContext == null) return;
        if (now < _lastTime) { Fail("Monotonic clock regressed in native acknowledgement."); return; }
        _lastTime = now;
        Add(new(_revision, "NativeCraftEvent", "Experimental native callback projection; layout acceptance pending.") { Native = evidence });
        if (_runner == null && _pending is { } overdue && now - overdue.Started > OutcomeTimeout)
        {
            PendingTimedOut = true;
            RejectNative("Outcome unconfirmed within eight seconds; no rejection inferred.", ExpectedNative(overdue), evidence,
                "Acknowledgement arrived after outcome deadline");
            return;
        }
        var expectation = ExpectedNative(_pending);
        if (evidence.Stream != _nativeContext.Stream || evidence.RecipeId != Craft.RecipeId
            || evidence.StartingQuality != _nativeContext.StartingQuality || evidence.Sequence <= _nativeSequence)
        { RejectNative("Native stream/context changed or duplicate/reordered event; refusing acknowledgement.", expectation, evidence); return; }
        _nativeSequence = evidence.Sequence;
        if (evidence.Operation == 4)
        {
            if (_nativeTerminal == null) RejectNative("Native finish without matched terminal action; no success inferred.", expectation, evidence, "Finish without matched terminal action");
            return;
        }
        if (!evidence.IsAdvance || _paused || _pending is not { } pending || pending.Acknowledgement != null
            || !EligibleForConfirmation(pending) || evidence.ActionId != pending.NativeActionId)
        {
            RejectNative("Unexpected, paused, untracked, rejected, duplicate, or different native action acknowledgement.", expectation, evidence,
                $"paused={_paused}; pending={_pending != null}; reported={_pending?.ReportedSuccess}; priorAcknowledgement={_pending?.Acknowledgement != null}");
            return;
        }
        var predictedTerminal = _simulation.Classify(pending.Predicted) != AdaptiveOutcome.Nonterminal;
        const string scalarFailure = "Native action result differs from supported model/terminal scalars; no predicted fields substituted.";
        var mismatch = AdaptiveNativeMismatch.Compare(scalarFailure, expectation, evidence);
        if (mismatch.Differences.Count != 0)
        { RejectNative(scalarFailure, expectation, evidence); return; }
        _pending = pending with { Acknowledgement = evidence, AcknowledgedAt = now };
        _lastNativeAcknowledgement = evidence;
        LastAcknowledgedRequest = pending.Ticket;
        ++_nativeAcknowledgements;
        Add(new(_revision, "NativeActionMatched", "Exact requested action ID and observed packet scalars matched; CP/status samples remain separately required.",
            RequestedAction: pending.Action) { Native = evidence });
        if (!predictedTerminal) return;
        LastConfirmedRequest = pending.Ticket;
        _nativeTerminal = evidence;
        _terminalObservation = evidence.Progress >= Craft.RequiredProgress && evidence.Quality >= Goal.MandatoryQuality
            ? "ExperimentalPacketSuccess" : "ExperimentalPacketFailure";
        // No fabricated complete frame, CP, buffs, combo, availability, or extra correlated action.
        _pending = null;
        _runner?.Revoke(SessionId, now, "Native terminal packet observed; historical work revoked.");
        DrainSearchEvents();
        Status = _runner == null
            ? "Experimental native terminal scalars observed; further live dispatch halted; waiting for managed craft end. Layout validation remains pending."
            : "Experimental native terminal scalars observed; waiting for managed craft end. Execution remains disabled.";
        Add(new(_revision, "NativeTerminalObserved", Status) { Native = evidence });
    }

    private AdaptiveNativeExpectation ExpectedNative(Pending? pending)
    {
        var terminal = pending != null && _simulation.Classify(pending.Predicted) != AdaptiveOutcome.Nonterminal;
        // Terminal packet does not advance the turn: preserve the input condition, not the
        // simulator's hypothetical next-turn condition. No random next condition at completion.
        var conditions = pending == null ? Array.Empty<int>() : terminal ? new[] { (int)pending.Before.Condition + 1 }
            : pending.Action != VulcanSkill.FinalAppraisal && _knownState.Condition == Condition.Normal ? new[] { 1, 2, 3 }
            : new[] { (int)pending.Predicted.Condition + 1 };
        return new(_nativeContext!.Stream, _nativeSequence, Craft.RecipeId, _nativeContext.StartingQuality,
            pending?.NativeActionId, pending == null ? null : pending.NativeActionId >= 100000 ? 9 : 10,
            pending == null ? null : terminal ? pending.Before.StepIndex : pending.ExpectedIndex,
            pending == null ? null : Math.Min(Craft.RequiredProgress, pending.Predicted.Progress), pending?.Predicted.Quality,
            pending == null ? null : Math.Max(0, pending.Predicted.Durability), Array.AsReadOnly(conditions),
            pending == null ? "No active request; action/result expectations unknown"
                : terminal ? "Terminal preserves input condition; no next turn" : "Nonterminal modeled next condition; Normal may become Good/Excellent",
            pending == null ? null : true, pending == null ? null : terminal);
    }

    private void RejectNative(string reason, AdaptiveNativeExpectation expected, AdaptiveNativeCraftEvent observed, string? protocolFailure = null)
    {
        _lastNativeMismatch = AdaptiveNativeMismatch.Compare(reason, expected, observed, protocolFailure);
        Add(new(_revision, "NativeMismatch", reason) { Native = observed, NativeMismatch = _lastNativeMismatch });
        Fail(reason);
    }

    public void Observe(long frameNumber, TimeSpan now, AdaptiveObservedFrame? frame, bool executingAction)
    {
        if (_disposed || _ended || _blocked || _nativeTerminal != null) return;
        if (frameNumber <= _lastFrame) return; // Same-frame reads cannot establish stability.
        _lastFrame = frameNumber;
        if (now < _lastTime) { Fail("Monotonic clock regressed."); return; }
        _lastTime = now;
        _runner?.Poll(SessionId, now);
        DrainSearchEvents();
        if (_paused) return;
        if (_pending is { } waiting && now - waiting.Started > OutcomeTimeout)
        {
            PendingTimedOut = true;
            Fail(_runner == null ? "Outcome unconfirmed within eight seconds; no rejection inferred." : "No correlated native outcome within eight seconds.");
            return;
        }
        if (_pending is { SettlingMismatch: { } unsettled, AcknowledgedAt: { } acknowledgedAt }
            && now - acknowledgedAt >= StatusSettlementTimeout)
        {
            _lastMismatch = unsettled;
            Add(new(_revision, "Mismatch", "Acknowledged action status fields did not settle within 500ms; last observed mismatch retained.")
                { Mismatch = unsettled });
            Fail("Acknowledged action status fields did not settle within 500ms; no model fields substituted.");
            return;
        }
        if (executingAction || !frame.HasValue)
        {
            ClearSamples();
            Invalidate(executingAction ? "Native action in progress; no recommendation is current." : "Native frame unavailable; no simulated fallback.");
            if (!executingAction)
            {
                _unavailableSince ??= now;
                if (now - _unavailableSince > TimeSpan.FromSeconds(3)) Fail("Native reader unavailable for three seconds.");
            }
            else _unavailableSince = null;
            return;
        }
        _unavailableSince = null;
        var observed = frame.Value;
        try { observed.Validate(Craft); }
        catch (ArgumentException) { Fail("Native frame invalid or outside supported ordinary mechanics."); return; }
        if (_candidate != observed)
        {
            _candidate = observed;
            _samples = 1;
            Invalidate("Waiting for second identical sample on a different host frame.");
            return;
        }
        if (++_samples < 2) return;
        if (_pending is { } pending)
        {
            if (observed == pending.Before || !EligibleForConfirmation(pending)
                || _nativeContext != null && pending.Acknowledgement == null)
            {
                Invalidate("Awaiting request response or changed native outcome; unchanged reads do not confirm execution.");
                return;
            }
            // Availability and combo are tracked history, not direct native fields. Promote them
            // only after every independently observed supported field and full step index agree.
            var available = _knownState.TrainedPerfectionAvailable && pending.Action != VulcanSkill.TrainedPerfection;
            var combo = pending.Action == VulcanSkill.StandardTouch && _knownState.Combo != VulcanSkill.BasicTouch
                ? VulcanSkill.None : pending.Action;
            var actual = observed.ToState(available, combo);
            var expected = pending.Predicted with
            {
                Progress = Math.Min(Craft.RequiredProgress, pending.Predicted.Progress),
                Durability = Math.Max(0, pending.Predicted.Durability),
            };
            var conditionMatches = actual.Condition == expected.Condition
                || pending.Action != VulcanSkill.FinalAppraisal && _knownState.Condition == Condition.Normal
                    && actual.Condition is Condition.Good or Condition.Excellent;
            if (observed.StepIndex != pending.ExpectedIndex || !conditionMatches
                || pending.Acknowledgement is { } ack && (observed.StepIndex != ack.StepIndex
                    || observed.Progress != ack.Progress || observed.Quality != ack.Quality
                    || observed.Durability != ack.Durability || (int)observed.Condition + 1 != ack.ConditionPlusOne)
                || (actual with { Condition = expected.Condition }) != expected)
            {
                var mismatch = AdaptiveShadowMismatch.Compare(pending.ExpectedIndex, observed.StepIndex, expected, actual, conditionMatches);
                // Only exact acknowledgement-matched scalars may wait for independent status
                // replication. Nothing is promoted until the entire real frame matches twice.
                var statusOnly = observed.StepIndex == pending.ExpectedIndex && conditionMatches
                    && actual.FirstStep == expected.FirstStep && actual.Progress == expected.Progress
                    && actual.Quality == expected.Quality && actual.Durability == expected.Durability
                    && actual.CP == expected.CP && actual.Combo == expected.Combo
                    && actual.TrainedPerfectionAvailable == expected.TrainedPerfectionAvailable
                    && pending.Acknowledgement is { } matched && observed.StepIndex == matched.StepIndex
                    && observed.Progress == matched.Progress && observed.Quality == matched.Quality
                    && observed.Durability == matched.Durability && (int)observed.Condition + 1 == matched.ConditionPlusOne;
                if (statusOnly && pending.AcknowledgedAt is { } at && now - at < StatusSettlementTimeout)
                {
                    _pending = pending with { SettlingMismatch = mismatch };
                    Invalidate("Acknowledged scalars match; waiting up to 500ms for independently observed status fields to settle.");
                    return;
                }
                _lastMismatch = mismatch;
                Add(new(_revision, "Mismatch", "Stable observed outcome differs from request prediction. No model fields substituted.", pending.Before, observed, pending.Action)
                    { Mismatch = _lastMismatch });
                Fail("Native/model mismatch or untracked action; history is no longer usable.");
                return;
            }
            ++CorrelatedActions;
            LastConfirmedRequest = pending.Ticket;
            _runner?.CorrelateAction(SessionId, pending.Action, now);
            _knownState = actual;
            _knownFrame = observed;
            _pending = null;
            Add(new(_revision, "Correlated", _runner == null
                ? "Exact experimental native acknowledgement and independent stable fields correlated; not blanket native validation."
                : "Stable request-correlated outcome; NOT native action-ID confirmation.", pending.Before, observed, pending.Action));
        }
        else if (observed != _knownFrame)
        {
            Add(new(_revision, "Untracked", "Observed state changed without a tracked request.", _knownFrame, observed));
            Fail("Untracked native transition; refusing to guess combo or remaining uses.");
            return;
        }
        _lastStableFrame = observed;
        var terminal = _simulation.Classify(_knownState);
        if (terminal == AdaptiveOutcome.Success) _terminalObservation = "ObservedSuccess";
        else if (terminal == AdaptiveOutcome.Failure) _terminalObservation = "ObservedFailure";
        if (_analysisMissing)
        {
            ++_revision;
            _analysisMissing = false;
            Status = _runner == null ? "Stable acknowledgement-correlated evidence; live host must separately authorize one-use execution."
                : "Stable request-correlated history; historical analysis only, never executable (native action identity unconfirmed).";
            Add(new(_revision, "Observation", Status, After: observed));
            _runner?.Submit(SessionId, _revision, observed, _knownState, now);
            DrainSearchEvents();
        }
        Poll();
    }

    public void Poll()
    {
        if (_disposed || _ended || _blocked || _paused) return;
        _runner?.Poll(SessionId, _lastTime);
        DrainSearchEvents();
    }

    public bool CheckToolContext(uint? currentMainHandItemId, TimeSpan now)
    {
        if (_disposed || _ended || _blocked) return false;
        if (now < _lastTime) { Fail("Monotonic clock regressed."); return false; }
        _lastTime = now;
        if (Craft.Tool is { } tool && currentMainHandItemId == tool.MainHandItemId) return true;
        Add(new(_revision, "ToolContextChanged", $"Captured tool={Craft.Tool?.MainHandItemId.ToString() ?? "unknown"}; current={currentMainHandItemId?.ToString() ?? "unavailable"}."));
        Fail("Equipped main-hand changed or tool identity unavailable; captured bonus/history revoked.");
        return false;
    }

    public void SetPaused(bool paused, TimeSpan? now = null)
    {
        if (_disposed || _ended || _blocked || _paused == paused) return;
        var time = now ?? _lastTime;
        if (time < _lastTime) { Fail("Monotonic clock regressed."); return; }
        _lastTime = time;
        if (paused) _pausedAt = time;
        else
        {
            if (_pending is { } pending && _pausedAt.HasValue)
                _pending = pending with { Started = pending.Started + (time - _pausedAt.Value) };
            _pausedAt = null;
            _unavailableSince = null;
        }
        _paused = paused;
        ClearSamples();
        _analysisMissing = true;
        if (paused) _runner?.Revoke(SessionId, time, "Shadow paused; historical in-flight work revoked.");
        else _runner?.Resume(SessionId, time);
        DrainSearchEvents();
        Status = _runner == null
            ? paused ? "Live confirmation paused; no current recommendation." : "Live confirmation resumed; fresh repeated native samples required."
            : paused ? "Shadow paused; no current recommendation." : "Shadow resumed; fresh repeated native samples required.";
        Add(new(_revision, paused ? "Pause" : "Resume", Status));
    }

    private void Invalidate(string reason)
    {
        if (Status != reason) Add(new(_revision, "Waiting", reason));
        Status = reason;
        if (_analysisMissing) return;
        _analysisMissing = true;
        ++_revision; // Current observation invalidated; captured historical work deliberately continues.
    }

    private void ClearSamples() { _candidate = null; _samples = 0; }
    private void Fail(string reason)
    {
        _blocked = true;
        _blockedReason = reason;
        Status = reason;
        _pending = null;
        _analysisMissing = true;
        _runner?.Revoke(SessionId, _lastTime, reason);
        DrainSearchEvents();
        Add(new(_revision, "Blocked", reason));
    }
    private void Add(AdaptiveShadowEvent entry)
    {
        ++EventVersion;
        if (_events.Count == 64) _events.RemoveAt(0);
        _events.Add(entry with { Sequence = EventVersion, Elapsed = _lastTime - _startedAt });
    }

    private void DrainSearchEvents()
    {
        if (_runner == null) return;
        foreach (var entry in _runner.DrainEvents(SessionId))
        {
            Add(new(entry.Revision, entry.Kind, entry.Detail) { Search = entry });
        }
    }

    public void End(string reason, TimeSpan now, bool managedCraftEnded = false)
    {
        if (_disposed || _ended) return;
        if (now < _lastTime) Fail("Monotonic clock regressed at session end.");
        else _lastTime = now;
        _ended = true;
        _managedCraftEnded = managedCraftEnded;
        _endReason = reason;
        _pending = null;
        _analysisMissing = true;
        _runner?.Revoke(SessionId, _lastTime, reason);
        DrainSearchEvents();
        _finalAnalysis = AnalysisSummary;
        _finalResults = Array.AsReadOnly(new List<AdaptiveHistoricalResult>(HistoricalResults).ToArray());
        Status = reason;
        Add(new(_revision, "End", _runner == null ? $"{reason} Live terminal observation: {_terminalObservation}."
            : $"{reason} Shadow terminal observation: {_terminalObservation}."));
    }

    public void Dispose()
    {
        if (_disposed) return;
        End("Shadow session disposed.", _lastTime);
        _disposed = true;
        if (_ownsRunner) _runner?.Dispose();
    }
}
