using Dalamud.Bindings.ImGui;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Adaptive;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static void DrawAdaptiveSolverSettings()
    {
        var target = System.Math.Clamp(GatherBuddy.Config.RaphaelSolverConfig.AdaptiveCollectabilityTier - 1, 0, 2);
        if (ImGui.Combo("Collectable target##AdaptiveCollectability", ref target, new[] { "Tier 1 (minimum)", "Tier 2", "Tier 3 (highest)" }, 3))
        {
            GatherBuddy.Config.RaphaelSolverConfig.AdaptiveCollectabilityTier = target + 1;
            GatherBuddy.Config.Save();
            CraftingGameInterop.ReloadSolvers();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Desired reward tier for Adaptive collectables. Tier 1 is always the minimum acceptable quality; selected higher tier is not guaranteed. Default: tier 3.");
        if (ImGui.TreeNode("Experimental / Diagnostics##AdaptiveSettingsDiagnostics"))
        {
            ImGui.TextWrapped("WARNING: Selecting Adaptive authorizes automated game actions, including consecutive queue crafts. Native hook/layout remains experimental and patch-sensitive; broader unattended acceptance is incomplete.");
            ImGui.TextWrapped("Capture and seed preparation run automatically for each Adaptive craft. Collectables use local reward-tier data; missing/ambiguous data blocks synthesis. Selected/associated macros are not supported. No retry or legacy fallback after uncertainty.");
            ImGui.TextWrapped(AdaptiveLiveDiagnostics.Status);
            var seed = AdaptiveLiveDiagnostics.SeedStatus;
            ImGui.TextWrapped($"Seed-first startup: {seed.Phase} — {seed.Reason}");
            if (ImGui.Button("Copy Adaptive trace##AdaptiveSettingsExport"))
                ImGui.SetClipboardText(AdaptiveLiveDiagnostics.Export());
            ImGui.TreePop();
        }
    }

    private static void DrawAdaptiveShadowDebug()
    {
        ImGui.TextUnformatted("Adaptive Live Trial — experimental");
        ImGui.TextWrapped("Adaptive runs when selected globally or per recipe/list. No separate arming is required. Experimental native hook/layout sends game actions; no retry or legacy fallback after uncertainty.");
        if ((AdaptiveLiveDiagnostics.OwnsCraft || AdaptiveLiveDiagnostics.Preparing) && ImGui.Button("Stop Adaptive (no fallback)##AdaptiveLiveStop"))
            AdaptiveLiveDiagnostics.RequestStop();
        ImGui.TextWrapped(AdaptiveLiveDiagnostics.Status);
        var seed = AdaptiveLiveDiagnostics.SeedStatus;
        ImGui.TextWrapped($"Seed-first startup: {seed.Phase} — {seed.Reason}");
        var live = AdaptiveLiveDiagnostics.Analysis; // Published detached snapshot only; never read native state from UI.
        ImGui.TextUnformatted($"Live roots: {live.RetainedRoots}/{live.TotalRoots}; evicted: {live.DroppedRoots}; unlinked: {live.TotalUnlinkedEvents}; dropped lifecycle: {live.DroppedLifecycleEvents}; coordinator drops: {live.CoordinatorEventsDropped}");
        if (live.Roots.Count > 0) DrawAdaptiveLiveRoot(live.Roots[live.Roots.Count - 1]);
        if (ImGui.TreeNode("Retained live decision history##AdaptiveLiveRoots"))
        {
            foreach (var root in live.Roots)
                if (ImGui.TreeNode($"Root {root.RootOrdinal}, step {root.NativeFrame.StepIndex}, revision {root.Identity.ObservationRevision}##AdaptiveRoot{root.RootOrdinal}"))
                {
                    DrawAdaptiveLiveRoot(root);
                    ImGui.TextWrapped($"Native: {root.NativeFrame}");
                    ImGui.TextWrapped($"History-derived: combo={root.Identity.State.Combo}, Trained Perfection available={root.Identity.State.TrainedPerfectionAvailable}");
                    ImGui.TextWrapped(root.StateSource);
                    if (root.Plan != null) ImGui.TextWrapped($"Validated proposed route (not executable): {string.Join(", ", root.Plan.Actions)}");
                    if (root.RevocationReason != null) ImGui.TextWrapped($"Revocation: {root.RevocationReason}");
                    if (root.CloseReason != null) ImGui.TextWrapped($"Closed: {root.CloseReason}");
                    ImGui.TreePop();
                }
            foreach (var entry in live.UnlinkedLifecycleEvents)
                ImGui.TextWrapped($"Unlinked {entry.Kind}: session={entry.SessionId}, revision={entry.Revision}, generation={entry.Generation}; {entry.Reason}");
            ImGui.TextWrapped(live.TimingSource);
            ImGui.TreePop();
        }
        if (ImGui.Button("Copy bounded live trial trace##AdaptiveLiveExport"))
            ImGui.SetClipboardText(AdaptiveLiveDiagnostics.Export());
        ImGui.Separator();
        ImGui.TextUnformatted("Adaptive Shadow Diagnostics");
        var enabled = AdaptiveShadowDiagnostics.Enabled;
        if (ImGui.Checkbox("Enable for next managed craft##AdaptiveShadow", ref enabled))
            AdaptiveShadowDiagnostics.RequestEnabled(enabled);
        ImGui.TextWrapped("Read-only experimental diagnostics. Selected solver keeps control. This never executes Adaptive actions.");
        ImGui.TextWrapped("Target: full raw quality for diagnostics, not active list/solver policy. Requested actions are correlated to stable native reads; native action identity is NOT confirmed.");
        var capture = AdaptiveShadowDiagnostics.NativeCaptureEnabled;
        if (ImGui.Checkbox("Experimental native acknowledgements for next craft##AdaptiveNative", ref capture))
            AdaptiveShadowDiagnostics.RequestNativeCapture(capture);
        ImGui.TextWrapped("Opt-in native hook, unverified signature/layout. Capture diagnostic evidence first; this does not enable Adaptive execution.");
        ImGui.TextWrapped(AdaptiveShadowDiagnostics.NativeCaptureStatus);
        ImGui.Separator();
        var view = AdaptiveShadowDiagnostics.View;
        ImGui.TextWrapped(view.Status);
        ImGui.TextWrapped(Vulcan.Adaptive.AdaptiveHistoricalResult.HistoricalLabel);
        ImGui.TextUnformatted($"Request-correlated actions: {view.CorrelatedActions}");
        if (view.Trace is Vulcan.Adaptive.AdaptiveShadowTrace trace)
        {
            var summary = trace.Summary;
            ImGui.TextUnformatted($"Good-quality multiplier: {trace.Craft.GoodQualityPercent}% (main hand: {trace.Craft.Tool?.MainHandItemId.ToString() ?? "offline/unknown"})");
            ImGui.TextUnformatted($"Shadow terminal observation: {summary.TerminalObservation}");
            if (summary.NativeEvidence is { } native)
            {
                ImGui.TextWrapped($"Native packet matches: {native.MatchedAcknowledgements}; required for correlation: {native.RequiredForCorrelation}; terminal packet: {native.Terminal != null}. Layout validation pending; execution disabled.");
                if (native.LastMismatch is { } packetMismatch)
                    foreach (var difference in packetMismatch.Differences)
                        ImGui.TextUnformatted($"Native mismatch {difference.Field}: expected {difference.Expected}; observed {difference.Observed}");
            }
            var analysis = summary.HistoricalAnalysis;
            ImGui.TextUnformatted($"Searches dispatched: {analysis.SearchesDispatched}; results retained: {analysis.HistoricalResultsRetained}; skipped roots: {analysis.ObservationsSkipped}");
            ImGui.TextUnformatted($"Validated plans: {analysis.ValidatedPlans}; desired-quality plans: {analysis.DesiredQualityPlans}; dropped results: {analysis.DroppedResults}");
            ImGui.TextUnformatted($"Events retained: {trace.Events.Count}; dropped: {summary.DroppedEvents}");
            if (summary.BlockedReason != null) ImGui.TextWrapped($"Shadow blocked: {summary.BlockedReason}");
            if (summary.LastMismatch != null)
                foreach (var difference in summary.LastMismatch.Differences)
                    ImGui.TextUnformatted($"Mismatch {difference.Field}: expected {difference.Expected}; observed {difference.Observed}");
            if (trace.HistoricalResults.Count > 0)
            {
                var result = trace.HistoricalResults[trace.HistoricalResults.Count - 1];
                ImGui.TextUnformatted($"Historical source step: {result.Source.NativeFrame.StepIndex}; revision: {result.Source.Identity.ObservationRevision}; age at retention: {result.AgeAtRetention.TotalMilliseconds:F1} ms");
                ImGui.TextUnformatted($"Replay: {result.ReplayValidation}; proposed first action: {result.ProposedFirstActionName ?? "none"}");
                ImGui.TextUnformatted($"Predicted final quality: {result.ValidatedFinalQuality?.ToString() ?? "unknown"} / {trace.Craft.MaxQuality}");
                ImGui.TextWrapped(result.Reason);
                if (result.Source.Seeding is { } seeding)
                    foreach (var available in seeding.Availability)
                        ImGui.TextWrapped($"Seed {available.Source}: {available.Status} — {available.Detail}");
                if (result.Diagnostics?.Seeding is { } seedResult)
                {
                    ImGui.TextUnformatted($"Seed incumbent: {seedResult.IncumbentSource ?? "none"}; quality: {seedResult.IncumbentQuality?.ToString() ?? "unknown"}; search: {seedResult.Comparison}");
                    foreach (var candidate in seedResult.Candidates)
                        ImGui.TextUnformatted($"Seed replay {candidate.Source}: {candidate.Status}; quality {candidate.FinalQuality?.ToString() ?? "unknown"}");
                }
            }
        }
        if (ImGui.Button("Copy bounded trace##AdaptiveShadow"))
            ImGui.SetClipboardText(AdaptiveShadowDiagnostics.ExportTrace());
        ImGui.TextDisabled("Runtime-only opt-in; reset on reload/logout. Trace omits account/character IDs.");
    }

    private static void DrawAdaptiveLiveRoot(AdaptiveLiveRootRecord root)
    {
        ImGui.TextWrapped($"Root {root.RootOrdinal}: {root.SearchOutcome}; proposed={root.ProposedActionName}; dispatched={(root.Transaction?.InvocationBegan == true ? root.Transaction.ActionName : "not invoked")}");
        if (root.Plan is { } plan)
            ImGui.TextUnformatted($"Validated quality: {plan.FinalQuality}/{root.Identity.Goal.DesiredQuality}; target={plan.TargetReached}; actions={plan.ActionCount}; modeled seconds={plan.ModeledDuration}");
        if (root.Diagnostics is { } diagnostics)
        {
            ImGui.TextUnformatted($"Search: {diagnostics.Transitions}/{root.Identity.Limits.MaximumTransitions} transitions, {diagnostics.StoredNodes}/{root.Identity.Limits.MaximumStoredNodes} nodes, elapsed={diagnostics.Elapsed.TotalMilliseconds:F2} ms, budget exhausted={diagnostics.BudgetExhausted}");
            ImGui.TextUnformatted($"Search stop: {diagnostics.TerminationReason}; effective transition cap={diagnostics.EffectiveMaximumTransitions}; target-reaching input seed={diagnostics.SeededInputEligible}; proven-bound prunes={diagnostics.ProvenBoundPrunes}");
            if (diagnostics.Seeding is { } seed)
                ImGui.TextWrapped($"Seed comparison: {seed.Comparison}; incumbent={seed.IncumbentSource ?? "none"}; replay steps={seed.ReplaySteps}; replay ms={seed.ReplayElapsed.TotalMilliseconds:F3}");
        }
        ImGui.TextWrapped($"Host ms: submit→result={LiveMilliseconds(root.SubmissionToResultObservation)}, publication→dispatch={LiveMilliseconds(root.PublicationToDispatch)}, dispatch→ack={LiveMilliseconds(root.DispatchToAcknowledgement)}, dispatch→frame={LiveMilliseconds(root.DispatchToFullFrame)}, dispatch→terminal={LiveMilliseconds(root.DispatchToTerminal)}");
        if (root.Transaction != null)
            ImGui.TextWrapped($"Transaction: {root.Transaction.OutcomeName}; native return={root.Transaction.NativeReturn?.ToString() ?? "not returned"}; acknowledgement={root.Transaction.AcknowledgementMatched}; full frame={root.Transaction.FullFrameCorrelated}");
        if (root.Reason != null) ImGui.TextWrapped(root.Reason);
        if (root.CaptureWarning != null) ImGui.TextWrapped(root.CaptureWarning);
    }

    private static string LiveMilliseconds(System.TimeSpan? time) => time.HasValue ? time.Value.TotalMilliseconds.ToString("F2") : "not observed";
}
