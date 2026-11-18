using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GatherBuddy.Plugin;

internal sealed class VendorPurchaseIpcOperations : IDisposable
{
    private const int RequestVersion = 1;
    private const int MaximumItemsPerRequest = 50;
    private const int MaximumRecentOperations = 128;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _gate = new();
    private readonly Action<string> _onCompleted;
    private readonly Dictionary<Guid, Operation> _operations = [];
    private readonly Dictionary<RequestKey, Guid> _requests = [];
    private readonly Queue<Guid> _operationOrder = [];
    private Guid? _activeOperationId;
    private bool _disposed;

    private readonly record struct RequestKey(string Caller, string RequestId);
    private sealed record ItemRequest(uint ItemId, uint Quantity);
    private sealed record ParsedRequest(string Caller, string RequestId, IReadOnlyList<ItemRequest> Items);

    private sealed class Operation(Guid id, ParsedRequest request)
    {
        public Guid Id { get; } = id;
        public ParsedRequest Request { get; } = request;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public string State { get; set; } = "Queued";
        public string? Message { get; set; }
        public bool CancelRequested { get; set; }
        public DateTime StartedAt { get; set; }
        public string? Completion { get; set; }
        public bool AccountingCertain { get; set; } = true;
        public bool CleanupSucceeded { get; set; }
        public Dictionary<uint, uint> Acquired { get; } = [];
        public Dictionary<uint, ulong?> ConfirmedSpend { get; } = [];
        public Dictionary<uint, string> ItemMessages { get; } = [];
        public ulong? TotalConfirmedGil { get; set; }
        public bool ResultAvailable { get; set; }
        public bool IsTerminal => State is "Completed" or "PartiallyFulfilled" or "Cancelled" or "Failed" or "Rejected";
    }

    public VendorPurchaseIpcOperations(Action<string> onCompleted)
    {
        _onCompleted = onCompleted;
    }

    public string Start(string requestJson)
    {
        if (!TryParseRequest(requestJson, out var request, out var error))
            return JsonSerializer.Serialize(new { accepted = false, errorCode = "InvalidRequest", error }, JsonOptions);

        lock (_gate)
        {
            if (_disposed)
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "Unavailable", error = "Vendor purchase IPC is shutting down." }, JsonOptions);

            var key = new RequestKey(request!.Caller, request.RequestId);
            if (_requests.TryGetValue(key, out var existingId) && _operations.TryGetValue(existingId, out var existing))
                return JsonSerializer.Serialize(new { accepted = true, duplicate = true, operationId = existing.Id, state = existing.State }, JsonOptions);

