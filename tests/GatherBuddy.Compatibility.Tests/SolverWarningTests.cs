using System.Reflection;
using GatherBuddy.Vulcan;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public sealed class SolverWarningTests
{
    [Theory]
    [InlineData(VulcanSkill.MastersMend, 100)]
    [InlineData(VulcanSkill.ImmaculateMend, 120)]
    [InlineData(VulcanSkill.PreparatoryTouch, 50)]
    public void QualityOverridePreservesBothRepairActions(VulcanSkill recommendation, int cp)
    {
        var craft = new CraftState
        {
            StatLevel = 100, StatCraftsmanship = 3000, StatControl = 3000, StatCP = 600,
            CraftLevel = 100, CraftDurability = 80, CraftProgress = 10000, CraftQualityMax = 10000,
            CraftProgressDivider = 100, CraftProgressModifier = 100, CraftQualityDivider = 100, CraftQualityModifier = 100,
        };
        var step = new StepState
        {
            Index = 5, Durability = 40, RemainingCP = cp, Condition = Condition.Normal, IQStacks = 10,
            // The real GetRecommendation delegates to fallback in this state. Inject only its recommendation;
            // exercise actual Solve's complete repair/quality postprocessing, not a copied pattern expression.
            MaterialMiracleActive = true,
        };
        var solver = new StandardSolver(new StandardSolverConfig());
        typeof(StandardSolver).GetField("_fallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(solver, new FixedSolver(recommendation));
        var actual = solver.Solve(craft, step).Action;
        Assert.Equal(recommendation == VulcanSkill.PreparatoryTouch ? VulcanSkill.ByregotsBlessing : recommendation, actual);
    }

    private sealed class FixedSolver(VulcanSkill action) : Solver
    {
        public override Recommendation Solve(CraftState craft, StepState step) => new(action);
    }

    [Fact]
    public void EveryDefinedConditionRetainsMapping()
    {
        foreach (var condition in Enum.GetValues<Condition>().Where(value => value != Condition.Unknown))
            Assert.Equal((ConditionFlags)(1 << (int)condition), condition.ConditionToFlag());
        Assert.Throws<NotImplementedException>(() => Condition.Unknown.ConditionToFlag());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void UndefinedConditionsFailExplicitly(int value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ((Condition)value).ConditionToFlag());
}
