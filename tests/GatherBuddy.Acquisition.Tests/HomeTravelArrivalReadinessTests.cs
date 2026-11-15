using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class HomeTravelArrivalReadinessTests
{
    [Fact]
    public void OutdoorsRequiresTwoContinuousSecondsOfIdleAndReady()
    {
        var gate = new HomeTravelArrivalReadiness();
        Assert.False(gate.Observe(TimeSpan.Zero, false, true, false));
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1999), false, true, false));
        Assert.True(gate.Observe(TimeSpan.FromSeconds(2), false, true, false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void BusyOrReadinessLossRestartsFullWindow(bool busy, bool ready)
    {
        var gate = new HomeTravelArrivalReadiness();
        gate.Observe(TimeSpan.Zero, false, true, false);
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1500), busy, ready, false));
        Assert.False(gate.Observe(TimeSpan.FromSeconds(2), false, true, false));
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(3999), false, true, false));
        Assert.True(gate.Observe(TimeSpan.FromSeconds(4), false, true, false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void HousingFastPathNeverBypassesBusyOrReadiness(bool busy, bool ready, bool expected)
        => Assert.Equal(expected, new HomeTravelArrivalReadiness().Observe(TimeSpan.Zero, busy, ready, true));

    [Fact]
    public void ApartmentEntryGapCannotAdmitOutdoorBell()
    {
        var gate = new HomeTravelArrivalReadiness();
        Assert.False(gate.Observe(TimeSpan.Zero, false, true, false)); // LS finished selecting apartment entry.
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1300), false, true, false));
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1327), false, false, false)); // Zoning starts.
        Assert.True(gate.Observe(TimeSpan.FromSeconds(4), false, true, true)); // Housing interior ready.
    }

    [Fact]
    public void MidRouteIdleGapDoesNotSurviveNextTravelStage()
    {
        var gate = new HomeTravelArrivalReadiness();
        gate.Observe(TimeSpan.Zero, false, true, false);
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1300), false, true, false));
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(1400), true, true, false));
        Assert.False(gate.Observe(TimeSpan.FromSeconds(3), false, true, false));
        Assert.True(gate.Observe(TimeSpan.FromSeconds(5), false, true, false));
    }

    [Fact]
    public void AdmissionIsRevokedOnLastMomentBusyAndResetDiscardsEarlierReadiness()
    {
        var gate = new HomeTravelArrivalReadiness();
        gate.Observe(TimeSpan.Zero, false, true, false);
        Assert.True(gate.Observe(TimeSpan.FromSeconds(2), false, true, false));
        Assert.False(gate.Observe(TimeSpan.FromMilliseconds(2100), true, true, true));
        Assert.False(gate.Observe(TimeSpan.FromSeconds(3), false, true, false));
        gate.Reset(); // New command, AR suspension/release, or gathering departure.
        Assert.False(gate.Observe(TimeSpan.FromSeconds(6), false, true, false));
        Assert.True(gate.Observe(TimeSpan.FromSeconds(8), false, true, false));
    }

    [Fact]
    public void ReceiptRetainsExistingFinalStabilityAfterArrivalHeuristic()
    {
        var arrival = new HomeTravelArrivalReadiness();
        var receipt = new PreCraftHomeTravel(7);
        receipt.RecordSubmission(TimeSpan.Zero, "auto");
        receipt.Observe(TimeSpan.Zero, 7, "auto", true, true, false);
        HomeTravelAdmission Observe(int ms)
        {
            var now = TimeSpan.FromMilliseconds(ms);
            return receipt.Observe(now, 7, "auto", true, false, arrival.Observe(now, false, true, false));
        }
        Assert.Equal(HomeTravelAdmission.Waiting, Observe(1000));
        Assert.Equal(HomeTravelAdmission.Waiting, Observe(2999));
        Assert.Equal(HomeTravelAdmission.Waiting, Observe(3000));
        Assert.Equal(HomeTravelAdmission.Ready, Observe(3500));
        Assert.False(receipt.RecordSubmission(TimeSpan.FromSeconds(4), "auto"));
    }
}
