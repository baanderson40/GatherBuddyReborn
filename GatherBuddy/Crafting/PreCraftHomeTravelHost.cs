using System;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

/// <summary>Framework-thread adapter; owned by one queue or one direct crafting request.</summary>
internal sealed class PreCraftHomeTravelHost
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly ulong _character = Dalamud.PlayerState.ContentId;
    private readonly PreCraftHomeTravel _receipt;
    private readonly HomeTravelArrivalReadiness _arrival = new();
    private TimeSpan _nextExitAt;
    public PreCraftHomeTravelHost() => _receipt = new(_character);
    public string Status => _receipt.Error ?? "Waiting for Lifestream idle and player readiness (2 seconds outdoors; housing/workshop fast path).";
    public bool Submitted => _receipt.Submitted;
    public void Consume() => _receipt.Consume();
    public void Cancel() => _receipt.Cancel();
    public bool SuspendForAutoRetainer(Guid handoff)
    {
        if (!_receipt.SuspendForAutoRetainer(handoff, Clock.Elapsed,
                Dalamud.ClientState.IsLoggedIn ? Dalamud.PlayerState.ContentId : 0)) return false;
        _arrival.Reset();
        return true;
    }
    public bool ResumeFromAutoRetainer(Guid handoff)
    {
        if (!_receipt.ResumeFromAutoRetainer(handoff, Clock.Elapsed,
                Dalamud.ClientState.IsLoggedIn ? Dalamud.PlayerState.ContentId : 0)) return false;
        _arrival.Reset();
        return true;
    }
    public bool InvalidateForGathering()
    {
        if (!_receipt.InvalidateForGathering()) return false;
        _arrival.Reset();
        GatherBuddy.Log.Debug("[Vulcan] Gathering departure invalidated home travel receipt; final return required before crafting");
        return true;
    }

    public void Observe()
    {
        if (_receipt.Suspended) return; // Only the bound coordinator release may validate the returning character.
        if (!Dalamud.ClientState.IsLoggedIn || Dalamud.PlayerState.ContentId != _character)
            _receipt.Fail("Character changed or logged out. Stop and restart crafting.");
        else if (Submitted)
            Tick(allowStart: false);
    }

    public HomeTravelAdmission Tick(bool allowStart)
    {
        try
        {
            if (_receipt.Suspended)
                return _receipt.Observe(Clock.Elapsed, 0, string.Empty, false, false, false);
            var command = HomeNavigationHelper.NormalizeCommand(GatherBuddy.Config.AutoGatherConfig.LifestreamCommand);
            var enabled = GatherBuddy.Config.VulcanReturnHomeBeforeCrafting;
            var character = Dalamud.ClientState.IsLoggedIn ? Dalamud.PlayerState.ContentId : 0;
            if (_receipt.Consumed)
                return _receipt.Observe(Clock.Elapsed, character, command, enabled, false, false);
            if (_receipt.Submitted && !Lifestream.Enabled)
                return _receipt.Fail("Lifestream disappeared during pre-craft travel. Inspect travel and restart crafting.");
            var busy = Lifestream.Enabled && Lifestream.IsBusy();
            var ready = CapturePlayerReady();
            var arrivalReady = !Submitted ? ready : ObserveArrival(busy, ready);
            var alreadyInside = enabled && !Submitted && !busy && ready && IsInsidePreCraftDestination();
            var skippedBefore = _receipt.SkippedIndoorFastPath;
            var admission = _receipt.Observe(Clock.Elapsed, character, command, enabled, busy, arrivalReady, alreadyInside);
            if (!skippedBefore && _receipt.SkippedIndoorFastPath)
                GatherBuddy.Log.Information("[Vulcan] Already inside housing or FC workshop; skipping pre-craft Lifestream command for this phase");
            if (admission is not (HomeTravelAdmission.Failed or HomeTravelAdmission.Cancelled)
                && allowStart && !Submitted && !busy && !ready
                && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
            {
                if (Clock.Elapsed >= _nextExitAt)
                {
                    _nextExitAt = Clock.Elapsed + TimeSpan.FromMilliseconds(500);
                    CraftingTasks.TaskExitCraft();
                }
                return HomeTravelAdmission.Waiting; // Also exit an old crafting stance when home travel is disabled.
            }
            if (admission != HomeTravelAdmission.NeedsCommand || !allowStart) return admission;
            if (Dalamud.Conditions[ConditionFlag.BoundByDuty]) return _receipt.Fail("Cannot return home while bound by duty.");
            if (!Lifestream.Enabled) return _receipt.Fail("Lifestream is not available. Enable it or turn off Return home before crafting in Vulcan Settings.");
            // Reserve before IPC. An exception may occur after the command took effect.
            if (!_receipt.RecordSubmission(Clock.Elapsed, command)) return HomeTravelAdmission.Waiting;
            _arrival.Reset(); // Idle time before submission cannot count as arrival.
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

    // Call only before bell selection/withdrawal, not while our own bell navigation is moving.
    public bool CheckBellArrival()
    {
        if (Tick(allowStart: false) != HomeTravelAdmission.Ready) return false;
        try
        {
            var busy = Lifestream.Enabled && Lifestream.IsBusy();
            return ObserveArrival(busy, CapturePlayerReady());
        }
        catch (Exception) { _arrival.Reset(); return false; }
    }

    private bool ObserveArrival(bool busy, bool ready)
        => _arrival.Observe(Clock.Elapsed, busy, ready,
            !busy && ready && IsInsidePreCraftDestination());

    private static bool IsInsidePreCraftDestination()
    {
        var territoryId = Dalamud.ClientState.TerritoryType;
        var intendedUse = Dalamud.GameData.GetExcelSheet<TerritoryType>()
            .GetRowOrDefault(territoryId)?.TerritoryIntendedUse.RowId ?? 0;
        return PreCraftHomeTravelRules.IsIndoorFastPath(
            SeFunctions.HousingManager.IsInsideHousing(), territoryId, intendedUse);
    }

    private static bool CapturePlayerReady()
        => CraftingQueueProcessor.CapturePreparationBlockers() == CraftPreparationBlocker.None
            && !Dalamud.Conditions[ConditionFlag.Gathering] && !Dalamud.Conditions[ConditionFlag.Fishing]
            && !Dalamud.Conditions[ConditionFlag.Casting] && !Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
            && !Dalamud.Conditions[ConditionFlag.Crafting] && !Dalamud.Conditions[ConditionFlag.PreparingToCraft]
            && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
}
