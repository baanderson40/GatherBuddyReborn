using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan.Vendors;

public enum LiveExchangeStatus { Ready, Match, Incomplete, Unsupported, Mismatch, Ambiguous }
public sealed record LiveExchangeRow(int CallbackIndex, uint ItemId, uint OutputQuantity, bool Hq,
    IReadOnlyList<AcquisitionExchangeCost> Costs);
public sealed record LiveExchangeMatch(LiveExchangeStatus Status, int CallbackIndex, string Detail);

/// <summary>Detached transaction matching. No native pointers, sheet-price substitution, or callbacks.</summary>
public static class LiveVendorExchange
{
    public static uint? UniqueCurrencyId(IReadOnlyList<uint> candidates)
        => candidates.Count == 1 && candidates[0] != 0 ? candidates[0] : null;

    public static LiveExchangeMatch Match(VendorPurchaseAllocation allocation, IReadOnlyList<LiveExchangeRow> rows,
        int? selectedIndex = null)
    {
        var candidates = rows.Where(r => r.ItemId == allocation.ItemId).ToArray();
        if (candidates.Length == 0) return new(LiveExchangeStatus.Incomplete, -1, "RequestedItemMissing");
        var exact = new List<LiveExchangeRow>();
        var incomplete = false;
        foreach (var row in candidates)
        {
            incomplete |= row.OutputQuantity == 0 || row.Costs.Count == 0 || row.Costs.Any(c => c.CurrencyItemId == 0 || c.Amount == 0);
            if (row.CallbackIndex < 0 || row.Hq || row.OutputQuantity != allocation.OutputQuantityPerTransaction) continue;
            var costs = row.Costs.GroupBy(c => c.CurrencyItemId).ToDictionary(g => g.Key,
                g => g.Aggregate(0ul, (sum, c) => checked(sum + c.Amount)));
            if (costs.Count == allocation.Costs.Count && allocation.Costs.All(c => costs.GetValueOrDefault(c.CurrencyItemId) == c.Amount))
                exact.Add(row);
        }
        if (exact.Count > 1) return new(LiveExchangeStatus.Ambiguous, -1, "AmbiguousRows");
        // An incomplete duplicate might become another exact row: never pick an apparent sole match early.
        if (incomplete) return new(LiveExchangeStatus.Incomplete, -1, "TransactionFieldsIncomplete");
        if (exact.Count == 0) return new(LiveExchangeStatus.Mismatch, -1, "OutputOrCompleteCostsMismatch");
        var match = exact[0];
        if (selectedIndex.HasValue && selectedIndex != match.CallbackIndex)
            return new(selectedIndex < 0 ? LiveExchangeStatus.Incomplete : LiveExchangeStatus.Mismatch, -1, "SelectedRowMismatch");
        return new(LiveExchangeStatus.Match, match.CallbackIndex, "CompleteTransactionMatched");
    }
}

/// <summary>
/// Basic ShopExchangeCurrency contract from checked-out ECommons AddonMaster.ShopExchangeCurrency:
/// NumEntries=4, CurrencyId via icon=87, item IDs=1066+i, live prices=456+i, callback indices=1310+i.
/// Other shop layouts must not be inferred from this one.
/// </summary>
public static class BasicCurrencyExchangeReader
{
    public const int CountSlot = 4;
    public const int CurrencyIconSlot = 87;
    public const int ItemBase = 1066;
    public const int PriceBase = 456;
    public const int CallbackBase = 1310;
    public const int MaximumRows = CallbackBase - ItemBase;

    public static LiveExchangeStatus Read(IReadOnlyList<uint?> values, IReadOnlyList<(uint ItemId, uint Quantity)> outputs,
        uint currencyId, VendorCurrencyGroup currencyGroup, out IReadOnlyList<LiveExchangeRow> rows, out string detail)
    {
        rows = Array.Empty<LiveExchangeRow>();
        if (values.Count <= CurrencyIconSlot || values[CurrencyIconSlot] is not > 0
            || values[CountSlot] is not { } count || count == 0 || currencyId == 0)
        { detail = "BasicCurrencyHeaderIncomplete"; return LiveExchangeStatus.Incomplete; }
        if (count > MaximumRows || outputs.Count > 2048 || values.Count < CallbackBase + count)
        { detail = $"BasicCurrencyBoundsUnsupported:count={count},values={values.Count},outputs={outputs.Count}"; return LiveExchangeStatus.Unsupported; }
        var result = new List<LiveExchangeRow>();
        for (var i = 0; i < count; i++)
        {
            if (values[ItemBase + i] is not { } item)
            { detail = $"BasicItemFieldIncomplete:slot={ItemBase + i}"; return LiveExchangeStatus.Incomplete; }
            if (item == 0) continue;
            if (values[PriceBase + i] is not { } price || values[CallbackBase + i] is not { } callback)
            { detail = $"BasicPriceOrCallbackIncomplete:row={i}"; return LiveExchangeStatus.Incomplete; }
            if (callback >= outputs.Count || outputs[(int)callback].ItemId != item)
            {
                var agentItem = callback < outputs.Count ? outputs[(int)callback].ItemId : 0;
                detail = $"BasicCallbackOutputDisagrees:row={i},item={item},callback={callback},agentItem={agentItem}";
                return LiveExchangeStatus.Unsupported;
            }
            result.Add(new((int)callback, item, outputs[(int)callback].Quantity, false,
                Array.AsReadOnly(new[] { new AcquisitionExchangeCost(currencyId, currencyGroup, price) })));
        }
        rows = result.AsReadOnly();
        detail = $"reader=ECommonsBasicCurrency; rows={count}; currency={currencyId}";
        return LiveExchangeStatus.Ready; // Readable snapshot is not authorization to purchase.
    }
}
