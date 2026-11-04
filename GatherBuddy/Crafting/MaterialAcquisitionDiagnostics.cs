using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

/// <summary>Read-only report shared by debug UI and tests. No native reads or logging side effects.</summary>
public static class MaterialAcquisitionDiagnostics
{
    public static IReadOnlyList<string> Format(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan,
        Func<uint, string>? itemName = null, bool includeFullOfferDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        if (input.SessionId != plan.SessionId) throw new ArgumentException("Report input and plan must belong to the same session.");
        string Name(uint id) => $"{Clean(itemName?.Invoke(id) ?? "Item")} [{id}]";
        if (!includeFullOfferDiagnostics) return FormatCompact(input, plan, Name);
        var lines = new List<string>
        {
            $"BEGIN session={input.SessionId}; READ ONLY; current inventory only; no retainer withdrawal simulated.",
            "Planned quantities are not purchases. Unknown vendor access must be verified before live execution.",
            $"Policy origin={input.Policy.Origin}; enabled={input.Policy.Enabled}; allowed={string.Join(",", input.Policy.AllowedCurrencyGroups)}; priority={string.Join(" -> ", input.Policy.CurrencyPriority)}; gilCap={input.Policy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}.",
            $"Vendor data={input.VendorDataStatus}; plan={plan.Status}; materials={plan.Materials.Count}; allocations={plan.Allocations.Count}.",
        };
        foreach (var currency in input.CurrencyBalances.OrderBy(pair => pair.Key))
            lines.Add($"CURRENCY {Name(currency.Key)}: balance={currency.Value}; reserve={input.Policy.GetReserve(currency.Key)}; committed={input.Ledger.OutstandingCommitments.GetValueOrDefault(currency.Key)}; settled={input.Ledger.SettledSpending.GetValueOrDefault(currency.Key)}; plannedSpend={plan.ProjectedSpending.GetValueOrDefault(currency.Key)}.");
        foreach (var requirement in input.Materials.OrderBy(item => item.ItemId))
        {
            var owned = input.Inventory.GetValueOrDefault(requirement.ItemId);
            lines.Add($"REQUIREMENT {Name(requirement.ItemId)}: total={requirement.QualityDemand.Total}; quality={Quality(requirement.QualityDemand)}; ownedNQ={owned.NQ}; ownedHQ={owned.HQ}; gatherNQ={requirement.CanGatherNQ}; gatherHQ={requirement.CanGatherHQ}; allowPurchasing={requirement.AllowPurchasing}.");
        }
        foreach (var offer in input.Offers.OrderBy(offer => offer.ItemId).ThenBy(offer => offer.CurrencyItemId).ThenBy(offer => offer.Vendor.NpcId).ThenBy(offer => offer.Vendor.ShopId))
            lines.Add($"OFFER {Name(offer.ItemId)}: vendor={Clean(offer.Vendor.Name)} [{offer.Vendor.NpcId}]; shop={offer.Vendor.ShopId}; sourceShop={offer.Vendor.SourceShopId}; type={offer.ShopType}/{offer.Vendor.MenuShopType}; row={offer.Vendor.ShopItemIndex}; currency={Name(offer.CurrencyItemId)} ({offer.CurrencyGroup}); cost={offer.CostPerTransaction}; output={offer.OutputQuantityPerTransaction}; supported={offer.Supported}; access={offer.Access}; currencyAllowed={input.Policy.Allows(offer.CurrencyGroup)}; rejection={Clean(offer.RejectionReason ?? "none; affordability evaluated by planner")}.");
        foreach (var allocation in plan.Allocations)
            lines.Add($"ALLOCATION {Name(allocation.ItemId)}: quantity={allocation.RequestedOutputQuantity}; transactions={allocation.TransactionCount}; currency={Name(allocation.CurrencyItemId)}; expectedCost={allocation.ExpectedCurrencyCost}; allCosts={string.Join(", ", allocation.ExpectedCosts.Select(c => $"{Name(c.Key)}={c.Value}"))}; vendor={allocation.Vendor.NpcId}; shop={allocation.Vendor.ShopId}; sourceShop={allocation.Vendor.SourceShopId}; sourceRow={allocation.Vendor.SourceItemIndex}; authorizedAlternativeVendors={allocation.AuthorizedAlternatives.Count}; allocation={allocation.AllocationId}.");
        foreach (var material in plan.Materials)
        {
            lines.Add($"MATERIAL {Name(material.ItemId)}: missingAfterInventory={material.DemandAfterInventory.Total} ({Quality(material.DemandAfterInventory)}); plannedPurchase={material.PlannedPurchaseQuantity}; gatherFallback={material.GatheringDemand.Total} ({Quality(material.GatheringDemand)}); unresolved={material.UnresolvedDemand.Total} ({Quality(material.UnresolvedDemand)}).");
            foreach (var shortage in material.Shortages)
                lines.Add($"{(shortage == material.PrimaryShortage ? "SHORTAGE PRIMARY" : "SHORTAGE ADDITIONAL")} {Name(material.ItemId)}: {shortage.Reason}: {Clean(shortage.Detail)}");
            foreach (var blocker in material.EligibleOptionBlockers)
                lines.Add($"ELIGIBLE OPTION BLOCKER {Name(material.ItemId)}: {blocker.Reason}: {Clean(blocker.Detail)}");
            if (material.RejectedAlternatives.Count > 0)
                lines.Add($"REJECTED ALTERNATIVES {Name(material.ItemId)}: {string.Join(", ", material.RejectedAlternatives.Select(summary => $"{summary.Kind}={summary.OfferCount}"))}.");
        }
        if (plan.Status == AcquisitionPlanningStatus.WaitingForVendorData)
            lines.Add("WAIT: caches still loading. Click preview again after loading; no fallback or shortage conclusion yet.");
        lines.Add($"END session={input.SessionId}; no purchases, navigation, or saved-list changes requested.");
        return lines.AsReadOnly();
    }

