using System;

namespace GatherBuddy.Marketboard;

public static class MarketTravelConflictPolicy
{
    // AutoRetainer.IsBusy includes Lifestream.IsBusy. Once our travel owns movement,
    // aggregate busy is not evidence of independent AR activity. Multi-mode and the
    // shared handoff still have their own explicit conflict checks.
    public static bool AutoRetainerBlocks(bool ownsTravel, bool multiMode, bool aggregateBusy)
        => multiMode || !ownsTravel && aggregateBusy;
}

public enum MarketTravelState { Idle, Validating, ChangingWorld, WaitingForReadiness, GoingToMarketboard, StoppingFollowup, Complete, Failed }
public sealed record MarketTravelDestination(uint WorldId, string WorldName, string DataCenter, uint ItemId, string ItemName, bool IsHq);
public sealed record MarketTravelObservation(ulong ContentId, uint HomeWorldId, uint WorldId, bool Ready, bool Busy, bool MarketboardOpen);

public interface IMarketTravelPort
{
    void VerifyAvailable();
    MarketTravelObservation Observe();
    string? ConflictReason { get; }
    bool CanVisit(MarketTravelDestination destination);
    bool ChangeWorld(uint worldId);
    void GoToMarketboard();
    void Log(string message);
    void Notify(string message, bool error);
}

/// <summary>Observation-based travel. Never aborts Lifestream or purchases anything.</summary>
public sealed class MarketboardTravelCoordinator
{
    private readonly IMarketTravelPort _port;
    private ulong _character;
    private uint _homeWorld;
    private TimeSpan _deadline, _ackDeadline;
    private TimeSpan? _readySince;
    private bool _sawBusy;
    private bool _submitted;
    public MarketTravelState State { get; private set; }
    public MarketTravelState? FailedStage { get; private set; }
    public MarketTravelDestination? Destination { get; private set; }
    public Guid RequestId { get; private set; }
    public bool BlocksAutomation { get; private set; }
    public string Status { get; private set; } = "No marketboard travel requested.";
    public MarketTravelObservation? LastObservation { get; private set; }

    public MarketboardTravelCoordinator(IMarketTravelPort port) => _port = port;

