using GatherBuddy.Crafting;
using GatherBuddy.Helpers;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class PreCraftTravelBoundaryTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData("/li auto", "auto")]
    [InlineData(" /LI home ", "home")]
    [InlineData("home", "home")]
    public void SharedCommandNormalization(string? input, string expected)
        => Assert.Equal(expected, HomeNavigationHelper.NormalizeCommand(input));

    [Fact]
    public void QueueAndGatherAndDirectCraftUseRunScopedGate()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        string[] Calls(string typeName, string methodName)
        {
            var type = Assert.Single(metadata.TypeDefinitions,
                h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == typeName
                    && (typeName != "GatherBuddy" || metadata.GetString(metadata.GetTypeDefinition(h).Namespace) == "GatherBuddy"));
            var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(),
                h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName);
            return TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
        }
        Assert.Contains("Tick", Calls("CraftingQueueProcessor", "EnsureCraftPreparationReady"));
        Assert.Contains("Consume", Calls("CraftingQueueProcessor", "EnsureCraftPreparationReady"));
        Assert.Contains("Pause", Calls("CraftingQueueProcessor", "EnsureCraftPreparationReady"));
        Assert.Contains("TryHandleGatherHomeReturn", Calls("AutoGather", "GoHome"));
        Assert.Contains("IsGatheringComplete", Calls("CraftingGatherBridge", "TryHandleGatherHomeReturn"));
        Assert.Contains("get_GoHomeWhenIdle", Calls("CraftingGatherBridge", "TryHandleGatherHomeReturn"));
        Assert.Contains("Observe", Calls("CraftingGatherBridge", "ObserveHomeTravel"));
        Assert.Contains("ObserveHomeTravel", Calls("GatherBuddy", "Update"));
        Assert.Contains("Tick", Calls("CraftingGameInterop", "Update"));
        Assert.Contains("Cancel", Calls("CraftingGameInterop", "CancelCurrentCraft"));
        Assert.Contains("Cancel", Calls("CraftingQueueProcessor", "Reset"));
        Assert.Contains("RecordSubmission", Calls("PreCraftHomeTravelHost", "Tick"));
        Assert.Contains("Invoke", Calls("PreCraftHomeTravelHost", "Tick")); // Lifestream IPC is a delegate field.
        Assert.DoesNotContain("ExecuteCommand", Calls("PreCraftHomeTravelHost", "Observe"));
        Assert.DoesNotContain("ExecuteCommand", Calls("PreCraftHomeTravel", "Observe"));
        var startup = Calls("CraftingQueueProcessor", "StartQueue");
        Assert.DoesNotContain("QueueRetainerBellNavigationTasks", startup);
        Assert.DoesNotContain("QueueRetainerWithdrawalTasks", startup);
        Assert.DoesNotContain("FindNearestBellForNavigation", startup);
        var update = Calls("CraftingQueueProcessor", "Update");
        Assert.True(Array.IndexOf(update, "UpdateRetainerHomeTravel") < Array.IndexOf(update, "ProcessTasks"));
        var retainerTravel = Calls("CraftingQueueProcessor", "UpdateRetainerHomeTravel");
        Assert.Contains("DeferForInventory", retainerTravel);
        Assert.Contains("Pause", retainerTravel);
        Assert.Contains("Consume", retainerTravel);
        Assert.True(Array.IndexOf(retainerTravel, "Tick") < Array.IndexOf(retainerTravel, "BeginRetainerStage"));
        Assert.Contains("QueueRetainerBellNavigationTasks", Calls("CraftingQueueProcessor", "BeginRetainerStage"));
        Assert.Contains("QueueRetainerWithdrawalTasks", Calls("CraftingQueueProcessor", "BeginRetainerStage"));
        Assert.Contains("OnGatheringDeparture", Calls("CraftingGatherBridge", "CreateGatherListForMissingIngredients"));
        Assert.Contains("OnGatheringDeparture", Calls("CraftingGatherBridge", "ResumeRetainedGathering"));
        Assert.Contains("InvalidateForGathering", Calls("CraftingQueueProcessor", "OnGatheringDeparture"));
    }
}
