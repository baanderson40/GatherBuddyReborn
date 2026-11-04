using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

public enum TransientVendorStartResult { Started, Busy, InvalidBatch, PurchasingDisabled, Unavailable }
public enum TransientVendorState { Idle, Ready, Purchasing, CleaningUp, Paused, CleanupBlocked, Finished }
public enum TransientPurchaseStart { Started, Skipped, Unsafe }
public sealed record TransientPurchaseReceipt(Guid SessionId, Guid AllocationId, uint AcquiredQuantity,
    ulong? ConfirmedSpend, bool Interrupted, bool Fatal, string Message)
{
    public IReadOnlyDictionary<uint, ulong>? ConfirmedCosts { get; init; }
    // Native adapter may reduce a request to the supported material stack size.
    public uint? SubmittedOutputQuantity { get; init; }
    public bool PreventVendorFallback { get; init; }
}
public sealed record TransientAllocationResult(Guid AllocationId, uint ItemId, uint RequestedQuantity,
    uint AcquiredQuantity, uint RemainingQuantity, ulong? ConfirmedSpend, string Message);
public sealed record TransientVendorResult(Guid SessionId, MaterialAcquisitionCompletion Completion,
    IReadOnlyList<TransientAllocationResult> Allocations, IReadOnlyDictionary<uint, ulong> SettledSpending,
    bool CleanupSucceeded, bool AccountingCertain);

/// <summary>Native side effects live behind this boundary. Each request is at most one existing-engine batch.</summary>
public interface ITransientVendorPort
{
    bool TryAcquire(Guid sessionId, out string reason);
    bool IsSafe(out string reason);
    uint ReadItemCount(uint itemId);
    TransientPurchaseStart TryStart(Guid sessionId, VendorPurchaseAllocation allocation, uint quantity,
        uint inventoryTarget, TransientPurchaseBudget budget, out string reason);
    TransientPurchaseReceipt? Poll(Guid sessionId);
    void Pause(Guid sessionId);
    void Cancel(Guid sessionId);
    bool TryCleanup(Guid sessionId);
    void Release(Guid sessionId);
    bool CanContinueInteraction(Guid sessionId, VendorPurchaseAllocation previous, VendorPurchaseAllocation next) => false;
}

