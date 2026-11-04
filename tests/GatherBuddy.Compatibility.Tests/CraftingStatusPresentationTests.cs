using System.Reflection;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class CraftingStatusPresentationTests
{
    private static readonly Type Presentation = typeof(CraftingQueueProgress).Assembly
        .GetType("GatherBuddy.Gui.CraftingStatusPresentation", throwOnError: true)!;

    private static string Header(CraftingQueueProcessor.QueueState state, bool paused = false,
        AutoRetainerHandoffState? handoff = null, string? recipe = "Electrum Ingot")
        => (string)Presentation.GetMethod("GetHeader", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [state, paused, handoff, recipe])!;

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
        => Assert.Equal("Gathering", Header(CraftingQueueProcessor.QueueState.WaitingForGather, recipe: null));

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
