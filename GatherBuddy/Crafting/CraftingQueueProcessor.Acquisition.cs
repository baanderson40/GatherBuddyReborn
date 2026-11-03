using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private CraftingAcquisitionCoordinator? _acquisition;
    private CraftingAcquisitionPort? _acquisitionPort;
    public string AcquisitionStatus { get; private set; } = string.Empty;
    public IReadOnlyList<string> AcquisitionShortages { get; private set; } = Array.Empty<string>();
    public string AcquisitionPolicyDescription => _executionPlan?.PurchasePolicy.Enabled == true
        ? $"Purchase policy: {_executionPlan.PurchasePolicy.Origin}; gil cap={_executionPlan.PurchasePolicy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}; GC seal reserve={_executionPlan.PurchasePolicy.GrandCompanySealReserve}; reserves: {string.Join(", ", _executionPlan.PurchasePolicy.CurrencyReservesByItemId.Select(p => $"{p.Key}={p.Value}"))}"
        : string.Empty;
    public bool IsAcquiringMaterials => _currentState is QueueState.PlanningAcquisition or QueueState.WaitingForVendorData
        or QueueState.PurchasingMaterials or QueueState.WaitingForVendorCleanup;

    private void BeginMaterialAcquisition()
    {
        if (_executionPlan == null) return;
        _acquisitionPort ??= new(_executionPlan);
        _acquisition = new(_executionPlan.AcquisitionSessionId, _executionPlan.PurchasePolicy, _acquisitionPort, DateTime.UtcNow);
        _currentState = QueueState.PlanningAcquisition;
        AcquisitionStatus = $"Policy: {_executionPlan.PurchasePolicy.Origin}; gil purchase cap={_executionPlan.PurchasePolicy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}. Planning acquisition.";
        GatherBuddy.Log.Information($"[Vulcan Acquisition][{_executionPlan.AcquisitionSessionId}][P4][Start] run={_executionPlan.AcquisitionRunId}; list={_executionPlan.ListId}; policy={_executionPlan.PurchasePolicy.Origin}; enabled={_executionPlan.PurchasePolicy.Enabled}; currentInventoryOnly=True; retainerRestock={_executionPlan.RetainerRestock}; debugOverrides=False");
        StateChanged?.Invoke(_currentState);
    }

    private void UpdateMaterialAcquisition()
    {
        if (_acquisition == null) return;
        _acquisition.Tick(DateTime.UtcNow);
        var previous = _currentState;
        AcquisitionStatus = _acquisition.Status;
        _currentState = _acquisition.State switch
        {
            CraftingAcquisitionState.Planning => QueueState.PlanningAcquisition,
            CraftingAcquisitionState.WaitingForVendorData => QueueState.WaitingForVendorData,
            CraftingAcquisitionState.Purchasing => QueueState.PurchasingMaterials,
            CraftingAcquisitionState.WaitingForCleanup => QueueState.WaitingForVendorCleanup,
            CraftingAcquisitionState.Failed => QueueState.AcquisitionFailed,
            CraftingAcquisitionState.Cancelled => QueueState.Cancelled,
            _ => QueueState.WaitingForGather,
        };
        if (previous != _currentState)
        {
            GatherBuddy.Log.Information($"[Vulcan Acquisition][{_acquisition.SessionId}][P4][State] {_currentState}: {AcquisitionStatus}");
            StateChanged?.Invoke(_currentState);
        }
        if (_acquisition.State == CraftingAcquisitionState.ReadyForGather)
        {
            ReportAcquisitionShortages(_acquisition.FinalPlan!, onlyUnresolved: true);
            CraftingGatherBridge.CreateGatherListForMissingIngredients(_acquisition.BuildGatherTargets());
            if (_currentState == QueueState.WaitingForGather && !GatherBuddy.AutoGather.Enabled && !CraftingGatherBridge.IsGatheringComplete())
                BlockAcquisition("Gathering could not start. Check gathering dependencies and remaining materials.", QueueState.AcquisitionFailed);
        }
        else if (_currentState is QueueState.AcquisitionFailed or QueueState.Cancelled)
        {
            YesAlready.Unlock();
            Communicator.PrintError($"[Vulcan] {AcquisitionStatus}");
        }
    }

    private bool ValidateAcquiredMaterials()
    {
        // Freeze expanded intent after initial acquisition. Rebuilding from the saved
        // list after partial crafting would resurrect completed work.
        if (_acquisitionPort != null) _acquisitionPort.RemainingRequirements = BuildRemainingRequirements;
        _readinessSelected = false;
        return SelectReadyCraft();
    }

    public void RecheckAcquiredMaterials()
    {
        if (GatherBuddy.AutoRetainerIntegration?.Coordinator.YieldRequested == true) return;
        if (_currentState != QueueState.BlockedByMaterials) return;
        if (_acquisitionPort == null || !_acquisitionPort.CanContinue(out _))
        {
            AcquisitionStatus = "Stop other automation and close interactions before rechecking materials.";
            return;
        }
        _missingIngredientFailures.Clear();
        if (!ValidateAcquiredMaterials()) return;
        _paused = false;
        _pauseReason = string.Empty;
        YesAlready.Lock();
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }

    public void RetryMaterialAcquisition()
    {
        if (GatherBuddy.AutoRetainerIntegration?.Coordinator.YieldRequested == true) return;
        if (_currentState != QueueState.BlockedByMaterials || _executionPlan == null) return;
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal
            || _acquisitionPort == null || !_acquisitionPort.CanContinue(out _))
        {
            AcquisitionStatus = "Close crafting/vendor interactions and stop other automation before retrying acquisition.";
            return;
        }
        _acquisitionPort.RemainingRequirements = BuildRemainingRequirements;
        try { _acquisitionPort.PrepareRetry(); }
        catch (Exception ex)
        {
            BlockAcquisition($"Acquisition retry refused: {ex.Message}", QueueState.AcquisitionFailed);
            return;
        }
        _missingIngredientFailures.Clear();
        _readinessSelected = false;
        _paused = false;
        _pauseReason = string.Empty;
        YesAlready.Lock();
        BeginMaterialAcquisition();
    }

    private void BlockAcquisition(string reason, QueueState state = QueueState.BlockedByMaterials)
    {
        _preparationGate.Suspend(_runClock.Elapsed);
        _inventoryRecovery?.Cancel();
        _inventoryRecovery = null;
        _resumeGatherAfterInventory = false;
        AcquisitionStatus = reason;
        _currentState = state;
        _tasks.Clear();
        YesAlready.Unlock();
        CraftingGatherBridge.DeleteTemporaryGatherList();
        GatherBuddy.Log.Warning($"[Vulcan Acquisition][{_executionPlan?.AcquisitionSessionId}][P4][{state}] {reason}");
        StateChanged?.Invoke(_currentState);
    }

    private void ReportAcquisitionShortages(MaterialAcquisitionPlan plan, bool onlyUnresolved)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        var shortages = new List<string>();
        foreach (var item in plan.Materials)
        {
            var quantity = onlyUnresolved ? item.UnresolvedDemand.Total : item.DemandAfterInventory.Total;
            if (quantity <= 0) continue;
            var name = sheet != null && sheet.TryGetRow(item.ItemId, out var row) ? row.Name.ExtractText() : $"Item {item.ItemId}";
            var original = _acquisition?.PurchasePlan?.Materials.FirstOrDefault(m => m.ItemId == item.ItemId);
            var reason = original?.PrimaryShortage?.Reason.ToString() ?? _acquisition?.PurchaseFailureReason
                ?? (item.UnresolvedDemand.RequiredHQ > 0 ? "Required HQ unavailable" : "Current inventory still short after acquisition");
            var line = $"{quantity} × {name}: {reason}";
            shortages.Add(line);
            if (!AcquisitionShortages.Contains(line))
            {
                GatherBuddy.Log.Warning($"[Vulcan Acquisition][{plan.SessionId}][P4][Shortage] {line}");
                Communicator.Print($"[Vulcan] Material shortage: {line} {(onlyUnresolved ? "Continuing other material acquisition." : "Queue retained.")}");
            }
        }
        AcquisitionShortages = shortages.AsReadOnly();
    }
}
