using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class LiveVendorExchangeTests
{
    private const uint Item = 44071;
    private const uint Gems = 26807;
    private static VendorPurchaseAllocation Allocation(uint output = 1, IReadOnlyList<AcquisitionExchangeCost>? costs = null)
        => new(Guid.NewGuid(), Item, 2, output, Gems, VendorCurrencyGroup.BicolorGemstones, 3,
            VendorShopType.SpecialCurrency, new(1048383, "Kajeel Ja", 1770736, VendorMenuShopType.SpecialShop,
                ShopItemIndex: 13, SourceShopId: 1770736), costs);

    private static (uint?[] Values, (uint ItemId, uint Quantity)[] Outputs) Fixture()
    {
        var values = new uint?[BasicCurrencyExchangeReader.CallbackBase + 27];
        values[BasicCurrencyExchangeReader.CountSlot] = 27;
        values[BasicCurrencyExchangeReader.CurrencyIconSlot] = 65071;
        var outputs = Enumerable.Range(0, 27).Select(i => (ItemId: (uint)(1000 + i), Quantity: 1u)).ToArray();
        outputs[26] = (Item, 1);
        for (var i = 0; i < 27; i++)
        {
            var callback = i == 13 ? 26 : i < 13 ? i : i - 1;
            values[BasicCurrencyExchangeReader.ItemBase + i] = outputs[callback].ItemId;
            values[BasicCurrencyExchangeReader.PriceBase + i] = 3;
            values[BasicCurrencyExchangeReader.CallbackBase + i] = (uint)callback;
        }
        return (values, outputs);
    }

    private static (LiveExchangeStatus Status, IReadOnlyList<LiveExchangeRow> Rows) Read(uint?[] values,
        (uint ItemId, uint Quantity)[] outputs, uint currency = Gems)
    {
        var status = BasicCurrencyExchangeReader.Read(values, outputs, currency, VendorCurrencyGroup.BicolorGemstones, out var rows, out _);
        return (status, rows);
    }

    [Fact]
    public void SourceBackedBasicCurrencyContractResolvesReorderedRow26AndLivePrice()
    {
        var (values, outputs) = Fixture();
        var snapshot = Read(values, outputs);
        Assert.Equal(LiveExchangeStatus.Ready, snapshot.Status);
        var match = LiveVendorExchange.Match(Allocation(), snapshot.Rows);
        Assert.Equal(LiveExchangeStatus.Match, match.Status);
        Assert.Equal(26, match.CallbackIndex);
        Assert.Equal(3ul, snapshot.Rows.Single(r => r.ItemId == Item).Costs.Single().Amount);
        // No AgentShop cost-array mapping is an input to this reader.
        Assert.Equal(27, snapshot.Rows.Count);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    public void LivePriceMismatchNeverFallsBackToAuthorizedSheetPrice(uint price)
    {
        var (values, outputs) = Fixture();
        values[BasicCurrencyExchangeReader.PriceBase + 13] = price;
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows).Status);
    }

    [Fact]
    public void IconCurrencyResolutionRequiresOneNonzeroIdentity()
    {
        Assert.Equal(Gems, LiveVendorExchange.UniqueCurrencyId([Gems]));
        Assert.Null(LiveVendorExchange.UniqueCurrencyId([]));
        Assert.Null(LiveVendorExchange.UniqueCurrencyId([0]));
        Assert.Null(LiveVendorExchange.UniqueCurrencyId([Gems, 29]));
    }

    [Fact]
    public void DifferentLiveCurrencyRejectsEvenWhenNumericPriceMatches()
    {
        var (values, outputs) = Fixture();
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(Allocation(), Read(values, outputs, 29).Rows).Status);
    }

    [Fact]
    public void CallbackMustAgreeWithAgentOutputRatherThanItemOnlyRemapping()
    {
        var (values, outputs) = Fixture();
        values[BasicCurrencyExchangeReader.CallbackBase + 13] = 0;
        Assert.Equal(LiveExchangeStatus.Unsupported, Read(values, outputs).Status);
    }

    [Theory]
    [InlineData(27u)]
    [InlineData(uint.MaxValue)]
    public void InvalidCallbackIndicesAreRejectedWithoutArrayAccess(uint callback)
    {
        var (values, outputs) = Fixture();
        values[BasicCurrencyExchangeReader.CallbackBase + 13] = callback;
        Assert.Equal(LiveExchangeStatus.Unsupported, Read(values, outputs).Status);
    }

    [Fact]
    public void TruncatedAndOversizedLayoutsFailClosed()
    {
        var (values, outputs) = Fixture();
        Assert.Equal(LiveExchangeStatus.Unsupported, Read(values[..^1], outputs).Status);
        values[BasicCurrencyExchangeReader.CountSlot] = BasicCurrencyExchangeReader.MaximumRows + 1;
        Assert.Equal(LiveExchangeStatus.Unsupported, Read(values, outputs).Status);
        Assert.Equal(LiveExchangeStatus.Incomplete, Read(Array.Empty<uint?>(), outputs).Status);
    }

    [Theory]
    [InlineData(BasicCurrencyExchangeReader.ItemBase)]
    [InlineData(BasicCurrencyExchangeReader.PriceBase)]
    [InlineData(BasicCurrencyExchangeReader.CallbackBase)]
    public void MissingTypedValuesAreIncompleteRatherThanInventedZeroes(int start)
    {
        var (values, outputs) = Fixture();
        values[start + 13] = null;
        Assert.Equal(LiveExchangeStatus.Incomplete, Read(values, outputs).Status);
    }

    [Fact]
    public void ZeroPriceIsIncompleteNotAFreePurchase()
    {
        var (values, outputs) = Fixture();
        values[BasicCurrencyExchangeReader.PriceBase + 13] = 0;
        Assert.Equal(LiveExchangeStatus.Incomplete, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows).Status);
    }

    [Fact]
    public void DuplicateMatchingRowsAreAmbiguous()
    {
        var (values, outputs) = Fixture();
        outputs[0] = (Item, 1);
        values[BasicCurrencyExchangeReader.ItemBase] = Item;
        Assert.Equal(LiveExchangeStatus.Ambiguous, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows).Status);
    }

    [Fact]
    public void MatchingCostsDisambiguateDifferentPriceRows()
    {
        var (values, outputs) = Fixture();
        outputs[0] = (Item, 1);
        values[BasicCurrencyExchangeReader.ItemBase] = Item;
        values[BasicCurrencyExchangeReader.PriceBase] = 7;
        Assert.Equal(26, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows).CallbackIndex);
    }

    [Fact]
    public void IncompleteDuplicateCannotHideAnAmbiguousTransaction()
    {
        var (values, outputs) = Fixture();
        outputs[0] = (Item, 1);
        values[BasicCurrencyExchangeReader.ItemBase] = Item;
        values[BasicCurrencyExchangeReader.PriceBase] = 0;
        Assert.Equal(LiveExchangeStatus.Incomplete, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows).Status);
    }

    [Theory]
    [InlineData(26, LiveExchangeStatus.Match)]
    [InlineData(0, LiveExchangeStatus.Mismatch)]
    [InlineData(-1, LiveExchangeStatus.Incomplete)]
    public void ConfirmationRetainsSelectedRowValidation(int selected, LiveExchangeStatus expected)
    {
        var (values, outputs) = Fixture();
        Assert.Equal(expected, LiveVendorExchange.Match(Allocation(), Read(values, outputs).Rows, selected).Status);
    }

    [Fact]
    public void PureMatcherValidatesAllComponentsAndAggregatesRepeatedCurrencySlots()
    {
        AcquisitionExchangeCost[] costs = [new(Gems, VendorCurrencyGroup.BicolorGemstones, 3), new(1, VendorCurrencyGroup.Gil, 5)];
        var allocation = Allocation(output: 5, costs: costs);
        var row = new LiveExchangeRow(26, Item, 5, false,
            [new(Gems, VendorCurrencyGroup.BicolorGemstones, 1), new(Gems, VendorCurrencyGroup.BicolorGemstones, 2), new(1, VendorCurrencyGroup.Gil, 5)]);
        Assert.Equal(LiveExchangeStatus.Match, LiveVendorExchange.Match(allocation, [row]).Status);
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(allocation, [row with { Costs = costs[..1] }]).Status);
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(allocation,
            [row with { Costs = [.. costs, new(29, VendorCurrencyGroup.MGP, 1)] }]).Status);
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(allocation, [row with { OutputQuantity = 1 }]).Status);
        Assert.Equal(LiveExchangeStatus.Mismatch, LiveVendorExchange.Match(allocation, [row with { Hq = true }]).Status);
    }

    [Fact]
    public void ReadableSnapshotDoesNotAliasMutableAtkOrAgentBuffers()
    {
        var (values, outputs) = Fixture();
        var snapshot = Read(values, outputs);
        values[BasicCurrencyExchangeReader.PriceBase + 13] = 99;
        outputs[26] = (0, 0);
        Assert.Equal(LiveExchangeStatus.Match, LiveVendorExchange.Match(Allocation(), snapshot.Rows).Status);
    }
}
