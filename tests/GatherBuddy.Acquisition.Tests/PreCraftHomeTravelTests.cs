using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class PreCraftHomeTravelTests
{
    private static HomeTravelAdmission Tick(PreCraftHomeTravel gate, int milliseconds, bool busy = false,
        bool ready = true, bool enabled = true, ulong character = 7, string command = "auto")
        => gate.Observe(TimeSpan.FromMilliseconds(milliseconds), character, command, enabled, busy, ready);

    [Fact]
    public void IngredientReadyRunSubmitsOnceAndWaitsForBusyThenStableReadiness()
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 0));
        Assert.True(gate.RecordSubmission(TimeSpan.Zero, "auto"));
        Assert.False(gate.RecordSubmission(TimeSpan.Zero, "auto"));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 500)); // Idle alone is not completion.
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1000, busy: true));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2000));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2499));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2500));
        gate.Consume();
        for (var recipe = 1; recipe <= 20; recipe++)
            Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2500 + recipe * 1000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(30), "auto"));
    }

    [Fact]
    public void GatherSubmittedReceiptIsReusedByCraftingWithoutSecondCommand()
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.True(gate.RecordSubmission(TimeSpan.Zero, "auto")); // GBR's submission in this run.
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 0, busy: true));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1000));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1500));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2000)); // Later Vulcan handoff.
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(2), "auto"));
        gate.Consume();
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 3000, command: "home")); // Already used; not per recipe.
    }

    [Fact]
    public void DisabledSettingSkipsCommandButCannotDismissOutstandingTravel()
    {
        var off = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(off, 0, enabled: false));
        Assert.False(off.RecordSubmission(TimeSpan.Zero, "auto"));
        var pending = new PreCraftHomeTravel(7);
        pending.RecordSubmission(TimeSpan.Zero, "auto");
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(pending, 0, busy: true, enabled: false));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(pending, 1000, enabled: false));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(pending, 1500, enabled: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnrelatedBusyOrUnreadyStateDefersSubmission(bool busy, bool unready)
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 0, busy: busy, ready: !unready));
        Assert.False(gate.Submitted);
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 1000));
    }

    [Fact]
    public void ReadinessLossRestartsCompletionStability()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: true);
        Tick(gate, 1000);
        Tick(gate, 1400, ready: false);
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2000));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2499));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2500));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoStartOrStuckBusyFailsWithoutRetry(bool sawBusy)
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: sawBusy);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, sawBusy ? 120000 : 10000, busy: sawBusy));
        Assert.NotNull(gate.Error);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 121000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(122), "auto"));
    }

    [Fact]
    public void WaitingForExternalReadinessIsAlsoBounded()
    {
        var gate = new PreCraftHomeTravel(7);
        Tick(gate, 0, busy: true);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 120000, busy: true));
        Assert.False(gate.Submitted);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(8UL)]
    public void LogoutOrCharacterChangeInvalidatesEvenConsumedReceipt(ulong character)
    {
        var gate = new PreCraftHomeTravel(7);
        Tick(gate, 0, enabled: false);
        gate.Consume();
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1000, character: character));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandChangeInvalidatesPendingOrUnconsumedCompletedReceipt(bool completed)
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: true);
        if (completed) { Tick(gate, 1000); Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1500)); }
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 2000, command: "home"));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(3), "home"));
    }

    [Fact]
    public void CancelAndNewRunCannotReusePriorReceipt()
    {
        var old = new PreCraftHomeTravel(7);
        old.RecordSubmission(TimeSpan.Zero, "auto");
        old.Cancel();
        Assert.Equal(HomeTravelAdmission.Cancelled, Tick(old, 1000));
        Assert.False(old.RecordSubmission(TimeSpan.FromSeconds(1), "auto"));
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(new(7), 1000));
    }

    [Fact]
    public void SubmissionExceptionRemainsStickyEvenAfterBusyClears()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        gate.Fail("IPC exception after possible submission");
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(2), "auto"));
    }

    private static PreCraftHomeTravel CompletedRetainerTravel()
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 0));
        Assert.True(gate.RecordSubmission(TimeSpan.Zero, "auto"));
        Tick(gate, 0, busy: true);
        Tick(gate, 1000);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1500));
        gate.Consume();
        return gate;
    }

    [Fact]
    public void RetainerOnlyRunReusesInitialTravelForCrafting()
    {
        var gate = CompletedRetainerTravel();
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 5000)); // Bell withdrawal completed; no departure.
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(5), "auto"));
        Assert.True(gate.Consumed);
    }

    [Fact]
    public void ActualGatherDepartureRequiresFreshFinalReturnAndThenReusesIt()
    {
        var gate = CompletedRetainerTravel();
        Assert.True(gate.InvalidateForGathering());
        Assert.False(gate.Submitted);
        Assert.False(gate.Consumed);
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2000, busy: true)); // Unrelated travel cannot satisfy new phase.
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 3000));
        Assert.True(gate.RecordSubmission(TimeSpan.FromSeconds(3), "auto")); // Final GBR home return.
        Assert.False(gate.InvalidateForGathering()); // Resume/repeated departure notification cannot erase pending trip.
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 3000, busy: true));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 4000));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 4500));
        gate.Consume();
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 5000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(5), "auto"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GatherDepartureCannotEraseFailedOrCancelledConsumedReceipt(bool cancelled)
    {
        var gate = CompletedRetainerTravel();
        if (cancelled) gate.Cancel();
        else gate.Fail("Travel status became uncertain");
        Assert.False(gate.InvalidateForGathering());
        Assert.Equal(cancelled ? HomeTravelAdmission.Cancelled : HomeTravelAdmission.Failed, Tick(gate, 2000));
    }

    [Fact]
    public void UnconsumedCompletedReceiptCannotBeResetAsImplicitRetry()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: true);
        Tick(gate, 1000);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1500));
        Assert.False(gate.InvalidateForGathering());
        Assert.True(gate.Submitted);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2000));
    }

    [Fact]
    public void DisabledRetainerAndGatherPhasesNeverSubmitHomeCommand()
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 0, enabled: false));
        gate.Consume();
        Assert.True(gate.InvalidateForGathering());
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1000, enabled: false));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(1), "auto"));
    }

    [Fact]
    public void CoordinatedRelogBeforeSubmissionWaitsThenAllowsOriginalTeleport()
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.Zero, 7));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1000, character: 0));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 300000, character: 8, busy: true));
        Assert.False(gate.RecordSubmission(TimeSpan.FromMinutes(5), "auto"));
        Assert.Null(gate.Error);
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromMinutes(6), 7));
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 360000));
        Assert.True(gate.RecordSubmission(TimeSpan.FromMinutes(6), "auto"));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 360100)); // AR travel was not counted as this command's busy evidence.
        Tick(gate, 360200, busy: true);
        Tick(gate, 361000);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 361500));
    }

    [Fact]
    public void SuspensionExcludesOnlyOwnedAbsenceFromUnsubmittedReadinessBudget()
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        Tick(gate, 0, ready: false);
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(100), 7));
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(1100), 7));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1119999, ready: false));
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1120000, ready: false));
    }

    [Fact]
    public void PendingSubmissionSurvivesRelogWithoutRetryAndKeepsStartBudget()
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(9), 7));
        Tick(gate, 500000, character: 0, busy: true);
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(1009), 7));
        Assert.True(gate.Submitted);
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(1009), "auto"));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1009999));
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1010000)); // Previous nine seconds were not erased.
    }

    [Fact]
    public void PendingCompletionBudgetAndFreshReadinessSurviveSuspension()
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: true);
        Tick(gate, 1000);
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromMilliseconds(1400), 7));
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(100), 7)); // Duplicate park must not move pause origin.
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromMilliseconds(1001400), 7));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1001400));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1001899));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1001900));
        var timed = new PreCraftHomeTravel(7);
        timed.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(timed, 0, busy: true);
        timed.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(100), 7);
        timed.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(1100), 7);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(timed, 1120000, busy: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongReturnCharacterOrHandoffCannotReleaseReceipt(bool wrongCharacter)
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        gate.SuspendForAutoRetainer(handoff, TimeSpan.Zero, 7);
        Assert.False(gate.ResumeFromAutoRetainer(wrongCharacter ? handoff : Guid.NewGuid(), TimeSpan.FromSeconds(1), wrongCharacter ? 8UL : 7UL));
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(1), "auto"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspensionCannotHealExistingFailureOrCancellation(bool cancelled)
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        gate.SuspendForAutoRetainer(handoff, TimeSpan.Zero, 7);
        if (cancelled) gate.Cancel(); else gate.Fail("Existing uncertainty");
        Assert.False(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(1), 7));
        Assert.False(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(2), 7));
        Assert.Equal(cancelled ? HomeTravelAdmission.Cancelled : HomeTravelAdmission.Failed, Tick(gate, 2000));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(8UL)]
    public void SuspensionRequiresOriginalCharacter(ulong character)
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.False(gate.SuspendForAutoRetainer(Guid.NewGuid(), TimeSpan.Zero, character));
        Assert.False(gate.Suspended);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 1000));
    }

    [Fact]
    public void ConsumedReceiptIsPreservedButCannotBeInvalidatedDuringForeignCharacterVisit()
    {
        var gate = CompletedRetainerTravel(); var handoff = Guid.NewGuid();
        gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(2), 7);
        Assert.False(gate.InvalidateForGathering());
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 3000, character: 8));
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(4), 7));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 4000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(4), "auto"));
    }

    [Fact]
    public void UnconsumedReadyReceiptIsRevokedIfAnotherTravelStageStarts()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Tick(gate, 0, busy: true);
        Tick(gate, 1000);
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1500));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 1600, busy: true));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(2), "auto"));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 2000));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2500));
    }

    [Fact]
    public void AlreadyInsideHousingSkipsCommandAndSharesReceiptAcrossSamePhase()
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Ready, gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true));
        Assert.True(gate.SkippedIndoorFastPath);
        Assert.False(gate.Submitted);
        Assert.False(gate.RecordSubmission(TimeSpan.Zero, "auto"));
        gate.Consume();
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 1000));
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(1), "auto"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HousingSkipRequiresIdleAndPlayerReadiness(bool busy, bool ready)
    {
        var gate = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Waiting, gate.Observe(TimeSpan.Zero, 7, "auto", true, busy, ready, insideFastPath: true));
        Assert.False(gate.SkippedIndoorFastPath);
        Assert.Equal(HomeTravelAdmission.Ready, gate.Observe(TimeSpan.FromSeconds(1), 7, "auto", true, false, true, insideFastPath: true));
        Assert.True(gate.SkippedIndoorFastPath);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SkippedButUnconsumedPhaseStillWaitsWithoutAllowingSubmission(bool busy, bool ready)
    {
        var gate = new PreCraftHomeTravel(7);
        gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true);
        Assert.Equal(HomeTravelAdmission.Waiting, gate.Observe(TimeSpan.FromSeconds(1), 7, "auto", true, busy, ready));
        gate.Consume();
        Assert.False(gate.Consumed);
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(1), "auto"));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 2000));
        gate.Consume();
        Assert.True(gate.Consumed);
    }

    [Fact]
    public void OutsideAndInnClassificationDoNotSkipAndDisabledSettingStaysIndependent()
    {
        var outside = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.NeedsCommand, outside.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: false));
        Assert.False(outside.SkippedIndoorFastPath);
        Assert.True(outside.RecordSubmission(TimeSpan.Zero, "auto"));
        var disabled = new PreCraftHomeTravel(7);
        Assert.Equal(HomeTravelAdmission.Ready, disabled.Observe(TimeSpan.Zero, 7, "auto", false, false, true, insideFastPath: true));
        Assert.False(disabled.SkippedIndoorFastPath);
    }

    [Fact]
    public void HousingCannotRetroactivelySkipAnAlreadySubmittedCommand()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.RecordSubmission(TimeSpan.Zero, "auto");
        Assert.Equal(HomeTravelAdmission.Waiting, gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true));
        Assert.False(gate.SkippedIndoorFastPath);
        Assert.Equal(HomeTravelAdmission.Failed, gate.Observe(TimeSpan.FromSeconds(10), 7, "auto", true, false, true, insideFastPath: true));
    }

    [Fact]
    public void GatheringDepartureAndFreshRunDoNotReuseHousingSkip()
    {
        var gate = new PreCraftHomeTravel(7);
        gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true);
        gate.Consume();
        Assert.True(gate.InvalidateForGathering());
        Assert.False(gate.SkippedIndoorFastPath);
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(gate, 1000));
        Assert.Equal(HomeTravelAdmission.NeedsCommand, Tick(new(7), 1000));
    }

    [Fact]
    public void CoordinatedArSuspensionPreservesHousingSkipButUnownedLogoutStillFails()
    {
        var gate = new PreCraftHomeTravel(7); var handoff = Guid.NewGuid();
        gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true);
        Assert.True(gate.SuspendForAutoRetainer(handoff, TimeSpan.FromSeconds(1), 7));
        Assert.Equal(HomeTravelAdmission.Waiting, Tick(gate, 300000, character: 0));
        Assert.True(gate.ResumeFromAutoRetainer(handoff, TimeSpan.FromSeconds(301), 7));
        Assert.Equal(HomeTravelAdmission.Ready, Tick(gate, 301000));
        Assert.True(gate.SkippedIndoorFastPath);
        Assert.False(gate.RecordSubmission(TimeSpan.FromSeconds(301), "auto"));
        Assert.Equal(HomeTravelAdmission.Failed, Tick(gate, 302000, character: 0));
    }

    [Fact]
    public void HousingSkipCannotClearCancellationOrFailureAndReadinessWaitRemainsBounded()
    {
        foreach (var cancelled in new[] { true, false })
        {
            var gate = new PreCraftHomeTravel(7);
            if (cancelled) gate.Cancel(); else gate.Fail("Existing uncertainty");
            Assert.Equal(cancelled ? HomeTravelAdmission.Cancelled : HomeTravelAdmission.Failed,
                gate.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true));
            Assert.False(gate.SkippedIndoorFastPath);
        }
        var waiting = new PreCraftHomeTravel(7);
        waiting.Observe(TimeSpan.Zero, 7, "auto", true, false, true, insideFastPath: true);
        Assert.Equal(HomeTravelAdmission.Failed, Tick(waiting, 120000, busy: true));
        Assert.False(waiting.RecordSubmission(TimeSpan.FromSeconds(120), "auto"));
    }
}
