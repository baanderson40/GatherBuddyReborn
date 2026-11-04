using GatherBuddy.Automation;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class TeleportReadinessTests
{
    private static readonly TeleportContext Context = new(42, 63, 63, 130);
    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void UnreadyAndInitialDebounceNeverInvokeNativeRefresh()
    {
        var cache = new TeleportSnapshotCache();
        var calls = 0;
        IReadOnlyDictionary<uint, int> Refresh() { calls++; return new Dictionary<uint, int> { [9] = 100 }; }
        Assert.False(cache.Observe(null, false, At(0)));
        Assert.False(cache.TryRead(Refresh, out _));
        Assert.False(cache.Observe(Context, true, At(100)));
        Assert.False(cache.Observe(Context, true, At(599)));
        Assert.False(cache.TryRead(Refresh, out _));
        Assert.Equal(0, calls);
        Assert.True(cache.Observe(Context, true, At(600)));
        Assert.True(cache.TryRead(Refresh, out var costs));
        Assert.Equal(100, costs![9]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NullIdentityNeverAuthorizesRefreshEvenIfCallerReportsReady()
    {
        var cache = new TeleportSnapshotCache();
        Assert.False(cache.Observe(null, true, At(0)));
        Assert.False(cache.Observe(null, true, At(10000)));
        Assert.False(cache.TryRead(() => throw new Exception("No player"), out _));
    }

    [Theory]
    [InlineData(0ul, 63u, 63u, 130u)]
    [InlineData(42ul, 0u, 63u, 130u)]
    [InlineData(42ul, 63u, 0u, 130u)]
    [InlineData(42ul, 63u, 63u, 0u)]
    public void PartialIdentityCannotBecomeReady(ulong cid, uint home, uint world, uint territory)
    {
        var cache = new TeleportSnapshotCache();
        var partial = new TeleportContext(cid, home, world, territory);
        Assert.False(cache.Observe(partial, true, At(0)));
        Assert.False(cache.Observe(partial, true, At(10000)));
        Assert.False(cache.TryRead(() => throw new Exception("Partial identity"), out _));
    }

    [Fact]
    public void ReadinessLossRestartsDebounceAndRefreshWithoutNewTerritory()
    {
        var cache = Ready();
        Assert.True(cache.TryRead(() => new Dictionary<uint, int> { [9] = 100 }, out _));
        Assert.False(cache.Observe(Context, false, At(600)));
        Assert.True(cache.Pending);
        Assert.False(cache.Observe(Context, true, At(700)));
        Assert.False(cache.Observe(Context, true, At(1199)));
        Assert.True(cache.Observe(Context, true, At(1200)));
        Assert.True(cache.TryRead(() => new Dictionary<uint, int> { [9] = 200 }, out var costs));
        Assert.Equal(200, costs![9]);
    }

    [Theory]
    [InlineData(99ul, 63u, 63u, 130u)]
    [InlineData(42ul, 40u, 63u, 130u)]
    [InlineData(42ul, 63u, 40u, 130u)]
    [InlineData(42ul, 63u, 63u, 131u)]
    public void IdentityOrTerritoryChangeCannotReusePreviousCosts(ulong cid, uint home, uint world, uint territory)
    {
        var cache = Ready();
        cache.TryRead(() => new Dictionary<uint, int> { [9] = 100 }, out _);
        var next = new TeleportContext(cid, home, world, territory);
        Assert.False(cache.Observe(next, true, At(600)));
        Assert.False(cache.TryRead(() => throw new Exception("Must not refresh yet"), out var missing));
        Assert.Null(missing);
        Assert.True(cache.Observe(next, true, At(1100)));
        Assert.True(cache.TryRead(() => new Dictionary<uint, int> { [8] = 50 }, out var costs));
        Assert.False(costs!.ContainsKey(9));
        Assert.Equal(50, costs[8]);
    }

    [Fact]
    public void NullRefreshRemainsPendingAndRetriesSameContext()
    {
        var cache = Ready();
        Assert.False(cache.TryRead(() => null, out _));
        Assert.True(cache.Pending);
        Assert.True(cache.TryRead(() => new Dictionary<uint, int> { [9] = 100 }, out _));
        Assert.False(cache.Pending);
    }

    [Fact]
    public void ManagedRefreshFailureCannotPublishPartialSnapshotOrConsumeRetry()
    {
        var cache = Ready();
        cache.TryRead(() => new Dictionary<uint, int> { [9] = 100 }, out var previous);
        cache.RequestRefresh();
        Assert.Throws<InvalidOperationException>(() => cache.TryRead(() => throw new InvalidOperationException("copy failed"), out _));
        Assert.True(cache.Pending);
        Assert.Equal(100, previous![9]);
        Assert.True(cache.TryRead(() => new Dictionary<uint, int> { [8] = 200 }, out var replacement));
        Assert.False(replacement!.ContainsKey(9));
    }

    [Fact]
    public void ReadinessChangeDuringRefreshCannotPublishOldCharacterData()
    {
        var cache = Ready();
        Assert.False(cache.TryRead(() =>
        {
            cache.Observe(Context with { ContentId = 99 }, true, At(600));
            return new Dictionary<uint, int> { [9] = 100 };
        }, out _));
        Assert.True(cache.Pending);
        Assert.Null(cache.ReadyContext);
        Assert.True(cache.Observe(Context with { ContentId = 99 }, true, At(1100)));
        Assert.False(cache.TryRead(() => null, out _));
    }

    [Fact]
    public void ReentrantReadCannotInvokeNativeRefreshRecursively()
    {
        var cache = Ready();
        Assert.True(cache.TryRead(() =>
        {
            Assert.False(cache.TryRead(() => throw new Exception("Recursive native refresh"), out _));
            return new Dictionary<uint, int> { [9] = 100 };
        }, out var costs));
        Assert.Equal(100, costs![9]);
    }

    [Fact]
    public void PublishedSnapshotDoesNotAliasMutableSourceAndCanBeReadWithoutRefresh()
    {
        var cache = Ready();
        var source = new Dictionary<uint, int> { [9] = 100 };
        cache.TryRead(() => source, out var costs);
        source.Clear();
        Assert.Equal(100, costs![9]);
        Assert.True(cache.TryRead(() => throw new Exception("Read-only consumer cannot refresh"), out var cached));
        Assert.Same(costs, cached);
    }

    [Fact]
    public void EmptySuccessfulSnapshotIsNotInventedAttunement()
    {
        var cache = Ready();
        Assert.True(cache.TryRead(() => new Dictionary<uint, int>(), out var costs));
        Assert.Empty(costs!);
        Assert.False(costs!.ContainsKey(9));
    }

    [Fact]
    public void LogoutInvalidatesSnapshotAndSameCharacterMustReinitialize()
    {
        var cache = Ready();
        cache.TryRead(() => new Dictionary<uint, int> { [9] = 100 }, out _);
        cache.Observe(null, false, At(600));
        Assert.Null(cache.ReadyContext);
        Assert.False(cache.TryRead(() => throw new Exception("Logged out"), out _));
        Assert.False(cache.Observe(Context, true, At(700)));
        Assert.True(cache.Observe(Context, true, At(1200)));
        Assert.False(cache.TryRead(() => null, out _)); // Old character data is not returned.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimedNodeInspectionReadsOnlyPublishedSchedulerDecision(bool decision)
    {
        var snapshot = new TimedNodeDeferralSnapshot();
        Assert.False(snapshot.TryRead(Context, 100, false, out _));
        snapshot.Publish(Context, 100, decision);
        Assert.True(snapshot.TryRead(Context, 100, false, out var defer));
        Assert.Equal(decision, defer);
        Assert.True(snapshot.TryRead(Context, 100, false, out _));
    }

    [Fact]
    public void ChangedHourListOrIdentityInvalidatesTimedPriorityUntilNormalRefresh()
    {
        var snapshot = new TimedNodeDeferralSnapshot();
        snapshot.Publish(Context, 100, true);
        Assert.False(snapshot.TryRead(Context, 101, false, out _));
        Assert.False(snapshot.TryRead(Context, 100, true, out _));
        Assert.False(snapshot.TryRead(Context with { ContentId = 99 }, 100, false, out _));
        Assert.False(snapshot.TryRead(null, 100, false, out _));
        snapshot.Publish(Context, 101, false); // Completed/visited/expired target removed by safe scheduler.
        Assert.True(snapshot.TryRead(Context, 101, false, out var defer));
        Assert.False(defer);
        snapshot.Invalidate();
        Assert.False(snapshot.TryRead(Context, 101, false, out _));
    }

    [Fact]
    public void TaskReadinessPauseRestoresDeadlineOnceWithoutConsumingQueuedAction()
    {
        var pause = new ReadinessTaskPause();
        Assert.Equal(0, pause.Observe(true, 0));
        Assert.Equal(0, pause.Observe(false, 100));
        Assert.Equal(0, pause.Observe(false, 10000));
        Assert.Equal(10000, pause.Observe(true, 10100));
        Assert.Equal(0, pause.Observe(true, 10200));
        Assert.Equal(0, pause.Observe(false, 11000));
        Assert.Equal(500, pause.Observe(true, 11500));
    }

    private static TeleportSnapshotCache Ready()
    {
        var cache = new TeleportSnapshotCache();
        Assert.False(cache.Observe(Context, true, At(0)));
        Assert.True(cache.Observe(Context, true, At(500)));
        return cache;
    }
}
