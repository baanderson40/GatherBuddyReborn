using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.AutoGather;

internal sealed class TimedNodeSearchState
{
    public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private (uint Territory, uint Node, long Start, long End)? _window;
    private DateTime _nextRescanAt = DateTime.MinValue;

    public HashSet<Vector3> ExcludedPositions { get; } = [];
    public bool IsWaitingForRescan { get; private set; }

    public void Reset()
    {
        _window = null;
        _nextRescanAt = DateTime.MinValue;
        IsWaitingForRescan = false;
        ExcludedPositions.Clear();
    }

    public void SetWindow(uint territory, uint node, long start, long end)
    {
        var window = (territory, node, start, end);
        if (_window == window)
            return;

        _window = window;
        _nextRescanAt = DateTime.MinValue;
        IsWaitingForRescan = false;
        ExcludedPositions.Clear();
    }

    /// <summary>Returns true while exhausted candidates should remain excluded.</summary>
    public bool WaitOrBeginRescan(DateTime now)
    {
        if (_nextRescanAt == DateTime.MinValue)
        {
            _nextRescanAt = now + RescanInterval;
            IsWaitingForRescan = true;
            return true;
        }

        if (now < _nextRescanAt)
        {
            IsWaitingForRescan = true;
            return true;
        }

        ExcludedPositions.Clear();
        _nextRescanAt = now + RescanInterval;
        IsWaitingForRescan = false;
        return false;
    }
}
