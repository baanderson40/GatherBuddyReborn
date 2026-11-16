using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using RebornMaterial;
using System;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static readonly uint[] DebugJobIds = [8, 9, 10, 11, 12, 13, 14, 15];

    private static readonly M3Segment[] VendorLocationSegments =
    [
        new("DataShare first", FontAwesomeIcon.Database, "Prefer AllaganTools DataShare locations, then fill gaps from the Level sheet, ENpcPlace supplemental data, and planevent.lgb."),
        new("LGB first", FontAwesomeIcon.Map, "Prefer planevent.lgb locations, then fill gaps from the Level sheet, ENpcPlace supplemental data, and AllaganTools DataShare."),
    ];

    private void DrawDebugPage()
    {
        using var child = ImRaii.Child("##vulcanDebug", Vector2.Zero);
        if (!child)
        {
            return;
        }

        using (M3Card.Begin("##debugMenus", "Context menus", FontAwesomeIcon.Bars, M3.AccentFor(0)))
        {
            var maxRecentLists = GatherBuddy.Config.MaxRecentCraftingListsInContextMenu;
            if (M3Widgets.RowNumber("Recent lists in menus", ref maxRecentLists, 1, 1, 50, supporting: "How many recent crafting lists context menus offer."))
            {
                GatherBuddy.Config.MaxRecentCraftingListsInContextMenu = Math.Clamp(maxRecentLists, 1, 50);
                GatherBuddy.Config.Save();
            }
        }

        DrawVendorNpcLocationDebug();

        using (M3Card.Begin("##debugRepair", "Repair", FontAwesomeIcon.Wrench, M3.AccentFor(2)))
        {
            M3Widgets.RowValue("Lowest equipped condition", $"{Crafting.RepairManager.GetMinEquippedPercent()}%");
            M3Widgets.RowValue("Can repair yourself", Crafting.RepairManager.CanRepairAny() ? "Yes" : "No");
            var npcNearby = Crafting.RepairManager.RepairNPCNearby(out _);
            M3Widgets.RowValue("Repair NPC nearby", npcNearby ? "Yes" : "No");
            if (npcNearby)
            {
                M3Widgets.RowValue("NPC repair price", $"{Crafting.RepairManager.GetNPCRepairPrice():N0} gil");
            }
        }

        using (M3Card.Begin("##debugMateria", "Materia extraction", FontAwesomeIcon.Gem, M3.AccentFor(3)))
        {
            M3Widgets.RowValue("Extraction unlocked", Crafting.MateriaManager.IsExtractionUnlocked() ? "Yes" : "No");
            M3Widgets.RowValue("Items ready", Crafting.MateriaManager.ReadySpiritbondItemCount().ToString());
            M3Widgets.RowValue("Free inventory slots", Crafting.MateriaManager.HasFreeInventorySlots() ? "Yes" : "No");
        }

        using (M3Card.Begin("##debugGearset", "Gearset stats", FontAwesomeIcon.UserCog, M3.AccentFor(4)))
        {
            var jobIndex = Math.Max(0, Array.IndexOf(DebugJobIds, _debugSelectedJobId));
            if (M3Widgets.RowCombo("Job", ref jobIndex, DebugJobIds.Length, i => GetDebugJobName(DebugJobIds[i])))
            {
                _debugSelectedJobId = DebugJobIds[jobIndex];
                _debugLastTestResult = null;
            }

            if (M3Widgets.Button("##readStats", "Read stats", M3ButtonStyle.Tonal, FontAwesomeIcon.Search))
            {
                var stats = GearsetStatsReader.ReadGearsetStatsForJob(_debugSelectedJobId);
                _debugLastTestResult = stats != null
                    ? $"Success: Craftsmanship={stats.Craftsmanship}, Control={stats.Control}, CP={stats.CP}, Manipulation={stats.Manipulation}"
                    : "Failed: Could not read gearset stats for this job";
            }

            ImGui.SameLine(0, M3.Space2);
            if (M3Widgets.Button("##updateGearset", "Update saved gearset", M3ButtonStyle.Text, FontAwesomeIcon.Save,
                    tooltip: "Updates the selected job's saved gearset from your currently equipped items. You must be on that job."))
            {
                var refreshResult = GearsetStatsReader.RefreshGearsetFromCurrentEquipped(_debugSelectedJobId);
                var stats = GearsetStatsReader.ReadGearsetStatsForJob(_debugSelectedJobId);
                _debugLastTestResult = stats != null
                    ? $"{refreshResult} Current read: Craftsmanship={stats.Craftsmanship}, Control={stats.Control}, CP={stats.CP}, Manipulation={stats.Manipulation}"
                    : refreshResult;
            }

            if (_debugLastTestResult != null)
            {
                M3Text.Draw(_debugLastTestResult, M3TextStyle.Muted, true);
            }
        }

#if DEBUG
        using (M3Card.Begin("##debugAcquisition", "Automatic acquisition preview", FontAwesomeIcon.ShoppingCart,
                   M3.AccentFor(5), M3CardStyle.Filled,
                   "Read-only planning preview. It never purchases items or requests travel."))
        {
            DrawAcquisitionPreviewDebug();
        }
#endif

        using (M3Card.Begin("##debugAdaptive", "Adaptive diagnostics (Experimental)", FontAwesomeIcon.ExclamationTriangle,
                   M3.AccentFor(5), M3CardStyle.Filled,
                   "Experimental live and shadow diagnostics. Native hook/layout remains unvalidated."))
        {
            DrawAdaptiveShadowDebug();
        }

        DrawKeyboardNavigationTest();
    }

    private static void DrawVendorNpcLocationDebug()
    {
        using var card = M3Card.Begin("##debugVendorLocations", "Vendor NPC locations", FontAwesomeIcon.MapMarkerAlt, M3.AccentFor(1));
        var dataShareFirst = GatherBuddy.Config.VendorNpcLocationsDataShareFirst;
        var index = dataShareFirst ? 0 : 1;
        if (M3Widgets.RowSegmented("Source to prefer", ref index, VendorLocationSegments))
        {
            SetVendorNpcLocationSourcePreference(index == 0);
        }

        M3Widgets.RowValue("Source order", dataShareFirst ? "DataShare, Level, supplemental, LGB" : "LGB, Level, supplemental, DataShare");
        M3Widgets.RowValue("Cache", GetVendorNpcLocationCacheStatus());
    }

    private static void DrawKeyboardNavigationTest()
    {
        using var card = M3Card.Begin("##debugKeyboardNavigation", "Keyboard navigation", FontAwesomeIcon.Keyboard, M3.AccentFor(5));
        var io = ImGui.GetIO();
        M3Widgets.RowValue("Navigation active", io.NavActive ? "Yes" : "No");
        M3Widgets.RowValue("Navigation visible", io.NavVisible ? "Yes" : "No");
        M3Widgets.RowValue("Config flags", io.ConfigFlags.ToString());

        var navKeyboardEnabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableKeyboard) != 0;
        if (M3Widgets.Button("##keyboardNav", navKeyboardEnabled ? "Turn off keyboard navigation" : "Turn on keyboard navigation", M3ButtonStyle.Tonal,
                FontAwesomeIcon.Keyboard))
        {
            io = ImGui.GetIO();
            io.ConfigFlags ^= ImGuiConfigFlags.NavEnableKeyboard;
            GatherBuddy.Log.Information($"[VulcanWindow] {(navKeyboardEnabled ? "Disabled" : "Enabled")} ImGui keyboard navigation");
        }

        M3Text.Draw("Press Tab to start keyboard navigation.", M3TextStyle.Muted);
    }
    private static string GetDebugJobName(uint jobId) => jobId switch
    {
        8 => "Carpenter (CRP)",
        9 => "Blacksmith (BSM)",
        10 => "Armorer (ARM)",
        11 => "Goldsmith (GSM)",
        12 => "Leatherworker (LTW)",
        13 => "Weaver (WVR)",
        14 => "Alchemist (ALC)",
        15 => "Culinarian (CUL)",
        _ => "Unknown"
    };

    private static string GetTerritoryName(uint territoryId)
    {
        var territorySheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (territorySheet?.TryGetRow(territoryId, out var territory) == true)
        {
            return territory.PlaceName.ValueNullable?.Name.ExtractText() ?? "Unknown";
        }
        return "Unknown";
    }

    private static void SetVendorNpcLocationSourcePreference(bool dataShareFirst)
    {
        if (GatherBuddy.Config.VendorNpcLocationsDataShareFirst == dataShareFirst)
        {
            return;
        }

        GatherBuddy.Config.VendorNpcLocationsDataShareFirst = dataShareFirst;
        GatherBuddy.Config.Save();
        GatherBuddy.Log.Debug($"[VulcanWindow] Vendor NPC location source order set to {(dataShareFirst ? "DataShare -> Level -> Supplemental -> LGB" : "LGB -> Level -> Supplemental -> DataShare")}");
        VendorNpcLocationCache.ReloadAsync();
    }

    private static string GetVendorNpcLocationCacheStatus()
    {
        if (VendorNpcLocationCache.IsInitializing)
        {
            return $"Rebuilding ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount})";
        }

        if (VendorNpcLocationCache.IsInitialized)
        {
            return $"Ready ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount})";
        }

        if (VendorShopResolver.IsInitializing)
        {
            return "Waiting for vendor data";
        }

        if (!VendorShopResolver.IsInitialized)
        {
            return "Vendor data not initialized";
        }

        return "Location cache not initialized";
    }
}
