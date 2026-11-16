using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveLiveEvidenceTests
{
    private sealed record Fixture(string Evidence, int SchemaVersion, Guid DecisionSessionId, Guid EvidenceSessionId,
        AdaptiveCraftSnapshot Craft, AdaptiveObservedFrame[] Roots, VulcanSkill[] Actions, bool?[] RawNativeReturns,
        AdaptiveNativeCraftEvent[] NativePackets, AdaptiveNativeCraftEvent Finish,
        AdaptiveLiveTransaction Transaction, JsonElement SourceSummary);
    private static Fixture Load(string name = "live-schema2-groundwork-success.json") => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", name)), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;

    [Fact]
    public void SecondLiveRunPreservesEighteenActionScopeAndCorrectManagedEnd()
    {
        var fixture = Load("live-schema3-manipulation-success.json");
        Assert.Equal(3, fixture.SchemaVersion);
        Assert.Contains("NOT reconstructed", fixture.Evidence);
        Assert.Contains("not controlled A/B", fixture.Evidence);
        Assert.Equal(12, fixture.Roots[0].StepIndex);
        Assert.Equal(18, fixture.Transaction.Ordinal);
        Assert.Equal(18, fixture.SourceSummary.GetProperty("MatchedAcknowledgements").GetInt32());
        Assert.Equal(17, fixture.SourceSummary.GetProperty("CorrelatedActions").GetInt32());
        Assert.Equal(0, fixture.SourceSummary.GetProperty("HistoricalSearchesDispatched").GetInt32());
        Assert.Contains("managed craft ended", fixture.SourceSummary.GetProperty("OriginalStatus").GetString());
        Assert.DoesNotContain("awaiting managed end", fixture.SourceSummary.GetProperty("OriginalStatus").GetString());
        Assert.Equal(7, fixture.NativePackets.Length);
        Assert.Null(fixture.RawNativeReturns[0]);
        Assert.Equal(6, fixture.RawNativeReturns.Count(value => value == true));
        Assert.All(fixture.NativePackets, packet => Assert.Equal(0x100u, packet.Flags & 0x100));
        Assert.Equal(64, fixture.SourceSummary.GetProperty("TotalEvents").GetInt32() - fixture.SourceSummary.GetProperty("DroppedEvents").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SecondRunRetainedManipulationRecoveryAndExpirationMatch(int index)
    {
        var fixture = Load("live-schema3-manipulation-success.json");
        var before = fixture.Roots[index];
        var after = fixture.Roots[index + 1];
        var packet = fixture.NativePackets[index];
        var simulation = new AdaptiveSimulation(fixture.Craft, AdaptiveCraftGoal.Resolve(fixture.Craft, AdaptiveGoalKind.Quality, 12000));
        // Explicit history projection; evicted combo/availability do not affect these suffix actions.
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(before.ToState(false, VulcanSkill.None), fixture.Actions[index], out var predicted));
        Assert.Equal(after.ToState(false, VulcanSkill.None), predicted with { Combo = VulcanSkill.None });
        Assert.Equal(after.Progress, packet.Progress);
        Assert.Equal(after.Quality, packet.Quality);
        Assert.Equal(after.Durability, packet.Durability);
        Assert.Equal(after.StepIndex, packet.StepIndex);
        Assert.Equal((int)after.Condition + 1, packet.ConditionPlusOne);
        Assert.Equal(274u, packet.Flags);
        Assert.Equal(Math.Max(0, before.Manipulation - 1), after.Manipulation);
    }

    [Fact]
    public void SecondRunTerminalKeepsUnknownFlagBitWithoutRelaxingRequiredBits()
    {
        var fixture = Load("live-schema3-manipulation-success.json");
        var before = fixture.Roots[^1];
        var packet = fixture.NativePackets[^1];
        var simulation = new AdaptiveSimulation(fixture.Craft, AdaptiveCraftGoal.Resolve(fixture.Craft, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Success, simulation.TryApply(before.ToState(false, VulcanSkill.None), VulcanSkill.Groundwork, out var predicted));
        Assert.Equal(6824, predicted.Progress);
        Assert.Equal(5, predicted.Durability);
        var expected = new AdaptiveNativeExpectation(1, 20, 5618, 0, 100410, 9, 18, 6600, 12000, 5,
            Array.AsReadOnly(new[] { 1 }), "Terminal preserves input condition", true, true);
        Assert.Empty(AdaptiveNativeMismatch.Compare("Synthetic comparison of supplied terminal projection", expected, packet).Differences);
        Assert.Empty(AdaptiveNativeMismatch.Compare("Mask unknown bit only", expected, packet with { Flags = packet.Flags & ~0x100u }).Differences);
        Assert.Contains(AdaptiveNativeMismatch.Compare("Remove success bit", expected, packet with { Flags = packet.Flags & ~0x10u }).Differences,
            item => item.Field == "ActionSucceeded");
        Assert.Equal(25, before.Durability);
        Assert.Equal(86, before.CP);
        Assert.Equal(278u, packet.Flags);
    }

    [Fact]
    public void SuppliedLiveEvidenceRetainsTotalsWithoutReconstructingEvictedPrefix()
    {
        var fixture = Load();
        Assert.Equal(2, fixture.SchemaVersion);
        Assert.Contains("NOT reconstructed", fixture.Evidence);
        Assert.Equal(10, fixture.Roots[0].StepIndex);
        Assert.Equal(7, fixture.NativePackets.Length);
        Assert.Equal(6, fixture.RawNativeReturns.Count(value => value == true));
        Assert.Null(fixture.RawNativeReturns[0]); // Byregot request/return was evicted, not guessed true.
        Assert.Equal(16, fixture.SourceSummary.GetProperty("MatchedAcknowledgements").GetInt32());
        Assert.Equal(15, fixture.SourceSummary.GetProperty("CorrelatedActions").GetInt32());
        Assert.Equal(0, fixture.SourceSummary.GetProperty("HistoricalSearchesDispatched").GetInt32());
        Assert.Equal(0, fixture.SourceSummary.GetProperty("RejectedResults").GetInt32());
        Assert.Equal(0, fixture.SourceSummary.GetProperty("PredictionMismatches").GetInt32());
        Assert.Equal(JsonValueKind.Null, fixture.SourceSummary.GetProperty("BlockedReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, fixture.SourceSummary.GetProperty("LastMismatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, fixture.SourceSummary.GetProperty("NativeLastMismatch").ValueKind);
        Assert.True(fixture.SourceSummary.GetProperty("ManagedCraftEnded").GetBoolean());
        Assert.Equal(64, fixture.SourceSummary.GetProperty("TotalEvents").GetInt32() - fixture.SourceSummary.GetProperty("DroppedEvents").GetInt32());
        Assert.Equal(fixture.EvidenceSessionId, fixture.Transaction.Request.SessionId);
        Assert.NotEqual(fixture.DecisionSessionId, fixture.EvidenceSessionId); // Separate coordinator/confirmation identities are intentional.
        Assert.Equal(16, fixture.Transaction.Ordinal);
        Assert.Equal(16, fixture.Transaction.Request.RequestId);
        Assert.Equal(VulcanSkill.Groundwork, fixture.Transaction.Action);
        Assert.Equal(100410u, fixture.Transaction.NativeActionId);
        Assert.True(fixture.Transaction.NativeReturn);
        Assert.True(fixture.Transaction.AcknowledgementMatched);
        Assert.False(fixture.Transaction.FullFrameCorrelated);
        Assert.Equal(AdaptiveTransactionOutcome.TerminalConfirmed, fixture.Transaction.Outcome);
        Assert.False(fixture.SourceSummary.GetProperty("OutstandingTransaction").GetBoolean());
        Assert.False(fixture.SourceSummary.GetProperty("NativeLayoutValidated").GetBoolean());
        Assert.False(fixture.SourceSummary.GetProperty("EvidenceExecutionAuthorized").GetBoolean());
        Assert.Contains("awaiting managed end", fixture.SourceSummary.GetProperty("OriginalStatus").GetString()); // Preserve original stale display as source evidence.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RetainedSuffixReplaysAllIndependentlyCapturedFields(int index)
    {
        var fixture = Load();
        var before = fixture.Roots[index];
        var after = fixture.Roots[index + 1];
        var packet = fixture.NativePackets[index];
        var simulation = new AdaptiveSimulation(fixture.Craft, AdaptiveCraftGoal.Resolve(fixture.Craft, AdaptiveGoalKind.Quality, 12000));
        // Availability/combo prefix was evicted. These explicit projection values are not native history;
        // none of the retained suffix actions depends on either value. Compare only captured fields.
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(before.ToState(false, VulcanSkill.None), fixture.Actions[index], out var predicted));
        var conditionMatches = after.Condition == predicted.Condition || before.Condition == Condition.Normal
            && after.Condition is Condition.Good or Condition.Excellent;
        Assert.True(conditionMatches);
        Assert.Equal(after.ToState(false, VulcanSkill.None), predicted with { Condition = after.Condition, Combo = VulcanSkill.None });
        Assert.Equal(before.StepIndex + 1, after.StepIndex);
        Assert.Equal(after.StepIndex, packet.StepIndex);
        Assert.Equal(after.Progress, packet.Progress);
        Assert.Equal(after.Quality, packet.Quality);
        Assert.Equal(after.Durability, packet.Durability);
        Assert.Equal((int)after.Condition + 1, packet.ConditionPlusOne);
        Assert.Equal(new uint[] { 100346, 100474, 19304, 100410, 100410, 100410 }[index], packet.ActionId);
        Assert.True(packet.ActionSucceeded);
        Assert.False(packet.Complete);
    }

    [Fact]
    public void SuppliedGroundworkCompletesAtZeroDurabilityWithoutFinalFrame()
    {
        var fixture = Load();
        var before = fixture.Roots[^1];
        var packet = fixture.NativePackets[^1];
        var simulation = new AdaptiveSimulation(fixture.Craft, AdaptiveCraftGoal.Resolve(fixture.Craft, AdaptiveGoalKind.Quality, 12000));
        Assert.Equal(AdaptiveOutcome.Success, simulation.TryApply(before.ToState(false, VulcanSkill.None), fixture.Actions[^1], out var predicted));
        Assert.Equal(6824, predicted.Progress); // Packet clamps actual completion at required6600.
        Assert.Equal(fixture.Craft.RequiredProgress, packet.Progress);
        Assert.Equal(12000, packet.Quality);
        Assert.Equal(0, packet.Durability);
        Assert.Equal(30u, packet.Flags);
        Assert.True(packet.ActionSucceeded);
        Assert.True(packet.Complete);
        Assert.Equal(before.StepIndex, packet.StepIndex);
        Assert.Equal((int)before.Condition + 1, packet.ConditionPlusOne);
        Assert.Equal(5118, before.Progress);
        Assert.Equal(20, before.Durability);
        Assert.Equal(110, before.CP);
        Assert.Equal(1, before.Veneration);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(packet));
        Assert.False(json.RootElement.TryGetProperty("CP", out _));
        Assert.False(json.RootElement.TryGetProperty("Veneration", out _));
        Assert.Equal(packet.Sequence + 1, fixture.Finish.Sequence);
        Assert.Equal(4, fixture.Finish.Operation);
        Assert.False(fixture.Finish.ActionSucceeded); // Finish itself is not success evidence.
    }

    private sealed class GroundworkScheduler : IAdaptiveSearchScheduler
    {
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            var simulation = new AdaptiveSimulation(request.Identity.Craft, request.Identity.Goal);
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, request.Identity.State, new[] { VulcanSkill.Groundwork }, out var plan));
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found, plan, new(0, 0, 0, TimeSpan.Zero, false), "Synthetic one-action live lifecycle test; not evicted native prefix."));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyntheticZeroDurabilityLiveLifecycleEndsWithHonestLiveExport(bool nativeReturn)
    {
        var fixture = Load();
        var craft = fixture.Craft with { MaxDurability = 20, RequiredProgress = 600 };
        var initial = new AdaptiveObservedFrame(1, 0, 0, 20, 664, Condition.Normal);
        using var coordinator = new AdaptiveSolveCoordinator(new GroundworkScheduler());
        using var trial = new AdaptiveLiveTrial(coordinator, craft, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly),
            initial, new(1, craft.RecipeId, 0, 2), TimeSpan.Zero, TimeSpan.Zero);
        trial.Tick(1, TimeSpan.Zero, initial, false, false);
        trial.Tick(2, TimeSpan.Zero, initial, false, false);
        Assert.Equal(AdaptiveDispatchResult.AwaitingOutcome, trial.TryDispatch(TimeSpan.Zero, initial, 100410, true, () => nativeReturn));
        trial.ObserveNative(new(1, 3, 9, craft.RecipeId, 0, 100410, 1, 600, 0, 0, 1, 30), TimeSpan.FromSeconds(2));
        Assert.Equal(AdaptiveTransactionOutcome.TerminalConfirmed, trial.Transaction!.Outcome);
        Assert.Equal(initial, trial.Evidence.Summary.LastStableFrame);
        Assert.False(trial.HasOutstandingTransaction);
        Assert.Contains("awaiting managed end", trial.Status);
        trial.ObserveNative(new(1, 4, 4, craft.RecipeId, 0), TimeSpan.FromSeconds(3));
        trial.End(TimeSpan.FromSeconds(4), true);
        Assert.Equal(AdaptiveDecisionPhase.Completed, trial.Decision.Phase);
        Assert.True(trial.ManagedCraftEnded);
        Assert.Contains("managed craft ended", trial.Status);
        Assert.DoesNotContain("awaiting managed end", trial.Status);
        Assert.DoesNotContain("awaiting managed end", trial.Evidence.Summary.SessionEndReason);
        var status = trial.Status;
        trial.End(TimeSpan.FromSeconds(5), true); // Duplicate teardown cannot downgrade success or reclassify evidence.
        Assert.Equal(status, trial.Status);
        Assert.Equal(AdaptiveDecisionPhase.Completed, trial.Decision.Phase);
        var evidence = AdaptiveLiveEvidenceTrace.FromConfirmation(trial.Evidence.CaptureTrace());
        var liveTrace = trial.Evidence.CaptureTrace();
        Assert.Throws<ArgumentException>(() => AdaptiveLiveEvidenceTrace.FromConfirmation(liveTrace with
        {
            Summary = liveTrace.Summary with
            {
                HistoricalAnalysis = liveTrace.Summary.HistoricalAnalysis with { ObservationsSubmitted = 1 },
            },
        }));
        var view = new AdaptiveLiveView(trial.Status, "Synthetic progress-only policy", trial.Decision,
            trial.ManagedCraftEnded, trial.Transaction, trial.HasOutstandingTransaction, false, evidence);
        var exported = AdaptiveLiveExport.Serialize(view);
        using var json = JsonDocument.Parse(exported);
        Assert.Equal(4, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(json.RootElement.GetProperty("NativeLayoutValidated").GetBoolean());
        var saved = json.RootElement.GetProperty("Evidence");
        Assert.False(saved.TryGetProperty("AnalysisUsage", out _));
        Assert.False(saved.TryGetProperty("HistoricalResults", out _));
        Assert.Contains("Adaptive confirmation", saved.GetProperty("EvidenceUsage").GetString());
        Assert.Contains("selected solver", saved.GetProperty("EvidenceUsage").GetString());
        Assert.False(saved.GetProperty("Summary").GetProperty("NativeEvidence").GetProperty("ExecutionAuthorized").GetBoolean());
        Assert.DoesNotContain("Historical shadow analysis", exported);
        Assert.DoesNotContain("Execution remains disabled", exported);
        Assert.DoesNotContain("Shadow terminal observation", exported);
        Assert.DoesNotContain("NOT native action-ID confirmation", exported);
        Assert.Equal(initial, evidence.Summary.LastStableFrame);
        Assert.Equal(0, evidence.Summary.CorrelatedActions);
        Assert.Equal(1, evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
        trial.Dispose();
        Assert.Equal(exported, AdaptiveLiveExport.Serialize(view)); // Detached snapshots survive disposal unchanged.
        Assert.Throws<NotSupportedException>(() => ((IList<AdaptiveShadowEvent>)evidence.Events).Clear());
    }
}
