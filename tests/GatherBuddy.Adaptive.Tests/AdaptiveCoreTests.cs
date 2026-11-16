using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Xunit;

namespace GatherBuddy.Adaptive.Tests;

public class AdaptiveCoreTests
{
    internal static AdaptiveSimulation Simulation(CraftState? craft = null, int target = 500, bool strict = false)
    {
        var snapshot = AdaptiveCraftSnapshot.Capture(craft ?? SimulatorAuditTests.Craft());
        return new(snapshot, AdaptiveCraftGoal.Resolve(snapshot, AdaptiveGoalKind.Quality, target, strict));
    }
    internal static AdaptiveSearchState Initial(StepState? step = null) => AdaptiveSearchState.Capture(step ?? SimulatorAuditTests.Step());
    internal static AdaptiveSearchLimits Limits() => new()
    {
        TimeLimit = null, BeamWidth = 128, Horizon = 8, MaximumTransitions = 30_000, MaximumStoredNodes = 20_000,
    };

    [Fact]
    public void CaptureOwnsValuesAndDoesNotReadMutableProbabilityArray()
    {
        var source = SimulatorAuditTests.Craft();
        var step = SimulatorAuditTests.Step();
        var snapshot = AdaptiveCraftSnapshot.Capture(source);
        var captured = AdaptiveSearchState.Capture(step);
        source.StatCP = 0;
        source.CraftConditionProbabilities[1] = 1;
        step.RemainingCP = 0;
        Assert.Equal(600, snapshot.MaxCP);
        Assert.Equal(600, captured.CP);
        var simulation = new AdaptiveSimulation(snapshot, AdaptiveCraftGoal.Resolve(snapshot, AdaptiveGoalKind.ProgressOnly));
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(captured, VulcanSkill.Innovation, out var next));
        Assert.Equal(Condition.Normal, next.Condition);
        Assert.Equal(582, next.CP);
    }

    [Fact]
    public void ExpertMissionAndUnsupportedConditionsFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { CraftExpert = true }));
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { IsCosmic = true }));
        Assert.Throws<NotSupportedException>(() => AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft() with { ConditionFlags = ConditionFlags.Pliant }));
        Assert.Throws<NotSupportedException>(() => Initial(SimulatorAuditTests.Step() with { HeartAndSoulActive = true }));
        Assert.Throws<ArgumentException>(() => new AdaptiveSearchEngine().Search(Simulation(), Initial() with { Condition = Condition.Unknown }));
    }

    [Fact]
    public void StateKeysDistinguishMechanicsButNotIrrelevantLaterStepIndex()
    {
        var initial = Initial();
        var states = new HashSet<AdaptiveSearchState>
        {
            initial, initial with { FirstStep = false }, initial with { Combo = VulcanSkill.BasicTouch },
            initial with { Innovation = 1 }, initial with { Condition = Condition.Good },
            initial with { TrainedPerfectionAvailable = false }, initial with { TrainedPerfectionActive = true },
            initial with { InnerQuiet = 1 }, initial with { Progress = 1 }, initial with { Quality = 1 },
            initial with { CP = 599 }, initial with { Durability = 70 }, initial with { FinalAppraisal = 1 },
        };
        Assert.Equal(13, states.Count);
        Assert.Equal(Initial(SimulatorAuditTests.Step() with { Index = 2 }), Initial(SimulatorAuditTests.Step() with { Index = 20 }));
    }

    [Fact]
    public void GoalsResolveHqChanceAndCollectabilityWithoutConfusingQualityPercent()
    {
        var craft = AdaptiveCraftSnapshot.Capture(SimulatorAuditTests.Craft());
        var goal = AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.HQChance, 80);
        Assert.NotEqual(4000, goal.DesiredQuality);
        Assert.True(Calculations.GetHQChance(goal.DesiredQuality * 100.0 / craft.MaxQuality) >= 80);
        Assert.True(Calculations.GetHQChance((goal.DesiredQuality - 1) * 100.0 / craft.MaxQuality) < 80);
        Assert.Equal(5000, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.HQChance, 100).DesiredQuality);
        craft = craft with { Collectable = true, Collectability1 = 1000, Collectability2 = 2000, Collectability3 = 3000, RequiredQuality = 500 };
        Assert.Equal(2000, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 2).DesiredQuality);
        Assert.Equal(500, AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.ProgressOnly).DesiredQuality);
        Assert.Throws<ArgumentException>(() => AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.HQChance));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveCraftGoal.Resolve(craft, AdaptiveGoalKind.Collectability, 4));
    }

    [Fact]
    public void LastDurabilityCompletionWinsButMandatoryQualityIsStillRequired()
    {
        var simulation = Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        var initial = Initial() with { Durability = 10 };
        Assert.Equal(AdaptiveOutcome.Success, simulation.TryApply(initial, VulcanSkill.BasicSynthesis, out var next));
        Assert.Equal(0, next.Durability);
        Assert.Equal(AdaptiveOutcome.Illegal, simulation.TryApply(next, VulcanSkill.BasicSynthesis, out _));
        simulation = Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100, CraftRequiredQuality = 100 }, 100);
        Assert.Equal(AdaptiveOutcome.Failure, simulation.TryApply(initial, VulcanSkill.BasicSynthesis, out _));
    }

    [Fact]
    public void ReliableActionAllowlistRejectsPseudoRiskySpecialistAndUnknownActions()
    {
        var simulation = Simulation();
        foreach (var action in new[] { VulcanSkill.None, VulcanSkill.TouchCombo, VulcanSkill.RapidSynthesis,
            VulcanSkill.HastyTouch, VulcanSkill.HeartAndSoul, VulcanSkill.QuickInnovation,
            VulcanSkill.MaterialMiracle, (VulcanSkill)999999 })
            Assert.Equal(AdaptiveOutcome.Illegal, simulation.TryApply(Initial(), action, out _));
    }

    [Fact]
    public void CurrentConditionIsRespectedAndUnknownFuturesAreNormalExceptForcedTransitions()
    {
        var simulation = Simulation();
        var initial = Initial() with { Condition = Condition.Excellent };
        Assert.Equal(AdaptiveOutcome.Nonterminal, simulation.TryApply(initial, VulcanSkill.FinalAppraisal, out var next));
        Assert.Equal(Condition.Excellent, next.Condition);
        Assert.True(next.FirstStep);
        simulation.TryApply(next, VulcanSkill.PreciseTouch, out next);
        Assert.Equal(810, next.Quality);
        Assert.Equal(Condition.Poor, next.Condition);
        Assert.False(next.FirstStep);
        Assert.Equal(AdaptiveOutcome.Illegal, simulation.TryApply(next, VulcanSkill.PreciseTouch, out _));
        simulation.TryApply(next, VulcanSkill.Observe, out next);
        Assert.Equal(Condition.Normal, next.Condition);
    }

    [Fact]
    public void PlanValidationRejectsIllegalExtraAndBelowMandatoryActions()
    {
        var simulation = Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, Initial(), [VulcanSkill.BasicSynthesis], out var plan));
        Assert.NotNull(plan);
        Assert.False(AdaptivePlanValidator.TryValidate(simulation, Initial(), [VulcanSkill.BasicSynthesis, VulcanSkill.BasicTouch], out _));
        Assert.False(AdaptivePlanValidator.TryValidate(simulation, Initial(), [VulcanSkill.PreciseTouch], out _));
        simulation = Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100, CraftRequiredQuality = 100 }, 100);
        Assert.False(AdaptivePlanValidator.TryValidate(simulation, Initial(), [VulcanSkill.BasicSynthesis], out _));
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, Initial(), [VulcanSkill.BasicTouch, VulcanSkill.BasicSynthesis], out _));
    }

    [Fact]
    public void PlansOwnTheirActionStorage()
    {
        var simulation = Simulation(SimulatorAuditTests.Craft() with { CraftProgress = 100 }, 0);
        var actions = new[] { VulcanSkill.BasicSynthesis };
        Assert.True(AdaptivePlanValidator.TryValidate(simulation, Initial(), actions, out var plan));
        actions[0] = VulcanSkill.None;
        Assert.Equal(VulcanSkill.BasicSynthesis, Assert.Single(plan!.Actions));
        Assert.False(plan.Actions is VulcanSkill[]);
    }

    [Fact]
    public void StandaloneStandardTouchDoesNotGrantAdvancedComboAndAmbiguousLiveHistoryIsRejected()
    {
        var simulation = Simulation();
        simulation.TryApply(Initial(), VulcanSkill.StandardTouch, out var standalone);
        Assert.Equal(VulcanSkill.None, standalone.Combo);
        simulation.TryApply(standalone, VulcanSkill.AdvancedTouch, out var advanced);
        Assert.Equal(46, standalone.CP - advanced.CP);
        simulation.TryApply(Initial(), VulcanSkill.BasicTouch, out var basic);
        simulation.TryApply(basic, VulcanSkill.StandardTouch, out var standard);
        Assert.Equal(VulcanSkill.StandardTouch, standard.Combo);
        simulation.TryApply(standard, VulcanSkill.AdvancedTouch, out advanced);
        Assert.Equal(18, standard.CP - advanced.CP);
        var ambiguous = SimulatorAuditTests.Step() with { Index = 2, PrevComboAction = VulcanSkill.StandardTouch };
        Assert.Throws<NotSupportedException>(() => AdaptiveSearchState.Capture(ambiguous));
        Assert.Equal(VulcanSkill.None, AdaptiveSearchState.Capture(ambiguous, false).Combo);
        Assert.Equal(VulcanSkill.StandardTouch, AdaptiveSearchState.Capture(ambiguous, true).Combo);
    }
}
