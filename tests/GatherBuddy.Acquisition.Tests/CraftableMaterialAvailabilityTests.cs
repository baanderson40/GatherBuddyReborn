using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class CraftableMaterialAvailabilityTests
{
    [Fact]
    public void CombinesBagAndRetainerStockForTotalDemand()
    {
        var demand = IngredientQualityDemand.FromPreferNQ(12);

        Assert.True(CraftableMaterialAvailability.CanSatisfy(demand, 5, 0, 7, 0));
        Assert.False(CraftableMaterialAvailability.CanSatisfy(demand, 5, 0, 6, 0));
    }

    [Fact]
    public void RequiredHqCannotBeSatisfiedByNqRetainerStock()
    {
        var demand = IngredientQualityDemand.FromRequiredHQ(2, 4);

        Assert.False(CraftableMaterialAvailability.CanSatisfy(demand, 2, 0, 2, 0));
        Assert.True(CraftableMaterialAvailability.CanSatisfy(demand, 2, 0, 0, 2));
    }
}
