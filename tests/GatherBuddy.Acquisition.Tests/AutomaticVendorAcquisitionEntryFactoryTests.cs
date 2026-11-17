using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AutomaticVendorAcquisitionEntryFactoryTests
{
    private static readonly VendorNpc InclusionVendor = new(144, "Anna", 3801094,
        VendorMenuShopType.InclusionShop, InclusionPageIndex: 0, InclusionSubPageIndex: 0,
        ShopItemIndex: 7, SourceShopId: 1770783, SourceItemIndex: 7);

    [Theory]
    [InlineData(33913, VendorCurrencyGroup.Scrips)]
    [InlineData(48, VendorCurrencyGroup.Tomestones)]
    public void AddsExactSingleCostCurrencyListingAndPreservesInclusionRoute(uint currency, VendorCurrencyGroup group)
    {
        Assert.True(AutomaticVendorAcquisitionEntryFactory.TryCreate(44848, "Condensed Solution", 123,
            currency, "Supported currency", group, 5, 1, false, 1, [InclusionVendor],
            1770783, 7, out var entry));

        Assert.NotNull(entry);
        Assert.Equal(44848u, entry.ItemId);
        Assert.Equal(currency, entry.CurrencyItemId);
        Assert.Equal(group, entry.Group);
        Assert.Equal(5u, entry.Cost);
        Assert.Single(entry.Npcs);
        var route = Assert.Single(entry.Npcs);
        Assert.Equal(VendorMenuShopType.InclusionShop, route.MenuShopType);
        Assert.Equal(3801094u, route.ShopId);
        Assert.Equal(1770783u, route.SourceShopId);
        Assert.Equal(7, route.SourceItemIndex);
        Assert.Equal(7, route.ShopItemIndex);
    }

    [Theory]
    [InlineData(2, false, 1, 1770783u, 7)] // Multi-cost listing.
    [InlineData(1, true, 1, 1770783u, 7)]  // HQ output.
    [InlineData(1, false, 0, 1770783u, 7)] // Zero output.
    [InlineData(1, false, 1, 0u, 7)]       // Missing source shop.
    [InlineData(1, false, 1, 1770783u, -1)] // Missing source row.
    public void RejectsUnverifiableListingShape(int costCount, bool hq, uint output, uint sourceShop, int sourceIndex)
    {
        Assert.False(AutomaticVendorAcquisitionEntryFactory.TryCreate(44848, "Condensed Solution", 123,
            33913, "Purple Crafter's Scrip", VendorCurrencyGroup.Scrips, 5, output, hq,
            costCount, [InclusionVendor], sourceShop, sourceIndex, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void RejectsUnsupportedCurrencyEvenWhenOtherMetadataLooksValid()
    {
        Assert.False(AutomaticVendorAcquisitionEntryFactory.TryCreate(44848, "Condensed Solution", 123,
            29, "MGP", VendorCurrencyGroup.MGP, 5, 1, false, 1, [InclusionVendor],
            1770783, 7, out var entry));
        Assert.Null(entry);
    }
}
