using System;
using System.Collections.Generic;
using System.Threading;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Bounded constructive completion search. Truncation always means Unknown, never impossible.</summary>
public sealed class AdaptiveCompletionPlanner
{
    public AdaptiveSearchResult Find(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptiveSearchLimits? limits = null, CancellationToken cancellation = default)
    {
        limits ??= new();
        limits.Validate();
        initial.Validate(simulation.Craft);
        var budget = new AdaptiveSearchBudget(limits, cancellation);
        var plan = Find(simulation, initial, limits, budget, limits.MaximumTransitions, limits.MaximumPlanLength);
        return AdaptiveSearchEngine.Result(simulation, initial, plan, budget);
    }

    internal AdaptivePlan? Find(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptiveSearchLimits limits, AdaptiveSearchBudget budget, int transitionLimit, int depthLimit,
        AdaptiveSimulation.Workspace? workspace = null, AdaptivePlan? incumbent = null,
        int prefixDuration = 0, int prefixLength = 0)
    {
        workspace ??= simulation.CreateWorkspace();
        ++budget.Probes;
        if (budget.Stopped) return null;
        if (AdaptivePlanValidator.TryValidate(simulation, initial, Array.Empty<VulcanSkill>(), out var complete, workspace)) return complete;
        var arena = new List<AdaptiveSearchNode> { new(initial, -1, VulcanSkill.None, VulcanSkill.None, 0, 0) };
        if (!budget.TryStore()) return null;
        var frontier = new List<int> { 0 };
        var seen = new Dictionary<AdaptiveSearchState, int> { [initial] = 0 };
        var end = (long)budget.Transitions + transitionLimit;
        for (var depth = 0; depth < depthLimit && frontier.Count > 0 && !budget.Stopped; ++depth)
        {
            var candidates = new List<int>();
            var layerSeen = new Dictionary<AdaptiveSearchState, int>();
            AdaptivePlan? best = null;
            foreach (var parent in frontier)
            {
                var node = arena[parent];
                if (AdaptiveSearchPruning.CannotImproveNonterminal(workspace, node.State, prefixDuration + node.Duration, prefixLength + node.Depth, incumbent, budget)) continue;
                foreach (var action in AdaptiveSimulation.Actions)
                {
                    if (budget.Transitions >= end || !budget.TryTransition()) return best;
                    // Once mandatory quality is met, use only progress/resource actions in suffix probes.
                    if (node.State.Quality >= simulation.Goal.MandatoryQuality
                        && (!simulation.Goal.StrictTarget || node.State.Quality >= simulation.Goal.DesiredQuality)
                        && !IsCompletionAction(action)) continue;
                    if (AdaptiveSearchPruning.DominatedStandaloneRefinedTouch(node.State, action)) continue;
                    var outcome = workspace.TryApply(node.State, action, out var next);
                    if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) continue;
                    if (AdaptiveSearchPruning.RedundantRefresh(node.State, action, next)) continue;
                    var duration = node.Duration + AdaptiveSimulation.ActionDuration(action);
                    if (outcome == AdaptiveOutcome.Nonterminal
                        && AdaptiveSearchPruning.CannotImproveNonterminal(workspace, next, prefixDuration + duration, prefixLength + node.Depth + 1, incumbent, budget)) continue;
                    var candidate = new AdaptiveSearchNode(next, parent, action, node.Depth == 0 ? action : node.RootAction, node.Depth + 1, duration,
                        CompletionRank(simulation, workspace, next));
                    if (seen.TryGetValue(next, out var prior) && AdaptiveSearchPruning.Dominates(arena, arena[prior], candidate)) continue;
                    if (layerSeen.TryGetValue(next, out var peer) && AdaptiveSearchPruning.Dominates(arena, arena[peer], candidate)) continue;
                    if (!budget.TryStore()) return best;
                    if (outcome == AdaptiveOutcome.Nonterminal && layerSeen.TryGetValue(next, out peer)
                        && AdaptiveSearchPruning.Dominates(arena, candidate, arena[peer]))
                    {
                        // No child points to this layer until selection completes.
                        arena[peer] = candidate;
                        continue;
                    }
                    var child = arena.Count;
                    arena.Add(candidate);
                    if (outcome == AdaptiveOutcome.Success)
                    {
                        if (AdaptivePlanValidator.TryValidate(simulation, initial, AdaptiveSearchNode.Path(arena, child), out var plan, workspace)
                            && AdaptivePlanValidator.IsBetter(plan!, best)) best = plan;
                    }
                    else
                    {
                        if (!layerSeen.ContainsKey(next)) layerSeen[next] = child;
                        candidates.Add(child);
                    }
                }
            }
            if (best != null) return best;
            candidates.Sort((a, b) =>
            {
                var order = arena[b].Score.CompareTo(arena[a].Score);
                return order != 0 ? order : a.CompareTo(b);
            });
            if (candidates.Count > limits.BeamWidth) candidates.RemoveRange(limits.BeamWidth, candidates.Count - limits.BeamWidth);
            // Discarded beam candidates must not suppress later retained paths.
            foreach (var child in candidates) AdaptiveSearchPruning.Remember(arena, seen, child);
            frontier = candidates;
        }
        return null;
    }

    private static double CompletionRank(AdaptiveSimulation simulation, AdaptiveSimulation.Workspace workspace, AdaptiveSearchState state)
        => Math.Min((long)state.Progress + workspace.ProgressBuffPotential(state), simulation.Craft.RequiredProgress)
                / (double)simulation.Craft.RequiredProgress * 1000
            + Math.Min(state.Quality, simulation.Goal.StrictTarget ? simulation.Goal.DesiredQuality : simulation.Goal.MandatoryQuality)
                / (double)Math.Max(1, simulation.Craft.MaxQuality) * 1000
            + state.CP * 0.1 + state.Durability + state.Manipulation * 4 + state.Veneration * 3;

    private static bool IsCompletionAction(VulcanSkill action) => action is
        VulcanSkill.BasicSynthesis or VulcanSkill.CarefulSynthesis or VulcanSkill.Groundwork
        or VulcanSkill.IntensiveSynthesis or VulcanSkill.PrudentSynthesis or VulcanSkill.MuscleMemory
        or VulcanSkill.DelicateSynthesis or VulcanSkill.Veneration or VulcanSkill.MastersMend
        or VulcanSkill.Manipulation or VulcanSkill.WasteNot or VulcanSkill.WasteNot2
        or VulcanSkill.TrainedPerfection or VulcanSkill.ImmaculateMend or VulcanSkill.TricksOfTrade;
}

internal readonly record struct AdaptiveSearchNode(AdaptiveSearchState State, int Parent, VulcanSkill Action,
    VulcanSkill RootAction, int Depth, int Duration, double Score = 0)
{
    internal static VulcanSkill[] Path(List<AdaptiveSearchNode> arena, int index)
    {
        var path = new VulcanSkill[arena[index].Depth];
        for (var i = path.Length - 1; i >= 0; --i)
        {
            path[i] = arena[index].Action;
            index = arena[index].Parent;
        }
        return path;
    }
}
