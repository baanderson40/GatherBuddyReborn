using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class InventoryRecoveryTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void LongApartmentLoadingDoesNotConsumeRecoveryWindow()
    {
        var wait = new InventoryRecovery(S(0));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(0), PlayerInventoryAvailability.Transitioning));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(600), PlayerInventoryAvailability.Transitioning));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(601), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(630), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.TimedOut, wait.Tick(S(631), PlayerInventoryAvailability.Unavailable));
    }

    [Fact]
    public void PostTransitionRecoveryProducesReadyOnlyWithValidInventory()
    {
        var wait = new InventoryRecovery(S(0));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(0), PlayerInventoryAvailability.Transitioning));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(10), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.Ready, wait.Tick(S(11), PlayerInventoryAvailability.Ready));
        wait.Cancel(); // Consuming the continuation invalidates further delivery.
        Assert.Equal(InventoryRecoveryResult.Cancelled, wait.Tick(S(12), PlayerInventoryAvailability.Ready));
    }

    [Fact]
    public void MissingPlayerOrManagerAfterLoadingTimesOutRatherThanCompleting()
    {
        var wait = new InventoryRecovery(S(0));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(0), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(29.9), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.TimedOut, wait.Tick(S(30), PlayerInventoryAvailability.Unavailable));
    }

    [Fact]
    public void RepeatedTransitionsSuspendButDoNotResetRecoveryBudget()
    {
        var wait = new InventoryRecovery(S(0));
        wait.Tick(S(0), PlayerInventoryAvailability.Unavailable);
        wait.Tick(S(10), PlayerInventoryAvailability.Unavailable);
        wait.Tick(S(11), PlayerInventoryAvailability.Transitioning);
        wait.Tick(S(500), PlayerInventoryAvailability.Transitioning);
        wait.Tick(S(501), PlayerInventoryAvailability.Unavailable);
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(520), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.TimedOut, wait.Tick(S(521), PlayerInventoryAvailability.Unavailable));
    }

    [Fact]
    public void PauseTimeDoesNotConsumeRecoveryBudgetOrDeliverContinuation()
    {
        var wait = new InventoryRecovery(S(0));
        wait.Tick(S(0), PlayerInventoryAvailability.Unavailable);
        wait.Tick(S(10), PlayerInventoryAvailability.Unavailable);
        wait.Pause(S(10));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(1000), PlayerInventoryAvailability.Ready));
        wait.Resume(S(1001));
        wait.Tick(S(1001), PlayerInventoryAvailability.Unavailable);
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(1020), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.TimedOut, wait.Tick(S(1021), PlayerInventoryAvailability.Unavailable));
    }

    [Fact]
    public void TimeoutCanBeExplicitlyRetriedWithFreshRecoveryBudget()
    {
        var old = new InventoryRecovery(S(0));
        old.Tick(S(0), PlayerInventoryAvailability.Unavailable);
        Assert.Equal(InventoryRecoveryResult.TimedOut, old.Tick(S(30), PlayerInventoryAvailability.Unavailable));
        old.Cancel();
        var retry = new InventoryRecovery(S(100));
        Assert.Equal(InventoryRecoveryResult.Waiting, retry.Tick(S(100), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.Ready, retry.Tick(S(101), PlayerInventoryAvailability.Ready));
        Assert.Equal(InventoryRecoveryResult.Cancelled, old.Tick(S(101), PlayerInventoryAvailability.Ready));
    }

    [Fact]
    public void ReadyPollingIsBounded()
    {
        var wait = new InventoryRecovery(S(0));
        Assert.Equal(InventoryRecoveryResult.Ready, wait.Tick(S(0), PlayerInventoryAvailability.Ready));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(.1), PlayerInventoryAvailability.Ready));
        Assert.Equal(InventoryRecoveryResult.Ready, wait.Tick(S(.5), PlayerInventoryAvailability.Ready));
    }

    [Fact]
    public void CancelAndResetRejectLateReadySignalsEvenAfterResume()
    {
        var cancelled = new InventoryRecovery(S(0));
        cancelled.Cancel();
        cancelled.Resume(S(1));
        Assert.Equal(InventoryRecoveryResult.Cancelled, cancelled.Tick(S(2), PlayerInventoryAvailability.Ready));
        var nextRun = new InventoryRecovery(S(2));
        Assert.Equal(InventoryRecoveryResult.Ready, nextRun.Tick(S(2), PlayerInventoryAvailability.Ready));
    }

    [Theory]
    [InlineData(PlayerInventoryAvailability.Transitioning, true, true, false, GatherHandoffDecision.WaitForInventory)]
    [InlineData(PlayerInventoryAvailability.Unavailable, true, true, false, GatherHandoffDecision.WaitForInventory)]
    [InlineData(PlayerInventoryAvailability.Ready, true, true, false, GatherHandoffDecision.ReadyToCraft)]
    [InlineData(PlayerInventoryAvailability.Ready, true, false, false, GatherHandoffDecision.PauseGathering)]
    [InlineData(PlayerInventoryAvailability.Ready, true, false, true, GatherHandoffDecision.ContinueGathering)]
    [InlineData(PlayerInventoryAvailability.Ready, false, false, false, GatherHandoffDecision.PauseGathering)]
    [InlineData(PlayerInventoryAvailability.Ready, false, true, false, GatherHandoffDecision.PauseGathering)]
    public void HandoffDoesNotConfuseUnavailableOrDeletedTargetsWithCompletion(PlayerInventoryAvailability availability,
        bool known, bool satisfied, bool enabled, GatherHandoffDecision expected)
        => Assert.Equal(expected, GatherHandoff.Evaluate(availability, known, satisfied, enabled));

    [Fact]
    public void DeletingTemporaryListDataCannotEraseRetainedTargets()
    {
        var listData = new Dictionary<uint, uint> { [10] = 5 };
        var retained = new RetainedGatherTargets(listData);
        listData.Clear();
        Assert.Equal(5u, retained[10]);
        Assert.False(retained.SatisfiedBy(new Dictionary<uint, AcquisitionInventory>()));
        Assert.False(retained.SatisfiedBy(new Dictionary<uint, AcquisitionInventory> { [10] = new(0, 5) }));
        Assert.True(retained.SatisfiedBy(new Dictionary<uint, AcquisitionInventory> { [10] = new(5, 0) }));
    }

    [Fact]
    public void PreCraftRecoveryUsesChangedInventoryInsteadOfOldReadiness()
    {
        var craft = new PendingCraft(1, 100, 1, new Dictionary<uint, IngredientQualityDemand>
            { [10] = IngredientQualityDemand.FromPreferNQ(5) });
        Assert.Empty(CraftReadiness.Missing(craft, new Dictionary<uint, AcquisitionInventory> { [10] = new(5, 0) }));
        var wait = new InventoryRecovery(S(0));
        wait.Tick(S(0), PlayerInventoryAvailability.Transitioning);
        Assert.Equal(InventoryRecoveryResult.Ready, wait.Tick(S(90), PlayerInventoryAvailability.Ready));
        Assert.Equal(5, CraftReadiness.Missing(craft, new Dictionary<uint, AcquisitionInventory>())[10].Total);
    }

    [Fact]
    public void OutputBaselineSurvivesRecoveryWithoutCreditDuringUnavailability()
    {
        var before = 10;
        var wait = new InventoryRecovery(S(0));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(0), PlayerInventoryAvailability.Transitioning));
        Assert.Equal(InventoryRecoveryResult.Waiting, wait.Tick(S(100), PlayerInventoryAvailability.Unavailable));
        Assert.Equal(InventoryRecoveryResult.Ready, wait.Tick(S(101), PlayerInventoryAvailability.Ready));
        Assert.Equal(1, CraftReadiness.ConfirmedCrafts(before, 13, 3, 2));
        wait.Cancel();
        Assert.Equal(InventoryRecoveryResult.Cancelled, wait.Tick(S(102), PlayerInventoryAvailability.Ready));
    }
}
