using System.Diagnostics;
using System.Reflection;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather;
using GatherBuddy.Crafting;
using Newtonsoft.Json;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class AutoRetainerCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyConfigurationPreservesOptInAndThreshold(bool enabled)
    {
        var json = "{\"AutoRetainerMultiMode\":" + (enabled ? "true" : "false")
            + ",\"AutoRetainerMultiModeThreshold\":127,\"AutoRetainerDelayForTimedNodes\":false,\"FoodItemId\":123}";
        var config = JsonConvert.DeserializeObject<AutoGatherConfig>(json)!;
        Assert.Equal(enabled, config.AutoRetainerMultiMode);
        Assert.Equal(AutoRetainerProcessingMode.Ventures, config.AutoRetainerMode);
        Assert.Equal(127, config.AutoRetainerMultiModeThreshold);
        Assert.False(config.AutoRetainerDelayForTimedNodes);
        Assert.Equal(123u, config.FoodItemId);
    }

    [Theory]
    [InlineData(AutoRetainerProcessingMode.Ventures)]
    [InlineData(AutoRetainerProcessingMode.Deployables)]
    [InlineData(AutoRetainerProcessingMode.Both)]
    public void AllModesRoundTripInActualConfiguration(AutoRetainerProcessingMode mode)
    {
        var config = new AutoGatherConfig { AutoRetainerMultiMode = true, AutoRetainerMode = mode };
        var restored = JsonConvert.DeserializeObject<AutoGatherConfig>(JsonConvert.SerializeObject(config))!;
        Assert.True(restored.AutoRetainerMultiMode);
        Assert.Equal(mode, restored.AutoRetainerMode);
    }

    [Theory]
    [InlineData(AutoRetainerProcessingMode.Ventures, "0")]
    [InlineData(AutoRetainerProcessingMode.Deployables, "1")]
    [InlineData(AutoRetainerProcessingMode.Both, "2")]
    public void NullableEnumUsesNumericCrossAssemblyIpcWireContract(AutoRetainerProcessingMode mode, string expected)
        => Assert.Equal(expected, JsonConvert.SerializeObject((AutoRetainerProcessingMode?)mode));

    [Fact]
    public void OfflineCharacterProjectionAcceptsArFieldNamesWithoutRetainingMutableData()
    {
        var projection = JsonConvert.DeserializeObject<AutoRetainerIntegration.CharacterData>(
            "{\"CID\":42,\"Name\":\"Test Character\",\"World\":\"World\",\"Enabled\":true,\"WorkshopEnabled\":true,\"ExcludeRetainer\":false,\"ExcludeWorkshop\":true,\"RetainerData\":[]}")!;
        Assert.Equal("Test Character", projection.Name);
        Assert.True(projection.Enabled);
        Assert.True(projection.WorkshopEnabled);
        Assert.False(projection.ExcludeRetainer);
        Assert.True(projection.ExcludeWorkshop);
    }

    [Fact]
    public void ParkingActualQueueStopsClockWithoutClearingTasksOrProgress()
    {
        var queue = new CraftingQueueProcessor();
        var type = typeof(CraftingQueueProcessor);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var tasks = (List<Func<CraftingTasks.TaskResult>>)type.GetField("_tasks", flags)!.GetValue(queue)!;
        var clock = (Stopwatch)type.GetField("_runClock", flags)!.GetValue(queue)!;
        tasks.Add(() => CraftingTasks.TaskResult.Done);
        type.GetField("_currentQueueIndex", flags)!.SetValue(queue, 7);
        type.GetField("_currentState", flags)!.SetValue(queue, CraftingQueueProcessor.QueueState.ReadyForCraft);
        try
        {
            type.GetMethod("ParkForAutoRetainer", flags)!.Invoke(queue, null);
            Assert.False(clock.IsRunning);
            Assert.Single(tasks);
            Assert.Equal(7, queue.CurrentQueueIndex);
            Assert.Equal(CraftingQueueProcessor.QueueState.ReadyForCraft, queue.CurrentState);
            Assert.False(queue.Paused); // Automatic park is distinct from a user Pause.
        }
        finally
        {
            CraftingGameInterop.CraftFinished -= queue.OnCraftFinished;
            var quickProgress = type.GetMethod("OnQuickSynthProgress", flags)!.CreateDelegate<Action<int, int>>(queue);
            CraftingGameInterop.QuickSynthProgress -= quickProgress;
        }
    }
}
