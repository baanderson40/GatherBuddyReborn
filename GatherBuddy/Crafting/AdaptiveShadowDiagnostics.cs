using System;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

/// <summary>Runtime opt-in diagnostic facade. No execution, solver registration, or saved settings.</summary>
internal static class AdaptiveShadowDiagnostics
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static AdaptiveShadowSession? _session;
    private static AdaptiveShadowAnalysisRunner? _runner;
    private static AdaptiveNativeCraftCapture? _nativeCapture;
    private static volatile bool _nativeCaptureEnabled;
    private static volatile string _nativeCaptureStatus = "Native packet capture disabled; live Adaptive execution unavailable.";
    private static ulong _character;
    private static uint _job;
    private static long _frame;
    private static long _loggedGeneration = -1;
    private static long _exportVersion = -1;
    private static object? _trace;
    private static volatile bool _enabled;
    private static volatile AdaptiveShadowView _view = new("Disabled. Enable before starting a managed craft.", 0, null);
    internal static bool Enabled => _enabled;
    internal static AdaptiveShadowView View => _view;
    internal static bool NativeCaptureEnabled => _nativeCaptureEnabled;
    internal static string NativeCaptureStatus => _nativeCaptureStatus;
    internal static bool Quiescent => _runner == null || _runner.WorkerCompletion.IsCompleted;
    internal static bool PrepareForLiveTrial()
    {
        SetEnabled(false);
        return Quiescent;
    }

    internal static void RequestNativeCapture(bool enabled)
        => _ = Dalamud.Framework.RunOnFrameworkThread(() => SetNativeCapture(enabled));

    private static void SetNativeCapture(bool enabled)
    {
        if (enabled && !Enabled)
        {
            _nativeCaptureStatus = "Enable shadow diagnostics before enabling experimental native capture.";
            return;
        }
        EndCraft("Native capture option changed; enable before next managed craft only.");
        _nativeCapture?.Dispose();
        _nativeCapture = null;
        _nativeCaptureEnabled = enabled;
        if (enabled)
        {
            _nativeCapture = new();
            _nativeCapture.TryEnable();
            _nativeCaptureStatus = _nativeCapture.Status;
        }
        else _nativeCaptureStatus = "Native packet capture disabled; live Adaptive execution unavailable.";
    }

    // UI only queues an opt-in change; native reads and lifecycle mutation stay on framework thread.
    internal static void RequestEnabled(bool enabled)
        => _ = Dalamud.Framework.RunOnFrameworkThread(() => SetEnabled(enabled));

    private static void SetEnabled(bool enabled)
    {
        if (enabled && (AdaptiveLiveDiagnostics.Preparing || AdaptiveLiveDiagnostics.OwnsCraft || !AdaptiveLiveDiagnostics.Quiescent))
        {
            _view = _view with { Status = "Live trial owns or is draining the search lane; shadow enable refused." };
            return;
        }
        EndCraft(enabled ? "Enabled; waiting for next managed craft (no mid-craft attachment)." : "Shadow diagnostics disabled.");
        _enabled = enabled;
        if (!enabled) SetNativeCapture(false);
    }

    internal static void BeginCraft(Recipe recipe, global::GatherBuddy.Vulcan.CraftState? cacheContext = null)
    {
        if (!Enabled) return;
        try
        {
            EndCraft("Preparing new shadow session.");
            _trace = new { AttemptedRecipeId = recipe.RowId, StartAccepted = false };
            _view = new("Preparing new shadow session.", 0, _trace);
            if (!AdaptiveShadowNativeReader.TryCraft(recipe, out var craft, out var reason)
                || !AdaptiveShadowNativeReader.TryFrame(out var frame, out reason))
            {
                _trace = new { AttemptedRecipeId = recipe.RowId, StartAccepted = false, Rejection = reason };
                _view = new(reason, 0, _trace);
                return;
            }
            _trace = new { Craft = craft, InitialNativeFrame = frame, StartAccepted = false };
            _view = new("Validating fresh native start.", 0, _trace);
            // Reuse the lane across crafts: a revoked worker must drain before replacement starts.
            _runner ??= new();
            AdaptivePlanSeed? seed = null;
            var seedReason = "No active Raphael request context supplied.";
            if (cacheContext != null && GatherBuddy.RaphaelSolveCoordinator is { } cache)
            {
                var request = RaphaelSolveRequest.FromCraftState(cacheContext, GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions);
                cache.TryGetAdaptiveSeed(request, out seed, out seedReason);
            }
            AdaptiveNativeCaptureContext? nativeContext = null;
            if (_nativeCaptureEnabled && (_nativeCapture == null
                || !_nativeCapture.TryAttach(craft!.RecipeId, frame.Quality, out nativeContext)))
            {
                _view = new("Native capture lacks matching fresh StartPrepare/StartInfo or lost events; no fallback attachment. Restart capture before next craft.", 0, _trace);
                return;
            }
            _session = new(craft!, frame, Clock.Elapsed, runner: _runner, seed: seed, seedCaptureReason: seedReason,
                nativeContext: nativeContext);
            _character = Dalamud.PlayerState.ContentId;
            _job = Dalamud.Objects.LocalPlayer!.ClassJob.RowId;
            _loggedGeneration = _exportVersion = -1;
            RefreshView();
            GatherBuddy.Log.Information($"[AdaptiveShadow] Begin recipe={craft!.RecipeId}; target={craft.MaxQuality}; diagnostic only; native action identity unconfirmed.");
        }
        catch (Exception error) { Fault(error); }
    }

    internal static void Tick()
    {
        if (!Enabled) return;
        try
        {
            // Opt-in is login-scoped even while waiting for a craft.
            if (!Dalamud.ClientState.IsLoggedIn)
            {
                SetEnabled(false);
                return;
            }
            if (_session == null) { DrainNative(); return; }
            if (!Dalamud.ClientState.IsLoggedIn || Dalamud.Objects.LocalPlayer == null
                || Dalamud.PlayerState.ContentId != _character || Dalamud.Objects.LocalPlayer.ClassJob.RowId != _job)
            {
                SetEnabled(false);
                return;
            }
            DrainNative(); // Terminal callback arrives while executing flag remains set/before addon disappears.
            if (!Dalamud.Conditions[ConditionFlag.Crafting]) { EndCraft("Crafting ended; final native outcome may be unavailable."); return; }
            if (!CheckToolContext()) return;
            var now = Clock.Elapsed;
            _session.SetPaused(CraftingGatherBridge.AutoRetainerQueue?.Paused == true, now);
            var executing = Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
            AdaptiveObservedFrame? observed = null;
            if (!executing && AdaptiveShadowNativeReader.TryFrame(out var frame, out _)) observed = frame;
            _session.Observe(++_frame, now, observed, executing);
            var history = _session.HistoricalResults;
            if (history.Count > 0 && history[history.Count - 1] is { } result
                && result.Source.Identity.Generation != _loggedGeneration)
            {
                _loggedGeneration = result.Source.Identity.Generation;
                GatherBuddy.Log.Information($"[AdaptiveShadow] recipe={_session.Craft.RecipeId}; sourceRevision={result.Source.Identity.ObservationRevision}; historicalOnly=true; proposed={result.ProposedFirstActionName ?? "none"}; replay={result.ReplayValidation}; finalQuality={result.ValidatedFinalQuality}; age={result.AgeAtRetention}; {result.Diagnostics}");
            }
            RefreshView();
        }
        catch (Exception error) { Fault(error); }
    }

    internal static AdaptiveShadowRequest RecordRequest(VulcanSkill action)
    {
        if (!Enabled || _session == null || !Dalamud.Framework.IsInFrameworkUpdateThread) return default;
        try
        {
            if (Dalamud.Objects.LocalPlayer == null || Dalamud.PlayerState.ContentId != _character
                || Dalamud.Objects.LocalPlayer.ClassJob.RowId != _job)
            {
                EndCraft("Shadow request player/job context changed.");
                return default;
            }
            if (!AdaptiveShadowNativeReader.TryFrame(out var before, out _))
            {
                EndCraft("Request origin unavailable; refusing to infer action history.");
                return default;
            }
            if (!CheckToolContext()) return default;
            DrainNative();
            var ticket = _session.RecordRequest(before, action, Clock.Elapsed,
                _nativeCaptureEnabled ? action.ActionId(_job) : 0);
            RefreshView();
            return ticket;
        }
        catch (Exception error) { Fault(error); return default; }
    }

    internal static void ReportRequest(AdaptiveShadowRequest ticket, bool reportedSuccess)
    {
        if (!Enabled || ticket == default) return;
        if (!Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            _ = Dalamud.Framework.RunOnFrameworkThread(() => ReportRequest(ticket, reportedSuccess));
            return;
        }
        if (_session == null || ticket.SessionId != _session.SessionId) return;
        try { _session.ReportRequest(ticket, reportedSuccess); RefreshView(); }
        catch (Exception error) { Fault(error); }
    }

    private static void RefreshView()
    {
        if (_session == null) return;
        if (_exportVersion != _session.EventVersion)
        {
            _exportVersion = _session.EventVersion;
            // Detach the bounded event array on framework thread. UI serialization never
            // enumerates mutable tracker collections or invokes native services.
            _trace = _session.CaptureTrace();
        }
        _view = AdaptiveShadowView.FromTrace(_session.Status, (AdaptiveShadowTrace)_trace!);
    }

    private static void DrainNative()
    {
        if (_nativeCapture == null) return;
        var events = _nativeCapture.Drain(out var lost);
        if (_session == null) return;
        if (lost) _session.NativeCaptureLost(Clock.Elapsed);
        foreach (var evidence in events) _session.ObserveNative(evidence, Clock.Elapsed);
        RefreshView();
    }

    private static bool CheckToolContext()
    {
        if (_session == null) return false;
        var read = AdaptiveShadowNativeReader.TryMainHandItemId(out var current);
        var valid = _session.CheckToolContext(read ? current : null, Clock.Elapsed);
        if (!valid) RefreshView();
        return valid;
    }

    internal static string ExportTrace()
    {
        return AdaptiveShadowExport.Serialize(_view);
    }

    internal static void EndCraft(string reason, bool managedCraftEnded = false)
    {
        if (_session != null)
        {
            DrainNative();
            _session.End(reason, Clock.Elapsed, managedCraftEnded);
            RefreshView();
            _session.Dispose();
            _session = null;
        }
        else if (managedCraftEnded && _trace is AdaptiveShadowTrace trace)
        {
            // Native crafting flag may have ended observation before the managed Finish hook.
            // Annotate the detached summary without inventing a terminal native sample.
            _trace = trace.RecordManagedEnd(reason);
            _view = AdaptiveShadowView.FromTrace(reason, (AdaptiveShadowTrace)_trace);
        }
        _view = _view with { Status = reason };
    }

    internal static void Shutdown()
    {
        EndCraft("Shadow diagnostics disposed.");
        _runner?.Dispose();
        _runner = null;
        _enabled = false;
        _nativeCapture?.Dispose();
        _nativeCapture = null;
        _nativeCaptureEnabled = false;
    }
    private static void Fault(Exception error)
    {
        EndCraft($"Shadow diagnostics stopped: {error.GetType().Name}. Existing solver unchanged.");
        GatherBuddy.Log.Warning($"[AdaptiveShadow] {_view.Status}");
    }
}
