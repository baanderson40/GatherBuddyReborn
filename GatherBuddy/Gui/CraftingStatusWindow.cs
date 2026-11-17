using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.Crafting;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using GatherBuddy.Automation;


namespace GatherBuddy.Gui;

public class CraftingStatusWindow : M3Window
{
    private CraftingQueueProcessor? _queueProcessor;
    private bool? _pendingCollapseState = null;
    private bool _requestFocus = false;
    private IReadOnlyList<CraftingListItem>? _cachedEstimateQueue = null;
    private CraftingListConsumableSettings? _cachedEstimateConsumables = null;
    private long[] _cachedRemainingEstimatesByIndex = [];
    private int _cachedEstimateQueueCount = -1;
    private int _cachedEstimateActionDelayMs = -1;

    public CraftingStatusWindow()
        : base("Crafting Status###GatherBuddyCraftingStatus", ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;
        ShowCloseButton = true;
        RespectCloseHotkey = true;
        SizeCondition = ImGuiCond.Appearing;
    }

    public void SetQueueProcessor(CraftingQueueProcessor? processor)
    {
        _queueProcessor = processor;
        ResetRemainingEstimateCache();
        if (processor == null)
        {
            IsOpen = false;
            _pendingCollapseState = null;
            _requestFocus = false;
            return;
        }

        OpenOrRestore();
    }

    public bool HasActiveQueue
        => _queueProcessor != null;

    public void OpenOrRestore()
    {
        if (_queueProcessor == null)
        {
            return;
        }

        IsOpen = true;
        _pendingCollapseState = false;
        _requestFocus = true;
    }

    public override bool DrawConditions()
    {
        return _queueProcessor != null && IsOpen;
    }

    protected override void PreDrawThemed()
    {
        if (!IsOpen)
        {
            return;
        }

        ImGui.SetNextWindowSizeConstraints(VulcanUiScaling.Scaled(300f, 0f),
            new Vector2(VulcanUiScaling.Scaled(420f), float.MaxValue));

        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }

