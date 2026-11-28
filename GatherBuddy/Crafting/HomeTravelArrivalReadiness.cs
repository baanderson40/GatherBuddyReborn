using System;

namespace GatherBuddy.Crafting;

/// <summary>Arrival heuristic, not destination proof. Busy/readiness loss always resets the idle window.</summary>
public sealed class HomeTravelArrivalReadiness
{
    public static readonly TimeSpan OutdoorStability = TimeSpan.FromSeconds(2);
    private TimeSpan? _clearSince;

    public bool Observe(TimeSpan now, bool busy, bool playerReady, bool insideFastPath)
    {
        if (busy || !playerReady) { Reset(); return false; }
        _clearSince ??= now;
        return insideFastPath || now - _clearSince.Value >= OutdoorStability;
    }

    public void Reset() => _clearSince = null;
}
