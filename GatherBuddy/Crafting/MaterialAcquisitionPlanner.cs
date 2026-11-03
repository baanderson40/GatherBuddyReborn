using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

/// <summary>Pure, deterministic allocation policy; reads no native services and never changes inputs.</summary>
public static class MaterialAcquisitionPlanner
{
    public static MaterialAcquisitionPlan Plan(MaterialAcquisitionPlanningInput input, AcquisitionTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (trace != null && trace.OperationId != input.SessionId)
            throw new ArgumentException("Planner trace must belong to the acquisition session.", nameof(trace));
        trace?.Record("P2", "Start", $"demandRows={input.Materials.Count}; offerRows={input.Offers.Count}; vendorData={input.VendorDataStatus}; purchasingEnabled={input.Policy.Enabled}");
        var commitments = input.Ledger.OutstandingCommitments.ToDictionary();
        ReleaseReplacedAllocations(input, commitments, trace);
        if (trace != null)
            foreach (var currency in commitments.Keys.Concat(input.Ledger.SettledSpending.Keys).Distinct().Order())
                trace.Record("P2", "LedgerBaseline", $"currency={currency}; retainedCommitments={commitments.GetValueOrDefault(currency)}; settled={input.Ledger.SettledSpending.GetValueOrDefault(currency)}; settledAlreadyReflectedInBalance=True");
        var spending = new Dictionary<uint, ulong>();
        var allocations = new List<VendorPurchaseAllocation>();
        var results = new List<PlannedMaterialAcquisition>();
        var demands = AggregateDemands(input.Materials);
        var needsVendors = input.Policy.Enabled && demands.Any(item =>
        {
            var remaining = RemainingDemand(input, item);
            return item.AllowPurchasing && remaining.Total > remaining.RequiredHQ;
        });
        var waiting = needsVendors && input.VendorDataStatus == VendorOfferDataStatus.Loading;
        var status = waiting ? AcquisitionPlanningStatus.WaitingForVendorData
            : needsVendors && input.VendorDataStatus == VendorOfferDataStatus.Unavailable
                ? AcquisitionPlanningStatus.VendorDataUnavailable : AcquisitionPlanningStatus.Ready;
        trace?.Record("P2", "Readiness", $"status={status}; needsVendors={needsVendors}; shortageConclusionsDeferred={waiting}",
            status == AcquisitionPlanningStatus.VendorDataUnavailable ? AcquisitionTraceLevel.Warning : AcquisitionTraceLevel.Debug);

        foreach (var item in demands.OrderBy(item => item.CanGatherNQ || item.CanGatherHQ).ThenBy(item => item.ItemId))
        {
            var demand = RemainingDemand(input, item);
            if (trace != null)
            {
                var owned = input.Inventory.GetValueOrDefault(item.ItemId);
                item.QualityDemand.ConsumeSplit(owned.NQ, owned.HQ, out var consumedNQ, out var consumedHQ);
                trace.Record("P2", "Demand", $"item={item.ItemId}; total={item.QualityDemand.Total}; aggregatedQuality={item.QualityDemand}; ownedNQ={owned.NQ}; ownedHQ={owned.HQ}; consumedNQ={consumedNQ}; consumedHQ={consumedHQ}; remainingQuality={demand}; allowPurchasing={item.AllowPurchasing}; gatherNQ={item.CanGatherNQ}; gatherHQ={item.CanGatherHQ}; order={(item.CanGatherNQ || item.CanGatherHQ ? "GatherFallbackAvailable" : "NonGatherableFirst")}");
            }
            var remaining = demand;
            uint purchased = 0;
            var offers = OrderOffers(input, input.Offers.Where(offer => offer.ItemId == item.ItemId)).ToList();
            if (remaining.Total > remaining.RequiredHQ && !waiting)
            {
                if (input.Policy.Enabled && item.AllowPurchasing && input.VendorDataStatus == VendorOfferDataStatus.Ready)
                {
                    foreach (var offer in offers)
                    {
                        var compatible = checked((uint)(remaining.RequiredNQ + remaining.PreferNQ + remaining.PreferHQ));
                        if (compatible == 0) break;
                        if (!IsValid(offer)) continue;
                        if (offer.Access == VendorOfferAccess.Inaccessible) continue;
                        if (!input.Policy.Allows(offer.CurrencyGroup)) continue;

                        var balance = input.CurrencyBalances.GetValueOrDefault(offer.CurrencyItemId);
                        var reserve = input.Policy.GetReserve(offer.CurrencyItemId);
                        var afterReserve = Subtract(balance, reserve);
                        var available = Subtract(afterReserve, commitments.GetValueOrDefault(offer.CurrencyItemId));
                        trace?.Record("P2", "Offer", $"item={item.ItemId}; currency={offer.CurrencyItemId}; group={offer.CurrencyGroup}; priority={input.Policy.CurrencyPriority.ToList().IndexOf(offer.CurrencyGroup)}; vendor={offer.Vendor.NpcId}; shop={offer.Vendor.ShopId}; costPerTransaction={offer.CostPerTransaction}; outputPerTransaction={offer.OutputQuantityPerTransaction}; balance={balance}; reserve={reserve}; commitments={commitments.GetValueOrDefault(offer.CurrencyItemId)}; settled={input.Ledger.SettledSpending.GetValueOrDefault(offer.CurrencyItemId)}; spendable={available}");
                        if (available < offer.CostPerTransaction)
                        {
                            trace?.Record("P2", "RejectFunding", $"item={item.ItemId}; currency={offer.CurrencyItemId}; reason={FundingShortage(input, offer, commitments)}");
                            continue;
                        }
                        if (offer.CurrencyItemId == VendorShopResolverGilId && input.Policy.MaximumGilSpendPerRun is { } cap)
                        {
                            var capRemaining = Subtract(Subtract(cap, input.Ledger.SettledSpending.GetValueOrDefault(VendorShopResolverGilId)),
                                commitments.GetValueOrDefault(VendorShopResolverGilId));
                            trace?.Record("P2", "GilBudget", $"item={item.ItemId}; cap={cap}; remainingCap={capRemaining}; currencySpendable={available}");
                            if (capRemaining < offer.CostPerTransaction)
                            {
                                trace?.Record("P2", "RejectFunding", $"item={item.ItemId}; currency=1; reason=SpendingCapReached");
                                continue;
                            }
                            available = Math.Min(available, capRemaining);
                        }

                        // Never overbuy a bundle. Residual units may be fulfilled by another offer or gathering.
                        var transactions = (uint)Math.Min(compatible / offer.OutputQuantityPerTransaction, available / offer.CostPerTransaction);
                        trace?.Record("P2", "Affordability", $"item={item.ItemId}; compatibleDemand={compatible}; affordableTransactions={available / offer.CostPerTransaction}; plannedTransactions={transactions}");
                        if (transactions == 0)
                        {
                            trace?.Record("P2", "RejectBundle", $"item={item.ItemId}; residual={compatible}; outputPerTransaction={offer.OutputQuantityPerTransaction}; excessPurchasingForbidden=True");
                            continue;
                        }
                        var allocation = new VendorPurchaseAllocation(input.SessionId, item.ItemId, transactions,
                            offer.OutputQuantityPerTransaction, offer.CurrencyItemId, offer.CurrencyGroup,
                            offer.CostPerTransaction, offer.ShopType, offer.Vendor);
                        allocations.Add(allocation);
                        var priorCommitment = commitments.GetValueOrDefault(offer.CurrencyItemId);
                        commitments[offer.CurrencyItemId] = checked(commitments.GetValueOrDefault(offer.CurrencyItemId) + allocation.ExpectedCurrencyCost);
                        spending[offer.CurrencyItemId] = checked(spending.GetValueOrDefault(offer.CurrencyItemId) + allocation.ExpectedCurrencyCost);
                        remaining = remaining.ConsumeSplit((int)allocation.RequestedOutputQuantity, 0, out _, out _);
                        purchased = checked(purchased + allocation.RequestedOutputQuantity);
                        trace?.Record("P2", "Allocate", $"item={item.ItemId}; currency={offer.CurrencyItemId}; quantity={allocation.RequestedOutputQuantity}; plannedCost={allocation.ExpectedCurrencyCost}; commitments={priorCommitment}->{commitments[offer.CurrencyItemId]}; spendable={available}->{Subtract(available, allocation.ExpectedCurrencyCost)}; remainingQuality={remaining}; notExecuted=True");
                    }
                }
            }

            var gathering = default(IngredientQualityDemand);
            if (!waiting && remaining.Total > 0)
            {
                var nq = item.CanGatherNQ ? remaining.RequiredNQ + remaining.PreferNQ + remaining.PreferHQ : 0;
                var hq = item.CanGatherHQ ? remaining.RequiredHQ + (item.CanGatherNQ ? 0 : remaining.PreferNQ + remaining.PreferHQ) : 0;
                var afterGather = remaining.ConsumeSplit(nq, hq, out _, out _);
                gathering = Difference(remaining, afterGather);
                remaining = afterGather;
            }
            // Loading creates no false shortage or fallback. Caller must wait, then rebuild the plan.
            var eligible = offers.Where(offer => GetOfferRejection(offer, input.Policy) == null).ToList();
            var blockers = new List<MaterialShortage>();
            var shortages = waiting || remaining.Total == 0 ? Array.Empty<MaterialShortage>()
                : ExplainShortage(input, item, remaining, offers, eligible, commitments, blockers);
            var rejected = offers.Select(offer => GetOfferRejection(offer, input.Policy)).Where(kind => kind.HasValue)
                .GroupBy(kind => kind!.Value).OrderBy(group => group.Key)
                .Select(group => new AcquisitionOfferRejectionSummary(group.Key, group.Count())).ToArray();
            if (trace != null)
            {
                foreach (var rejection in rejected)
                    trace.Record("P2", "RejectedOffers", $"item={item.ItemId}; category={rejection.Kind}; offerCount={rejection.OfferCount}; grouped=True");
                trace.Record("P2", "Outcome", $"item={item.ItemId}; plannedPurchase={purchased}; gatheringQuality={gathering}; unresolvedQuality={remaining}; primary={(shortages.Length > 0 ? shortages[0].Reason.ToString() : waiting ? "deferred" : "none")}; selectionRule=HighestPriorityEligibleOptionOrSourceRestriction");
                foreach (var blocker in blockers)
                    trace.Record("P2", "EligibleBlocker", $"item={item.ItemId}; reason={blocker.Reason}; {blocker.Detail}");
            }
            results.Add(new(item.ItemId, demand, purchased, gathering, remaining, Array.AsReadOnly(shortages))
            {
                EligibleOptionBlockers = blockers.AsReadOnly(),
                RejectedAlternatives = Array.AsReadOnly(rejected),
            });
        }
        if (trace != null)
        {
            foreach (var currency in spending.OrderBy(pair => pair.Key))
                trace.Record("P2", "ProjectedSpending", $"currency={currency.Key}; plannedCost={currency.Value}; commitments={commitments.GetValueOrDefault(currency.Key)}; notExecuted=True");
            trace.Complete($"status={status}; allocations={allocations.Count}; gatheringTypes={results.Count(item => item.GatheringDemand.Total > 0)}; unresolvedTypes={(waiting ? "deferred" : results.Count(item => item.UnresolvedDemand.Total > 0).ToString())}");
        }
        return new(input.SessionId, status, allocations, results, spending,
            new AcquisitionCurrencyLedgerSnapshot(commitments, input.Ledger.SettledSpending.ToDictionary()));
    }