        if (_requestFocus)
        {
            ImGui.SetNextWindowFocus();
            _requestFocus = false;
        }
    }

    public override void Draw()
    {
        if (_queueProcessor == null)
        {
            return;
        }

        ReportFocus();
        var currentState = _queueProcessor.CurrentState;
        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var currentItem = _queueProcessor.CurrentRecipeItem;
        var complete = currentState == CraftingQueueProcessor.QueueState.Complete;
        var paused = _queueProcessor.Paused;
        var totalCount = _queueProcessor.QueueCount;
        var coordinator = GatherBuddy.AutoRetainerIntegration?.Coordinator;
        var handoff = coordinator?.YieldRequested == true ? coordinator : null;
        string? recipeName = null;
        if (currentItem != null
         && Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>()?.TryGetRow(currentItem.RecipeId, out var recipe) == true)
            recipeName = recipe.ItemResult.Value.Name.ExtractText();
        var gatherItemName = currentState == CraftingQueueProcessor.QueueState.WaitingForGather
            && ReferenceEquals(_queueProcessor, CraftingGatherBridge.AutoRetainerQueue)
            && GatherBuddy.AutoGather.CurrentRequestedTarget is { } target
                ? target.Name[GatherBuddy.Language] : null;

        var width = 300f * M3.Scale;
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width);
        var header = CraftingStatusPresentation.GetHeader(currentState, paused, handoff?.State, recipeName,
            _queueProcessor.ActivePurchaseItemName, gatherItemName);
        var (_, stateTone, stateIcon) = DescribeState(currentState, paused);
        M3Widgets.Pill("##craft_state", header, stateTone, stateIcon);
        if (handoff != null && ImGui.IsItemHovered())
            M3Tooltip.Hovered(handoff.Status);
        var progress = _queueProcessor.Progress;
        using (M3Card.Begin("##craftProgress", "Craft progress", FontAwesomeIcon.Tasks, M3.AccentFor(0)))
        {
            M3Text.Draw($"{progress.ConfirmedCrafts} / {progress.RequestedCrafts} crafts confirmed", M3TextStyle.Muted);
            M3Tooltip.Hovered("Counts expanded craft entries, not output item units. Only inventory-confirmed output advances successful crafts; explicit skips are excluded.");
            var progressWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - M3Card.RightInset);
            M3Widgets.LinearProgress(new Vector2(progressWidth, 6f * M3.Scale), progress.RequestedCrafts > 0
                ? Math.Clamp((float)progress.ConfirmedCrafts / progress.RequestedCrafts, 0f, 1f) : 0f);
            M3Text.Draw($"{progress.RemainingCrafts} crafts remaining", M3TextStyle.Muted);
            if (progress.ExplicitSkips > 0)
                M3Text.Draw($"{progress.ExplicitSkips} explicitly skipped", M3TextStyle.Warning);
        }

        if (handoff?.State == AutoRetainerHandoffState.Failed)
            AutoRetainerSettingsUi.DrawStatus();
        if (currentState == CraftingQueueProcessor.QueueState.BlockedByMaterials)
        {
            M3Widgets.Banner("##blockedMaterials", "Remaining work is retained. Add materials and recheck without purchases, or explicitly retry acquisition under the captured run policy.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            if (M3Widgets.Button("##recheckMaterials", "Recheck materials (no purchases)", M3ButtonStyle.Tonal, FontAwesomeIcon.Sync))
                _queueProcessor.RecheckAcquiredMaterials();
            if (M3Widgets.Button("##retryAcquisition", "Retry acquisition", M3ButtonStyle.Outlined, FontAwesomeIcon.Redo,
                    tooltip: "May spend gil/current-GC seals allowed by the captured policy, then gather remaining supported materials. Earlier purchase spending still counts toward the run cap."))
                _queueProcessor.RetryMaterialAcquisition();
        }

        if (paused && !string.IsNullOrWhiteSpace(_queueProcessor.PauseReason))
            M3Text.Draw(_queueProcessor.PauseReason, M3TextStyle.Warning, true);

        if (ShouldShowRemainingEstimate(currentState))
        {
            M3Text.Draw($"About {CraftingTimeEstimator.FormatDuration(GetRemainingEstimate(currentIndex))} left", M3TextStyle.Muted);
            M3Tooltip.Hovered("Estimate uses the active craft path for each queue item. Excludes gathering, repairs, and job switching.");
        }
        else if (!ShouldRetainRemainingEstimateCache(currentState))
        {
            ResetRemainingEstimateCache();
        }

        ImGui.Dummy(new Vector2(width, M3.Space1));
        if (complete)
        {
            M3Text.Draw($"All {totalCount} crafts done", M3TextStyle.Success);
            if (M3Widgets.Button("##craft_close", "Close", M3ButtonStyle.Filled, FontAwesomeIcon.Check))
            {
                IsOpen = false;
                _queueProcessor = null;
            }
            return;
        }

        var retainedStop = currentState is CraftingQueueProcessor.QueueState.BlockedByMaterials
            or CraftingQueueProcessor.QueueState.AcquisitionFailed or CraftingQueueProcessor.QueueState.Cancelled;
        if (paused)
        {
            if (!retainedStop && handoff?.IsStopping != true)
            {
                using (ImRaii.Disabled(handoff != null))
                {
                    if (M3Widgets.Button("##craft_resume", "Resume", M3ButtonStyle.Filled, FontAwesomeIcon.Play))
                        _queueProcessor.Resume();
                }
                if (handoff != null) M3Tooltip.Hovered(handoff.Status);
            }
        }
        else if (!retainedStop && M3Widgets.Button("##craft_pause", "Pause", M3ButtonStyle.Tonal, FontAwesomeIcon.Pause,
                     tooltip: "Pause queue processing. An active crafting action may finish first."))
        {
            _queueProcessor.Pause();
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.Button("##craft_stop", "Stop queue", M3ButtonStyle.Danger, FontAwesomeIcon.Stop))
        {
            CraftingGatherBridge.StopQueue();
            return;
        }

        var completionEnabled = GatherBuddy.Config.AutoGatherConfig.EnableAutoRetainerWhenFinished;
        if (M3Widgets.RowSwitch("AutoRetainer when finished##craftCompletionHandoff", ref completionEnabled))
        {
            AutoRetainerCompletionPreference.SetEnabled(completionEnabled);
            GatherBuddy.Config.Save();
        }

        if (ImGui.CollapsingHeader("Details"))
        {
            if (currentItem != null)
            {
                var recipeProgress = CraftingStatusPresentation.GetRecipeProgress(_queueProcessor.Queue, currentIndex, currentItem.RecipeId);
                var label = handoff != null ? "Parked recipe" : CraftingStatusPresentation.IsRecipePhase(currentState) ? "Current recipe" : "Next queued recipe";
                M3Text.Draw($"{label}: {recipeProgress.ConfirmedCrafts} / {recipeProgress.RequestedCrafts} crafts confirmed", M3TextStyle.Body, true);
                if ((handoff != null || !CraftingStatusPresentation.IsRecipePhase(currentState)) && recipeName != null)
                    M3Text.Draw(recipeName, M3TextStyle.Muted, true);
            }
            M3Text.Draw($"Queue entries remaining: {Math.Max(0, totalCount - Math.Max(0, currentIndex))}", M3TextStyle.Muted);
            if (handoff != null) M3Text.Draw(handoff.Status, M3TextStyle.Warning, true);
            if (!string.IsNullOrEmpty(_queueProcessor.RetainerCoordinationStatus)
                && currentState is CraftingQueueProcessor.QueueState.NavigatingToRetainerBell
                    or CraftingQueueProcessor.QueueState.WithdrawingFromRetainer
                    or CraftingQueueProcessor.QueueState.WaitingForRetainerTravel)
                M3Text.Draw(_queueProcessor.RetainerCoordinationStatus, M3TextStyle.Muted, true);
            if (currentState is CraftingQueueProcessor.QueueState.BlockedByMaterials or CraftingQueueProcessor.QueueState.AcquisitionFailed)
            {
                M3Text.Draw(_queueProcessor.AcquisitionStatus, M3TextStyle.Warning, true);
                foreach (var shortage in _queueProcessor.AcquisitionShortages)
                    M3Text.Draw(shortage, M3TextStyle.Muted, true);
            }
        }

        if (M3Widgets.Button("##craft_open_vulcan", "Open Vulcan", M3ButtonStyle.Text, FontAwesomeIcon.Hammer))
            GatherBuddy.VulcanWindow?.RestoreWindow();
    }

    private static (string Label, Vector4 Tone, FontAwesomeIcon Icon) DescribeState(CraftingQueueProcessor.QueueState state, bool paused)
    {
        if (paused && state != CraftingQueueProcessor.QueueState.Complete)
            return ("Paused", M3.Severity(M3Severity.Warning), FontAwesomeIcon.Pause);
        var label = state switch
        {
            CraftingQueueProcessor.QueueState.Idle => "Idle",
            CraftingQueueProcessor.QueueState.NavigatingToRetainerBell => "Retainer bell",
            CraftingQueueProcessor.QueueState.WaitingForGather => "Gathering materials",
            CraftingQueueProcessor.QueueState.WaitingForJobSwitch => "Switching job",
            CraftingQueueProcessor.QueueState.Repairing => "Repairing",
            CraftingQueueProcessor.QueueState.ExtractingMateria => "Extracting materia",
            CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution => "Solving",
            CraftingQueueProcessor.QueueState.ReadyForCraft => "Ready to craft",
            CraftingQueueProcessor.QueueState.Crafting => "Crafting",
            CraftingQueueProcessor.QueueState.Complete => "Complete",
            CraftingQueueProcessor.QueueState.BlockedByMaterials => "Materials blocked",
            CraftingQueueProcessor.QueueState.AcquisitionFailed => "Acquisition failed",
            _ => "Working",
        };
        return state == CraftingQueueProcessor.QueueState.Complete
            ? (label, M3.Severity(M3Severity.Success), FontAwesomeIcon.CheckCircle)
            : state is CraftingQueueProcessor.QueueState.Crafting or CraftingQueueProcessor.QueueState.ReadyForCraft
                ? (label, M3.Scheme.Primary, FontAwesomeIcon.Hammer)
                : (label, M3.Severity(M3Severity.Info), FontAwesomeIcon.HourglassHalf);
    }

    private void ResetRemainingEstimateCache()
    {
        _cachedEstimateQueue = null;
        _cachedEstimateConsumables = null;
        _cachedRemainingEstimatesByIndex = [];
        _cachedEstimateQueueCount = -1;
        _cachedEstimateActionDelayMs = -1;
    }

    private long GetRemainingEstimate(int currentIndex)
    {
        if (_queueProcessor == null)
        {
            return 0;
        }

        var queue = _queueProcessor.Queue;
        var consumables = _queueProcessor.ListConsumables;
        var actionDelayMs = GatherBuddy.Config.VulcanExecutionDelayMs;
        if (ReferenceEquals(_cachedEstimateQueue, queue)
         && ReferenceEquals(_cachedEstimateConsumables, consumables)
         && _cachedEstimateQueueCount == queue.Count
         && _cachedEstimateActionDelayMs == actionDelayMs)
        {
            return GetCachedRemainingEstimateAtIndex(currentIndex);
        }

        _cachedEstimateQueue = queue;
        _cachedEstimateConsumables = consumables;
        _cachedEstimateQueueCount = queue.Count;
        _cachedEstimateActionDelayMs = actionDelayMs;
        _cachedRemainingEstimatesByIndex = BuildRemainingEstimates(queue, actionDelayMs, consumables);
        return GetCachedRemainingEstimateAtIndex(currentIndex);
    }

    private long GetCachedRemainingEstimateAtIndex(int currentIndex)
    {
        if (_cachedRemainingEstimatesByIndex.Length == 0)
        {
            return 0;
        }

        var safeIndex = Math.Clamp(currentIndex, 0, _cachedRemainingEstimatesByIndex.Length - 1);
        return _cachedRemainingEstimatesByIndex[safeIndex];
    }

    private static long[] BuildRemainingEstimates(IReadOnlyList<CraftingListItem> queue, int actionDelayMs, CraftingListConsumableSettings? consumables)
    {
        if (queue.Count == 0)
        {
            return [];
        }

        var remainingByIndex = new long[queue.Count];
        long remainingMs = 0;
        for (var i = queue.Count - 1; i >= 0; i--)
        {
            remainingMs += CraftingTimeEstimator.EstimateItemMs(queue[i], actionDelayMs, consumables);
            remainingByIndex[i] = remainingMs;
        }

        return remainingByIndex;
    }

    private static bool ShouldShowRemainingEstimate(CraftingQueueProcessor.QueueState state)
    {
        return state == CraftingQueueProcessor.QueueState.ReadyForCraft
            || state == CraftingQueueProcessor.QueueState.Crafting;
    }

    private static bool ShouldRetainRemainingEstimateCache(CraftingQueueProcessor.QueueState state)
    {
        return state == CraftingQueueProcessor.QueueState.ReadyForCraft
            || state == CraftingQueueProcessor.QueueState.Crafting
            || state == CraftingQueueProcessor.QueueState.WaitingForJobSwitch
            || state == CraftingQueueProcessor.QueueState.Repairing
            || state == CraftingQueueProcessor.QueueState.ExtractingMateria;
    }

    private static string GetStateDisplayName(CraftingQueueProcessor.QueueState state)
    {
        return state switch
        {
            CraftingQueueProcessor.QueueState.Idle => "Idle",
            CraftingQueueProcessor.QueueState.NavigatingToRetainerBell => "Navigating to Retainer Bell",
            CraftingQueueProcessor.QueueState.WaitingForGather => "Gathering Materials",
            CraftingQueueProcessor.QueueState.WaitingForJobSwitch => "Switching Job",
            CraftingQueueProcessor.QueueState.Repairing => "Repairing Equipment",
            CraftingQueueProcessor.QueueState.ExtractingMateria => "Extracting Materia",
            CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution => "Solving with Raphael",
            CraftingQueueProcessor.QueueState.ReadyForCraft => "Ready to Craft",
            CraftingQueueProcessor.QueueState.Crafting => "Crafting",
            CraftingQueueProcessor.QueueState.Complete => "Complete",
            _ => "Unknown"
        };
    }

    private (int currentCraft, int totalCrafts) GetCurrentRecipeCraftNumbers()
    {
        if (_queueProcessor == null)
        {
            return (0, 0);
        }

        var currentItem = _queueProcessor.CurrentRecipeItem;
        if (currentItem == null)
        {
            return (0, 0);
        }

        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var currentRecipeId = currentItem.RecipeId;

        var firstIndex = currentIndex;
        while (firstIndex > 0)
        {
            var prevItem = GetQueueItemAt(firstIndex - 1);
            if (prevItem == null || prevItem.RecipeId != currentRecipeId)
            {
                break;
            }

            firstIndex--;
        }

        var lastIndex = currentIndex;
        while (lastIndex < _queueProcessor.QueueCount - 1)
        {
            var nextItem = GetQueueItemAt(lastIndex + 1);
            if (nextItem == null || nextItem.RecipeId != currentRecipeId)
            {
                break;
            }

            lastIndex++;
        }

        var currentCraftNumber = currentIndex - firstIndex + 1;
        var totalCrafts = lastIndex - firstIndex + 1;

        return (currentCraftNumber, totalCrafts);
    }

    private CraftingListItem? GetQueueItemAt(int index)
    {
        if (_queueProcessor == null || index < 0 || index >= _queueProcessor.QueueCount)
        {
            return null;
        }

        return _queueProcessor.Queue[index];
    }

}
