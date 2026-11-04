using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class MovementModeBoundaryTests
{
    private static string[] Calls(string methodName)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == "OverrideMovement");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName);
        return TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
    }

    [Theory]
    [InlineData("UiControlChanged")]
    [InlineData("UiConfigChanged")]
    [InlineData("Login")]
    public void EverySubscriptionHasMatchingDisposal(string eventName)
    {
        Assert.Contains($"add_{eventName}", Calls(".ctor"));
        Assert.Contains($"remove_{eventName}", Calls("Dispose"));
    }

    [Fact]
    public void EventAdaptersUseFilteredStateRefreshAndReadOnlyConfig()
    {
        Assert.Contains("RefreshForConfigChange", Calls("OnConfigChanged"));
        Assert.Contains("RefreshForConfigChange", Calls("OnUiConfigChanged"));
        Assert.Contains("RefreshForChange", Calls("RefreshForConfigChange"));
        Assert.Contains("TryGetUInt", Calls("ReadMoveMode"));
        Assert.DoesNotContain("Set", Calls("ReadMoveMode"));
    }

    [Fact]
    public void MovementRefreshesBeforeHookEnableAndLogsAtDebugOnly()
    {
        var enable = Calls("set_Enabled").ToList();
        Assert.True(enable.IndexOf("UpdateLegacyMode") >= 0);
        Assert.True(enable.IndexOf("UpdateLegacyMode") < enable.IndexOf("Enable"));
        Assert.Contains("Debug", Calls("LogLegacyMode"));
        Assert.DoesNotContain("Information", Calls("LogLegacyMode"));
        Assert.Contains("get_LegacyMode", Calls("DirectionToDestination"));
    }
}
