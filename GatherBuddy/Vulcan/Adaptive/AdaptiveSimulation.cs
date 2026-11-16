using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

public enum AdaptiveOutcome { Illegal, Nonterminal, Success, Failure }

/// <summary>Experimental ordinary-craft projection over the shared production simulator.</summary>
public sealed class AdaptiveSimulation
{
    private readonly CraftState _craft;
    public AdaptiveCraftSnapshot Craft { get; }
    public AdaptiveCraftGoal Goal { get; }

    // Deliberate allowlist: no pseudo actions, probabilistic actions, specialist or mission mechanics.
    private static readonly VulcanSkill[] SupportedActions =
    [
        VulcanSkill.BasicSynthesis, VulcanSkill.CarefulSynthesis, VulcanSkill.Groundwork,
        VulcanSkill.IntensiveSynthesis, VulcanSkill.PrudentSynthesis, VulcanSkill.MuscleMemory,
        VulcanSkill.DelicateSynthesis, VulcanSkill.Veneration, VulcanSkill.MastersMend,
        VulcanSkill.Manipulation, VulcanSkill.WasteNot, VulcanSkill.WasteNot2,
        VulcanSkill.TrainedPerfection, VulcanSkill.ImmaculateMend, VulcanSkill.TricksOfTrade,
        VulcanSkill.Reflect, VulcanSkill.TrainedEye, VulcanSkill.BasicTouch,
        VulcanSkill.StandardTouch, VulcanSkill.AdvancedTouch, VulcanSkill.PreparatoryTouch,
        VulcanSkill.PreciseTouch, VulcanSkill.PrudentTouch, VulcanSkill.RefinedTouch,
        VulcanSkill.TrainedFinesse, VulcanSkill.ByregotsBlessing, VulcanSkill.Innovation,
        VulcanSkill.GreatStrides, VulcanSkill.FinalAppraisal, VulcanSkill.Observe,
    ];
    public static IReadOnlyList<VulcanSkill> Actions { get; } = Array.AsReadOnly(SupportedActions);

    public AdaptiveSimulation(AdaptiveCraftSnapshot craft, AdaptiveCraftGoal goal)
    {
        craft.Validate();
        var mandatory = goal.Kind == AdaptiveGoalKind.Collectability
            ? Math.Max(craft.RequiredQuality, craft.Collectability1) : craft.RequiredQuality;
        if (goal.MandatoryQuality != mandatory || goal.DesiredQuality > craft.MaxQuality)
            throw new ArgumentException("Goal does not match snapshot.");
        Craft = craft;
        Goal = goal;
        _craft = craft.ToSimulatorCraft();
    }

    public AdaptiveOutcome Classify(AdaptiveSearchState state)
    {
        if (state.Progress >= Craft.RequiredProgress)
            return state.Quality >= Goal.MandatoryQuality ? AdaptiveOutcome.Success : AdaptiveOutcome.Failure;
        return state.Durability <= 0 ? AdaptiveOutcome.Failure : AdaptiveOutcome.Nonterminal;
    }

    public AdaptiveOutcome TryApply(AdaptiveSearchState state, VulcanSkill action, out AdaptiveSearchState next)
        => TryApply(state, action, new StepState(), new StepState(), out next);

    private AdaptiveOutcome TryApply(AdaptiveSearchState state, VulcanSkill action,
        StepState step, StepState simulated, out AdaptiveSearchState next)
    {
        state.Validate(Craft);
        next = state;
        if (Classify(state) != AdaptiveOutcome.Nonterminal || Array.IndexOf(SupportedActions, action) < 0)
            return AdaptiveOutcome.Illegal;
        state.CopyTo(step);
        if (Simulator.GetSuccessRate(step, action) < 1) return AdaptiveOutcome.Illegal;
        var result = Simulator.ExecuteInto(_craft, step, action, 0, 1, simulated);
        if (result != Simulator.ExecuteResult.Succeeded) return AdaptiveOutcome.Illegal;
        // A standalone Standard Touch does not establish the Basic→Standard→Advanced combo.
        // Legacy StepState records the last action; Adaptive's state records valid combo eligibility.
        next = AdaptiveSearchState.Capture(simulated, standardTouchComboValid:
            action == VulcanSkill.StandardTouch && state.Combo == VulcanSkill.BasicTouch)
            with { Quality = Math.Min(Craft.MaxQuality, simulated.Quality) };
        return Classify(next);
    }

    internal Workspace CreateWorkspace() => new(this);

    // Owned by one Search call, never by the shared simulation instance or a static/thread-local pool.
    // Independent simultaneous searches therefore cannot overwrite one another's scratch state.
    internal sealed class Workspace(AdaptiveSimulation simulation)
    {
        private readonly StepState _input = new();
        private readonly StepState _output = new();
        private AdaptiveProgressBound? _completionBound;
        internal AdaptiveProgressBound CompletionBound => _completionBound ??= AdaptiveProgressBound.Create(simulation._craft);
        internal AdaptiveOutcome TryApply(AdaptiveSearchState state, VulcanSkill action, out AdaptiveSearchState next)
            => simulation.TryApply(state, action, _input, _output, out next);

        internal int FinisherPotential(AdaptiveSearchState state)
        {
            state.CopyTo(_input);
            if (!Simulator.CanUseAction(simulation._craft, _input, VulcanSkill.ByregotsBlessing)) return 0;
            // Optimistic ranking only, never a proof or an allocated plan. Value future setup so
            // building IQ can compete with cashing it out immediately. Actual routes are replayed.
            if (state.CP >= 56) _input.GreatStridesLeft = 1;
            if (state.CP >= 74) _input.InnovationLeft = 1;
            return Simulator.CalculateQuality(simulation._craft, _input, VulcanSkill.ByregotsBlessing);
        }

        internal int ProgressBuffPotential(AdaptiveSearchState state)
        {
            if (state.Veneration == 0) return 0;
            state.CopyTo(_input);
            _input.VenerationLeft = 0;
            var best = 0;
            foreach (var action in ProgressActions)
                if (Simulator.CanUseAction(simulation._craft, _input, action))
                    best = Math.Max(best, Simulator.CalculateProgress(simulation._craft, _input, action));
            return best * Math.Min(3, state.Veneration) / 2;
        }

        private static readonly VulcanSkill[] ProgressActions =
            [VulcanSkill.BasicSynthesis, VulcanSkill.CarefulSynthesis, VulcanSkill.Groundwork, VulcanSkill.PrudentSynthesis];
    }

    // Animation duration model, not game action recast/latency. Used consistently for tie-breaking.
    public static int ActionDuration(VulcanSkill action) => action switch
    {
        VulcanSkill.Veneration or VulcanSkill.Innovation or VulcanSkill.GreatStrides or VulcanSkill.WasteNot
            or VulcanSkill.WasteNot2 or VulcanSkill.Manipulation or VulcanSkill.FinalAppraisal => 2,
        _ => 3,
    };
}