    private static IReadOnlyList<string> FormatCompact(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan,
        Func<uint, string> name)
    {
        var waiting = plan.Status == AcquisitionPlanningStatus.WaitingForVendorData;
        var missing = plan.Materials.Where(material => material.DemandAfterInventory.Total > 0).ToList();
        var missingIds = missing.Select(material => material.ItemId).ToHashSet();
        var offers = input.Offers.Where(offer => missingIds.Contains(offer.ItemId)).ToList();
        var relevantCurrencies = offers.Where(offer => MaterialAcquisitionPlanner.GetOfferRejection(offer, input.Policy) == null
                && input.Materials.Any(material => material.ItemId == offer.ItemId && material.AllowPurchasing))
            .SelectMany(offer => offer.Costs.Select(c => c.CurrencyItemId)).Concat(plan.ProjectedSpending.Keys).ToHashSet();
        var lines = new List<string>
        {
            $"BEGIN session={input.SessionId}; READ ONLY; current inventory only; no retainer withdrawal simulated.",
            $"Policy: enabled={input.Policy.Enabled}; origin={input.Policy.Origin}; allowed={string.Join(",", input.Policy.AllowedCurrencyGroups)}; priority={string.Join(" -> ", input.Policy.CurrencyPriority)}; gilCap={input.Policy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}.",
            $"Plan={plan.Status}; missingMaterialTypes={missing.Count}; allocations={plan.Allocations.Count}. All quantities/spending below are planned, not executed.",
        };
        foreach (var id in relevantCurrencies.Order())
            lines.Add($"CURRENCY {name(id)}: balance={input.CurrencyBalances.GetValueOrDefault(id)}; reserve={input.Policy.GetReserve(id)}; committed={input.Ledger.OutstandingCommitments.GetValueOrDefault(id)}; settled={input.Ledger.SettledSpending.GetValueOrDefault(id)}; plannedSpend={plan.ProjectedSpending.GetValueOrDefault(id)}.");
        if (input.VendorDataStatus == VendorOfferDataStatus.Ready)
        {
            var rejections = offers.Select(offer => MaterialAcquisitionPlanner.GetOfferRejection(offer, input.Policy)).ToList();
            var groups = rejections.Where(kind => kind.HasValue).GroupBy(kind => kind!.Value).OrderBy(group => group.Key);
            lines.Add($"OFFERS (missing materials; before affordability/source preference): eligible={rejections.Count(kind => !kind.HasValue)}"
                + string.Concat(groups.Select(group => $"; {group.Key}={group.Count()}"))
                + ". Counts are offers, not distinct NPCs. Unknown access is not verified.");
        }
        foreach (var allocation in plan.Allocations)
        {
            var unknownAccess = offers.Any(offer => offer.ItemId == allocation.ItemId && offer.CurrencyItemId == allocation.CurrencyItemId
                && offer.Vendor == allocation.Vendor && offer.Access == VendorOfferAccess.Unknown);
            lines.Add($"BUY {name(allocation.ItemId)}: quantity={allocation.RequestedOutputQuantity}; transactions={allocation.TransactionCount}; outputPerTransaction={allocation.OutputQuantityPerTransaction}; currency={name(allocation.CurrencyItemId)}; cost={allocation.ExpectedCurrencyCost}; allCosts={string.Join(", ", allocation.ExpectedCosts.Select(c => $"{name(c.Key)}={c.Value}"))}; vendor={Clean(allocation.Vendor.Name)} [{allocation.Vendor.NpcId}]; shop={allocation.Vendor.ShopId}; access={(unknownAccess ? "not verified" : "snapshot accessible; recheck before purchase")}.");
        }
        foreach (var material in missing)
        {
            var hardQuality = material.UnresolvedDemand.HasHardRequirements || material.GatheringDemand.HasHardRequirements
                ? $"; unresolvedQuality=({Quality(material.UnresolvedDemand)}); gatheringQuality=({Quality(material.GatheringDemand)})" : "";
            lines.Add(waiting
                ? $"PENDING {name(material.ItemId)}: missing={material.DemandAfterInventory.Total}; vendor data still loading."
                : $"MATERIAL {name(material.ItemId)}: missing={material.DemandAfterInventory.Total}; purchase={material.PlannedPurchaseQuantity}; gather={material.GatheringDemand.Total}; unresolved={material.UnresolvedDemand.Total}{hardQuality}.");
            if (!waiting && material.PrimaryShortage is { } shortage)
                lines.Add($"SHORT {name(material.ItemId)}: {material.UnresolvedDemand.Total} | {shortage.Reason}: {Clean(shortage.Detail)}");
        }
        if (waiting) lines.Add("WAIT: caches still loading. Click preview again after loading; no fallback or shortage conclusion yet.");
        lines.Add($"SUMMARY: purchase={plan.Materials.Count(material => material.PlannedPurchaseQuantity > 0)} item types; gathering={plan.Materials.Count(material => material.GatheringDemand.Total > 0)} item types; unresolved={(waiting ? "pending" : plan.Materials.Count(material => material.UnresolvedDemand.Total > 0).ToString())} item types; satisfied={plan.Materials.Count(material => material.DemandAfterInventory.Total == 0)} item types hidden.");
        lines.Add($"END session={input.SessionId}; no purchases, navigation, or saved-list changes requested.");
        return lines.AsReadOnly();
    }

    private static string Quality(IngredientQualityDemand demand)
        => $"requiredHQ={demand.RequiredHQ}, requiredNQ={demand.RequiredNQ}, preferHQ={demand.PreferHQ}, preferNQ={demand.PreferNQ}";
    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
