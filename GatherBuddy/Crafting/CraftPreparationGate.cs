using System;

namespace GatherBuddy.Crafting;

[Flags]
public enum CraftPreparationBlocker
{
    None = 0,
    ZoneTransition = 1,
    LoadingScreen = 2,
    PlayerInventoryUnavailable = 4,
    LifestreamBusy = 8,
    NavigationBusy = 16,
    TravelStatusUnavailable = 32,
}

public enum CraftPreparationAdmission { Blocked, Stabilizing, Ready, TimedOut, Cancelled }

/// <summary>Read-only admission: never stops travel, submits actions, or consumes equip attempts.</summary>
public sealed class CraftPreparationGate
{
    public const CraftPreparationBlocker InventoryBlockers = CraftPreparationBlocker.ZoneTransition
        | CraftPreparationBlocker.LoadingScreen | CraftPreparationBlocker.PlayerInventoryUnavailable;
    public static readonly TimeSpan Stability = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan TravelTimeout = TimeSpan.FromSeconds(120);
    private TimeSpan _last;
    private TimeSpan _travelElapsed;
    private TimeSpan? _clearSince;
    private CraftPreparationBlocker _previous;
    private bool _suspended = true;
    private bool _paused;
    private bool _cancelled;
    public bool TimedOut { get; private set; }

    public CraftPreparationAdmission Tick(TimeSpan now, CraftPreparationBlocker blockers)
    {
        if (_cancelled) return CraftPreparationAdmission.Cancelled;
        var delta = now > _last ? now - _last : TimeSpan.Zero;
        _last = now;
        if (_paused) return CraftPreparationAdmission.Blocked;
        if ((blockers & InventoryBlockers) != 0)
        {
            Suspend(now);
            return CraftPreparationAdmission.Blocked;
        }
        if (!_suspended && _previous != CraftPreparationBlocker.None) _travelElapsed += delta;
        _suspended = false;
        _previous = blockers;
        if (blockers != CraftPreparationBlocker.None)
        {
            _clearSince = null;
            if (_travelElapsed >= TravelTimeout)
            {
                TimedOut = true;
                return CraftPreparationAdmission.TimedOut;
            }
            return CraftPreparationAdmission.Blocked;
        }
        _clearSince ??= now;
        if (now - _clearSince.Value < Stability) return CraftPreparationAdmission.Stabilizing;
        _travelElapsed = TimeSpan.Zero;
        return CraftPreparationAdmission.Ready;
    }

    public void Suspend(TimeSpan now)
    {
        _last = now;
        _suspended = true;
        _clearSince = null;
        _previous = CraftPreparationBlocker.None;
    }
    public void Pause(TimeSpan now) { Suspend(now); _paused = true; }
    public void Resume(TimeSpan now)
    {
        if (TimedOut) { _travelElapsed = TimeSpan.Zero; TimedOut = false; }
        _paused = false;
        Suspend(now);
    }
    public void Cancel() => _cancelled = true;
}
