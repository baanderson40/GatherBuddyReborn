using GatherBuddy.Vulcan;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class SimulatorAuditTests
{
    // Base progress = 102, base quality = 135. Simple enough to calculate independently.
    internal static CraftState Craft() => new()
    {
        StatCraftsmanship = 1000, StatControl = 1000, StatCP = 600, StatLevel = 100,
        CraftLevel = 100, CraftDurability = 80, CraftProgress = 2000, CraftQualityMax = 5000,
        CraftProgressDivider = 100, CraftProgressModifier = 100,
        CraftQualityDivider = 100, CraftQualityModifier = 100, UnlockedManipulation = true,
        CraftHQ = true, CraftConditionProbabilities = [1, 0.25f, 0.04f],
    };

    internal static StepState Step() => new()
    {
        Index = 1, Durability = 80, RemainingCP = 600, Condition = Condition.Normal,
        TrainedPerfectionAvailable = true,
    };

    private static StepState Apply(VulcanSkill action, StepState? step = null, CraftState? craft = null)
    {
        var (result, next) = Simulator.Execute(craft ?? Craft(), step ?? Step(), action, 0, 1);
        Assert.Equal(Simulator.ExecuteResult.Succeeded, result);
        return next;
    }

    [Theory]
    [InlineData(VulcanSkill.BasicSynthesis, 122, 0, 0, 10)]
    [InlineData(VulcanSkill.CarefulSynthesis, 183, 0, 7, 10)]
    [InlineData(VulcanSkill.Groundwork, 367, 0, 18, 20)]
    [InlineData(VulcanSkill.BasicTouch, 0, 135, 18, 10)]
    [InlineData(VulcanSkill.StandardTouch, 0, 168, 32, 10)]
    [InlineData(VulcanSkill.AdvancedTouch, 0, 202, 46, 10)]
    [InlineData(VulcanSkill.PreparatoryTouch, 0, 270, 40, 20)]
    [InlineData(VulcanSkill.Reflect, 0, 405, 6, 10)]
    public void HandCalculatedActionFixtures(VulcanSkill action, int progress, int quality, int cp, int durability)
    {
        var next = Apply(action);
        Assert.Equal(progress, next.Progress);
        Assert.Equal(quality, next.Quality);
        Assert.Equal(600 - cp, next.RemainingCP);
        Assert.Equal(80 - durability, next.Durability);
    }

    [Fact]
    public void ComboCostsAndStandaloneRefinedTouchFollowConfirmedRules()
    {
        var basic = Apply(VulcanSkill.BasicTouch);
        Assert.Equal(18, Simulator.GetCPCost(basic, VulcanSkill.StandardTouch));
        Assert.True(Simulator.CanUseAction(Craft(), basic, VulcanSkill.RefinedTouch));
        Assert.True(Simulator.CanUseAction(Craft(), Step(), VulcanSkill.RefinedTouch));
        var standard = Apply(VulcanSkill.StandardTouch, basic);
        Assert.Equal(18, Simulator.GetCPCost(standard, VulcanSkill.AdvancedTouch));
        Assert.Equal(18, Simulator.GetCPCost(Step() with { PrevComboAction = VulcanSkill.Observe }, VulcanSkill.AdvancedTouch));
    }

    [Theory]
    [InlineData(VulcanSkill.None)]
    [InlineData(VulcanSkill.TouchCombo)]
    [InlineData((VulcanSkill)999999)]
    public void UnknownAndPseudoActionsAreNotExecutable(VulcanSkill action)
        => Assert.False(Simulator.CanUseAction(Craft(), Step(), action));

    [Fact]
    public void TerminalStatesRejectFurtherActions()
    {
        Assert.False(Simulator.CanUseAction(Craft(), Step() with { Durability = 0 }, VulcanSkill.BasicSynthesis));
        Assert.False(Simulator.CanUseAction(Craft(), Step() with { Progress = 2000 }, VulcanSkill.BasicSynthesis));
    }

    [Fact]
    public void TrainedEyeFillsRatherThanAddsQualityWithoutDurabilityCost()
    {
        var next = Apply(VulcanSkill.TrainedEye, Step() with { Quality = 900 }, Craft() with { CraftLevel = 90 });
        Assert.Equal(5000, next.Quality);
        Assert.Equal(80, next.Durability);
        Assert.Equal(350, next.RemainingCP);
    }

    [Theory]
    [InlineData(Condition.Normal, false)]
    [InlineData(Condition.Good, false)]
    [InlineData(Condition.Excellent, false)]
    [InlineData(Condition.Poor, false)]
    [InlineData(Condition.Normal, true)]
    public void TrainedEyeNeverSpendsDurabilityOrConsumesTrainedPerfection(Condition condition, bool perfection)
    {
        var before = Step() with { Durability = 5, Quality = 900, Condition = condition,
            TrainedPerfectionActive = perfection, WasteNotLeft = 4 };
        Assert.Equal(0, Simulator.GetDurabilityCost(before, VulcanSkill.TrainedEye));
        var next = Apply(VulcanSkill.TrainedEye, before, Craft() with { CraftLevel = 90 });
        Assert.Equal(5, next.Durability);
        Assert.Equal(350, next.RemainingCP);
        Assert.Equal(5000, next.Quality);
        Assert.Equal(perfection, next.TrainedPerfectionActive);
    }

    [Fact]
    public void LowLevelCannotAccumulateInnerQuiet()
    {
        var next = Apply(VulcanSkill.BasicTouch, craft: Craft() with { StatLevel = 10 });
        Assert.Equal(0, next.IQStacks);
    }

    [Fact]
    public void InnerQuietDoubleStacksAndByregotConsumption()
    {
        Assert.Equal(2, Apply(VulcanSkill.Reflect).IQStacks);
        Assert.Equal(2, Apply(VulcanSkill.PreparatoryTouch).IQStacks);
        var refined = Apply(VulcanSkill.RefinedTouch, Apply(VulcanSkill.BasicTouch));
        Assert.Equal(3, refined.IQStacks);
        Assert.Equal(10, Apply(VulcanSkill.BasicTouch, Step() with { IQStacks = 10 }).IQStacks);
        var byregot = Apply(VulcanSkill.ByregotsBlessing, Step() with { IQStacks = 10 });
        Assert.Equal(810, byregot.Quality); // 135 * 300% potency * 200% IQ
        Assert.Equal(0, byregot.IQStacks);
    }

    [Fact]
    public void BuffsTickAndAreConsumedAtCorrectAction()
    {
        var innovation = Apply(VulcanSkill.Innovation);
        Assert.Equal(4, innovation.InnovationLeft);
        var strides = Apply(VulcanSkill.GreatStrides, innovation);
        Assert.Equal(3, strides.InnovationLeft);
        var touch = Apply(VulcanSkill.BasicTouch, strides);
        Assert.Equal(337, touch.Quality); // floor(135 * (1 + 0.5 + 1))
        Assert.Equal(0, touch.GreatStridesLeft);
        Assert.Equal(2, touch.InnovationLeft);
    }

    [Theory]
    [InlineData(VulcanSkill.PreparatoryTouch, Condition.Normal, 945)]
    [InlineData(VulcanSkill.BasicTouch, Condition.Excellent, 1890)]
    public void QualityMultipliersDoNotLoseOnePointThroughFloatError(VulcanSkill action, Condition condition, int expected)
    {
        var next = Apply(action, Step() with { IQStacks = 4, InnovationLeft = 3, Condition = condition },
            Craft() with { StatControl = 1900 }); // Base quality 225; IQ 140%; Innovation 150%.
        Assert.Equal(expected, next.Quality);
    }

    [Fact]
    public void ManipulationCannotRescueZeroDurabilityAndRepairIsCapped()
    {
        var broken = Apply(VulcanSkill.BasicTouch, Step() with { Durability = 10, ManipulationLeft = 3 });
        Assert.Equal(0, broken.Durability);
        Assert.Equal(80, Apply(VulcanSkill.MastersMend, Step() with { Durability = 70 }).Durability);
        Assert.Equal(80, Apply(VulcanSkill.ImmaculateMend, Step() with { Durability = 10 }).Durability);
        var recovered = Apply(VulcanSkill.BasicTouch, Step() with { Durability = 20, ManipulationLeft = 3 });
        Assert.Equal(15, recovered.Durability);
        Assert.Equal(2, recovered.ManipulationLeft);
    }

    [Fact]
    public void FinalAppraisalPreservesConditionAndBuffDurations()
    {
        var next = Apply(VulcanSkill.FinalAppraisal, Step() with { Condition = Condition.Excellent, InnovationLeft = 2 });
        Assert.Equal(1, next.Index);
        Assert.Equal(Condition.Excellent, next.Condition);
        Assert.Equal(2, next.InnovationLeft);
        next = Apply(VulcanSkill.BasicSynthesis, next with { Progress = 1990 });
        Assert.Equal(1999, next.Progress);
        Assert.Equal(0, next.FinalAppraisalLeft);
        Assert.Equal(Condition.Poor, next.Condition);
    }

    [Fact]
    public void TrainedPerfectionPreservesChargeThroughBuffThenConsumesOnCost()
    {
        var next = Apply(VulcanSkill.TrainedPerfection);
        Assert.False(next.TrainedPerfectionAvailable);
        next = Apply(VulcanSkill.Innovation, next);
        Assert.True(next.TrainedPerfectionActive);
        next = Apply(VulcanSkill.Groundwork, next);
        Assert.Equal(80, next.Durability);
        Assert.False(next.TrainedPerfectionActive);
    }

    [Theory]
    [InlineData(VulcanSkill.TrainedEye, true)]
    [InlineData(VulcanSkill.DaringTouch, false)]
    public void TrainedPerfectionConsumptionFollowsActualBaseDurabilityCost(VulcanSkill action, bool remainsActive)
    {
        var next = Apply(action, Step() with { TrainedPerfectionActive = true, ExpedienceLeft = 1 },
            Craft() with { CraftLevel = 90 });
        Assert.Equal(80, next.Durability);
        Assert.Equal(remainsActive, next.TrainedPerfectionActive);
    }

    [Fact]
    public void GroundworkPenaltyUsesActualReducedDurabilityCost()
    {
        Assert.Equal(183, Apply(VulcanSkill.Groundwork, Step() with { Durability = 10 }).Progress);
        Assert.Equal(367, Apply(VulcanSkill.Groundwork, Step() with { Durability = 10, WasteNotLeft = 2 }).Progress);
    }

    [Fact]
    public void UnlocksLevelsAndResourcesAreChecked()
    {
        Assert.False(Simulator.CanUseAction(Craft() with { UnlockedManipulation = false }, Step(), VulcanSkill.Manipulation));
        Assert.False(Simulator.CanUseAction(Craft() with { StatLevel = 1 }, Step(), VulcanSkill.CarefulSynthesis));
        Assert.False(Simulator.CanUseAction(Craft(), Step() with { RemainingCP = 0 }, VulcanSkill.BasicTouch));
        Assert.False(Simulator.CanUseAction(Craft(), Step() with { WasteNotLeft = 1 }, VulcanSkill.PrudentTouch));
        Assert.False(Simulator.CanUseAction(Craft(), Step() with { Index = 2 }, VulcanSkill.Reflect));
    }

    [Theory]
    [InlineData(Condition.Good, Condition.Normal)]
    [InlineData(Condition.Excellent, Condition.Poor)]
    [InlineData(Condition.Poor, Condition.Normal)]
    public void ForcedConditionTransitions(Condition current, Condition expected)
        => Assert.Equal(expected, Apply(VulcanSkill.Observe, Step() with { Condition = current }).Condition);
}
