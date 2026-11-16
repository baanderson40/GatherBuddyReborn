using System.Diagnostics;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GatherBuddy.Adaptive.Tests;

/// <summary>Reproducible synthetic smoke benchmark, not a release acceptance suite.</summary>
public class AdaptiveBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void RepeatedPerTurnSearchAcrossSyntheticCrafts()
    {
        var latencies = new List<double>();
        var completed = 0;
        var blocked = 0;
        var targets = 0;
        var maxStored = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var random = new Random(1337);
        foreach (var level in new[] { 18, 50, 100 })
        foreach (var durability in new[] { 40, 80 })
        foreach (var conditions in new[] { false, true })
        {
            var craft = SimulatorAuditTests.Craft() with
            {
                StatLevel = level, CraftLevel = level, CraftDurability = durability,
                CraftProgress = level == 100 ? 800 : 300, StatCP = level == 18 ? 150 : 600,
            };
            var simulation = AdaptiveCoreTests.Simulation(craft, level == 100 ? 1500 : 400);
            var state = AdaptiveCoreTests.Initial() with
            {
                Durability = durability, CP = craft.StatCP, TrainedPerfectionAvailable = level == 100,
            };
            var limits = AdaptiveCoreTests.Limits() with { MaximumTransitions = 8000, MaximumStoredNodes = 5000 };
            IReadOnlyList<VulcanSkill>? continuation = null;
            for (var turn = 0; turn < 64; ++turn)
            {
                var timer = Stopwatch.StartNew();
                var result = new AdaptiveSearchEngine().Search(simulation, state, limits, continuation);
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                maxStored = Math.Max(maxStored, result.Diagnostics.StoredNodes);
                if (result.Plan == null) { ++blocked; break; }
                Assert.True(AdaptivePlanValidator.TryValidate(simulation, state, result.Plan.Actions, out _));
                var action = result.Plan.Actions[0];
                var previousCondition = state.Condition;
                var outcome = simulation.TryApply(state, action, out state);
                Assert.NotEqual(AdaptiveOutcome.Illegal, outcome);
                Assert.NotEqual(AdaptiveOutcome.Failure, outcome);
                continuation = result.Plan.Actions.Skip(1).ToArray();
                if (outcome == AdaptiveOutcome.Success)
                {
                    ++completed;
                    if (state.Quality >= simulation.Goal.DesiredQuality) ++targets;
                    break;
                }
                if (conditions && action != VulcanSkill.FinalAppraisal)
                {
                    // Independent ordinary-condition transition source, not the search's Normal model.
                    state = state with { Condition = previousCondition switch
                    {
                        Condition.Excellent => Condition.Poor,
                        Condition.Poor or Condition.Good => Condition.Normal,
                        _ => random.NextDouble() switch { < 0.04 => Condition.Excellent, < 0.29 => Condition.Good, _ => Condition.Normal },
                    } };
                }
            }
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        latencies.Sort();
        double Percentile(double p) => latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(latencies.Count * p) - 1)];
        output.WriteLine($"Synthetic crafts: completed={completed}/12; blocked={blocked}; targets={targets}; decisions={latencies.Count}");
        output.WriteLine($"Decision ms: p50={Percentile(0.5):F2}; p95={Percentile(0.95):F2}; p99={Percentile(0.99):F2}");
        output.WriteLine($"Allocated bytes (whole benchmark, not peak resident memory)={allocated}; max cumulative stored nodes/decision={maxStored}");
        Assert.Equal(12, completed + blocked);
        Assert.True(maxStored <= MaximumStoredNodes);
    }

    private const int MaximumStoredNodes = 5000;
}
