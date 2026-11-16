using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Vulcan.Adaptive;

public enum AdaptiveDecisionPhase
{
    Idle, Searching, Ready, AwaitingObservation, Paused, Blocked, Cancelled, Failed, Completed,
}

/// <summary>Structural identity, not a lossy hash. Revisions also distinguish non-step actions.</summary>
public sealed record AdaptiveDecisionIdentity(Guid SessionId, long ObservationRevision, long Generation,
    AdaptiveCraftSnapshot Craft, AdaptiveCraftGoal Goal, AdaptiveSearchLimits Limits, AdaptiveSearchState State);

public sealed class AdaptiveSearchRequest
{
    public AdaptiveDecisionIdentity Identity { get; }
    public IReadOnlyList<VulcanSkill> Continuation { get; }
    public IReadOnlyList<AdaptiveSeedCandidate> Seeds { get; }

    public AdaptiveSearchRequest(AdaptiveDecisionIdentity identity, IEnumerable<VulcanSkill>? continuation = null,
        IReadOnlyList<AdaptiveSeedCandidate>? seeds = null)
    {
        Identity = identity;
        Continuation = Array.AsReadOnly(continuation?.ToArray() ?? []);
        if (seeds is { Count: > 2 }) throw new ArgumentException("At most two seeds per root.");
        Seeds = Array.AsReadOnly(seeds?.ToArray() ?? []);
    }
}

/// <summary>Scheduling seam for deterministic lifecycle tests. Production work uses Task.Run.</summary>
public interface IAdaptiveSearchScheduler
{
    Task<AdaptiveSearchResult> Schedule(AdaptiveSearchRequest request, CancellationToken cancellation);
}

public sealed record AdaptiveDecisionSnapshot(AdaptiveDecisionPhase Phase, Guid SessionId,
    AdaptiveDecisionIdentity? Decision, VulcanSkill Action, string Reason, long RejectedResults,
    long PredictionMismatches, AdaptiveSearchDiagnostics? SearchDiagnostics);

/// <summary>Host-observed lifecycle timing, not worker CPU start/completion timestamps.</summary>
public sealed record AdaptiveSearchLifecycleEvent(TimeSpan Elapsed, long Revision, long Generation,
    string Kind, string Detail, AdaptiveSearchStatus? SearchStatus = null,
    AdaptiveSearchDiagnostics? Diagnostics = null, AdaptiveSearchLimits? Limits = null);

/// <summary>Reservation only, not permission to call the game. Must be consumed against a fresh live read.</summary>
public sealed class AdaptiveExecutionLease
{
    public AdaptiveDecisionIdentity Decision { get; }
    public VulcanSkill Action { get; }
    public AdaptiveSearchState PredictedState { get; }

    internal AdaptiveExecutionLease(AdaptiveDecisionIdentity decision, VulcanSkill action, AdaptiveSearchState predictedState)
        => (Decision, Action, PredictedState) = (decision, action, predictedState);
}
