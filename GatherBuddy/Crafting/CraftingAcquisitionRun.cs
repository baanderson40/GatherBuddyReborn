using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

/// <summary>Fresh ownership per retry, but one immutable policy's spending scope per crafting run.</summary>
public sealed class CraftingAcquisitionRun
{
    private Dictionary<uint, ulong> _spent = new();
    private TransientVendorResult? _accounted;
    public Guid RunId { get; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }
    public CraftingAcquisitionRun() => SessionId = RunId;
    public Dictionary<uint, ulong> SpendingSnapshot() => _spent.ToDictionary();

    public void Observe(TransientVendorResult? result)
    {
        if (result == null || result.SessionId != SessionId || ReferenceEquals(result, _accounted)) return;
        if (!result.AccountingCertain || !result.CleanupSucceeded)
            throw new InvalidOperationException("Prior purchase accounting or cleanup remains uncertain; acquisition retry is unsafe.");
        var next = _spent.ToDictionary();
        foreach (var (currency, amount) in result.SettledSpending)
            next[currency] = checked(next.GetValueOrDefault(currency) + amount);
        _spent = next;
        _accounted = result;
    }

    public void Retry(TransientVendorResult? previousResult)
    {
        Observe(previousResult);
        SessionId = Guid.NewGuid();
    }
}
