using GatherBuddy.Automation;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class RetainerWithdrawalGuardTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private sealed class Port : IRetainerWithdrawalPort
    {
        public bool Available { get; set; } = true;
        private bool _busy;
        public bool ThrowBusy;
        public bool Busy { get { if (ThrowBusy) throw new InvalidOperationException("busy IPC missing"); return _busy; } set => _busy = value; }
        public bool MultiMode { get; set; }
        public bool Suppressed { get; set; }
        public bool PendingWork { get; set; }
        public bool Ready { get; set; } = true;
        public ulong CharacterId { get; set; } = 42;
        public bool InteractionClosed { get; set; } = true;
        public List<bool> Writes { get; } = new();
        public int Closes;
        public Action<bool>? DuringSet;
        public void SetSuppressed(bool value)
        {
            Writes.Add(value);
            Suppressed = value;
            DuringSet?.Invoke(value);
        }
        public void CloseOwnedInteraction() => Closes++;
    }

    private static RetainerWithdrawalGuard Acquire(Port port)
    {
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.True(guard.TryAcquire(Owner, 500));
        Assert.Equal(Owner, guard.Owner);
        return guard;
    }

    [Fact]
    public void PendingWorkWinsBeforeSuppressionOrBellOwnership()
    {
        var port = new Port { PendingWork = true };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 10000));
        Assert.Null(guard.Owner);
        Assert.Empty(port.Writes);
        port.PendingWork = false;
        Assert.False(guard.TryAcquire(Owner, 11000));
        Assert.True(guard.TryAcquire(Owner, 11500));
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExternalBusyOrMultiModeCannotBeSuppressed(bool busy, bool multi)
    {
        var port = new Port { Busy = busy, MultiMode = multi };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 1000));
        Assert.Null(guard.Owner);
        Assert.Empty(port.Writes);
        Assert.Equal(0, port.Closes);
    }

    [Fact]
    public void ExistingSuppressionIsBorrowedNotClearedAndPendingSchedulingIsNotForced()
    {
        var port = new Port { Suppressed = true, PendingWork = true };
        var guard = Acquire(port);
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(1500);
        Assert.Null(guard.Owner);
        Assert.True(port.Suppressed);
        Assert.Empty(port.Writes);
    }

    [Fact]
    public void ReadyWorkAppearingInsideOwnedBellSessionWaitsUntilCompletion()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.PendingWork = true;
        port.InteractionClosed = false;
        Assert.True(guard.TryAcquire(Owner, 1000));
        Assert.Equal(new[] { true }, port.Writes);
        Assert.False(guard.Releasing);
    }

    [Fact]
    public void ReleaseKeepsSuppressionUntilBellClosedAndStable()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.InteractionClosed = false;
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(10000);
        Assert.Equal(Owner, guard.Owner);
        Assert.True(port.Suppressed);
        Assert.Equal(2, port.Closes);
        port.InteractionClosed = true;
        guard.Tick(11000);
        Assert.True(port.Suppressed);
        guard.Tick(11499);
        Assert.True(port.Suppressed);
        guard.Tick(11500);
        Assert.False(port.Suppressed);
        Assert.Null(guard.Owner);
        Assert.Equal(new[] { true, false }, port.Writes);
    }

    [Fact]
    public void RepeatedReleaseRequestsDoNotRestartCleanupDeadline()
    {
        var port = new Port();
        var guard = Acquire(port);
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.RequestRelease(Owner);
        guard.Tick(1499);
        guard.RequestRelease(Owner);
        guard.Tick(1500);
        Assert.Null(guard.Owner);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void CleanupDoesNotTouchExternalTasksOrWriteDuringLoading(bool busy, bool multi, bool ready)
    {
        var port = new Port();
        var guard = Acquire(port);
        port.Busy = busy;
        port.MultiMode = multi;
        port.Ready = ready;
        port.InteractionClosed = false;
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(10000);
        Assert.Equal(Owner, guard.Owner);
        Assert.Equal(0, port.Closes);
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Fact]
    public void ForeignQueueCannotAcquireOrReleaseOwnedSuppression()
    {
        var port = new Port();
        var guard = Acquire(port);
        var other = Guid.NewGuid();
        Assert.False(guard.TryAcquire(other, 1000));
        guard.RequestRelease(other);
        guard.Tick(2000);
        Assert.Equal(Owner, guard.Owner);
        Assert.False(guard.Releasing);
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Fact]
    public void ExternalSuppressionClearFaultsInsteadOfReassertingIt()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.Suppressed = false;
        Assert.False(guard.TryAcquire(Owner, 1000));
        Assert.True(guard.Faulted);
        Assert.Equal(new[] { true }, port.Writes);
        guard.RequestRelease(Owner);
        guard.Tick(1100);
        guard.Tick(1600);
        Assert.Null(guard.Owner);
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Fact]
    public void AmbiguousThrowAfterSuppressionWriteRetainsCleanupOwnership()
    {
        var port = new Port { DuringSet = _ => throw new InvalidOperationException("IPC threw after writing") };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 500));
        Assert.Equal(Owner, guard.Owner);
        Assert.True(guard.Faulted);
        Assert.True(port.Suppressed);
        port.DuringSet = null;
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(1500);
        Assert.Null(guard.Owner);
        Assert.False(port.Suppressed);
    }

    [Fact]
    public void RestoreFailureKeepsOwnershipAndCanRetryWithoutWithdrawalRestart()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.DuringSet = value => { if (!value) { port.Suppressed = true; throw new InvalidOperationException("restore failed"); } };
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(1500);
        Assert.Equal(Owner, guard.Owner);
        Assert.True(guard.Releasing);
        Assert.False(guard.TryAcquire(Owner, 1600));
        port.DuringSet = null;
        guard.Tick(2000);
        Assert.Null(guard.Owner);
    }

    [Fact]
    public void ArStartingDuringSuppressionAcquisitionPreventsBellWork()
    {
        var port = new Port();
        port.DuringSet = _ => port.Busy = true;
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 500));
        Assert.True(guard.Faulted);
        Assert.Equal(Owner, guard.Owner);
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        Assert.Equal(0, port.Closes);
    }

    [Fact]
    public void ArAbsentNeedsNoSuppressionButLateLoadRequiresReconciliation()
    {
        var port = new Port { Available = false };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.True(guard.TryAcquire(Owner, 0));
        Assert.Empty(port.Writes);
        port.Available = true;
        Assert.False(guard.TryAcquire(Owner, 1000));
        Assert.True(guard.Faulted);
        Assert.Empty(port.Writes);
    }

    [Fact]
    public void ArUnloadDoesNotStrandClosedBellLeaseOrAttemptUnavailableIpc()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.Available = false;
        guard.RequestRelease(Owner);
        guard.Tick(1000);
        guard.Tick(1500);
        Assert.Null(guard.Owner);
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnloadMayRestoreOnlyAfterInteractionIsClosed(bool closed)
    {
        var port = new Port();
        var guard = Acquire(port);
        port.InteractionClosed = closed;
        guard.RequestRelease(Owner);
        guard.Tick(1000, unloading: true);
        Assert.Equal(closed, guard.Owner == null);
        Assert.Equal(!closed, port.Suppressed);
    }

    [Fact]
    public void CharacterChangePausesAndNeverClosesForeignInteraction()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.CharacterId = 99;
        port.InteractionClosed = false;
        Assert.False(guard.TryAcquire(Owner, 1000));
        Assert.True(guard.Faulted);
        guard.RequestRelease(Owner);
        guard.Tick(1100);
        Assert.Equal(0, port.Closes);
        Assert.Equal(Owner, guard.Owner);
        port.InteractionClosed = true;
        guard.Tick(2000);
        guard.Tick(2500);
        Assert.Null(guard.Owner);
        Assert.False(port.Suppressed);
    }

    [Fact]
    public void MissingBusyIpcFailsClosedWithoutSuppressionOrOwnership()
    {
        var port = new Port { ThrowBusy = true };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 10000));
        Assert.Null(guard.Owner);
        Assert.Empty(port.Writes);
        Assert.Contains("IPC", guard.Status);
    }

    [Fact]
    public void ReadinessLossRestartsPreWithdrawalIdleStability()
    {
        var port = new Port();
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        port.Ready = false;
        Assert.False(guard.TryAcquire(Owner, 400));
        port.Ready = true;
        Assert.False(guard.TryAcquire(Owner, 600));
        Assert.False(guard.TryAcquire(Owner, 1099));
        Assert.True(guard.TryAcquire(Owner, 1100));
    }

    [Fact]
    public void BorrowedFlagClearedExternallyIsNeverWrittenBack()
    {
        var port = new Port { Suppressed = true };
        var guard = Acquire(port);
        port.Suppressed = false;
        Assert.False(guard.TryAcquire(Owner, 1000));
        Assert.True(guard.Faulted);
        guard.RequestRelease(Owner);
        guard.Tick(1100);
        guard.Tick(1600);
        Assert.Null(guard.Owner);
        Assert.Empty(port.Writes);
        Assert.False(port.Suppressed);
    }

    [Fact]
    public void UnloadDoesNotRestoreDuringLoadingEvenIfMenusAppearClosed()
    {
        var port = new Port();
        var guard = Acquire(port);
        port.Ready = false;
        guard.RequestRelease(Owner);
        guard.Tick(1000, unloading: true);
        Assert.Equal(Owner, guard.Owner);
        Assert.True(port.Suppressed);
        Assert.Equal(new[] { true }, port.Writes);
    }

    [Fact]
    public void PreexistingInteractionIsNeverClaimedOrClosed()
    {
        var port = new Port { InteractionClosed = false };
        var guard = new RetainerWithdrawalGuard(port);
        Assert.False(guard.TryAcquire(Owner, 0));
        Assert.False(guard.TryAcquire(Owner, 10000));
        Assert.Null(guard.Owner);
        Assert.Empty(port.Writes);
        Assert.Equal(0, port.Closes);
    }
}
