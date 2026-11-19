using System;

namespace GatherBuddy.AutoGather.Lists;

internal sealed class GatherInventoryDemand
{
    public long PlayerOnlyTarget { get; private set; }
    public long RetainerAwareTarget { get; private set; }

    public void Add(uint quantity, bool useRetainerInventory)
    {
        if (useRetainerInventory)
            RetainerAwareTarget = AddSaturated(RetainerAwareTarget, quantity);
        else
            PlayerOnlyTarget = AddSaturated(PlayerOnlyTarget, quantity);
    }

    private static long AddSaturated(long current, uint quantity)
        => current > long.MaxValue - quantity ? long.MaxValue : current + quantity;

    public int GetRemaining(int playerInventory, int totalInventory)
        => GetRemaining(playerInventory, totalInventory, retainerInventoryAvailable: true);

    public int GetRemaining(int playerInventory, int totalInventory, bool retainerInventoryAvailable)
    {
        if (!retainerInventoryAvailable)
            return (int)Math.Min(int.MaxValue,
                Math.Max(0, PlayerOnlyTarget + RetainerAwareTarget - playerInventory));

        var playerOnlyRemaining = Math.Max(0, PlayerOnlyTarget - playerInventory);
        var retainerAwareRemaining = Math.Max(0, RetainerAwareTarget - totalInventory);
        return (int)Math.Min(int.MaxValue, Math.Max(playerOnlyRemaining, retainerAwareRemaining));
    }

    public GatherInventoryDemandProgress GetProgress(int playerInventory, int retainerInventory, bool retainerInventoryAvailable)
    {
        var playerOnlyTarget = retainerInventoryAvailable ? PlayerOnlyTarget : PlayerOnlyTarget + RetainerAwareTarget;
        var retainerAwareTarget = retainerInventoryAvailable ? RetainerAwareTarget : 0;
        var totalInventory = (int)Math.Min(int.MaxValue, (long)playerInventory + retainerInventory);
        var remaining = GetRemaining(playerInventory, totalInventory, retainerInventoryAvailable);
        return new(playerOnlyTarget, retainerAwareTarget, playerInventory, totalInventory, remaining);
    }
}

internal readonly record struct GatherInventoryDemandProgress(
    long PlayerOnlyTarget,
    long RetainerAwareTarget,
    int PlayerInventory,
    int TotalInventory,
    int Remaining)
{
    public bool HasPlayerOnlyTarget => PlayerOnlyTarget > 0;
    public bool HasRetainerAwareTarget => RetainerAwareTarget > 0;
    public bool NeedsPlayerOnlyInventory => HasPlayerOnlyTarget && PlayerInventory < PlayerOnlyTarget;
    public bool NeedsRetainerInventory => HasRetainerAwareTarget && TotalInventory < RetainerAwareTarget;
    public bool IsSatisfied => Remaining == 0;
}
