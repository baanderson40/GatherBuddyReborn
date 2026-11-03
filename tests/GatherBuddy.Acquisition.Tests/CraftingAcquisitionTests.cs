using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class CraftingAcquisitionTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Session = Guid.NewGuid();
    private static ResolvedVendorPurchasePolicy Policy(bool enabled = true)
        => ResolvedVendorPurchasePolicy.Resolve(new() { Enabled = enabled });
    private static CraftingAcquisitionCoordinator Run(Port port, ResolvedVendorPurchasePolicy? policy = null)
        => new(Session, policy ?? Policy(), port, Now);

    [Fact]
    public void CurrentInventoryAfterWithdrawalReducesPurchaseAndAbsoluteTarget()
    {
        var port = new Port();
        port.Inventory[100] = new(3, 1);
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(6u, port.StartPlan!.Allocations.Single().RequestedOutputQuantity);
        Assert.Equal(10u, port.Targets![100]);
    }

    [Fact]
    public void OwnedMaterialsNeedNeitherPurchasingNorGathering()
    {
        var port = new Port();
        port.Inventory[100] = new(10, 0);
        var run = Run(port);
        run.Tick(Now);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(0, port.Starts);
        Assert.Empty(run.BuildGatherTargets());
    }

    [Fact]
    public void CleanupMustFinishBeforeFreshReconciliation()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(CraftingAcquisitionState.Purchasing, run.State);
        port.State = TransientVendorState.CleaningUp;
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.WaitingForCleanup, run.State);
        Assert.Null(run.FinalPlan);
        Assert.Equal(1, port.Captures);
        port.Inventory[100] = new(10, 0);
        port.Complete();
        run.Tick(Now.AddSeconds(2));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Empty(run.BuildGatherTargets());
        Assert.Equal(1, port.Starts);
        Assert.False(port.CapturePurchasing.Last());
    }

    [Fact]
    public void PartialPurchaseGathersOnlyRemainingNQWithoutDoubleDeduction()
    {
        var port = new Port();
        port.Inventory[100] = new(2, 3);
        var run = Run(port);
        run.Tick(Now);
        port.Inventory[100] = new(4, 3);
        port.Complete(MaterialAcquisitionCompletion.PartiallyFulfilled);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(3, run.FinalPlan!.Materials.Single().GatheringDemand.Total);
        Assert.Equal(7, run.BuildGatherTargets()[100]); // NQ count 4 + residual 3, not total demand 10.
        Assert.Equal(1, port.Starts);
        run.Tick(Now.AddSeconds(10));
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void NonGatherableShortageDoesNotSuppressUnrelatedGathering()
    {
        var port = new Port { Balance = 0 };
        port.Demands.Add(new(101, IngredientQualityDemand.FromPreferNQ(3), CanGatherNQ: false));
        var run = Run(port);
        run.Tick(Now);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(10, run.BuildGatherTargets()[100]);
        Assert.False(run.BuildGatherTargets().ContainsKey(101));
        Assert.Equal(3, run.FinalPlan!.Materials.Single(m => m.ItemId == 101).UnresolvedDemand.Total);
    }

    [Fact]
    public void MandatoryHQRemainsUnresolvedRatherThanPurchasedOrGatheredAsNQ()
    {
        var port = new Port();
        port.Demands.Clear();
        port.Demands.Add(new(100, IngredientQualityDemand.FromRequiredHQ(4, 10), CanGatherNQ: true));
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(6u, port.StartPlan!.Allocations.Single().RequestedOutputQuantity);
        port.Inventory[100] = new(6, 0);
        port.Complete();
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(4, run.FinalPlan!.Materials.Single().UnresolvedDemand.RequiredHQ);
        Assert.Empty(run.BuildGatherTargets());
    }

    [Fact]
    public void LoadingVendorDataWaitsThenFallsBackWithinBound()
    {
        var port = new Port { DataStatus = VendorOfferDataStatus.Loading };
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(CraftingAcquisitionState.WaitingForVendorData, run.State);
        run.Tick(Now.AddSeconds(30));
        run.Tick(Now.AddSeconds(31));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(0, port.Starts);
        Assert.Equal(10, run.BuildGatherTargets()[100]);
        Assert.Contains("timed out", run.PurchaseFailureReason);
    }

    [Fact]
    public void DisabledPolicyDoesNotWaitForVendorCachesOrPurchase()
    {
        var port = new Port { DataStatus = VendorOfferDataStatus.Loading };
        var run = Run(port, Policy(false));
        run.Tick(Now);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(0, port.Starts);
    }

    [Fact]
    public void MissingInventoryFailsWithoutGatherTransition()
    {
        var port = new Port { InventoryUnavailable = true };
        var run = Run(port);
        run.Tick(Now);
        run.Tick(Now.AddSeconds(30));
        Assert.Equal(CraftingAcquisitionState.Failed, run.State);
        Assert.Null(run.FinalPlan);
    }

    [Theory]
    [InlineData(TransientVendorStartResult.Unavailable)]
    [InlineData(TransientVendorStartResult.Busy)]
    public void UnavailableExecutionWaitsWithoutHijackingAndCannotAdvanceWhileOtherWorkActive(TransientVendorStartResult start)
    {
        var port = new Port { StartResult = start, Safe = false };
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(CraftingAcquisitionState.WaitingForVendorData, run.State);
        Assert.Equal(0, port.Cancels);
        run.Tick(Now.AddSeconds(30));
        run.Tick(Now.AddSeconds(31));
        Assert.Equal(CraftingAcquisitionState.Failed, run.State);
        Assert.Null(run.FinalPlan);
        Assert.Equal(0, port.Cancels);
    }

    [Fact]
    public void SafeUnavailableExecutorCanFallBackWithoutRepeatedRoutes()
    {
        var port = new Port { StartResult = TransientVendorStartResult.Unavailable };
        var run = Run(port);
        run.Tick(Now);
        run.Tick(Now.AddSeconds(30));
        run.Tick(Now.AddSeconds(31));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(1, port.Starts);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UncertainAccountingOrCleanupNeverAdvances(bool certain, bool cleanup)
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        port.Complete(certain: certain, cleanup: cleanup);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.Failed, run.State);
        Assert.Null(run.FinalPlan);
    }

    [Fact]
    public void OrdinaryFailedPurchaseCanContinueSafeGathering()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        port.Complete(MaterialAcquisitionCompletion.Failed);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
        Assert.Equal(10, run.BuildGatherTargets()[100]);
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void StaleReceiptFailsClosed()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        port.Complete(session: Guid.NewGuid());
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.Failed, run.State);
        Assert.Null(run.FinalPlan);
    }

    [Fact]
    public void PauseResumePreservesWaitBudgetAndDoesNotSubmitWhilePaused()
    {
        var port = new Port { DataStatus = VendorOfferDataStatus.Loading };
        var run = Run(port);
        run.Tick(Now);
        run.Pause(Now.AddSeconds(1));
        run.Tick(Now.AddSeconds(100));
        Assert.Equal(1, port.Captures);
        run.Resume(Now.AddSeconds(101));
        port.DataStatus = VendorOfferDataStatus.Ready;
        run.Tick(Now.AddSeconds(102));
        Assert.Equal(CraftingAcquisitionState.Purchasing, run.State);
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void PauseSubmittedWorkForwardsButDoesNotReconcileUntilResume()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        run.Pause(Now.AddSeconds(1));
        port.Complete();
        run.Tick(Now.AddSeconds(10));
        Assert.Equal(1, port.Pauses);
        Assert.Null(run.FinalPlan);
        run.Resume(Now.AddSeconds(11));
        run.Tick(Now.AddSeconds(12));
        Assert.Equal(1, port.Resumes);
        Assert.Equal(CraftingAcquisitionState.ReadyForGather, run.State);
    }

    [Fact]
    public void CancelBeforePurchaseDoesNotTouchUnrelatedExecution()
    {
        var port = new Port();
        var run = Run(port);
        run.Cancel();
        run.Tick(Now);
        Assert.Equal(CraftingAcquisitionState.Cancelled, run.State);
        Assert.Equal(0, port.Captures);
        Assert.Equal(0, port.Cancels);
    }

    [Fact]
    public void CancelledSessionCannotAdvanceFromLateCompletion()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        run.Cancel();
        port.Complete();
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(1, port.Cancels);
        Assert.Equal(CraftingAcquisitionState.Cancelled, run.State);
        Assert.Null(run.FinalPlan);
    }

    [Fact]
    public void ExecutorCancellationDoesNotStartGathering()
    {
        var port = new Port();
        var run = Run(port);
        run.Tick(Now);
        port.Complete(MaterialAcquisitionCompletion.Cancelled);
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.Cancelled, run.State);
        Assert.Null(run.FinalPlan);
    }

    [Fact]
    public void SplitCurrencyTargetsAreAggregatedOnce()
    {
        var port = new Port { Balance = 30 };
        port.SealBalance = 100;
        port.Inventory[100] = new(2, 0);
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(2, port.StartPlan!.Allocations.Count);
        Assert.Equal(10u, port.Targets![100]);
        Assert.Equal(new uint[] { 3, 5 }, port.StartPlan.Allocations.Select(a => a.RequestedOutputQuantity));
    }

    [Fact]
    public void RunKeepsPolicySnapshotAcrossSettingsEditsAndDataWait()
    {
        var settings = new AutomaticVendorPurchasePolicy { Enabled = true, MaximumGilSpendPerRun = 40 };
        var port = new Port { DataStatus = VendorOfferDataStatus.Loading };
        var run = Run(port, ResolvedVendorPurchasePolicy.Resolve(settings));
        run.Tick(Now);
        settings.Enabled = false;
        settings.MaximumGilSpendPerRun = 0;
        settings.CurrencyReservesByItemId[1] = 100;
        port.DataStatus = VendorOfferDataStatus.Ready;
        run.Tick(Now.AddSeconds(1));
        Assert.Equal(CraftingAcquisitionState.Purchasing, run.State);
        Assert.Equal(4u, port.StartPlan!.Allocations.Single().RequestedOutputQuantity);
    }

    [Fact]
    public void CaptureIsThrottledAndCleanupBlockedDoesNotAdvance()
    {
        var port = new Port { DataStatus = VendorOfferDataStatus.Loading };
        var run = Run(port);
        run.Tick(Now);
        run.Tick(Now.AddMilliseconds(100));
        Assert.Equal(1, port.Captures);
        port.DataStatus = VendorOfferDataStatus.Ready;
        run.Tick(Now.AddSeconds(1));
        port.State = TransientVendorState.CleanupBlocked;
        run.Tick(Now.AddMinutes(1));
        Assert.Equal(CraftingAcquisitionState.WaitingForCleanup, run.State);
        Assert.Null(run.FinalPlan);
        Assert.Equal(2, port.Captures);
    }

    [Fact]
    public void InvalidBatchStopsRatherThanFallingThroughToCrafting()
    {
        var port = new Port { StartResult = TransientVendorStartResult.InvalidBatch };
        var run = Run(port);
        run.Tick(Now);
        Assert.Equal(CraftingAcquisitionState.Failed, run.State);
        Assert.Null(run.FinalPlan);
    }

    private sealed class Port : ICraftingAcquisitionPort
    {
        public List<AcquisitionMaterialDemand> Demands = [new(100, IngredientQualityDemand.FromPreferNQ(10), CanGatherNQ: true)];
        public Dictionary<uint, AcquisitionInventory> Inventory = new();
        public ulong Balance = 100, SealBalance;
        public bool InventoryUnavailable, Safe = true;
        public VendorOfferDataStatus DataStatus = VendorOfferDataStatus.Ready;
        public int Captures, Starts, Pauses, Resumes, Cancels;
        public List<bool> CapturePurchasing = new();
        public Dictionary<uint, uint>? Targets;
        public MaterialAcquisitionPlan? StartPlan;
        public TransientVendorStartResult StartResult = TransientVendorStartResult.Started;
        public TransientVendorState State = TransientVendorState.Idle;
        public TransientVendorResult? Result;
        public TransientVendorState PurchaseState => State;
        public TransientVendorResult? PurchaseResult => Result;
        public string PurchaseStatus => "Fake purchase status";
        public MaterialAcquisitionPlanningInput? Capture(Guid session, ResolvedVendorPurchasePolicy policy, bool allowPurchasing)
        {
            Captures++;
            CapturePurchasing.Add(allowPurchasing);
            if (InventoryUnavailable) return null;
            return new(session, policy, Demands.Select(d => d with { AllowPurchasing = allowPurchasing }),
                [new(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop, new(10, "Vendor", 20)),
                 new(100, 20, VendorCurrencyGroup.GrandCompanySeals, 10, 1, VendorShopType.GrandCompanySeals, new(11, "GC", 21))],
                Inventory, new Dictionary<uint, ulong> { [1] = Balance, [20] = SealBalance },
                new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), allowPurchasing ? DataStatus : VendorOfferDataStatus.Ready);
        }
        public TransientVendorStartResult StartPurchases(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan, Dictionary<uint, uint> inventoryTargets)
        { Starts++; Targets = inventoryTargets; StartPlan = plan; if (StartResult == TransientVendorStartResult.Started) State = TransientVendorState.Purchasing; return StartResult; }
        public void Complete(MaterialAcquisitionCompletion completion = MaterialAcquisitionCompletion.Completed,
            bool certain = true, bool cleanup = true, Guid? session = null)
        {
            Result = new(session ?? Session, completion, Array.Empty<TransientAllocationResult>(), new Dictionary<uint, ulong>(), cleanup, certain);
            State = TransientVendorState.Finished;
        }
        public bool CanContinue(out string reason) { reason = "Other work active"; return Safe; }
        public void Pause(Guid session) => Pauses++;
        public void Resume(Guid session) => Resumes++;
        public void Cancel(Guid session) => Cancels++;
    }
}
