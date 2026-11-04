using System;

namespace GatherBuddy.Automation;

public static class RetainerNavigationAdmission
{
    // Read-only: navigation must never acquire suppression or start/cancel an AR pass.
    public static bool Check(IRetainerWithdrawalPort port, bool handoff, bool cleanup, out string reason)
    {
        reason = "Waiting for AutoRetainer handoff / owned bell cleanup before navigation";
        if (handoff || cleanup) return false;
        try
        {
            reason = "Waiting for stable readiness before bell navigation";
            if (!port.Ready || port.CharacterId == 0) return false;
            if (port.Available)
            {
                reason = "Waiting for external AutoRetainer work before bell navigation";
                if (port.Busy || port.MultiMode) return false;
                reason = "Waiting for configured AutoRetainer work before bell navigation";
                if (!port.Suppressed && port.PendingWork) return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        { reason = $"Cannot verify AutoRetainer navigation admission: {ex.Message}"; return false; }
    }
}