            PruneHistory();
            if (_operations.Count >= MaximumRecentOperations)
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "HistoryFull", error = "Recent operation history is full; retry after an operation completes." }, JsonOptions);
            if (_operations.Values.Any(operation => !operation.IsTerminal))
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "Busy", error = "Another IPC vendor purchase request is active; requests are not queued." }, JsonOptions);

            var id = Guid.NewGuid();
            var operation = new Operation(id, request);
            _operations.Add(id, operation);
            _requests[key] = id;
            _operationOrder.Enqueue(id);
            return JsonSerializer.Serialize(new { accepted = true, duplicate = false, operationId = id, state = operation.State }, JsonOptions);
        }
    }

    public string GetStatus(string operationId)
    {
        if (!Guid.TryParse(operationId, out var id))
            return JsonSerializer.Serialize(new { found = false, errorCode = "InvalidOperationId" }, JsonOptions);

        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
                return JsonSerializer.Serialize(new { found = false, errorCode = "NotFound" }, JsonOptions);
            return JsonSerializer.Serialize(StatusView(operation), JsonOptions);
        }
    }

    public string GetResult(string operationId)
    {
        if (!Guid.TryParse(operationId, out var id))
            return JsonSerializer.Serialize(new { found = false, errorCode = "InvalidOperationId" }, JsonOptions);

        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
                return JsonSerializer.Serialize(new { found = false, errorCode = "NotFound" }, JsonOptions);
            if (!operation.IsTerminal || !operation.ResultAvailable)
                return JsonSerializer.Serialize(new { found = true, available = false, operationId = id, state = operation.State }, JsonOptions);

            return JsonSerializer.Serialize(new
            {
                found = true,
                available = true,
                operationId = id,
                caller = operation.Request.Caller,
                requestId = operation.Request.RequestId,
                completion = operation.Completion,
                accountingCertain = operation.AccountingCertain,
                cleanupSucceeded = operation.CleanupSucceeded,
                totalConfirmedGil = operation.TotalConfirmedGil,
                items = operation.Request.Items.Select(item => new
                {
                    itemId = item.ItemId,
                    requestedQuantity = item.Quantity,
                    acquiredQuantity = operation.Acquired.GetValueOrDefault(item.ItemId),
                    remainingQuantity = item.Quantity - operation.Acquired.GetValueOrDefault(item.ItemId),
                    confirmedGil = operation.ConfirmedSpend.GetValueOrDefault(item.ItemId),
                    message = operation.ItemMessages.GetValueOrDefault(item.ItemId),
                }),
                message = operation.Message,
            }, JsonOptions);
        }
    }

    public string Cancel(string operationId)
    {
        if (!Guid.TryParse(operationId, out var id))
            return JsonSerializer.Serialize(new { accepted = false, errorCode = "InvalidOperationId" }, JsonOptions);

        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation))
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "NotFound" }, JsonOptions);
            if (operation.IsTerminal)
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "Terminal", state = operation.State }, JsonOptions);
            if (operation.CancelRequested)
                return JsonSerializer.Serialize(new { accepted = true, duplicate = true, operationId = id, state = operation.State }, JsonOptions);

            operation.CancelRequested = true;
            return JsonSerializer.Serialize(new { accepted = true, duplicate = false, operationId = id, state = operation.State }, JsonOptions);
        }
    }

    /// <summary>Runs from the plugin framework update; all native vendor work stays on that thread.</summary>
    public void Update()
    {
        Operation? operation;
        lock (_gate)
        {
            if (_disposed) return;
            if (_activeOperationId is { } activeId && _operations.TryGetValue(activeId, out operation))
            {
                // Existing owner remains active until purchase settlement and cleanup complete.
            }
            else
            {
                operation = _operations.Values.FirstOrDefault(candidate => !candidate.IsTerminal);
                if (operation == null) return;
                _activeOperationId = operation.Id;
            }
        }

        try
        {
            if (operation.CancelRequested)
            {
                if (GatherBuddy.TransientVendorPurchases.Execution.SessionId == operation.Id
                    && GatherBuddy.TransientVendorPurchases.CancelForIpc(operation.Id))
                {
                    lock (_gate) operation.State = "CleaningUp";
                    SyncExecution(operation);
                    return;
                }

                if (GatherBuddy.TransientVendorPurchases.Execution.SessionId != operation.Id)
                {
                    CompleteWithoutExecution(operation, "Cancelled", "Cancelled before vendor execution began.");
                    return;
                }
            }

            if (GatherBuddy.TransientVendorPurchases.Execution.SessionId == operation.Id)
            {
                SyncExecution(operation);
                return;
            }

            if (GatherBuddy.TransientVendorPurchases.Execution.IsBusy)
            {
                CompleteWithoutExecution(operation, "Rejected", "Another vendor operation owns automation; requests are not queued behind it.");
                return;
            }

            PrepareAndStart(operation);
        }
        catch (Exception ex)
        {
            CompleteWithoutExecution(operation, "Failed", $"Vendor purchase request failed safely: {ex.Message}");
        }
    }

    private void PrepareAndStart(Operation operation)
    {
        if (DateTime.UtcNow - operation.CreatedAt > TimeSpan.FromMinutes(2))
        {
            CompleteWithoutExecution(operation, "Failed", "Vendor offer data did not become ready before the request expired.");
            return;
        }

        VendorShopResolver.InitializeAsync();
        if (VendorShopResolver.IsInitialized)
            VendorNpcLocationCache.InitializeAsync(VendorShopResolver.AllVendorNpcIds);
        var offersSnapshot = IntegratedMaterialOfferAdapter.Capture(operation.Request.Items.Select(item => item.ItemId));
        if (offersSnapshot.Status == VendorOfferDataStatus.Loading)
        {
            lock (_gate)
            {
                operation.State = "WaitingForVendorData";
                operation.Message = "Loading vendor offers and locations.";
            }
            return;
        }
        if (offersSnapshot.Status == VendorOfferDataStatus.Unavailable)
        {
            CompleteWithoutExecution(operation, "Failed", "Vendor offer or location data is unavailable.");
            return;
        }

        var gilOffers = offersSnapshot.Offers
            .Where(offer => offer.CurrencyGroup == VendorCurrencyGroup.Gil && offer.CurrencyItemId == AutomaticVendorCurrencyCatalog.GilCurrencyItemId)
            .ToArray();
        var policySettings = new AutomaticVendorPurchasePolicy
        {
            Enabled = true,
            AllowedCurrencyGroups = [VendorCurrencyGroup.Gil],
            CurrencyPriority = [VendorCurrencyGroup.Gil],
            AllowedCurrencyItemIds = [],
            CurrencyReservesByItemId = [],
            MaximumSpendPerRunByCurrencyItemId = [],
            MaximumGilSpendPerRun = null,
            GrandCompanySealReserve = 0,
        };
        var listSettings = new VendorPurchaseListSettings
        {
            Mode = VendorPurchaseOverrideMode.Custom,
            CustomPolicy = policySettings,
        };
        var trace = new AcquisitionTrace(operation.Id);
        var policy = ResolvedVendorPurchasePolicy.Resolve(null, listSettings, trace, temporary: true);
        var materials = operation.Request.Items.Select(item => new AcquisitionMaterialDemand(
            item.ItemId, IngredientQualityDemand.FromPreferNQ(checked((int)item.Quantity)), AllowPurchasing: true)).ToArray();
        var balances = new Dictionary<uint, ulong>
        {
            [AutomaticVendorCurrencyCatalog.GilCurrencyItemId] = VendorCurrencyAvailabilityResolver.Resolve(
                VendorCurrencyGroup.Gil, AutomaticVendorCurrencyCatalog.GilCurrencyItemId, "Gil").AvailableAmount,
        };
        var input = new MaterialAcquisitionPlanningInput(operation.Id, policy, materials, gilOffers,
            new Dictionary<uint, AcquisitionInventory>(), balances,
            new AcquisitionCurrencyLedgerSnapshot(new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>()));
        var plan = MaterialAcquisitionPlanner.Plan(input, trace);
        if (plan.Allocations.Count == 0)
        {
            var shortage = plan.Materials.SelectMany(material => material.Shortages).Select(shortage => shortage.Detail).FirstOrDefault();
            CompleteWithoutExecution(operation, "Failed", shortage ?? "No eligible gil vendor purchase could be planned.");
            return;
        }

        var targets = new Dictionary<uint, uint>();
        foreach (var item in operation.Request.Items)
        {
            var current = GatherBuddy.TransientVendorPurchases.ReadItemCount(item.ItemId);
            if ((ulong)current + item.Quantity > uint.MaxValue)
            {
                CompleteWithoutExecution(operation, "Failed", $"Requested inventory target exceeds supported quantity for item {item.ItemId}.");
                return;
            }
            targets[item.ItemId] = current + item.Quantity;
        }

        var start = GatherBuddy.TransientVendorPurchases.StartForIpc(operation.Id, policy, plan.Allocations, targets);
        if (start != TransientVendorStartResult.Started)
        {
            var reason = start switch
            {
                TransientVendorStartResult.Busy => "Another vendor operation owns automation; requests are not queued behind it.",
                TransientVendorStartResult.InvalidBatch => "Gil vendor plan failed execution validation.",
                TransientVendorStartResult.PurchasingDisabled => "Gil purchasing is not enabled for this request.",
                _ => "Player state, dependencies, or automation ownership are not ready.",
            };
            CompleteWithoutExecution(operation, "Rejected", reason);
            return;
        }

        lock (_gate)
        {
            operation.State = "Purchasing";
            operation.StartedAt = DateTime.UtcNow;
            operation.Message = "Gil purchase plan accepted; execution and cleanup in progress.";
        }
    }

    private void SyncExecution(Operation operation)
    {
        var execution = GatherBuddy.TransientVendorPurchases.Execution;
        lock (_gate)
        {
            operation.State = execution.State switch
            {
                TransientVendorState.CleaningUp or TransientVendorState.CleanupBlocked => "CleaningUp",
                TransientVendorState.Paused => "Paused",
                _ => execution.IsBusy ? "Purchasing" : operation.State,
            };
            operation.Message = execution.Status;
        }

        if (execution.State != TransientVendorState.Finished || execution.LastResult is not { } result || result.SessionId != operation.Id)
            return;

        var acquired = result.Allocations.GroupBy(allocation => allocation.ItemId)
            .ToDictionary(group => group.Key, group => group.Aggregate(0u, (sum, item) => checked(sum + item.AcquiredQuantity)));
        var spend = result.Allocations.GroupBy(allocation => allocation.ItemId).ToDictionary(group => group.Key, group =>
        {
            if (group.Any(allocation => !allocation.ConfirmedSpend.HasValue))
                return (ulong?)null;
            return group.Aggregate(0ul, (sum, allocation) => checked(sum + allocation.ConfirmedSpend!.Value));
        });
        var completion = result.Completion.ToString();
        if (operation.Request.Items.Any(item => acquired.GetValueOrDefault(item.ItemId) < item.Quantity)
            && result.Completion == MaterialAcquisitionCompletion.Completed)
            completion = "PartiallyFulfilled";

        lock (_gate)
        {
            foreach (var item in operation.Request.Items)
            {
                operation.Acquired[item.ItemId] = acquired.GetValueOrDefault(item.ItemId);
                operation.ConfirmedSpend[item.ItemId] = spend.GetValueOrDefault(item.ItemId);
                var allocationMessages = result.Allocations.Where(allocation => allocation.ItemId == item.ItemId)
                    .Select(allocation => allocation.Message).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct();
                operation.ItemMessages[item.ItemId] = string.Join("; ", allocationMessages);
            }
            operation.Completion = completion;
            operation.AccountingCertain = result.AccountingCertain;
            operation.CleanupSucceeded = result.CleanupSucceeded;
            operation.TotalConfirmedGil = result.AccountingCertain
                ? result.SettledSpending.GetValueOrDefault(AutomaticVendorCurrencyCatalog.GilCurrencyItemId)
                : null;
            operation.Message = result.Completion.ToString();
            operation.State = completion;
            operation.ResultAvailable = true;
            if (_activeOperationId == operation.Id) _activeOperationId = null;
        }
        RaiseCompleted(operation.Id);
    }

    private void CompleteWithoutExecution(Operation operation, string state, string message)
    {
        lock (_gate)
        {
            if (operation.IsTerminal) return;
            operation.State = state;
            operation.Completion = state;
            operation.Message = message;
            operation.ResultAvailable = true;
            operation.CleanupSucceeded = true;
            operation.AccountingCertain = true;
            operation.TotalConfirmedGil = 0;
            foreach (var item in operation.Request.Items)
            {
                operation.Acquired[item.ItemId] = 0;
                operation.ConfirmedSpend[item.ItemId] = 0;
                operation.ItemMessages[item.ItemId] = message;
            }
            if (_activeOperationId == operation.Id) _activeOperationId = null;
        }
        RaiseCompleted(operation.Id);
    }

    private void RaiseCompleted(Guid operationId)
    {
        try { _onCompleted(JsonSerializer.Serialize(new { operationId }, JsonOptions)); }
        catch (Exception ex) { Dalamud.Log.Warning($"[VendorPurchaseIPC] Completion callback failed: {ex.Message}"); }
    }

    private static object StatusView(Operation operation)
        => new
        {
            found = true,
            operationId = operation.Id,
            caller = operation.Request.Caller,
            requestId = operation.Request.RequestId,
            state = operation.State,
            createdAt = operation.CreatedAt,
            startedAt = operation.StartedAt == default ? (DateTime?)null : operation.StartedAt,
            cancelRequested = operation.CancelRequested,
            message = operation.Message,
        };

    private bool TryParseRequest(string json, out ParsedRequest? request, out string error)
    {
        request = null;
        error = "Request JSON is required.";
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_768) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "Request must be a JSON object."; return false; }
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "version", "caller", "requestId", "currency", "items" };
            var unknown = root.EnumerateObject().FirstOrDefault(property => !allowed.Contains(property.Name));
            if (unknown.Name != null) { error = $"Unsupported request field '{unknown.Name}'. No budgets or other currencies are accepted."; return false; }

            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var versionValue) || versionValue != RequestVersion)
            { error = $"Request version must be {RequestVersion}."; return false; }
            if (!root.TryGetProperty("caller", out var callerElement) || callerElement.ValueKind != JsonValueKind.String)
            { error = "Caller name is required."; return false; }
            var caller = callerElement.GetString()?.Trim() ?? string.Empty;
            if (caller.Length is < 1 or > 128 || caller.Any(char.IsControl)) { error = "Caller name must be 1–128 printable characters."; return false; }
            if (!root.TryGetProperty("requestId", out var requestIdElement) || requestIdElement.ValueKind != JsonValueKind.String)
            { error = "Caller requestId is required for idempotency."; return false; }
            var requestId = requestIdElement.GetString()?.Trim() ?? string.Empty;
            if (requestId.Length is < 1 or > 128 || requestId.Any(char.IsControl)) { error = "requestId must be 1–128 printable characters."; return false; }
            if (root.TryGetProperty("currency", out var currencyElement)
                && (currencyElement.ValueKind != JsonValueKind.String || !string.Equals(currencyElement.GetString(), "gil", StringComparison.OrdinalIgnoreCase)))
            { error = "Only gil purchases are supported."; return false; }
            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            { error = "Items array is required."; return false; }
            var items = new List<ItemRequest>();
            foreach (var element in itemsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("itemId", out var itemIdElement) || !itemIdElement.TryGetUInt32(out var itemId) || itemId == 0
                    || !element.TryGetProperty("quantity", out var quantityElement) || !quantityElement.TryGetUInt32(out var quantity)
                    || quantity == 0 || quantity > int.MaxValue)
                { error = "Each item needs a nonzero itemId and quantity between 1 and 2147483647."; return false; }
                if (element.EnumerateObject().Any(property => property.Name is not ("itemId" or "quantity")))
                { error = "Item entries accept only itemId and quantity."; return false; }
                items.Add(new(itemId, quantity));
                if (items.Count > MaximumItemsPerRequest) { error = $"At most {MaximumItemsPerRequest} item rows are allowed."; return false; }
            }
            if (items.Count == 0) { error = "At least one item is required."; return false; }
            if (items.Select(item => item.ItemId).Distinct().Count() != items.Count)
            { error = "Duplicate item IDs are not allowed; combine quantities into one row."; return false; }

            request = new(caller, requestId, items.AsReadOnly());
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Malformed request JSON: {ex.Message}";
            return false;
        }
    }

    private void PruneHistory()
    {
        while (_operations.Count > MaximumRecentOperations && _operationOrder.TryPeek(out var oldestId))
        {
            if (!_operations.TryGetValue(oldestId, out var oldest))
            {
                _operationOrder.Dequeue();
                continue;
            }
            if (!oldest.IsTerminal) break;
            _operationOrder.Dequeue();
            _operations.Remove(oldestId);
            _requests.Remove(new(oldest.Request.Caller, oldest.Request.RequestId));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _operations.Clear();
            _requests.Clear();
            _operationOrder.Clear();
            _activeOperationId = null;
        }
    }
}
