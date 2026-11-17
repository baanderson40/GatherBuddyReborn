using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

/// <summary>Read-only presentation snapshot. Never authorizes or allocates purchases.</summary>
public sealed record MaterialPurchaseDisplayContext(ResolvedVendorPurchasePolicy Policy, Guid? RunId, string CacheKey, string Description)
{
    public static MaterialPurchaseDisplayContext Resolve(int listId, AutomaticVendorPurchasePolicy global,
        VendorPurchaseListSettings list, int? runListId = null, Guid? runId = null, ResolvedVendorPurchasePolicy? captured = null)
    {
        var useRun = runListId == listId && runId.HasValue && captured != null;
        var policy = useRun ? captured! : ResolvedVendorPurchasePolicy.Resolve(global, list);
        var owner = useRun ? runId : null;
        var source = policy.Origin switch
        {
            VendorPurchasePolicyOrigin.Global => "Global settings",
            VendorPurchasePolicyOrigin.ListOverride => "Custom list settings",
            _ => "Disabled for this list",
        };
        var key = FormattableString.Invariant($"{listId}|{owner}|{policy.Enabled}|{policy.Origin}|{policy.MaximumGilSpendPerRun}|{policy.GrandCompanySealReserve}")
            + "|" + string.Join(",", policy.AllowedCurrencyGroups)
            + "|" + string.Join(",", policy.AllowedCurrencyItemIds.Order())
            + "|" + string.Join(",", policy.CurrencyPriority)
            + "|" + string.Join(",", policy.CurrencyReservesByItemId.OrderBy(p => p.Key)
                .Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ":" + p.Value.ToString(CultureInfo.InvariantCulture)))
            + "|" + string.Join(",", policy.MaximumSpendPerRunByCurrencyItemId.OrderBy(p => p.Key)
                .Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ":" + p.Value.ToString(CultureInfo.InvariantCulture)));
        return new(policy, owner, key, $"{(useRun ? "Captured run" : "Next run")}: {source} — purchases {(policy.Enabled ? "enabled" : "disabled")}");
    }
}

/// <summary>Shared execution-support boundary; unknown item metadata fails closed.</summary>
public static class IntegratedMaterialOfferSupport
{
    public static AcquisitionVendorOffer Apply(AcquisitionVendorOffer offer, uint? stackSize, bool housing, bool equippable)
    {
        var supported = (offer.ShopType != VendorShopType.SpecialCurrency
                || offer.CurrencyGroup == VendorCurrencyGroup.BicolorGemstones
                || AutomaticVendorCurrencyCatalog.Supports(offer.CurrencyGroup, offer.CurrencyItemId))
            && !housing && !equippable && stackSize is { } stack && stack > 1 && offer.OutputQuantityPerTransaction <= stack;
        return supported ? offer : offer with { Supported = false,
            RejectionReason = "Integrated execution supports stackable gil/GC, gemstones, and validated scrip/tomestone material exchanges only.",
            RejectionKind = AcquisitionOfferRejectionKind.Unsupported };
    }
}

public enum MaterialPurchaseCandidateKind { None, Eligible, Partial, PendingData }

public sealed record MaterialPurchaseCandidate(MaterialPurchaseCandidateKind Kind, IReadOnlyList<VendorCurrencyGroup> Currencies, string Reason)
{
    public bool IsCandidate => Kind is MaterialPurchaseCandidateKind.Eligible or MaterialPurchaseCandidateKind.Partial;
    public string CurrencyDescription => string.Join(", ", Currencies.Select(group => group switch
    {
        VendorCurrencyGroup.Gil => "Gil",
        VendorCurrencyGroup.GrandCompanySeals => "Current-GC seals",
        VendorCurrencyGroup.BicolorGemstones => "Bicolor Gemstones",
        VendorCurrencyGroup.Scrips => "Scrips",
        VendorCurrencyGroup.Tomestones => "Tomestones",
        _ => group.ToString(),
    }));
    public const string Disclaimer = "Automatic purchase candidate, not a guaranteed purchase. Final buying depends on funds, reserves, spending limits, inventory capacity, access and live shop validation. Unfulfilled demand falls back to gathering or shortage reporting.";
    public static MaterialPurchaseCandidate None(string reason) => new(MaterialPurchaseCandidateKind.None, Array.Empty<VendorCurrencyGroup>(), reason);
}

public static class MaterialPurchasePresentation
{
    public static MaterialPurchaseCandidate Classify(uint itemId, IngredientQualityDemand demand, bool acquisitionMaterial,
        ResolvedVendorPurchasePolicy policy, VendorOfferDataStatus status, IEnumerable<AcquisitionVendorOffer> offers, uint currentSeals)
    {
        if (!acquisitionMaterial) return MaterialPurchaseCandidate.None("Reference/precraft row; not an automatic acquisition requirement.");
        if (!policy.Enabled) return MaterialPurchaseCandidate.None("Automatic purchasing disabled by the displayed policy.");
        if (demand.Total <= 0 || demand.Total == demand.RequiredHQ)
            return MaterialPurchaseCandidate.None("No NQ-compatible demand; NQ vendor offers cannot satisfy mandatory HQ.");
        if (status != VendorOfferDataStatus.Ready)
            return new(MaterialPurchaseCandidateKind.PendingData, Array.Empty<VendorCurrencyGroup>(), "Automatic offer data pending or unavailable; normal source grouping shown.");
        var groups = offers.Where(o => o.ItemId == itemId && MaterialAcquisitionPlanner.IsValid(o) && o.Access != VendorOfferAccess.Inaccessible
                && o.Costs.All(c => policy.Allows(c.CurrencyGroup, c.CurrencyItemId)
                    && (c.CurrencyGroup != VendorCurrencyGroup.GrandCompanySeals || GrandCompanySealPolicy.IsCurrentCurrency(c.CurrencyItemId, currentSeals))
                    && policy.GetMaximumSpendPerRun(c.CurrencyItemId) != 0))
            .SelectMany(o => o.Costs.Select(c => c.CurrencyGroup)).Distinct()
            .OrderBy(g => { var index = policy.CurrencyPriority.ToList().IndexOf(g); return index < 0 ? int.MaxValue : index; }).ToArray();
        if (groups.Length == 0) return MaterialPurchaseCandidate.None("No supported, accessible automatic offer authorized by the displayed policy.");
        var partial = demand.RequiredHQ > 0;
        return new(partial ? MaterialPurchaseCandidateKind.Partial : MaterialPurchaseCandidateKind.Eligible, Array.AsReadOnly(groups),
            partial ? "Only NQ-compatible demand is a purchase candidate; mandatory HQ still requires another source." : "Supported offer permitted by the displayed policy; access may still require live verification.");
    }
}
