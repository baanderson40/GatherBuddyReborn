using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class FishingHookBoundaryTests
{
    private static string[] Calls(string typeName, string methodName)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == typeName);
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(), h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName);
        return TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
    }

    [Fact]
    public void ParserConstructorUsesRollbackPair()
        => Assert.Contains("CreatePair", Calls("FishingParser", ".ctor"));

    [Fact]
    public void EnableHasRollbackAndDisposalGuard()
    {
        var parser = Calls("FishingParser", "Enable");
        Assert.Contains("ThrowIf", parser);
        Assert.Contains("Disable", parser);
        Assert.Contains("Disable", Calls("FishRecorder", "Enable"));
    }

    [Theory]
    [InlineData("FishingParser")]
    [InlineData("FishRecorder")]
    public void DisposalUsesIndependentCleanupAttempts(string typeName)
        => Assert.True(Calls(typeName, "Dispose").Count(c => c == "Cleanup") >= 2);

    [Fact]
    public void DiagnosticsInspectLoadedVersionsWithoutChangingHookBackend()
    {
        var describe = Calls("FishingParser", "DescribeHookEnvironmentCore");
        Assert.Contains("GetAssemblies", describe);
        Assert.Contains("get_Modules", describe);
        var create = Calls("FishingParser", "CreateFishingHook");
        Assert.Contains("HookFromAddress", create);
        Assert.DoesNotContain("Sleep", create);
        Assert.DoesNotContain("GetFunctionPointerForDelegate", create);
    }
}
