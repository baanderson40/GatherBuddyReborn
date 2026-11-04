using System.Reflection;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class CraftingStatusPresentationTests
{
    private static readonly Type Presentation = typeof(CraftingQueueProgress).Assembly
        .GetType("GatherBuddy.Gui.CraftingStatusPresentation", throwOnError: true)!;

    private static string Header(CraftingQueueProcessor.QueueState state, bool paused = false,
        AutoRetainerHandoffState? handoff = null, string? recipe = "Electrum Ingot",
        string? purchase = null, string? gather = null)
        => (string)Presentation.GetMethod("GetHeader", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [state, paused, handoff, recipe, purchase, gather])!;

    [Fact]
    public void CraftingUsesSinglePhaseAndRecipeHeader()
        => Assert.Equal("Crafting · Electrum Ingot", Header(CraftingQueueProcessor.QueueState.Crafting));

    [Fact]
    public void PauseReplacesActiveCraftingPhase()
        => Assert.Equal("Paused · Electrum Ingot", Header(CraftingQueueProcessor.QueueState.Crafting, paused: true));

    [Theory]
    [InlineData(AutoRetainerHandoffState.Yielding, "Yielding")]
    [InlineData(AutoRetainerHandoffState.StartingPass, "Starting pass")]
    [InlineData(AutoRetainerHandoffState.RunningPass, "Processing pass")]
    [InlineData(AutoRetainerHandoffState.WaitingForIdle, "Cleaning up")]
    [InlineData(AutoRetainerHandoffState.Returning, "Returning")]
    [InlineData(AutoRetainerHandoffState.Settling, "Settling")]
    [InlineData(AutoRetainerHandoffState.Cancelling, "Stopping")]
    [InlineData(AutoRetainerHandoffState.Failed, "Recovery required")]
    public void HandoffHidesStaleQueuePhaseAndRecipe(AutoRetainerHandoffState state, string phase)
        => Assert.Equal($"AutoRetainer · {phase}",
            Header(CraftingQueueProcessor.QueueState.WaitingForJobSwitch, handoff: state));

    [Fact]
    public void MissingRecipeDoesNotLeaveDanglingSeparator()
        => Assert.Equal("Gathering materials", Header(CraftingQueueProcessor.QueueState.WaitingForGather, recipe: null));

    [Fact]
    public void PurchaseHeaderUsesActualVendorTargetNotNextSpruceRecipe()
        => Assert.Equal("Purchasing · Bomb Ash", Header(CraftingQueueProcessor.QueueState.PurchasingMaterials,
            recipe: "Spruce Lumber", purchase: "Bomb Ash", gather: "Spruce Log"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingVendorTargetNeverFallsBackToQueuedRecipe(string? target)
        => Assert.Equal("Purchasing materials", Header(CraftingQueueProcessor.QueueState.PurchasingMaterials,
            recipe: "Spruce Lumber", purchase: target));

    [Fact]
    public void GatheringHeaderUsesLiveGatherItemAndIgnoresPurchaseAndRecipe()
        => Assert.Equal("Gathering · Spruce Log", Header(CraftingQueueProcessor.QueueState.WaitingForGather,
            recipe: "Spruce Lumber", purchase: "Bomb Ash", gather: "Spruce Log"));

    [Fact]
    public void PausedPurchasingDoesNotLabelRecipeAsPurchase()
        => Assert.Equal("Paused · Purchasing materials", Header(CraftingQueueProcessor.QueueState.PurchasingMaterials,
            paused: true, recipe: "Spruce Lumber"));

    [Theory]
    [InlineData(CraftingQueueProcessor.QueueState.Idle)]
    [InlineData(CraftingQueueProcessor.QueueState.NavigatingToRetainerBell)]
    [InlineData(CraftingQueueProcessor.QueueState.WithdrawingFromRetainer)]
    [InlineData(CraftingQueueProcessor.QueueState.PlanningAcquisition)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForVendorData)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForVendorCleanup)]
    [InlineData(CraftingQueueProcessor.QueueState.BlockedByMaterials)]
    [InlineData(CraftingQueueProcessor.QueueState.AcquisitionFailed)]
    [InlineData(CraftingQueueProcessor.QueueState.Cancelled)]
    [InlineData(CraftingQueueProcessor.QueueState.Repairing)]
    [InlineData(CraftingQueueProcessor.QueueState.ExtractingMateria)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForPlayerInventory)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForTravelCompletion)]
    public void NonRecipePhasesDoNotAppendQueuedRecipeOrStaleActionTargets(CraftingQueueProcessor.QueueState state)
    {
        var header = Header(state, recipe: "Spruce Lumber", purchase: "Bomb Ash", gather: "Spruce Log");
        Assert.DoesNotContain("Spruce", header);
        Assert.DoesNotContain("Bomb Ash", header);
        Assert.NotEqual("Unknown", header);
    }

    [Theory]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForJobSwitch)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForRaphaelSolution)]
    [InlineData(CraftingQueueProcessor.QueueState.ReadyForCraft)]
    [InlineData(CraftingQueueProcessor.QueueState.Crafting)]
    [InlineData(CraftingQueueProcessor.QueueState.WaitingForCraftResult)]
    public void RecipePhasesKeepRecipeNameAndIgnoreOtherTargets(CraftingQueueProcessor.QueueState state)
    {
        var header = Header(state, recipe: "Spruce Lumber", purchase: "Bomb Ash", gather: "Spruce Log");
        Assert.EndsWith(" · Spruce Lumber", header);
        Assert.DoesNotContain("Bomb Ash", header);
        Assert.DoesNotContain("Spruce Log", header);
    }

    [Fact]
    public void VendorTargetProjectionRequiresMatchingActiveSessionAndLease()
    {
        var manager = new VendorPurchaseManager();
        var session = Guid.NewGuid();
        Assert.Null(manager.GetActiveItemNameForSession(session));
        var type = typeof(VendorPurchaseManager);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var requestType = type.GetNestedType("VendorPurchaseRequest", BindingFlags.NonPublic)!;
        var constructor = Assert.Single(requestType.GetConstructors());
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(string)
            ? (object)"Bomb Ash" : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        type.GetField("_request", flags)!.SetValue(manager, constructor.Invoke(arguments));
        var state = type.GetField("_state", flags)!;
        state.SetValue(manager, Enum.Parse(state.FieldType, "PurchasingItem"));
        Assert.Null(manager.GetActiveItemNameForSession(session)); // Standalone purchase.
        type.GetField("_ownerSessionId", flags)!.SetValue(manager, session);
        Assert.Null(manager.GetActiveItemNameForSession(session)); // No acquired lease.
        Assert.True(manager.AutomationLease.TryAcquire(session));
        Assert.Equal("Bomb Ash", manager.GetActiveItemNameForSession(session));
        Assert.Null(manager.GetActiveItemNameForSession(Guid.NewGuid()));
        state.SetValue(manager, Enum.Parse(state.FieldType, "Idle"));
        Assert.Null(manager.GetActiveItemNameForSession(session)); // Completed/stale request.
    }

    [Fact]
    public void CompleteDoesNotShowStaleRecipe()
        => Assert.Equal("Complete", Header(CraftingQueueProcessor.QueueState.Complete));

    [Fact]
    public void PerRecipeProgressCountsConfirmedOutputAcrossReorderedEntriesAndExcludesSkips()
    {
        CraftingListItem[] queue =
        [
            new(43, 1),
            new(99, 1),
            new(43, 1) { Options = new() { Skipping = true } },
            new(43, 1), // Current craft has not produced confirmed output.
            new(99, 1),
            new(43, 1),
        ];
        var progress = (CraftingQueueProgress)Presentation.GetMethod("GetRecipeProgress")!
            .Invoke(null, [queue, 3, 43u])!;
        Assert.Equal(4, progress.TotalEntries);
        Assert.Equal(1, progress.ConfirmedCrafts);
        Assert.Equal(3, progress.RequestedCrafts);
        Assert.Equal(2, progress.RemainingCrafts);
        Assert.Equal(1, progress.ExplicitSkips);
        Assert.Equal(43u, queue[3].RecipeId); // Presentation does not reorder/mutate.
    }
}
