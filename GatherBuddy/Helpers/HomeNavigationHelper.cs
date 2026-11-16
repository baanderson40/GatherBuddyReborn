using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Plugin;
using System;

namespace GatherBuddy.Helpers;

public static class HomeNavigationHelper
{
    public static string NormalizeCommand(string? command)
    {
        command = command?.Trim();
        if (command?.StartsWith("/li ", StringComparison.OrdinalIgnoreCase) == true)
            command = command[4..].Trim();
        return string.IsNullOrWhiteSpace(command) ? "auto" : command;
    }

    public static bool ShouldReturnHomeAfterCollectables()
        => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle;

    public static bool TryStartReturnHome(out string? error)
    {
        error = null;
        if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
        {
            error = "Cannot return home while bound by duty.";
            return false;
        }

        if (!Lifestream.Enabled)
        {
            error = "Lifestream is not available.";
            return false;
        }

        if (Lifestream.IsBusy())
        {
            return false;
        }

        var command = NormalizeCommand(GatherBuddy.Config.AutoGatherConfig.LifestreamCommand);

        Lifestream.ExecuteCommand(command);
        return true;
    }

    public static bool IsReturnComplete()
        => !Lifestream.Enabled || !Lifestream.IsBusy();
}
