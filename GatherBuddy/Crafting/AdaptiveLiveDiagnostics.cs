using System;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Adaptive;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

/// <summary>Framework-thread-only selected Adaptive host. Every craft prepares fresh capture.</summary>
internal static class AdaptiveLiveDiagnostics
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly AdaptiveSolveCoordinator Coordinator = new(decisionDiagnosticsClock: () => Clock.Elapsed);
    private static AdaptiveLiveTrial? _trial;
    private static AdaptiveNativeCraftCapture? _capture;
    private static Recipe _recipe;
    private static AdaptiveCraftSnapshot? _craft;
    private static string _policy = "";
    private static ulong _character;
    private static uint _job;
    private static long _frame;
    private static volatile bool _owns;
    private static volatile string _status = "Select Adaptive to use confirmed-outcome crafting.";
    private static AdaptiveStartupGate? _startup;
    private static volatile string _export = "{}";
    private static long _exportVersion = -1;
    private static AdaptiveDecisionSnapshot? _exportDecision;
    private static string? _exportStatus;
    private static bool _exportManagedEnd;
    private static AdaptiveLiveTransaction? _exportTransaction;
    private static bool? _queueOccupied, _exportQueueOccupied;
    private static Guid _preparationTicket;
    private static uint _preparationRecipe;
    private static uint _attemptedRecipe;
    private static Guid _attemptedTicket;
    private static AdaptiveSeedPreparation? _preparation;
    private static volatile AdaptiveLiveSeedStatus _seedStatus = new("NotAttempted", "Seed-first preparation runs before managed synthesis.");
    private static AdaptiveLiveSeedStatus? _exportSeedStatus;
    internal static AdaptiveLiveSeedStatus SeedStatus => _seedStatus;
    internal static int PreparationTimeoutMs => Math.Clamp(GatherBuddy.Config.RaphaelSolverConfig.RaphaelTimeoutMinutes, 1, 60) * 60_000;
    private static long _analysisVersion = -1;
    private static volatile AdaptiveLiveAnalysisSnapshot _analysis = AdaptiveLiveAnalysisSnapshot.Empty;
    internal static AdaptiveLiveAnalysisSnapshot Analysis => _analysis;
    internal static bool Preparing => _startup is { Closed: false };
    internal static bool CaptureReady => _startup is { Closed: false, CaptureReady: true };
    internal static bool OwnsCraft => _owns;
    internal static bool Quiescent => Coordinator.WorkerCompletion.IsCompleted;
    internal static bool HasTerminal => _trial?.Terminal == true;
    internal static string Status => _status;
    internal static string Export() => _export;

    internal static void RequestStop()
        => _ = Dalamud.Framework.RunOnFrameworkThread(() =>
        {
            CraftingGatherBridge.AutoRetainerQueue?.Pause("Adaptive stopped by user; inspect craft before resuming.");
            Stop("Adaptive stopped; no fallback for owned craft.");
        });

    internal static Guid BeginSeedPreparation(uint recipeId)
    {
        if (!CraftingGameInterop.AdaptiveSelected || _owns) return default;
        CancelPreparation();
        _startup = new(Clock.Elapsed, TimeSpan.FromMilliseconds(PreparationTimeoutMs));
        _preparationTicket = _startup.Ticket;
        _attemptedRecipe = recipeId;
        _attemptedTicket = _preparationTicket;
        _analysis = AdaptiveLiveAnalysisSnapshot.Empty;
        _analysisVersion = -1;
        _preparationRecipe = recipeId;
        _preparation = new(Clock.Elapsed, TimeSpan.FromMilliseconds(PreparationTimeoutMs));
        _seedStatus = _preparation.Status;
        _exportSeedStatus = null;
        PublishPreparationStatus();
        return _preparationTicket;
    }

    internal static void CancelPreparation()
    {
        _startup?.Cancel();
        _startup = null;
        _preparation?.Cancel();
        if (_preparation != null) _seedStatus = _preparation.Status;
        _preparation = null;
        _preparationTicket = default;
        _preparationRecipe = 0;
    }

    // Nullable task result: false waits; true allows synthesis; null aborts a stale/stopped task.
    internal static bool IsSeedPreparationCurrent(Guid ticket, uint recipeId)
        => ticket != default && ticket == _preparationTicket && Preparing && !_owns
            && _preparation != null && _preparationRecipe == recipeId;

    internal static bool? PrepareCaptureForCraft(Guid ticket, uint recipeId)
    {
        if (!IsSeedPreparationCurrent(ticket, recipeId) || !Dalamud.ClientState.IsLoggedIn
            || !Dalamud.Framework.IsInFrameworkUpdateThread) return null;
        if (CraftingGatherBridge.AutoRetainerQueue?.Paused == true) return false;
        return PrepareCapture();
    }

    internal static bool? PrepareSeed(Guid ticket, Recipe recipe)
    {
        var capture = PrepareCaptureForCraft(ticket, recipe.RowId);
        if (capture != true) return capture;
        AdaptiveSeedPreparationContext? context;
        RaphaelSolveRequest? request;
        try
        {
            if (!TryPreparationContext(recipe, out context, out request, out var reason))
            {
                RejectPreparation($"Adaptive context rejected before synthesis: {reason}");
                return null;
            }
        }
        catch (Exception error)
        {
            RejectPreparation($"Adaptive context read failed: {error.GetType().Name}; synthesis not sent.");
            return null;
        }
        bool ready;
        try
        {
            ready = _preparation!.Poll(context!, Clock.Elapsed, _ => LookupSeed(request!), _ => QueueSeed(request!));
        }
        catch (Exception error) { ready = _preparation!.Fallback($"Seed preparation exception ({error.GetType().Name}); using unseeded search."); }
        _seedStatus = _preparation!.Status;
        _status = _seedStatus.Reason;
        PublishPreparationStatus();
        return ready;
    }

    private static void PublishPreparationStatus()
    {
        if (_exportSeedStatus == _seedStatus) return;
        _exportSeedStatus = _seedStatus;
        _export = AdaptiveLiveExport.SerializePreparation(new(_attemptedRecipe, _attemptedTicket, _seedStatus.Reason, _seedStatus));
    }

    private static bool? PrepareCapture()
    {
        var startup = _startup;
        if (startup == null) return null;
        try
        {
            var shadowDrained = AdaptiveShadowDiagnostics.PrepareForLiveTrial();
            var ready = startup.Poll(Clock.Elapsed, CraftingGameInterop.AdaptiveSelected,
                Dalamud.ClientState.IsLoggedIn && AdaptiveStartupGate.IsPreSynthesisIdle(
                    CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.PreparingCraft,
                    Dalamud.Conditions[ConditionFlag.Crafting], Dalamud.Conditions[ConditionFlag.PreparingToCraft],
                    Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]),
                shadowDrained && Quiescent, EnableCapture);
            if (ready == null) RejectPreparation(startup.Reason);
            else if (ready == false) _status = startup.Reason;
            return ready;
        }
        catch (Exception error)
        {
            RejectPreparation($"Adaptive capture setup rejected: {error.GetType().Name}; synthesis not sent.");
            return null;
        }
    }

    private static bool EnableCapture()
    {
        _capture?.Dispose();
        _capture = new();
        return _capture.TryEnable();
    }

    private static AdaptiveSeedLookup LookupSeed(RaphaelSolveRequest request)
    {
        var cache = GatherBuddy.RaphaelSolveCoordinator;
        if (cache.TryGetAdaptiveSeed(request, out var seed, out var reason)) return new(AdaptiveSeedLookupState.Ready, seed);
        if (cache.HasFailedSolution(request, out var failed)) return new(AdaptiveSeedLookupState.Failed, Reason: failed ?? "Solve failed.");
        if (cache.TryGetSolution(request, out _)) return new(AdaptiveSeedLookupState.Unavailable, Reason: reason);
        if (!GatherBuddy.Config.RaphaelSolverConfig.RaphaelEnabled) return new(AdaptiveSeedLookupState.Unavailable, Reason: "Raphael generation disabled.");
        return new(cache.IsKnown(request) ? AdaptiveSeedLookupState.Pending : AdaptiveSeedLookupState.Missing);
    }

    private static void QueueSeed(RaphaelSolveRequest request)
        => GatherBuddy.RaphaelSolveCoordinator.EnqueueOrPromoteRequest(request, RaphaelSolvePriority.Urgent);

    private static bool TryPreparationContext(Recipe recipe, out AdaptiveSeedPreparationContext? context,
        out RaphaelSolveRequest? request, out string reason)
    {
        context = null;
        request = null;
        if (!AdaptiveShadowNativeReader.TryCraft(recipe, out var craft, out reason)
            || !CraftingGameInterop.TryResolveAdaptiveGoal(craft!, out var goal, out var policy, out reason)) return false;
        if (!CraftingGameInterop.TryGetAdaptiveExpectedInitialQuality(recipe, out var expected))
        { reason = "Ingredient quality policy unavailable."; return false; }
        // Policy estimate is only a solve input. Actual native starting quality is replayed later.
        request = new(craft!.RecipeId, craft.Level, craft.Craftsmanship, craft.Control, craft.MaxCP,
            craft.ManipulationUnlocked, false, expected);
        context = new(Dalamud.PlayerState.ContentId, Dalamud.Objects.LocalPlayer!.ClassJob.RowId,
            craft, goal!, policy, request.GetKey(), expected);
        return true;
    }

    internal static bool TryBegin(Recipe recipe, bool required = false)
    {
        if (!CraftingGameInterop.AdaptiveSelected && !required) return false;
        var permitted = CraftingGameInterop.AdaptiveSelected && _startup?.TryConsume(_preparationTicket) == true;
        _owns = true; // Remains exclusive even if initialization rejects. Never fall back mid-craft.
        _trial?.Dispose();
        _trial = null;
        _exportVersion = -1;
        _exportDecision = null;
        _exportStatus = null;
        _exportManagedEnd = false;
        _exportTransaction = null;
        _queueOccupied = _exportQueueOccupied = null;
        _analysisVersion = -1;
        _analysis = AdaptiveLiveAnalysisSnapshot.Empty;
        _export = "{}";
        try
        {
            if (!permitted)
                throw new InvalidOperationException("Adaptive startup missing, stopped or selection changed; no legacy fallback.");
            if (!AdaptiveShadowDiagnostics.Quiescent || !Quiescent)
                throw new InvalidOperationException("Search lane not drained.");
            if (!AdaptiveShadowNativeReader.TryCraft(recipe, out var craft, out var reason))
                throw new InvalidOperationException(reason);
            if (!CraftingGameInterop.TryResolveAdaptiveGoal(craft!, out var goal, out var policy, out reason))
                throw new InvalidOperationException(reason);
            if (!AdaptiveShadowNativeReader.TryFrame(out var initial, out reason))
                throw new InvalidOperationException(reason);
            if (_capture == null || !_capture.TryAttach(recipe.RowId, initial.Quality, out var native) || native == null)
                throw new InvalidOperationException("Missing matching fresh native start; no mid-craft attachment.");
            _recipe = recipe;
            _craft = craft;
            _policy = policy;
            _character = Dalamud.PlayerState.ContentId;
            _job = Dalamud.Objects.LocalPlayer!.ClassJob.RowId;
            AdaptivePlanSeed? startupSeed = null;
            if (_preparation != null && _preparationRecipe == recipe.RowId
                && TryPreparationContext(recipe, out var seedContext, out _, out _))
            {
                startupSeed = _preparation.Consume(seedContext!);
                _seedStatus = _preparation.Status;
            }
            else _seedStatus = new("Unseeded", "No matching completed pre-synthesis preparation; using unseeded search.");
            _preparation = null;
            _startup = null;
            _preparationTicket = default;
            _preparationRecipe = 0; // Freeze handoff; never fetch a late seed during the craft.
            _trial = new(Coordinator, craft!, goal!, initial, native, Clock.Elapsed,
                TimeSpan.FromMilliseconds(Math.Max(0, GatherBuddy.Config.VulcanExecutionDelayMs)), startupSeed, _seedStatus);
            _status = _trial.Status;
            Refresh();
        }
        catch (Exception error) { Stop($"Trial start rejected: {error.Message}"); }
        return true;
    }

    internal static void Tick()
    {
        if (!Dalamud.ClientState.IsLoggedIn)
        {
            if (Preparing || _owns) { Stop("Logged out; Adaptive cancelled."); End(false); }
            return;
        }
        if (!_owns) { _capture?.Drain(out _); return; }
        if (_trial == null) return;
        try
        {
            Drain();
            if (_trial.Stopped || _trial.Terminal) { Refresh(); return; }
            if (!CheckContext()) return;
            if (!Dalamud.Conditions[ConditionFlag.Crafting])
            { Stop("Crafting ended without admitted terminal evidence."); return; }
            var executing = Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
            AdaptiveObservedFrame? frame = null;
            if (!executing && AdaptiveShadowNativeReader.TryFrame(out var read, out _)) frame = read;
            _trial.Tick(++_frame, Clock.Elapsed, frame, executing, CraftingGatherBridge.AutoRetainerQueue?.Paused == true);
            Refresh();
        }
        catch (Exception error) { Stop($"Live observation exception: {error.GetType().Name}."); }
    }

    private static bool CheckContext()
    {
        if (Dalamud.Objects.LocalPlayer == null || Dalamud.PlayerState.ContentId != _character
            || Dalamud.Objects.LocalPlayer.ClassJob.RowId != _job
            || !AdaptiveShadowNativeReader.TryCraft(_recipe, out var current, out _) || current != _craft
            || !CraftingGameInterop.TryResolveAdaptiveGoal(current!, out _, out var policy, out _) || policy != _policy)
        { Stop("Player/job/stats/tool/recipe/goal context changed or unavailable."); return false; }
        return true;
    }

    internal static unsafe void DispatchIfReady()
    {
        if (!_owns || _trial == null || _trial.Stopped || _trial.Terminal
            || !Dalamud.Framework.IsInFrameworkUpdateThread || CraftingGatherBridge.AutoRetainerQueue?.Paused == true
            || Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]) return;
        try
        {
            Drain();
            if (!CheckContext() || _trial.Stopped || _trial.Terminal
                || _trial.Decision.Phase != AdaptiveDecisionPhase.Ready) return;
            if (!AdaptiveShadowNativeReader.TryFrame(out var fresh, out _)) { Stop("Final native read unavailable."); return; }
            var action = _trial.Decision.Action;
            var id = action.ActionId(_job); // No generic ID fallback in live trial.
            var manager = ActionManager.Instance();
            var type = id >= 100000 ? ActionType.CraftAction : ActionType.Action;
            var available = id != 0 && manager != null && manager->GetActionStatus(type, id) == 0;
            _queueOccupied = manager == null ? null : manager->ActionQueued;
            _trial.TryDispatch(Clock.Elapsed, fresh, id, available, () => ActionManager.Instance()->UseAction(type, id),
                nativeQueueOccupied: _queueOccupied == true); // Observe only. Never clear/overwrite another queued action.
            Drain();
            Refresh();
        }
        catch (Exception error) { Stop($"Native dispatch exception: {error.GetType().Name}; no retry."); }
    }

    private static void Drain()
    {
        if (_capture == null || _trial == null) return;
        var packets = _capture.Drain(out var lost);
        if (lost) _trial.CaptureLost();
        foreach (var packet in packets) _trial.ObserveNative(packet, Clock.Elapsed);
    }

    private static void Refresh()
    {
        if (_trial == null) return;
        _status = _trial.Status;
        _seedStatus = _trial.StartupSeedStatus;
        // Event changes include pending/stable outcomes; detach JSON on framework thread only.
        if (_exportVersion == _trial.Evidence.EventVersion && _exportDecision == _trial.Decision && _exportStatus == _status
            && _exportManagedEnd == _trial.ManagedCraftEnded && _exportTransaction == _trial.Transaction
            && _exportQueueOccupied == _queueOccupied && _analysisVersion == _trial.AnalysisVersion && _exportSeedStatus == _seedStatus) return;
        _exportVersion = _trial.Evidence.EventVersion;
        _exportDecision = _trial.Decision;
        _exportStatus = _status;
        _exportManagedEnd = _trial.ManagedCraftEnded;
        _exportTransaction = _trial.Transaction;
        _exportQueueOccupied = _queueOccupied;
        _analysisVersion = _trial.AnalysisVersion;
        _analysis = _trial.Analysis;
        _exportSeedStatus = _seedStatus;
        _export = AdaptiveLiveExport.Serialize(new(_status, _policy, _trial.Decision, _trial.ManagedCraftEnded,
            _trial.Transaction, _trial.HasOutstandingTransaction, _queueOccupied,
            AdaptiveLiveEvidenceTrace.FromConfirmation(_trial.Evidence.CaptureTrace()), _analysis) { SeedStartup = _seedStatus });
    }

    private static void RejectPreparation(string reason)
    {
        GatherBuddy.Log.Warning($"[Adaptive] recipe={_attemptedRecipe}: {reason}");
        CraftingGatherBridge.AutoRetainerQueue?.Pause(reason);
        Stop(reason);
    }

    internal static void Stop(string reason, uint attemptedRecipeId = 0)
    {
        if (attemptedRecipeId != 0) { _attemptedRecipe = attemptedRecipeId; _attemptedTicket = default; }
        CancelPreparation();
        _trial?.Stop(reason);
        if (!_owns) { _capture?.Dispose(); _capture = null; }
        _status = reason;
        Refresh();
        if (_trial == null)
        {
            _analysis = AdaptiveLiveAnalysisSnapshot.Empty;
            _export = AdaptiveLiveExport.SerializePreparation(new(_attemptedRecipe, _attemptedTicket, reason, _seedStatus));
        }
    }

    internal static void End(bool managedEnded)
    {
        if (!_owns) return;
        Drain();
        _trial?.End(Clock.Elapsed, managedEnded);
        Refresh();
        _trial?.Dispose();
        _trial = null;
        _owns = false;
        _capture?.Dispose();
        _capture = null;
    }

    internal static void Shutdown()
    {
        Stop("Adaptive host shut down.");
        End(false);
        Coordinator.Stop(); // Keep controller alive so cancellation-ignoring worker remains owned/drainable.
    }
}
