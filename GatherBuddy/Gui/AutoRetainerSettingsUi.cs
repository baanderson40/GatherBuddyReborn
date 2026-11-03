using Dalamud.Bindings.ImGui;
using GatherBuddy.Automation;

namespace GatherBuddy.Gui;

internal static class AutoRetainerSettingsUi
{
    public static void Draw()
    {
        var config = GatherBuddy.Config.AutoGatherConfig;
        var enabled = config.AutoRetainerMultiMode;
        if (ImGui.Checkbox("Wait for AutoRetainer Multi-mode", ref enabled))
        {
            config.AutoRetainerMultiMode = enabled;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shared by GBR gathering and Vulcan queues. Safely yields for one AR pass, returns to the original character, then resumes.");
        if (enabled)
        {
            ImGui.TextWrapped("Requires AutoRetainer single-pass IPC and Lifestream for character return.");
            var mode = (int)config.AutoRetainerMode;
            if (ImGui.Combo("Process", ref mode, "Ventures\0Deployables\0Both\0"))
            {
                config.AutoRetainerMode = (AutoRetainerProcessingMode)mode;
                GatherBuddy.Config.Save();
            }
            if (config.AutoRetainerMode is AutoRetainerProcessingMode.Ventures or AutoRetainerProcessingMode.Both)
            {
                var seconds = config.AutoRetainerMultiModeThreshold;
                if (ImGui.SliderInt("Venture threshold (seconds)", ref seconds, 0, 3600))
                {
                    config.AutoRetainerMultiModeThreshold = seconds;
                    GatherBuddy.Config.Save();
                }
            }
            if (config.AutoRetainerMode is AutoRetainerProcessingMode.Deployables or AutoRetainerProcessingMode.Both)
                ImGui.TextWrapped("Deployables: airships and submarines. Readiness uses AutoRetainer's advance timer and wait-for-all settings.");
            var timed = config.AutoRetainerDelayForTimedNodes;
            if (ImGui.Checkbox("Prioritize timed gathering nodes", ref timed))
            {
                config.AutoRetainerDelayForTimedNodes = timed;
                GatherBuddy.Config.Save();
            }
        }
        DrawStatus();
    }

    public static void DrawStatus()
    {
        if (GatherBuddy.AutoRetainerIntegration is not { } integration) return;
        if (integration.Enabled || integration.Coordinator.YieldRequested)
            ImGui.TextWrapped(integration.Coordinator.Status);
        if (integration.Coordinator.State == AutoRetainerHandoffState.Failed)
        {
            ImGui.TextWrapped("Stop AutoRetainer and return to the original character. Restore required AR IPC if unavailable. Recovery releases retained work paused; resume manually after inspection.");
            if (ImGui.Button("Recover AutoRetainer handoff")) integration.Recover();
        }
    }
}
