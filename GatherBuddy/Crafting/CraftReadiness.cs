using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public sealed record PendingCraft(uint RecipeId, uint OutputItemId, int OutputQuantity,
    IReadOnlyDictionary<uint, IngredientQualityDemand> Ingredients, bool Skipped = false, bool IsPrecraft = false);

/// <summary>Plans only existing queue entries; never assumes planned output is already owned.</summary>
public static class CraftReadiness
{
    public static Dictionary<uint, IngredientQualityDemand> Missing(PendingCraft craft,
        IReadOnlyDictionary<uint, AcquisitionInventory> inventory)
        => craft.Ingredients.Select(p =>
        {
            var owned = inventory.GetValueOrDefault(p.Key);
            return (p.Key, Demand: p.Value.ConsumeSplit(owned.NQ, owned.HQ, out _, out _));
        }).Where(p => p.Demand.Total > 0).ToDictionary(p => p.Key, p => p.Demand);

    public static int Select(IReadOnlyList<PendingCraft> remaining, IReadOnlyDictionary<uint, AcquisitionInventory> inventory,
        ISet<uint>? preparationFailures = null)
    {
        var pendingOutputs = new HashSet<uint>();
        for (var i = 0; i < remaining.Count; i++)
        {
            var craft = remaining[i];
            if (craft.Skipped) continue;
            if (preparationFailures?.Contains(craft.RecipeId) != true
                && !craft.Ingredients.Keys.Any(pendingOutputs.Contains) && Missing(craft, inventory).Count == 0)
                return i;
            if (craft.IsPrecraft) pendingOutputs.Add(craft.OutputItemId);
        }
        return -1;
    }

    // Gross external requirements for the retained suffix. Planned precrafts cover
    // flexible demand only: never promise an HQ/NQ result before it actually exists.
    public static Dictionary<uint, IngredientQualityDemand> ExternalRequirements(IReadOnlyList<PendingCraft> remaining)
    {
        var outputs = new Dictionary<uint, int>();
        var demand = new Dictionary<uint, IngredientQualityDemand>();
        foreach (var craft in remaining.Where(c => !c.Skipped))
        {
            foreach (var (id, amount) in craft.Ingredients)
            {
                var residual = amount.ConsumeUnknownQuality(outputs.GetValueOrDefault(id), out var consumed);
                outputs[id] = outputs.GetValueOrDefault(id) - consumed;
                demand[id] = demand.GetValueOrDefault(id).Add(residual);
            }
            if (craft.IsPrecraft)
                outputs[craft.OutputItemId] = checked(outputs.GetValueOrDefault(craft.OutputItemId) + craft.OutputQuantity);
        }
        return demand.Where(p => p.Value.Total > 0).ToDictionary();
    }

    public static int ConfirmedCrafts(int before, int after, int outputPerCraft, int requested)
        => outputPerCraft <= 0 ? 0 : Math.Clamp((after - before) / outputPerCraft, 0, requested);

    public static int AttemptedCrafts(bool quickSynthesis, int requested, int reported, bool intentionalStop)
        => quickSynthesis && (reported > 0 || intentionalStop) ? Math.Clamp(reported, 0, requested) : requested;

    public static int MaximumReadyBatch(IReadOnlyDictionary<uint, IngredientQualityDemand> ingredients,
        IReadOnlyDictionary<uint, AcquisitionInventory> inventory, int requested)
    {
        for (var count = Math.Clamp(requested, 0, 99); count > 0; count--)
        {
            if (ingredients.All(p =>
                {
                    var owned = inventory.GetValueOrDefault(p.Key);
                    return p.Value.Scale(count).ConsumeSplit(owned.NQ, owned.HQ, out _, out _).Total == 0;
                })) return count;
        }
        return 0;
    }
}

/// <summary>Submission is not confirmation. Uses caller's monotonic elapsed clock.</summary>
public sealed class JobSwitchGuard
{
    private uint _job;
    private int _attempts;
    private TimeSpan _last;
    public bool TimedOut { get; private set; }
    public void Reset() { _job = 0; _attempts = 0; TimedOut = false; }
    public void MarkSubmitted(TimeSpan now) { _last = now; _attempts++; }
    public bool ShouldSubmit(uint job, TimeSpan now)
    {
        if (_job != job) { Reset(); _job = job; }
        if (_attempts > 0 && now - _last < TimeSpan.FromSeconds(2)) return false;
        if (_attempts >= 3) { TimedOut = true; return false; }
        return true;
    }
}
