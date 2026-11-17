using Dalamud.Game.Addon.Lifecycle;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Crafting;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vector2 = System.Numerics.Vector2;

namespace GatherBuddy.Gui;

/// <summary>Adds addon-owned Craft All controls to the regular RecipeNote.</summary>
internal sealed unsafe class CraftingLogCraftControls : IDisposable
{
    private const string AddonName = "RecipeNote";
    private const int SynthesisButtonNodeId = 104;
    private const int CraftButtonNodeId = 100000;
    private const int QuantityNodeId = 100001;
    private const float HorizontalExpansion = 80f;
    private const float VerticalExpansion = 64f;
    private const float QuantityWidth = 140f;

    private readonly AddonController _controller;
    private readonly List<NodeSnapshot> _originalNodes = [];
    private AtkUnitBase* _addon;
    private TextButtonNode? _craftButton;
    private NumericInputNode? _quantityInput;
    private uint _selectedRecipeId;
    private int _craftableCount;
    private ushort _originalAddonWidth;
    private ushort _originalAddonHeight;
    private bool _layoutExpanded;
    private bool _disposed;

    private readonly record struct NodeSnapshot(uint Id, float X, float Y, ushort Width, ushort Height);

    public CraftingLogCraftControls()
    {
        _controller = new AddonController
        {
            AddonName = AddonName,
            OnSetup = Setup,
            OnRefresh = Refresh,
            OnUpdate = Update,
            OnFinalize = Finalize,
        };
        _controller.Enable();
    }

    private void Setup(AtkUnitBase* addon)
    {
        if (_disposed) return;
        RemoveControls(addon, restoreLayout: true);
        if (!GatherBuddy.Config.ShowCraftingLogCraftControls || ArtisanIsLoaded()) return;

        var synthesisButton = addon->GetNodeById(SynthesisButtonNodeId);
        var synthesisComponent = synthesisButton == null ? null : synthesisButton->GetAsAtkComponentButton();
        var synthBackground = synthesisComponent == null || synthesisComponent->ButtonBGNode == null
            ? null
            : synthesisComponent->ButtonBGNode->GetAsAtkNineGridNode();
        var quantityText = addon->GetTextNodeById(78);
        if (addon->RootNode == null || synthesisButton == null || synthBackground == null || quantityText == null)
            return;

        if (!TryResizeForControls(addon, synthesisButton))
            return;

        _addon = addon;
        _selectedRecipeId = GetSelectedRecipeId();
        ReadCraftableCount(quantityText);

        var buttonPosition = new Vector2(synthesisButton->X,
            synthesisButton->Y + VerticalExpansion * 0.5f);
        var buttonSize = new Vector2(synthBackground->Width, synthBackground->Height);
        _craftButton = new TextButtonNode
        {
            Position = buttonPosition,
            Size = buttonSize,
            String = GetButtonLabel(),
            IsEnabled = _craftableCount > 0 && !CraftingGatherBridge.HasActiveCraftingLogQueue,
            TextTooltip = "Start Vulcan crafting with current inventory only. No gathering, vendor purchases, or retainer restock.",
            NodeId = CraftButtonNodeId,
            OnClick = StartCrafting,
        };
        _craftButton.AttachNode(synthesisButton, NodePosition.AfterTarget);

        _quantityInput = new NumericInputNode
        {
            Position = new Vector2(buttonPosition.X - QuantityWidth, buttonPosition.Y + 5f),
            Size = new Vector2(QuantityWidth, 28f),
            Max = _craftableCount,
            Step = 1,
            IsEnabled = _craftableCount > 0 && !CraftingGatherBridge.HasActiveCraftingLogQueue,
            NodeId = QuantityNodeId,
            OnValueUpdate = _ => UpdateButton(),
        };
        _quantityInput.AttachNode(_craftButton, NodePosition.AfterTarget);
        UpdateButton();
    }

