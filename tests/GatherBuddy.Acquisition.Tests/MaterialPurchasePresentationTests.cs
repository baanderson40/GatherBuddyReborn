using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class MaterialPurchasePresentationTests
{
    private static AutomaticVendorPurchasePolicy Settings(params VendorCurrencyGroup[] groups)
        => new() { Enabled = true, AllowedCurrencyGroups = groups.ToList(), CurrencyPriority = groups.ToList() };
    private static AcquisitionVendorOffer Gil() => new(100, 1, VendorCurrencyGroup.Gil, 10, 1, VendorShopType.GilShop, new(1, "Vendor", 1));
    private static MaterialPurchaseCandidate Candidate(AutomaticVendorPurchasePolicy? settings = null,
        AcquisitionVendorOffer? offer = null, IngredientQualityDemand? demand = null, bool acquisition = true,
        VendorOfferDataStatus status = VendorOfferDataStatus.Ready, uint seals = 20)
        => MaterialPurchasePresentation.Classify(100, demand ?? IngredientQualityDemand.FromPreferNQ(10), acquisition,
            ResolvedVendorPurchasePolicy.Resolve(settings ?? Settings(VendorCurrencyGroup.Gil)), status, [offer ?? Gil()], seals);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InheritedPolicyFollowsGlobalEnabledState(bool enabled)
    {
        var settings = Settings(VendorCurrencyGroup.Gil);
        settings.Enabled = enabled;
        var context = MaterialPurchaseDisplayContext.Resolve(1, settings, new());
        Assert.Equal(enabled, context.Policy.Enabled);
        Assert.Equal(VendorPurchasePolicyOrigin.Global, context.Policy.Origin);
        Assert.Equal(enabled, Candidate(settings).IsCandidate);
    }

    [Fact]
    public void ListDisabledOverridesGlobalAndCustomPolicyOverridesDisabledGlobal()
    {
        var global = Settings(VendorCurrencyGroup.Gil);
        Assert.False(MaterialPurchaseDisplayContext.Resolve(1, global,
            new() { Mode = VendorPurchaseOverrideMode.Disabled }).Policy.Enabled);
        global.Enabled = false;
        var context = MaterialPurchaseDisplayContext.Resolve(1, global,
            new() { Mode = VendorPurchaseOverrideMode.Custom, CustomPolicy = Settings(VendorCurrencyGroup.BicolorGemstones) });
        Assert.True(context.Policy.Allows(VendorCurrencyGroup.BicolorGemstones));
        Assert.False(context.Policy.Allows(VendorCurrencyGroup.Gil));
    }

    [Fact]
    public void CapturedRunRemainsStableDuringSavedEditsAndCannotLeakToOtherList()
    {
        var global = Settings(VendorCurrencyGroup.Gil);
        var captured = ResolvedVendorPurchasePolicy.Resolve(global);
        var run = Guid.NewGuid();
        var before = MaterialPurchaseDisplayContext.Resolve(1, global, new(), 1, run, captured);
        global.Enabled = false;
        global.MaximumGilSpendPerRun = 0;
        var after = MaterialPurchaseDisplayContext.Resolve(1, global, new(), 1, run, captured);
        Assert.Equal(before.CacheKey, after.CacheKey);
        Assert.Same(captured, after.Policy);
        Assert.Contains("Captured run", after.Description);
        Assert.False(MaterialPurchaseDisplayContext.Resolve(2, global, new(), 1, run, captured).Policy.Enabled);
        Assert.Null(MaterialPurchaseDisplayContext.Resolve(2, global, new(), 1, run, captured).RunId);
        var stopped = MaterialPurchaseDisplayContext.Resolve(1, global, new());
        Assert.NotEqual(before.CacheKey, stopped.CacheKey);
        Assert.False(stopped.Policy.Enabled);
    }

    [Fact]
    public void PreviewKeyChangesForInPlacePolicyEditsWithoutMutatingSettings()
    {
        var settings = Settings(VendorCurrencyGroup.Gil);
        var first = MaterialPurchaseDisplayContext.Resolve(1, settings, new());
        Assert.Equal(first.CacheKey, MaterialPurchaseDisplayContext.Resolve(1, settings, new()).CacheKey);
        settings.CurrencyReservesByItemId[1] = 200;
        var reserve = MaterialPurchaseDisplayContext.Resolve(1, settings, new());
        Assert.NotEqual(first.CacheKey, reserve.CacheKey);
        settings.MaximumGilSpendPerRun = 0;
        Assert.NotEqual(reserve.CacheKey, MaterialPurchaseDisplayContext.Resolve(1, settings, new()).CacheKey);
        Assert.Empty(first.Policy.CurrencyReservesByItemId);
        Assert.Equal(200ul, settings.CurrencyReservesByItemId[1]);
    }

    [Fact]
    public void CandidateRequiresAllowedCurrencyAndNonzeroGilCap()
    {
        Assert.True(Candidate().IsCandidate);
        Assert.False(Candidate(Settings(VendorCurrencyGroup.BicolorGemstones)).IsCandidate);
        var settings = Settings(VendorCurrencyGroup.Gil);
        settings.MaximumGilSpendPerRun = 0;
        Assert.False(Candidate(settings).IsCandidate);
    }

    [Fact]
    public void AllCostsMustBeAllowedAndCurrentGrandCompanyMustMatch()
    {
        var offer = Gil() with { Costs = [new(1, VendorCurrencyGroup.Gil, 10), new(20, VendorCurrencyGroup.GrandCompanySeals, 2)] };
        Assert.False(Candidate(Settings(VendorCurrencyGroup.Gil), offer).IsCandidate);
        var policy = Settings(VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals);
        Assert.True(Candidate(policy, offer).IsCandidate);
        Assert.False(Candidate(policy, offer, seals: 21).IsCandidate);
        Assert.False(Candidate(policy, offer, seals: 0).IsCandidate);
    }

    [Fact]
    public void SupportedGemstonesQualifyWithoutOverridingManualCurrencyChoice()
    {
        var offer = Gil() with { CurrencyItemId = 26807, CurrencyGroup = VendorCurrencyGroup.BicolorGemstones, CostPerTransaction = 3,
            ShopType = VendorShopType.SpecialCurrency, Costs = [new(26807, VendorCurrencyGroup.BicolorGemstones, 3)] };
        Assert.True(Candidate(Settings(VendorCurrencyGroup.BicolorGemstones), offer).IsCandidate);
        Assert.False(Candidate(Settings(VendorCurrencyGroup.Gil), offer).IsCandidate);
    }

    [Theory]
    [InlineData(VendorOfferDataStatus.Loading)]
    [InlineData(VendorOfferDataStatus.Unavailable)]
    public void MissingDataIsPendingNotAConfirmedOfferAbsence(VendorOfferDataStatus status)
    {
        var candidate = Candidate(status: status);
        Assert.Equal(MaterialPurchaseCandidateKind.PendingData, candidate.Kind);
        Assert.False(candidate.IsCandidate);
    }

    [Fact]
    public void UnsupportedInaccessibleAndInvalidOffersCannotQualify()
    {
        Assert.False(Candidate(offer: Gil() with { Supported = false }).IsCandidate);
        Assert.False(Candidate(offer: Gil() with { Access = VendorOfferAccess.Inaccessible }).IsCandidate);
        Assert.False(Candidate(offer: Gil() with { OutputQuantityPerTransaction = 0 }).IsCandidate);
        Assert.False(Candidate(offer: Gil() with { Costs = [] }).IsCandidate);
        Assert.False(Candidate(offer: Gil() with { Costs = [new(1, VendorCurrencyGroup.Gil, 99)] }).IsCandidate);
        Assert.False(Candidate(offer: Gil() with { Vendor = new(0, "Missing vendor", 0) }).IsCandidate);
        Assert.True(Candidate(offer: Gil() with { Access = VendorOfferAccess.Unknown }).IsCandidate);
    }

    [Fact]
    public void HqOnlyDemandAndReferencePrecraftsDoNotBecomePurchaseCandidates()
    {
        Assert.False(Candidate(acquisition: false).IsCandidate);
        Assert.False(Candidate(demand: new IngredientQualityDemand(RequiredHQ: 10, RequiredNQ: 0, PreferHQ: 0, PreferNQ: 0)).IsCandidate);
        var partial = Candidate(demand: new IngredientQualityDemand(RequiredHQ: 5, RequiredNQ: 5, PreferHQ: 0, PreferNQ: 0));
        Assert.Equal(MaterialPurchaseCandidateKind.Partial, partial.Kind);
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData(1u, false, false, false)]
    [InlineData(99u, true, false, false)]
    [InlineData(99u, false, true, false)]
    [InlineData(99u, false, false, true)]
    public void SharedSupportPreservesExecutionMaterialRestrictions(uint? stack, bool housing, bool equippable, bool expected)
        => Assert.Equal(expected, IntegratedMaterialOfferSupport.Apply(Gil(), stack, housing, equippable).Supported);

    [Fact]
    public void PolicyPriorityOrdersCandidateCurrenciesAndInvalidatesPreviewKey()
    {
        var settings = Settings(VendorCurrencyGroup.Gil, VendorCurrencyGroup.GrandCompanySeals);
        var before = MaterialPurchaseDisplayContext.Resolve(1, settings, new());
        settings.CurrencyPriority.Reverse();
        var after = MaterialPurchaseDisplayContext.Resolve(1, settings, new());
        Assert.NotEqual(before.CacheKey, after.CacheKey);
        var seals = new AcquisitionVendorOffer(100, 20, VendorCurrencyGroup.GrandCompanySeals, 2, 1,
            VendorShopType.GrandCompanySeals, new(2, "GC vendor", 2, VendorMenuShopType.GrandCompanyShop));
        var candidate = MaterialPurchasePresentation.Classify(100, IngredientQualityDemand.FromPreferNQ(10), true,
            after.Policy, VendorOfferDataStatus.Ready, [Gil(), seals], 20);
        Assert.Equal(new[] { VendorCurrencyGroup.GrandCompanySeals, VendorCurrencyGroup.Gil }, candidate.Currencies);
        Assert.Equal("Current-GC seals, Gil", candidate.CurrencyDescription);
        settings.AllowedCurrencyGroups.Remove(VendorCurrencyGroup.Gil);
        Assert.NotEqual(after.CacheKey, MaterialPurchaseDisplayContext.Resolve(1, settings, new()).CacheKey);
    }

    [Fact]
    public void ResolvingPreviewNormalizesOnlyDetachedCopies()
    {
        var settings = Settings(VendorCurrencyGroup.Gil, VendorCurrencyGroup.Gil);
        var list = new VendorPurchaseListSettings { Mode = (VendorPurchaseOverrideMode)999 };
        var context = MaterialPurchaseDisplayContext.Resolve(1, settings, list);
        Assert.False(context.Policy.Enabled);
        Assert.Equal((VendorPurchaseOverrideMode)999, list.Mode);
        Assert.Equal(2, settings.AllowedCurrencyGroups.Count);
    }

    [Fact]
    public void SharedSupportRejectsOversizedBundlesAndPreservesExistingRejections()
    {
        Assert.False(IntegratedMaterialOfferSupport.Apply(Gil() with { OutputQuantityPerTransaction = 100 }, 99, false, false).Supported);
        var rejected = Gil() with { Supported = false, RejectionReason = "native reader unavailable" };
        Assert.Equal(rejected, IntegratedMaterialOfferSupport.Apply(rejected, 99, false, false));
        Assert.False(IntegratedMaterialOfferSupport.Apply(Gil() with { ShopType = VendorShopType.SpecialCurrency,
            CurrencyGroup = VendorCurrencyGroup.Other }, 99, false, false).Supported);
    }
}
