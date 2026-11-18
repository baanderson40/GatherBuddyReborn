using GatherBuddy.Crafting;
using GatherBuddy.Gui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GatherBuddy.Plugin;

internal sealed class CraftingListIpcOperations : IDisposable
{
    private const int MaximumHistory = 64;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StartOperation> _operations = [];
    private readonly Queue<Guid> _order = [];
    private bool _disposed;

    private sealed class StartOperation(int listId)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int ListId { get; } = listId;
        public string State { get; set; } = "Queued";
        public string? Message { get; set; }
    }

    public string Start(int listId)
    {
        lock (_gate)
        {
            if (_disposed)
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "Unavailable", error = "Crafting-list IPC is shutting down." }, JsonOptions);

            Prune();
            if (_operations.Values.Any(operation => operation.State == "Queued"))
                return JsonSerializer.Serialize(new { accepted = false, errorCode = "Busy", error = "A crafting-list start request is already queued." }, JsonOptions);

            var operation = new StartOperation(listId);
            _operations.Add(operation.Id, operation);
            _order.Enqueue(operation.Id);
            return JsonSerializer.Serialize(new { accepted = true, operationId = operation.Id, listId, state = operation.State }, JsonOptions);
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
            return JsonSerializer.Serialize(new
            {
                found = true,
                operationId = operation.Id,
                listId = operation.ListId,
                state = operation.State,
                message = operation.Message,
            }, JsonOptions);
        }
    }

    public void Update()
    {
        StartOperation? operation;
        lock (_gate)
        {
            if (_disposed) return;
            operation = _operations.Values.FirstOrDefault(candidate => candidate.State == "Queued");
            if (operation == null) return;
            operation.State = "Starting";
        }

        try
        {
            var list = GatherBuddy.CraftingListManager.GetListByID(operation.ListId);
            if (list == null)
            {
                Finish(operation, "Rejected", $"No saved crafting list exists with ID {operation.ListId}.");
                return;
            }
            if (list.Recipes.Count == 0)
            {
                Finish(operation, "Rejected", "Cannot start an empty crafting list.");
                return;
            }

            var plan = CraftingExecutionPlan.Create(list);
            if (!CraftingGatherBridge.StartQueueCraftAndGather(plan, list.Consumables, list.Ephemeral ? list.ID : null))
            {
                Finish(operation, "Rejected", "Gather + Craft could not start; another automation may be active or prerequisites are unavailable.");
                return;
            }

            Finish(operation, "Started", "Gather + Craft started using the saved list's settings.");
        }
        catch (Exception ex)
        {
            Finish(operation, "Rejected", $"Could not start crafting list: {ex.Message}");
        }
    }

    private void Finish(StartOperation operation, string state, string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            operation.State = state;
            operation.Message = message;
        }
    }

    private void Prune()
    {
        while (_operations.Count >= MaximumHistory && _order.TryDequeue(out var oldest))
            _operations.Remove(oldest);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _operations.Clear();
            _order.Clear();
        }
    }
}
