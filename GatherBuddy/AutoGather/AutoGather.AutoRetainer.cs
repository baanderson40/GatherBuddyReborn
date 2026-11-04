using System;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using GatherBuddy.AutoGather.Helpers;
using NodeType = GatherBuddy.Enums.NodeType;
using Dalamud.Game.ClientState.Conditions;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    internal Guid AutomationRunId { get; private set; } = Guid.NewGuid();
    private bool _arAutoHookStopped;
    internal bool DeferAutoRetainerForTimedNode
    {
        get
        {
            // Relogging from Diadem is not supported. Do not mutate saved settings.
            if (Dalamud.ClientState.TerritoryType == Diadem.Territory.Id) return true;
            if (!GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes) return false;
            if (!_activeItemList.TryReadTimedNodeDeferral(out var nextDefer)) return true;
            if (_currentGatherTarget is { } current && current.Node is { NodeType: NodeType.Legendary or NodeType.Unspoiled } timed
                && current.Time.InRange(AdjustedServerTime) && !_activeItemList.DebugVisitedTimedLocations.ContainsKey(timed)) return true;
            return nextDefer;
        }
    }

    internal bool TryYieldToAutoRetainer(out string reason)
    {
        reason = "Gathering actions settling";
        // Drain already queued operations; do not enqueue new target/travel work.
        if (TaskManager.IsBusy) return false;
        if (_advancedUnstuck.IsRunning) { reason = "Owned unstuck movement settling"; return false; }
        if (!_arAutoHookStopped && _currentAutoHookTarget != null && AutoHook.Enabled)
        {
            AutoHook.SetAutoStartFishing?.Invoke(false);
            AutoHook.SetAutoGigState?.Invoke(false);
            AutoHook.SetPluginState?.Invoke(false);
            _arAutoHookStopped = true;
        }
        if (IsFishing)
        {
            QueueQuitFishingTasks();
            return false;
        }
        if (IsGathering)
        {
            if (Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]) return false;
            CloseGatheringAddons();
            return false;
        }
        StopNavigation();
        if (_currentAutoHookTarget != null) CleanupAutoHook();
        ResetFishingApproachState();
        reason = "Gathering yielded";
        return true;
    }

    internal void SetAutoRetainerStatus(string status)
    {
        if (!Waiting) _plugin.Ipc.AutoGatherWaiting();
        Waiting = true;
        AutoStatus = status;
    }

    internal void ReleaseFromAutoRetainer()
    {
        _arAutoHookStopped = false;
        _autoHookSetupComplete = false;
        _currentGatherTarget = null;
        _currentRequestedTarget = null;
        ResetPendingFishingTargetChange();
        ResetFishingApproachState();
        _activeItemList.ForceRefresh();
        Waiting = false;
    }
}
