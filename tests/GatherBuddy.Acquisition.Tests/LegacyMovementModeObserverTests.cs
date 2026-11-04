using GatherBuddy.AutoGather.Movement;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class LegacyMovementModeObserverTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    public void FirstSuccessfulReadIsReportedOnce(uint value, bool expected)
    {
        var observer = new LegacyMovementModeObserver();
        Assert.True(observer.Refresh(() => value));
        Assert.True(observer.HasValue);
        Assert.Equal(expected, observer.LegacyMode);
        Assert.False(observer.Refresh(() => value));
    }

    [Fact]
    public void FailedInitialReadDoesNotClaimStandardMode()
    {
        var observer = new LegacyMovementModeObserver();
        Assert.False(observer.Refresh(() => null));
        Assert.False(observer.HasValue);
        Assert.True(observer.Refresh(() => 0));
        Assert.True(observer.HasValue);
    }

    [Fact]
    public void FailedReadPreservesLegacyAndDoesNotProduceFalseTransitions()
    {
        var observer = new LegacyMovementModeObserver();
        Assert.True(observer.Refresh(() => 1));
        Assert.False(observer.Refresh(() => null));
        Assert.True(observer.LegacyMode);
        Assert.False(observer.Refresh(() => 1));
        Assert.True(observer.Refresh(() => 0));
        Assert.False(observer.LegacyMode);
        Assert.True(observer.Refresh(() => 1));
    }

    [Theory]
    [InlineData("OtherOption", true)]
    [InlineData("OtherOption", false)]
    [InlineData("PadMode", true)]
    [InlineData("MoveMode", false)]
    public void UnrelatedEventsDoNotEvenReadMode(string option, bool uiControl)
    {
        var observer = new LegacyMovementModeObserver();
        Assert.False(observer.RefreshForChange(option, uiControl, () => throw new Exception("Unrelated read")));
        Assert.False(observer.HasValue);
    }

    [Theory]
    [InlineData("MoveMode", true)]
    [InlineData("PadMode", false)]
    public void RelevantChangesReadActiveBankAndOnlyReportChangedState(string option, bool uiControl)
    {
        var observer = new LegacyMovementModeObserver();
        Assert.True(observer.Refresh(() => 1));
        Assert.False(observer.RefreshForChange(option, uiControl, () => 1));
        Assert.True(observer.RefreshForChange(option, uiControl, () => 0));
        Assert.False(observer.LegacyMode);
    }

    [Fact]
    public void DisposeIsIdempotentAndLateCallbacksCannotReadOrUpdate()
    {
        var observer = new LegacyMovementModeObserver();
        observer.Refresh(() => 1);
        observer.Dispose();
        observer.Dispose();
        Assert.False(observer.Refresh(() => throw new Exception("Disposed read")));
        Assert.False(observer.RefreshForChange("PadMode", false, () => throw new Exception("Disposed event")));
        Assert.True(observer.LegacyMode);
    }
}
