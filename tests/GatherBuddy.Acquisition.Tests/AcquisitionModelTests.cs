using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AcquisitionModelTests
{
    [Fact]
    public void RequirementsKeepQualityAndClampOwnedSurplus()
    {
        var quality = IngredientQualityDemand.FromRequiredHQ(3, 10);
        var requirement = new MaterialAcquisitionRequirement(100, 10, 4, quality);
        Assert.Equal(6, requirement.RemainingQuantity);
        Assert.Equal(3, requirement.QualityDemand.RequiredHQ);
        Assert.Equal(0, new MaterialAcquisitionRequirement(100, 10, 20, quality).RemainingQuantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterialAcquisitionRequirement(100, -1, 0, default));
        Assert.Throws<ArgumentException>(() => new MaterialAcquisitionRequirement(100, 9, 0, quality));
        Assert.Throws<OverflowException>(() => new MaterialAcquisitionRequirement(100, 1, 0, new(int.MaxValue, 1, 0, 0)));
    }

    [Fact]
    public void NQInventoryDoesNotConsumeMandatoryHQDemand()
    {
        var remaining = IngredientQualityDemand.FromRequiredHQ(3, 10).ConsumeSplit(10, 0, out var nq, out var hq);
        Assert.Equal(7, nq);
        Assert.Equal(0, hq);
        Assert.Equal(3, remaining.RequiredHQ);
    }

    [Fact]
    public void AllocationPreservesExactOfferAndCheckedBundleCosts()
    {
        var vendor = new VendorNpc(10, "Vendor", 20, VendorMenuShopType.GrandCompanyShop);
        var allocation = new VendorPurchaseAllocation(Guid.NewGuid(), 100, 3, 5, 20,
            VendorCurrencyGroup.GrandCompanySeals, 50, VendorShopType.GrandCompanySeals, vendor);
        Assert.Equal(15u, allocation.RequestedOutputQuantity);
        Assert.Equal(150ul, allocation.ExpectedCurrencyCost);
        Assert.Equal(20u, allocation.CurrencyItemId);
        Assert.Equal(vendor, allocation.Vendor);
        Assert.Throws<OverflowException>(() => new VendorPurchaseAllocation(Guid.NewGuid(), 100, uint.MaxValue, 2,
            1, VendorCurrencyGroup.Gil, 1, VendorShopType.GilShop, vendor));
        Assert.Throws<OverflowException>(() => new VendorPurchaseAllocation(Guid.NewGuid(), 100, 2, 1,
            1, VendorCurrencyGroup.Gil, ulong.MaxValue, VendorShopType.GilShop, vendor));
    }

    [Fact]
    public void LedgerSeparatesSettledSpendingAndOutstandingCommitments()
    {
        var commitments = new Dictionary<uint, ulong> { [1] = 30 };
        var ledger = new AcquisitionCurrencyLedgerSnapshot(commitments, new Dictionary<uint, ulong> { [1] = 100 });
        commitments.Clear();
        Assert.Equal(50ul, ledger.GetSpendable(1, 100, 20));
        Assert.Equal(0ul, ledger.GetSpendable(1, 10, 20));
        Assert.Equal(0ul, ledger.GetSpendable(1, 40, 20));
        Assert.Equal(0ul, ledger.GetSpendable(1, ulong.MaxValue, ulong.MaxValue));
        Assert.Equal(100ul, ledger.SettledSpending[1]);
    }

    [Fact]
    public void PendingAcquisitionCannotReportCompleted()
    {
        var ledger = new AcquisitionCurrencyLedgerSnapshot(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>());
        var outcomes = new List<MaterialAcquisitionOutcome> { new(100, 2, 3, 1, [new(MaterialShortageReason.InsufficientCurrency, "Short")]) };
        Assert.Throws<ArgumentException>(() => new MaterialAcquisitionResult(Guid.NewGuid(), MaterialAcquisitionCompletion.Completed, outcomes, ledger));
        var result = new MaterialAcquisitionResult(Guid.NewGuid(), MaterialAcquisitionCompletion.PartiallyFulfilled, outcomes, ledger);
        outcomes.Clear();
        Assert.Single(result.Materials);
    }
}
