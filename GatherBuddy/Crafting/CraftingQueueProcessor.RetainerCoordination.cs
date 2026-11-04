using System;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private bool _retainerRestartAfterAr;
    private string _lastNavigationAdmission = string.Empty;
    private DateTime _nextNavigationAdmissionLog;
    public string RetainerCoordinationStatus { get; private set; } = string.Empty;

    private bool EnsureRetainerNavigationAdmission()
    {
        if (GatherBuddy.AutoRetainerIntegration is not { } integration)
            return !RetainerBellNavigator.CleanupPending && SeFunctions.TeleportData.IsReady;
        var admitted = integration.CanNavigateToRetainerBell(out var reason);
        RetainerCoordinationStatus = reason;
        if (reason != _lastNavigationAdmission && DateTime.UtcNow >= _nextNavigationAdmissionLog)
        {
            _lastNavigationAdmission = reason;
            _nextNavigationAdmissionLog = DateTime.UtcNow.AddSeconds(5);
            GatherBuddy.Log.Debug($"[RetainerBellNavigator] run={_executionPlan?.AcquisitionRunId}; admission={admitted}; {reason}");
        }
        return admitted;
    }

    private bool EnsureRetainerWithdrawalProtection()
    {
        if (RetainerBellNavigator.HasOutstanding)
        { RetainerCoordinationStatus = "Settling bell movement before withdrawal"; return false; }
        var integration = GatherBuddy.AutoRetainerIntegration;
        if (integration == null) return true;
        if (integration.Coordinator.YieldRequested)
        { RetainerCoordinationStatus = "Waiting for AutoRetainer handoff before bell access"; return false; }
        if (_executionPlan == null) return false;
        var ready = integration.Withdrawal.TryAcquire(_executionPlan.AcquisitionRunId, Environment.TickCount64);
        RetainerCoordinationStatus = integration.Withdrawal.Status;
        if (integration.Withdrawal.Faulted) Pause(RetainerCoordinationStatus);
        return ready;
    }

    private void ReleaseRetainerWithdrawalProtection()
    {
        if (_executionPlan != null)
            GatherBuddy.AutoRetainerIntegration?.Withdrawal.RequestRelease(_executionPlan.AcquisitionRunId);
    }

    private bool FinishRetainerWithdrawalProtection()
    {
        ReleaseRetainerWithdrawalProtection();
        var guard = GatherBuddy.AutoRetainerIntegration?.Withdrawal;
        if (guard?.Owner.HasValue == true)
        { RetainerCoordinationStatus = guard.Status; return false; }
        RetainerCoordinationStatus = string.Empty;
        return true;
    }

    private void SuspendRetainerStageForAutoRetainer()
    {
        _retainerRestartAfterAr = true;
        _tasks.Clear();
        _retainerBellNavigator?.Stop();
        _retainerBellNavigator = null;
        _retainerExecutor = null;
        ReleaseRetainerWithdrawalProtection();
    }

    private void RestartRetainerStageAfterAutoRetainer()
    {
        if (!_retainerRestartAfterAr) return;
        _retainerRestartAfterAr = false;
        _retainerExecutor = null;
        _tasks.Clear();
        // Planning is rebuilt behind the entry guard, after AR cleanup/character readiness.
        if (_currentState == QueueState.NavigatingToRetainerBell) QueueRetainerBellNavigationTasks();
        else QueueRetainerWithdrawalTasks();
    }
}
