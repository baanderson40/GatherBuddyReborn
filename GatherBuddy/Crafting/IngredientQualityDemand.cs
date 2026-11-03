using System;

namespace GatherBuddy.Crafting;

public readonly record struct IngredientQualityDemand(int RequiredHQ, int RequiredNQ, int PreferHQ, int PreferNQ)
{
    public int Total
        => RequiredHQ + RequiredNQ + PreferHQ + PreferNQ;

    public int GuaranteedHQ
        => RequiredHQ;

    public bool HasHardRequirements
        => RequiredHQ > 0 || RequiredNQ > 0;

    public static IngredientQualityDemand FromPreferHQ(int total)
        => new(0, 0, Math.Max(0, total), 0);

    public static IngredientQualityDemand FromPreferNQ(int total)
        => new(0, 0, 0, Math.Max(0, total));

    public static IngredientQualityDemand FromRequireNQOnly(int total)
        => new(0, Math.Max(0, total), 0, 0);

    public static IngredientQualityDemand FromRequiredHQ(int requiredHQ, int total)
    {
        requiredHQ = Math.Clamp(requiredHQ, 0, Math.Max(0, total));
        return new(requiredHQ, 0, 0, Math.Max(0, total - requiredHQ));
    }

    public IngredientQualityDemand Scale(int factor)
        => factor <= 1
            ? this
            : new(RequiredHQ * factor, RequiredNQ * factor, PreferHQ * factor, PreferNQ * factor);

    public IngredientQualityDemand Add(IngredientQualityDemand other)
        => new(
            RequiredHQ + other.RequiredHQ,
            RequiredNQ + other.RequiredNQ,
            PreferHQ + other.PreferHQ,
            PreferNQ + other.PreferNQ);

    public IngredientQualityDemand ConsumeUnknownQuality(int available, out int consumed)
    {
        consumed = 0;
        if (available <= 0 || Total <= 0)
            return this;

        var remainingPreferNQ = PreferNQ;
        var remainingPreferHQ = PreferHQ;

        var takePreferNQ = Math.Min(available, remainingPreferNQ);
        remainingPreferNQ -= takePreferNQ;
        available -= takePreferNQ;
        consumed += takePreferNQ;

        var takePreferHQ = Math.Min(available, remainingPreferHQ);
        remainingPreferHQ -= takePreferHQ;
        consumed += takePreferHQ;

        return new(RequiredHQ, RequiredNQ, remainingPreferHQ, remainingPreferNQ);
    }

    public IngredientQualityDemand ConsumeSplit(int availableNQ, int availableHQ, out int consumedNQ, out int consumedHQ)
    {
        consumedNQ = 0;
        consumedHQ = 0;

        var remainingRequiredHQ = RequiredHQ;
        var remainingRequiredNQ = RequiredNQ;
        var remainingPreferHQ = PreferHQ;
        var remainingPreferNQ = PreferNQ;

        var takeHQ = Math.Min(remainingRequiredHQ, availableHQ);
        remainingRequiredHQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        var takeNQ = Math.Min(remainingRequiredNQ, availableNQ);
        remainingRequiredNQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        takeNQ = Math.Min(remainingPreferNQ, availableNQ);
        remainingPreferNQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        takeHQ = Math.Min(remainingPreferNQ, availableHQ);
        remainingPreferNQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        takeHQ = Math.Min(remainingPreferHQ, availableHQ);
        remainingPreferHQ -= takeHQ;
        availableHQ -= takeHQ;
        consumedHQ += takeHQ;

        takeNQ = Math.Min(remainingPreferHQ, availableNQ);
        remainingPreferHQ -= takeNQ;
        availableNQ -= takeNQ;
        consumedNQ += takeNQ;

        return new(remainingRequiredHQ, remainingRequiredNQ, remainingPreferHQ, remainingPreferNQ);
    }
}