    // Keep the pure planner independent of the native VendorShopResolver implementation.
    private const uint VendorShopResolverGilId = 1;
    private static ulong Subtract(ulong value, ulong amount) => value > amount ? value - amount : 0;

    /// <summary>One mutually exclusive rejection category per offer; affordability is a separate decision.</summary>
    public static AcquisitionOfferRejectionKind? GetOfferRejection(AcquisitionVendorOffer offer, ResolvedVendorPurchasePolicy policy)
    {
        if (offer.RejectionKind == AcquisitionOfferRejectionKind.WrongGrandCompany && offer.Access == VendorOfferAccess.Inaccessible)
            return AcquisitionOfferRejectionKind.WrongGrandCompany;
        if (!IsValid(offer)) return AcquisitionOfferRejectionKind.Unsupported;
        if (!policy.Allows(offer.CurrencyGroup)) return AcquisitionOfferRejectionKind.CurrencyDisallowed;
        if (offer.Access == VendorOfferAccess.Inaccessible)
            return offer.RejectionKind == AcquisitionOfferRejectionKind.MissingLocation
                ? AcquisitionOfferRejectionKind.MissingLocation : AcquisitionOfferRejectionKind.VendorInaccessible;
        return null;
    }

    private static MaterialShortage[] ExplainShortage(MaterialAcquisitionPlanningInput input, AcquisitionMaterialDemand item,
        IngredientQualityDemand remaining, List<AcquisitionVendorOffer> offers, List<AcquisitionVendorOffer> eligible,
        Dictionary<uint, ulong> commitments, List<MaterialShortage> eligibleBlockers)
    {
        var result = new List<MaterialShortage>();
        var nqShortage = remaining.Total - remaining.RequiredHQ;
        void Add(MaterialShortageReason reason, string? detail = null)
        {
            if (!result.Any(shortage => shortage.Reason == reason))
                result.Add(new(reason, detail ?? Describe(reason)));
        }
        if (nqShortage > 0)
        {
            if (!input.Policy.Enabled || !item.AllowPurchasing) Add(MaterialShortageReason.PurchasingDisabled);
            else if (input.VendorDataStatus == VendorOfferDataStatus.Unavailable) Add(MaterialShortageReason.VendorDataUnavailable);
            else if (eligible.Count > 0)
            {
                foreach (var offer in eligible)
                {
                    var reason = offer.OutputQuantityPerTransaction > (uint)nqShortage
                        ? MaterialShortageReason.BundleQuantityMismatch
                        : FundingShortage(input, offer, commitments) ?? MaterialShortageReason.BundleQuantityMismatch;
                    var blocker = new MaterialShortage(reason,
                        $"{nqShortage} NQ-compatible units remain. Currency {offer.CurrencyItemId}, vendor {offer.Vendor.NpcId}, shop {offer.Vendor.ShopId}: {Describe(reason)}");
                    eligibleBlockers.Add(blocker);
                    // Already sorted by currency preference, cost, and stable vendor identity.
                    Add(blocker.Reason, blocker.Detail);
                }
            }
            else
            {
                var supported = offers.Where(IsValid).ToList();
                var allowed = supported.Where(offer => input.Policy.Allows(offer.CurrencyGroup)).ToList();
                if (allowed.Count > 0) Add(MaterialShortageReason.VendorInaccessible);
                else if (supported.Count > 0) Add(MaterialShortageReason.CurrencyDisallowed);
                else Add(MaterialShortageReason.UnsupportedOffer);
            }
        }
        if (remaining.RequiredHQ > 0)
            Add(MaterialShortageReason.RequiredQualityUnavailable, $"{remaining.RequiredHQ} mandatory HQ units remain. {Describe(MaterialShortageReason.RequiredQualityUnavailable)}");
        return result.ToArray();
    }

