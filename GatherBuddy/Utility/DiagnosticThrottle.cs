using System;
using System.Collections.Generic;

namespace GatherBuddy.Utility;

/// <summary>Thread-safe, bounded diagnostic admission. Never caches or changes the operation being diagnosed.</summary>
public sealed class DiagnosticThrottle<TKey> where TKey : notnull
{
    private sealed class Entry(long timestamp, string state, LinkedListNode<TKey> node)
    {
        public long Timestamp = timestamp;
        public string State = state;
        public long Suppressed;
        public LinkedListNode<TKey> Node = node;
    }
    private readonly object _lock = new();
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<TKey> _order = new();
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly TimeProvider _clock;

    public DiagnosticThrottle(TimeSpan interval, int capacity = 512, TimeProvider? clock = null, IEqualityComparer<TKey>? comparer = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _interval = interval;
        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;
        _entries = new(comparer);
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    public bool TryLog(TKey key, string state, out long suppressed)
    {
        lock (_lock)
        {
            var now = _clock.GetTimestamp();
            suppressed = 0;
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count == _capacity)
                {
                    var oldest = _order.First!;
                    _entries.Remove(oldest.Value);
                    _order.RemoveFirst();
                }
                _entries.Add(key, new(now, state, _order.AddLast(key)));
                return true;
            }
            _order.Remove(entry.Node);
            _order.AddLast(entry.Node);
            var elapsed = _clock.GetElapsedTime(entry.Timestamp, now);
            if (entry.State == state && elapsed >= TimeSpan.Zero && elapsed < _interval)
            {
                if (entry.Suppressed < long.MaxValue) entry.Suppressed++;
                return false;
            }
            suppressed = entry.Suppressed;
            entry.Suppressed = 0;
            entry.State = state;
            entry.Timestamp = now;
            return true;
        }
    }

    public void Forget(TKey key)
    {
        lock (_lock)
        {
            if (_entries.Remove(key, out var entry)) _order.Remove(entry.Node);
        }
    }
    public void Clear()
    {
        lock (_lock) { _entries.Clear(); _order.Clear(); }
    }

}

public static class DiagnosticThrottle
{
    public static string Suffix(long suppressed) => suppressed > 0 ? $" (suppressed {suppressed} earlier repeats)" : string.Empty;
}
