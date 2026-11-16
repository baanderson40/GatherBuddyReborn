using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveNativeTerminalTests
{
    private sealed record Fixture(string Evidence, int SchemaVersion, uint RecipeId, uint JobId, uint MainHandItemId,
        int GoodQualityPercent, AdaptiveNativeCaptureContext TestStartContext, AdaptiveObservedFrame[] Roots,
        Dictionary<int, AdaptiveNativeCraftEvent> NativePackets, JsonElement NativeSummaryBeforeFix,
        AdaptiveNativeCraftEvent? Finish, JsonElement NativeSummaryAfterFix);
    private static Fixture Load(string name) => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", name)), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    private sealed class NoWork : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
            => Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Unknown, null, new(0, 0, 0, TimeSpan.Zero, false), "No search in terminal evidence test."));
    }
    private sealed class Replay : IDisposable
    {
        public Fixture Evidence { get; }
        public AdaptiveShadowSession Session { get; }
        public TimeSpan Now { get; private set; }
        public Replay(string fixture = "native-schema6-poor-terminal.json")
        {
            Evidence = Load(fixture);
            var before = new AdaptiveObservedFrame(1, 0, 0, 80, 664, Condition.Normal);
            Session = new(AdaptiveSeedRegressionCorpus.Craft, before, Now, scheduler: new NoWork(), nativeContext: Evidence.TestStartContext);
            for (var index = 0; index < Evidence.Roots.Length; ++index)
            {
                var action = AdaptiveSeedRegressionCorpus.RecordedActions[index];
                var after = Evidence.Roots[index];
                // First twelve raw envelopes were evicted in supplied trace. These synthetic
                // envelopes prepare the real host; only retained packets are native evidence.
                var packet = Evidence.NativePackets.TryGetValue(index, out var captured) ? captured
                    : new(1, index + 4, (uint)action >= 100000 ? 9 : 10, Evidence.RecipeId, 0, (uint)action,
                        after.StepIndex, after.Progress, after.Quality, after.Durability, (int)after.Condition + 1, 18);
                var ticket = Session.RecordRequest(before, action, Now, packet.ActionId);
                Assert.NotEqual(default, ticket);
                Assert.True(Session.ReportRequest(ticket, true));
                Session.ObserveNative(packet, Now);
                Session.Observe(index * 2 + 1, Now += TimeSpan.FromMilliseconds(10), after, false);
                Session.Observe(index * 2 + 2, Now += TimeSpan.FromMilliseconds(10), after, false);
                Assert.False(Session.Blocked);
                Assert.Equal(index + 1, Session.CorrelatedActions);
                before = after;
            }
        }
        public void Final(AdaptiveNativeCraftEvent? packet = null)
        {
            var terminal = Evidence.NativePackets[16];
            var ticket = Session.RecordRequest(Evidence.Roots[^1], VulcanSkill.DelicateSynthesis, Now, terminal.ActionId);
            Assert.True(Session.ReportRequest(ticket, true));
            Session.ObserveNative(packet ?? terminal, Now);
        }
        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public void SuppliedPostFixNormalTerminalConfirmsMissingQualityWithoutInventingFinalFrame()
    {
        using var replay = new Replay("native-schema6-normal-terminal.json");
        Assert.Equal(6, replay.Evidence.SchemaVersion);
        Assert.Contains("evicted", replay.Evidence.Evidence);
        var frame = replay.Evidence.Roots[^1];
        Assert.Equal(11550, frame.Quality);
        Assert.Equal(Condition.Normal, frame.Condition);
        Assert.Equal(Condition.Good, replay.Evidence.Roots[13].Condition);
        replay.Final();
        var terminal = replay.Evidence.NativePackets[16];
        Assert.Equal(450, terminal.Quality - frame.Quality);
        Assert.Equal(6600, terminal.Progress);
        Assert.Equal(5, terminal.Durability);
        Assert.Equal(frame.StepIndex, terminal.StepIndex);
        replay.Session.ObserveNative(replay.Evidence.Finish!, replay.Now);
        replay.Session.End("Managed craft finished.", replay.Now, managedCraftEnded: true);
        var summary = replay.Session.Summary;
        Assert.False(replay.Session.Blocked);
        Assert.Equal(replay.Evidence.NativeSummaryAfterFix.GetProperty("TerminalObservation").GetString(), summary.TerminalObservation);
        Assert.Equal(17, summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(terminal, summary.NativeEvidence.Terminal);
        Assert.Equal(16, summary.CorrelatedActions);
        Assert.Equal(frame, summary.LastStableFrame);
        Assert.Null(summary.LastMismatch);
        Assert.Null(summary.NativeEvidence.LastMismatch);
        Assert.False(summary.NativeEvidence.ExecutionAuthorized);
        using var exported = JsonDocument.Parse(AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(replay.Session.Status, replay.Session.CaptureTrace())));
        Assert.False(exported.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
        var saved = exported.RootElement.GetProperty("Data").GetProperty("Summary");
        Assert.Equal(11550, saved.GetProperty("LastStableFrame").GetProperty("Quality").GetInt32());
        Assert.Equal(12000, saved.GetProperty("NativeEvidence").GetProperty("Terminal").GetProperty("Quality").GetInt32());
        Assert.False(saved.GetProperty("NativeEvidence").GetProperty("ExecutionAuthorized").GetBoolean());
    }

    [Fact]
    public void SuppliedPoorTerminalPacketIsAcceptedWithoutInventingFinalFrame()
    {
        using var replay = new Replay();
        Assert.Equal(6, replay.Evidence.SchemaVersion);
        Assert.Contains("evicted", replay.Evidence.Evidence);
        Assert.Equal(15u, replay.Evidence.JobId);
        Assert.Equal(51785u, replay.Evidence.MainHandItemId);
        Assert.Equal(175, replay.Evidence.GoodQualityPercent);
        Assert.Equal("Unobserved", replay.Evidence.NativeSummaryBeforeFix.GetProperty("TerminalObservation").GetString());
        var frame = replay.Evidence.Roots[^1];
        var simulation = new AdaptiveSimulation(AdaptiveSeedRegressionCorpus.Craft,
            AdaptiveCraftGoal.Resolve(AdaptiveSeedRegressionCorpus.Craft, AdaptiveGoalKind.Quality, 12000));
        simulation.TryApply(frame.ToState(true, VulcanSkill.ByregotsBlessing), VulcanSkill.DelicateSynthesis, out var predicted);
        Assert.Equal(Condition.Normal, predicted.Condition); // Hypothetical next turn, not terminal packet condition.
        Assert.Equal(Condition.Poor, frame.Condition);
        replay.Final();
        Assert.False(replay.Session.Blocked);
        var summary = replay.Session.Summary;
        Assert.Equal("ExperimentalPacketSuccess", summary.TerminalObservation);
        Assert.Equal(17, summary.NativeEvidence!.MatchedAcknowledgements);
        Assert.Equal(replay.Evidence.NativePackets[16], summary.NativeEvidence.Terminal);
        Assert.Null(summary.NativeEvidence.LastMismatch);
        Assert.Equal(16, summary.CorrelatedActions);
        Assert.Equal(frame, summary.LastStableFrame);
        Assert.Equal(6209, summary.LastStableFrame!.Value.Progress);
        Assert.False(summary.NativeEvidence.ExecutionAuthorized);
    }

    [Theory]
    [InlineData("condition", "ConditionPlusOne")]
    [InlineData("step", "StepIndex")]
    [InlineData("progress", "Progress")]
    [InlineData("quality", "Quality")]
    [InlineData("durability", "Durability")]
    [InlineData("action", "ActionId")]
    [InlineData("operation", "Operation")]
    [InlineData("success", "ActionSucceeded")]
    [InlineData("complete", "Complete")]
    [InlineData("stream", "Stream")]
    [InlineData("sequence", "Sequence")]
    [InlineData("recipe", "RecipeId")]
    [InlineData("startingQuality", "StartingQuality")]
    public void WrongTerminalPacketStillBlocksWithDurableFieldEvidence(string wrong, string field)
    {
        using var replay = new Replay();
        var packet = replay.Evidence.NativePackets[16];
        packet = wrong switch
        {
            "condition" => packet with { ConditionPlusOne = 1 }, "step" => packet with { StepIndex = 18 },
            "progress" => packet with { Progress = 6599 }, "quality" => packet with { Quality = 11999 },
            "durability" => packet with { Durability = 6 }, "action" => packet with { ActionId = 100203 },
            "operation" => packet with { Operation = 10 }, "success" => packet with { Flags = 6 },
            "complete" => packet with { Flags = 18 }, "stream" => packet with { Stream = 2 },
            "sequence" => packet with { Sequence = 19 }, "recipe" => packet with { RecipeId = 5619 },
            "startingQuality" => packet with { StartingQuality = 1 }, _ => throw new ArgumentException(),
        };
        replay.Final(packet);
        Assert.True(replay.Session.Blocked);
        var mismatch = replay.Session.Summary.NativeEvidence!.LastMismatch!;
        Assert.Contains(mismatch.Differences, difference => difference.Field == field);
        Assert.Equal(packet, mismatch.Observed);
        Assert.Equal(100330u, mismatch.Expected.ActionId);
        Assert.Equal(19, mismatch.Expected.AfterSequence);
        Assert.Equal(new[] { 4 }, mismatch.Expected.AllowedConditionPlusOne);
        Assert.Equal("Terminal preserves input condition; no next turn", mismatch.Expected.ConditionRule);
        Assert.Equal(16, replay.Session.Summary.NativeEvidence.MatchedAcknowledgements);
        Assert.Null(replay.Session.Summary.NativeEvidence.Terminal);
        Assert.Equal("Unobserved", replay.Session.Summary.TerminalObservation);
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveFieldDifference>)mismatch.Differences).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)mismatch.Expected.AllowedConditionPlusOne).Clear());
        replay.Session.End("Managed craft finished.", replay.Now, managedCraftEnded: true);
        replay.Session.Dispose();
        Assert.Same(mismatch, replay.Session.Summary.NativeEvidence!.LastMismatch);
        using var exported = JsonDocument.Parse(AdaptiveShadowExport.Serialize(AdaptiveShadowView.FromTrace(replay.Session.Status, replay.Session.CaptureTrace())));
        var native = exported.RootElement.GetProperty("Data").GetProperty("Summary").GetProperty("NativeEvidence");
        Assert.Equal(packet.ActionId, native.GetProperty("LastMismatch").GetProperty("Observed").GetProperty("ActionId").GetUInt32());
        Assert.False(native.GetProperty("ExecutionAuthorized").GetBoolean());
        Assert.False(exported.RootElement.GetProperty("NativeActionIdentityConfirmed").GetBoolean());
    }

    [Theory]
    [InlineData(Condition.Normal)]
    [InlineData(Condition.Good)]
    [InlineData(Condition.Excellent)]
    [InlineData(Condition.Poor)]
    public void SyntheticTerminalConditionsPreserveInputWhileNonterminalRulesStillApply(Condition condition)
    {
        var craft = AdaptiveSeedRegressionCorpus.Craft with { RequiredProgress = 200 };
        var simulation = new AdaptiveSimulation(craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Quality, 12000));
        var before = new AdaptiveObservedFrame(1, 0, 0, 80, 664, Condition.Normal);
        using var session = new AdaptiveShadowSession(craft, before, TimeSpan.Zero, scheduler: new NoWork(), nativeContext: new(1, craft.RecipeId, 0, 2));
        var state = before.ToState(true, VulcanSkill.None);
        var sequence = 2L;
        var frameNumber = 0L;
        foreach (var nextCondition in condition == Condition.Poor ? new[] { Condition.Excellent, Condition.Poor } : new[] { condition })
        {
            var ticket = session.RecordRequest(before, VulcanSkill.BasicTouch, TimeSpan.Zero, (uint)VulcanSkill.BasicTouch);
            session.ReportRequest(ticket, true);
            simulation.TryApply(state, VulcanSkill.BasicTouch, out state);
            state = state with { Condition = nextCondition };
            var after = new AdaptiveObservedFrame(before.StepIndex + 1, state.Progress, state.Quality, state.Durability,
                state.CP, nextCondition, InnerQuiet: state.InnerQuiet);
            session.ObserveNative(new(1, ++sequence, 9, craft.RecipeId, 0, (uint)VulcanSkill.BasicTouch,
                after.StepIndex, after.Progress, after.Quality, after.Durability, (int)nextCondition + 1, 18), TimeSpan.Zero);
            session.Observe(++frameNumber, TimeSpan.Zero, after, false);
            session.Observe(++frameNumber, TimeSpan.Zero, after, false);
            Assert.False(session.Blocked);
            before = after;
        }
        var final = session.RecordRequest(before, VulcanSkill.CarefulSynthesis, TimeSpan.Zero, (uint)VulcanSkill.CarefulSynthesis);
        session.ReportRequest(final, true);
        simulation.TryApply(state, VulcanSkill.CarefulSynthesis, out var predicted);
        session.ObserveNative(new(1, ++sequence, 9, craft.RecipeId, 0, (uint)VulcanSkill.CarefulSynthesis,
            before.StepIndex, 200, predicted.Quality, predicted.Durability, (int)condition + 1, 22), TimeSpan.Zero);
        Assert.False(session.Blocked);
        Assert.Equal("ExperimentalPacketSuccess", session.Summary.TerminalObservation);
        Assert.Equal(before, session.Summary.LastStableFrame);
        Assert.Equal((int)condition + 1, session.Summary.NativeEvidence!.Terminal!.ConditionPlusOne);
    }
}
