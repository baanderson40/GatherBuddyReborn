using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>
/// Pure lifecycle controller; does not read or execute game actions. The host must call Poll and
/// consume leases on its game thread, with a stable authoritative observation and native action check.
/// Worker tasks never publish recommendations or call host callbacks. Public operations are serialized.
/// </summary>
public sealed class AdaptiveSolveCoordinator : IDisposable
{
    private sealed record Work(AdaptiveSearchRequest Request, CancellationTokenSource Cancellation, Task<AdaptiveSearchResult> Task);
    private readonly object _gate = new();
    private readonly IAdaptiveSearchScheduler _scheduler;
    private Work? _worker;
    private AdaptiveSearchRequest? _pending;
    private AdaptiveDecisionIdentity? _decision;
    private AdaptiveSimulation? _simulation;
    private AdaptiveSearchLimits _limits = new();
    private Guid _session;
    private long _revision = -1;
    private long _freshAfterRevision = -1;
    private long _generation;
    private AdaptiveSearchState? _observed;
    private AdaptivePlan? _plan;
    private VulcanSkill[]? _continuation;
    private AdaptivePlanSeed? _startupSeed;
    private AdaptiveLiveSeedStatus _startupSeedStatus = new("Unseeded", "No startup seed supplied.");
    public AdaptiveLiveSeedStatus StartupSeedStatus { get { lock (_gate) return _startupSeedStatus; } }
    private AdaptiveExecutionLease? _reserved;
    private AdaptiveExecutionLease? _executing;
    private AdaptivePlan? _executingPlan;
    private bool _executionAccepted;
    private bool _paused;
    private bool _active;
    private bool _disposed;
    private AdaptiveDecisionPhase _phase;
    private string _reason = "No craft session.";
    private long _rejectedResults;
    private long _predictionMismatches;
    private AdaptiveSearchDiagnostics? _diagnostics;
    private readonly Func<TimeSpan>? _diagnosticsClock;
    private readonly Func<TimeSpan>? _decisionDiagnosticsClock;
    private readonly Queue<AdaptiveSearchLifecycleEvent> _lifecycle = new();
    public long DroppedLifecycleEvents { get; private set; }
    private bool _captureDecisionDiagnostics;
    private readonly Queue<AdaptiveCoordinatorDiagnostic> _decisionDiagnostics = new();
    public long DroppedDecisionDiagnostics { get; private set; }

    public void EnableDecisionDiagnostics() { lock (_gate) _captureDecisionDiagnostics = true; }
    public AdaptiveCoordinatorDiagnostic[] DrainDecisionDiagnostics()
    {
        lock (_gate)
        {
            var entries = _decisionDiagnostics.ToArray();
            _decisionDiagnostics.Clear();
            return entries;
        }
    }

    public AdaptiveSolveCoordinator(IAdaptiveSearchScheduler? scheduler = null, Func<TimeSpan>? diagnosticsClock = null,
        Func<TimeSpan>? decisionDiagnosticsClock = null)
        => (_scheduler, _diagnosticsClock, _decisionDiagnosticsClock) = (scheduler ?? new ThreadPoolScheduler(), diagnosticsClock, decisionDiagnosticsClock);

    public AdaptiveSearchLifecycleEvent[] DrainLifecycleEvents()
    {
        lock (_gate)
        {
            if (_lifecycle.Count == 0) return Array.Empty<AdaptiveSearchLifecycleEvent>();
            var events = _lifecycle.ToArray();
            _lifecycle.Clear();
            return events;
        }
    }

    private void Trace(AdaptiveDecisionIdentity identity, string kind, string detail, AdaptiveSearchResult? result = null)
    {
        CaptureDecision(identity, kind, detail, result);
        if (_diagnosticsClock == null) return;
        if (_lifecycle.Count == 64) { _lifecycle.Dequeue(); ++DroppedLifecycleEvents; }
        _lifecycle.Enqueue(new(_diagnosticsClock(), identity.ObservationRevision, identity.Generation,
            kind, detail, result?.Status, result?.Diagnostics, identity.Limits));
    }

