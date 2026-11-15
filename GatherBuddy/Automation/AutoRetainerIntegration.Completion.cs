using System;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;

namespace GatherBuddy.Automation;

public sealed partial class AutoRetainerIntegration : IAutoRetainerCompletionPort
{
    public AutoRetainerCompletion Completion { get; private set; } = null!;
    public bool CompletionEnabled => GatherBuddy.Config.AutoGatherConfig.EnableAutoRetainerWhenFinished;
    public ulong CompletionCharacterId => Dalamud.ClientState.IsLoggedIn ? Dalamud.PlayerState.ContentId : 0;
    private TimeSpan _nextCompletionClose;
    private AutoRetainerCompletionState _lastCompletionState;

    internal void RequestCompletion(AutoRetainerCompletionTicket? ticket)
    {
        if (!_disposed) Completion.Request(ticket, _clock.Elapsed);
    }

    public bool CompletionCleanupReady
    {
        get
        {
            if (ActiveOwner != null || Coordinator.YieldRequested || Withdrawal.Owner.HasValue
                || IsSuppressedForHandoff()
                || RetainerBellNavigator.HasOutstanding || GatherBuddy.AutoGather.Enabled
                || CraftingGatherBridge.WaitingForGatherComplete || CraftingGameInterop.HasPendingHomeTravel
                || GatherBuddy.AutoGather.TaskManager.IsBusy || !CharacterReady
                || GatherBuddy.CollectableManager.IsRunning || GatherBuddy.VendorBuyListManager.IsBusy
                || GatherBuddy.VendorPurchaseManager.IsRunning || GatherBuddy.VendorNavigator.IsActive
                || GatherBuddy.VendorPurchaseManager.AutomationLease.Owner.HasValue || GatherBuddy.TransientVendorPurchases.Execution.IsBusy
                || GatherBuddy.MarketboardTravel?.Coordinator.BlocksAutomation == true
                || !Vulcan.Vendors.VendorInteractionHelper.IsReadyToLeaveVendor()) return false;
            if (Lifestream.Enabled && Lifestream.IsBusy()) return false;
            if (VNavmesh.Enabled && (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress() || VNavmesh.Nav.PathfindInProgress())) return false;
            if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
            {
                if (_clock.Elapsed >= _nextCompletionClose)
                {
                    _nextCompletionClose = _clock.Elapsed + TimeSpan.FromMilliseconds(500);
                    CraftingTasks.TaskExitCraft();
                }
                return false;
            }
            return CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal
                && !Dalamud.Conditions[ConditionFlag.InCombat] && !Dalamud.Conditions[ConditionFlag.Casting]
                && !Dalamud.Conditions[ConditionFlag.Gathering] && !Dalamud.Conditions[ConditionFlag.Fishing]
                && !Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent] && !Dalamud.Conditions[ConditionFlag.Occupied39]
                && !Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell] && !Dalamud.Conditions[ConditionFlag.Crafting]
                && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction] && !Dalamud.Conditions[ConditionFlag.PreparingToCraft];
        }
    }

    public void VerifyContinuousAvailable()
    {
        if (!IPCSubscriber.IsReady("AutoRetainer")) throw new InvalidOperationException("AutoRetainer not loaded/ready");
        Require(Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "EnableMultiMode"), true);
        Require(Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "GetMultiModeStatus"));
        Require(Dalamud.PluginInterface.GetIpcSubscriber<bool>(Prefix + "IsBusy"));
    }

    public void EnableContinuous()
        => Dalamud.PluginInterface.GetIpcSubscriber<object>(Prefix + "EnableMultiMode").InvokeAction();

    private void UpdateCompletion()
    {
        Completion.Tick(_clock.Elapsed);
        if (_lastCompletionState == Completion.State) return;
        _lastCompletionState = Completion.State;
        GatherBuddy.Log.Information($"[AutoRetainer completion] {Completion.Status}");
        if (Completion.State == AutoRetainerCompletionState.Failed)
            Communicator.PrintError($"[Vulcan/GBR] {Completion.Status}");
    }
}
