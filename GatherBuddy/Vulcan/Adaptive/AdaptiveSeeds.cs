using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Adaptive;

public sealed record AdaptiveSeedProvenance(string Source, string RequestKey, uint RecipeId, int Level,
    int Craftsmanship, int Control, int MaxCP, bool Manipulation, bool Specialist, int InitialQuality,
    string? ValidationContext = null);

/// <summary>Owned candidate data, never permission or a claim of native execution.</summary>
public sealed class AdaptivePlanSeed
{
    public AdaptiveSeedProvenance Provenance { get; }
    public IReadOnlyList<VulcanSkill> Actions { get; }
    public AdaptivePlanSeed(AdaptiveSeedProvenance provenance, IReadOnlyList<VulcanSkill> actions)
    {
        if (actions.Count is < 1 or > 256 || provenance.RequestKey.Length > 2048
            || actions.Any(a => !AdaptiveSimulation.Actions.Contains(a)))
            throw new ArgumentException("Empty, oversized, or unsupported seed candidate.");
        Provenance = provenance;
        Actions = Array.AsReadOnly(actions.ToArray());
    }
}

public sealed class AdaptiveSeedCandidate
{
    public AdaptivePlanSeed Seed { get; }
    public int Cursor { get; }
    public long OriginRevision { get; }
    public IReadOnlyList<VulcanSkill> Actions { get; }
    public AdaptiveSeedCandidate(AdaptivePlanSeed seed, int cursor, long originRevision)
    {
        if (cursor < 0 || cursor > seed.Actions.Count) throw new ArgumentOutOfRangeException(nameof(cursor));
        Seed = seed;
        Cursor = cursor;
        OriginRevision = originRevision;
        Actions = Array.AsReadOnly(seed.Actions.Skip(cursor).ToArray());
    }
}

public sealed record AdaptiveSeedAvailability(string Source, string Status, string Detail, int? Cursor = null);
public sealed record AdaptiveSeedBatch(IReadOnlyList<AdaptiveSeedCandidate> Candidates, IReadOnlyList<AdaptiveSeedAvailability> Availability);
public sealed record AdaptiveSeedAssessment(string Source, int Cursor, string Status, string Reason,
    int? FinalQuality = null, bool? TargetReached = null, int? ActionCount = null, int? Duration = null);
public sealed record AdaptiveSeedDiagnostics(IReadOnlyList<AdaptiveSeedAssessment> Candidates,
    string? IncumbentSource, int? IncumbentQuality, bool? IncumbentTargetReached, int? IncumbentActionCount,
    int? IncumbentDuration, int ReplaySteps, TimeSpan ReplayElapsed, string Comparison);
