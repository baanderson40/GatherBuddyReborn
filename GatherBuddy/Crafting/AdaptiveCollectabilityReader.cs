using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>Local game-sheet lookup only. Never reads legacy fallback stats or executes actions.</summary>
internal static class AdaptiveCollectabilityReader
{
    private static readonly Dictionary<(uint Item, int Quality), AdaptiveCollectabilityEvidence> Cache = new();

    internal static bool TryResolve(uint itemId, int maximumQuality, out AdaptiveCollectabilityEvidence? evidence, out string reason)
    {
        evidence = null;
        reason = "No supported collectability reward row for this result item.";
        if (Cache.TryGetValue((itemId, maximumQuality), out evidence)) { reason = evidence.Source; return true; }
        var candidates = new AdaptiveCollectabilityAccumulator(itemId, maximumQuality);
        void Add(string source, int low, int mid, int high)
            => candidates.Add(new(itemId, source, low, mid, high));
        try
        {
            var shop = Dalamud.GameData.GetSubrowExcelSheet<CollectablesShopItem>();
            if (shop != null)
                foreach (var group in shop)
                foreach (var row in group)
                    if (row.Item.RowId == itemId)
                    {
                        if (row.CollectablesShopRefine.RowId == 0) throw new NotSupportedException("Collectables shop refine row missing.");
                        var tiers = row.CollectablesShopRefine.Value;
                        Add($"CollectablesShopItem/{row.RowId}/{row.SubrowId}; CollectablesShopRefine/{row.CollectablesShopRefine.RowId}",
                            tiers.LowCollectability, tiers.MidCollectability, tiers.HighCollectability);
                    }
            var hwd = Dalamud.GameData.GetExcelSheet<HWDCrafterSupply>();
            if (hwd != null)
                foreach (var row in hwd)
                foreach (var entry in row.HWDCrafterSupplyParams)
                    if (entry.ItemTradeIn.RowId == itemId)
                        Add($"HWDCrafterSupply/{row.RowId}", entry.BaseCollectableRating, entry.MidCollectableRating, entry.HighCollectableRating);
            var satisfaction = Dalamud.GameData.GetSubrowExcelSheet<SatisfactionSupply>();
            if (satisfaction != null)
                foreach (var group in satisfaction)
                foreach (var row in group)
                    if (row.Item.RowId == itemId)
                        Add($"SatisfactionSupply/{row.RowId}/{row.SubrowId}", row.CollectabilityLow, row.CollectabilityMid, row.CollectabilityHigh);
            var sharlayan = Dalamud.GameData.GetExcelSheet<SharlayanCraftWorksSupply>();
            if (sharlayan != null)
                foreach (var row in sharlayan)
                foreach (var entry in row.Item)
                    if (entry.ItemId.RowId == itemId)
                        // This source declares two tiers. Map tier1/2 to its lower tier, tier3 to upper, as legacy does.
                        Add($"SharlayanCraftWorksSupply/{row.RowId} (two tiers)", entry.CollectabilityMid, entry.CollectabilityMid, entry.CollectabilityHigh);
            var banka = Dalamud.GameData.GetExcelSheet<BankaCraftWorksSupply>();
            if (banka != null)
                foreach (var row in banka)
                foreach (var entry in row.Item)
                    if (entry.ItemId.RowId == itemId)
                    {
                        if (entry.Collectability.RowId == 0) throw new NotSupportedException("Banka collectability row missing.");
                        var tiers = entry.Collectability.Value;
                        Add($"BankaCraftWorksSupply/{row.RowId}; Collectability/{entry.Collectability.RowId}",
                            tiers.CollectabilityLow, tiers.CollectabilityMid, tiers.CollectabilityHigh);
                    }
            var found = candidates.Result;
            if (found == null) return false;
            if (Cache.Count >= 128) Cache.Clear(); // Framework-owned, bounded successful metadata cache; failures can be retried on next craft.
            Cache[(itemId, maximumQuality)] = evidence = found;
            reason = found.Source;
            return true;
        }
        catch (Exception error)
        {
            evidence = null;
            reason = $"Collectability lookup rejected: {error.Message}";
            return false;
        }
    }
}
