using System;

namespace GatherBuddy.Vulcan.Vendors;

/// <summary>Distances from the live object's center, not from a cached map marker.</summary>
public readonly record struct AethernetSourceRange(float Radius, float Horizontal, float Vertical)
{
    public const float InteractionMargin = 3f;
    public float Limit => Radius + InteractionMargin;
    public bool IsValid => float.IsFinite(Radius) && Radius >= 0 && float.IsFinite(Limit)
        && float.IsFinite(Horizontal) && Horizontal >= 0 && float.IsFinite(Vertical) && Vertical >= 0;
    // Separate vertical guard prevents horizontal proximity on another floor from passing.
    // Scale it with the hitbox too: large source object origins may sit above the ground.
    public bool InRange => IsValid && Horizontal <= Limit && Vertical <= Limit;
    public float Remaining => IsValid ? MathF.Max(0, MathF.Max(Horizontal - Limit, Vertical - Limit)) : float.PositiveInfinity;
}

public enum AethernetSourceAction { Approach, Stop, Wait, CorrectiveApproach, Ready, Failed }

/// <summary>Game-thread-only, pure approach decisions. Native calls acknowledge submitted actions explicitly.</summary>
public sealed class AethernetSourceApproach
{
    public static readonly TimeSpan RecognitionGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ApproachTimeout = TimeSpan.FromSeconds(60);
    private (uint Territory, ulong Source)? _source;
    private DateTime? _started;
    private DateTime? _arrived;
    private bool _stopped;
    private bool _corrected;
    private bool _finished;

    public void Reset()
    {
        _source = null;
        _started = _arrived = null;
        _stopped = _corrected = _finished = false;
    }

    public AethernetSourceAction Evaluate(DateTime now, uint territory, ulong sourceId,
        AethernetSourceRange range, bool recognized)
    {
        if (_finished) return AethernetSourceAction.Failed;
        var identity = (territory, sourceId);
        if (_source != identity)
        {
            // A missing/replaced source must not reset the overall approach deadline.
            if (_source is { } old && old.Territory != territory) Reset();
            _source = identity;
            _arrived = null;
            _stopped = _corrected = false;
        }
        _started ??= now;
        if (now - _started.Value >= ApproachTimeout) return Fail();
        if (sourceId == 0) return AethernetSourceAction.Wait;
        if (!range.IsValid) return Fail();
        if (_arrived is { } arrival && now - arrival >= RecognitionTimeout) return Fail();
        if (!range.InRange)
        {
            _stopped = false;
            return AethernetSourceAction.Approach;
        }
        _arrived ??= now;
        if (recognized) return AethernetSourceAction.Ready;
        if (!_stopped) return AethernetSourceAction.Stop;
        if (!_corrected && now - _arrived.Value >= RecognitionGrace) return AethernetSourceAction.CorrectiveApproach;
        return AethernetSourceAction.Wait;
    }

    public void MarkStopped() => _stopped = true;
    public void MarkCorrectiveApproachStarted() => _corrected = true;
    // Accepted teleport or cancellation is terminal until a new navigation explicitly resets this tracker.
    public void Finish() => _finished = true;
    private AethernetSourceAction Fail() { _finished = true; return AethernetSourceAction.Failed; }
}
