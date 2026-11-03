using GatherBuddy.Crafting;

namespace GatherBuddy.Plugin;

internal static class AcquisitionTraceLogger
{
    public static void Write(AcquisitionTrace trace, bool explicitPreview = false)
    {
        foreach (var entry in trace.Events)
        {
            if (entry.Level == AcquisitionTraceLevel.Warning)
                Dalamud.Log.Warning(trace.Format(entry));
            else
                Dalamud.Log.Debug(trace.Format(entry));
        }
        if (explicitPreview)
            Dalamud.Log.Information(trace.Summary + " Enable Debug-level console output to see P1/P2 events.");
        else if (trace.DroppedEvents > 0)
            Dalamud.Log.Warning(trace.Summary);
    }
}
