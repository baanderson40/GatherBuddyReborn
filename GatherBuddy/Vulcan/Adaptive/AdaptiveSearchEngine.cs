using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Offline bounded optimizer. Never calls game services or authorizes execution.</summary>
public sealed class AdaptiveSearchEngine
{
    public AdaptiveSearchResult Search(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptiveSearchLimits? limits = null, IReadOnlyList<VulcanSkill>? continuation = null,
        CancellationToken cancellation = default, IReadOnlyList<AdaptiveSeedCandidate>? seeds = null)
    {
        limits ??= new();
        limits.Validate();
        initial.Validate(simulation.Craft);
        var budget = new AdaptiveSearchBudget(limits, cancellation);
        var workspace = simulation.CreateWorkspace();
        AdaptivePlan? incumbent = null;
        if (seeds != null && seeds.Count + (continuation is { Count: > 0 } ? 1 : 0) > 2)
            throw new ArgumentException("At most two total seed candidates, including continuation.");
        if (budget.Cancelled) return Result(simulation, initial, null, budget);
        if (continuation is { Count: > 0 } || seeds is { Count: > 0 })
        {
            budget.SeedAssessments = new();
            if (continuation is { Count: > 0 }) ReplaySeed(continuation, "Continuation", 0);
            if (seeds != null)
                foreach (var candidate in seeds)
                    ReplaySeed(candidate.Actions, candidate.Seed.Provenance.Source, candidate.Cursor);

            void ReplaySeed(IReadOnlyList<VulcanSkill> actions, string source, int cursor)
            {
                var started = Stopwatch.GetTimestamp();
                AdaptivePlan? accepted = null;
                var replaySteps = 0;
                var status = actions.Count > limits.MaximumPlanLength ? "TooLong" : budget.Stopped ? "BudgetStopped"
                    : AdaptivePlanValidator.TryValidate(simulation, initial, actions, out accepted, workspace,
                        () => budget.Stopped, out replaySteps) ? "Validated" : budget.Stopped ? "BudgetStopped" : "Invalid";
                budget.SeedReplaySteps += replaySteps;
                budget.SeedReplayElapsed += Stopwatch.GetElapsedTime(started);
                budget.SeedAssessments.Add(new(source, cursor, status,
                    status == "Validated" ? "Exact-root completion replay accepted." : "Seed rejected; no state substituted or suffix repaired.",
                    accepted?.FinalState.Quality, accepted?.TargetReached, accepted?.Actions.Count, accepted?.Duration));
                if (accepted != null && AdaptivePlanValidator.IsBetter(accepted, incumbent))
                {
                    incumbent = accepted;
                    budget.SeedIncumbent = accepted;
                    budget.SeedSource = source;
                }
            }
        }
        budget.ConfigureSeededAllowance(); // Input replay only; a later unseeded discovery never enables policy truncation.
        if (simulation.Classify(initial) == AdaptiveOutcome.Nonterminal)
        {
            // If an allowed single action already meets the goal, longer plans cannot beat its
            // modeled duration (all actions take >=2 seconds, all finishers take 3). Compare all
            // available one-action routes for resource ties, then avoid pointless tree allocation.
            foreach (var action in AdaptiveSimulation.Actions)
            {
                if (!budget.TryTransition()) break;
                var outcome = workspace.TryApply(initial, action, out var next);
                if (outcome != AdaptiveOutcome.Success || next.Quality < simulation.Goal.DesiredQuality) continue;
                if (!AdaptivePlanValidator.CouldImprove(simulation.Goal, next, AdaptiveSimulation.ActionDuration(action), 1, incumbent)) continue;
                if (AdaptivePlanValidator.TryValidate(simulation, initial, new[] { action }, out var plan, workspace)
                    && AdaptivePlanValidator.IsBetter(plan!, incumbent)) incumbent = plan;
            }
            if (incumbent is { TargetReached: true, Actions.Count: 1 }) return Result(simulation, initial, incumbent, budget);
        }
        var completion = new AdaptiveCompletionPlanner();
        var completionLimits = limits with { BeamWidth = Math.Min(limits.BeamWidth, 8) };
        var baseline = completion.Find(simulation, initial, completionLimits, budget,
            Math.Max(1, Math.Min(budget.EffectiveMaximumTransitions / 8, 2048)), limits.MaximumPlanLength, workspace, incumbent);
        if (baseline != null && AdaptivePlanValidator.IsBetter(baseline, incumbent)) incumbent = baseline;
        if (simulation.Classify(initial) != AdaptiveOutcome.Nonterminal)
            return Result(simulation, initial, incumbent, budget);

        // A narrow continuation rollout reaches buff/repair/finisher combinations beyond the
        // wide beam horizon. It shares the SAME total transition, memory, and time budgets.
        if (initial.Quality < simulation.Goal.DesiredQuality && limits.MaximumPlanLength > limits.Horizon)
        {
            var before = budget.Transitions;
            incumbent = SearchPass(simulation, initial, limits, completionLimits, workspace, completion, budget,
                incumbent, Math.Min(limits.BeamWidth, 16), Math.Min(limits.MaximumPlanLength, 32),
                (int)(budget.EffectiveMaximumTransitions * 3L / 5), deep: true);
            budget.DeepTransitions = budget.Transitions - before;
        }
        incumbent = SearchPass(simulation, initial, limits, completionLimits, workspace, completion, budget,
            incumbent, limits.BeamWidth, Math.Min(limits.Horizon, limits.MaximumPlanLength),
            budget.EffectiveMaximumTransitions, deep: false);
        return Result(simulation, initial, incumbent, budget);
    }

