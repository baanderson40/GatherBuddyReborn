using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class PlannerTests
{
    private static readonly Guid Session = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static AcquisitionMaterialDemand Demand(uint item = 100, int quantity = 20, bool gather = false)
        => new(item, IngredientQualityDemand.FromPreferNQ(quantity), gather);
    private static AcquisitionVendorOffer Gil(uint item = 100, ulong cost = 10, uint npc = 10, uint output = 1)
        => new(item, 1, VendorCurrencyGroup.Gil, cost, output, VendorShopType.GilShop, new(npc, "Gil vendor", 20));
    private static AcquisitionVendorOffer Seals(uint item = 100, ulong cost = 5)
        => new(item, 20, VendorCurrencyGroup.GrandCompanySeals, cost, 1, VendorShopType.GrandCompanySeals,
            new(11, "GC vendor", 21, VendorMenuShopType.GrandCompanyShop));
    private static AutomaticVendorPurchasePolicy Policy() => new() { Enabled = true };
    private static MaterialAcquisitionPlanningInput Input(IEnumerable<AcquisitionMaterialDemand>? demands = null,
        IEnumerable<AcquisitionVendorOffer>? offers = null, Dictionary<uint, ulong>? balances = null,
        AutomaticVendorPurchasePolicy? policy = null, Dictionary<uint, AcquisitionInventory>? inventory = null,
        AcquisitionCurrencyLedgerSnapshot? ledger = null, VendorOfferDataStatus status = VendorOfferDataStatus.Ready,
        IEnumerable<VendorPurchaseAllocation>? replaced = null)
        => new(Session, ResolvedVendorPurchasePolicy.Resolve(policy ?? Policy()), demands ?? [Demand()], offers ?? [Gil(), Seals()],
            inventory ?? new(), balances ?? new() { [1] = 200, [20] = 100 },
            ledger ?? new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), status, replaced);

    [Fact]
    public void OwnedMaterialsNeedNoPurchases()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(inventory: new() { [100] = new(20, 0) }));
        Assert.Empty(plan.Allocations);
        Assert.Equal(0, plan.Materials.Single().UnresolvedDemand.Total);
    }

    [Theory]
    [InlineData(200ul, 0ul, 1u)]
    [InlineData(0ul, 100ul, 20u)]
    [InlineData(200ul, 100ul, 1u)]
    public void UsesAffordableCurrencyInPriorityOrder(ulong gil, ulong seals, uint expectedCurrency)
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(balances: new() { [1] = gil, [20] = seals }));
        var allocation = Assert.Single(plan.Allocations);
        Assert.Equal(expectedCurrency, allocation.CurrencyItemId);
        Assert.Equal(20u, allocation.RequestedOutputQuantity);
    }

    [Fact]
    public void ConfiguredPriorityWinsOverUnlikeNumericCosts()
    {
        var policy = Policy();
        policy.CurrencyPriority.Reverse();
        var plan = MaterialAcquisitionPlanner.Plan(Input(policy: policy));
        Assert.Equal(20u, Assert.Single(plan.Allocations).CurrencyItemId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SplitsPurchasesThenFallsBackOrWarns(bool gather)
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: gather)], balances: new() { [1] = 120, [20] = 25 }));
        Assert.Equal(new uint[] { 12, 5 }, plan.Allocations.Select(a => a.RequestedOutputQuantity));
        var material = Assert.Single(plan.Materials);
        Assert.Equal(17u, material.PlannedPurchaseQuantity);
        Assert.Equal(gather ? 3 : 0, material.GatheringDemand.Total);
        Assert.Equal(gather ? 0 : 3, material.UnresolvedDemand.Total);
        Assert.Equal(gather, material.Shortages.Count == 0);
    }

    [Fact]
    public void PrioritizesNonGatherableDemandAndNeverOvercommits()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(100, 10, true), Demand(200, 10)],
            offers: [Gil(100), Gil(200)], balances: new() { [1] = 100 }));
        Assert.Equal(200u, Assert.Single(plan.Allocations).ItemId);
        Assert.Equal(100ul, plan.ProjectedSpending[1]);
        Assert.Equal(10, plan.Materials.Single(m => m.ItemId == 100).GatheringDemand.Total);
    }

    [Fact]
    public void ProtectsReserveAndReportsReserveShortage()
    {
        var policy = Policy();
        policy.CurrencyReservesByItemId[1] = 300;
        var plan = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()], policy: policy));
        Assert.Empty(plan.Allocations);
        Assert.Contains(plan.Materials.Single().Shortages, s => s.Reason == MaterialShortageReason.ReserveReached);
    }

    [Fact]
    public void GilCapAccountsForSettledSpendingAndOtherCommitments()
    {
        var policy = Policy();
        policy.MaximumGilSpendPerRun = 100;
        var ledger = new AcquisitionCurrencyLedgerSnapshot(new Dictionary<uint, ulong> { [1] = 20 }, new Dictionary<uint, ulong> { [1] = 30 });
        var plan = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()], policy: policy, ledger: ledger));
        Assert.Equal(5u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Equal(70ul, plan.ResultingLedger.OutstandingCommitments[1]);
        Assert.Equal(30ul, plan.ResultingLedger.SettledSpending[1]);
        Assert.Contains(plan.Materials.Single().Shortages, s => s.Reason == MaterialShortageReason.SpendingCapReached);
    }

    [Fact]
    public void ZeroCapAndDisallowedCurrenciesCannotSpend()
    {
        var policy = Policy();
        policy.MaximumGilSpendPerRun = 0;
        policy.AllowedCurrencyGroups = [VendorCurrencyGroup.Gil];
        var plan = MaterialAcquisitionPlanner.Plan(Input(policy: policy));
        Assert.Empty(plan.Allocations);
        Assert.Contains(plan.Materials.Single().Shortages, s => s.Reason == MaterialShortageReason.SpendingCapReached);
        Assert.Contains(plan.Materials.Single().RejectedAlternatives, summary => summary.Kind == AcquisitionOfferRejectionKind.CurrencyDisallowed);
    }

    [Fact]
    public void MandatoryHQIsNotSatisfiedByNQInventoryOrOffers()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [new(100, new(3, 0, 0, 7), true)],
            inventory: new() { [100] = new(2, 0) }));
        Assert.Equal(5u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Equal(3, plan.Materials.Single().UnresolvedDemand.RequiredHQ);
        Assert.Contains(plan.Materials.Single().Shortages, s => s.Reason == MaterialShortageReason.RequiredQualityUnavailable);
    }

    [Fact]
    public void GatheringFallbackPreservesHQDemandWhenSourceCanProvideIt()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [new(100, new(3, 2, 0, 5), true, true)],
            offers: [], balances: new()));
        var material = Assert.Single(plan.Materials);
        Assert.Equal(new IngredientQualityDemand(3, 2, 0, 5), material.GatheringDemand);
        Assert.Equal(0, material.UnresolvedDemand.Total);
    }

    [Fact]
    public void DuplicateRequirementsConsumeOwnedInventoryOnce()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(quantity: 5), Demand(quantity: 5)],
            inventory: new() { [100] = new(4, 0) }));
        Assert.Equal(6u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Single(plan.Materials);
    }

    [Fact]
    public void UnsupportedInaccessibleAndWrongCurrencyMetadataCannotBeAllocated()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(offers:
            [Gil() with { Supported = false }, Gil() with { Access = VendorOfferAccess.Inaccessible },
             Gil() with { CurrencyItemId = 20 }]));
        Assert.Empty(plan.Allocations);
        Assert.Contains(plan.Materials.Single().RejectedAlternatives, summary => summary.Kind == AcquisitionOfferRejectionKind.Unsupported);
        Assert.Contains(plan.Materials.Single().Shortages, s => s.Reason == MaterialShortageReason.VendorInaccessible);
    }

    [Fact]
    public void LoadingWaitsWithoutFalseShortagesOrFallback()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: true)], status: VendorOfferDataStatus.Loading));
        Assert.Equal(AcquisitionPlanningStatus.WaitingForVendorData, plan.Status);
        Assert.Empty(plan.Allocations);
        Assert.Empty(plan.Materials.Single().Shortages);
        Assert.Equal(0, plan.Materials.Single().GatheringDemand.Total);
    }

    [Fact]
    public void UnavailableDataStillAllowsGatheringAndWarnsForOtherItems()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: true), Demand(200)], status: VendorOfferDataStatus.Unavailable));
        Assert.Equal(AcquisitionPlanningStatus.VendorDataUnavailable, plan.Status);
        Assert.Equal(20, plan.Materials.Single(m => m.ItemId == 100).GatheringDemand.Total);
        Assert.Contains(plan.Materials.Single(m => m.ItemId == 200).Shortages, s => s.Reason == MaterialShortageReason.VendorDataUnavailable);
    }

    [Fact]
    public void DisabledPolicyDoesNotWaitOrPurchase()
    {
        var policy = new AutomaticVendorPurchasePolicy();
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: true)], policy: policy, status: VendorOfferDataStatus.Loading));
        Assert.Equal(AcquisitionPlanningStatus.Ready, plan.Status);
        Assert.Empty(plan.Allocations);
        Assert.Equal(20, plan.Materials.Single().GatheringDemand.Total);
    }

    [Fact]
    public void SourcePreferenceCanExcludePurchasing()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: true) with { AllowPurchasing = false }]));
        Assert.Empty(plan.Allocations);
        Assert.Equal(20, plan.Materials.Single().GatheringDemand.Total);
    }

    [Fact]
    public void NoPurchasableDemandDoesNotWaitForVendorData()
    {
        var owned = MaterialAcquisitionPlanner.Plan(Input(inventory: new() { [100] = new(20, 0) }, status: VendorOfferDataStatus.Loading));
        Assert.Equal(AcquisitionPlanningStatus.Ready, owned.Status);
        var hqOnly = MaterialAcquisitionPlanner.Plan(Input(demands: [new(100, IngredientQualityDemand.FromRequiredHQ(5, 5), true, true)],
            status: VendorOfferDataStatus.Loading));
        Assert.Equal(AcquisitionPlanningStatus.Ready, hqOnly.Status);
        Assert.Equal(5, hqOnly.Materials.Single().GatheringDemand.RequiredHQ);
        var gatherOnly = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(gather: true) with { AllowPurchasing = false }],
            status: VendorOfferDataStatus.Unavailable));
        Assert.Equal(AcquisitionPlanningStatus.Ready, gatherOnly.Status);
    }

    [Fact]
    public void ExistingCommitmentsRemainReservedWhenNotExplicitlyReplaced()
    {
        var ledger = new AcquisitionCurrencyLedgerSnapshot(new Dictionary<uint, ulong> { [1] = 100 }, new Dictionary<uint, ulong>());
        var plan = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()], ledger: ledger));
        Assert.Equal(10u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Equal(200ul, plan.ResultingLedger.OutstandingCommitments[1]);
    }

    [Fact]
    public void ConflictingSourcePermissionsAndNegativeInventoryAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(), Demand(gather: true)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterialAcquisitionPlanner.Plan(Input(inventory: new() { [100] = new(-1, 0) })));
    }

    [Fact]
    public void ShuffledInputsProduceSameAllocationDecisions()
    {
        var demands = new[] { Demand(200, 2), Demand(100, 3) };
        var offers = new[] { Gil(100, 20, 11), Gil(200), Gil(100, 10, 12), Gil(100, 10, 10) };
        var first = MaterialAcquisitionPlanner.Plan(Input(demands, offers));
        var second = MaterialAcquisitionPlanner.Plan(Input(demands.Reverse(), offers.Reverse()));
        Assert.Equal(first.Allocations.Select(a => (a.ItemId, a.CurrencyItemId, a.Vendor, a.RequestedOutputQuantity)),
            second.Allocations.Select(a => (a.ItemId, a.CurrencyItemId, a.Vendor, a.RequestedOutputQuantity)));
        Assert.Equal(10u, first.Allocations[0].Vendor.NpcId);
    }

    [Fact]
    public void ReplanningReleasesOnlyExplicitCancelledAllocations()
    {
        var first = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()]));
        var second = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()], ledger: first.ResultingLedger, replaced: first.Allocations));
        Assert.Equal(20u, Assert.Single(second.Allocations).RequestedOutputQuantity);
        Assert.Equal(200ul, second.ResultingLedger.OutstandingCommitments[1]);
        Assert.Equal(200ul, first.ResultingLedger.OutstandingCommitments[1]);
        Assert.Throws<ArgumentException>(() => MaterialAcquisitionPlanner.Plan(Input(ledger: first.ResultingLedger,
            replaced: first.Allocations.Concat(first.Allocations))));
        var otherSession = new MaterialAcquisitionPlanningInput(Guid.NewGuid(), ResolvedVendorPurchasePolicy.Resolve(Policy()),
            [Demand()], [Gil()], new Dictionary<uint, AcquisitionInventory>(), new Dictionary<uint, ulong>(), first.ResultingLedger,
            replacedAllocations: first.Allocations);
        Assert.Throws<ArgumentException>(() => MaterialAcquisitionPlanner.Plan(otherSession));
    }

    [Fact]
    public void InputSnapshotsAndLedgerAreNotMutated()
    {
        var balances = new Dictionary<uint, ulong> { [1] = 200 };
        var inventory = new Dictionary<uint, AcquisitionInventory>();
        var input = Input(balances: balances, inventory: inventory);
        balances.Clear();
        inventory[100] = new(100, 0);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        Assert.Equal(20u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Empty(input.Ledger.OutstandingCommitments);
        Assert.Empty(input.Inventory);
    }

    [Fact]
    public void BundlesPermitMinimumOverbuyWithoutAnExtraConfirmation()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(quantity: 7)],
            offers: [Gil(cost: 20, output: 5), Gil(cost: 10, npc: 11)]));
        Assert.Equal(new uint[] { 10 }, plan.Allocations.Select(a => a.RequestedOutputQuantity));
        Assert.Equal(40ul, plan.ProjectedSpending[1]);
        var residual = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(quantity: 3)], offers: [Gil(output: 5)]));
        Assert.Equal(5u, Assert.Single(residual.Allocations).RequestedOutputQuantity);
        Assert.Equal(0, residual.Materials.Single().UnresolvedDemand.Total);
    }

    [Fact]
    public void BoundaryCostsDoNotOverflow()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(quantity: 2)], offers: [Gil(cost: ulong.MaxValue)],
            balances: new() { [1] = ulong.MaxValue }));
        Assert.Equal(1u, Assert.Single(plan.Allocations).RequestedOutputQuantity);
        Assert.Equal(ulong.MaxValue, plan.ProjectedSpending[1]);
        Assert.Equal(1, plan.Materials.Single().UnresolvedDemand.Total);
        Assert.Throws<OverflowException>(() => MaterialAcquisitionPlanner.Plan(Input(demands:
            [Demand(quantity: int.MaxValue), Demand(quantity: 1)])));
    }

    [Fact]
    public void FundingPrimaryExcludesRejectedWrongGcAndUnsupportedAlternatives()
    {
        var policy = Policy();
        policy.CurrencyReservesByItemId[1] = 200;
        var offers = new[] { Gil(), Seals() with { Access = VendorOfferAccess.Inaccessible,
            RejectionKind = AcquisitionOfferRejectionKind.WrongGrandCompany }, Gil(npc: 12) with { Supported = false } };
        var first = MaterialAcquisitionPlanner.Plan(Input(offers: offers, policy: policy));
        var second = MaterialAcquisitionPlanner.Plan(Input(offers: offers.Reverse(), policy: policy));
        var material = Assert.Single(first.Materials);
        Assert.Equal(MaterialShortageReason.ReserveReached, material.PrimaryShortage!.Reason);
        Assert.Single(material.Shortages);
        Assert.Single(material.EligibleOptionBlockers);
        Assert.Equal(material.PrimaryShortage, second.Materials.Single().PrimaryShortage);
        Assert.Contains(material.RejectedAlternatives, summary => summary.Kind == AcquisitionOfferRejectionKind.WrongGrandCompany);
        Assert.Contains(material.RejectedAlternatives, summary => summary.Kind == AcquisitionOfferRejectionKind.Unsupported);
    }

    [Theory]
    [InlineData(20ul, 20ul, MaterialShortageReason.ReserveReached)]
    [InlineData(0ul, 20ul, MaterialShortageReason.InsufficientCurrency)]
    public void PrimarySeparatesActualPovertyFromProtectedBalance(ulong balance, ulong reserve, MaterialShortageReason expected)
    {
        var policy = Policy();
        policy.CurrencyReservesByItemId[1] = reserve;
        var plan = MaterialAcquisitionPlanner.Plan(Input(offers: [Gil()], balances: new() { [1] = balance }, policy: policy));
        Assert.Equal(expected, plan.Materials.Single().PrimaryShortage!.Reason);
    }

    [Fact]
    public void PartialPurchaseAndMixedQualityExplainOnlyResidualDemand()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(demands: [new(100, new(3, 0, 0, 7))], offers: [Gil()],
            balances: new() { [1] = 20 }));
        var material = plan.Materials.Single();
        Assert.Equal(2u, material.PlannedPurchaseQuantity);
        Assert.Equal(8, material.UnresolvedDemand.Total);
        Assert.Equal(MaterialShortageReason.InsufficientCurrency, material.PrimaryShortage!.Reason);
        Assert.Contains("5 NQ-compatible", material.PrimaryShortage.Detail);
        Assert.Contains(material.Shortages, shortage => shortage.Reason == MaterialShortageReason.RequiredQualityUnavailable
            && shortage.Detail.StartsWith("3 mandatory HQ"));
    }

    [Fact]
    public void BundleOverbuyDoesNotSatisfyMandatoryHqDemand()
    {
        var bundle = MaterialAcquisitionPlanner.Plan(Input(demands: [Demand(quantity: 3)], offers: [Gil(output: 5)]));
        Assert.Null(bundle.Materials.Single().PrimaryShortage);
        var hq = MaterialAcquisitionPlanner.Plan(Input(demands: [new(100, IngredientQualityDemand.FromRequiredHQ(3, 3))]));
        Assert.Equal(MaterialShortageReason.RequiredQualityUnavailable, hq.Materials.Single().PrimaryShortage!.Reason);
    }

    [Fact]
    public void HighestPriorityEligibleOptionSuppliesPrimaryWithOtherBlockersRetained()
    {
        var policy = Policy();
        policy.MaximumGilSpendPerRun = 0;
        var plan = MaterialAcquisitionPlanner.Plan(Input(policy: policy, balances: new() { [1] = 200, [20] = 0 }));
        var material = plan.Materials.Single();
        Assert.Equal(MaterialShortageReason.SpendingCapReached, material.PrimaryShortage!.Reason);
        Assert.Contains(material.EligibleOptionBlockers, blocker => blocker.Reason == MaterialShortageReason.InsufficientCurrency);
        policy.CurrencyPriority.Reverse();
        var reversed = MaterialAcquisitionPlanner.Plan(Input(policy: policy, balances: new() { [1] = 200, [20] = 0 }));
        Assert.Equal(MaterialShortageReason.InsufficientCurrency, reversed.Materials.Single().PrimaryShortage!.Reason);
    }
}
