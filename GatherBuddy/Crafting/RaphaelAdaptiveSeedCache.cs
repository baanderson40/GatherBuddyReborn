using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Crafting;

/// <summary>
/// Immutable sidecar, populated only from locally owned complete cache publications/load rows.
/// Never copies a mutable ActionIds list during shadow lookup, queues work, or changes live entries.
/// </summary>
public sealed class RaphaelAdaptiveSeedCache
{
    private sealed record Publication(CachedRaphaelSolution Owner, RaphaelSolveRequest Request, AdaptivePlanSeed? Seed, string Reason);
    private readonly ConcurrentDictionary<string, Publication> _entries = new();

    public void Publish(CachedRaphaelSolution entry, IReadOnlyList<uint>? ownedActions)
    {
        if (entry.Request == null || string.IsNullOrEmpty(entry.Key)) return;
        var request = entry.Request;
        AdaptivePlanSeed? seed = null;
        var reason = "Failed, empty, oversized, or unsupported cached candidate.";
        if (!entry.IsFailed && ownedActions is { Count: > 0 and <= 256 } && entry.Key == request.GetKey())
        {
            try
            {
                seed = new(new("RaphaelCache", entry.Key, request.RecipeId, request.Level,
                    request.Craftsmanship, request.Control, request.CP, request.Manipulation,
                    request.Specialist, request.InitialQuality, request.ValidationContext),
                    ownedActions.Select(id => (VulcanSkill)id).ToArray());
                reason = "Detached cached candidate captured; requires alignment and exact-root replay.";
            }
            catch (ArgumentException) { }
        }
        _entries[entry.Key] = new(entry, request, seed, reason);
    }

    public bool TryGet(RaphaelSolveRequest request, CachedRaphaelSolution? current,
        out AdaptivePlanSeed? seed, out string reason)
    {
        seed = null;
        reason = "No immutable publication for the exact active Raphael request.";
        var key = request.GetKey();
        if (current == null || current.IsFailed || current.Key != key || current.Request != request
            || !_entries.TryGetValue(key, out var published) || !ReferenceEquals(current, published.Owner)
            || published.Request != request) return false;
        seed = published.Seed;
        reason = published.Reason;
        return seed != null;
    }

    public void Clear() => _entries.Clear();
    public void Remove(string key) => _entries.TryRemove(key, out _);
}
