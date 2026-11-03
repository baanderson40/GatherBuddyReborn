using System;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private bool _arAcquisitionPaused, _arParked, _arQuickSynthStop;
    private DateTime _arLastQuickStop;
    internal bool TryYieldToAutoRetainer(out string reason)
    {
        reason = $"Vulcan settling {_currentState}";
        if (!_arAcquisitionPaused)
        {
            _acquisition?.Pause(DateTime.UtcNow);
            _arAcquisitionPaused = true;
        }
        if (_tasks.Count > 0 || _currentState is QueueState.Crafting or QueueState.WaitingForCraftResult)
            return false;
        if (_currentState == QueueState.WaitingForPlayerInventory
            && _inventoryContinuation is QueueState.Crafting or QueueState.WaitingForCraftResult) return false;
        var purchases = GatherBuddy.TransientVendorPurchases.Execution;
        if (purchases.IsBusy && purchases.State != TransientVendorState.Paused) return false;
        return true;
    }

    private void UpdateAutoRetainerYield()
    {
        if (_arParked) return;
        // Let transaction/repair/withdrawal task chains settle. No state dispatcher
        // is allowed to start another craft or advance acquisition while yielding.
        ProcessTasks();
        if (_currentState == QueueState.WaitingForPlayerInventory
            && _inventoryContinuation != QueueState.WaitingForGather) UpdateInventoryRecovery();
        if (_currentState == QueueState.WaitingForCraftResult) UpdateCraftResult();
        if (_currentState == QueueState.Crafting && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
        {
            if (HandlePreparationFailure()) return;
            if (_craftHangSince == DateTime.MinValue) _craftHangSince = DateTime.Now;
            else if ((DateTime.Now - _craftHangSince).TotalSeconds > 3)
            {
                _currentState = QueueState.WaitingForJobSwitch;
                StateChanged?.Invoke(_currentState);
                Pause("Craft start was not confirmed before AutoRetainer handoff. Inspect output and resume retained work manually.");
            }
        }
        if (_currentState == QueueState.Crafting && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.QuickSynthesis
            && DateTime.UtcNow - _arLastQuickStop >= TimeSpan.FromSeconds(2))
        {
            _arQuickSynthStop = true;
            _arLastQuickStop = DateTime.UtcNow;
            CloseQuickSynthWindow();
        }
    }

    internal void ParkForAutoRetainer()
    {
        if (_arParked) return;
        _arParked = true;
        _acquisition?.Pause(DateTime.UtcNow);
        _preparationGate.Pause(_runClock.Elapsed);
        _inventoryRecovery?.Pause(_runClock.Elapsed);
        _runClock.Stop();
    }

    internal void ReleaseFromAutoRetainer(bool resume)
    {
        if (_currentState is QueueState.Cancelled or QueueState.Complete or QueueState.Idle) return;
        _runClock.Start();
        _arParked = false;
        _arQuickSynthStop = false;
        _jobSwitchGuard.Reset();
        _readinessSelected = false;
        if (_currentState is QueueState.ReadyForCraft or QueueState.WaitingForRaphaelSolution)
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
        ResetPreparationGate();
        _inventoryRecovery?.Resume(_runClock.Elapsed);
        // Preserve user's explicit pause and its acquisition suspension.
        if (!_paused && resume)
        {
            _acquisition?.Resume(DateTime.UtcNow);
            YesAlready.Lock();
        }
        _arAcquisitionPaused = false;
        if (!resume && !_paused) Pause("AutoRetainer handoff released. Inspect retained work, then Resume.");
    }
}
