using System;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Native scalar reads only. No guessed combo, unused-action availability, or player identity.</summary>
public readonly record struct AdaptiveObservedFrame(int StepIndex, int Progress, int Quality, int Durability, int CP,
    Condition Condition, int InnerQuiet = 0, int WasteNot = 0, int Manipulation = 0, int GreatStrides = 0,
    int Innovation = 0, int Veneration = 0, int MuscleMemory = 0, int FinalAppraisal = 0,
    bool TrainedPerfectionActive = false, bool HeartAndSoulActive = false, int Expedience = 0)
{
    internal AdaptiveSearchState ToState(bool perfectionAvailable, VulcanSkill combo) => new(
        StepIndex == 1, Progress, Quality, Durability, CP, Condition, InnerQuiet, WasteNot, Manipulation,
        GreatStrides, Innovation, Veneration, MuscleMemory, FinalAppraisal, perfectionAvailable, TrainedPerfectionActive, combo);

    internal void Validate(AdaptiveCraftSnapshot craft)
    {
        if (StepIndex is < 1 or > 1000 || Progress > craft.RequiredProgress || Durability < 0 || HeartAndSoulActive || Expedience != 0)
            throw new ArgumentException("Invalid or unsupported native crafting frame.");
        ToState(false, VulcanSkill.None).Validate(craft);
    }

    internal bool IsFreshStart => StepIndex == 1 && Condition == Condition.Normal && Progress == 0 && InnerQuiet == 0 && WasteNot == 0
        && Manipulation == 0 && GreatStrides == 0 && Innovation == 0 && Veneration == 0 && MuscleMemory == 0
        && FinalAppraisal == 0 && !TrainedPerfectionActive && !HeartAndSoulActive && Expedience == 0;
}

public readonly record struct AdaptiveShadowRequest(Guid SessionId, long RequestId);
public sealed record AdaptiveShadowEvent(long Revision, string Kind, string Detail,
    AdaptiveObservedFrame? Before = null, AdaptiveObservedFrame? After = null, VulcanSkill? RequestedAction = null)
{
    public long Sequence { get; init; }
    public TimeSpan Elapsed { get; init; }
    public AdaptiveSearchLifecycleEvent? Search { get; init; }
    public AdaptiveShadowMismatch? Mismatch { get; init; }
    public AdaptiveNativeCraftEvent? Native { get; init; }
    public AdaptiveNativeMismatch? NativeMismatch { get; init; }
    public string? RequestedActionName => RequestedAction?.ToString();
}

public sealed record AdaptiveShadowSummary(bool SessionEnded, string? SessionEndReason,
    bool ManagedCraftEnded, string? ManagedEndReason,
    string TerminalObservation, AdaptiveObservedFrame? LastStableFrame, int CorrelatedActions,
    string? BlockedReason, long TotalEvents, long DroppedEvents, AdaptiveHistoricalAnalysisSummary HistoricalAnalysis,
    AdaptiveShadowMismatch? LastMismatch = null)
{
    public AdaptiveNativeEvidenceSummary? NativeEvidence { get; init; }
}

public sealed record AdaptiveShadowTrace(AdaptiveCraftSnapshot Craft, AdaptiveCraftGoal Goal,
    AdaptiveShadowSummary Summary, System.Collections.Generic.IReadOnlyList<AdaptiveShadowEvent> Events,
    System.Collections.Generic.IReadOnlyList<AdaptiveHistoricalResult> HistoricalResults)
{
    public string AnalysisUsage => AdaptiveHistoricalResult.HistoricalLabel;
    public AdaptiveShadowTrace RecordManagedEnd(string reason) => Summary.ManagedCraftEnded ? this
        : this with { Summary = Summary with { ManagedCraftEnded = true, ManagedEndReason = reason } };

    public string ReaderLayout => "Synthesis AtkValues 15/5/9/7/12 + player CP/status parameters; native verification pending";
    public string TimingSource => "Monotonic session-relative host observation time; dispatch is not worker CPU start; result time is host polling, not task completion";
}
