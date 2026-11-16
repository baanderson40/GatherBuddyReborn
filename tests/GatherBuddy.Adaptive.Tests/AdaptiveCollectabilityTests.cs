using System.Text.Json;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveCollectabilityTests
{
    private static AdaptiveCollectabilityEvidence Evidence => new(44232,
        "CollectablesShopItem/30/27; CollectablesShopRefine/187", 660, 900, 1140);

    private static AdaptiveCraftSnapshot Tacos => AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with
    {
        RecipeId = 35829, CraftCollectible = true, CraftHQ = false, CraftQualityMax = 12000,
        StatCraftsmanship = 5953, StatControl = 5490, StatCP = 664,
        CraftQualityMin1 = 6600, CraftQualityMin2 = 9000, CraftQualityMin3 = 11400,
        CraftProgress = 6600, CraftDurability = 80, CraftLevel = 100,
        CraftProgressDivider = 170, CraftQualityDivider = 150,
        CraftProgressModifier = 90, CraftQualityModifier = 75,
    }, collectabilityEvidence: Evidence);

    [Fact]
    public void FrozenTacosRewardRowConvertsRatingsNotPercentages()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "collectable-tacos.json")));
        var row = data.RootElement;
        var evidence = new AdaptiveCollectabilityEvidence(row.GetProperty("ItemId").GetUInt32(),
            row.GetProperty("Source").GetString()!, row.GetProperty("LowRating").GetInt32(),
            row.GetProperty("MidRating").GetInt32(), row.GetProperty("HighRating").GetInt32());
        Assert.Equal(Evidence, evidence);
        Assert.Equal((6600, 9000, 11400), evidence.QualityThresholds(row.GetProperty("MaximumQuality").GetInt32()));
        Assert.Equal(35829u, Tacos.RecipeId);
        Assert.Equal(evidence, Tacos.CollectabilityEvidence);
        Assert.Equal(6600, Tacos.ToSimulatorCraft().CraftQualityMin1);
        Assert.Equal(11400, Tacos.ToSimulatorCraft().CraftQualityMin3);
    }

    [Theory]
    [InlineData(1, 6600)]
    [InlineData(2, 9000)]
    [InlineData(3, 11400)]
    public void AdaptiveUsesSelectedTierWithMandatoryFirstRewardFloor(int tier, int desired)
    {
        var goal = new AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode.Adaptive, 100, tier).Resolve(Tacos);
        Assert.Equal(AdaptiveGoalKind.Collectability, goal.Kind);
        Assert.Equal(6600, goal.MandatoryQuality);
        Assert.Equal(desired, goal.DesiredQuality);
        Assert.False(goal.StrictTarget);
        Assert.Equal(desired, new AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode.Standard, 100, tier).Resolve(Tacos).DesiredQuality);
        Assert.Equal(11400, new AdaptiveLiveGoalPolicy(AdaptiveLivePolicyMode.Raphael, 100, tier).Resolve(Tacos).DesiredQuality);
        Assert.Equal(9500, AdaptiveCraftGoal.Resolve(Tacos with { RequiredQuality = 9500 }, AdaptiveGoalKind.Collectability, 1).MandatoryQuality);
    }

    [Theory]
    [InlineData(0, 900, 1140)]
    [InlineData(-1, 900, 1140)]
    [InlineData(901, 900, 1140)]
    [InlineData(660, 1141, 1140)]
    [InlineData(660, 900, 1201)]
    [InlineData(660, 900, int.MaxValue)]
    public void InvalidMatchedMetadataRejectsWithoutClamping(int low, int mid, int high)
        => Assert.Throws<ArgumentException>(() => new AdaptiveCollectabilityEvidence(44232, "fixture", low, mid, high).QualityThresholds(12000));

    [Fact]
    public void ProvenanceIsRequiredBoundedAndMustMatchScalarThresholds()
    {
        Assert.Throws<ArgumentException>(() => (Evidence with { Source = "" }).QualityThresholds(12000));
        Assert.Throws<ArgumentException>(() => (Evidence with { Source = new string('x', 257) }).QualityThresholds(12000));
        Assert.Throws<ArgumentException>(() => (Evidence with { ItemId = 0 }).QualityThresholds(12000));
        Assert.Throws<ArgumentException>(() => (Tacos with { Collectability1 = 660 }).Validate());
        Assert.Throws<ArgumentException>(() => (Tacos with { Collectable = false }).Validate());
        Assert.NotEqual(Tacos, Tacos with { CollectabilityEvidence = Evidence with { Source = "different row" } });
        Assert.NotEqual(Tacos, Tacos with { CollectabilityEvidence = Evidence with { ItemId = 44233 } });
        Assert.Equal(Tacos, JsonSerializer.Deserialize<AdaptiveCraftSnapshot>(JsonSerializer.Serialize(Tacos)));
    }

    [Fact]
    public void MatchingRowsAcceptDuplicatesButRejectConflictsWrongItemAndUnboundedMatches()
    {
        var rows = new AdaptiveCollectabilityAccumulator(44232, 12000);
        Assert.Null(rows.Result);
        rows.Add(Evidence);
        rows.Add(Evidence with { Source = "duplicate context" });
        Assert.Equal(Evidence, rows.Result);
        Assert.Throws<NotSupportedException>(() => rows.Add(Evidence with { LowRating = 661 }));
        Assert.Throws<ArgumentException>(() => rows.Add(Evidence with { ItemId = 1 }));
        rows = new(44232, 12000);
        for (var i = 0; i < 16; ++i) rows.Add(Evidence);
        Assert.Throws<NotSupportedException>(() => rows.Add(Evidence));
    }

    [Fact]
    public void TwoTierSourceNormalizesLowerTierToFirstAndSecondSlots()
    {
        var evidence = new AdaptiveCollectabilityEvidence(1, "SharlayanCraftWorksSupply/fixture (two tiers)", 500, 500, 900);
        Assert.Equal((5000, 5000, 9000), evidence.QualityThresholds(10000));
        var craft = Tacos with { CollectabilityEvidence = evidence, Collectability1 = 5000, Collectability2 = 5000, Collectability3 = 9000 };
        Assert.Equal(5000, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 2).DesiredQuality);
        Assert.Equal(9000, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 3).DesiredQuality);
    }

    [Theory]
    [InlineData(6599, false, false)]
    [InlineData(6600, false, true)]
    [InlineData(9000, false, true)]
    [InlineData(11400, false, true)]
    [InlineData(9000, true, false)]
    [InlineData(11400, true, true)]
    public void SeedReplayNeverAcceptsCompletionBelowMandatoryOrStrictFloor(int quality, bool strict, bool accepted)
    {
        var craft = Tacos with { RequiredProgress = 100 };
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 3, strict);
        var simulation = new AdaptiveSimulation(craft, goal);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, Quality = quality };
        Assert.Equal(accepted, AdaptivePlanValidator.TryValidate(simulation, initial, new[] { VulcanSkill.BasicSynthesis }, out var plan));
        if (accepted) Assert.Equal(quality >= 11400, plan!.TargetReached);
    }

    [Fact]
    public void BoundedUnseededTacosSearchProducesIndependentlyReplayedRewardCompletion()
    {
        var simulation = new AdaptiveSimulation(Tacos, AdaptiveCraftGoal.Resolve(Tacos, AdaptiveGoalKind.Collectability, 3));
        var initial = AdaptiveCoreTests.Initial() with { CP = 664 };
        var result = new AdaptiveSearchEngine().Search(simulation, initial,
            new AdaptiveSearchLimits { TimeLimit = null });
        Assert.NotNull(result.Plan);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, result.Plan!.Actions, out var replay));
        Assert.True(replay!.FinalState.Quality >= 6600);
        Assert.True(result.Diagnostics.Transitions <= 40000);
    }

    [Fact]
    public void SelectedTierAndMetadataDriftInvalidatePreparationContext()
    {
        var craft = Tacos;
        var context = new AdaptiveSeedPreparationContext(1, 15, craft,
            AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 3), "Adaptive", "fixture", 0);
        var changes = new[]
        {
            context with { Goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 2) },
            context with { Craft = craft with { CollectabilityEvidence = Evidence with { Source = "changed provenance" } } },
            context with { Craft = craft with { Collectability1 = 6610, CollectabilityEvidence = Evidence with { LowRating = 661 } } },
        };
        foreach (var changed in changes)
        {
            var preparation = new AdaptiveSeedPreparation(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            var requests = 0;
            Assert.False(preparation.Poll(context, TimeSpan.Zero, _ => new(AdaptiveSeedLookupState.Missing), _ => ++requests));
            Assert.True(preparation.Poll(changed, TimeSpan.FromMilliseconds(1), _ => throw new Exception("Stale lookup"), _ => ++requests));
            Assert.Equal(1, requests);
            Assert.Equal("Unseeded", preparation.Status.Phase);
            Assert.Null(preparation.Consume(changed));
        }
    }

    [Fact]
    public void RejectedStartExportNamesAttemptedRecipeAndCannotContainPreviousCraftRoots()
    {
        var ticket = Guid.NewGuid();
        var view = new AdaptivePreparationView(35829, ticket, "Collectability lookup rejected",
            new("Cancelled", "Preparation revoked"));
        using var json = JsonDocument.Parse(AdaptiveLiveExport.SerializePreparation(view));
        var root = json.RootElement;
        Assert.Equal(35829u, root.GetProperty("AttemptedRecipeId").GetUInt32());
        Assert.Equal(ticket, root.GetProperty("PreparationTicket").GetGuid());
        Assert.Equal("BeforeSynthesis", root.GetProperty("Phase").GetString());
        Assert.False(root.GetProperty("StartAccepted").GetBoolean());
        Assert.Equal(0, root.GetProperty("LiveAnalysis").GetProperty("TotalRoots").GetInt64());
        Assert.Empty(root.GetProperty("LiveAnalysis").GetProperty("Roots").EnumerateArray());
        Assert.DoesNotContain("5282", root.GetRawText());
    }
}
