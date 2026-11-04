using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Marketboard;

public interface IMarketDataSource
{
    Task<MarketItemData> FetchAsync(MarketCatalogItem item, MarketTarget target, CancellationToken cancellationToken);
}

public sealed record MarketTargetStatus(MarketTarget Target, MarketItemData? Data, DateTimeOffset? FetchedAt,
    bool Pending, string? Error, bool Stale);

/// <summary>Per-target cache and request ownership. Old/forgotten responses cannot overwrite new requests.</summary>
public sealed class MarketLookupCoordinator : IDisposable
{
    private sealed class Entry
    {
        public MarketItemData? Data;
        public DateTimeOffset? FetchedAt;
        public string? Error;
        public bool Pending;
        public Task Work = Task.CompletedTask;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(uint Item, MarketTarget Target), Entry> _entries = new();
    private readonly HashSet<Task> _workers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IMarketDataSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<MarketCatalogItem, MarketTarget, Exception>? _onFailure;
    private bool _disposed;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    public MarketLookupCoordinator(IMarketDataSource source, Func<DateTimeOffset>? now = null,
        Action<MarketCatalogItem, MarketTarget, Exception>? onFailure = null)
    {
        _source = source;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _onFailure = onFailure;
    }

    public void Request(MarketCatalogItem item, MarketScope scope, bool force = false, bool failedOnly = false)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var target in scope.Targets)
            {
                var key = (item.ItemId, target);
                if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();
                if (entry.Pending || (failedOnly && entry.Error == null)) continue;
                if (!force && entry.Error == null && entry.Data != null && _now() - entry.FetchedAt < CacheLifetime) continue;
                entry.Pending = true;
                entry.Error = null;
                var owned = entry;
                entry.Work = Task.Run(() => Fetch(item, target, owned, _lifetime.Token));
                _workers.Add(entry.Work);
                _ = entry.Work.ContinueWith(work => { lock (_lock) _workers.Remove(work); }, TaskScheduler.Default);
            }
        }
    }

    private async Task Fetch(MarketCatalogItem item, MarketTarget target, Entry owned, CancellationToken ct)
    {
        MarketItemData? data = null;
        string? error = null;
        try
        {
            data = await _source.FetchAsync(item, target, ct).ConfigureAwait(false);
            if (data.ItemId != item.ItemId) throw new InvalidOperationException("Market response item did not match request.");
        }
        catch (OperationCanceledException) { error = "Request cancelled."; }
        catch (Exception ex)
        {
            error = ex.Message;
            _onFailure?.Invoke(item, target, ex); // Once after retries, not once per HTTP attempt/frame.
        }
        lock (_lock)
        {
            if (_disposed || !_entries.TryGetValue((item.ItemId, target), out var current) || !ReferenceEquals(owned, current)) return;
            owned.Pending = false;
            owned.Error = error;
            if (data != null && error == null)
            {
                owned.Data = data;
                owned.FetchedAt = _now();
            }
        }
    }

    public IReadOnlyList<MarketTargetStatus> Snapshot(uint itemId, MarketScope scope)
    {
        lock (_lock)
            return scope.Targets.Select(target =>
            {
                _entries.TryGetValue((itemId, target), out var entry);
                return new MarketTargetStatus(target, entry?.Data, entry?.FetchedAt, entry?.Pending == true,
                    entry?.Error, entry?.Data != null && (_now() - entry.FetchedAt >= CacheLifetime || entry.Error != null));
            }).ToArray();
    }

    public void Forget(uint itemId)
    {
        lock (_lock)
            foreach (var key in _entries.Keys.Where(k => k.Item == itemId).ToArray()) _entries.Remove(key);
    }

    public void Clear() { lock (_lock) _entries.Clear(); }

    public Task WhenIdleAsync() { lock (_lock) return Task.WhenAll(_workers.ToArray()); }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        _ = WhenIdleAsync().ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }
}
