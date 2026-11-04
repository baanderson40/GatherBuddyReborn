using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class GemstoneAcquisitionTests
{
    private const uint Gems = 26807;
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
    private static AutomaticVendorPurchasePolicy Settings(bool allow = true, ulong reserve = 0)
        => new() { Enabled = true,
            AllowedCurrencyGroups = allow ? [VendorCurrencyGroup.Gil, VendorCurrencyGroup.BicolorGemstones] : [VendorCurrencyGroup.Gil],
            CurrencyPriority = [VendorCurrencyGroup.Gil, VendorCurrencyGroup.BicolorGemstones],
            CurrencyReservesByItemId = new() { [Gems] = reserve } };
    private static AcquisitionVendorOffer Offer(uint item = 100, uint output = 1, ulong gems = 2, ulong gil = 0)
        => new(item, Gems, VendorCurrencyGroup.BicolorGemstones, gems, output, VendorShopType.SpecialCurrency,
            new(10, "Gem trader", 20, VendorMenuShopType.SpecialShop, ShopItemIndex: 0, SourceShopId: 20, SourceItemIndex: 0))
        { Costs = gil == 0 ? [new(Gems, VendorCurrencyGroup.BicolorGemstones, gems)]
            : [new(1, VendorCurrencyGroup.Gil, gil), new(Gems, VendorCurrencyGroup.BicolorGemstones, gems)] };
    private static MaterialAcquisitionPlanningInput Input(AcquisitionVendorOffer[]? offers = null, uint quantity = 7,
        ulong gems = 100, ulong gil = 100, AutomaticVendorPurchasePolicy? settings = null,
        IDictionary<uint, ulong>? spent = null)
        => new(Session, ResolvedVendorPurchasePolicy.Resolve(settings ?? Settings()),
            (offers ?? [Offer()]).Select(o => o.ItemId).Distinct().Select(id => new AcquisitionMaterialDemand(id, IngredientQualityDemand.FromRequiredHQ(0, (int)quantity))),
            offers ?? [Offer()], new Dictionary<uint, AcquisitionInventory>(), new Dictionary<uint, ulong> { [Gems] = gems, [1] = gil },
            new(new Dictionary<uint, ulong>(), spent ?? new Dictionary<uint, ulong>()));

    [Fact]
    public void DefaultAndExplicitlyDisabledGemstonesNeverAllocate()
    {
        Assert.DoesNotContain(VendorCurrencyGroup.BicolorGemstones, new AutomaticVendorPurchasePolicy().AllowedCurrencyGroups);
        var plan = MaterialAcquisitionPlanner.Plan(Input(settings: Settings(false)));
        Assert.Empty(plan.Allocations);
        Assert.Equal(MaterialShortageReason.CurrencyDisallowed, plan.Materials.Single().PrimaryShortage!.Reason);
    }

    [Theory]
    [InlineData(100ul, 96ul, 2u)]
    [InlineData(100ul, 100ul, 0u)]
    [InlineData(100ul, 0ul, 7u)]
    [InlineData(0ul, 0ul, 0u)]
    public void GemstoneReserveBoundsAffordableOutput(ulong balance, ulong reserve, uint output)
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input(gems: balance, settings: Settings(reserve: reserve)));
        Assert.Equal(output, plan.Materials.Single().PlannedPurchaseQuantity);
        Assert.Equal((ulong)output * 2, plan.ProjectedSpending.GetValueOrDefault(Gems));
    }

    [Fact]
    public void SnapshotKeepsOptInReserveAndExplicitZeroAcrossEdits()
    {
        var settings = Settings(reserve: 0);
        var input = Input(settings: settings);
        settings.CurrencyReservesByItemId[Gems] = 100;
        settings.AllowedCurrencyGroups.Clear();
        Assert.True(input.Policy.Allows(VendorCurrencyGroup.BicolorGemstones));
        Assert.Equal(0ul, input.Policy.GetReserve(Gems));
        Assert.Single(MaterialAcquisitionPlanner.Plan(input).Allocations);
    }

    [Fact]
    public void SharedGemstoneBalanceCannotBeCommittedTwice()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input([Offer(100), Offer(101)], gems: 10));
        Assert.Equal(10ul, plan.ProjectedSpending[Gems]);
        Assert.Equal(5u, plan.Allocations.Sum(a => a.RequestedOutputQuantity));
        Assert.Equal(9, plan.Materials.Sum(m => m.UnresolvedDemand.Total));
    }

    [Theory]
    [InlineData(100ul, 10u, 4ul)]
    [InlineData(2ul, 5u, 2ul)]
    public void BundlesUseWholeTransactionsAndMinimumOverbuy(ulong balance, uint output, ulong spend)
    {
        var input = Input([Offer(output: 5)], gems: balance);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var allocation = Assert.Single(plan.Allocations);
        Assert.Equal(output, allocation.RequestedOutputQuantity);
        Assert.Equal(spend, plan.ProjectedSpending[Gems]);
        // Target is demand, not rounded output: external inventory changes must reduce transactions correctly.
        Assert.Equal(Math.Min(7u, output), CraftingAcquisitionCoordinator.BuildInventoryTargets(input, plan)[100]);
    }

    [Theory]
    [InlineData(100ul, 9ul, 3u)]
    [InlineData(4ul, 100ul, 2u)]
    [InlineData(100ul, 0ul, 0u)]
    public void EveryCostComponentLimitsTransactions(ulong gems, ulong gil, uint count)
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input([Offer(gil: 3)], gems: gems, gil: gil));
        Assert.Equal(count, plan.Materials.Single().PlannedPurchaseQuantity);
        Assert.Equal((ulong)count * 2, plan.ProjectedSpending.GetValueOrDefault(Gems));
        Assert.Equal((ulong)count * 3, plan.ProjectedSpending.GetValueOrDefault(1u));
    }

    [Fact]
    public void DisallowedSecondaryCurrencyRejectsEntireOffer()
    {
        var settings = Settings();
        settings.AllowedCurrencyGroups.Remove(VendorCurrencyGroup.Gil);
        var plan = MaterialAcquisitionPlanner.Plan(Input([Offer(gil: 3)], settings: settings));
        Assert.Empty(plan.Allocations);
        Assert.Empty(plan.ProjectedSpending);
        Assert.Equal(MaterialShortageReason.CurrencyDisallowed, plan.Materials.Single().PrimaryShortage!.Reason);
    }

    [Fact]
    public void SecondaryGilCostHonorsCumulativeRunCap()
    {
        var settings = Settings();
        settings.MaximumGilSpendPerRun = 10;
        var plan = MaterialAcquisitionPlanner.Plan(Input([Offer(gil: 3)], settings: settings, spent: new Dictionary<uint, ulong> { [1] = 7 }));
        Assert.Equal(1u, Assert.Single(plan.Allocations).TransactionCount);
        Assert.Equal(3ul, plan.ProjectedSpending[1]);
    }

    [Fact]
    public void LockedVendorDoesNotAllocateFunds()
    {
        var plan = MaterialAcquisitionPlanner.Plan(Input([Offer() with { Access = VendorOfferAccess.Inaccessible }]));
        Assert.Empty(plan.Allocations);
        Assert.Equal(MaterialShortageReason.VendorInaccessible, plan.Materials.Single().PrimaryShortage!.Reason);
    }

    [Fact]
    public void GemstonePolicyRoundTripsWithoutLosingExplicitZero()
    {
        var settings = Settings();
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(Newtonsoft.Json.JsonConvert.SerializeObject(settings))!;
        Assert.Contains(VendorCurrencyGroup.BicolorGemstones, copy.AllowedCurrencyGroups);
        Assert.True(copy.CurrencyReservesByItemId.ContainsKey(Gems));
        Assert.Equal(0ul, copy.CurrencyReservesByItemId[Gems]);
    }

    [Theory]
    [InlineData(1u, VendorCurrencyGroup.Gil, VendorShopType.GilShop, VendorMenuShopType.GilShop)]
    [InlineData(20u, VendorCurrencyGroup.GrandCompanySeals, VendorShopType.GrandCompanySeals, VendorMenuShopType.GrandCompanyShop)]
    [InlineData(Gems, VendorCurrencyGroup.BicolorGemstones, VendorShopType.SpecialCurrency, VendorMenuShopType.SpecialShop)]
    public void ShopReuseSupportsGilGcAndGemstonesWithoutCrossingOwnershipOrSource(uint currency, VendorCurrencyGroup group, VendorShopType type, VendorMenuShopType menu)
    {
        var first = new VendorPurchaseAllocation(Session, 100, 1, 1, currency, group, 2, type, new(10, "Vendor", 20, menu, SourceShopId: 20));
        VendorPurchaseAllocation Next(VendorNpc vendor, Guid? session = null) => new(session ?? Session, 101, 1, 1, currency, group, 2, type, vendor);
        Assert.True(first.CanShareShopWith(Next(first.Vendor with { ShopItemIndex = 5 })));
        Assert.False(first.CanShareShopWith(Next(first.Vendor with { NpcId = 11 })));
        Assert.False(first.CanShareShopWith(Next(first.Vendor with { SourceShopId = 21 })));
        Assert.False(first.CanShareShopWith(Next(first.Vendor, Guid.NewGuid())));
    }

    [Fact]
    public void OnlyIdenticalAuthorizedTransactionVendorsCanBeRetried()
    {
        var primary = Offer();
        var alternate = primary with { Vendor = primary.Vendor with { NpcId = 11 } };
        var changed = Offer(gems: 3) with { Vendor = primary.Vendor with { NpcId = 12 } };
        var input = Input([primary, alternate, changed]);
        var allocation = Assert.Single(MaterialAcquisitionPlanner.Plan(input).Allocations);
        Assert.Equal(alternate.Vendor, Assert.Single(allocation.AuthorizedAlternatives));
        Assert.Throws<ArgumentException>(() => allocation.WithAuthorizedVendor(changed.Vendor));
        var replacement = allocation.WithAuthorizedVendor(alternate.Vendor);
        Assert.Equal(allocation.AllocationId, replacement.AllocationId);
        Assert.Equal(allocation.ExpectedCosts, replacement.ExpectedCosts);
    }

    [Fact]
    public void FailedVendorsAreAttemptedOnceThenReportedWithoutLooping()
    {
        var offer = Offer();
        var alternate = offer with { Vendor = offer.Vendor with { NpcId = 11 } };
        var input = Input([offer, alternate]);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var port = new Port { StartResult = TransientPurchaseStart.Skipped };
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Started, execution.Start(Session, input.Policy, plan.Allocations,
            CraftingAcquisitionCoordinator.BuildInventoryTargets(input, plan)));
        for (var i = 0; i < 10; i++) execution.Tick(Now);
        Assert.Equal(2, port.Requests.Count);
        Assert.Equal(new uint[] { 10, 11 }, port.Requests.Select(r => r.Allocation.Vendor.NpcId));
        Assert.Equal(MaterialAcquisitionCompletion.PartiallyFulfilled, execution.LastResult!.Completion);
        Assert.Empty(execution.LastResult.SettledSpending);
        Assert.True(port.Released);
    }

    [Fact]
    public void UnsupportedReaderDoesNotTourEquivalentAuthorizedVendors()
    {
        var offer = Offer();
        var alternate = offer with { Vendor = offer.Vendor with { NpcId = 11 } };
        var input = Input([offer, alternate]);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var allocation = Assert.Single(plan.Allocations);
        Assert.Single(allocation.AuthorizedAlternatives);
        var port = new Port();
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Started, execution.Start(Session, input.Policy, plan.Allocations,
            CraftingAcquisitionCoordinator.BuildInventoryTargets(input, plan)));
        execution.Tick(Now);
        port.Receipts.Enqueue(new(Session, allocation.AllocationId, 0, 0, false, false, "Unsupported live reader")
        { PreventVendorFallback = true });
        for (var i = 0; i < 8; i++) execution.Tick(Now);
        Assert.Single(port.Requests);
        Assert.True(port.Released);
        Assert.True(execution.LastResult!.AccountingCertain);
        Assert.Equal(0ul, execution.LastResult.SettledSpending.GetValueOrDefault(Gems));
        Assert.Equal(MaterialAcquisitionCompletion.PartiallyFulfilled, execution.LastResult.Completion);
    }

    [Fact]
    public void UnsupportedGemstoneReaderRetainsShortageButDoesNotBlockOtherCurrencyWork()
    {
        var input = Input();
        var first = Assert.Single(MaterialAcquisitionPlanner.Plan(input).Allocations);
        var gil = new VendorPurchaseAllocation(Session, 101, 1, 1, 1, VendorCurrencyGroup.Gil, 1,
            VendorShopType.GilShop, new(20, "Gil vendor", 30));
        var port = new Port();
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Started, execution.Start(Session, input.Policy, [first, gil],
            new Dictionary<uint, uint> { [100] = 7, [101] = 1 }));
        execution.Tick(Now);
        port.Receipts.Enqueue(new(Session, first.AllocationId, 0, 0, false, false, "Unsupported reader") { PreventVendorFallback = true });
        execution.Tick(Now);
        execution.Tick(Now);
        execution.Tick(Now);
        Assert.Equal(101u, port.Requests.Last().Allocation.ItemId);
        port.Inventory[101] = 1;
        port.Receipts.Enqueue(new(Session, gil.AllocationId, 1, 1, false, false, "Gil receipt"));
        for (var i = 0; i < 5; i++) execution.Tick(Now);
        Assert.Equal(2, port.Requests.Count);
        Assert.Equal(7u, execution.LastResult!.Allocations[0].RemainingQuantity);
        Assert.Equal(1ul, execution.LastResult.SettledSpending[1]);
        Assert.True(port.Released);
    }

    [Fact]
    public void MultipleCostsRemainDetachedAndOverflowChecked()
    {
        var costs = new List<AcquisitionExchangeCost> { new(Gems, VendorCurrencyGroup.BicolorGemstones, 2) };
        var offer = Offer() with { Costs = costs };
        var input = Input([offer]);
        costs.Clear();
        var allocation = Assert.Single(MaterialAcquisitionPlanner.Plan(input).Allocations);
        Assert.Equal(14ul, allocation.ExpectedCosts[Gems]);
        Assert.Throws<OverflowException>(() => new VendorPurchaseAllocation(Session, 100, 2, 1, Gems,
            VendorCurrencyGroup.BicolorGemstones, ulong.MaxValue, VendorShopType.SpecialCurrency, offer.Vendor));
    }

    private sealed class Port : ITransientVendorPort
    {
        public readonly Dictionary<uint, uint> Inventory = new();
        public readonly Queue<TransientPurchaseReceipt> Receipts = new();
        public readonly List<(VendorPurchaseAllocation Allocation, uint Quantity, TransientPurchaseBudget Budget)> Requests = new();
        public bool KeepOpen = true;
        public TransientPurchaseStart StartResult = TransientPurchaseStart.Started;
        public int Cleanups;
        public bool Released;
        public bool TryAcquire(Guid session, out string reason) { reason = "owned"; return true; }
        public bool IsSafe(out string reason) { reason = "ready"; return true; }
        public uint ReadItemCount(uint item) => Inventory.GetValueOrDefault(item);
        public TransientPurchaseStart TryStart(Guid session, VendorPurchaseAllocation allocation, uint quantity, uint target, TransientPurchaseBudget budget, out string reason)
        { Requests.Add((allocation, quantity, budget)); reason = "started"; return StartResult; }
        public TransientPurchaseReceipt? Poll(Guid session) => Receipts.TryDequeue(out var receipt) ? receipt : null;
        public void Pause(Guid session) { }
        public void Cancel(Guid session) { }
        public bool TryCleanup(Guid session) { Cleanups++; return true; }
        public void Release(Guid session) => Released = true;
        public bool CanContinueInteraction(Guid session, VendorPurchaseAllocation previous, VendorPurchaseAllocation next) => KeepOpen;
    }

    private static (TransientVendorExecution Execution, Port Port, VendorPurchaseAllocation Allocation) Start(uint quantity = 7, uint output = 5, bool multi = true)
    {
        var input = Input([Offer(output: output, gil: multi ? 3u : 0)], quantity: quantity, gems: 10000, gil: 10000);
        var plan = MaterialAcquisitionPlanner.Plan(input);
        var port = new Port();
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Started, execution.Start(Session, input.Policy, plan.Allocations,
            CraftingAcquisitionCoordinator.BuildInventoryTargets(input, plan)));
        execution.Tick(Now);
        return (execution, port, plan.Allocations.Single());
    }

    private static void Receipt(TransientVendorExecution execution, Port port, VendorPurchaseAllocation allocation, uint output, bool omitSecondary = false)
    {
        port.Inventory[100] = port.Inventory.GetValueOrDefault(100u) + output;
        var costs = allocation.Costs.Where(c => !omitSecondary || c.CurrencyItemId == Gems)
            .ToDictionary(c => c.CurrencyItemId, c => c.Amount * (output / allocation.OutputQuantityPerTransaction));
        port.Receipts.Enqueue(new(Session, allocation.AllocationId, output, costs.GetValueOrDefault(Gems), false, false, "settled") { ConfirmedCosts = costs });
        execution.Tick(Now);
    }

    [Fact]
    public void ExecutorReconcilesBundleTransactionsAndAllCosts()
    {
        var (execution, port, allocation) = Start();
        Assert.Equal(10u, port.Requests.Single().Quantity);
        Receipt(execution, port, allocation, 10);
        execution.Tick(Now);
        Assert.True(port.Released);
        Assert.Equal(4ul, execution.LastResult!.SettledSpending[Gems]);
        Assert.Equal(6ul, execution.LastResult.SettledSpending[1]);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult.Completion);
    }

    [Fact]
    public void MissingSecondaryReceiptStopsWithoutInventingSpending()
    {
        var (execution, port, allocation) = Start();
        Receipt(execution, port, allocation, 10, omitSecondary: true);
        execution.Tick(Now);
        Assert.False(execution.LastResult!.AccountingCertain);
        Assert.Empty(execution.LastResult.SettledSpending);
        Assert.Single(port.Requests);
    }

    [Fact]
    public void CompatibleBatchesKeepShopOpenAndRecheckRemainingBudget()
    {
        var (execution, port, allocation) = Start(105, 1);
        Receipt(execution, port, allocation, 99);
        Assert.Equal(TransientVendorState.Ready, execution.State);
        Assert.Equal(0, port.Cleanups);
        execution.Tick(Now);
        Assert.Equal(6u, port.Requests.Last().Quantity);
        Assert.Equal(12ul, port.Requests.Last().Budget.CurrencyBudgets![Gems].MaximumSpend);
        Assert.Equal(18ul, port.Requests.Last().Budget.CurrencyBudgets![1].MaximumSpend);
        Receipt(execution, port, allocation, 6);
        execution.Tick(Now);
        Assert.Equal(1, port.Cleanups);
        Assert.True(port.Released);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopOrPauseBetweenBatchesPreventsNextSubmission(bool cancel)
    {
        var (execution, port, allocation) = Start(105, 1);
        Receipt(execution, port, allocation, 99);
        if (cancel) execution.Cancel(); else execution.Pause();
        execution.Tick(Now);
        execution.Tick(Now);
        Assert.Single(port.Requests);
        if (cancel) Assert.Equal(MaterialAcquisitionCompletion.Cancelled, execution.LastResult!.Completion);
        else
        {
            execution.Resume();
            execution.Tick(Now);
            Assert.Equal(6u, port.Requests.Last().Quantity);
        }
    }

    [Fact]
    public void StackBoundNativeRequestContinuesInsteadOfSkippingRemainingBundles()
    {
        var (execution, port, allocation) = Start(502, 5, multi: false);
        port.Inventory[100] = 250;
        port.Receipts.Enqueue(new(Session, allocation.AllocationId, 250, 100, false, false, "Stack-bounded request completed")
        { SubmittedOutputQuantity = 250 });
        execution.Tick(Now);
        Assert.Equal(TransientVendorState.Ready, execution.State);
        execution.Tick(Now);
        Assert.Equal(255u, port.Requests.Last().Quantity);
        Assert.Equal(102ul, port.Requests.Last().Budget.MaximumSpend);
    }

    [Fact]
    public void ExternalInventoryReducesBundleTransactionsWithoutExtraOverbuy()
    {
        var (execution, port, allocation) = Start(502, 5, multi: false);
        Receipt(execution, port, allocation, 495);
        port.Inventory[100] += 4;
        execution.Tick(Now);
        Assert.Equal(5u, port.Requests.Last().Quantity);
    }
}
