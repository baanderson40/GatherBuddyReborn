using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public enum CraftingAcquisitionState { Planning, WaitingForVendorData, Purchasing, WaitingForCleanup, ReadyForGather, Failed, Cancelled }

public interface ICraftingAcquisitionPort
{
    MaterialAcquisitionPlanningInput? Capture(Guid session, ResolvedVendorPurchasePolicy policy, bool allowPurchasing);
    TransientVendorStartResult StartPurchases(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan,
        Dictionary<uint, uint> inventoryTargets);
    TransientVendorState PurchaseState { get; }
    TransientVendorResult? PurchaseResult { get; }
    string PurchaseStatus { get; }
    bool CanContinue(out string reason);
    void Pause(Guid session);
    void Resume(Guid session);
    void Cancel(Guid session);
}

/// <summary>One purchase pass, followed by fresh inventory reconciliation. Never starts gathering/crafting itself.</summary>
public sealed class CraftingAcquisitionCoordinator
{
    public static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(30);
    private readonly ICraftingAcquisitionPort _port;
    private readonly ResolvedVendorPurchasePolicy _policy;
    private DateTime _waitingSince;
    private DateTime _nextCapture;
    private DateTime? _pausedAt;
    private bool _startedPurchases;
    private bool _reconciling;
    public Guid SessionId { get; }
    public CraftingAcquisitionState State { get; private set; } = CraftingAcquisitionState.Planning;
    public string Status { get; private set; } = "Planning material acquisition";
    public MaterialAcquisitionPlan? PurchasePlan { get; private set; }
    public MaterialAcquisitionPlanningInput? FinalInput { get; private set; }
    public MaterialAcquisitionPlan? FinalPlan { get; private set; }
    public string? PurchaseFailureReason { get; private set; }

    public CraftingAcquisitionCoordinator(Guid session, ResolvedVendorPurchasePolicy policy,
        ICraftingAcquisitionPort port, DateTime now)
    {
        if (session == Guid.Empty) throw new ArgumentException("A crafting acquisition session is required.", nameof(session));
        SessionId = session;
        _policy = policy;
        _port = port;
        _waitingSince = now;
    }

