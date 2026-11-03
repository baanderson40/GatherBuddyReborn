using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utility;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace GatherBuddy.Vulcan.Vendors;

public class VendorNavigator
{
    private static readonly string[] ResidentialDistrictMenuEntries =
    [
        "Residential District Aethernet.",
        "冒険者居住区転送",
        "Wohngebiet",
        "Quartier résidentiel",
        "冒险者住宅区传送",
        "冒險者住宅區傳送",
        "모험가 거주구 이동",
    ];

    private static readonly string[] GoToWardMenuEntries =
    [
        "Go to specified ward. (Review Tabs)",
        "区を指定して移動（ハウスアピール確認）",
        "Zum angegebenen Bezirk (Zweck der Unterkunft einsehen)",
        "Spécifier le secteur où aller (Voir les attraits)",
        "移动到指定小区（查看房屋宣传标签）",
        "移動到指定小區（查看房屋宣傳標籤）",
        "移動到指定社區（查看房屋宣傳標籤）",
        "구역을 지정하여 이동(주택 정보 확인)",
    ];

    private static readonly string[] HousingTravelConfirmationTexts =
    [
        "Travel to",
        "よろしいですか？",
        "Zu Wohnbezirk",
        "Vous allez vous rendre à",
        "要移动到",
        "要移動到",
        "이동하시겠습니까?",
    ];
    private const uint FirmamentTerritoryId = 886u;
    private const uint FirmamentAetheryteId = 70u;
    private readonly record struct CustomAethernetDestination(Vector2 Position, uint PlaceNameId);

    private static readonly CustomAethernetDestination[] FirmamentAethernetDestinations =
    [
        new(new Vector2(23.9f, 169.4f), 3436u),
        new(new Vector2(76.0f, 10.3f), 3473u),
        new(new Vector2(149.5f, 98.6f), 3475u),
        new(new Vector2(207.8f, -25.6f), 3474u),
        new(new Vector2(-78.8f, 76.0f), 3525u),
        new(new Vector2(-132.6f, -14.7f), 3528u),
        new(new Vector2(-91.7f, -115.2f), 3646u),
        new(new Vector2(114.3f, -107.4f), 3645u),
    ];

    private static readonly uint[] KnownAethernetShardSeedDataIds =
    [
        2000151u,
        2014665u,
        2014664u,
        2003395u,
    ];

    private static HashSet<uint>? _knownAethernetShardDataIds;
    private enum HousingDistrict
    {
        Mist,
        LavenderBeds,
        Goblet,
        Empyreum,
        Shirogane,
    }

    private static float GetPathfindTolerance(IGameObject? liveNpc, NavigationDestinationMode destinationMode)
    {
        if (liveNpc == null || destinationMode != NavigationDestinationMode.TargetInteractionRange)
            return 0f;
        return VendorApproachCompletionDistance;
    }
    private enum State
    {
        Idle,
        Teleporting,
        WaitingForTeleport,
        WaitingForZoneLoad,
        Navigating,
        ReadyToPurchase,
        Failed,
    }

    private enum NavigationDestinationMode
    {
        TargetInteractionRange,
        SafeStagingPoint,
        FinalApproachPoint,
    }

    private static bool TryFindHousingRoute(uint targetTerritoryId, out uint aetheryteId, bool logRouteDiagnostics = true)
    {
        aetheryteId = 0;
        if (!TryGetHousingDistrict(targetTerritoryId, out var district))
            return false;

        aetheryteId = district switch
        {
            HousingDistrict.Mist         => 8u,
            HousingDistrict.LavenderBeds => 2u,
            HousingDistrict.Goblet       => 9u,
            HousingDistrict.Empyreum     => 70u,
            HousingDistrict.Shirogane    => 111u,
            _                            => 0u,
        };

        if (aetheryteId == 0)
            return true;

        if (!Teleporter.IsAttuned(aetheryteId))
        {
            if (logRouteDiagnostics)
                GatherBuddy.Log.Debug($"[VendorNavigator] Housing route for territory {targetTerritoryId} requires unattuned aetheryte {aetheryteId}");
            aetheryteId = 0;
        }
        else if (logRouteDiagnostics)
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Using housing route for territory {targetTerritoryId}: district={district}, cityAetheryte={aetheryteId}, ward=1");
        }

