using System;

namespace GatherBuddy.Automation;

public interface IRetainerWithdrawalPort
{
    bool Available { get; }
    bool Busy { get; }
    bool MultiMode { get; }
    bool Suppressed { get; }
    bool PendingWork { get; }
    bool Ready { get; }
    ulong CharacterId { get; }
    bool InteractionClosed { get; }
    void SetSuppressed(bool value);
    void CloseOwnedInteraction();
}

/// <summary>Framework-thread lease over Vulcan bell access, not an ownership claim on AR's global boolean.</summary>
public sealed class RetainerWithdrawalGuard(IRetainerWithdrawalPort port)
{
    public Guid? Owner { get; private set; }
    public bool Releasing { get; private set; }
    public bool Faulted { get; private set; }
    public string Status { get; private set; } = string.Empty;
    private bool _changedSuppression;
    private bool _hadAutoRetainer;
    private long? _idleSince;
    private ulong _characterId;

    public bool TryAcquire(Guid owner, long now)
    {
        if (owner == Guid.Empty) throw new ArgumentException("Withdrawal requires a run owner.", nameof(owner));
        if (Owner.HasValue && Owner != owner) return Wait("Previous Vulcan bell cleanup still owns suppression");
        if (Releasing || Faulted) return Wait("Waiting for owned bell cleanup before withdrawal can resume");
        try
        {
            if (!port.Ready || port.CharacterId == 0) { _idleSince = null; return Wait("Waiting for stable player readiness before withdrawal"); }
            if (Owner == owner)
            {
                if (port.CharacterId != _characterId || port.Available != _hadAutoRetainer || port.Available && (port.Busy || port.MultiMode || !port.Suppressed))
                {
                    Faulted = true;
                    return Wait("Character or AutoRetainer availability/activity/suppression changed during withdrawal; pause and reconcile");
                }
                return true; // Ready work appearing during our bell session waits until cleanup.
            }
            if (port.Available)
            {
                if (port.Busy || port.MultiMode) { _idleSince = null; return Wait("Waiting for external AutoRetainer work to finish"); }
                var suppressed = port.Suppressed;
                if (!suppressed && port.PendingWork)
                { _idleSince = null; return Wait("Waiting for configured AutoRetainer work before Vulcan withdrawal"); }
                if (!port.InteractionClosed) { _idleSince = null; return Wait("Close existing retainer interaction before Vulcan takes ownership"); }
                _idleSince ??= now;
                if (now - _idleSince.Value < 500) return Wait("Waiting for AutoRetainer/bell idle stability");
                Owner = owner;
                _characterId = port.CharacterId;
                _hadAutoRetainer = true;
                // Record intent before IPC: a throwing call may already have written the global flag.
                _changedSuppression = !suppressed;
                if (_changedSuppression) port.SetSuppressed(true);
                if (!port.Suppressed || port.Busy || port.MultiMode || port.CharacterId != _characterId)
                { Faulted = true; return Wait("AutoRetainer changed while acquiring bell suppression; withdrawal not started"); }
                Status = suppressed ? "Vulcan withdrawal protected by existing AR suppression (not owned)" : "Vulcan withdrawal protected by scoped AR suppression";
            }
            else
            {
                if (!port.InteractionClosed) return Wait("Close existing retainer interaction before Vulcan withdrawal");
                Owner = owner;
                _characterId = port.CharacterId;
                _hadAutoRetainer = false;
                Status = "Vulcan withdrawal owns bell access; AutoRetainer unavailable";
            }
            return true;
        }
        catch (Exception ex)
        {
            Faulted |= Owner.HasValue;
            return Wait($"AutoRetainer withdrawal IPC unavailable: {ex.Message}");
        }
    }

    public void RequestRelease(Guid owner)
    {
        if (Owner != owner || Releasing) return;
        Releasing = true;
        _idleSince = null;
        Status = "Closing owned bell interaction before releasing AR suppression";
    }

    public void Tick(long now, bool unloading = false)
    {
        if (!Owner.HasValue || !Releasing) return;
        try
        {
            if (!port.Ready || port.CharacterId == 0 || port.Available && (port.Busy || port.MultiMode))
            { _idleSince = null; Status = "Withdrawal cleanup waiting for readiness/external AR tasks; no tasks aborted"; return; }
            if (!port.InteractionClosed)
            {
                _idleSince = null;
                if (port.CharacterId != _characterId)
                { Status = "Character changed; close current interaction manually (no foreign-character menus closed)"; return; }
                Status = "Closing owned bell interaction; close remaining confirmations manually if needed (suppression retained)";
                port.CloseOwnedInteraction();
                return;
            }
            _idleSince ??= now;
            if (!unloading && now - _idleSince.Value < 500) return;
            var unavailableRestore = _changedSuppression && !port.Available;
            if (_changedSuppression && port.Available)
            {
                if (port.Suppressed) port.SetSuppressed(false);
            }
            Owner = null;
            Releasing = Faulted = _changedSuppression = _hadAutoRetainer = false;
            _characterId = 0;
            _idleSince = null;
            Status = unavailableRestore
                ? "Owned bell closed; AR unloaded so no IPC restore possible; inspect suppression if AR reloads"
                : "Owned bell cleanup complete; Vulcan-applied suppression released, external suppression preserved";
        }
        catch (Exception ex) { Status = $"Bell cleanup/suppression restore blocked; ownership retained: {ex.Message}"; }
    }

    private bool Wait(string reason) { Status = reason; return false; }
}
