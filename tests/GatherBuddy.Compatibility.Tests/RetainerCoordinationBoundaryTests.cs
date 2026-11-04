using GatherBuddy.Crafting;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class RetainerCoordinationBoundaryTests
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
    public void BellNavigationStartupOnlyEnqueuesWork()
    {
        var calls = Calls("CraftingQueueProcessor", "QueueRetainerBellNavigationTasks");
        Assert.DoesNotContain("StartNavigation", calls);
        Assert.DoesNotContain("FindNearestBellForNavigation", calls);
    }

    [Fact]
    public void CompiledNavigationCallbackChecksAdmissionBeforeSubmittingMovement()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var callbacks = metadata.MethodDefinitions.Where(h => metadata.GetString(metadata.GetMethodDefinition(h).Name)
            .StartsWith("<QueueRetainerBellNavigationTasks>", StringComparison.Ordinal))
            .Select(h => TeleportNativeBoundaryTests.Calls(pe, metadata, h).ToList()).ToArray();
        var navigate = Assert.Single(callbacks, c => c.Contains("StartNavigation"));
        Assert.True(navigate.IndexOf("EnsureRetainerNavigationAdmission") < navigate.IndexOf("StartNavigation"));
        Assert.Contains("Stop", navigate);
        Assert.Contains("get_IsSettling", navigate);
    }

    [Fact]
    public void NavigationUsesCancelableQueryAndFrameworkThreadFollowNotSimpleMove()
    {
        var calls = Calls("RetainerBellNavigator", "StartNavigation");
        Assert.DoesNotContain("PathfindAndMoveTo", calls);
        Assert.Contains(".ctor", calls);
        Assert.Contains("TakeCompleted", Calls("RetainerBellNavigator", "Update"));
        Assert.Contains("StopAndSettle", Calls("RetainerBellNavigator", "Settle"));
        Assert.Contains("get_CleanupPending", Calls("CraftingQueueProcessor", "TryYieldToAutoRetainer"));
    }

    [Fact]
    public void WithdrawalPlanningIsDeferredInsteadOfRunningDuringQueueStartup()
    {
        var calls = Calls("CraftingQueueProcessor", "QueueRetainerWithdrawalTasks");
        Assert.DoesNotContain("RefreshRetainerRestockPlanForWithdrawal", calls);
        Assert.DoesNotContain("BuildProtectedRetainerWithdrawalTasks", calls);
        Assert.DoesNotContain("Tick", calls);
    }

    [Fact]
    public void YieldSuspendsBellTasksInsteadOfAdvancingTheirTransitions()
    {
        Assert.Contains("SuspendRetainerStageForAutoRetainer", Calls("CraftingQueueProcessor", "TryYieldToAutoRetainer"));
        var calls = Calls("CraftingQueueProcessor", "UpdateAutoRetainerYield").ToList();
        Assert.Contains("TryYieldToAutoRetainer", calls);
        Assert.True(calls.IndexOf("TryYieldToAutoRetainer") < calls.IndexOf("ProcessTasks"));
        Assert.Contains("RestartRetainerStageAfterAutoRetainer", Calls("CraftingQueueProcessor", "ReleaseFromAutoRetainer"));
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Pause")]
    [InlineData("Reset")]
    [InlineData("CompleteQueue")]
    public void QueueLifecycleAlwaysRequestsOwnedSuppressionCleanup(string method)
        => Assert.Contains("ReleaseRetainerWithdrawalProtection", Calls("CraftingQueueProcessor", method));

    [Fact]
    public void CompiledWithdrawalCallbacksGuardBothExecutorAndAcquisitionTransition()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var methods = metadata.MethodDefinitions.Where(h => metadata.GetString(metadata.GetMethodDefinition(h).Name)
            .StartsWith("<QueueRetainerWithdrawalExecutionTasks>", StringComparison.Ordinal)).ToArray();
        var callbacks = methods.Select(h => TeleportNativeBoundaryTests.Calls(pe, metadata, h).ToList()).ToArray();
        var tick = Assert.Single(callbacks, c => c.Contains("Tick"));
        Assert.True(tick.IndexOf("EnsureRetainerWithdrawalProtection") < tick.IndexOf("Tick"));
        Assert.Contains("EnsureRetainerWithdrawalProtection", tick);
        var transition = Assert.Single(callbacks, c => c.Contains("TransitionFromRetainerWithdrawComplete"));
        Assert.Contains("FinishRetainerWithdrawalProtection", transition);
        Assert.True(transition.IndexOf("FinishRetainerWithdrawalProtection") < transition.IndexOf("TransitionFromRetainerWithdrawComplete"));
    }

    [Fact]
    public void SuppressionCleanupSurvivesQueueStopAndBlocksNewHandoffAdmission()
    {
        Assert.Contains("Tick", Calls("AutoRetainerIntegration", "Update"));
        Assert.Contains("get_Withdrawal", Calls("AutoRetainerIntegration", "get_CanRequestHandoff"));
        Assert.Contains("IsSuppressedForHandoff", Calls("AutoRetainerIntegration", "get_CanRequestHandoff"));
        Assert.Contains("RequestRelease", Calls("AutoRetainerIntegration", "Dispose"));
    }
}
