using System;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;

namespace GatherBuddy.Crafting;

public partial class CraftingQueueProcessor
{
    private CraftPreparationGate _preparationGate = new();
    private readonly DiagnosticThrottle<string> _preparationDiagnostics = new(TimeSpan.FromSeconds(5), capacity: 8);
    private CraftPreparationBlocker? _lastPreparationBlockers;
    private string _preparationDiagnosticState = string.Empty;
    private string _preparationWaitText = string.Empty;
    private bool _preparationHadWait;
    private PreCraftHomeTravelHost? _homeTravel;
    internal PreCraftHomeTravelHost? HomeTravel => _homeTravel;

    private void UpdateRetainerHomeTravel()
    {
        if (_paused || _currentState != QueueState.WaitingForRetainerTravel) return;
        if (DeferForInventory(QueueState.WaitingForRetainerTravel)) return;
        var home = _homeTravel?.Tick(allowStart: true) ?? HomeTravelAdmission.Ready;
        if (home is HomeTravelAdmission.Failed or HomeTravelAdmission.Cancelled)
        {
            Pause(_homeTravel?.Status ?? "Pre-retainer home travel cancelled.");
            return;
        }
        if (home != HomeTravelAdmission.Ready)
        {
            _preparationGate.Suspend(_runClock.Elapsed);
            RetainerCoordinationStatus = _homeTravel!.Status;
            return;
        }
        var blockers = CapturePreparationBlockers();
        var admission = _preparationGate.Tick(_runClock.Elapsed, blockers);
        RetainerCoordinationStatus = "Waiting for stable readiness before retainer bell access";
        if (admission == CraftPreparationAdmission.TimedOut)
        {
            Pause($"Pre-retainer preparation wait exceeded 120 seconds: {blockers}. Queue retained.");
            return;
        }
        if (admission != CraftPreparationAdmission.Ready) return;
        _homeTravel?.Consume();
        RetainerCoordinationStatus = string.Empty;
        BeginRetainerStage();
    }

    private void BeginRetainerStage()
    {
        // Bell discovery lives inside these deferred tasks, after arrival and AR admission.
        if (GatherBuddy.Config.VulcanRetainerBellConfig.AutoNavigateToRetainerBell)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Home travel ready; starting retainer bell navigation");
            _currentState = QueueState.NavigatingToRetainerBell;
            QueueRetainerBellNavigationTasks();
        }
        else
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Home travel ready; starting retainer withdrawal");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
        }
        StateChanged?.Invoke(_currentState);
    }

    internal void OnGatheringDeparture()
    {
        if (_homeTravel?.InvalidateForGathering() == true)
            _preparationGate.Suspend(_runClock.Elapsed);
    }

    internal static CraftPreparationBlocker CapturePreparationBlockers()
    {
        var blockers = CraftPreparationBlocker.None;
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            blockers |= CraftPreparationBlocker.ZoneTransition;
        if (!GenericHelpers.IsScreenReady()) blockers |= CraftPreparationBlocker.LoadingScreen;
        if (GetPlayerInventoryAvailability() == PlayerInventoryAvailability.Unavailable)
            blockers |= CraftPreparationBlocker.PlayerInventoryUnavailable;
        if ((blockers & CraftPreparationGate.InventoryBlockers) != 0) return blockers;
        try
        {
            if (Lifestream.Enabled && Lifestream.IsBusy()) blockers |= CraftPreparationBlocker.LifestreamBusy;
            // Observe all active nav work, including unrelated movement; never stop it
            // or claim ownership merely because crafting preparation is blocked.
            if (VNavmesh.Enabled && (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress()
                    || VNavmesh.Nav.PathfindInProgress())) blockers |= CraftPreparationBlocker.NavigationBusy;
        }
        catch (Exception) { blockers |= CraftPreparationBlocker.TravelStatusUnavailable; }
        return blockers;
    }

    private bool EnsureCraftPreparationReady()
    {
        if (_paused || _currentState is not (QueueState.WaitingForJobSwitch or QueueState.WaitingForRaphaelSolution
            or QueueState.ReadyForCraft or QueueState.WaitingForTravelCompletion)) return false;
        var blockers = CapturePreparationBlockers();
        if ((blockers & CraftPreparationGate.InventoryBlockers) != 0)
        {
            _preparationGate.Suspend(_runClock.Elapsed);
            LogPreparationWait(blockers);
            WaitForInventory(QueueState.WaitingForJobSwitch);
            return false;
        }
        var home = _homeTravel?.Tick(allowStart: true) ?? HomeTravelAdmission.Ready;
        if (home is HomeTravelAdmission.Failed or HomeTravelAdmission.Cancelled)
        {
            _tasks.Clear();
            Pause(_homeTravel?.Status ?? "Pre-craft home travel cancelled.");
            return false;
        }
        if (home != HomeTravelAdmission.Ready)
        {
            _tasks.Clear();
            _preparationGate.Suspend(_runClock.Elapsed);
            AcquisitionStatus = _homeTravel!.Status;
            if (_currentState != QueueState.WaitingForTravelCompletion)
            {
                _currentState = QueueState.WaitingForTravelCompletion;
                StateChanged?.Invoke(_currentState);
            }
            return false;
        }
        _homeTravel?.Consume();
        var admission = _preparationGate.Tick(_runClock.Elapsed, blockers);
        if (admission == CraftPreparationAdmission.Cancelled) return false;
        if (admission == CraftPreparationAdmission.Ready)
        {
            if (_preparationHadWait)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Travel/preparation blockers cleared; readiness stable for 500 ms");
                _preparationHadWait = false;
                _preparationDiagnostics.Clear();
                _lastPreparationBlockers = null;
            }
            if (_currentState != QueueState.WaitingForTravelCompletion) return true;
            // Re-select from live inventory, not a stale queued equip/craft closure.
            _tasks.Clear();
            _readinessSelected = false;
            AcquisitionStatus = "Travel complete; rechecking ready work.";
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
            return false;
        }
        LogPreparationWait(blockers);
        if (_currentState != QueueState.WaitingForTravelCompletion)
        {
            _tasks.Clear();
            var plan = _executionPlan;
            _currentState = QueueState.WaitingForTravelCompletion;
            StateChanged?.Invoke(_currentState);
            if (!ReferenceEquals(_executionPlan, plan)) return false;
        }
        if (!_paused && _currentState == QueueState.WaitingForTravelCompletion && admission == CraftPreparationAdmission.TimedOut)
            Pause($"Travel/preparation wait exceeded 120 seconds: {blockers}. Queue retained. Finish or stop travel, then Resume.");
        return false;
    }

    private void LogPreparationWait(CraftPreparationBlocker blockers)
    {
        _preparationHadWait = true;
        if (_lastPreparationBlockers != blockers)
        {
            _lastPreparationBlockers = blockers;
            _preparationDiagnosticState = ((int)blockers).ToString();
            _preparationWaitText = blockers == CraftPreparationBlocker.None
                ? "Travel complete; confirming readiness (500 ms)."
                : $"Waiting for crafting preparation: {blockers}; queue retained.";
        }
        AcquisitionStatus = _preparationWaitText;
        if (_preparationDiagnostics.TryLog("PreparationWait", _preparationDiagnosticState, out var suppressed))
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] {_preparationWaitText}{DiagnosticThrottle.Suffix(suppressed)}");
    }

    private void ResetPreparationGate()
    {
        _preparationGate.Cancel();
        _preparationGate = new();
        _preparationDiagnostics.Clear();
        _lastPreparationBlockers = null;
        _preparationHadWait = false;
    }
}
