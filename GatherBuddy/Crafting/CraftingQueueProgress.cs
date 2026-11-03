using System;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

/// <summary>Presentation only: processed non-skipped entries have confirmed output.
/// Explicit exclusions never count as successful crafts, including pending skips.</summary>
public readonly record struct CraftingQueueProgress(int TotalEntries, int ConfirmedCrafts, int ExplicitSkips, int RemainingCrafts)
{
    public int RequestedCrafts => TotalEntries - ExplicitSkips;

    public static CraftingQueueProgress Capture<T>(IReadOnlyList<T> queue, int processed, Func<T, bool> isExplicitlySkipped)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(isExplicitlySkipped);
        if (processed < 0 || processed > queue.Count) throw new ArgumentOutOfRangeException(nameof(processed));
        var confirmed = 0;
        var skipped = 0;
        var remaining = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            if (isExplicitlySkipped(queue[i])) skipped++;
            else if (i < processed) confirmed++;
            else remaining++;
        }
        return new(queue.Count, confirmed, skipped, remaining);
    }
}
