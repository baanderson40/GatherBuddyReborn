using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GatherBuddy.Automation;

public readonly record struct TeleportContext(ulong ContentId, uint HomeWorld, uint World, uint Territory);

public sealed class ReadinessTaskPause
{
    private long? _pausedAt;
    public long Observe(bool ready, long now)
    {
        if (!ready) { _pausedAt ??= now; return 0; }
        var elapsed = _pausedAt is { } since ? Math.Max(0, now - since) : 0;
        _pausedAt = null;
        return elapsed;
    }
}

/// <summary>Managed policy for readiness, atomic snapshots and deferred refresh retries.</summary>
public sealed class TeleportSnapshotCache
{
    private readonly object _sync = new();
    private TeleportContext? _context;
    private TimeSpan? _readySince;
    private IReadOnlyDictionary<uint, int>? _costs;
    private bool _pending = true;
    private bool _refreshing;
    private long _generation;
    private TeleportContext? _readyContext;
    public TeleportContext? ReadyContext { get { lock (_sync) return _readyContext; } private set { lock (_sync) _readyContext = value; } }
    public bool Pending { get { lock (_sync) return _pending; } }
    public void RequestRefresh() { lock (_sync) { _pending = true; _generation++; } }

    public bool Observe(TeleportContext? context, bool ready, TimeSpan now)
    {
        lock (_sync) return ObserveCore(context, ready, now);
    }

    private bool ObserveCore(TeleportContext? context, bool ready, TimeSpan now)
    {
        if (context != _context)
        {
            _context = context;
            _costs = null;
            _readySince = null;
            _pending = true;
            _generation++;
        }
        if (!ready || context is not { ContentId: not 0, HomeWorld: not 0, World: not 0, Territory: not 0 })
        {
            _readySince = null;
            ReadyContext = null;
            _pending = true;
            _generation++;
            return false;
        }
        _readySince ??= now;
        ReadyContext = now - _readySince.Value >= TimeSpan.FromMilliseconds(500) ? context : null;
        return ReadyContext != null;
    }

    public bool TryRead(Func<IReadOnlyDictionary<uint, int>?> refresh, out IReadOnlyDictionary<uint, int>? costs)
    {
        lock (_sync) return TryReadCore(refresh, out costs);
    }

    private bool TryReadCore(Func<IReadOnlyDictionary<uint, int>?> refresh, out IReadOnlyDictionary<uint, int>? costs)
    {
        costs = null;
        if (ReadyContext == null) return false;
        if (_pending)
        {
            if (_refreshing) return false;
            var generation = _generation;
            IReadOnlyDictionary<uint, int>? replacement;
            _refreshing = true;
            try { replacement = refresh(); }
            finally { _refreshing = false; }
            if (replacement == null || generation != _generation || ReadyContext == null) return false;
            _costs = new ReadOnlyDictionary<uint, int>(new Dictionary<uint, int>(replacement));
            _pending = false;
        }
        costs = _costs;
        return costs != null;
    }
}

/// <summary>AR reads scheduler output, never invokes the scheduler or native APIs.</summary>
public sealed class TimedNodeDeferralSnapshot
{
    private TeleportContext? _context;
    private long _hour;
    private bool _defer;
    public void Publish(TeleportContext context, long hour, bool defer)
    { _context = context; _hour = hour; _defer = defer; }
    public void Invalidate() => _context = null;
    public bool TryRead(TeleportContext? context, long hour, bool dirty, out bool defer)
    {
        defer = _defer;
        return !dirty && context != null && context == _context && hour == _hour;
    }
}
