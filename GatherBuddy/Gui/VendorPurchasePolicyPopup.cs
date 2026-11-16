using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using RebornMaterial;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class VendorPurchasePolicyPopup
{
    private CraftingListDefinition? _list;
    private VendorPurchaseListSettings _workingSettings = new();

    internal static RebornUI.HostedPanelWindow CreateWindow()
        => new("GatherBuddyListVendorPurchases", "Automatic Vendor Purchases", new Vector2(560f, 620f),
            new Vector2(460f, 320f), bodyScrolls: false);

    public void Open(CraftingListDefinition list)
    {
        _list = list;
        _workingSettings = list.VendorPurchases.Clone();
        GatherBuddy.ListVendorPurchaseWindow?.Show(this, DrawContents, () => list.Name);
    }

    private void Close()
        => GatherBuddy.ListVendorPurchaseWindow?.Hide(this);

    private void DrawContents()
    {
        var footer = M3Widgets.ButtonHeight + M3.Space1 + ImGui.GetStyle().ItemSpacing.Y * 2f;
        using (var body = ImRaii.Child("##listVendorPurchaseBody", new Vector2(-1, -footer), false))
        {
            if (body)
            {
                using var card = M3Card.Begin("##listVendorPurchaseSettings", "Purchase policy",
                    FontAwesomeIcon.ShoppingCart, M3.AccentFor(0));
                VendorPurchasePolicyUi.DrawListSettings(_workingSettings);
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        if (M3Widgets.Button("##saveVendorPurchasePolicy", "Save", M3ButtonStyle.Filled, FontAwesomeIcon.Save))
        {
            if (_list != null)
            {
                _list.VendorPurchases = _workingSettings.Clone();
                GatherBuddy.CraftingListManager.SaveList(_list);
                M3Snackbar.Show($"Saved vendor purchases for {_list.Name}.");
            }
            Close();
        }

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##cancelVendorPurchasePolicy", "Cancel", M3ButtonStyle.Text))
            Close();
    }
}
