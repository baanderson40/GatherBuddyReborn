using System.Collections;
using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class VulcanNavigationTests
{
    private static IList MainTabs => (IList)typeof(CraftingQueueProgress).Assembly
        .GetType("GatherBuddy.Gui.VulcanNavigation", throwOnError: true)!
        .GetProperty("MainTabs")!.GetValue(null)!;

    [Fact]
    public void MainTabsFollowCompactOrderWithoutMovedTools()
        => Assert.Equal(
            ["CraftingLists", "Recipes", "Workshops", "Marketboard", "Vendors", "Settings"],
            MainTabs.Cast<object>().Select(tab => tab.ToString() ?? "<unnamed>").ToArray());

    [Fact]
    public void ControllerIndicesMatchImmutableRenderedOrder()
    {
        var tabs = MainTabs;
        Assert.True(tabs.IsReadOnly);
        Assert.Equal(6, tabs.Count);
        for (var index = 0; index < tabs.Count; index++)
            Assert.Equal(index, Convert.ToInt32(tabs[index]));
    }
}