    private static AdaptivePlan? SearchPass(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptiveSearchLimits limits, AdaptiveSearchLimits completionLimits, AdaptiveSimulation.Workspace workspace,
        AdaptiveCompletionPlanner completion, AdaptiveSearchBudget budget, AdaptivePlan? incumbent,
        int width, int depthLimit, int transitionLimit, bool deep)
    {
        if (budget.Stopped || transitionLimit <= 0 || !budget.TryStore()) return incumbent;
        var end = Math.Min((long)budget.EffectiveMaximumTransitions, (long)budget.Transitions + transitionLimit);
        // Only retained beam nodes enter the parent arena/closed set. Discarded candidates do not
        // leave long-lived records or suppress later paths that actually survived selection.
        var arena = new List<AdaptiveSearchNode> { new(initial, -1, VulcanSkill.None, VulcanSkill.None, 0, 0) };
        var frontier = new List<int> { 0 };
        var seen = new Dictionary<AdaptiveSearchState, int> { [initial] = 0 };
        var capacity = Math.Min(limits.MaximumStoredNodes, width * AdaptiveSimulation.Actions.Count);
        var candidates = new List<AdaptiveSearchNode>(capacity);
        var layerSeen = new Dictionary<AdaptiveSearchState, int>(capacity);
        for (var depth = 0; depth < depthLimit && frontier.Count > 0 && !budget.Stopped; ++depth)
        {
            candidates.Clear();
            layerSeen.Clear();
            budget.DeepestLayer = Math.Max(budget.DeepestLayer, depth + 1);
            foreach (var parent in frontier)
            {
                var node = arena[parent];
                if (AdaptiveSearchPruning.CannotImproveNonterminal(workspace, node.State, node.Duration, node.Depth, incumbent, budget)) continue;
                foreach (var action in AdaptiveSimulation.Actions)
                {
                    if (budget.Transitions >= end || !budget.TryTransition()) return incumbent;
                    if (AdaptiveSearchPruning.DominatedStandaloneRefinedTouch(node.State, action)) continue;
                    var outcome = workspace.TryApply(node.State, action, out var next);
                    if (outcome is AdaptiveOutcome.Illegal or AdaptiveOutcome.Failure) continue;
                    if (AdaptiveSearchPruning.RedundantRefresh(node.State, action, next)) continue;
                    var duration = node.Duration + AdaptiveSimulation.ActionDuration(action);
                    if (outcome == AdaptiveOutcome.Success)
                    {
                        if (!AdaptivePlanValidator.CouldImprove(simulation.Goal, next, duration, node.Depth + 1, incumbent)) continue;
                        var prefix = AdaptiveSearchNode.Path(arena, parent);
                        var actions = new VulcanSkill[prefix.Length + 1];
                        prefix.CopyTo(actions, 0);
                        actions[^1] = action;
                        if (AdaptivePlanValidator.TryValidate(simulation, initial, actions, out var plan, workspace)
                            && AdaptivePlanValidator.IsBetter(plan!, incumbent)) incumbent = plan;
                        continue;
                    }
                    if (AdaptiveSearchPruning.CannotImproveNonterminal(workspace, next, duration, node.Depth + 1, incumbent, budget)) continue;
                    var candidate = new AdaptiveSearchNode(next, parent, action, node.Depth == 0 ? action : node.RootAction,
                        node.Depth + 1, duration, Rank(simulation, workspace, next, duration));
                    if (seen.TryGetValue(next, out var previous) && AdaptiveSearchPruning.Dominates(arena, arena[previous], candidate)) continue;
                    if (layerSeen.TryGetValue(next, out var prior) && AdaptiveSearchPruning.Dominates(arena, candidates[prior], candidate)) continue;
                    if (!budget.TryStore()) return incumbent;
                    if (layerSeen.TryGetValue(next, out prior) && AdaptiveSearchPruning.Dominates(arena, candidate, candidates[prior]))
                        candidates[prior] = candidate;
                    else
                    {
                        if (!layerSeen.ContainsKey(next)) layerSeen[next] = candidates.Count;
                        candidates.Add(candidate);
                    }
                }
            }
            candidates.Sort(static (a, b) =>
            {
                var rank = b.Score.CompareTo(a.Score);
                if (rank != 0) return rank;
                var parentOrder = a.Parent.CompareTo(b.Parent);
                return parentOrder != 0 ? parentOrder : a.Action.CompareTo(b.Action);
            });
            frontier = DiverseFrontier(arena, candidates, width, seen, simulation, diverseRoots: !deep);
            // Keep the rank-leading witness, then alternate achieved-quality and economy
            // witnesses. Potential-only ranks can hide useful cash-outs after redundant-action
            // filtering changes the beam. All probes still share the SAME work/time budgets.
            var probeCount = limits.CompletionProbesPerLayer;
            for (var p = 0; p < Math.Min(probeCount, frontier.Count) && !budget.Stopped; ++p)
            {
                var index = frontier[p];
                if (deep && p == 1 && depth % 2 == 0)
                    foreach (var alternative in frontier)
                        if (arena[alternative].State.Quality > arena[index].State.Quality) index = alternative;
                if (deep && (p == 1 || probeCount == 1) && depth % 2 == 1)
                    foreach (var alternative in frontier)
                        if (EconomyRank(simulation, arena[alternative]) > EconomyRank(simulation, arena[index])) index = alternative;
                var node = arena[index];
                if (AdaptiveSearchPruning.CannotImproveNonterminal(workspace, node.State, node.Duration, node.Depth, incumbent, budget)) continue;
                var remaining = limits.MaximumPlanLength - node.Depth;
                var localBudget = (int)Math.Min(limits.CompletionProbeTransitions, end - budget.Transitions);
                if (remaining <= 0 || localBudget <= 0) continue;
                var suffix = AdaptiveFinisherProbe.Find(simulation, node.State, completionLimits, completion, budget, localBudget, remaining, workspace,
                    incumbent, node.Duration, node.Depth);
                if (suffix == null) continue;
                var prefix = AdaptiveSearchNode.Path(arena, index);
                var actions = new VulcanSkill[prefix.Length + suffix.Actions.Count];
                prefix.CopyTo(actions, 0);
                for (var i = 0; i < suffix.Actions.Count; ++i) actions[prefix.Length + i] = suffix.Actions[i];
                if (AdaptivePlanValidator.TryValidate(simulation, initial, actions, out var plan, workspace)
                    && AdaptivePlanValidator.IsBetter(plan!, incumbent)) incumbent = plan;
            }
        }
        return incumbent;
    }

