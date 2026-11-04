using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;

namespace GatherBuddy.Automation;

/// <summary>Framework-thread adapter. Cancels only an owned pass on explicit Stop; never aborts submitted AR tasks.</summary>
public sealed class AutoRetainerIntegration : IAutoRetainerCoordinationPort, IDisposable
{
    private const string Prefix = "AutoRetainer.PluginState.";
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ICallGateSubscriber<object> _finished;
    private CraftingQueueProcessor? _parkedQueue;
    private bool _gatherWasEnabled;
    private bool _disposed;
    private DateTime _nextCancelDisable, _nextCancelClose;
    private bool _cancelIdleDisabled;
    public AutoRetainerCoordinator Coordinator { get; }
    public AutoRetainerIntegration()
    {
        Coordinator = new(this);
        _finished = Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "OnSingleMultiModeFinished");
        _finished.Subscribe(Coordinator.OnPassFinished);
    }

    public bool Enabled => GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode;
    public AutoRetainerProcessingMode Mode => GatherBuddy.Config.AutoGatherConfig.AutoRetainerMode;
    public string? ActiveOwner => CraftingGatherBridge.AutoRetainerOwner
        ?? (GatherBuddy.AutoGather.Enabled ? $"AutoGather:{GatherBuddy.AutoGather.AutomationRunId}" : null);
    public bool CanRequestHandoff => GatherBuddy.MarketboardTravel?.Coordinator.BlocksAutomation != true
        && CraftingGatherBridge.AutoRetainerQueue?.Paused != true;
    public bool DeferForTimedNode => GatherBuddy.AutoGather.Enabled && GatherBuddy.AutoGather.DeferAutoRetainerForTimedNode;
    public AutoRetainerCharacter? Character => Dalamud.Objects.LocalPlayer is { } player && Dalamud.PlayerState.ContentId != 0
        ? new(Dalamud.PlayerState.ContentId, $"{player.Name}@{player.HomeWorld.Value.Name}") : null;
    public unsafe bool CharacterReady
    {
        get
        {
            if (!Dalamud.ClientState.IsLoggedIn || Character == null || !GenericHelpers.IsScreenReady()
                || Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]) return false;
            var inventory = InventoryManager.Instance();
            if (inventory == null) return false;
            foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) return false;
            }
            if (!Coordinator.BlocksAutomation) return true;
            if (Dalamud.Conditions[ConditionFlag.InCombat] || Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
                || Dalamud.Conditions[ConditionFlag.Occupied39]) return false;
            try
            {
                return !VNavmesh.Enabled || !(VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress());
            }
            catch (Exception) { return false; }
        }
    }
    public bool MultiModeEnabled => Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "GetMultiModeStatus").InvokeFunc();
    public bool Busy => Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "IsBusy").InvokeFunc();
    public bool CanRequestReturn => CharacterReady || Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "CanAutoLogin").InvokeFunc();
    public bool CanReturnFromLoggedIn => Lifestream.Enabled
        && Dalamud.PluginInterface.GetIpcSubscriber<string, string, int>("Lifestream.ChangeCharacter").HasFunction;

    public void VerifyAvailable()
    {
        if (!IPCSubscriber.IsReady("AutoRetainer")) throw new InvalidOperationException("AutoRetainer not loaded/ready");
        if (!Enum.IsDefined(Mode)) throw new InvalidOperationException("Unknown AutoRetainer processing mode");
        Require(Dalamud.PluginInterface.GetIpcSubscriber<AutoRetainerProcessingMode?, object>(Prefix + "EnableSingleMultiMode"), true);
        Require(Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "DisableAllFunctions"), true);
        Require(Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "GetMultiModeStatus"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "IsBusy"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<ulong, CharacterData?>("AutoRetainer.GetOfflineCharacterData"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "CanAutoLogin"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<string, bool>(Prefix + "Relog"));
        if (Mode is AutoRetainerProcessingMode.Ventures or AutoRetainerProcessingMode.Both)
            Require(Dalamud.PluginInterface.GetIpcSubscriber<ulong, long?>(Prefix + "GetClosestRetainerVentureSecondsRemaining"));
        if (Mode is AutoRetainerProcessingMode.Deployables or AutoRetainerProcessingMode.Both)
            Require(Dalamud.PluginInterface.GetIpcSubscriber<ulong, bool?>(Prefix + "AreAnyEnabledVesselsReady"));
    }

    public bool CanStartAutomation()
    {
        if (!Marketboard.MarketboardTravelIntegration.CanStartAutomation()) return false;
        if (Coordinator.YieldRequested)
        {
            Communicator.PrintError(Coordinator.IsStopping
                ? $"Stopping AutoRetainer; wait for cleanup and original-character return. {Coordinator.Status}"
                : "Finish/recover the AutoRetainer handoff before starting automation.");
            return false;
        }
        if (!Enabled || !IPCSubscriber.IsReady("AutoRetainer")) return true;
        try
        {
            if (!MultiModeEnabled && !Busy) return true;
            Communicator.PrintError("AutoRetainer/travel is active. Wait or stop it before starting automation.");
            return false;
        }
        catch (Exception) { return true; } // Missing APIs are reported by the coordinator; existing automation remains usable.
    }

    private static void Require(ICallGateSubscriber gate, bool action = false)
    {
        if (action ? !gate.HasAction : !gate.HasFunction)
            throw new InvalidOperationException("Required AutoRetainer single-pass/readiness IPC unavailable; update AutoRetainer");
    }

    // Read-only IPC projection. Dalamud's call gate JSON-converts cross-assembly DTOs.
    public sealed class CharacterData
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool WorkshopEnabled { get; set; }
        public bool ExcludeRetainer { get; set; }
        public bool ExcludeWorkshop { get; set; }
    }

    public string? FindReadyWork(AutoRetainerProcessingMode mode)
    {
        var ids = Dalamud.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
        foreach (var cid in ids.Distinct())
        {
            var data = Dalamud.PluginInterface.GetIpcSubscriber<ulong, CharacterData?>("AutoRetainer.GetOfflineCharacterData").InvokeFunc(cid);
            if (data == null) continue;
            var name = $"{data.Name}@{data.World}";
            if (AutoRetainerReadiness.WantsVentures(mode) && data.Enabled && !data.ExcludeRetainer)
            {
                var seconds = Dalamud.PluginInterface.GetIpcSubscriber<ulong, long?>(Prefix + "GetClosestRetainerVentureSecondsRemaining").InvokeFunc(cid);
                if (AutoRetainerReadiness.VenturesReady(mode, data.Enabled, data.ExcludeRetainer, seconds, GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold))
                    return $"ventures on {name} ({Math.Max(0, seconds!.Value)}s)";
            }
            if (AutoRetainerReadiness.WantsDeployables(mode) && data.WorkshopEnabled && !data.ExcludeWorkshop)
            {
                if (AutoRetainerReadiness.DeployablesReady(mode, data.WorkshopEnabled, data.ExcludeWorkshop,
                    Dalamud.PluginInterface.GetIpcSubscriber<ulong, bool?>(Prefix + "AreAnyEnabledVesselsReady").InvokeFunc(cid)))
                    return $"deployables on {name} (AR advance timer/wait-for-all)";
            }
        }
        return null;
    }

    public bool TryYield(out string reason)
    {
        reason = "Waiting for safe player state";
        if (!CharacterReady) return false;
        if (GatherBuddy.CollectableManager.IsRunning) { reason = "Collectable turn-in in progress"; return false; }
        if (GatherBuddy.VendorBuyListManager.IsBusy) { reason = "Unrelated vendor list active"; return false; }
        var queue = CraftingGatherBridge.AutoRetainerQueue;
        if (queue != null)
        {
            _parkedQueue = queue;
            if (!queue.TryYieldToAutoRetainer(out reason)) return false;
        }
        if (GatherBuddy.AutoGather.Enabled)
        {
            _gatherWasEnabled = true;
            if (!GatherBuddy.AutoGather.TryYieldToAutoRetainer(out reason)) return false;
        }
        if (GatherBuddy.AutoGather.TaskManager.IsBusy) { reason = "Queued game actions settling"; return false; }
        if (GatherBuddy.VendorPurchaseManager.IsRunning || GatherBuddy.VendorNavigator.IsActive)
        { reason = "Vendor transaction/navigation settling"; return false; }
        if (!Vulcan.Vendors.VendorInteractionHelper.IsReadyToLeaveVendor()) { reason = "Close vendor interaction"; return false; }
        if (CraftingTasks.TaskExitCraft() != CraftingTasks.TaskResult.Done) { reason = "Finishing craft/closing recipe menu"; return false; }
        if (Dalamud.Conditions[ConditionFlag.InCombat] || Dalamud.Conditions[ConditionFlag.Gathering]
            || Dalamud.Conditions[ConditionFlag.Fishing] || Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
            || Dalamud.Conditions[ConditionFlag.Occupied39] || Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell]
            || Dalamud.Conditions[ConditionFlag.Crafting] || Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]
            || Dalamud.Conditions[ConditionFlag.PreparingToCraft]) return false;
        if (Lifestream.Enabled && Lifestream.IsBusy()) { reason = "Travel in progress"; return false; }
        if (VNavmesh.Enabled && (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress()))
        { reason = "Navigation in progress (no unrelated movement stopped)"; return false; }
        _parkedQueue?.ParkForAutoRetainer();
        YesAlready.Unlock();
        return true;
    }

    public void StartPass(AutoRetainerProcessingMode mode)
    {
        _nextCancelDisable = _nextCancelClose = DateTime.MinValue;
        _cancelIdleDisabled = false;
        // Refuse to start a pass that this installed AR cannot stop cooperatively.
        Require(Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "DisableAllFunctions"), true);
        Dalamud.PluginInterface.GetIpcSubscriber<AutoRetainerProcessingMode?, object>(Prefix + "EnableSingleMultiMode").InvokeAction(mode);
    }

    public void CancelPass()
    {
        var busy = Busy;
        if (busy) _cancelIdleDisabled = false;
        // Reassert once when the queue becomes idle: a draining login/deployment
        // task may have re-enabled a scheduler after the initial Stop request.
        if (DateTime.UtcNow < _nextCancelDisable && (busy || _cancelIdleDisabled)) return;
        var stop = Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "DisableAllFunctions");
        Require(stop, true);
        // The coordinator permits this only for its submitted, unfinished pass.
        // Stop scheduling/relogging, but let AR's queued transactions drain. Never
        // AbortAllTasks: that could strand a submitted transfer or purchase.
        stop.InvokeAction();
        _cancelIdleDisabled = !busy;
        _nextCancelDisable = DateTime.UtcNow.AddSeconds(1);
    }

    public unsafe bool TryFinishCancellation(out string reason)
    {
        reason = "Waiting for login/loading to settle";
        if (Character == null) return CanRequestReturn; // Ready title screen can return through AR.
        if (!GenericHelpers.IsScreenReady()) return false;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && talk->IsVisible)
        {
            reason = "Dismissing owned AR closing dialogue";
            if (DateTime.UtcNow >= _nextCancelClose)
            {
                new AddonMaster.Talk((nint)talk).Click();
                _nextCancelClose = DateTime.UtcNow.AddMilliseconds(500);
            }
            return false;
        }
        // Called only after AR's task queue is idle. Cancel menus; never select a
        // new venture/deployment, accept confirmation, or interrupt a transaction.
        foreach (var name in new[] { "SelectString", "SelectIconString", "RetainerList" })
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) || !addon->IsVisible) continue;
            reason = $"Closing owned AR menu {name}";
            if (DateTime.UtcNow >= _nextCancelClose)
            {
                Callback.Fire(addon, true, -1);
                _nextCancelClose = DateTime.UtcNow.AddMilliseconds(500);
            }
            return false;
        }
        reason = "Waiting for owned AR interaction/navigation cleanup; close any remaining dialog";
        return CharacterReady && !Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell]
            && !Dalamud.Conditions[ConditionFlag.Crafting] && !Dalamud.Conditions[ConditionFlag.Gathering];
    }

    public void StopOwner(string? owner)
    {
        if (!_disposed) Coordinator.StopOwner(owner, _clock.Elapsed);
    }

    public void StopVulcanOwner(string? owner)
    {
        // A second Stop can also cancel a stale handoff whose queue was already cleared.
        owner ??= Coordinator.Owner?.StartsWith("Vulcan:", StringComparison.Ordinal) == true ? Coordinator.Owner : null;
        StopOwner(owner);
    }

    public bool ReturnTo(AutoRetainerCharacter character)
    {
        if (Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "CanAutoLogin").InvokeFunc()
            && Dalamud.PluginInterface.GetIpcSubscriber<string, bool>(Prefix + "Relog").InvokeFunc(character.NameWithWorld)) return true;
        if (!Lifestream.Enabled || Lifestream.IsBusy()) return false;
        var parts = character.NameWithWorld.Split('@');
        return parts.Length == 2 && Lifestream.ChangeCharacter(parts[0], parts[1]) == 0;
    }

    public void Release(bool resume)
    {
        var queue = _parkedQueue ?? CraftingGatherBridge.AutoRetainerQueue;
        queue?.ReleaseFromAutoRetainer(resume && ReferenceEquals(queue, CraftingGatherBridge.AutoRetainerQueue));
        if (_gatherWasEnabled || !resume && GatherBuddy.AutoGather.Enabled)
        {
            GatherBuddy.AutoGather.ReleaseFromAutoRetainer();
            if (!resume) GatherBuddy.AutoGather.Enabled = false;
        }
        _parkedQueue = null;
        _gatherWasEnabled = false;
    }

    public void Update()
    {
        if (_disposed) return;
        if (GatherBuddy.MarketboardTravel?.Coordinator.BlocksAutomation == true) return;
        Coordinator.Tick(_clock.Elapsed);
        if (Coordinator.YieldRequested && GatherBuddy.AutoGather.Enabled)
            GatherBuddy.AutoGather.SetAutoRetainerStatus(Coordinator.Status);
    }

    public bool Recover() => Coordinator.Recover(_clock.Elapsed);
    public void Log(string message) => GatherBuddy.Log.Information(message);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _finished.Unsubscribe(Coordinator.OnPassFinished);
        // Do not abort an AR pass on unload or claim that its return completed.
    }
}
