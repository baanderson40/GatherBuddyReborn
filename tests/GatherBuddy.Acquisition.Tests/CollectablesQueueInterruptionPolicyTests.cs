using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class CollectablesQueueInterruptionPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    public void SkipsCompletedQueueOnlyWhenTurnInIsAlreadyDoneOrTasksRemain(
        bool complete, bool startPending, bool finalRunDone, bool hasPendingTasks, bool expected)
        => Assert.Equal(expected, CollectablesQueueInterruptionPolicy.ShouldSkipCompletedQueue(
            complete, startPending, finalRunDone, hasPendingTasks));
}
