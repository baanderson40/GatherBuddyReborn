using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

public sealed record AcquisitionExchangeCost(uint CurrencyItemId, VendorCurrencyGroup CurrencyGroup, ulong Amount);

/// <summary>Whole exchange transactions; excess is limited to the last required bundle.</summary>
public static class ExchangeTransactions
{
    public static uint Needed(uint units, uint output)
    {
        ArgumentOutOfRangeException.ThrowIfZero(output);
        return checked((uint)(((ulong)units + output - 1) / output));
    }

    public static bool ValidCosts(IReadOnlyList<AcquisitionExchangeCost> costs)
        => costs.Count > 0 && costs.Select(c => c.CurrencyItemId).Distinct().Count() == costs.Count
        && costs.All(c => c.CurrencyItemId != 0 && c.Amount > 0 && Enum.IsDefined(c.CurrencyGroup)
            && (c.CurrencyGroup == VendorCurrencyGroup.Gil) == (c.CurrencyItemId == 1));
}

public enum MaterialShortageReason
{
    InsufficientCurrency,
    CurrencyDisallowed,
    ReserveReached,
    SpendingCapReached,
    UnsupportedOffer,
    VendorInaccessible,
    InventoryCapacity,
    RequiredQualityUnavailable,
    PurchaseInterrupted,
    PartialPurchase,
    VendorDataUnavailable,
    BundleQuantityMismatch,
    PurchasingDisabled,
}

// Loading is deliberately not a shortage: the coordinator must wait before concluding no offer exists.
public enum MaterialAcquisitionStage
{
    Planning,
    WaitingForVendorData,
    Purchasing,
    WaitingForVendorCleanup,
    Finished,
}

public enum MaterialAcquisitionCompletion
{
    Completed,
    PartiallyFulfilled,
    Cancelled,
    Failed,
}

/// <summary>Total demand before owned inventory is deducted. Owned quantities must be quality-compatible.</summary>
public sealed record MaterialAcquisitionRequirement
{
    public uint ItemId { get; }
    public int RequiredQuantity { get; }
    public int UsableOwnedQuantity { get; }
    public int RemainingQuantity => Math.Max(0, RequiredQuantity - UsableOwnedQuantity);
    public IngredientQualityDemand QualityDemand { get; }

    public MaterialAcquisitionRequirement(uint itemId, int requiredQuantity, int usableOwnedQuantity, IngredientQualityDemand qualityDemand)
    {
        ArgumentOutOfRangeException.ThrowIfZero(itemId);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredQuantity);
        ArgumentOutOfRangeException.ThrowIfNegative(usableOwnedQuantity);
        ArgumentOutOfRangeException.ThrowIfNegative(qualityDemand.RequiredHQ);
        ArgumentOutOfRangeException.ThrowIfNegative(qualityDemand.RequiredNQ);
        ArgumentOutOfRangeException.ThrowIfNegative(qualityDemand.PreferHQ);
        ArgumentOutOfRangeException.ThrowIfNegative(qualityDemand.PreferNQ);
        var total = checked(qualityDemand.RequiredHQ + qualityDemand.RequiredNQ + qualityDemand.PreferHQ + qualityDemand.PreferNQ);
        if (total != requiredQuantity)
            throw new ArgumentException("Quality demand must describe the entire required quantity.", nameof(qualityDemand));
        ItemId = itemId;
        RequiredQuantity = requiredQuantity;
        UsableOwnedQuantity = usableOwnedQuantity;
        QualityDemand = qualityDemand;
    }
}

/// <summary>One exact offer, not permission for the executor to select a different currency.</summary>
public sealed record VendorPurchaseAllocation
{
    public Guid AllocationId { get; } = Guid.NewGuid();
    public Guid SessionId { get; }
    public uint ItemId { get; }
    public uint RequestedOutputQuantity { get; }
    public uint TransactionCount { get; }
    public uint OutputQuantityPerTransaction { get; }
    public uint CurrencyItemId { get; }
    public VendorCurrencyGroup CurrencyGroup { get; }
    public ulong ExpectedCurrencyCost { get; }
    public VendorShopType ShopType { get; }
    public VendorNpc Vendor { get; private init; }
    public IReadOnlyList<AcquisitionExchangeCost> Costs { get; }
    public IReadOnlyDictionary<uint, ulong> ExpectedCosts { get; }
    public IReadOnlyList<VendorNpc> AuthorizedAlternatives { get; }