    private void Refresh(AtkUnitBase* addon)
    {
        if (_disposed) return;
        if (!GatherBuddy.Config.ShowCraftingLogCraftControls || ArtisanIsLoaded())
        {
            RemoveControls(addon, restoreLayout: true);
            return;
        }

        if (_craftButton == null || _quantityInput == null)
        {
            Setup(addon);
            return;
        }

        _addon = addon;
        var selectedRecipeId = GetSelectedRecipeId();
        if (_selectedRecipeId != selectedRecipeId)
        {
            _selectedRecipeId = selectedRecipeId;
            _quantityInput.Value = 0;
        }

        ReadCraftableCount(addon->GetTextNodeById(78));
        UpdateButton();
    }

    private void Update(AtkUnitBase* addon)
    {
        if (_disposed) return;
        if (!GatherBuddy.Config.ShowCraftingLogCraftControls || ArtisanIsLoaded())
        {
            if (_craftButton != null) RemoveControls(addon, restoreLayout: true);
            return;
        }

        if (_craftButton == null)
        {
            Setup(addon);
            return;
        }

        if (GetSelectedRecipeId() != _selectedRecipeId)
            Refresh(addon);
        else
        {
            var previousCount = _craftableCount;
            ReadCraftableCount(addon->GetTextNodeById(78));
            if (previousCount != _craftableCount) UpdateButton();
            UpdateEnabledState();
        }
    }

    private void StartCrafting()
    {
        if (_addon == null || _craftButton == null || _quantityInput == null
            || !GatherBuddy.Config.ShowCraftingLogCraftControls
            || CraftingGatherBridge.HasActiveCraftingLogQueue)
            return;

        var recipeId = GetSelectedRecipeId();
        var text = _addon->GetTextNodeById(78);
        if (recipeId == 0 || text == null || !TryParseCount(text->NodeText.ToString(), out var latestCount) || latestCount <= 0)
        {
            ReadCraftableCount(text);
            UpdateButton();
            return;
        }

        if (_selectedRecipeId != recipeId)
        {
            _selectedRecipeId = recipeId;
            _quantityInput.Value = 0;
        }

        ReadCraftableCount(text);
        var requested = _quantityInput.Value == 0 ? _craftableCount : Math.Min(_quantityInput.Value, _craftableCount);
        if (requested <= 0) return;
        CraftingGatherBridge.StartCraftingLogQueue(recipeId, requested);
        UpdateEnabledState();
    }

    private void ReadCraftableCount(AtkTextNode* text)
    {
        _craftableCount = text != null && TryParseCount(text->NodeText.ToString(), out var count)
            ? Math.Max(0, count)
            : 0;
        if (_quantityInput != null)
        {
            _quantityInput.Max = _craftableCount;
            if (_quantityInput.Value > _craftableCount)
                _quantityInput.Value = _craftableCount;
        }
    }