    private void CaptureDecision(AdaptiveDecisionIdentity identity, string kind, string detail, AdaptiveSearchResult? result = null)
    {
        if (!_captureDecisionDiagnostics) return;
        if (_decisionDiagnostics.Count == 64) { _decisionDiagnostics.Dequeue(); ++DroppedDecisionDiagnostics; }
        var publication = kind == "SearchPublication";
        var validated = publication && identity == _decision && _phase is AdaptiveDecisionPhase.Ready or AdaptiveDecisionPhase.Completed ? _plan : null;
        var diagnostics = AdaptiveCoordinatorDiagnostic.Detach(result?.Diagnostics);
        var warning = result?.Diagnostics != null && diagnostics == null ? "Oversized seed diagnostics omitted (maximum two)."
            : validated?.Actions.Count > 64 ? "Validated plan actions omitted (diagnostic maximum64); execution limits unchanged." : null;
        _decisionDiagnostics.Enqueue(new(identity, kind, AdaptiveCoordinatorDiagnostic.Text(detail), result?.Status,
            diagnostics, publication ? _phase : null, AdaptivePublishedPlan.Capture(validated), warning,
            validated is { Actions.Count: > 0 } ? validated.Actions[0] : VulcanSkill.None, _decisionDiagnosticsClock?.Invoke()));
    }

