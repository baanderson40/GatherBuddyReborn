using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

public sealed record VendorAcquisitionOfferSnapshot(VendorOfferDataStatus Status, IReadOnlyList<AcquisitionVendorOffer> Offers);

/// <summary>Call on the game thread after the existing caches have been requested. Does not start travel or purchases.</summary>
public static class VendorAcquisitionOfferAdapter
{
    public static VendorAcquisitionOfferSnapshot Capture(IEnumerable<uint> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!VendorShopResolver.IsInitialized || !VendorNpcLocationCache.IsInitialized)
            return new(VendorShopResolver.IsInitializing || VendorNpcLocationCache.IsInitializing
                ? VendorOfferDataStatus.Loading : VendorOfferDataStatus.Unavailable, Array.Empty<AcquisitionVendorOffer>());

        var requested = itemIds.ToHashSet();
        var offers = new List<AcquisitionVendorOffer>();
        var currentSeals = Dalamud.ClientState.IsLoggedIn && Dalamud.Objects.LocalPlayer != null
            ? VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() : 0;
        foreach (var entry in VendorShopResolver.GilShopEntries.Concat(VendorShopResolver.SpecialShopEntries)
                     .Concat(VendorShopResolver.GcShopEntries)
                     .Where(entry => requested.Contains(entry.ItemId)))
        {
            foreach (var vendor in entry.Npcs)
            {
                string? rejection = !VendorPurchaseManager.IsPurchaseSupported(entry, vendor) ? "Unsupported shop/menu metadata."
                    : VendorDevExclusions.IsExcluded(vendor) ? "Vendor excluded from automation." : null;
                // Resolver entries flatten exchange costs/yields. Verify the original listing before trusting its unit cost.
                if (entry.ShopType == VendorShopType.SpecialCurrency)
                    rejection ??= GetUnsupportedExchangeReason(entry, vendor);
                var supported = rejection == null;
                AcquisitionOfferRejectionKind? rejectionKind = supported ? null : AcquisitionOfferRejectionKind.Unsupported;
                var access = VendorNpcLocationCache.GetLocations(vendor.NpcId).Count == 0
                    ? VendorOfferAccess.Inaccessible : VendorOfferAccess.Unknown;
                if ((entry.ShopType == VendorShopType.GrandCompanySeals || entry.Group == VendorCurrencyGroup.GrandCompanySeals)
                    && !GrandCompanySealPolicy.IsCurrentCurrency(entry.CurrencyItemId, currentSeals))
                {
                    access = VendorOfferAccess.Inaccessible;
                    rejectionKind = AcquisitionOfferRejectionKind.WrongGrandCompany;
                    rejection ??= currentSeals == 0 ? "Current Grand Company unavailable or unaffiliated; seal purchases unavailable."
                        : "Offer belongs to a different Grand Company.";
                }
                if (access == VendorOfferAccess.Inaccessible)
                {
                    rejectionKind ??= AcquisitionOfferRejectionKind.MissingLocation;
                    rejection ??= "No cached vendor location.";
                }
                offers.Add(new(entry.ItemId, entry.CurrencyItemId, entry.Group, entry.Cost, 1,
                    entry.ShopType, vendor, supported, access, rejection, rejectionKind));
            }
        }
        return new(VendorOfferDataStatus.Ready, offers.AsReadOnly());
    }

    private static string? GetUnsupportedExchangeReason(VendorShopEntry entry, VendorNpc vendor)
    {
        if (vendor.SourceShopId == 0 || vendor.ShopItemIndex < 0) return "Original exchange listing metadata missing.";
        var sheet = Dalamud.GameData.GetExcelSheet<SpecialShop>();
        if (sheet == null || !sheet.TryGetRow(vendor.SourceShopId, out var shop) || vendor.ShopItemIndex >= shop.Item.Count)
            return "Original exchange listing unavailable.";
        var listing = shop.Item[vendor.ShopItemIndex];
        var costs = listing.ItemCosts.Where(cost => cost.ItemCost.RowId != 0 && cost.CurrencyCost != 0).ToList();
        var received = listing.ReceiveItems.Where(item => item.Item.RowId != 0).ToList();
        // Existing executor treats quantity as output units. Multi-cost, multi-output and bundled exchanges are not safe yet.
        if (costs.Count != 1) return $"Unsupported exchange cost count: {costs.Count}.";
        if (costs[0].CurrencyCost != entry.Cost) return "Original exchange cost differs from resolved unit cost.";
        if (received.Count != 1) return $"Unsupported exchange output count: {received.Count}.";
        if (received[0].Item.RowId != entry.ItemId) return "Original listing output does not match resolved item.";
        if (received[0].ReceiveCount != 1) return $"Unsupported exchange bundle size: {received[0].ReceiveCount}.";
        if (received[0].ReceiveHq) return "HQ exchange output is not supported by the NQ offer adapter.";
        return null;
    }
}
