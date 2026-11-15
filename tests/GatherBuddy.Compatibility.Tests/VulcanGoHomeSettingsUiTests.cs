using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class VulcanGoHomeSettingsUiTests
{
    [Fact]
    public void GeneralSettingsExposeAndSaveSharedGoHomeToggleWithoutStartingTravel()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions,
            h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "VulcanWindow");
        var methods = metadata.GetTypeDefinition(type).GetMethods().ToArray();
        var general = Assert.Single(methods,
            h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "DrawGeneralSettings");
        Assert.Contains("DrawGoHomeSettings", TeleportNativeBoundaryTests.Calls(pe, metadata, general));
        var home = Assert.Single(methods,
            h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "DrawGoHomeSettings");
        var calls = TeleportNativeBoundaryTests.Calls(pe, metadata, home).ToArray();
        Assert.Contains("Checkbox", calls);
        Assert.Contains("get_GoHomeWhenIdle", calls);
        Assert.Contains("set_GoHomeWhenIdle", calls);
        Assert.Contains("Save", calls);
        Assert.DoesNotContain("set_GoHomeWhenDone", calls);
        Assert.DoesNotContain("ExecuteCommand", calls);
        Assert.DoesNotContain("TryStartReturnHome", calls);
    }
}
