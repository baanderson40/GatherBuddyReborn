using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Marketboard;

public sealed class UniversalisService : IMarketDataSource, IDisposable
{
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _throttle = new(3, 3);

    public UniversalisService(HttpClient? client = null)
    {
        _http = client ?? new HttpClient();
        _ownsClient = client == null;
        if (_ownsClient) _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GatherBuddyReborn-Vulcan");
    }

    public async Task<MarketItemData> FetchAsync(MarketCatalogItem item, MarketTarget target, CancellationToken cancellationToken)
    {
        // Fetch each quality independently so cheap NQ stacks cannot crowd HQ out.
        var nq = await FetchQuality(item.ItemId, target, item.CanBeHq ? false : null, cancellationToken).ConfigureAwait(false);
        var hq = item.CanBeHq
            ? await FetchQuality(item.ItemId, target, true, cancellationToken).ConfigureAwait(false) : null;
        return new MarketItemData
        {
            ItemId = item.ItemId, ItemName = item.Name, IconId = item.IconId,
            Listings = nq.Listings.Concat(hq?.Listings ?? Enumerable.Empty<MarketListing>()).ToList(),
            SourceUpdatedAt = Latest(nq.SourceUpdatedAt, hq?.SourceUpdatedAt),
        };
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b)
        => a == null ? b : b == null ? a : a > b ? a : b;

    private async Task<MarketItemData> FetchQuality(uint itemId, MarketTarget target, bool? hq, CancellationToken ct)
    {
        var quality = hq.HasValue ? $"&hq={(hq.Value ? 1 : 0)}" : string.Empty;
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(target.ApiScope)}/{itemId}?listings=20&entries=0{quality}";
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidDataException("Universalis has no data for this item/scope (HTTP 404).");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new InvalidDataException("Market response exceeds size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("Market response exceeds size limit.");
                    buffer.Write(chunk, 0, read);
                }
                return Parse(buffer.ToArray(), itemId, target);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 2) throw new TimeoutException("Universalis request timed out.");
            }
            catch (HttpRequestException) when (attempt < 2) { }
            finally { _throttle.Release(); }
        }
    }

    public static MarketItemData Parse(byte[] json, uint expectedItemId, MarketTarget target)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("itemID", out var id) || id.GetUInt32() != expectedItemId)
            throw new InvalidDataException("Universalis response item did not match request.");
        if (!root.TryGetProperty("listings", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Universalis response has no listings array.");
        var listings = new List<MarketListing>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("pricePerUnit", out var price) || !price.TryGetInt32(out var unitPrice) || unitPrice <= 0
             || !row.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt32(out var count) || count <= 0) continue;
            var world = row.TryGetProperty("worldName", out var worldName) && worldName.ValueKind == JsonValueKind.String
                ? worldName.GetString() : null;
            listings.Add(new MarketListing
            {
                PricePerUnit = unitPrice, Quantity = count,
                IsHq = row.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True,
                WorldName = string.IsNullOrEmpty(world) ? target.IsWorld ? target.Name : "Unknown world" : world,
                DataCenter = target.DataCenter,
            });
        }
        DateTimeOffset? uploaded = null;
        if (root.TryGetProperty("lastUploadTime", out var timestamp) && timestamp.TryGetInt64(out var milliseconds) && milliseconds > 0)
        {
            try { uploaded = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        return new MarketItemData { ItemId = expectedItemId, Listings = listings, SourceUpdatedAt = uploaded };
    }

    // Owner waits for cancelled requests before disposing the HTTP/semaphore resources.
    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
        _throttle.Dispose();
    }
}
