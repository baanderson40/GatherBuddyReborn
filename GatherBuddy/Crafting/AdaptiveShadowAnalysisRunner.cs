using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>
/// Framework-owned historical-only lane. One worker plus latest pending root, including across sessions.
/// Workers only compute immutable results; only Poll retains/replays them. No live coordinator or leases.
/// </summary>
public sealed class AdaptiveShadowAnalysisRunner : IDisposable
{
    private sealed record Work(AdaptiveHistoricalObservation Source, long Epoch, TimeSpan DispatchedAt,
        CancellationTokenSource Cancellation, Task<AdaptiveSearchResult> Task);
    private readonly IAdaptiveSearchScheduler _scheduler;
    private readonly List<AdaptiveHistoricalResult> _history = new();
    private readonly Queue<AdaptiveSearchLifecycleEvent> _events = new();
    private Work? _worker;
    private AdaptiveHistoricalObservation? _pending;
    private AdaptiveSimulation? _simulation;
    private AdaptiveSearchLimits _limits = new();
    private Guid _session;
    private long _epoch;
    private long _revision = -1;
    private long _generation;
    private TimeSpan _start;
    private TimeSpan _lastTime;
    private bool _accepting;
    private bool _disposed;
    private int _submitted, _skipped, _dispatched, _cancellationRequests, _observed, _discarded, _retained, _validated, _targets;
    private long _droppedResults, _droppedEvents;
    private AdaptiveSearchLifecycleEvent? _lastEvent, _lastResult;
    private AdaptiveSeedTracker _seeds = new(null, "No cached seed supplied.");

