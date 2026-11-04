using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Native adapter for transient execution. Does not create, select, or edit vendor buy lists.</summary>
public sealed class TransientVendorPurchaseService : ITransientVendorPort, IDisposable
{
    private readonly VendorPurchaseManager _manager;
    private VendorPurchaseAllocation? _active;
    private VendorPurchaseManager.PurchaseResult? _receipt;
    private int _inventoryBefore;
    private DateTime _requestStarted;
    private DateTime _resultTime;
    private DateTime _lastClose;
    private bool _disposed;
    private Guid _loggedSession;
    private int _loggedEvents;
    private bool _loggedSummary;
    private Guid? _craftingSession;
    public TransientVendorExecution Execution { get; }

    public TransientVendorPurchaseService(VendorPurchaseManager manager)
    {
        _manager = manager;
        Execution = new(this);
        _manager.PurchaseFinished += OnPurchaseFinished;
    }

    public TransientVendorStartResult StartForCrafting(Guid session, ResolvedVendorPurchasePolicy policy,
        IEnumerable<VendorPurchaseAllocation> allocations, IDictionary<uint, uint> inventoryTargets)
    {
        if (Execution.IsBusy) return TransientVendorStartResult.Busy;
        if (!CraftingGatherBridge.IsAcquiringMaterialsFor(session)) return TransientVendorStartResult.Unavailable;
        _craftingSession = session;
        var start = Execution.Start(session, policy, allocations, inventoryTargets);
        if (start != TransientVendorStartResult.Started) _craftingSession = null;
        return start;
    }

    public void Update()
    {
        if (_disposed) return;
        Execution.Tick(DateTime.UtcNow);
        if (Execution.Trace is not { } trace) return;
        if (_loggedSession != trace.OperationId)
        {
            _loggedSession = trace.OperationId;
            _loggedEvents = 0;
            _loggedSummary = false;
        }
        foreach (var entry in trace.Events.Skip(_loggedEvents))
            Dalamud.Log.Debug(trace.Format(entry));
        _loggedEvents = trace.Events.Count;
        if (Execution.State == TransientVendorState.Finished && !_loggedSummary)
        {
            Dalamud.Log.Information(trace.Summary);
            _loggedSummary = true;
        }
    }

