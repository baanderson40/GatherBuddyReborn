using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class AutoRetainerCompletionBoundaryTests
{
    [Fact]
    public void TerminalHandoffUsesCleanupBoundaryAndCannotStartSinglePassOrReturn()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        string[] Calls(string typeName, string methodName)
        {
            var type = Assert.Single(metadata.TypeDefinitions,
                h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == typeName);
            var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(),
                h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName);
            return TeleportNativeBoundaryTests.Calls(pe, metadata, method).ToArray();
        }
        var terminal = Calls("AutoRetainerCompletion", "Tick");
        Assert.Contains("get_CompletionCleanupReady", terminal);
        Assert.Contains("EnableContinuous", terminal);
        foreach (var forbidden in new[] { "StartPass", "ReturnTo", "Release", "CancelPass", "DisableAllFunctions", "FindReadyWork" })
            Assert.DoesNotContain(forbidden, terminal);
        Assert.DoesNotContain("RequestCompletion", Calls("CraftingQueueProcessor", "CompleteQueue"));
        var bridge = Calls("CraftingGatherBridge", "Update");
        Assert.Contains("HasPendingTasks", bridge);
        Assert.Contains("get_SuccessfulCompletionTicket", bridge);
        Assert.Contains("RequestCompletion", bridge);
        Assert.True(Array.IndexOf(bridge, "RestoreDisabledGatherLists") < Array.IndexOf(bridge, "RequestCompletion"));
        var ready = Calls("AutoRetainerIntegration", "get_CompletionCleanupReady");
        foreach (var expected in new[] { "get_ActiveOwner", "get_YieldRequested", "get_Withdrawal", "get_HasOutstanding",
                     "get_IsBusy", "get_IsRunning", "TaskExitCraft", "IsSuppressedForHandoff" })
            Assert.Contains(expected, ready);
        Assert.Contains("UpdateCompletion", Calls("AutoRetainerIntegration", "Update"));
        Assert.Contains("get_Pending", Calls("AutoRetainerIntegration", "CanStartAutomation"));
        Assert.Contains("get_EnableAutoRetainerWhenFinished", Calls("AutoRetainerSettingsUi", "Draw"));
        Assert.Contains("set_EnableAutoRetainerWhenFinished", Calls("AutoRetainerSettingsUi", "Draw"));
        Assert.Contains("get_EnableAutoRetainerWhenFinished", Calls("CraftingGatherBridge", "StartGatherAndCraft"));
        Assert.Contains("get_ConfirmedCrafts", Calls("CraftingQueueProcessor", "get_SuccessfulCompletionTicket"));
        Assert.Contains("get_RemainingCrafts", Calls("CraftingQueueProcessor", "get_SuccessfulCompletionTicket"));
    }
}
