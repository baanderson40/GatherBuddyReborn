using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Shared read-only offer projection for acquisition and Materials presentation.</summary>
internal static class IntegratedMaterialOfferAdapter
{
    internal static VendorAcquisitionOfferSnapshot Capture(IEnumerable<uint> itemIds)
    {
        var snapshot = VendorAcquisitionOfferAdapter.Capture(itemIds);
        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        return snapshot with { Offers = snapshot.Offers.Select(offer =>
        {
            uint? stack = sheet != null && sheet.TryGetRow(offer.ItemId, out var item) ? item.StackSize : null;
            return IntegratedMaterialOfferSupport.Apply(offer, stack, VendorShopResolver.HousingItemIds.Contains(offer.ItemId),
                VendorShopResolver.EquippableItemIds.Contains(offer.ItemId));
        }).ToList().AsReadOnly() };
    }
}
