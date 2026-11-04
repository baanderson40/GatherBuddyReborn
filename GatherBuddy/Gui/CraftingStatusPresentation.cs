using System.Collections.Generic;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;

namespace GatherBuddy.Gui;

/// <summary>Managed-only status formatting. Never changes queue or handoff state.</summary>
internal static class CraftingStatusPresentation
{
    public static string GetHeader(CraftingQueueProcessor.QueueState state, bool paused,
        AutoRetainerHandoffState? handoff, string? recipeName, string? purchaseItemName = null, string? gatherItemName = null)
    {
        if (handoff != null)
            return "AutoRetainer · " + (handoff switch
            {
                AutoRetainerHandoffState.Yielding => "Yielding",
                AutoRetainerHandoffState.StartingPass => "Starting pass",
                AutoRetainerHandoffState.RunningPass => "Processing pass",
                AutoRetainerHandoffState.WaitingForIdle => "Cleaning up",
                AutoRetainerHandoffState.Returning => "Returning",
                AutoRetainerHandoffState.Settling => "Settling",
                AutoRetainerHandoffState.Cancelling => "Stopping",
                AutoRetainerHandoffState.Failed => "Recovery required",
                _ => "Waiting",
            });
        var subject = state switch
        {
            CraftingQueueProcessor.QueueState.PurchasingMaterials => purchaseItemName,
            CraftingQueueProcessor.QueueState.WaitingForGather => gatherItemName,
            _ => IsRecipePhase(state) ? recipeName : null,
        };
        var phase = GetStateDisplayName(state);
        if (state == CraftingQueueProcessor.QueueState.PurchasingMaterials && string.IsNullOrWhiteSpace(subject)) phase = "Purchasing materials";
        if (state == CraftingQueueProcessor.QueueState.WaitingForGather && string.IsNullOrWhiteSpace(subject)) phase = "Gathering materials";
        if (paused) phase = IsRecipePhase(state) ? "Paused" : $"Paused · {phase}";
        return string.IsNullOrWhiteSpace(subject) ? phase : $"{phase} · {subject}";
    }

    public static bool IsRecipePhase(CraftingQueueProcessor.QueueState state)
        => state is CraftingQueueProcessor.QueueState.WaitingForJobSwitch
            or CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution
            or CraftingQueueProcessor.QueueState.ReadyForCraft
            or CraftingQueueProcessor.QueueState.Crafting
            or CraftingQueueProcessor.QueueState.WaitingForCraftResult;

    private static string GetStateDisplayName(CraftingQueueProcessor.QueueState state)
        => state switch
        {
            CraftingQueueProcessor.QueueState.Idle => "Idle",
            CraftingQueueProcessor.QueueState.NavigatingToRetainerBell => "Retainer restock",
            CraftingQueueProcessor.QueueState.WithdrawingFromRetainer => "Withdrawing materials",
            CraftingQueueProcessor.QueueState.WaitingForGather => "Gathering",
            CraftingQueueProcessor.QueueState.PlanningAcquisition => "Planning materials",
            CraftingQueueProcessor.QueueState.WaitingForVendorData => "Waiting for vendor data",
            CraftingQueueProcessor.QueueState.PurchasingMaterials => "Purchasing",
            CraftingQueueProcessor.QueueState.WaitingForVendorCleanup => "Vendor cleanup",
            CraftingQueueProcessor.QueueState.BlockedByMaterials => "Materials blocked",
            CraftingQueueProcessor.QueueState.AcquisitionFailed => "Acquisition failed",
            CraftingQueueProcessor.QueueState.Cancelled => "Cancelled",
            CraftingQueueProcessor.QueueState.WaitingForJobSwitch => "Switching job",
            CraftingQueueProcessor.QueueState.Repairing => "Repairing",
            CraftingQueueProcessor.QueueState.ExtractingMateria => "Extracting materia",
            CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution => "Solving with Raphael",
            CraftingQueueProcessor.QueueState.ReadyForCraft => "Preparing",
            CraftingQueueProcessor.QueueState.Crafting => "Crafting",
            CraftingQueueProcessor.QueueState.WaitingForCraftResult => "Confirming output",
            CraftingQueueProcessor.QueueState.WaitingForPlayerInventory => "Waiting for inventory",
            CraftingQueueProcessor.QueueState.WaitingForTravelCompletion => "Preparing / traveling",
            CraftingQueueProcessor.QueueState.Complete => "Complete",
            _ => "Unknown",
        };

    public static CraftingQueueProgress GetRecipeProgress(IReadOnlyList<CraftingListItem> queue, int currentIndex, uint recipeId)
    {
        var total = 0;
        var confirmed = 0;
        var skipped = 0;
        var remaining = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            if (queue[i].RecipeId != recipeId) continue;
            total++;
            if (queue[i].Options.Skipping) skipped++;
            else if (i < currentIndex) confirmed++;
            else remaining++;
        }
        return new(total, confirmed, skipped, remaining);
    }
}
