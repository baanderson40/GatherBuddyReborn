using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Gui;

internal static class VendorPurchasePolicyUi
{
    private sealed class AmountBuffer(ulong value)
    {
        public ulong Applied = value;
        public string Text = value.ToString(CultureInfo.InvariantCulture);
    }
    // Weak keys avoid retaining deleted lists or replaced custom policies through editing buffers.
    private static readonly ConditionalWeakTable<AutomaticVendorPurchasePolicy, Dictionary<string, AmountBuffer>> Buffers = new();

    public static void DrawGlobal()
    {
        if (!ImGui.CollapsingHeader("Automatic Vendor Purchases", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.PushID("GlobalVendorPurchasePolicy");
        if (DrawPolicy(GatherBuddy.Config.AutomaticVendorPurchases)) GatherBuddy.Config.Save();
        ImGui.PopID();
        ImGui.Spacing();
        ImGui.Separator();
    }

    public static void DrawList(CraftingListDefinition list)
    {
        ImGui.PushID(list.ID);
        if (ImGui.Button("Automatic Vendor Purchases...")) ImGui.OpenPopup("ListVendorPurchasePolicy");
        if (ImGui.IsPopupOpen("ListVendorPurchasePolicy"))
            ImGui.SetNextWindowSizeConstraints(new Vector2(VulcanUiScaling.Scaled(350f), 0),
                new Vector2(VulcanUiScaling.Scaled(650f), ImGui.GetMainViewport().WorkSize.Y * 0.8f));
        if (ImGui.BeginPopup("ListVendorPurchasePolicy"))
        {
            ImGui.TextUnformatted("Automatic Vendor Purchases — This List");
            ImGui.Separator();
            var settings = list.VendorPurchases;
            var changed = false;
            var mode = (int)settings.Mode;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(220f));
            if (ImGui.Combo("Policy", ref mode, new[] { "Use global settings", "Disabled", "Custom settings" }, 3))
            {
                settings.Mode = (VendorPurchaseOverrideMode)mode;
                changed = true;
            }
            if (settings.Mode == VendorPurchaseOverrideMode.Custom)
                changed |= DrawPolicy(settings.CustomPolicy);
            else if (settings.Mode == VendorPurchaseOverrideMode.Disabled)
                ImGui.TextWrapped("This list will not buy materials, even when global automatic purchases are enabled.");
            else
            {
                var policy = GatherBuddy.Config.AutomaticVendorPurchases;
                ImGui.TextWrapped($"Global purchases: {(policy.Enabled ? "enabled" : "disabled")}; gil purchase cap: {policy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}. Edit global defaults under Vulcan > Settings.");
            }
            ImGui.TextWrapped("Saved changes apply to the next run. An active or paused run keeps its original policy.");
            if (changed) GatherBuddy.CraftingListManager.SaveList(list);
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }

    private static bool DrawPolicy(AutomaticVendorPurchasePolicy policy)
    {
        ImGui.TextWrapped("Enabling this authorizes real material purchases when Gather + Craft starts. Actual balances and this policy are used, not the material window's selected currency or temporary Debug controls.");
        ImGui.TextWrapped("Gil caps/reserves cover purchases only; teleport fees are additional. Unaffordable gatherable materials fall back to gathering. Remaining shortages are reported and retained.");
        var changed = false;
        var enabled = policy.Enabled;
        if (ImGui.Checkbox("Enable automatic vendor purchases", ref enabled))
        { policy.Enabled = enabled; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applies to the next Gather + Craft run. To stop current automation, use the queue's Stop button; changing this setting does not change its captured policy.");
        // Leave limits editable while disabled so users can configure protections before enabling spending.
        var gil = policy.AllowedCurrencyGroups.Contains(VendorCurrencyGroup.Gil);
        if (ImGui.Checkbox("Allow gil", ref gil))
        { VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.Gil, gil); changed = true; }
        var seals = policy.AllowedCurrencyGroups.Contains(VendorCurrencyGroup.GrandCompanySeals);
        if (ImGui.Checkbox("Allow current-GC seals", ref seals))
        { VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.GrandCompanySeals, seals); changed = true; }
        var gemstones = policy.AllowedCurrencyGroups.Contains(VendorCurrencyGroup.BicolorGemstones);
        if (ImGui.Checkbox("Allow Bicolor Gemstones", ref gemstones))
        { VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, VendorCurrencyGroup.BicolorGemstones, gemstones); changed = true; }
        changed |= policy.Normalize();
        ImGui.TextUnformatted("Currency priority (top spends first)");
        for (var i = 0; i < policy.CurrencyPriority.Count; i++)
        {
            var group = policy.CurrencyPriority[i];
            if (group is not (VendorCurrencyGroup.Gil or VendorCurrencyGroup.GrandCompanySeals or VendorCurrencyGroup.BicolorGemstones)) continue;
            ImGui.PushID(i);
            ImGui.BeginDisabled(i == 0);
            if (ImGui.SmallButton("Up") && i > 0)
            { (policy.CurrencyPriority[i - 1], policy.CurrencyPriority[i]) = (policy.CurrencyPriority[i], policy.CurrencyPriority[i - 1]); changed = true; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i + 1 >= policy.CurrencyPriority.Count);
            if (ImGui.SmallButton("Down") && i + 1 < policy.CurrencyPriority.Count)
            { (policy.CurrencyPriority[i + 1], policy.CurrencyPriority[i]) = (policy.CurrencyPriority[i], policy.CurrencyPriority[i + 1]); changed = true; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(group switch { VendorCurrencyGroup.Gil => "Gil", VendorCurrencyGroup.GrandCompanySeals => "Current-GC seals", _ => "Bicolor Gemstones" });
            ImGui.PopID();
        }
        if (!gil && !seals && !gemstones) ImGui.TextWrapped("No supported purchase currency allowed; materials will fall back to gathering or shortage reporting.");

        changed |= DrawReserve(policy, "Gil reserve", 1);
        changed |= DrawReserve(policy, "Bicolor Gemstone reserve", VendorShopResolver.BicolorCurrencyItemId);
        // Normalize legacy settings before editing, including explicit zero handling.
        changed |= policy.Normalize();
        var sealReserve = policy.GrandCompanySealReserve ?? 0;
        if (DrawAmount(policy, "GC seal reserve", ref sealReserve))
        { policy.GrandCompanySealReserve = sealReserve; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("One reserve follows your current Grand Company. Other-GC seals cannot fund automatic purchases. Changes apply to the next run.");
        var currentSeals = Dalamud.ClientState.IsLoggedIn && Dalamud.Objects.LocalPlayer != null
            ? VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() : 0;
        ImGui.TextWrapped(currentSeals switch
        {
            20 => "Current GC: Maelstrom — Storm Seals",
            21 => "Current GC: Order of the Twin Adder — Serpent Seals",
            22 => "Current GC: Immortal Flames — Flame Seals",
            _ => "Current GC unavailable or unaffiliated. Seal purchases unavailable; reserve remains editable.",
        });

        var limited = policy.MaximumGilSpendPerRun.HasValue;
        if (ImGui.Checkbox("Limit gil purchases per run", ref limited))
        {
            policy.MaximumGilSpendPerRun = limited ? 1000ul : null;
            if (Buffers.TryGetValue(policy, out var fields)) fields.Remove("Maximum gil purchase spend");
            changed = true;
        }
        if (policy.MaximumGilSpendPerRun is { } cap)
        {
            if (DrawAmount(policy, "Maximum gil purchase spend", ref cap))
            { policy.MaximumGilSpendPerRun = cap; changed = true; }
            ImGui.TextWrapped("Zero forbids gil purchases. GC seals and gemstones use their own allowlist/reserves, not this gil cap. A mixed-cost exchange must satisfy every cost's limits.");
        }
        else ImGui.TextWrapped("Gil purchase spending is unlimited (reserves still apply). Set a low cap for initial testing.");
        ImGui.TextWrapped("Stackable gil/GC materials and validated gemstone exchanges. Bundles may buy the minimum excess needed; every cost must be authorized and within reserves/budgets. Locked or unverifiable shops remain shortages. Changes apply to future runs, not an active or resumed run.");
        return changed;
    }

    private static bool DrawReserve(AutomaticVendorPurchasePolicy policy, string label, uint currency)
    {
        var reserve = policy.CurrencyReservesByItemId.GetValueOrDefault(currency);
        if (!DrawAmount(policy, label, ref reserve)) return false;
        policy.CurrencyReservesByItemId[currency] = reserve;
        return true;
    }

    private static bool DrawAmount(AutomaticVendorPurchasePolicy policy, string label, ref ulong value)
    {
        var fields = Buffers.GetOrCreateValue(policy);
        if (!fields.TryGetValue(label, out var buffer) || buffer.Applied != value)
            fields[label] = buffer = new(value);
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(200f));
        var edited = ImGui.InputText(label, ref buffer.Text, 32);
        if (!VendorPurchasePolicyEditing.TryParseAmount(buffer.Text, out var parsed))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.6f, 0.2f, 1));
            ImGui.TextWrapped($"Invalid amount. Saved value remains {value}. Use digits only (0–{ulong.MaxValue}).");
            ImGui.PopStyleColor();
            return false;
        }
        if (!edited || parsed == value) return false;
        buffer.Applied = value = parsed;
        return true;
    }
}
