using System;

namespace GatherBuddy.AutoGather.Movement;

/// <summary>Read-only mode cache. Unknown reads never overwrite the last successful observation.</summary>
internal sealed class LegacyMovementModeObserver : IDisposable
{
    public bool HasValue { get; private set; }
    public bool LegacyMode { get; private set; }
    public bool IsDisposed { get; private set; }

    internal static bool IsRelevant(string option, bool uiControl)
        => option == (uiControl ? "MoveMode" : "PadMode");

    internal bool RefreshForChange(string option, bool uiControl, Func<uint?> read)
        => IsRelevant(option, uiControl) && Refresh(read);

    internal bool Refresh(Func<uint?> read)
    {
        if (IsDisposed || read() is not { } mode) return false;
        var legacy = mode == 1;
        var changed = !HasValue || LegacyMode != legacy;
        LegacyMode = legacy;
        HasValue = true;
        return changed;
    }

    public void Dispose() => IsDisposed = true;
}