    public AdaptiveDecisionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_phase, _session, _decision,
                    _phase == AdaptiveDecisionPhase.Ready && _plan is { Actions.Count: > 0 } ? _plan.Actions[0] : VulcanSkill.None,
                    _reason, _rejectedResults, _predictionMismatches, _diagnostics);
        }
    }

    // Completion is not publication. Used by deterministic async tests and future shutdown/drain logic.
    internal Task WorkerCompletion { get { lock (_gate) return _worker?.Task ?? Task.CompletedTask; } }

    public Guid Begin(AdaptiveCraftSnapshot craft, AdaptiveCraftGoal goal, AdaptiveSearchLimits? limits = null,
        AdaptivePlanSeed? startupSeed = null, AdaptiveLiveSeedStatus? seedStatus = null)
    {
        var simulation = new AdaptiveSimulation(craft, goal);
        limits ??= new();
        limits.Validate();
        lock (_gate)
        {
            ThrowIfDisposed();
            Invalidate();
            _session = Guid.NewGuid();
            _simulation = simulation;
            _limits = limits;
            _revision = -1;
            _freshAfterRevision = -1;
            _observed = null;
            _continuation = null;
            _executing = null;
            _startupSeed = startupSeed;
            _startupSeedStatus = seedStatus ?? new(startupSeed == null ? "Unseeded" : "AwaitingRootReplay",
                startupSeed == null ? "No startup seed supplied." : "Startup candidate requires stable actual-root replay.",
                startupSeed?.Provenance.RequestKey, startupSeed?.Provenance.Source, startupSeed?.Actions.Count);
            _executingPlan = null;
            _executionAccepted = false;
            _paused = false;
            _active = true;
            _diagnostics = null;
            SetPhase(AdaptiveDecisionPhase.Idle, "Awaiting first authoritative observation.");
            return _session;
        }
    }

    /// <summary>
    /// Revision is host-assigned and increases for newly confirmed observations, not every frame.
    /// Null means live data unavailable; it invalidates ready work and never substitutes prediction.
    /// </summary>
    public bool Observe(Guid session, long revision, AdaptiveSearchState? state)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_active || session != _session || revision < 0 || revision < _revision) return false;
            if (revision == _revision)
            {
                if (state == _observed) return true; // Same frame/read: no work restart or lease renewal.
                Invalidate();
                _active = false;
                SetPhase(AdaptiveDecisionPhase.Failed, "Observation revision reused with different state; begin a new session.");
                return false;
            }
            _revision = revision;
            _observed = state;
            Invalidate();
            if (state.HasValue)
            {
                try { state.Value.Validate(_simulation!.Craft); }
                catch (ArgumentException)
                {
                    _observed = null;
                    _continuation = null;
                    SetPhase(_paused ? AdaptiveDecisionPhase.Paused : AdaptiveDecisionPhase.Blocked, "Invalid or unsupported live observation.");
                    return false;
                }
            }
            if (_paused) return true;
            ProcessObservation();
            return true;
        }
    }

    /// <summary>Nonblocking. Only this host-driven operation publishes a worker result.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_worker is { Task.IsCompleted: true } completed)
            {
                _worker = null;
                AdaptiveSearchResult? result = null;
                Exception? failure = null;
                try { result = completed.Task.GetAwaiter().GetResult(); }
                catch (Exception error) { failure = error; }
                var cancelled = completed.Cancellation.IsCancellationRequested;
                completed.Cancellation.Dispose();
                Trace(completed.Request.Identity, "SearchResultObserved",
                    failure != null ? $"Worker fault: {failure.GetType().Name}." : result?.Reason ?? "Worker returned no result.", result);
                if (!_active || _paused || cancelled || completed.Request.Identity != _decision)
                {
                    ++_rejectedResults;
                    Trace(completed.Request.Identity, "SearchResultRejected",
                        $"Not current: active={_active}; paused={_paused}; cancellationRequested={cancelled}; identityMatches={completed.Request.Identity == _decision}.", result);
                }
                else if (failure != null || result == null)
                {
                    SetPhase(AdaptiveDecisionPhase.Failed, $"Adaptive search failed: {failure?.GetType().Name ?? "EmptyResult"}.");
                    Trace(completed.Request.Identity, "SearchPublication", $"{_phase}: {_reason}", result);
                }
                else
                {
                    Publish(result!, completed.Request);
                    Trace(completed.Request.Identity, "SearchPublication", $"{_phase}: {_reason}", result);
                }
            }
            StartPending();
        }
    }

    public bool TryCreateLease(Guid session, long revision, AdaptiveSearchState liveState, out AdaptiveExecutionLease? lease)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            lease = null;
            if (!CheckExecutionInput(session, revision, liveState) || _reserved != null) return false;
            var outcome = _simulation!.TryApply(liveState, _plan!.Actions[0], out var predicted);
            if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) return false;
            lease = _reserved = new(_decision!, _plan.Actions[0], predicted);
            return true;
        }
    }

    /// <summary>
    /// Last pure check before host's native execution check/call. Consuming once prevents duplicates;
    /// this type cannot guarantee native execution or atomicity with the game. Do not consume ahead of a delay.
    /// </summary>
    public bool TryConsumeLease(AdaptiveExecutionLease lease, Guid session, long revision, AdaptiveSearchState liveState)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (lease == null || !ReferenceEquals(lease, _reserved) || lease.Decision != _decision
                || !CheckExecutionInput(session, revision, liveState)) return false;
            _reserved = null;
            _executing = lease;
            _executingPlan = _plan;
            _executionAccepted = false;
            _plan = null;
            SetPhase(AdaptiveDecisionPhase.AwaitingObservation, "Lease consumed; awaiting execution response and authoritative outcome.");
            return true;
        }
    }

    /// <summary>Acceptance is not proof of action success. Only a later observation can advance a plan.</summary>
    public bool ReportExecution(AdaptiveExecutionLease lease, bool accepted)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (lease == null || !_active || !ReferenceEquals(lease, _executing) || _executionAccepted) return false;
            if (!accepted)
            {
                // Retain the unconsumed whole plan as a seed, never its tail. Require a newer read to retry.
                _continuation = _executingPlan?.Actions.ToArray();
                _executing = null;
                _executingPlan = null;
                SetPhase(_paused ? AdaptiveDecisionPhase.Paused : AdaptiveDecisionPhase.Blocked,
                    "Execution rejected; awaiting a fresh authoritative observation before replanning.");
                return true;
            }
            _executionAccepted = true;
            if (!_paused) ProcessObservation();
            return true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_active) return;
            Invalidate();
            _paused = true;
            _continuation = null;
            SetPhase(AdaptiveDecisionPhase.Paused, "Paused; leases revoked and search cancelled.");
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_active || !_paused) return;
            _paused = false;
            _freshAfterRevision = _revision;
            // Do not reuse an observation collected before/during pause. Observe requires a newer revision.
            SetPhase(AdaptiveDecisionPhase.Blocked, "Resumed; awaiting a fresh authoritative observation.");
        }
    }

    public void Stop(bool completed = false)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Invalidate();
            _active = false;
            _executing = null;
            _executingPlan = null;
            _continuation = null;
            SetPhase(completed ? AdaptiveDecisionPhase.Completed : AdaptiveDecisionPhase.Cancelled,
                completed ? "Craft session finished." : "Craft session stopped.");
            _startupSeed = null;
        }
    }

    private void ProcessObservation()
    {
        if (_revision <= _freshAfterRevision)
        {
            SetPhase(AdaptiveDecisionPhase.Blocked, "Awaiting a new authoritative observation after resume.");
            return;
        }
        if (_observed is not { } state)
        {
            _continuation = null;
            SetPhase(AdaptiveDecisionPhase.Blocked, "Authoritative live state unavailable; no simulated fallback.");
            return;
        }
        if (_executing != null)
        {
            if (!_executionAccepted || _revision <= _executing.Decision.ObservationRevision || state == _executing.Decision.State)
            {
                SetPhase(AdaptiveDecisionPhase.AwaitingObservation, "Waiting for a confirmed action outcome; duplicate execution prohibited.");
                return;
            }
            if (!MatchesPrediction(_executing, state))
            {
                ++_predictionMismatches;
                _continuation = null;
                _executing = null;
                _executingPlan = null;
                _active = false;
                SetPhase(AdaptiveDecisionPhase.Failed, "Observed action outcome differs from simulation; begin a new session after investigation.");
                return;
            }
            _continuation = _executingPlan!.Actions.Skip(1).ToArray();
            _executing = null;
            _executingPlan = null;
            _executionAccepted = false;
        }
        var outcome = _simulation!.Classify(state);
        if (outcome is AdaptiveOutcome.Success or AdaptiveOutcome.Failure)
        {
            _active = false;
            SetPhase(outcome == AdaptiveOutcome.Success ? AdaptiveDecisionPhase.Completed : AdaptiveDecisionPhase.Failed,
                outcome == AdaptiveOutcome.Success ? "Observed craft completion." : "Observed unsuccessful terminal craft.");
            return;
        }
        _decision = new(_session, _revision, ++_generation, _simulation.Craft, _simulation.Goal, _limits, state);
        AdaptiveSeedCandidate[]? seeds = null;
        if (_startupSeed is { } seed)
        {
            _startupSeed = null; // First authoritative root only; never a mid-craft cache injection.
            var accepted = state.FirstStep && state.Progress == 0 && state.Combo == VulcanSkill.None
                && AdaptiveSeedPreparation.MatchesCraft(seed, _simulation.Craft) && seed.Actions.Count <= _limits.MaximumPlanLength
                && AdaptivePlanValidator.TryValidate(_simulation, state, seed.Actions, out _);
            _startupSeedStatus = _startupSeedStatus with
            {
                Phase = accepted ? "Validated" : "Unseeded",
                Reason = accepted ? "Complete startup macro independently replayed from actual stable root and active goal; subtarget completion is allowed."
                    : "Startup candidate failed actual stable-root/context/goal replay; using unseeded search.",
                ObservedInitialQuality = state.Quality,
            };
            if (accepted) seeds = [new(seed, 0, 0)];
        }
        _pending = new(_decision, _continuation, seeds);
        SetPhase(AdaptiveDecisionPhase.Searching, "Searching from authoritative observation.");
        Trace(_decision, "SearchQueued", _worker == null ? "No previous worker outstanding." : "Waiting for previous worker to finish; no overlap permitted.");
        StartPending();
    }

    private void StartPending()
    {
        if (_worker != null || _pending == null || !_active || _paused) return;
        var request = _pending;
        _pending = null;
        var cancellation = new CancellationTokenSource();
        try
        {
            Trace(request.Identity, "SearchDispatched", "Schedule called; actual worker CPU start is not measured.");
            _worker = new(request, cancellation, _scheduler.Schedule(request, cancellation.Token)
                ?? throw new InvalidOperationException("Scheduler returned no task."));
        }
        catch (Exception error)
        {
            cancellation.Dispose();
            SetPhase(AdaptiveDecisionPhase.Failed, $"Adaptive worker could not start: {error.GetType().Name}.");
            Trace(request.Identity, "SearchDispatchFailed", _reason);
        }
    }

    private void Publish(AdaptiveSearchResult result, AdaptiveSearchRequest request)
    {
        _diagnostics = result.Diagnostics;
        if (result.Status != AdaptiveSearchStatus.Found || result.Plan == null)
        {
            SetPhase(result.Status == AdaptiveSearchStatus.Cancelled ? AdaptiveDecisionPhase.Cancelled : AdaptiveDecisionPhase.Blocked, result.Reason);
            return;
        }
        // Defense in depth: never trust a worker plan without replay from this exact request.
        if (result.Plan.Actions.Count > _limits.MaximumPlanLength
            || !AdaptivePlanValidator.TryValidate(_simulation!, _decision!.State, result.Plan.Actions, out _plan))
        {
            SetPhase(AdaptiveDecisionPhase.Failed, "Worker returned a plan that failed authoritative-root replay.");
            return;
        }
        if (_plan!.Actions.Count == 0)
        {
            SetPhase(AdaptiveDecisionPhase.Completed, "No action required.");
            return;
        }
        // Defend the startup baseline against faulty schedulers too. Replay bounded action
        // lists; neither seed metadata nor worker final-state metadata is state authority.
        var reason = result.Reason;
        foreach (var seed in request.Seeds)
            if (seed.Actions.Count <= _limits.MaximumPlanLength
                && AdaptivePlanValidator.TryValidate(_simulation!, _decision!.State, seed.Actions, out var baseline)
                && AdaptivePlanValidator.IsBetter(baseline!, _plan))
            {
                _plan = baseline;
                reason = "Validated startup baseline retained over lower-ranked worker result.";
            }
        SetPhase(AdaptiveDecisionPhase.Ready, reason);
    }

    private bool MatchesReady(Guid session, long revision, AdaptiveSearchState state)
        => _active && !_paused && _phase == AdaptiveDecisionPhase.Ready && _executing == null
            && session == _session && revision == _revision && _observed == state && _decision?.State == state
            && _plan is { Actions.Count: > 0 };

    private static bool MatchesPrediction(AdaptiveExecutionLease execution, AdaptiveSearchState actual)
    {
        var predicted = execution.PredictedState;
        if ((actual with { Condition = predicted.Condition }) != predicted) return false;
        if (actual.Condition == predicted.Condition) return true;
        // Only Normal's random next condition is unknown in the ordinary-craft model.
        // Good/Excellent/Poor transitions and Final Appraisal's preserved condition are deterministic.
        return execution.Action != VulcanSkill.FinalAppraisal && execution.Decision.State.Condition == Condition.Normal
            && actual.Condition is Condition.Good or Condition.Excellent;
    }

    private bool CheckExecutionInput(Guid session, long revision, AdaptiveSearchState state)
    {
        if (_active && !_paused && _phase == AdaptiveDecisionPhase.Ready
            && session == _session && revision >= _revision && state != _observed)
        {
            Invalidate();
            SetPhase(AdaptiveDecisionPhase.Blocked, "Execution-time observation changed; awaiting a fresh authoritative revision.");
            return false;
        }
        return MatchesReady(session, revision, state);
    }

    private void Invalidate()
    {
        if (_decision != null) CaptureDecision(_decision, "DecisionRevoked", "Decision invalidated; recommendation no longer current.");
        if (_worker is { } worker && !worker.Cancellation.IsCancellationRequested)
            Trace(worker.Request.Identity, "SearchCancellationRequested", "Decision invalidated; result cannot publish even if cancellation is ignored.");
        if (_pending is { } pending)
            Trace(pending.Identity, "SearchQueueDiscarded", "Queued observation superseded before dispatch.");
        _worker?.Cancellation.Cancel();
        _pending = null;
        _decision = null;
        _plan = null;
        _reserved = null;
    }

    private void SetPhase(AdaptiveDecisionPhase phase, string reason) => (_phase, _reason) = (phase, reason);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Invalidate();
            _disposed = true;
            _active = false;
            _executing = null;
            _executingPlan = null;
            _continuation = null;
            _startupSeed = null;
            SetPhase(AdaptiveDecisionPhase.Cancelled, "Coordinator disposed.");
            if (_worker is { } worker)
            {
                _worker = null;
                // Observe eventual faults and release CTS even if the host stops polling. No result publication.
                _ = worker.Task.ContinueWith(task =>
                {
                    _ = task.Exception;
                    worker.Cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private sealed class ThreadPoolScheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.Run(() => new AdaptiveSearchEngine().Search(
                new(request.Identity.Craft, request.Identity.Goal), request.Identity.State,
                request.Identity.Limits, request.Continuation, cancellation, request.Seeds), cancellation);
    }
}
