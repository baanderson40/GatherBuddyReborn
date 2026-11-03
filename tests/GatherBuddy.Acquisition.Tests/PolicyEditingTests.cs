using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class PolicyEditingTests
{
    [Theory]
    [InlineData("0", 0ul)]
    [InlineData("1000", 1000ul)]
    [InlineData("00010", 10ul)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void WholeAmountsKeepExactUnsignedValues(string text, ulong expected)
    {
        Assert.True(VendorPurchasePolicyEditing.TryParseAmount(text, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,000")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("18446744073709551616")]
    public void InvalidAmountsDoNotWrapClampOrParsePartially(string text)
        => Assert.False(VendorPurchasePolicyEditing.TryParseAmount(text, out _));

    [Fact]
    public void AllowlistEditsPreserveUnrelatedGroupsReservesCapAndEnablement()
    {
        var policy = new AutomaticVendorPurchasePolicy
        {
            AllowedCurrencyGroups = [VendorCurrencyGroup.Gil, VendorCurrencyGroup.Scrips],
            CurrencyReservesByItemId = new() { [1] = 500, [20] = 400, [999] = ulong.MaxValue },
            MaximumGilSpendPerRun = 0,
        };
        VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.Gil, false);
        VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.GrandCompanySeals, true);
        VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.GrandCompanySeals, true);
        Assert.Equal(new[] { VendorCurrencyGroup.Scrips, VendorCurrencyGroup.GrandCompanySeals }, policy.AllowedCurrencyGroups);
        Assert.False(policy.Enabled);
        Assert.Equal(0ul, policy.MaximumGilSpendPerRun);
        Assert.Equal(500ul, policy.CurrencyReservesByItemId[1]);
        Assert.Equal(400ul, policy.GrandCompanySealReserve);
        Assert.False(policy.CurrencyReservesByItemId.ContainsKey(20));
        Assert.Equal(ulong.MaxValue, policy.CurrencyReservesByItemId[999]);
    }

    [Fact]
    public void PriorityEditsSwapOnlySelectedCurrencies()
    {
        var policy = new AutomaticVendorPurchasePolicy
        {
            CurrencyPriority = [VendorCurrencyGroup.Scrips, VendorCurrencyGroup.Gil, VendorCurrencyGroup.Other, VendorCurrencyGroup.GrandCompanySeals],
        };
        VendorPurchasePolicyEditing.SetGrandCompanyFirst(policy, true);
        Assert.Equal(new[] { VendorCurrencyGroup.Scrips, VendorCurrencyGroup.GrandCompanySeals, VendorCurrencyGroup.Other, VendorCurrencyGroup.Gil }, policy.CurrencyPriority);
        VendorPurchasePolicyEditing.SetGrandCompanyFirst(policy, false);
        Assert.Equal(new[] { VendorCurrencyGroup.Scrips, VendorCurrencyGroup.Gil, VendorCurrencyGroup.Other, VendorCurrencyGroup.GrandCompanySeals }, policy.CurrencyPriority);
    }

    [Fact]
    public void PriorityDoesNotAuthorizeCurrenciesOrEnablePurchasing()
    {
        var policy = new AutomaticVendorPurchasePolicy { AllowedCurrencyGroups = [], CurrencyPriority = [] };
        VendorPurchasePolicyEditing.SetGrandCompanyFirst(policy, true);
        Assert.Empty(policy.AllowedCurrencyGroups);
        Assert.False(policy.Enabled);
        Assert.Equal(new[] { VendorCurrencyGroup.GrandCompanySeals, VendorCurrencyGroup.Gil }, policy.CurrencyPriority);
    }

    [Fact]
    public void UiEditsDoNotChangeExistingRunSnapshotOrGlobalPolicy()
    {
        var global = new AutomaticVendorPurchasePolicy { Enabled = true };
        var list = new VendorPurchaseListSettings { Mode = VendorPurchaseOverrideMode.Custom, CustomPolicy = global.Clone() };
        var snapshot = ResolvedVendorPurchasePolicy.Resolve(global, list);
        VendorPurchasePolicyEditing.SetCurrencyAllowed(list.CustomPolicy, VendorCurrencyGroup.Gil, false);
        VendorPurchasePolicyEditing.SetGrandCompanyFirst(list.CustomPolicy, true);
        list.CustomPolicy.MaximumGilSpendPerRun = 1000;
        Assert.Contains(VendorCurrencyGroup.Gil, global.AllowedCurrencyGroups);
        Assert.True(snapshot.Allows(VendorCurrencyGroup.Gil));
        Assert.Equal(VendorCurrencyGroup.Gil, snapshot.CurrencyPriority[0]);
        Assert.Null(snapshot.MaximumGilSpendPerRun);
        Assert.False(ResolvedVendorPurchasePolicy.Resolve(global, list).Allows(VendorCurrencyGroup.Gil));
    }
}
