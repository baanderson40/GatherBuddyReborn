using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;

namespace GatherBuddy.AutoGather.Extensions;

/// <summary>
/// Extension methods for the IGatherable interface.
/// </summary>
public static class GatherableExtensions
{
    private static readonly uint[] _retainerInventoryTypes =
        [
            (uint)InventoryType.RetainerCrystals,
            (uint)InventoryType.RetainerPage1,
            (uint)InventoryType.RetainerPage2,
            (uint)InventoryType.RetainerPage3,
            (uint)InventoryType.RetainerPage4,
            (uint)InventoryType.RetainerPage5,
            (uint)InventoryType.RetainerPage6,
            (uint)InventoryType.RetainerPage7
        ];

    /// <summary>
    /// Gets the inventory count for a gatherable item.
    /// </summary>
    /// <param name="gatherable">The gatherable item to check.</param>
    /// <param name="checkRetainers">Check retainer inventory.</param>
    /// <returns>The count of the item in the inventory.</returns>
    public static unsafe int GetInventoryCount(this IGatherable gatherable)
    {
        var inventory = InventoryManager.Instance();
        var count = inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 0);

        if (gatherable.ItemData.IsCollectable)
        {
            count += inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 1);
        }

        return count;
    }

    public static int GetTotalCount(this IGatherable gatherable)
        => gatherable.GetTotalCount(true);

    public static bool CanReadRetainerInventory
    {
        get
        {
            if (!GatherBuddy.Config.AutoGatherConfig.CheckRetainers || !AllaganTools.Enabled)
                return false;

            try { return AllaganTools.IsInitialized(); }
            catch { return false; }
        }
    }

    public static int GetRetainerInventoryCount(this IGatherable gatherable)
    {
        if (!CanReadRetainerInventory)
            return 0;

        try
        {
            var count = AllaganTools.ItemCountOwned(gatherable.ItemId, true, _retainerInventoryTypes);
            return (int)Math.Min(count, (uint)int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    public static int GetTotalCount(this IGatherable gatherable, bool useRetainerInventory)
    {
        var localCount = gatherable.GetInventoryCount();
        if (!useRetainerInventory)
            return localCount;

        return (int)Math.Min((long)localCount + gatherable.GetRetainerInventoryCount(), int.MaxValue);
    }
}
