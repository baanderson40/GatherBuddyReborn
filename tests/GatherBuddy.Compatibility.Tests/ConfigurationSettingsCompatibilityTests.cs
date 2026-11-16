using GatherBuddy.Config;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public sealed class ConfigurationSettingsCompatibilityTests
{
    [Fact]
    public void LegacySettingsJsonPreservesGatheringAndAdaptiveValues()
    {
        const string json = """
            {
              "Version": 17,
              "AutoGatherConfig": {
                "EnableSurfaceSlap": true,
                "SurfaceSlapGPThreshold": 375,
                "SurfaceSlapGPAbove": false,
                "UseCordialForFishing": true,
                "CordialForFishingGPThreshold": 620,
                "FoodItemId": 1000123,
                "MedicineItemId": 1000456
              },
              "RaphaelSolverConfig": {
                "AdaptiveCollectabilityTier": 2
              }
            }
            """;

        var config = JsonConvert.DeserializeObject<Configuration>(json)!;

        Assert.Equal(17, config.Version);
        Assert.True(config.AutoGatherConfig.EnableSurfaceSlap);
        Assert.Equal(375, config.AutoGatherConfig.SurfaceSlapGPThreshold);
        Assert.False(config.AutoGatherConfig.SurfaceSlapGPAbove);
        Assert.True(config.AutoGatherConfig.UseCordialForFishing);
        Assert.Equal(620, config.AutoGatherConfig.CordialForFishingGPThreshold);
        Assert.Equal(1000123u, config.AutoGatherConfig.FoodItemId);
        Assert.Equal(1000456u, config.AutoGatherConfig.MedicineItemId);
        Assert.Equal(2, config.RaphaelSolverConfig.AdaptiveCollectabilityTier);

        var roundTrip = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;
        Assert.Equal(2, roundTrip.RaphaelSolverConfig.AdaptiveCollectabilityTier);
        Assert.Equal(375, roundTrip.AutoGatherConfig.SurfaceSlapGPThreshold);
        Assert.Equal(620, roundTrip.AutoGatherConfig.CordialForFishingGPThreshold);
        Assert.Equal(1000123u, roundTrip.AutoGatherConfig.FoodItemId);
        Assert.Equal(1000456u, roundTrip.AutoGatherConfig.MedicineItemId);
    }

    [Fact]
    public void NewConfigurationKeepsAdaptiveCollectabilityDefaultAtTierThree()
        => Assert.Equal(3, new Configuration().RaphaelSolverConfig.AdaptiveCollectabilityTier);
}
