using GatherBuddy.AutoGather.Lists;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class GatherInventoryDemandTests
{
    [Fact]
    public void PlayerOnlyListRemainsUnsatisfiedByRetainerStock()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(1000, useRetainerInventory: false);
        demand.Add(500, useRetainerInventory: true);

        Assert.Equal(900, demand.GetRemaining(playerInventory: 100, totalInventory: 2100));
    }

    [Fact]
    public void GatheringCanSatisfyBothPoliciesWithoutAddingDeficits()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(1000, useRetainerInventory: false);
        demand.Add(500, useRetainerInventory: true);

        Assert.Equal(900, demand.GetRemaining(playerInventory: 100, totalInventory: 100));
        Assert.Equal(0, demand.GetRemaining(playerInventory: 1000, totalInventory: 2500));
    }

    [Fact]
    public void SamePolicyListQuantitiesRemainAdditive()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(100, useRetainerInventory: false);
        demand.Add(200, useRetainerInventory: false);

        Assert.Equal(250, demand.GetRemaining(playerInventory: 50, totalInventory: 500));
    }

    [Fact]
    public void RetainerAwareRequirementUsesCurrentCombinedCount()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(500, useRetainerInventory: true);

        Assert.Equal(0, demand.GetRemaining(playerInventory: 100, totalInventory: 800));
        Assert.Equal(200, demand.GetRemaining(playerInventory: 100, totalInventory: 300));
    }

    [Fact]
    public void UnavailableRetainerInventoryMakesAllListTargetsPlayerOnly()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(1000, useRetainerInventory: false);
        demand.Add(500, useRetainerInventory: true);

        var progress = demand.GetProgress(playerInventory: 100, retainerInventory: 0,
            retainerInventoryAvailable: false);

        Assert.Equal(1400, progress.Remaining);
        Assert.Equal(1500, progress.PlayerOnlyTarget);
        Assert.Equal(0, progress.RetainerAwareTarget);
    }

    [Fact]
    public void ProgressHidesFulfilledPolicyIndependently()
    {
        var demand = new GatherInventoryDemand();
        demand.Add(1000, useRetainerInventory: false);
        demand.Add(1500, useRetainerInventory: true);

        var retainerSatisfied = demand.GetProgress(playerInventory: 126, retainerInventory: 2000,
            retainerInventoryAvailable: true);
        Assert.True(retainerSatisfied.NeedsPlayerOnlyInventory);
        Assert.False(retainerSatisfied.NeedsRetainerInventory);

        var playerSatisfied = demand.GetProgress(playerInventory: 1000, retainerInventory: 0,
            retainerInventoryAvailable: true);
        Assert.False(playerSatisfied.NeedsPlayerOnlyInventory);
        Assert.True(playerSatisfied.NeedsRetainerInventory);

        var allSatisfied = demand.GetProgress(playerInventory: 1000, retainerInventory: 500,
            retainerInventoryAvailable: true);
        Assert.True(allSatisfied.IsSatisfied);
        Assert.False(allSatisfied.NeedsPlayerOnlyInventory);
        Assert.False(allSatisfied.NeedsRetainerInventory);
    }
}