    public VendorPurchaseAllocation(Guid sessionId, uint itemId, uint transactionCount, uint outputQuantityPerTransaction,
        uint currencyItemId, VendorCurrencyGroup currencyGroup, ulong costPerTransaction, VendorShopType shopType, VendorNpc vendor,
        IReadOnlyList<AcquisitionExchangeCost>? costs = null, IReadOnlyList<VendorNpc>? authorizedAlternatives = null)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A purchase must belong to an acquisition session.", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfZero(itemId);
        ArgumentOutOfRangeException.ThrowIfZero(transactionCount);
        ArgumentOutOfRangeException.ThrowIfZero(outputQuantityPerTransaction);
        ArgumentOutOfRangeException.ThrowIfZero(currencyItemId);
        ArgumentOutOfRangeException.ThrowIfZero(costPerTransaction);
        ArgumentNullException.ThrowIfNull(vendor);
        if (!Enum.IsDefined(currencyGroup) || !Enum.IsDefined(shopType))
            throw new ArgumentException("Unknown currency group or shop type.");
        SessionId = sessionId;
        ItemId = itemId;
        TransactionCount = transactionCount;
        OutputQuantityPerTransaction = outputQuantityPerTransaction;
        RequestedOutputQuantity = checked(transactionCount * outputQuantityPerTransaction);
        CurrencyItemId = currencyItemId;
        CurrencyGroup = currencyGroup;
        ExpectedCurrencyCost = checked(costPerTransaction * transactionCount);
        ShopType = shopType;
        Vendor = vendor;
        Costs = Array.AsReadOnly((costs ?? new[] { new AcquisitionExchangeCost(currencyItemId, currencyGroup, costPerTransaction) }).ToArray());
        if (!ExchangeTransactions.ValidCosts(Costs) || !Costs.Contains(new(currencyItemId, currencyGroup, costPerTransaction)))
            throw new ArgumentException("Invalid exchange costs or primary cost mismatch.", nameof(costs));
        ExpectedCosts = new ReadOnlyDictionary<uint, ulong>(Costs.ToDictionary(c => c.CurrencyItemId, c => checked(c.Amount * transactionCount)));
        AuthorizedAlternatives = Array.AsReadOnly((authorizedAlternatives ?? Array.Empty<VendorNpc>()).Distinct().ToArray());
    }

    public VendorPurchaseAllocation WithAuthorizedVendor(VendorNpc vendor)
    {
        if (!AuthorizedAlternatives.Contains(vendor)) throw new ArgumentException("Vendor was not authorized by the planner.", nameof(vendor));
        return this with { Vendor = vendor };
    }

    public bool CanShareShopWith(VendorPurchaseAllocation next)
        => SessionId == next.SessionId && Vendor.NpcId == next.Vendor.NpcId && ShopType == next.ShopType
            && Vendor.MenuShopType == next.Vendor.MenuShopType && Vendor.ShopId == next.Vendor.ShopId
            && Vendor.SourceShopId == next.Vendor.SourceShopId;
}

public sealed record MaterialShortage(MaterialShortageReason Reason, string Detail);

/// <summary>Acquisition result, not crafting completion. Gathering fallback still requires execution.</summary>
public sealed class MaterialAcquisitionOutcome
{
    public uint ItemId { get; }
    public uint PurchasedQuantity { get; }
    public uint GatheringFallbackQuantity { get; }
    public uint UnresolvedQuantity { get; }
    public IReadOnlyList<MaterialShortage> Shortages { get; }

    public MaterialAcquisitionOutcome(uint itemId, uint purchasedQuantity, uint gatheringFallbackQuantity,
        uint unresolvedQuantity, IEnumerable<MaterialShortage> shortages)
    {
        ArgumentOutOfRangeException.ThrowIfZero(itemId);
        ArgumentNullException.ThrowIfNull(shortages);
        ItemId = itemId;
        PurchasedQuantity = purchasedQuantity;
        GatheringFallbackQuantity = gatheringFallbackQuantity;
        UnresolvedQuantity = unresolvedQuantity;
        Shortages = shortages.ToList().AsReadOnly();
    }
}

/// <summary>Actual settled spending and outstanding commitments are distinct, detached ledger views.</summary>
public sealed class AcquisitionCurrencyLedgerSnapshot
{
    public IReadOnlyDictionary<uint, ulong> OutstandingCommitments { get; }
    public IReadOnlyDictionary<uint, ulong> SettledSpending { get; }

    public AcquisitionCurrencyLedgerSnapshot(IDictionary<uint, ulong> outstandingCommitments, IDictionary<uint, ulong> settledSpending)
    {
        ArgumentNullException.ThrowIfNull(outstandingCommitments);
        ArgumentNullException.ThrowIfNull(settledSpending);
        OutstandingCommitments = new ReadOnlyDictionary<uint, ulong>(new Dictionary<uint, ulong>(outstandingCommitments));
        SettledSpending = new ReadOnlyDictionary<uint, ulong>(new Dictionary<uint, ulong>(settledSpending));
    }

    // Subtract in stages to avoid unsigned overflow/underflow. Actual balance already reflects settled spending.
    public ulong GetSpendable(uint currencyItemId, ulong actualBalance, ulong reserve)
    {
        var afterReserve = actualBalance > reserve ? actualBalance - reserve : 0;
        var committed = OutstandingCommitments.GetValueOrDefault(currencyItemId);
        return afterReserve > committed ? afterReserve - committed : 0;
    }
}

public sealed class MaterialAcquisitionResult
{
    public Guid SessionId { get; }
    public MaterialAcquisitionCompletion Completion { get; }
    public IReadOnlyList<MaterialAcquisitionOutcome> Materials { get; }
    public AcquisitionCurrencyLedgerSnapshot CurrencyLedger { get; }

    public MaterialAcquisitionResult(Guid sessionId, MaterialAcquisitionCompletion completion,
        IEnumerable<MaterialAcquisitionOutcome> materials, AcquisitionCurrencyLedgerSnapshot currencyLedger)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("An acquisition result must identify its session.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(currencyLedger);
        if (!Enum.IsDefined(completion))
            throw new ArgumentOutOfRangeException(nameof(completion));
        var outcomes = materials.ToList();
        if (completion == MaterialAcquisitionCompletion.Completed && outcomes.Any(item => item.UnresolvedQuantity > 0 || item.GatheringFallbackQuantity > 0))
            throw new ArgumentException("Pending materials cannot be reported as fully acquired.", nameof(completion));
        SessionId = sessionId;
        Completion = completion;
        Materials = outcomes.AsReadOnly();
        CurrencyLedger = currencyLedger;
    }
}
