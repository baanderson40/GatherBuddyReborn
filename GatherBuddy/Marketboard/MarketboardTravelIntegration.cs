using System;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Crafting;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Marketboard;

/// <summary>Framework-thread adapter. Uses public IPC only; never calls Lifestream.Abort.</summary>
public sealed class MarketboardTravelIntegration : IMarketTravelPort, IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;
    public MarketboardTravelCoordinator Coordinator { get; }
    public MarketboardTravelIntegration() => Coordinator = new(this);

    public static bool CanStartAutomation()
    {
        if (GatherBuddy.MarketboardTravel?.Coordinator.BlocksAutomation != true) return true;
        Communicator.PrintError("Finish, stop follow-up, or recover Marketboard travel before starting automation.");
        return false;
    }

    public MarketTravelDestination? ResolveDestination(MarketListing listing, uint itemId, string name, out string reason)
    {
        reason = string.Empty;
        var worlds = Dalamud.GameData.GetExcelSheet<World>();
        if (worlds != null)
            foreach (var world in worlds)
            {
                if (!world.IsPublic || !string.Equals(world.Name.ExtractText(), listing.WorldName, StringComparison.OrdinalIgnoreCase)) continue;
                var dc = world.DataCenter.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrEmpty(dc)) return new(world.RowId, world.Name.ExtractText(), dc, itemId, name, listing.IsHq);
            }
        reason = "Listing world could not be resolved to a public game world.";
        return null;
    }

    public void VerifyAvailable()
    {
        if (_disposed || !IPCSubscriber.IsReady("Lifestream")) throw new InvalidOperationException("Lifestream is unavailable.");
        if (!Dalamud.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").HasFunction
         || !Dalamud.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById").HasFunction
         || !Dalamud.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC").HasFunction
         || !Dalamud.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC").HasFunction
         || !Dalamud.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").HasAction)
            throw new InvalidOperationException("Required Lifestream travel IPC is missing; update Lifestream.");
    }

    public string? ConflictReason
    {
        get
        {
            if (GatherBuddy.AutoGather.Enabled || GatherBuddy.AutoGather.TaskManager.IsBusy)
                return "Stop gathering/fishing and let queued GBR actions settle first.";
            if (CraftingGatherBridge.AutoRetainerOwner != null || CraftingGatherBridge.HasAcquisitionOwnership
                || CraftingGatherBridge.WaitingForGatherComplete || CraftingGatherBridge.RecipeToCraft != 0)
                return "Stop the Vulcan queue/material acquisition first.";
            if (CraftingGameInterop.CurrentState is not (CraftingGameInterop.CraftState.IdleNormal or CraftingGameInterop.CraftState.IdleBetween))
                return "Finish or stop the current crafting operation first.";
            if (GatherBuddy.VendorNavigator.IsActive || GatherBuddy.VendorPurchaseManager.IsRunning
                || GatherBuddy.VendorPurchaseManager.AutomationLease.Owner.HasValue || GatherBuddy.VendorBuyListManager.IsBusy
                || GatherBuddy.TransientVendorPurchases.Execution.IsBusy || GatherBuddy.CollectableManager.IsRunning)
                return "Stop vendor/collectable automation and let its interaction settle first.";
            if (GatherBuddy.AutoRetainerIntegration?.Coordinator.YieldRequested == true) return "Finish/recover the AutoRetainer handoff first.";
            if (IPCSubscriber.IsReady("AutoRetainer"))
            {
                var busy = Dalamud.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
                var multi = Dalamud.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus");
                if (!busy.HasFunction || !multi.HasFunction) return "Cannot verify AutoRetainer idle state.";
                var ownsTravel = Coordinator.BlocksAutomation;
                var multiMode = multi.InvokeFunc();
                var aggregateBusy = !ownsTravel && busy.InvokeFunc();
                if (MarketTravelConflictPolicy.AutoRetainerBlocks(ownsTravel, multiMode, aggregateBusy))
                    return "Stop AutoRetainer processing before manual Marketboard travel.";
            }
            return null;
        }
    }

    public unsafe MarketTravelObservation Observe()
    {
        var player = Dalamud.Objects.LocalPlayer;
        var loggedIn = Dalamud.ClientState.IsLoggedIn && player != null;
        var marketOpen = GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearch", out var search) && search->IsVisible
            && !Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell];
        var ready = loggedIn && GenericHelpers.IsScreenReady()
            && !new[] { ConditionFlag.InCombat, ConditionFlag.BoundByDuty, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
                ConditionFlag.Crafting, ConditionFlag.PreparingToCraft, ConditionFlag.ExecutingCraftingAction,
                ConditionFlag.Gathering, ConditionFlag.Fishing, ConditionFlag.OccupiedSummoningBell }.Any(flag => Dalamud.Conditions[flag]);
        if (!marketOpen && new[] { ConditionFlag.Occupied, ConditionFlag.Occupied39, ConditionFlag.OccupiedInEvent,
                ConditionFlag.OccupiedInQuestEvent }.Any(flag => Dalamud.Conditions[flag])) ready = false;
        foreach (var name in new[] { "SelectYesno", "SelectString", "SelectIconString", "Talk", "Shop", "RecipeNote" })
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var dialog) && dialog->IsVisible) ready = false;
        if (VNavmesh.Enabled && (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress())) ready = false;
        return new(loggedIn ? Dalamud.PlayerState.ContentId : 0, loggedIn ? player!.HomeWorld.RowId : 0,
            loggedIn ? player!.CurrentWorld.RowId : 0, ready,
            Dalamud.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(), marketOpen);
    }

    public bool CanVisit(MarketTravelDestination destination)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<World>();
        if (sheet?.TryGetRow(destination.WorldId, out var world) != true || !world.IsPublic
            || world.Name.ExtractText() != destination.WorldName) return false;
        if (Dalamud.Objects.LocalPlayer?.CurrentWorld.RowId == destination.WorldId) return true;
        return Dalamud.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC").InvokeFunc(destination.WorldName)
            || Dalamud.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC").InvokeFunc(destination.WorldName);
    }
    public bool ChangeWorld(uint worldId) => Dalamud.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById").InvokeFunc(worldId);
    public void GoToMarketboard() => Dalamud.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction("mb");
    public bool Start(MarketTravelDestination destination, out string reason) => Coordinator.Start(destination, _clock.Elapsed, out reason);
    public void StopFollowup() => Coordinator.StopFollowup(_clock.Elapsed);
    public bool Recover() => Coordinator.Recover(_clock.Elapsed);
    public void Update() { if (!_disposed) Coordinator.Tick(_clock.Elapsed); }
    public void Log(string message) => GatherBuddy.Log.Information(message);
    public void Notify(string message, bool error)
    {
        var text = $"Marketboard travel ({Coordinator.Destination?.WorldName}): {message}";
        if (error) Communicator.PrintError(text); else Communicator.Print(text);
    }
    public void Dispose() => _disposed = true; // No follow-up and no global Lifestream abort on unload.
}
