using System;

namespace GatherBuddy.Automation;

// Numeric values match AutoRetainerAPI.Configuration.MultiModeType. Dalamud converts
// IPC arguments across plugin assembly boundaries (including nullable enums).
public enum AutoRetainerProcessingMode { Ventures = 0, Deployables = 1, Both = 2 }
public enum AutoRetainerHandoffState { Monitoring, Yielding, StartingPass, RunningPass, WaitingForIdle, Returning, Settling, Failed, Cancelling }
public sealed record AutoRetainerCharacter(ulong ContentId, string NameWithWorld);

public interface IAutoRetainerCoordinationPort
{
    bool Enabled { get; }
    AutoRetainerProcessingMode Mode { get; }
    string? ActiveOwner { get; }
    bool CanRequestHandoff { get; }
    bool DeferForTimedNode { get; }
    AutoRetainerCharacter? Character { get; }
    bool CharacterReady { get; }
    bool MultiModeEnabled { get; }
    bool Busy { get; }
    bool CanRequestReturn { get; }
    bool CanReturnFromLoggedIn { get; }
    void VerifyAvailable();
    string? FindReadyWork(AutoRetainerProcessingMode mode);
    bool TryYield(out string reason);
    void StartPass(AutoRetainerProcessingMode mode);
    void CancelPass();
    bool TryFinishCancellation(out string reason);
    bool ReturnTo(AutoRetainerCharacter character);
    void Release(bool resume);
    void Log(string message);
}

/// <summary>One shared handoff. Native work and IPC are isolated behind the port.</summary>
public sealed class AutoRetainerCoordinator
{
    private readonly IAutoRetainerCoordinationPort _port;
    private TimeSpan _nextPoll, _deadline;
    private TimeSpan? _readySince;
    private bool _completed, _returnRequested, _resume = true, _yielded, _external;
    private bool _startedPass, _stopRequested, _cancelActivePass;
    private string? _lastWork;
    private AutoRetainerProcessingMode _lastMode;
    private int _repeatedWork;
    private string? _owner;
    private AutoRetainerCharacter? _original;
    private AutoRetainerProcessingMode _mode;
    private string? _observedOwner;
    private AutoRetainerCharacter? _ownerCharacter;
    public AutoRetainerCoordinator(IAutoRetainerCoordinationPort port) => _port = port;
    public AutoRetainerHandoffState State { get; private set; }
    public string Status { get; private set; } = "AutoRetainer coordination idle";
    public Guid HandoffId { get; private set; }
    public bool YieldRequested => State != AutoRetainerHandoffState.Monitoring;
    public bool BlocksAutomation => _yielded;
    public bool OwnsPass => State is AutoRetainerHandoffState.StartingPass or AutoRetainerHandoffState.RunningPass;
    public string? Owner => _owner;
    public bool IsStopping => _stopRequested && YieldRequested && State != AutoRetainerHandoffState.Failed;

    public void OnPassFinished()
    {
        // Subscribe before starting; completion can occur during the IPC call.
        if (OwnsPass) _completed = true;
    }

    public void PreventResume() => _resume = false;

    /// <summary>Explicit owner Stop, unlike Pause. Never cancels an external AR run.</summary>
    public bool StopOwner(string? owner, TimeSpan now)
    {
        if (owner == null || owner != _owner || !YieldRequested) return false;
        if (_stopRequested && State != AutoRetainerHandoffState.Failed) return true;
        _resume = false;
        _stopRequested = true;
        // No AR request was submitted, or we only observed somebody else's mode.
        // The caller has already stopped its own local work, so detach immediately.
        if (!_startedPass || _external)
        {
            try { Finish(now); }
            catch (Exception ex) { SetState(AutoRetainerHandoffState.Failed, $"Stop cleanup failed: {ex.GetBaseException().Message}"); }
            return true;
        }
        _cancelActivePass = !_completed;
        _yielded = true;
        _deadline = now + TimeSpan.FromMinutes(3);
        SetState(AutoRetainerHandoffState.Cancelling, "Stopping AutoRetainer: settling the queue-owned pass");
        return true;
    }

