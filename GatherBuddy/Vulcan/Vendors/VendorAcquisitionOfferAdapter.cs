using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GatherBuddy.Vulcan.Vendors;

public sealed record VendorAcquisitionOfferSnapshot(VendorOfferDataStatus Status, IReadOnlyList<AcquisitionVendorOffer> Offers);

/// <summary>Call on the game thread after the existing caches have been requested. Does not start travel or purchases.</summary>
public static class VendorAcquisitionOfferAdapter
{
    private static readonly Dictionary<uint, string> LastResolverDiagnostics = [];

    public static VendorAcquisitionOfferSnapshot Capture(IEnumerable<uint> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!VendorShopResolver.IsInitialized || !VendorNpcLocationCache.IsInitialized)
            return new(VendorShopResolver.IsInitializing || VendorNpcLocationCache.IsInitializing
                ? VendorOfferDataStatus.Loading : VendorOfferDataStatus.Unavailable, Array.Empty<AcquisitionVendorOffer>());

        var requested = itemIds.ToHashSet();
        var offers = new List<AcquisitionVendorOffer>();
        foreach (var itemId in requested.Order())
        {
            var gil = VendorShopResolver.GilShopEntries.Where(entry => entry.ItemId == itemId).ToArray();
            var special = VendorShopResolver.SpecialShopEntries.Where(entry => entry.ItemId == itemId).ToArray();
            var acquisition = VendorShopResolver.AcquisitionSpecialEntries.Where(entry => entry.ItemId == itemId).ToArray();
            var gc = VendorShopResolver.GcShopEntries.Where(entry => entry.ItemId == itemId).ToArray();
            var sourceDetails = string.Join(" | ", special.Concat(acquisition).SelectMany(entry => entry.Npcs.Select(npc =>
                $"{entry.Group}:{entry.CurrencyItemId};npc={npc.NpcId};menu={npc.MenuShopType};shop={npc.ShopId};source={npc.SourceShopId}:{npc.SourceItemIndex};routePage={npc.InclusionPageIndex}/{npc.InclusionSubPageIndex}"))
                .Distinct().Take(4));
            var diagnostic = $"gil={gil.Length}; special={special.Length}; acquisition={acquisition.Length}; gc={gc.Length}; specialRoutes=[{sourceDetails}]";
            if (!LastResolverDiagnostics.TryGetValue(itemId, out var previous) || previous != diagnostic)
            {
                LastResolverDiagnostics[itemId] = diagnostic;
                Dalamud.Log.Debug($"[VendorAcquisitionOfferAdapter] Resolver matches item={itemId}; {diagnostic}");
            }
        }
        var currentSeals = Dalamud.ClientState.IsLoggedIn && Dalamud.Objects.LocalPlayer != null
            ? VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() : 0;
        foreach (var entry in VendorShopResolver.GilShopEntries.Concat(VendorShopResolver.AcquisitionSpecialEntries)
                     .Concat(VendorShopResolver.GcShopEntries)
                     .Where(entry => requested.Contains(entry.ItemId)))
        {
            foreach (var vendor in entry.Npcs)
            {
                string? rejection = !VendorPurchaseManager.IsPurchaseSupported(entry, vendor) ? "Unsupported shop/menu metadata."
                    : VendorDevExclusions.IsExcluded(vendor) ? "Vendor excluded from automation." : null;
                // Resolver entries flatten exchange costs/yields. Verify the original listing before trusting its unit cost.
                IReadOnlyList<AcquisitionExchangeCost> costs = Array.AsReadOnly(new[] { new AcquisitionExchangeCost(entry.CurrencyItemId, entry.Group, entry.Cost) });
                uint output = 1;
                if (entry.ShopType == VendorShopType.SpecialCurrency)
                {
                    rejection ??= ReadExchange(entry.ItemId, vendor, out costs, out output);
                    if (costs.Count > 1)
                        rejection ??= "Multi-cost exchange metadata is modeled, but complete live verification for that native layout is unavailable.";
                }
                var supported = rejection == null;
                AcquisitionOfferRejectionKind? rejectionKind = supported ? null : AcquisitionOfferRejectionKind.Unsupported;
                var access = VendorNpcLocationCache.GetLocations(vendor.NpcId).Count == 0
                    ? VendorOfferAccess.Inaccessible : VendorOfferAccess.Unknown;
                if (entry.ShopType == VendorShopType.SpecialCurrency && !HasKnownQuestAccess(vendor))
                {
                    access = VendorOfferAccess.Inaccessible;
                    rejectionKind = AcquisitionOfferRejectionKind.VendorInaccessible;
                    rejection ??= "Required exchange quest has not been completed.";
                }
                if (costs.Any(c => c.CurrencyGroup == VendorCurrencyGroup.GrandCompanySeals
                    && !GrandCompanySealPolicy.IsCurrentCurrency(c.CurrencyItemId, currentSeals)))
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
                var primary = costs.FirstOrDefault(c => c.CurrencyItemId == entry.CurrencyItemId);
                offers.Add(new(entry.ItemId, entry.CurrencyItemId, entry.Group, primary?.Amount ?? entry.Cost, output,
                    entry.ShopType, vendor, supported, access, rejection, rejectionKind) { Costs = costs });
            }
        }
        return new(VendorOfferDataStatus.Ready, offers.AsReadOnly());
    }

    public static string? ReadExchange(uint itemId, VendorNpc vendor, out IReadOnlyList<AcquisitionExchangeCost> resolvedCosts, out uint output)
    {
        resolvedCosts = Array.Empty<AcquisitionExchangeCost>();
        output = 0;
        var index = vendor.SourceItemIndex >= 0 ? vendor.SourceItemIndex : vendor.ShopItemIndex;
        if (vendor.SourceShopId == 0 || index < 0) return "Original exchange listing metadata missing.";
        var sheet = Dalamud.GameData.GetExcelSheet<SpecialShop>();
        if (sheet == null || !sheet.TryGetRow(vendor.SourceShopId, out var shop) || index >= shop.Item.Count)
            return "Original exchange listing unavailable.";
        var listing = shop.Item[index];
        var costs = listing.ItemCosts.Where(cost => cost.ItemCost.RowId != 0 && cost.CurrencyCost != 0).ToList();
        var received = listing.ReceiveItems.Where(item => item.Item.RowId != 0).ToList();
        if (costs.Count == 0) return "Exchange has no modeled cost.";
        if (received.Count != 1) return $"Unsupported exchange output count: {received.Count}.";
        if (received[0].Item.RowId != itemId) return "Original listing output does not match resolved item.";
        if (received[0].ReceiveCount == 0) return "Exchange output quantity is zero.";
        if (received[0].ReceiveHq) return "HQ exchange output is not supported by the NQ offer adapter.";
        var result = new List<AcquisitionExchangeCost>();
        foreach (var cost in costs)
        {
            var metadataRejection = SpecialShopCostMetadata.Validate(cost.CostType, cost.CollectabilityCost);
            if (metadataRejection != null) return metadataRejection;
            var currency = VendorShopResolver.ResolveExchangeCurrency(shop.RowId, cost.ItemCost.RowId, shop.UseCurrencyType);
            var group = currency switch
            {
                VendorShopResolver.GilCurrencyItemId => VendorCurrencyGroup.Gil,
                20 or 21 or 22 => VendorCurrencyGroup.GrandCompanySeals,
                _ => VendorShopResolver.GetCurrencyGroup(VendorShopType.SpecialCurrency, currency),
            };
            var supportedCurrency = group is VendorCurrencyGroup.Gil or VendorCurrencyGroup.GrandCompanySeals
                or VendorCurrencyGroup.BicolorGemstones
                || AutomaticVendorCurrencyCatalog.Supports(group, currency);
            if (!supportedCurrency) return $"Exchange payment item/currency {currency} is not supported; crafting stock is protected.";
            result.Add(new(currency, group, (ulong)cost.CurrencyCost));
        }
        resolvedCosts = result.GroupBy(c => c.CurrencyItemId).Select(g => new AcquisitionExchangeCost(g.Key, g.First().CurrencyGroup,
            g.Aggregate(0ul, (sum, c) => checked(sum + c.Amount)))).OrderBy(c => c.CurrencyItemId).ToList().AsReadOnly();
        output = (uint)received[0].ReceiveCount;
        return null;
    }

    public static unsafe bool HasKnownQuestAccess(VendorNpc vendor)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<SpecialShop>();
        var index = vendor.SourceItemIndex >= 0 ? vendor.SourceItemIndex : vendor.ShopItemIndex;
        if (sheet == null || !sheet.TryGetRow(vendor.SourceShopId, out var shop) || index < 0 || index >= shop.Item.Count) return false;
        var quest = shop.Item[index].Quest.RowId;
        return Dalamud.ClientState.IsLoggedIn && (quest == 0 || QuestManager.IsQuestComplete(quest));
    }
}
