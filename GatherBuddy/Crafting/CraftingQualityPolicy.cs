using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum CraftingQualityOverrideMode
{
    None,
    PreferNQWithHQFallback,
    RequireNQOnly,
}

public enum PlannedOutputQuality
{
    Unknown,
    NQ,
    HQ,
}

public sealed class CraftingQualityPolicy
{
    private readonly Dictionary<uint, IngredientQualityDemand> _ingredientDemands;

    public uint RecipeId { get; }
    public CraftingQualityOverrideMode OverrideMode { get; }
    public bool HasExplicitHQRequirements { get; }
    public bool AllowHQMaterialsInQuickSynthesis => OverrideMode != CraftingQualityOverrideMode.RequireNQOnly;
    public IReadOnlyDictionary<uint, IngredientQualityDemand> IngredientDemands => _ingredientDemands;

    public CraftingQualityPolicy(
        uint recipeId,
        CraftingQualityOverrideMode overrideMode,
        bool hasExplicitHQRequirements,
        Dictionary<uint, IngredientQualityDemand> ingredientDemands)
    {
        RecipeId = recipeId;
        OverrideMode = overrideMode;
        HasExplicitHQRequirements = hasExplicitHQRequirements;
        _ingredientDemands = ingredientDemands;
    }

    public IngredientQualityDemand GetDemand(uint itemId)
        => _ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;

    public Dictionary<uint, int> BuildGuaranteedHQPreferences()
    {
        var guaranteed = new Dictionary<uint, int>();
        foreach (var (itemId, demand) in _ingredientDemands)
        {
            if (demand.GuaranteedHQ > 0)
                guaranteed[itemId] = demand.GuaranteedHQ;
        }

        return guaranteed;
    }

    public int CalculateGuaranteedInitialQuality(Recipe recipe)
        => QualityCalculator.CalculateInitialQuality(recipe, BuildGuaranteedHQPreferences());

    public bool TryResolveIngredientSelection(
        uint itemId,
        int availableNQ,
        int availableHQ,
        out int assignedNQ,
        out int assignedHQ,
        out string details)
    {
        var demand = GetDemand(itemId);
        var remaining = demand.ConsumeSplit(availableNQ, availableHQ, out assignedNQ, out assignedHQ);
        if (remaining.Total == 0)
        {
            details = string.Empty;
            return true;
        }

        details = $"need HQ={demand.RequiredHQ}, NQ={demand.RequiredNQ}, prefer HQ={demand.PreferHQ}, prefer NQ={demand.PreferNQ}; available NQ={availableNQ}, HQ={availableHQ}";
        return false;
    }

    public bool UsesHQFallbackForNQPreference(uint itemId, int assignedHQ)
    {
        var demand = GetDemand(itemId);
        return demand.PreferNQ > 0 && assignedHQ > demand.RequiredHQ;
    }

    public bool UsesNQFallbackForHQPreference(uint itemId, int assignedNQ)
    {
        var demand = GetDemand(itemId);
        return demand.PreferHQ > 0 && assignedNQ > demand.RequiredNQ;
    }
}

public static class CraftingQualityPolicyResolver
{
    public static PlannedOutputQuality ResolvePlannedOutputQuality(
        Recipe recipe,
        CraftingQualityPolicy qualityPolicy,
        IngredientQualityDemand demand)
    {
        if (demand.RequiredHQ > 0 && demand.RequiredNQ == 0)
            return PlannedOutputQuality.HQ;

        if (demand.RequiredNQ > 0
            && demand.RequiredHQ == 0
            && recipe.CanQuickSynth
            && qualityPolicy.OverrideMode == CraftingQualityOverrideMode.RequireNQOnly)
            return PlannedOutputQuality.NQ;

        return PlannedOutputQuality.Unknown;
    }

    public static bool HasExplicitQualitySettings(RecipeCraftSettings? settings)
        => settings?.UseAllNQ == true || (settings?.IngredientPreferences.Count ?? 0) > 0;

    public static Dictionary<uint, int> BuildAllHQPreferences(Recipe recipe)
    {
        var preferences = new Dictionary<uint, int>();
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null)
            return preferences;

        foreach (var (itemId, amount) in RecipeManager.GetIngredients(recipe))
        {
            if (itemSheet.TryGetRow(itemId, out var item) && item.CanBeHq)
                preferences[itemId] = amount;
        }

        return preferences;
    }

    public static bool MatchesAllHQPreferences(Recipe recipe, IReadOnlyDictionary<uint, int> preferences)
    {
        var expected = BuildAllHQPreferences(recipe);
        if (preferences.Count != expected.Count)
            return false;

        foreach (var (itemId, amount) in expected)
        {
            if (!preferences.TryGetValue(itemId, out var preferredAmount) || preferredAmount != amount)
                return false;
        }

        return true;
    }

    public static RecipeCraftSettings? BuildEffectiveSettings(
        Recipe recipe,
        RecipeCraftSettings? sourceSettings,
        bool useAllHQByDefault,
        bool forcePreferNQ = false)
    {
        var settings = sourceSettings?.Clone();
        if (forcePreferNQ)
        {
            settings ??= new RecipeCraftSettings();
            settings.UseAllNQ = true;
            settings.IngredientPreferences.Clear();
            return settings;
        }

        if (!useAllHQByDefault || HasExplicitQualitySettings(sourceSettings))
            return settings;

        var allHQPreferences = BuildAllHQPreferences(recipe);
        if (allHQPreferences.Count == 0 && settings == null)
            return null;

        settings ??= new RecipeCraftSettings();
        settings.UseAllNQ = false;
        settings.IngredientPreferences = allHQPreferences;
        return settings;
    }
    public static CraftingQualityPolicy Resolve(
        Recipe recipe,
        RecipeCraftSettings? settings,
        CraftingQualityOverrideMode overrideMode = CraftingQualityOverrideMode.None)
    {
        var explicitPreferences = settings?.IngredientPreferences ?? new Dictionary<uint, int>();
        var hasExplicitHQRequirements = explicitPreferences.Values.Any(preferredHQ => preferredHQ > 0);
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var ingredientDemands = new Dictionary<uint, IngredientQualityDemand>();

        foreach (var (itemId, amount) in RecipeManager.GetIngredients(recipe))
        {
            var canBeHQ = itemSheet != null
                && itemSheet.TryGetRow(itemId, out var item)
                && item.CanBeHq;

            IngredientQualityDemand demand;
            if (!canBeHQ)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else if (overrideMode == CraftingQualityOverrideMode.RequireNQOnly)
            {
                demand = IngredientQualityDemand.FromRequireNQOnly(amount);
            }
            else if (overrideMode == CraftingQualityOverrideMode.PreferNQWithHQFallback)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else if (hasExplicitHQRequirements)
            {
                var requiredHQ = explicitPreferences.TryGetValue(itemId, out var preferredHQ)
                    ? preferredHQ
                    : 0;
                demand = IngredientQualityDemand.FromRequiredHQ(requiredHQ, amount);
            }
            else if (settings?.UseAllNQ == true)
            {
                demand = IngredientQualityDemand.FromPreferNQ(amount);
            }
            else
            {
                demand = IngredientQualityDemand.FromPreferHQ(amount);
            }

            ingredientDemands[itemId] = demand;
        }

        return new CraftingQualityPolicy(
            recipe.RowId,
            overrideMode,
            ingredientDemands.Values.Any(demand => demand.RequiredHQ > 0),
            ingredientDemands);
    }
}
