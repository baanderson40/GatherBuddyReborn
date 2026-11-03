using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class GrandCompanyReserveTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public void LegacyCurrencyMigratesWithoutChangingUnrelatedReserves(int currency)
    {
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(
            $"{{\"CurrencyReservesByItemId\":{{\"{currency}\":1234,\"1\":99,\"999\":88}}}}")!;
        Assert.Equal(1234ul, policy.GrandCompanySealReserve);
        Assert.Equal(99ul, policy.CurrencyReservesByItemId[1]);
        Assert.Equal(88ul, policy.CurrencyReservesByItemId[999]);
        Assert.False(policy.CurrencyReservesByItemId.ContainsKey((uint)currency));
        Assert.False(policy.Normalize());
    }

    [Fact]
    public void DifferentLegacyValuesUseMaximumWithBoundedDiagnostic()
    {
        var policy = new AutomaticVendorPurchasePolicy { CurrencyReservesByItemId = new() { [20] = 10, [21] = 500, [22] = 20 } };
        var trace = new AcquisitionTrace(Guid.NewGuid());
        Assert.True(policy.Normalize(trace));
        Assert.Equal(500ul, policy.GrandCompanySealReserve);
        Assert.Empty(policy.CurrencyReservesByItemId);
        Assert.Single(trace.Events, e => e.Action == "MigrateSealReserve" && e.Level == AcquisitionTraceLevel.Warning);
        Assert.False(policy.Normalize(trace));
        Assert.Single(trace.Events, e => e.Action == "MigrateSealReserve");
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(50ul)]
    public void ExplicitNewValueWinsIncludingZero(ulong reserve)
    {
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(
            $"{{\"GrandCompanySealReserve\":{reserve},\"CurrencyReservesByItemId\":{{\"20\":9000}}}}")!;
        Assert.Equal(reserve, policy.GrandCompanySealReserve);
        Assert.Empty(policy.CurrencyReservesByItemId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"GrandCompanySealReserve\":null,\"CurrencyReservesByItemId\":null}")]
    public void MissingSettingsDefaultToZero(string json)
    {
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(json)!;
        Assert.Equal(0ul, policy.GrandCompanySealReserve);
        Assert.Empty(policy.CurrencyReservesByItemId);
    }

    [Fact]
    public void ExportMigratesLegacyAndRoundTripsUnifiedValue()
    {
        var list = new VendorPurchaseListSettings { Mode = VendorPurchaseOverrideMode.Custom,
            CustomPolicy = new() { GrandCompanySealReserve = null, CurrencyReservesByItemId = new() { [22] = 3000 } } };
        var json = JsonConvert.SerializeObject(list);
        Assert.Contains("\"GrandCompanySealReserve\":3000", json);
        Assert.DoesNotContain("\"22\":", json);
        var imported = JsonConvert.DeserializeObject<VendorPurchaseListSettings>(json)!;
        Assert.Equal(3000ul, imported.CustomPolicy.GrandCompanySealReserve);
    }

    [Fact]
    public void OverridesAndRunSnapshotsStayDetached()
    {
        var global = new AutomaticVendorPurchasePolicy { Enabled = true, GrandCompanySealReserve = 1000 };
        var list = new VendorPurchaseListSettings { Mode = VendorPurchaseOverrideMode.Custom,
            CustomPolicy = new() { Enabled = true, GrandCompanySealReserve = 2000 } };
        var resolved = ResolvedVendorPurchasePolicy.Resolve(global, list);
        var clone = list.Clone();
        list.CustomPolicy.GrandCompanySealReserve = 0;
        global.GrandCompanySealReserve = 10;
        Assert.Equal(2000ul, resolved.GrandCompanySealReserve);
        Assert.Equal(2000ul, clone.CustomPolicy.GrandCompanySealReserve);
        list.Mode = VendorPurchaseOverrideMode.Inherit;
        Assert.Equal(10ul, ResolvedVendorPurchasePolicy.Resolve(global, list).GrandCompanySealReserve);
        list.Mode = VendorPurchaseOverrideMode.Disabled;
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(global, list).Enabled);
    }

    [Theory]
    [InlineData(20u, 20u, true)]
    [InlineData(21u, 21u, true)]
    [InlineData(22u, 22u, true)]
    [InlineData(20u, 21u, false)]
    [InlineData(22u, 20u, false)]
    [InlineData(20u, 0u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(99u, 99u, false)]
    public void OnlyKnownCurrentGcCurrencyIsEligible(uint offered, uint current, bool expected)
        => Assert.Equal(expected, GrandCompanySealPolicy.IsCurrentCurrency(offered, current));

    [Theory]
    [InlineData(20u, 900ul, 0u)]
    [InlineData(21u, 1000ul, 0u)]
    [InlineData(22u, 1250ul, 2u)]
    public void PlannerProtectsUnifiedReserveForEveryGc(uint currency, ulong balance, uint expected)
    {
        var policy = ResolvedVendorPurchasePolicy.Resolve(new AutomaticVendorPurchasePolicy
            { Enabled = true, GrandCompanySealReserve = 1000 });
        var plan = Plan(policy, currency, currency, balance);
        Assert.Equal(expected, plan.Allocations.Aggregate(0u, (sum, a) => sum + a.RequestedOutputQuantity));
        Assert.Equal(1000ul, policy.GetReserve(currency));
        Assert.Equal(0ul, policy.GetReserve(1));
    }

    [Theory]
    [InlineData(20u, 21u)]
    [InlineData(22u, 0u)]
    public void OtherGcOrUnknownMembershipCannotSpendEvenWithBalance(uint offered, uint current)
    {
        var policy = ResolvedVendorPurchasePolicy.Resolve(new AutomaticVendorPurchasePolicy
            { Enabled = true, GrandCompanySealReserve = 0 });
        var plan = Plan(policy, offered, current, 99999);
        Assert.Empty(plan.Allocations);
        Assert.Equal(5, Assert.Single(plan.Materials).UnresolvedDemand.Total);
    }

    [Fact]
    public void MembershipChangeRejectsStaleCurrencyAndNewPlanKeepsCapturedReserve()
    {
        var settings = new AutomaticVendorPurchasePolicy { Enabled = true, GrandCompanySealReserve = 1000 };
        var policy = ResolvedVendorPurchasePolicy.Resolve(settings);
        Assert.Equal(2u, Assert.Single(Plan(policy, 20, 20, 1250).Allocations).RequestedOutputQuantity);
        Assert.Empty(Plan(policy, 20, 21, 99999).Allocations);
        settings.GrandCompanySealReserve = 0; // Editing settings does not weaken active snapshot.
        Assert.Equal(2u, Assert.Single(Plan(policy, 21, 21, 1250).Allocations).RequestedOutputQuantity);
        Assert.Equal(1000ul, policy.GetReserve(21));
    }

    private static MaterialAcquisitionPlan Plan(ResolvedVendorPurchasePolicy policy, uint offered, uint current, ulong balance)
    {
        var eligible = GrandCompanySealPolicy.IsCurrentCurrency(offered, current);
        var offer = new AcquisitionVendorOffer(100, offered, VendorCurrencyGroup.GrandCompanySeals, 100, 1,
            VendorShopType.GrandCompanySeals, new(10, "Vendor", 20, VendorMenuShopType.GrandCompanyShop),
            Access: eligible ? VendorOfferAccess.Unknown : VendorOfferAccess.Inaccessible,
            RejectionKind: eligible ? null : AcquisitionOfferRejectionKind.WrongGrandCompany);
        return MaterialAcquisitionPlanner.Plan(new(Guid.NewGuid(), policy,
            new[] { new AcquisitionMaterialDemand(100, IngredientQualityDemand.FromPreferNQ(5), false) }, new[] { offer },
            new Dictionary<uint, AcquisitionInventory>(), new Dictionary<uint, ulong> { [offered] = balance },
            new(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()), VendorOfferDataStatus.Ready));
    }
}
