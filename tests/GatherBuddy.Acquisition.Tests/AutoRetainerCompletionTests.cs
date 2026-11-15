using GatherBuddy.Automation;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class AutoRetainerCompletionTests
{
    private sealed class Port : IAutoRetainerCompletionPort
    {
        public bool CompletionEnabled { get; set; } = true;
        public ulong CompletionCharacterId { get; set; } = 7;
        public bool CompletionCleanupReady { get; set; } = true;
        public bool MultiModeEnabled { get; set; }
        public bool Busy { get; set; }
        public bool MissingApi, ThrowAfterSubmission;
        public int Submissions;
        public Action? OnEnable;
        public void VerifyContinuousAvailable() { if (MissingApi) throw new InvalidOperationException(); }
        public void EnableContinuous()
        {
            Submissions++;
            MultiModeEnabled = true;
            OnEnable?.Invoke();
            if (ThrowAfterSubmission) throw new InvalidOperationException();
        }
    }
    private static AutoRetainerCompletionTicket Ticket(string owner = "Vulcan:run", bool armed = true)
        => new(owner, 7, armed);

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void GatheringRequiresSuccessfulWorkAndNoPendingCraft(bool completed, bool work, bool craft, bool expected)
        => Assert.Equal(expected, AutoRetainerCompletionEligibility.Gathering(completed, work, craft));

    [Theory]
    [InlineData(true, false, 2, 0, true)]
    [InlineData(false, false, 2, 0, false)]
    [InlineData(true, true, 2, 0, false)]
    [InlineData(true, false, 0, 0, false)]
    [InlineData(true, false, 2, 1, false)]
    public void QueueRequiresTerminalUnpausedConfirmedWork(bool complete, bool paused, int confirmed, int remaining, bool expected)
        => Assert.Equal(expected, AutoRetainerCompletionEligibility.Queue(complete, paused, confirmed, remaining));

    [Fact]
    public void SuccessfulCompletionSubmitsOnceAfterStableCleanupAndNeverReturnsOrResumes()
    {
        var port = new Port(); var completion = new AutoRetainerCompletion(port); var ticket = Ticket();
        Assert.True(completion.Request(ticket, TimeSpan.Zero));
        Assert.False(completion.Request(ticket, TimeSpan.Zero));
        completion.Tick(TimeSpan.Zero);
        Assert.Equal(0, port.Submissions);
        completion.Tick(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, port.Submissions);
        completion.Tick(TimeSpan.FromMilliseconds(500));
        Assert.Equal(AutoRetainerCompletionState.HandedOff, completion.State);
        Assert.True(port.MultiModeEnabled);
        port.CompletionCharacterId = 8; // Continuous AR can relog: no return/ownership after submission.
        completion.Cancel();
        completion.Tick(TimeSpan.FromMinutes(5));
        Assert.Equal(AutoRetainerCompletionState.HandedOff, completion.State);
        Assert.Equal(1, port.Submissions);
        Assert.False(completion.Request(ticket, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void CleanupAndBusyTransitionsRestartStability()
    {
        var port = new Port { CompletionCleanupReady = false }; var completion = new AutoRetainerCompletion(port);
        completion.Request(Ticket(), TimeSpan.Zero);
        completion.Tick(TimeSpan.Zero);
        port.CompletionCleanupReady = true;
        completion.Tick(TimeSpan.FromSeconds(1));
        port.Busy = true;
        completion.Tick(TimeSpan.FromMilliseconds(1400));
        port.Busy = false;
        completion.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(0, port.Submissions);
        completion.Tick(TimeSpan.FromMilliseconds(2500));
        Assert.Equal(1, port.Submissions);
    }

    [Fact]
    public void AlreadyEnabledModeIsNotRestartedOrDisabled()
    {
        var port = new Port { MultiModeEnabled = true, Busy = true }; var completion = new AutoRetainerCompletion(port);
        completion.Request(Ticket(), TimeSpan.Zero);
        completion.Tick(TimeSpan.Zero);
        Assert.Equal(AutoRetainerCompletionState.AlreadyEnabled, completion.State);
        Assert.Equal(0, port.Submissions);
        completion.Cancel();
        Assert.True(port.MultiModeEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledAtStartCannotArmAndDisablingPendingCancels(bool initiallyArmed)
    {
        var port = new Port(); var completion = new AutoRetainerCompletion(port);
        Assert.Equal(initiallyArmed, completion.Request(Ticket(armed: initiallyArmed), TimeSpan.Zero));
        port.CompletionEnabled = false;
        completion.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(0, port.Submissions);
        Assert.Equal(initiallyArmed ? AutoRetainerCompletionState.Cancelled : AutoRetainerCompletionState.Idle, completion.State);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(8UL)]
    public void LogoutOrCharacterChangeCannotTriggerCompletion(ulong character)
    {
        var port = new Port(); var completion = new AutoRetainerCompletion(port);
        completion.Request(Ticket(), TimeSpan.Zero);
        port.CompletionCharacterId = character;
        completion.Tick(TimeSpan.Zero);
        Assert.Equal(AutoRetainerCompletionState.Failed, completion.State);
        Assert.Equal(0, port.Submissions);
    }

    [Fact]
    public void ExplicitStopCancelsOnlyMatchingPendingOwner()
    {
        var port = new Port(); var completion = new AutoRetainerCompletion(port);
        completion.Request(Ticket(), TimeSpan.Zero);
        completion.Cancel("AutoGather:other");
        Assert.True(completion.Pending);
        completion.Cancel("Vulcan:run");
        completion.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerCompletionState.Cancelled, completion.State);
        Assert.Equal(0, port.Submissions);
    }

    [Fact]
    public void CleanupTimeoutDoesNotRetryOrDiscardOtherPendingTicket()
    {
        var port = new Port { CompletionCleanupReady = false }; var completion = new AutoRetainerCompletion(port);
        var ticket = Ticket(); var next = Ticket("Vulcan:next");
        completion.Request(ticket, TimeSpan.Zero);
        Assert.False(completion.Request(next, TimeSpan.Zero));
        Assert.False(next.Claimed);
        completion.Tick(TimeSpan.FromSeconds(120));
        Assert.Equal(AutoRetainerCompletionState.Failed, completion.State);
        port.CompletionCleanupReady = true;
        completion.Tick(TimeSpan.FromSeconds(121));
        Assert.False(completion.Request(ticket, TimeSpan.FromSeconds(121)));
        Assert.True(completion.Request(next, TimeSpan.FromSeconds(122)));
        completion.Tick(TimeSpan.FromSeconds(122));
        completion.Tick(TimeSpan.FromMilliseconds(122500));
        Assert.Equal(1, port.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIpcOrAmbiguousSubmissionFailsWithoutRetry(bool ambiguousSubmission)
    {
        var port = new Port { MissingApi = !ambiguousSubmission, ThrowAfterSubmission = ambiguousSubmission };
        var completion = new AutoRetainerCompletion(port); var ticket = Ticket();
        completion.Request(ticket, TimeSpan.Zero);
        completion.Tick(TimeSpan.Zero);
        completion.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoRetainerCompletionState.Failed, completion.State);
        port.MissingApi = port.ThrowAfterSubmission = false;
        completion.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(ambiguousSubmission ? 1 : 0, port.Submissions);
        Assert.Equal(ambiguousSubmission, completion.SubmissionAttempted);
        Assert.False(completion.Request(ticket, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ReentrantIpcCannotResubmitOrReplaceSubmittedTicket()
    {
        var port = new Port(); var completion = new AutoRetainerCompletion(port);
        var next = Ticket("Vulcan:next");
        port.OnEnable = () =>
        {
            completion.Tick(TimeSpan.FromSeconds(1));
            Assert.False(completion.Request(next, TimeSpan.FromSeconds(1)));
            completion.Cancel(); // Submitted AR is no longer ours to cancel.
        };
        completion.Request(Ticket(), TimeSpan.Zero);
        completion.Tick(TimeSpan.Zero);
        completion.Tick(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, port.Submissions);
        Assert.False(next.Claimed);
        Assert.Equal(AutoRetainerCompletionState.HandedOff, completion.State);
    }
}
