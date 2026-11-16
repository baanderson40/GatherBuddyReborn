using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Evaluate short quality cash-outs AND their real progress suffix, never just a quality estimate.</summary>
internal static class AdaptiveFinisherProbe
{
    private static readonly VulcanSkill[][] Setups =
    [
        [], [VulcanSkill.ByregotsBlessing], [VulcanSkill.GreatStrides, VulcanSkill.ByregotsBlessing],
        [VulcanSkill.Innovation, VulcanSkill.ByregotsBlessing],
        [VulcanSkill.Innovation, VulcanSkill.GreatStrides, VulcanSkill.ByregotsBlessing],
        [VulcanSkill.GreatStrides, VulcanSkill.Innovation, VulcanSkill.ByregotsBlessing],
    ];

    private readonly record struct Candidate(AdaptiveSearchState State, VulcanSkill[] Setup, int Duration, int Ordinal);

    internal static AdaptivePlan? Find(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptiveSearchLimits limits, AdaptiveCompletionPlanner completion, AdaptiveSearchBudget budget,
        int transitions, int maximumLength, AdaptiveSimulation.Workspace workspace,
        AdaptivePlan? incumbent = null, int prefixDuration = 0, int prefixLength = 0)
    {
        var end = (long)budget.Transitions + transitions;
        var candidates = new List<Candidate>(Setups.Length);
        foreach (var setup in Setups)
        {
            if (setup.Length > maximumLength) continue;
            if (initial.Quality >= simulation.Goal.DesiredQuality && setup.Length != 0) continue;
            var state = initial;
            var duration = 0;
            var valid = true;
            foreach (var action in setup)
            {
                if (budget.Transitions >= end || !budget.TryTransition()) return null;
                var outcome = workspace.TryApply(state, action, out var next);
                if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) { valid = false; break; }
                state = next;
                duration += AdaptiveSimulation.ActionDuration(action);
            }
            if (valid) candidates.Add(new(state, setup, duration, candidates.Count));
        }
        candidates.Sort(static (a, b) =>
        {
            var quality = b.State.Quality.CompareTo(a.State.Quality);
            if (quality != 0) return quality;
            var time = a.Duration.CompareTo(b.Duration);
            if (time != 0) return time;
            var cp = b.State.CP.CompareTo(a.State.CP);
            return cp != 0 ? cp : a.Ordinal.CompareTo(b.Ordinal);
        });
        AdaptivePlan? best = null;
        // A narrow constructive suffix is cheap enough to test long routes, instead of spending
        // every suffix budget on just its first two layers. All failures remain Unknown.
        var suffixLimits = limits with { BeamWidth = 1 };
        foreach (var candidate in candidates)
        {
            var remainingTransitions = (int)Math.Max(0, end - budget.Transitions);
            if (remainingTransitions <= 0 || budget.Stopped) break;
            var suffix = completion.Find(simulation, candidate.State, suffixLimits, budget,
                remainingTransitions, maximumLength - candidate.Setup.Length, workspace, incumbent,
                prefixDuration + candidate.Duration, prefixLength + candidate.Setup.Length);
            if (suffix == null) continue;
            var actions = new VulcanSkill[candidate.Setup.Length + suffix.Actions.Count];
            candidate.Setup.CopyTo(actions, 0);
            for (var i = 0; i < suffix.Actions.Count; ++i) actions[candidate.Setup.Length + i] = suffix.Actions[i];
            if (AdaptivePlanValidator.TryValidate(simulation, initial, actions, out var plan, workspace)
                && AdaptivePlanValidator.IsBetter(plan!, best)) best = plan;
        }
        return best;
    }
}
