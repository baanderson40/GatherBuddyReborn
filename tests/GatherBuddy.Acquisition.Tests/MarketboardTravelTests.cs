using GatherBuddy.Marketboard;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class MarketboardTravelTests
{
    private static readonly MarketTravelDestination Target = new(40, "Jenova", "Aether", 5057, "Electrum Ingot", false);
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    private sealed class Port : IMarketTravelPort
    {
        public MarketTravelObservation Observation = new(42, 63, 63, true, false, false);
        private string? _conflictReason;
        public bool AutoRetainerAggregateBusy, AutoRetainerMultiMode, MirrorLifestreamBusy;
        public Func<bool>? OwnsTravel;
        public string? ConflictReason
        {
            get => _conflictReason ?? (MarketTravelConflictPolicy.AutoRetainerBlocks(OwnsTravel?.Invoke() == true,
                AutoRetainerMultiMode, AutoRetainerAggregateBusy || MirrorLifestreamBusy && Observation.Busy)
                ? "AutoRetainer processing" : null);
            set => _conflictReason = value;
        }
        public bool Available = true, Eligible = true, Accepted = true, ThrowChange, ThrowMarket;
        public int Changes, Markets;
        public uint RequestedWorld;
        public List<string> Logs = new();
        public List<(string Message, bool Error)> Notices = new();
        public void VerifyAvailable() { if (!Available) throw new InvalidOperationException("IPC missing"); }
        public MarketTravelObservation Observe() => Observation;
        public bool CanVisit(MarketTravelDestination destination) => Eligible;
        public bool ChangeWorld(uint worldId)
        {
            Changes++; RequestedWorld = worldId;
            if (ThrowChange) throw new InvalidOperationException("IPC threw after possible submission");
            return Accepted;
        }
        public void GoToMarketboard()
        {
            Markets++;
            if (ThrowMarket) throw new InvalidOperationException("Shortcut IPC failed");
        }
        public void Log(string message) => Logs.Add(message);
        public void Notify(string message, bool error) => Notices.Add((message, error));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void AutoRetainerAggregateBusyIsOnlyAStartConflict(bool ownsTravel, bool multiMode, bool busy, bool blocks)
        => Assert.Equal(blocks, MarketTravelConflictPolicy.AutoRetainerBlocks(ownsTravel, multiMode, busy));

    [Fact]
    public void OwnedLifestreamTravelDoesNotFailWhenAutoRetainerMirrorsItsBusyState()
    {
        var port = new Port { MirrorLifestreamBusy = true };
        var coordinator = new MarketboardTravelCoordinator(port);
        port.OwnsTravel = () => coordinator.BlocksAutomation;
        Assert.True(coordinator.Start(Target, At(0), out _));
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.ChangingWorld, coordinator.State);
        // AR may briefly retain its own ancillary task after the travel busy flag.
        port.AutoRetainerAggregateBusy = true;
        port.Observation = port.Observation with { Busy = false, WorldId = 40 };
        coordinator.Tick(At(2));
        coordinator.Tick(At(3));
        coordinator.Tick(At(3.5));
        Assert.Equal(1, port.Markets);
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(4));
        port.Observation = port.Observation with { Busy = false, MarketboardOpen = true };
        coordinator.Tick(At(5));
        coordinator.Tick(At(5.5));
        Assert.Equal(MarketTravelState.Complete, coordinator.State);
        Assert.False(coordinator.BlocksAutomation);
        Assert.DoesNotContain(port.Notices, notice => notice.Error);
        // Aggregate busy must still prevent another travel request before ownership.
        Assert.False(coordinator.CanStart(Target, out _));
    }

    [Fact]
    public void RealAutoRetainerMultiModeStillInterruptsOwnedTravel()
    {
        var port = new Port { MirrorLifestreamBusy = true };
        var coordinator = new MarketboardTravelCoordinator(port);
        port.OwnsTravel = () => coordinator.BlocksAutomation;
        Assert.True(coordinator.Start(Target, At(0), out _));
        port.AutoRetainerMultiMode = true;
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("not ready")]
    [InlineData("logged out")]
    [InlineData("unsupported")]
    [InlineData("missing IPC")]
    [InlineData("automation")]
    [InlineData("market open")]
    public void UnsafeStartsAreRejectedWithoutSubmittingOrAcquiringGate(string blocker)
    {
        var port = new Port();
        switch (blocker)
        {
            case "busy": port.Observation = port.Observation with { Busy = true }; break;
            case "not ready": port.Observation = port.Observation with { Ready = false }; break;
            case "logged out": port.Observation = port.Observation with { ContentId = 0 }; break;
            case "unsupported": port.Eligible = false; break;
            case "missing IPC": port.Available = false; break;
            case "automation": port.ConflictReason = "queue active"; break;
            case "market open": port.Observation = port.Observation with { MarketboardOpen = true }; break;
        }
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.False(coordinator.Start(Target, At(0), out var reason));
        Assert.NotEmpty(reason);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Changes + port.Markets);
    }

    [Fact]
    public void TravelSelectionRechecksReadinessAfterMenuWasOpened()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.True(coordinator.CanStart(Target, out _));
        port.ConflictReason = "vendor started";
        Assert.False(coordinator.Start(Target, At(0), out var reason));
        Assert.Equal("vendor started", reason);
        Assert.Equal(0, port.Changes);
    }

    [Fact]
    public void SameWorldSkipsTransferAndSubmitsShortcutOnceAfterStableReadiness()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.True(coordinator.Start(Target, At(0), out _));
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.499));
        Assert.Equal(0, port.Markets);
        coordinator.Tick(At(0.5));
        coordinator.Tick(At(0.6));
        Assert.Equal(0, port.Changes);
        Assert.Equal(1, port.Markets);
        Assert.True(coordinator.BlocksAutomation);
    }

    [Fact]
    public void AlreadyOpenMarketboardCompletesWithoutAnyTravelCommand()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, true) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        Assert.Equal(MarketTravelState.Complete, coordinator.State);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Changes + port.Markets);
    }

    [Fact]
    public void CrossDcLogoutAndLoadingKeepOwnershipUntilCharacterWorldAndMarketAreVerified()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.True(coordinator.Start(Target, At(0), out _));
        Assert.Equal(40u, port.RequestedWorld);
        port.Observation = new(0, 0, 0, false, true, false);
        coordinator.Tick(At(1));
        coordinator.Tick(At(60));
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
        port.Observation = new(42, 63, 40, false, true, false);
        coordinator.Tick(At(61));
        port.Observation = port.Observation with { Ready = true, Busy = false };
        coordinator.Tick(At(62));
        coordinator.Tick(At(62.5));
        Assert.Equal(1, port.Markets);
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(63));
        port.Observation = port.Observation with { Busy = false, MarketboardOpen = true };
        coordinator.Tick(At(64));
        Assert.True(coordinator.BlocksAutomation);
        coordinator.Tick(At(64.5));
        Assert.Equal(MarketTravelState.Complete, coordinator.State);
        Assert.Equal(1, port.Changes);
        Assert.Equal(1, port.Markets);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Single(port.Notices);
        Assert.False(port.Notices[0].Error);
    }

    [Fact]
    public void IdleAloneDoesNotProveWorldArrival()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.ChangingWorld, coordinator.State);
        coordinator.Tick(At(15));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void RejectedWorldChangeReleasesGateWithoutMarketboardFollowup()
    {
        var port = new Port { Accepted = false };
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.False(coordinator.Start(Target, At(0), out _));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void ThrowingWorldIpcRetainsGateBecauseSubmissionMayHaveOccurred()
    {
        var port = new Port { ThrowChange = true };
        var coordinator = new MarketboardTravelCoordinator(port);
        Assert.False(coordinator.Start(Target, At(0), out _));
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
    }

    [Fact]
    public void WrongCharacterNeverGetsFollowupAndCannotReleaseRecoveryGate()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.Observation = new(99, 63, 40, true, false, true);
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.False(coordinator.Recover(At(2)));
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void DifferentHomeWorldAlsoInvalidatesCharacterIdentity()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.Observation = new(42, 99, 40, true, false, false);
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void ReadinessMustStayStableAndBusyResetsDebounce()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(0.4));
        port.Observation = port.Observation with { Busy = false };
        coordinator.Tick(At(1));
        coordinator.Tick(At(1.49));
        Assert.Equal(0, port.Markets);
        coordinator.Tick(At(1.5));
        Assert.Equal(1, port.Markets);
    }

    [Fact]
    public void WrongWorldAfterArrivalFailsWithoutSubmittingShortcut()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.Observation = port.Observation with { WorldId = 40 };
        coordinator.Tick(At(1));
        port.Observation = port.Observation with { WorldId = 99 };
        coordinator.Tick(At(2));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void NoOpShortcutTimesOutWithoutRepeatedSubmission()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        coordinator.Tick(At(16));
        coordinator.Tick(At(17));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(1, port.Markets);
        Assert.Single(port.Notices);
    }

    [Fact]
    public void IdleAfterShortcutDoesNotCountAsArrivalWithoutOpenMarketboard()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(1));
        port.Observation = port.Observation with { Busy = false };
        coordinator.Tick(At(2));
        coordinator.Tick(At(3));
        Assert.Equal(MarketTravelState.GoingToMarketboard, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        coordinator.Tick(At(301));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.Equal(1, port.Markets);
    }

    [Fact]
    public void StopAfterShortcutWaitsForSubmittedRouteWithoutResubmitting()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        port.Observation = port.Observation with { Busy = true };
        coordinator.StopFollowup(At(1));
        coordinator.Tick(At(2));
        Assert.True(coordinator.BlocksAutomation);
        port.Observation = port.Observation with { Busy = false };
        coordinator.Tick(At(3));
        coordinator.Tick(At(3.5));
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(1, port.Markets);
        Assert.Equal(0, port.Changes);
    }

    [Fact]
    public void RepeatedStopDoesNotExtendCleanupDeadline()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.StopFollowup(At(1));
        port.Observation = port.Observation with { Busy = true };
        coordinator.StopFollowup(At(200));
        coordinator.Tick(At(302));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
    }

    [Fact]
    public void ThrowingShortcutNeverGetsAutomaticallyRetried()
    {
        var port = new Port { ThrowMarket = true, Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(1, port.Markets);
    }

    [Fact]
    public void StopFollowupDrainsWorldTravelWithoutMarketboardSubmissionOrAbort()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.StopFollowup(At(1));
        port.Observation = new(0, 0, 0, false, true, false);
        coordinator.Tick(At(2));
        Assert.True(coordinator.BlocksAutomation);
        port.Observation = new(42, 63, 40, true, false, false);
        coordinator.Tick(At(3));
        coordinator.Tick(At(3.5));
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
        Assert.Equal(1, port.Changes);
    }

    [Fact]
    public void StopBeforeAnySubmissionDoesNotStartAnything()
    {
        var port = new Port { Observation = new(42, 63, 40, true, false, false) };
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        coordinator.StopFollowup(At(0));
        coordinator.Tick(At(0));
        coordinator.Tick(At(0.5));
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Changes + port.Markets);
    }

    [Fact]
    public void TransferTimeoutRetainsGateAndRecoveryRequiresIdleOriginalCharacterFor500Ms()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.Observation = port.Observation with { Busy = true };
        coordinator.Tick(At(1));
        coordinator.Tick(At(901));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.False(coordinator.Recover(At(902)));
        port.Observation = port.Observation with { Busy = false };
        coordinator.Tick(At(903));
        Assert.False(coordinator.Recover(At(903.49)));
        Assert.True(coordinator.Recover(At(903.5)));
        Assert.False(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
    }

    [Fact]
    public void ConflictingAutomationDetectedMidTravelFailsAndBlocksFurtherStages()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.ConflictReason = "external AutoRetainer started";
        coordinator.Tick(At(1));
        Assert.Equal(MarketTravelState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Markets);
        Assert.False(coordinator.Recover(At(2)));
    }

    [Fact]
    public void MissingIpcMidTravelRetainsGateAndDoesNotFloodLogs()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        port.Available = false;
        coordinator.Tick(At(1));
        coordinator.Tick(At(2));
        coordinator.Tick(At(3));
        Assert.True(coordinator.BlocksAutomation);
        Assert.Single(port.Notices);
        Assert.Equal(3, port.Logs.Count);
    }

    [Fact]
    public void ConcurrentTravelStartCannotReplaceCapturedDestination()
    {
        var port = new Port();
        var coordinator = new MarketboardTravelCoordinator(port);
        coordinator.Start(Target, At(0), out _);
        Assert.False(coordinator.Start(Target with { WorldId = 99, WorldName = "Other" }, At(1), out _));
        Assert.Equal(Target, coordinator.Destination);
        Assert.Equal(1, port.Changes);
        Assert.NotEqual(Guid.Empty, coordinator.RequestId);
    }
}
