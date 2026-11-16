using System.Buffers.Binary;
using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveNativeConfirmationTests
{
    private sealed class NoWork : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Unknown, null,
                new(0, 0, 0, TimeSpan.Zero, false), "No search in acknowledgement tests."));
    }
    private sealed class Harness : IDisposable
    {
        public AdaptiveCraftSnapshot Craft { get; }
        public AdaptiveObservedFrame Initial { get; }
        public AdaptiveShadowSession Session { get; }
        public AdaptiveShadowRequest Ticket { get; }
        public AdaptiveSearchState Predicted { get; }
        public VulcanSkill Action { get; }
        public Harness(VulcanSkill action = VulcanSkill.BasicTouch, bool terminal = false, bool report = true, int durability = 80)
        {
            Action = action;
            Initial = new(1, 0, 0, durability, 664, Condition.Normal);
            Craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredProgress = terminal ? 200 : 6600 };
            Session = new(Craft, Initial, TimeSpan.Zero, scheduler: new NoWork(), nativeContext: new(1, Craft.RecipeId, 0, 2));
            var simulation = new AdaptiveSimulation(Craft, AdaptiveCraftGoal.Resolve(Craft, AdaptiveGoalKind.Quality, 12000));
            simulation.TryApply(Initial.ToState(true, VulcanSkill.None), action, out var predicted);
            Predicted = predicted;
            Ticket = Session.RecordRequest(Initial, action, TimeSpan.Zero, (uint)action);
            Assert.NotEqual(default, Ticket);
            if (report) Assert.True(Session.ReportRequest(Ticket, true));
        }
        public AdaptiveNativeCraftEvent Ack(bool terminal = false) => new(1, 3, (uint)Action >= 100000 ? 9 : 10,
            Craft.RecipeId, 0, (uint)Action, terminal || Action == VulcanSkill.FinalAppraisal ? 1 : 2,
            Math.Min(Craft.RequiredProgress, Predicted.Progress), Predicted.Quality, Math.Max(0, Predicted.Durability),
            (int)Predicted.Condition + 1, terminal ? 0x14u : 0x10u);
        public AdaptiveObservedFrame After => new(Action == VulcanSkill.FinalAppraisal ? 1 : 2,
            Predicted.Progress, Predicted.Quality, Predicted.Durability, Predicted.CP, Predicted.Condition,
            Predicted.InnerQuiet, Predicted.WasteNot, Predicted.Manipulation, Predicted.GreatStrides,
            Predicted.Innovation, Predicted.Veneration, Predicted.MuscleMemory, Predicted.FinalAppraisal,
            Predicted.TrainedPerfectionActive);
        public void Stable(AdaptiveObservedFrame frame, int start = 1)
        {
            Session.Observe(start, TimeSpan.FromMilliseconds(start), frame, false);
            Session.Observe(start + 1, TimeSpan.FromMilliseconds(start + 1), frame, false);
        }
        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public void CallbackPrefixParsingOwnsOnlyObservedScalarsAndUsesExactOffsets()
    {
        var bytes = new byte[0x48];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), 100323);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18), 17);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1c), 6600);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x24), 12000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x30), 5);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x44), 0x14);
        Assert.True(AdaptiveNativeCraftEvent.TryParseAdvance(bytes, 1, 3, 5618, 0, out var evidence));
        Assert.Equal(new(1, 3, 9, 5618, 0, 100323, 17, 6600, 12000, 5, 1, 0x14), evidence);
        Array.Clear(bytes);
        Assert.Equal(12000, evidence!.Quality);
        Assert.True(evidence.Complete);
        Assert.True(evidence.ActionSucceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x47)]
    [InlineData(0x49)]
    public void TruncatedAndUnexpectedPrefixLengthsCannotParse(int length)
        => Assert.False(AdaptiveNativeCraftEvent.TryParseAdvance(new byte[length], 1, 3, 5618, 0, out _));

    [Theory]
    [InlineData(0, 12)]
    [InlineData(0x10, 0)]
    [InlineData(0x18, 0)]
    [InlineData(0x1c, -1)]
    [InlineData(0x24, -1)]
    [InlineData(0x30, -1)]
    [InlineData(0x38, 0)]
    [InlineData(0x38, 5)]
    public void InvalidNativeScalarsCannotParse(int offset, int value)
    {
        var bytes = new byte[0x48];
        foreach (var (position, scalar) in new[] { (0, 9), (0x10, 100323), (0x18, 17), (0x38, 1) })
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position), scalar);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.False(AdaptiveNativeCraftEvent.TryParseAdvance(bytes, 1, 3, 5618, 0, out _));
    }

    [Fact]
    public void ReportAndStableEffectsAloneCannotAdvanceStrictNativeHistory()
    {
        using var h = new Harness();
        h.Stable(h.After);
        Assert.Equal(0, h.Session.CorrelatedActions);
        h.Session.ObserveNative(h.Ack(), TimeSpan.FromMilliseconds(3));
        h.Session.Observe(3, TimeSpan.FromMilliseconds(3), h.After, false);
        Assert.Equal(1, h.Session.CorrelatedActions);
        Assert.Equal(1, h.Session.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.False(h.Session.Summary.NativeEvidence.ExecutionAuthorized);
    }

    [Fact]
    public void AcknowledgementAloneCannotSubstituteCpOrBuffs()
    {
        using var h = new Harness();
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        Assert.Equal(0, h.Session.CorrelatedActions);
        h.Stable(h.After with { CP = h.After.CP - 1 });
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.CorrelatedActions);
        Assert.Contains(h.Session.Summary.LastMismatch!.Differences, difference => difference.Field == "CP");
    }

    [Fact]
    public void SuppliedTacosTraceOldInnovationAndMissingVenerationSettleToScreenshotWithoutRedispatch()
    {
        var craft = AdaptiveSeedRegressionCorpus.Craft with { RecipeId = 35829, Collectable = true,
            Collectability1 = 6600, Collectability2 = 9000, Collectability3 = 11400 };
        var initial = new AdaptiveObservedFrame(1, 0, 6000, 80, 664, Condition.Normal);
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 3);
        using var session = new AdaptiveShadowSession(craft, initial, TimeSpan.Zero,
            nativeContext: new(1, 35829, 6000, 3), confirmationGoal: goal);
        // Exact supplied trace projection; no expected state is used as an observed input.
        var actions = new[] { VulcanSkill.Reflect, VulcanSkill.PreparatoryTouch, VulcanSkill.PreparatoryTouch,
            VulcanSkill.GreatStrides, VulcanSkill.Innovation, VulcanSkill.PreparatoryTouch, VulcanSkill.ImmaculateMend,
            VulcanSkill.Veneration };
        var nativeIds = new uint[] { 100394, 100306, 100306, 267, 19011, 100306, 100474, 19304 };
        var frames = new AdaptiveObservedFrame[]
        {
            new(2, 0, 6900, 70, 658, Condition.Normal, InnerQuiet: 2),
            new(3, 0, 7620, 50, 618, Condition.Good, InnerQuiet: 4),
            new(4, 0, 9090, 30, 578, Condition.Normal, InnerQuiet: 6),
            new(5, 0, 9090, 30, 546, Condition.Normal, InnerQuiet: 6, GreatStrides: 3),
            new(6, 0, 9090, 30, 528, Condition.Normal, InnerQuiet: 6, GreatStrides: 2, Innovation: 4),
            new(7, 0, 11490, 10, 488, Condition.Normal, InnerQuiet: 8, Innovation: 3),
            new(8, 0, 11490, 80, 376, Condition.Normal, InnerQuiet: 8, Innovation: 2),
            // Screenshot corroborates these buffs at step9; arrival time below is synthetic.
            new(9, 0, 11490, 80, 358, Condition.Good, InnerQuiet: 8, Innovation: 1, Veneration: 4),
        };
        var before = initial;
        long hostFrame = 0;
        for (var i = 0; i < actions.Length; ++i)
        {
            var now = TimeSpan.FromSeconds(i + 1);
            var ticket = session.RecordRequest(before, actions[i], now, nativeIds[i]);
            Assert.NotEqual(default, ticket);
            Assert.Equal(i + 1, ticket.RequestId);
            Assert.True(session.MarkLiveDispatchInvoked(ticket, now));
            Assert.True(session.RecordLiveNativeReturn(ticket, true, now));
            var after = frames[i];
            session.ObserveNative(new(1, i + 4, nativeIds[i] >= 100000 ? 9 : 10, 35829, 6000,
                nativeIds[i], after.StepIndex, after.Progress, after.Quality, after.Durability, (int)after.Condition + 1, 18), now);
            var first = i == 7 ? after with { Innovation = 2, Veneration = 0 } : after;
            session.Observe(++hostFrame, now + TimeSpan.FromMilliseconds(1), first, false);
            session.Observe(++hostFrame, now + TimeSpan.FromMilliseconds(34), first, false);
            if (i == 7)
            {
                Assert.Equal(7, session.CorrelatedActions);
                Assert.Null(session.ConfirmedState);
                session.Observe(++hostFrame, now + TimeSpan.FromMilliseconds(100), after, false);
                Assert.Equal(7, session.CorrelatedActions);
                session.Observe(++hostFrame, now + TimeSpan.FromMilliseconds(133), after, false);
            }
            Assert.False(session.Blocked);
            before = after;
        }
        Assert.Equal(8, session.CorrelatedActions);
        Assert.Equal(8, session.Summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(8, session.LastConfirmedRequest.RequestId); // Event ring intentionally evicts older requests.
        Assert.Null(session.Summary.LastMismatch);
        Assert.Equal(frames[^1], session.Summary.LastStableFrame);
    }

    [Fact]
    public void AcknowledgedVenerationWaitsForStatusReplicationWithoutPromotingPrediction()
    {
        using var h = new Harness(VulcanSkill.Veneration);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        h.Stable(h.After with { Veneration = 0 });
        Assert.False(h.Session.Blocked);
        Assert.True(h.Session.HasPendingRequest);
        Assert.Null(h.Session.ConfirmedState);
        Assert.Equal(0, h.Session.CorrelatedActions);
        Assert.Null(h.Session.Summary.LastMismatch);
        h.Session.Observe(3, TimeSpan.FromMilliseconds(100), h.After, false);
        Assert.Equal(0, h.Session.CorrelatedActions); // One correct sample is not enough.
        h.Session.Observe(4, TimeSpan.FromMilliseconds(133), h.After, false);
        Assert.Equal(1, h.Session.CorrelatedActions);
        Assert.False(h.Session.HasPendingRequest);
        Assert.False(h.Session.Blocked);
        Assert.Equal(4, h.Session.ConfirmedState!.Value.Veneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusSettlementDeadlineCannotSlideOrAcceptLateCorrection(bool correctedAtDeadline)
    {
        using var h = new Harness(VulcanSkill.Veneration);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        h.Stable(h.After with { Veneration = 0 });
        h.Session.Observe(3, TimeSpan.FromMilliseconds(400), h.After with { Veneration = 1 }, false);
        h.Session.Observe(4, TimeSpan.FromMilliseconds(499), h.After with { Veneration = 1 }, false);
        Assert.False(h.Session.Blocked);
        h.Session.Observe(5, TimeSpan.FromMilliseconds(500), correctedAtDeadline ? h.After : null, false);
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.CorrelatedActions);
        Assert.Contains("500ms", h.Session.Status);
        Assert.Contains(h.Session.Summary.LastMismatch!.Differences, d => d.Field == "Veneration");
    }

    [Theory]
    [InlineData("CP")]
    [InlineData("Progress")]
    [InlineData("Quality")]
    [InlineData("Durability")]
    [InlineData("Condition")]
    [InlineData("StepIndex")]
    public void StatusWindowNeverDefersScalarMismatch(string field)
    {
        using var h = new Harness(VulcanSkill.Veneration);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        var observed = field switch
        {
            "CP" => h.After with { CP = h.After.CP - 1 },
            "Progress" => h.After with { Progress = 1 },
            "Quality" => h.After with { Quality = 1 },
            "Durability" => h.After with { Durability = h.After.Durability - 1 },
            "Condition" => h.After with { Condition = Condition.Good },
            _ => h.After with { StepIndex = h.After.StepIndex + 1 },
        };
        h.Stable(observed with { Veneration = 0 });
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.CorrelatedActions);
    }

    [Fact]
    public void WithoutAcknowledgementStatusLagCannotConfirmOrEnableWindow()
    {
        using var h = new Harness(VulcanSkill.Veneration);
        h.Stable(h.After with { Veneration = 0 });
        h.Session.Observe(3, TimeSpan.FromMilliseconds(600), h.After, false);
        h.Session.Observe(4, TimeSpan.FromMilliseconds(633), h.After, false);
        Assert.False(h.Session.Blocked);
        Assert.Null(h.Session.ConfirmedState);
        Assert.Equal(0, h.Session.CorrelatedActions);
        Assert.DoesNotContain(h.Session.Events, e => e.Detail.Contains("500ms"));
    }

    [Fact]
    public void NonStepNativeActionStillNeedsRepeatedMatchingBuffSamples()
    {
        using var h = new Harness(VulcanSkill.FinalAppraisal);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        h.Stable(h.After);
        Assert.False(h.Session.Blocked);
        Assert.Equal(1, h.Session.CorrelatedActions);
        Assert.Equal(1, h.Session.Summary.LastStableFrame!.Value.StepIndex);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("recipe")]
    [InlineData("qualityContext")]
    [InlineData("sequence")]
    [InlineData("action")]
    [InlineData("step")]
    [InlineData("quality")]
    [InlineData("progress")]
    [InlineData("durability")]
    [InlineData("condition")]
    [InlineData("failedAction")]
    [InlineData("completion")]
    public void WrongAcknowledgementBlocksWithoutPromotingPrediction(string mismatch)
    {
        using var h = new Harness();
        var ack = h.Ack();
        ack = mismatch switch
        {
            "stream" => ack with { Stream = 2 }, "recipe" => ack with { RecipeId = 5619 },
            "qualityContext" => ack with { StartingQuality = 1 }, "sequence" => ack with { Sequence = 2 },
            "action" => ack with { ActionId = (uint)VulcanSkill.StandardTouch }, "step" => ack with { StepIndex = 3 },
            "quality" => ack with { Quality = ack.Quality + 1 }, "progress" => ack with { Progress = 1 },
            "durability" => ack with { Durability = 0 }, "condition" => ack with { ConditionPlusOne = 0 },
            "failedAction" => ack with { Flags = 0 }, "completion" => ack with { Flags = 0x14 }, _ => throw new ArgumentException(),
        };
        h.Session.ObserveNative(ack, TimeSpan.Zero);
        h.Stable(h.After);
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.CorrelatedActions);
        Assert.Equal("Unobserved", h.Session.Summary.TerminalObservation);
        Assert.Null(h.Session.Summary.NativeEvidence!.Terminal);
    }

    [Fact]
    public void PacketAndAddonConditionsMustAgreeEvenWhenBothAreLegalNormalTransitions()
    {
        using var h = new Harness();
        h.Session.ObserveNative(h.Ack() with { ConditionPlusOne = 2 }, TimeSpan.Zero); // Good.
        h.Stable(h.After with { Condition = Condition.Excellent });
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.CorrelatedActions);
    }

    [Fact]
    public void DuplicateAcknowledgementCannotCountTwice()
    {
        using var h = new Harness();
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        Assert.True(h.Session.Blocked);
        Assert.Equal(1, h.Session.Summary.NativeEvidence!.MatchedAcknowledgements);
    }

    [Fact]
    public void CaptureLossRevokesHistoryAndCannotAdmitLateAcknowledgement()
    {
        using var h = new Harness();
        h.Session.NativeCaptureLost(TimeSpan.Zero);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.Summary.NativeEvidence!.MatchedAcknowledgements);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("rejected")]
    [InlineData("unreported")]
    public void PausedRejectedOrUnreportedRequestCannotAcceptAcknowledgement(string mode)
    {
        using var h = new Harness(report: false);
        if (mode == "paused") { h.Session.ReportRequest(h.Ticket, true); h.Session.SetPaused(true); }
        if (mode == "rejected") h.Session.ReportRequest(h.Ticket, false);
        h.Session.ObserveNative(h.Ack(), TimeSpan.Zero);
        Assert.True(h.Session.Blocked);
        Assert.Equal(0, h.Session.Summary.NativeEvidence!.MatchedAcknowledgements);
    }

    [Fact]
    public void TerminalPacketUsesUnadvancedStepAndDoesNotInventFullFinalFrame()
    {
        using var h = new Harness(VulcanSkill.MuscleMemory, terminal: true);
        h.Session.ObserveNative(h.Ack(terminal: true), TimeSpan.Zero);
        Assert.False(h.Session.Blocked);
        Assert.Equal("ExperimentalPacketSuccess", h.Session.Summary.TerminalObservation);
        Assert.Equal(0, h.Session.CorrelatedActions); // CP/status outcome never sampled.
        Assert.Null(h.Session.Summary.LastStableFrame);
        var native = h.Session.Summary.NativeEvidence!;
        Assert.Equal(200, native.Terminal!.Progress); // Observed clamped payload, not overshoot.
        Assert.Equal(1, native.Terminal.StepIndex);
        h.Session.ObserveNative(new(1, 4, 4, h.Craft.RecipeId, 0), TimeSpan.Zero);
        h.Session.End("Managed craft finished.", TimeSpan.Zero, managedCraftEnded: true);
        Assert.False(h.Session.Blocked);
        Assert.Equal("ExperimentalPacketSuccess", h.Session.Summary.TerminalObservation);
        using var json = JsonDocument.Parse(AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(h.Session.Status, h.Session.CaptureTrace())));
        Assert.Equal(6, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(json.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
        Assert.False(native.ExecutionAuthorized);
    }

    [Fact]
    public void FinishWithoutTerminalActionNeverProvesSuccess()
    {
        using var h = new Harness();
        h.Session.ObserveNative(new(1, 3, 4, h.Craft.RecipeId, 0), TimeSpan.Zero);
        Assert.True(h.Session.Blocked);
        Assert.Equal("Unobserved", h.Session.Summary.TerminalObservation);
    }

    [Theory]
    [InlineData(0x14u)] // Source documents complete-success bit can also occur on durability failure.
    [InlineData(0x18u)]
    [InlineData(0x1cu)]
    public void TerminalFailureIsDeterminedFromMatchedObservedScalarsNotSuccessBit(uint flags)
    {
        using var h = new Harness(durability: 10);
        h.Session.ObserveNative(h.Ack(terminal: true) with { Flags = flags }, TimeSpan.Zero);
        Assert.False(h.Session.Blocked);
        Assert.Equal("ExperimentalPacketFailure", h.Session.Summary.TerminalObservation);
        Assert.Equal(0, h.Session.Summary.NativeEvidence!.Terminal!.Durability);
        Assert.Equal(0, h.Session.Summary.NativeEvidence.Terminal.Progress);
        Assert.Equal(0, h.Session.CorrelatedActions);
    }

    [Fact]
    public void EndedSessionCannotAcceptLateTerminalEvidence()
    {
        using var h = new Harness(VulcanSkill.MuscleMemory, terminal: true);
        h.Session.End("Cancelled before acknowledgement.", TimeSpan.Zero);
        h.Session.ObserveNative(h.Ack(terminal: true), TimeSpan.Zero);
        Assert.Equal("Unobserved", h.Session.Summary.TerminalObservation);
        Assert.Equal(0, h.Session.Summary.NativeEvidence!.MatchedAcknowledgements);
    }

    [Theory]
    [InlineData(11550)]
    [InlineData(11775)]
    [InlineData(12000)]
    public void SuppliedFinalRootsNeedQualityFinisherBelowCapButCanConserveCpAtCap(int quality)
    {
        var craft = AdaptiveSeedRegressionCorpus.Craft;
        var frame = new AdaptiveObservedFrame(17, 6209, quality, 15, 86, Condition.Normal, Innovation: 1);
        var state = frame.ToState(true, VulcanSkill.ByregotsBlessing);
        var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Success, simulation.TryApply(state, VulcanSkill.DelicateSynthesis, out var delicate));
        Assert.Equal(12000, delicate.Quality);
        Assert.Equal(6683, delicate.Progress);
        Assert.Equal(AdaptiveOutcome.Success, simulation.TryApply(state, VulcanSkill.CarefulSynthesis, out var careful));
        Assert.Equal(quality, careful.Quality);
        var seed = new AdaptivePlanSeed(AdaptiveSeedRegressionCorpus.Seed().Provenance, new[] { VulcanSkill.DelicateSynthesis });
        var result = new AdaptiveSearchEngine().Search(simulation, state, new() { TimeLimit = null },
            seeds: new[] { new AdaptiveSeedCandidate(seed, 0, 0) });
        Assert.Equal(12000, result.Plan!.FinalState.Quality);
        if (quality < 12000) Assert.Equal(VulcanSkill.DelicateSynthesis, Assert.Single(result.Plan.Actions));
        else Assert.True(result.Plan.FinalState.CP > delicate.CP);
        // No observed terminal frame was provided in any of these native traces.
        Assert.True(frame.Progress < craft.RequiredProgress);
    }
}
