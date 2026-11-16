using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Per-craft capture readiness, not a second user permission. No native services.</summary>
internal sealed class AdaptiveStartupGate(TimeSpan started, TimeSpan timeout)
{
    internal Guid Ticket { get; } = Guid.NewGuid();
    internal bool CaptureReady { get; private set; }
    internal bool Closed { get; private set; }
    internal string Reason { get; private set; } = "Waiting for drained search lanes and fresh capture.";

    internal static bool IsPreSynthesisIdle(bool managedPreparing, bool crafting, bool preparingToCraft, bool executing)
        => managedPreparing && !executing && (!crafting || preparingToCraft);

    internal bool? Poll(TimeSpan now, bool selected, bool idle, bool lanesDrained, Func<bool> enableCapture)
    {
        if (Closed) return null;
        if (!selected || !idle) return Reject("Adaptive selection or pre-synthesis context changed; startup cancelled.");
        if (CaptureReady) return true;
        if (now - started >= timeout) return Reject("Adaptive startup timed out waiting for drained lanes/capture; synthesis not sent.");
        if (!lanesDrained) return false;
        // Exactly one attempt. Failure is not an invitation to reinstall a native hook.
        try
        {
            if (!enableCapture()) return Reject("Adaptive native capture unavailable; synthesis not sent.");
        }
        catch
        {
            Reject("Adaptive native capture setup threw; synthesis not sent.");
            throw;
        }
        CaptureReady = true;
        Reason = "Fresh capture ready for this selected Adaptive craft.";
        return true;
    }

    internal bool TryConsume(Guid ticket)
    {
        if (Closed || !CaptureReady || ticket != Ticket) return false;
        Closed = true;
        return true;
    }

    internal void Cancel() => Reject("Adaptive startup stopped; stale tasks cannot synthesize or attach.");

    private bool? Reject(string reason)
    {
        Closed = true;
        Reason = reason;
        return null;
    }
}
