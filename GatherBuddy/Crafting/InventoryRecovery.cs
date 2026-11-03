using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GatherBuddy.Crafting;

public enum PlayerInventoryAvailability { Ready, Transitioning, Unavailable }
public enum InventoryRecoveryResult { Waiting, Ready, TimedOut, Cancelled }

/// <summary>Only observed non-transition recovery time consumes the timeout. No native side effects.</summary>
public sealed class InventoryRecovery
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private TimeSpan _last;
    private TimeSpan _nextPoll;
    private TimeSpan _elapsed;
    private PlayerInventoryAvailability _previous = PlayerInventoryAvailability.Transitioning;
    private bool _paused;
    private bool _cancelled;

    public InventoryRecovery(TimeSpan now) { _last = now; _nextPoll = now; }
    public InventoryRecoveryResult Tick(TimeSpan now, PlayerInventoryAvailability availability)
    {
        if (_cancelled) return InventoryRecoveryResult.Cancelled;
        var delta = now > _last ? now - _last : TimeSpan.Zero;
        _last = now;
        if (_paused) return InventoryRecoveryResult.Waiting;
        if (availability != PlayerInventoryAvailability.Transitioning && _previous == PlayerInventoryAvailability.Unavailable)
            _elapsed += delta;
        _previous = availability;
        if (availability == PlayerInventoryAvailability.Ready && now >= _nextPoll)
        {
            _nextPoll = now + PollInterval;
            return InventoryRecoveryResult.Ready;
        }
        if (availability != PlayerInventoryAvailability.Transitioning && _elapsed >= Timeout)
            return InventoryRecoveryResult.TimedOut;
        return InventoryRecoveryResult.Waiting;
    }
    public void Pause(TimeSpan now) { _paused = true; _last = now; }
    public void Resume(TimeSpan now) { _paused = false; _last = now; _previous = PlayerInventoryAvailability.Transitioning; }
    public void Cancel() => _cancelled = true;
}

public enum GatherHandoffDecision { WaitForInventory, ContinueGathering, PauseGathering, ReadyToCraft }
public static class GatherHandoff
{
    public static GatherHandoffDecision Evaluate(PlayerInventoryAvailability availability, bool targetsKnown,
        bool targetsSatisfied, bool gatheringEnabled)
        => availability != PlayerInventoryAvailability.Ready ? GatherHandoffDecision.WaitForInventory
            : !targetsKnown ? GatherHandoffDecision.PauseGathering
            : targetsSatisfied ? GatherHandoffDecision.ReadyToCraft
            : gatheringEnabled ? GatherHandoffDecision.ContinueGathering : GatherHandoffDecision.PauseGathering;
}

/// <summary>Independent of the temporary list's lifetime; only an explicit new stage replaces this snapshot.</summary>
public sealed class RetainedGatherTargets(IDictionary<uint, uint> targets)
    : ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>(targets))
{
    public bool SatisfiedBy(IReadOnlyDictionary<uint, AcquisitionInventory> inventory)
        => this.All(p => inventory.TryGetValue(p.Key, out var owned) && owned.NQ >= p.Value);
}