    public bool TryAcquire(Guid sessionId, out string reason)
    {
        if (GatherBuddy.MarketboardTravel?.Coordinator.BlocksAutomation == true)
        { reason = "Finish/recover Marketboard travel first."; return false; }
        if (_disposed) { reason = "Transient service disposed"; return false; }
        if (!IsSafe(out reason)) return false;
        if (Dalamud.Objects.LocalPlayer == null || !GenericHelpers.IsScreenReady())
        {
            reason = "Wait for the player and game screen to be ready before acquiring vendor ownership.";
            return false;
        }
        if (_manager.AutomationLease.Owner.HasValue || _manager.IsRunning || GatherBuddy.VendorBuyListManager.IsBusy
            || GatherBuddy.VendorNavigator.IsActive || !VendorInteractionHelper.IsReadyToLeaveVendor())
        {
            reason = "Another vendor operation or interaction is active; close/stop it first.";
            return false;
        }
        if (!VendorAutomationRequirements.IsAvailable || !VNavmesh.Enabled)
        {
            reason = "Vendor automation dependencies unavailable (inventory integration and vnavmesh required).";
            return false;
        }
        try
        {
            if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress()
                || Lifestream.Enabled && Lifestream.IsBusy())
            {
                reason = "Other movement/travel is active.";
                return false;
            }
        }
        catch (Exception ex) { reason = $"Could not verify movement ownership: {ex.Message}"; return false; }
        if (!_manager.AutomationLease.TryAcquire(sessionId)) { reason = "Vendor ownership busy"; return false; }
        _receipt = null;
        _active = null;
        reason = "Exclusive transient ownership acquired";
        return true;
    }

    public bool IsSafe(out string reason)
    {
        reason = "Ready";
        if (!Dalamud.ClientState.IsLoggedIn || Dalamud.Conditions[ConditionFlag.InCombat]
            || Dalamud.Conditions[ConditionFlag.Crafting] || Dalamud.Conditions[ConditionFlag.Gathering])
        {
            reason = "Player logged out, in combat, crafting, or gathering; transient purchasing cannot continue.";
            return false;
        }
        var owningCraftRun = _craftingSession is { } session && CraftingGatherBridge.IsAcquiringMaterialsFor(session);
        if (GatherBuddy.AutoGather?.Enabled == true || GatherBuddy.CollectableManager?.IsRunning == true
            || (CraftingGatherBridge.GetActiveExecutionPlan() != null || _craftingSession.HasValue) && !owningCraftRun)
        {
            reason = "Another gathering/crafting/collectable run is active.";
            return false;
        }
        return true;
    }

    public uint ReadItemCount(uint itemId) => (uint)Math.Max(0, ItemHelper.GetInventoryAndArmoryItemCount(itemId));

    public unsafe TransientPurchaseStart TryStart(Guid sessionId, VendorPurchaseAllocation allocation, uint quantity,
        uint inventoryTarget, TransientPurchaseBudget budget, out string reason)
    {
        if (_manager.AutomationLease.Owner != sessionId || !IsSafe(out reason))
        {
            reason = "Lost ownership or unsafe player state";
            return TransientPurchaseStart.Unsafe;
        }
        if (_manager.IsRunning || _active != null) { reason = "Previous purchase has not settled"; return TransientPurchaseStart.Unsafe; }
        if (allocation.OutputQuantityPerTransaction != 1 || allocation.ShopType == VendorShopType.SpecialCurrency)
        {
            reason = "Transient live execution currently supports unit-output gil and GC shops only; special exchanges remain preview-only.";
            return TransientPurchaseStart.Skipped;
        }
        if (VendorShopResolver.HousingItemIds.Contains(allocation.ItemId) || VendorShopResolver.EquippableItemIds.Contains(allocation.ItemId))
        {
            reason = "Initial transient executor supports stackable crafting materials only.";
            return TransientPurchaseStart.Skipped;
        }
        var offerSnapshot = VendorAcquisitionOfferAdapter.Capture([allocation.ItemId]);
        var unitCost = allocation.ExpectedCurrencyCost / allocation.TransactionCount;
        var offer = offerSnapshot.Offers.FirstOrDefault(candidate => candidate.Supported && candidate.Access != VendorOfferAccess.Inaccessible
            && candidate.ItemId == allocation.ItemId && candidate.CurrencyItemId == allocation.CurrencyItemId
            && candidate.CurrencyGroup == allocation.CurrencyGroup && candidate.ShopType == allocation.ShopType
            && candidate.CostPerTransaction == unitCost && candidate.Vendor == allocation.Vendor);
        if (offer == null || offerSnapshot.Status != VendorOfferDataStatus.Ready || unitCost > uint.MaxValue)
        {
            reason = "Exact authorized offer unavailable, changed, or vendor data not ready. No substitution performed.";
            return TransientPurchaseStart.Skipped;
        }
        var player = Dalamud.Objects.LocalPlayer!;
        var location = VendorNpcLocationCache.GetLocations(allocation.Vendor.NpcId)
            .OrderBy(place => place.TerritoryId != Dalamud.ClientState.TerritoryType)
            .ThenBy(place => place.TerritoryId).ThenBy(place => Vector3.DistanceSquared(player.Position, place.Position)).FirstOrDefault();
        if (location == null) { reason = "Vendor location unavailable"; return TransientPurchaseStart.Skipped; }
        var inventory = InventoryManager.Instance();
        if (inventory == null || inventory->GetEmptySlotsInBag() == 0)
        {
            reason = "No free inventory slot; conservative capacity check prevents purchasing.";
            return TransientPurchaseStart.Skipped;
        }
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(allocation.ItemId, out var item) || item.StackSize <= 1)
        {
            reason = "Item is not a supported stackable material";
            return TransientPurchaseStart.Skipped;
        }
        if (allocation.ShopType == VendorShopType.GrandCompanySeals)
        {
            var state = PlayerState.Instance();
            if (state == null || !GrandCompanySealPolicy.IsCurrentCurrency(allocation.CurrencyItemId, VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId()))
            {
                reason = "Wrong Grand Company or player data unavailable";
                return TransientPurchaseStart.Skipped;
            }
        }
        var availability = VendorCurrencyAvailabilityResolver.Resolve(allocation.CurrencyGroup, allocation.CurrencyItemId, "authorized currency");
        if (budget.Spendable(availability.AvailableAmount, 0) < unitCost)
        {
            reason = "Live balance/reserve/other commitments/run cap permit no purchase";
            return TransientPurchaseStart.Skipped;
        }
        var entry = new VendorShopEntry(allocation.ItemId, item.Name.ExtractText(), (ushort)item.Icon, (uint)unitCost,
            allocation.CurrencyItemId, "authorized currency", [allocation.Vendor], allocation.ShopType, allocation.CurrencyGroup);
        _inventoryBefore = (int)ReadItemCount(allocation.ItemId);
        _requestStarted = DateTime.UtcNow;
        _receipt = null;
        _active = allocation;
        _manager.StartPurchase(entry, allocation.Vendor, location, Math.Min(item.StackSize, Math.Min(99u, quantity)), purchaseConstraints: new()
        {
            TransientBudget = budget,
            TargetInventoryQuantity = inventoryTarget,
        }, ownerSessionId: sessionId, allocationId: allocation.AllocationId);
        if (!_manager.IsRunning)
        {
            _active = null;
            reason = "Existing purchase manager rejected the request";
            return TransientPurchaseStart.Skipped;
        }
        reason = $"Purchasing {quantity} of {entry.ItemName} using currency {entry.CurrencyItemId}";
        return TransientPurchaseStart.Started;
    }

    public TransientPurchaseReceipt? Poll(Guid sessionId)
    {
        if (_manager.AutomationLease.Owner != sessionId || _active == null) return null;
        // Zoning can temporarily remove LocalPlayer and inventory access. Do not reconcile that as a purchase loss.
        if (Dalamud.ClientState.IsLoggedIn && (Dalamud.Objects.LocalPlayer == null || !GenericHelpers.IsScreenReady())) return null;
        if (_receipt == null && (DateTime.UtcNow - _requestStarted).TotalMinutes >= 3)
            _manager.Stop(sessionId);
        if (_receipt == null || (DateTime.UtcNow - _resultTime).TotalSeconds < 1) return null;
        var receipt = _receipt;
        var count = ReadItemCount(_active.ItemId);
        var delta = count > (uint)_inventoryBefore ? count - (uint)_inventoryBefore : 0;
        var certain = !receipt.AccountingUncertain && delta == receipt.CompletedQuantity;
        var result = new TransientPurchaseReceipt(sessionId, _active.AllocationId, certain ? receipt.CompletedQuantity : delta,
            certain ? receipt.ConfirmedCurrencySpent : null, receipt.State == VendorPurchaseManager.CompletionState.Cancelled,
            !certain, receipt.Message);
        _active = null;
        _receipt = null;
        return result;
    }

    private void OnPurchaseFinished(VendorPurchaseManager.PurchaseResult result)
    {
        if (_active == null || result.SessionId != _manager.AutomationLease.Owner || result.AllocationId != _active.AllocationId) return;
        _receipt = result;
        _resultTime = DateTime.UtcNow;
    }

    public void Pause(Guid sessionId)
    {
        // Do not abandon a submitted transaction. One owned request is at most one batch.
        if (_manager.AutomationLease.Owner == sessionId && !_manager.HasSubmittedBatch) _manager.Stop(sessionId);
    }
    public void Cancel(Guid sessionId)
    {
        if (_manager.AutomationLease.Owner != sessionId) return;
        _manager.Stop(sessionId);
        GatherBuddy.VendorNavigator.Stop(sessionId);
    }
    public bool TryCleanup(Guid sessionId)
    {
        if (_manager.AutomationLease.Owner != sessionId) return false;
        if (_manager.IsRunning || _active != null || Dalamud.Objects.LocalPlayer == null) return false;
        if ((DateTime.UtcNow - _resultTime).TotalSeconds < 2) return false;
        if (!VendorInteractionHelper.IsReadyToLeaveVendor())
        {
            if ((DateTime.UtcNow - _lastClose).TotalMilliseconds >= 300)
            {
                VendorInteractionHelper.TryExitVendorInteraction();
                _lastClose = DateTime.UtcNow;
            }
            return false;
        }
        GatherBuddy.VendorNavigator.Stop(sessionId);
        return true;
    }
    public void Release(Guid sessionId)
    {
        if (_active != null || _manager.IsRunning || !VendorInteractionHelper.IsReadyToLeaveVendor())
            throw new InvalidOperationException("Cannot release transient ownership before purchase and interaction cleanup.");
        _manager.AutomationLease.Release(sessionId);
        _craftingSession = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (Execution.IsBusy)
        {
            Execution.Cancel();
            VendorInteractionHelper.TryExitVendorInteraction();
            Dalamud.Log.Warning("[Vulcan Acquisition][P3][Dispose] Transient execution cancelled on unload. Submitted transactions may need manual reconciliation; no further work will start.");
        }
        _disposed = true;
        _manager.PurchaseFinished -= OnPurchaseFinished;
    }
}
