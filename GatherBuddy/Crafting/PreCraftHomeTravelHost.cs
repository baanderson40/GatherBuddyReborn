using System;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>Framework-thread adapter; owned by one queue or one direct crafting request.</summary>
internal sealed class PreCraftHomeTravelHost
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly ulong _character = Dalamud.PlayerState.ContentId;
    private readonly PreCraftHomeTravel _receipt;
    private TimeSpan _nextExitAt;
    public PreCraftHomeTravelHost() => _receipt = new(_character);
    public string Status => _receipt.Error ?? "Waiting for pre-craft Lifestream travel and stable player readiness.";
    public bool Submitted => _receipt.Submitted;
    public void Consume() => _receipt.Consume();
    public void Cancel() => _receipt.Cancel();
    public bool InvalidateForGathering()
    {
        if (!_receipt.InvalidateForGathering()) return false;
        GatherBuddy.Log.Debug("[Vulcan] Gathering departure invalidated home travel receipt; final return required before crafting");
        return true;
    }

    public void Observe()
    {
        if (!Dalamud.ClientState.IsLoggedIn || Dalamud.PlayerState.ContentId != _character)
            _receipt.Fail("Character changed or logged out. Stop and restart crafting.");
        else if (Submitted)
            Tick(allowStart: false);
    }

    public HomeTravelAdmission Tick(bool allowStart)
    {
        try
        {
            var command = HomeNavigationHelper.NormalizeCommand(GatherBuddy.Config.AutoGatherConfig.LifestreamCommand);
            var enabled = GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle;
            var character = Dalamud.ClientState.IsLoggedIn ? Dalamud.PlayerState.ContentId : 0;
            if (_receipt.Consumed)
                return _receipt.Observe(Clock.Elapsed, character, command, enabled, false, false);
            if (_receipt.Submitted && !Lifestream.Enabled)
                return _receipt.Fail("Lifestream disappeared during pre-craft travel. Inspect travel and restart crafting.");
            var busy = Lifestream.Enabled && Lifestream.IsBusy();
            var ready = CraftingQueueProcessor.CapturePreparationBlockers() == CraftPreparationBlocker.None
                && !Dalamud.Conditions[ConditionFlag.Gathering] && !Dalamud.Conditions[ConditionFlag.Fishing]
                && !Dalamud.Conditions[ConditionFlag.Casting] && !Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
                && !Dalamud.Conditions[ConditionFlag.Crafting] && !Dalamud.Conditions[ConditionFlag.PreparingToCraft]
                && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
            var admission = _receipt.Observe(Clock.Elapsed, character, command, enabled, busy, ready);
            if (admission == HomeTravelAdmission.Waiting && allowStart && enabled && !Submitted && !busy
                && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween
                && Clock.Elapsed >= _nextExitAt)
            {
                _nextExitAt = Clock.Elapsed + TimeSpan.FromMilliseconds(500);
                CraftingTasks.TaskExitCraft();
            }
            if (admission != HomeTravelAdmission.NeedsCommand || !allowStart) return admission;
            if (Dalamud.Conditions[ConditionFlag.BoundByDuty]) return _receipt.Fail("Cannot return home while bound by duty.");
            if (!Lifestream.Enabled) return _receipt.Fail("Lifestream is not available. Enable it or turn off Go Home When Idle before restarting crafting.");
            // Reserve before IPC. An exception may occur after the command took effect.
            if (!_receipt.RecordSubmission(Clock.Elapsed, command)) return HomeTravelAdmission.Waiting;
            GatherBuddy.Log.Information($"[Vulcan] Pre-craft Lifestream command submitted: {command}");
            Lifestream.ExecuteCommand(command);
            // Capture synchronous busy as well as subsequent framework observations.
            return _receipt.Observe(Clock.Elapsed, Dalamud.PlayerState.ContentId, command, enabled, Lifestream.IsBusy(), false);
        }
        catch (Exception ex)
        {
            return _receipt.Fail($"Pre-craft travel status/submission failed ({ex.GetType().Name}). No automatic retry; inspect travel and restart crafting.");
        }
    }
}
