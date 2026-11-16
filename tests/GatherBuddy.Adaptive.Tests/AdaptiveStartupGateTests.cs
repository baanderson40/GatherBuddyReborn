using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveStartupGateTests
{
    private static AdaptiveStartupGate Gate() => new(TimeSpan.Zero, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, false, true)] // Recipe log between crafts: Crafting flag can remain set.
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, true, false, false)]
    public void BetweenCraftRecipeLogIsAllowedButActiveOrUnmanagedCraftIsNot(bool managedPreparing,
        bool crafting, bool preparingToCraft, bool executing, bool expected)
        => Assert.Equal(expected, AdaptiveStartupGate.IsPreSynthesisIdle(managedPreparing, crafting, preparingToCraft, executing));

    [Fact]
    public void ConsecutiveCraftsAutomaticallyPrepareFreshOneUseTickets()
    {
        var first = Gate();
        var enables = 0;
        Assert.True(first.Poll(TimeSpan.Zero, true, true, true, () => ++enables > 0));
        Assert.True(first.TryConsume(first.Ticket));
        Assert.False(first.TryConsume(first.Ticket));
        var second = Gate();
        Assert.NotEqual(first.Ticket, second.Ticket);
        Assert.False(second.CaptureReady);
        Assert.False(second.TryConsume(first.Ticket));
        Assert.True(second.Poll(TimeSpan.Zero, true, true, true, () => ++enables > 0));
        Assert.False(second.TryConsume(first.Ticket));
        Assert.True(second.TryConsume(second.Ticket));
        Assert.Equal(2, enables);
        Assert.Null(first.Poll(TimeSpan.Zero, true, true, true, () => throw new Exception("Must not re-enable")));
    }

    [Fact]
    public void PendingWorkerMustDrainBeforeCaptureOrSynthesis()
    {
        var gate = Gate();
        var enables = 0;
        Assert.False(gate.Poll(TimeSpan.Zero, true, true, false, () => ++enables > 0));
        Assert.False(gate.CaptureReady);
        Assert.False(gate.TryConsume(gate.Ticket));
        Assert.Equal(0, enables);
        Assert.True(gate.Poll(TimeSpan.FromSeconds(1), true, true, true, () => ++enables > 0));
        Assert.True(gate.Poll(TimeSpan.FromSeconds(2), true, true, true, () => throw new Exception("No second hook")));
        Assert.Equal(1, enables);
    }

    [Fact]
    public void DrainTimeoutFailsClosedRatherThanStartingAnotherWorkerOrRetrying()
    {
        var gate = Gate();
        Assert.Null(gate.Poll(TimeSpan.FromSeconds(5), true, true, false, () => throw new Exception("No capture")));
        Assert.True(gate.Closed);
        Assert.False(gate.TryConsume(gate.Ticket));
        Assert.Null(gate.Poll(TimeSpan.FromSeconds(6), true, true, true, () => throw new Exception("No late retry")));
    }

    [Fact]
    public void CaptureFailureIsNotRetriedAndCannotAuthorizeStartup()
    {
        var gate = Gate();
        Assert.Null(gate.Poll(TimeSpan.Zero, true, true, true, () => false));
        Assert.False(gate.CaptureReady);
        Assert.False(gate.TryConsume(gate.Ticket));
        Assert.Null(gate.Poll(TimeSpan.Zero, true, true, true, () => throw new Exception("No retry")));
    }

    [Fact]
    public void CaptureExceptionClosesGateBeforePropagating()
    {
        var gate = Gate();
        Assert.Throws<InvalidOperationException>(() => gate.Poll(TimeSpan.Zero, true, true, true, () => throw new InvalidOperationException()));
        Assert.True(gate.Closed);
        Assert.False(gate.TryConsume(gate.Ticket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInvalidatesTasksBeforeAndAfterCapture(bool ready)
    {
        var gate = Gate();
        if (ready) Assert.True(gate.Poll(TimeSpan.Zero, true, true, true, () => true));
        gate.Cancel();
        Assert.False(gate.TryConsume(gate.Ticket));
        Assert.Null(gate.Poll(TimeSpan.Zero, true, true, true, () => throw new Exception("No restart")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void SelectionOrContextDriftClosesPreparedGate(bool selected, bool idle)
    {
        var gate = Gate();
        Assert.True(gate.Poll(TimeSpan.Zero, true, true, true, () => true));
        Assert.Null(gate.Poll(TimeSpan.Zero, selected, idle, true, () => throw new Exception("No second hook")));
        Assert.False(gate.TryConsume(gate.Ticket));
    }
}
