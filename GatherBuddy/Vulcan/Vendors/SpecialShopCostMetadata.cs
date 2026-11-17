namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Pure validation for SpecialShop payment metadata.</summary>
public static class SpecialShopCostMetadata
{
    /// <summary>
    /// SpecialShop cost references use CostType 3 for SpecialBucketId; values 0-2 link to Item.
    /// Bucket references are accepted here but must still resolve to an explicitly supported currency.
    /// A nonzero collectability rating means payment is a collectable, not an ordinary currency exchange.
    /// </summary>
    public static string? Validate(int costType, int collectabilityCost)
    {
        if (costType is < 0 or > 3)
            return $"Unsupported cost reference type {costType}; expected an item reference (0-2) or SpecialBucketId (3).";
        if (collectabilityCost != 0)
            return $"Unsupported collectability payment (rating={collectabilityCost}).";
        return null;
    }
}
