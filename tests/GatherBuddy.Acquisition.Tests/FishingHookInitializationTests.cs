using GatherBuddy.FishTimer.Parser;
using System.Runtime.InteropServices;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class FishingHookInitializationTests
{
    private sealed class Resource : IDisposable
    {
        public int Disposals;
        public bool FailDispose;
        public void Dispose()
        {
            Disposals++;
            if (FailDispose) throw new InvalidOperationException("dispose failed");
        }
    }

    [Fact]
    public void SuccessfulPairTransfersBothWithoutPrematureDisposal()
    {
        var first = new Resource();
        var second = new Resource();
        var result = HookInitialization.CreatePair(() => first, () => second, (_, _) => Assert.Fail("Unexpected cleanup"));
        Assert.Same(first, result.First);
        Assert.Same(second, result.Second);
        Assert.Equal(0, first.Disposals);
        Assert.Equal(0, second.Disposals);
    }

    [Fact]
    public void FirstFactoryFailureIsNotRetriedAndNeverRunsSecondFactory()
    {
        var primary = new InvalidOperationException("allocation failed");
        var attempts = 0;
        var actual = Assert.Throws<InvalidOperationException>(() => HookInitialization.CreatePair<Resource, Resource>(
            () => { attempts++; throw primary; }, () => throw new Exception("second must not run"), (_, _) => Assert.Fail("Nothing to clean")));
        Assert.Same(primary, actual);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SecondFactoryFailureRollsBackFirstAndPreservesPrimary(bool disposeThrows, bool reportThrows)
    {
        var first = new Resource { FailDispose = disposeThrows };
        var primary = new InvalidOperationException("second allocation failed");
        var attempts = 0;
        var reports = 0;
        var actual = Assert.Throws<InvalidOperationException>(() => HookInitialization.CreatePair<Resource, Resource>(
            () => first, () => { attempts++; throw primary; }, (_, _) =>
            {
                reports++;
                if (reportThrows) throw new InvalidOperationException("logger failed");
            }));
        Assert.Same(primary, actual);
        Assert.Equal(1, attempts);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(disposeThrows ? 1 : 0, reports);
    }

    [Fact]
    public void CleanupFailureDoesNotPreventIndependentCleanup()
    {
        var first = new Resource { FailDispose = true };
        var second = new Resource();
        var errors = new List<Exception>();
        HookInitialization.Cleanup(first.Dispose, "first", (_, ex) => errors.Add(ex));
        HookInitialization.Cleanup(second.Dispose, "second", (_, ex) => errors.Add(ex));
        Assert.Single(errors);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void NativeFailuresAreNotReclassifiedAsRecoverableAllocationErrors()
    {
        Assert.False(HookInitialization.IsManagedFailure(new AccessViolationException()));
        Assert.False(HookInitialization.IsManagedFailure(new SEHException()));
        Assert.True(HookInitialization.IsManagedFailure(new InvalidOperationException()));
    }
}
