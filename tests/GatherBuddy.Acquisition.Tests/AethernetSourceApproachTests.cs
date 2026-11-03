using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AethernetSourceApproachTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly AethernetSourceRange Near = new(1, 4, 0);
    private static readonly AethernetSourceRange Far = new(1, 5, 0);

    [Theory]
    [InlineData(0f, 3f, true, 0f)]
    [InlineData(1f, 4f, true, 0f)]
    [InlineData(8f, 11f, true, 0f)]
    [InlineData(8f, 11.2f, false, 0.2f)]
    [InlineData(0f, 3.01f, false, 0.01f)]
    public void UsesLiveRadiusPlusThree(float radius, float horizontal, bool within, float remaining)
    {
        var range = new AethernetSourceRange(radius, horizontal, 0);
        Assert.Equal(radius + 3, range.Limit);
        Assert.Equal(within, range.InRange);
        Assert.Equal(remaining, range.Remaining, 4);
    }

    [Theory]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(1f, float.NaN, 0f)]
    [InlineData(1f, 0f, float.PositiveInfinity)]
    [InlineData(1f, -1f, 0f)]
    [InlineData(1f, 0f, -1f)]
    public void InvalidGeometryCannotHandoff(float radius, float horizontal, float vertical)
    {
        var range = new AethernetSourceRange(radius, horizontal, vertical);
        Assert.False(range.InRange);
        Assert.Equal(float.PositiveInfinity, range.Remaining);
        Assert.Equal(AethernetSourceAction.Failed,
            new AethernetSourceApproach().Evaluate(Now, 128, 1, range, true));
    }

    [Fact]
    public void VerticalGuardRejectsOtherFloorEvenWhenHorizontallyClose()
    {
        var range = new AethernetSourceRange(1, 0, 10);
        Assert.False(range.InRange);
        Assert.Equal(6, range.Remaining);
        Assert.Equal(AethernetSourceAction.Approach,
            new AethernetSourceApproach().Evaluate(Now, 128, 1, range, true));
    }

    [Fact]
    public void StopsOnceThenMakesOneCorrectiveApproachAfterGrace()
    {
        var approach = new AethernetSourceApproach();
        Assert.Equal(AethernetSourceAction.Approach, approach.Evaluate(Now, 128, 1, Far, false));
        Assert.Equal(AethernetSourceAction.Stop, approach.Evaluate(Now.AddSeconds(1), 128, 1, Near, false));
        approach.MarkStopped();
        Assert.Equal(AethernetSourceAction.Wait, approach.Evaluate(Now.AddSeconds(2), 128, 1, Near, false));
        Assert.Equal(AethernetSourceAction.CorrectiveApproach, approach.Evaluate(Now.AddSeconds(3), 128, 1, Near, false));
        approach.MarkCorrectiveApproachStarted();
        Assert.Equal(AethernetSourceAction.Wait, approach.Evaluate(Now.AddSeconds(4), 128, 1, Near, false));
        Assert.Equal(AethernetSourceAction.Wait, approach.Evaluate(Now.AddSeconds(5), 128, 1, Near, false));
        Assert.Equal(AethernetSourceAction.Ready, approach.Evaluate(Now.AddSeconds(6), 128, 1, Near, true));
        approach.Finish();
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(7), 128, 1, Near, true));
    }

    [Fact]
    public void NoRecognitionTimesOutWithoutRepeatedStopOrTeleport()
    {
        var approach = new AethernetSourceApproach();
        approach.Evaluate(Now, 128, 1, Near, false);
        approach.MarkStopped();
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(10), 128, 1, Near, false));
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(11), 128, 1, Near, true));
    }

    [Fact]
    public void RefusedTeleportCannotWaitForeverEvenWithRecognition()
    {
        var approach = new AethernetSourceApproach();
        Assert.Equal(AethernetSourceAction.Ready, approach.Evaluate(Now, 128, 1, Near, true));
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(10), 128, 1, Near, true));
    }

    [Fact]
    public void DriftingOutsideResumesApproachButDoesNotResetDeadline()
    {
        var approach = new AethernetSourceApproach();
        approach.Evaluate(Now, 128, 1, Near, false);
        approach.MarkStopped();
        Assert.Equal(AethernetSourceAction.Approach, approach.Evaluate(Now.AddSeconds(1), 128, 1, Far, false));
        Assert.Equal(AethernetSourceAction.Stop, approach.Evaluate(Now.AddSeconds(2), 128, 1, Near, false));
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(10), 128, 1, Far, false));
    }

    [Fact]
    public void MissingOrChangingSourceCannotExtendOverallTimeout()
    {
        var approach = new AethernetSourceApproach();
        Assert.Equal(AethernetSourceAction.Wait, approach.Evaluate(Now, 128, 0, default, false));
        Assert.Equal(AethernetSourceAction.Approach, approach.Evaluate(Now.AddSeconds(30), 128, 1, Far, false));
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(60), 128, 2, Far, false));
    }

    [Fact]
    public void SourceChangeGetsFreshRecognitionGraceAndTerritoryChangeResetsApproach()
    {
        var approach = new AethernetSourceApproach();
        approach.Evaluate(Now, 128, 1, Near, false);
        approach.MarkStopped();
        approach.MarkCorrectiveApproachStarted();
        Assert.Equal(AethernetSourceAction.Stop, approach.Evaluate(Now.AddSeconds(9), 128, 2, Near, false));
        approach.MarkStopped();
        Assert.Equal(AethernetSourceAction.Wait, approach.Evaluate(Now.AddSeconds(10), 128, 2, Near, false));
        Assert.Equal(AethernetSourceAction.Stop, approach.Evaluate(Now.AddSeconds(61), 129, 2, Near, false));
    }

    [Fact]
    public void CancellationIsTerminalUntilExplicitReset()
    {
        var approach = new AethernetSourceApproach();
        approach.Evaluate(Now, 128, 1, Far, false);
        approach.Finish();
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(1), 128, 1, Near, true));
        Assert.Equal(AethernetSourceAction.Failed, approach.Evaluate(Now.AddSeconds(2), 129, 2, Near, true));
        approach.Reset();
        Assert.Equal(AethernetSourceAction.Ready, approach.Evaluate(Now.AddSeconds(3), 129, 2, Near, true));
    }
}
