using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptivePruningTests
{
    [Theory]
    [InlineData(Condition.Normal)]
    [InlineData(Condition.Good)]
    [InlineData(Condition.Excellent)]
    [InlineData(Condition.Poor)]
    public void OnlyExactCpWastingRefreshIsSuppressed(Condition condition)
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var first = AdaptiveCoreTests.Initial() with { Condition = condition, Manipulation = 5, Innovation = 3 };
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(first, VulcanSkill.FinalAppraisal, out var active));
        Assert.False(AdaptiveSearchPruning.RedundantRefresh(first, VulcanSkill.FinalAppraisal, active));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(active, VulcanSkill.FinalAppraisal, out var repeat));
        Assert.True(AdaptiveSearchPruning.RedundantRefresh(active, VulcanSkill.FinalAppraisal, repeat));
        Assert.Equal(active with { CP = active.CP - 1 }, repeat);
        Assert.Equal(first.Condition, repeat.Condition);
        Assert.Equal(first.FirstStep, repeat.FirstStep);
        Assert.Equal(first.Durability, repeat.Durability);
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(active, VulcanSkill.Observe, out var advanced));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(advanced, VulcanSkill.FinalAppraisal, out var refresh));
        Assert.False(AdaptiveSearchPruning.RedundantRefresh(advanced, VulcanSkill.FinalAppraisal, refresh));
    }

    [Fact]
    public void UsefulFirstAppraisalStillProtectsMandatoryQualityWork()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with
            { CraftProgress = 100, CraftRequiredQuality = 200 }, 200, true);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false };
        Assert.Equal(AdaptiveOutcome.Failure, simulation.TryApply(initial, VulcanSkill.DelicateSynthesis, out _));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(initial, VulcanSkill.FinalAppraisal, out var protectedState));
        Assert.False(AdaptiveSearchPruning.RedundantRefresh(initial, VulcanSkill.FinalAppraisal, protectedState));
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial,
            new[] { VulcanSkill.FinalAppraisal, VulcanSkill.DelicateSynthesis, VulcanSkill.BasicTouch, VulcanSkill.BasicSynthesis }, out var plan));
        Assert.True(plan!.TargetReached);
    }

    [Fact]
    public void InefficientLegalSeedsRemainValidAndReplayIsNotNormalized()
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        var initial = AdaptiveCoreTests.Initial();
        // Final Appraisal prevents immediate synthesis completion; it must expire first.
        VulcanSkill[] actions = [VulcanSkill.FinalAppraisal, VulcanSkill.FinalAppraisal,
            VulcanSkill.Observe, VulcanSkill.Observe, VulcanSkill.Observe, VulcanSkill.Observe, VulcanSkill.Observe,
            VulcanSkill.BasicSynthesis];
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, initial, actions, out var replay));
        var result = new AdaptiveSearchEngine().Search(simulation, initial,
            new() { MaximumTransitions = 1, TimeLimit = null }, actions);
        Assert.Equal("Validated", Assert.Single(result.Diagnostics.Seeding!.Candidates).Status);
        Assert.Equal(actions.Length, result.Diagnostics.Seeding.IncumbentActionCount);
        Assert.Equal(22, replay!.Duration);
    }

    [Fact]
    public void DurationBoundsKeepSubtargetAndExactResourceTies()
    {
        var state = AdaptiveCoreTests.Initial();
        var target = new AdaptivePlan([VulcanSkill.Veneration, VulcanSkill.Groundwork], state, 5, true);
        var shortfall = new AdaptivePlan([VulcanSkill.BasicSynthesis], state, 3, false);
        Assert.Equal(2, AdaptiveSearchPruning.MinimumRemainingDuration);
        Assert.True(AdaptiveSearchPruning.CannotImproveNonterminal(4, 1, target));
        Assert.False(AdaptiveSearchPruning.CannotImproveNonterminal(3, 1, target)); // equal time/count can win resources
        Assert.True(AdaptiveSearchPruning.CannotImproveNonterminal(3, 2, target)); // equal time but too many actions
        Assert.False(AdaptiveSearchPruning.CannotImproveNonterminal(100, 50, shortfall));
        Assert.False(AdaptiveSearchPruning.CannotImproveNonterminal(100, 50, null));
        Assert.True(AdaptiveSearchPruning.CannotImproveNonterminal(int.MaxValue, int.MaxValue, target));
        foreach (var action in AdaptiveSimulation.Actions)
            Assert.True(AdaptiveSimulation.ActionDuration(action) >= AdaptiveSearchPruning.MinimumRemainingDuration);
    }

    [Fact]
    public void ExactStateLabelsPreserveDepthTradeoffsAndLexicalTies()
    {
        var state = AdaptiveCoreTests.Initial();
        var root = new AdaptiveSearchNode(state, -1, VulcanSkill.None, VulcanSkill.None, 0, 0);
        var low = new AdaptiveSearchNode(state, 0, VulcanSkill.GreatStrides, VulcanSkill.GreatStrides, 1, 2);
        var high = new AdaptiveSearchNode(state, 0, VulcanSkill.Innovation, VulcanSkill.Innovation, 1, 2);
        var arena = new List<AdaptiveSearchNode> { root, low, high };
        var lexicalFirst = new AdaptiveSearchNode(state, 1, VulcanSkill.Innovation, low.Action, 2, 4);
        var lexicalLast = new AdaptiveSearchNode(state, 2, VulcanSkill.GreatStrides, high.Action, 2, 4);
        Assert.True(AdaptiveSearchPruning.Dominates(arena, lexicalFirst, lexicalLast));
        Assert.False(AdaptiveSearchPruning.Dominates(arena, lexicalLast, lexicalFirst));
        Assert.False(AdaptiveSearchPruning.Dominates(arena, lexicalFirst, high));
        Assert.False(AdaptiveSearchPruning.Dominates(arena, lexicalFirst with { Duration = 1 }, high)); // faster but longer
        Assert.False(AdaptiveSearchPruning.Dominates(arena, high, lexicalFirst with { Duration = 1 }));
        Assert.True(AdaptiveSearchPruning.Dominates(arena, high, lexicalFirst));
        Assert.False(AdaptiveSearchPruning.Dominates(arena, high, lexicalFirst with { State = state with { CP = state.CP - 1 } }));
        arena.Add(lexicalLast);
        arena.Add(lexicalFirst);
        var seen = new Dictionary<AdaptiveSearchState, int> { [state] = 3 };
        AdaptiveSearchPruning.Remember(arena, seen, 4);
        Assert.Equal(4, seen[state]);
        // A faster but longer label must not replace the representative or be discarded.
        arena.Add(lexicalFirst with { Duration = 1 });
        AdaptiveSearchPruning.Remember(arena, seen, 2);
        Assert.Equal(2, seen[state]);
        AdaptiveSearchPruning.Remember(arena, seen, 5);
        Assert.Equal(2, seen[state]);
    }

    [Theory]
    [InlineData(Condition.Normal, 0, false)]
    [InlineData(Condition.Good, 500, false)]
    [InlineData(Condition.Excellent, 1200, false)]
    [InlineData(Condition.Poor, 500, false)]
    [InlineData(Condition.Normal, 5000, false)]
    [InlineData(Condition.Normal, 500, true)]
    public void PruningMatchesUnprunedExhaustiveRoutes(Condition condition, int target, bool strict)
    {
        var simulation = AdaptiveCoreTests.Simulation(SimulatorAuditTests.Craft() with
            { CraftProgress = 400, CraftRequiredQuality = strict ? 200 : 0 }, target, strict);
        var initial = AdaptiveCoreTests.Initial() with { FirstStep = false, CP = 90, Durability = 30, Condition = condition, InnerQuiet = 4,
            Quality = strict ? 400 : 0 };
        var plain = Enumerate(false);
        var pruned = Enumerate(true);
        Assert.NotNull(plain);
        Assert.NotNull(pruned);
        Assert.False(AdaptivePlanValidator.IsBetter(plain, pruned));
        Assert.False(AdaptivePlanValidator.IsBetter(pruned, plain));
        Assert.Equal(plain.Actions, pruned.Actions);

        AdaptivePlan? Enumerate(bool prune)
        {
            AdaptivePlan? best = null;
            var path = new List<VulcanSkill>();
            Visit(initial, 0);
            return best;

            void Visit(AdaptiveSearchState state, int duration)
            {
                if (simulation.Classify(state) == AdaptiveOutcome.Success)
                {
                    if (AdaptivePlanValidator.TryValidate(simulation, initial, path, out var plan)
                        && AdaptivePlanValidator.IsBetter(plan!, best)) best = plan;
                    return;
                }
                if (path.Count == 3 || (prune && AdaptiveSearchPruning.CannotImproveNonterminal(duration, path.Count, best))) return;
                foreach (var action in AdaptiveSimulation.Actions)
                {
                    var outcome = simulation.TryApply(state, action, out var next);
                    if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) continue;
                    if (prune && AdaptiveSearchPruning.RedundantRefresh(state, action, next)) continue;
                    path.Add(action);
                    Visit(next, duration + AdaptiveSimulation.ActionDuration(action));
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
    }
}
