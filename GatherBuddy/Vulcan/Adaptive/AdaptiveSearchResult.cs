using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GatherBuddy.Vulcan.Adaptive;

public enum AdaptiveSearchStatus { Found, Unknown, ProvenImpossible, Cancelled }
public enum AdaptiveSearchTermination { Completed, TransitionLimit, StoredNodeLimit, TimeLimit, SeededImprovementLimit, Cancelled }

public sealed class AdaptivePlan
{
    public IReadOnlyList<VulcanSkill> Actions { get; }
    public AdaptiveSearchState FinalState { get; }
    public int Duration { get; }
    public bool TargetReached { get; }

    internal AdaptivePlan(VulcanSkill[] actions, AdaptiveSearchState finalState, int duration, bool targetReached)
        => (Actions, FinalState, Duration, TargetReached) = (Array.AsReadOnly((VulcanSkill[])actions.Clone()), finalState, duration, targetReached);
}

public sealed record AdaptiveSearchDiagnostics(int Transitions, int StoredNodes, int CompletionProbes,
    TimeSpan Elapsed, bool BudgetExhausted, int DeepestLayer = 0, int DeepTransitions = 0)
{
    public AdaptiveSeedDiagnostics? Seeding { get; init; }
    public AdaptiveSearchTermination Termination { get; init; }
    public string TerminationReason => Termination.ToString();
    public bool SeededInputEligible { get; init; }
    public int EffectiveMaximumTransitions { get; init; }
    public int ProvenBoundPrunes { get; init; }
}

public sealed record AdaptiveSearchResult(AdaptiveSearchStatus Status, AdaptivePlan? Plan,
    AdaptiveSearchDiagnostics Diagnostics, string Reason);

public sealed record AdaptiveSearchLimits
{
    public int BeamWidth { get; init; } = 256;
    public int Horizon { get; init; } = 10;
    public int MaximumPlanLength { get; init; } = 64;
    public int MaximumTransitions { get; init; } = 40_000;
    public int MaximumStoredNodes { get; init; } = 20_000;
    public int CompletionProbeTransitions { get; init; } = 512;
    public int CompletionProbesPerLayer { get; init; } = 2;
    // Optional policy truncation ONLY after an input seed/continuation replays to desired target.
    // Null preserves full search. This immutable field participates in decision identity.
    public int? SeededImprovementTransitions { get; init; }
    public bool UseProgressCompletionBound { get; init; } = true;
    // Null disables wall-clock cutoff for reproducible offline tests; work/memory limits still apply.
    public TimeSpan? TimeLimit { get; init; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        if (BeamWidth is < 1 or > 8192 || Horizon is < 1 or > 128 || MaximumPlanLength is < 1 or > 256
            || MaximumTransitions is < 1 or > 2_000_000 || MaximumStoredNodes is < 1 or > 1_000_000
            || CompletionProbeTransitions < 1 || CompletionProbesPerLayer is < 0 or > 32
            || SeededImprovementTransitions is < 1 or > 2_000_000
            || TimeLimit is { } time && time <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveSearchLimits));
    }
}

/// <summary>One budget covers baseline, quality search, and all suffix probes.</summary>
internal sealed class AdaptiveSearchBudget(AdaptiveSearchLimits limits, CancellationToken cancellation)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal int Transitions { get; private set; }
    internal int StoredNodes { get; private set; }
    internal int Probes { get; set; }
    internal int DeepestLayer { get; set; }
    internal int DeepTransitions { get; set; }
    internal List<AdaptiveSeedAssessment>? SeedAssessments { get; set; }
    internal AdaptivePlan? SeedIncumbent { get; set; }
    internal string? SeedSource { get; set; }
    internal int SeedReplaySteps { get; set; }
    internal TimeSpan SeedReplayElapsed { get; set; }
    private int _effectiveTransitions = limits.MaximumTransitions;
    internal int EffectiveMaximumTransitions => _effectiveTransitions;
    internal bool SeededInputEligible { get; private set; }
    internal int ProvenBoundPrunes { get; set; }
    internal bool UseProgressCompletionBound => limits.UseProgressCompletionBound;
    internal void ConfigureSeededAllowance()
    {
        SeededInputEligible = SeedIncumbent is { TargetReached: true };
        if (SeededInputEligible && limits.SeededImprovementTransitions is { } allowance)
            _effectiveTransitions = Math.Min(limits.MaximumTransitions, allowance);
    }
    internal bool Cancelled => cancellation.IsCancellationRequested;
    internal bool Exhausted => Transitions >= limits.MaximumTransitions || StoredNodes >= limits.MaximumStoredNodes
        || limits.TimeLimit is { } time && _clock.Elapsed >= time;
    private bool SeededLimitReached => _effectiveTransitions < limits.MaximumTransitions && Transitions >= _effectiveTransitions;
    internal bool Stopped => Cancelled || Exhausted || SeededLimitReached;
    internal bool TryTransition()
    {
        if (Stopped) return false;
        ++Transitions;
        return true;
    }
    internal bool TryStore()
    {
        if (Stopped) return false;
        ++StoredNodes;
        return true;
    }
    internal AdaptiveSearchDiagnostics Diagnostics => new(Transitions, StoredNodes, Probes, _clock.Elapsed, Exhausted, DeepestLayer, DeepTransitions)
    {
        Termination = Cancelled ? AdaptiveSearchTermination.Cancelled
            : limits.TimeLimit is { } time && _clock.Elapsed >= time ? AdaptiveSearchTermination.TimeLimit
            : StoredNodes >= limits.MaximumStoredNodes ? AdaptiveSearchTermination.StoredNodeLimit
            : Transitions >= limits.MaximumTransitions ? AdaptiveSearchTermination.TransitionLimit
            : SeededLimitReached ? AdaptiveSearchTermination.SeededImprovementLimit : AdaptiveSearchTermination.Completed,
        SeededInputEligible = SeededInputEligible,
        EffectiveMaximumTransitions = _effectiveTransitions,
        ProvenBoundPrunes = ProvenBoundPrunes,
    };
    internal AdaptiveSearchDiagnostics DiagnosticsFor(AdaptivePlan? final)
    {
        if (SeedAssessments == null) return Diagnostics;
        var comparison = Cancelled ? "Cancelled" : SeedIncumbent == null ? "NoAcceptedSeed" : final == null ? "NoPlan"
            : AdaptivePlanValidator.IsBetter(final, SeedIncumbent) ? "Improved"
            : AdaptivePlanValidator.IsBetter(SeedIncumbent, final) ? "BelowSeed" : "Tied";
        return Diagnostics with { Seeding = new(Array.AsReadOnly(SeedAssessments.ToArray()), SeedSource,
            SeedIncumbent?.FinalState.Quality, SeedIncumbent?.TargetReached, SeedIncumbent?.Actions.Count,
            SeedIncumbent?.Duration, SeedReplaySteps, SeedReplayElapsed, comparison) };
    }
}
