using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

public enum VendorOfferDataStatus { Ready, Loading, Unavailable }
public enum VendorOfferAccess { Unknown, Accessible, Inaccessible }
public enum AcquisitionPlanningStatus { Ready, WaitingForVendorData, VendorDataUnavailable }
public enum AcquisitionOfferRejectionKind { Unsupported, WrongGrandCompany, MissingLocation, VendorInaccessible, CurrencyDisallowed }
public sealed record AcquisitionOfferRejectionSummary(AcquisitionOfferRejectionKind Kind, int OfferCount);

/// <summary>Total demand, not a net shortage. Repeated items are summed before inventory is consumed once.</summary>
public sealed record AcquisitionMaterialDemand(uint ItemId, IngredientQualityDemand QualityDemand,
    bool CanGatherNQ = false, bool CanGatherHQ = false, bool AllowPurchasing = true);

public readonly record struct AcquisitionInventory(int NQ, int HQ);

/// <summary>Detached exact offer. Unknown access requires executor verification, not assumed access.</summary>
public sealed record AcquisitionVendorOffer(uint ItemId, uint CurrencyItemId, VendorCurrencyGroup CurrencyGroup,
    ulong CostPerTransaction, uint OutputQuantityPerTransaction, VendorShopType ShopType, VendorNpc Vendor,
    bool Supported = true, VendorOfferAccess Access = VendorOfferAccess.Unknown, string? RejectionReason = null,
    AcquisitionOfferRejectionKind? RejectionKind = null)
{
    public IReadOnlyList<AcquisitionExchangeCost> Costs { get; init; } =
        Array.AsReadOnly(new[] { new AcquisitionExchangeCost(CurrencyItemId, CurrencyGroup, CostPerTransaction) });
}

public sealed class MaterialAcquisitionPlanningInput
{
    public Guid SessionId { get; }
    public ResolvedVendorPurchasePolicy Policy { get; }
    public IReadOnlyList<AcquisitionMaterialDemand> Materials { get; }
    public IReadOnlyList<AcquisitionVendorOffer> Offers { get; }
    public IReadOnlyDictionary<uint, AcquisitionInventory> Inventory { get; }
    public IReadOnlyDictionary<uint, ulong> CurrencyBalances { get; }
    public AcquisitionCurrencyLedgerSnapshot Ledger { get; }
    public VendorOfferDataStatus VendorDataStatus { get; }
    /// <summary>Only this session's cancelled, unexecuted allocations. Never release in-flight purchases.</summary>
    public IReadOnlyList<VendorPurchaseAllocation> ReplacedAllocations { get; }

    public MaterialAcquisitionPlanningInput(Guid sessionId, ResolvedVendorPurchasePolicy policy,
        IEnumerable<AcquisitionMaterialDemand> materials, IEnumerable<AcquisitionVendorOffer> offers,
        IDictionary<uint, AcquisitionInventory> inventory, IDictionary<uint, ulong> currencyBalances,
        AcquisitionCurrencyLedgerSnapshot ledger, VendorOfferDataStatus vendorDataStatus = VendorOfferDataStatus.Ready,
        IEnumerable<VendorPurchaseAllocation>? replacedAllocations = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A planning session is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(currencyBalances);
        ArgumentNullException.ThrowIfNull(ledger);
        if (!Enum.IsDefined(vendorDataStatus)) throw new ArgumentOutOfRangeException(nameof(vendorDataStatus));
        SessionId = sessionId;
        Policy = policy;
        Materials = materials.ToList().AsReadOnly();
        Offers = offers.Select(o => o with { Costs = Array.AsReadOnly(o.Costs.ToArray()) }).ToList().AsReadOnly();
        Inventory = new ReadOnlyDictionary<uint, AcquisitionInventory>(new Dictionary<uint, AcquisitionInventory>(inventory));
        CurrencyBalances = new ReadOnlyDictionary<uint, ulong>(new Dictionary<uint, ulong>(currencyBalances));
        Ledger = ledger;
        VendorDataStatus = vendorDataStatus;
        ReplacedAllocations = (replacedAllocations ?? []).ToList().AsReadOnly();
    }
}

/// <summary>All quantities are planned, never evidence that a purchase or gathering action occurred.</summary>
public sealed record PlannedMaterialAcquisition(uint ItemId, IngredientQualityDemand DemandAfterInventory,
    uint PlannedPurchaseQuantity, IngredientQualityDemand GatheringDemand, IngredientQualityDemand UnresolvedDemand,
    IReadOnlyList<MaterialShortage> Shortages)
{
    public MaterialShortage? PrimaryShortage => Shortages.Count == 0 ? null : Shortages[0];
    public IReadOnlyList<MaterialShortage> EligibleOptionBlockers { get; init; } = Array.Empty<MaterialShortage>();
    public IReadOnlyList<AcquisitionOfferRejectionSummary> RejectedAlternatives { get; init; } = Array.Empty<AcquisitionOfferRejectionSummary>();
}

public sealed class MaterialAcquisitionPlan
{
    public Guid SessionId { get; }
    public AcquisitionPlanningStatus Status { get; }
    public IReadOnlyList<VendorPurchaseAllocation> Allocations { get; }
    public IReadOnlyList<PlannedMaterialAcquisition> Materials { get; }
    public IReadOnlyDictionary<uint, ulong> ProjectedSpending { get; }
    public AcquisitionCurrencyLedgerSnapshot ResultingLedger { get; }

    internal MaterialAcquisitionPlan(Guid sessionId, AcquisitionPlanningStatus status,
        IEnumerable<VendorPurchaseAllocation> allocations, IEnumerable<PlannedMaterialAcquisition> materials,
        IDictionary<uint, ulong> projectedSpending, AcquisitionCurrencyLedgerSnapshot resultingLedger)
    {
        SessionId = sessionId;
        Status = status;
        Allocations = allocations.ToList().AsReadOnly();
        Materials = materials.ToList().AsReadOnly();
        ProjectedSpending = new ReadOnlyDictionary<uint, ulong>(new Dictionary<uint, ulong>(projectedSpending));
        ResultingLedger = resultingLedger;
    }
}
