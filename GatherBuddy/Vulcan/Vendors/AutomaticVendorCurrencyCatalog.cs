using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Vendors;

public sealed record AutomaticVendorCurrency(uint ItemId, VendorCurrencyGroup Group, string FallbackName);

/// <summary>Currency item IDs explicitly supported by the automatic purchase policy UI.</summary>
public static class AutomaticVendorCurrencyCatalog
{
    public const uint GilCurrencyItemId = 1;

    public static IReadOnlyList<AutomaticVendorCurrency> Currencies { get; } = new[]
    {
        new AutomaticVendorCurrency(28, VendorCurrencyGroup.Tomestones, "Allagan Tomestone of Poetics"),
        new AutomaticVendorCurrency(48, VendorCurrencyGroup.Tomestones, "Allagan Tomestone of Mathematics"),
        new AutomaticVendorCurrency(49, VendorCurrencyGroup.Tomestones, "Allagan Tomestone of Mnemonics"),
        new AutomaticVendorCurrency(33913, VendorCurrencyGroup.Scrips, "Purple Crafter's Scrip"),
        new AutomaticVendorCurrency(33914, VendorCurrencyGroup.Scrips, "Purple Gatherer's Scrip"),
        new AutomaticVendorCurrency(41784, VendorCurrencyGroup.Scrips, "Orange Crafter's Scrip"),
        new AutomaticVendorCurrency(41785, VendorCurrencyGroup.Scrips, "Orange Gatherer's Scrip"),
    };

    private static readonly Dictionary<uint, AutomaticVendorCurrency> ByItemId = Currencies.ToDictionary(currency => currency.ItemId);

    public static bool TryGet(uint itemId, out AutomaticVendorCurrency currency)
        => ByItemId.TryGetValue(itemId, out currency!);

    public static bool Supports(VendorCurrencyGroup group, uint itemId)
        => ByItemId.TryGetValue(itemId, out var currency) && currency.Group == group;
}
