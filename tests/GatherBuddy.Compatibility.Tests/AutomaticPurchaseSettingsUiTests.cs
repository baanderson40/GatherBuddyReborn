using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class AutomaticPurchaseSettingsUiTests
{
    [Fact]
    public void GlobalSettingsDrawPolicyWithoutAnExtraCollapsibleSection()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions,
            h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "VendorPurchasePolicyUi");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(),
            h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "DrawGlobal");
        var calls = TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
        Assert.DoesNotContain("CollapsingHeader", calls);
        Assert.Contains("DrawPolicy", calls);
        Assert.Contains("Save", calls);
    }
}
