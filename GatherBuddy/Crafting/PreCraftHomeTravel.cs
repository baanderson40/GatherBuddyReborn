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
    private Guid? _handoff;
    private TimeSpan _suspendedAt;
    public string? Error { get; private set; }
    public bool Submitted => _submittedAt.HasValue;
    public bool Consumed => _consumed;
    public bool SkippedInsideHousing { get; private set; }
    public bool Suspended => _handoff.HasValue;

    public HomeTravelAdmission Observe(TimeSpan now, ulong currentCharacter, string command,
        bool enabled, bool busy, bool ready, bool insideHousing = false)
    {
        if (_cancelled) return HomeTravelAdmission.Cancelled;
        if (Error != null) return HomeTravelAdmission.Failed;
        if (Suspended) return HomeTravelAdmission.Waiting;
        if (character == 0 || currentCharacter != character)
            return Fail("Character changed or logged out. Stop and restart crafting.");
        if (_consumed) return HomeTravelAdmission.Ready;
        if (Submitted && !string.Equals(_command, command, StringComparison.Ordinal))
            return Fail("Lifestream command changed during travel. Stop and restart crafting.");
        if (SkippedInsideHousing)
        {
            _satisfied = !busy && ready;
            if (!_satisfied && now - _waitingAt!.Value >= CompletionTimeout)
                return Fail("Housing skip readiness exceeded 120 seconds. Inspect travel/player state and restart crafting.");
            return _satisfied ? HomeTravelAdmission.Ready : HomeTravelAdmission.Waiting;
        }
        if (_satisfied)
        {
            if (!Submitted || !busy && ready) return HomeTravelAdmission.Ready;
            _satisfied = false; // Admission is revocable until consumed; a later travel stage must block again.
        }
        if (!Submitted)
        {
            if (!enabled) { _satisfied = true; return HomeTravelAdmission.Ready; }
            _waitingAt ??= now;
            if (now - _waitingAt.Value >= CompletionTimeout)
                return Fail("Pre-craft travel readiness exceeded 120 seconds. Finish other travel or interactions and restart crafting.");
            if (!busy && ready && insideHousing)
            {
                SkippedInsideHousing = _satisfied = true;
                return HomeTravelAdmission.Ready;
            }
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
        if (Submitted || SkippedInsideHousing || Suspended || _cancelled || _satisfied || Error != null) return false;
        _submittedAt = now;
        _command = command;
        return true;
    }

    public HomeTravelAdmission Fail(string error) { Error ??= error; return HomeTravelAdmission.Failed; }
    public bool SuspendForAutoRetainer(Guid handoff, TimeSpan now, ulong currentCharacter)
    {
        if (_cancelled || Error != null) return false;
        if (handoff == Guid.Empty || character == 0 || currentCharacter != character)
        { Fail("Cannot suspend home travel without a matching original character and AR handoff."); return false; }
        if (Suspended) return _handoff == handoff;
        _handoff = handoff;
        _suspendedAt = now;
        _clearSince = null;
        return true;
    }

    public bool ResumeFromAutoRetainer(Guid handoff, TimeSpan now, ulong currentCharacter)
    {
        if (_cancelled || Error != null) return false;
        if (_handoff != handoff || handoff == Guid.Empty || currentCharacter != character || character == 0)
        { Fail("Home travel AR return did not match the suspended handoff/original character. Restart crafting."); return false; }
        var elapsed = now > _suspendedAt ? now - _suspendedAt : TimeSpan.Zero;
        // Keep previously spent budget; only the coordinated absence is excluded.
        if (_submittedAt.HasValue) _submittedAt += elapsed;
        if (_waitingAt.HasValue) _waitingAt += elapsed;
        _clearSince = null;
        _handoff = null;
        return true;
    }

    public bool InvalidateForGathering()
    {
        // Only a consumed receipt can be replaced. Never erase uncertainty or retry pending IPC.
        if (!_consumed || Suspended || _cancelled || Error != null) return false;
        _submittedAt = _clearSince = _waitingAt = null;
        _command = null;
        _sawBusy = _satisfied = _consumed = false;
        SkippedInsideHousing = false;
        return true;
    }

    public void Consume() { if (_satisfied && !Suspended && !_cancelled && Error == null) _consumed = true; }
    public void Cancel() => _cancelled = true;
}
