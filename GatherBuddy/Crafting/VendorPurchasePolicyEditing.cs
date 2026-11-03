using System;
using System.Globalization;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

/// <summary>Focused edits preserve unrelated currencies, priorities, and reserves.</summary>
public static class VendorPurchasePolicyEditing
{
    public static bool TryParseAmount(string text, out ulong amount)
        => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out amount);

    public static void SetCurrencyAllowed(AutomaticVendorPurchasePolicy policy, VendorCurrencyGroup group, bool allowed)
    {
        if (!Enum.IsDefined(group)) throw new ArgumentOutOfRangeException(nameof(group));
        policy.Normalize();
        policy.AllowedCurrencyGroups.RemoveAll(value => value == group);
        if (allowed) policy.AllowedCurrencyGroups.Add(group);
        policy.Normalize();
    }

    public static void SetGrandCompanyFirst(AutomaticVendorPurchasePolicy policy, bool sealsFirst)
    {
        policy.Normalize();
        var priority = policy.CurrencyPriority;
        if (!priority.Contains(VendorCurrencyGroup.Gil)) priority.Add(VendorCurrencyGroup.Gil);
        if (!priority.Contains(VendorCurrencyGroup.GrandCompanySeals)) priority.Add(VendorCurrencyGroup.GrandCompanySeals);
        var gil = priority.IndexOf(VendorCurrencyGroup.Gil);
        var seals = priority.IndexOf(VendorCurrencyGroup.GrandCompanySeals);
        if ((seals < gil) != sealsFirst) (priority[gil], priority[seals]) = (priority[seals], priority[gil]);
    }
}