    public void Tick(TimeSpan now)
    {
        try
        {
            if (State == AutoRetainerHandoffState.Monitoring)
            {
                var activeOwner = _port.ActiveOwner;
                if (!_port.Enabled || activeOwner == null)
                {
                    _observedOwner = null;
                    _ownerCharacter = null;
                    return;
                }
                if (activeOwner != _observedOwner)
                {
                    _observedOwner = activeOwner;
                    _ownerCharacter = _port.Character;
                    _lastWork = null;
                    _repeatedWork = 0;
                }
                _ownerCharacter ??= _port.Character;
                if (_ownerCharacter != null && _port.Character is { } currentCharacter && currentCharacter.ContentId != _ownerCharacter.ContentId)
                {
                    _original = _ownerCharacter;
                    _owner = activeOwner;
                    _yielded = true;
                    HandoffId = Guid.NewGuid();
                    SetState(AutoRetainerHandoffState.Failed, "Character changed outside owned handoff; return to original character before recovery");
                    return;
                }
                // Priority inspection is informational, but adapters must not reach
                // scheduler/native helpers before the original player is ready.
                if (_port.Character is not { ContentId: not 0 } || !_port.CharacterReady)
                { Status = "Waiting for player readiness before AutoRetainer monitoring"; return; }
                if (!_port.CanRequestHandoff || now < _nextPoll) return;
                _nextPoll = now + TimeSpan.FromSeconds(2);
                _port.VerifyAvailable();
                _external = _port.MultiModeEnabled;
                if (!_external && _port.Busy) { Status = "Waiting for AR/travel tasks to become idle"; return; }
                if (!_external && _port.DeferForTimedNode) { Status = "AutoRetainer deferred for timed gathering node/duty"; return; }
                var work = _external ? "external AutoRetainer activity (no ownership taken)" : _port.FindReadyWork(_port.Mode);
                if (work == null)
                {
                    _lastWork = null;
                    _repeatedWork = 0;
                    Status = "No AutoRetainer work reported ready";
                    return;
                }
                if (_port.Character is not { ContentId: not 0 } original || !_port.CharacterReady) return;
                if (!_external && !_port.CanReturnFromLoggedIn)
                    throw new InvalidOperationException("Lifestream is required for logged-in character return; AR Relog IPC supports title-screen login only");
                _original = original;
                _owner = _port.ActiveOwner;
                _mode = _port.Mode;
                _completed = _returnRequested = _yielded = _startedPass = _stopRequested = _cancelActivePass = false;
                _resume = true;
                HandoffId = Guid.NewGuid();
                SetState(AutoRetainerHandoffState.Yielding, $"Yielding for {work}");
            }

            if (_port.ActiveOwner != _owner) _resume = false;
            if (State == AutoRetainerHandoffState.Failed) return;
            // Cancellation does not depend on readiness APIs or the user's current
            // processing mode. Only the stop/status/cleanup contract is needed.
            if (State == AutoRetainerHandoffState.Cancelling)
            {
                if (_cancelActivePass) _port.CancelPass();
                if (_port.MultiModeEnabled || _port.Busy)
                {
                    if (now >= _deadline) throw new TimeoutException("Owned AutoRetainer cancellation did not settle; stop AR and recover manually");
                    return;
                }
                if (!_port.TryFinishCancellation(out var cleanupReason))
                {
                    Status = $"Stopping AutoRetainer: {cleanupReason}";
                    if (now >= _deadline) throw new TimeoutException($"Owned AutoRetainer cleanup blocked: {cleanupReason}");
                    return;
                }
                _deadline = now + TimeSpan.FromMinutes(5);
                SetState(AutoRetainerHandoffState.Returning, $"Stopping AutoRetainer: returning to {_original!.NameWithWorld}");
            }
            if (!_stopRequested) _port.VerifyAvailable();
            if (State == AutoRetainerHandoffState.Yielding)
            {
                if (_port.Character is { } current && current.ContentId != _original!.ContentId)
                    throw new InvalidOperationException("Character changed before the owned AutoRetainer pass started");
                if (!_port.TryYield(out var reason)) { Status = $"Yielding: {reason}"; return; }
                _yielded = true;
                if (_external || _port.MultiModeEnabled || _port.Busy)
                {
                    // Bell access can start AR while Vulcan is yielding. Observe/drain;
                    // never adopt ownership, start a competing pass, or abort its tasks.
                    _external = true;
                    _deadline = now + TimeSpan.FromMinutes(60);
                    SetState(AutoRetainerHandoffState.WaitingForIdle, "Waiting for external AutoRetainer activity to end (no mode changes)");
                    return;
                }
                var readyWork = _port.FindReadyWork(_mode);
                if (!_resume || !_port.Enabled || readyWork == null)
                { Finish(now); return; }
                _repeatedWork = readyWork == _lastWork && _mode == _lastMode ? _repeatedWork + 1 : 1;
                _lastWork = readyWork;
                _lastMode = _mode;
                if (_repeatedWork > 2)
                    throw new InvalidOperationException("Repeated identical AR readiness after two passes; inspect AR eligibility/resources before recovery");
                _deadline = now + TimeSpan.FromSeconds(15);
                SetState(AutoRetainerHandoffState.StartingPass, "Starting AutoRetainer single pass");
                _startedPass = true; // A throwing IPC call may already have submitted work.
                _port.StartPass(_mode);
            }
            if (State is AutoRetainerHandoffState.StartingPass or AutoRetainerHandoffState.RunningPass)
            {
                if (_completed)
                {
                    _deadline = now + TimeSpan.FromMinutes(3);
                    SetState(AutoRetainerHandoffState.WaitingForIdle, "AutoRetainer pass finished; waiting for cleanup");
                }
                else if (State == AutoRetainerHandoffState.StartingPass && _port.MultiModeEnabled)
                {
                    _deadline = now + TimeSpan.FromMinutes(60);
                    SetState(AutoRetainerHandoffState.RunningPass, "Processing AutoRetainer single pass");
                }
                else if (State == AutoRetainerHandoffState.RunningPass && !_port.MultiModeEnabled)
                    throw new InvalidOperationException("AutoRetainer stopped without single-pass completion; inspect AR then recover");
            }
            if (State == AutoRetainerHandoffState.WaitingForIdle && !_port.MultiModeEnabled && !_port.Busy)
            {
                _deadline = now + TimeSpan.FromMinutes(5);
                SetState(AutoRetainerHandoffState.Returning, $"Returning to {_original!.NameWithWorld}");
            }
            if (State == AutoRetainerHandoffState.Returning)
            {
                if (_port.Character?.ContentId == _original!.ContentId)
                {
                    _readySince = null;
                    SetState(AutoRetainerHandoffState.Settling, _stopRequested
                        ? "Stopping AutoRetainer: waiting for original character readiness"
                        : "Waiting for original character and inventory readiness");
                }
                else if (!_returnRequested && !_port.MultiModeEnabled && !_port.Busy && _port.CanRequestReturn)
                {
                    if (!_port.ReturnTo(_original)) throw new InvalidOperationException("Original-character return was rejected; return manually then recover");
                    _returnRequested = true;
                }
            }
            if (State == AutoRetainerHandoffState.Settling)
            {
                if (_port.MultiModeEnabled || _port.Character is { } current && current.ContentId != _original!.ContentId)
                    throw new InvalidOperationException("Character or AR mode changed before resume");
                if (!_port.CharacterReady || _port.Character == null || _port.Busy) _readySince = null;
                else
                {
                    _readySince ??= now;
                    if (now - _readySince.Value >= TimeSpan.FromMilliseconds(500)) { Finish(now); return; }
                }
            }
            if (State is not (AutoRetainerHandoffState.Yielding or AutoRetainerHandoffState.Monitoring) && now >= _deadline)
                throw new TimeoutException("AutoRetainer handoff timed out; retained work requires recovery");
        }
        catch (Exception ex)
        {
            if (State == AutoRetainerHandoffState.Monitoring)
            {
                var message = $"AutoRetainer unavailable: {ex.GetBaseException().Message}";
                if (Status != message) _port.Log(message);
                Status = message;
            }
            else
            {
                // Unexpected character changes must never expose an alternate inventory.
                if (_port.Character?.ContentId != _original?.ContentId) _yielded = true;
                SetState(AutoRetainerHandoffState.Failed, $"AutoRetainer recovery required: {ex.GetBaseException().Message}");
            }
        }
    }

