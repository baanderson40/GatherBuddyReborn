using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class CraftReadinessTests
{
    private static PendingCraft Craft(uint recipe, uint ingredient, int amount = 1, uint output = 100,
        int outputQuantity = 1, bool precraft = false)
        => new(recipe, output, outputQuantity,
            new Dictionary<uint, IngredientQualityDemand> { [ingredient] = IngredientQualityDemand.FromPreferNQ(amount) },
            IsPrecraft: precraft);

    [Fact]
    public void BlockedFirstRecipeDoesNotPreventIndependentCraft()
    {
        var queue = new[] { Craft(1, 10), Craft(2, 20) };
        var inventory = new Dictionary<uint, AcquisitionInventory> { [20] = new(1, 0) };
        Assert.Equal(1, CraftReadiness.Select(queue, inventory));
        Assert.Equal(1, CraftReadiness.Missing(queue[0], inventory)[10].Total);
        Assert.Equal((uint)1, queue[0].RecipeId); // Selection does not mutate intent.
    }

    [Fact]
    public void ReadyOrderIsStable()
    {
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(10, 0) };
        Assert.Equal(0, CraftReadiness.Select(new[] { Craft(1, 10), Craft(2, 10) }, inventory));
    }

    [Fact]
    public void BlockedPrecraftKeepsDependentFinalBehindIt()
    {
        var queue = new[] { Craft(1, 10, output: 20, precraft: true), Craft(2, 20), Craft(3, 30) };
        var inventory = new Dictionary<uint, AcquisitionInventory> { [20] = new(1, 0), [30] = new(1, 0) };
        Assert.Equal(2, CraftReadiness.Select(queue, inventory));
    }

    [Fact]
    public void ConfirmedPrecraftOutputUnlocksDependentCraftOnNextSelection()
    {
        var queue = new[] { Craft(1, 10, output: 20, outputQuantity: 3, precraft: true), Craft(2, 20, 3) };
        Assert.Equal(0, CraftReadiness.Select(queue, new Dictionary<uint, AcquisitionInventory> { [10] = new(1, 0) }));
        Assert.Equal(-1, CraftReadiness.Select(queue[1..], new Dictionary<uint, AcquisitionInventory>()));
        Assert.Equal(0, CraftReadiness.Select(queue[1..], new Dictionary<uint, AcquisitionInventory> { [20] = new(3, 0) }));
    }

    [Fact]
    public void SharedIngredientsAreNotReservedForTwoCrafts()
    {
        var queue = new[] { Craft(1, 10, 2), Craft(2, 10, 2) };
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(2, 0) };
        Assert.Equal(0, CraftReadiness.Select(queue, inventory));
        inventory[10] = new(0, 0); // Confirmed craft consumed the only stock.
        Assert.Equal(-1, CraftReadiness.Select(queue[1..], inventory));
    }

    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(0, 2, true)]
    public void MandatoryHqCannotBeFilledByNq(int nq, int hq, bool ready)
    {
        var craft = new PendingCraft(1, 100, 1, new Dictionary<uint, IngredientQualityDemand>
            { [10] = IngredientQualityDemand.FromRequiredHQ(2, 2) });
        Assert.Equal(ready, CraftReadiness.Missing(craft,
            new Dictionary<uint, AcquisitionInventory> { [10] = new(nq, hq) }).Count == 0);
    }

    [Fact]
    public void MandatoryNqAndFlexibleDemandStayDistinct()
    {
        var craft = new PendingCraft(1, 100, 1, new Dictionary<uint, IngredientQualityDemand>
            { [10] = IngredientQualityDemand.FromRequireNQOnly(2) });
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(0, 2) };
        Assert.Equal(2, CraftReadiness.Missing(craft, inventory)[10].RequiredNQ);
        Assert.Empty(CraftReadiness.Missing(Craft(1, 10, 2), inventory));
    }

    [Fact]
    public void SkippedEntriesAreExplicitExclusionsNotDependencyBarriers()
    {
        var skipped = Craft(1, 10, output: 20, precraft: true) with { Skipped = true };
        Assert.Equal(1, CraftReadiness.Select(new[] { skipped, Craft(2, 20) },
            new Dictionary<uint, AcquisitionInventory> { [20] = new(1, 0) }));
        Assert.Empty(CraftReadiness.ExternalRequirements(new[] { skipped }));
    }

    [Fact]
    public void PreparationFailureDefersWithoutDroppingAndRecheckRearms()
    {
        var queue = new[] { Craft(1, 10), Craft(2, 10) };
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(2, 0) };
        var failures = new HashSet<uint> { 1 };
        Assert.Equal(1, CraftReadiness.Select(queue, inventory, failures));
        failures.Add(2);
        Assert.Equal(-1, CraftReadiness.Select(queue, inventory, failures));
        failures.Clear();
        Assert.Equal(0, CraftReadiness.Select(queue, inventory, failures));
    }

    [Fact]
    public void RemainingRequirementsNetMultiOutputPrecraftsWithoutResurrectingCompletedWork()
    {
        var queue = new[] { Craft(1, 10, 2, output: 20, outputQuantity: 3, precraft: true), Craft(2, 20, 2), Craft(3, 20, 2) };
        var initial = CraftReadiness.ExternalRequirements(queue);
        Assert.Equal(2, initial[10].Total);
        Assert.Equal(1, initial[20].Total);
        var remaining = CraftReadiness.ExternalRequirements(queue[1..]);
        Assert.False(remaining.ContainsKey(10));
        Assert.Equal(4, remaining[20].Total); // Current inventory supplies the already-produced stock.
    }

    [Fact]
    public void PlannedOutputNeverGuaranteesHq()
    {
        var consumer = new PendingCraft(2, 100, 1, new Dictionary<uint, IngredientQualityDemand>
            { [20] = IngredientQualityDemand.FromRequiredHQ(1, 1) });
        var requirements = CraftReadiness.ExternalRequirements(new[] { Craft(1, 10, output: 20, precraft: true), consumer });
        Assert.Equal(1, requirements[20].RequiredHQ);
    }

    [Fact]
    public void InventoryChangeBeforeSynthesisMakesPreviouslyReadyCraftBlocked()
    {
        var craft = Craft(1, 10);
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(1, 0) };
        Assert.Empty(CraftReadiness.Missing(craft, inventory));
        inventory.Clear();
        Assert.NotEmpty(CraftReadiness.Missing(craft, inventory));
    }

    [Theory]
    [InlineData(10, 10, 3, 2, 0)]
    [InlineData(10, 9, 3, 2, 0)]
    [InlineData(10, 12, 3, 2, 0)]
    [InlineData(10, 13, 3, 2, 1)]
    [InlineData(10, 16, 3, 2, 2)]
    [InlineData(10, 19, 3, 2, 2)]
    public void OutputDeltaConfirmsOnlyWholeSuccessfulCrafts(int before, int after, int yield, int requested, int expected)
        => Assert.Equal(expected, CraftReadiness.ConfirmedCrafts(before, after, yield, requested));

    [Fact]
    public void JobSwitchWaitsForConfirmationAndBoundsRetries()
    {
        var guard = new JobSwitchGuard();
        Assert.True(guard.ShouldSubmit(8, TimeSpan.Zero));
        guard.MarkSubmitted(TimeSpan.Zero);
        for (var ms = 1; ms < 2000; ms += 10)
            Assert.False(guard.ShouldSubmit(8, TimeSpan.FromMilliseconds(ms)));
        Assert.True(guard.ShouldSubmit(8, TimeSpan.FromSeconds(2)));
        guard.MarkSubmitted(TimeSpan.FromSeconds(2));
        Assert.True(guard.ShouldSubmit(8, TimeSpan.FromSeconds(4)));
        guard.MarkSubmitted(TimeSpan.FromSeconds(4));
        Assert.False(guard.ShouldSubmit(8, TimeSpan.FromSeconds(6)));
        Assert.True(guard.TimedOut);
        guard.Reset(); // Confirmed job, new run, or explicit resume.
        Assert.True(guard.ShouldSubmit(8, TimeSpan.FromSeconds(6)));
        Assert.False(guard.TimedOut);
    }

    [Fact]
    public void ExitCraftDelayDoesNotShortenEquipConfirmationWindow()
    {
        var guard = new JobSwitchGuard();
        Assert.True(guard.ShouldSubmit(8, TimeSpan.Zero));
        guard.MarkSubmitted(TimeSpan.FromSeconds(10));
        Assert.False(guard.ShouldSubmit(8, TimeSpan.FromSeconds(11)));
        Assert.True(guard.ShouldSubmit(8, TimeSpan.FromSeconds(12)));
        Assert.True(guard.ShouldSubmit(9, TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void BatchUsesScarceIngredientLimitAndNeverExceeds99()
    {
        var ingredients = new Dictionary<uint, IngredientQualityDemand>
        {
            [10] = IngredientQualityDemand.FromPreferNQ(2),
            [20] = IngredientQualityDemand.FromRequireNQOnly(1),
        };
        var inventory = new Dictionary<uint, AcquisitionInventory> { [10] = new(20, 0), [20] = new(3, 100) };
        Assert.Equal(3, CraftReadiness.MaximumReadyBatch(ingredients, inventory, 99));
        inventory[20] = new(0, 100);
        Assert.Equal(0, CraftReadiness.MaximumReadyBatch(ingredients, inventory, 99));
        inventory[10] = new(1000, 0);
        inventory[20] = new(1000, 0);
        Assert.Equal(99, CraftReadiness.MaximumReadyBatch(ingredients, inventory, 1000));
    }

    [Fact]
    public void CancelledRunGuardResetDoesNotCarryRetryExhaustionIntoNewRun()
    {
        var guard = new JobSwitchGuard();
        Assert.True(guard.ShouldSubmit(8, TimeSpan.Zero));
        guard.Reset(); // Stop/reset drops pending tasks; new run starts fresh.
        Assert.True(guard.ShouldSubmit(8, TimeSpan.Zero));
        guard.MarkSubmitted(TimeSpan.Zero);
        Assert.False(guard.ShouldSubmit(8, TimeSpan.FromMilliseconds(50)));
    }
}
