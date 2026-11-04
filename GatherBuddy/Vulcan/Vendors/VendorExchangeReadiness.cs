using System;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>One bounded wait per selection attempt; polling/reason changes cannot restart its deadline.</summary>
public sealed class VendorExchangeReadiness
{
    private long? _since;
    public const long WaitMilliseconds = 3000;

    public bool ShouldWait(bool incomplete, long now)
    {
        if (!incomplete) return false;
        _since ??= now;
        return Math.Max(0, now - _since.Value) < WaitMilliseconds;
    }

    public void Reset() => _since = null;
}
