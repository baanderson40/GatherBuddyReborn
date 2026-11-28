using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class PreCraftHomeTravelRulesTests
{
    [Theory]
    [InlineData(423)]
    [InlineData(424)]
    [InlineData(425)]
    [InlineData(653)]
    [InlineData(984)]
    public void EveryCompanyWorkshopUsesIndoorArrivalFastPath(uint territoryId)
    {
        Assert.True(PreCraftHomeTravelRules.IsCompanyWorkshop(territoryId));
        Assert.True(PreCraftHomeTravelRules.IsIndoorFastPath(false, territoryId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(132)]
    [InlineData(653 - 1)]
    [InlineData(985)]
    public void OutdoorAndOtherInteriorTerritoriesDoNotUseWorkshopFastPath(uint territoryId)
    {
        Assert.False(PreCraftHomeTravelRules.IsCompanyWorkshop(territoryId));
        Assert.False(PreCraftHomeTravelRules.IsIndoorFastPath(false, territoryId));
    }

    [Fact]
    public void NativeHousingStillUsesIndoorFastPath()
        => Assert.True(PreCraftHomeTravelRules.IsIndoorFastPath(true, 132));

    [Fact]
    public void InnIntendedUseIsSafeButPublicLobbyIsNot()
    {
        Assert.True(PreCraftHomeTravelRules.IsIndoorFastPath(false, 177,
            PreCraftHomeTravelRules.InnIntendedUseId));
        Assert.False(PreCraftHomeTravelRules.IsIndoorFastPath(false, 177, intendedUseId: 0));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void NearbyBellSkipsInitialTravelOnlyBeforeTravelSubmission(
        bool bellAvailable, bool travelSubmitted, bool expected)
        => Assert.Equal(expected,
            PreCraftHomeTravelRules.ShouldSkipInitialTravelForNearbyBell(bellAvailable, travelSubmitted));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LegacyGoHomePreferenceMigratesOnce(bool legacyValue, bool expected)
    {
        Assert.Equal(expected, PreCraftHomeTravelRules.MigratePreference(17, legacyValue, !legacyValue));
        Assert.Equal(!legacyValue, PreCraftHomeTravelRules.MigratePreference(18, legacyValue, !legacyValue));
    }
}