    private static bool TryParseCount(string text, out int count)
    {
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    private void UpdateButton()
    {
        if (_craftButton == null || _quantityInput == null) return;
        _craftButton.String = _quantityInput.Value == 0
            ? $"Craft All ({_craftableCount})"
            : $"Craft {_quantityInput.Value}";
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        var enabled = _craftableCount > 0 && !CraftingGatherBridge.HasActiveCraftingLogQueue;
        if (_craftButton != null) _craftButton.IsEnabled = enabled;
        if (_quantityInput != null) _quantityInput.IsEnabled = enabled;
    }

    private static string GetButtonLabel() => "Craft All (0)";

    private static uint GetSelectedRecipeId()
    {
        var recipeNote = RecipeNote.Instance();
        var selected = recipeNote == null || recipeNote->RecipeList == null
            ? null
            : recipeNote->RecipeList->SelectedRecipe;
        return selected == null ? 0u : selected->RecipeId;
    }

    private bool TryResizeForControls(AtkUnitBase* addon, AtkResNode* synthesisButton)
    {
        var requiredIds = new uint[] { 3, 4, 5, 6, 7, 8, 9, 10, 39, 81, 102, 103, 105, 106, 107, 108 };
        if (requiredIds.Any(id => addon->GetNodeById(id) == null))
            return false;

        CaptureLayout(addon);
        var oldWidth = addon->RootNode->Width;
        var oldHeight = addon->RootNode->Height;
        _originalAddonWidth = oldWidth;
        _originalAddonHeight = oldHeight;
        _layoutExpanded = true;
        try
        {
            addon->SetSize((ushort)(oldWidth + HorizontalExpansion), (ushort)(oldHeight + VerticalExpansion));

            var tree = addon->GetNodeById(39);
            tree->SetHeight((ushort)(tree->Height + VerticalExpansion));
            var leftBorder = addon->GetNodeById(105);
            var rightBorder = addon->GetNodeById(108);
            var bottomBorder = addon->GetNodeById(107);
            leftBorder->SetWidth((ushort)(addon->RootNode->Height - 96));
            leftBorder->SetYFloat(addon->RootNode->Height - 19);
            rightBorder->SetWidth((ushort)(addon->RootNode->Height - 96));
            rightBorder->SetYFloat(addon->RootNode->Height - 19);
            bottomBorder->SetWidth(335);

            for (uint id = 3; id <= 10; id++)
                addon->GetNodeById(id)->SetXFloat(addon->GetNodeById(id)->X + HorizontalExpansion);

            var ingredientsBackground = addon->GetNodeById(81);
            ingredientsBackground->SetWidth((ushort)(ingredientsBackground->Width + HorizontalExpansion));
            foreach (var id in new uint[] { 102, 103, 104 })
            {
                var node = addon->GetNodeById(id);
                if (node != null) node->SetXFloat(node->X + HorizontalExpansion);
            }

            return true;
        }
        catch (Exception ex)
        {
            RestoreLayout(addon);
            GatherBuddy.Log.Warning($"[CraftingLogCraftControls] Failed to expand RecipeNote for native controls: {ex.Message}");
            return false;
        }
    }

    private void CaptureLayout(AtkUnitBase* addon)
    {
        _originalNodes.Clear();
        foreach (var id in new uint[] { 3, 4, 5, 6, 7, 8, 9, 10, 39, 81, 102, 103, 104, 105, 106, 107, 108 })
        {
            var node = addon->GetNodeById(id);
            if (node != null)
                _originalNodes.Add(new NodeSnapshot(id, node->X, node->Y, node->Width, node->Height));
        }
    }

    private void RestoreLayout(AtkUnitBase* addon)
    {
        if (!_layoutExpanded || addon == null || addon->RootNode == null) return;
        addon->SetSize(_originalAddonWidth, _originalAddonHeight);
        foreach (var snapshot in _originalNodes)
        {
            var node = addon->GetNodeById(snapshot.Id);
            if (node == null) continue;
            node->SetXFloat(snapshot.X);
            node->SetYFloat(snapshot.Y);
            node->SetWidth(snapshot.Width);
            node->SetHeight(snapshot.Height);
        }
        _originalNodes.Clear();
        _layoutExpanded = false;
        _originalAddonWidth = 0;
        _originalAddonHeight = 0;
    }

    private void RemoveControls(AtkUnitBase* addon, bool restoreLayout)
    {
        _quantityInput?.Dispose();
        _quantityInput = null;
        _craftButton?.Dispose();
        _craftButton = null;
        if (restoreLayout) RestoreLayout(addon);
        _addon = null;
        _selectedRecipeId = 0;
        _craftableCount = 0;
    }

    private static bool ArtisanIsLoaded()
        => Dalamud.PluginInterface.InstalledPlugins.Any(plugin => plugin.IsLoaded
            && string.Equals(plugin.Name, "Artisan", StringComparison.OrdinalIgnoreCase));

    private void Finalize(AtkUnitBase* addon)
        => RemoveControls(addon, restoreLayout: true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Dispose();
        RemoveControls(_addon, restoreLayout: true);
    }
}
