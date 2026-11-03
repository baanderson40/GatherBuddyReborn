using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class CraftingAcquisitionRunTests
{
    private static TransientVendorResult Result(Guid session, ulong gil, bool certain = true, bool cleaned = true)
        => new(session, MaterialAcquisitionCompletion.Completed, Array.Empty<TransientAllocationResult>(),
            new Dictionary<uint, ulong> { [1] = gil }, cleaned, certain);

    [Fact]
    public void RetryUsesFreshOwnershipWithoutResettingRunSpending()
    {
        var run = new CraftingAcquisitionRun();
        var firstId = run.SessionId;
        var first = Result(firstId, 70);
        run.Observe(first);
        run.Observe(first);
        Assert.Equal(70ul, run.SpendingSnapshot()[1]);
        run.Retry(first);
        Assert.NotEqual(firstId, run.SessionId);
        Assert.Equal(firstId, run.RunId);
        Assert.Equal(70ul, run.SpendingSnapshot()[1]);
        run.Observe(Result(run.SessionId, 20));
        Assert.Equal(90ul, run.SpendingSnapshot()[1]);
    }

    [Fact]
    public void StaleResultCannotBeChargedToLaterAttempt()
    {
        var run = new CraftingAcquisitionRun();
        var first = Result(run.SessionId, 70);
        run.Retry(first);
        run.Observe(first);
        run.Observe(Result(Guid.NewGuid(), 1000));
        Assert.Equal(70ul, run.SpendingSnapshot()[1]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UncertainAccountingOrCleanupRefusesRetry(bool certain, bool cleaned)
    {
        var run = new CraftingAcquisitionRun();
        var session = run.SessionId;
        Assert.Throws<InvalidOperationException>(() => run.Retry(Result(session, 70, certain, cleaned)));
        Assert.Equal(session, run.SessionId);
        Assert.Empty(run.SpendingSnapshot());
    }

    [Fact]
    public void RetryBeforeAnyPurchasesStillGetsFreshOwnership()
    {
        var run = new CraftingAcquisitionRun();
        var session = run.SessionId;
        run.Retry(null);
        Assert.NotEqual(session, run.SessionId);
        Assert.Empty(run.SpendingSnapshot());
    }

    [Fact]
    public void SpendingSnapshotsCannotMutateRunLedger()
    {
        var run = new CraftingAcquisitionRun();
        run.Observe(Result(run.SessionId, 70));
        run.SpendingSnapshot().Clear();
        Assert.Equal(70ul, run.SpendingSnapshot()[1]);
    }

    [Fact]
    public void RetryPlannerOnlyAllocatesUnspentPartOfRunCap()
    {
        var run = new CraftingAcquisitionRun();
        run.Retry(Result(run.SessionId, 70));
        var policy = ResolvedVendorPurchasePolicy.Resolve(new AutomaticVendorPurchasePolicy
            { Enabled = true, MaximumGilSpendPerRun = 100 });
        var input = new MaterialAcquisitionPlanningInput(run.SessionId, policy,
            new[] { new AcquisitionMaterialDemand(100, IngredientQualityDemand.FromPreferNQ(5), false) },
            new[] { new AcquisitionVendorOffer(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop,
                new(10, "Vendor", 20)) },
            new Dictionary<uint, AcquisitionInventory>(), new Dictionary<uint, ulong> { [1] = 1000 },
            new(new Dictionary<uint, ulong>(), run.SpendingSnapshot()), VendorOfferDataStatus.Ready);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        Assert.Equal(3u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Equal(2, Assert.Single(plan.Materials).UnresolvedDemand.Total);
    }
}
