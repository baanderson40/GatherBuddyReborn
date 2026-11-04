using System.Collections;
using System.Text.Json;
using GatherBuddy.Marketboard;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class MarketboardHistoryCompatibilityTests
{
    [Fact]
    public void ExistingHistoryFileEntriesDeserializeWithUnchangedNamesAndIds()
    {
        // Exercise the actual private persistence DTO without constructing the host-dependent service.
        var entryType = typeof(MarketItemData).Assembly.GetType("GatherBuddy.Marketboard.MarketboardService+PersistedEntry", throwOnError: true)!;
        var entries = (IList)JsonSerializer.Deserialize("""[{"ItemId":5057,"Name":"Electrum Ingot","IconId":10}]""",
            typeof(List<>).MakeGenericType(entryType))!;
        var entry = Assert.Single(entries.Cast<object>());
        Assert.Equal(5057u, entryType.GetProperty("ItemId")!.GetValue(entry));
        Assert.Equal("Electrum Ingot", entryType.GetProperty("Name")!.GetValue(entry));
        Assert.Equal(10u, entryType.GetProperty("IconId")!.GetValue(entry));
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(entries, entries.GetType()));
        Assert.Equal(5057u, saved.RootElement[0].GetProperty("ItemId").GetUInt32());
        Assert.Equal("Electrum Ingot", saved.RootElement[0].GetProperty("Name").GetString());
    }
}
