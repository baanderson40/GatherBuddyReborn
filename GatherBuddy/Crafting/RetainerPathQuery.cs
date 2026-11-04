using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Crafting;

/// <summary>Caller-owned query only. Completion never starts movement; cancellation permanently discards results.</summary>
public sealed class RetainerPathQuery : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task<List<Vector3>> _task;
    private bool _stopped, _disposed, _taken;
    public RetainerPathQuery(Func<CancellationToken, Task<List<Vector3>>> query)
    {
        try { _task = query(_cancel.Token); }
        catch { _cancel.Dispose(); throw; }
    }

    public bool Completed => _task.IsCompleted;
    public List<Vector3>? TakeCompleted()
    {
        if (_stopped || _taken || !Completed) return null;
        _taken = true;
        return _task.GetAwaiter().GetResult();
    }

    public bool StopAndSettle()
    {
        if (!_stopped) { _stopped = true; _cancel.Cancel(); }
        if (!Completed) return false;
        _ = _task.Exception;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.Dispose();
    }

    // Best available identity evidence: vnavmesh consumes a prefix, but exposes no operation ID.
    public static bool MatchesRemaining(IReadOnlyList<Vector3> submitted, IReadOnlyList<Vector3> current)
        => current.Count > 0 && current.Count <= submitted.Count
        && current.SequenceEqual(submitted.Skip(submitted.Count - current.Count));
}
