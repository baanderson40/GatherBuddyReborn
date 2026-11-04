using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class VendorExchangeReadinessTests
{
    [Fact]
    public void IncompleteRowsGetOneBoundedWaitWithoutPollingExtendingDeadline()
    {
        var wait = new VendorExchangeReadiness();
        Assert.True(wait.ShouldWait(true, 100));
        Assert.True(wait.ShouldWait(true, 200));
        Assert.True(wait.ShouldWait(true, 3099));
        Assert.False(wait.ShouldWait(true, 3100));
        Assert.False(wait.ShouldWait(true, 9000));
    }

    [Fact]
    public void DefiniteMismatchDoesNotReceiveReadinessGrace()
    {
        var wait = new VendorExchangeReadiness();
        Assert.False(wait.ShouldWait(false, 100));
        Assert.False(wait.ShouldWait(false, 200));
        Assert.True(wait.ShouldWait(true, 300));
        Assert.False(wait.ShouldWait(false, 400));
        Assert.False(wait.ShouldWait(true, 3300));
    }

    [Fact]
    public void SuccessfulValidationOrNewRequestCanResetWait()
    {
        var wait = new VendorExchangeReadiness();
        Assert.True(wait.ShouldWait(true, 0));
        Assert.False(wait.ShouldWait(true, 3000));
        wait.Reset();
        Assert.True(wait.ShouldWait(true, 4000));
        Assert.False(wait.ShouldWait(true, 7000));
    }
}
