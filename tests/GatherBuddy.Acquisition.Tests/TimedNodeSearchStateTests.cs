using GatherBuddy.AutoGather;
using System.Numerics;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class TimedNodeSearchStateTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExhaustedSearchWaitsThenStartsSingleRescan()
    {
        var state = new TimedNodeSearchState();
        state.SetWindow(1189, 123, 100, 200);
        state.ExcludedPositions.Add(new Vector3(1, 2, 3));

        Assert.True(state.WaitOrBeginRescan(Now));
        Assert.True(state.IsWaitingForRescan);
        Assert.Single(state.ExcludedPositions);

        Assert.True(state.WaitOrBeginRescan(Now.AddSeconds(29)));
        Assert.Single(state.ExcludedPositions);

        Assert.False(state.WaitOrBeginRescan(Now.Add(TimedNodeSearchState.RescanInterval)));
        Assert.False(state.IsWaitingForRescan);
        Assert.Empty(state.ExcludedPositions);
    }

    [Fact]
    public void ChangingSpawnWindowClearsExclusionsAndWait()
    {
        var state = new TimedNodeSearchState();
        state.SetWindow(1189, 123, 100, 200);
        state.ExcludedPositions.Add(Vector3.One);
        state.WaitOrBeginRescan(Now);

        state.SetWindow(1189, 123, 300, 400);

        Assert.Empty(state.ExcludedPositions);
        Assert.False(state.IsWaitingForRescan);
        Assert.True(state.WaitOrBeginRescan(Now));
    }

    [Fact]
    public void ChangingNodeOrTerritoryResetsSearch()
    {
        var state = new TimedNodeSearchState();
        state.SetWindow(1189, 123, 100, 200);
        state.ExcludedPositions.Add(Vector3.One);

        state.SetWindow(1189, 124, 100, 200);
        Assert.Empty(state.ExcludedPositions);
        state.ExcludedPositions.Add(Vector3.One);

        state.SetWindow(1188, 124, 100, 200);
        Assert.Empty(state.ExcludedPositions);
    }
}
