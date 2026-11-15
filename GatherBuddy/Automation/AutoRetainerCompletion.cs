using System;

namespace GatherBuddy.Automation;

/// <summary>Run-scoped opt-in snapshot; claiming it never permits a second submission.</summary>
public sealed class AutoRetainerCompletionTicket(string owner, ulong characterId, bool armed)
{
    public string Owner { get; } = owner;
    public ulong CharacterId { get; } = characterId;
    public bool Armed { get; } = armed;
    public bool Claimed { get; internal set; }
}

public interface IAutoRetainerCompletionPort
{
    bool CompletionEnabled { get; }
    ulong CompletionCharacterId { get; }
    bool CompletionCleanupReady { get; }
    void VerifyContinuousAvailable();
    bool MultiModeEnabled { get; }
    bool Busy { get; }
    void EnableContinuous();
}

public enum AutoRetainerCompletionState { Idle, Waiting, Submitted, HandedOff, AlreadyEnabled, Cancelled, Failed }

public static class AutoRetainerCompletionEligibility
{
    public static bool Gathering(bool completed, bool observedWork, bool craftPending)
        => completed && observedWork && !craftPending;
    public static bool Queue(bool complete, bool paused, int confirmed, int remaining)
        => complete && !paused && confirmed > 0 && remaining == 0;
}

/// <summary>Terminal handoff only. Never owns an AR pass, relogs, disables AR, or resumes local work.</summary>
public sealed class AutoRetainerCompletion(IAutoRetainerCompletionPort port)
{
    private AutoRetainerCompletionTicket? _ticket;
    private TimeSpan _deadline;
    private TimeSpan? _readySince;
    public AutoRetainerCompletionState State { get; private set; }
    public string Status { get; private set; } = "Completion handoff idle";
    public bool Pending => State == AutoRetainerCompletionState.Waiting;
    public bool SubmissionAttempted { get; private set; }
    public string? Owner => _ticket?.Owner;

    public bool Request(AutoRetainerCompletionTicket? ticket, TimeSpan now)
    {
        if (ticket is not { Armed: true, Claimed: false, CharacterId: not 0 } || Pending || State == AutoRetainerCompletionState.Submitted) return false;
        ticket.Claimed = true;
        _ticket = ticket;
        _deadline = now + TimeSpan.FromSeconds(120);
        _readySince = null;
        SubmissionAttempted = false;
        State = AutoRetainerCompletionState.Waiting;
        Status = "Work finished; waiting for cleanup before continuous AutoRetainer handoff";
        return true;
    }

    public void Cancel(string? owner = null)
    {
        if (!Pending || owner != null && owner != Owner) return;
        State = AutoRetainerCompletionState.Cancelled;
        Status = "Completion handoff cancelled; AutoRetainer left unchanged";
    }

    public void Tick(TimeSpan now)
    {
        if (!Pending) return;
        try
        {
            if (!port.CompletionEnabled) { Cancel(); return; }
            if (port.CompletionCharacterId != _ticket!.CharacterId)
            { Fail("Completion character changed or logged out; AutoRetainer left unchanged"); return; }
            if (now >= _deadline) { Fail("Completion cleanup exceeded 120 seconds; no AutoRetainer retry"); return; }
            if (!port.CompletionCleanupReady) { _readySince = null; return; }
            port.VerifyContinuousAvailable();
            if (port.MultiModeEnabled)
            {
                State = AutoRetainerCompletionState.AlreadyEnabled;
                Status = "AutoRetainer multi-mode already enabled; ownership left external";
                return;
            }
            if (port.Busy) { _readySince = null; return; }
            _readySince ??= now;
            if (now - _readySince.Value < TimeSpan.FromMilliseconds(500)) return;
            State = AutoRetainerCompletionState.Submitted; // Reserve before IPC, even if it throws after taking effect.
            SubmissionAttempted = true;
            port.EnableContinuous();
            State = AutoRetainerCompletionState.HandedOff;
            Status = "Continuous AutoRetainer requested; GBR/Vulcan finished (no return or resume)";
        }
        catch (Exception ex) { Fail($"Continuous AutoRetainer handoff failed ({ex.GetType().Name}); no retry. Inspect AutoRetainer manually."); }
    }

    private void Fail(string reason) { State = AutoRetainerCompletionState.Failed; Status = reason; }
}
