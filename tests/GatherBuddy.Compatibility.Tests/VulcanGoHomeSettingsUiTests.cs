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
            h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "GatherBuddySettings");
        var methods = metadata.GetTypeDefinition(type).GetMethods().ToArray();
        var home = Assert.Single(methods,
            h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == "BuildAutoGatherPage");
        var calls = TeleportNativeBoundaryTests.Calls(pe, metadata, home).ToArray();
        Assert.Contains("PluginToggle", calls);
        Assert.DoesNotContain("ExecuteCommand", calls);
        Assert.DoesNotContain("TryStartReturnHome", calls);

        var settingLambdas = metadata.MethodDefinitions
            .Where(h => metadata.GetString(metadata.GetMethodDefinition(h).Name).StartsWith("<BuildAutoGatherPage>", StringComparison.Ordinal))
            .SelectMany(h => TeleportNativeBoundaryTests.Calls(pe, metadata, h))
            .ToArray();
        Assert.Contains("get_GoHomeWhenIdle", settingLambdas);
        Assert.Contains("set_GoHomeWhenIdle", settingLambdas);
        Assert.Contains("get_GoHomeWhenDone", settingLambdas);
        Assert.Contains("set_GoHomeWhenDone", settingLambdas);
    }
}
