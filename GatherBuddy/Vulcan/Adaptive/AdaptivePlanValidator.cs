using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

public static class AdaptivePlanValidator
{
    public static bool TryValidate(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        IReadOnlyList<VulcanSkill> actions, out AdaptivePlan? plan)
        => TryValidate(simulation, initial, actions, out plan, simulation.CreateWorkspace());

    internal static bool TryValidate(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        IReadOnlyList<VulcanSkill> actions, out AdaptivePlan? plan, AdaptiveSimulation.Workspace workspace)
        => TryValidate(simulation, initial, actions, out plan, workspace, null, out _);

    internal static bool TryValidate(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        IReadOnlyList<VulcanSkill> actions, out AdaptivePlan? plan, AdaptiveSimulation.Workspace workspace,
        Func<bool>? stopped, out int replaySteps)
    {
        replaySteps = 0;
        initial.Validate(simulation.Craft);
        plan = null;
        if (actions.Count > 256) return false;
        var state = initial;
        var duration = 0;
        var ownedActions = new VulcanSkill[actions.Count];
        for (var i = 0; i < actions.Count; ++i)
        {
            if (stopped?.Invoke() == true) return false;
            ++replaySteps;
            ownedActions[i] = actions[i];
            var outcome = workspace.TryApply(state, actions[i], out var next);
            if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) return false;
            state = next;
            duration += AdaptiveSimulation.ActionDuration(actions[i]);
        }
        if (stopped?.Invoke() == true || simulation.Classify(state) != AdaptiveOutcome.Success) return false;
        var reached = state.Quality >= simulation.Goal.DesiredQuality;
        if (simulation.Goal.StrictTarget && !reached) return false;
        plan = new(ownedActions, state, duration, reached);
        return true;
    }

    internal static bool IsBetter(AdaptivePlan candidate, AdaptivePlan? incumbent)
    {
        if (incumbent == null) return true;
        if (candidate.TargetReached != incumbent.TargetReached) return candidate.TargetReached;
        if (!candidate.TargetReached && candidate.FinalState.Quality != incumbent.FinalState.Quality)
            return candidate.FinalState.Quality > incumbent.FinalState.Quality;
        if (candidate.Duration != incumbent.Duration) return candidate.Duration < incumbent.Duration;
        if (candidate.Actions.Count != incumbent.Actions.Count) return candidate.Actions.Count < incumbent.Actions.Count;
        // Deterministic final ties conserve resources. No claim of globally optimal resource use.
        if (candidate.FinalState.TrainedPerfectionAvailable != incumbent.FinalState.TrainedPerfectionAvailable)
            return candidate.FinalState.TrainedPerfectionAvailable;
        if (candidate.FinalState.CP != incumbent.FinalState.CP) return candidate.FinalState.CP > incumbent.FinalState.CP;
        if (candidate.FinalState.Durability != incumbent.FinalState.Durability) return candidate.FinalState.Durability > incumbent.FinalState.Durability;
        for (var i = 0; i < candidate.Actions.Count; ++i)
            if (candidate.Actions[i] != incumbent.Actions[i]) return candidate.Actions[i] < incumbent.Actions[i];
        return false;
    }

    internal static bool CouldImprove(AdaptiveCraftGoal goal, AdaptiveSearchState state, int duration, int length, AdaptivePlan? incumbent)
    {
        var reached = state.Quality >= goal.DesiredQuality;
        if (goal.StrictTarget && !reached) return false;
        if (incumbent == null) return true;
        if (reached != incumbent.TargetReached) return reached;
        if (!reached && state.Quality != incumbent.FinalState.Quality) return state.Quality > incumbent.FinalState.Quality;
        if (duration != incumbent.Duration) return duration < incumbent.Duration;
        if (length != incumbent.Actions.Count) return length < incumbent.Actions.Count;
        // Leave exact ties for full replay and lexicographic/resource comparison.
        return true;
    }
}