    public AdaptiveShadowAnalysisRunner(IAdaptiveSearchScheduler? scheduler = null) => _scheduler = scheduler ?? new Scheduler();
    internal Task WorkerCompletion => _worker?.Task ?? Task.CompletedTask;
    public IReadOnlyList<AdaptiveHistoricalResult> History => _history.AsReadOnly();
    public AdaptiveHistoricalAnalysisSummary Summary => new(_submitted, _skipped, _dispatched, _cancellationRequests,
        _observed, _discarded, _retained, _validated, _targets, _droppedResults, _droppedEvents, _lastEvent, _lastResult);
    public IReadOnlyList<AdaptiveHistoricalResult> GetHistory(Guid session)
        => session == _session ? History : Array.Empty<AdaptiveHistoricalResult>();
    public AdaptiveHistoricalAnalysisSummary GetSummary(Guid session)
        => session == _session ? Summary : new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);

    public Guid Begin(AdaptiveCraftSnapshot craft, AdaptiveCraftGoal goal, AdaptiveSearchLimits? limits, TimeSpan now,
        AdaptivePlanSeed? seed = null, string seedCaptureReason = "No cached seed supplied.")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var simulation = new AdaptiveSimulation(craft, goal);
        limits ??= new();
        limits.Validate();
        CancelWorker();
        ++_epoch;
        _session = Guid.NewGuid();
        _simulation = simulation;
        _limits = limits;
        _pending = null;
        _revision = -1;
        _generation = 0;
        _start = _lastTime = now;
        _accepting = true;
        _history.Clear();
        _events.Clear();
        _submitted = _skipped = _dispatched = _cancellationRequests = _observed = _discarded = _retained = _validated = _targets = 0;
        _droppedResults = _droppedEvents = 0;
        _lastEvent = _lastResult = null;
        _seeds = new(seed, seedCaptureReason);
        return _session;
    }

    public bool Submit(Guid session, long revision, AdaptiveObservedFrame native, AdaptiveSearchState state, TimeSpan now)
    {
        if (!Current(session, now) || !_accepting || revision <= _revision) return false;
        native.Validate(_simulation!.Craft);
        state.Validate(_simulation.Craft);
        if (native.ToState(state.TrainedPerfectionAvailable, state.Combo) != state)
            throw new ArgumentException("Historical root differs from captured native scalars.");
        _revision = revision;
        if (_simulation.Classify(state) is AdaptiveOutcome.Success or AdaptiveOutcome.Failure) return false;
        var source = new AdaptiveHistoricalObservation(new(_session, revision, ++_generation,
            _simulation.Craft, _simulation.Goal, _limits, state), native, now - _start) { ActionOrdinal = _seeds.Ordinal };
        ++_submitted;
        if (_pending is { } skipped)
        {
            ++_skipped;
            Trace(skipped, now, "HistoricalObservationSkipped", "Latest pending observation replaced this root before dispatch.");
        }
        _pending = source;
        Trace(source, now, "SearchQueued", _worker == null ? "Historical root queued." : "Waiting for previous worker; latest pending root only.");
        StartPending(now);
        return true;
    }

    public void CorrelateAction(Guid session, VulcanSkill action, TimeSpan now)
    {
        if (Current(session, now) && _accepting) _seeds.Correlate(action);
    }

    public void RecordAttempt(Guid session, long revision, VulcanSkill action, TimeSpan now)
    {
        if (!Current(session, now) || !_accepting) return;
        AdaptiveHistoricalObservation Link(AdaptiveHistoricalObservation source)
            => source with { ExistingSolverAttempt = action, AttemptedAt = now - _start };
        var linked = false;
        if (_worker is { } work && work.Epoch == _epoch && work.Source.Identity.ObservationRevision == revision)
        { _worker = work with { Source = Link(work.Source) }; linked = true; }
        if (_pending is { } pending && pending.Identity.ObservationRevision == revision)
        { _pending = Link(pending); linked = true; }
        for (var i = 0; i < _history.Count; ++i)
        {
            if (_history[i].Source.Identity.ObservationRevision != revision) continue;
            _history[i] = _history[i] with { Source = Link(_history[i].Source) };
            linked = true;
        }
        if (linked) TraceRevision(revision, now, "HistoricalAttemptLinked", $"Existing solver requested {action}; not native action-ID confirmation. Historical search is not cancelled.");
    }

    public void Poll(Guid session, TimeSpan now)
    {
        if (!Current(session, now)) return;
        if (_worker is { Task.IsCompleted: true } completed)
        {
            _worker = null;
            AdaptiveSearchResult? result = null;
            Exception? failure = null;
            try { result = completed.Task.GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; }
            var revoked = completed.Epoch != _epoch || completed.Source.Identity.SessionId != _session
                || completed.Cancellation.IsCancellationRequested || !_accepting;
            completed.Cancellation.Dispose();
            if (revoked)
            {
                ++_discarded;
                TraceRevision(_revision, now, "RevokedWorkerDrained", "Revoked worker finished; no historical result retained. No replacement overlapped it.");
            }
            else
            {
                ++_observed;
                Trace(completed.Source, now, "SearchResultObserved", failure != null
                    ? $"Worker fault: {failure.GetType().Name}." : result?.Reason ?? "Empty worker result.", result);
                Retain(completed.Source, completed.DispatchedAt, now, result, failure);
            }
        }
        StartPending(now);
    }

    public void Revoke(Guid session, TimeSpan now, string reason)
    {
        if (!Current(session, now)) return;
        _accepting = false;
        ++_epoch;
        if (_pending is { } pending)
        {
            ++_skipped;
            Trace(pending, now, "HistoricalObservationSkipped", $"Pending root revoked: {reason}");
            _pending = null;
        }
        if (_worker is { } work && !work.Cancellation.IsCancellationRequested)
        {
            ++_cancellationRequests;
            Trace(work.Source, now, "SearchCancellationRequested", reason);
        }
        CancelWorker();
    }

    public void Resume(Guid session, TimeSpan now)
    {
        if (Current(session, now)) _accepting = true; // Host must obtain fresh repeated samples before Submit.
    }

    public AdaptiveSearchLifecycleEvent[] DrainEvents(Guid session)
    {
        if (session != _session || _events.Count == 0) return Array.Empty<AdaptiveSearchLifecycleEvent>();
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    private bool Current(Guid session, TimeSpan now)
    {
        if (_disposed || session != _session) return false;
        if (now < _lastTime) throw new ArgumentException("Historical runner clock regressed.");
        _lastTime = now;
        return true;
    }

    private void StartPending(TimeSpan now)
    {
        if (_worker != null || !_accepting || _pending is not { } source) return;
        _pending = null;
        source = source with { Seeding = _seeds.Capture(source.Identity.Craft, source.ActionOrdinal) };
        var cancellation = new CancellationTokenSource();
        ++_dispatched;
        Trace(source, now, "SearchDispatched", "Historical-only dispatch; actual worker CPU start is not measured.");
        try
        {
            var task = _scheduler.Schedule(new(source.Identity, seeds: source.Seeding.Candidates), cancellation.Token)
                ?? throw new InvalidOperationException("Scheduler returned no task.");
            _worker = new(source, _epoch, now - _start, cancellation, task);
        }
        catch (Exception error)
        {
            cancellation.Dispose();
            Trace(source, now, "SearchDispatchFailed", $"Scheduler failure: {error.GetType().Name}.");
            Retain(source, now - _start, now, null, error);
        }
    }

    private void Retain(AdaptiveHistoricalObservation source, TimeSpan dispatchedAt, TimeSpan now,
        AdaptiveSearchResult? result, Exception? failure)
    {
        AdaptivePlan? validated = null;
        var replay = "NotApplicable";
        var reason = failure != null ? $"Worker failure: {failure.GetType().Name}." : result?.Reason ?? "Empty worker result.";
        if (failure != null || result == null) replay = "WorkerFailed";
        else if (result.Status == AdaptiveSearchStatus.Found)
        {
            if (result.Plan == null || result.Plan.Actions.Count > source.Identity.Limits.MaximumPlanLength
                || !AdaptivePlanValidator.TryValidate(new(source.Identity.Craft, source.Identity.Goal),
                    source.Identity.State, result.Plan.Actions, out validated))
            {
                validated = null;
                replay = "Invalid";
                reason = "Historical plan failed exact captured-root replay or length bound.";
            }
            else
            {
                replay = "Validated";
                ++_validated;
                if (validated!.TargetReached) ++_targets;
                _seeds.Remember(source, validated);
            }
        }
        var retained = new AdaptiveHistoricalResult(source, dispatchedAt, now - _start,
            result?.Status, reason, result?.Diagnostics, replay, validated);
        if (_history.Count == 16) { _history.RemoveAt(0); ++_droppedResults; }
        _history.Add(retained);
        ++_retained;
        Trace(source, now, "HistoricalResultRetained", $"{replay}: {reason} Not current; never executable.", result);
    }

    private void TraceRevision(long revision, TimeSpan now, string kind, string detail)
        => AddEvent(new(now - _start, revision, 0, kind, detail));
    private void Trace(AdaptiveHistoricalObservation source, TimeSpan now, string kind, string detail, AdaptiveSearchResult? result = null)
        => AddEvent(new(now - _start, source.Identity.ObservationRevision, source.Identity.Generation,
            kind, detail, result?.Status, result?.Diagnostics, source.Identity.Limits));
    private void AddEvent(AdaptiveSearchLifecycleEvent entry)
    {
        _lastEvent = entry;
        if (entry.Kind == "SearchResultObserved") _lastResult = entry;
        if (_events.Count == 64) { _events.Dequeue(); ++_droppedEvents; }
        _events.Enqueue(entry);
    }
    private void CancelWorker() => _worker?.Cancellation.Cancel();
    public void Dispose()
    {
        if (_disposed) return;
        Revoke(_session, _lastTime, "Historical runner disposed.");
        _disposed = true;
        if (_worker is { } worker)
        {
            _worker = null;
            _ = worker.Task.ContinueWith(task => { _ = task.Exception; worker.Cancellation.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class Scheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.Run(() => new AdaptiveSearchEngine().Search(new(request.Identity.Craft, request.Identity.Goal),
                request.Identity.State, request.Identity.Limits, cancellation: cancellation, seeds: request.Seeds), cancellation);
    }
}