    private static List<int> DiverseFrontier(List<AdaptiveSearchNode> arena, List<AdaptiveSearchNode> ranked,
        int width, Dictionary<AdaptiveSearchState, int> seen, AdaptiveSimulation simulation, bool diverseRoots)
    {
        if (!diverseRoots)
        {
            var retained = new List<int>(Math.Min(width, ranked.Count));
            var chosen = new HashSet<int>();
            for (var i = 0; i < Math.Min(Math.Max(1, width / 2), ranked.Count); ++i) chosen.Add(i);
            var economy = new List<int>(ranked.Count);
            for (var i = 0; i < ranked.Count; ++i) economy.Add(i);
            economy.Sort((a, b) =>
            {
                var order = EconomyRank(simulation, ranked[b]).CompareTo(EconomyRank(simulation, ranked[a]));
                return order != 0 ? order : a.CompareTo(b);
            });
            foreach (var index in economy)
            {
                if (chosen.Count >= width) break;
                chosen.Add(index);
            }
            for (var i = 0; i < ranked.Count; ++i)
            {
                if (!chosen.Contains(i)) continue;
                retained.Add(arena.Count);
                arena.Add(ranked[i]);
                AdaptiveSearchPruning.Remember(arena, seen, arena.Count - 1);
            }
            return retained;
        }
        var groups = new Dictionary<VulcanSkill, Queue<int>>();
        // Index into ranked; parent links stay in the retained arena.
        for (var i = 0; i < ranked.Count; ++i)
        {
            if (!groups.TryGetValue(ranked[i].RootAction, out var group)) groups[ranked[i].RootAction] = group = new();
            group.Enqueue(i);
        }
        var selected = new List<int>(Math.Min(width, ranked.Count));
        while (selected.Count < width)
        {
            var added = false;
            foreach (var group in groups.Values)
            {
                if (!group.TryDequeue(out var index)) continue;
                var node = ranked[index];
                selected.Add(arena.Count);
                arena.Add(node);
                AdaptiveSearchPruning.Remember(arena, seen, arena.Count - 1);
                added = true;
                if (selected.Count == width) break;
            }
            if (!added) break;
        }
        return selected;
    }