        return true;
    }

    private static bool TryFindFirmamentRoute(uint targetTerritoryId, out uint aetheryteId, bool logRouteDiagnostics = true)
    {
        aetheryteId = 0;
        if (targetTerritoryId != FirmamentTerritoryId)
            return false;

        if (!Lifestream.SupportsFirmamentTeleport)
        {
            if (logRouteDiagnostics)
                GatherBuddy.Log.Debug($"[VendorNavigator] Firmament route for territory {targetTerritoryId} requires Lifestream Firmament entry support.");
            return true;
        }

        aetheryteId = FirmamentAetheryteId;
        if (!Teleporter.IsAttuned(aetheryteId))
        {
            if (logRouteDiagnostics)
                GatherBuddy.Log.Debug($"[VendorNavigator] Firmament route for territory {targetTerritoryId} requires unattuned aetheryte {aetheryteId}");
            aetheryteId = 0;
        }
        else if (logRouteDiagnostics)
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Using Firmament route for territory {targetTerritoryId}: cityAetheryte={aetheryteId}");
        }

        return true;
    }

    private bool HandlePendingFirmamentEntry()
    {
        if (!_pendingFirmamentEntry || !Lifestream.SupportsFirmamentTeleport)
            return false;

        if (_pendingAethernetParentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
            return true;

        if (!CanIssueTeleportRequest())
            return true;

        if (Lifestream.ActiveAetheryteId != FirmamentAetheryteId)
        {
            LogHousingEntryStatus($"[VendorNavigator] Waiting to reach Foundation aetheryte {FirmamentAetheryteId} before entering the Firmament (active={Lifestream.ActiveAetheryteId})");
            return true;
        }

        if (Lifestream.TryEnterFirmament())
        {
            GatherBuddy.Log.Debug("[VendorNavigator] Entering the Firmament from the Foundation aetheryte.");
            _stateStartTime = DateTime.UtcNow;
            _teleportWaitStartTime = _stateStartTime;
            _lastTeleportWaitDiagnosticsLogTime = DateTime.MinValue;
            return true;
        }

        LogHousingEntryStatus("[VendorNavigator] Lifestream did not accept the Firmament entry request yet; retrying.");
        _stateStartTime = DateTime.UtcNow;
        return true;
    }

    private static bool TryFindNearestShardInTerritory(uint territoryId, Vector3 npcPosition, out string shardName, out float shardDistanceToVendor, out float shardDistanceToPlayer)
    {
        if (TryGetHousingDistrict(territoryId, out var housingDistrict))
            return TryFindNearestResidentialShard(housingDistrict, npcPosition, out shardName, out shardDistanceToVendor, out shardDistanceToPlayer);
        if (TryFindNearestCustomAethernetShardInTerritory(territoryId, npcPosition, out shardName, out shardDistanceToVendor, out shardDistanceToPlayer))
            return true;
        shardName             = string.Empty;
        shardDistanceToVendor = float.MaxValue;
        shardDistanceToPlayer = float.MaxValue;

        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return false;

        var npcXZ    = new Vector2(npcPosition.X, npcPosition.Z);
        var player   = Dalamud.Objects.LocalPlayer;
        var playerXZ = player != null ? new Vector2(player.Position.X, player.Position.Z) : Vector2.Zero;

        foreach (var shard in aetheryteSheet)
        {
            if (shard.IsAetheryte || shard.Territory.RowId != territoryId || shard.AethernetName.RowId == 0)
                continue;

            var currentShardName = shard.AethernetName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(currentShardName))
                continue;

            var shardXZ = GetAetheryteXZ(shard);
            if (!shardXZ.HasValue)
                continue;

            var distanceToVendor = Vector2.Distance(npcXZ, shardXZ.Value);
            if (distanceToVendor >= shardDistanceToVendor)
                continue;

            shardName             = currentShardName;
            shardDistanceToVendor = distanceToVendor;
            shardDistanceToPlayer = player != null ? Vector2.Distance(playerXZ, shardXZ.Value) : float.MaxValue;
        }

        return shardName.Length > 0;
    }

    private static bool TryFindNearestCustomAethernetShardInTerritory(uint territoryId, Vector3 npcPosition, out string shardName, out float shardDistanceToVendor, out float shardDistanceToPlayer)
    {
        shardName             = string.Empty;
        shardDistanceToVendor = float.MaxValue;
        shardDistanceToPlayer = float.MaxValue;

        var destinations = territoryId switch
        {
            FirmamentTerritoryId => FirmamentAethernetDestinations,
            _                    => Array.Empty<CustomAethernetDestination>(),
        };
        if (destinations.Length == 0)
            return false;

        var placeNameSheet = Dalamud.GameData.GetExcelSheet<PlaceName>();
        if (placeNameSheet == null)
            return false;

        var npcXZ    = new Vector2(npcPosition.X, npcPosition.Z);
        var player   = Dalamud.Objects.LocalPlayer;
        var playerXZ = player != null ? new Vector2(player.Position.X, player.Position.Z) : Vector2.Zero;

        foreach (var destination in destinations)
        {
            if (!placeNameSheet.TryGetRow(destination.PlaceNameId, out var placeName))
                continue;

            var currentShardName = placeName.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(currentShardName))
                continue;

            var distanceToVendor = Vector2.Distance(npcXZ, destination.Position);
            if (distanceToVendor >= shardDistanceToVendor)
                continue;

            shardName             = currentShardName;
            shardDistanceToVendor = distanceToVendor;
            shardDistanceToPlayer = player != null ? Vector2.Distance(playerXZ, destination.Position) : float.MaxValue;
        }

        return shardName.Length > 0;
    }

    private static bool TryFindNearestResidentialShard(HousingDistrict district, Vector3 npcPosition, out string shardName, out float shardDistanceToVendor, out float shardDistanceToPlayer)
    {
        shardName             = string.Empty;
        shardDistanceToVendor = float.MaxValue;
        shardDistanceToPlayer = float.MaxValue;

        var housingAethernetSheet = Dalamud.GameData.GetExcelSheet<HousingAethernet>();
        var mapSheet              = Dalamud.GameData.GetExcelSheet<Map>();
        if (housingAethernetSheet == null || mapSheet == null)
            return false;

        var territoryCandidates = GetHousingDistrictTerritoryCandidates(district);
        var player              = Dalamud.Objects.LocalPlayer;
        var playerXZ            = player != null ? new Vector2(player.Position.X, player.Position.Z) : Vector2.Zero;
        var npcXZ               = new Vector2(npcPosition.X, npcPosition.Z);
        foreach (var territoryCandidate in territoryCandidates)
        {
            var housingShards = housingAethernetSheet
                .Where(a => a.TerritoryType.RowId == territoryCandidate)
                .OrderBy(a => a.Order)
                .ToList();
            if (housingShards.Count == 0)
                continue;

            var subdivisionModifier = GetHousingSubdivisionModifier(district);
            var subdivisionStart    = housingShards.Count / 2;
            for (var index = 0; index < housingShards.Count; index++)
            {
                var housingShard = housingShards[index];
                var currentShardName = housingShard.PlaceName.Value.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(currentShardName))
                    continue;

                var shardXZ = GetHousingAetheryteXZ(housingShard, territoryCandidate, mapSheet);
                if (!shardXZ.HasValue)
                    continue;
                var shardPosition = shardXZ.Value;

                if (index >= subdivisionStart)
                    shardPosition += subdivisionModifier;

                var distanceToVendor = Vector2.Distance(npcXZ, shardPosition);
                if (distanceToVendor >= shardDistanceToVendor)
                    continue;

                shardName             = currentShardName;
                shardDistanceToVendor = distanceToVendor;
                shardDistanceToPlayer = player != null ? Vector2.Distance(playerXZ, shardPosition) : float.MaxValue;
            }

            if (shardName.Length > 0)
                return true;
        }

        GatherBuddy.Log.Debug($"[VendorNavigator] No residential shard candidate found for housing district {district}");
        return false;
    }

    private static bool TryGetHousingDistrict(uint territoryId, out HousingDistrict district)
    {
        switch (territoryId)
        {
            case 136:
            case 339:
                district = HousingDistrict.Mist;
                return true;
            case 340:
                district = HousingDistrict.LavenderBeds;
                return true;
            case 341:
                district = HousingDistrict.Goblet;
                return true;
            case 649:
            case 979:
                district = HousingDistrict.Empyreum;
                return true;
            case 641:
                district = HousingDistrict.Shirogane;
                return true;
            default:
                district = default;
                return false;
        }
    }

    private static uint[] GetHousingDistrictTerritoryCandidates(HousingDistrict district)
        => district switch
        {
            HousingDistrict.Mist         => [339u, 136u],
            HousingDistrict.LavenderBeds => [340u],
            HousingDistrict.Goblet       => [341u],
            HousingDistrict.Empyreum     => [979u, 649u],
            HousingDistrict.Shirogane    => [641u],
            _                            => Array.Empty<uint>(),
        };

    private static Vector2 GetHousingSubdivisionModifier(HousingDistrict district)
        => district == HousingDistrict.Empyreum
            ? new Vector2(-704f, -654f)
            : new Vector2(-700f, -700f);

    private static Vector2? GetHousingAetheryteXZ(HousingAethernet housingShard, uint territoryId, ExcelSheet<Map> mapSheet)
    {
        var map = mapSheet.FirstOrDefault(m => m.TerritoryType.RowId == territoryId);
        if (map.RowId == 0)
            return null;
        var markerSheet = Dalamud.GameData.GetSubrowExcelSheet<MapMarker>();
        if (markerSheet == null)
            return null;

        foreach (var markerRow in markerSheet)
        {
            foreach (var marker in markerRow)
            {
                if (marker.DataType != 4 || marker.DataKey.RowId != housingShard.PlaceName.RowId)
                    continue;

                var x = (marker.X - 1024f) / (map.SizeFactor / 100f);
                var z = (marker.Y - 1024f) / (map.SizeFactor / 100f);
                return new Vector2(x, z);
            }
        }

        return null;
    }

    internal static bool IsEquivalentTerritory(uint currentTerritoryId, uint targetTerritoryId)
    {
        if (currentTerritoryId == targetTerritoryId)
            return true;

        return TryGetHousingDistrict(currentTerritoryId, out var currentDistrict)
            && TryGetHousingDistrict(targetTerritoryId, out var targetDistrict)
            && currentDistrict == targetDistrict;
    }

    private static bool ShouldForceFreshHousingEntry(uint currentTerritoryId, uint targetTerritoryId)
        => TryGetHousingDistrict(currentTerritoryId, out var currentDistrict)
        && TryGetHousingDistrict(targetTerritoryId, out var targetDistrict)
        && currentDistrict == targetDistrict;

    private const float  LiveNpcInteractionDistance         = 5.0f;
    private const float  CachedInteractionDistance          = 5.0f;
    private const float  LocalAethernetDestinationProximityThreshold = 10.0f;
    private const float  LocalAethernetEstimatedInteractionCost      = 10.0f;
    private const float  LocalAethernetMinimumSavings                = 15.0f;
    private const float  RepathDistanceThreshold    = 1.5f;
    private const double RepathCooldown             = 0.75;
    private const double TeleportCooldown           = 3.0;
    private const double TeleportWaitDiagnosticsDelay = 15.0;
    private const double TeleportWaitDiagnosticsCooldown = 5.0;
    private const double ZoneLoadWait               = 5.0;
    private const double MountRetryCooldown         = 2.0;
    private const double LandingRetryCooldown       = 1.0;
    private const double VendorApproachDiagnosticsCooldown = 5.0;
    private const double VendorGroundApproachStabilizationSeconds = 2.0;
    private const double VendorGroundProgressTimeout = 2.0;
    private const double VendorGroundProgressRetryCooldown = 1.0;
    private const double VendorGroundInteractionRangeStallTimeout = 0.75;
    private const double VendorGroundInteractionRangeFallbackTimeout = 1.0;
    private const double VendorInteractionRangeFallbackDurationSeconds = 3.0;
    private const float  VendorGroundProgressMinimumMovement = 1.0f;
    private const float  VendorGroundProgressMinimumDistanceGain = 0.75f;
    private const float  VendorLandingDistance      = 25.0f;
    private const float  VendorGroundApproachDistance = 25.0f;
    private const float  VendorDirectLiveNpcApproachDistance = 20.0f;
    private const float  VendorStagingPointArrivalDistance = 2.5f;
    private const float  VendorApproachCompletionDistance = 1.5f;
    private const float  VendorApproachFloorSearchHeight  = 10.0f;
    private const float  VendorApproachMeshSearchRadius   = 3.0f;
    private const float  VendorFallbackFloorSearchRadius  = 2.0f;
    private const float  VendorApproachMaxHorizontalDrift = 2.5f;
    private const float  VendorStagingMaxHorizontalDrift  = 4.0f;
    private const float  VendorApproachMaxVerticalDrift   = 4.0f;
    private const float  VendorInteractionMaxVerticalDistance = 2.0f;
    private const float  VendorApproachDistanceStep       = 0.5f;
    private const float  VendorApproachMinimumStandOff    = 0.35f;
    private const float  VendorCloseCombinedLandingDistanceMinimum = 4.0f;
    private const float  VendorCloseCombinedLandingDistanceMaximum = 12.0f;
    private const float  VendorCloseCombinedLandingDistanceBuffer  = 2.0f;

    private static readonly float[] VendorApproachCandidateAngles =
    [
        0f,
        MathF.PI / 8f,
        -MathF.PI / 8f,
        MathF.PI / 4f,
        -MathF.PI / 4f,
        3f * MathF.PI / 8f,
        -3f * MathF.PI / 8f,
        MathF.PI / 2f,
        -MathF.PI / 2f,
        3f * MathF.PI / 4f,
        -3f * MathF.PI / 4f,
        MathF.PI,
    ];

    private static readonly float[] VendorLandingCandidateDistances =
    [
        8f,
        10f,
        12.5f,
        15f,
        18f,
        22f,
        25f,
    ];

    private static readonly float[] VendorCloseStagingCandidateDistances =
    [
        3.5f,
        4.5f,
        5.5f,
        6.5f,
        7.5f,
    ];

    private State              _state = State.Idle;
    private readonly AethernetSourceApproach _sourceApproach = new();
    private DateTime _lastSourceActionTime;
    private DateTime _lastSourceLogTime;
    private (ulong Source, AethernetSourceAction Action)? _lastSourceLog;
    private Task<List<Vector3>>? _sourcePathTask;
    private CancellationTokenSource? _sourcePathCancellation;
    private ulong _sourcePathObjectId;
    private VendorNpcLocation? _target;
    private DateTime           _stateStartTime;
    private DateTime           _teleportWaitStartTime;
    private DateTime           _lastRepathTime;
    private bool               _teleportAttempted;
    private string?            _pendingAethernetName;
    private bool               _pendingAethernetNeedsSourceApproach;
    private bool               _pendingHousingEntry;
    private bool               _pendingFirmamentEntry;
    private string?            _pendingHousingShardTeleportName;
    private float              _pendingHousingShardDistanceToVendor;
    private uint               _pendingAethernetParentTerritoryId;
    private Vector3            _navigationDestination;
    private float              _navigationDestinationTolerance;
    private bool               _hasNavigationDestination;
    private bool               _usingLiveNpcDestination;
    private CancellationTokenSource? _pathCancellationTokenSource;
    private Task<List<Vector3>>?     _pathTask;
    private DateTime                 _lastMountAttemptTime;
    private DateTime                 _lastLandingAttemptTime;
    private DateTime                 _lastHousingEntryActionTime;
    private DateTime                 _lastHousingEntryStatusLogTime;
    private bool                     _pathUsesFlight;
    private bool                     _pathUsesCombinedApproach;
    private bool                     _mountingUp;
    private bool                     _waitingForMount;
    private int                      _landingWaypointCount;
    private bool                     _attemptedHousingShardTeleport;
    private uint                     _forcedHousingEntryOriginTerritoryId;
    private bool                     _awaitingForcedHousingOriginExit;
    private readonly DiagnosticThrottle<(ulong Npc, string Category)> _navigationDiagnostics = new(TimeSpan.FromSeconds(VendorApproachDiagnosticsCooldown), capacity: 64);
    private DateTime                 _forceGroundApproachUntil;
    private Vector3                  _lastGroundProgressPosition;
    private float                    _lastGroundProgressDistance = float.MaxValue;
    private DateTime                 _lastGroundProgressTime;
    private Vector3                  _lastGroundRetryPosition;
    private DateTime                 _lastGroundRetryTime;
    private int                      _groundRetryCount;
    private DateTime                 _forceInteractionRangeApproachUntil;
    private DateTime                 _lastTeleportWaitDiagnosticsLogTime;

    public bool               IsReadyToPurchase => _state == State.ReadyToPurchase;
    public bool               IsFailed          => _state == State.Failed;
    public bool               IsActive          => _state is not (State.Idle or State.ReadyToPurchase or State.Failed);
    public VendorNpcLocation? CurrentTarget     => _target;

    public void PlaceMapMarker(VendorNpcLocation target)
        => PlaceMapFlag(target);

    public void StartNavigation(VendorNpcLocation target, bool continueCurrentVendorInteraction = false, Guid? ownerSessionId = null)
    {
        if (!GatherBuddy.VendorPurchaseManager.AutomationLease.Allows(ownerSessionId)) return;
        if (ownerSessionId == null && Crafting.CraftingGatherBridge.HasAcquisitionOwnership) return;
        Stop(ownerSessionId);
        ResetSourceApproach();
        _target            = target;
        _teleportAttempted = false;
        _stateStartTime    = DateTime.UtcNow;
        _forcedHousingEntryOriginTerritoryId = 0;
        _awaitingForcedHousingOriginExit     = false;
        _teleportWaitStartTime               = DateTime.MinValue;
        _lastTeleportWaitDiagnosticsLogTime  = DateTime.MinValue;

        GatherBuddy.Log.Information($"[VendorNavigator] Starting navigation to {target.NpcName} (territory {target.TerritoryId})");

        PlaceMapFlag(target);

        var shouldForceFreshHousingEntry = ShouldForceFreshHousingEntry(Dalamud.ClientState.TerritoryType, target.TerritoryId);

        if (!continueCurrentVendorInteraction && shouldForceFreshHousingEntry)
        {
            _forcedHousingEntryOriginTerritoryId = Dalamud.ClientState.TerritoryType;
            _awaitingForcedHousingOriginExit     = true;
            GatherBuddy.Log.Debug($"[VendorNavigator] Starting inside housing district {Dalamud.ClientState.TerritoryType} for housing target {target.TerritoryId}, forcing a fresh city entry route");
            _state = State.Teleporting;
            return;
        }

        var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (Dalamud.ClientState.TerritoryType == target.TerritoryId && playerPos != Vector3.Zero)
        {
            var liveNpc = FindLiveNpcObject();
            if (liveNpc != null)
                TryTargetLiveNpc(liveNpc);
            var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out _);
            if (destinationMode != NavigationDestinationMode.SafeStagingPoint
             && HasReachedNavigationDestination(playerPos, liveNpc, destination, destinationMode))
            {
                GatherBuddy.Log.Information($"[VendorNavigator] Already adjacent to {GetDisplayName(liveNpc)} ({GetTargetDistance(playerPos, liveNpc):F1}m)");
                _state = State.ReadyToPurchase;
                return;
            }

            if (TryPrepareCurrentTerritoryAethernetShortcut(playerPos, liveNpc))
                return;

            _state = State.WaitingForZoneLoad;
        }
        else if (IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, target.TerritoryId) && playerPos != Vector3.Zero)
        {
            _state = State.WaitingForZoneLoad;
        }
        else
        {
            _state = State.Teleporting;
        }
    }

    private static bool CanIssueTeleportRequest()
        => VendorInteractionHelper.IsReadyToLeaveVendor();

    public void Update()
    {
        if (_target == null || _state is State.Idle or State.ReadyToPurchase or State.Failed)
            return;

        try
        {
            switch (_state)
            {
                case State.Teleporting:        UpdateTeleporting();        break;
                case State.WaitingForTeleport: UpdateWaitingForTeleport(); break;
                case State.WaitingForZoneLoad: UpdateWaitingForZoneLoad(); break;
                case State.Navigating:         UpdateNavigating();         break;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[VendorNavigator] Error in Update: {ex.Message}");
            StopPathing();
            _sourceApproach.Finish();
            _state = State.Failed;
        }
    }

    public void Stop(Guid? ownerSessionId = null)
    {
        if (!GatherBuddy.VendorPurchaseManager.AutomationLease.Allows(ownerSessionId)) return;
        StopPathing();
        _sourceApproach.Finish();
        _state                              = State.Idle;
        _target                             = null;
        _pendingAethernetName               = null;
        _pendingAethernetNeedsSourceApproach = false;
        _pendingHousingEntry                = false;
        _pendingFirmamentEntry              = false;
        _pendingHousingShardTeleportName    = null;
        _pendingHousingShardDistanceToVendor = 0f;
        _pendingAethernetParentTerritoryId  = 0;
        _navigationDestination              = Vector3.Zero;
        _navigationDestinationTolerance     = 0f;
        _hasNavigationDestination           = false;
        _usingLiveNpcDestination            = false;
        _lastRepathTime                     = DateTime.MinValue;
        _lastMountAttemptTime               = DateTime.MinValue;
        _lastLandingAttemptTime             = DateTime.MinValue;
        _lastHousingEntryActionTime         = DateTime.MinValue;
        _lastHousingEntryStatusLogTime      = DateTime.MinValue;
        _pathUsesFlight                     = false;
        _pathUsesCombinedApproach           = false;
        _mountingUp                         = false;
        _waitingForMount                    = false;
        _landingWaypointCount               = 0;
        _attemptedHousingShardTeleport       = false;
        _forcedHousingEntryOriginTerritoryId = 0;
        _awaitingForcedHousingOriginExit     = false;
        _navigationDiagnostics.Clear();
        _forceGroundApproachUntil            = DateTime.MinValue;
        _lastGroundProgressPosition          = Vector3.Zero;
        _lastGroundProgressDistance          = float.MaxValue;
        _lastGroundProgressTime              = DateTime.MinValue;
        _lastGroundRetryPosition             = Vector3.Zero;
        _lastGroundRetryTime                 = DateTime.MinValue;
        _groundRetryCount                    = 0;
        _forceInteractionRangeApproachUntil  = DateTime.MinValue;
        _teleportWaitStartTime               = DateTime.MinValue;
        _lastTeleportWaitDiagnosticsLogTime  = DateTime.MinValue;
    }

    private static unsafe void PlaceMapFlag(VendorNpcLocation target)
    {
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null) return;

            var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
            if (territorySheet == null || !territorySheet.TryGetRow(target.TerritoryId, out var territory)) return;
            var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
            Map? map = null;
            if (target.MapRowId != 0 && mapSheet != null && mapSheet.TryGetRow(target.MapRowId, out var selectedMap))
                map = selectedMap;
            else
                map = territory.Map.ValueNullable;
            if (map == null) return;
            var mapRowId = map.Value.RowId;


            agentMap->FlagMarkerCount = 0;
            agentMap->SetFlagMapMarker(target.TerritoryId, mapRowId, target.Position.X, target.Position.Z, 60561U);
            agentMap->OpenMap(mapRowId, target.TerritoryId, target.NpcName, MapType.FlagMarker);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[VendorNavigator] Failed to place map flag: {ex.Message}");
        }
    }

    private void UpdateTeleporting()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
            return;
        if (!CanIssueTeleportRequest())
            return;

        if (_teleportAttempted) return;

        var (aetheryteId, aethernetName, requiresHousingEntry, requiresFirmamentEntry) = FindBestRoute(_target!.TerritoryId, _target.Position, true);
        if (aetheryteId == 0)
        {
            if (TryGetHousingDistrict(_target.TerritoryId, out _))
                GatherBuddy.Log.Error($"[VendorNavigator] Housing route unavailable for territory {_target.TerritoryId}; the required city aetheryte may be unattuned.");
            else if (_target.TerritoryId == FirmamentTerritoryId)
                GatherBuddy.Log.Error($"[VendorNavigator] Firmament route unavailable for territory {_target.TerritoryId}; Lifestream may be unavailable or the Foundation aetheryte may be unattuned.");
            GatherBuddy.Log.Error($"[VendorNavigator] No route found to {_target.NpcName} in territory {_target.TerritoryId} (source={_target.Source}, map={_target.MapRowId}, position={_target.Position})");
            GatherBuddy.Log.Error($"[VendorNavigator] No route found to territory {_target.TerritoryId}");
            _state = State.Failed;
            return;
        }
        var parentTerritoryId = (aethernetName != null || requiresHousingEntry || requiresFirmamentEntry)
            ? Dalamud.GameData.GetExcelSheet<Aetheryte>()?.GetRow(aetheryteId).Territory.RowId ?? 0
            : 0;
        if (requiresFirmamentEntry
         && parentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType == parentTerritoryId
         && Lifestream.ActiveAetheryteId == FirmamentAetheryteId)
        {
            QueueFirmamentEntry(parentTerritoryId);
            return;
        }

        if (aethernetName != null
         && parentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType == parentTerritoryId)
        {
            QueueLocalAethernetTeleport(aethernetName, parentTerritoryId);
            return;
        }

        if (Teleporter.Teleport(aetheryteId))
        {
            _teleportAttempted                  = true;
            _pendingAethernetName               = aethernetName;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingHousingEntry                = requiresHousingEntry;
            _pendingFirmamentEntry              = requiresFirmamentEntry;
            _pendingAethernetParentTerritoryId  = parentTerritoryId;
            _stateStartTime       = DateTime.UtcNow;
            _teleportWaitStartTime = _stateStartTime;
            _lastTeleportWaitDiagnosticsLogTime = DateTime.MinValue;
            _state                = State.WaitingForTeleport;
            var followUp = aethernetName != null
                ? $", then aethernet to '{aethernetName}'"
                : requiresHousingEntry
                    ? ", then enter the housing ward from the city aetheryte"
                    : requiresFirmamentEntry
                        ? ", then enter the Firmament from the Foundation aetheryte"
                    : string.Empty;
            GatherBuddy.Log.Debug($"[VendorNavigator] Teleporting to aetheryte {aetheryteId}{followUp}");
        }
        else
        {
            GatherBuddy.Log.Error("[VendorNavigator] Teleport failed");
            _state = State.Failed;
        }
    }

    private void UpdateWaitingForTeleport()
    {
        if ((_pendingAethernetName != null || _pendingFirmamentEntry) && Lifestream.Enabled && Lifestream.IsBusy()) return;
        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < TeleportCooldown) return;
        LogLongTeleportWaitIfNeeded();
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]) return;
        if (!GenericHelpers.IsScreenReady())
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for screen ready before continuing teleport/aethernet travel");
            return;
        }
        if (_awaitingForcedHousingOriginExit)
        {
            if (Dalamud.ClientState.TerritoryType == _forcedHousingEntryOriginTerritoryId)
            {
                LogHousingEntryStatus($"[VendorNavigator] Waiting to leave original housing territory {_forcedHousingEntryOriginTerritoryId} before continuing the fresh city entry route");
                return;
            }

            _awaitingForcedHousingOriginExit = false;
        }
        if (_pendingAethernetName != null && HandlePendingLocalAethernetTeleport())
            return;
        if (IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target!.TerritoryId))
        {
            _pendingAethernetName = null;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingHousingEntry = false;
            _pendingFirmamentEntry = false;
            _pendingAethernetParentTerritoryId = 0;
            _forcedHousingEntryOriginTerritoryId = 0;
            _teleportWaitStartTime = DateTime.MinValue;
            _lastTeleportWaitDiagnosticsLogTime = DateTime.MinValue;
            _state                = State.WaitingForZoneLoad;
            _stateStartTime       = DateTime.UtcNow;
            return;
        }
        if (_pendingHousingEntry)
        {
            if (_pendingAethernetParentTerritoryId != 0 &&
                Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
                return;
            TryAdvanceHousingEntry();
            return;
        }
        if (_pendingFirmamentEntry)
        {
            if (_pendingAethernetParentTerritoryId != 0
             && Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
                return;
            if (HandlePendingFirmamentEntry())
                return;
        }

    }

    private void UpdateWaitingForZoneLoad()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51]) return;
        if (!GenericHelpers.IsScreenReady())
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for screen ready before starting post-teleport vendor navigation");
            return;
        }
        var pos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (pos == Vector3.Zero) return;
        if ((DateTime.UtcNow - _stateStartTime).TotalSeconds < ZoneLoadWait) return;
        if (!VNavmesh.Nav.IsReady()) return;
        var liveNpc = FindLiveNpcObject();
        if (liveNpc != null)
            TryTargetLiveNpc(liveNpc);
        var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out var navigationReason);

        if (destinationMode != NavigationDestinationMode.SafeStagingPoint
         && HasReachedNavigationDestination(pos, liveNpc, destination, destinationMode))
        {
            GatherBuddy.Log.Information($"[VendorNavigator] Already near {GetDisplayName(liveNpc)} after zone load ({GetTargetDistance(pos, liveNpc):F1}m)");
            _state = State.ReadyToPurchase;
            return;
        }

        if (HandlePendingHousingShardTeleport())
            return;

        if (!_attemptedHousingShardTeleport
         && _target != null
         && TryGetHousingDistrict(_target.TerritoryId, out _)
         && Lifestream.Enabled
         && !Lifestream.IsBusy()
         && TryFindNearestShardInTerritory(_target.TerritoryId, _target.Position, out var shardName, out var shardDistanceToVendor, out var shardDistanceToPlayer))
        {
            if (shardDistanceToPlayer <= 10f)
            {
                _attemptedHousingShardTeleport = true;
            }
            else
            {
                _attemptedHousingShardTeleport      = true;
                ResetSourceApproach();
                _pendingHousingShardTeleportName    = shardName;
                _pendingHousingShardDistanceToVendor = shardDistanceToVendor;
                if (HandlePendingHousingShardTeleport())
                    return;
                _pendingHousingShardTeleportName     = null;
                _pendingHousingShardDistanceToVendor = 0f;
            }
        }

        if (TryPrepareCurrentTerritoryAethernetShortcut(pos, liveNpc))
            return;

        StartVNavmesh(destination, usingLiveNpc, destinationMode);
    }

    private bool HandlePendingHousingShardTeleport()
    {
        if (_pendingHousingShardTeleportName == null)
            return false;

        if (!PrepareAethernetSource(true, out var sourceAetheryte) || sourceAetheryte == null)
            return true;

        if (Lifestream.AethernetTeleport(_pendingHousingShardTeleportName))
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Using housing shard '{_pendingHousingShardTeleportName}' ({_pendingHousingShardDistanceToVendor:F1}m from vendor, source='{sourceAetheryte.Name.TextValue}')");
            _sourceApproach.Finish();
            _pendingHousingShardTeleportName     = null;
            _pendingHousingShardDistanceToVendor = 0f;
            _lastHousingEntryActionTime          = DateTime.UtcNow;
            _stateStartTime                      = DateTime.UtcNow;
            _teleportWaitStartTime               = _stateStartTime;
            _lastTeleportWaitDiagnosticsLogTime  = DateTime.MinValue;
            return true;
        }

        LogHousingEntryStatus($"[VendorNavigator] Waiting to use residential aetheryte '{sourceAetheryte.Name.TextValue}' for housing shard '{_pendingHousingShardTeleportName}'");
        return true;
    }

    private bool HandlePendingLocalAethernetTeleport()
    {
        if (_pendingAethernetName == null)
            return false;

        if (_pendingAethernetParentTerritoryId != 0
         && Dalamud.ClientState.TerritoryType != _pendingAethernetParentTerritoryId)
            return true;

        if (!PrepareAethernetSource(false, out _))
            return true;
        if (Lifestream.AethernetTeleport(_pendingAethernetName))
        {
            _sourceApproach.Finish();
            _pendingAethernetName                = null;
            _pendingAethernetNeedsSourceApproach = false;
            _pendingAethernetParentTerritoryId   = 0;
            _stateStartTime                      = DateTime.UtcNow;
            _teleportWaitStartTime               = _stateStartTime;
            _lastTeleportWaitDiagnosticsLogTime  = DateTime.MinValue;
        }

        return true;
    }

    private bool TryPrepareCurrentTerritoryAethernetShortcut(Vector3 playerPosition, IGameObject? liveNpc)
    {
        if (_target == null || !Lifestream.Enabled)
            return false;

        if (TryGetHousingDistrict(_target.TerritoryId, out _))
            return false;

        if (liveNpc != null && ShouldUseLiveNpcDestination(liveNpc))
        {
            GatherBuddy.Log.Debug(
                $"[VendorNavigator] Skipping local aethernet shortcut for {_target.NpcName} because the live NPC is already available for direct ground navigation at {liveNpc.Position}");
            return false;
        }

        var directDistanceToVendor = GetHorizontalDistance(playerPosition, _target.Position);
        if (!TryFindNearestShardInTerritory(_target.TerritoryId, _target.Position, out var shardName, out var shardDistanceToVendor, out var shardDistanceToPlayer))
            return false;

        if (shardDistanceToPlayer <= LocalAethernetDestinationProximityThreshold)
            return false;

        // Without a live source, there is no trustworthy hitbox-relative shortcut estimate.
        if (!TryGetNearestAethernetSource(out var sourceAetheryte, out var sourceHorizontalDistance, out _) || sourceAetheryte == null)
            return false;
        var sourceRange = GetAethernetSourceRange(sourceAetheryte, sourceHorizontalDistance);
        if (!sourceRange.IsValid) return false;
        var sourceApproachDistance = sourceRange.Remaining;
        var alreadyAtSource = sourceRange.InRange;
        var estimatedInteractionCost = alreadyAtSource ? 0f : LocalAethernetEstimatedInteractionCost;
        var requiredSavings          = alreadyAtSource ? 0f : LocalAethernetMinimumSavings;
        var estimatedShortcutDistance = sourceApproachDistance + shardDistanceToVendor + estimatedInteractionCost;
        var estimatedSavings          = directDistanceToVendor - estimatedShortcutDistance;
        if (estimatedSavings < requiredSavings)
            return false;

        QueueLocalAethernetTeleport(shardName, _target.TerritoryId);
        GatherBuddy.Log.Debug($"[VendorNavigator] Using local aethernet to '{shardName}' for {_target.NpcName}: direct={directDistanceToVendor:F1}m, sourceApproach={sourceApproachDistance:F1}m, post-teleport={shardDistanceToVendor:F1}m, interactionCost={estimatedInteractionCost:F1}m, estimatedSavings={estimatedSavings:F1}m");
        return true;
    }

    private void QueueLocalAethernetTeleport(string aethernetName, uint parentTerritoryId)
    {
        ResetSourceApproach();
        _teleportAttempted                  = true;
        _pendingAethernetName               = aethernetName;
        _pendingAethernetNeedsSourceApproach = true;
        _pendingHousingEntry                = false;
        _pendingFirmamentEntry              = false;
        _pendingHousingShardTeleportName    = null;
        _pendingHousingShardDistanceToVendor = 0f;
        _pendingAethernetParentTerritoryId  = parentTerritoryId;
        _stateStartTime                     = DateTime.UtcNow - TimeSpan.FromSeconds(TeleportCooldown);
        _teleportWaitStartTime              = DateTime.UtcNow;
        _lastTeleportWaitDiagnosticsLogTime = DateTime.MinValue;
        _state                              = State.WaitingForTeleport;
    }

    private void QueueFirmamentEntry(uint parentTerritoryId)
    {
        _teleportAttempted                  = true;
        _pendingAethernetName               = null;
        _pendingAethernetNeedsSourceApproach = false;
        _pendingHousingEntry                = false;
        _pendingFirmamentEntry              = true;
        _pendingHousingShardTeleportName    = null;
        _pendingHousingShardDistanceToVendor = 0f;
        _pendingAethernetParentTerritoryId  = parentTerritoryId;
        _stateStartTime                     = DateTime.UtcNow - TimeSpan.FromSeconds(TeleportCooldown);
        _teleportWaitStartTime              = DateTime.UtcNow;
        _lastTeleportWaitDiagnosticsLogTime = DateTime.MinValue;
        _state                              = State.WaitingForTeleport;
    }
    private void LogLongTeleportWaitIfNeeded()
    {
        if (_teleportWaitStartTime == DateTime.MinValue)
            return;

        var waitSeconds = (DateTime.UtcNow - _teleportWaitStartTime).TotalSeconds;
        if (waitSeconds < TeleportWaitDiagnosticsDelay)
            return;
        if ((DateTime.UtcNow - _lastTeleportWaitDiagnosticsLogTime).TotalSeconds < TeleportWaitDiagnosticsCooldown)
            return;

        _lastTeleportWaitDiagnosticsLogTime = DateTime.UtcNow;
        var lifestreamBusy = Lifestream.Enabled && Lifestream.IsBusy();
        var betweenAreas = Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51];
        var screenReady = GenericHelpers.IsScreenReady();
        GatherBuddy.Log.Debug(
            $"[VendorNavigator] Teleport wait still in progress for {_target?.NpcName ?? "vendor"} after {waitSeconds:F1}s: currentTerritory={Dalamud.ClientState.TerritoryType}, targetTerritory={_target?.TerritoryId ?? 0}, pendingAethernet={_pendingAethernetName ?? "none"}, needsSourceApproach={_pendingAethernetNeedsSourceApproach}, pendingHousingEntry={_pendingHousingEntry}, pendingFirmamentEntry={_pendingFirmamentEntry}, pendingHousingShard={_pendingHousingShardTeleportName ?? "none"}, awaitingForcedHousingExit={_awaitingForcedHousingOriginExit}, betweenAreas={betweenAreas}, screenReady={screenReady}, lifestreamBusy={lifestreamBusy}");
    }

    private void StartVNavmesh(Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Cannot start navigation without a local player");
            return;
        }

        StopPathing();

        _state                    = State.Navigating;
        _navigationDestination    = destination;
        _hasNavigationDestination = true;
        _usingLiveNpcDestination  = usingLiveNpc;
        _navigationDestinationTolerance = 0f;
        _lastRepathTime           = DateTime.UtcNow;
        _waitingForMount          = false;
        _landingWaypointCount     = 0;

        var forceGroundApproach = IsGroundApproachStabilizing();
        var liveNpc = FindLiveNpcObject();
        var allowAirborneLiveNpcInteractionRange = usingLiveNpc
            && liveNpc != null
            && destinationMode == NavigationDestinationMode.TargetInteractionRange
            && (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving]);
        var allowFlightLiveNpcApproach = usingLiveNpc
            && liveNpc != null
            && (destinationMode == NavigationDestinationMode.FinalApproachPoint || allowAirborneLiveNpcInteractionRange);
        var useGroundLiveNpcApproach = usingLiveNpc && liveNpc != null && !allowFlightLiveNpcApproach;
        var canMount = !forceGroundApproach
            && !useGroundLiveNpcApproach
            && CanMountForDestination(destination);
        var shouldFly = !forceGroundApproach
            && !useGroundLiveNpcApproach
            && ShouldFly(destination);
        _pathUsesFlight = shouldFly && (canMount || Dalamud.Conditions[ConditionFlag.Mounted]);
        _pathUsesFlight |= Dalamud.Conditions[ConditionFlag.Diving];
        _pathUsesCombinedApproach = false;
        _mountingUp = _pathUsesFlight && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.Diving];

        if (!Dalamud.Conditions[ConditionFlag.Mounted] && canMount)
        {
            if (CanRetryMountAttempt())
                TryMountUp();

            if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
            {
                _waitingForMount = true;
                return;
            }
        }

        var destinationTolerance = GetPathfindTolerance(liveNpc, destinationMode);
        var landingDistance = GetVendorLandingDistance(liveNpc, destination, destinationMode);
        var combinedPathTarget = destination;
        if (_pathUsesFlight
         && landingDistance > 0f
         && !Dalamud.Conditions[ConditionFlag.Diving]
         && destinationMode == NavigationDestinationMode.SafeStagingPoint)
        {
            _pathUsesCombinedApproach   = true;
            _pathCancellationTokenSource = new CancellationTokenSource();
            _pathTask = FindCombinedVendorPath(player.Position, combinedPathTarget, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _pathCancellationTokenSource.Token);
            return;
        }
        if (!_pathUsesFlight && useGroundLiveNpcApproach && destinationTolerance > 0f)
        {
            _navigationDestinationTolerance = destinationTolerance;
            _pathCancellationTokenSource = new CancellationTokenSource();
            _pathTask = VNavmesh.Nav.PathfindCancelable(player.Position, destination, false, _pathCancellationTokenSource.Token);
            return;
        }

        _pathCancellationTokenSource = new CancellationTokenSource();
        _pathTask = VNavmesh.Nav.PathfindCancelable(player.Position, destination, _pathUsesFlight, _pathCancellationTokenSource.Token);
    }

    private void UpdateNavigating()
    {
        var playerPos = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero) return;

        if (!IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target!.TerritoryId))
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Unexpected territory change during navigation");
            _state             = State.Teleporting;
            _teleportAttempted = false;
            return;
        }

        var liveNpc = FindLiveNpcObject();
        if (liveNpc != null)
            TryTargetLiveNpc(liveNpc);
        var destination = GetDesiredNavigationDestination(liveNpc, out var usingLiveNpc, out var destinationMode, out var navigationReason);
        var dist = GetTargetDistance(playerPos, liveNpc);
        if (usingLiveNpc
         && destinationMode == NavigationDestinationMode.FinalApproachPoint
         && Dalamud.Conditions[ConditionFlag.Mounted]
         && !Dalamud.Conditions[ConditionFlag.InFlight]
         && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            if (CanRetryLandingAttempt())
                TryLand();
            return;
        }

        if (HasReachedNavigationDestination(playerPos, liveNpc, destination, destinationMode))
        {
            if (destinationMode == NavigationDestinationMode.SafeStagingPoint)
            {
                StopPathing();
                if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving] || Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    if (CanRetryLandingAttempt())
                        TryLand();
                }
                else
                {
                    if (usingLiveNpc && liveNpc != null)
                    {
                        _forceInteractionRangeApproachUntil = DateTime.UtcNow.AddSeconds(VendorInteractionRangeFallbackDurationSeconds);
                        GatherBuddy.Log.Debug($"[VendorNavigator] Reached live NPC staging point near {GetDisplayName(liveNpc)}, falling back to direct interaction-range navigation");
                        StartVNavmesh(GetLiveNpcFallbackDestination(liveNpc), true, NavigationDestinationMode.TargetInteractionRange);
                        return;
                    }
                }
                ClearGroundApproachProgress();
                return;
            }
            if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
            {
                if (VNavmesh.Path.IsRunning() || _pathTask != null)
                    return;
                StopPathing();
                if (CanRetryLandingAttempt())
                    TryLand();
                return;
            }

            StopPathing();
            GatherBuddy.Log.Information($"[VendorNavigator] Arrived at {GetDisplayName(liveNpc)} ({dist:F1}m)");
            _state = State.ReadyToPurchase;
            return;
        }

        if (ShouldRepath(destination, usingLiveNpc, destinationMode))
        {
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
            return;
        }
        if (HandleMovementMode(playerPos, destination, usingLiveNpc, destinationMode))
            return;
        if (TryRecoverStalledGroundApproach(playerPos, liveNpc, destination, usingLiveNpc, destinationMode, navigationReason))
            return;

        HandlePathfinding();
        if (_state != State.Navigating || _waitingForMount)
            return;

        if (!VNavmesh.Path.IsRunning() && _pathTask == null)
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
    }

    private IGameObject? FindLiveNpcObject()
    {
        if (_target == null || !IsEquivalentTerritory(Dalamud.ClientState.TerritoryType, _target.TerritoryId))
            return null;

        IGameObject? bestObject = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc || obj.BaseId != _target.NpcId || !obj.IsTargetable)
                continue;

            var distance = Vector3.DistanceSquared(obj.Position, _target.Position);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestObject   = obj;
        }

        return bestObject;
    }

    private unsafe void TryTargetLiveNpc(IGameObject liveNpc)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;

        var target = (GameObjectStruct*)liveNpc.Address;
        if (targetSystem->Target == target)
            return;

        targetSystem->Target = target;
    }

    private Vector3 GetDesiredNavigationDestination(IGameObject? liveNpc, out bool usingLiveNpc, out NavigationDestinationMode destinationMode, out string navigationReason)
    {
        usingLiveNpc   = ShouldUseLiveNpcDestination(liveNpc);
        destinationMode = NavigationDestinationMode.TargetInteractionRange;
        Vector3 destination;
        var isAirborne = Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving];
        var hasStagingDestination = false;
        Vector3 stagingDestination = default;
        string stagingNavigationReason = string.Empty;
        var shouldUseCloseRangeLiveNpcApproach = ShouldUseCloseRangeLiveNpcApproach(liveNpc, usingLiveNpc);
        var useAirborneDirectLiveNpcNavigation = false;
        Vector3 airborneDirectLiveNpcDestination = default;
        if (!usingLiveNpc && liveNpc != null && isAirborne)
            hasStagingDestination = TryGetVendorStagingDestination(liveNpc, out stagingDestination, out stagingNavigationReason);
        if (isAirborne && hasStagingDestination && shouldUseCloseRangeLiveNpcApproach && liveNpc != null)
        {
            airborneDirectLiveNpcDestination = ResolveLiveNpcFallbackDestination(liveNpc, out var usesProjectedDestination);
            useAirborneDirectLiveNpcNavigation = !usesProjectedDestination;
        }

        if (useAirborneDirectLiveNpcNavigation && liveNpc != null)
        {
            usingLiveNpc = true;
            destinationMode = NavigationDestinationMode.TargetInteractionRange;
            navigationReason = "live NPC aerial interaction-range navigation";
            destination = airborneDirectLiveNpcDestination;
        }
        else if ((!isAirborne || !hasStagingDestination) && shouldUseCloseRangeLiveNpcApproach && liveNpc != null)
        {
            usingLiveNpc = true;
            if (TryGetVendorFinalApproachDestination(liveNpc, out destination, out navigationReason))
            {
                destinationMode = NavigationDestinationMode.FinalApproachPoint;
            }
            else
            {
                destinationMode = NavigationDestinationMode.FinalApproachPoint;
                navigationReason = "live NPC final approach fallback";
                destination = GetLiveNpcFallbackDestination(liveNpc);
            }
        }
        else if (usingLiveNpc && liveNpc != null)
        {
            navigationReason = "live NPC direct ground interaction-range navigation";
            destination = GetLiveNpcFallbackDestination(liveNpc);
        }
        else if (!usingLiveNpc && (hasStagingDestination || TryGetVendorStagingDestination(liveNpc, out stagingDestination, out stagingNavigationReason)))
        {
            destination = stagingDestination;
            navigationReason = stagingNavigationReason;
            destinationMode = NavigationDestinationMode.SafeStagingPoint;
        }
        else
        {
            navigationReason = usingLiveNpc
                ? liveNpc != null
                    ? "live NPC position fallback"
                    : "cached vendor location"
                : liveNpc != null
                    ? "live NPC position fallback"
                    : "cached vendor location";
            destination = usingLiveNpc && liveNpc != null
                ? GetLiveNpcFallbackDestination(liveNpc)
                : liveNpc != null
                    ? GetLiveNpcFallbackDestination(liveNpc)
                    : SnapToMesh(_target!.Position);
        }

        if (_usingLiveNpcDestination != usingLiveNpc)
        {
            if (_navigationDiagnostics.TryLog((liveNpc?.GameObjectId ?? 0, "DestinationMode"), usingLiveNpc ? navigationReason : "Unavailable", out var suppressed))
                GatherBuddy.Log.Debug((usingLiveNpc
                    ? $"[VendorNavigator] Switching to direct live NPC navigation for {_target!.NpcName} using {navigationReason} at {destination}"
                    : liveNpc != null
                        ? $"[VendorNavigator] Using {navigationReason} for {_target!.NpcName} while the live NPC destination is unavailable (npc={liveNpc.Position}, rotation={liveNpc.Rotation:F2})"
                        : $"[VendorNavigator] Live NPC object unavailable for {_target!.NpcName}, falling back to cached location") + DiagnosticThrottle.Suffix(suppressed));
        }

        return destination;
    }

    private bool ShouldUseLiveNpcDestination(IGameObject? liveNpc)
    {
        if (liveNpc == null)
            return false;

        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPosition == Vector3.Zero)
            return false;

        if (_usingLiveNpcDestination
         && !_pathUsesFlight
         && !_pathUsesCombinedApproach
         && !_mountingUp
         && !_waitingForMount
         && !Dalamud.Conditions[ConditionFlag.InFlight]
         && !Dalamud.Conditions[ConditionFlag.Diving]
         && GetHorizontalDistance(playerPosition, liveNpc.Position) <= VendorDirectLiveNpcApproachDistance)
            return true;

        return !IsFlightVendorApproachActive(liveNpc.Position);
    }

    private bool IsGroundApproachStabilizing()
        => DateTime.UtcNow < _forceGroundApproachUntil;

    private bool IsFlightVendorApproachActive(Vector3 destination)
    {
        if (_pathUsesFlight
         || _pathUsesCombinedApproach
         || _mountingUp
         || _waitingForMount
         || Dalamud.Conditions[ConditionFlag.InFlight]
         || Dalamud.Conditions[ConditionFlag.Diving])
            return true;

        if (IsGroundApproachStabilizing())
            return false;

        return ShouldFly(destination)
            && (CanMountForDestination(destination) || Dalamud.Conditions[ConditionFlag.Mounted]);
    }

    private bool ShouldRepath(Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        if (!_hasNavigationDestination)
            return true;

        if (_usingLiveNpcDestination != usingLiveNpc)
            return true;
        if (!_waitingForMount && _pathUsesFlight != ShouldUseFlightPath(destination, usingLiveNpc, destinationMode))
            return true;

        return Vector3.DistanceSquared(_navigationDestination, destination) >= RepathDistanceThreshold * RepathDistanceThreshold
            && (DateTime.UtcNow - _lastRepathTime).TotalSeconds >= RepathCooldown;
    }

    private bool IsWithinArrivalDistance(Vector3 playerPosition, IGameObject? liveNpc)
        => GetTargetDistance(playerPosition, liveNpc) <= (liveNpc != null ? LiveNpcInteractionDistance : CachedInteractionDistance);

    private bool IsWithinLiveNpcInteractionWindow(Vector3 playerPosition, IGameObject liveNpc)
        => GetHorizontalDistance(playerPosition, liveNpc.Position) <= LiveNpcInteractionDistance
        && GetVerticalDistance(playerPosition, liveNpc.Position) <= VendorInteractionMaxVerticalDistance;

    private bool HasReachedNavigationDestination(Vector3 playerPosition, IGameObject? liveNpc, Vector3 destination, NavigationDestinationMode destinationMode)
    {
        return destinationMode switch
        {
            NavigationDestinationMode.SafeStagingPoint =>
                liveNpc != null && _navigationDestinationTolerance > 0f
                    ? GetHorizontalDistance(playerPosition, liveNpc.Position) <= _navigationDestinationTolerance + VendorStagingPointArrivalDistance
                    : GetHorizontalDistance(playerPosition, destination) <= VendorStagingPointArrivalDistance,
            NavigationDestinationMode.FinalApproachPoint when liveNpc != null =>
                IsWithinLiveNpcInteractionWindow(playerPosition, liveNpc)
                && (_navigationDestinationTolerance > 0f || GetHorizontalDistance(playerPosition, destination) <= VendorApproachCompletionDistance),
            NavigationDestinationMode.TargetInteractionRange when liveNpc != null =>
                IsWithinLiveNpcInteractionWindow(playerPosition, liveNpc),
            _ => IsWithinArrivalDistance(playerPosition, liveNpc),
        };
    }

    private float GetTargetDistance(Vector3 playerPosition, IGameObject? liveNpc)
        => Vector3.Distance(playerPosition, liveNpc?.Position ?? _target!.Position);

    private string GetDisplayName(IGameObject? liveNpc)
        => liveNpc?.Name.TextValue ?? _target?.NpcName ?? "vendor";
    private bool HandleMovementMode(Vector3 playerPosition, Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        if (_waitingForMount)
        {
            if (Dalamud.Conditions[ConditionFlag.Mounted] || !CanMountForDestination(destination))
            {
                StartVNavmesh(destination, usingLiveNpc, destinationMode);
                return true;
            }
            if (CanRetryMountAttempt())
                TryMountUp();
            return true;
        }

        if (_pathUsesFlight && _mountingUp && Dalamud.Conditions[ConditionFlag.Mounted])
        {
            _mountingUp = false;

            if (VNavmesh.Path.IsRunning())
            {
                var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);
                var path = VNavmesh.Path.ListWaypoints()
                    .SkipWhile(p => Vector2.DistanceSquared(playerXZ, new Vector2(p.X, p.Z)) < 16f)
                    .ToList();

                if (path.Count == 0)
                {
                    StartVNavmesh(destination, usingLiveNpc, destinationMode);
                    return true;
                }

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, true);
                return true;
            }
        }

        if (_pathUsesFlight && !_mountingUp && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.InFlight]
         && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            GatherBuddy.Log.Debug("[VendorNavigator] Lost mounted state during flight navigation, recalculating path");
            StartVNavmesh(destination, usingLiveNpc, destinationMode);
            return true;
        }

        return false;
    }

    private void HandlePathfinding()
    {
        if (_pathUsesCombinedApproach && _landingWaypointCount > 0 && !Dalamud.Conditions[ConditionFlag.Diving])
        {
            var remainingPath = VNavmesh.Path.ListWaypoints().ToList();
            if (remainingPath.Count < _landingWaypointCount)
            {
                VNavmesh.Path.Stop();
                if (remainingPath.Count > 0)
                    VNavmesh.Path.MoveTo(remainingPath, false);

                _pathUsesFlight           = false;
                _pathUsesCombinedApproach = false;
                _landingWaypointCount     = 0;
                _forceGroundApproachUntil = DateTime.UtcNow.AddSeconds(VendorGroundApproachStabilizationSeconds);
                if (CanRetryLandingAttempt())
                    TryLand();
                return;
            }
        }
        if (_pathTask == null || !_pathTask.IsCompleted)
            return;

        List<Vector3> path;
        try
        {
            path = _pathTask.Result;
        }
        catch (Exception) when (_pathCancellationTokenSource != null && _pathCancellationTokenSource.IsCancellationRequested)
        {
            ClearPathTask();
            return;
        }
        catch (Exception ex)
        {
            ClearPathTask();
            GatherBuddy.Log.Error($"[VendorNavigator] Pathfinding task failed: {ex.Message}");
            StopPathing();
            _state = State.Failed;
            return;
        }

        ClearPathTask();
        if (path.Count == 0)
        {
            if (_pathUsesCombinedApproach && _pathUsesFlight)
            {
                var player = Dalamud.Objects.LocalPlayer;
                if (player != null)
                {
                    _pathUsesCombinedApproach   = false;
                    _landingWaypointCount       = 0;
                    _pathCancellationTokenSource = new CancellationTokenSource();
                    _pathTask = VNavmesh.Nav.PathfindCancelable(player.Position, _navigationDestination, true, _pathCancellationTokenSource.Token);
                    GatherBuddy.Log.Debug($"[VendorNavigator] Combined pathfinding failed for {_navigationDestination}, falling back to direct flight path");
                    return;
                }
            }
            GatherBuddy.Log.Error($"[VendorNavigator] VNavmesh failed to find a path to {_navigationDestination}");
            StopPathing();
            _state = State.Failed;
            return;
        }

        if (_pathUsesCombinedApproach && path.Count > 1)
        {
            var landingWaypoint = path[^1];
            path.RemoveAt(path.Count - 1);
            var landingIndex = path.FindLastIndex(point => point == landingWaypoint);
            _landingWaypointCount = landingIndex >= 0 ? System.Math.Max(1, path.Count - landingIndex) : 1;
        }
        else
        {
            _landingWaypointCount = 0;
        }

        if (VNavmesh.Path.IsRunning())
            RemovePassedWaypoints(path);

        if (path.Count == 0)
        {
            GatherBuddy.Log.Warning("[VendorNavigator] Path resolved with no remaining waypoints after pruning, recalculating");
            return;
        }

        VNavmesh.Path.Stop();
        VNavmesh.Path.MoveTo(path, _pathUsesFlight && !_mountingUp);
    }

    private void StopPathing()
    {
        CancelSourcePath();
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        _landingWaypointCount     = 0;
        _pathUsesCombinedApproach = false;
        _pathUsesFlight           = false;
        _mountingUp               = false;
        _waitingForMount          = false;
        _navigationDestinationTolerance = 0f;
        ClearGroundApproachProgress();
        if (_pathCancellationTokenSource != null)
            _pathCancellationTokenSource.Cancel();
        ClearPathTask();
    }

    private void ClearPathTask()
    {
        _pathTask = null;
        _pathCancellationTokenSource?.Dispose();
        _pathCancellationTokenSource = null;
    }

    private unsafe bool TryAdvanceHousingEntry()
    {
        if (_target == null || !TryGetHousingDistrict(_target.TerritoryId, out _))
            return false;

        if (!GenericHelpers.IsScreenReady())
            return false;

        if (TryConfirmHousingTravel())
            return true;

        if (TryConfirmHousingWardSelection())
            return true;

        if (TrySelectHousingAetheryteMenuEntry(GoToWardMenuEntries))
            return true;
        if (TrySelectHousingAetheryteMenuEntry(ResidentialDistrictMenuEntries))
            return true;

        if (TryInteractWithHousingEntryAetheryte())
            return true;
        if (TryMoveToHousingEntryAetheryte())
            return true;

        LogHousingEntryStatus("[VendorNavigator] Waiting for city aetheryte/menu state to enter the housing ward");

        return false;
    }

    private unsafe bool TryConfirmHousingTravel()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon)
         || !addon->AtkUnitBase.IsVisible
         || addon->YesButton == null
         || !MatchesAnyText(ReadSelectYesnoPrompt(addon), HousingTravelConfirmationTexts))
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        new AddonMaster.SelectYesno((nint)addon).Yes();
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private unsafe bool TryConfirmHousingWardSelection()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("HousingSelectBlock", out var addon) || !addon->IsVisible)
            return false;

        var button = addon->GetComponentButtonById(34);
        if (button == null || !button->IsEnabled || button->AtkComponentBase.OwnerNode == null || !button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        ClickButton(addon, button);
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private unsafe bool TrySelectHousingAetheryteMenuEntry(IReadOnlyList<string> entries)
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !addon->AtkUnitBase.IsVisible)
            return false;

        for (var index = 0; index < addon->PopupMenu.PopupMenu.EntryCount; index++)
        {
            var label = MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[index].Value).TextValue.Trim();
            if (!MatchesAnyText(label, entries))
                continue;

            if (!CanIssueHousingEntryAction())
                return false;

            new AddonMaster.SelectString((nint)addon).Entries[index].Select();
            _lastHousingEntryActionTime = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    private unsafe bool TryInteractWithHousingEntryAetheryte()
    {
        if (!TryGetNearestHousingEntryAetheryte(out var aetheryte, out var horizontalDistance, out var distance)
         || aetheryte == null
         || horizontalDistance >= 11f
         || distance >= 15f)
            return false;

        if (!CanIssueHousingEntryAction())
            return false;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return false;
        if (VNavmesh.Path.IsRunning())
            VNavmesh.Path.Stop();

        targetSystem->Target = (GameObjectStruct*)aetheryte.Address;
        targetSystem->OpenObjectInteraction((GameObjectStruct*)aetheryte.Address);
        _lastHousingEntryActionTime = DateTime.UtcNow;
        return true;
    }

    private bool TryMoveToHousingEntryAetheryte()
    {
        if (!TryGetNearestHousingEntryAetheryte(out var aetheryte, out var horizontalDistance, out var distance) || aetheryte == null)
        {
            LogHousingEntryStatus("[VendorNavigator] Waiting for the city aetheryte object to become available for housing entry");
            return false;
        }

        if (horizontalDistance < 11f && distance < 15f)
            return false;

        if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
            return true;

        if (!CanIssueHousingEntryAction())
            return false;

        var started = VNavmesh.SimpleMove.PathfindAndMoveTo(aetheryte.Position, false);
        if (started)
        {
            _lastHousingEntryActionTime = DateTime.UtcNow;
            return true;
        }

        LogHousingEntryStatus($"[VendorNavigator] Failed to start movement toward city aetheryte '{aetheryte.Name.TextValue}' ({distance:F1}m)");
        return false;
    }

    private bool TryGetNearestHousingEntryAetheryte(out IGameObject? aetheryte, out float horizontalDistance, out float distance)
    {
        aetheryte          = null;
        horizontalDistance = float.MaxValue;
        distance           = float.MaxValue;

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        var nearest = Dalamud.Objects
            .Where(obj => obj.ObjectKind == ObjectKind.Aetheryte && obj.IsTargetable)
            .Select(obj => (Object: obj, HorizontalDistance: Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(obj.Position.X, obj.Position.Z)), Distance: Vector3.Distance(player.Position, obj.Position)))
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        if (nearest.Object == null)
            return false;

        aetheryte          = nearest.Object;
        horizontalDistance = nearest.HorizontalDistance;
        distance           = nearest.Distance;
        return true;
    }

    private bool TryGetNearestAethernetSource(out IGameObject? aetheryte, out float horizontalDistance, out float distance)
    {
        aetheryte          = null;
        horizontalDistance = float.MaxValue;
        distance           = float.MaxValue;

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        var nearest = Dalamud.Objects
            .Where(IsAethernetSource)
            .Select(obj => (Object: obj, HorizontalDistance: Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(obj.Position.X, obj.Position.Z)), Distance: Vector3.Distance(player.Position, obj.Position)))
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        if (nearest.Object == null)
            return false;

        aetheryte          = nearest.Object;
        horizontalDistance = nearest.HorizontalDistance;
        distance           = nearest.Distance;
        return true;
    }

    private static AethernetSourceRange GetAethernetSourceRange(IGameObject source, float horizontalDistance)
        => new(source.HitboxRadius, horizontalDistance,
            MathF.Abs((Dalamud.Objects.LocalPlayer?.Position.Y ?? float.NaN) - source.Position.Y));

    private void ResetSourceApproach()
    {
        CancelSourcePath();
        _sourceApproach.Reset();
        _lastSourceActionTime = _lastSourceLogTime = DateTime.MinValue;
        _lastSourceLog = null;
    }

    private bool PrepareAethernetSource(bool housing, out IGameObject? source)
    {
        source = null;
        if (!Lifestream.Enabled)
        {
            FailAethernetSource("Lifestream unavailable during aethernet source approach");
            return false;
        }
        // Lifestream owns movement while busy. Never issue competing path requests.
        if (Lifestream.IsBusy()) return false;
        var now = DateTime.UtcNow;
        TryGetNearestAethernetSource(out source, out var horizontal, out _);
        var range = source == null ? default : GetAethernetSourceRange(source, horizontal);
        var sourceState = "no live source";
        var recognized = source != null && IsLifestreamAethernetSourceReady(source, out sourceState);
        var sourceId = source?.GameObjectId ?? 0;
        if (_sourcePathObjectId != 0 && _sourcePathObjectId != sourceId) StopPathing();
        var action = _sourceApproach.Evaluate(now, Dalamud.ClientState.TerritoryType, sourceId, range, recognized);
        var logKey = (sourceId, action);
        if (_lastSourceLog != logKey || now - _lastSourceLogTime >= TimeSpan.FromSeconds(2))
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Aethernet source {action}: '{source?.Name.TextValue ?? "missing"}'; sourceId={sourceId}; territory={Dalamud.ClientState.TerritoryType}; radius={range.Radius:F2}m; margin={AethernetSourceRange.InteractionMargin:F1}m; allowedHorizontal={range.Limit:F2}m; horizontal={range.Horizontal:F2}m; vertical={range.Vertical:F2}m; verticalLimit={range.Limit:F2}m; recognized={recognized}; {sourceState}");
            _lastSourceLog = logKey;
            _lastSourceLogTime = now;
        }
        if (action == AethernetSourceAction.Failed)
        {
            FailAethernetSource("Invalid source geometry or timed out approaching/recognizing aethernet source (60s approach / 10s after arrival)");
            return false;
        }
        if (action == AethernetSourceAction.Stop)
        {
            StopPathing();
            _sourceApproach.MarkStopped();
            return false;
        }
        if (source == null) return false;
        if (action == AethernetSourceAction.Ready)
        {
            // Do not allow a pending corrective path calculation to launch after handoff.
            StopPathing();
        }
        if (_sourcePathTask is { } task)
        {
            if (!task.IsCompleted) return false;
            if (!CanIssueTeleportRequest()) return false;
            _sourcePathTask = null;
            _sourcePathCancellation?.Dispose();
            _sourcePathCancellation = null;
            if (task.IsCompletedSuccessfully && task.Result.Count > 0)
                VNavmesh.Path.MoveTo(task.Result, false);
            else
                GatherBuddy.Log.Warning($"[VendorNavigator] Source approach path failed or empty: {task.Exception?.GetBaseException().Message ?? "no route"}");
            return false;
        }
        if (action == AethernetSourceAction.Wait) return false;
        if (now - _lastSourceActionTime < TimeSpan.FromSeconds(1)
            || (housing ? !CanIssueHousingEntryAction() : !CanIssueTeleportRequest())) return false;
        if (action == AethernetSourceAction.Ready)
        {
            _lastSourceActionTime = now;
            return true;
        }
        if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress()) return false;
        _lastSourceActionTime = now;
        // Own the cancellable calculation: SimpleMove can otherwise launch movement after cancellation/handoff.
        _sourcePathCancellation = new CancellationTokenSource();
        _sourcePathObjectId = sourceId;
        _sourcePathTask = VNavmesh.Nav.PathfindCancelable(Dalamud.Objects.LocalPlayer!.Position,
            source.Position, false, _sourcePathCancellation.Token);
        _ = _sourcePathTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        if (housing) _lastHousingEntryActionTime = now;
        if (action == AethernetSourceAction.CorrectiveApproach) _sourceApproach.MarkCorrectiveApproachStarted();
        return false;
    }

    private void CancelSourcePath()
    {
        _sourcePathCancellation?.Cancel();
        _sourcePathCancellation?.Dispose();
        _sourcePathCancellation = null;
        _sourcePathTask = null;
        _sourcePathObjectId = 0;
    }

    private void FailAethernetSource(string reason)
    {
        StopPathing();
        _sourceApproach.Finish();
        _state = State.Failed;
        GatherBuddy.Log.Warning($"[VendorNavigator] {reason}; destination={_pendingAethernetName ?? _pendingHousingShardTeleportName ?? "unknown"}");
    }

    private static bool IsAethernetSource(IGameObject obj)
        => obj.IsTargetable
        && (obj.ObjectKind == ObjectKind.Aetheryte
         || IsKnownAethernetShardDataId(obj.BaseId));

    private static bool IsKnownAethernetShardDataId(uint dataId)
        => GetKnownAethernetShardDataIds().Contains(dataId);

    private static bool IsLifestreamAethernetSourceReady(IGameObject source, out string state)
    {
        var activeAetheryteId            = Lifestream.ActiveAetheryteId;
        var activeCustomAetheryteId      = Lifestream.ActiveCustomAetheryteId;
        var activeResidentialAetheryteId = Lifestream.ActiveResidentialAetheryteId;

        var sourceRecognized = source.ObjectKind == ObjectKind.EventObj
            ? activeResidentialAetheryteId != 0 || activeCustomAetheryteId != 0
            : activeAetheryteId != 0 || activeResidentialAetheryteId != 0 || activeCustomAetheryteId != 0;

        // These IPCs expose active IDs, not the selected live object's identity mapping.
        // Keep existing recognition semantics; proximity alone never authorizes a teleport.

        state = $"active={activeAetheryteId}, residential={activeResidentialAetheryteId}, custom={activeCustomAetheryteId}";
        return sourceRecognized;
    }

    private static HashSet<uint> GetKnownAethernetShardDataIds()
    {
        if (_knownAethernetShardDataIds != null)
            return _knownAethernetShardDataIds;

        var shardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataIds    = new HashSet<uint>(KnownAethernetShardSeedDataIds);
        var sheet      = Dalamud.GameData.GetExcelSheet<EObjName>();

        CollectAethernetShardSeedNames(sheet, shardNames);
        CollectMatchingAethernetShardDataIds(sheet, shardNames, dataIds);

        try
        {
            var englishSheet = Dalamud.GameData.GetExcelSheet<EObjName>(ClientLanguage.English);
            CollectAethernetShardSeedNames(englishSheet, shardNames);
            CollectMatchingAethernetShardDataIds(englishSheet, shardNames, dataIds);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[VendorNavigator] Failed to load English EObjName sheet while building residential shard ids: {ex.Message}");
        }

        _knownAethernetShardDataIds = dataIds;
        return _knownAethernetShardDataIds;
    }

    private static void CollectAethernetShardSeedNames(ExcelSheet<EObjName>? sheet, HashSet<string> shardNames)
    {
        if (sheet == null)
            return;

        foreach (var rowId in KnownAethernetShardSeedDataIds)
        {
            if (!sheet.TryGetRow(rowId, out var row))
                continue;

            var name = row.Singular.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(name))
                shardNames.Add(name);
        }
    }

    private static void CollectMatchingAethernetShardDataIds(ExcelSheet<EObjName>? sheet, HashSet<string> shardNames, HashSet<uint> dataIds)
    {
        if (sheet == null || shardNames.Count == 0)
            return;

        foreach (var row in sheet)
        {
            var name = row.Singular.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(name) && shardNames.Contains(name))
                dataIds.Add(row.RowId);
        }
    }

    private bool CanIssueHousingEntryAction()
        => (DateTime.UtcNow - _lastHousingEntryActionTime).TotalSeconds >= 0.5;

    private void LogHousingEntryStatus(string message)
    {
        if ((DateTime.UtcNow - _lastHousingEntryStatusLogTime).TotalSeconds < 2)
            return;

        _lastHousingEntryStatusLogTime = DateTime.UtcNow;
        GatherBuddy.Log.Debug(message);
    }

    private static unsafe string ReadSelectYesnoPrompt(AddonSelectYesno* addon)
        => addon->PromptText == null
            ? string.Empty
            : addon->PromptText->NodeText.ToString();

    private static bool MatchesAnyText(string text, IReadOnlyList<string> candidates)
        => !string.IsNullOrWhiteSpace(text)
         && candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static unsafe void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        var buttonNode = button->AtkComponentBase.OwnerNode;
        if (addon == null || buttonNode == null)
            return;

        var eventPointer = buttonNode->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return;

        var atkEvent = (AtkEvent*)eventPointer;
        addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, eventPointer);
    }

    private bool ShouldUseFlightPath(Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode)
    {
        if (IsGroundApproachStabilizing())
            return false;
        var allowAirborneLiveNpcInteractionRange = usingLiveNpc
            && destinationMode == NavigationDestinationMode.TargetInteractionRange
            && (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving]);
        if (usingLiveNpc && destinationMode != NavigationDestinationMode.FinalApproachPoint && !allowAirborneLiveNpcInteractionRange)
            return false;
        var shouldFly = ShouldFly(destination);
        shouldFly &= CanMountForDestination(destination) || Dalamud.Conditions[ConditionFlag.Mounted];
        shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];
        return shouldFly;
    }

    private bool CanMountForDestination(Vector3 destination)
    {
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPosition == Vector3.Zero)
            return false;

        var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);
        var targetXZ = new Vector2(destination.X, destination.Z);
        return Vector2.Distance(playerXZ, targetXZ) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance && CanMount();
    }

    private unsafe bool TryMountUp()
    {
        const uint MountRouletteGeneralActionId = 9;
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        var mountId = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
        var success = false;
        if (IsMountUnlocked(mountId) && actionManager->GetActionStatus(ActionType.Mount, mountId) == 0)
        {
            success = actionManager->UseAction(ActionType.Mount, mountId);
        }
        else if (actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) == 0)
        {
            success = actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
        }

        if (success)
            _lastMountAttemptTime = DateTime.UtcNow;
        return success;
    }

    private unsafe bool TryLand()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null || actionManager->GetActionStatus(ActionType.GeneralAction, 23) != 0)
            return false;

        var success = actionManager->UseAction(ActionType.GeneralAction, 23);
        if (success)
            _lastLandingAttemptTime = DateTime.UtcNow;
        return success;
    }

    private bool CanRetryMountAttempt()
        => (DateTime.UtcNow - _lastMountAttemptTime).TotalSeconds >= MountRetryCooldown;

    private bool CanRetryLandingAttempt()
        => (DateTime.UtcNow - _lastLandingAttemptTime).TotalSeconds >= LandingRetryCooldown;

    private static unsafe bool CanMount()
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->GetActionStatus(ActionType.Mount, 0) == 0;
    }

    private static float GetVendorLandingDistance(IGameObject? liveNpc, Vector3 destination, NavigationDestinationMode destinationMode)
    {
        if (liveNpc == null || destinationMode != NavigationDestinationMode.SafeStagingPoint)
            return VendorLandingDistance;

        var liveNpcDistance = GetHorizontalDistance(destination, liveNpc.Position);
        return liveNpcDistance < VendorLandingCandidateDistances[0]
            ? GetCloseRangeCombinedLandingDistance(liveNpcDistance)
            : VendorLandingDistance;
    }

    private static unsafe bool IsMountUnlocked(uint mountId)
    {
        var playerState = PlayerState.Instance();
        return playerState != null && playerState->IsMountUnlocked(mountId);
    }

    private static unsafe bool ShouldFly(Vector3 destination)
    {
        if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
            return true;

        var player = Dalamud.Objects.LocalPlayer;
        if (GatherBuddy.Config.AutoGatherConfig.ForceWalking || player == null)
            return false;

        if (Diadem.IsInside)
            return Vector3.Distance(player.Position, destination) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;

        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territorySheet == null || !territorySheet.TryGetRow(Dalamud.ClientState.TerritoryType, out var territory))
            return false;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return false;

        var aetherCurrentComp = territory.AetherCurrentCompFlgSet.RowId;
        return aetherCurrentComp != 0
            && playerState->IsAetherCurrentZoneComplete(aetherCurrentComp)
            && Vector3.Distance(player.Position, destination) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
    }

    private static Vector3 SnapToMesh(Vector3 position)
        => VNavmesh.Query.Mesh.NearestPoint(position, 3, 10000).GetValueOrDefault(position);

    private Vector3 GetLiveNpcFallbackDestination(IGameObject liveNpc)
        => ResolveLiveNpcFallbackDestination(liveNpc, out _);

    private Vector3 ResolveLiveNpcFallbackDestination(IGameObject liveNpc, out bool usesProjectedDestination)
    {
        usesProjectedDestination = false;
        var pointOnFloor = VNavmesh.Query.Mesh.PointOnFloor;
        Vector3? snapped = pointOnFloor != null
            ? pointOnFloor(liveNpc.Position, true, VendorFallbackFloorSearchRadius)
            : null;
        var snapSource = "PointOnFloor";
        if (!snapped.HasValue)
        {
            snapped = VNavmesh.Query.Mesh.NearestPoint(liveNpc.Position, VendorApproachMeshSearchRadius, VendorApproachFloorSearchHeight);
            snapSource = "NearestPoint";
        }
        if (!snapped.HasValue)
        {
            if (_navigationDiagnostics.TryLog((liveNpc.GameObjectId, "MissingProjection"), "Missing", out var suppressed))
                GatherBuddy.Log.Debug($"[VendorNavigator] Failed to find a live NPC fallback projection for {_target?.NpcName ?? "vendor"} at {liveNpc.Position}, using the live NPC position instead{DiagnosticThrottle.Suffix(suppressed)}");
            return liveNpc.Position;
        }
        var snappedPosition = snapped.Value;
        var verticalDrift = MathF.Abs(snappedPosition.Y - liveNpc.Position.Y);
        var horizontalDrift = GetHorizontalDistance(snappedPosition, liveNpc.Position);
        if (verticalDrift <= VendorApproachMaxVerticalDrift && horizontalDrift <= VendorStagingMaxHorizontalDrift)
        {
            _navigationDiagnostics.Forget((liveNpc.GameObjectId, "MissingProjection"));
            _navigationDiagnostics.Forget((liveNpc.GameObjectId, "PointOnFloor"));
            _navigationDiagnostics.Forget((liveNpc.GameObjectId, "NearestPoint"));
            usesProjectedDestination = true;
            return snappedPosition;
        }

        if (_navigationDiagnostics.TryLog((liveNpc.GameObjectId, snapSource), "Rejected", out var rejectedRepeats))
            GatherBuddy.Log.Debug($"[VendorNavigator] Rejecting {snapSource} live NPC fallback {snappedPosition} for {_target?.NpcName ?? "vendor"} because it drifted {horizontalDrift:F1}m horizontally and {verticalDrift:F1}m vertically from {liveNpc.Position}, using the live NPC position instead{DiagnosticThrottle.Suffix(rejectedRepeats)}");
        return liveNpc.Position;
    }

    private bool ShouldUseCloseRangeLiveNpcApproach(IGameObject? liveNpc, bool usingLiveNpc)
    {
        if (usingLiveNpc || liveNpc == null)
            return false;

        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        return playerPosition != Vector3.Zero
            && IsFlightVendorApproachActive(liveNpc.Position)
            && GetHorizontalDistance(playerPosition, liveNpc.Position) <= VendorDirectLiveNpcApproachDistance;
    }

    private static float GetCloseRangeCombinedLandingDistance(float liveNpcDistance)
        => System.Math.Clamp(
            liveNpcDistance - VendorCloseCombinedLandingDistanceBuffer,
            VendorCloseCombinedLandingDistanceMinimum,
            VendorCloseCombinedLandingDistanceMaximum);

    private bool TryGetVendorStagingDestination(IGameObject? liveNpc, out Vector3 destination, out string navigationReason)
    {
        if (liveNpc == null)
        {
            destination      = default;
            navigationReason = "cached vendor location";
            return false;
        }

        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + 1.0f, VendorLandingCandidateDistances[0]);
        var preferPlayerSide = !ShouldFly(liveNpc.Position);
        var found = TryFindVendorApproachDestination(liveNpc, VendorLandingCandidateDistances, minimumDistance, VendorLandingDistance,
            VendorStagingMaxHorizontalDrift, preferPlayerSide, out destination, out var outerRingDiagnostics);
        if (found)
        {
            navigationReason = preferPlayerSide
                ? "ground vendor staging point"
                : "safe vendor staging point";
            LogApproachDiagnostics(liveNpc, "Staging", navigationReason, outerRingDiagnostics);
            return true;
        }
        var closeMinimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 1.5f);
        found = TryFindVendorApproachDestination(liveNpc, VendorCloseStagingCandidateDistances, closeMinimumDistance, VendorLandingCandidateDistances[0],
            VendorApproachMaxHorizontalDrift, false, out destination, out var closeRingDiagnostics);
        if (found)
        {
            navigationReason = "close vendor staging point";
            LogApproachDiagnostics(liveNpc, "Staging", navigationReason, outerRingDiagnostics, closeRingDiagnostics);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, "Staging", navigationReason, outerRingDiagnostics, closeRingDiagnostics);
        return false;
    }

    private bool TryGetVendorFinalApproachDestination(IGameObject liveNpc, out Vector3 destination, out string navigationReason)
    {
        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 0.75f);
        var maximumDistance = LiveNpcInteractionDistance - 0.25f;
        var found = TryFindVendorApproachDestination(liveNpc, GetVendorApproachCandidateDistances(liveNpc), minimumDistance, maximumDistance,
            VendorApproachMaxHorizontalDrift, true, out destination, out var diagnosticsSummary);
        if (found)
        {
            navigationReason = "live NPC final approach point";
            LogApproachDiagnostics(liveNpc, "FinalApproach", navigationReason, diagnosticsSummary);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, "FinalApproach", navigationReason, diagnosticsSummary);
        return false;
    }

    private bool TryGetVendorCloseApproachDestination(IGameObject liveNpc, out Vector3 destination, out string navigationReason)
    {
        var minimumDistance = MathF.Max(liveNpc.HitboxRadius + VendorApproachMinimumStandOff, 1.5f);
        var maximumDistance = VendorLandingCandidateDistances[0];
        var found = TryFindVendorApproachDestination(liveNpc, VendorCloseStagingCandidateDistances, minimumDistance, maximumDistance,
            VendorStagingMaxHorizontalDrift, true, out destination, out var diagnosticsSummary);
        if (found)
        {
            navigationReason = "close vendor approach point";
            LogApproachDiagnostics(liveNpc, "CloseApproach", navigationReason, diagnosticsSummary);
            return true;
        }

        navigationReason = "live NPC position fallback";
        LogApproachDiagnostics(liveNpc, "CloseApproach", navigationReason, diagnosticsSummary);
        return false;
    }

    private static float[] GetVendorApproachCandidateDistances(IGameObject liveNpc)
    {
        var interactionLimit = LiveNpcInteractionDistance - 0.25f;
        var hitboxRadius     = MathF.Max(liveNpc.HitboxRadius, 0.5f);
        var firstDistance    = MathF.Min(interactionLimit, hitboxRadius + VendorApproachMinimumStandOff);
        var distances        = new List<float>();

        for (var distance = firstDistance; distance < interactionLimit; distance += VendorApproachDistanceStep)
            distances.Add(distance);

        distances.Add(interactionLimit);
        return distances
            .Distinct()
            .ToArray();
    }

    private readonly record struct ProjectionFailure(string Kind, Vector3 Candidate, Vector3 Projected = default,
        Vector3 Npc = default, float Value = 0, float Limit = 0, string? Error = null)
    {
        public override string ToString() => Kind switch
        {
            "NoFloor" => $"no floor projection (candidate={Candidate})",
            "HorizontalDrift" => $"horizontalDrift={Value:F2}>{Limit:F2} (candidate={Candidate}, projected={Projected})",
            "VerticalDrift" => $"verticalDrift={Value:F2}>{Limit:F2} (candidate={Candidate}, projected={Projected}, npc={Npc})",
            "TooFar" => $"npcDistance={Value:F2}>{Limit:F2} (candidate={Candidate}, projected={Projected}, npc={Npc})",
            "TooClose" => $"npcDistance={Value:F2}<{Limit:F2} (candidate={Candidate}, projected={Projected}, npc={Npc})",
            "Exception" => $"projection exception for {Candidate}: {Error}",
            _ => string.Empty,
        };
    }

    private readonly record struct ApproachDiagnostics(bool Selected, Vector3 Candidate, float Distance, float ForwardDot,
        int Valid, int Rejected, ProjectionFailure? FirstFailure)
    {
        public override string ToString() => Selected
            ? $"selected={Candidate}, npcDistance={Distance:F1}, forwardDot={ForwardDot:F2}, valid={Valid}, rejected={Rejected}"
            : Rejected > 0 ? $"projectionFailures={Rejected} (first: {FirstFailure})" : "no valid approach candidates";
    }

    private void LogApproachDiagnostics(IGameObject liveNpc, string category, string navigationReason,
        ApproachDiagnostics first, ApproachDiagnostics? second = null)
    {
        if (_target == null || !_navigationDiagnostics.TryLog((liveNpc.GameObjectId, category), navigationReason, out var suppressed)) return;
        // Format coordinates/failure details only when admitted. Alternating phases have independent windows.
        var detail = second.HasValue ? $"outer ring: {first}; close ring: {second.Value}" : first.ToString();
        GatherBuddy.Log.Debug($"[VendorNavigator] {navigationReason} [{category}] for {_target.NpcName} after {detail} (npc={liveNpc.Position}, rotation={liveNpc.Rotation:F2}){DiagnosticThrottle.Suffix(suppressed)}");
    }

    private bool TryFindVendorApproachDestination(IGameObject liveNpc, IReadOnlyList<float> distances, float minimumDistance, float maximumDistance, float maximumDrift, bool preferPlayerSide, out Vector3 destination, out ApproachDiagnostics diagnosticsSummary)
    {
        var npcPosition = liveNpc.Position;
        var forward     = GetFacingDirection(liveNpc.Rotation);
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var bestScore   = float.NegativeInfinity;
        var bestForwardDot = float.NegativeInfinity;
        var bestDistance   = 0f;
        var bestCandidate  = Vector3.Zero;
        var validCandidates = 0;
        var projectionFailures = 0;
        ProjectionFailure? firstProjectionFailure = null;

        foreach (var distance in distances)
        {
            foreach (var angle in VendorApproachCandidateAngles)
            {
                var candidate = npcPosition + RotateHorizontal(forward * distance, angle);
                if (!TryProjectVendorApproachCandidate(candidate, npcPosition, minimumDistance, maximumDistance, maximumDrift, out var projected, out var projectionFailure))
                {
                    projectionFailures++;
                    firstProjectionFailure ??= projectionFailure;
                    continue;
                }

                validCandidates++;
                var npcDistance = GetHorizontalDistance(projected, npcPosition);
                var forwardDot  = GetForwardDot(projected, npcPosition, forward);
                var playerDot   = GetPlayerDot(projected, npcPosition, playerPosition);
                var score       = ScoreVendorApproachCandidate(npcDistance, forwardDot, playerDot, preferPlayerSide);
                if (_hasNavigationDestination && GetHorizontalDistance(projected, _navigationDestination) <= 1.0f)
                    score += 0.5f;

                if (!(score > bestScore))
                    continue;

                bestScore      = score;
                bestForwardDot = forwardDot;
                bestDistance   = npcDistance;
                bestCandidate  = projected;
            }
        }

        if (bestScore > float.NegativeInfinity)
        {
            destination = bestCandidate;
            diagnosticsSummary = new(true, bestCandidate, bestDistance, bestForwardDot, validCandidates, projectionFailures, firstProjectionFailure);
            return true;
        }

        destination = default;
        diagnosticsSummary = new(false, default, 0, 0, validCandidates, projectionFailures, firstProjectionFailure);
        return false;
    }

    private bool TryProjectVendorApproachCandidate(Vector3 candidate, Vector3 npcPosition, float minimumDistance, float maximumDistance, float maximumDrift, out Vector3 projected, out ProjectionFailure failureReason)
    {
        projected     = default;
        failureReason = default;

        try
        {
            var pointOnFloor = VNavmesh.Query.Mesh.PointOnFloor;
            Vector3? snapped = pointOnFloor != null
                ? pointOnFloor(candidate, true, VendorApproachFloorSearchHeight)
                : null;
            snapped ??= VNavmesh.Query.Mesh.NearestPoint(candidate, VendorApproachMeshSearchRadius, VendorApproachFloorSearchHeight);
            if (!snapped.HasValue)
            {
                failureReason = new("NoFloor", candidate);
                return false;
            }

            var horizontalDrift = GetHorizontalDistance(snapped.Value, candidate);
            if (horizontalDrift > maximumDrift)
            {
                failureReason = new("HorizontalDrift", candidate, snapped.Value, npcPosition, horizontalDrift, maximumDrift);
                return false;
            }

            var verticalDrift = MathF.Abs(snapped.Value.Y - npcPosition.Y);
            if (verticalDrift > VendorApproachMaxVerticalDrift)
            {
                failureReason = new("VerticalDrift", candidate, snapped.Value, npcPosition, verticalDrift, VendorApproachMaxVerticalDrift);
                return false;
            }

            var interactionDistance = GetHorizontalDistance(snapped.Value, npcPosition);
            if (interactionDistance > maximumDistance)
            {
                failureReason = new("TooFar", candidate, snapped.Value, npcPosition, interactionDistance, maximumDistance);
                return false;
            }
            if (interactionDistance < minimumDistance)
            {
                failureReason = new("TooClose", candidate, snapped.Value, npcPosition, interactionDistance, minimumDistance);
                return false;
            }

            projected = snapped.Value;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = new("Exception", candidate, Error: ex.Message);
            return false;
        }
    }

    private bool TryRecoverStalledGroundApproach(Vector3 playerPosition, IGameObject? liveNpc, Vector3 destination, bool usingLiveNpc, NavigationDestinationMode destinationMode, string navigationReason)
    {
        var isGroundApproachMode = destinationMode is NavigationDestinationMode.FinalApproachPoint or NavigationDestinationMode.TargetInteractionRange;
        if (!usingLiveNpc || !isGroundApproachMode || _pathUsesFlight || _waitingForMount || _pathTask != null || !VNavmesh.Path.IsRunning())
        {
            ClearGroundApproachProgress();
            return false;
        }

        var destinationDistance = GetHorizontalDistance(playerPosition, destination);
        var movedDistance = _lastGroundProgressPosition == Vector3.Zero
            ? float.MaxValue
            : GetHorizontalDistance(playerPosition, _lastGroundProgressPosition);
        var distanceGain = _lastGroundProgressDistance - destinationDistance;
        if (_lastGroundProgressTime == DateTime.MinValue
         || movedDistance >= VendorGroundProgressMinimumMovement
         || distanceGain >= VendorGroundProgressMinimumDistanceGain)
        {
            UpdateGroundApproachProgress(playerPosition, destination);
            return false;
        }

        var now = DateTime.UtcNow;
        var stalledSeconds = (now - _lastGroundProgressTime).TotalSeconds;
        if (liveNpc != null && stalledSeconds >= VendorGroundInteractionRangeStallTimeout)
        {
            if (IsWithinLiveNpcInteractionWindow(playerPosition, liveNpc))
            {
                GatherBuddy.Log.Debug($@"[VendorNavigator] Ground approach stalled within the live NPC interaction window of {GetDisplayName(liveNpc)} with npcDistance={GetTargetDistance(playerPosition, liveNpc):F1}m, destinationDistance={destinationDistance:F1}m, npcVerticalDistance={GetVerticalDistance(playerPosition, liveNpc.Position):F1}m, handing off to vendor interaction");
                StopPathing();
                _state = State.ReadyToPurchase;
                return true;
            }
        }
        if (stalledSeconds < VendorGroundInteractionRangeFallbackTimeout)
            return false;
        if (liveNpc != null
         && destinationMode != NavigationDestinationMode.TargetInteractionRange
         && _forceInteractionRangeApproachUntil < now)
        {
            _forceInteractionRangeApproachUntil = now.AddSeconds(VendorInteractionRangeFallbackDurationSeconds);
            GatherBuddy.Log.Debug($"[VendorNavigator] Ground approach stalled for {GetDisplayName(liveNpc)} at {destinationDistance:F1}m from the approach point, falling back to direct interaction-range navigation for {VendorInteractionRangeFallbackDurationSeconds:F1}s");
            UpdateGroundApproachProgress(playerPosition, liveNpc.Position);
            StartVNavmesh(GetLiveNpcFallbackDestination(liveNpc), true, NavigationDestinationMode.TargetInteractionRange);
            return true;
        }
        if (stalledSeconds < VendorGroundProgressTimeout
         || (now - _lastGroundRetryTime).TotalSeconds < VendorGroundProgressRetryCooldown)
            return false;

        _groundRetryCount = _lastGroundRetryPosition != Vector3.Zero && GetHorizontalDistance(playerPosition, _lastGroundRetryPosition) <= 3f
            ? _groundRetryCount + 1
            : 1;
        _lastGroundRetryPosition = playerPosition;
        _lastGroundRetryTime     = now;
        GatherBuddy.Log.Debug($"[VendorNavigator] Ground approach stalled for {_target?.NpcName ?? "vendor"} at {destinationDistance:F1}m from the approach point, restarting path using {navigationReason} (retry {_groundRetryCount})");
        UpdateGroundApproachProgress(playerPosition, destination);
        StartVNavmesh(destination, usingLiveNpc, destinationMode);
        return true;
    }

    private void UpdateGroundApproachProgress(Vector3 playerPosition, Vector3 destination)
    {
        _lastGroundProgressPosition = playerPosition;
        _lastGroundProgressDistance = GetHorizontalDistance(playerPosition, destination);
        _lastGroundProgressTime     = DateTime.UtcNow;
    }

    private void ClearGroundApproachProgress()
    {
        _lastGroundProgressPosition = Vector3.Zero;
        _lastGroundProgressDistance = float.MaxValue;
        _lastGroundProgressTime     = DateTime.MinValue;
    }

    private static float GetPlayerDot(Vector3 position, Vector3 npcPosition, Vector3 playerPosition)
    {
        var playerOffset = new Vector2(playerPosition.X - npcPosition.X, playerPosition.Z - npcPosition.Z);
        if (playerOffset.LengthSquared() < 0.01f)
            return 0f;

        var candidateOffset = new Vector2(position.X - npcPosition.X, position.Z - npcPosition.Z);
        if (candidateOffset.LengthSquared() < 0.01f)
            return 0f;

        return Vector2.Dot(Vector2.Normalize(candidateOffset), Vector2.Normalize(playerOffset));
    }

    private static float ScoreVendorApproachCandidate(float npcDistance, float forwardDot, float playerDot, bool preferPlayerSide)
    {
        var score = forwardDot * (preferPlayerSide ? 2.5f : 3.5f) - npcDistance * 0.05f;
        if (preferPlayerSide)
            score += playerDot * 4.0f;
        return score;
    }

    private static Vector3 GetFacingDirection(float rotation)
        => new(MathF.Sin(rotation), 0f, MathF.Cos(rotation));

    private static Vector3 RotateHorizontal(Vector3 vector, float angle)
    {
        var sin = MathF.Sin(angle);
        var cos = MathF.Cos(angle);
        return new Vector3(
            vector.X * cos - vector.Z * sin,
            vector.Y,
            vector.X * sin + vector.Z * cos);
    }

    private static float GetHorizontalDistance(Vector3 first, Vector3 second)
        => Vector2.Distance(new Vector2(first.X, first.Z), new Vector2(second.X, second.Z));

    private static float GetVerticalDistance(Vector3 first, Vector3 second)
        => MathF.Abs(first.Y - second.Y);

    private static float GetForwardDot(Vector3 position, Vector3 npcPosition, Vector3 forward)
    {
        var horizontalOffset = new Vector2(position.X - npcPosition.X, position.Z - npcPosition.Z);
        if (horizontalOffset.LengthSquared() < 0.01f)
            return float.NegativeInfinity;

        return Vector2.Dot(Vector2.Normalize(horizontalOffset), new Vector2(forward.X, forward.Z));
    }

    private static async Task<List<Vector3>> FindCombinedVendorPath(Vector3 player, Vector3 target, float landingDistance, bool flying, CancellationToken token)
    {
        var pointOnFloor = flying && VNavmesh.Query.Mesh.PointOnFloor != null
            ? VNavmesh.Query.Mesh.PointOnFloor(player, false, 5f)
            : player;
        if (pointOnFloor == null)
            return [];

        var groundPath = await VNavmesh.Nav.PathfindCancelable(pointOnFloor.Value, target, false, token);
        if (groundPath.Count == 0)
            return [];
        if (groundPath.Count == 1)
            return groundPath;

        var intersectionIndex = FindIntersection(groundPath, target, landingDistance);
        var landingWaypoint = GetPointAtRadius(groundPath[intersectionIndex], groundPath[intersectionIndex + 1], target, landingDistance);
        var meshLandingWaypoint = VNavmesh.Query.Mesh.NearestPoint(landingWaypoint, landingDistance, 10f);
        if (meshLandingWaypoint == null || MathF.Abs(target.Y - meshLandingWaypoint.Value.Y) > 10f)
            return [];

        var flyPath = await VNavmesh.Nav.PathfindCancelable(player, meshLandingWaypoint.Value, true, token);
        if (flyPath.Count == 0)
            return [];

        if (flyPath.Count > 1 && Vector3.DistanceSquared(flyPath[^1], flyPath[^2]) < 0.01f)
            flyPath.RemoveAt(flyPath.Count - 1);

        landingWaypoint = flyPath[^1];
        flyPath.AddRange(groundPath.Skip(intersectionIndex + 1));
        flyPath.Add(landingWaypoint);
        return flyPath;

        static int FindIntersection(List<Vector3> waypoints, Vector3 destination, float radius)
        {
            var radiusSquared = radius * radius;
            for (var index = waypoints.Count - 2; index > 0; index--)
            {
                if (Vector3.DistanceSquared(waypoints[index], destination) > radiusSquared)
                    return index;
            }

            return 0;
        }

        static Vector3 GetPointAtRadius(Vector3 first, Vector3 second, Vector3 destination, float radius)
        {
            var direction = new Vector2(second.X - first.X, second.Z - first.Z);
            var relative = new Vector2(first.X - destination.X, first.Z - destination.Z);
            var a = Vector2.Dot(direction, direction);
            var b = 2 * Vector2.Dot(relative, direction);
            var c = Vector2.Dot(relative, relative) - radius * radius;
            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
                return first;

            discriminant = MathF.Sqrt(discriminant);
            var firstT = (-b - discriminant) / (2 * a);
            var secondT = (-b + discriminant) / (2 * a);
            var t = firstT >= 0f && firstT <= 1f ? firstT : secondT;
            return first + (second - first) * t;
        }
    }

    private static void RemovePassedWaypoints(List<Vector3> path)
    {
        if (path.Count == 0 || Dalamud.Objects.LocalPlayer == null)
            return;

        var playerPosition = Dalamud.Objects.LocalPlayer.Position;
        var target         = path[^1];
        var forward        = new Vector3(target.X - playerPosition.X, 0f, target.Z - playerPosition.Z);
        if (forward.LengthSquared() < 1f)
            return;

        forward = Vector3.Normalize(forward);
        var removeCount = 0;
        while (removeCount < path.Count)
        {
            var next = new Vector3(path[removeCount].X - playerPosition.X, 0f, path[removeCount].Z - playerPosition.Z);
            if (Vector3.Dot(forward, next) > 0)
                break;

            removeCount++;
        }

        if (removeCount > 0)
            path.RemoveRange(0, removeCount);
    }

    internal static uint GetPrimaryRouteAetheryteId(uint targetTerritoryId, Vector3 npcPosition)
        => FindBestRoute(targetTerritoryId, npcPosition, false).AetheryteId;
    private static (uint AetheryteId, string? AethernetName, bool RequiresHousingEntry, bool RequiresFirmamentEntry) FindBestRoute(uint targetTerritoryId, Vector3 npcPosition,
        bool logRouteDiagnostics)
    {
        if (TryFindHousingRoute(targetTerritoryId, out var housingAetheryteId, logRouteDiagnostics))
            return (housingAetheryteId, null, housingAetheryteId != 0, false);
        if (TryFindFirmamentRoute(targetTerritoryId, out var firmamentAetheryteId, logRouteDiagnostics))
            return (firmamentAetheryteId, null, false, firmamentAetheryteId != 0);
        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null) return (0, null, false, false);
        if (TryFindNearestDirectAetheryteInTerritory(aetheryteSheet, targetTerritoryId, npcPosition, out var directAetheryteId, logRouteDiagnostics))
            return (directAetheryteId, null, false, false);

        var bestDist              = float.MaxValue;
        uint bestAetheryteId      = 0;
        string? bestShardName     = null;
        uint fallbackAetheryteId  = 0;
        string? fallbackShardName = null;

        var npcXZ = new Vector2(npcPosition.X, npcPosition.Z);

        foreach (var shard in aetheryteSheet)
        {
            if (shard.IsAetheryte || shard.Territory.RowId != targetTerritoryId || shard.AethernetName.RowId == 0)
                continue;

            var shardName = shard.AethernetName.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(shardName)) continue;

            var group   = shard.AethernetGroup;
            uint mainId = 0;
            foreach (var mainA in aetheryteSheet)
            {
                if (mainA.IsAetheryte && mainA.AethernetGroup == group && Teleporter.IsAttuned(mainA.RowId))
                {
                    mainId = mainA.RowId;
                    break;
                }
            }
            if (mainId == 0) continue;

            if (fallbackAetheryteId == 0)
            {
                fallbackAetheryteId = mainId;
                fallbackShardName   = shardName;
            }

            var shardXZ = GetAetheryteXZ(shard);
            if (!shardXZ.HasValue) continue;

            var dist = Vector2.Distance(npcXZ, shardXZ.Value);
            if (dist < bestDist)
            {
                bestDist         = dist;
                bestAetheryteId  = mainId;
                bestShardName    = shardName;
            }
        }

        if (bestAetheryteId != 0)
            return (bestAetheryteId, bestShardName, false, false);
        return (fallbackAetheryteId, fallbackShardName, false, false);
    }

    // Port of Lifestream DataStore.GetTinyAetheryte position logic.
    private static Vector2? GetAetheryteXZ(Aetheryte shard)
    {

        var mapSheet    = Dalamud.GameData.GetExcelSheet<Map>();
        var markerSheet = Dalamud.GameData.GetSubrowExcelSheet<MapMarker>();
        if (mapSheet == null || markerSheet == null) return null;

        var territoryId = shard.Territory.RowId;
        var scale = 100f;
        foreach (var m in mapSheet)
        {
            if (m.TerritoryType.RowId == territoryId)
            {
                scale = m.SizeFactor;
                break;
            }
        }

        var markerDataType = shard.IsAetheryte ? 3u : 4u;
        var markerKey = shard.IsAetheryte ? shard.RowId : shard.AethernetName.RowId;
        if (markerKey == 0)
            return null;

        foreach (var markerRow in markerSheet)
        {
            foreach (var m in markerRow)
            {
                if (m.DataType == markerDataType && m.DataKey.RowId == markerKey)
                {
                    var x = (m.X - 1024f) / (scale / 100f);
                    var z = (m.Y - 1024f) / (scale / 100f);
                    return new Vector2(x, z);
                }
            }
        }
        return null;
    }

    private static bool TryFindNearestDirectAetheryteInTerritory(ExcelSheet<Aetheryte> aetheryteSheet, uint territoryId, Vector3 npcPosition, out uint aetheryteId,
        bool logRouteDiagnostics)
    {
        aetheryteId = 0;
        uint fallbackAetheryteId = 0;
        string? fallbackName = null;
        string? selectedName = null;
        var selectedDistance = float.MaxValue;
        var candidateCount = 0;
        var npcXZ = new Vector2(npcPosition.X, npcPosition.Z);

        foreach (var aetheryte in aetheryteSheet)
        {
            if (!aetheryte.IsAetheryte || aetheryte.Territory.RowId != territoryId || !Teleporter.IsAttuned(aetheryte.RowId))
                continue;

            candidateCount++;
            if (fallbackAetheryteId == 0)
            {
                fallbackAetheryteId = aetheryte.RowId;
                fallbackName = aetheryte.PlaceName.Value.Name.ExtractText();
            }

            var aetheryteXZ = GetAetheryteXZ(aetheryte);
            if (!aetheryteXZ.HasValue)
                continue;

            var distanceToVendor = Vector2.Distance(npcXZ, aetheryteXZ.Value);
            if (distanceToVendor >= selectedDistance)
                continue;

            aetheryteId = aetheryte.RowId;
            selectedName = aetheryte.PlaceName.Value.Name.ExtractText();
            selectedDistance = distanceToVendor;
        }

        if (aetheryteId != 0)
        {
            if (logRouteDiagnostics)
                GatherBuddy.Log.Debug($@"[VendorNavigator] Using direct aetheryte '{selectedName ?? aetheryteId.ToString()}' ({aetheryteId}) for territory {territoryId}: vendorDistance={selectedDistance:F1}m, candidates={candidateCount}");
            return true;
        }

        if (fallbackAetheryteId == 0)
            return false;

        aetheryteId = fallbackAetheryteId;
        if (logRouteDiagnostics)
            GatherBuddy.Log.Debug($@"[VendorNavigator] Using fallback direct aetheryte '{fallbackName ?? fallbackAetheryteId.ToString()}' ({fallbackAetheryteId}) for territory {territoryId} because no direct map position was available among {candidateCount} candidates");
        return true;
    }
}
