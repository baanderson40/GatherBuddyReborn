using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Projects only source-backed, single-cost scrip/tomestone listings into acquisition offers.</summary>
public static class AutomaticVendorAcquisitionEntryFactory
{
    public static bool TryCreate(uint itemId, string itemName, ushort iconId, uint currencyItemId,
        string currencyName, VendorCurrencyGroup currencyGroup, uint cost, uint outputQuantity, bool outputHq,
        int sourceCostCount, IReadOnlyList<VendorNpc> routes, uint sourceShopId, int sourceItemIndex,
        out VendorShopEntry? entry)
        => TryCreate(itemId, itemName, iconId, currencyItemId, currencyName, currencyGroup, cost, outputQuantity,
            outputHq, sourceCostCount, routes, sourceShopId, sourceItemIndex, out entry, out _);

    public static bool TryCreate(uint itemId, string itemName, ushort iconId, uint currencyItemId,
        string currencyName, VendorCurrencyGroup currencyGroup, uint cost, uint outputQuantity, bool outputHq,
        int sourceCostCount, IReadOnlyList<VendorNpc> routes, uint sourceShopId, int sourceItemIndex,
        out VendorShopEntry? entry, out string rejectionReason)
    {
        entry = null;
        rejectionReason = itemId == 0 ? "MissingOutputItem"
            : string.IsNullOrWhiteSpace(itemName) ? "MissingOutputName"
            : string.IsNullOrWhiteSpace(currencyName) ? "MissingCurrencyName"
            : cost == 0 ? "ZeroCost"
            : outputQuantity == 0 ? "ZeroOutput"
            : outputHq ? "HqOutputUnsupported"
            : sourceCostCount != 1 ? $"CostComponentCount={sourceCostCount}"
            : sourceShopId == 0 ? "MissingSourceShop"
            : sourceItemIndex < 0 ? "MissingSourceRow"
            : routes.Count == 0 ? "NoVendorRoutes"
            : !AutomaticVendorCurrencyCatalog.Supports(currencyGroup, currencyItemId) ? "CurrencyNotCatalogued"
            : string.Empty;
        if (rejectionReason.Length > 0) return false;

        var routedNpcs = routes.Select(npc => npc with
        {
            ShopItemIndex = sourceItemIndex,
            SourceShopId = sourceShopId,
            SourceItemIndex = sourceItemIndex,
        }).ToList();
        entry = new(itemId, itemName, iconId, cost, currencyItemId, currencyName, routedNpcs,
            VendorShopType.SpecialCurrency, currencyGroup);
        return true;
    }
}
