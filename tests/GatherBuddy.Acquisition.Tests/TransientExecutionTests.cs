using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class TransientExecutionTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
    private static VendorPurchaseAllocation Allocation(uint quantity = 5, uint item = 100, ulong cost = 10)
        => new(Session, item, quantity, 1, 1, VendorCurrencyGroup.Gil, cost, VendorShopType.GilShop, new(10, "Vendor", 20));
    private static ResolvedVendorPurchasePolicy Policy(ulong? cap = null)
        => ResolvedVendorPurchasePolicy.Resolve(new() { Enabled = true, MaximumGilSpendPerRun = cap,
            CurrencyReservesByItemId = new() { [1] = 50 } });
    private static TransientVendorExecution Start(FakePort port, VendorPurchaseAllocation[]? allocations = null,
        uint target = 5, ulong? cap = null)
    {
        var batch = allocations ?? [Allocation()];
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Started, execution.Start(Session, Policy(cap), batch,
            batch.Select(a => a.ItemId).Distinct().ToDictionary(id => id, _ => target)));
        return execution;
    }
    private static void Settle(TransientVendorExecution execution, FakePort port, uint quantity, ulong? spend,
        bool interrupted = false, bool fatal = false)
    {
        var allocation = port.Requests.Last().Allocation;
        port.Inventory[allocation.ItemId] = port.ReadItemCount(allocation.ItemId) + quantity;
        port.Receipts.Enqueue(new(Session, allocation.AllocationId, quantity, spend, interrupted, fatal, "Receipt"));
        execution.Tick(Now);
        execution.Tick(Now);
    }

    [Fact]
    public void LeaseRejectsForeignStaleAndOwnerlessCalls()
    {
        var lease = new VendorAutomationLease();
        Assert.True(lease.Allows(null));
        Assert.False(lease.TryAcquire(Guid.Empty));
        Assert.True(lease.TryAcquire(Session));
        Assert.False(lease.Allows(null));
        Assert.False(lease.Allows(Guid.NewGuid()));
        Assert.True(lease.Allows(Session));
        Assert.False(lease.TryAcquire(Session));
        Assert.False(lease.Release(Guid.NewGuid()));
        Assert.True(lease.Release(Session));
        Assert.False(lease.Allows(Session));
    }

    [Theory]
    [InlineData(100ul, 20ul, 30ul, 80ul, 10ul, 50ul)]
    [InlineData(10ul, 20ul, 30ul, 80ul, 10ul, 0ul)]
    [InlineData(100ul, 20ul, 30ul, 40ul, 50ul, 0ul)]
    [InlineData(100ul, 20ul, 30ul, 40ul, 10ul, 30ul)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0ul, 0ul)]
    public void BudgetSaturatesAndDoesNotDoubleSubtractSettledSpend(ulong balance, ulong reserve,
        ulong commitments, ulong cap, ulong spent, ulong expected)
        => Assert.Equal(expected, new TransientPurchaseBudget(reserve, commitments, cap).Spendable(balance, spent));

    [Fact]
    public void UnlimitedBudgetStillProtectsReserveAndCommitments()
        => Assert.Equal(50ul, new TransientPurchaseBudget(20, 30, null).Spendable(100, 99));

    [Fact]
    public void SuccessfulPurchaseWaitsForCleanupThenReleases()
    {
        var port = new FakePort { CleanupReady = false };
        var execution = Start(port);
        execution.Tick(Now);
        Settle(execution, port, 5, 50);
        Assert.Equal(TransientVendorState.CleaningUp, execution.State);
        Assert.False(port.Released);
        Assert.Null(execution.LastResult);
        port.CleanupReady = true;
        execution.Tick(Now);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult!.Completion);
        Assert.Equal(50ul, execution.LastResult.SettledSpending[1]);
        Assert.True(port.Released);
    }

    [Fact]
    public void LargeAllocationUsesSeparateBatchesAndRemainingBudget()
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(205)], 205, 2050);
        execution.Tick(Now);
        Assert.Equal(99u, port.Requests.Last().Quantity);
        Settle(execution, port, 99, 990);
        execution.Tick(Now);
        Assert.Equal(1060ul, port.Requests.Last().Budget.MaximumSpend);
        Settle(execution, port, 99, 990);
        execution.Tick(Now);
        Assert.Equal(7u, port.Requests.Last().Quantity);
        Settle(execution, port, 7, 70);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult!.Completion);
        Assert.Equal(2050ul, execution.LastResult.SettledSpending[1]);
    }

    [Fact]
    public void SharedBudgetProtectsFutureAllocationsAndRunCap()
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(), Allocation(item: 101)], cap: 80);
        execution.Tick(Now);
        var budget = port.Requests.Single().Budget;
        Assert.Equal(50ul, budget.Reserve);
        Assert.Equal(50ul, budget.OtherCommitments);
        Assert.Equal(30ul, budget.MaximumSpend);
    }

    [Fact]
    public void AlreadyOwnedTargetRequiresNoPurchaseAndReportsNoRemainingWork()
    {
        var port = new FakePort();
        port.Inventory[100] = 5;
        var execution = Start(port);
        execution.Tick(Now);
        execution.Tick(Now);
        execution.Tick(Now);
        Assert.Empty(port.Requests);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult!.Completion);
        Assert.Equal(0u, execution.LastResult.Allocations.Single().AcquiredQuantity);
        Assert.Equal(0u, execution.LastResult.Allocations.Single().RemainingQuantity);
    }

    [Fact]
    public void InventoryChangeReducesSubmittedQuantity()
    {
        var port = new FakePort();
        var execution = Start(port);
        port.Inventory[100] = 3;
        execution.Tick(Now);
        Assert.Equal(2u, port.Requests.Single().Quantity);
        Settle(execution, port, 2, 20);
        execution.Tick(Now);
        execution.Tick(Now);
        execution.Tick(Now);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult!.Completion);
    }

    [Fact]
    public void PartialPurchaseContinuesOtherAllocationsWithoutClaimingSuccess()
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(), Allocation(item: 101)]);
        execution.Tick(Now);
        Settle(execution, port, 2, 20);
        execution.Tick(Now);
        Assert.Equal(101u, port.Requests.Last().Allocation.ItemId);
        Assert.Equal(0ul, port.Requests.Last().Budget.OtherCommitments);
        Settle(execution, port, 5, 50);
        Assert.Equal(MaterialAcquisitionCompletion.PartiallyFulfilled, execution.LastResult!.Completion);
        Assert.Equal(3u, execution.LastResult.Allocations[0].RemainingQuantity);
    }

    [Fact]
    public void PauseAllowsReceiptAndCleanupButNoNextBatchUntilResume()
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(105)], 105);
        execution.Tick(Now);
        execution.Pause();
        Settle(execution, port, 99, 990);
        Assert.Equal(TransientVendorState.Paused, execution.State);
        execution.Tick(Now);
        Assert.Single(port.Requests);
        execution.Resume();
        execution.Tick(Now);
        Assert.Equal(6u, port.Requests.Last().Quantity);
    }

    [Fact]
    public void CancelPreservesConfirmedSpendingAndNeverStartsNextAllocation()
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(), Allocation(item: 101)]);
        execution.Tick(Now);
        execution.Cancel();
        Settle(execution, port, 2, 20, interrupted: true);
        execution.Tick(Now);
        Assert.Single(port.Requests);
        Assert.Equal(MaterialAcquisitionCompletion.Cancelled, execution.LastResult!.Completion);
        Assert.Equal(20ul, execution.LastResult.SettledSpending[1]);
        Assert.Equal(5u, execution.LastResult.Allocations[1].RemainingQuantity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(49ul)]
    public void UnknownOrMismatchedAccountingStopsBatch(ulong? spend)
    {
        var port = new FakePort();
        var execution = Start(port, [Allocation(), Allocation(item: 101)]);
        execution.Tick(Now);
        Settle(execution, port, 5, spend);
        Assert.Single(port.Requests);
        Assert.False(execution.LastResult!.AccountingCertain);
        Assert.Empty(execution.LastResult.SettledSpending);
        Assert.Equal(MaterialAcquisitionCompletion.Failed, execution.LastResult.Completion);
    }

    [Fact]
    public void UnsafeStateBeforeSubmissionStopsWithoutSpending()
    {
        var port = new FakePort();
        var execution = Start(port);
        port.Safe = false;
        execution.Tick(Now);
        execution.Tick(Now);
        Assert.Empty(port.Requests);
        Assert.Equal(MaterialAcquisitionCompletion.Failed, execution.LastResult!.Completion);
        Assert.True(port.Released);
    }

    [Theory]
    [InlineData(TransientPurchaseStart.Skipped, MaterialAcquisitionCompletion.PartiallyFulfilled)]
    [InlineData(TransientPurchaseStart.Unsafe, MaterialAcquisitionCompletion.Failed)]
    public void RejectedSubmissionStillCleansUp(TransientPurchaseStart start, MaterialAcquisitionCompletion completion)
    {
        var port = new FakePort { StartResult = start };
        var execution = Start(port);
        execution.Tick(Now);
        Assert.False(port.Released);
        execution.Tick(Now);
        Assert.Equal(completion, execution.LastResult!.Completion);
        Assert.True(port.Released);
        Assert.Equal(5u, execution.LastResult.Allocations.Single().RemainingQuantity);
    }

    [Fact]
    public void ExcessInventoryReceiptCannotBeCountedAsAuthorizedSpending()
    {
        var port = new FakePort();
        var execution = Start(port);
        execution.Tick(Now);
        Settle(execution, port, 6, 60);
        Assert.False(execution.LastResult!.AccountingCertain);
        Assert.Empty(execution.LastResult.SettledSpending);
        Assert.Equal(5u, execution.LastResult.Allocations.Single().AcquiredQuantity);
    }

    [Fact]
    public void StaleReceiptCannotAdvanceCurrentSession()
    {
        var port = new FakePort();
        var execution = Start(port);
        execution.Tick(Now);
        port.Receipts.Enqueue(new(Guid.NewGuid(), port.Requests.Single().Allocation.AllocationId, 5, 50, false, false, "Stale"));
        execution.Tick(Now);
        Assert.Equal(TransientVendorState.Purchasing, execution.State);
        Settle(execution, port, 5, 50);
        Assert.Equal(MaterialAcquisitionCompletion.Completed, execution.LastResult!.Completion);
    }

    [Fact]
    public void CleanupTimeoutRetainsOwnershipAndCannotStartNewRun()
    {
        var port = new FakePort { CleanupReady = false };
        var execution = Start(port);
        execution.Tick(Now);
        Settle(execution, port, 5, 50);
        execution.Tick(Now.AddSeconds(16));
        Assert.Equal(TransientVendorState.CleanupBlocked, execution.State);
        Assert.False(port.Released);
        Assert.Null(execution.LastResult);
        Assert.Equal(TransientVendorStartResult.Busy, execution.Start(Session, Policy(), [Allocation()], new Dictionary<uint, uint> { [100] = 5 }));
        port.CleanupReady = true;
        execution.Tick(Now.AddSeconds(17));
        Assert.Equal(MaterialAcquisitionCompletion.Failed, execution.LastResult!.Completion);
        Assert.True(port.Released);
        Assert.False(execution.LastResult.CleanupSucceeded);
    }

    [Fact]
    public void InvalidBatchAndDisabledPolicyDoNotAcquireOwnership()
    {
        var port = new FakePort();
        var execution = new TransientVendorExecution(port);
        var allocation = Allocation();
        Assert.Equal(TransientVendorStartResult.InvalidBatch, execution.Start(Session, Policy(), [allocation, allocation], new Dictionary<uint, uint> { [100] = 5 }));
        Assert.Equal(TransientVendorStartResult.InvalidBatch, execution.Start(Guid.NewGuid(), Policy(), [allocation], new Dictionary<uint, uint> { [100] = 5 }));
        Assert.Equal(TransientVendorStartResult.PurchasingDisabled, execution.Start(Session,
            ResolvedVendorPurchasePolicy.Resolve(new()), [allocation], new Dictionary<uint, uint> { [100] = 5 }));
        Assert.False(port.Acquired);
    }

    [Fact]
    public void UnavailablePortDoesNotTouchOrReleaseOtherWork()
    {
        var port = new FakePort { Available = false };
        var execution = new TransientVendorExecution(port);
        Assert.Equal(TransientVendorStartResult.Unavailable, execution.Start(Session, Policy(), [Allocation()], new Dictionary<uint, uint> { [100] = 5 }));
        Assert.False(port.Acquired);
        Assert.False(port.Released);
        Assert.Empty(port.Requests);
    }

    [Fact]
    public void FinishedSessionIdCannotBeReused()
    {
        var port = new FakePort();
        var execution = Start(port);
        execution.Tick(Now);
        Settle(execution, port, 5, 50);
        Assert.Equal(TransientVendorStartResult.InvalidBatch, execution.Start(Session, Policy(), [Allocation()],
            new Dictionary<uint, uint> { [100] = 10 }));
        Assert.Single(port.Requests);
    }

    private sealed class FakePort : ITransientVendorPort
    {
        public bool Available = true, Safe = true, CleanupReady = true, Acquired, Released;
        public TransientPurchaseStart StartResult = TransientPurchaseStart.Started;
        public Dictionary<uint, uint> Inventory { get; } = new();
        public List<(VendorPurchaseAllocation Allocation, uint Quantity, TransientPurchaseBudget Budget)> Requests { get; } = new();
        public Queue<TransientPurchaseReceipt> Receipts { get; } = new();
        public bool TryAcquire(Guid sessionId, out string reason) { reason = "Ownership"; return Acquired = Available; }
        public bool IsSafe(out string reason) { reason = "Safety"; return Safe; }
        public uint ReadItemCount(uint itemId) => Inventory.GetValueOrDefault(itemId);
        public TransientPurchaseStart TryStart(Guid sessionId, VendorPurchaseAllocation allocation, uint quantity,
            uint inventoryTarget, TransientPurchaseBudget budget, out string reason)
        {
            Requests.Add((allocation, quantity, budget));
            reason = "Started";
            return StartResult;
        }
        public TransientPurchaseReceipt? Poll(Guid sessionId) => Receipts.TryDequeue(out var receipt) ? receipt : null;
        public void Pause(Guid sessionId) { }
        public void Cancel(Guid sessionId) { }
        public bool TryCleanup(Guid sessionId) => CleanupReady;
        public void Release(Guid sessionId) => Released = true;
    }
}
