using System;
using System.Collections.Generic;

namespace GatherBuddy.Gui;

internal enum VulcanMainTab { CraftingLists, Recipes, Workshops, Marketboard, Vendors, Settings }

internal static class VulcanNavigation
{
    // Shared rendering/controller order; moved settings and Debug are not main tabs.
    public static IReadOnlyList<VulcanMainTab> MainTabs { get; } = Array.AsReadOnly(new[]
    {
        VulcanMainTab.CraftingLists, VulcanMainTab.Recipes, VulcanMainTab.Workshops,
        VulcanMainTab.Marketboard, VulcanMainTab.Vendors, VulcanMainTab.Settings,
    });
}
