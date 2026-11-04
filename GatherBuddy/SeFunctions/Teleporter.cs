using System;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;

namespace GatherBuddy.SeFunctions;

public static unsafe class Teleporter
{
    private static readonly DiagnosticThrottle<string> ReadinessMessages = new(TimeSpan.FromSeconds(10));

    public static bool IsAttuned(uint aetheryte)
    {
        if (!TeleportData.TryGetCachedCosts(out var costs) || costs == null) return false;
        if (costs.ContainsKey(aetheryte)) return true;
        TeleportData.RequestRefresh(); // UI queries schedule a bounded framework retry, never native work on the draw thread.
        return false;
    }

    public static bool Teleport(uint aetheryte)
    {
        if (!TeleportData.IsReady || !TeleportData.TryGetCosts(out var costs, forceRefresh: true) || costs == null)
        {
            ReportNotReady();
            return false;
        }
        if (costs.ContainsKey(aetheryte))
        {
            var teleport = Telepo.Instance();
            if (teleport == null) { ReportNotReady(); return false; }
            return teleport->Teleport(aetheryte, 0);
        }

        Communicator.PrintError("Could not teleport to ",
            GatherBuddy.GameData.Aetherytes.TryGetValue(aetheryte, out var a) ? a.Name : "Unknown Aetheryte", GatherBuddy.Config.SeColorNames,
            " not attuned.");
        return false;
    }

    // Skips attunement only, never player/native readiness.
    public static void TeleportUnchecked(uint aetheryte)
    {
        if (!TeleportData.IsReady) { ReportNotReady(); return; }
        var teleport = Telepo.Instance();
        if (teleport == null) { ReportNotReady(); return; }
        teleport->Teleport(aetheryte, 0);
    }

    private static void ReportNotReady()
    {
        if (ReadinessMessages.TryLog("teleport", "not ready", out _))
            Communicator.PrintError("Teleport temporarily unavailable; wait for stable player readiness and retry.");
    }
}
