using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class CollectableCraftOutputTests
{
    [Fact]
    public void TacosOutputDeltaConfirmsCraftEvenWhenOrdinaryCountRemainsZero()
    {
        var before = new CollectableCraftOutputCounter(44232);
        before.Add(44233, 1, true);
        var after = new CollectableCraftOutputCounter(44232);
        after.Add(44233, 1, true);
        after.Add(44232, 1, true);
        Assert.Equal(0, before.Count);
        Assert.Equal(1, after.Count);
        Assert.Equal(1, CraftReadiness.ConfirmedCrafts(before.Count, after.Count, 1, 1));
        Assert.Equal(0, CraftReadiness.ConfirmedCrafts(0, 0, 1, 1)); // Supplied old NQ/HQ result.
    }

    [Fact]
    public void CountIncludesAllMatchingBagSlotsButNeverOrdinaryCopiesOrOtherItems()
    {
        var counter = new CollectableCraftOutputCounter(44232);
        counter.Add(44232, 1, true);
        counter.Add(44232, 1, true);
        counter.Add(44232, 5, false);
        counter.Add(44233, 10, true);
        counter.Add(0, 0, false);
        Assert.Equal(2, counter.Count);
    }

    [Theory]
    [InlineData(2, 2, 1, 1, 0)]
    [InlineData(2, 1, 1, 1, 0)]
    [InlineData(2, 3, 1, 1, 1)]
    [InlineData(2, 4, 1, 1, 1)]
    [InlineData(2, 3, 2, 2, 0)]
    [InlineData(2, 6, 2, 2, 2)]
    public void ExistingOutputNeedsNewDeltaAndConfirmationRemainsYieldAwareAndCapped(int before, int after, int yield, int requested, int expected)
        => Assert.Equal(expected, CraftReadiness.ConfirmedCrafts(before, after, yield, requested));

    [Fact]
    public void ConsecutiveCraftsRequireFreshBaselineRatherThanCountingSameOutputTwice()
    {
        var first = new CollectableCraftOutputCounter(44232);
        first.Add(44232, 1, true);
        Assert.Equal(1, CraftReadiness.ConfirmedCrafts(0, first.Count, 1, 1));
        var secondBefore = first.Count;
        Assert.Equal(0, CraftReadiness.ConfirmedCrafts(secondBefore, first.Count, 1, 1));
        first.Add(44232, 1, true);
        Assert.Equal(1, CraftReadiness.ConfirmedCrafts(secondBefore, first.Count, 1, 1));
    }

    [Fact]
    public void InvalidOrOverflowingCountsNeverWrapIntoSuccessfulOutput()
    {
        var counter = new CollectableCraftOutputCounter(44232);
        Assert.Throws<ArgumentOutOfRangeException>(() => counter.Add(44232, -1, true));
        counter.Add(44232, int.MaxValue, true);
        Assert.Throws<OverflowException>(() => counter.Add(44232, 1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectableCraftOutputCounter(0).Add(0, 1, true));
    }
}
