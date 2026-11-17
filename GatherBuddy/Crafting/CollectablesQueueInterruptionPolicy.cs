namespace GatherBuddy.Crafting;

internal static class CollectablesQueueInterruptionPolicy
{
    internal static bool ShouldSkipCompletedQueue(bool queueComplete, bool startPending, bool finalRunDone, bool hasPendingTasks)
        => queueComplete && !startPending && (finalRunDone || hasPendingTasks);
}
