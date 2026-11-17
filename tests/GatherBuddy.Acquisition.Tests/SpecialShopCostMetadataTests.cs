using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class SpecialShopCostMetadataTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AcceptsKnownItemAndSpecialBucketReferenceTypes(int costType)
        => Assert.Null(SpecialShopCostMetadata.Validate(costType, 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(255)]
    public void RejectsUnknownCostReferenceTypes(int costType)
        => Assert.Contains("Unsupported cost reference type",
            SpecialShopCostMetadata.Validate(costType, 0));

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void RejectsCollectabilityPaymentForItemCurrencyOffers(int rating)
        => Assert.Contains($"rating={rating}", SpecialShopCostMetadata.Validate(0, rating));
}