    public void Tick(DateTime now)
    {
        if (_pausedAt.HasValue || State is CraftingAcquisitionState.ReadyForGather or CraftingAcquisitionState.Failed or CraftingAcquisitionState.Cancelled) return;
        try
        {
            if (_startedPurchases && !_reconciling)
            {
                Status = _port.PurchaseStatus;
                State = _port.PurchaseState is TransientVendorState.CleaningUp or TransientVendorState.CleanupBlocked
                    ? CraftingAcquisitionState.WaitingForCleanup : CraftingAcquisitionState.Purchasing;
                if (_port.PurchaseState != TransientVendorState.Finished) return;
                var result = _port.PurchaseResult;
                if (result == null || result.SessionId != SessionId)
                { Fail("Vendor result did not match the owning crafting session"); return; }
                if (!result.AccountingCertain || !result.CleanupSucceeded)
                { Fail("Vendor accounting or cleanup uncertain; reconcile manually before continuing"); return; }
                if (result.Completion == MaterialAcquisitionCompletion.Cancelled)
                { State = CraftingAcquisitionState.Cancelled; Status = "Material purchasing cancelled; crafting not advanced"; return; }
                if (result.Completion != MaterialAcquisitionCompletion.Completed)
                    PurchaseFailureReason = string.Join("; ", result.Allocations.Where(a => a.RemainingQuantity > 0).Select(a => a.Message).Distinct());
                _reconciling = true;
                _waitingSince = now;
                _nextCapture = now;
            }
            if (now < _nextCapture) return;
            _nextCapture = now.AddMilliseconds(500);
            var expired = now - _waitingSince >= DataTimeout;
            var input = _port.Capture(SessionId, _policy, !_reconciling && !expired);
            if (input == null)
            {
                if (expired) Fail("Player inventory unavailable for acquisition reconciliation");
                else { State = CraftingAcquisitionState.WaitingForVendorData; Status = "Waiting for player inventory"; }
                return;
            }
            if (input.SessionId != SessionId) { Fail("Acquisition capture belongs to another session"); return; }
            var plan = MaterialAcquisitionPlanner.Plan(input);
            if (plan.Status == AcquisitionPlanningStatus.WaitingForVendorData)
            { State = CraftingAcquisitionState.WaitingForVendorData; Status = "Waiting for vendor caches (30 second limit)"; return; }
            if (!_reconciling)
            {
                if (!expired || PurchasePlan == null) PurchasePlan = plan;
                if (expired) PurchaseFailureReason ??= "Vendor data or purchase availability timed out; no further purchase attempt made.";
                if (plan.Allocations.Count > 0)
                {
                    var start = _port.StartPurchases(input, plan, BuildInventoryTargets(input, plan));
                    Status = $"Vendor purchase start: {start}. {_port.PurchaseStatus}";
                    if (start != TransientVendorStartResult.Started) PurchaseFailureReason = Status;
                    if (start == TransientVendorStartResult.Started)
                    { PurchaseFailureReason = null; _startedPurchases = true; State = CraftingAcquisitionState.Purchasing; return; }
                    if (!expired && start is TransientVendorStartResult.Unavailable or TransientVendorStartResult.Busy)
                    { State = CraftingAcquisitionState.WaitingForVendorData; return; }
                    if (start == TransientVendorStartResult.InvalidBatch)
                    { Fail("Invalid transient purchase batch; crafting not advanced"); return; }
                }
                // Re-capture with purchases disabled: even a failed/partial offer is attempted at most once.
                _reconciling = true;
                _waitingSince = now;
                _nextCapture = now;
                return;
            }
            if (!_port.CanContinue(out var reason)) { Fail(reason); return; }
            FinalInput = input;
            FinalPlan = plan;
            State = CraftingAcquisitionState.ReadyForGather;
            Status = $"Purchases reconciled; gather={plan.Materials.Count(m => m.GatheringDemand.Total > 0)} types; unresolved={plan.Materials.Count(m => m.UnresolvedDemand.Total > 0)} types";
        }
        catch (Exception ex)
        {
            if (_startedPurchases) _port.Cancel(SessionId);
            Fail($"Acquisition failed: {ex.Message}");
        }
    }

    public static Dictionary<uint, uint> BuildInventoryTargets(MaterialAcquisitionPlanningInput input, MaterialAcquisitionPlan plan)
        => plan.Allocations.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g =>
        {
            var owned = input.Inventory.GetValueOrDefault(g.Key);
            return g.Aggregate(checked((uint)owned.NQ + (uint)owned.HQ), (target, allocation) => checked(target + allocation.RequestedOutputQuantity));
        });

    public Dictionary<uint, int> BuildGatherTargets()
    {
        if (FinalInput == null || FinalPlan == null) throw new InvalidOperationException("Acquisition has not reconciled.");
        // The gather bridge compares NQ bag counts. HQ already satisfied compatible demand in the planner.
        return FinalPlan.Materials.Where(m => m.GatheringDemand.Total > 0).ToDictionary(m => m.ItemId,
            m => checked(FinalInput.Inventory.GetValueOrDefault(m.ItemId).NQ + m.GatheringDemand.Total));
    }

    public void Pause(DateTime now)
    {
        if (_pausedAt.HasValue) return;
        _pausedAt = now;
        if (_startedPurchases) _port.Pause(SessionId);
    }
    public void Resume(DateTime now)
    {
        if (_pausedAt is not { } since) return;
        _waitingSince += now - since;
        _pausedAt = null;
        if (_startedPurchases) _port.Resume(SessionId);
    }
    public void Cancel()
    {
        if (_startedPurchases) _port.Cancel(SessionId);
        State = CraftingAcquisitionState.Cancelled;
        Status = "Material acquisition cancelled; no gathering/crafting transition authorized";
    }
    private void Fail(string reason) { State = CraftingAcquisitionState.Failed; Status = reason; }
}
