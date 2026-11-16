using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveTrainedEyeTests
{
    private sealed record Fixture(AdaptiveCraftSnapshot Craft, AdaptiveObservedFrame Initial,
        VulcanSkill[] Plan, uint NativeActionId, AdaptiveNativeCaptureContext NativeContext,
        AdaptiveNativeCraftEvent Packet, int OriginalExpectedDurability);
    private static Fixture Load() => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "live-schema4-trained-eye-durability-mismatch.json")))!;

    private sealed class Scheduler(Fixture fixture) : IAdaptiveSearchScheduler
    {
        public int Calls;
        public Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation)
        {
            ++Calls;
            var simulation = new AdaptiveSimulation(request.Identity.Craft, request.Identity.Goal);
            var actions = request.Identity.State.FirstStep ? fixture.Plan : [VulcanSkill.Groundwork];
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, request.Identity.State, actions, out var plan));
            return Task.FromResult(new AdaptiveSearchResult(AdaptiveSearchStatus.Found, plan,
                new(0, 0, 0, TimeSpan.Zero, false), "Synthetic scheduler; captured first native packet only."));
        }
    }

    [Fact]
    public void CapturedRootReplaysTwoActionSeedWithZeroTrainedEyeDurabilityCost()
    {
        var f = Load();
        var simulation = new AdaptiveSimulation(f.Craft, AdaptiveCraftGoal.Resolve(f.Craft, AdaptiveGoalKind.Quality, 5200));
        var initial = f.Initial.ToState(true, VulcanSkill.None);
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(initial, VulcanSkill.TrainedEye, out var next));
        Assert.Equal(40, next.Durability);
        Assert.Equal(414, next.CP); // Modeled CP, not an independently supplied post-action observation.
        Assert.Equal(5200, next.Quality);
        Assert.Equal(0, next.Progress);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, f.Plan, out var seed));
        Assert.Equal(20, seed!.FinalState.Durability);
        Assert.Equal(6, seed.Duration);
        var result = new AdaptiveSearchEngine().Search(simulation, initial, new() { TimeLimit = null }, f.Plan);
        Assert.NotNull(result.Plan);
        Assert.False(AdaptivePlanValidator.IsBetter(seed, result.Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPacketMatchesButErroneousTenDurabilityLossStillFailsStrictly(bool wrongDurability)
    {
        var f = Load();
        var scheduler = new Scheduler(f);
        using var coordinator = new AdaptiveSolveCoordinator(scheduler);
        using var trial = new AdaptiveLiveTrial(coordinator, f.Craft,
            AdaptiveCraftGoal.Resolve(f.Craft, AdaptiveGoalKind.Quality, 5200), f.Initial,
            f.NativeContext, TimeSpan.Zero, TimeSpan.Zero);
        trial.Tick(1, TimeSpan.FromMilliseconds(33), f.Initial, false, false);
        trial.Tick(2, TimeSpan.FromMilliseconds(66), f.Initial, false, false);
        Assert.Equal(VulcanSkill.TrainedEye, trial.Decision.Action);
        Assert.Equal(AdaptiveDispatchResult.AwaitingOutcome,
            trial.TryDispatch(TimeSpan.FromMilliseconds(101), f.Initial, f.NativeActionId, true, () => true));
        var packet = wrongDurability ? f.Packet with { Durability = f.OriginalExpectedDurability } : f.Packet;
        trial.ObserveNative(packet, TimeSpan.FromMilliseconds(2304));
        if (wrongDurability)
        {
            Assert.True(trial.Stopped);
            Assert.Equal(AdaptiveTransactionOutcome.ProtocolFailure, trial.Transaction!.Outcome);
            Assert.Contains(trial.Evidence.Summary.NativeEvidence!.LastMismatch!.Differences, d => d.Field == "Durability");
        }
        else
        {
            Assert.False(trial.Stopped);
            Assert.True(trial.Transaction!.AcknowledgementMatched);
            Assert.False(trial.Transaction.FullFrameCorrelated);
            Assert.True(trial.HasOutstandingTransaction);
            Assert.Equal(1, trial.Evidence.Summary.NativeEvidence!.MatchedAcknowledgements);
            Assert.Equal(0, trial.Evidence.CorrelatedActions);
            Assert.Equal(f.Initial, trial.Evidence.Summary.LastStableFrame);
            Assert.False(trial.ManagedCraftEnded);
        }
        Assert.Equal(1, scheduler.Calls); // Neither packet-only acceptance nor failure permits another dispatch/root.
    }
}
