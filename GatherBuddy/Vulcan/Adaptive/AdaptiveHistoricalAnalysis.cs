using System;

namespace GatherBuddy.Vulcan.Adaptive;

public sealed record AdaptiveHistoricalObservation(AdaptiveDecisionIdentity Identity, AdaptiveObservedFrame NativeFrame,
    TimeSpan ObservedAt, VulcanSkill? ExistingSolverAttempt = null, TimeSpan? AttemptedAt = null)
{
    public string? ExistingSolverAttemptName => ExistingSolverAttempt?.ToString();
    public long ActionOrdinal { get; init; }
    public AdaptiveSeedBatch? Seeding { get; init; }
}

/// <summary>Offline evidence for a captured root. Never a current recommendation or execution identity.</summary>
public sealed record AdaptiveHistoricalResult(AdaptiveHistoricalObservation Source, TimeSpan DispatchedAt,
    TimeSpan ResultObservedAt, AdaptiveSearchStatus? SearchStatus, string Reason,
    AdaptiveSearchDiagnostics? Diagnostics, string ReplayValidation, AdaptivePlan? ValidatedPlan)
{
    public const string HistoricalLabel = "Historical shadow analysis — not current; never executable";
    public string Usage => HistoricalLabel;
    public bool Executable => false;
    public string? SearchStatusName => SearchStatus?.ToString();
    public TimeSpan AgeAtRetention => ResultObservedAt - Source.ObservedAt;
    public VulcanSkill? ProposedFirstAction => ValidatedPlan is { Actions.Count: > 0 } ? ValidatedPlan.Actions[0] : null;
    public string? ProposedFirstActionName => ProposedFirstAction?.ToString();
    public int? ValidatedFinalQuality => ValidatedPlan?.FinalState.Quality;
    public bool? DesiredQualityReached => ValidatedPlan?.TargetReached;
}

public sealed record AdaptiveHistoricalAnalysisSummary(int ObservationsSubmitted, int ObservationsSkipped,
    int SearchesDispatched, int SearchCancellationRequests, int SearchResultsObserved,
    int RevokedResultsDiscarded, int HistoricalResultsRetained, int ValidatedPlans, int DesiredQualityPlans,
    long DroppedResults, long DroppedLifecycleEvents, AdaptiveSearchLifecycleEvent? LastSearchEvent,
    AdaptiveSearchLifecycleEvent? LastObservedSearchResult);