    public bool CanStart(MarketTravelDestination destination, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (BlocksAutomation) throw new InvalidOperationException("Finish or recover the existing marketboard travel first.");
            _port.VerifyAvailable();
            var observation = _port.Observe();
            if (_port.ConflictReason is { } conflict) throw new InvalidOperationException(conflict);
            if (destination.WorldId == 0 || string.IsNullOrWhiteSpace(destination.WorldName)) throw new InvalidOperationException("Destination world is invalid.");
            if (observation.ContentId == 0 || observation.HomeWorldId == 0 || !observation.Ready)
                throw new InvalidOperationException("Log in and close blocking interactions; wait until safe to travel.");
            if (observation.Busy) throw new InvalidOperationException("Lifestream is busy; wait for its current operation.");
            if (observation.MarketboardOpen && observation.WorldId != destination.WorldId)
                throw new InvalidOperationException("Close the current marketboard before changing worlds.");
            if (!_port.CanVisit(destination))
                throw new InvalidOperationException("Lifestream cannot visit this destination from the current world/DC.");
            return true;
        }
        catch (Exception ex) { reason = ex.GetBaseException().Message; return false; }
    }

    public bool Start(MarketTravelDestination destination, TimeSpan now, out string reason)
    {
        if (!CanStart(destination, out reason)) return false;
        MarketTravelObservation observation;
        try
        {
            observation = _port.Observe();
            if (observation.ContentId == 0 || observation.HomeWorldId == 0 || !observation.Ready || observation.Busy)
                throw new InvalidOperationException("Player/travel readiness changed; confirm again when ready.");
        }
        catch (Exception ex) { reason = ex.GetBaseException().Message; return false; }
        RequestId = Guid.NewGuid();
        FailedStage = null;
        Destination = destination;
        LastObservation = observation;
        _character = observation.ContentId;
        _homeWorld = observation.HomeWorldId;
        _submitted = _sawBusy = false;
        _readySince = null;
        BlocksAutomation = true;
        SetState(MarketTravelState.Validating, "Destination validated; reserving movement");
        try
        {
            // Recheck immediately before submission. IPC calls may have side effects
            // even when they throw, so set submitted before entering them.
            if (_port.ConflictReason is { } conflict) throw new InvalidOperationException(conflict);
            if (observation.WorldId == destination.WorldId)
            {
                _deadline = now + TimeSpan.FromMinutes(5);
                SetState(MarketTravelState.WaitingForReadiness, "Waiting for destination readiness");
            }
            else
            {
                _deadline = now + TimeSpan.FromMinutes(15);
                _ackDeadline = now + TimeSpan.FromSeconds(15);
                SetState(MarketTravelState.ChangingWorld, "Changing world");
                _submitted = true;
                if (!_port.ChangeWorld(destination.WorldId))
                {
                    _submitted = false; // Verified IPC false means no request accepted.
                    throw new InvalidOperationException("Lifestream rejected the world-change request.");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex.GetBaseException().Message);
            reason = Status;
            return false;
        }
    }

    public void Tick(TimeSpan now)
    {
        if (!BlocksAutomation) return;
        try
        {
            _port.VerifyAvailable();
            var observed = LastObservation = _port.Observe();
            var sameCharacter = observed.ContentId == _character && observed.HomeWorldId == _homeWorld;
            var stable = sameCharacter && observed.Ready && !observed.Busy && _port.ConflictReason == null;
            if (stable) _readySince ??= now;
            else _readySince = null;
            if (State == MarketTravelState.Failed) return;
            if (observed.ContentId != 0 && !sameCharacter) throw new InvalidOperationException("A different character is logged in; no follow-up will be submitted.");
            if (_port.ConflictReason is { } conflict) throw new InvalidOperationException(conflict);
            if (State == MarketTravelState.StoppingFollowup)
            {
                if (IsStable(now)) Finish("Follow-up stopped; submitted Lifestream travel was not aborted.");
                else if (now >= _deadline) throw new TimeoutException("Waiting for submitted travel to settle. Stop Lifestream manually if needed, then recover.");
                return;
            }
            if (now >= _deadline) throw new TimeoutException("Travel timed out. No further commands will be submitted; inspect Lifestream and recover.");
            if (State == MarketTravelState.ChangingWorld)
            {
                _sawBusy |= observed.Busy;
                if (sameCharacter && observed.WorldId == Destination!.WorldId)
                {
                    _readySince = null;
                    SetState(MarketTravelState.WaitingForReadiness, "Waiting for destination readiness");
                    return;
                }
                if (!_sawBusy && now >= _ackDeadline)
                    throw new TimeoutException("Lifestream did not begin world travel or reach the destination.");
                return; // Logout/title-screen/loading are expected during cross-DC travel.
            }
            if (!sameCharacter || !observed.Ready) return;
            if (observed.WorldId != Destination!.WorldId)
                throw new InvalidOperationException("Current world no longer matches the requested destination.");
            if (State == MarketTravelState.WaitingForReadiness)
            {
                if (!IsStable(now)) return;
                if (observed.MarketboardOpen) { Finish("Marketboard already open on destination world."); return; }
                _submitted = true;
                _sawBusy = false;
                _readySince = null;
                _deadline = now + TimeSpan.FromMinutes(5);
                _ackDeadline = now + TimeSpan.FromSeconds(15);
                SetState(MarketTravelState.GoingToMarketboard, "Travelling to Ul’dah marketboard");
                _port.GoToMarketboard();
                return;
            }
            if (State == MarketTravelState.GoingToMarketboard)
            {
                _sawBusy |= observed.Busy;
                if (observed.MarketboardOpen && IsStable(now)) Finish("Arrived; marketboard open on requested world.");
                else if (!_sawBusy && !observed.MarketboardOpen && now >= _ackDeadline)
                    throw new TimeoutException("Lifestream marketboard shortcut did not start or open the marketboard.");
            }
        }
        catch (Exception ex) { _readySince = null; Fail(ex.GetBaseException().Message); }
    }

    public void StopFollowup(TimeSpan now)
    {
        if (!BlocksAutomation || State == MarketTravelState.StoppingFollowup) return;
        _readySince = null;
        _deadline = now + TimeSpan.FromMinutes(5);
        SetState(MarketTravelState.StoppingFollowup, "Follow-up stopped; waiting for submitted travel to settle");
    }

    public bool Recover(TimeSpan now)
    {
        if (!BlocksAutomation || State != MarketTravelState.Failed) return false;
        Tick(now);
        if (!IsStable(now)) return false;
        Finish("Travel recovered manually; no return or follow-up submitted.");
        return true;
    }

    private bool IsStable(TimeSpan now) => _readySince is { } since && now - since >= TimeSpan.FromMilliseconds(500);
    private void Finish(string message)
    {
        BlocksAutomation = false;
        SetState(MarketTravelState.Complete, message);
        _port.Notify(Status, false);
    }
    private void Fail(string message)
    {
        BlocksAutomation = _submitted;
        if (State == MarketTravelState.Failed && Status == message) return;
        if (State != MarketTravelState.Failed) FailedStage = State;
        SetState(MarketTravelState.Failed, message);
        _port.Notify(Status, true);
    }
    private void SetState(MarketTravelState state, string message)
    {
        State = state;
        Status = message;
        _port.Log($"[Marketboard Travel][{RequestId}][{Destination?.WorldName}] {state}: {message}");
    }
}
