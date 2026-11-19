using System;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using GatherBuddy.AutoGather.Helpers;
using NodeType = GatherBuddy.Enums.NodeType;
using Dalamud.Game.ClientState.Conditions;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private const uint FisherClassJobId = 18;

    internal Guid AutomationRunId { get; private set; } = Guid.NewGuid();
    private bool _arAutoHookStopped;
    private long _nextAutoRetainerQuitFishingAttemptAt;
    private AutoRetainerCompletionTicket? _completionTicket;
    private bool _completionHadGatherWork;
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
        var isFisher = Dalamud.Objects.LocalPlayer?.ClassJob.RowId == FisherClassJobId;
        var fisherRodOut = isFisher && Dalamud.Conditions[ConditionFlag.Gathering];
        if (IsFishing || fisherRodOut)
        {
            TryQuitFishingForAutoRetainer();
            reason = "Waiting for fishing rod to be put away";
            return false;
        }
        _nextAutoRetainerQuitFishingAttemptAt = 0;
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

    private void TryQuitFishingForAutoRetainer()
    {
        var now = Environment.TickCount64;
        if (now < _nextAutoRetainerQuitFishingAttemptAt)
            return;

        // AutoRetainerCoordinator blocks TaskManager while yielding, so a queued Quit
        // action cannot run here. Issue it directly and retry until the game clears
        // ConditionFlag.Fishing.
        _nextAutoRetainerQuitFishingAttemptAt = now + 1000;
        UseAction(Actions.Quit);
        GatherBuddy.Log.Debug("[AutoGather] Requested Quit Fishing for AutoRetainer handoff.");
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
        _nextAutoRetainerQuitFishingAttemptAt = 0;
        _autoHookSetupComplete = false;
        _currentGatherTarget = null;
        _currentRequestedTarget = null;
        ResetPendingFishingTargetChange();
        ResetFishingApproachState();
        _activeItemList.ForceRefresh();
        Waiting = false;
    }
}
