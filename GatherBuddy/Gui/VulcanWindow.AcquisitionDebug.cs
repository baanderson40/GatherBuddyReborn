#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private int? _acquisitionPreviewListId;
    private bool _acquisitionPreviewTemporaryPolicy = true;
    private bool _acquisitionPreviewEnabled = true;
    private bool _acquisitionPreviewGil = true;
    private bool _acquisitionPreviewSeals = true;
    private bool _acquisitionPreviewSealsFirst;
    private bool _acquisitionPreviewPreferVendors = true;
    private bool _acquisitionPreviewFullDiagnostics;
    private bool _acquisitionPreviewTrace;
    private int _acquisitionPreviewGilReserve;
    private int _acquisitionPreviewSealReserve;
    private int _acquisitionPreviewGilCap = -1;
    private string? _acquisitionPreviewStatus;
    private MaterialAcquisitionPlanningInput? _acquisitionPreviewLastInput;
    private MaterialAcquisitionPlan? _acquisitionPreviewLastPlan;

    private void DrawAcquisitionPreviewDebug()
    {
        ImGui.TextUnformatted("Automatic Acquisition Preview (read only)");
        ImGui.TextWrapped("Logs to the Dalamud console (/xllog). Current inventory only; no retainer withdrawal simulated. No purchases or travel. Temporary controls are never saved.");
        var lists = GatherBuddy.CraftingListManager.Lists;
        var selected = lists.FirstOrDefault(list => list.ID == _acquisitionPreviewListId)
            ?? _editingList ?? lists.FirstOrDefault();
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(300f));
        if (ImGui.BeginCombo("Crafting list###AcquisitionPreviewList", selected?.Name ?? "No crafting lists"))
        {
            foreach (var list in lists)
                if (ImGui.Selectable($"{list.Name}##AcquisitionPreview{list.ID}", selected?.ID == list.ID))
                {
                    _acquisitionPreviewListId = list.ID;
                    _acquisitionPreviewLastInput = null;
                    _acquisitionPreviewLastPlan = null;
                    _transientTestConfirmed = false;
                    selected = list;
                }
            ImGui.EndCombo();
        }
        ImGui.Checkbox("Use temporary preview policy", ref _acquisitionPreviewTemporaryPolicy);
        if (_acquisitionPreviewTemporaryPolicy)
        {
            ImGui.Checkbox("Preview purchasing enabled", ref _acquisitionPreviewEnabled);
            ImGui.Checkbox("Allow gil", ref _acquisitionPreviewGil);
            ImGui.SameLine();
            ImGui.Checkbox("Allow current-GC seals", ref _acquisitionPreviewSeals);
            ImGui.Checkbox("Prefer GC seals before gil", ref _acquisitionPreviewSealsFirst);
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
            ImGui.InputInt("Gil reserve###AcquisitionGilReserve", ref _acquisitionPreviewGilReserve);
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
            ImGui.InputInt("Current-GC seal reserve###AcquisitionSealReserve", ref _acquisitionPreviewSealReserve);
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
            ImGui.InputInt("Gil cap (-1 = unlimited)###AcquisitionGilCap", ref _acquisitionPreviewGilCap);
            _acquisitionPreviewGilReserve = Math.Max(0, _acquisitionPreviewGilReserve);
            _acquisitionPreviewSealReserve = Math.Max(0, _acquisitionPreviewSealReserve);
            _acquisitionPreviewGilCap = Math.Max(-1, _acquisitionPreviewGilCap);
        }
        ImGui.Checkbox("Prefer purchasing over gathering (preview only)", ref _acquisitionPreviewPreferVendors);
        ImGui.Checkbox("Include full offer diagnostics", ref _acquisitionPreviewFullDiagnostics);
        ImGui.Checkbox("Include policy/planner trace", ref _acquisitionPreviewTrace);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Logs bounded P1/P2 events at Debug level in /xllog. Independent of full offer diagnostics. No settings are saved.");
        using (ImRaii.Disabled(selected == null || Dalamud.Objects.LocalPlayer == null))
            if (ImGui.Button("Log Acquisition Preview to Dalamud Console"))
                LogAcquisitionPreview(selected!);
        if (_acquisitionPreviewStatus != null) ImGui.TextWrapped(_acquisitionPreviewStatus);
        DrawTransientPurchaseDebug();
    }

    private unsafe void LogAcquisitionPreview(CraftingListDefinition list)
    {
        const string prefix = "[Vulcan Acquisition Preview]";
        var sessionId = Guid.NewGuid();
        var trace = _acquisitionPreviewTrace ? new AcquisitionTrace(sessionId) : null;
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (Dalamud.Objects.LocalPlayer == null || inventoryManager == null)
                throw new InvalidOperationException("Log in before capturing an inventory preview.");

            // Clone before planning: quality-policy resolution may populate preferences on the planning list.
            var snapshot = list.CreateRetainerPlanningSnapshot();
            var requirements = CraftingListPlanner.Build(snapshot, new CraftingListPlannerOptions(
                UseRetainerCraftableAvailability: false, ConsumeIntermediateAvailability: true, ConsumeFinalAvailability: true));
            var inventory = new Dictionary<uint, AcquisitionInventory>();
            var demands = new List<AcquisitionMaterialDemand>();
            foreach (var (itemId, quantity) in requirements.Materials)
            {
                var quality = requirements.IngredientDemands.GetValueOrDefault(itemId);
                if (quality.Total != quantity)
                    throw new InvalidOperationException($"Material {itemId} demand ({quality.Total}) differs from leaf requirement ({quantity}); preview aborted to avoid inaccurate quantities.");
                inventory[itemId] = new(
                    (int)inventoryManager->GetInventoryItemCount(itemId, false, false, false),
                    (int)inventoryManager->GetInventoryItemCount(itemId, true, false, false));
                var canGather = GatherBuddy.GameData.Gatherables.ContainsKey(itemId) || GatherBuddy.GameData.Fishes.ContainsKey(itemId);
                // Current gathering provides NQ materials; do not imply mandatory HQ can be gathered.
                demands.Add(new(itemId, quality, CanGatherNQ: canGather, CanGatherHQ: false,
                    AllowPurchasing: !canGather || _acquisitionPreviewPreferVendors));
            }

            trace?.Record("P1", "Preview", $"list={list.ID}; temporaryPolicy={_acquisitionPreviewTemporaryPolicy}; preferVendors={_acquisitionPreviewPreferVendors}; currentInventoryOnly=True; retainerWithdrawalSimulated=False");
            var policy = ResolveAcquisitionPreviewPolicy(list, trace);
            VendorShopResolver.InitializeAsync();
            if (VendorShopResolver.IsInitialized)
                VendorNpcLocationCache.InitializeAsync(VendorShopResolver.AllVendorNpcIds);
            var offerSnapshot = VendorAcquisitionOfferAdapter.Capture(requirements.Materials.Keys);
            trace?.Record("P2", "NativeData", $"status={offerSnapshot.Status}; shopsReady={VendorShopResolver.IsInitialized}; shopsLoading={VendorShopResolver.IsInitializing}; locationsReady={VendorNpcLocationCache.IsInitialized}; locationsLoading={VendorNpcLocationCache.IsInitializing}",
                offerSnapshot.Status == VendorOfferDataStatus.Unavailable ? AcquisitionTraceLevel.Warning : AcquisitionTraceLevel.Debug);
            var balances = new Dictionary<uint, ulong>();
            var currencies = offerSnapshot.Offers.Select(offer => (offer.CurrencyItemId, offer.CurrencyGroup))
                .Concat(new[] { (1u, VendorCurrencyGroup.Gil),
                    (VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId(), VendorCurrencyGroup.GrandCompanySeals) })
                .Where(currency => currency.Item1 != 0).Distinct().OrderBy(currency => currency.Item1);
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            string ItemName(uint id) => itemSheet != null && itemSheet.TryGetRow(id, out var item) ? item.Name.ExtractText() : "Item";
            Dalamud.Log.Information($"{prefix} List={list.Name.Replace('\r', ' ').Replace('\n', ' ')} [{list.ID}]; temporaryPolicy={_acquisitionPreviewTemporaryPolicy}; preferVendors={_acquisitionPreviewPreferVendors}; currentGC={VendorShopResolver.GetCurrentGrandCompanyId()}.");
            Dalamud.Log.Information($"{prefix} Cache state: shopsReady={VendorShopResolver.IsInitialized}, shopsLoading={VendorShopResolver.IsInitializing}, locationsReady={VendorNpcLocationCache.IsInitialized}, locationsLoading={VendorNpcLocationCache.IsInitializing}. Shared caches may load asynchronously; click again when ready.");
            foreach (var (currencyId, group) in currencies)
            {
                var availability = VendorCurrencyAvailabilityResolver.Resolve(group, currencyId, ItemName(currencyId));
                balances[currencyId] = availability.AvailableAmount;
                trace?.Record("P2", "BalanceSnapshot", $"currency={currencyId}; group={group}; actualBalance={availability.AvailableAmount}; source={availability.Source}");
                if (_acquisitionPreviewFullDiagnostics)
                    Dalamud.Log.Information($"{prefix} Currency balance source: id={currencyId}; group={group}; amount={availability.AvailableAmount}; source={availability.Source}.");
            }
            var input = new MaterialAcquisitionPlanningInput(sessionId, policy, demands, offerSnapshot.Offers,
                inventory, balances, new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), offerSnapshot.Status);
            var plan = MaterialAcquisitionPlanner.Plan(input, trace);
            _acquisitionPreviewLastInput = input;
            _acquisitionPreviewLastPlan = plan;
            _transientTestConfirmed = false;
            foreach (var line in MaterialAcquisitionDiagnostics.Format(input, plan, ItemName, _acquisitionPreviewFullDiagnostics))
                Dalamud.Log.Information($"{prefix} {line}");
            _acquisitionPreviewStatus = $"Logged to /xllog: {plan.Status}; {plan.Materials.Count} materials; {plan.Allocations.Count} planned allocations. Nothing purchased.";
        }
        catch (Exception ex)
        {
            _acquisitionPreviewLastInput = null;
            _acquisitionPreviewLastPlan = null;
            _transientTestConfirmed = false;
            trace?.Complete($"previewFailed=True; exceptionType={ex.GetType().Name}");
            Dalamud.Log.Error(ex, $"{prefix} Read-only preview failed.");
            _acquisitionPreviewStatus = "Preview failed; see /xllog. No purchases or navigation requested.";
        }
        finally
        {
            if (trace != null) AcquisitionTraceLogger.Write(trace, explicitPreview: true);
        }
    }

    private ResolvedVendorPurchasePolicy ResolveAcquisitionPreviewPolicy(CraftingListDefinition list, AcquisitionTrace? trace)
    {
        if (!_acquisitionPreviewTemporaryPolicy)
            return ResolvedVendorPurchasePolicy.Resolve(GatherBuddy.Config.AutomaticVendorPurchases, list.VendorPurchases, trace);
        var policy = new AutomaticVendorPurchasePolicy
        {
            Enabled = _acquisitionPreviewEnabled,
            AllowedCurrencyGroups = new(),
            CurrencyPriority = _acquisitionPreviewSealsFirst
                ? [VendorCurrencyGroup.GrandCompanySeals, VendorCurrencyGroup.Gil]
                : [VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals],
            MaximumGilSpendPerRun = _acquisitionPreviewGilCap < 0 ? null : (ulong)_acquisitionPreviewGilCap,
        };
        if (_acquisitionPreviewGil) policy.AllowedCurrencyGroups.Add(VendorCurrencyGroup.Gil);
        if (_acquisitionPreviewSeals) policy.AllowedCurrencyGroups.Add(VendorCurrencyGroup.GrandCompanySeals);
        policy.CurrencyReservesByItemId[1] = (ulong)_acquisitionPreviewGilReserve;
        var sealsId = VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId();
        if (sealsId != 0) policy.CurrencyReservesByItemId[sealsId] = (ulong)_acquisitionPreviewSealReserve;
        return ResolvedVendorPurchasePolicy.Resolve(null,
            new() { Mode = VendorPurchaseOverrideMode.Custom, CustomPolicy = policy }, trace, temporary: true);
    }
}
#endif