    private static MaterialShortageReason? FundingShortage(MaterialAcquisitionPlanningInput input, AcquisitionVendorOffer offer,
        Dictionary<uint, ulong> commitments)
    {
        var committed = commitments.GetValueOrDefault(offer.CurrencyItemId);
        var balance = input.CurrencyBalances.GetValueOrDefault(offer.CurrencyItemId);
        var available = Subtract(Subtract(balance, input.Policy.GetReserve(offer.CurrencyItemId)), committed);
        if (available < offer.CostPerTransaction)
            return Subtract(balance, committed) >= offer.CostPerTransaction
                ? MaterialShortageReason.ReserveReached : MaterialShortageReason.InsufficientCurrency;
        if (offer.CurrencyItemId == VendorShopResolverGilId && input.Policy.MaximumGilSpendPerRun is { } cap
            && Subtract(Subtract(cap, input.Ledger.SettledSpending.GetValueOrDefault(VendorShopResolverGilId)), committed) < offer.CostPerTransaction)
            return MaterialShortageReason.SpendingCapReached;
        return null;
    }

    private static bool IsValid(AcquisitionVendorOffer offer)
        => offer.Supported && offer.CurrencyItemId != 0 && offer.CostPerTransaction > 0 && offer.OutputQuantityPerTransaction > 0
        && offer.Vendor is { NpcId: > 0, ShopId: > 0 } && Enum.IsDefined(offer.CurrencyGroup)
        && Enum.IsDefined(offer.ShopType) && Enum.IsDefined(offer.Access)
        && (offer.CurrencyGroup == VendorCurrencyGroup.Gil) == (offer.CurrencyItemId == VendorShopResolverGilId);

