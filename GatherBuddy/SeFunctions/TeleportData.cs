using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Automation;
using GatherBuddy.Utility;

namespace GatherBuddy.SeFunctions;

/// <summary>All GBR teleport-list refreshes pass through this framework-thread boundary.</summary>
internal static unsafe class TeleportData
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly TeleportSnapshotCache Cache = new();
    private static bool _deferred;
    private static volatile bool _refreshRequested;
    private static TimeSpan _lastRefresh;
    private static readonly DiagnosticThrottle<string> Diagnostics = new(TimeSpan.FromSeconds(30));
    public static TeleportContext? ReadyContext => Cache.ReadyContext;
    public static bool IsReady => ObserveReadiness();

    public static void Update()
    {
        if (!ObserveReadiness()) return;
        if (Cache.Pending || _refreshRequested && Clock.Elapsed - _lastRefresh >= TimeSpan.FromSeconds(5))
        {
            if (_refreshRequested) Cache.RequestRefresh();
            try { TryGetCosts(out _); }
            catch (Exception ex) when (ex is not (AccessViolationException or System.Runtime.InteropServices.SEHException))
            {
                if (Diagnostics.TryLog("refresh", ex.Message, out var suppressed))
                    GatherBuddy.Log.Error($"[Teleport readiness] Managed snapshot refresh failed; retry remains pending: {ex.Message}{DiagnosticThrottle.Suffix(suppressed)}");
            }
        }
    }

    public static void RequestRefresh() => _refreshRequested = true;

    public static bool TryGetCachedCosts(out IReadOnlyDictionary<uint, int>? costs)
    {
        _refreshRequested = true;
        return Cache.TryRead(() => null, out costs);
    }

    private static bool TryCapture(out TeleportContext context)
    {
        context = default;
        if (!Dalamud.Framework.IsInFrameworkUpdateThread || !Dalamud.ClientState.IsLoggedIn
            || Dalamud.Objects.LocalPlayer is not { } player || Dalamud.PlayerState.ContentId == 0
            || !GenericHelpers.IsScreenReady() || Dalamud.Conditions[ConditionFlag.BetweenAreas]
            || Dalamud.Conditions[ConditionFlag.BetweenAreas51] || Dalamud.ClientState.TerritoryType == 0) return false;
        var control = Control.Instance();
        var state = PlayerState.Instance();
        if (control == null || control->LocalPlayer == null || (nint)control->LocalPlayer != player.Address
            || state == null || !state->IsLoaded || state->ContentId != Dalamud.PlayerState.ContentId || UIState.Instance() == null) return false;
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return false;
        }
        context = new(Dalamud.PlayerState.ContentId, player.HomeWorld.RowId, player.CurrentWorld.RowId, Dalamud.ClientState.TerritoryType);
        return true;
    }

    private static bool ObserveReadiness()
    {
        if (!Dalamud.Framework.IsInFrameworkUpdateThread) return false;
        var ready = TryCapture(out var context);
        return Cache.Observe(ready ? context : null, ready, Clock.Elapsed);
    }

    public static bool TryGetCosts(out IReadOnlyDictionary<uint, int>? costs, bool forceRefresh = false)
    {
        costs = null;
        if (!Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            // UI consumers can only read an already-published managed snapshot.
            return TryGetCachedCosts(out costs);
        }
        if (!ObserveReadiness())
        {
            _deferred = true;
            if (Diagnostics.TryLog("readiness", "deferred", out var suppressed))
                GatherBuddy.Log.Debug($"[Teleport readiness] Refresh deferred until player/login/inventory readiness is stable.{DiagnosticThrottle.Suffix(suppressed)}");
            return false;
        }
        if (forceRefresh) Cache.RequestRefresh();
        var context = Cache.ReadyContext;
        var success = Cache.TryRead(() =>
        {
            // Recheck immediately before the native call; singleton existence alone
            // does not establish player/UI/inventory readiness during login/zoning.
            if (!TryCapture(out var current) || current != context) return null;
            var telepo = Telepo.Instance();
            if (telepo == null) return null;
            telepo->UpdateAetheryteList();
            if (!TryCapture(out current) || current != context) return null;
            var replacement = new Dictionary<uint, int>();
            for (var i = 0; i < telepo->TeleportList.Count; i++)
            {
                var entry = telepo->TeleportList[i];
                replacement[entry.AetheryteId] = entry.GilCost > int.MaxValue ? int.MaxValue : (int)entry.GilCost;
            }
            if (!TryCapture(out current) || current != context) return null;
            _lastRefresh = Clock.Elapsed;
            _refreshRequested = false;
            return replacement;
        }, out costs);
        if (success && _deferred)
        {
            GatherBuddy.Log.Debug("[Teleport readiness] Deferred teleport data refreshed after stable player readiness.");
            _deferred = false;
        }
        else if (!success) _deferred = true;
        return success;
    }
}
