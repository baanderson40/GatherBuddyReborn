using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

/// <summary>Game-thread adapter. Never reads Debug preview settings or creates saved shopping lists.</summary>
internal sealed class CraftingAcquisitionPort(CraftingExecutionPlan executionPlan) : ICraftingAcquisitionPort
{
    public Func<Dictionary<uint, IngredientQualityDemand>>? RemainingRequirements { get; set; }
    public void PrepareRetry() => executionPlan.AcquisitionRun.Retry(Service.Execution.LastResult);
    private TransientVendorPurchaseService Service => GatherBuddy.TransientVendorPurchases;
    public TransientVendorState PurchaseState => Service.Execution.State;
    public TransientVendorResult? PurchaseResult => Service.Execution.LastResult;
    public string PurchaseStatus => $"{Service.Execution.Status}. {GatherBuddy.VendorPurchaseManager.StatusText}";

    public unsafe MaterialAcquisitionPlanningInput? Capture(Guid session, ResolvedVendorPurchasePolicy policy, bool allowPurchasing)
    {
        var manager = InventoryManager.Instance();
        if (manager == null || Dalamud.Objects.LocalPlayer == null || !GenericHelpers.IsScreenReady()) return null;
        if (RemainingRequirements == null) executionPlan.RefreshFromCurrentInventory();
        var requirements = RemainingRequirements?.Invoke() ?? executionPlan.Materials.ToDictionary(p => p.Key, p =>
        {
            var demand = executionPlan.IngredientDemands.GetValueOrDefault(p.Key);
            if (demand.Total != p.Value) throw new InvalidOperationException($"Material {p.Key}: quality demand differs from total requirement.");
            return demand;
        });
        var inventory = new Dictionary<uint, AcquisitionInventory>();
        var demands = new List<AcquisitionMaterialDemand>();
        foreach (var (itemId, demand) in requirements)
        {
            inventory[itemId] = new(checked((int)manager->GetInventoryItemCount(itemId, false, false, false)),
                checked((int)manager->GetInventoryItemCount(itemId, true, false, false)));
            var rawItemId = AutoGather.Helpers.Diadem.ApprovedToRawItemIds.GetValueOrDefault(itemId, itemId);
            var canGather = GatherBuddy.GameData.Gatherables.ContainsKey(rawItemId) || GatherBuddy.GameData.Fishes.ContainsKey(rawItemId);
            demands.Add(new(itemId, demand, CanGatherNQ: canGather, CanGatherHQ: false, AllowPurchasing: allowPurchasing));
        }
        var snapshot = new VendorAcquisitionOfferSnapshot(VendorOfferDataStatus.Ready, Array.Empty<AcquisitionVendorOffer>());
        if (allowPurchasing && policy.Enabled)
        {
            VendorShopResolver.InitializeAsync();
            if (VendorShopResolver.IsInitialized) VendorNpcLocationCache.InitializeAsync(VendorShopResolver.AllVendorNpcIds);
            snapshot = VendorAcquisitionOfferAdapter.Capture(requirements.Keys);
            var sheet = Dalamud.GameData.GetExcelSheet<Item>();
            // Do not reserve currency for offers the transient executor cannot actually execute.
            snapshot = snapshot with { Offers = snapshot.Offers.Select(offer =>
            {
                var supported = offer.ShopType != VendorShopType.SpecialCurrency
                    && !VendorShopResolver.HousingItemIds.Contains(offer.ItemId) && !VendorShopResolver.EquippableItemIds.Contains(offer.ItemId)
                    && sheet != null && sheet.TryGetRow(offer.ItemId, out var item) && item.StackSize > 1;
                return supported ? offer : offer with { Supported = false,
                    RejectionReason = "Integrated execution supports stackable unit-output gil/GC materials only.",
                    RejectionKind = AcquisitionOfferRejectionKind.Unsupported };
            }).ToList().AsReadOnly() };
        }
        var balances = new Dictionary<uint, ulong>();
        var currentSeals = VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId();
        foreach (var currency in snapshot.Offers.Select(o => (o.CurrencyItemId, o.CurrencyGroup)).Distinct())
        {
            if (currency.CurrencyGroup == VendorCurrencyGroup.GrandCompanySeals
                && !GrandCompanySealPolicy.IsCurrentCurrency(currency.CurrencyItemId, currentSeals)) continue;
            balances[currency.CurrencyItemId] = VendorCurrencyAvailabilityResolver.Resolve(currency.CurrencyGroup, currency.CurrencyItemId, "currency").AvailableAmount;
        }
        executionPlan.AcquisitionRun.Observe(Service.Execution.LastResult);
        return new(session, policy, demands, snapshot.Offers, inventory, balances,
            new(new Dictionary<uint, ulong>(), executionPlan.AcquisitionRun.SpendingSnapshot()), snapshot.Status);
    }

    public TransientVendorStartResult StartPurchases(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan,
        Dictionary<uint, uint> inventoryTargets)
        => Service.StartForCrafting(input.SessionId, input.Policy, plan.Allocations, inventoryTargets);

    public bool CanContinue(out string reason)
    {
        reason = "Other vendor, travel, or player activity remains active; acquisition cannot advance safely.";
        if (GatherBuddy.VendorPurchaseManager.AutomationLease.Owner.HasValue || GatherBuddy.VendorPurchaseManager.IsRunning
            || GatherBuddy.VendorBuyListManager.IsBusy || GatherBuddy.VendorNavigator.IsActive
            || !VendorInteractionHelper.IsReadyToLeaveVendor() || !Dalamud.ClientState.IsLoggedIn
            || Dalamud.Conditions[ConditionFlag.InCombat] || Dalamud.Conditions[ConditionFlag.Crafting]
            || Dalamud.Conditions[ConditionFlag.Gathering] || GatherBuddy.AutoGather.Enabled || GatherBuddy.CollectableManager.IsRunning
            || Dalamud.Objects.LocalPlayer == null || !GenericHelpers.IsScreenReady()) return false;
        if (VNavmesh.Enabled && (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress())
            || Lifestream.Enabled && Lifestream.IsBusy()) return false;
        reason = "Vendor cleanup complete";
        return true;
    }
    public void Pause(Guid session) { if (Service.Execution.SessionId == session) Service.Execution.Pause(); }
    public void Resume(Guid session) { if (Service.Execution.SessionId == session) Service.Execution.Resume(); }
    public void Cancel(Guid session) { if (Service.Execution.SessionId == session) Service.Execution.Cancel(); }
}
