using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Adaptive;

/// <summary>Experimental native callback scalars. Never a complete crafting state or execution permission.</summary>
public sealed record AdaptiveNativeCraftEvent(long Stream, long Sequence, int Operation, uint RecipeId,
    int StartingQuality, uint ActionId = 0, int StepIndex = 0, int Progress = 0, int Quality = 0,
    int Durability = 0, int ConditionPlusOne = 0, uint Flags = 0)
{
    public bool IsAdvance => Operation is 9 or 10;
    public bool ActionSucceeded => (Flags & 0x10) != 0;
    public bool Complete => (Flags & 0x0c) != 0;
    public const string Layout = "Experimental crafting callback v1: op9/10, action@10 step@18 progress@1C quality@24 durability@30 condition+1@38 flags@44; native validation pending";

    // Fixed bounded payload prefix only. No CP/status fields are present in this projection.
    public static bool TryParseAdvance(ReadOnlySpan<byte> payload, long stream, long sequence,
        uint recipe, int startingQuality, out AdaptiveNativeCraftEvent? evidence)
    {
        evidence = null;
        if (payload.Length != 0x48 || stream <= 0 || sequence <= 0 || recipe == 0 || startingQuality < 0) return false;
        var op = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (op is not (9 or 10)) return false;
        var action = BinaryPrimitives.ReadUInt32LittleEndian(payload[0x10..]);
        var step = BinaryPrimitives.ReadUInt16LittleEndian(payload[0x18..]);
        var progress = BinaryPrimitives.ReadInt32LittleEndian(payload[0x1c..]);
        var quality = BinaryPrimitives.ReadInt32LittleEndian(payload[0x24..]);
        var durability = BinaryPrimitives.ReadInt32LittleEndian(payload[0x30..]);
        var condition = BinaryPrimitives.ReadInt32LittleEndian(payload[0x38..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[0x44..]);
        if (action == 0 || step is < 1 or > 1000 || progress < 0 || quality < 0 || durability < 0 || condition is < 1 or > 4) return false;
        evidence = new(stream, sequence, op, recipe, startingQuality, action, step, progress, quality, durability, condition, flags);
        return true;
    }
}

public sealed record AdaptiveNativeCaptureContext(long Stream, uint RecipeId, int StartingQuality, long Sequence);

/// <summary>Host-serialized bounded storage. Draining never conceals loss; reset requires a fresh empty stream.</summary>
public sealed class AdaptiveNativeEvidenceBuffer
{
    private readonly Queue<AdaptiveNativeCraftEvent> _events = new();
    private bool _lost;
    public int Count => _events.Count;
    public void Add(AdaptiveNativeCraftEvent evidence)
    {
        if (_events.Count == 64) { _lost = true; return; }
        _events.Enqueue(evidence);
    }
    public AdaptiveNativeCraftEvent[] Drain(out bool lost)
    {
        lost = _lost;
        var result = _events.ToArray();
        _events.Clear();
        return result;
    }
    public bool ResetForFreshStream()
    {
        if (_events.Count != 0) return false;
        _lost = false;
        return true;
    }
}

public sealed record AdaptiveNativeEvidenceSummary(bool RequiredForCorrelation, int MatchedAcknowledgements,
    AdaptiveNativeCraftEvent? LastAcknowledgement, AdaptiveNativeCraftEvent? Terminal,
    string Layout = AdaptiveNativeCraftEvent.Layout)
{
    public bool ExecutionAuthorized => false;
    public AdaptiveNativeMismatch? LastMismatch { get; init; }
}
