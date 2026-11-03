using System;

namespace GatherBuddy.Automation;

/// <summary>AR computes readiness; this policy only applies the shared user selection.</summary>
public static class AutoRetainerReadiness
{
    public static bool WantsVentures(AutoRetainerProcessingMode mode)
        => mode is AutoRetainerProcessingMode.Ventures or AutoRetainerProcessingMode.Both;
    public static bool WantsDeployables(AutoRetainerProcessingMode mode)
        => mode is AutoRetainerProcessingMode.Deployables or AutoRetainerProcessingMode.Both;
    public static bool VenturesReady(AutoRetainerProcessingMode mode, bool enabled, bool excluded, long? seconds, int threshold)
        => WantsVentures(mode) && enabled && !excluded && seconds.HasValue && seconds.Value <= Math.Clamp(threshold, 0, 3600);
    public static bool DeployablesReady(AutoRetainerProcessingMode mode, bool enabled, bool excluded, bool? ready)
        => WantsDeployables(mode) && enabled && !excluded && ready == true;
}
