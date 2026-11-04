using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GatherBuddy.Marketboard;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class MarketboardTests
{
    private static readonly MarketCatalogItem Item = new(43, "Electrum Ingot", 10, false);
    private static readonly MarketHome Home = new(63, "Aether", [new(63, "Gilgamesh"), new(40, "Jenova")]);
    private static readonly MarketScope World = MarketScope.Resolve(null, Home)!;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private sealed class Source : IMarketDataSource
    {
        public readonly ConcurrentQueue<(MarketTarget Target, TaskCompletionSource<MarketItemData> Reply)> Calls = new();
        public readonly SemaphoreSlim Started = new(0);
        public async Task<MarketItemData> FetchAsync(MarketCatalogItem item, MarketTarget target, CancellationToken ct)
        {
            var reply = new TaskCompletionSource<MarketItemData>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Enqueue((target, reply));
            Started.Release();
            return await reply.Task.WaitAsync(ct);
        }

        public async Task<(MarketTarget Target, TaskCompletionSource<MarketItemData> Reply)> Next()
        {
            Assert.True(await Started.WaitAsync(TestTimeout));
            Assert.True(Calls.TryDequeue(out var call));
            return call;
        }
    }

    private static MarketItemData Data(int price = 100, bool hq = false, string world = "Gilgamesh", string dc = "Aether")
        => new() { ItemId = 43, Listings = [new() { PricePerUnit = price, Quantity = 2, IsHq = hq, WorldName = world, DataCenter = dc }] };

    [Theory]
    [InlineData("world:63", MarketQuality.Any)]
    [InlineData("cross-world", MarketQuality.Nq)]
    [InlineData("cross-dc:NA", MarketQuality.Hq)]
    public void PreferencesRoundTripThroughPluginJsonSerializer(string scope, MarketQuality quality)
    {
        var settings = new MarketboardPreferences { ScopeId = scope, Quality = quality };
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<MarketboardPreferences>(Newtonsoft.Json.JsonConvert.SerializeObject(settings))!;
        Assert.Equal(scope, restored.ScopeId);
        Assert.Equal(quality, restored.Quality);
    }

    [Fact]
    public void LegacyMissingPreferencesDeferHomeDefaultAndInvalidQualityDoesNotBreakFilter()
    {
        var defaults = Newtonsoft.Json.JsonConvert.DeserializeObject<MarketboardPreferences>("{}")!;
        Assert.Equal(string.Empty, defaults.ScopeId);
        Assert.Equal(MarketQuality.Any, defaults.Quality);
        var invalid = Newtonsoft.Json.JsonConvert.DeserializeObject<MarketboardPreferences>("""{"ScopeId":null,"Quality":999}""")!;
        Assert.Equal(string.Empty, invalid.ScopeId);
        Assert.Equal(MarketQuality.Any, invalid.Quality);
    }

    [Theory]
    [InlineData(false, 1u, true)]
    [InlineData(true, 1u, false)]
    [InlineData(false, 0u, false)]
    [InlineData(true, 0u, false)]
    public void MarketabilityRequiresTradableItemAndMarketSearchCategory(bool untradable, uint category, bool expected)
        => Assert.Equal(expected, MarketSearch.IsMarketable(untradable, category));

    [Fact]
    public void SearchRanksExactThenPrefixThenContainsWithoutChangingCatalog()
    {
        MarketCatalogItem[] items = [new(1, "Enchanted Electrum", 0, true), new(2, "Electrum Ore", 0, true), new(3, "Electrum", 0, true), new(4, "Iron", 0, false)];
        Assert.Equal(new uint[] { 3, 2, 1 }, MarketSearch.Find(items, "  eLECTRUM  ").Select(i => i.ItemId));
        Assert.Equal(4, items.Length);
        Assert.Empty(MarketSearch.Find(items, "   "));
        Assert.Empty(MarketSearch.Find(items, "missing"));
    }

    [Fact]
    public void SearchUsesItemIdToBreakDuplicateNameTies()
        => Assert.Equal(new uint[] { 1, 2 }, MarketSearch.Find([new(2, "Ore", 0, false), new(1, "Ore", 0, false)], "ore").Select(i => i.ItemId));

    [Fact]
    public void DefaultScopeIsActualHomeWorldNotVisitedWorldOrAssumedDc()
    {
        Assert.Equal("world:63", World.Id);
        Assert.Equal("Gilgamesh", World.Label);
        Assert.Equal("63", Assert.Single(World.Targets).ApiScope);
        Assert.Null(MarketScope.Resolve("cross-dc:NA", null));
    }

    [Fact]
    public void DropdownContainsFixedNaAndHomeDcThenAlphabeticHomeDcWorlds()
    {
        var options = MarketScope.Options(Home);
        Assert.Equal(new[] { "Cross-DC", "Cross-World", "Gilgamesh", "Jenova" }, options.Select(o => o.Label));
        Assert.Equal(new[] { "Aether", "Primal", "Crystal", "Dynamis" }, options[0].Targets.Select(t => t.ApiScope));
        Assert.Equal("Aether", Assert.Single(options[1].Targets).ApiScope);
        Assert.All(options.Skip(2), scope => Assert.True(Assert.Single(scope.Targets).IsWorld));
    }

    [Theory]
    [InlineData("world:40", "world:40")]
    [InlineData("world:999", "world:63")]
    [InlineData("obsolete", "world:63")]
    [InlineData("cross-world", "cross-world")]
    [InlineData("cross-dc:NA", "cross-dc:NA")]
    public void StableScopeIdsResolveIndependentlyOfDropdownPosition(string saved, string expected)
        => Assert.Equal(expected, MarketScope.Resolve(saved, Home)!.Id);

    [Fact]
    public void CharacterChangeRebindsDcAndInvalidWorldFallsBackToNewHome()
    {
        var other = new MarketHome(100, "Primal", [new(100, "Test World")]);
        Assert.Equal("Primal", Assert.Single(MarketScope.Resolve("cross-world", other)!.Targets).ApiScope);
        Assert.Equal("world:100", MarketScope.Resolve("world:63", other)!.Id);
        Assert.Equal(4, MarketScope.Resolve("cross-dc:NA", other)!.Targets.Count);
    }

    [Fact]
    public void MergedListingsSortByPriceThenWorldAndFilterQuality()
    {
        var rows = new[] { Data(200).Listings[0], Data(100, true, "Jenova").Listings[0], Data(100).Listings[0], Data(0).Listings[0] };
        Assert.Equal(new[] { "Gilgamesh", "Jenova", "Gilgamesh" }, MarketSearch.Listings(rows, MarketQuality.Any).Select(l => l.WorldName));
        Assert.Single(MarketSearch.Listings(rows, MarketQuality.Hq));
        Assert.Equal(2, MarketSearch.Listings(rows, MarketQuality.Nq).Count);
    }

    [Fact]
    public void StackTotalCannotOverflowInt32()
        => Assert.Equal(4_611_686_014_132_420_609L, new MarketListing { PricePerUnit = int.MaxValue, Quantity = int.MaxValue }.StackTotal);

    [Fact]
    public async Task PendingRequestsAreDeduplicatedEvenOnRefresh()
    {
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        var call = await source.Next();
        cache.Request(Item, World, force: true);
        Assert.True(Assert.Single(cache.Snapshot(43, World)).Pending);
        call.Reply.SetResult(Data());
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Empty(source.Calls);
        Assert.False(Assert.Single(cache.Snapshot(43, World)).Pending);
    }

    [Fact]
    public async Task FreshCacheIsReusedButExpiredCacheRemainsVisibleDuringRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source, () => now);
        cache.Request(Item, World);
        (await source.Next()).Reply.SetResult(Data());
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        cache.Request(Item, World);
        Assert.Empty(source.Calls);
        now += TimeSpan.FromMinutes(16);
        cache.Request(Item, World);
        var call = await source.Next();
        var status = Assert.Single(cache.Snapshot(43, World));
        Assert.True(status.Pending);
        Assert.True(status.Stale);
        Assert.NotNull(status.Data);
        call.Reply.SetResult(Data(200));
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Equal(200, Assert.Single(Assert.Single(cache.Snapshot(43, World)).Data!.Listings).PricePerUnit);
    }

    [Fact]
    public async Task FailedRefreshKeepsPreviousDataAndTimestamp()
    {
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        (await source.Next()).Reply.SetResult(Data());
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        var before = Assert.Single(cache.Snapshot(43, World));
        cache.Request(Item, World, force: true);
        (await source.Next()).Reply.SetException(new IOException("offline"));
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        var after = Assert.Single(cache.Snapshot(43, World));
        Assert.Same(before.Data, after.Data);
        Assert.Equal(before.FetchedAt, after.FetchedAt);
        Assert.Equal("offline", after.Error);
        Assert.True(after.Stale);
    }

    [Fact]
    public async Task CrossDcShowsPartialResultsAndRetriesOnlyFailedDc()
    {
        var scope = MarketScope.Resolve(MarketScope.CrossDcId, Home)!;
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, scope);
        var calls = new List<(MarketTarget Target, TaskCompletionSource<MarketItemData> Reply)>();
        for (var i = 0; i < 4; i++) calls.Add(await source.Next());
        Assert.All(cache.Snapshot(43, scope), status => Assert.True(status.Pending));
        foreach (var call in calls)
        {
            if (call.Target.Name == "Primal") call.Reply.SetException(new IOException("failed DC"));
            else call.Reply.SetResult(Data(200, world: "Other", dc: call.Target.DataCenter));
        }
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        var snapshot = cache.Snapshot(43, scope);
        Assert.Equal(3, snapshot.Count(s => s.Error == null));
        Assert.Single(snapshot, s => s.Error != null);
        cache.Request(Item, scope, force: true, failedOnly: true);
        var retry = await source.Next();
        Assert.Equal("Primal", retry.Target.Name);
        Assert.Empty(source.Calls);
        retry.Reply.SetResult(Data(50, world: "Primal World", dc: "Primal"));
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.All(cache.Snapshot(43, scope), s => Assert.Null(s.Error));
    }

    [Fact]
    public async Task OutOfOrderResponsesStayBoundToTheirOwnItemAndScope()
    {
        var source = new Source();
        var dc = MarketScope.Resolve(MarketScope.CrossWorldId, Home)!;
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        var worldCall = await source.Next();
        cache.Request(Item, dc);
        var dcCall = await source.Next();
        cache.Request(Item with { ItemId = 99 }, World);
        var otherCall = await source.Next();
        otherCall.Reply.SetResult(new MarketItemData { ItemId = 99, Listings = Data(300).Listings });
        dcCall.Reply.SetResult(Data(200));
        worldCall.Reply.SetResult(Data(100));
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Equal(100, Assert.Single(Assert.Single(cache.Snapshot(43, World)).Data!.Listings).PricePerUnit);
        Assert.Equal(200, Assert.Single(Assert.Single(cache.Snapshot(43, dc)).Data!.Listings).PricePerUnit);
        Assert.Equal(300, Assert.Single(Assert.Single(cache.Snapshot(99, World)).Data!.Listings).PricePerUnit);
    }

    [Fact]
    public async Task LateForgottenResponseCannotReplaceNewLookup()
    {
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        var old = await source.Next();
        cache.Forget(43);
        cache.Request(Item, World);
        var current = await source.Next();
        current.Reply.SetResult(Data(200));
        old.Reply.SetResult(Data(1));
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Equal(200, Assert.Single(Assert.Single(cache.Snapshot(43, World)).Data!.Listings).PricePerUnit);
    }

    [Fact]
    public async Task ClearedHistoryCacheCannotBeRepopulatedByLateResponse()
    {
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        var old = await source.Next();
        cache.Clear();
        old.Reply.SetResult(Data());
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Null(Assert.Single(cache.Snapshot(43, World)).Data);
    }

    [Fact]
    public async Task ResponsesForAnotherItemNeverPublish()
    {
        var source = new Source();
        using var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        (await source.Next()).Reply.SetResult(new() { ItemId = 99 });
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        var status = Assert.Single(cache.Snapshot(43, World));
        Assert.Null(status.Data);
        Assert.Contains("match", status.Error);
    }

    [Fact]
    public async Task DisposalCancelsRequestsAndDrainsWithoutPublishing()
    {
        var source = new Source();
        var cache = new MarketLookupCoordinator(source);
        cache.Request(Item, World);
        await source.Next();
        cache.Dispose();
        await cache.WhenIdleAsync().WaitAsync(TestTimeout);
        Assert.Null(Assert.Single(cache.Snapshot(43, World)).Data);
        cache.Request(Item, World);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public void ParserAddsWorldAndDcAndKeepsActualSourceTimestamp()
    {
        var json = Encoding.UTF8.GetBytes("""{"itemID":43,"lastUploadTime":1700000000000,"listings":[{"pricePerUnit":42,"quantity":99,"hq":true}]}""");
        var result = UniversalisService.Parse(json, 43, World.Targets[0]);
        var row = Assert.Single(result.Listings);
        Assert.Equal("Gilgamesh", row.WorldName);
        Assert.Equal("Aether", row.DataCenter);
        Assert.Equal(4158, row.StackTotal);
        Assert.True(row.IsHq);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), result.SourceUpdatedAt);
    }

    [Fact]
    public void EmptyListingsAreSuccessButMissingArrayIsFailure()
    {
        Assert.Empty(UniversalisService.Parse(Encoding.UTF8.GetBytes("""{"itemID":43,"listings":[]}"""), 43, World.Targets[0]).Listings);
        Assert.Throws<InvalidDataException>(() => UniversalisService.Parse(Encoding.UTF8.GetBytes("""{"itemID":43}"""), 43, World.Targets[0]));
        Assert.Throws<InvalidDataException>(() => UniversalisService.Parse(Encoding.UTF8.GetBytes("""{"itemID":99,"listings":[]}"""), 43, World.Targets[0]));
    }

    [Fact]
    public void ParserRejectsInvalidRowsWithoutInventingZeroPriceAndHandlesMissingFreshness()
    {
        var json = Encoding.UTF8.GetBytes("""{"itemID":43,"listings":[{"pricePerUnit":0,"quantity":1},{"pricePerUnit":4,"quantity":0},{"pricePerUnit":4,"quantity":1,"worldName":"Jenova"}]}""");
        var result = UniversalisService.Parse(json, 43, new("Aether", "Aether", "Aether"));
        Assert.Equal("Jenova", Assert.Single(result.Listings).WorldName);
        Assert.Null(result.SourceUpdatedAt);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public async Task HqCapableItemsFetchBothQualitiesAndMergeRatherThanCrowdingHqOut()
    {
        var queries = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"itemID":43,"listings":[{"pricePerUnit":100,"quantity":1,"hq":{{request.RequestUri.Query.Contains("hq=1").ToString().ToLowerInvariant()}}}]}"""),
            });
        }));
        using var api = new UniversalisService(http);
        var result = await api.FetchAsync(Item with { CanBeHq = true }, World.Targets[0], CancellationToken.None);
        Assert.Equal(2, result.Listings.Count);
        Assert.Contains(result.Listings, l => l.IsHq);
        Assert.Contains(result.Listings, l => !l.IsHq);
        Assert.Contains("hq=0", queries[0]);
        Assert.Contains("hq=1", queries[1]);
    }

    [Fact]
    public async Task Http404IsErrorNotEmptyListingSuccess()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var api = new UniversalisService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => api.FetchAsync(Item, World.Targets[0], CancellationToken.None));
    }

    [Fact]
    public async Task OversizedHttpResponseIsRejected()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', 1024 * 1024 + 1)) })));
        using var api = new UniversalisService(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => api.FetchAsync(Item, World.Targets[0], CancellationToken.None));
    }

    [Fact]
    public async Task HttpConcurrencyIsBoundedToThree()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var started = new SemaphoreSlim(0);
        var running = 0;
        var maximum = 0;
        var concurrencyLock = new object();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            var active = Interlocked.Increment(ref running);
            lock (concurrencyLock) maximum = Math.Max(maximum, active);
            started.Release();
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"itemID":43,"listings":[]}""") };
        }));
        using var api = new UniversalisService(http);
        var work = Enumerable.Range(0, 8).Select(_ => api.FetchAsync(Item, World.Targets[0], CancellationToken.None)).ToArray();
        for (var i = 0; i < 3; i++) Assert.True(await started.WaitAsync(TestTimeout));
        Assert.Equal(3, running);
        release.SetResult();
        await Task.WhenAll(work).WaitAsync(TestTimeout);
        Assert.Equal(3, maximum);
    }
}
