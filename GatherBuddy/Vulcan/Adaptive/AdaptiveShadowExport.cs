using System.Text.Json;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Published managed data only. Trace is either a detached session or a rejected-start record.</summary>
public sealed record AdaptiveShadowView(string Status, int CorrelatedActions, object? Trace)
{
    public static AdaptiveShadowView FromTrace(string status, AdaptiveShadowTrace trace)
        => new(status, trace.Summary.CorrelatedActions, trace);
}

public static class AdaptiveShadowExport
{
    public static string Serialize(AdaptiveShadowView view) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 6, ShadowOnly = true, NativeActionIdentityConfirmed = false,
        NativeEvidenceUsage = "Experimental callback layout; matched IDs/terminal scalars are diagnostic evidence, not execution authority",
        AnalysisUsage = AdaptiveHistoricalResult.HistoricalLabel,
        TargetSource = "Diagnostic full raw quality, not active solver/list policy",
        view.Status, view.CorrelatedActions, Data = view.Trace,
    }, new JsonSerializerOptions { WriteIndented = true });
}
