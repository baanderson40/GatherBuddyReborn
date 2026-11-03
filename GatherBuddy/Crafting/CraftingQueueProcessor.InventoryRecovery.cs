using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private InventoryRecovery? _inventoryRecovery;
    private QueueState _inventoryContinuation;
    private TimeSpan _inventoryWaitStarted;
    private PlayerInventoryAvailability? _lastInventoryAvailability;
    private bool _inventoryRecoveryTimedOut;
    private bool _resumeGatherAfterInventory;

    internal static unsafe PlayerInventoryAvailability GetPlayerInventoryAvailability()
    {
        if (!Dalamud.ClientState.IsLoggedIn) return PlayerInventoryAvailability.Unavailable;
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
            || !GenericHelpers.IsScreenReady())
            return PlayerInventoryAvailability.Transitioning;
        return Dalamud.Objects.LocalPlayer == null || InventoryManager.Instance() == null
            ? PlayerInventoryAvailability.Unavailable : PlayerInventoryAvailability.Ready;
    }

    private void WaitForInventory(QueueState continuation)
    {
        if (_currentState == QueueState.WaitingForPlayerInventory) return;
        _inventoryRecovery?.Cancel();
        _preparationGate.Suspend(_runClock.Elapsed);
        if (continuation is QueueState.WaitingForJobSwitch or QueueState.ReadyForCraft or QueueState.WaitingForRaphaelSolution)
        {
            continuation = QueueState.WaitingForJobSwitch;
            _tasks.Clear();
        }
        _inventoryRecovery = new(_runClock.Elapsed);
        _inventoryRecoveryTimedOut = false;
        _inventoryContinuation = continuation;
        _inventoryWaitStarted = _runClock.Elapsed;
        _lastInventoryAvailability = null;
        _currentState = QueueState.WaitingForPlayerInventory;
        StateChanged?.Invoke(_currentState);
        UpdateInventoryRecovery();
    }

    private bool DeferForInventory(QueueState continuation)
    {
        if (GetPlayerInventoryAvailability() == PlayerInventoryAvailability.Ready) return false;
        WaitForInventory(continuation);
        return true;
    }

    private void UpdateInventoryRecovery()
    {
        if (_paused || _inventoryRecovery == null) return;
        var availability = GetPlayerInventoryAvailability();
        AcquisitionStatus = availability == PlayerInventoryAvailability.Transitioning
            ? "Waiting for zone transition; queue retained." : "Waiting for player inventory; queue retained.";
        if (_lastInventoryAvailability != availability)
        {
            _lastInventoryAvailability = availability;
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] {AcquisitionStatus}");
        }
        var result = _inventoryRecovery.Tick(_runClock.Elapsed, availability);
        if (result == InventoryRecoveryResult.TimedOut)
        {
            _inventoryRecoveryTimedOut = true;
            AcquisitionStatus = "Inventory recovery timed out; queue retained. Resume when the player is ready.";
            Pause(AcquisitionStatus);
            return;
        }
        if (result != InventoryRecoveryResult.Ready) return;
        _inventoryRecovery.Cancel();
        _inventoryRecovery = null;
        if (_inventoryContinuation == QueueState.WaitingForCraftResult)
            _resultDeadline += _runClock.Elapsed - _inventoryWaitStarted;
        var continuation = _inventoryContinuation;
        var plan = _executionPlan;
        _currentState = continuation;
        _readinessSelected = false;
        StateChanged?.Invoke(_currentState);
        if (_paused || _currentState != continuation || !ReferenceEquals(_executionPlan, plan)) return;
        if (_currentState == QueueState.WaitingForGather)
        {
            if (_resumeGatherAfterInventory)
            {
                _resumeGatherAfterInventory = false;
                if (!CraftingGatherBridge.IsGatheringComplete() && !CraftingGatherBridge.ResumeRetainedGathering())
                {
                    Pause("Gathering targets unavailable or gathering could not start. Queue retained; inspect gathering settings before resuming.");
                    _pausedDuringGather = true;
                    return;
                }
            }
            OnGatherComplete();
        }
        // Other stages run on the next Update: no recursive craft/equip submission.
    }

    public void OnGatherInterrupted()
    {
        if (_currentState != QueueState.WaitingForGather || _paused) return;
        // The setter does not know whether disable was manual, automatic, or zoning.
        // Reconcile retained targets; never infer completion from list cleanup.
        OnGatherComplete();
    }

    private bool ReconcileGatherHandoff()
    {
        var availability = GetPlayerInventoryAvailability();
        var satisfied = false;
        if (availability == PlayerInventoryAvailability.Ready && !CraftingGatherBridge.TryGetGatherCompletion(out satisfied))
            availability = PlayerInventoryAvailability.Unavailable;
        var decision = GatherHandoff.Evaluate(availability, CraftingGatherBridge.HasRetainedGatherTargets,
            satisfied, GatherBuddy.AutoGather.Enabled);
        switch (decision)
        {
            case GatherHandoffDecision.WaitForInventory:
                WaitForInventory(QueueState.WaitingForGather);
                return false;
            case GatherHandoffDecision.ContinueGathering:
                return false;
            case GatherHandoffDecision.PauseGathering:
                Pause("Gathering interrupted; targets remain or are unavailable. Queue retained. Resume gathering explicitly.");
                _pausedDuringGather = true;
                return false;
            default:
                return true;
        }
    }
}
