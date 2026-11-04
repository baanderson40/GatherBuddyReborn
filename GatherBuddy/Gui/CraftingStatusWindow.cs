using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GatherBuddy.Crafting;
using GatherBuddy.Automation;

namespace GatherBuddy.Gui;

public class CraftingStatusWindow : Window
{
    private CraftingQueueProcessor? _queueProcessor;
    private bool? _pendingCollapseState = null;
    private bool _requestFocus = false;
    private bool _wasFocusedLastFrame = false;
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
            return;

        IsOpen = true;
        _pendingCollapseState = false;
        _requestFocus = true;
    }

    public override bool DrawConditions()
    {
        return _queueProcessor != null && IsOpen;
    }

    public override void PreDraw()
    {
        if (!IsOpen)
            return;

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
            return;
        
        var isFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (isFocused)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow("Crafting Status###GatherBuddyCraftingStatus");
            _wasFocusedLastFrame = true;
        }
        else if (_wasFocusedLastFrame)
        {
            GatherBuddy.ControllerSupport?.UpdateFocusedWindow(null);
            _wasFocusedLastFrame = false;
        }

        var currentState = _queueProcessor.CurrentState;
        var currentIndex = _queueProcessor.CurrentQueueIndex;
        var currentItem = _queueProcessor.CurrentRecipeItem;
        var coordinator = GatherBuddy.AutoRetainerIntegration?.Coordinator;
        var handoff = coordinator?.YieldRequested == true ? coordinator : null;
        string? recipeName = null;
        if (currentItem != null
         && Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().TryGetRow(currentItem.RecipeId, out var recipe))
            recipeName = recipe.ItemResult.Value.Name.ExtractText();
        var gatherItemName = currentState == CraftingQueueProcessor.QueueState.WaitingForGather
            && ReferenceEquals(_queueProcessor, CraftingGatherBridge.AutoRetainerQueue)
            && GatherBuddy.AutoGather.CurrentRequestedTarget is { } target
                ? target.Name[GatherBuddy.Language] : null;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 0.8f, 1.0f));
        ImGui.TextWrapped(CraftingStatusPresentation.GetHeader(currentState, _queueProcessor.Paused, handoff?.State,
            recipeName, _queueProcessor.ActivePurchaseItemName, gatherItemName));
        ImGui.PopStyleColor();
        if (handoff != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(handoff.Status);
        ImGui.Spacing();

        var progress = _queueProcessor.Progress;
        ImGui.Text($"{progress.ConfirmedCrafts} / {progress.RequestedCrafts} crafts confirmed");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Counts expanded craft entries, not output item units. Only inventory-confirmed output advances successful crafts; explicit skips are excluded.");
        ImGui.ProgressBar(progress.RequestedCrafts > 0
                ? (float)progress.ConfirmedCrafts / progress.RequestedCrafts : 0f,
            new Vector2(-1f, VulcanUiScaling.Scaled(8f)), string.Empty);
        ImGui.Text($"{progress.RemainingCrafts} crafts remaining");
        if (progress.ExplicitSkips > 0)
            ImGui.TextDisabled($"{progress.ExplicitSkips} explicitly skipped");

        // Recovery controls remain visible even with Details collapsed.
        if (handoff?.State == AutoRetainerHandoffState.Failed)
            AutoRetainerSettingsUi.DrawStatus();
        if (currentState == CraftingQueueProcessor.QueueState.BlockedByMaterials)
        {
            ImGui.TextWrapped("Remaining work is retained. Add materials and Recheck without purchases, or explicitly Retry acquisition under the captured run policy.");
            if (ImGui.Button("Recheck materials (no purchases)")) _queueProcessor.RecheckAcquiredMaterials();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-read current inventory and select ready recipes. Does not start purchases or gathering.");
            if (ImGui.Button("Retry acquisition")) _queueProcessor.RetryMaterialAcquisition();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("May spend gil/current-GC seals allowed by the captured policy, then gather remaining supported materials. Earlier purchase spending still counts toward the run cap. Close crafting/vendor interactions first.");
        }

        if (_queueProcessor.Paused && !string.IsNullOrWhiteSpace(_queueProcessor.PauseReason))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.82f, 0.24f, 1.0f), _queueProcessor.PauseReason);
            ImGui.PopTextWrapPos();
        }

        if (!ShouldRetainRemainingEstimateCache(currentState))
            ResetRemainingEstimateCache();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (currentState == CraftingQueueProcessor.QueueState.Complete)
        {
            if (ImGui.Button("Close"))
            {
                IsOpen = false;
                _queueProcessor = null;
                return;
            }
        }
        else
        {
            var retainedStop = currentState is CraftingQueueProcessor.QueueState.BlockedByMaterials
                or CraftingQueueProcessor.QueueState.AcquisitionFailed or CraftingQueueProcessor.QueueState.Cancelled;
            if (!retainedStop && !_queueProcessor.Paused && handoff?.IsStopping != true)
            {
                if (ImGui.Button("Pause"))
                {
                    _queueProcessor.Pause();
                }
                if (handoff != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Prevent automatic queue resume after AutoRetainer finishes. Does not cancel the AR pass.");
            }
            else if (!retainedStop && handoff?.IsStopping != true)
            {
                ImGui.BeginDisabled(handoff != null);
                if (ImGui.Button("Resume"))
                {
                    _queueProcessor.Resume();
                }
                ImGui.EndDisabled();
                if (handoff != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Wait for the AutoRetainer handoff to settle before resuming.");
            }
            
            if (!retainedStop && handoff?.IsStopping != true) ImGui.SameLine();
            if (ImGui.Button("Stop Queue"))
            {
                CraftingGatherBridge.StopQueue();
                return; // Stop can clear the queue/window during this draw.
            }
        }

        if (ImGui.CollapsingHeader("Details"))
        {
            if (currentItem != null)
            {
                var recipeProgress = CraftingStatusPresentation.GetRecipeProgress(_queueProcessor.Queue, currentIndex, currentItem.RecipeId);
                var recipePhase = CraftingStatusPresentation.IsRecipePhase(currentState);
                var label = handoff != null ? "Parked recipe" : recipePhase ? "Current recipe" : "Next queued recipe";
                ImGui.TextWrapped($"{label}: {recipeProgress.ConfirmedCrafts} / {recipeProgress.RequestedCrafts} crafts confirmed");
                if ((handoff != null || !recipePhase) && recipeName != null) ImGui.TextWrapped(recipeName);
            }
            ImGui.Text($"Queue entries remaining: {Math.Max(0, _queueProcessor.QueueCount - currentIndex)}");
            if (handoff == null && ShouldShowRemainingEstimate(currentState))
            {
                ImGui.Text($"Estimated crafting time: ~{CraftingTimeEstimator.FormatDuration(GetRemainingEstimate(currentIndex))}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Estimate for remaining crafts; excludes gathering, repairs, travel, and AutoRetainer. Raphael uses cached solutions when available, otherwise a provisional default.");
            }
            if (handoff != null) ImGui.TextWrapped(handoff.Status);
            // Preserve useful failure/shortage diagnostics, not routine acquisition chatter.
            if (currentState is CraftingQueueProcessor.QueueState.BlockedByMaterials or CraftingQueueProcessor.QueueState.AcquisitionFailed)
            {
                ImGui.TextWrapped(_queueProcessor.AcquisitionStatus);
                foreach (var shortage in _queueProcessor.AcquisitionShortages) ImGui.TextWrapped(shortage);
            }
        }
        if (ImGui.Button("Open Vulcan") && GatherBuddy.VulcanWindow != null)
            GatherBuddy.VulcanWindow.IsOpen = true;
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
            return 0;

        var queue = _queueProcessor.Queue;
        var consumables = _queueProcessor.ListConsumables;
        var actionDelayMs = GatherBuddy.Config.VulcanExecutionDelayMs;
        if (ReferenceEquals(_cachedEstimateQueue, queue)
         && ReferenceEquals(_cachedEstimateConsumables, consumables)
         && _cachedEstimateQueueCount == queue.Count
         && _cachedEstimateActionDelayMs == actionDelayMs)
            return GetCachedRemainingEstimateAtIndex(currentIndex);

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
            return 0;

        var safeIndex = Math.Clamp(currentIndex, 0, _cachedRemainingEstimatesByIndex.Length - 1);
        return _cachedRemainingEstimatesByIndex[safeIndex];
    }

    private static long[] BuildRemainingEstimates(IReadOnlyList<CraftingListItem> queue, int actionDelayMs, CraftingListConsumableSettings? consumables)
    {
        if (queue.Count == 0)
            return [];

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

}
