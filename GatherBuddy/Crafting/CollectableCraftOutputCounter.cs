using System;

namespace GatherBuddy.Crafting;

/// <summary>Output-only accounting. Never exposes collectables as NQ/HQ ingredients.</summary>
internal sealed class CollectableCraftOutputCounter(uint resultItemId)
{
    internal int Count { get; private set; }

    internal void Add(uint baseItemId, int quantity, bool isCollectable)
    {
        if (resultItemId == 0) throw new ArgumentOutOfRangeException(nameof(resultItemId));
        if (baseItemId != resultItemId || !isCollectable) return;
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Count = checked(Count + quantity);
    }
}
