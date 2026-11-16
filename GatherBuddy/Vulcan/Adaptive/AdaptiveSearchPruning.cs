using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Search-only proofs. Never changes simulator legality, seed replay, or goal ordering.</summary>
internal static class AdaptiveSearchPruning
{
    // Deliberately optimistic: even non-progress actions count. Derive from the allowlist so a
    // future faster action cannot silently invalidate the bound. No heuristic potential is proof.
    internal static readonly int MinimumRemainingDuration = MinimumActionDuration();

    private static int MinimumActionDuration()
    {
        var minimum = int.MaxValue;
        foreach (var action in AdaptiveSimulation.Actions)
            minimum = Math.Min(minimum, AdaptiveSimulation.ActionDuration(action));
        return minimum;
    }

    internal static bool CannotImproveNonterminal(int duration, int length, AdaptivePlan? incumbent)
    {
        // Below-target incumbents rank quality before time: no duration pruning is sound there.
        if (incumbent is not { TargetReached: true }) return false;
        var earliest = (long)duration + MinimumRemainingDuration;
        if (earliest != incumbent.Duration) return earliest > incumbent.Duration;
        // Equal duration/count can still win CP/durability/perfection or lexical ties.
        return (long)length + 1 > incumbent.Actions.Count;
    }

    internal static bool CannotImproveNonterminal(AdaptiveSimulation.Workspace workspace, AdaptiveSearchState state,
        int duration, int length, AdaptivePlan? incumbent, AdaptiveSearchBudget budget)
    {
        if (incumbent is not { TargetReached: true }) return false;
        var prune = CannotImproveNonterminal(duration, length, incumbent);
        if (!prune && budget.UseProgressCompletionBound)
        {
            var bound = workspace.CompletionBound;
            var remaining = bound.MinimumActions(state.Progress);
            var earliest = (long)duration + remaining * bound.MinimumProgressDuration;
            prune = earliest > incumbent.Duration || (earliest == incumbent.Duration && (long)length + remaining > incumbent.Actions.Count);
        }
        if (prune) ++budget.ProvenBoundPrunes;
        return prune;
    }

    internal static bool RedundantRefresh(AdaptiveSearchState before, VulcanSkill action, AdaptiveSearchState after)
        // Supported ordinary mechanics have no benefit for spending extra CP with every other
        // state field unchanged. Higher CP never disables an allowed action; capped Tricks gains
        // may erase the advantage but cannot reverse it. Keep combo/first-step/condition exact.
        => action == VulcanSkill.FinalAppraisal && after.CP < before.CP
            && after == before with { CP = after.CP };

    // Ordinary-model search only: standalone Refined and Basic Touch have equal
    // potency/durability/IQ/duration. Basic costs6 less CP, has lower lexical ID,
    // and grants combo eligibility instead of clearing it. This is not illegality:
    // observed actions and input seed replay must still accept standalone Refined.
    internal static bool DominatedStandaloneRefinedTouch(AdaptiveSearchState before, VulcanSkill action)
        => action == VulcanSkill.RefinedTouch && before.Combo != VulcanSkill.BasicTouch;

    internal static bool Dominates(List<AdaptiveSearchNode> arena, AdaptiveSearchNode prior, AdaptiveSearchNode candidate)
    {
        // Exact same state only. Require BOTH dimensions: a faster but longer prefix can run
        // out of remaining plan length/depth. Incomparable labels must remain searchable.
        if (prior.State != candidate.State || prior.Duration > candidate.Duration || prior.Depth > candidate.Depth) return false;
        if (prior.Duration < candidate.Duration || prior.Depth < candidate.Depth) return true;
        // Search depth is bounded by MaximumPlanLength (<=256). Ties must not allocate
        // two managed path arrays on every duplicate comparison.
        Span<VulcanSkill> a = stackalloc VulcanSkill[prior.Depth];
        Span<VulcanSkill> b = stackalloc VulcanSkill[candidate.Depth];
        CopyPrefix(arena, prior, a);
        CopyPrefix(arena, candidate, b);
        for (var i = 0; i < a.Length; ++i)
            if (a[i] != b[i]) return a[i] < b[i];
        return true;
    }

    private static void CopyPrefix(List<AdaptiveSearchNode> arena, AdaptiveSearchNode node, Span<VulcanSkill> path)
    {
        if (path.Length == 0) return;
        path[^1] = node.Action;
        for (var i = path.Length - 2; i >= 0; --i)
        {
            node = arena[node.Parent];
            path[i] = node.Action;
        }
    }

    internal static void Remember(List<AdaptiveSearchNode> arena, Dictionary<AdaptiveSearchState, int> seen, int index)
    {
        var node = arena[index];
        // One bounded representative per state. Keep incomparable paths in the arena/frontier;
        // failing to remember all Pareto labels can miss pruning, never justify extra pruning.
        if (!seen.TryGetValue(node.State, out var prior) || Dominates(arena, node, arena[prior])) seen[node.State] = index;
    }
}

/// <summary>Relaxed ordinary-craft progress bound, not a heuristic or a feasibility classifier.</summary>
internal readonly record struct AdaptiveProgressBound(int RequiredProgress, int MaximumProgressGain, int MinimumProgressDuration)
{
    internal static AdaptiveProgressBound Create(CraftState craft)
    {
        // The largest audited ordinary multiplier is10 (400% Intensive *2.5 buffs).
        // Leave extra float-to-int headroom. Extreme synthetic profiles use the weak
        // bound instead of relying on overflowing/non-monotone numeric conversions.
        var basis = Simulator.BaseProgress(craft);
        if (basis < 0 || basis > int.MaxValue / 16)
            return new(craft.CraftProgress, 0, AdaptiveSearchPruning.MinimumRemainingDuration);
        // CalculateProgress currently depends only on fixed stats/level, progress buffs,
        // condition and Groundwork's durability efficiency. Simultaneous maximum buffs,
        // free durability cost and Good/Intensive eligibility deliberately ignore setup,
        // CP/unlocks and remaining uses. This can overestimate, never restrict, legal gain.
        // Expert/Malleable and mission mechanics are rejected by the simulation boundary.
        var optimistic = new StepState
        {
            Durability = craft.CraftDurability, Condition = Condition.Good,
            MuscleMemoryLeft = 5, VenerationLeft = 4, TrainedPerfectionActive = true,
        };
        var maximum = 0;
        var minimumDuration = int.MaxValue;
        foreach (var action in AdaptiveSimulation.Actions)
        {
            var gain = Simulator.CalculateProgress(craft, optimistic, action);
            if (gain < 0) return new(craft.CraftProgress, 0, AdaptiveSearchPruning.MinimumRemainingDuration);
            if (gain <= 0) continue;
            maximum = Math.Max(maximum, gain);
            minimumDuration = Math.Min(minimumDuration, AdaptiveSimulation.ActionDuration(action));
        }
        // A degenerate arithmetic profile falls back to the old one-action bound, not impossible.
        return new(craft.CraftProgress, maximum, maximum > 0 ? minimumDuration : AdaptiveSearchPruning.MinimumRemainingDuration);
    }

    internal long MinimumActions(int progress) => MaximumProgressGain <= 0 ? 1
        : Math.Max(1, ((long)RequiredProgress - progress + MaximumProgressGain - 1) / MaximumProgressGain);
}