    private static IEnumerable<AcquisitionVendorOffer> OrderOffers(MaterialAcquisitionPlanningInput input, IEnumerable<AcquisitionVendorOffer> offers)
        => offers.OrderBy(offer => input.Policy.CurrencyPriority.ToList().IndexOf(offer.CurrencyGroup) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(offer => offer.CurrencyGroup).ThenBy(offer => offer.CurrencyItemId)
            .ThenBy(offer => offer.OutputQuantityPerTransaction == 0 ? decimal.MaxValue : (decimal)offer.CostPerTransaction / offer.OutputQuantityPerTransaction)
            .ThenBy(offer => offer.Vendor.NpcId).ThenBy(offer => offer.ShopType).ThenBy(offer => offer.Vendor.MenuShopType)
            .ThenBy(offer => offer.Vendor.ShopId).ThenBy(offer => offer.Vendor.SourceShopId)
            .ThenBy(offer => offer.Vendor.ShopItemIndex).ThenBy(offer => offer.Vendor.InclusionPageIndex)
            .ThenBy(offer => offer.Vendor.InclusionSubPageIndex).ThenBy(offer => offer.Vendor.GcRankIndex)
            .ThenBy(offer => offer.Vendor.GcCategoryIndex).ThenBy(offer => offer.CostPerTransaction)
            .ThenBy(offer => offer.OutputQuantityPerTransaction).ThenBy(offer => offer.Supported).ThenBy(offer => offer.Access);

    private static List<AcquisitionMaterialDemand> AggregateDemands(IEnumerable<AcquisitionMaterialDemand> materials)
    {
        var result = new List<AcquisitionMaterialDemand>();
        foreach (var group in materials.GroupBy(item => item.ItemId))
        {
            var first = group.First();
            var total = default(IngredientQualityDemand);
            foreach (var item in group)
            {
                // Validate individual demands before summing; otherwise negative values could cancel positives.
                _ = new MaterialAcquisitionRequirement(item.ItemId, checked(item.QualityDemand.RequiredHQ + item.QualityDemand.RequiredNQ
                    + item.QualityDemand.PreferHQ + item.QualityDemand.PreferNQ), 0, item.QualityDemand);
                if (item.CanGatherNQ != first.CanGatherNQ || item.CanGatherHQ != first.CanGatherHQ || item.AllowPurchasing != first.AllowPurchasing)
                    throw new ArgumentException("Repeated material demands must use identical source permissions.", nameof(materials));
                total = new(checked(total.RequiredHQ + item.QualityDemand.RequiredHQ), checked(total.RequiredNQ + item.QualityDemand.RequiredNQ),
                    checked(total.PreferHQ + item.QualityDemand.PreferHQ), checked(total.PreferNQ + item.QualityDemand.PreferNQ));
                _ = checked(total.RequiredHQ + total.RequiredNQ + total.PreferHQ + total.PreferNQ);
            }
            result.Add(first with { QualityDemand = total });
        }
        return result;
    }

    private static IngredientQualityDemand RemainingDemand(MaterialAcquisitionPlanningInput input, AcquisitionMaterialDemand item)
    {
        var inventory = input.Inventory.GetValueOrDefault(item.ItemId);
        ArgumentOutOfRangeException.ThrowIfNegative(inventory.NQ);
        ArgumentOutOfRangeException.ThrowIfNegative(inventory.HQ);
        return item.QualityDemand.ConsumeSplit(inventory.NQ, inventory.HQ, out _, out _);
    }

    private static IngredientQualityDemand Difference(IngredientQualityDemand before, IngredientQualityDemand after)
        => new(before.RequiredHQ - after.RequiredHQ, before.RequiredNQ - after.RequiredNQ,
            before.PreferHQ - after.PreferHQ, before.PreferNQ - after.PreferNQ);

    private static void ReleaseReplacedAllocations(MaterialAcquisitionPlanningInput input, Dictionary<uint, ulong> commitments, AcquisitionTrace? trace)
    {
        var seen = new HashSet<Guid>();
        foreach (var allocation in input.ReplacedAllocations)
        {
            if (allocation.SessionId != input.SessionId || !seen.Add(allocation.AllocationId))
                throw new ArgumentException("Only unique unexecuted allocations belonging to this session may be replaced.");
            var committed = commitments.GetValueOrDefault(allocation.CurrencyItemId);
            if (allocation.ExpectedCurrencyCost > committed)
                throw new ArgumentException("Replacement allocations exceed recorded outstanding commitments.");
            commitments[allocation.CurrencyItemId] = committed - allocation.ExpectedCurrencyCost;
            trace?.Record("P2", "ReleaseReplaced", $"allocation={allocation.AllocationId}; currency={allocation.CurrencyItemId}; released={allocation.ExpectedCurrencyCost}; commitments={committed}->{commitments[allocation.CurrencyItemId]}; settledSpendingUnchanged=True");
        }
    }

    private static string Describe(MaterialShortageReason reason) => reason switch
    {
        MaterialShortageReason.CurrencyDisallowed => "Automatic purchasing or this currency/source is disabled.",
        MaterialShortageReason.PurchasingDisabled => "Automatic purchasing or this material's purchase source is disabled.",
        MaterialShortageReason.BundleQuantityMismatch => "Offer bundle cannot fill the residual quantity without excess purchasing.",
        MaterialShortageReason.InsufficientCurrency => "Available currency is insufficient after other planned commitments.",
        MaterialShortageReason.ReserveReached => "Protected currency reserve prevents further purchasing.",
        MaterialShortageReason.SpendingCapReached => "Run gil spending limit prevents further purchasing.",
        MaterialShortageReason.VendorInaccessible => "Known vendor access or route restriction prevents purchasing.",
        MaterialShortageReason.RequiredQualityUnavailable => "Mandatory HQ demand cannot be fulfilled by NQ vendor offers.",
        MaterialShortageReason.VendorDataUnavailable => "Vendor data is unavailable; retry after resolving data availability.",
        _ => "No supported vendor offer is available for the remaining material.",
    };
}
