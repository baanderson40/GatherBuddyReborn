using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Game-thread-only exclusive ownership of the shared vendor navigation/purchase services.</summary>
public sealed class VendorAutomationLease
{
    public Guid? Owner { get; private set; }
    public bool Allows(Guid? sessionId) => Owner == null ? sessionId == null : Owner == sessionId;
    public bool TryAcquire(Guid sessionId)
    {
        if (sessionId == Guid.Empty || Owner != null) return false;
        Owner = sessionId;
        return true;
    }
    public bool Release(Guid sessionId)
    {
        if (Owner != sessionId) return false;
        Owner = null;
        return true;
    }
}

/// <summary>Pure spending guard. Other commitments exclude this allocation's own reservation.</summary>
public sealed record TransientPurchaseBudget(ulong Reserve, ulong OtherCommitments, ulong? MaximumSpend)
{
    public IReadOnlyDictionary<uint, TransientPurchaseBudget>? CurrencyBudgets { get; init; }
    public ulong Spendable(ulong actualBalance, ulong alreadySpent)
    {
        static ulong Subtract(ulong value, ulong amount) => value > amount ? value - amount : 0;
        var available = Subtract(Subtract(actualBalance, Reserve), OtherCommitments);
        return MaximumSpend is { } cap ? Math.Min(available, Subtract(cap, alreadySpent)) : available;
    }
}
