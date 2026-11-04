using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public class RetainerBellNavigator
{
    // Framework-thread registry survives cleared tasks, Stop, Reset and discarded queues.
    private static readonly HashSet<RetainerBellNavigator> Outstanding = [];
    public static bool HasOutstanding => Outstanding.Count != 0;
    public static bool CleanupPending => Outstanding.Any(n => n._stopping);
    private RetainerPathQuery? _query;
    private List<Vector3>? _submitted;
    private bool _stopping, _terminal, _failed;
    private ulong _bellId, _character;
    private uint _territory;
    private DateTime _started;
    private DateTime _nextSettlementLog;
    public bool IsComplete => _terminal && !Outstanding.Contains(this);
    public bool IsFailed => _failed;
    public bool IsSettling => _stopping;
    public bool IsNavigating => !_terminal && Outstanding.Contains(this);

    public static void TickCleanup()
    {
        foreach (var navigator in Outstanding.Where(n => n._stopping).ToArray()) navigator.Settle();
    }

    public static void StopAll()
    {
        foreach (var navigator in Outstanding.ToArray()) navigator.Stop();
        TickCleanup();
        if (Outstanding.Count != 0)
            GatherBuddy.Log.Warning("[RetainerBellNavigator] Navigation unresolved on unload; inspect vnavmesh movement manually. No foreign paths cancelled.");
    }

    public bool StartNavigation(IGameObject bell)
    {
        if (Outstanding.Count != 0) return false;
        _bellId = bell.GameObjectId;
        _character = Dalamud.PlayerState.ContentId;
        _territory = Dalamud.ClientState.TerritoryType;
        _started = DateTime.UtcNow;
        Outstanding.Add(this);
        try
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null) throw new InvalidOperationException("Player unavailable");
            var hitboxRadius = bell.HitboxRadius;
            if (RetainerBellGeometry.InRange(player.Position, bell.Position, hitboxRadius))
            { Finish(false); return true; }
            if (!VNavmesh.Enabled || !VNavmesh.Nav.IsReady() || !Idle())
                throw new InvalidOperationException("vnavmesh unavailable or movement already in use");
            Vector3? destination = null;
            for (var candidate = 0; candidate < 8; ++candidate)
            {
                var desired = RetainerBellGeometry.Approach(player.Position, bell.Position, hitboxRadius, candidate);
                var projected = VNavmesh.Query.Mesh.NearestPoint(desired, 0.75f, 1.5f);
                if (projected.HasValue && RetainerBellGeometry.InRange(projected.Value, bell.Position, hitboxRadius))
                { destination = projected; break; }
            }
            if (!destination.HasValue) throw new InvalidOperationException("No same-floor bell approach point");
            _query = new(token => VNavmesh.Nav.PathfindCancelable(player.Position, destination.Value, false, token));
            GatherBuddy.Log.Debug($"[RetainerBellNavigator] bell={_bellId}; query submitted; approach={destination}");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[RetainerBellNavigator] {ex.Message}");
            Finish(true);
            return false;
        }
    }

    public void Update()
    {
        if (_stopping) { Settle(); return; }
        if (_terminal) return;
        try
        {
            var bell = Dalamud.Objects.FirstOrDefault(o => o.GameObjectId == _bellId && o.IsTargetable);
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null || bell == null || _character != Dalamud.PlayerState.ContentId
                || _territory != Dalamud.ClientState.TerritoryType || !SeFunctions.TeleportData.IsReady)
            { Finish(true); return; }
            if (RetainerBellGeometry.InRange(player.Position, bell.Position, bell.HitboxRadius))
            { Finish(false); return; }
            if (DateTime.UtcNow - _started > TimeSpan.FromSeconds(60)) { Finish(true); return; }
            if (_query != null)
            {
                if (!_query.Completed) return;
                var path = _query.TakeCompleted();
                _query.Dispose();
                _query = null;
                if (path == null || path.Count == 0 || !Idle() || !RetainerBellGeometry.InRange(path[^1], bell.Position,
                        bell.HitboxRadius))
                { Finish(true); return; }
                _submitted = path.ToList(); // Record intent before potentially ambiguous IPC failure.
                VNavmesh.Path.MoveTo(path, false); // Framework thread only; never a continuation.
            }
            else if (!VNavmesh.Path.IsRunning()) Finish(true); // Never race vnavmesh's stuck retry.
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[RetainerBellNavigator] bell={_bellId}; {ex.Message}");
            Finish(true);
        }
    }

    private static bool Idle() => !VNavmesh.Path.IsRunning() && !VNavmesh.SimpleMove.PathfindInProgress();

    private void Finish(bool failed)
    {
        _terminal = true;
        _failed |= failed;
        Stop();
    }

    public void Stop()
    {
        _terminal = true;
        _stopping = true;
        Settle();
    }

    private void Settle()
    {
        try
        {
            if (_query != null)
            {
                if (!_query.StopAndSettle())
                {
                    if (DateTime.UtcNow - _started > TimeSpan.FromSeconds(5))
                        LogSettlementWait("waiting for caller-cancelled path query to finish; completed results will be discarded");
                    return;
                }
                _query = null;
            }
            if (_submitted != null)
            {
                if (!VNavmesh.Enabled) return; // Cannot establish settlement through absent IPC.
                var current = VNavmesh.Path.ListWaypoints();
                // FollowPath consumes a prefix; a different path is not ours to stop.
                if (_character == Dalamud.PlayerState.ContentId && _territory == Dalamud.ClientState.TerritoryType
                    && RetainerPathQuery.MatchesRemaining(_submitted, current))
                    VNavmesh.Path.Stop();
                // Global automatic retry has no operation identity or cancellation API.
                // Keep handoff blocked while any ambiguous retry/replacement remains active.
                if (!Idle())
                {
                    LogSettlementWait("possible automatic retry or replaced path still active; handoff blocked, no foreign movement stopped");
                    return;
                }
                _submitted = null;
            }
            if (Outstanding.Remove(this))
                GatherBuddy.Log.Debug($"[RetainerBellNavigator] bell={_bellId}; settled; failed={_failed}");
        }
        catch (Exception ex)
        { LogSettlementWait(ex.Message); }
    }

    private void LogSettlementWait(string reason)
    {
        if (DateTime.UtcNow < _nextSettlementLog) return;
        _nextSettlementLog = DateTime.UtcNow.AddSeconds(5);
        GatherBuddy.Log.Warning($"[RetainerBellNavigator] bell={_bellId}; settlement blocked; inspect vnavmesh manually: {reason}");
    }
}
