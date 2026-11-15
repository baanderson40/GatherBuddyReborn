using System;

namespace GatherBuddy.Crafting;

public enum HomeTravelAdmission { NeedsCommand, Waiting, Ready, Failed, Cancelled }

/// <summary>One crafting run's travel receipt. Submission is not completion; no implicit retry.</summary>
public sealed class PreCraftHomeTravel(ulong character)
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(120);
    private TimeSpan? _submittedAt, _clearSince, _waitingAt;
    private string? _command;
    private bool _sawBusy, _satisfied, _cancelled, _consumed;
    public string? Error { get; private set; }
    public bool Submitted => _submittedAt.HasValue;
    public bool Consumed => _consumed;

    public HomeTravelAdmission Observe(TimeSpan now, ulong currentCharacter, string command,
        bool enabled, bool busy, bool ready)
    {
        if (_cancelled) return HomeTravelAdmission.Cancelled;
        if (Error != null) return HomeTravelAdmission.Failed;
        if (character == 0 || currentCharacter != character)
            return Fail("Character changed or logged out. Stop and restart crafting.");
        if (_consumed) return HomeTravelAdmission.Ready;
        if (Submitted && !string.Equals(_command, command, StringComparison.Ordinal))
            return Fail("Lifestream command changed during travel. Stop and restart crafting.");
        if (_satisfied) return HomeTravelAdmission.Ready;
        if (!Submitted)
        {
            if (!enabled) { _satisfied = true; return HomeTravelAdmission.Ready; }
            _waitingAt ??= now;
            if (now - _waitingAt.Value >= CompletionTimeout)
                return Fail("Pre-craft travel readiness exceeded 120 seconds. Finish other travel or interactions and restart crafting.");
            return !busy && ready ? HomeTravelAdmission.NeedsCommand : HomeTravelAdmission.Waiting;
        }
        _sawBusy |= busy;
        if (now - _submittedAt!.Value >= CompletionTimeout)
            return Fail("Pre-craft travel exceeded 120 seconds. No command retry; inspect travel and restart crafting.");
        if (!_sawBusy)
        {
            if (now - _submittedAt.Value >= StartTimeout)
                return Fail("Lifestream did not report starting within 10 seconds. No command retry; inspect travel and restart crafting.");
            return HomeTravelAdmission.Waiting;
        }
        if (busy || !ready) { _clearSince = null; return HomeTravelAdmission.Waiting; }
        _clearSince ??= now;
        if (now - _clearSince.Value < TimeSpan.FromMilliseconds(500)) return HomeTravelAdmission.Waiting;
        _satisfied = true;
        return HomeTravelAdmission.Ready;
    }

    public bool RecordSubmission(TimeSpan now, string command)
    {
        if (Submitted || _cancelled || _satisfied || Error != null) return false;
        _submittedAt = now;
        _command = command;
        return true;
    }

    public HomeTravelAdmission Fail(string error) { Error ??= error; return HomeTravelAdmission.Failed; }
    public bool InvalidateForGathering()
    {
        // Only a consumed receipt can be replaced. Never erase uncertainty or retry pending IPC.
        if (!_consumed || _cancelled || Error != null) return false;
        _submittedAt = _clearSince = _waitingAt = null;
        _command = null;
        _sawBusy = _satisfied = _consumed = false;
        return true;
    }

    public void Consume() { if (_satisfied) _consumed = true; }
    public void Cancel() => _cancelled = true;
}