/// <summary>Transient sequencing only. Does not create saved lists or advance gathering/crafting.</summary>
public sealed class TransientVendorExecution
{
    private readonly ITransientVendorPort _port;
    private readonly List<TransientAllocationResult> _results = new();
    private readonly Dictionary<uint, ulong> _spent = new();
    private readonly HashSet<Guid> _startedSessions = new();
    private IReadOnlyList<VendorPurchaseAllocation> _allocations = Array.Empty<VendorPurchaseAllocation>();
    private IReadOnlyDictionary<uint, uint> _targets = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>());
    private ResolvedVendorPurchasePolicy? _policy;
    private AcquisitionTrace? _trace;
    private int _index;
    private uint _acquired;
    private ulong _allocationSpend;
    private readonly Dictionary<uint, ulong> _allocationCosts = new();
    private VendorPurchaseAllocation? _alternative;
    private readonly HashSet<VendorNpc> _attemptedVendors = new();
    private uint _activeRequested;
    private bool _pause;
    private bool _cancel;
    private bool _fatal;
    private bool _uncertain;
    private bool _skipAllocation;
    private bool _cleanupFault;
    private bool _targetSatisfied;
    private DateTime _cleanupStarted;
    private string _allocationMessage = string.Empty;
    public Guid SessionId { get; private set; }
    public TransientVendorState State { get; private set; }
    public bool IsBusy => State is not (TransientVendorState.Idle or TransientVendorState.Finished);
    public string Status { get; private set; } = "Idle";
    public TransientVendorResult? LastResult { get; private set; }
    public AcquisitionTrace? Trace => _trace;

    public TransientVendorExecution(ITransientVendorPort port) => _port = port;

    public TransientVendorStartResult Start(Guid sessionId, ResolvedVendorPurchasePolicy policy,
        IEnumerable<VendorPurchaseAllocation> allocations, IDictionary<uint, uint> inventoryTargets)
    {
        if (IsBusy) return TransientVendorStartResult.Busy;
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(inventoryTargets);
        if (!policy.Enabled) return TransientVendorStartResult.PurchasingDisabled;
        var batch = allocations.ToList();
        if (sessionId == Guid.Empty || _startedSessions.Contains(sessionId) || batch.Count == 0 || batch.Any(a => a.SessionId != sessionId
                || a.Costs.Any(c => !policy.Allows(c.CurrencyGroup))
                || (a.CurrencyGroup == VendorCurrencyGroup.Gil) != (a.CurrencyItemId == 1)
                || !inventoryTargets.TryGetValue(a.ItemId, out var target) || target == 0)
            || batch.Select(a => a.AllocationId).Distinct().Count() != batch.Count)
            return TransientVendorStartResult.InvalidBatch;
        try
        {
            foreach (var group in batch.SelectMany(a => a.ExpectedCosts).GroupBy(c => c.Key))
                _ = group.Aggregate(0ul, (sum, cost) => checked(sum + cost.Value));
        }
        catch (OverflowException) { return TransientVendorStartResult.InvalidBatch; }
        if (!_port.TryAcquire(sessionId, out var reason))
        {
            Status = reason;
            return TransientVendorStartResult.Unavailable;
        }
        SessionId = sessionId;
        _startedSessions.Add(sessionId);
        _policy = policy;
        _allocations = batch.AsReadOnly();
        _targets = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>(inventoryTargets));
        _results.Clear();
        _spent.Clear();
        _index = 0;
        _acquired = 0;
        _allocationSpend = 0;
        _allocationCosts.Clear();
        _alternative = null;
        _attemptedVendors.Clear();
        _pause = _cancel = _fatal = _uncertain = _skipAllocation = _cleanupFault = false;
        _targetSatisfied = false;
        _allocationMessage = string.Empty;
        LastResult = null;
        _trace = new(sessionId);
        Record("Start", $"allocations={batch.Count}; savedListsUntouched=True");
        State = TransientVendorState.Ready;
        Status = "Transient purchase batch ready";
        return TransientVendorStartResult.Started;
    }

    public void Tick(DateTime now)
    {
        if (!IsBusy) return;
        try
        {
            if (State == TransientVendorState.Purchasing)
            {
                if (!_port.IsSafe(out var unsafeReason))
                {
                    _fatal = true;
                    _allocationMessage = unsafeReason;
                    _port.Cancel(SessionId);
                }
                var receipt = _port.Poll(SessionId);
                if (receipt == null) return;
                var allocation = _alternative ?? _allocations[_index];
                if (receipt.SessionId != SessionId || receipt.AllocationId != allocation.AllocationId)
                {
                    Record("StaleResult", "Ignored mismatched session/allocation receipt");
                    return;
                }
                var transactions = receipt.AcquiredQuantity / allocation.OutputQuantityPerTransaction;
                var actualCosts = receipt.ConfirmedCosts ?? (allocation.Costs.Count == 1 && receipt.ConfirmedSpend.HasValue
                    ? new Dictionary<uint, ulong> { [allocation.CurrencyItemId] = receipt.ConfirmedSpend.Value } : null);
                var submitted = receipt.SubmittedOutputQuantity ?? _activeRequested;
                var receiptCertain = submitted > 0 && submitted <= _activeRequested
                    && submitted % allocation.OutputQuantityPerTransaction == 0
                    && receipt.AcquiredQuantity <= submitted
                    && receipt.AcquiredQuantity % allocation.OutputQuantityPerTransaction == 0
                    && actualCosts != null && actualCosts.Count == allocation.Costs.Count
                    && allocation.Costs.All(c => actualCosts.GetValueOrDefault(c.CurrencyItemId) == checked(c.Amount * transactions));
                if (!receiptCertain)
                    _uncertain = true;
                _acquired = checked(_acquired + Math.Min(receipt.AcquiredQuantity, _activeRequested));
                if (receiptCertain)
                {
                    foreach (var cost in actualCosts!)
                    {
                        _spent[cost.Key] = checked(_spent.GetValueOrDefault(cost.Key) + cost.Value);
                        _allocationCosts[cost.Key] = checked(_allocationCosts.GetValueOrDefault(cost.Key) + cost.Value);
                    }
                    _allocationSpend = _allocationCosts.GetValueOrDefault(allocation.CurrencyItemId);
                }
                _fatal |= receipt.Fatal || _uncertain;
                _allocationMessage = receipt.Message;
                _skipAllocation = receipt.AcquiredQuantity < submitted && !receipt.Interrupted;
                if (_skipAllocation && receipt.AcquiredQuantity == 0 && !_fatal && !_cancel && !receipt.Interrupted && !receipt.PreventVendorFallback)
                    TryNextAuthorizedVendor();
                Record("Receipt", $"allocation={allocation.AllocationId}; acquired={receipt.AcquiredQuantity}; confirmedSpend={receipt.ConfirmedSpend?.ToString() ?? "unknown"}; certain={!_uncertain}; message={receipt.Message}");
                var finished = _skipAllocation || _acquired >= allocation.RequestedOutputQuantity;
                var nextIndex = finished ? _index + 1 : _index;
                if (!_cancel && !_fatal && !_pause && !receipt.Interrupted && !_skipAllocation
                    && receipt.AcquiredQuantity > 0 && nextIndex < _allocations.Count
                    && _port.CanContinueInteraction(SessionId, allocation, nextIndex == _index ? _alternative ?? allocation : _allocations[nextIndex]))
                {
                    if (finished) FinishAllocation();
                    State = TransientVendorState.Ready;
                    Status = "Keeping owned shop open; revalidating next batch";
                    Record("KeepShopOpen", "Settled transaction; compatible owned vendor context retained");
                    return;
                }
                BeginCleanup(now);
                return;
            }
            if (State is TransientVendorState.CleaningUp or TransientVendorState.CleanupBlocked)
            {
                if (!_port.TryCleanup(SessionId))
                {
                    if ((now - _cleanupStarted).TotalSeconds >= 15)
                    {
                        _fatal = _cleanupFault = true;
                        State = TransientVendorState.CleanupBlocked;
                        Status = "Vendor cleanup blocked. Ownership retained; close vendor interaction to finish safely.";
                    }
                    return;
                }
                Record("Cleanup", "Shop closed; navigation stopped; inventory/accounting settled");
                if (_index < _allocations.Count && (_skipAllocation || _acquired >= _allocations[_index].RequestedOutputQuantity))
                    FinishAllocation();
                if (_cancel || _fatal || _index >= _allocations.Count) { Finish(); return; }
                State = _pause ? TransientVendorState.Paused : TransientVendorState.Ready;
                Status = _pause ? "Paused after safe cleanup" : "Ready for next purchase batch";
                return;
            }
            if (_cancel || _fatal) { BeginCleanup(now); return; }
            if (_pause) { State = TransientVendorState.Paused; return; }
            if (_index >= _allocations.Count) { BeginCleanup(now); return; }
            if (!_port.IsSafe(out var reason))
            {
                _fatal = true;
                _allocationMessage = reason;
                BeginCleanup(now);
                return;
            }
            State = TransientVendorState.Ready;
            var current = _alternative ?? _allocations[_index];
            var owned = _port.ReadItemCount(current.ItemId);
            var target = _targets[current.ItemId];
            var needed = target > owned ? target - owned : 0;
            var transactionsNeeded = ExchangeTransactions.Needed(needed, current.OutputQuantityPerTransaction);
            var transactionsLeft = (current.RequestedOutputQuantity - _acquired) / current.OutputQuantityPerTransaction;
            var quantity = checked(Math.Min(99u, Math.Min(transactionsLeft, transactionsNeeded)) * current.OutputQuantityPerTransaction);
            if (quantity == 0)
            {
                _targetSatisfied = needed == 0;
                _allocationMessage = "Inventory target already satisfied or allocation fulfilled";
                FinishAllocation();
                return;
            }
            var budgets = new Dictionary<uint, TransientPurchaseBudget>();
            foreach (var cost in current.Costs)
            {
                var other = _allocations.Skip(_index + 1).Aggregate(0ul, (sum, a) => checked(sum + a.ExpectedCosts.GetValueOrDefault(cost.CurrencyItemId)));
                var maximumCost = current.ExpectedCosts[cost.CurrencyItemId] - _allocationCosts.GetValueOrDefault(cost.CurrencyItemId);
                if (cost.CurrencyItemId == 1 && _policy!.MaximumGilSpendPerRun is { } cap)
                {
                    var used = _spent.GetValueOrDefault(1u);
                    var remainingCap = cap > used ? cap - used : 0;
                    remainingCap = remainingCap > other ? remainingCap - other : 0;
                    maximumCost = Math.Min(maximumCost, remainingCap);
                }
                budgets[cost.CurrencyItemId] = new(_policy!.GetReserve(cost.CurrencyItemId), other, maximumCost);
            }
            var budget = budgets[current.CurrencyItemId] with { CurrencyBudgets = new ReadOnlyDictionary<uint, TransientPurchaseBudget>(budgets) };
            _activeRequested = quantity;
            _attemptedVendors.Add(current.Vendor);
            var start = _port.TryStart(SessionId, current, quantity, target, budget, out var startReason);
            Record("Request", $"allocation={current.AllocationId}; item={current.ItemId}; quantity={quantity}; otherCommitments={budget.OtherCommitments}; maximumSpend={budget.MaximumSpend}; result={start}; reason={startReason}");
            Status = startReason;
            if (start == TransientPurchaseStart.Started) State = TransientVendorState.Purchasing;
            else
            {
                _allocationMessage = startReason;
                _skipAllocation = true;
                _fatal |= start == TransientPurchaseStart.Unsafe;
                if (!_fatal && !_cancel) TryNextAuthorizedVendor();
                BeginCleanup(now);
            }
        }
        catch (Exception ex)
        {
            _fatal = _uncertain = true;
            _allocationMessage = $"Execution exception: {ex.Message}";
            Record("Exception", _allocationMessage);
            _port.Cancel(SessionId);
            BeginCleanup(now);
        }
    }

    public void Pause()
    {
        if (!IsBusy) return;
        _pause = true;
        _port.Pause(SessionId);
        if (State == TransientVendorState.Ready) BeginCleanup(DateTime.UtcNow);
        Record("Pause", "No new batch will start; submitted transaction must settle");
    }
    public void Resume()
    {
        if (!IsBusy || _cancel || _fatal) return;
        _pause = false;
        if (State == TransientVendorState.Paused) State = TransientVendorState.Ready;
        Record("Resume", "Revalidate inventory, offer, and currency before next batch");
    }
    public void Cancel()
    {
        if (!IsBusy) return;
        _cancel = true;
        _port.Cancel(SessionId);
        if (State != TransientVendorState.Purchasing) BeginCleanup(DateTime.UtcNow);
        Record("Cancel", "Owned work cancelled; awaiting cleanup before release");
    }

    private void BeginCleanup(DateTime now)
    {
        State = TransientVendorState.CleaningUp;
        _cleanupStarted = now;
        Status = "Waiting for vendor interaction and purchase settlement";
    }

    private void TryNextAuthorizedVendor()
    {
        var original = _allocations[_index];
        var next = original.AuthorizedAlternatives.FirstOrDefault(v => !_attemptedVendors.Contains(v));
        if (next == null) return;
        _alternative = original.WithAuthorizedVendor(next);
        _skipAllocation = false;
        Record("AlternativeVendor", $"allocation={original.AllocationId}; vendor={next.NpcId}; originalCostsAndOutputUnchanged=True; plannedAuthorization=True");
    }
    private void FinishAllocation()
    {
        var current = _allocations[_index];
        _results.Add(new(current.AllocationId, current.ItemId, current.RequestedOutputQuantity, _acquired,
            _targetSatisfied ? 0 : current.RequestedOutputQuantity - _acquired, _uncertain ? null : _allocationSpend, _allocationMessage));
        Record("AllocationFinished", $"allocation={current.AllocationId}; acquired={_acquired}; unspentReservationReleased={!_uncertain}");
        _index++;
        _acquired = 0;
        _allocationSpend = 0;
        _allocationCosts.Clear();
        _alternative = null;
        _attemptedVendors.Clear();
        _skipAllocation = false;
        _targetSatisfied = false;
        _allocationMessage = string.Empty;
    }
    private void Finish()
    {
        if (_index < _allocations.Count) FinishAllocation();
        while (_index < _allocations.Count)
        {
            _allocationMessage = "Not executed: batch stopped";
            FinishAllocation();
        }
        var completion = _cancel ? MaterialAcquisitionCompletion.Cancelled
            : _fatal ? MaterialAcquisitionCompletion.Failed
            : _results.Any(r => r.RemainingQuantity > 0) ? MaterialAcquisitionCompletion.PartiallyFulfilled
            : MaterialAcquisitionCompletion.Completed;
        LastResult = new(SessionId, completion, _results.ToList().AsReadOnly(),
            new ReadOnlyDictionary<uint, ulong>(new Dictionary<uint, ulong>(_spent)), !_cleanupFault, !_uncertain);
        _port.Release(SessionId);
        State = TransientVendorState.Finished;
        Status = $"Transient purchases: {completion}; accountingCertain={!_uncertain}";
        Record("Finished", Status);
        _trace?.Complete(Status);
    }
    private void Record(string action, string detail) => _trace?.Record("P3", action, detail);
}
