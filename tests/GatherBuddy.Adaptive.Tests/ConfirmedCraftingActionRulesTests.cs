using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class ConfirmedCraftingActionRulesTests
{
    [Theory]
    [InlineData(VulcanSkill.None, 1)]
    [InlineData(VulcanSkill.BasicTouch, 2)]
    [InlineData(VulcanSkill.StandardTouch, 1)]
    [InlineData(VulcanSkill.Observe, 1)]
    public void RefinedTouchIsLegalStandaloneAndExtraIqRequiresBasicTouch(VulcanSkill previous, int iqGain)
    {
        var craft = SimulatorAuditTests.Craft();
        var before = SimulatorAuditTests.Step() with { Index = 3, PrevComboAction = previous };
        Assert.True(Simulator.CanUseAction(craft, before, VulcanSkill.RefinedTouch));
        var (result, after) = Simulator.Execute(craft, before, VulcanSkill.RefinedTouch, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        Assert.Equal(135, after.Quality);
        Assert.Equal(576, after.RemainingCP);
        Assert.Equal(70, after.Durability);
        Assert.Equal(4, after.Index);
        Assert.Equal(iqGain, after.IQStacks);
    }

    [Fact]
    public void BasicThenRefinedReachesThreeTotalIqFromZeroAndRespectsCapAndUnlock()
    {
        var craft = SimulatorAuditTests.Craft();
        var (_, basic) = Simulator.Execute(craft, SimulatorAuditTests.Step(), VulcanSkill.BasicTouch, 0, 1);
        Assert.Equal(1, basic.IQStacks);
        var (_, refined) = Simulator.Execute(craft, basic, VulcanSkill.RefinedTouch, 0, 1);
        Assert.Equal(3, refined.IQStacks);
        var (_, capped) = Simulator.Execute(craft, basic with { IQStacks = 9 }, VulcanSkill.RefinedTouch, 0, 1);
        Assert.Equal(10, capped.IQStacks);
        Assert.False(Simulator.CanUseAction(craft with { StatLevel = 91 }, basic, VulcanSkill.RefinedTouch));
        Assert.False(Simulator.CanUseAction(craft, basic with { RemainingCP = 23 }, VulcanSkill.RefinedTouch));
    }

    [Fact]
    public void AdaptivePublicAndWorkspacePathsAcceptStandaloneRefinedTouch()
    {
        var simulation = AdaptiveCoreTests.Simulation();
        var before = AdaptiveCoreTests.Initial() with { FirstStep = false };
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(before, VulcanSkill.RefinedTouch, out var after));
        Assert.Equal(1, after.InnerQuiet);
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.CreateWorkspace().TryApply(before, VulcanSkill.RefinedTouch, out var scratch));
        Assert.Equal(after, scratch);
    }

    [Theory]
    [InlineData(Condition.Normal)]
    [InlineData(Condition.Good)]
    [InlineData(Condition.Excellent)]
    [InlineData(Condition.Poor)]
    public void StandaloneRefinedIsSearchDominatedByBasicButInputReplayRemainsLegal(Condition condition)
    {
        var simulation = AdaptiveCoreTests.Simulation();
        foreach (var combo in new[] { VulcanSkill.None, VulcanSkill.Observe, VulcanSkill.StandardTouch })
        foreach (var iq in new[] { 0, 9, 10 })
        foreach (var perfection in new[] { false, true })
        {
            var before = AdaptiveCoreTests.Initial() with { FirstStep = false, Condition = condition,
                Combo = combo, InnerQuiet = iq, TrainedPerfectionActive = perfection,
                WasteNot = 3, Manipulation = 4, Innovation = 2, GreatStrides = 1 };
            Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(before, VulcanSkill.BasicTouch, out var basic));
            Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(before, VulcanSkill.RefinedTouch, out var refined));
            Assert.Equal(basic with { CP = refined.CP, Combo = refined.Combo }, refined);
            Assert.Equal(refined.CP + 6, basic.CP);
            Assert.Equal(VulcanSkill.BasicTouch, basic.Combo);
            Assert.True(AdaptiveSearchPruning.DominatedStandaloneRefinedTouch(before, VulcanSkill.RefinedTouch));
            Assert.False(AdaptiveSearchPruning.DominatedStandaloneRefinedTouch(before, VulcanSkill.BasicTouch));
            Assert.Equal(AdaptiveSimulation.ActionDuration(VulcanSkill.BasicTouch), AdaptiveSimulation.ActionDuration(VulcanSkill.RefinedTouch));
            Assert.True((int)VulcanSkill.BasicTouch < (int)VulcanSkill.RefinedTouch);
        }
        Assert.False(AdaptiveSearchPruning.DominatedStandaloneRefinedTouch(
            AdaptiveCoreTests.Initial() with { Combo = VulcanSkill.BasicTouch }, VulcanSkill.RefinedTouch));
        var craft = SimulatorAuditTests.Craft() with { CraftProgress = 100 };
        simulation = AdaptiveCoreTests.Simulation(craft, 0);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, AdaptiveCoreTests.Initial(),
            new[] { VulcanSkill.RefinedTouch, VulcanSkill.BasicSynthesis }, out var plan));
        Assert.Equal(VulcanSkill.RefinedTouch, plan!.Actions[0]); // Replay is not rewritten/pruned.
    }

    [Theory]
    [InlineData(Condition.Normal)]
    [InlineData(Condition.Good)]
    [InlineData(Condition.Excellent)]
    [InlineData(Condition.Poor)]
    public void QuickInnovationDoesNotAdvanceStepConditionTimersOrManipulationRepair(Condition condition)
    {
        var craft = SimulatorAuditTests.Craft();
        var before = SimulatorAuditTests.Step() with { Index = 4, Condition = condition, Durability = 30,
            QuickInnoLeft = 1, QuickInnoAvailable = true, WasteNotLeft = 3, ManipulationLeft = 5,
            VenerationLeft = 2, MuscleMemoryLeft = 4, FinalAppraisalLeft = 3, GreatStridesLeft = 2,
            ExpedienceLeft = 1, IQStacks = 4, TrainedPerfectionActive = true };
        var (result, after) = Simulator.Execute(craft, before, VulcanSkill.QuickInnovation, 0, 0);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        Assert.True(Simulator.SkipUpdates(VulcanSkill.QuickInnovation));
        Assert.Equal(before.Index, after.Index);
        Assert.Equal(before.Condition, after.Condition);
        Assert.Equal(before.Durability, after.Durability);
        Assert.Equal(before.RemainingCP, after.RemainingCP);
        Assert.Equal(before.Progress, after.Progress);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Equal(before.IQStacks, after.IQStacks);
        Assert.Equal(before.WasteNotLeft, after.WasteNotLeft);
        Assert.Equal(before.ManipulationLeft, after.ManipulationLeft);
        Assert.Equal(before.GreatStridesLeft, after.GreatStridesLeft);
        Assert.Equal(before.VenerationLeft, after.VenerationLeft);
        Assert.Equal(before.MuscleMemoryLeft, after.MuscleMemoryLeft);
        Assert.Equal(before.FinalAppraisalLeft, after.FinalAppraisalLeft);
        Assert.Equal(before.ExpedienceLeft, after.ExpedienceLeft);
        Assert.True(after.TrainedPerfectionActive);
        Assert.Equal(1, after.InnovationLeft);
        Assert.Equal(0, after.QuickInnoLeft);
        Assert.False(after.QuickInnoAvailable);
        Assert.False(Simulator.CanUseAction(craft, after, VulcanSkill.QuickInnovation));
        var (_, touch) = Simulator.Execute(craft, after, VulcanSkill.BasicTouch, 0, 1);
        Assert.Equal(before.Index + 1, touch.Index);
        Assert.Equal(0, touch.InnovationLeft);
        Assert.False(Simulator.CanUseAction(craft, touch, VulcanSkill.QuickInnovation));
    }

    [Fact]
    public void QuickInnovationRetainsFirstStepButStillRequiresUnlockChargeAndNoInnovation()
    {
        var craft = SimulatorAuditTests.Craft();
        var before = SimulatorAuditTests.Step() with { QuickInnoLeft = 1 };
        Assert.False(Simulator.CanUseAction(craft with { StatLevel = 95 }, before, VulcanSkill.QuickInnovation));
        Assert.False(Simulator.CanUseAction(craft, before with { QuickInnoLeft = 0 }, VulcanSkill.QuickInnovation));
        Assert.False(Simulator.CanUseAction(craft, before with { InnovationLeft = 1 }, VulcanSkill.QuickInnovation));
        var (_, after) = Simulator.Execute(craft, before, VulcanSkill.QuickInnovation, 0, 1);
        Assert.Equal(1, after.Index);
        Assert.True(Simulator.CanUseAction(craft, after, VulcanSkill.MuscleMemory));
        Assert.True(Simulator.CanUseAction(craft, after, VulcanSkill.Reflect));
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(95, 0)]
    [InlineData(96, 1)]
    [InlineData(100, 1)]
    public void SuccessfulHastyTouchGrantsExpedienceOnlyAtTraitLevel(int level, int expedience)
    {
        var craft = SimulatorAuditTests.Craft() with { StatLevel = level };
        var (result, after) = Simulator.Execute(craft, SimulatorAuditTests.Step(), VulcanSkill.HastyTouch, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        Assert.Equal(expedience, after.ExpedienceLeft);
        Assert.Equal(level >= 96, Simulator.CanUseAction(craft, after, VulcanSkill.DaringTouch));
    }

    [Fact]
    public void StandaloneRefinedTouchRawRustLegalityErratumHasBoundedSpecCoverage()
    {
        var count = 0;
        foreach (var recipe in ReferenceCorpus.Loaded.Recipes)
        foreach (var row in ReferenceCorpus.Loaded.Transitions.Where(r => r.RecipeKey == recipe.Key
                     && ReferenceCorpus.IsStandaloneRefinedTouchErratum(recipe, r)))
        {
            ++count;
            Assert.Null(row.After); // Raw Rust never supplied a standalone outcome; not independent parity.
            var simulation = new AdaptiveSimulation(recipe.Craft,
                AdaptiveCraftGoal.Resolve(recipe.Craft, AdaptiveGoalKind.Quality, recipe.TargetQuality));
            var outcome = simulation.TryApply(row.Before, row.Action, out var after);
            Assert.NotEqual(AdaptiveOutcome.Illegal, outcome);
            Assert.Equal(row.Before.CP - 24, after.CP);
            Assert.Equal(row.Before.Progress, after.Progress);
            Assert.Equal(Math.Min(10, row.Before.InnerQuiet + 1), after.InnerQuiet);
            var cost = row.Before.TrainedPerfectionActive ? 0 : row.Before.WasteNot > 0 ? 5 : 10;
            var durability = row.Before.Durability - cost;
            if (durability > 0 && row.Before.Manipulation > 0) durability = Math.Min(recipe.Craft.MaxDurability, durability + 5);
            Assert.Equal(durability, after.Durability);
            var condition = row.Before.Condition switch { Condition.Good => 1.5m, Condition.Excellent => 4m, Condition.Poor => 0.5m, _ => 1m };
            var buffs = 1m + (row.Before.Innovation > 0 ? 0.5m : 0m) + (row.Before.GreatStrides > 0 ? 1m : 0m);
            var gain = (int)decimal.Floor(recipe.BaseQuality * condition * buffs * (1m + row.Before.InnerQuiet / 10m));
            Assert.Equal(Math.Min(recipe.Craft.MaxQuality, row.Before.Quality + gain), after.Quality);
        }
        Assert.Equal(360, count);
    }
}
