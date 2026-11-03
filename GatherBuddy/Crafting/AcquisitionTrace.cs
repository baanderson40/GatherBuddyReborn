using System;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

public enum AcquisitionTraceLevel { Debug, Warning }

public sealed record AcquisitionTraceEvent(int Sequence, string Phase, string Action, string Detail, AcquisitionTraceLevel Level);

/// <summary>Optional bounded diagnostic collector. No native services, logger calls, or configuration serialization.</summary>
public sealed class AcquisitionTrace
{
    private readonly List<AcquisitionTraceEvent> _events = new();
    private readonly int _capacity;
    public Guid OperationId { get; }
    public IReadOnlyList<AcquisitionTraceEvent> Events { get; }
    public long DroppedEvents { get; private set; }
    public string? OutcomeSummary { get; private set; }

    public AcquisitionTrace(Guid operationId, int capacity = 1024)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Trace operation ID is required.", nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        OperationId = operationId;
        _capacity = capacity;
        Events = _events.AsReadOnly();
    }

    public void Record(string phase, string action, string detail, AcquisitionTraceLevel level = AcquisitionTraceLevel.Debug)
    {
        if (_events.Count >= _capacity) { DroppedEvents++; return; }
        _events.Add(new(_events.Count + 1, Clean(phase), Clean(action), Clean(detail), level));
    }

    public string Format(AcquisitionTraceEvent entry)
        => $"[Vulcan Acquisition][{OperationId}][{entry.Sequence:D4}][{entry.Phase}][{entry.Action}] {entry.Detail}";

    // Keep the outcome outside the bounded event buffer so truncation cannot hide final state.
    public void Complete(string detail) => OutcomeSummary = Clean(detail);

    public string Summary => $"[Vulcan Acquisition][{OperationId}][TraceSummary] recorded={_events.Count}; omitted={DroppedEvents}; truncated={DroppedEvents > 0}; outcome={OutcomeSummary ?? "not supplied"}. No spending or execution performed by tracing.";

    private static string Clean(string value)
    {
        const int maxLength = 1024;
        var clean = value.Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length <= maxLength ? clean : clean[..maxLength] + " [field truncated]";
    }
}
