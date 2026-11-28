using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Aetheryte = GatherBuddy.Classes.Aetheryte;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueDismount()
        {
            TaskManager.Enqueue(StopNavigation);

            var am = ActionManager.Instance();
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { am->UseAction(ActionType.Mount, 0); } }, "Dismount");

            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.InFlight] && CanAct, 1000, "Wait for not in flight");
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { am->UseAction(ActionType.Mount, 0); } }, "Dismount 2");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Mounted] && CanAct, 1000, "Wait for dismount");
            TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { TaskManager.DelayNextImmediate(500); } });//Prevent "Unable to execute command while jumping."
        }

        private unsafe void EnqueueMountUp()
        {
            const uint MountRouletteGeneralActionId = 9;
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
            Action doMount;

            if (IsMountUnlocked(mount) && am->GetActionStatus(ActionType.Mount, mount) == 0)
            {
                doMount = () => am->UseAction(ActionType.Mount, mount);
            }
            else
            {
                if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0)
                {
                    return;
                }

                doMount = () => am->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
            }

            EnqueueActionWithDelay(doMount);
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Mounted], 2000);
        }

        private unsafe bool CanMount()
        {
            var am = ActionManager.Instance();
            return am->GetActionStatus(ActionType.Mount, 0) == 0;
        }

        private unsafe bool IsMountUnlocked(uint mount)
        {
            var instance = PlayerState.Instance();
            if (instance == null)
            {
                return false;
            }

            return instance->IsMountUnlocked(mount);
        }

        private void MoveToCloseNode(IGameObject gameObject, Gatherable targetItem, ConfigPreset config)
        {
            if (!Player.Available)
            {
                return;
            }

            // We can open a node with less than 3 vertical and less than 3.5 horizontal separation
            var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
            var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);
            var inInteractionRange = GatherNodeApproachRules.IsWithinInteractionRange(gameObject.Position, Player.Position);
            if (inInteractionRange)
            {
                ResetNodeApproachRecovery();
            }
            else
            {
                var key = (Dalamud.ClientState.TerritoryType, gameObject.BaseId, gameObject.Position);
                if (_nodeApproachRecoveryKey != key)
                {
                    ResetNodeApproachRecovery();
                    _nodeApproachRecoveryKey = key;
                }

                if (_nodeApproachRecoveryKey == key)
                {
                    if (CurrentDestination == gameObject.Position && (IsPathing || IsPathGenerating))
                        _nodeApproachOriginalRouteObserved = true;
                    else if (_nodeApproachOriginalRouteObserved && !IsPathing && !IsPathGenerating
                        && TryRecoverElevatedNodeApproach(gameObject))
                        return;
                }

                if (hSeparation < 3.5f && vSeparation >= 3f
                    && !IsPathing && !IsPathGenerating
                    && TryRecoverElevatedNodeApproach(gameObject))
                    return;
            }

            if (hSeparation < 3.5)
            {
                var waitGP = targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted] && (waitGP || GetConsumablesWithCastTime(config) > 0))
                {
                    EnqueueDismount();
                    TaskManager.Enqueue(() =>
                    {
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            ForceLandAndDismount();
                        }
                    });
                }
                else if (waitGP)
                {
                    StopNavigation();
                    AutoStatus = "Waiting for GP to regenerate...";
                }
                else
                {
                    // Use consumables with cast time just before gathering a node when player is surely not mounted
                    if (GetConsumablesWithCastTime(config) is var consumable and > 0)
                    {
                        if (IsPathing)
                        {
                            StopNavigation();
                        }
                        else
                        {
                            EnqueueActionWithDelay(() => UseItem(consumable));
                        }
                    }
                    else
                    {
                        // Check perception requirement before interacting with node
                        if (DiscipleOfLand.Perception < targetItem.GatheringData.PerceptionReq)
                        {
                            Communicator.PrintError($"Insufficient Perception to gather this item. Required: {targetItem.GatheringData.PerceptionReq}, current: {DiscipleOfLand.Perception}");
                            AbortAutoGather();
                            return;
                        }

                        // If flying direct path to offset, complete navigation first, since offset is expected to be on the ground.
                        // Otherwise, stop once in range to interact.
                        if (vSeparation < 3 && !(_navState.offset && Dalamud.Conditions[ConditionFlag.InFlight] && IsPathing))
                        {
                            StopNavigation();
                            EnqueueNodeInteraction(gameObject, targetItem);
                        }
                        else
                        {
                            Navigate(gameObject.Position, false, nodeId: gameObject.BaseId);
                        }
                    }
                }
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position), nodeId: gameObject.BaseId);
            }
        }

        private bool TryRecoverElevatedNodeApproach(IGameObject node)
        {
            var now = DateTime.UtcNow;
            var territoryId = Dalamud.ClientState.TerritoryType;
            var key = (territoryId, node.BaseId, node.Position);
            if (_nodeApproachRecoveryKey != key)
            {
                _nodeApproachRecoveryKey = key;
                _nodeApproachRecoveryDestination = default;
                _nodeApproachRecoveryAttempted = false;
                _nodeApproachRecoveryObservedMovement = false;
                _nodeApproachOriginalRouteObserved = false;
            }

            if (_failedGatherSpawnCooldown.Contains(territoryId, node.BaseId, node.Position, now))
            {
                StopNavigation();
                AutoStatus = "Skipping unreachable node...";
                return true;
            }

            if (_nodeApproachRecoveryAttempted)
            {
                if (IsPathing || IsPathGenerating)
                {
                    _nodeApproachRecoveryObservedMovement = true;
                    AutoStatus = "Trying an alternate approach to elevated node...";
                    return true;
                }

                if (!_nodeApproachRecoveryObservedMovement)
                {
                    _nodeApproachRecoveryObservedMovement = true;
                    AutoStatus = "Trying an alternate approach to elevated node...";
                    return true;
                }

                _failedGatherSpawnCooldown.MarkFailed(territoryId, node.BaseId, node.Position, now);
                GatherBuddy.Log.Warning(
                    $"[AutoGather] Elevated node approach failed; temporarily skipping spawn: territory={territoryId}; node={node.BaseId}; player={Player.Position}; endpoint={_nodeApproachRecoveryDestination}; nodePosition={node.Position}; horizontal={Vector2.Distance(Player.Position.ToVector2(), node.Position.ToVector2()):F2}; vertical={Math.Abs(Player.Position.Y - node.Position.Y):F2}; cooldown={FailedGatherSpawnCooldown.Duration.TotalSeconds:F0}s");
                StopNavigation();
                AutoStatus = "Elevated node unreachable; trying another spawn...";
                ResetNodeApproachRecovery();
                return true;
            }

            if (!TryGetElevatedNodeApproach(node, out var approach))
            {
                _failedGatherSpawnCooldown.MarkFailed(territoryId, node.BaseId, node.Position, now);
                GatherBuddy.Log.Warning(
                    $"[AutoGather] No reachable-height approach point found; temporarily skipping spawn: territory={territoryId}; node={node.BaseId}; player={Player.Position}; nodePosition={node.Position}; horizontal={Vector2.Distance(Player.Position.ToVector2(), node.Position.ToVector2()):F2}; vertical={Math.Abs(Player.Position.Y - node.Position.Y):F2}; cooldown={FailedGatherSpawnCooldown.Duration.TotalSeconds:F0}s");
                StopNavigation();
                AutoStatus = "No safe approach to elevated node; trying another spawn...";
                ResetNodeApproachRecovery();
                return true;
            }

            _nodeApproachRecoveryAttempted = true;
            _nodeApproachRecoveryDestination = approach;
            _nodeApproachRecoveryObservedMovement = IsPathing || IsPathGenerating;
            GatherBuddy.Log.Warning(
                $"[AutoGather] Player is beside node but below interaction height; trying alternate direct approach: territory={territoryId}; node={node.BaseId}; player={Player.Position}; endpoint={approach}; nodePosition={node.Position}; horizontal={Vector2.Distance(Player.Position.ToVector2(), node.Position.ToVector2()):F2}; vertical={Math.Abs(Player.Position.Y - node.Position.Y):F2}");
            Navigate(approach, shouldFly: true, direct: true);
            AutoStatus = "Trying an alternate approach to elevated node...";
            return true;
        }

        private static bool TryGetElevatedNodeApproach(IGameObject node, out Vector3 approach)
        {
            const float desiredRadius = 2.5f;
            const float horizontalLimit = 3.25f;
            const float verticalLimit = 2.5f;
            var candidates = new List<Vector3>(16);
            var player = Player.Position;
            var toPlayer = new Vector2(player.X - node.Position.X, player.Z - node.Position.Z);
            var preferredAngle = toPlayer.LengthSquared() > 0.001f ? MathF.Atan2(toPlayer.Y, toPlayer.X) : 0f;

            for (var i = 0; i < 16; i++)
            {
                var angle = preferredAngle + i * (MathF.Tau / 16f);
                var proposed = new Vector3(
                    node.Position.X + MathF.Cos(angle) * desiredRadius,
                    node.Position.Y,
                    node.Position.Z + MathF.Sin(angle) * desiredRadius);
                Vector3? projected = null;
                try
                {
                    projected = VNavmesh.Query.Mesh.NearestPoint(proposed, 2.0f, verticalLimit);
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Debug($"[AutoGather] Elevated node approach projection failed at {proposed}: {ex.GetBaseException().Message}");
                }

                if (projected is not { } point
                    || Vector2.Distance(point.ToVector2(), node.Position.ToVector2()) > horizontalLimit
                    || Math.Abs(point.Y - node.Position.Y) >= verticalLimit
                    || Vector3.DistanceSquared(point, player) < 0.25f)
                    continue;

                candidates.Add(point);
            }

            if (candidates.Count == 0)
            {
                approach = default;
                return false;
            }

            approach = candidates.OrderBy(point => Vector3.DistanceSquared(point, player)).First();
            return true;
        }

        private void ResetNodeApproachRecovery()
        {
            _nodeApproachRecoveryKey = null;
            _nodeApproachRecoveryDestination = default;
            _nodeApproachRecoveryAttempted = false;
            _nodeApproachRecoveryObservedMovement = false;
            _nodeApproachOriginalRouteObserved = false;
        }

        private void ForceLandAndDismount()
        {
            var floor = VNavmesh.Query.Mesh.NearestPoint(Player.Position, 5, 5);
            if (floor != null)
            {
                Navigate(floor.Value, true, direct: true);
                TaskManager.Enqueue(() => !IsPathGenerating);
                TaskManager.DelayNext(50);
                TaskManager.Enqueue(() => !IsPathing, 1000);
                EnqueueDismount();
            }
            // If even that fails, do advanced unstuck
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { _advancedUnstuck.Force(); } });
        }

        private void MoveToCloseSpearfishingNode(IGameObject gameObject, Classes.Fish targetFish, FishingSpot expectedSpot)
        {
            var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
            var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);

            if (hSeparation < 3.5)
            {
                if (vSeparation < 3)
                {
                    if (Dalamud.Conditions[ConditionFlag.Mounted])
                    {
                        EnqueueDismount();
                    }
                    else
                    {
                        EnqueueSpearfishingNodeInteraction(gameObject, targetFish, expectedSpot);
                    }
                }

                if (!Dalamud.Conditions[ConditionFlag.Diving])
                {
                    TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Gathering]) { Navigate(gameObject.Position, false, nodeId: gameObject.BaseId); } });
                }
            }
            else if (hSeparation < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5))
            {
                Navigate(gameObject.Position, false, nodeId: gameObject.BaseId);
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position), nodeId: gameObject.BaseId);
            }
        }

        private void StopNavigation()
        {
            // Reset navigation logic here
            StopPathfinding();

            _navState = default;
            if (VNavmesh.Enabled)
            {
                VNavmesh.Path.Stop();
            }
        }

        private void StopPathfinding()
        {
            if (_navState.cts != null && _navState.task != null)
            {
                var cts = _navState.cts;
                cts.Cancel();
                _navState.task.ContinueWith(_ => cts.Dispose());
                _navState.task = null;
                _navState.cts = null;
            }
        }

        private unsafe void SetRotation(Angle angle)
        {
            if (!Player.Available)
            {
                return;
            }

            var playerObject = (GameObject*)Player.Object.Address;
            GatherBuddy.Log.Debug($"Setting rotation to {angle.Rad}");
            playerObject->SetRotation(angle.Rad);
        }

        private void Navigate(Vector3 destination, bool shouldFly, bool direct = false, uint? nodeId = null)
        {
            var canMount = Vector2.Distance(destination.ToVector2(), Player.Position.ToVector2()) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance && CanMount();
            if (!Dalamud.Conditions[ConditionFlag.Mounted] && canMount)
            {
                EnqueueMountUp();
                if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                {
                    StopNavigation();
                    return;
                }
            }

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;

            if (_navState.destination == destination && _navState.stage == PathfindingStage.RetryCombinedPathfinding
                && _navState.task == null && Environment.TickCount64 - _navState.lastTry > 1000)
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.cts = new CancellationTokenSource();
                _navState.task = FindCombinedPath(Player.Position, destination, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _navState.cts.Token);
                GatherBuddy.Log.Debug($"Retrying combined pathfinding to {destination}.");
                return;
            }

            if (_navState.destination == destination && (IsPathing || _navState.task != null))
            {
                return;
            }

            StopPathfinding();

            shouldFly &= canMount || Dalamud.Conditions[ConditionFlag.Mounted];
            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            var offsettedDestination = GetCorrectedDestination(destination, Player.Position, nodeId);
            _navState = default;
            _navState.destination = destination;
            _navState.flying = shouldFly;
            _navState.mountingUp = shouldFly && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.Diving];
            _navState.direct = direct || !shouldFly || landingDistance == 0 || destination != offsettedDestination || Dalamud.Conditions[ConditionFlag.Diving];
            _navState.offset = destination != offsettedDestination;
            _navState.cts = new CancellationTokenSource();

            if (_navState.direct)
            {
                _navState.task = VNavmesh.Nav.PathfindCancelable(Player.Position, offsettedDestination, shouldFly, _navState.cts.Token);
                GatherBuddy.Log.Debug($"Starting direct pathfinding to {offsettedDestination} (original: {destination}), flying: {shouldFly}.");
            }
            else
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.stage = PathfindingStage.InitialCombinedPathfinding;
                _navState.task = FindCombinedPath(Player.Position, destination, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _navState.cts.Token);
                GatherBuddy.Log.Debug($"Starting combined pathfinding to {destination}.");
            }
        }

        private void HandlePathfinding()
        {
            if (_navState.destination == default)
            {
                return;
            }

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;
            var player = Player.Position;
            List<Vector3> path;

            if (_navState.flying && _navState.stage == PathfindingStage.Done
                && !Dalamud.Conditions[ConditionFlag.Diving]
                && (path = VNavmesh.Path.ListWaypoints()).Count < _navState.landWP)
            {
                // Switch vnavmesh to no-fly mode when close to landing point
                path = [.. path]; // Clone, because Stop() clears the list
                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, false);
                _navState.flying = false;
                Dismount(); // Try to land (not dismount)
                GatherBuddy.Log.Debug($"Switching to ground movement, {path.Count} waypoints left.");
                return;
            }

            if (_navState.flying && _navState.mountingUp && Dalamud.Conditions[ConditionFlag.Mounted])
            {
                // Switch vnavmesh to fly mode when mounted up
                path = [.. VNavmesh.Path.ListWaypoints()
                    // Remove waypoints that are too close.
                    .SkipWhile(p => Vector2.DistanceSquared(player.AsVector2(), p.AsVector2()) < 16f)];

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, true);
                _navState.mountingUp = false;
                GatherBuddy.Log.Debug($"Switching to flying movement, {path.Count} waypoints left.");
                return;
            }

            if (_navState.task == null || _navState.cts == null || !_navState.task.IsCompleted)
            {
                return;
            }

            try
            {
                path = _navState.task.Result;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Pathfinding task threw an exception: {ex.Message}");
                StopNavigation();
                _advancedUnstuck.Force();
                return;
            }
            _navState.cts.Dispose();
            _navState.task = null;
            _navState.cts = null;

            if (path.Count == 0)
            {
                if (_navState.direct || _navState.stage == PathfindingStage.FallbackDirectPathfinding)
                {
                    GatherBuddy.Log.Error($"VNavmesh failed to find a path.");
                    StopNavigation();
                    _advancedUnstuck.Force();
                }
                else if (_navState.stage == PathfindingStage.InitialCombinedPathfinding)
                {
                    GatherBuddy.Log.Debug($"VNavmesh failed to find a combined path, falling back to direct path.");
                    _navState.stage++;
                    _navState.cts = new CancellationTokenSource();
                    _navState.task = VNavmesh.Nav.PathfindCancelable(player, _navState.destination, _navState.flying, _navState.cts.Token);
                }
                else if (_navState.stage != PathfindingStage.RetryCombinedPathfinding)
                {
                    GatherBuddy.Log.Error($"BUG: Pathfinding failure at unexpected stage {_navState.stage}.");
                    AbortAutoGather();
                }
            }
            else
            {
                var pathtype = "unknown";
                if (_navState.direct)
                {
                    pathtype = "direct";
                }
                else
                {
                    switch (_navState.stage)
                    {
                        case PathfindingStage.InitialCombinedPathfinding:
                        case PathfindingStage.RetryCombinedPathfinding:
                            pathtype = "combined";
                            _navState.stage = PathfindingStage.Done;
                            // Extract landing waypoint that is passed at the end of the list by FindCombinedPath()
                            var landWP = path[^1];
                            path.RemoveAt(path.Count - 1);
                            _navState.landWP = path.Count - path.FindLastIndex(x => x == landWP);
                            break;
                        case PathfindingStage.FallbackDirectPathfinding:
                            pathtype = "fallback direct";
                            _navState.stage++;
                            break;
                    }
                }

                if (_nodeApproachRecoveryKey is { } recovery
                    && recovery.Position == _navState.destination
                    && !_nodeApproachRecoveryAttempted)
                {
                    var hasEndpoint = _navState.direct
                        ? path.Count > 0
                        : GatherNodeApproachRules.TryGetCombinedPathGroundEndpoint(path, out _);
                    var endpoint = _navState.direct
                        ? (path.Count > 0 ? path[^1] : default)
                        : (GatherNodeApproachRules.TryGetCombinedPathGroundEndpoint(path, out var groundEndpoint)
                            ? groundEndpoint
                            : default);
                    if (!hasEndpoint || !GatherNodeApproachRules.IsPathEndpointReachable(recovery.Position, endpoint))
                    {
                        _nodeApproachOriginalRouteObserved = true;
                        GatherBuddy.Log.Warning(
                            $"[AutoGather] Rejecting gathering-node path ending outside interaction range: territory={recovery.TerritoryId}; node={recovery.NodeId}; player={Player.Position}; endpoint={endpoint}; nodePosition={recovery.Position}; horizontal={Vector2.Distance(endpoint.ToVector2(), recovery.Position.ToVector2()):F2}; vertical={Math.Abs(endpoint.Y - recovery.Position.Y):F2}; combined={!_navState.direct}");
                        StopNavigation();
                        return;
                    }
                }

                if (IsPathing)
                {
                    RemovePassedWaypoints(path);
                }

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, _navState.flying && !_navState.mountingUp);
                GatherBuddy.Log.Debug($"VNavmesh started moving via {pathtype} path, {path.Count} waypoints.");
            }

            static void RemovePassedWaypoints(List<Vector3> path)
            {
                var p = Player.Position;
                var t = path[^1];
                var fwd = new Vector3(t.X - p.X, 0f, t.Z - p.Z);
                if (fwd.LengthSquared() < 1f)
                {
                    return;
                }

                fwd = Vector3.Normalize(fwd);

                var n = 0;
                while (n < path.Count)
                {
                    var next = new Vector3(path[n].X - p.X, 0f, path[n].Z - p.Z);
                    if (Vector3.Dot(fwd, next) > 0)
                    {
                        break;
                    }

                    n++;
                }
                path.RemoveRange(0, n);
            }
        }

        private unsafe void Dismount()
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23); // Hotkey Z
        }

        private static async Task<List<Vector3>> FindCombinedPath(Vector3 player, Vector3 target, float landingDistance, bool flying, CancellationToken token)
        {
            var point = flying ? VNavmesh.Query.Mesh.PointOnFloor(player, false, 5f) : player;
            if (point == null)
            {
                return [];
            }

            var groundPath = await VNavmesh.Nav.PathfindCancelable(point.Value, target, false, token);
            if (groundPath.Count == 0)
            {
                return [];
            }

            var n = FindIntersection(groundPath, target, landingDistance);
            var landingWP = GetPointAtRadius(groundPath[n], groundPath[n + 1], target, landingDistance);
            var meshWP = VNavmesh.Query.Mesh.NearestPoint(landingWP, landingDistance, 10f); // Diadem fix
            if (meshWP == null)
            {
                return [];
            }

            if (Math.Abs(target.Y - meshWP.Value.Y) > 10f)
            {
                return []; // Sanity check
            }

            var flyPath = await VNavmesh.Nav.PathfindCancelable(player, meshWP.Value, true, token);
            if (flyPath.Count == 0)
            {
                return [];
            }

            if (flyPath.Count > 1 && Vector3.DistanceSquared(flyPath[^1], flyPath[^2]) < 0.01f)
            {
                flyPath.RemoveAt(flyPath.Count - 1);
            }

            landingWP = flyPath[^1];
            flyPath.AddRange(groundPath.Skip(n + 1));
            flyPath.Add(landingWP); // Pass landing waypoint at the end of the list; it will be handled separately.
            return flyPath;

            int FindIntersection(List<Vector3> wp, Vector3 p, float radius)
            {
                var r2 = radius * radius;
                for (var i = wp.Count - 2; i > 0; i--)
                {
                    if (Vector3.DistanceSquared(wp[i], p) > r2)
                    {
                        return i;
                    }
                }
                return 0;
            }

            Vector3 GetPointAtRadius(Vector3 p1, Vector3 p2, Vector3 target, float radius)
            {
                var d = (p2 - p1).ToVector2();        // Segment direction vector.
                var f = (p1 - target).ToVector2();    // Vector from target to segment start.

                // Quadratic coefficients: at^2 + bt + c = 0.
                var a = Vector2.Dot(d, d);
                var b = 2 * Vector2.Dot(f, d);
                var c = Vector2.Dot(f, f) - radius * radius;

                var discriminant = b * b - 4 * a * c;

                // If discriminant < 0, there is no intersection (math safety).
                if (discriminant < 0)
                {
                    return p1;
                }

                discriminant = (float)Math.Sqrt(discriminant);

                // Calculate the two possible intersection points on the infinite line.
                var t1 = (-b - discriminant) / (2 * a);
                var t2 = (-b + discriminant) / (2 * a);

                // Since one point is inside and one is outside, one 't' will be between 0 and 1.
                var t = (t1 >= 0 && t1 <= 1) ? t1 : t2;

                // Final point on the segment.
                return p1 + (p2 - p1) * t;
            }
        }

        private static Vector3 GetCorrectedDestination(in Vector3 destination, in Vector3 player, uint? nodeId)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            if (!GatherBuddy.Config.AutoGatherConfig.DisableRandomLandingPositions
                && nodeId.HasValue
                && AutoOffsets.TryGetRandomOffset(nodeId.Value, destination, player, out var offset))
            {
                GatherBuddy.Log.Debug($"Using auto-offset for node {nodeId.Value}: {offset}. Distance to node: {Vector2.Distance(offset.ToVector2(), destination.ToVector2()):F2}y, angle: {Math.Acos(Vector2.Dot(Vector2.Normalize((player - destination).ToVector2()), Vector2.Normalize((offset - destination).ToVector2()))) * 180.0 / Math.PI:F1}°");
                return offset;
            }

            try
            {
                float separation;
                if (WorldData.NodeOffsets.TryGetValue(destination, out offset))
                {
                    offset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation).GetValueOrDefault(offset);
                    if ((separation = Vector2.Distance(offset.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                    {
                        GatherBuddy.Log.Warning($"Offset is ignored because the horizontal separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxHorizontalSeparation}.");
                    }
                    else if ((separation = Math.Abs(offset.Y - destination.Y)) > MaxVerticalSeparation)
                    {
                        GatherBuddy.Log.Warning($"Offset is ignored because the vertical separation {separation} is too large after correcting for mesh. Maximum allowed is {MaxVerticalSeparation}.");
                    }
                    else
                    {
                        return offset;
                    }
                }

                // There was code that corrected the destination to the nearest point on the mesh, but testing showed that
                // navigating directly to the node yields better results, and points within landing distance are on the mesh anyway.
            }
            catch (Exception) { }

            return destination;
        }

        private unsafe void MoveToFishingSpot(Vector3 position, Angle angle)
        {
            Navigate(position, ShouldFly(position));
        }

        public static Aetheryte? FindClosestAetheryte(ILocation location)
        {
            var aetheryte = location.ClosestAetheryte;

            var territory = location.Territory;
            if (ForcedAetherytes.ZonesWithoutAetherytes.FirstOrDefault(x => x.ZoneId == territory.Id).AetheryteId is var alt && alt > 0)
            {
                territory = GatherBuddy.GameData.Aetherytes[alt].Territory;
            }

            if (aetheryte == null || !Teleporter.IsAttuned(aetheryte.Id) || aetheryte.Territory != territory)
            {
                aetheryte = territory.Aetherytes
                    .Where(a => Teleporter.IsAttuned(a.Id))
                    .OrderBy(a => a.WorldDistance(territory.Id, location.IntegralXCoord, location.IntegralYCoord))
                    .FirstOrDefault();
            }

            return aetheryte;
        }

        private bool MoveToTerritory(ILocation location)
        {
            var aetheryte = FindClosestAetheryte(location);
            if (aetheryte == null)
            {
                Communicator.PrintError("Couldn't find an attuned aetheryte to teleport to.");
                return false;
            }

            GatherBuddy.Log.Debug($"[MoveToTerritory] Teleporting to {aetheryte.Name}");
            EnqueueActionWithDelay(() => Teleporter.Teleport(aetheryte.Id));
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.BetweenAreas]);
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.BetweenAreas]);
            TaskManager.DelayNext(1500);

            return true;
        }
    }
}
