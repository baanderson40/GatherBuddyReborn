using GatherBuddy.Automation;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AutoRetainerCoordinatorTests
{
    private sealed class Port : IAutoRetainerCoordinationPort
    {
        public bool Enabled { get; set; } = true;
        public AutoRetainerProcessingMode Mode { get; set; }
        public string? ActiveOwner { get; set; } = "Vulcan:run1";
        public bool CanRequestHandoff { get; set; } = true;
        private bool _deferForTimedNode;
        public int TimedNodeReads;
        public bool DeferForTimedNode { get { TimedNodeReads++; return _deferForTimedNode; } set => _deferForTimedNode = value; }
        public AutoRetainerCharacter? Character { get; set; } = new(42, "Original@World");
        public bool CharacterReady { get; set; } = true;
        public bool MultiModeEnabled { get; set; }
        public bool Busy { get; set; }
        public bool CanRequestReturn { get; set; } = true;
        public bool CanReturnFromLoggedIn { get; set; } = true;
        public bool Available { get; set; } = true;
        public bool YieldReady { get; set; } = true;
        public bool ReturnAccepted { get; set; } = true;
        public string? Work { get; set; } = "deployables on Other@World";
        public int Starts, Returns, Releases, Cancels;
        public bool CancellationClean { get; set; } = true;
        public bool CancelThrows { get; set; }
        public bool? Resumed;
        public Action? DuringStart;
        public AutoRetainerProcessingMode StartedMode;
        public void VerifyAvailable() { if (!Available) throw new InvalidOperationException("IPC missing"); }
        public string? FindReadyWork(AutoRetainerProcessingMode mode) => Work;
        public bool TryYield(out string reason) { reason = "current transaction"; return YieldReady; }
        public void StartPass(AutoRetainerProcessingMode mode)
        {
            Starts++;
            StartedMode = mode;
            MultiModeEnabled = true;
            DuringStart?.Invoke();
        }
        public bool ReturnTo(AutoRetainerCharacter character) { Returns++; return ReturnAccepted; }
        public void CancelPass()
        {
            Cancels++;
            if (CancelThrows) throw new InvalidOperationException("Stop IPC missing");
            MultiModeEnabled = false;
        }
        public bool TryFinishCancellation(out string reason) { reason = "Retainer menu still closing"; return CancellationClean; }
        public void Release(bool resume) { Releases++; Resumed = resume; }
        public void Log(string message) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadyMonitoringNeverReadsTimedNodePriority(bool missingCharacter)
    {
        var port = new Port { CharacterReady = false, Character = missingCharacter ? null : new(42, "Original@World") };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(0, port.TimedNodeReads);
        Assert.Equal(0, port.Starts);
        Assert.Equal(AutoRetainerHandoffState.Monitoring, coordinator.State);
        port.Character = new(42, "Original@World");
        port.CharacterReady = true;
        coordinator.Tick(TimeSpan.FromSeconds(3));
        Assert.Equal(1, port.TimedNodeReads);
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void ReadinessLossAfterMonitoringPollPreventsFurtherPriorityReads()
    {
        var port = new Port { Work = null };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(1, port.TimedNodeReads);
        port.CharacterReady = false;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.TimedNodeReads);
        Assert.Equal(0, port.Starts);
    }

    [Theory]
    [InlineData(AutoRetainerProcessingMode.Ventures)]
    [InlineData(AutoRetainerProcessingMode.Deployables)]
    [InlineData(AutoRetainerProcessingMode.Both)]
    public void StartsOnePassWithCapturedMode(AutoRetainerProcessingMode mode)
    {
        var port = new Port { Mode = mode };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Mode = AutoRetainerProcessingMode.Both;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, port.Starts);
        Assert.Equal(mode, port.StartedMode);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(AutoRetainerHandoffState.RunningPass, coordinator.State);
    }

    [Fact]
    public void DoesNotStartUntilTransactionSettlesAndRechecksReadiness()
    {
        var port = new Port { YieldReady = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Starts);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Contains("current transaction", coordinator.Status);
        port.YieldReady = true;
        port.Work = null;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Starts);
        Assert.Equal(1, port.Releases);
        Assert.Equal(AutoRetainerHandoffState.Monitoring, coordinator.State);
    }

    [Fact]
    public void CompletionRequiresOriginalCharacterAndStableReadiness()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Character = new(99, "Other@World");
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, port.Returns);
        Assert.Equal(0, port.Releases);
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Returns);
        port.Character = new(42, "Original@World");
        port.CharacterReady = false;
        coordinator.Tick(TimeSpan.FromSeconds(3));
        port.CharacterReady = true;
        coordinator.Tick(TimeSpan.FromMilliseconds(3400));
        Assert.Equal(0, port.Releases);
        coordinator.Tick(TimeSpan.FromMilliseconds(3900));
        Assert.Equal(1, port.Releases);
        Assert.True(port.Resumed);
        Assert.False(coordinator.BlocksAutomation);
    }

    [Fact]
    public void BusyGapDoesNotCompletePass()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(0, port.Releases);
        Assert.Equal(AutoRetainerHandoffState.RunningPass, coordinator.State);
    }

    [Fact]
    public void InterruptedPassRequiresExplicitRecoveryOnOriginalCharacter()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.MultiModeEnabled = false;
        port.Character = new(99, "Other@World");
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.False(coordinator.Recover(TimeSpan.FromSeconds(2)));
        port.Character = new(42, "Original@World");
        Assert.True(coordinator.Recover(TimeSpan.FromSeconds(3)));
        Assert.False(port.Resumed);
    }

    [Fact]
    public void CompletionDuringStartIsNotLost()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        port.DuringStart = () => { port.MultiModeEnabled = false; coordinator.OnPassFinished(); };
        coordinator.Tick(TimeSpan.Zero);
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, port.Releases);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void NoStartWhenDisabledTimedOrUnavailable(bool disabled, bool timed, bool unavailable)
    {
        var port = new Port { Enabled = !disabled, DeferForTimedNode = timed, Available = !unavailable };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Starts);
        Assert.False(coordinator.BlocksAutomation);
    }

    [Fact]
    public void ExternalModeNeverGetsReplacedOrRequiresOurCompletionEvent()
    {
        var port = new Port { MultiModeEnabled = true };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Starts);
        Assert.True(coordinator.BlocksAutomation);
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Releases);
    }

    [Fact]
    public void ManualPauseAndSettingDisableDoNotReleaseAlternateCharacter()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.PreventResume();
        port.Enabled = false;
        port.Character = new(99, "Other@World");
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Releases);
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        port.Character = new(42, "Original@World");
        coordinator.Tick(TimeSpan.FromSeconds(3));
        coordinator.Tick(TimeSpan.FromSeconds(4));
        Assert.False(port.Resumed);
    }

    [Fact]
    public void OwnerChangeCannotResumeNewRun()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.ActiveOwner = "Vulcan:run2";
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.False(port.Resumed);
    }

    [Fact]
    public void IdleCompletionEventsAreIgnoredAndPollsAreBounded()
    {
        var port = new Port { Work = null };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.OnPassFinished();
        coordinator.Tick(TimeSpan.Zero);
        port.Work = "ventures ready";
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Starts);
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(AutoRetainerHandoffState.RunningPass, coordinator.State);
    }

    [Fact]
    public void MissingIpcDuringPassRetainsGate()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Available = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Releases);
    }

    [Fact]
    public void FailedReturnDoesNotResume()
    {
        var port = new Port { ReturnAccepted = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        port.Character = new(99, "Other@World");
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.Equal(0, port.Releases);
    }

    [Fact]
    public void PassTimeoutNeverPretendsCompletion()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.Tick(TimeSpan.FromMinutes(61));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.Equal(0, port.Releases);
    }

    [Fact]
    public void RepeatedIdenticalReadinessStopsEmptyPassLoop()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        for (var pass = 0; pass < 2; pass++)
        {
            var start = TimeSpan.FromSeconds(pass * 40);
            coordinator.Tick(start);
            coordinator.OnPassFinished();
            port.MultiModeEnabled = false;
            coordinator.Tick(start + TimeSpan.FromSeconds(1));
            coordinator.Tick(start + TimeSpan.FromSeconds(2));
        }
        coordinator.Tick(TimeSpan.FromSeconds(80));
        Assert.Equal(2, port.Starts);
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
    }

    [Fact]
    public void UnexpectedCharacterChangeBetweenPollsImmediatelyBlocksInventoryConsumers()
    {
        var port = new Port { Work = null };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Character = new(99, "Other@World");
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Starts);
    }

    [Fact]
    public void ZoningDuringYieldIsNotAnAlternateCharacter()
    {
        var port = new Port { YieldReady = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Character = null;
        port.CharacterReady = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Yielding, coordinator.State);
        port.Character = new(42, "Original@World");
        port.CharacterReady = port.YieldReady = true;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void OwnTravelBusyDoesNotGetMistakenForAnExternalMultiModePass()
    {
        var port = new Port { Busy = true };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(AutoRetainerHandoffState.Monitoring, coordinator.State);
        Assert.False(coordinator.BlocksAutomation);
        port.Busy = false;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Starts);
    }

    [Fact]
    public void BusyCleanupAndRenewedReadinessDelayRelease()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        port.Busy = true;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.WaitingForIdle, coordinator.State);
        port.Busy = false;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        port.CharacterReady = false;
        coordinator.Tick(TimeSpan.FromMilliseconds(2400));
        port.CharacterReady = true;
        coordinator.Tick(TimeSpan.FromMilliseconds(2800));
        Assert.Equal(0, port.Releases);
        coordinator.Tick(TimeSpan.FromMilliseconds(3300));
        Assert.Equal(1, port.Releases);
    }

    [Fact]
    public void OwnerStopBeforeStartCancelsPassAfterSafeYield()
    {
        var port = new Port { YieldReady = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.ActiveOwner = null;
        port.YieldReady = true;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Starts);
        Assert.False(port.Resumed);
    }

    [Theory]
    [InlineData(true, 99, 0, true, 0)]
    [InlineData(true, 99, 4, true, 4)]
    [InlineData(true, 99, 0, false, 99)]
    [InlineData(true, 99, 4, false, 4)]
    [InlineData(false, 1, 0, true, 1)]
    public void IntentionalQuickStopRetainsUnattemptedCrafts(bool quick, int requested, int reported, bool stop, int expected)
        => Assert.Equal(expected, Crafting.CraftReadiness.AttemptedCrafts(quick, requested, reported, stop));

    [Fact]
    public void DoesNotRequestReturnDuringLoginLoadingGap()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        port.Character = null;
        port.CharacterReady = port.CanRequestReturn = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Returns);
        Assert.Equal(AutoRetainerHandoffState.Returning, coordinator.State);
        port.CanRequestReturn = true; // e.g. ready title-screen AR login
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Returns);
        Assert.Equal(0, port.Releases);
    }

    [Fact]
    public void PausedQueueDoesNotRequestPassButRetainsCharacterIdentity()
    {
        var port = new Port { CanRequestHandoff = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Starts);
        port.Character = new(99, "Other@World");
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
    }

    [Fact]
    public void ReadinessClearingResetsRepeatedPassProtection()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        for (var pass = 0; pass < 3; pass++)
        {
            var start = TimeSpan.FromSeconds(pass * 50);
            port.Work = "same work on next voyage cycle";
            coordinator.Tick(start);
            coordinator.OnPassFinished();
            port.MultiModeEnabled = false;
            coordinator.Tick(start + TimeSpan.FromSeconds(1));
            coordinator.Tick(start + TimeSpan.FromSeconds(2));
            port.Work = null;
            coordinator.Tick(start + TimeSpan.FromSeconds(35));
        }
        Assert.Equal(3, port.Starts);
        Assert.Equal(AutoRetainerHandoffState.Monitoring, coordinator.State);
    }

    [Fact]
    public void BriefInventoryLossAfterReturnRestartsStableReadinessInsteadOfFailing()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.OnPassFinished();
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        port.Character = null;
        port.CharacterReady = false;
        coordinator.Tick(TimeSpan.FromMilliseconds(1400));
        Assert.Equal(AutoRetainerHandoffState.Settling, coordinator.State);
        port.Character = new(42, "Original@World");
        port.CharacterReady = true;
        coordinator.Tick(TimeSpan.FromMilliseconds(1500));
        coordinator.Tick(TimeSpan.FromMilliseconds(1900));
        Assert.Equal(0, port.Releases);
        coordinator.Tick(TimeSpan.FromMilliseconds(2000));
        Assert.Equal(1, port.Releases);
    }

    [Fact]
    public void MissingLoggedInReturnCapabilityDoesNotStartOrParkAutomation()
    {
        var port = new Port { CanReturnFromLoggedIn = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Starts);
        Assert.Equal(AutoRetainerHandoffState.Monitoring, coordinator.State);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Contains("Lifestream", coordinator.Status);
    }

    [Fact]
    public void QueueStopCancelsOwnedPassAndClearsStaleHandoffAfterCleanupWithoutCompletionEvent()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        // Mirrors StopQueue: discard the local queue, then cancel its captured owner.
        port.ActiveOwner = null;
        port.Busy = true;
        Assert.True(coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1)));
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, port.Cancels);
        Assert.False(port.MultiModeEnabled);
        Assert.True(coordinator.IsStopping);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Releases);
        port.Busy = false;
        port.CancellationClean = false;
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Contains("Retainer menu", coordinator.Status);
        Assert.Equal(0, port.Releases);
        port.CancellationClean = true;
        coordinator.Tick(TimeSpan.FromSeconds(3));
        coordinator.Tick(TimeSpan.FromSeconds(4));
        Assert.Equal(1, port.Releases);
        Assert.False(port.Resumed);
        Assert.False(coordinator.YieldRequested);
        Assert.False(coordinator.BlocksAutomation);
        Assert.Null(coordinator.Owner);
        Assert.Contains("stopped queue will not resume", coordinator.Status);
    }

    [Fact]
    public void StoppingOnAnotherCharacterStillReturnsBeforeReleasingGate()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Character = new(99, "Other@World");
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1));
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(1, port.Returns);
        Assert.Equal(0, port.Releases);
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Returns);
        port.Character = new(42, "Original@World");
        coordinator.Tick(TimeSpan.FromSeconds(3));
        coordinator.Tick(TimeSpan.FromSeconds(4));
        Assert.False(port.Resumed);
        Assert.False(coordinator.BlocksAutomation);
    }

    [Fact]
    public void StopBeforePassStartsDetachesWithoutStartingOrStoppingAr()
    {
        var port = new Port { YieldReady = false };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.True(coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1)));
        Assert.Equal(0, port.Starts);
        Assert.Equal(0, port.Cancels);
        Assert.False(port.Resumed);
        Assert.False(coordinator.YieldRequested);
    }

    [Fact]
    public void StoppingQueueObservingExternalModeNeverDisablesExternalAr()
    {
        var port = new Port { MultiModeEnabled = true };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Busy = true;
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Cancels);
        Assert.True(port.MultiModeEnabled);
        Assert.False(coordinator.YieldRequested);
        Assert.False(port.Resumed);
    }

    [Fact]
    public void OtherOwnersStopCannotCancelOwnedPass()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        Assert.False(coordinator.StopOwner("AutoGather:another-run", TimeSpan.FromSeconds(1)));
        Assert.False(coordinator.StopOwner(null, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, port.Cancels);
        Assert.Equal(AutoRetainerHandoffState.RunningPass, coordinator.State);
    }

    [Fact]
    public void StopCanReleasePreviouslyFailedOwnedPassWithoutManualRecovery()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.MultiModeEnabled = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(2));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        coordinator.Tick(TimeSpan.FromSeconds(3));
        Assert.False(coordinator.YieldRequested);
        Assert.False(port.Resumed);
    }

    [Fact]
    public void CancellationDoesNotRequireReadinessApisAndIgnoresLateCompletion()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1));
        coordinator.OnPassFinished();
        port.Available = false;
        coordinator.Tick(TimeSpan.FromSeconds(1));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Cancels);
        Assert.False(coordinator.YieldRequested);
        Assert.False(port.Resumed);
    }

    [Fact]
    public void StopAfterNormalCompletionDoesNotCancelNewExternalMode()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.OnPassFinished();
        // A different mode is now active; our completion was already observed.
        coordinator.Tick(TimeSpan.FromSeconds(1));
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(2));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(0, port.Cancels);
        Assert.True(port.MultiModeEnabled);
        Assert.True(coordinator.BlocksAutomation);
    }

    [Fact]
    public void RepeatedStopDoesNotResetCancellationDeadline()
    {
        var port = new Port();
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        port.Busy = true;
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1));
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(100));
        coordinator.Tick(TimeSpan.FromSeconds(182));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        Assert.Equal(0, port.Releases);
    }

    [Fact]
    public void FailedCancellationRetainsGateAndExplicitStopCanRetry()
    {
        var port = new Port { CancelThrows = true };
        var coordinator = new AutoRetainerCoordinator(port);
        coordinator.Tick(TimeSpan.Zero);
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(1));
        coordinator.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerHandoffState.Failed, coordinator.State);
        Assert.True(coordinator.BlocksAutomation);
        port.CancelThrows = false;
        coordinator.StopOwner("Vulcan:run1", TimeSpan.FromSeconds(2));
        coordinator.Tick(TimeSpan.FromSeconds(2));
        coordinator.Tick(TimeSpan.FromSeconds(3));
        Assert.False(coordinator.YieldRequested);
        Assert.False(port.Resumed);
    }
}