    // Explicit recovery never assumes a stopped mode completed. User must return
    // to the original character; the owner remains manually paused afterwards.
    public bool Recover(TimeSpan now)
    {
        if (State != AutoRetainerHandoffState.Failed) return false;
        try
        {
            _port.VerifyAvailable();
            if (_port.MultiModeEnabled || _port.Busy || !_port.CharacterReady || _port.Character?.ContentId != _original?.ContentId) return false;
            if (!_yielded && !_port.TryYield(out _)) return false;
            _resume = false;
            _repeatedWork = 0;
            Finish(now);
            return true;
        }
        catch (Exception) { return false; }
    }

    private void Finish(TimeSpan now)
    {
        var stopped = _stopRequested;
        // Keep the gate closed while reconciling/releasing participants.
        _port.Release(_resume && _port.ActiveOwner == _owner);
        _yielded = false;
        _owner = null;
        _original = null;
        _startedPass = _stopRequested = _cancelActivePass = false;
        if (stopped)
        {
            _observedOwner = null;
            _ownerCharacter = null;
            _lastWork = null;
            _repeatedWork = 0;
        }
        _nextPoll = now + TimeSpan.FromSeconds(30);
        SetState(AutoRetainerHandoffState.Monitoring, stopped
            ? "AutoRetainer stopped; stopped queue will not resume"
            : "AutoRetainer handoff settled (30 second debounce)");
    }

    private void SetState(AutoRetainerHandoffState state, string status)
    {
        State = state;
        Status = status;
        _port.Log($"[AR Handoff][{HandoffId}][{_owner}] {state}: {status}");
    }
}
