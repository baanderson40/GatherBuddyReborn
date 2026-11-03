using GatherBuddy.Crafting;
using GatherBuddy.Utility;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class CraftPreparationGateTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ClearReadinessMustRemainStableFor500Milliseconds()
    {
        var gate = new CraftPreparationGate();
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(0), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(.499), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(.5), CraftPreparationBlocker.None));
        // Last-moment checks are repeatable, not a one-shot permission token.
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(.5), CraftPreparationBlocker.None));
    }

    [Theory]
    [InlineData(CraftPreparationBlocker.LifestreamBusy)]
    [InlineData(CraftPreparationBlocker.NavigationBusy)]
    [InlineData(CraftPreparationBlocker.TravelStatusUnavailable)]
    public void TravelBlockersPreventActionsEvenWithScreenInventoryAndCorrectJobReady(CraftPreparationBlocker blocker)
    {
        var gate = new CraftPreparationGate();
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(0), blocker));
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(10), blocker));
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(11), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(11.5), CraftPreparationBlocker.None));
    }

    [Fact]
    public void BriefReadyGapBetweenTravelStagesCannotAdmitNativeSubmission()
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.LifestreamBusy);
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(1), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(1.4), CraftPreparationBlocker.NavigationBusy));
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(2), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Stabilizing, gate.Tick(S(2.49), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(2.5), CraftPreparationBlocker.None));
    }

    [Fact]
    public void QueuedActionMustRecheckIfTravelStartsAfterEarlierAdmission()
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.None);
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(.5), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(.501), CraftPreparationBlocker.LifestreamBusy));
    }

    [Fact]
    public void TravelWaitTimesOutSeparatelyFromInventoryRecovery()
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.NavigationBusy);
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(119), CraftPreparationBlocker.NavigationBusy));
        Assert.Equal(CraftPreparationAdmission.TimedOut, gate.Tick(S(120), CraftPreparationBlocker.NavigationBusy));
        Assert.True(gate.TimedOut);
        gate.Pause(S(120));
        gate.Resume(S(1000)); // Explicit Resume starts a new timeout budget.
        Assert.False(gate.TimedOut);
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(1000), CraftPreparationBlocker.NavigationBusy));
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(1119), CraftPreparationBlocker.NavigationBusy));
    }

    [Fact]
    public void PausedTimeDoesNotConsumeTravelTimeoutOrAuthorizeActions()
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.LifestreamBusy);
        gate.Tick(S(10), CraftPreparationBlocker.LifestreamBusy);
        gate.Pause(S(10));
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(1000), CraftPreparationBlocker.None));
        gate.Resume(S(1001));
        gate.Tick(S(1001), CraftPreparationBlocker.LifestreamBusy);
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(1110), CraftPreparationBlocker.LifestreamBusy));
        Assert.Equal(CraftPreparationAdmission.TimedOut, gate.Tick(S(1111), CraftPreparationBlocker.LifestreamBusy));
    }

    [Theory]
    [InlineData(CraftPreparationBlocker.ZoneTransition)]
    [InlineData(CraftPreparationBlocker.LoadingScreen)]
    [InlineData(CraftPreparationBlocker.PlayerInventoryUnavailable)]
    public void InventoryRecoverySuspendsRatherThanResetsTravelBudget(CraftPreparationBlocker inventoryBlocker)
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.LifestreamBusy);
        gate.Tick(S(10), CraftPreparationBlocker.LifestreamBusy);
        gate.Tick(S(11), inventoryBlocker);
        gate.Tick(S(500), inventoryBlocker);
        gate.Tick(S(501), CraftPreparationBlocker.LifestreamBusy);
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(610), CraftPreparationBlocker.LifestreamBusy));
        Assert.Equal(CraftPreparationAdmission.TimedOut, gate.Tick(S(611), CraftPreparationBlocker.LifestreamBusy));
    }

    [Fact]
    public void ExplicitInventorySuspensionExcludesUnpolledLoadingTime()
    {
        var gate = new CraftPreparationGate();
        gate.Tick(S(0), CraftPreparationBlocker.NavigationBusy);
        gate.Tick(S(10), CraftPreparationBlocker.NavigationBusy);
        gate.Suspend(S(10));
        gate.Tick(S(500), CraftPreparationBlocker.NavigationBusy);
        Assert.Equal(CraftPreparationAdmission.Blocked, gate.Tick(S(609), CraftPreparationBlocker.NavigationBusy));
        Assert.Equal(CraftPreparationAdmission.TimedOut, gate.Tick(S(610), CraftPreparationBlocker.NavigationBusy));
    }

    [Fact]
    public void CancelRejectsLateContinuationAndNewRunUsesFreshGate()
    {
        var old = new CraftPreparationGate();
        old.Tick(S(0), CraftPreparationBlocker.LifestreamBusy);
        old.Cancel();
        old.Resume(S(1));
        Assert.Equal(CraftPreparationAdmission.Cancelled, old.Tick(S(2), CraftPreparationBlocker.None));
        var next = new CraftPreparationGate();
        Assert.Equal(CraftPreparationAdmission.Stabilizing, next.Tick(S(2), CraftPreparationBlocker.None));
        Assert.Equal(CraftPreparationAdmission.Ready, next.Tick(S(2.5), CraftPreparationBlocker.None));
    }

    [Fact]
    public void SchedulingAndBlockedChecksDoNotConsumeEquipAttempts()
    {
        var guard = new JobSwitchGuard();
        for (var i = 0; i < 1000; i++) Assert.True(guard.ShouldSubmit(9, S(i)));
        Assert.False(guard.TimedOut);
        guard.MarkSubmitted(S(1000));
        Assert.False(guard.ShouldSubmit(9, S(1001)));
        Assert.True(guard.ShouldSubmit(9, S(1002)));
        guard.MarkSubmitted(S(1002));
        Assert.True(guard.ShouldSubmit(9, S(1004)));
        guard.MarkSubmitted(S(1004));
        Assert.False(guard.ShouldSubmit(9, S(1006)));
        Assert.True(guard.TimedOut);
    }

    [Fact]
    public void ZoneRecoveryDoesNotResetExistingEquipAttemptCount()
    {
        var guard = new JobSwitchGuard();
        var gate = new CraftPreparationGate();
        Assert.True(guard.ShouldSubmit(9, S(0)));
        guard.MarkSubmitted(S(0));
        gate.Tick(S(1), CraftPreparationBlocker.ZoneTransition);
        gate.Tick(S(20), CraftPreparationBlocker.None);
        Assert.Equal(CraftPreparationAdmission.Ready, gate.Tick(S(20.5), CraftPreparationBlocker.None));
        Assert.True(guard.ShouldSubmit(9, S(20.5)));
        guard.MarkSubmitted(S(20.5));
        Assert.True(guard.ShouldSubmit(9, S(22.5)));
        guard.MarkSubmitted(S(22.5));
        Assert.False(guard.ShouldSubmit(9, S(24.5)));
    }

    private sealed class Clock : TimeProvider
    {
        public long Stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Stamp;
    }

    [Fact]
    public void IdenticalBlockerLogsAreBoundedAndReasonChangesEmitImmediately()
    {
        var clock = new Clock();
        var throttle = new DiagnosticThrottle<string>(S(5), capacity: 8, clock: clock);
        Assert.True(throttle.TryLog("PreparationWait", "8", out _));
        for (var i = 0; i < 149; i++) Assert.False(throttle.TryLog("PreparationWait", "8", out _));
        clock.Stamp = S(5).Ticks;
        Assert.True(throttle.TryLog("PreparationWait", "8", out var suppressed));
        Assert.Equal(149, suppressed);
        Assert.True(throttle.TryLog("PreparationWait", "16", out _));
        Assert.True(throttle.TryLog("PreparationWait", "24", out _));
        Assert.True(throttle.TryLog("PreparationWait", "0", out _));
    }
}
