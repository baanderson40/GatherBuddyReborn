using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Marketboard;

public sealed class MarketListing
{
    public int    PricePerUnit { get; init; }
    public int    Quantity     { get; init; }
    public bool   IsHq         { get; init; }
    public string WorldName    { get; init; } = string.Empty;
    public string DataCenter   { get; init; } = string.Empty;
    public long StackTotal => (long)PricePerUnit * Quantity;
}

public sealed class MarketItemData
{
    public uint   ItemId                { get; init; }
    public string ItemName              { get; set;  } = string.Empty;
    public uint   IconId                { get; set;  }
    public float  MinPrice              { get; init; }
    public List<MarketListing> Listings { get; init; } = new();
    public DateTimeOffset? SourceUpdatedAt { get; init; }
}

public enum MarketQuality { Any, Nq, Hq }
public sealed class MarketboardPreferences
{
    private string _scopeId = string.Empty;
    private MarketQuality _quality;
    public string ScopeId { get => _scopeId; set => _scopeId = value ?? string.Empty; }
    public MarketQuality Quality { get => _quality; set => _quality = Enum.IsDefined(value) ? value : MarketQuality.Any; }
}

public sealed record MarketCatalogItem(uint ItemId, string Name, uint IconId, bool CanBeHq);
public sealed record MarketWorld(uint Id, string Name);
public sealed record MarketHome(uint WorldId, string DataCenter, IReadOnlyList<MarketWorld> Worlds);
public sealed record MarketTarget(string ApiScope, string Name, string DataCenter, bool IsWorld = false);
public sealed record MarketScope(string Id, string Label, IReadOnlyList<MarketTarget> Targets)
{
    public const string CrossDcId = "cross-dc:NA";
    public const string CrossWorldId = "cross-world";
    public static IReadOnlyList<string> NorthAmericanDataCenters { get; } = Array.AsReadOnly(new[] { "Aether", "Primal", "Crystal", "Dynamis" });

    public static IReadOnlyList<MarketScope> Options(MarketHome home)
        => new[]
        {
            new MarketScope(CrossDcId, "Cross-DC", NorthAmericanDataCenters.Select(dc => new MarketTarget(dc, dc, dc)).ToArray()),
            new MarketScope(CrossWorldId, "Cross-World", new[] { new MarketTarget(home.DataCenter, home.DataCenter, home.DataCenter) }),
        }.Concat(home.Worlds.OrderBy(w => w.Name, StringComparer.Ordinal)
            .Select(w => new MarketScope($"world:{w.Id}", w.Name,
                new[] { new MarketTarget(w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), w.Name, home.DataCenter, true) }))).ToArray();

    public static MarketScope? Resolve(string? savedId, MarketHome? home)
    {
        if (home == null) return null;
        var options = Options(home);
        return options.FirstOrDefault(s => s.Id == savedId)
            ?? options.FirstOrDefault(s => s.Id == $"world:{home.WorldId}");
    }
}

public static class MarketSearch
{
    public static bool IsMarketable(bool untradable, uint searchCategory) => !untradable && searchCategory != 0;

    public static IReadOnlyList<MarketCatalogItem> Find(IEnumerable<MarketCatalogItem> items, string text)
    {
        var query = text.Trim();
        if (query.Length == 0) return Array.Empty<MarketCatalogItem>();
        return items.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                : item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ItemId).ToArray();
    }

    public static IReadOnlyList<MarketListing> Listings(IEnumerable<MarketListing> items, MarketQuality quality)
        => items.Where(l => l.PricePerUnit > 0 && l.Quantity > 0
                && (quality == MarketQuality.Any || l.IsHq == (quality == MarketQuality.Hq)))
            .OrderBy(l => l.PricePerUnit).ThenBy(l => l.WorldName, StringComparer.Ordinal)
            .ThenBy(l => l.DataCenter, StringComparer.Ordinal).ThenBy(l => l.IsHq).ThenBy(l => l.Quantity).ToArray();
}
