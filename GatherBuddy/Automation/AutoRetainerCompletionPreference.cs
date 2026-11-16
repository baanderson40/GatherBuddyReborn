using GatherBuddy.Crafting;

namespace GatherBuddy.Automation;

/// <summary>Shared completion opt-in for GBR, Vulcan, and the active crafting queue.</summary>
public static class AutoRetainerCompletionPreference
{
    public static void SetEnabled(bool enabled)
    {
        GatherBuddy.Config.AutoGatherConfig.EnableAutoRetainerWhenFinished = enabled;
        CraftingGatherBridge.SetActiveQueueCompletionPreference(enabled);
        if (!enabled)
            GatherBuddy.AutoRetainerIntegration?.Completion.Cancel();
    }
}
