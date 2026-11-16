using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Detached live confirmation projection. Never labels live evidence as historical search results.</summary>
public sealed record AdaptiveLiveEvidenceTrace(AdaptiveCraftSnapshot Craft, AdaptiveCraftGoal Goal,
    AdaptiveShadowSummary Summary, IReadOnlyList<AdaptiveShadowEvent> Events)
{
    public string EvidenceUsage => "Adaptive confirmation evidence; selected solver authorizes host dispatch, not this evidence object";
    public string NativeEvidenceUsage => "Experimental packet matching; NativeEvidence.ExecutionAuthorized=false is not blanket native-layout validation or host dispatch permission";
    public string ReaderLayout => "Synthesis AtkValues 15/5/9/7/12 + player CP/status parameters; native verification pending";
    public string TimingSource => "Confirmation event time is session-relative; transaction StartedAt uses host monotonic clock. Search diagnostics do not measure native dispatch latency.";

    public static AdaptiveLiveEvidenceTrace FromConfirmation(AdaptiveShadowTrace trace)
    {
        if (trace.HistoricalResults.Count != 0 || trace.Summary.HistoricalAnalysis.ObservationsSubmitted != 0
            || trace.Summary.HistoricalAnalysis.SearchesDispatched != 0)
            throw new ArgumentException("Historical search results cannot be relabelled as live confirmation evidence.");
        return new(trace.Craft, trace.Goal, trace.Summary, Array.AsReadOnly(new List<AdaptiveShadowEvent>(trace.Events).ToArray()));
    }
}

public sealed record AdaptiveLiveView(string Status, string GoalSource, AdaptiveDecisionSnapshot Decision,
    bool ManagedCraftEnded, AdaptiveLiveTransaction? Transaction, bool OutstandingTransaction,
    bool? PreDispatchNativeQueueOccupied, AdaptiveLiveEvidenceTrace Evidence, AdaptiveLiveAnalysisSnapshot? LiveAnalysis = null)
{
    public AdaptiveLiveSeedStatus? SeedStartup { get; init; }
    public int SchemaVersion => 4;
    public bool ExperimentalLiveTrial => true;
    public bool NativeLayoutValidated => false;
    public string DispatchPermission => "Selected Adaptive solver; not blanket native validation";
    public string Owner => "AdaptiveTrial";
}

public static class AdaptiveLiveExport
{
    public static string Serialize(AdaptiveLiveView view) => JsonSerializer.Serialize(view);
    public static string SerializePreparation(AdaptivePreparationView view) => JsonSerializer.Serialize(view);
}

/// <summary>A new/rejected startup is not the previous craft's decision history.</summary>
public sealed record AdaptivePreparationView(uint AttemptedRecipeId, Guid PreparationTicket, string Status, AdaptiveLiveSeedStatus SeedStartup)
{
    public int SchemaVersion => 4;
    public string Phase => "BeforeSynthesis";
    public bool ExperimentalLiveTrial => true;
    public bool NativeLayoutValidated => false;
    public bool StartAccepted => false;
    public AdaptiveLiveAnalysisSnapshot LiveAnalysis => AdaptiveLiveAnalysisSnapshot.Empty;
    public string DispatchPermission => "Selected Adaptive solver; preparation is not an execution lease";
}
