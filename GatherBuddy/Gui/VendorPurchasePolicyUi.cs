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
    private sealed record CurrencyRow(uint ItemId, VendorCurrencyGroup Group, string Name, string FullName);
    private static readonly CurrencyRow GilCurrency = new(1, VendorCurrencyGroup.Gil, "Gil", "Gil");
    private static readonly CurrencyRow SealsCurrency = new(20, VendorCurrencyGroup.GrandCompanySeals, "Current-GC seals", "Current Grand Company seals");
    private static readonly CurrencyRow GemstoneCurrency = new(VendorShopResolver.BicolorCurrencyItemId,
        VendorCurrencyGroup.BicolorGemstones, "Bicolor Gemstones", "Bicolor Gemstones");
    // Weak keys avoid retaining deleted lists or replaced custom policies through editing buffers.
    private static readonly ConditionalWeakTable<AutomaticVendorPurchasePolicy, Dictionary<string, AmountBuffer>> Buffers = new();

    public static void DrawGlobal()
    {
        ImGui.PushID("GlobalVendorPurchasePolicy");
        if (DrawPolicy(GatherBuddy.Config.AutomaticVendorPurchases)) GatherBuddy.Config.Save();
        ImGui.PopID();
        ImGui.Spacing();
        ImGui.Separator();
    }

    public static void DrawListSettings(VendorPurchaseListSettings settings)
    {
        var mode = (int)settings.Mode;
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(220f));
        if (ImGui.Combo("Policy", ref mode, new[] { "Use global settings", "Disabled", "Custom settings" }, 3))
        {
            settings.Mode = (VendorPurchaseOverrideMode)mode;
        }
        if (settings.Mode == VendorPurchaseOverrideMode.Custom)
            DrawPolicy(settings.CustomPolicy);
        else if (settings.Mode == VendorPurchaseOverrideMode.Disabled)
            ImGui.TextWrapped("This list will not buy materials, even when global automatic purchases are enabled.");
        else
        {
            var policy = GatherBuddy.Config.AutomaticVendorPurchases;
            ImGui.TextWrapped($"Global purchases: {(policy.Enabled ? "enabled" : "disabled")}; gil purchase cap: {policy.MaximumGilSpendPerRun?.ToString() ?? "unlimited"}. Edit global defaults under Vulcan > Settings.");
        }
        ImGui.TextWrapped("Saved changes apply to the next run. An active or paused run keeps its original policy.");
    }

    private static bool DrawPolicy(AutomaticVendorPurchasePolicy policy)
    {
        var changed = false;
        var enabled = policy.Enabled;
        if (ImGui.Checkbox("Enable automatic vendor purchases", ref enabled))
        { policy.Enabled = enabled; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applies to the next Gather + Craft run. To stop current automation, use the queue's Stop button; changing this setting does not change its captured policy.");
        if (!policy.Enabled)
        {
            ImGui.TextDisabled("Currency permissions and limits appear when enabled.");
            return changed;
        }

        changed |= policy.Normalize();
        var commonCurrencies = new[] { GilCurrency, SealsCurrency, GemstoneCurrency };
        if (ImGui.CollapsingHeader("Gil, seals & gemstones", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawCurrencyTable(policy, "CommonCurrencies", commonCurrencies);
            var currentSeals = Dalamud.ClientState.IsLoggedIn && Dalamud.Objects.LocalPlayer != null
                ? VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId() : 0;
            ImGui.TextDisabled(currentSeals switch
            {
                20 => "Current GC: Maelstrom — Storm Seals",
                21 => "Current GC: Order of the Twin Adder — Serpent Seals",
                22 => "Current GC: Immortal Flames — Flame Seals",
                _ => "Current GC unavailable; seal purchases unavailable.",
            });
        }

        var tomestones = AutomaticVendorCurrencyCatalog.Currencies
            .Where(currency => currency.Group == VendorCurrencyGroup.Tomestones)
            .Select(currency => new CurrencyRow(currency.ItemId, currency.Group,
                currency.ItemId switch { 28 => "Poetics", 48 => "Mathematics", _ => "Mnemonics" }, currency.FallbackName)).ToArray();
        if (ImGui.CollapsingHeader($"Tomestones ({CountEnabled(policy, tomestones)}/{tomestones.Length} enabled)"))
            changed |= DrawCurrencyTable(policy, "Tomestones", tomestones);

        var scrips = AutomaticVendorCurrencyCatalog.Currencies
            .Where(currency => currency.Group == VendorCurrencyGroup.Scrips)
            .Select(currency => new CurrencyRow(currency.ItemId, currency.Group,
                currency.ItemId switch
                {
                    33913 => "Purple Crafter",
                    33914 => "Purple Gatherer",
                    41784 => "Orange Crafter",
                    _ => "Orange Gatherer",
                }, currency.FallbackName)).ToArray();
        if (ImGui.CollapsingHeader($"Scrips ({CountEnabled(policy, scrips)}/{scrips.Length} enabled)"))
            changed |= DrawCurrencyTable(policy, "Scrips", scrips);

        if (ImGui.CollapsingHeader("Spending priority"))
        {
            ImGui.TextDisabled("Higher currencies spend first. Scrips and tomestones are ordered by group.");
            for (var i = 0; i < policy.CurrencyPriority.Count; i++)
            {
                var group = policy.CurrencyPriority[i];
                if (group is not (VendorCurrencyGroup.Gil or VendorCurrencyGroup.GrandCompanySeals or VendorCurrencyGroup.BicolorGemstones
                    or VendorCurrencyGroup.Scrips or VendorCurrencyGroup.Tomestones)) continue;
                if (!PolicyAllowsGroup(policy, group)) continue;
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
                ImGui.TextUnformatted(GroupName(group));
                ImGui.PopID();
            }
        }
        if (!commonCurrencies.Any(currency => IsAllowed(policy, currency)) && policy.AllowedCurrencyItemIds.Count == 0)
            ImGui.TextWrapped("No supported purchase currency allowed; materials will fall back to gathering or shortage reporting.");

        ImGui.TextDisabled("Actual balances used. Reserves protected; travel gil is additional.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Run caps limit purchase spending only; teleport fees are additional. The materials window's selected currency and temporary Debug controls do not affect purchasing. Unaffordable gatherable materials fall back to gathering; other shortages remain reported. Changes apply to the next run.");
        return changed;
    }

    private static bool DrawCurrencyTable(AutomaticVendorPurchasePolicy policy, string tableId, IReadOnlyList<CurrencyRow> rows)
    {
        var changed = false;
        ImGui.PushID(tableId);
        if (ImGui.BeginTable("##CurrencySettings", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(38f));
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Reserve", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(100f));
            ImGui.TableSetupColumn("Run cap", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(118f));
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var column = 0; column < 4; column++)
            {
                ImGui.TableSetColumnIndex(column);
                if (column == 0) ImGui.TextDisabled("Use");
                else if (column == 1) ImGui.TextDisabled("Currency");
                else if (column == 2) ImGui.TextDisabled("Reserve");
                else ImGui.TextDisabled("Run cap");
            }
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var allowed = IsAllowed(policy, row);
                ImGui.PushID((int)row.ItemId);
                if (ImGui.Checkbox("##Allowed", ref allowed))
                {
                    if (row.Group is VendorCurrencyGroup.Gil or VendorCurrencyGroup.GrandCompanySeals or VendorCurrencyGroup.BicolorGemstones)
                        VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, row.Group, allowed);
                    else
                        VendorPurchasePolicyEditing.SetCurrencyAllowed(policy, row.ItemId, allowed);
                    changed = true;
                }
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.FullName);
                ImGui.TableSetColumnIndex(2);
                var reserve = GetReserve(policy, row);
                if (DrawAmount(policy, $"reserve-{row.ItemId}", ref reserve, enabled: allowed))
                {
                    SetReserve(policy, row, reserve);
                    changed = true;
                }
                ImGui.TableSetColumnIndex(3);
                changed |= DrawSpendingCap(policy, row, allowed);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.PopID();
        return changed;
    }

    private static bool DrawSpendingCap(AutomaticVendorPurchasePolicy policy, CurrencyRow row, bool currencyEnabled)
    {
        var changed = false;
        var limited = GetCap(policy, row).HasValue;
        ImGui.BeginDisabled(!currencyEnabled);
        ImGui.PushID($"cap-{row.ItemId}");
        if (ImGui.Checkbox("##Limit", ref limited))
        {
            SetCap(policy, row, limited ? 1000ul : null);
            if (Buffers.TryGetValue(policy, out var fields)) fields.Remove($"cap-value-{row.ItemId}");
            changed = true;
        }
        ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
        if (limited)
        {
            var cap = GetCap(policy, row) ?? 0;
            if (DrawAmount(policy, $"cap-value-{row.ItemId}", ref cap, enabled: currencyEnabled))
            {
                SetCap(policy, row, cap);
                changed = true;
            }
        }
        else
        {
            ImGui.TextDisabled("Unlimited");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"No per-run {row.Name} spending limit; reserve still applies.");
        }
        ImGui.PopID();
        ImGui.EndDisabled();
        return changed;
    }

    private static bool DrawAmount(AutomaticVendorPurchasePolicy policy, string key, ref ulong value, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        var fields = Buffers.GetOrCreateValue(policy);
        if (!fields.TryGetValue(key, out var buffer) || buffer.Applied != value)
            fields[key] = buffer = new(value);
        ImGui.SetNextItemWidth(-1);
        var edited = ImGui.InputText("##Amount", ref buffer.Text, 32);
        if (!VendorPurchasePolicyEditing.TryParseAmount(buffer.Text, out var parsed))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.6f, 0.2f, 1));
            ImGui.SameLine(0, VulcanUiScaling.Scaled(3f));
            ImGui.TextUnformatted("!");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Invalid amount. Saved value remains {value}. Use digits only (0–{ulong.MaxValue}).");
            ImGui.PopStyleColor();
            ImGui.EndDisabled();
            return false;
        }
        if (!edited || parsed == value)
        {
            ImGui.EndDisabled();
            return false;
        }
        buffer.Applied = value = parsed;
        ImGui.EndDisabled();
        return true;
    }

    private static int CountEnabled(AutomaticVendorPurchasePolicy policy, IEnumerable<CurrencyRow> rows)
        => rows.Count(row => IsAllowed(policy, row));

    private static bool IsAllowed(AutomaticVendorPurchasePolicy policy, CurrencyRow row)
        => row.Group is VendorCurrencyGroup.Gil or VendorCurrencyGroup.GrandCompanySeals or VendorCurrencyGroup.BicolorGemstones
            ? policy.AllowedCurrencyGroups.Contains(row.Group)
            : policy.AllowedCurrencyItemIds.Contains(row.ItemId);

    private static bool PolicyAllowsGroup(AutomaticVendorPurchasePolicy policy, VendorCurrencyGroup group)
        => group is VendorCurrencyGroup.Scrips or VendorCurrencyGroup.Tomestones
            ? AutomaticVendorCurrencyCatalog.Currencies.Any(currency => currency.Group == group && policy.AllowedCurrencyItemIds.Contains(currency.ItemId))
            : policy.AllowedCurrencyGroups.Contains(group);

    private static ulong GetReserve(AutomaticVendorPurchasePolicy policy, CurrencyRow row)
        => row.Group == VendorCurrencyGroup.GrandCompanySeals
            ? policy.GrandCompanySealReserve ?? 0
            : policy.CurrencyReservesByItemId.GetValueOrDefault(row.ItemId);

    private static void SetReserve(AutomaticVendorPurchasePolicy policy, CurrencyRow row, ulong value)
    {
        if (row.Group == VendorCurrencyGroup.GrandCompanySeals) policy.GrandCompanySealReserve = value;
        else policy.CurrencyReservesByItemId[row.ItemId] = value;
    }

    private static ulong? GetCap(AutomaticVendorPurchasePolicy policy, CurrencyRow row)
        => row.Group == VendorCurrencyGroup.Gil
            ? policy.MaximumGilSpendPerRun
            : policy.MaximumSpendPerRunByCurrencyItemId.TryGetValue(row.ItemId, out var cap) ? cap : null;

    private static void SetCap(AutomaticVendorPurchasePolicy policy, CurrencyRow row, ulong? cap)
    {
        if (row.Group == VendorCurrencyGroup.Gil) policy.MaximumGilSpendPerRun = cap;
        else if (cap.HasValue) policy.MaximumSpendPerRunByCurrencyItemId[row.ItemId] = cap.Value;
        else policy.MaximumSpendPerRunByCurrencyItemId.Remove(row.ItemId);
    }

    private static string GroupName(VendorCurrencyGroup group) => group switch
    {
        VendorCurrencyGroup.Gil => "Gil",
        VendorCurrencyGroup.GrandCompanySeals => "Current-GC seals",
        VendorCurrencyGroup.BicolorGemstones => "Bicolor Gemstones",
        VendorCurrencyGroup.Scrips => "Scrips",
        _ => "Tomestones",
    };
}
