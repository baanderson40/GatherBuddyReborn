using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.AutoGather;

internal sealed class FailedGatherSpawnCooldown
{
    internal readonly record struct SpawnKey(uint TerritoryId, uint NodeId, Vector3 Position);

    private readonly Dictionary<SpawnKey, DateTime> _until = [];

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);

    public bool Contains(uint territoryId, uint nodeId, Vector3 position, DateTime now)
    {
        Prune(now);
        return _until.TryGetValue(new SpawnKey(territoryId, nodeId, position), out var until) && until > now;
    }

    public void MarkFailed(uint territoryId, uint nodeId, Vector3 position, DateTime now)
        => _until[new SpawnKey(territoryId, nodeId, position)] = now + Duration;

    public void Clear() => _until.Clear();

    private void Prune(DateTime now)
    {
        foreach (var key in new List<SpawnKey>(_until.Keys))
        {
            if (_until[key] <= now)
                _until.Remove(key);
        }
    }
}
