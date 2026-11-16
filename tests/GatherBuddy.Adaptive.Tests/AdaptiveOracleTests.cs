using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveOracleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(18, 100)]
    [InlineData(36, 200)]
    [InlineData(54, 400)]
    [InlineData(54, 5000)]
    public void SmallSearchMatchesExhaustiveEnumeration(int cp, int target)
    {
        var craft = SimulatorAuditTests.Craft() with { StatLevel = 18, CraftLevel = 18, CraftProgress = 180, CraftDurability = 40, StatCP = cp };
        var simulation = AdaptiveCoreTests.Simulation(craft, target);
        var initial = AdaptiveCoreTests.Initial() with { CP = cp, Durability = 40, FirstStep = false, TrainedPerfectionAvailable = false };
        var oracle = Enumerate(simulation, initial, 4);
        var limits = AdaptiveCoreTests.Limits() with
        {
            BeamWidth = 8192, Horizon = 4, MaximumPlanLength = 4, MaximumTransitions = 200_000,
            MaximumStoredNodes = 100_000, CompletionProbesPerLayer = 0,
        };
        var result = new AdaptiveSearchEngine().Search(simulation, initial, limits);
        Assert.NotNull(oracle);
        Assert.Equal(AdaptiveSearchStatus.Found, result.Status);
        Assert.Equal(oracle.TargetReached, result.Plan!.TargetReached);
        if (!oracle.TargetReached) Assert.Equal(oracle.FinalState.Quality, result.Plan.FinalState.Quality);
        Assert.Equal(oracle.Duration, result.Plan.Duration);
        Assert.Equal(oracle.Actions.Count, result.Plan.Actions.Count);
    }

    // Exhaustive path enumeration intentionally does not use engine pruning, beams, or its comparator.
    private static AdaptivePlan? Enumerate(AdaptiveSimulation simulation, AdaptiveSearchState initial, int maximumDepth)
    {
        AdaptivePlan? best = null;
        var path = new List<VulcanSkill>();
        void Visit(AdaptiveSearchState state)
        {
            if (simulation.Classify(state) == AdaptiveOutcome.Success)
            {
                if (AdaptivePlanValidator.TryValidate(simulation, initial, path, out var candidate)
                    && (best == null || candidate!.TargetReached && !best.TargetReached
                        || candidate!.TargetReached == best.TargetReached
                            && ((!candidate.TargetReached && candidate.FinalState.Quality > best.FinalState.Quality)
                                || (candidate.TargetReached || candidate.FinalState.Quality == best.FinalState.Quality)
                                    && (candidate.Duration < best.Duration
                                        || candidate.Duration == best.Duration && candidate.Actions.Count < best.Actions.Count))))
                    best = candidate;
                return;
            }
            if (path.Count == maximumDepth) return;
            foreach (var action in AdaptiveSimulation.Actions)
            {
                var outcome = simulation.TryApply(state, action, out var next);
                if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) continue;
                path.Add(action);
                Visit(next);
                path.RemoveAt(path.Count - 1);
            }
        }
        Visit(initial);
        return best;
    }

    [Fact]
    public void SeededStateSweepNeverReturnsAnInvalidPlanOrMutatesInputs()
    {
        var random = new Random(9271);
        for (var i = 0; i < 40; ++i)
        {
            var craft = SimulatorAuditTests.Craft() with { CraftProgress = random.Next(1, 1200), CraftDurability = 40 };
            var simulation = AdaptiveCoreTests.Simulation(craft, random.Next(0, 1500));
            var initial = AdaptiveCoreTests.Initial() with
            {
                FirstStep = false, Durability = random.Next(1, 5) * 10, CP = random.Next(0, 601),
                Condition = (Condition)random.Next(0, 4), Quality = random.Next(0, 400), InnerQuiet = random.Next(0, 11),
            };
            var original = initial;
            var result = new AdaptiveSearchEngine().Search(simulation, initial,
                AdaptiveCoreTests.Limits() with { MaximumTransitions = 1500, MaximumStoredNodes = 1000 });
            Assert.Equal(original, initial);
            Assert.True(result.Diagnostics.Transitions <= 1500);
            Assert.True(result.Diagnostics.StoredNodes <= 1000);
            Assert.NotEqual(AdaptiveSearchStatus.ProvenImpossible, result.Status);
            if (result.Plan == null) continue;
            Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, result.Plan.Actions, out _));
            Assert.True(result.Plan.FinalState.Progress >= craft.CraftProgress);
            Assert.True(result.Plan.FinalState.Quality >= simulation.Goal.MandatoryQuality);
        }
    }
}
