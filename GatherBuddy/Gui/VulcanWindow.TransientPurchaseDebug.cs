#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private Guid? _transientTestAllocationId;
    private int _transientTestQuantity = 1;
    private bool _transientTestConfirmed;
    private string? _transientTestStatus;

    private void DrawTransientPurchaseDebug()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1, 0.65f, 0.2f, 1), "LIVE transient purchase test — spends real currency");
        ImGui.TextWrapped("Separate from read-only preview. Uses its last successful policy/offer snapshot. One allocation per test; at most 5 items and 1,000 gil or 500 GC seals. Travel may cost additional gil. Saved shopping lists are not changed.");
        var execution = GatherBuddy.TransientVendorPurchases?.Execution;
        if (execution == null) return;
        ImGui.TextWrapped($"State: {execution.State}. {execution.Status}");
        if (execution.IsBusy)
        {
            if (ImGui.Button("Pause transient test")) execution.Pause();
            ImGui.SameLine();
            if (ImGui.Button("Resume transient test")) execution.Resume();
            if (ImGui.Button("STOP transient purchase test")) execution.Cancel();
            ImGui.TextWrapped("A submitted transaction may still settle. Ownership remains held until vendor cleanup completes. /xllog: [Vulcan Acquisition], P3.");
            return;
        }
        if (execution.LastResult is { } result)
        {
            ImGui.TextWrapped($"Last result: {result.Completion}; accounting certain={result.AccountingCertain}; cleanup succeeded={result.CleanupSucceeded}.");
            foreach (var item in result.Allocations)
                ImGui.TextWrapped($"Item {item.ItemId}: acquired {item.AcquiredQuantity}/{item.RequestedQuantity}; remaining {item.RemainingQuantity}; confirmed spend={item.ConfirmedSpend?.ToString() ?? "unknown"}. {item.Message}");
        }
        var plan = _acquisitionPreviewLastPlan;
        var input = _acquisitionPreviewLastInput;
        if (plan == null || input == null || plan.Status != AcquisitionPlanningStatus.Ready || plan.Allocations.Count == 0)
        {
            ImGui.TextWrapped("Run a successful preview with purchase allocations first.");
            return;
        }
        var candidates = plan.Allocations.Where(a => a.ShopType is VendorShopType.GilShop or VendorShopType.GrandCompanySeals).ToList();
        var selected = candidates.FirstOrDefault(a => a.AllocationId == _transientTestAllocationId) ?? candidates.FirstOrDefault();
        if (selected == null) { ImGui.TextWrapped("No unit-output gil/GC allocation available for the initial live test."); return; }
        ImGui.TextUnformatted($"Source preview: {plan.SessionId}");
        if (ImGui.BeginCombo("Test allocation###TransientAllocation", $"Item {selected.ItemId} / currency {selected.CurrencyItemId} / vendor {selected.Vendor.NpcId}"))
        {
            foreach (var candidate in candidates)
                if (ImGui.Selectable($"Item {candidate.ItemId}: {candidate.RequestedOutputQuantity}x, currency {candidate.CurrencyItemId}, {candidate.Vendor.Name}##{candidate.AllocationId}", candidate == selected))
                {
                    selected = candidate;
                    _transientTestAllocationId = candidate.AllocationId;
                    _transientTestConfirmed = false;
                }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(130f));
        if (ImGui.InputInt("Test quantity (1–5)", ref _transientTestQuantity)) _transientTestConfirmed = false;
        _transientTestQuantity = Math.Clamp(_transientTestQuantity, 1, (int)Math.Min(5u, selected.RequestedOutputQuantity));
        var unitCost = selected.ExpectedCurrencyCost / selected.TransactionCount;
        var maximum = checked(unitCost * (uint)_transientTestQuantity);
        var limit = selected.CurrencyItemId == 1 ? 1000ul : 500ul;
        ImGui.TextWrapped($"Captured policy: {input.Policy.Origin}; currency reserve={input.Policy.CurrencyReservesByItemId.GetValueOrDefault(selected.CurrencyItemId)}; gil purchase cap={input.Policy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}. Preview control edits do not change this snapshot; run preview again to refresh it.");
        ImGui.TextWrapped($"Authorize at most {_transientTestQuantity}x item {selected.ItemId}; maximum purchase cost {maximum} of currency {selected.CurrencyItemId}; vendor {selected.Vendor.Name} [{selected.Vendor.NpcId}], shop {selected.Vendor.ShopId}. Effective policy reserves/caps still apply.");
        ImGui.Checkbox("I authorize this real purchase and vendor travel", ref _transientTestConfirmed);
        if (maximum > limit) ImGui.TextWrapped($"Cost exceeds debug test limit ({limit}); choose a smaller/cheaper test.");
        using (ImRaii.Disabled(!_transientTestConfirmed || maximum > limit))
            if (ImGui.Button("Execute confirmed single-allocation test"))
            {
                try
                {
                    var session = Guid.NewGuid();
                    var allocation = new VendorPurchaseAllocation(session, selected.ItemId, (uint)_transientTestQuantity, 1,
                        selected.CurrencyItemId, selected.CurrencyGroup, unitCost, selected.ShopType, selected.Vendor);
                    var owned = input.Inventory.GetValueOrDefault(selected.ItemId);
                    var target = checked((uint)(owned.NQ + owned.HQ) + (uint)_transientTestQuantity);
                    var start = execution.Start(session, input.Policy, [allocation], new Dictionary<uint, uint> { [selected.ItemId] = target });
                    _transientTestStatus = $"Start result: {start}. {execution.Status}";
                    Dalamud.Log.Information($"[Vulcan Acquisition][{session}][P3][DebugAuthorization] item={selected.ItemId}; quantity={_transientTestQuantity}; currency={selected.CurrencyItemId}; maximumPurchaseSpend={maximum}; additionalTravelGilPossible=True; result={start}");
                }
                catch (Exception ex)
                {
                    Dalamud.Log.Error(ex, "[Vulcan Acquisition][P3] Debug transient test could not start");
                    _transientTestStatus = ex.Message;
                }
                _transientTestConfirmed = false;
            }
        if (_transientTestStatus != null) ImGui.TextWrapped(_transientTestStatus);
    }
}
#endif