    private static double Rank(AdaptiveSimulation simulation, AdaptiveSimulation.Workspace workspace, AdaptiveSearchState state, int duration)
    {
        var target = Math.Max(1, simulation.Goal.DesiredQuality);
        var potential = Math.Min(simulation.Goal.DesiredQuality, (long)state.Quality + workspace.FinisherPotential(state));
        return potential / (double)target * 1000
            + Math.Min(state.Quality, simulation.Goal.DesiredQuality) / (double)target * 250
            + Math.Min(state.Progress, simulation.Craft.RequiredProgress) / (double)simulation.Craft.RequiredProgress * 120
            + state.CP * 0.35 + state.Durability * 2 + state.InnerQuiet * 15
            + state.Innovation * 6 + state.GreatStrides * 4 + state.Manipulation * 12
            + state.WasteNot * 3 + state.MuscleMemory * 3 - duration;
    }

    private static double EconomyRank(AdaptiveSimulation simulation, AdaptiveSearchNode node)
    {
        var state = node.State;
        return state.CP + (state.Durability + state.Manipulation * 5) * 3 + state.InnerQuiet * 30
            + Math.Min(state.Quality, simulation.Goal.DesiredQuality) / (double)Math.Max(1, simulation.Goal.DesiredQuality) * 300
            + Math.Min(state.Progress, simulation.Craft.RequiredProgress) / (double)simulation.Craft.RequiredProgress * 40 - node.Duration;
    }

    internal static AdaptiveSearchResult Result(AdaptiveSimulation simulation, AdaptiveSearchState initial,
        AdaptivePlan? plan, AdaptiveSearchBudget budget)
    {
        var diagnostics = budget.DiagnosticsFor(plan);
        if (budget.Cancelled) return new(AdaptiveSearchStatus.Cancelled, null,
            diagnostics with { Termination = AdaptiveSearchTermination.Cancelled }, "Search cancelled; no executable result.");
        if (plan != null) return new(AdaptiveSearchStatus.Found, plan, diagnostics,
            plan.TargetReached ? "Validated completion meeting target found." : "Validated completion found; desired target not found within search limits.");
        if (simulation.Classify(initial) == AdaptiveOutcome.Failure)
            return new(AdaptiveSearchStatus.ProvenImpossible, null, diagnostics, "Initial craft is already terminal and unsuccessful.");
        return new(AdaptiveSearchStatus.Unknown, null, diagnostics, "No validated completion found within search limits; infeasibility not proven.");
    }
}
