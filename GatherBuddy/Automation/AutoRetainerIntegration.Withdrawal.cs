using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Plugin;

namespace GatherBuddy.Automation;

public sealed partial class AutoRetainerIntegration
{
    internal bool CanNavigateToRetainerBell(out string reason)
        => RetainerNavigationAdmission.Check(new WithdrawalPort(this), Coordinator.YieldRequested,
            Withdrawal.Owner.HasValue || Crafting.RetainerBellNavigator.CleanupPending, out reason);

    public RetainerWithdrawalGuard Withdrawal { get; }
    private string _lastWithdrawalStatus = string.Empty;
    private readonly Utility.DiagnosticThrottle<string> _withdrawalDiagnostics = new(TimeSpan.FromSeconds(5), 1);
    private DateTime _nextWithdrawalClose;

    private bool IsSuppressedForHandoff()
    {
        if (!IPCSubscriber.IsReady("AutoRetainer")) return false;
        try
        {
            var ipc = Dalamud.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
            return ipc.HasFunction && ipc.InvokeFunc();
        }
        catch { return true; } // A failed suppression read is not permission to start an AR pass.
    }

    private void LogWithdrawalStatus()
    {
        if (Withdrawal.Status == _lastWithdrawalStatus) return;
        if (!_withdrawalDiagnostics.TryLog("status", "changed", out var suppressed)) return;
        _lastWithdrawalStatus = Withdrawal.Status;
        GatherBuddy.Log.Debug($"[Retainer coordination] owner={Withdrawal.Owner}; releasing={Withdrawal.Releasing}; {Withdrawal.Status}{Utility.DiagnosticThrottle.Suffix(suppressed)}");
    }

    private sealed class WithdrawalPort(AutoRetainerIntegration integration) : IRetainerWithdrawalPort
    {
        public bool Available => IPCSubscriber.IsReady("AutoRetainer");
        public bool Busy => integration.Busy;
        public bool MultiMode => integration.MultiModeEnabled;
        public bool Suppressed => Dalamud.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
        public bool PendingWork => integration.Enabled && !integration.Coordinator.YieldRequested && integration.FindReadyWork(integration.Mode) != null;
        public bool Ready => SeFunctions.TeleportData.IsReady;
        public ulong CharacterId => Dalamud.PlayerState.ContentId;
        public void SetSuppressed(bool value)
            => Dalamud.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(value);

        public unsafe bool InteractionClosed
        {
            get
            {
                if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell]) return false;
                foreach (var name in new[] { "RetainerList", "SelectString", "SelectIconString", "Talk", "InputNumeric", "SelectYesno" })
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible) return false;
                var module = AgentModule.Instance();
                var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
                return module != null && (agent == null || !agent->IsAgentActive());
            }
        }

        public unsafe void CloseOwnedInteraction()
        {
            // Guard invokes only for its own bell session, after AR task queues are idle.
            if (DateTime.UtcNow < integration._nextWithdrawalClose) return;
            integration._nextWithdrawalClose = DateTime.UtcNow.AddMilliseconds(500);
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && yesno->IsVisible)
                return; // Never accept an unknown confirmation; retain suppression for manual cleanup.
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numeric) && numeric->IsVisible)
            { numeric->Close(true); return; }
            var module = AgentModule.Instance();
            var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
            if (agent != null && agent->IsAgentActive()) { agent->Hide(); return; }
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && talk->IsVisible)
            { new AddonMaster.Talk((nint)talk).Click(); return; }
            foreach (var name in new[] { "SelectString", "SelectIconString", "RetainerList" })
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible)
                { Callback.Fire(addon, true, -1); return; }
        }
    }
}
