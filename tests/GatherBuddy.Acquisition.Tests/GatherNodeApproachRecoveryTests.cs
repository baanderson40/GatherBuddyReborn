using GatherBuddy.AutoGather;
using System.Numerics;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class GatherNodeApproachRecoveryTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(3.49f, 2.99f, true)]
    [InlineData(3.5f, 0f, false)]
    [InlineData(0f, 3f, false)]
    [InlineData(1.82f, 4.92f, false)]
    public void InteractionRangeRequiresStrictHorizontalAndVerticalLimits(
        float horizontal, float vertical, bool expected)
    {
        var node = new Vector3(0, 0, 0);
        var player = new Vector3(horizontal, vertical, 0);

        Assert.Equal(expected, GatherNodeApproachRules.IsWithinInteractionRange(node, player));
    }

    [Fact]
    public void CombinedPathGroundEndpointIgnoresTrailingLandingMetadata()
    {
        Vector3[] path =
        [
            new(0, 10, 0),
            new(0, 0, 0),
            new(0, 20, 0),
        ];

        Assert.True(GatherNodeApproachRules.TryGetCombinedPathGroundEndpoint(path, out var endpoint));
        Assert.Equal(path[1], endpoint);
        Assert.True(GatherNodeApproachRules.IsPathEndpointReachable(Vector3.Zero, endpoint));
        Assert.False(GatherNodeApproachRules.IsPathEndpointReachable(Vector3.Zero, path[^1]));
    }

    [Fact]
    public void FailedSpawnCooldownIsScopedToExactSpawnAndExpires()
    {
        var cooldown = new FailedGatherSpawnCooldown();
        var spawn = new Vector3(1, 2, 3);
        cooldown.MarkFailed(397, 31337, spawn, Now);

        Assert.True(cooldown.Contains(397, 31337, spawn, Now.AddSeconds(30)));
        Assert.False(cooldown.Contains(397, 31337, new Vector3(4, 5, 6), Now.AddSeconds(30)));
        Assert.False(cooldown.Contains(397, 31338, spawn, Now.AddSeconds(30)));
        Assert.False(cooldown.Contains(398, 31337, spawn, Now.AddSeconds(30)));
        Assert.False(cooldown.Contains(397, 31337, spawn, Now.Add(FailedGatherSpawnCooldown.Duration)));
    }
}
