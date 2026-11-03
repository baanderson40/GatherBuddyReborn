using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class PolicyTests
{
    [Fact]
    public void LegacyDocumentsDefaultToDisabledGlobalAndInheritedList()
    {
        var global = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>("{}")!;
        var list = JsonConvert.DeserializeObject<VendorPurchaseListSettings>("{}")!;
        Assert.False(global.Enabled);
        Assert.Equal(VendorPurchaseOverrideMode.Inherit, list.Mode);
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(global, list).Enabled);
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(null).Enabled);
    }

    [Fact]
    public void OverrideModesResolveIndependently()
    {
        var global = new AutomaticVendorPurchasePolicy { Enabled = true };
        var list = new VendorPurchaseListSettings();
        Assert.True(ResolvedVendorPurchasePolicy.Resolve(global, list).Enabled);
        list.Mode = VendorPurchaseOverrideMode.Disabled;
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(global, list).Enabled);
        list.Mode = VendorPurchaseOverrideMode.Custom;
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(global, list).Enabled);
        list.CustomPolicy.Enabled = true;
        global.Enabled = false;
        var resolved = ResolvedVendorPurchasePolicy.Resolve(global, list);
        Assert.True(resolved.Enabled);
        Assert.Equal(VendorPurchasePolicyOrigin.ListOverride, resolved.Origin);
    }

    [Fact]
    public void NormalizationFiltersUnknownGroupsAndNeverEnablesPriorityEntries()
    {
        var policy = new AutomaticVendorPurchasePolicy
        {
            Enabled = true,
            AllowedCurrencyGroups = [VendorCurrencyGroup.Gil, VendorCurrencyGroup.Gil, (VendorCurrencyGroup)999],
            CurrencyPriority = [VendorCurrencyGroup.Scrips, VendorCurrencyGroup.Scrips, (VendorCurrencyGroup)999],
            CurrencyReservesByItemId = new() { [0] = 100, [1] = 50 },
        };
        Assert.True(policy.Normalize());
        Assert.False(policy.Normalize());
        Assert.Equal(new[] { VendorCurrencyGroup.Scrips, VendorCurrencyGroup.Gil }, policy.CurrencyPriority);
        Assert.Single(policy.AllowedCurrencyGroups);
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(policy).Allows(VendorCurrencyGroup.Scrips));
        Assert.False(policy.CurrencyReservesByItemId.ContainsKey(0));
    }

    [Fact]
    public void NullCollectionsAndInvalidOverrideAreSafe()
    {
        var policy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(
            "{\"AllowedCurrencyGroups\":null,\"CurrencyPriority\":null,\"CurrencyReservesByItemId\":null}")!;
        Assert.NotNull(policy.CurrencyReservesByItemId);
        Assert.Equal(2, policy.AllowedCurrencyGroups.Count);
        var list = JsonConvert.DeserializeObject<VendorPurchaseListSettings>("{\"Mode\":999,\"CustomPolicy\":null}")!;
        Assert.Equal(VendorPurchaseOverrideMode.Disabled, list.Mode);
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(new() { Enabled = true }, list).Enabled);
    }

    [Fact]
    public void RoundTripPreservesExactCurrencyOrderAllowlistAndReserves()
    {
        var list = new VendorPurchaseListSettings
        {
            Mode = VendorPurchaseOverrideMode.Custom,
            CustomPolicy = new()
            {
                Enabled = true,
                AllowedCurrencyGroups = [VendorCurrencyGroup.GrandCompanySeals],
                CurrencyPriority = [VendorCurrencyGroup.GrandCompanySeals, VendorCurrencyGroup.Gil],
                CurrencyReservesByItemId = new() { [20] = 10, [21] = 30 },
                MaximumGilSpendPerRun = 0,
            },
        };
        var copy = JsonConvert.DeserializeObject<VendorPurchaseListSettings>(JsonConvert.SerializeObject(list))!;
        Assert.Equal(list.Mode, copy.Mode);
        Assert.Equal(list.CustomPolicy.CurrencyPriority, copy.CustomPolicy.CurrencyPriority);
        Assert.Equal(list.CustomPolicy.AllowedCurrencyGroups, copy.CustomPolicy.AllowedCurrencyGroups);
        Assert.Equal(10ul, copy.CustomPolicy.CurrencyReservesByItemId[20]);
        Assert.Equal(30ul, copy.CustomPolicy.CurrencyReservesByItemId[21]);
        Assert.Equal((ulong?)0, copy.CustomPolicy.MaximumGilSpendPerRun);
    }

    [Fact]
    public void SnapshotAndClonesDoNotShareEditableCollections()
    {
        var list = new VendorPurchaseListSettings { Mode = VendorPurchaseOverrideMode.Custom };
        list.CustomPolicy.Enabled = true;
        list.CustomPolicy.CurrencyReservesByItemId[1] = 500;
        var snapshot = ResolvedVendorPurchasePolicy.Resolve(null, list);
        var copy = list.Clone();
        list.CustomPolicy.Enabled = false;
        list.CustomPolicy.AllowedCurrencyGroups.Clear();
        list.CustomPolicy.CurrencyPriority.Reverse();
        list.CustomPolicy.CurrencyReservesByItemId[1] = 1;
        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.Allows(VendorCurrencyGroup.Gil));
        Assert.Equal(VendorCurrencyGroup.Gil, snapshot.CurrencyPriority[0]);
        Assert.Equal(500ul, snapshot.CurrencyReservesByItemId[1]);
        Assert.Equal(500ul, copy.CustomPolicy.CurrencyReservesByItemId[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<VendorCurrencyGroup>)snapshot.CurrencyPriority).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<uint, ulong>)snapshot.CurrencyReservesByItemId).Clear());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0ul)]
    [InlineData(100ul)]
    public void GilCapDistinguishesUnlimitedZeroAndLimit(ulong? cap)
    {
        var policy = new AutomaticVendorPurchasePolicy { MaximumGilSpendPerRun = cap };
        var copy = JsonConvert.DeserializeObject<AutomaticVendorPurchasePolicy>(JsonConvert.SerializeObject(policy));
        Assert.Equal(cap, ResolvedVendorPurchasePolicy.Resolve(copy).MaximumGilSpendPerRun);
    }
}
