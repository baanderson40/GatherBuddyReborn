using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private readonly JobSwitchGuard _jobSwitchGuard = new();
    private readonly Stopwatch _runClock = Stopwatch.StartNew();
    private bool _readinessSelected;
    private uint _resultItemId;
    private int _resultOutputQuantity;
    private int _resultBefore;
    private int _resultRequested;
    private int _resultReported;
    private bool _resultCancelled;
    private TimeSpan _resultDeadline;

    private List<PendingCraft> CapturePendingCrafts()
    {
        var pending = new List<PendingCraft>();
        foreach (var item in QueueItems.Skip(_currentQueueIndex))
        {
            var recipe = RecipeManager.GetRecipe(item.RecipeId)
                ?? throw new InvalidOperationException($"Recipe {item.RecipeId} is unavailable; queue retained.");
            var context = CraftingContextResolver.ResolveExecutionContext(item, recipe, _listConsumables);
            pending.Add(new(item.RecipeId, recipe.ItemResult.RowId, recipe.AmountResult,
                context.QualityPolicy.IngredientDemands, item.Options.Skipping, !item.IsOriginalRecipe));
        }
        return pending;
    }

    private Dictionary<uint, IngredientQualityDemand> BuildRemainingRequirements()
        => CraftReadiness.ExternalRequirements(CapturePendingCrafts());

    internal static unsafe bool TryCaptureCraftInventory(IEnumerable<uint> items, out Dictionary<uint, AcquisitionInventory> inventory)
    {
        inventory = new();
        if (GetPlayerInventoryAvailability() != PlayerInventoryAvailability.Ready) return false;
        var manager = InventoryManager.Instance();
        if (manager == null) return false;
        inventory = items.Distinct().ToDictionary(id => id, id => new AcquisitionInventory(
            checked((int)manager->GetInventoryItemCount(id, false, false, false)),
            checked((int)manager->GetInventoryItemCount(id, true, false, false))));
        return true;
    }

    private bool SelectReadyCraft()
    {
        if (DeferForInventory(QueueState.WaitingForJobSwitch)) return false;
        if (_currentQueueIndex >= QueueItems.Count) return true;
        try
        {
            // Explicit user exclusions are not missing-material skips.
            while (_currentQueueIndex < QueueItems.Count && QueueItems[_currentQueueIndex].Options.Skipping)
                _currentQueueIndex++;
            if (_currentQueueIndex >= QueueItems.Count) return true;
            var pending = CapturePendingCrafts();
            if (!TryCaptureCraftInventory(pending.SelectMany(c => c.Ingredients.Keys), out var inventory))
            {
                WaitForInventory(QueueState.WaitingForJobSwitch);
                return false;
            }
            var selected = CraftReadiness.Select(pending, inventory, _missingIngredientFailures.Keys.ToHashSet());
            if (selected < 0)
            {
                AcquisitionShortages = CraftReadiness.ExternalRequirements(pending).Select(p =>
                {
                    var owned = inventory.GetValueOrDefault(p.Key);
                    var missing = p.Value.ConsumeSplit(owned.NQ, owned.HQ, out _, out _);
                    var sheet = Dalamud.GameData.GetExcelSheet<Item>();
                    var name = sheet != null && sheet.TryGetRow(p.Key, out var row) ? row.Name.ExtractText() : $"Item {p.Key}";
                    return (missing, text: $"{missing.Total} × {name} (required HQ={missing.RequiredHQ}, required NQ={missing.RequiredNQ})");
                }).Where(p => p.missing.Total > 0).Select(p => p.text).ToArray();
                BlockAcquisition($"No ready recipes. {QueueItems.Count - _currentQueueIndex} queue entries retained. "
                    + (_missingIngredientFailures.Count > 0 ? "Ingredient assignment failed; recheck after correcting materials/settings. " : string.Empty)
                    + "Recheck materials or Retry acquisition; no automatic vendor retry.");
                return false;
            }
            if (selected > 0)
            {
                var next = QueueItems[_currentQueueIndex + selected];
                QueueItems.RemoveAt(_currentQueueIndex + selected);
                QueueItems.Insert(_currentQueueIndex, next);
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Deferred {selected} blocked queue entries; selected ready recipe {next.RecipeId}");
            }
            _readinessSelected = true;
            AcquisitionStatus = $"Crafting ready work; {QueueItems.Count - _currentQueueIndex} entries remain.";
            return true;
        }
        catch (Exception ex)
        {
            BlockAcquisition($"Readiness check failed: {ex.Message}", QueueState.AcquisitionFailed);
            return false;
        }
    }

    private bool RevalidateCurrentCraft(CraftingListItem item, Recipe recipe)
    {
        if (DeferForInventory(QueueState.WaitingForJobSwitch)) return false;
        try
        {
            if (Dalamud.Objects.LocalPlayer?.ClassJob.RowId != recipe.CraftType.RowId + 8)
            {
                _currentState = QueueState.WaitingForJobSwitch;
                _readinessSelected = false;
                StateChanged?.Invoke(_currentState);
                return false;
            }
            var policy = CraftingContextResolver.ResolveExecutionContext(item, recipe, _listConsumables).QualityPolicy;
            if (!TryCaptureCraftInventory(policy.IngredientDemands.Keys, out var inventory))
            {
                WaitForInventory(QueueState.WaitingForJobSwitch);
                return false;
            }
            if (CraftReadiness.Missing(new(item.RecipeId, recipe.ItemResult.RowId, recipe.AmountResult,
                    policy.IngredientDemands), inventory).Count == 0) return true;
            _readinessSelected = false;
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
        catch (Exception ex) { BlockAcquisition($"Inventory revalidation failed: {ex.Message}", QueueState.AcquisitionFailed); }
        return false;
    }

    private bool BeginResultTracking(Recipe recipe, int requested)
    {
        if (!TryCaptureCraftInventory(new[] { recipe.ItemResult.RowId }, out var inventory)) return false;
        _resultItemId = recipe.ItemResult.RowId;
        _resultOutputQuantity = recipe.AmountResult;
        var owned = inventory[_resultItemId];
        _resultBefore = checked(owned.NQ + owned.HQ);
        _resultRequested = requested;
        _resultReported = 0;
        _resultCancelled = false;
        return true;
    }

    private void UpdateCraftResult()
    {
        int confirmed;
        int observedOutputCount;
        try
        {
            if (!TryCaptureCraftInventory(new[] { _resultItemId }, out var inventory))
            {
                WaitForInventory(QueueState.WaitingForCraftResult);
                return;
            }
            var owned = inventory[_resultItemId];
            observedOutputCount = checked(owned.NQ + owned.HQ);
            confirmed = CraftReadiness.ConfirmedCrafts(_resultBefore, observedOutputCount,
                _resultOutputQuantity, _resultRequested);
        }
        catch (Exception ex)
        {
            BlockAcquisition($"Craft output reconciliation failed: {ex.Message}", QueueState.AcquisitionFailed);
            return;
        }
        var attempted = _lastCraftWasQuickSynth && _resultReported > 0
            ? Math.Min(_resultRequested, _resultReported) : _resultRequested;
        if (confirmed < attempted && _runClock.Elapsed < _resultDeadline) return;
        var recipeId = CurrentRecipeItem?.RecipeId ?? 0;
        var previousIndex = _currentQueueIndex;
        _currentQueueIndex += confirmed;
        var progress = Progress;
        GatherBuddy.Log.Debug($"[Vulcan Crafting][{_executionPlan?.AcquisitionRunId}][Result] recipe={recipeId}; outputItem={_resultItemId}; before={_resultBefore}; after={observedOutputCount}; yield={_resultOutputQuantity}; requested={_resultRequested}; attempted={attempted}; confirmed={confirmed}; cancelled={_resultCancelled}; index={previousIndex}->{_currentQueueIndex}; confirmedTotal={progress.ConfirmedCrafts}; explicitlySkipped={progress.ExplicitSkips}; remaining={progress.RemainingCrafts}");
        _readinessSelected = false;
        _jobSwitchGuard.Reset();
        if (confirmed > 0) _missingIngredientFailures.Clear();
        if (confirmed < attempted || _resultCancelled)
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
            Pause($"Confirmed {confirmed}/{attempted} attempted crafts from inventory results. Unconfirmed work retained; inspect failed/cancelled synthesis before resuming.");
            return;
        }
        if (_currentQueueIndex >= QueueItems.Count) CompleteQueue();
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }
}
